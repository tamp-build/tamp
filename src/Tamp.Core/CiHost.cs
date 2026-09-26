namespace Tamp;

/// <summary>
/// Typed adapter for a CI vendor — read-only metadata plus write
/// operations (log groups, error/warning markers, set-variable). The
/// abstract base defines the common surface; concrete subclasses
/// implement each vendor's command syntax.
/// </summary>
/// <remarks>
/// Output goes directly to the supplied writer (default
/// <see cref="Console.Out"/>) — these messages are produced for the
/// CI server's log parser, not for the executor's logger, so they
/// bypass the redaction layer. Build scripts that need to reference
/// secret values in CI commands should register them with the
/// executor's <see cref="RedactionTable"/> first; then any value
/// that would leak via a wrapper's child-process stdout is scrubbed
/// before it reaches the CI log.
/// </remarks>
public abstract class CiHost
{
    protected CiHost(TextWriter? writer = null)
    {
        Writer = writer ?? Console.Out;
    }

    /// <summary>The vendor enum value matching <see cref="HostProfile.Ci"/>.</summary>
    public abstract CiVendor Vendor { get; }

    /// <summary>Output sink for CI command lines.</summary>
    protected TextWriter Writer { get; }

    /// <summary>Open a collapsible log group.</summary>
    public abstract void OpenGroup(string name);

    /// <summary>Close the most-recently-opened group.</summary>
    public abstract void CloseGroup();

    /// <summary>Emit a CI-visible warning line.</summary>
    public abstract void LogWarning(string message);

    /// <summary>Emit a CI-visible error line. Most vendors mark the build with this.</summary>
    public abstract void LogError(string message);

    /// <summary>
    /// Set a variable visible to subsequent CI steps. Each vendor's
    /// scoping rules apply.
    /// </summary>
    public abstract void SetVariable(string name, string value);

    /// <summary>
    /// Detect the active CI vendor from environment variables and return
    /// the matching <see cref="CiHost"/>, or null if not in a recognized
    /// CI environment.
    /// </summary>
    public static CiHost? Detect(Func<string, string?>? getEnv = null, TextWriter? writer = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var vendor = HostProfileBuilder.DetectCiVendor(getEnv);
        return vendor switch
        {
            CiVendor.GitHubActions => new GitHubActionsHost(getEnv, writer),
            CiVendor.AzureDevOps => new AzureDevOpsHost(getEnv, writer),
            CiVendor.TeamCity => new TeamCityHost(getEnv, writer),
            _ => null,
        };
    }
}

/// <summary>
/// GitHub Actions CI host. Workflow commands per
/// <c>https://docs.github.com/actions/learn-github-actions/workflow-commands-for-github-actions</c>.
/// </summary>
public sealed class GitHubActionsHost : CiHost
{
    private readonly Func<string, string?> _env;

    public GitHubActionsHost(Func<string, string?>? getEnv = null, TextWriter? writer = null)
        : base(writer)
    {
        _env = getEnv ?? Environment.GetEnvironmentVariable;
    }

    public override CiVendor Vendor => CiVendor.GitHubActions;

    public string? Workflow => _env("GITHUB_WORKFLOW");
    public string? RunId => _env("GITHUB_RUN_ID");
    public string? RunNumber => _env("GITHUB_RUN_NUMBER");
    public string? Job => _env("GITHUB_JOB");
    public string? Actor => _env("GITHUB_ACTOR");
    public string? Repository => _env("GITHUB_REPOSITORY");
    public string? Sha => _env("GITHUB_SHA");
    public string? Ref => _env("GITHUB_REF");
    public string? RefName => _env("GITHUB_REF_NAME");
    public string? HeadRef => _env("GITHUB_HEAD_REF");
    public string? BaseRef => _env("GITHUB_BASE_REF");
    public string? EventName => _env("GITHUB_EVENT_NAME");
    public string? Workspace => _env("GITHUB_WORKSPACE");
    public string? RunnerOs => _env("RUNNER_OS");

    /// <summary>True when this run is for a pull request (event-name "pull_request" or "pull_request_target").</summary>
    public bool IsPullRequest => EventName is "pull_request" or "pull_request_target";

