# Windows release bundles

The portable Windows x64 bundle contains the CLI, Desktop, their .NET runtimes and the Chromium
headless runtime matching the pinned Playwright package. Extract the ZIP completely, keep its
directory structure and open `Start-IronPaw.cmd`, then choose the folder IronPaw can work in.
The chosen folder appears in the window. `desktop/IronPaw.Desktop.exe --workspace <absolute-folder>`
selects it explicitly when launching from a terminal. No administrator installation is required.
Git and the pinned .NET SDK are still needed for repository task graphs and build/test verification.

Application state remains in the user's IronPaw state directory, outside the extracted bundle.
Do not move an older running application over a new release. Close it, extract the new bundle into
a separate directory, and start the new application. Keep the previous bundle for rollback;
database format compatibility must be reviewed before downgrading storage packages.

Basic HTML browsing requires no browser configuration. For JavaScript sites, review a browser
configuration through the Desktop's new-workflow settings or the CLI's `--browser-config` option.
The bundle selects its own `browser-runtime/chrome-headless-shell.exe`; it does not use a personal
Chrome profile or require a browser download on first use. See [browser controls](browser-runtime.md).
MCP connectors are likewise disabled until explicitly configured; see [connector setup](mcp-connectors.md).

## Build a candidate

Use PowerShell 7 on Windows x64 with the SDK in `global.json`. Prepare the pinned browser before
running the complete test suite. The installer below prevents Playwright's cache cleanup from
removing browser versions used by other projects. A private cache can be selected through
`PLAYWRIGHT_BROWSERS_PATH`; CI uses a fresh runner directory.

```powershell
$env:PLAYWRIGHT_SKIP_BROWSER_GC = '1'
dotnet build src/IronPaw.Cli -c Release -warnaserror
./src/IronPaw.Cli/bin/Release/net10.0/playwright.ps1 install chromium --only-shell
./eng/Validate.ps1 -OutputDirectory C:\Temp\ironpaw-validation-001
./eng/Audit-Dependencies.ps1 -OutputDirectory C:\Temp\ironpaw-dependencies-001
./eng/Package.ps1 -ValidationDirectory C:\Temp\ironpaw-validation-001 -OutputDirectory C:\Temp\ironpaw-package-001
```

Each output directory must be new or empty. Release packaging requires a clean Git tree and a
passing source manifest matching every source file before and after publication. `-AllowDirty`
permits an explicitly marked local draft for review; it does not establish a clean release commit.
No script commits, pushes, publishes a public release or calls a live model provider.

The packaging command publishes fresh self-contained apps, copies the matching browser distribution,
checks application dependency graphs and assets for removed SQLite/xUnit packages, then runs the
actual CLI and Desktop executables against separate fresh state/workspace directories. The smoke
workflows use only the built-in local client, create an actual workspace file through `write_file`,
and verify its durable tool outcome, CSharpDB state, transcripts and history after reopening.
Desktop evidence includes normal and workflow-settings screenshots.
The packaged CLI also launches its bundled Chromium, executes static JavaScript and clicks a
button with network admission closed. These are integration checks, not live-model evaluations.

The bundle includes a per-file manifest, its source-manifest digest, source commit and draft flag.
The archive is read back and every file hash checked. `sha256sums.txt` and `release-manifest.json`
describe the resulting ZIP. After extraction, `./Verify-Package.ps1` verifies the files against the
manifest. Content hashes establish integrity against the supplied manifest; they do not establish
publisher identity or replace a trusted download/signature channel.

The GitHub validation workflow runs the same local validation, dependency audit and packaging
commands, retaining test/smoke evidence and the qualified Windows ZIP as workflow artifacts.
Actual hosted CI success is established only by its completed run after publication. Public release
publication, signing and distribution require their own explicit approval and evidence.
