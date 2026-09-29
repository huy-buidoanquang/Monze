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
SELECT welcome_enabled,
       role_enabled,
       length(welcome_text) AS welcome_text_length,
       welcome_embed IS NOT NULL AS has_welcome_embed,
       version
FROM clan_settings
WHERE clan_id = @clan;
'@ @{ clan = $ClanId })
    Add-InspectionRows $settings

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

    $meetingSummaries = @(Invoke-InspectionQuery $connection 'meeting_summary' @'
SELECT count(*) AS rows,
       count(*) FILTER (WHERE posted) AS posted_rows
FROM meeting_summary ms
JOIN meeting_session s ON s.id = ms.session_id
WHERE s.clan_id = @clan;
'@ @{ clan = $ClanId })
    Add-InspectionRows $meetingSummaries

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

    $outboxKinds = @(Invoke-InspectionQuery $connection 'outbox_kinds' @'
SELECT kind, count(*) AS rows
FROM outbox_delivery
GROUP BY kind
ORDER BY kind;
'@)
    Add-InspectionRows $outboxKinds

    $outboxColumns = @(Invoke-InspectionQuery $connection 'outbox_columns' @'
SELECT EXISTS (
           SELECT 1 FROM information_schema.columns
           WHERE table_schema = 'public'
             AND table_name = 'outbox_delivery'
             AND column_name = 'mention_everyone') AS mention_everyone,
       EXISTS (
           SELECT 1 FROM information_schema.columns
           WHERE table_schema = 'public'
             AND table_name = 'outbox_delivery'
             AND column_name = 'content_json') AS content_json;
'@)
    Add-InspectionRows $outboxColumns

    $schema = @(Invoke-InspectionQuery $connection 'retained_schema' @'
SELECT name,
       to_regclass('public.' || name)::text IS NOT NULL AS present
FROM unnest(ARRAY[
    'clan_registry', 'clan_settings', 'clan_admin', 'channel_policy',
    'ai_usage', 'role_rule', 'role_grant', 'meeting_session',
    'voice_claim', 'meeting_summary', 'meeting_schedule', 'agent_event',
    'inbox_event', 'command_inbox', 'outbox_delivery', 'schema_migrations'
]::text[]) AS names(name)
ORDER BY name;
'@)
    Add-InspectionRows $schema

    $removedSchema = @(Invoke-InspectionQuery $connection 'removed_schema' @'
SELECT name,
       to_regclass('public.' || name)::text IS NOT NULL AS present
FROM unnest(ARRAY[
    'community_event', 'signup_entry', 'knowledge_entry', 'activity_ledger',
    'activity_balance', 'game_attempt', 'wheel_cooldown', 'topic_prompt'
]::text[]) AS names(name)
ORDER BY name;
'@)
    Add-InspectionRows $removedSchema

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
        foreach ($migrationVersion in @('008_topic_prompts', '009_wheel_cooldown', '010_role_rule_conditions', '011_meeting_summary_retry', '012_meeting_request_cleanup', '013_meeting_summary_leases', '014_command_inbox', '015_event_capacity', '017_meeting_schedule_details', '018_remove_community_features', '019_meeting_invitation_delivery', '020_role_automation', '021_agent_summary_binding')) {
            $migration = @($migrations | Where-Object { [string]$_.version -like "*$migrationVersion" })
            if ($migration.Count -ne 1) {
                throw "DB assertion failed: migration $migrationVersion is not applied."
            }
        }
        if ($settings.Count -ne 1) {
            throw "DB assertion failed: clan $ClanId has no settings row."
        }
        if (-not [bool]$settings[0].PSObject.Properties['role_enabled']) {
            throw 'DB assertion failed: role_enabled is missing from clan_settings.'
        }
        if (@($schema | Where-Object { -not [bool]$_.present }).Count -gt 0) {
            throw "DB assertion failed: one or more retained Monze tables are missing."
        }
        if (@($removedSchema | Where-Object { [bool]$_.present }).Count -gt 0) {
            throw "DB assertion failed: one or more removed community tables are still present."
        }
        if ($outboxColumns.Count -ne 1 -or -not [bool]$outboxColumns[0].mention_everyone -or -not [bool]$outboxColumns[0].content_json) {
            throw 'DB assertion failed: meeting invitation outbox columns are missing.'
        }
        $obsoleteOutbox = @($outboxKinds | Where-Object { [string]$_.kind -notin @('Announcement', 'MeetingSummary') })
        if ($obsoleteOutbox.Count -gt 0) {
            throw "DB assertion failed: obsolete outbox kinds remain for clan $ClanId."
        }
    }

    $results
}
finally {
    $connection.Dispose()
}
