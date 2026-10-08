#Requires -Version 7.2
<#
.SYNOPSIS
    Runs the Monze automated test campaign and writes ONE consolidated report.

.DESCRIPTION
    Every tier runs against throwaway, labelled Docker containers built from
    images already on the machine (--pull never). Nothing touches the
    development database, the PostgreSQL service on 5432 or the shared Redis
    container on 6379. The report is docs/test-artifacts/<stamp>-<profile>-campaign/REPORT.md;
    raw artifacts stay in the git-ignored raw/ folder next to it.

    Exit codes (highest precedence first): 4 safety/redaction violation,
    3 harness/build error, 1 test or gate failure, 2 requested tier blocked,
    not run or timed out, 130 interrupted, 0 success.

.EXAMPLE
    pwsh scripts/run-test-campaign.ps1            # quick profile
    pwsh scripts/run-test-campaign.ps1 -Full
    pwsh scripts/run-test-campaign.ps1 -Full -Soak
#>
[CmdletBinding()]
param(
    [switch]$Quick,
    [switch]$Full,
    [switch]$Soak,
    [switch]$Deep,
    [switch]$Live,
    [switch]$ConfirmLive,
    [string[]]$Tiers,
    [string]$CampaignDir,
    [switch]$Resume,
    [string]$Seed,
    [int]$SoakMinutes = 120,
    [string[]]$Variants,
    [switch]$KeepContainers,
    [switch]$NoBuild,
    [switch]$UpdatePerfBaseline,
    [string]$BrowserLedger
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

# ---------------------------------------------------------------- profile
$profileName = if ($Full -and $Soak) { 'full-soak' } elseif ($Full) { 'full' } elseif ($Deep) { 'deep' } elseif ($Soak) { 'soak' } else { 'quick' }
$allTiers = @('build', 'inventory', 'unit', 'property', 'integration', 'e2e', 'micro', 'component', 'load', 'k6', 'capacity', 'chaos', 'soak', 'live')
# Tiers whose runners exist in this revision. Later commits add to this list.
$implemented = @('build', 'inventory', 'unit', 'property', 'integration', 'e2e', 'micro', 'component')
$profileTiers = switch ($profileName) {
    'quick' { @('build', 'inventory', 'unit', 'property', 'integration', 'e2e', 'micro', 'component', 'load', 'capacity', 'chaos') }
    'deep' { @('build', 'inventory', 'unit', 'property', 'integration', 'e2e') }
    'soak' { @('build', 'soak') }
    default { @('build', 'inventory', 'unit', 'property', 'integration', 'e2e', 'micro', 'component', 'load', 'k6', 'capacity', 'chaos') + $(if ($Soak) { @('soak') } else { @() }) }
}
if ($Live) { $profileTiers += 'live' }
if ($Tiers) { $profileTiers = @('build') + @($Tiers | Where-Object { $_ -ne 'build' }) }
$requested = @($profileTiers | Where-Object { $implemented -contains $_ })

# ---------------------------------------------------------------- campaign folder
$started = [DateTimeOffset]::UtcNow
$campaignId = if ($Resume -and $CampaignDir) { Split-Path -Leaf $CampaignDir } else { '{0}-{1}' -f $started.ToString('yyyyMMdd-HHmm'), $profileName }
if (-not $CampaignDir) { $CampaignDir = Join-Path $repo "docs/test-artifacts/$campaignId-campaign" }
$CampaignDir = [IO.Path]::GetFullPath($CampaignDir)
$raw = Join-Path $CampaignDir 'raw'
foreach ($sub in 'trx', 'coverage', 'ledgers', 'logs', 'testresults', 'bdn', 'micro', 'redaction') {
    New-Item -ItemType Directory -Force -Path (Join-Path $raw $sub) | Out-Null
}
$label = "monze.campaign=$campaignId"
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "monze-campaign-$campaignId"
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
$secretsFile = Join-Path $tempRoot 'secrets.json'

$tierRecords = [System.Collections.Generic.List[object]]::new()
$notRun = [System.Collections.Generic.List[string]]::new()
$containers = [System.Collections.Generic.List[string]]::new()
$safetyViolation = $false
$harnessError = $false
$interrupted = $false

function New-Secret([int]$Length = 32) {
    $bytes = [byte[]]::new($Length)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    return ([Convert]::ToHexString($bytes)).ToLowerInvariant()
}

function Add-Tier([string]$Name, [string]$Status, [double]$Seconds, $ExitCode, [string]$Command, [string]$Note) {
    $tierRecords.Add([ordered]@{
            name            = $Name
            status          = $Status
            durationSeconds = [Math]::Round($Seconds, 1)
            exitCode        = $ExitCode
            command         = $Command
            note            = $Note
        })
}

function Format-Argument([string]$Value) {
    if ($Value -match '[\s"]') { return '"' + $Value.Replace('"', '\"') + '"' }
    return $Value
}

function Invoke-Logged {
    param([string]$Name, [string]$File, [string[]]$Arguments, [int]$TimeoutMinutes)
    $log = Join-Path $raw "logs/$Name.log"
    $err = Join-Path $raw "logs/$Name.err.log"
    $argumentLine = ($Arguments | ForEach-Object { Format-Argument $_ }) -join ' '
    $process = Start-Process -FilePath $File -ArgumentList $argumentLine -NoNewWindow -PassThru `
        -RedirectStandardOutput $log -RedirectStandardError $err -WorkingDirectory $repo
    $exited = $process.WaitForExit($TimeoutMinutes * 60 * 1000)
    if (-not $exited) {
        & taskkill /PID $process.Id /T /F 2>$null | Out-Null
        return @{ ExitCode = $null; TimedOut = $true; Command = "$File $argumentLine" }
    }
    $process.WaitForExit()
    return @{ ExitCode = $process.ExitCode; TimedOut = $false; Command = "$File $argumentLine" }
}

function Get-TrxCounts([string]$Path) {
    [xml]$trx = Get-Content -Raw $Path
    $ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
    $results = Select-Xml -Xml $trx -XPath '//t:UnitTestResult' -Namespace $ns
    $outcomes = @($results | ForEach-Object { $_.Node.outcome })
    return @{
        Total   = $outcomes.Count
        Passed  = @($outcomes | Where-Object { $_ -eq 'Passed' }).Count
        Failed  = @($outcomes | Where-Object { $_ -in 'Failed', 'Error', 'Timeout', 'Aborted' }).Count
        Skipped = @($outcomes | Where-Object { $_ -in 'NotExecuted', 'Inconclusive' }).Count
    }
}

function Invoke-TestTier {
    param([string]$Tier, [string]$Project, [string]$Filter, [int]$TimeoutMinutes, [switch]$Strict, [switch]$Coverage)
    $projectName = [IO.Path]::GetFileNameWithoutExtension($Project)
    $results = Join-Path $raw "testresults/$Tier"
    $trxName = "$Tier.$projectName.trx"
    $arguments = @('test', $Project, '-c', 'Release', '--no-build', '--nologo',
        '--logger', "trx;LogFileName=$trxName", '--results-directory', $results,
        '--blame-hang-timeout', '10m', '--blame-hang-dump-type', 'none')
    if ($Filter) { $arguments += @('--filter', $Filter) }
    if ($Coverage) { $arguments += @('--collect', 'Code Coverage;Format=cobertura', '--settings', (Join-Path $repo 'tests/campaign.runsettings')) }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $run = Invoke-Logged -Name "$Tier.$projectName" -File 'dotnet' -Arguments $arguments -TimeoutMinutes $TimeoutMinutes
    $watch.Stop()
    $trx = Get-ChildItem -Path $results -Filter $trxName -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($trx) { Copy-Item $trx.FullName (Join-Path $raw "trx/$trxName") -Force }
    $index = 0
    foreach ($coverageFile in @(Get-ChildItem -Path $results -Filter '*.cobertura.xml' -Recurse -ErrorAction SilentlyContinue)) {
        Copy-Item $coverageFile.FullName (Join-Path $raw "coverage/$Tier.$projectName.$index.cobertura.xml") -Force
        $index++
    }

    if ($run.TimedOut) { return @{ Status = 'TIMEOUT'; Note = "Quá $TimeoutMinutes phút"; Exit = $null; Command = $run.Command; Seconds = $watch.Elapsed.TotalSeconds } }
    if (-not $trx) { return @{ Status = 'ERROR'; Note = 'Không có TRX (build hoặc runner lỗi)'; Exit = $run.ExitCode; Command = $run.Command; Seconds = $watch.Elapsed.TotalSeconds } }
    $counts = Get-TrxCounts (Join-Path $raw "trx/$trxName")
    $note = "$($counts.Passed)/$($counts.Total) pass, $($counts.Failed) fail, $($counts.Skipped) skip"
    $status = if ($counts.Failed -gt 0 -or $run.ExitCode -ne 0) { 'FAIL' } else { 'PASS' }
    if ($Strict -and $counts.Skipped -gt 0) { $status = 'FAIL'; $note += ' (tier strict không được có test skip)' }
    if ($counts.Total -eq 0) { $status = 'FAIL'; $note = 'Không có test nào chạy' }
    return @{ Status = $status; Note = $note; Exit = $run.ExitCode; Command = $run.Command; Seconds = $watch.Elapsed.TotalSeconds }
}

function Invoke-ComponentTier {
    param([int]$TimeoutMinutes)
    # Component scenarios run one after another on an otherwise idle machine
    # and write one monze.artifact.v1 file each to raw/component/.
    $arguments = @((Join-Path $repo 'tests/Monze.Campaign/bin/Release/net10.0/Monze.Campaign.dll'), 'component', '--artifacts', $raw)
    if ($profileName -ne 'quick') { $arguments += '--full' }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $run = Invoke-Logged -Name 'component' -File 'dotnet' -Arguments $arguments -TimeoutMinutes $TimeoutMinutes
    $watch.Stop()
    $results = @(Get-ChildItem -Path (Join-Path $raw 'component') -Filter '*.json' -ErrorAction SilentlyContinue |
        ForEach-Object { Get-Content -Raw $_.FullName | ConvertFrom-Json })
    if ($run.TimedOut) { return @{ Status = 'TIMEOUT'; Note = "Quá $TimeoutMinutes phút"; Exit = $null; Command = $run.Command; Seconds = $watch.Elapsed.TotalSeconds } }
    if ($results.Count -eq 0) { return @{ Status = 'ERROR'; Note = 'Không có artifact component (xem raw/logs/component*.log)'; Exit = $run.ExitCode; Command = $run.Command; Seconds = $watch.Elapsed.TotalSeconds } }
    # A KNOWN_GAP reproduces a registered defect, like an xfail; a gap that no
    # longer reproduces fails the tier until its expectation is updated.
    $failed = @($results | Where-Object { $_.verdict -notin 'PASS', 'KNOWN_GAP', 'BLOCKED' })
    $known = @($results | Where-Object { $_.verdict -eq 'KNOWN_GAP' })
    $blocked = @($results | Where-Object { $_.verdict -eq 'BLOCKED' })
    $status = if ($failed.Count -gt 0) { 'FAIL' } elseif ($blocked.Count -gt 0) { 'BLOCKED' } else { 'PASS' }
    $note = "$(@($results | Where-Object { $_.verdict -eq 'PASS' }).Count)/$($results.Count) PASS, $($known.Count) KNOWN_GAP"
    if ($known.Count -gt 0) { $note += ' (' + (@($known | ForEach-Object { "$($_.id): $(@($_.defectIds) -join '/')" }) -join ', ') + ')' }
    if ($failed.Count -gt 0) { $note += ', FAIL: ' + (@($failed | ForEach-Object { "$($_.id)=$($_.verdict)" }) -join ', ') }
    if ($blocked.Count -gt 0) { $note += ', BLOCKED: ' + (@($blocked | ForEach-Object { $_.id }) -join ', ') }
    return @{ Status = $status; Note = $note; Exit = $run.ExitCode; Command = $run.Command; Seconds = $watch.Elapsed.TotalSeconds }
}

function Invoke-MicroTier {
    param([string[]]$Filters, [int]$TimeoutMinutes)
    # BenchmarkDotNet builds and runs its generated projects outside the repo
    # (artifacts under the campaign temp folder); only the JSON reports and the
    # gate artifact are kept in raw/.
    $bdn = Join-Path $tempRoot 'bdn'
    $artifact = Join-Path $raw 'micro/micro.json'
    $arguments = @((Join-Path $repo 'Monze.Benchmarks/bin/Release/net10.0/Monze.Benchmarks.dll'), '--filter') + $Filters +
        @('--artifacts', $bdn, '--exporters', 'json', '--campaign-artifact', $artifact, '--baseline', (Join-Path $repo 'tests/perf-baseline.json'))
    if ($UpdatePerfBaseline) { $arguments += '--update-baseline' }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $run = Invoke-Logged -Name 'micro' -File 'dotnet' -Arguments $arguments -TimeoutMinutes $TimeoutMinutes
    $watch.Stop()
    foreach ($report in @(Get-ChildItem -Path $bdn -Filter '*-report-full*.json' -Recurse -ErrorAction SilentlyContinue)) {
        Copy-Item $report.FullName (Join-Path $raw "bdn/$($report.Name)") -Force
    }

    if ($run.TimedOut) { return @{ Status = 'TIMEOUT'; Note = "Quá $TimeoutMinutes phút"; Exit = $null; Command = $run.Command; Seconds = $watch.Elapsed.TotalSeconds } }
    if (-not (Test-Path $artifact)) { return @{ Status = 'ERROR'; Note = 'Không có artifact micro (xem raw/logs/micro*.log)'; Exit = $run.ExitCode; Command = $run.Command; Seconds = $watch.Elapsed.TotalSeconds } }
    $micro = Get-Content -Raw $artifact | ConvertFrom-Json
    $status = switch ($micro.verdict) { 'PASS' { 'PASS' } 'BLOCKED' { 'BLOCKED' } default { 'FAIL' } }
    $note = "$($micro.metrics.benchmarks) benchmark, $($micro.metrics.zeroAllocationGates) gate 0 B/op, $($micro.metrics.meanRegressions) mean vượt 1,2 × baseline"
    return @{ Status = $status; Note = $note; Exit = $run.ExitCode; Command = $run.Command; Seconds = $watch.Elapsed.TotalSeconds }
}

function Get-ContainerState([string]$Name) {
    $state = & docker inspect -f '{{.State.StartedAt}}|{{.RestartCount}}|{{.State.Status}}' $Name 2>$null
    if ($LASTEXITCODE -ne 0) { return 'absent' }
    return "$state"
}

function Test-PortFree([int]$Port) {
    return -not (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
}

function Start-CampaignPostgres([string]$Suffix, [string]$Image, [int]$Port, [string]$Password, [string]$Database, [switch]$Durable) {
    $name = "monze-campaign-$campaignId-$Suffix"
    $arguments = @('run', '-d', '--pull', 'never', '--name', $name, '--label', $label,
        '-p', "127.0.0.1:${Port}:5432", '-e', 'POSTGRES_USER=monze', '-e', "POSTGRES_PASSWORD=$Password",
        '-e', "POSTGRES_DB=$Database")
    if (-not $Durable) { $arguments += @('--tmpfs', '/var/lib/postgresql/data') }
    $arguments += @($Image, '-c', "cluster_name=monze-test-$campaignId-$Suffix", '-c', 'max_connections=400',
        '-c', 'shared_preload_libraries=pg_stat_statements')
    if (-not $Durable) { $arguments += @('-c', 'fsync=off', '-c', 'synchronous_commit=off', '-c', 'full_page_writes=off') }
    & docker @arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "docker run failed for $Suffix" }
    $containers.Add($name)
    for ($i = 0; $i -lt 120; $i++) {
        & docker exec $name pg_isready -h 127.0.0.1 -U monze -d $Database 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { return $name }
        Start-Sleep -Milliseconds 500
    }
    throw "PostgreSQL container $Suffix did not become ready"
}

function Start-CampaignRedis([string]$Suffix, [int]$Port, [string]$Password) {
    $name = "monze-campaign-$campaignId-$Suffix"
    & docker run -d --pull never --name $name --label $label -p "127.0.0.1:${Port}:6379" redis:7-alpine `
        redis-server --save '' --appendonly no --requirepass $Password | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "docker run failed for $Suffix" }
    $containers.Add($name)
    for ($i = 0; $i -lt 60; $i++) {
        $pong = & docker exec $name redis-cli --no-auth-warning -a $Password ping 2>$null
        if ("$pong".Trim() -eq 'PONG') { return $name }
        Start-Sleep -Milliseconds 500
    }
    throw "Redis container $Suffix did not become ready"
}

