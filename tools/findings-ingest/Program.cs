using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Tamp;
using Tamp.Conformance;
using Tamp.Ingest.V1;
using Tamp.Sarif;
using Tamp.Sbom;

// tamp-core dogfood: push our own build evidence to tamp-findings and gate on the result.
//
//   ingest  — SBOM + SAST + secrets + coverage + test-results + conformance + receipts → findings
//   gate    — GET the ship gate; exit 1 if any blocking gate fails (ClearToShip == false)
//
// Config (env):
//   FINDINGS_URL        default https://tamp-findings.brewingcoder.com
//   FINDINGS_TOKEN      required (prj_/cli_ scoped to tamp-core)
//   FINDINGS_PROJECT_ID default 44f1f80c-976f-4233-89fc-64a6de33520b (tamp-core) — used by `gate`
//   TAMP_CLIENT/PROJECT/COMPONENT   default Tamp / tamp-core / tamp-core
//   TAMP_VERSION        default 0.0.0
//   TAMP_COMMIT         default `git rev-parse HEAD`
//   TAMP_BRANCH         default `git rev-parse --abbrev-ref HEAD`
//   EVIDENCE_DIR        default ./artifacts/evidence (sbom.cdx.json, opengrep.sarif, coverage cobertura, trx/**)
//   REPO_ROOT           default cwd (conformance scans this tree)

static string Env(string k, string d = "") => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : d;
// Resolve an executable to its absolute path via PATH (avoids relying on PATH resolution at spawn time).
static string ResolveExe(string name)
{
    var exts = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", "" } : new[] { "" };
    foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
    {
        if (string.IsNullOrEmpty(dir)) continue;
        foreach (var ext in exts)
        {
            var full = Path.Combine(dir, name + ext);
            if (File.Exists(full)) return full;
        }
    }
    return name; // not found on PATH — let the OS surface the error
}
static string Git(string args, string cwd)
{
    try
    {
        var psi = new ProcessStartInfo(ResolveExe("git"), args) { WorkingDirectory = cwd, RedirectStandardOutput = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return o;
    }
    catch { return ""; }
}

var cmd = args.Length > 0 ? args[0] : "ingest";
var url = Env("FINDINGS_URL", "https://tamp-findings.brewingcoder.com").TrimEnd('/');
var token = Env("FINDINGS_TOKEN");
if (token.Length == 0) { Console.Error.WriteLine("FATAL: FINDINGS_TOKEN not set"); return 2; }
var projectId = Env("FINDINGS_PROJECT_ID", "44f1f80c-976f-4233-89fc-64a6de33520b");
var repoRoot = Env("REPO_ROOT", Directory.GetCurrentDirectory());

var client = Env("TAMP_CLIENT", "Tamp");
var project = Env("TAMP_PROJECT", "tamp-core");
var component = Env("TAMP_COMPONENT", "tamp-core");
var version = Env("TAMP_VERSION", "0.0.0");
var commit = Env("TAMP_COMMIT", Git("rev-parse HEAD", repoRoot));
var branch = Env("TAMP_BRANCH", Git("rev-parse --abbrev-ref HEAD", repoRoot));
var evidence = Env("EVIDENCE_DIR", Path.Combine(repoRoot, "artifacts", "evidence"));

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
http.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

if (cmd == "gate") return await GateAsync();
if (cmd == "ingest") return await IngestAsync();
Console.Error.WriteLine($"unknown command '{cmd}' (expected: ingest | gate)");
return 2;

async Task<int> GateAsync()
{
    var q = $"{url}/projects/{projectId}/gate?commitSha={Uri.EscapeDataString(commit)}";
    var resp = await http.GetAsync(q);
    var body = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode) { Console.Error.WriteLine($"gate query {(int)resp.StatusCode}: {Trunc(body)}"); return 2; }
    using var doc = JsonDocument.Parse(body);
    var root = doc.RootElement;
    int Blk(string n) => root.TryGetProperty(n, out var e) && e.TryGetInt32(out var i) ? i : -1;
    var blocking = Blk("gatesBlocking");
    var failed = Blk("gatesFailed");
    var unknown = Blk("gatesUnknown");
    Console.WriteLine($"Ship gate for {client}/{project} @ {Short(commit)} (version {root.GetProperty("versionString").GetString()}):");
    if (root.TryGetProperty("gates", out var gates) && gates.ValueKind == JsonValueKind.Array)
        foreach (var g in gates.EnumerateArray())
        {
            var key = g.GetProperty("key").GetString();
            var verdict = g.GetProperty("verdict").GetString();
            var obs = g.TryGetProperty("observed", out var o) ? o.GetString() : (g.TryGetProperty("reason", out var r) ? r.GetString() : "");
            var mark = verdict == "Pass" ? "PASS" : verdict == "Fail" ? "FAIL" : verdict == "NotApplicable" ? "n/a " : "??? ";
            Console.WriteLine($"  {mark} {key,-16} {obs}");
        }
    Console.WriteLine($"\nblocking={blocking} failed={failed} unknown={unknown}");
    if (blocking == 0) { Console.WriteLine("CLEAR TO SHIP ✅"); return 0; }
    Console.Error.WriteLine($"::error::BLOCKED — {blocking} gate(s) blocking (failed={failed}, unknown={unknown}). Refusing to ship.");
    return 1;
}

