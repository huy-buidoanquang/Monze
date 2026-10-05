param(
    [Parameter(Mandatory = $true)]
    [long]$ClanId,
    [string]$Name = ''
)

$ErrorActionPreference = 'Stop'
$configPath = Join-Path $PSScriptRoot '..\appsettings.json'
$config = Get-Content $configPath -Raw | ConvertFrom-Json
$secretPath = Join-Path $PSScriptRoot '..\appsettings.secrets.json'
$connectionString = [string]$config.Monze.Postgres
if ([string]::IsNullOrWhiteSpace($connectionString) -and (Test-Path -LiteralPath $secretPath)) {
    $secret = Get-Content $secretPath -Raw | ConvertFrom-Json
    $connectionString = [string]$secret.Monze.Postgres
}
if ([string]::IsNullOrWhiteSpace($connectionString)) {
    throw 'Monze.Postgres is not configured.'
}

$assemblyPath = Join-Path $PSScriptRoot '..\bin\Release\net10.0\Npgsql.dll'
[void][Reflection.Assembly]::LoadFrom((Resolve-Path $assemblyPath))
$connection = [Npgsql.NpgsqlConnection]::new($connectionString)

function Invoke-Query {
    param(
        [Npgsql.NpgsqlConnection]$Connection,
        [string]$Sql,
        [hashtable]$Parameters
    )

    $command = $Connection.CreateCommand()
    try {
        $command.CommandText = $Sql
        foreach ($entry in $Parameters.GetEnumerator()) {
            [void]$command.Parameters.AddWithValue($entry.Key, $entry.Value)
        }

        $reader = $command.ExecuteReader()
        try {
            while ($reader.Read()) {
                $row = [ordered]@{}
                for ($index = 0; $index -lt $reader.FieldCount; $index++) {
                    $value = $reader.GetValue($index)
                    $row[$reader.GetName($index)] = if ($value -is [DBNull]) { $null } else { $value }
                }
                [pscustomobject]$row
            }
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $command.Dispose()
    }
}

try {
    $connection.Open()
    $nameFilter = if ([string]::IsNullOrWhiteSpace($Name)) { '%' } else { $Name }

    'Schedules'
    Invoke-Query $connection @'
SELECT id, title, kind, status, next_run_at, channel_id, requester_id,
       repeat_minutes, last_error
FROM meeting_schedule
WHERE clan_id = @clan AND title LIKE @name
ORDER BY id DESC;
'@ @{ clan = $ClanId; name = $nameFilter }

    'Sessions'
    Invoke-Query $connection @'
SELECT id, status, voice_channel_id, text_channel_id, requester_id,
       notification_channel_id, notification_message_id, source_message_id,
       root_session_id, room_id, started_at, ended_at, context_closed_at,
       claim_until, summary_attempts, summary_next_attempt_at,
       summary_last_error
FROM meeting_session
WHERE clan_id = @clan
ORDER BY id DESC
LIMIT 20;
'@ @{ clan = $ClanId }

    'Summaries'
    Invoke-Query $connection @'
SELECT ms.session_id, s.room_id, s.ended_at,
       length(ms.summary_text) AS summary_length,
       length(ms.transcript) AS transcript_length,
       ms.full_transcript IS NOT NULL AS has_full_transcript,
       ms.posted
FROM meeting_summary ms
JOIN meeting_session s ON s.id = ms.session_id
WHERE s.clan_id = @clan
ORDER BY ms.session_id DESC
LIMIT 20;
'@ @{ clan = $ClanId }

    'Outbox'
    Invoke-Query $connection @'
SELECT id, kind, status, channel_id, mention_everyone,
       content_json IS NOT NULL AS has_content_json,
       position('@here' in COALESCE(content_json, '')) > 0 AS content_has_here,
       position('ChromeDue' in COALESCE(content_json, '')) > 0 AS content_has_test_title,
       external_message_id, meeting_session_id, reply_to_message_id,
       attempts, due_at, last_error
FROM outbox_delivery
WHERE clan_id = @clan
ORDER BY id DESC
LIMIT 20;
'@ @{ clan = $ClanId }

    'VoiceClaims'
    Invoke-Query $connection @'
SELECT clan_id, voice_channel_id, session_id, expires_at
FROM voice_claim
WHERE clan_id = @clan
ORDER BY expires_at DESC;
'@ @{ clan = $ClanId }
}
finally {
    $connection.Dispose()
}
