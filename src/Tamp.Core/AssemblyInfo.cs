using System.Runtime.CompilerServices;

// As of Tamp.Core 1.6.0 (TAM-196), Secret.Reveal() is public — gated by the TAMP004
// Roslyn analyzer rather than by per-satellite InternalsVisibleTo. The InternalsVisibleTo
// entries below are retained for now (a) for tests, and (b) for transitively-internal-touching
// surfaces other than Reveal() — Tamp.Core may add other internal members in future minors
// that named satellites continue to need. They are NOT load-bearing for Secret.Reveal().
//
// Do not add new satellite entries here. New satellites should not need internal access.
// If a new satellite has a legitimate need for an internal Tamp.Core surface, file a ticket
// to evaluate making that surface public + analyzer-gated like Secret was.

[assembly: InternalsVisibleTo("Tamp.Core.Tests")]
[assembly: InternalsVisibleTo("Tamp.Cli")]
[assembly: InternalsVisibleTo("Tamp.Migrate")] // in-tree tool (ADR 0021), not a satellite: ExecutableLocator
[assembly: InternalsVisibleTo("Tamp.NetCli.V8")]
[assembly: InternalsVisibleTo("Tamp.NetCli.V9")]
[assembly: InternalsVisibleTo("Tamp.NetCli.V10")]
[assembly: InternalsVisibleTo("Tamp.Docker.V27")]
[assembly: InternalsVisibleTo("Tamp.SonarScanner.V10")]
[assembly: InternalsVisibleTo("Tamp.SonarScannerCli.V6")]
[assembly: InternalsVisibleTo("Tamp.DotNetCoverage.V18")]
[assembly: InternalsVisibleTo("Tamp.EFCore.V8")]
[assembly: InternalsVisibleTo("Tamp.EFCore.V9")]
[assembly: InternalsVisibleTo("Tamp.EFCore.V10")]
[assembly: InternalsVisibleTo("Tamp.GitVersion.V6")]
[assembly: InternalsVisibleTo("Tamp.ReportGenerator.V5")]
[assembly: InternalsVisibleTo("Tamp.GitHubCli.V2")]
[assembly: InternalsVisibleTo("Tamp.Yarn.V4")]
[assembly: InternalsVisibleTo("Tamp.Turbo.V2")]
[assembly: InternalsVisibleTo("Tamp.Vite.V5")]
[assembly: InternalsVisibleTo("Tamp.GraphQLCodegen.V5")]
[assembly: InternalsVisibleTo("Tamp.Playwright.V1")]
[assembly: InternalsVisibleTo("Tamp.TruffleHog.V3")]
[assembly: InternalsVisibleTo("Tamp.CodeQL.V2")]
[assembly: InternalsVisibleTo("Tamp.AzureCli.V2")]
[assembly: InternalsVisibleTo("Tamp.AzureStaticWebApps.V2")]
[assembly: InternalsVisibleTo("Tamp.Bicep")]
[assembly: InternalsVisibleTo("Tamp.AdoRest.V7")]
[assembly: InternalsVisibleTo("Tamp.AdoServiceConnection.V1")]
[assembly: InternalsVisibleTo("Tamp.AzureFunctionsCoreTools.V4")]
[assembly: InternalsVisibleTo("Tamp.Coverlet.V6")]
[assembly: InternalsVisibleTo("Tamp.Testcontainers.V4")]
[assembly: InternalsVisibleTo("Tamp.ServiceBus.V7")]
[assembly: InternalsVisibleTo("Tamp.ServiceBus.V8")]
[assembly: InternalsVisibleTo("Tamp.Http")]
[assembly: InternalsVisibleTo("Tamp.AdjacentContainer")]
[assembly: InternalsVisibleTo("Tamp.AdoGit")]
[assembly: InternalsVisibleTo("Tamp.Npm.V10")]
[assembly: InternalsVisibleTo("Tamp.AzureAppService")]
[assembly: InternalsVisibleTo("Tamp.PostgresFlex")]
[assembly: InternalsVisibleTo("Tamp.Kudu")]