async Task<int> IngestAsync()
{
    Console.WriteLine($"→ {url}  {client}/{project}/{component} version={version} commit={Short(commit)} branch={branch}");
    Console.WriteLine($"  evidence={evidence}\n");
    var hier = new IngestHierarchy
    {
        Client = client, Project = project, Component = component, ComponentKind = "solution",
        Version = version, CommitSha = commit, Branch = branch,
    };
    using var ingest = new TampIngestClient(new Uri(url), new Secret("ingest-token", token));
    var started = DateTimeOffset.UtcNow;
    var receipts = new List<ScanRunReceipt>();
    ScanRunReceipt Receipt(ScannerKind k, string tool, string ver, int count) => new()
    {
        Scanner = k, Status = ScanRunStatus.Succeeded, StartedAt = started, CompletedAt = DateTimeOffset.UtcNow,
        FindingsCount = count, ToolName = tool, ToolVersion = ver,
    };
    var errors = 0;

    // 1) SBOM
    var sbomPath = Path.Combine(evidence, "sbom.cdx.json");
    if (File.Exists(sbomPath))
        try
        {
            var bom = SbomReader.LoadFromFile(AbsolutePath.Create(sbomPath));
            var r = await ingest.PostSbomAsync(hier, bom, toolName: "CycloneDX", toolVersion: "6.2.0");
            Console.WriteLine($"SBOM     → snapshot={r.SbomSnapshotId} components={bom.Components?.Count}");
        }
        catch (Exception ex) { Console.Error.WriteLine($"SBOM     ✗ {Trunc(ex.Message)}"); errors++; }
    else Console.Error.WriteLine($"SBOM     ⚠ missing {sbomPath}");

    // 2) SAST (opengrep SARIF)
    var sastPath = Path.Combine(evidence, "opengrep.sarif");
    if (File.Exists(sastPath))
        try
        {
            var sarif = SarifReader.LoadFromFile(AbsolutePath.Create(sastPath));
            var r = await ingest.PostFindingsAsync(hier, ScannerKind.OpenGrep, sarif);
            Console.WriteLine($"SAST     → cv={r.ComponentVersionId} inserted={r.FindingsInserted} (opengrep)");
            receipts.Add(Receipt(ScannerKind.OpenGrep, "opengrep", "1.x", r.FindingsInserted));
        }
        catch (Exception ex) { Console.Error.WriteLine($"SAST     ✗ {Trunc(ex.Message)}"); errors++; }
    else Console.Error.WriteLine($"SAST     ⚠ missing {sastPath}");

    // 3) Secrets (trufflehog — record the scan ran; verified secrets fail the workflow step upstream)
    try
    {
        var r = await ingest.PostFindingsAsync(new FindingsIngestRequest
        {
            Client = client, Project = project, Component = component, ComponentKind = "solution",
            Version = version, CommitSha = commit, Branch = branch,
            Scanner = ScannerKind.TruffleHog, Findings = Array.Empty<IngestFinding>(),
        });
        Console.WriteLine($"SECRETS  → cv={r.ComponentVersionId} inserted={r.FindingsInserted} closed={r.FindingsClosed} (trufflehog)");
        receipts.Add(Receipt(ScannerKind.TruffleHog, "trufflehog", "3.x", 0));
    }
    catch (Exception ex) { Console.Error.WriteLine($"SECRETS  ✗ {Trunc(ex.Message)}"); errors++; }

    // 4) Coverage (raw cobertura → /ingest/coverage/raw)
    var covPath = FirstExisting(
        Path.Combine(evidence, "coverage", "Cobertura.xml"),
        Path.Combine(evidence, "Cobertura.xml"),
        Path.Combine(evidence, "coverage.cobertura.xml"));
    if (covPath is not null)
    {
        var (code, resp) = await PostFileRawAsync("/ingest/coverage/raw", covPath, "application/xml", extra: "&toolVersion=5.5.1");
        if (code is 200 or 201) Console.WriteLine($"COVERAGE → {Trunc(resp)}");
        else { Console.Error.WriteLine($"COVERAGE ✗ {code} {Trunc(resp)}"); errors++; }
        receipts.Add(Receipt(ScannerKind.Coverlet, "reportgenerator", "5.5.1", 0));
    }
    else Console.Error.WriteLine("COVERAGE ⚠ no cobertura found");

    // 5) Test-results (every .trx → /ingest/test-results/raw; findings replaces by assembly)
    var trxDir = Path.Combine(evidence, "trx");
    var trx = Directory.Exists(trxDir) ? Directory.GetFiles(trxDir, "*.trx", SearchOption.AllDirectories) : Array.Empty<string>();
    if (trx.Length > 0)
    {
        int ok = 0, bad = 0;
        foreach (var f in trx)
        {
            var (code, resp) = await PostFileRawAsync("/ingest/test-results/raw", f, "application/xml");
            if (code is 200 or 201) ok++; else { bad++; Console.Error.WriteLine($"  trx ✗ {code} {Path.GetFileName(f)}: {Trunc(resp)}"); }
        }
        Console.WriteLine($"TESTS    → {ok}/{trx.Length} trx posted (replace-by-assembly)");
        if (bad > 0) errors++;
    }
    else Console.Error.WriteLine($"TESTS    ⚠ no trx under {trxDir}");

    // 6) Conformance — fetch authoritative ruleset from findings, evaluate HEAD (deterministic), forward
    try
    {
        var opts = new ConformanceOptions { RepoRoot = AbsolutePath.Create(repoRoot), CommitSha = commit, Enforcing = false };
        using var rules = new FindingsAdrRulesClient();
        var fetched = rules.FetchActive(url, token);
        var result = ConformanceRunner.CheckFetched(fetched, opts, null);
        var by = result.Results.GroupBy(r => r.Verdict).ToDictionary(g => g.Key, g => g.Count());
        var ndjson = BuildConformanceNdjson(result, commit);
        var (code, resp) = await PostStringAsync("/ingest/conformance", ndjson, "application/x-ndjson");
        if (code is 200 or 201) Console.WriteLine($"CONFORM  → {result.Results.Count} verdicts (pass={by.GetValueOrDefault("pass")} fail={by.GetValueOrDefault("fail")} unknown={by.GetValueOrDefault("unknown")}) → {Trunc(resp)}");
        else { Console.Error.WriteLine($"CONFORM  ✗ {code} {Trunc(resp)}"); errors++; }
    }
    catch (Exception ex) { Console.Error.WriteLine($"CONFORM  ✗ {Trunc(ex.Message)}"); errors++; }

    // 7) Scan-run receipts
    try
    {
        await ingest.PostScanRunsAsync(new ScanRunsIngestRequest
        {
            Client = client, Project = project, Component = component, ComponentKind = "solution",
            Version = version, CommitSha = commit, Branch = branch, Receipts = receipts,
        });
        Console.WriteLine($"RECEIPTS → {receipts.Count} posted");
    }
    catch (Exception ex) { Console.Error.WriteLine($"RECEIPTS ✗ {Trunc(ex.Message)}"); errors++; }

    Console.WriteLine(errors == 0 ? "\nIngest complete ✅" : $"\nIngest completed with {errors} error(s) ⚠");
    return errors == 0 ? 0 : 1;
}