# ---------------------------------------------------------------- run
$branch = (& git rev-parse --abbrev-ref HEAD).Trim()
$commit = (& git rev-parse --short HEAD).Trim()
$dirty = [bool](& git status --porcelain --untracked-files=no)
$sharedRedisBefore = Get-ContainerState 'mezube-redis-1'
$environmentInfo = [ordered]@{}

try {
    # ------------------------------------------------------------ preflight
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $preflightNotes = [System.Collections.Generic.List[string]]::new()
    & docker version --format '{{.Server.Version}}' 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Docker daemon is not reachable.' }
    foreach ($image in 'postgres:17-alpine', 'postgres:16', 'redis:7-alpine') {
        & docker image inspect $image 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Required local image $image is missing (campaign never pulls)." }
    }
    $leftovers = @(& docker ps -aq --filter 'label=monze.campaign')
    if ($leftovers.Count -gt 0) {
        & docker rm -f @leftovers 2>$null | Out-Null
        $preflightNotes.Add("Đã xoá $($leftovers.Count) container campaign còn sót")
    }
    foreach ($port in 55432, 55433, 56379) {
        if (-not (Test-PortFree $port)) { throw "Campaign port $port is already in use." }
    }

    $environmentInfo['Hệ điều hành'] = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
    $environmentInfo['CPU logic'] = "$([Environment]::ProcessorCount)"
    $environmentInfo['.NET SDK'] = (& dotnet --version).Trim()
    $environmentInfo['Docker'] = (& docker version --format '{{.Server.Version}}').Trim()
    $environmentInfo['Image'] = (@('postgres:17-alpine', 'postgres:16', 'redis:7-alpine') | ForEach-Object {
            "$_@" + ((& docker image inspect --format '{{.Id}}' $_).Trim() -replace '^sha256:', '').Substring(0, 12)
        }) -join ', '
    $environmentInfo['Profile'] = $profileName
    $environmentInfo['Seed'] = if ($Seed) { $Seed } else { 'mặc định' }
    $environmentInfo['Namespace dữ liệu'] = "container label $label, DB monze_t_*, cổng 55432/55433/56379"
    $environmentInfo['mezube-redis-1 trước'] = if ($sharedRedisBefore -eq 'absent') { 'không tồn tại' } else { 'đang có (không đụng tới)' }

    $secrets = [ordered]@{
        postgres = New-Secret
        postgres16 = New-Secret
        redis = New-Secret
        canary = "CANARY-$campaignId-$(New-Secret 8)"
    }
    $secrets | ConvertTo-Json | Set-Content -Path $secretsFile -Encoding utf8NoBOM
    & icacls $secretsFile /inheritance:r /grant:r "$($env:USERNAME):F" 2>$null | Out-Null
    $watch.Stop()
    Add-Tier 'preflight' 'PASS' $watch.Elapsed.TotalSeconds 0 $null (($preflightNotes + @("branch $branch")) -join '; ')

    # ------------------------------------------------------------ containers (start while building)
    $needsDb = @($requested | Where-Object { $_ -in 'integration', 'e2e', 'component' }).Count -gt 0
    if ($needsDb) {
        $null = Start-CampaignPostgres 'pg17' 'postgres:17-alpine' 55432 $secrets.postgres 'monze_t_integration'
        $null = Start-CampaignPostgres 'pg16' 'postgres:16' 55433 $secrets.postgres 'monze_t_integration'
        $null = Start-CampaignRedis 'redis' 56379 $secrets.redis
    }

    # ------------------------------------------------------------ build
    if ($NoBuild) {
        Add-Tier 'build' 'PASS' 0 0 $null 'Bỏ qua build (-NoBuild)'
    }
    else {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $restore = Invoke-Logged -Name 'build.restore' -File 'dotnet' -Arguments @('restore', 'Monze.slnx', '--locked-mode', '--nologo') -TimeoutMinutes 10
        $build = if ($restore.ExitCode -eq 0) {
            Invoke-Logged -Name 'build' -File 'dotnet' -Arguments @('build', 'Monze.slnx', '-c', 'Release', '--no-restore', '--nologo') -TimeoutMinutes 10
        }
        else { $restore }
        $watch.Stop()
        if ($build.ExitCode -ne 0) {
            Add-Tier 'build' 'ERROR' $watch.Elapsed.TotalSeconds $build.ExitCode $build.Command 'Restore hoặc build thất bại, xem raw/logs/build*.log'
            $harnessError = $true
            throw 'Build failed.'
        }
        Add-Tier 'build' 'PASS' $watch.Elapsed.TotalSeconds 0 $build.Command 'Release, restore --locked-mode'
    }

    $env:MONZE_CAMPAIGN_ID = $campaignId
    $env:MONZE_CAMPAIGN_STRICT = '1'
    $env:MONZE_CASE_LEDGER_DIR = Join-Path $raw 'ledgers'
    $env:MONZE_CAMPAIGN_ARTIFACTS = $raw
    $env:MONZE_PBT_SCALE = if ($Deep) { '10' } else { '1' }
    if ($Seed) { $env:MONZE_CAMPAIGN_SEED = $Seed }

    # ------------------------------------------------------------ tiers
    foreach ($tier in $allTiers | Where-Object { $_ -ne 'build' }) {
        if ($profileTiers -notcontains $tier) {
            Add-Tier $tier 'NOT_RUN' 0 $null $null "Không thuộc profile $profileName"
            continue
        }
        if ($implemented -notcontains $tier) {
            Add-Tier $tier 'NOT_RUN' 0 $null $null 'Runner chưa được triển khai trong revision này'
            $notRun.Add("Tier $tier được profile yêu cầu nhưng runner chưa có trong revision $commit.")
            continue
        }

        switch ($tier) {
            'inventory' {
                $result = Invoke-TestTier -Tier 'inventory' -Project 'Monze.Tests/Monze.Tests.csproj' -Filter 'FullyQualifiedName~Monze.Tests.Inventory' -TimeoutMinutes 5
            }
            'unit' {
                $result = Invoke-TestTier -Tier 'unit' -Project 'Monze.Tests/Monze.Tests.csproj' -Filter 'FullyQualifiedName!~Monze.Tests.Inventory' -TimeoutMinutes 20 -Coverage
            }
            'property' {
                $result = Invoke-TestTier -Tier 'property' -Project 'tests/Monze.Tests.Property/Monze.Tests.Property.csproj' -TimeoutMinutes $(if ($Deep) { 600 } else { 90 }) -Coverage
            }
            'integration' {
                $env:MONZE_TEST_POSTGRES = "Host=127.0.0.1;Port=55432;Database=monze_t_integration;Username=monze;Password=$($secrets.postgres)"
                $env:MONZE_TEST_POSTGRES_ALT = "Host=127.0.0.1;Port=55433;Database=monze_t_integration;Username=monze;Password=$($secrets.postgres)"
                $env:MONZE_REDIS_CONNECTION = "127.0.0.1:56379,password=$($secrets.redis)"
                try {
                    $result = Invoke-TestTier -Tier 'integration' -Project 'tests/Monze.Tests.Integration/Monze.Tests.Integration.csproj' -TimeoutMinutes 25 -Strict -Coverage
                }
                finally {
                    Remove-Item Env:MONZE_TEST_POSTGRES, Env:MONZE_TEST_POSTGRES_ALT, Env:MONZE_REDIS_CONNECTION -ErrorAction SilentlyContinue
                }
            }
            'e2e' {
                $env:MONZE_TEST_POSTGRES = "Host=127.0.0.1;Port=55432;Database=monze_t_integration;Username=monze;Password=$($secrets.postgres)"
                try {
                    $result = Invoke-TestTier -Tier 'e2e' -Project 'tests/Monze.Tests.E2E/Monze.Tests.E2E.csproj' -TimeoutMinutes 30 -Strict -Coverage
                }
                finally {
                    Remove-Item Env:MONZE_TEST_POSTGRES -ErrorAction SilentlyContinue
                }
            }
            'component' {
                $env:MONZE_TEST_POSTGRES = "Host=127.0.0.1;Port=55432;Database=monze_t_integration;Username=monze;Password=$($secrets.postgres)"
                $env:MONZE_TEST_POSTGRES_ALT = "Host=127.0.0.1;Port=55433;Database=monze_t_integration;Username=monze;Password=$($secrets.postgres)"
                $env:MONZE_REDIS_CONNECTION = "127.0.0.1:56379,password=$($secrets.redis)"
                try {
                    $result = Invoke-ComponentTier -TimeoutMinutes $(if ($profileName -eq 'quick') { 10 } else { 45 })
                }
                finally {
                    Remove-Item Env:MONZE_TEST_POSTGRES, Env:MONZE_TEST_POSTGRES_ALT, Env:MONZE_REDIS_CONNECTION -ErrorAction SilentlyContinue
                }
            }
            'micro' {
                # Quick runs the classes that hold the 0 B/op gates; other profiles run every micro benchmark.
                $filters = if ($profileName -eq 'quick') {
                    @('*MonzeHotPathBenchmarks*', '*IngressCallbackBenchmarks*', '*MonzeCacheHotPathBenchmarks*', '*MonzeMetricsBenchmarks*', '*RateLimiterBenchmarks*')
                }
                else { @('*') }
                $result = Invoke-MicroTier -Filters $filters -TimeoutMinutes $(if ($profileName -eq 'quick') { 20 } else { 60 })
            }
        }

        Add-Tier $tier $result.Status $result.Seconds $result.Exit $result.Command $result.Note
    }
}
catch [System.Management.Automation.PipelineStoppedException] {
    $interrupted = $true
}
catch {
    $harnessError = $true
    $notRun.Add("Harness dừng sớm: $($_.Exception.GetType().Name) (chi tiết ở raw/logs/harness-error.log)")
    ($_ | Out-String) + "`n" + $_.ScriptStackTrace | Set-Content -Path (Join-Path $raw 'logs/harness-error.log') -Encoding utf8NoBOM
}
finally {
    # ------------------------------------------------------------ cleanup (always)
    foreach ($name in 'MONZE_CAMPAIGN_ID', 'MONZE_CAMPAIGN_STRICT', 'MONZE_CASE_LEDGER_DIR', 'MONZE_CAMPAIGN_ARTIFACTS', 'MONZE_PBT_SCALE', 'MONZE_CAMPAIGN_SEED', 'MONZE_TEST_POSTGRES', 'MONZE_TEST_POSTGRES_ALT', 'MONZE_REDIS_CONNECTION') {
        Remove-Item "Env:$name" -ErrorAction SilentlyContinue
    }
    if (-not $KeepContainers) {
        $owned = @(& docker ps -aq --filter "label=$label")
        if ($owned.Count -gt 0) { & docker rm -f @owned 2>$null | Out-Null }
    }
    $remaining = @(& docker ps -aq --filter "label=$label").Count
    $sharedRedisAfter = Get-ContainerState 'mezube-redis-1'
    $sharedRedisUnchanged = $sharedRedisBefore -eq $sharedRedisAfter
    if (-not $sharedRedisUnchanged) { $safetyViolation = $true }
    if ($remaining -gt 0 -and -not $KeepContainers) { $safetyViolation = $true }
    Remove-Item -Recurse -Force (Join-Path $raw 'testresults') -ErrorAction SilentlyContinue

    [ordered]@{
        'Container campaign còn lại'         = if ($KeepContainers) { "$remaining (giữ lại theo -KeepContainers)" } else { "$remaining" }
        'mezube-redis-1 không đổi'           = "$sharedRedisUnchanged"
        'Guard kết nối'                      = 'TestPostgres từ chối cổng 5432 và host không phải loopback; TestRedis từ chối cổng 6379'
        'Thư mục tạm (gồm file secret)'      = 'đã xoá sau khi dựng báo cáo'
        'Lệnh dọn dữ liệu live'              = 'không chạy (campaign không bao giờ gọi cleanup-live-test.ps1)'
    } | ConvertTo-Json | Set-Content -Path (Join-Path $raw 'cleanup.json') -Encoding utf8NoBOM

    $finished = [DateTimeOffset]::UtcNow
    $environmentInfo['mezube-redis-1 sau'] = if ($sharedRedisAfter -eq 'absent') { 'không tồn tại' } elseif ($sharedRedisUnchanged) { 'không đổi' } else { 'ĐÃ THAY ĐỔI' }
    [ordered]@{
        campaignId  = $campaignId
        profile     = $profileName
        startedUtc  = $started.ToString('o')
        finishedUtc = $finished.ToString('o')
        commit      = $commit
        branch      = $branch
        dirty       = $dirty
        environment = $environmentInfo
        tiers       = $tierRecords
        notRun      = $notRun
    } | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $raw 'manifest.json') -Encoding utf8NoBOM

    # ------------------------------------------------------------ report
    $reportDll = Join-Path $repo 'tests/Monze.TestReport/bin/Release/net10.0/Monze.TestReport.dll'
    $reportExit = 3
    if (Test-Path $reportDll) {
        $reportArgs = @($reportDll, '--campaign-dir', $CampaignDir, '--repo-root', $repo, '--secrets-file', $secretsFile)
        if ($BrowserLedger) { $reportArgs += @('--browser-ledger', $BrowserLedger) }
        & dotnet @reportArgs
        $reportExit = $LASTEXITCODE
    }
    if ($reportExit -ne 0 -and -not (Test-Path (Join-Path $CampaignDir 'REPORT.md'))) {
        "# Monze — báo cáo chiến dịch $campaignId`n`n> **Verdict: NOT READY — harness error.** Trình dựng báo cáo không chạy được (exit $reportExit). Xem raw/manifest.json." |
            Set-Content -Path (Join-Path $CampaignDir 'REPORT.md') -Encoding utf8NoBOM
    }
    Remove-Item -Recurse -Force $tempRoot -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------- exit code
$statuses = @($tierRecords | ForEach-Object { $_.status })
$requestedRecords = @($tierRecords | Where-Object { $requested -contains $_.name })
$exitCode = 0
if (@($requestedRecords | Where-Object { $_.status -in 'NOT_RUN', 'BLOCKED', 'TIMEOUT' }).Count -gt 0) { $exitCode = 2 }
if ($statuses -contains 'FAIL') { $exitCode = 1 }
if ($harnessError -or ($statuses -contains 'ERROR') -or $reportExit -eq 3) { $exitCode = 3 }
if ($safetyViolation -or $reportExit -eq 4) { $exitCode = 4 }
if ($interrupted -and $exitCode -lt 3) { $exitCode = 130 }

Write-Host "Campaign $campaignId finished with exit code $exitCode. Report: $(Join-Path $CampaignDir 'REPORT.md')"
exit $exitCode
