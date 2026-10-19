# IronPaw for Windows

Extract the complete ZIP, keep its folders together, and open `Start-IronPaw.cmd`.
Choose the folder IronPaw can work in. The command-line app is `cli/IronPaw.Cli.exe`;
run it with `--help` to see its options.

The bundle includes its .NET and JavaScript browser runtimes. Git and the pinned
.NET SDK are still needed for repository task graphs and build/test verification.
Application state is saved outside this bundle in your IronPaw state directory.

- [Setup, updates, verification and rollback](docs/releases.md)
- [JavaScript browser permissions](docs/browser-runtime.md)
- [Reviewed MCP connectors](docs/mcp-connectors.md)
- [Configuration examples](docs/examples)
- [Delivery qualification and remaining evidence](docs/delivery-qualification.md)

Connectors and JavaScript site access need an explicit reviewed configuration.
Live evaluation remains disabled until its provider, model, prices and spending cap
are configured and the run is authorized.

Run `./Verify-Package.ps1` in PowerShell 7 to check the extracted files against
`manifest.json`. These hashes verify contents against the supplied manifest;
publisher identity depends on the channel from which you obtained the bundle.