string BuildConformanceNdjson(ConformanceRunResult result, string sha)
{
    var runId = Guid.NewGuid().ToString("N");
    var traceId = sha.Length >= 32 ? sha[..32] : sha.PadRight(32, '0');
    long seq = 0;
    var sb = new StringBuilder();
    foreach (var r in result.Results)
    {
        var ev = new BuildEvent
        {
            Type = BuildEventTypes.ConformanceEvaluated,
            BuildId = traceId, RunId = runId, TraceId = traceId,
            SpanId = Guid.NewGuid().ToString("N")[..16],
            WorkerId = "ci:tamp-findings-ingest", Seq = seq++,
            Payload = new ConformanceEvaluatedPayload
            {
                AdrRef = r.AdrRef, RuleId = r.RuleId, Verdict = r.Verdict, Method = r.Method,
                AdrQuote = r.AdrQuote, CodeEvidence = r.CodeEvidence,
                Location = r.File is null ? null : new DiagnosticLocation { File = r.File, Line = r.Line },
                Blocks = r.Blocks, ControlRefs = r.ControlRefs,
                Provenance = new Provenance { CommitSha = sha, RulesSha = "", Method = r.Method },
                ZtPillar = r.ZtPillar, ZtFunction = r.ZtFunction, ZtStage = r.ZtStage, MandateId = r.MandateId,
            },
        };
        sb.Append(BuildEventJson.Serialize(ev)).Append('\n');
    }
    return sb.ToString();
}

async Task<(int, string)> PostFileRawAsync(string path, string file, string contentType, string extra = "")
{
    var q = $"{url}{path}?client={Uri.EscapeDataString(client)}&project={Uri.EscapeDataString(project)}&version={Uri.EscapeDataString(version)}&commitSha={Uri.EscapeDataString(commit)}&branch={Uri.EscapeDataString(branch)}{extra}";
    using var content = new ByteArrayContent(await File.ReadAllBytesAsync(file));
    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
    var resp = await http.PostAsync(q, content);
    return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
}

async Task<(int, string)> PostStringAsync(string path, string bodyStr, string contentType)
{
    using var content = new StringContent(bodyStr, Encoding.UTF8, contentType);
    var resp = await http.PostAsync($"{url}{path}", content);
    return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
}

static string? FirstExisting(params string[] paths) => Array.Find(paths, File.Exists);
static string Short(string sha) => sha.Length >= 7 ? sha[..7] : sha;
static string Trunc(string s) { s = s.Replace('\n', ' ').Trim(); return s.Length > 200 ? s[..200] + "…" : s; }
