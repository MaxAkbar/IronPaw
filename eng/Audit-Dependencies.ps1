[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/dependency-audit'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count -gt 0) {
    throw 'Dependency evidence requires a new or empty directory.'
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
function Get-PackageFindings($Report) {
    foreach ($project in $Report.projects) {
        if ($project.PSObject.Properties.Name -notcontains 'frameworks') { continue }
        foreach ($framework in $project.frameworks) {
            foreach ($kind in @('topLevelPackages', 'transitivePackages')) {
                if ($framework.PSObject.Properties.Name -notcontains $kind) { continue }
                foreach ($package in $framework.$kind) {
                    [ordered]@{ project = $project.path; framework = $framework.framework; kind = $kind; package = $package }
                }
            }
        }
    }
}
Push-Location $repository
try {
    $reports = @{}
    foreach ($kind in @('outdated', 'vulnerable')) {
        $json = & dotnet list IronPaw.sln package "--$kind" --include-transitive --no-restore --format json --source https://api.nuget.org/v3/index.json
        if ($LASTEXITCODE -ne 0) { throw "Dependency $kind query failed." }
        $json | Set-Content -LiteralPath (Join-Path $output "$kind.json") -Encoding utf8
        $report = ($json -join "`n") | ConvertFrom-Json
        if ($report.projects.Count -eq 0) { throw 'Dependency evidence contains no projects.' }
        if ($report.PSObject.Properties.Name -contains 'logs' -and @($report.logs | Where-Object level -eq 'error').Count -gt 0) { throw 'NuGet reported an audit error.' }
        $reports[$kind] = @(Get-PackageFindings $report)
    }
    [ordered]@{ sourceRevision = (& git rev-parse HEAD).Trim(); completedAt = [DateTimeOffset]::UtcNow
        outdated = $reports['outdated']; vulnerabilities = $reports['vulnerable']
        policy = 'Outdated packages are review findings. Known vulnerable dependencies block qualification. No vulnerability exception is implicit.'
    } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
    if ($reports['vulnerable'].Count -gt 0) { throw 'Known vulnerable dependencies block release qualification; inspect the retained audit.' }
    Write-Host "Dependency audit passed: no known vulnerabilities; $($reports['outdated'].Count) outdated package occurrences retained for review."
}
finally { Pop-Location }
