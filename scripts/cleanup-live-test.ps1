param(
    [Parameter(Mandatory = $true)]
    [long]$ClanId,
    [Parameter(Mandatory = $true)]
    [long]$ChannelId,
    [Parameter(Mandatory = $true)]
    [long]$UserId,
    [long[]]$RoleId = @()
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
    $transaction = $connection.BeginTransaction()
    try {
        function Invoke-CleanupCommand {
            param(
                [string]$Sql,
                [hashtable]$Parameters = @{}
            )

            $command = $connection.CreateCommand()
            $command.Transaction = $transaction
            $command.CommandText = $Sql
            foreach ($entry in $Parameters.GetEnumerator()) {
                [void]$command.Parameters.AddWithValue($entry.Key, $entry.Value)
            }
            try {
                return $command.ExecuteNonQuery()
            }
            finally {
                $command.Dispose()
            }
        }

        $eventIds = @()
        $eventCommand = $connection.CreateCommand()
        $eventCommand.Transaction = $transaction
        $eventCommand.CommandText = @'
SELECT id
FROM community_event
WHERE clan_id = @clan
  AND channel_id = @channel
  AND title IN ('Monze chrome event', 'Monze smoke test');
'@
        [void]$eventCommand.Parameters.AddWithValue('clan', $ClanId)
        [void]$eventCommand.Parameters.AddWithValue('channel', $ChannelId)
        $eventReader = $eventCommand.ExecuteReader()
        try {
            while ($eventReader.Read()) {
                $eventIds += [int64]$eventReader.GetValue(0)
            }
        }
        finally {
            $eventReader.Dispose()
            $eventCommand.Dispose()
        }

        if ($eventIds.Count -gt 0) {
            $eventIdList = ($eventIds -join ',')
            [void](Invoke-CleanupCommand "DELETE FROM signup_entry WHERE event_id IN ($eventIdList);")
            [void](Invoke-CleanupCommand "DELETE FROM community_event WHERE id IN ($eventIdList);")
        }

        [void](Invoke-CleanupCommand @'
DELETE FROM knowledge_entry
WHERE clan_id = @clan
  AND question IN ('Monze chrome smoke FAQ', 'onboarding');
'@ @{ clan = $ClanId })
        [void](Invoke-CleanupCommand @'
DELETE FROM outbox_delivery
WHERE clan_id = @clan
  AND (body IN ('Chrome smoke notification')
       OR body LIKE 'Monze smoke test:%'
       OR body LIKE 'Monze chrome event:%');
'@ @{ clan = $ClanId })
        [void](Invoke-CleanupCommand @'
DELETE FROM voice_claim
WHERE clan_id = @clan;
'@ @{ clan = $ClanId })
        [void](Invoke-CleanupCommand @'
DELETE FROM meeting_session
WHERE clan_id = @clan
  AND text_channel_id = @channel
  AND room_id IS NULL;
'@ @{ clan = $ClanId; channel = $ChannelId })
        [void](Invoke-CleanupCommand @'
DELETE FROM meeting_schedule
WHERE clan_id = @clan
  AND channel_id = @channel;
'@ @{ clan = $ClanId; channel = $ChannelId })
        [void](Invoke-CleanupCommand @'
DELETE FROM game_attempt
WHERE clan_id = @clan AND user_id = @user;
'@ @{ clan = $ClanId; user = $UserId })
        [void](Invoke-CleanupCommand @'
DELETE FROM wheel_cooldown
WHERE clan_id = @clan AND user_id = @user;
'@ @{ clan = $ClanId; user = $UserId })
        [void](Invoke-CleanupCommand @'
DELETE FROM activity_ledger
WHERE clan_id = @clan AND user_id = @user AND source_type = 'command';
DELETE FROM activity_balance
WHERE clan_id = @clan AND user_id = @user;
'@ @{ clan = $ClanId; user = $UserId })
        [void](Invoke-CleanupCommand @'
DELETE FROM ai_usage
WHERE clan_id = @clan AND user_id = @user;
'@ @{ clan = $ClanId; user = $UserId })
        if ($RoleId.Count -gt 0) {
            [void](Invoke-CleanupCommand @'
DELETE FROM role_rule
WHERE clan_id = @clan
  AND role_id = ANY(@roles);
'@ @{ clan = $ClanId; roles = $RoleId })
        }
        [void](Invoke-CleanupCommand @'
UPDATE clan_settings
SET welcome_enabled = FALSE,
    welcome_text = NULL,
    version = version + 1
WHERE clan_id = @clan;
'@ @{ clan = $ClanId })

        $transaction.Commit()
        [pscustomobject]@{
            clan_id = $ClanId
            channel_id = $ChannelId
            user_id = $UserId
            status = 'cleaned'
        }
    }
    catch {
        $transaction.Rollback()
        throw
    }
    finally {
        $transaction.Dispose()
    }
}
finally {
    $connection.Dispose()
}
