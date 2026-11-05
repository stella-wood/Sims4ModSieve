param([string]$Solution)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Solution) { $Solution = Join-Path $root 'Sims4ModSieve.sln' }
$json = & "$PSScriptRoot\dotnet.cmd" list $Solution package --no-restore --vulnerable --include-transitive --format json --output-version 1
if ($LASTEXITCODE -ne 0) { throw 'NuGet vulnerability query failed.' }
$raw = $json -join "`n"
$report = $raw | ConvertFrom-Json
$record = Join-Path $root 'artifacts/release/vulnerabilities.json'
New-Item -ItemType Directory -Force (Split-Path $record -Parent) | Out-Null
$raw | Set-Content -LiteralPath $record -Encoding utf8
if ($report.version -ne 1 -or -not $report.sources -or -not $report.projects -or $report.problems -or
    @($report.projects | Where-Object { $_.problems -or @($_.frameworks | Where-Object problems).Count -gt 0 }).Count -gt 0) {
    throw 'NuGet vulnerability query returned an incomplete report.'
}
$findings = @($report.projects | ForEach-Object {
    $project = $_.path
    @($_.frameworks | ForEach-Object {
        @($_.topLevelPackages) + @($_.transitivePackages) | Where-Object { $_ -and $_.vulnerabilities } |
            ForEach-Object { [pscustomobject]@{ Project = $project; Package = $_.id; Version = $_.resolvedVersion; Advisories = $_.vulnerabilities } }
    })
})
if ($findings.Count -gt 0) {
    $findings | Format-List | Out-String | Write-Host
    throw "$($findings.Count) vulnerable package occurrence(s) found."
}
Write-Host 'NuGet reports no vulnerable packages, including transitive dependencies.'
