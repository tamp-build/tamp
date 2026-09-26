using System.Diagnostics;

namespace Tamp.Cli.Mcp;

/// <summary>
/// Real <see cref="IBuildInvoker"/>: shells to the located build project via
/// <c>dotnet run --project &lt;build&gt; -- …</c> (the same dispatch the CLI already uses),
/// capturing stdout/stderr so the MCP stdio channel stays clean.
/// </summary>
internal sealed class BuildProjectInvoker : IBuildInvoker
{
    private readonly string _buildProject;

    public BuildProjectInvoker(string buildProject) => _buildProject = buildProject;

    public (int ExitCode, string Stdout, string Stderr) Run(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(_buildProject);
        psi.ArgumentList.Add("--");
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi);
        if (process is null) return (-1, string.Empty, "failed to start dotnet");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }
}
