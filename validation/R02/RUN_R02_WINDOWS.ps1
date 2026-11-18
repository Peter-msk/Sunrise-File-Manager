# Only run explicitly on Windows. Does not build installers or run GitHub Actions.
param([switch]$NoPause)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') { throw 'Windows 실행 환경이 필요합니다.' }
$dotnet = Get-Command dotnet -ErrorAction Stop
$project = Join-Path $root 'source/tests/CrossPaneFolderMoveSmoke/CrossPaneFolderMoveSmoke.csproj'
$app = Join-Path $root 'source/Sunrise.FileManager/Sunrise.FileManager.csproj'
Write-Host '[R02] Windows .NET10 / WPF compilation (only changed-product impact)'
& $dotnet.Source build $app --configuration Release --nologo
if ($LASTEXITCODE -ne 0) { throw "WPF build failed: $LASTEXITCODE" }
Write-Host '[R02] Isolated temp-only local folder move smoke'
& $dotnet.Source run --project $project --configuration Release --no-launch-profile --nologo
if ($LASTEXITCODE -ne 0) { throw "Folder move smoke failed: $LASTEXITCODE" }
Write-Host '[R02] PASS: native local folder move checks. DPI and cloud remain separately unverified.'
