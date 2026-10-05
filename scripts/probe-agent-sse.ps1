param(
    [int]$ProbeSeconds = 10,
    [string]$OutputPath = ''
)

$ErrorActionPreference = 'Stop'

function Get-Setting {
    param(
        [object]$Root,
        [string]$Path
    )

    $value = $Root
    foreach ($part in $Path.Split(':')) {
        $property = $value.PSObject.Properties[$part]
        if ($null -eq $property) {
            return $null
        }
        $value = $property.Value
    }
    return $value
}

$projectRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$baseSettings = Get-Content (Join-Path $projectRoot 'appsettings.json') -Raw | ConvertFrom-Json
$secretPath = Join-Path $projectRoot 'appsettings.secrets.json'
$secretSettings = if (Test-Path -LiteralPath $secretPath) {
    Get-Content $secretPath -Raw | ConvertFrom-Json
} else {
    $null
}

$baseUrl = [string](Get-Setting $baseSettings 'Mezon:AgentBaseUrl')
$botId = [long](Get-Setting $baseSettings 'Mezon:BotId')
$token = if ($null -ne $secretSettings) {
    [string](Get-Setting $secretSettings 'Mezon:Token')
} else {
    [string](Get-Setting $baseSettings 'Mezon:Token')
}

if ([string]::IsNullOrWhiteSpace($baseUrl) -or $botId -le 0 -or [string]::IsNullOrWhiteSpace($token)) {
    throw 'AgentBaseUrl, Mezon:BotId and Mezon:Token must be configured.'
}

$endpoint = '{0}/api/sse/metadata?appid={1}&token={2}' -f $baseUrl.TrimEnd('/'), $botId, [Uri]::EscapeDataString($token)
$safeEndpoint = '{0}/api/sse/metadata?appid={1}&token=<redacted>' -f $baseUrl.TrimEnd('/'), $botId
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [System.Threading.Timeout]::InfiniteTimeSpan
$request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $endpoint)
$request.Headers.TryAddWithoutValidation('Accept', 'text/event-stream') | Out-Null
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$result = [ordered]@{
    timestamp = [DateTime]::UtcNow.ToString('O')
    endpoint = $safeEndpoint
    status_code = $null
    content_type = $null
    elapsed_ms = $null
    stream_opened = $false
    bytes_read = 0
    sse_frame_count = 0
    sse_event_types = @()
    data_json_keys = @()
    event_types = @()
    first_data_frame = $false
    outcome = $null
    error_type = $null
    inner_error_type = $null
}

try {
    $timeout = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds([Math]::Max(1, $ProbeSeconds)))
    try {
        $response = $client.SendAsync(
            $request,
            [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead,
            $timeout.Token).GetAwaiter().GetResult()
        $result.status_code = [int]$response.StatusCode
        $result.content_type = if ($null -ne $response.Content.Headers.ContentType) {
            [string]$response.Content.Headers.ContentType.MediaType
        } else {
            $null
        }

        if ($response.IsSuccessStatusCode) {
            $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $result.stream_opened = $true
            $buffer = New-Object byte[] 4096
            try {
                while ($true) {
                    try {
                        $read = $stream.ReadAsync($buffer, 0, $buffer.Length, $timeout.Token).GetAwaiter().GetResult()
                    } catch [OperationCanceledException] {
                        break
                    }
                    if ($read -le 0) { break }
                    $result.bytes_read += $read
                    $text = [Text.Encoding]::UTF8.GetString($buffer, 0, $read)
                    if ($text.Contains('data:')) { $result.first_data_frame = $true }
                    foreach ($match in [regex]::Matches($text, '(?m)^event:\s*([^\r\n]+)')) {
                        $eventType = $match.Groups[1].Value.Trim()
                        if ($result.sse_event_types -notcontains $eventType) {
                            $result.sse_event_types += $eventType
                        }
                    }
                    foreach ($match in [regex]::Matches($text, '(?m)^data:\s*(.*)$')) {
                        $result.sse_frame_count++
                        try {
                            $json = $match.Groups[1].Value | ConvertFrom-Json
                            if ($null -ne $json -and $json.PSObject.Properties.Count -gt 0) {
                                foreach ($property in $json.PSObject.Properties.Name) {
                                    if ($result.data_json_keys -notcontains $property) {
                                        $result.data_json_keys += $property
                                    }
                                }
                            }
                        } catch {
                            # Keep the probe redacted if the server sends a non-JSON heartbeat.
                        }
                    }
                    foreach ($match in [regex]::Matches($text, '"event_type"\s*:\s*"([^"]+)"')) {
                        $eventType = $match.Groups[1].Value
                        if ($result.event_types -notcontains $eventType) {
                            $result.event_types += $eventType
                        }
                    }
                }
            } finally {
                $stream.Dispose()
            }
            $result.outcome = if ($timeout.IsCancellationRequested) { 'connected_until_timeout' } else { 'stream_closed' }
        } else {
            $result.outcome = 'http_error_response'
        }
        $response.Dispose()
    } finally {
        $timeout.Dispose()
    }
} catch [OperationCanceledException] {
    $result.outcome = 'timeout_before_headers'
    $result.error_type = 'OperationCanceledException'
} catch {
    $result.outcome = 'exception'
    $result.error_type = $_.Exception.GetType().Name
    if ($null -ne $_.Exception.InnerException) {
        $result.inner_error_type = $_.Exception.InnerException.GetType().Name
    }
} finally {
    $stopwatch.Stop()
    $result.elapsed_ms = [Math]::Round($stopwatch.Elapsed.TotalMilliseconds, 1)
    $request.Dispose()
    $client.Dispose()
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $projectRoot 'docs/test-artifacts/20260930-correctness/agent-sse-probe.json'
}
$directory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$result | ConvertTo-Json -Depth 5