    public override void OpenGroup(string name) => Writer.WriteLine($"::group::{name}");
    public override void CloseGroup() => Writer.WriteLine("::endgroup::");
    public override void LogWarning(string message) => Writer.WriteLine($"::warning::{Escape(message)}");
    public override void LogError(string message) => Writer.WriteLine($"::error::{Escape(message)}");

    /// <summary>
    /// On GitHub Actions, "set variable for next steps" writes to the
    /// special <c>GITHUB_ENV</c> file that the runner sources after this
    /// step. Falls back to the legacy <c>::set-env::</c> command if the
    /// env file is not configured (rare; modern runners always set it).
    /// </summary>
    public override void SetVariable(string name, string value)
    {
        var envFile = _env("GITHUB_ENV");
        if (!string.IsNullOrEmpty(envFile))
        {
            // Use the multiline-safe heredoc-like form so values with
            // newlines round-trip correctly.
            var marker = "TAMP_EOF_" + Guid.NewGuid().ToString("N");
            File.AppendAllText(envFile, $"{name}<<{marker}\n{value}\n{marker}\n");
        }
        else
        {
            Writer.WriteLine($"::set-env name={name}::{value}");
        }
    }

    /// <summary>
    /// Append a markdown summary block to the job's run summary
    /// (<c>GITHUB_STEP_SUMMARY</c>). No-op if the env var is unset.
    /// </summary>
    public void AppendStepSummary(string markdown)
    {
        var summaryFile = _env("GITHUB_STEP_SUMMARY");
        if (string.IsNullOrEmpty(summaryFile)) return;
        File.AppendAllText(summaryFile, markdown.EndsWith('\n') ? markdown : markdown + "\n");
    }

    /// <summary>Tell GitHub Actions to mask <paramref name="value"/> in subsequent log output.</summary>
    public void MaskValue(string value) => Writer.WriteLine($"::add-mask::{value}");

    private static string Escape(string s)
        => s.Replace("%", "%25").Replace("\n", "%0A").Replace("\r", "%0D");
}

/// <summary>
/// Azure DevOps Pipelines CI host. Logging commands per
/// <c>https://learn.microsoft.com/azure/devops/pipelines/scripts/logging-commands</c>.
/// </summary>
public sealed class AzureDevOpsHost : CiHost
{
    private readonly Func<string, string?> _env;

    public AzureDevOpsHost(Func<string, string?>? getEnv = null, TextWriter? writer = null)
        : base(writer)
    {
        _env = getEnv ?? Environment.GetEnvironmentVariable;
    }

    public override CiVendor Vendor => CiVendor.AzureDevOps;

    public string? BuildId => _env("BUILD_BUILDID");
    public string? BuildNumber => _env("BUILD_BUILDNUMBER");
    public string? DefinitionName => _env("BUILD_DEFINITIONNAME");
    public string? Reason => _env("BUILD_REASON");
    public string? RepositoryName => _env("BUILD_REPOSITORY_NAME");
    public string? RepositoryUri => _env("BUILD_REPOSITORY_URI");
    public string? SourceBranch => _env("BUILD_SOURCEBRANCH");
    public string? SourceBranchName => _env("BUILD_SOURCEBRANCHNAME");
    public string? SourceVersion => _env("BUILD_SOURCEVERSION");
    public string? SourcesDirectory => _env("BUILD_SOURCESDIRECTORY");
    public string? ArtifactStagingDirectory => _env("BUILD_ARTIFACTSTAGINGDIRECTORY");
    public string? AgentName => _env("AGENT_NAME");
    public string? AgentOs => _env("AGENT_OS");
    public string? CollectionUri => _env("SYSTEM_TEAMFOUNDATIONCOLLECTIONURI");
    public string? TeamProject => _env("SYSTEM_TEAMPROJECT");
    public string? PullRequestId => _env("SYSTEM_PULLREQUEST_PULLREQUESTID");
    public string? PullRequestTargetBranch => _env("SYSTEM_PULLREQUEST_TARGETBRANCH");

    /// <summary>The identity that requested (triggered) the build — the CI actor for attribution (#19).</summary>
    public string? RequestedFor => _env("BUILD_REQUESTEDFOR");

