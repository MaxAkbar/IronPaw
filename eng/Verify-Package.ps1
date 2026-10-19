[CmdletBinding()]
param([string]$Directory = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath($Directory)
$manifest = Get-Content -LiteralPath (Join-Path $root 'manifest.json') -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.files.Count -eq 0) { throw 'Package manifest is missing or unsupported.' }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest.files) {
    if ([IO.Path]::IsPathRooted($entry.path) -or -not $seen.Add($entry.path)) { throw 'Package manifest has an invalid or duplicate path.' }
    $file = [IO.Path]::GetFullPath((Join-Path $root $entry.path))
    if (-not $file.StartsWith($root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package path escapes the payload.' }
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -ne $entry.bytes -or
        (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry.sha256) { throw ('Package file is missing or changed: ' + $entry.path) }
}
$actual = @(Get-ChildItem -LiteralPath $root -Recurse -Force -File | Where-Object { $_.FullName -ne (Join-Path $root 'manifest.json') })
if ($actual.Count -ne $manifest.files.Count) { throw 'Package contains files absent from its manifest.' }
Write-Host "Package file verification passed: $($manifest.files.Count) files. This verifies content hashes, not publisher identity."
