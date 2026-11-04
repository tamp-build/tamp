using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Tamp;

/// <summary>The outcome of one readiness check: ready or not, and a short note on why not.</summary>
public readonly record struct ReadinessResult(bool IsReady, string? Detail = null)
{
    /// <summary>Ready.</summary>
    public static ReadinessResult Ready => new(true);

    /// <summary>Not ready yet, with a reason for the timeout message.</summary>
    public static ReadinessResult NotYet(string detail) => new(false, detail);
}

/// <summary>
/// How a <see cref="ManagedProcess"/> decides its service is usable (#125). Checked repeatedly until it reports ready,
/// the process exits, or the readiness timeout runs out.
/// </summary>
public abstract class Readiness : IDisposable
{
    /// <summary>What is checked, for messages and <c>service.ready</c>'s <c>readinessProbe</c>.</summary>
    public abstract string Description { get; }

    /// <summary>Check once.</summary>
    public abstract Task<ReadinessResult> CheckAsync(CancellationToken cancellationToken);

    /// <summary>Called with each line of the process's output, before the line is written anywhere.</summary>
    protected internal virtual void OnOutputLine(string line) { }

    /// <summary>
    /// Ready once an HTTP(S) GET of <paramref name="url"/> returns a success status; an open port alone isn't enough.
    /// </summary>
    /// <param name="url">The URL to request.</param>
    /// <param name="acceptLoopbackSelfSigned">
    /// Accept any certificate from a loopback host (<c>localhost</c>, <c>127.x</c>, <c>::1</c>), for dev servers with
    /// self-signed certificates. Other hosts are always validated.
    /// </param>
    public static Readiness Http(Uri url, bool acceptLoopbackSelfSigned = false) => new HttpReadiness(url, acceptLoopbackSelfSigned);

    /// <summary>Ready once a TCP connection to <paramref name="host"/>:<paramref name="port"/> succeeds.</summary>
    public static Readiness Tcp(string host, int port) => new TcpReadiness(host, port);

    /// <summary>Ready once a line of the process's output matches <paramref name="pattern"/>.</summary>
    public static Readiness OutputMatches(Regex pattern) => new OutputReadiness(pattern);

    /// <summary>Ready once <paramref name="check"/> returns true.</summary>
    public static Readiness When(Func<CancellationToken, Task<bool>> check, string description) => new DelegateReadiness(check, description);

    /// <summary>True for <c>localhost</c>, <c>*.localhost</c> and loopback addresses.</summary>
    public static bool IsLoopbackHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Release resources (an HTTP client).</summary>
    protected virtual void Dispose(bool disposing) { }

    private sealed class HttpReadiness : Readiness
    {
        private readonly Uri _url;
        private readonly HttpClientHandler _handler;
        private readonly HttpClient _client;

        public HttpReadiness(Uri url, bool acceptLoopbackSelfSigned)
        {
            if (url is null) throw new ArgumentNullException(nameof(url));
            if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
                throw new ArgumentException("The readiness URL must be an absolute http(s) URL.", nameof(url));
            _url = url;
            _handler = new HttpClientHandler();
            if (acceptLoopbackSelfSigned)
                _handler.ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                    errors == SslPolicyErrors.None || (request.RequestUri is { } u && IsLoopbackHost(u.Host));
            _client = new HttpClient(_handler) { Timeout = TimeSpan.FromSeconds(10) };
        }

        public override string Description => _url.ToString();

        public override async Task<ReadinessResult> CheckAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var response = await _client.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                return response.IsSuccessStatusCode
                    ? ReadinessResult.Ready
                    : ReadinessResult.NotYet("HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            }
            catch (HttpRequestException ex) { return ReadinessResult.NotYet(ex.Message); }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return ReadinessResult.NotYet("request timed out"); }
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing) return;
            _client.Dispose();
            _handler.Dispose();
        }
    }

    private sealed class TcpReadiness : Readiness
    {
        private readonly string _host;
        private readonly int _port;

        public TcpReadiness(string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("host is required.", nameof(host));
            if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), port, "port must be 1-65535.");
            _host = host;
            _port = port;
        }

        public override string Description => string.Create(CultureInfo.InvariantCulture, $"tcp://{_host}:{_port}");

        public override async Task<ReadinessResult> CheckAsync(CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(_host, _port, timeout.Token).ConfigureAwait(false);
                return ReadinessResult.Ready;
            }
            catch (SocketException ex) { return ReadinessResult.NotYet(ex.Message); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return ReadinessResult.NotYet("connect timed out"); }
        }
    }

    private sealed class OutputReadiness : Readiness
    {
        private readonly Regex _pattern;
        private volatile bool _seen;

        public OutputReadiness(Regex pattern) => _pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));

        public override string Description => $"output matching /{_pattern}/";

        protected internal override void OnOutputLine(string line)
        {
            if (!_seen && _pattern.IsMatch(line)) _seen = true;
        }

        public override Task<ReadinessResult> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_seen ? ReadinessResult.Ready : ReadinessResult.NotYet("no matching output yet"));
    }

    private sealed class DelegateReadiness : Readiness
    {
        private readonly Func<CancellationToken, Task<bool>> _check;
        private readonly string _description;

        public DelegateReadiness(Func<CancellationToken, Task<bool>> check, string description)
        {
            _check = check ?? throw new ArgumentNullException(nameof(check));
            _description = string.IsNullOrWhiteSpace(description) ? "custom check" : description;
        }

        public override string Description => _description;

        public override async Task<ReadinessResult> CheckAsync(CancellationToken cancellationToken) =>
            await _check(cancellationToken).ConfigureAwait(false) ? ReadinessResult.Ready : ReadinessResult.NotYet("check returned false");
    }
}
