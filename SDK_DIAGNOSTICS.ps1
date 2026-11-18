# Sunrise File Manager SDK path diagnostic: read-only, no installs, no builds.
# Run from the unpacked source root: powershell -NoProfile -File .\SDK_DIAGNOSTICS.ps1
param([string]$ProjectRoot = $PSScriptRoot)
$ErrorActionPreference = 'Continue'
Write-Output '===== Sunrise .NET SDK discovery (read-only) ====='
Write-Output "PROJECT_ROOT=$ProjectRoot"
if (-not (Test-Path -LiteralPath (Join-Path $ProjectRoot 'global.json'))) {
    Write-Output 'WARNING: global.json not found in ProjectRoot. SDK project selection cannot be fully checked.'
}
$candidates = [System.Collections.Generic.List[string]]::new()
function Add-Candidate([string]$path) {
    if (-not [string]::IsNullOrWhiteSpace($path)) {
        $resolved = [Environment]::ExpandEnvironmentVariables($path.Trim('"'))
        if ([IO.Path]::GetExtension($resolved) -ne '.exe') { $resolved = Join-Path $resolved 'dotnet.exe' }
        if (-not $script:candidates.Contains($resolved)) { $script:candidates.Add($resolved) }
    }
}
Add-Candidate $env:SUNRISE_DOTNET
Add-Candidate $env:DOTNET_ROOT_X64
Add-Candidate $env:DOTNET_ROOT
Add-Candidate $env:DOTNET_INSTALL_DIR
if ($env:ProgramW6432) { Add-Candidate (Join-Path $env:ProgramW6432 'dotnet') }
if ($env:ProgramFiles) { Add-Candidate (Join-Path $env:ProgramFiles 'dotnet') }
if ($env:LOCALAPPDATA) { Add-Candidate (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet') }
if ($env:USERPROFILE) { Add-Candidate (Join-Path $env:USERPROFILE '.dotnet') }
$programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
if ($programFilesX86) { Add-Candidate (Join-Path $programFilesX86 'dotnet') }
foreach ($cmd in (Get-Command dotnet -All -CommandType Application -ErrorAction SilentlyContinue)) { if ($cmd.Source) { Add-Candidate $cmd.Source } }
# The registry can describe non-default installations. Read only.
foreach ($root in @('HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x86', 'HKCU:\SOFTWARE\dotnet\Setup\InstalledVersions\x64')) {
    try {
        $p = Get-ItemProperty -LiteralPath $root -Name InstallLocation -ErrorAction Stop
        if ($p.InstallLocation) { Add-Candidate $p.InstallLocation }
    } catch { }
}
$found = 0
$usable = 0
foreach ($exe in $candidates) {
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { continue }
    $found++
    Write-Output "`nHOST=$exe"
    try {
        $list = @(& $exe --list-sdks 2>&1)
        Write-Output 'INSTALLED_SDKS:'
        if (-not $list.Count) { Write-Output '  (none visible to this host)' }
        foreach ($line in $list) { Write-Output "  $line" }
        $matchesTen = @($list | Where-Object { "$_" -match '^10\.0\.[0-9]+\s+\[' })
        if (-not $matchesTen.Count) { Write-Output 'SDK10_VISIBLE=NO'; continue }
        Write-Output 'SDK10_VISIBLE=YES'
        if (-not (Test-Path -LiteralPath $ProjectRoot -PathType Container)) { continue }
        Push-Location -LiteralPath $ProjectRoot
        try { $selected = @(& $exe --version 2>&1); $exitCode = $LASTEXITCODE }
        finally { Pop-Location }
        $selectedVersion = (($selected | Where-Object { "$_" -match '^10\.0\.[0-9]+$' } | Select-Object -First 1) -as [string])
        if ($exitCode -eq 0 -and $selectedVersion) {
            $usable++
            Write-Output "PROJECT_SELECT=PASS ($selectedVersion)"
            Write-Output "NEXT_STEP: set SUNRISE_DOTNET=$exe"
        } else {
            Write-Output 'PROJECT_SELECT=FAILED; global.json mismatch, host SDK isolation or environment restrictions possible.'
            $selected | Select-Object -First 8 | ForEach-Object { Write-Output "  $_" }
        }
    } catch { Write-Output "PROBE_ERROR=$($_.Exception.Message)" }
}
Write-Output "`nSUMMARY HOSTS_FOUND=$found COMPATIBLE_HOSTS=$usable"
if (-not $found) { Write-Output 'NEXT: locate the dotnet.exe installed with .NET SDK 10 (possibly a custom drive or Visual Studio-managed root).' }
elseif (-not $usable) { Write-Output 'NEXT: share this output; no SDK reinstallation is implied.' }
Write-Output '===== end ====='
