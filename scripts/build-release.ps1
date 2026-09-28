[CmdletBinding()]
param(
    [switch]$BuildTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$appProject = Join-Path $projectRoot 'Monze.csproj'
$testProject = Join-Path $projectRoot 'Monze.Tests\Monze.Tests.csproj'
function Invoke-ReleaseBuild([string]$projectPath) {
    & dotnet build $projectPath --configuration Release --framework net10.0 --no-restore `
        -p:BuildProjectReferences=true -p:BuildInParallel=false
    if ($LASTEXITCODE -ne 0) {
        throw "Release build failed: $projectPath"
    }
}

Push-Location $projectRoot
try {
    Invoke-ReleaseBuild $appProject

    if ($BuildTests) {
        Invoke-ReleaseBuild $testProject
    }

    Write-Host 'Release build completed with explicit net10.0 framework and published Mezon.Net.Sdk 1.6.0 package references.'
}
finally {
    Pop-Location
}
