namespace Tamp.NetCli.V11;

/// <summary>
/// Tamp wrapper for the .NET 11 SDK CLI. Each method returns a
/// <see cref="CommandPlan"/> the runner dispatches or prints; nothing is
/// executed at call time.
/// </summary>
/// <remarks>
/// Settings classes are mutable while the configurer runs, then frozen
/// into the returned <see cref="CommandPlan"/>. Pass <c>null</c> as the
/// configurer to use the verb's defaults.
/// </remarks>
public static class DotNet
{
    public static CommandPlan Restore(Action<DotNetRestoreSettings>? configure = null)
    {
        var s = new DotNetRestoreSettings();
        configure?.Invoke(s);
        return s.ToCommandPlan();
    }

    public static CommandPlan Build(Action<DotNetBuildSettings>? configure = null)
    {
        var s = new DotNetBuildSettings();
        configure?.Invoke(s);
        return s.ToCommandPlan();
    }

    /// <summary>
    /// <c>dotnet clean</c> — removes <c>bin/</c> and <c>obj/</c> outputs for the configured (or all)
    /// configurations. Pair with a Clean target's <c>AbsolutePath.GlobDirectories("**/bin", "**/obj")</c>
    /// loop when you need to nuke the artifact tree harder than <c>dotnet clean</c> goes.
    /// </summary>
    public static CommandPlan Clean(Action<DotNetCleanSettings>? configure = null)
    {
        var s = new DotNetCleanSettings();
        configure?.Invoke(s);
        return s.ToCommandPlan();
    }

    public static CommandPlan Test(Action<DotNetTestSettings>? configure = null)
    {
        var s = new DotNetTestSettings();
        configure?.Invoke(s);
        return s.ToCommandPlan();
    }

    public static CommandPlan Pack(Action<DotNetPackSettings>? configure = null)
    {
        var s = new DotNetPackSettings();
        configure?.Invoke(s);
        return s.ToCommandPlan();
    }

    public static CommandPlan Publish(Action<DotNetPublishSettings>? configure = null)
    {
        var s = new DotNetPublishSettings();
        configure?.Invoke(s);
        return s.ToCommandPlan();
    }

    /// <summary>
    /// <c>dotnet nuget push</c>. Pushes a <c>.nupkg</c> (or a glob of them)
    /// to a NuGet feed. Pass the API key as a typed <see cref="Secret"/> so
    /// it's registered with the runner's redaction table.
    /// </summary>
    public static CommandPlan NuGetPush(Action<DotNetNuGetPushSettings> configure)
    {
        if (configure is null) throw new ArgumentNullException(nameof(configure));
        var s = new DotNetNuGetPushSettings();
        configure(s);
        return s.ToCommandPlan();
    }

    /// <summary>
    /// <c>dotnet format</c> — runs whitespace + style + analyzer fixes.
    /// Pair with <c>SetVerifyNoChanges(true)</c> in CI to fail the build
    /// when the tree is out of compliance.
    /// </summary>
    public static CommandPlan Format(Action<DotNetFormatSettings>? configure = null)
    {
        var s = new DotNetFormatSettings();
        configure?.Invoke(s);
        return s.ToCommandPlan();
    }

    /// <summary><c>dotnet format whitespace</c> — whitespace-only fixes (the cheapest CI gate).</summary>
    public static CommandPlan FormatWhitespace(Action<DotNetFormatWhitespaceSettings>? configure = null)
    {
        var s = new DotNetFormatWhitespaceSettings();
        configure?.Invoke(s);
        return s.ToCommandPlan();
    }

    /// <summary><c>dotnet format style</c> — code-style analyzer fixes only.</summary>
    public static CommandPlan FormatStyle(Action<DotNetFormatStyleSettings>? configure = null)
    {
        var s = new DotNetFormatStyleSettings();
        configure?.Invoke(s);
        return s.ToCommandPlan();
    }

    /// <summary><c>dotnet format analyzers</c> — third-party analyzer fixes only.</summary>
    public static CommandPlan FormatAnalyzers(Action<DotNetFormatAnalyzersSettings>? configure = null)
    {
        var s = new DotNetFormatAnalyzersSettings();
        configure?.Invoke(s);
        return s.ToCommandPlan();
    }

    // ---- Object-init overloads (1.2.0+, TAM-161) ----
    // Two equivalent authoring styles; both produce identical CommandPlans. Fluent
    // stays canonical in docs and `tamp init` templates; object-init available for
    // consumers who prefer the C# initializer shape.
    //
    //     DotNet.Build(new() { Project = Solution.Path, Configuration = Configuration });
    //
    // is equivalent to:
    //
    //     DotNet.Build(s => s.SetProject(Solution.Path).SetConfiguration(Configuration));

    public static CommandPlan Restore(DotNetRestoreSettings settings) => settings.ToCommandPlan();
    public static CommandPlan Build(DotNetBuildSettings settings) => settings.ToCommandPlan();
    public static CommandPlan Clean(DotNetCleanSettings settings) => settings.ToCommandPlan();
    public static CommandPlan Test(DotNetTestSettings settings) => settings.ToCommandPlan();
    public static CommandPlan Pack(DotNetPackSettings settings) => settings.ToCommandPlan();
    public static CommandPlan Publish(DotNetPublishSettings settings) => settings.ToCommandPlan();
    public static CommandPlan NuGetPush(DotNetNuGetPushSettings settings) => settings.ToCommandPlan();
    public static CommandPlan Format(DotNetFormatSettings settings) => settings.ToCommandPlan();
    public static CommandPlan FormatWhitespace(DotNetFormatWhitespaceSettings settings) => settings.ToCommandPlan();
    public static CommandPlan FormatStyle(DotNetFormatStyleSettings settings) => settings.ToCommandPlan();
    public static CommandPlan FormatAnalyzers(DotNetFormatAnalyzersSettings settings) => settings.ToCommandPlan();
}
