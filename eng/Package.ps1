[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ValidationDirectory,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/package'),
    [switch]$AllowDirty
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    throw 'The desktop release bundle must be built and smoke-tested on Windows x64.'
}
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$validation = [IO.Path]::GetFullPath($ValidationDirectory)
if ((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count -gt 0) { throw 'Packaging requires a new or empty output directory.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$payload = Join-Path $output 'IronPaw-win-x64'
New-Item -ItemType Directory -Path $payload | Out-Null
function Invoke-Publish([string[]]$Arguments) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $output 'publish.log') -Append
    if ($LASTEXITCODE -ne 0) { throw ('Publish failed: dotnet ' + ($Arguments -join ' ')) }
}
function Invoke-PackagedApp([string]$Executable, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    foreach ($name in @('OPENAI_API_KEY', 'ANTHROPIC_API_KEY', 'OLLAMA_API_KEY', 'LMSTUDIO_API_KEY', 'IRONPAW_CONFIG', 'IRONPAW_PROVIDER', 'IRONPAW_MODEL')) { $start.Environment.Remove($name) | Out-Null }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw 'The owned release smoke process exceeded its deadline.' }
        $stdout.GetAwaiter().GetResult() | Add-Content -LiteralPath (Join-Path $output 'smoke.log')
        $stderr.GetAwaiter().GetResult() | Add-Content -LiteralPath (Join-Path $output 'smoke.log')
        if ($process.ExitCode -ne 0) { throw ('Packaged application failed: ' + [IO.Path]::GetFileName($Executable)) }
    }
    finally { $process.Dispose() }
}
Push-Location $repository
try {
    $summary = Get-Content -LiteralPath (Join-Path $validation 'summary.json') -Raw | ConvertFrom-Json
    $manifest = @(Get-Content -LiteralPath (Join-Path $validation 'source-manifest.json') -Raw | ConvertFrom-Json)
    $revision = (& git rev-parse HEAD).Trim()
    $dirty = -not [string]::IsNullOrWhiteSpace((& git status --porcelain=v1 | Out-String))
    if ($dirty -and -not $AllowDirty) { throw 'Release packages require a clean source tree. Use -AllowDirty only for an explicitly identified local draft.' }
    if ($summary.sourceRevision -ne $revision -or $summary.failed -ne 0 -or $summary.skipped -ne 0 -or $summary.total -ne $summary.passed -or $summary.total -le 0) {
        throw 'Packaging requires a passing validation for this source revision.'
    }
    $currentPaths = @(& git ls-files --cached --others --exclude-standard | Sort-Object -Unique | Where-Object { Test-Path -LiteralPath (Join-Path $repository $_) -PathType Leaf })
    if ($LASTEXITCODE -ne 0 -or $currentPaths.Count -ne $manifest.Count) { throw 'Source membership changed after validation.' }
    foreach ($entry in $manifest) {
        if ($entry.path -notin $currentPaths -or (Get-FileHash -LiteralPath (Join-Path $repository $entry.path) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry.sha256) {
            throw ('Source changed after validation: ' + $entry.path)
        }
    }
    foreach ($hostName in @('Cli', 'Desktop')) {
        $project = "src/IronPaw.$hostName/IronPaw.$hostName.csproj"
        $target = Join-Path $payload $hostName.ToLowerInvariant()
        Invoke-Publish -Arguments @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $target,
            '-warnaserror', '-p:DebugType=None', '-p:DebugSymbols=false', '-p:NuGetAuditMode=all')
    }
    $browserCatalog = Get-Content -LiteralPath (Join-Path $payload 'cli/.playwright/package/browsers.json') -Raw | ConvertFrom-Json
    $browserRevision = ($browserCatalog.browsers | Where-Object name -eq 'chromium-headless-shell').revision
    $cache = if ([string]::IsNullOrWhiteSpace($env:PLAYWRIGHT_BROWSERS_PATH)) { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ms-playwright' } else { $env:PLAYWRIGHT_BROWSERS_PATH }
    $runtime = Join-Path $cache "chromium_headless_shell-$browserRevision"
    if (-not (Test-Path -LiteralPath $runtime)) { throw 'Install the pinned Chromium headless runtime before packaging; see docs/releases.md.' }
    $executables = @(Get-ChildItem -LiteralPath $runtime -Recurse -File -Filter chrome-headless-shell.exe)
    if ($executables.Count -ne 1) { throw 'The pinned browser distribution is ambiguous or incomplete.' }
    $browserOutput = Join-Path $payload 'browser-runtime'
    New-Item -ItemType Directory -Path $browserOutput | Out-Null
    Get-ChildItem -LiteralPath $executables[0].DirectoryName -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $browserOutput -Recurse }
    Copy-Item -LiteralPath (Join-Path $repository 'eng/Package-Readme.md') -Destination (Join-Path $payload 'README.md')
    Copy-Item -LiteralPath (Join-Path $repository 'docs') -Destination (Join-Path $payload 'docs') -Recurse
    if (Test-Path -LiteralPath (Join-Path $repository 'LICENSE')) { Copy-Item -LiteralPath (Join-Path $repository 'LICENSE') -Destination $payload }
    Copy-Item -LiteralPath (Join-Path $repository 'eng/Verify-Package.ps1') -Destination $payload
    Set-Content -LiteralPath (Join-Path $payload 'Start-IronPaw.cmd') -Encoding ascii -Value '@start "" "%~dp0desktop\IronPaw.Desktop.exe"'
    $obsolete = @(Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object { $_.Name -match '(?i)(SQLitePCLRaw|Microsoft\.Data\.Sqlite|System\.Data\.SQLite|e_sqlite3|xunit).*\.(dll|so|dylib)$' })
    foreach ($file in Get-ChildItem -LiteralPath $payload -Recurse -File -Filter '*.deps.json') {
        $dependencies = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        if (@($dependencies.libraries.PSObject.Properties.Name | Where-Object { $_ -match '(?i)^(Microsoft\.Data\.Sqlite|SQLitePCLRaw|System\.Data\.SQLite|xunit)' }).Count -gt 0) { throw 'Obsolete persistence or test packages entered the application bundle.' }
    }
    if ($obsolete.Count -gt 0) { throw 'Obsolete SQLite application-storage or xUnit assets entered the bundle.' }
    Invoke-PackagedApp -Executable (Join-Path $payload 'cli/IronPaw.Cli.exe') -Arguments @('--help')
    foreach ($hostName in @('Cli', 'Desktop')) {
        $evidence = Join-Path $output ('smoke/' + $hostName.ToLowerInvariant())
        Invoke-PackagedApp -Executable (Join-Path $payload ($hostName.ToLowerInvariant() + "/IronPaw.$hostName.exe")) -Arguments @('--smoke-test', $evidence)
        $smoke = Get-Content -LiteralPath (Join-Path $evidence 'smoke-report.json') -Raw | ConvertFrom-Json
        if (-not $smoke.completed -or -not $smoke.stateReopened -or -not $smoke.toolArtifactVerified -or $smoke.liveProviderCalled) { throw 'Published-host smoke qualification failed.' }
        if ($hostName -eq 'Cli' -and -not $smoke.browserInstallationVerified) { throw 'The packaged JavaScript browser was not exercised.' }
    }
    foreach ($entry in $manifest) {
        if ((Get-FileHash -LiteralPath (Join-Path $repository $entry.path) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry.sha256) { throw 'Source changed while publishing. Preserve this evidence and rerun validation.' }
    }
    $afterPaths = @(& git ls-files --cached --others --exclude-standard | Sort-Object -Unique | Where-Object { Test-Path -LiteralPath (Join-Path $repository $_) -PathType Leaf })
    if ($afterPaths.Count -ne $currentPaths.Count -or (Compare-Object $currentPaths $afterPaths)) { throw 'Source membership changed while publishing.' }
    $files = @(Get-ChildItem -LiteralPath $payload -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetRelativePath($payload, $_.FullName).Replace('\', '/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $release = [ordered]@{ schemaVersion = 1; sourceRevision = $revision; draft = $dirty; sdk = $summary.sdk; validatedTests = $summary.passed
        sourceManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $validation 'source-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        runtime = 'win-x64 self-contained'; browserRevision = $browserRevision; files = $files
        liveBaselineQualification = 'NOT_ESTABLISHED'; hostedCiQualification = 'Requires an actual hosted workflow result'
    }
    $release | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $payload 'manifest.json') -Encoding utf8
    & (Join-Path $PSScriptRoot 'Verify-Package.ps1') -Directory $payload
    $archive = Join-Path $output 'IronPaw-win-x64.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $expected = @{}
        foreach ($entry in $files) { $expected[$entry.path] = $entry }
        $entries = @($zip.Entries | Where-Object { $_.Name.Length -gt 0 })
        if ($entries.Count -ne $files.Count + 1) { throw 'Archive membership does not match the payload.' }
        foreach ($entry in $entries) {
            if ($entry.FullName -eq 'manifest.json') { $hash = (Get-FileHash -LiteralPath (Join-Path $payload 'manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant() }
            elseif ($expected.ContainsKey($entry.FullName)) { $hash = $expected[$entry.FullName].sha256 }
            else { throw 'Archive contains an unidentified file.' }
            $stream = $entry.Open()
            try { if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() -cne $hash) { throw 'Archive content hash mismatch.' } }
            finally { $stream.Dispose() }
        }
    }
    finally { $zip.Dispose() }
    $archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$archiveHash  IronPaw-win-x64.zip" | Set-Content -LiteralPath (Join-Path $output 'sha256sums.txt') -Encoding ascii
    [ordered]@{ sourceRevision = $revision; draft = $dirty; archive = [IO.Path]::GetFileName($archive); sha256 = $archiveHash; bytes = (Get-Item -LiteralPath $archive).Length
        payloadFiles = $files.Count; tests = $summary.passed; packagedHostSmokes = @('CLI', 'Desktop'); liveProviderCalled = $false
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'release-manifest.json') -Encoding utf8
    Write-Host "Package and both published-host smokes passed. Output: $output"
}
finally { Pop-Location }
