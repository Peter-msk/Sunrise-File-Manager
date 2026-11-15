# Sunrise File Manager v1.8.2 R00 targeted Windows release-blocker checks.
# No install, no updater, no GitHub Actions, no live business folder writes.
# Run with: powershell -NoProfile -ExecutionPolicy Bypass -File .\validation\R05\RUN_WINDOWS_GATES.ps1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
Set-Location -LiteralPath $root
# Version gate: never record native PASS against a stale candidate.
$expectedProductVersion = '1.8.2'
$actualProductVersion = (Get-Content -LiteralPath (Join-Path $root 'installer/RELEASE_VERSION.txt') -Raw).Trim()
if ($actualProductVersion -ne $expectedProductVersion) {
    throw "Wrong source candidate: expected $expectedProductVersion, got '$actualProductVersion'."
}
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Windows-only WPF/native smoke. Do not report this gate PASS on non-Windows hosts.'
}
$dotnetVersion = (& dotnet --version 2>&1 | Select-Object -First 1).ToString().Trim()
if ($LASTEXITCODE -ne 0 -or -not $dotnetVersion.StartsWith('10.')) {
    throw "Requires .NET 10 SDK; found '$dotnetVersion'."
}
$evidence = Join-Path ([System.IO.Path]::GetTempPath()) ('Sunrise_FM_R05_GATES_' + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $evidence)
$results = [System.Collections.Generic.List[object]]::new()
function Invoke-CheckedDotnet {
    param([Parameter(Mandatory=$true)][string]$Name,
          [Parameter(Mandatory=$true)][string[]]$Arguments,
          [string]$ResultJson,
          [string]$ExpectedMarker)
    $log = Join-Path $evidence ($Name + '.log')
    Write-Host "[RUN] $Name"
    & dotnet @Arguments *> $log
    $code = $LASTEXITCODE
    $status = 'FAIL'
    $reason = if ($code -ne 0) { "dotnet exit code $code" } else { '' }
    if ($code -eq 0 -and $ResultJson) {
        if (Test-Path -LiteralPath $ResultJson) {
            try {
                $json = Get-Content -LiteralPath $ResultJson -Raw | ConvertFrom-Json
                if ($json.status -eq 'PASS') { $status = 'PASS' } else { $reason = 'UI_RESULT.json not PASS' }
            } catch { $reason = 'Invalid UI_RESULT.json: ' + $_.Exception.Message }
        } else { $reason = 'Missing UI_RESULT.json' }
    } elseif ($code -eq 0 -and $ExpectedMarker) {
        if (Select-String -Path $log -SimpleMatch -Pattern $ExpectedMarker -Quiet) {
            $status = 'PASS'
        } else { $reason = "Missing native test marker: $ExpectedMarker" }
    } elseif ($code -eq 0) { $status = 'PASS' }
    $results.Add([PSCustomObject]@{ name=$Name; status=$status; code=$code; log=$log; reason=$reason })
    if ($status -ne 'PASS') { throw "Gate $Name failed: $reason. Log: $log" }
    Write-Host "[PASS] $Name"
}
$overall = 'FAIL'
try {
    Invoke-CheckedDotnet -Name '01-WPF-Release-Build' -Arguments @('build','source/Sunrise.FileManager/Sunrise.FileManager.csproj','-c','Release','--nologo')
    Invoke-CheckedDotnet -Name '02-R03-Transfer-Safety' -Arguments @('run','--project','source/tests/LogicTests/LogicTests.csproj','-c','Release','--','--v181-r03-transfer-safety') -ExpectedMarker 'R03 FILE TRANSFER SAFETY:'
    Invoke-CheckedDotnet -Name '03-EIASS-Production-Folders' -Arguments @('run','--project','source/tests/EiaWorkspaceProductionSmoke/EiaWorkspaceProductionSmoke.csproj','-c','Release') -ExpectedMarker 'EIA_WORKSPACE_PRODUCTION_LOCAL_WINDOWS_SMOKE_PASS'
    foreach ($item in @(
       @{name='04-R05-Batch-Rename';flag='--r05-batch-rename-only'},
       @{name='05-EIASS-PDF-Rename';flag='--eiass-rename-only'},
       @{name='06-R04-Recursive-Pages';flag='--r04-recursive-page-only'})) {
        $output = Join-Path $evidence $item.name
        [void](New-Item -ItemType Directory -Path $output)
        Invoke-CheckedDotnet -Name $item.name -Arguments @('run','--project','source/tests/UiSmokeTests/UiSmokeTests.csproj','-c','Release','--',$item.flag,$output) -ResultJson (Join-Path $output 'UI_RESULT.json')
    }
    $overall = 'PASS_TARGETED_WINDOWS_CODE'
} finally {
    $summary = [PSCustomObject]@{
        product='Sunrise File Manager'; version='1.8.2 R00';
        evidence_kind='ISOLATED_WINDOWS_CODE_SMOKE_ONLY';
        overall=$overall; dotnet=$dotnetVersion;
        executed_utc=[DateTimeOffset]::UtcNow.ToString('O');
        evidence_root=$evidence; results=@($results.ToArray());
        not_verified=@('100-175% DPI visual review','live cloud/Ollama',
            'SMB/NAS operations','signed EXE/Setup','upgrade and rollback')
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'R05_WINDOWS_GATE_RESULT.json') -Encoding UTF8
    Write-Host "[EVIDENCE] $evidence"
    Write-Host "[FINAL] $overall (not an official release approval)"
}
