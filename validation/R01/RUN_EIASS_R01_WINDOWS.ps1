# v1.8.2 R01: isolated EIASS direct-create Windows acceptance preflight.
# Does NOT run GitHub Actions, install, upgrade, publish, or touch real business folders.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Windows .NET 10 WPF execution is required. Do not claim a native PASS on Linux.'
}
$version = (Get-Content -LiteralPath (Join-Path $root 'installer\RELEASE_VERSION.txt') -Raw).Trim()
if ($version -ne '1.8.2') { throw "Unexpected installer version: $version" }
$SDK = (& dotnet --version 2>&1 | Select-Object -First 1).ToString().Trim()
if ($LASTEXITCODE -ne 0 -or -not $SDK.StartsWith('10.')) { throw ".NET 10 SDK required, detected: $SDK" }
$evidence = Join-Path ([IO.Path]::GetTempPath()) ('Sunrise_FM_EIASS_R01_' + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $evidence)
$results = [System.Collections.Generic.List[object]]::new()
$overall = 'FAIL'
function Invoke-Stage([string]$Stage, [string[]]$CommandArgs, [string]$Expected) {
    $log = Join-Path $evidence ($Stage + '.log')
    & dotnet @CommandArgs *> $log
    $code = $LASTEXITCODE
    $markerOk = -not $Expected -or (Select-String -LiteralPath $log -SimpleMatch -Pattern $Expected -Quiet)
    $status = if ($code -eq 0 -and $markerOk) { 'PASS' } else { 'FAIL' }
    $results.Add([PSCustomObject]@{step=$Stage;status=$status;exit_code=$code;log=$log})
    if ($status -ne 'PASS') { throw "$Stage failed; inspect $log" }
    Write-Host "[PASS] $Stage"
}
try {
    Invoke-Stage '01-WPF-Compile' @('build',(Join-Path $root 'source\Sunrise.FileManager\Sunrise.FileManager.csproj'),'-c','Release','--nologo') ''
    Invoke-Stage '02-Native-Directory-Only' @('run','--project',(Join-Path $root 'source\tests\EiaWorkspaceProductionSmoke\EiaWorkspaceProductionSmoke.csproj'),'-c','Release') 'EIA_WORKSPACE_PRODUCTION_LOCAL_WINDOWS_SMOKE_PASS'
    $overall = 'PASS_CODE_ONLY'
} finally {
    [PSCustomObject]@{
        product='Sunrise File Manager';version='1.8.2 R01';gate='EIASS_SINGLE_SCREEN'
        status=$overall;windows_sdk=$SDK;execution_utc=[DateTimeOffset]::UtcNow.ToString('O')
        evidence_root=$evidence;steps=@($results.ToArray())
        pending=@('actual WPF visual inspection 100/125/150/175 DPI',
                  'manual click-through and permissions/cancellation',
                  'installer signing and update integration')
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'EIASS_R01_RESULT.json') -Encoding UTF8
    Write-Host "Evidence: $evidence"
}
