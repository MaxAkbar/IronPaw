# Dependency maintenance

NuGet and GitHub Actions updates are reviewed through Dependabot. Microsoft.Extensions packages
and MSTest/test-platform packages are grouped so compatible families can be validated together.
Action revisions remain pinned to commit SHAs. There is no automatic merging or release publication.
CSharpDB major upgrades require an explicit storage-compatibility review and are excluded from
automatic major-version proposals.

For every dependency change:

1. Review upstream release notes and breaking changes, including transitive dependencies.
2. Run the complete Release validation, including real Chromium and local MCP transports.
3. Retain the current vulnerability/outdated-package audit.
4. Produce and smoke-test a fresh self-contained application bundle.
5. Review persistence/recovery compatibility before adopting storage or serialization changes.

`eng/Audit-Dependencies.ps1` queries the public NuGet feed, retains machine-readable direct and
transitive findings and fails on known vulnerable dependencies or audit failures. An outdated
version is a review finding; it is not automatically replaced across a breaking major version.
The weekly GitHub dependency workflow runs this audit without models, credentials or application
state. Dependabot and scheduled checks become active only after the configuration is published.

The current MSTest adapter's telemetry extension declares `Microsoft.ApplicationInsights` 2.23.0.
It is retained at that upstream version rather than forcing a separate 3.x telemetry migration.
This is an age/version exception, not a vulnerability exception; current security advisories still
block qualification. Remove the exception when the upstream adapter changes or a separately
reviewed compatibility change is qualified. See the
[MSTest dependency declarations](https://github.com/microsoft/testfx/blob/main/Directory.Packages.props)
and [Application Insights major-version changes](https://github.com/microsoft/ApplicationInsights-dotnet/blob/main/BreakingChanges.md).

Updating Microsoft.Playwright also requires updating/installing its matching Chromium revision,
rerunning the real-browser checks, and repackaging the browser directory. Never substitute an
arbitrary installed Chrome version for the pinned release runtime.
