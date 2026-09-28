param(
    [Parameter(Mandatory = $true)]
    [long]$ClanId,
    [Parameter(Mandatory = $true)]
    [long]$ChannelId,
    [switch]$Assert
)

$ErrorActionPreference = 'Stop'
$config = Get-Content (Join-Path $PSScriptRoot '..\appsettings.json') -Raw | ConvertFrom-Json
$secretPath = Join-Path $PSScriptRoot '..\appsettings.secrets.json'
if ([string]::IsNullOrWhiteSpace([string]$config.Monze.Postgres) -and (Test-Path -LiteralPath $secretPath)) {
    $secret = Get-Content $secretPath -Raw | ConvertFrom-Json
    $connectionString = [string]$secret.Monze.Postgres
}
else {
    $connectionString = [string]$config.Monze.Postgres
}
if ([string]::IsNullOrWhiteSpace($connectionString)) {
    throw 'Monze.Postgres is not configured in appsettings.json or appsettings.secrets.json.'
}
$assemblyPath = Join-Path $PSScriptRoot '..\bin\Release\net10.0\Npgsql.dll'
[void][Reflection.Assembly]::LoadFrom((Resolve-Path $assemblyPath))

$connection = [Npgsql.NpgsqlConnection]::new($connectionString)
try {
    $connection.Open()

    function Invoke-InspectionQuery {
        param(
            [Npgsql.NpgsqlConnection]$Connection,
            [string]$Name,
            [string]$Sql,
            [hashtable]$Parameters = @{}
        )

        $command = $Connection.CreateCommand()
        try {
            $command.CommandText = $Sql
            foreach ($entry in $Parameters.GetEnumerator()) {
                [void]$command.Parameters.AddWithValue($entry.Key, $entry.Value)
            }

            $reader = $command.ExecuteReader()
            try {
                $rows = @()
                while ($reader.Read()) {
                    $row = [ordered]@{ check = $Name }
                    for ($index = 0; $index -lt $reader.FieldCount; $index++) {
                        $value = $reader.GetValue($index)
                        $row[$reader.GetName($index)] = if ($value -is [DBNull]) { $null } else { $value }
                    }
                    $rows += [pscustomobject]$row
                }
                $rows
            }
            finally {
                $reader.Dispose()
            }
        }
        finally {
            $command.Dispose()
        }
    }

    $results = [System.Collections.Generic.List[object]]::new()
    function Add-InspectionRows {
        param([object[]]$Rows)
        foreach ($row in $Rows) {
            if ($null -ne $row) {
                [void]$results.Add($row)
            }
        }
    }

    $registry = @(Invoke-InspectionQuery $connection 'clan_registry' @'
SELECT clan_id, owner_id, inactive_reason IS NULL AS active
FROM clan_registry
WHERE clan_id = @clan;
'@ @{ clan = $ClanId })
    Add-InspectionRows $registry

    $admins = @(Invoke-InspectionQuery $connection 'clan_admin' @'
SELECT count(*) AS rows
FROM clan_admin
WHERE clan_id = @clan;
'@ @{ clan = $ClanId })
    Add-InspectionRows $admins

    $migrations = @(Invoke-InspectionQuery $connection 'schema_migration' @'
SELECT version, applied_at
FROM schema_migrations
ORDER BY version;
'@)
    Add-InspectionRows $migrations

    $roleRules = @(Invoke-InspectionQuery $connection 'role_rule' @'
SELECT count(*) AS rows,
       count(*) FILTER (WHERE enabled) AS enabled_rows,
       count(*) FILTER (WHERE enabled AND condition_value IS NOT NULL) AS conditioned_rows
FROM role_rule
WHERE clan_id = @clan;
'@ @{ clan = $ClanId })
    Add-InspectionRows $roleRules

    $settings = @(Invoke-InspectionQuery $connection 'clan_settings' @'
SELECT welcome_enabled, version
FROM clan_settings
WHERE clan_id = @clan;
'@ @{ clan = $ClanId })
    Add-InspectionRows $settings

    $activity = @(Invoke-InspectionQuery $connection 'activity' @'
SELECT count(*) AS ledger_rows,
       coalesce(sum(delta), 0) AS total_delta,
       (SELECT count(*) FROM activity_balance WHERE clan_id = @clan) AS balance_rows,
       (SELECT coalesce(sum(points), 0) FROM activity_balance WHERE clan_id = @clan) AS balance_points
FROM activity_ledger
WHERE clan_id = @clan;
'@ @{ clan = $ClanId })
    Add-InspectionRows $activity

    $meetingSchedule = @(Invoke-InspectionQuery $connection 'meeting_schedule' @'
SELECT kind, status, count(*) AS rows
FROM meeting_schedule
WHERE clan_id = @clan
GROUP BY kind, status
ORDER BY kind, status;
'@ @{ clan = $ClanId })
    Add-InspectionRows $meetingSchedule

    $meetingSessions = @(Invoke-InspectionQuery $connection 'meeting_session' @'
SELECT status, count(*) AS rows
FROM meeting_session
WHERE clan_id = @clan
GROUP BY status
ORDER BY status;
'@ @{ clan = $ClanId })
    Add-InspectionRows $meetingSessions

    $commandInbox = @(Invoke-InspectionQuery $connection 'command_inbox' @'
SELECT status, count(*) AS rows
FROM command_inbox
WHERE clan_id = @clan
GROUP BY status
ORDER BY status;
'@ @{ clan = $ClanId })
    Add-InspectionRows $commandInbox

    $outbox = @(Invoke-InspectionQuery $connection 'outbox' @'
SELECT kind, status, count(*) AS rows
FROM outbox_delivery
WHERE clan_id = @clan
GROUP BY kind, status
ORDER BY kind, status;
'@ @{ clan = $ClanId })
    Add-InspectionRows $outbox

    $faq = @(Invoke-InspectionQuery $connection 'faq' @'
SELECT count(*) AS rows
FROM knowledge_entry
WHERE clan_id = @clan;
'@ @{ clan = $ClanId })
    Add-InspectionRows $faq

    $topics = @(Invoke-InspectionQuery $connection 'topic_prompt' @'
SELECT count(*) AS rows,
       count(*) FILTER (WHERE last_used_at IS NULL) AS unused_rows
FROM topic_prompt
WHERE clan_id = @clan;
'@ @{ clan = $ClanId })
    Add-InspectionRows $topics

    $wheelCooldown = @(Invoke-InspectionQuery $connection 'wheel_cooldown' @'
SELECT count(*) AS rows,
       count(*) FILTER (WHERE next_allowed_at > now()) AS active_rows
FROM wheel_cooldown
WHERE clan_id = @clan;
'@ @{ clan = $ClanId })
    Add-InspectionRows $wheelCooldown

    $policy = @(Invoke-InspectionQuery $connection 'channel_policy' @'
SELECT persist_messages, has_gap
FROM channel_policy
WHERE clan_id = @clan AND channel_id = @channel;
'@ @{ clan = $ClanId; channel = $ChannelId })
    Add-InspectionRows $policy

    if ($Assert) {
        if ($registry.Count -ne 1 -or -not [bool]$registry[0].active) {
            throw "DB assertion failed: clan $ClanId is not registered and active."
        }
        $roleMigration = @($migrations | Where-Object { [string]$_.version -like '*007_roles' })
        if ($roleMigration.Count -ne 1) {
            throw "DB assertion failed: migration 007_roles is not applied."
        }
        foreach ($migrationVersion in @('008_topic_prompts', '009_wheel_cooldown', '010_role_rule_conditions', '011_meeting_summary_retry', '012_meeting_request_cleanup', '013_meeting_summary_leases', '014_command_inbox', '015_event_capacity')) {
            $migration = @($migrations | Where-Object { [string]$_.version -like "*$migrationVersion" })
            if ($migration.Count -ne 1) {
                throw "DB assertion failed: migration $migrationVersion is not applied."
            }
        }
        if ($settings.Count -ne 1) {
            throw "DB assertion failed: clan $ClanId has no settings row."
        }
        if ($activity.Count -ne 1 -or [int64]$activity[0].total_delta -ne [int64]$activity[0].balance_points) {
            throw "DB assertion failed: activity ledger and balance totals differ for clan $ClanId."
        }
        if ($topics.Count -ne 1 -or [int64]$topics[0].rows -lt 1) {
            throw "DB assertion failed: clan $ClanId has no topic prompts."
        }
    }

    $results
}
finally {
    $connection.Dispose()
}
