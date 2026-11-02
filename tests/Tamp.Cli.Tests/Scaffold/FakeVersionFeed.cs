using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tamp.Cli.Scaffold;

namespace Tamp.Cli.Tests.Scaffold;

/// <summary>In-memory <see cref="ITampVersionFeed"/> so `tamp init` tests never touch the network (#119).</summary>
internal sealed class FakeVersionFeed : ITampVersionFeed
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _get;

    public FakeVersionFeed(params string[] versions)
        : this(_ => Task.FromResult<IReadOnlyList<string>>(versions)) { }

    public FakeVersionFeed(Func<CancellationToken, Task<IReadOnlyList<string>>> get) => _get = get;

    public string DisplayName => "https://feed.test/v3-flatcontainer/";

    public int Calls { get; private set; }

    public Task<IReadOnlyList<string>> GetVersionsAsync(CancellationToken ct)
    {
        Calls++;
        return _get(ct);
    }

    /// <summary>A feed that throws, like an unreachable host.</summary>
    public static FakeVersionFeed Failing(Exception ex) => new(_ => Task.FromException<IReadOnlyList<string>>(ex));

    /// <summary>A feed that never answers on its own (only cancellation ends it).</summary>
    public static FakeVersionFeed Hanging() => new(async ct =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return Array.Empty<string>();
    });

    /// <summary>A feed that ignores cancellation entirely and never completes.</summary>
    public static FakeVersionFeed Unresponsive() => new(_ => new TaskCompletionSource<IReadOnlyList<string>>().Task);
}