    public bool IsPullRequest => !string.IsNullOrEmpty(PullRequestId);

    public override void OpenGroup(string name) => Writer.WriteLine($"##[group]{name}");
    public override void CloseGroup() => Writer.WriteLine("##[endgroup]");
    public override void LogWarning(string message) => Writer.WriteLine($"##vso[task.logissue type=warning]{message}");
    public override void LogError(string message) => Writer.WriteLine($"##vso[task.logissue type=error]{message}");

    public override void SetVariable(string name, string value)
        => Writer.WriteLine($"##vso[task.setvariable variable={name}]{value}");

    /// <summary>Set a variable AND mark it as secret (redacted in logs by ADO itself).</summary>
    public void SetSecretVariable(string name, string value)
        => Writer.WriteLine($"##vso[task.setvariable variable={name};issecret=true]{value}");

    /// <summary>Override the build number for this run.</summary>
    public void UpdateBuildNumber(string newNumber)
        => Writer.WriteLine($"##vso[build.updatebuildnumber]{newNumber}");

    /// <summary>Upload a file as a build artifact.</summary>
    public void UploadArtifact(string path)
        => Writer.WriteLine($"##vso[task.uploadfile]{path}");
}

/// <summary>
/// TeamCity CI host. Service messages per
/// <c>https://www.jetbrains.com/help/teamcity/service-messages.html</c>.
/// </summary>
public sealed class TeamCityHost : CiHost
{
    private readonly Func<string, string?> _env;

    public TeamCityHost(Func<string, string?>? getEnv = null, TextWriter? writer = null)
        : base(writer)
    {
        _env = getEnv ?? Environment.GetEnvironmentVariable;
    }

    public override CiVendor Vendor => CiVendor.TeamCity;

    public string? Version => _env("TEAMCITY_VERSION");
    public string? ProjectName => _env("TEAMCITY_PROJECT_NAME");
    public string? BuildConfigurationName => _env("TEAMCITY_BUILDCONF_NAME");
    public string? BuildNumber => _env("BUILD_NUMBER");
    public string? VcsNumber => _env("BUILD_VCS_NUMBER");

    /// <summary>Path to the .properties file containing all build properties (typed values, secrets, etc.).</summary>
    public string? BuildPropertiesFile => _env("TEAMCITY_BUILD_PROPERTIES_FILE");

    public override void OpenGroup(string name)
        => Writer.WriteLine($"##teamcity[blockOpened name='{Escape(name)}']");

    public override void CloseGroup()
        => Writer.WriteLine("##teamcity[blockClosed name='']");

    public override void LogWarning(string message)
        => Writer.WriteLine($"##teamcity[message text='{Escape(message)}' status='WARNING']");

    public override void LogError(string message)
        => Writer.WriteLine($"##teamcity[message text='{Escape(message)}' status='ERROR']");

    public override void SetVariable(string name, string value)
        => Writer.WriteLine($"##teamcity[setParameter name='{Escape(name)}' value='{Escape(value)}']");

    /// <summary>Override the build number for this run.</summary>
    public void UpdateBuildNumber(string newNumber)
        => Writer.WriteLine($"##teamcity[buildNumber '{Escape(newNumber)}']");

    /// <summary>Publish a file or directory as a build artifact.</summary>
    public void PublishArtifact(string path)
        => Writer.WriteLine($"##teamcity[publishArtifacts '{Escape(path)}']");

    /// <summary>Mark the build as failed with a problem description.</summary>
    public void ReportBuildProblem(string description)
        => Writer.WriteLine($"##teamcity[buildProblem description='{Escape(description)}']");

    /// <summary>
    /// Escape a string for inclusion in a TeamCity service message
    /// per <c>https://www.jetbrains.com/help/teamcity/service-messages.html#Escaped+values</c>.
    /// </summary>
    private static string Escape(string s)
    {
        // TeamCity escape rules: | → ||, ' → |', \n → |n, \r → |r, [ → |[, ] → |]
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '|': sb.Append("||"); break;
                case '\'': sb.Append("|'"); break;
                case '\n': sb.Append("|n"); break;
                case '\r': sb.Append("|r"); break;
                case '[': sb.Append("|["); break;
                case ']': sb.Append("|]"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
