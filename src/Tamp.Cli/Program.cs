using System.Diagnostics;
using Tamp.Cli.Scaffold.Commands;

namespace Tamp.Cli;

/// <summary>
/// Tamp global-tool entry point. Locates a build project in the current
/// working tree (or an ancestor) and forwards the invocation to it via
/// <c>dotnet run</c>. The build project itself is a regular .NET console
/// project that calls <see cref="Tamp.TampBuild.Execute{T}"/>.
/// </summary>
internal static class Program
{
    private const int ExitNoBuildProject = 2;
    private const int ExitDispatchFailed = 3;

    public static int Main(string[] args)
    {
        // Built-in informational flags handled here, not delegated.
        if (args.Length == 1 && (args[0] == "--version" || args[0] == "-v"))
        {
            Console.WriteLine($"tamp {Version}");
            return 0;
        }
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            PrintHelp();
            return 0;
        }

        // Built-in subcommands handled by the CLI itself (no build-project dispatch).
        // `init` scaffolds a new Build.cs into the current directory — by design it runs
        // BEFORE a build project exists, so the BuildProjectLocator-based forwarder is
        // skipped.
        if (args.Length > 0 && args[0] == "init")
        {
            var initArgs = new string[args.Length - 1];
            System.Array.Copy(args, 1, initArgs, 0, initArgs.Length);
            return InitCommand.Run(initArgs);
        }

        // `tamp mcp` (#22) — start the MCP control-surface server over stdio. It introspects
        // and drives the build project (found the same way as normal dispatch).
        if (args.Length > 0 && args[0] == "mcp")
        {
            var mcpBuildProject = BuildProjectLocator.Locate(Environment.CurrentDirectory);
            if (mcpBuildProject is null)
            {
                Console.Error.WriteLine("tamp: no build project found for the mcp server.");
                return ExitNoBuildProject;
            }
            return Mcp.TampMcpServer.RunAsync(mcpBuildProject).GetAwaiter().GetResult();
        }

        var cwd = Environment.CurrentDirectory;
        var buildProject = BuildProjectLocator.Locate(cwd);
        if (buildProject is null)
        {
            Console.Error.WriteLine("tamp: no build project found.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Looked for one of:");
            Console.Error.WriteLine("  build/Build.csproj   build/_build.csproj   build/build.csproj");
            Console.Error.WriteLine("  _build/...           .tamp/build/...");
            Console.Error.WriteLine("  (or any folder above) containing a single .csproj");
            Console.Error.WriteLine();
            Console.Error.WriteLine("To bootstrap, create build/Build.csproj referencing Tamp.Core,");
            Console.Error.WriteLine("derive a class from TampBuild, and call Execute<T>(args) from Main.");
            return ExitNoBuildProject;
        }

        Console.WriteLine($"tamp: dispatching to {buildProject}");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(buildProject);
        psi.ArgumentList.Add("--");
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                Console.Error.WriteLine("tamp: failed to start dotnet.");
                return ExitDispatchFailed;
            }
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"tamp: dispatch failed — {ex.Message}");
            return ExitDispatchFailed;
        }
    }

    private static string Version
        => typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private static void PrintHelp()
    {
        Console.WriteLine("tamp — small-core, plugin-driven build automation framework");
        Console.WriteLine();
        Console.WriteLine("USAGE:");
        Console.WriteLine("  dotnet tamp init [--solution <path>] [--dry-run]    scaffold Build.cs into the current dir");
        Console.WriteLine("  dotnet tamp mcp                                     start the MCP control-surface server (stdio)");
        Console.WriteLine("  dotnet tamp <target> [--dry-run | --plan | --list | --list-tree] [--<param> <value>]");
        Console.WriteLine("  dotnet tamp --version | --help");
        Console.WriteLine();
        Console.WriteLine("INSTALL:");
        Console.WriteLine("  dotnet tool install --global Tamp.Cli            (or --local, with a tool manifest)");
        Console.WriteLine();
        Console.WriteLine("FLAGS:");
        Console.WriteLine("  --dry-run            Print every CommandPlan that would run; execute nothing.");
        Console.WriteLine("  --plan               Render the target dependency graph; execute nothing.");
        Console.WriteLine("  --list               List top-level targets (or all, if none are marked).");
        Console.WriteLine("  --list-tree          List targets with their dependencies.");
        Console.WriteLine("  --all                Used with --list / --list-tree: show internal targets too.");
        Console.WriteLine("  --verbosity <level>  quiet | minimal | normal | verbose | diagnostic");
        Console.WriteLine("  --quiet | -v         Shortcuts for --verbosity quiet | --verbosity verbose");
        Console.WriteLine();
        Console.WriteLine("Tamp locates a build project under build/, _build/, or .tamp/build/ in the");
        Console.WriteLine("current working tree (or an ancestor) and forwards the invocation to it.");
        Console.WriteLine();
        Console.WriteLine("Documentation: https://github.com/tamp-build/tamp");
    }
}
