[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/validation'))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count -gt 0) {
    throw 'Validation output must be a new or empty directory so stale results cannot pass validation.'
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$testResults = Join-Path $output 'tests'
New-Item -ItemType Directory -Path $testResults | Out-Null
$started = [DateTimeOffset]::UtcNow
$previousEnvironment = @{}
$isolatedEnvironment = @{
    IRONPAW_CONFIG = (Join-Path $repository 'docs/examples/settings.example.json')
    OPENAI_API_KEY = ''; ANTHROPIC_API_KEY = ''; OLLAMA_API_KEY = ''; LMSTUDIO_API_KEY = ''
    IRONPAW_PROVIDER = ''; IRONPAW_MODEL = ''
    IRONPAW_CODEGEN_PROVIDER = ''; IRONPAW_CODEGEN_MODEL = ''
    IRONPAW_REVIEWER_PROVIDER = ''; IRONPAW_REVIEWER_MODEL = ''
    IRONPAW_WEB_BROWSER_PROVIDER = ''; IRONPAW_WEB_BROWSER_MODEL = ''
    IRONPAW_LLM_CONTEXT_WINDOW_TOKENS = ''; IRONPAW_LLM_SAFETY_MARGIN_TOKENS = ''
    IRONPAW_LLM_MIN_OUTPUT_TOKENS_PER_REQUEST = ''; IRONPAW_LLM_MAX_AUTO_CONTINUATION_PASSES = ''
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'; DOTNET_NOLOGO = '1'
}
function Invoke-ValidationDotnet([string[]]$Arguments) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $output 'validation.log') -Append
    if ($LASTEXITCODE -ne 0) { throw ('Validation command failed: dotnet ' + ($Arguments -join ' ')) }
}
function Get-ValidationSourceManifest {
    $sourceFiles = @(& git ls-files --cached --others --exclude-standard | Sort-Object -Unique)
    if ($LASTEXITCODE -ne 0) { throw 'The source manifest could not be recorded.' }
    foreach ($file in $sourceFiles) {
        $sourcePath = Join-Path $repository $file
        if (Test-Path -LiteralPath $sourcePath -PathType Leaf) {
            [ordered]@{ path = $file; sha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant() }
        }
    }
}
Push-Location $repository
try {
    foreach ($name in $isolatedEnvironment.Keys) {
        $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, $isolatedEnvironment[$name])
    }
    $sdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'The pinned .NET SDK is not installed.' }
    $revision = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'The source revision could not be recorded.' }
    $dirty = -not [string]::IsNullOrWhiteSpace((& git status --porcelain=v1 | Out-String))
    $sourceManifest = @(Get-ValidationSourceManifest) | ConvertTo-Json -Depth 4
    $sourceManifest | Set-Content -LiteralPath (Join-Path $output 'source-manifest.json') -Encoding utf8
    Invoke-ValidationDotnet -Arguments @('restore', 'IronPaw.sln', '-p:NuGetAuditMode=all', '-warnaserror')
    Invoke-ValidationDotnet -Arguments @('build', 'IronPaw.sln', '--configuration', 'Release', '--no-restore', '-warnaserror')
    Invoke-ValidationDotnet -Arguments @('test', 'IronPaw.sln', '--configuration', 'Release', '--no-build', '--no-restore', '--maxcpucount:1', '--logger', 'trx', '--results-directory', $testResults)
    $results = @(Get-ChildItem -LiteralPath $testResults -Filter '*.trx')
    $expectedProjects = @(Get-ChildItem -LiteralPath (Join-Path $repository 'tests') -Recurse -Filter '*.Tests.csproj').Count
    if ($expectedProjects -eq 0 -or $results.Count -ne $expectedProjects) { throw 'Every test project must produce a fresh TRX report.' }
    $total = 0; $passed = 0; $failed = 0; $skipped = 0
    foreach ($result in $results) {
        [xml]$document = Get-Content -LiteralPath $result.FullName -Raw
        $counters = $document.TestRun.ResultSummary.Counters
        if ($document.TestRun.ResultSummary.outcome -ne 'Completed' -or [int]$counters.total -le 0) {
            throw ('A test report is empty or incomplete: ' + $result.Name)
        }
        $total += [int]$counters.total; $passed += [int]$counters.passed
        $failed += [int]$counters.failed; $skipped += [int]$counters.notExecuted
    }
    if ($failed -ne 0 -or $skipped -ne 0 -or $total -ne $passed) { throw 'All tests must pass; skipped tests do not satisfy the release check.' }
    Invoke-ValidationDotnet -Arguments @('src/IronPaw.Cli/bin/Release/net10.0/IronPaw.Cli.dll', '--validate-config', 'docs/examples/settings.example.json')
    Invoke-ValidationDotnet -Arguments @('src/IronPaw.Cli/bin/Release/net10.0/IronPaw.Cli.dll', '--validate-evaluation-config', 'docs/examples/live-evaluation.example.json')
    Invoke-ValidationDotnet -Arguments @('src/IronPaw.Cli/bin/Release/net10.0/IronPaw.Cli.dll', '--inspect-mcp-config', 'docs/examples/mcp.example.json')
    Invoke-ValidationDotnet -Arguments @('src/IronPaw.Cli/bin/Release/net10.0/IronPaw.Cli.dll', '--inspect-browser-config', 'docs/examples/browser.example.json')
    if ($sourceManifest -cne (@(Get-ValidationSourceManifest) | ConvertTo-Json -Depth 4)) {
        throw 'Source files changed during validation. Preserve this evidence and rerun against a stable source tree.'
    }
    [ordered]@{ schemaVersion = 1; sourceRevision = $revision; workingTreeDirty = $dirty; sdk = $sdk; startedAt = $started; completedAt = [DateTimeOffset]::UtcNow
        testProjects = $results.Count; total = $total; passed = $passed; failed = $failed; skipped = $skipped
        liveBaselineQualification = 'NOT_ESTABLISHED'; billableProvidersEnabled = $false
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
    Write-Host "Validation passed: $passed tests across $($results.Count) projects. Evidence: $output"
}
finally {
    foreach ($name in $previousEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name]) }
    Pop-Location
}
