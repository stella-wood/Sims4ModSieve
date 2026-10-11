$ErrorActionPreference = 'Stop'
$reviewDir = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')) 'artifacts/conflict-review'
New-Item -ItemType Directory -Path $reviewDir -Force | Out-Null
$env:SIMS4_CONFLICT_REVIEW_DIR = $reviewDir
& (Join-Path $PSScriptRoot 'dotnet.cmd') test (Join-Path $PSScriptRoot '../tests/Sims4ModSieve.Desktop.Tests/Sims4ModSieve.Desktop.Tests.csproj') -c Release --no-restore --nologo -m:1 --filter FullyQualifiedName~ConflictPageRenderTests
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Get-ChildItem -LiteralPath $reviewDir -Filter '*.png' | Select-Object FullName,Length
