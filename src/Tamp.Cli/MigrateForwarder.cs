using System.Diagnostics;
using System.Reflection;

namespace Tamp.Cli;

/// <summary>
/// <c>tamp migrate &lt;source&gt; …</c> (ADR 0021): runs the <c>Tamp.Migrate</c> tool at this CLI's own version
/// through <c>dotnet tool execute</c>, so the <c>tamp</c> tool every CI run installs doesn't carry Roslyn for a
/// one-off job. Everything after <c>migrate</c> is passed through unchanged.
/// </summary>
internal static class MigrateForwarder
{
    internal const string PackageId = "Tamp.Migrate";
    private const int ExitDispatchFailed = 3;

    public static int Run(IReadOnlyList<string> args)
    {
        if (ExecutableLocator.Current.ResolveDotnet() is not { } dotnet)
        {
            Console.Error.WriteLine($"tamp: {Mcp.BuildProjectInvoker.DotnetNotFound}");
            return ExitDispatchFailed;
        }

        var psi = new ProcessStartInfo(dotnet) { UseShellExecute = false };
        foreach (var a in Arguments(PackageVersion, args)) psi.ArgumentList.Add(a);

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                Console.Error.WriteLine("tamp: failed to start dotnet.");
                return ExitDispatchFailed;
            }
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"tamp: `tamp migrate` runs {PackageId} {PackageVersion} with `dotnet tool execute`, which needs the .NET 10 SDK.");
                Console.Error.WriteLine($"  On an older SDK, install it instead: dotnet tool install -g {PackageId} --version {PackageVersion}");
                Console.Error.WriteLine("  and run: tamp-migrate " + string.Join(' ', args));
            }
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"tamp: couldn't run {PackageId}: {ex.Message}");
            return ExitDispatchFailed;
        }
    }

    /// <summary>
    /// The <c>dotnet</c> arguments: <c>tool execute --allow-roll-forward Tamp.Migrate@&lt;v&gt; &lt;args…&gt;</c>.
    /// SDK options go before the package id; everything after it goes to the tool unchanged.
    /// </summary>
    internal static IReadOnlyList<string> Arguments(string version, IReadOnlyList<string> args) =>
        ["tool", "execute", "--allow-roll-forward", $"{PackageId}@{version}", .. args];

    /// <summary>This CLI's package version: Tamp.Migrate ships on the same release train.</summary>
    internal static string PackageVersion
    {
        get
        {
            var info = typeof(MigrateForwarder).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(MigrateForwarder).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
    }
}
