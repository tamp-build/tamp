using System.IO;
using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// Tests for #19 worker-identity resolution precedence
/// (<c>TAMP_WORKER_ID</c> → CI actor → git author → <c>human:&lt;login&gt;</c>) and the
/// threading of the resolved <c>workerId</c> onto artifact.produced.
/// </summary>
public sealed class WorkerIdResolverTests
{
    // Injected inputs: env for the explicit override, plus ciActor / gitAuthor funcs.
    private static string Resolve(string? tampWorkerId = null, string? ciActor = null, string? gitAuthor = null) =>
        WorkerIdResolver.Resolve(
            getEnv: k => k == "TAMP_WORKER_ID" ? tampWorkerId : null,
            ciActor: () => ciActor,
            gitAuthor: () => gitAuthor);

    [Fact]
    public void Explicit_TampWorkerId_Wins_And_Is_Verbatim()
    {
        Assert.Equal("agent:pool/3", Resolve(tampWorkerId: "agent:pool/3", ciActor: "octocat", gitAuthor: "a@b.com"));
        Assert.Equal("agent:pool/3", Resolve(tampWorkerId: "  agent:pool/3  "));   // trimmed, not prefixed
    }

    [Fact]
    public void CiActor_Beats_GitAuthor_And_Login()
    {
        Assert.Equal("human:octocat", Resolve(ciActor: "octocat", gitAuthor: "a@b.com"));
    }

    [Fact]
    public void GitAuthor_Used_When_No_Explicit_Or_Ci()
    {
        Assert.Equal("human:dev@example.com", Resolve(gitAuthor: "dev@example.com"));
    }

    [Fact]
    public void Falls_Back_To_Os_Login_When_Nothing_Else()
    {
        var id = Resolve();   // no explicit, no CI, no git
        Assert.StartsWith("human:", id);
        Assert.NotEqual("human:", id);   // a login (or "unknown") is present
    }

    [Fact]
    public void ResolveCiActor_Maps_GitHub_And_Azure()
    {
        Assert.Equal("octocat", WorkerIdResolver.ResolveCiActor(
            k => k switch { "GITHUB_ACTIONS" => "true", "GITHUB_ACTOR" => "octocat", _ => null }));

        Assert.Equal("Jane Dev", WorkerIdResolver.ResolveCiActor(
            k => k switch { "TF_BUILD" => "True", "BUILD_REQUESTEDFOR" => "Jane Dev", _ => null }));

        Assert.Null(WorkerIdResolver.ResolveCiActor(_ => null));   // off-CI
    }

    private sealed class CapturingSink : IBuildEventSink
    {
        public readonly List<BuildEvent> Events = new();
        public void Emit(BuildEvent e) => Events.Add(e);
    }

    private sealed class ArtifactBuild : TampBuild
    {
        public Target Run => _ => _.Executes(() => BuildEvents.Artifact("bin/out.dll", hash: "sha256:abc", kind: "assembly", sizeBytes: 10));
    }

    [Fact]
    public void ArtifactProduced_Carries_A_Resolved_WorkerId()
    {
        var targets = TampBuild.CollectTargets(new ArtifactBuild());
        var sink = new CapturingSink();
        new Executor(new TargetGraph(targets), output: TextWriter.Null, eventSink: sink).Run("Run");

        var artifact = sink.Events.Single(e => e.Type == BuildEventTypes.ArtifactProduced);
        Assert.False(string.IsNullOrWhiteSpace(artifact.WorkerId));
        // Every event in the run shares the one resolved worker id.
        Assert.All(sink.Events, e => Assert.Equal(artifact.WorkerId, e.WorkerId));
    }
}
