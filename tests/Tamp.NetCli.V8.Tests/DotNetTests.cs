using Xunit;

namespace Tamp.NetCli.V8.Tests;

public sealed class DotNetTests
{
    private static int IndexOf(IReadOnlyList<string> args, string value, int start = 0)
    {
        for (var i = start; i < args.Count; i++)
            if (args[i] == value) return i;
        return -1;
    }

    // ---- Common shape ----

    [Fact]
    public void Every_Verb_Targets_The_Dotnet_Executable()
    {
        Assert.Equal("dotnet", DotNet.Restore().Executable);
        Assert.Equal("dotnet", DotNet.Build().Executable);
        Assert.Equal("dotnet", DotNet.Test().Executable);
        Assert.Equal("dotnet", DotNet.Pack().Executable);
        Assert.Equal("dotnet", DotNet.Publish().Executable);
    }

    [Fact]
    public void Every_Verb_Sets_NoLogo_And_TelemetryOptOut()
    {
        foreach (var plan in new[] { DotNet.Restore(), DotNet.Build(), DotNet.Test(), DotNet.Pack(), DotNet.Publish() })
        {
            Assert.Equal("1", plan.Environment["DOTNET_NOLOGO"]);
            Assert.Equal("1", plan.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"]);
        }
    }

    [Fact]
    public void Every_Verb_Begins_With_Its_Verb_Token()
    {
        Assert.Equal("restore", DotNet.Restore().Arguments[0]);
        Assert.Equal("build", DotNet.Build().Arguments[0]);
        Assert.Equal("test", DotNet.Test().Arguments[0]);
        Assert.Equal("pack", DotNet.Pack().Arguments[0]);
        Assert.Equal("publish", DotNet.Publish().Arguments[0]);
    }

    [Fact]
    public void Default_Plan_Has_No_Working_Directory()
    {
        Assert.Null(DotNet.Build().WorkingDirectory);
    }

    // ---- Restore ----

    [Fact]
    public void Restore_Project_Becomes_Positional_Argument()
    {
        var plan = DotNet.Restore(s => s.SetProject("./Foo.csproj"));
        Assert.Equal("./Foo.csproj", plan.Arguments[1]);
    }

    [Fact]
    public void Restore_NoCache_And_Force_Are_Distinct_Flags()
    {
        var plan = DotNet.Restore(s => s.SetNoCache(true).SetForce(true));
        Assert.Contains("--no-cache", plan.Arguments);
        Assert.Contains("--force", plan.Arguments);
    }

    [Fact]
    public void Restore_Sources_Emit_As_Repeated_Source_Flags()
    {
        var plan = DotNet.Restore(s => s.AddSource("https://a/v3/index.json").AddSource("https://b/v3/index.json"));
        var args = plan.Arguments;
        var firstSource = IndexOf(args, "--source");
        var secondSource = IndexOf(args, "--source", firstSource + 1);
        Assert.True(firstSource >= 0);
        Assert.True(secondSource > firstSource);
        Assert.Equal("https://a/v3/index.json", args[firstSource + 1]);
        Assert.Equal("https://b/v3/index.json", args[secondSource + 1]);
    }

    [Fact]
    public void Restore_LockedMode_Implies_Use_Lock_File_Mention_Independently()
    {
        var plan = DotNet.Restore(s => s.SetLockedMode(true).SetUseLockFile(true));
        Assert.Contains("--use-lock-file", plan.Arguments);
        Assert.Contains("--locked-mode", plan.Arguments);
    }

    // ---- Build ----

    [Fact]
    public void Build_Configuration_Maps_To_Title_Case_Argument()
    {
        var plan = DotNet.Build(s => s.SetConfiguration(Configuration.Release));
        var args = plan.Arguments;
        var idx = IndexOf(args, "--configuration");
        Assert.True(idx >= 0);
        Assert.Equal("Release", args[idx + 1]);
    }

    [Fact]
    public void Build_NoRestore_Becomes_Flag()
    {
        var plan = DotNet.Build(s => s.SetNoRestore(true));
        Assert.Contains("--no-restore", plan.Arguments);
    }

    [Fact]
    public void Build_Properties_Emit_As_DashP_Pairs()
    {
        var plan = DotNet.Build(s => s.SetProperty("Version", "1.2.3").SetProperty("Foo", "bar"));
        Assert.Contains("-p:Version=1.2.3", plan.Arguments);
        Assert.Contains("-p:Foo=bar", plan.Arguments);
    }

    [Fact]
    public void Build_Output_And_Framework_Round_Trip()
    {
        var plan = DotNet.Build(s => s.SetOutput("bin/Release").SetFramework("net10.0"));
        var args = plan.Arguments;
        Assert.Equal("bin/Release", args[IndexOf(args, "--output") + 1]);
        Assert.Equal("net10.0", args[IndexOf(args, "--framework") + 1]);
    }

    // ---- Test ----

    [Fact]
    public void Test_NoBuild_Without_NoRestore_Is_Permitted()
    {
        var plan = DotNet.Test(s => s.SetNoBuild(true));
        Assert.Contains("--no-build", plan.Arguments);
        Assert.DoesNotContain("--no-restore", plan.Arguments);
    }

    [Fact]
    public void Test_Filter_And_Loggers_Round_Trip()
    {
        var plan = DotNet.Test(s => s
            .SetFilter("Category=Smoke")
            .AddLogger("trx;LogFileName=t.trx")
            .AddLogger("console;verbosity=normal"));
        var args = plan.Arguments;
        Assert.Equal("Category=Smoke", args[IndexOf(args, "--filter") + 1]);
        var firstLogger = IndexOf(args, "--logger");
        var secondLogger = IndexOf(args, "--logger", firstLogger + 1);
        Assert.Equal("trx;LogFileName=t.trx", args[firstLogger + 1]);
        Assert.Equal("console;verbosity=normal", args[secondLogger + 1]);
    }

    [Fact]
    public void Test_Collectors_Each_Emit_Their_Own_Collect_Pair()
    {
        var plan = DotNet.Test(s => s
            .AddDataCollector("Code Coverage")
            .AddDataCollector("XPlat Code Coverage"));
        var args = plan.Arguments;
        var first = IndexOf(args, "--collect");
        var second = IndexOf(args, "--collect", first + 1);
        Assert.Equal("Code Coverage", args[first + 1]);
        Assert.Equal("XPlat Code Coverage", args[second + 1]);
    }

    [Fact]
    public void Test_Collector_Value_With_Spaces_Stays_Single_Argument()
    {
        // Regression guard: vstest expects "--collect" followed by a single
        // arg containing spaces (e.g. "Code Coverage"). The wrapper must NOT
        // split on whitespace — that's the spawner's job to quote.
        var plan = DotNet.Test(s => s.AddDataCollector("Code Coverage;Format=Cobertura"));
        var args = plan.Arguments;
        var i = IndexOf(args, "--collect");
        Assert.Equal("Code Coverage;Format=Cobertura", args[i + 1]);
    }

    [Fact]
    public void Test_No_Collector_Means_No_Collect_Flag_At_All()
    {
        var plan = DotNet.Test();
        Assert.DoesNotContain("--collect", plan.Arguments);
    }

    [Fact]
    public void Test_BlameHang_With_Timeout_Emits_Milliseconds()
    {
        var plan = DotNet.Test(s => s.SetBlameHang(true).SetBlameHangTimeout(TimeSpan.FromSeconds(45)));
        var args = plan.Arguments;
        Assert.Contains("--blame-hang", args);
        Assert.Equal("45000ms", args[IndexOf(args, "--blame-hang-timeout") + 1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Test_BlameCrash_Flag_Does_Not_Require_A_Dump_Type(bool enabled)
    {
        var args = DotNet.Test(s => s.SetBlameCrash(enabled)).Arguments;
        Assert.Equal(enabled, args.Contains("--blame-crash"));
        Assert.DoesNotContain("--blame-crash-dump-type", args);
    }

    [Theory]
    [InlineData(DotNetCrashDumpType.Mini, "mini")]
    [InlineData(DotNetCrashDumpType.Full, "full")]
    public void Test_BlameCrash_Emits_Requested_Dump_Alongside_Hang_Diagnostics(
        DotNetCrashDumpType dumpType, string token)
    {
        var plan = DotNet.Test(s => s.SetBlameCrash(true).SetBlameCrashDumpType(dumpType)
            .SetBlameHang(true).SetBlameHangTimeout(TimeSpan.FromSeconds(45)));
        var args = plan.Arguments;
        Assert.Contains("--blame-crash", args);
        Assert.Equal(token, args[IndexOf(args, "--blame-crash-dump-type") + 1]);
        Assert.Contains("--blame-hang", args);
        Assert.Equal("45000ms", args[IndexOf(args, "--blame-hang-timeout") + 1]);
    }

    [Fact]
    public void Test_BlameCrash_Defaults_And_Reset_Omit_Crash_Flags()
    {
        Assert.DoesNotContain("--blame-crash", DotNet.Test().Arguments);
        Assert.DoesNotContain("--blame-crash-dump-type", DotNet.Test().Arguments);
        var plan = DotNet.Test(s => s.SetBlameCrash(true).SetBlameCrashDumpType(DotNetCrashDumpType.Mini)
            .SetBlameCrash(false).SetBlameCrashDumpType(null));
        Assert.DoesNotContain("--blame-crash", plan.Arguments);
        Assert.DoesNotContain("--blame-crash-dump-type", plan.Arguments);
    }

    [Fact]
    public void Test_Crash_Dump_Type_Can_Be_Used_Without_Explicit_Blame_Flag()
    {
        var settings = new DotNetTestSettings { BlameCrashDumpType = DotNetCrashDumpType.Mini };
        var args = settings.ToCommandPlan().Arguments;
        Assert.Equal("mini", args[IndexOf(args, "--blame-crash-dump-type") + 1]);
        Assert.DoesNotContain("--blame-crash", args); // VSTest implies it from dump type.
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public void Test_Invalid_Crash_Dump_Type_Is_Rejected_Before_It_Becomes_State(int value)
    {
        var settings = new DotNetTestSettings { BlameCrashDumpType = DotNetCrashDumpType.Full };
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.BlameCrashDumpType = (DotNetCrashDumpType)value);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.SetBlameCrashDumpType((DotNetCrashDumpType)value));
        Assert.Equal(DotNetCrashDumpType.Full, settings.BlameCrashDumpType);
    }

    // ---- Pack ----

    [Fact]
    public void Pack_Output_Round_Trips()
    {
        var plan = DotNet.Pack(s => s.SetConfiguration(Configuration.Release).SetOutput("artifacts"));
        var args = plan.Arguments;
        Assert.Equal("Release", args[IndexOf(args, "--configuration") + 1]);
        Assert.Equal("artifacts", args[IndexOf(args, "--output") + 1]);
    }

    [Fact]
    public void Pack_IncludeSymbols_And_IncludeSource_Are_Independent()
    {
        var plan = DotNet.Pack(s => s.SetIncludeSymbols(true).SetIncludeSource(true));
        Assert.Contains("--include-symbols", plan.Arguments);
        Assert.Contains("--include-source", plan.Arguments);
    }

    // ---- Publish ----

    [Fact]
    public void Publish_SelfContained_Maps_To_Affirmative_Or_Negative_Flag()
    {
        var on = DotNet.Publish(s => s.SetSelfContained(true));
        var off = DotNet.Publish(s => s.SetSelfContained(false));
        Assert.Contains("--self-contained", on.Arguments);
        Assert.Contains("--no-self-contained", off.Arguments);
    }

    [Fact]
    public void Publish_Boolean_MSBuild_Properties_Emit_As_DashP_True_Pairs()
    {
        var plan = DotNet.Publish(s => s
            .SetPublishSingleFile(true)
            .SetPublishTrimmed(true)
            .SetPublishReadyToRun(true));
        Assert.Contains("-p:PublishSingleFile=true", plan.Arguments);
        Assert.Contains("-p:PublishTrimmed=true", plan.Arguments);
        Assert.Contains("-p:PublishReadyToRun=true", plan.Arguments);
    }

    // ---- Verbosity (shared via base) ----

    [Theory]
    [InlineData(DotNetVerbosity.Quiet, "quiet")]
    [InlineData(DotNetVerbosity.Minimal, "minimal")]
    [InlineData(DotNetVerbosity.Normal, "normal")]
    [InlineData(DotNetVerbosity.Detailed, "detailed")]
    [InlineData(DotNetVerbosity.Diagnostic, "diagnostic")]
    public void Verbosity_Round_Trips_For_Build(DotNetVerbosity v, string expected)
    {
        var plan = DotNet.Build(s => s.SetVerbosity(v));
        var args = plan.Arguments;
        Assert.Equal(expected, args[IndexOf(args, "--verbosity") + 1]);
    }

    [Fact]
    public void Verbosity_Round_Trips_For_All_Verbs()
    {
        // Sanity that the shared base is wired into every settings class.
        var verbs = new CommandPlan[]
        {
            DotNet.Restore(s => s.SetVerbosity(DotNetVerbosity.Detailed)),
            DotNet.Build(s => s.SetVerbosity(DotNetVerbosity.Detailed)),
            DotNet.Test(s => s.SetVerbosity(DotNetVerbosity.Detailed)),
            DotNet.Pack(s => s.SetVerbosity(DotNetVerbosity.Detailed)),
            DotNet.Publish(s => s.SetVerbosity(DotNetVerbosity.Detailed)),
        };
        foreach (var plan in verbs)
        {
            Assert.Contains("--verbosity", plan.Arguments);
            Assert.Contains("detailed", plan.Arguments);
        }
    }

    [Fact]
    public void Working_Directory_Round_Trips()
    {
        var plan = DotNet.Build(s => s.SetWorkingDirectory("/tmp/work"));
        Assert.Equal("/tmp/work", plan.WorkingDirectory);
    }

    [Fact]
    public void Null_Configurer_Produces_Default_Plan()
    {
        var plan = DotNet.Build();
        Assert.Equal(["build"], plan.Arguments);
    }

    [Fact]
    public void Custom_Environment_Variable_Survives_Plus_Defaults_Stay_Set()
    {
        var plan = DotNet.Build(s => s.EnvironmentVariables["MY_VAR"] = "hello");
        Assert.Equal("hello", plan.Environment["MY_VAR"]);
        Assert.Equal("1", plan.Environment["DOTNET_NOLOGO"]);
    }

    // ---- NuGetPush ----

    [Fact]
    public void NuGetPush_Requires_PackagePath()
    {
        Assert.Throws<InvalidOperationException>(() => DotNet.NuGetPush(s => { }));
    }

    [Fact]
    public void NuGetPush_Throws_On_Null_Configurer()
    {
        Assert.Throws<ArgumentNullException>(() => DotNet.NuGetPush((Action<DotNetNuGetPushSettings>)null!));
    }

    [Fact]
    public void NuGetPush_Begins_With_Verb_Tokens_Then_Package_Path()
    {
        var plan = DotNet.NuGetPush(s => s.SetPackagePath("artifacts/MyApp.1.0.0.nupkg"));
        Assert.Equal("nuget", plan.Arguments[0]);
        Assert.Equal("push", plan.Arguments[1]);
        Assert.Equal("artifacts/MyApp.1.0.0.nupkg", plan.Arguments[2]);
    }

    [Fact]
    public void NuGetPush_Glob_Path_Is_Passed_Through_Verbatim()
    {
        var plan = DotNet.NuGetPush(s => s.SetPackagePath("artifacts/*.nupkg"));
        Assert.Contains("artifacts/*.nupkg", plan.Arguments);
    }

    [Fact]
    public void NuGetPush_With_Source_And_ApiKey()
    {
        var key = new Secret("NuGetApiKey", "p4tt3rn-key");
        var plan = DotNet.NuGetPush(s => s
            .SetPackagePath("a.nupkg")
            .SetSource("https://api.nuget.org/v3/index.json")
            .SetApiKey(key));
        var args = plan.Arguments;
        Assert.Equal("https://api.nuget.org/v3/index.json", args[IndexOf(args, "--source") + 1]);
        Assert.Equal("p4tt3rn-key", args[IndexOf(args, "--api-key") + 1]);
    }

    [Fact]
    public void NuGetPush_Registers_ApiKey_As_Secret_For_Redaction()
    {
        var key = new Secret("NuGetApiKey", "supersecret");
        var plan = DotNet.NuGetPush(s => s
            .SetPackagePath("a.nupkg")
            .SetApiKey(key));
        Assert.Single(plan.Secrets);
        Assert.Same(key, plan.Secrets[0]);
    }

    [Fact]
    public void NuGetPush_With_SymbolSource_And_SymbolApiKey()
    {
        var key = new Secret("Key", "v1");
        var symbolKey = new Secret("SymKey", "v2");
        var plan = DotNet.NuGetPush(s => s
            .SetPackagePath("a.nupkg")
            .SetApiKey(key)
            .SetSymbolSource("https://nuget.smbsrc.net/")
            .SetSymbolApiKey(symbolKey));
        var args = plan.Arguments;
        Assert.Equal("https://nuget.smbsrc.net/", args[IndexOf(args, "--symbol-source") + 1]);
        Assert.Equal("v2", args[IndexOf(args, "--symbol-api-key") + 1]);
        Assert.Equal(2, plan.Secrets.Count);
    }

    [Fact]
    public void NuGetPush_SkipDuplicate_Becomes_Flag()
    {
        var plan = DotNet.NuGetPush(s => s.SetPackagePath("a.nupkg").SetSkipDuplicate(true));
        Assert.Contains("--skip-duplicate", plan.Arguments);
    }

    [Fact]
    public void NuGetPush_NoSymbols_Becomes_Flag()
    {
        var plan = DotNet.NuGetPush(s => s.SetPackagePath("a.nupkg").SetNoSymbols(true));
        Assert.Contains("--no-symbols", plan.Arguments);
    }

    [Fact]
    public void NuGetPush_Timeout_Emits_Whole_Seconds()
    {
        var plan = DotNet.NuGetPush(s => s
            .SetPackagePath("a.nupkg")
            .SetTimeout(TimeSpan.FromMinutes(10)));
        var args = plan.Arguments;
        Assert.Equal("600", args[IndexOf(args, "--timeout") + 1]);
    }

    [Fact]
    public void NuGetPush_DisableBuffering_And_NoServiceEndpoint_Are_Independent()
    {
        var plan = DotNet.NuGetPush(s => s
            .SetPackagePath("a.nupkg")
            .SetDisableBuffering(true)
            .SetNoServiceEndpoint(true));
        Assert.Contains("--disable-buffering", plan.Arguments);
        Assert.Contains("--no-service-endpoint", plan.Arguments);
    }

    [Fact]
    public void NuGetPush_Default_Source_And_NoFlags()
    {
        var plan = DotNet.NuGetPush(s => s.SetPackagePath("a.nupkg"));
        Assert.Equal(["nuget", "push", "a.nupkg"], plan.Arguments);
        Assert.Empty(plan.Secrets);
    }

    // ---- format ----

    [Fact]
    public void Format_Bare_Emits_Just_The_Verb()
    {
        var args = DotNet.Format().Arguments;
        Assert.Equal("format", args[0]);
        Assert.Single(args);
    }

    [Fact]
    public void Format_Project_Is_Positional_After_Verb()
    {
        var args = DotNet.Format(s => s.SetProject("./MySolution.sln")).Arguments;
        Assert.Equal("format", args[0]);
        Assert.Equal("./MySolution.sln", args[1]);
    }

    [Fact]
    public void Format_VerifyNoChanges_Is_The_CI_Gate_Flag()
    {
        var args = DotNet.Format(s => s.SetVerifyNoChanges()).Arguments;
        Assert.Contains("--verify-no-changes", args);
    }

    [Fact]
    public void Format_NoRestore_Round_Trips()
    {
        var args = DotNet.Format(s => s.SetNoRestore()).Arguments;
        Assert.Contains("--no-restore", args);
    }

    [Fact]
    public void Format_Include_And_Exclude_Are_Space_Joined()
    {
        var args = DotNet.Format(s => s
            .AddInclude("src/")
            .AddInclude("tests/")
            .AddExclude("**/*.Designer.cs")
            .AddExclude("**/Migrations/")).Arguments;
        Assert.Equal("src/ tests/", args[IndexOf(args, "--include") + 1]);
        Assert.Equal("**/*.Designer.cs **/Migrations/", args[IndexOf(args, "--exclude") + 1]);
    }

    [Fact]
    public void Format_IncludeGenerated_Round_Trips()
    {
        var args = DotNet.Format(s => s.SetIncludeGenerated()).Arguments;
        Assert.Contains("--include-generated", args);
    }

    [Fact]
    public void Format_BinaryLog_And_Report_Round_Trip()
    {
        var args = DotNet.Format(s => s.SetBinaryLog("/tmp/format.binlog").SetReport("/tmp/format-report")).Arguments;
        Assert.Equal("/tmp/format.binlog", args[IndexOf(args, "--binarylog") + 1]);
        Assert.Equal("/tmp/format-report", args[IndexOf(args, "--report") + 1]);
    }

    [Theory]
    [InlineData(DotNetFormatSeverity.Info, "info")]
    [InlineData(DotNetFormatSeverity.Warn, "warn")]
    [InlineData(DotNetFormatSeverity.Error, "error")]
    public void Format_Severity_Maps_To_Lowercase_Token(DotNetFormatSeverity severity, string expected)
    {
        var args = DotNet.Format(s => s.SetSeverity(severity)).Arguments;
        Assert.Equal(expected, args[IndexOf(args, "--severity") + 1]);
    }

    [Fact]
    public void Format_Diagnostics_And_ExcludeDiagnostics_Are_Space_Joined()
    {
        var args = DotNet.Format(s => s
            .AddDiagnosticId("IDE0005")
            .AddDiagnosticId("IDE0044")
            .AddExcludeDiagnosticId("CA1822")).Arguments;
        Assert.Equal("IDE0005 IDE0044", args[IndexOf(args, "--diagnostics") + 1]);
        Assert.Equal("CA1822", args[IndexOf(args, "--exclude-diagnostics") + 1]);
    }

    [Fact]
    public void FormatWhitespace_Verb_Tokens_Are_Format_Whitespace()
    {
        var args = DotNet.FormatWhitespace().Arguments;
        Assert.Equal("format", args[0]);
        Assert.Equal("whitespace", args[1]);
    }

    [Fact]
    public void FormatWhitespace_Folder_Round_Trips()
    {
        var args = DotNet.FormatWhitespace(s => s.SetFolder()).Arguments;
        Assert.Contains("--folder", args);
    }

    [Fact]
    public void FormatWhitespace_Has_No_Diagnostics_Setters()
    {
        // Surface-policing: dotnet format whitespace does NOT accept
        // --severity / --diagnostics / --exclude-diagnostics. The
        // wrapper must not expose them.
        var setters = typeof(DotNetFormatWhitespaceSettings)
            .GetMethods()
            .Where(m => m.Name.StartsWith("Set"))
            .Select(m => m.Name)
            .ToHashSet();
        Assert.DoesNotContain("SetSeverity", setters);
        var addMethods = typeof(DotNetFormatWhitespaceSettings)
            .GetMethods()
            .Where(m => m.Name.StartsWith("Add"))
            .Select(m => m.Name)
            .ToHashSet();
        Assert.DoesNotContain("AddDiagnosticId", addMethods);
        Assert.DoesNotContain("AddExcludeDiagnosticId", addMethods);
    }

    [Fact]
    public void FormatStyle_Verb_Tokens_Are_Format_Style()
    {
        var args = DotNet.FormatStyle().Arguments;
        Assert.Equal("format", args[0]);
        Assert.Equal("style", args[1]);
    }

    [Fact]
    public void FormatStyle_Severity_Round_Trips()
    {
        var args = DotNet.FormatStyle(s => s.SetSeverity(DotNetFormatSeverity.Warn)).Arguments;
        Assert.Equal("warn", args[IndexOf(args, "--severity") + 1]);
    }

    [Fact]
    public void FormatAnalyzers_Verb_Tokens_Are_Format_Analyzers()
    {
        var args = DotNet.FormatAnalyzers().Arguments;
        Assert.Equal("format", args[0]);
        Assert.Equal("analyzers", args[1]);
    }

    [Fact]
    public void FormatAnalyzers_VerifyNoChanges_And_Severity_Round_Trip()
    {
        var args = DotNet.FormatAnalyzers(s => s
            .SetVerifyNoChanges()
            .SetSeverity(DotNetFormatSeverity.Error)).Arguments;
        Assert.Contains("--verify-no-changes", args);
        Assert.Equal("error", args[IndexOf(args, "--severity") + 1]);
    }

    [Fact]
    public void Format_Verbosity_Round_Trips_Via_DotNetSettingsBase()
    {
        var args = DotNet.Format(s => s.SetVerbosity(DotNetVerbosity.Diagnostic)).Arguments;
        Assert.Equal("diagnostic", args[IndexOf(args, "--verbosity") + 1]);
    }

    // ---- Clean (TAM-112) ----

    [Fact]
    public void Clean_Verb_Token_Is_clean()
    {
        Assert.Equal("clean", DotNet.Clean().Arguments[0]);
    }

    [Fact]
    public void Clean_All_Flags_Round_Trip()
    {
        var args = DotNet.Clean(s => s
            .SetProject("./Foo.csproj")
            .SetConfiguration(Configuration.Release)
            .SetFramework("net8.0")
            .SetRuntime("linux-x64")
            .SetOutput("./bin/Release/net8.0")
            .SetNoLogo()).Arguments;
        Assert.Contains("./Foo.csproj", args);
        Assert.Equal("Release", args[IndexOf(args, "--configuration") + 1]);
        Assert.Equal("net8.0", args[IndexOf(args, "--framework") + 1]);
        Assert.Contains("--nologo", args);
    }

    // ---- Test — TRX rewrite on solution mode (TAM-111) ----

    [Fact]
    public void Test_Solution_Project_With_LogFileName_Rewrites_To_LogFilePrefix()
    {
        var args = DotNet.Test(s => s
            .SetProject("./Foo.slnx")
            .AddLogger("trx;LogFileName=test-results.trx")).Arguments;
        Assert.Equal("trx;LogFilePrefix=test-results", args[IndexOf(args, "--logger") + 1]);
    }

    [Fact]
    public void Test_Csproj_Project_Leaves_LogFileName_Untouched()
    {
        var args = DotNet.Test(s => s
            .SetProject("./Tests/Foo.Tests.csproj")
            .AddLogger("trx;LogFileName=test-results.trx")).Arguments;
        Assert.Equal("trx;LogFileName=test-results.trx", args[IndexOf(args, "--logger") + 1]);
    }

    [Fact]
    public void Test_Solution_AutoExpand_Disabled_Preserves_LogFileName()
    {
        var args = DotNet.Test(s => s
            .SetProject("./Foo.slnx")
            .AddLogger("trx;LogFileName=test-results.trx")
            .SetAutoExpandTrxForSolution(false)).Arguments;
        Assert.Equal("trx;LogFileName=test-results.trx", args[IndexOf(args, "--logger") + 1]);
    }
}
