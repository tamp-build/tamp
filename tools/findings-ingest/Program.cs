using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Tamp;
using Tamp.Conformance;
using Tamp.Conformance.Quality;
using Tamp.Ingest.V1;
using Tamp.Sarif;
using Tamp.Sbom;

// tamp-core dogfood: push our own build evidence to tamp-findings and gate on the result.
//
//   ingest  — SBOM + SAST + secrets + coverage + test-results + conformance + receipts → findings
//   posture — repo/org security posture (branch protection, reviews, signatures, CODEOWNERS, org 2FA) → findings
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

const int RetryMaxAttempts = 4; // transient-retry budget: 1 try + 3 retries; backoff 2s, 4s, 8s

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
if (cmd == "quality") return await QualityAsync();
if (cmd == "posture") return await PostureAsync();
if (cmd == "generate") return await GenerateAsync();
Console.Error.WriteLine($"unknown command '{cmd}' (expected: ingest | quality | posture | gate | generate)");
return 2;

async Task<int> GateAsync()
{
    var q = $"{url}/projects/{projectId}/gate?commitSha={Uri.EscapeDataString(commit)}";
    var (code, body) = await SendWithRetryAsync(async () =>
    {
        var r = await http.GetAsync(q);
        return ((int)r.StatusCode, await r.Content.ReadAsStringAsync());
    }, "GET gate");
    if (code is not (200 or 201)) { Console.Error.WriteLine($"gate query {code}: {Trunc(body)}"); return 2; }
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

    // 1) SBOM — via Tamp.Ingest.V1 0.3.0 PostSbomAsync (tamp-ingest-v1#8 shipped SbomComponent.DevDependency
    // + the mapper classifier, retiring the former hand-rolled POST). The lic resolver runs first so license
    // facts ride the SBOM; devDependency is a producer fact computed from the per-project restore closures
    // (shipping = packable src libs; dev = test projects + the Tamp.Analyzers DevelopmentDependency) and
    // applied per component by the mapper. Dependency edges are carried by the mapper. findings owns the
    // exemption policy.
    Guid? sbomSnapshotId = null;
    var sbomComponents = 0;
    var sbomPath = Path.Combine(evidence, "sbom.cdx.json");
    if (File.Exists(sbomPath))
        try
        {
            var (sbomToLoad, licResolved) = ResolveLicenses(sbomPath);
            var shipping = ShippingClosure(repoRoot);
            var bom = SbomReader.Parse(File.ReadAllText(sbomToLoad));
            var sbomReq = CycloneDxSbomMapper.BuildRequest(hier, bom,
                toolName: "CycloneDX", toolVersion: "6.2.0",
                metadataTools: new[] { new SbomMetadataTool { Vendor = "CycloneDX", Name = "CycloneDX module for .NET", Version = "6.2.0" } },
                isDevDependency: c => !shipping.Contains($"{c.Name}|{c.Version}"));
            var count = sbomReq.Components.Count;
            var devCount = sbomReq.Components.Count(c => c.DevDependency == true);
            sbomComponents = count;
            var sbomResp = await RetryOnExAsync(() => ingest.PostSbomAsync(sbomReq), "SBOM");
            sbomSnapshotId = sbomResp.SbomSnapshotId;
            Console.WriteLine($"SBOM     → snapshot={sbomSnapshotId} components={count} dev={devCount} shipping={count - devCount} licenses-resolved={licResolved}");

            // Enrich components with registry version data (LatestVersion / LatestReleasedAt) so SBOM
            // freshness reads real values, not "Not assessed". findings only enriches on this call.
            var (ec, er) = await PostStringAsync($"/sbom-components/enrich-versions?snapshotId={sbomSnapshotId}", "", "application/json");
            if (ec is 200 or 201) Console.WriteLine($"SBOM-ENR → {Trunc(er)}");
            else Console.Error.WriteLine($"SBOM-ENR ⚠ {ec} {Trunc(er)}");
        }
        catch (Exception ex) { Console.Error.WriteLine($"SBOM     ✗ {Trunc(ex.Message)}"); errors++; }
    else Console.Error.WriteLine($"SBOM     ⚠ missing {sbomPath}");

    // 2) SAST (opengrep SARIF)
    var sastPath = Path.Combine(evidence, "opengrep.sarif");
    if (File.Exists(sastPath))
        try
        {
            var sarif = SarifReader.LoadFromFile(AbsolutePath.Create(sastPath));
            var r = await RetryOnExAsync(() => ingest.PostFindingsAsync(hier, ScannerKind.OpenGrep, sarif), "SAST");
            Console.WriteLine($"SAST     → cv={r.ComponentVersionId} inserted={r.FindingsInserted} (opengrep)");
            receipts.Add(Receipt(ScannerKind.OpenGrep, "opengrep", "1.x", r.FindingsInserted));
        }
        catch (Exception ex) { Console.Error.WriteLine($"SAST     ✗ {Trunc(ex.Message)}"); errors++; }
    else Console.Error.WriteLine($"SAST     ⚠ missing {sastPath}");

    // 3) Secrets (trufflehog — record the scan ran; verified secrets fail the workflow step upstream)
    try
    {
        var r = await RetryOnExAsync(() => ingest.PostFindingsAsync(new FindingsIngestRequest
        {
            Client = client, Project = project, Component = component, ComponentKind = "solution",
            Version = version, CommitSha = commit, Branch = branch,
            Scanner = ScannerKind.TruffleHog, Findings = Array.Empty<IngestFinding>(),
        }), "SECRETS");
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

    // 5b) SCA vulnerabilities (grype against the SBOM) → /sbom-vulnerabilities/upsert + a Grype
    // scan-run receipt whose Notes carry the advisory-DB provenance. This is what makes "0 CVEs"
    // mean "scanned clean against grype-db <build>" instead of "no scan ran".
    var grypePath = Path.Combine(evidence, "grype.json");
    if (sbomSnapshotId is { } snap && File.Exists(grypePath))
        try
        {
            var (vulns, dbNotes, toolVer) = ParseGrype(grypePath, sbomComponents);
            var resp = await RetryOnExAsync(() => ingest.PostSbomVulnerabilitiesUpsertAsync(new SbomVulnerabilitiesUpsertRequest
            {
                SnapshotId = snap, Vulnerabilities = vulns,
            }), "SCA");
            Console.WriteLine($"SCA      → {vulns.Count} CVEs (matched={resp.Matched} inserted={resp.Inserted}) — {dbNotes}");
            receipts.Add(new ScanRunReceipt
            {
                Scanner = ScannerKind.Grype, Status = ScanRunStatus.Succeeded,
                StartedAt = started, CompletedAt = DateTimeOffset.UtcNow,
                FindingsCount = vulns.Count, ToolName = "grype", ToolVersion = toolVer, Notes = dbNotes,
            });
        }
        catch (Exception ex) { Console.Error.WriteLine($"SCA      ✗ {Trunc(ex.Message)}"); errors++; }
    else if (sbomSnapshotId is null) Console.Error.WriteLine("SCA      ⚠ no SBOM snapshot id — skipping vuln upsert");
    else Console.Error.WriteLine($"SCA      ⚠ missing {grypePath} — no SCA scan to ingest");

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

        // Surface each non-pass verdict in the log — the summary count alone ("fail=10") doesn't say
        // WHICH ADR rules fail, so the dogfood gap stays invisible. Print fails/errors with location +
        // evidence so they're actionable straight from the CI log. (unknown = semantic rule w/ no
        // evaluator wired — deterministic-only run; not a real gap, so left out of this list.)
        foreach (var r in result.Results.Where(r => r.Verdict is "fail" or "error")
                                        .OrderBy(r => r.AdrRef, StringComparer.Ordinal).ThenBy(r => r.RuleId, StringComparer.Ordinal))
        {
            var loc = r.File is { Length: > 0 } ? $" {r.File}{(r.Line > 0 ? $":{r.Line}" : "")}" : "";
            var why = Trunc(r.CodeEvidence ?? r.AdrQuote ?? "");
            Console.WriteLine($"  CONFORM {(r.Verdict == "error" ? "‼" : "✗")} {r.AdrRef}/{r.RuleId}{loc} — {why}");
        }
    }
    catch (Exception ex) { Console.Error.WriteLine($"CONFORM  ✗ {Trunc(ex.Message)}"); errors++; }

    // 7) Scan-run receipts
    try
    {
        await RetryVoidAsync(() => ingest.PostScanRunsAsync(new ScanRunsIngestRequest
        {
            Client = client, Project = project, Component = component, ComponentKind = "solution",
            Version = version, CommitSha = commit, Branch = branch, Receipts = receipts,
        }), "RECEIPTS");
        Console.WriteLine($"RECEIPTS → {receipts.Count} posted");
    }
    catch (Exception ex) { Console.Error.WriteLine($"RECEIPTS ✗ {Trunc(ex.Message)}"); errors++; }

    // 8) Scan token usage — the deterministic conformance check spends NO model tokens, but we
    // report it (empty usage set) so the Costs page shows "scanned, 0 tokens" not a blank. Real
    // token cost lives in rule GENERATION (the `generate` command), reported there.
    try
    {
        await PostScanUsageAsync(Array.Empty<object>());
        Console.WriteLine("USAGE    → scan-usage reported (0 model tokens — conformance check is deterministic)");
    }
    catch (Exception ex) { Console.Error.WriteLine($"USAGE    ✗ {Trunc(ex.Message)}"); errors++; }

    Console.WriteLine(errors == 0 ? "\nIngest complete ✅" : $"\nIngest completed with {errors} error(s) ⚠");
    return errors == 0 ? 0 : 1;
}

// Quality + SAST lane (ADR 0006): emit BOTH sources — live SonarCloud fetch + local Roslyn/SonarAnalyzer
// SARIF — as distinct scanner batches (findings owns cross-source dedupe via normalizedRuleId), plus the
// SonarCloud quality-gate verdict and the analysis-coverage report. Runs inline so all quality evidence
// shares the build's commit with the rest. Each source degrades gracefully: no SONAR_TOKEN → skip
// SonarCloud (Roslyn-only, no gate); no SARIF dir → skip Roslyn.
async Task<int> QualityAsync()
{
    Console.WriteLine($"→ {url}  QUALITY {client}/{project}/{component} version={version} commit={Short(commit)} branch={branch}\n");
    var env = new QualityEnvelope(client, project, version, commit, branch, component, "solution", null);
    var errors = 0;
    var receipts = new List<object>();
    var started = DateTimeOffset.UtcNow;
    var coverageContribs = new List<CoverageContribution>();

    // 1) SonarCloud (live) → findings batch + quality-gate verdict. Needs SONAR_TOKEN.
    var sonarToken = Env("SONAR_TOKEN");
    if (sonarToken.Length > 0)
        try
        {
            var sonarUrl = Env("SONAR_URL", "https://sonarcloud.io");
            var projectKey = Env("SONAR_PROJECT_KEY", Env("GITHUB_REPOSITORY").Replace('/', '_'));
            var sonarBranch = Env("SONAR_BRANCH", branch);
            using var sonarHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var snap = await new SonarCloudFetcher(sonarHttp, sonarToken, sonarUrl).FetchAsync(projectKey, sonarBranch);
            var sonarFindings = new QualityAdapter(snap.ToSource()).Run().All;
            var (fc, fr) = await PostStringAsync("/ingest/findings", QualityEmit.FindingsBody(env, "SonarQube", sonarFindings), "application/json");
            if (fc is 200 or 201) Console.WriteLine($"QUAL-SQ  → {sonarFindings.Count} findings (projectKey={projectKey}) → {Trunc(fr)}");
            else { Console.Error.WriteLine($"QUAL-SQ  ✗ {fc} {Trunc(fr)}"); errors++; }
            receipts.Add(new { scanner = "SonarQube", status = "Succeeded", startedAt = started, completedAt = DateTimeOffset.UtcNow, findingsCount = sonarFindings.Count, toolName = "SonarCloud", toolVersion = "cloud", notes = $"server={sonarUrl}; projectKey={projectKey}; branch={sonarBranch}; findings={sonarFindings.Count}" });

            var gate = snap.ToGateVerdict();
            var (gc, gr) = await PostStringAsync("/ingest/quality-gate", QualityEmit.QualityGateBody(env, gate), "application/json");
            if (gc is 200 or 201) Console.WriteLine($"QUAL-GATE→ status={gate.Status} → {Trunc(gr)}");
            else { Console.Error.WriteLine($"QUAL-GATE✗ {gc} {Trunc(gr)}"); errors++; }

            coverageContribs.Add(new CoverageContribution("sonarqube", sonarFindings.Select(f => f.FilePath).Distinct().ToArray()));
        }
        catch (Exception ex) { Console.Error.WriteLine($"QUAL-SQ  ✗ {Trunc(ex.Message)}"); errors++; }
    else Console.Error.WriteLine("QUAL-SQ  ⚠ SONAR_TOKEN not set — skipping SonarCloud quality lane");

    // 2) Local Roslyn/SonarAnalyzer SARIF → findings batch (no gate; a local analyzer has none).
    var roslynDir = Env("ROSLYN_SARIF_DIR", Path.Combine(repoRoot, "artifacts", "security", "roslyn"));
    if (Directory.Exists(roslynDir))
        try
        {
            var roslynFindings = new QualityAdapter(RoslynSarifSource.FromDirectory(roslynDir, repoRoot)).Run().All;
            var (rc, rr) = await PostStringAsync("/ingest/findings", QualityEmit.FindingsBody(env, "Roslyn", roslynFindings), "application/json");
            if (rc is 200 or 201) Console.WriteLine($"QUAL-ROS → {roslynFindings.Count} findings ({roslynDir}) → {Trunc(rr)}");
            else { Console.Error.WriteLine($"QUAL-ROS ✗ {rc} {Trunc(rr)}"); errors++; }
            receipts.Add(new { scanner = "Roslyn", status = "Succeeded", startedAt = started, completedAt = DateTimeOffset.UtcNow, findingsCount = roslynFindings.Count, toolName = "SonarAnalyzer.CSharp", toolVersion = "roslyn", notes = $"local in-CI Roslyn analysis; profile=SonarWay; findings={roslynFindings.Count}" });
            coverageContribs.Add(new CoverageContribution("roslyn", EnumerateCsFiles(repoRoot)));
        }
        catch (Exception ex) { Console.Error.WriteLine($"QUAL-ROS ✗ {Trunc(ex.Message)}"); errors++; }
    else Console.Error.WriteLine($"QUAL-ROS ⚠ no SARIF dir {roslynDir} — skipping Roslyn quality lane");

    // 3) Analysis-coverage — only the producer can compute it (full checkout).
    try
    {
        var report = new AnalysisCoverageAccountant(repoRoot).Compute(coverageContribs);
        var (cc, cr) = await PostStringAsync("/ingest/analysis-coverage", QualityEmit.AnalysisCoverageBody(env, report), "application/json");
        var gaps = string.Join(",", report.Overall.LanguagesWithFootprintNoAnalyzer);
        if (cc is 200 or 201) Console.WriteLine($"QUAL-COV → {report.Languages.Count} langs, overall {report.Overall.PercentAnalyzed}% (gaps: {(gaps.Length == 0 ? "none" : gaps)}) → {Trunc(cr)}");
        else { Console.Error.WriteLine($"QUAL-COV ✗ {cc} {Trunc(cr)}"); errors++; }
    }
    catch (Exception ex) { Console.Error.WriteLine($"QUAL-COV ✗ {Trunc(ex.Message)}"); errors++; }

    // 4) Quality scan-run receipts (SonarQube credits RanQuality+RanSast; Roslyn credits the same).
    if (receipts.Count > 0)
        try
        {
            var body = JsonSerializer.Serialize(new { client, project, component, componentKind = "solution", version, commitSha = commit, branch, receipts });
            var (pc, pr) = await PostStringAsync("/ingest/scan-runs", body, "application/json");
            if (pc is 200 or 201) Console.WriteLine($"QUAL-RCPT→ {receipts.Count} posted");
            else { Console.Error.WriteLine($"QUAL-RCPT✗ {pc} {Trunc(pr)}"); errors++; }
        }
        catch (Exception ex) { Console.Error.WriteLine($"QUAL-RCPT✗ {Trunc(ex.Message)}"); errors++; }

    Console.WriteLine(errors == 0 ? "\nQuality lane complete ✅" : $"\nQuality lane completed with {errors} error(s) ⚠");
    return errors == 0 ? 0 : 1;
}

// All compiled C# files (the Roslyn-analyzed set for coverage attribution), minus build/generated output.
static string[] EnumerateCsFiles(string root)
{
    try
    {
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p =>
            {
                var u = p.Replace('\\', '/');
                return !u.Contains("/bin/") && !u.Contains("/obj/") && !u.Contains("/artifacts/")
                    && !u.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                    && !u.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();
    }
    catch { return Array.Empty<string>(); }
}

// Regenerate ADR rules via the LLM (the real token spend) → push to findings → report token usage.
// Run this when ADRs change (an ADR-edit workflow), not nightly.
async Task<int> GenerateAsync()
{
    var apiKey = Env("ANTHROPIC_API_KEY");
    if (apiKey.Length == 0) { Console.Error.WriteLine("FATAL: ANTHROPIC_API_KEY not set (required for rule generation)"); return 2; }
    var genModel = Env("GEN_MODEL_ID", "claude-opus-4-8");
    Console.WriteLine($"→ generate rules from ADRs via {genModel}  {client}/{project} commit={Short(commit)}\n");

    var opts = new ConformanceOptions { RepoRoot = AbsolutePath.Create(repoRoot), CommitSha = commit };
    using var chat = new UsageChat(apiKey, genModel, maxTokens: 8192);
    var extractor = new LlmRuleExtractor(chat);
    var adrs = RuleStore.AdrFiles(opts.ResolvedAdrDir);
    var all = new List<AdrRuleWithRef>();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    foreach (var (adrId, file) in adrs)
    {
        try
        {
            var set = RuleGeneration.Generate(adrId, file, extractor, extractedBy: chat.ModelId);
            foreach (var r in set.Rules) all.Add(new AdrRuleWithRef { AdrRef = adrId, Rule = r });
        }
        catch (Exception ex) { Console.Error.WriteLine($"  ADR {adrId}: extraction failed — {Trunc(ex.Message)}"); }
    }
    sw.Stop();
    Console.WriteLine($"Extracted {all.Count} rules from {adrs.Count} ADRs — {chat.InputTokens:N0} in / {chat.OutputTokens:N0} out over {chat.Calls} calls ({sw.Elapsed.TotalSeconds:N0}s)");

    using var store = new FindingsAdrRulesClient();
    var pushed = store.PushGeneration(url, token, chat.ModelId, all);
    Console.WriteLine($"PUSH     → upserted={pushed.Upserted} retired={pushed.Retired} active={pushed.Active}");

    try
    {
        await PostScanUsageAsync(new[] { new
        {
            adapter = "adr-generation", modelId = genModel, provider = "anthropic", capability = "rule-extraction",
            inputTokens = chat.InputTokens, outputTokens = chat.OutputTokens,
            latencyMs = (long)sw.Elapsed.TotalMilliseconds, observedAt = DateTimeOffset.UtcNow,
        }});
        Console.WriteLine($"USAGE    → scan-usage reported ({chat.InputTokens:N0} in / {chat.OutputTokens:N0} out on {genModel})");
    }
    catch (Exception ex) { Console.Error.WriteLine($"USAGE    ✗ {Trunc(ex.Message)}"); return 1; }
    return 0;
}

// POST /ingest/posture (TAM-290) — repo/org security posture as FACTS, not judgements. Reads five
// GitHub signals via `gh api` and reports each as pass|fail|not-applicable|unknown. Posture is a
// property of the repo/org (no build/commit), so the body is just client/project/source/observations.
//
// Non-blocking by construction: a signal we cannot read is reported `unknown`, never omitted — the
// full set is posted every run (findings ages an observation out after 30 days, and an enabled gate
// with no report is itself Unknown+blocking, so silence is the worse failure). findings decides what
// gates (CM-3 / CM-5 / CM-5(1) / IA-2(1) / IA-2(2)); this just tells it the truth.
//
// Auth: `gh` uses GH_TOKEN. branch-protection / required_signatures need `administration: read`;
// contents needs `contents: read`. org-2fa needs a `read:org` token — the default GITHUB_TOKEN
// cannot read it, so on an org repo org-2fa reports `unknown` unless a read:org token is supplied.
async Task<int> PostureAsync()
{
    var repo = Env("GITHUB_REPOSITORY", "tamp-build/tamp");
    var slash = repo.IndexOf('/');
    var owner = slash > 0 ? repo[..slash] : repo;
    var name = slash > 0 ? repo[(slash + 1)..] : repo;
    Console.WriteLine($"→ {url}  POSTURE {client}/{project}  repo={owner}/{name}\n");

    // Repo record: owner type (→ org-2fa applicability) + default branch (protection is defined on it).
    string? ownerType = null, defaultBranch = null;
    var (repoOut, _, repoExit) = Gh($"api repos/{owner}/{name}", repoRoot);
    if (repoExit == 0)
        try
        {
            using var d = JsonDocument.Parse(repoOut);
            defaultBranch = Prop(d.RootElement, "default_branch");
            if (d.RootElement.TryGetProperty("owner", out var ow)) ownerType = Prop(ow, "type");
        }
        catch { /* fall through to defaults */ }
    var brch = Env("TAMP_BRANCH", defaultBranch ?? "main");

    var now = DateTimeOffset.UtcNow;
    var obs = new List<PostureObservation>();
    PostureObservation O(string id, PostureStatus s, string? detail = null) =>
        new() { CheckId = id, Status = s, Detail = detail, ObservedAt = now };

    // 1+2) branch-protection + pr-reviews-required — one payload.
    var (protOut, protErr, protExit) = Gh($"api repos/{owner}/{name}/branches/{brch}/protection", repoRoot);
    if (protExit == 0)
        try
        {
            using var d = JsonDocument.Parse(protOut);
            var r = d.RootElement;
            var forceAllowed = r.TryGetProperty("allow_force_pushes", out var afp)
                && afp.TryGetProperty("enabled", out var afpe) && afpe.ValueKind == JsonValueKind.True;
            var checks = 0;
            if (r.TryGetProperty("required_status_checks", out var rsc) && rsc.ValueKind == JsonValueKind.Object)
            {
                if (rsc.TryGetProperty("checks", out var ch) && ch.ValueKind == JsonValueKind.Array) checks = ch.GetArrayLength();
                else if (rsc.TryGetProperty("contexts", out var ctx) && ctx.ValueKind == JsonValueKind.Array) checks = ctx.GetArrayLength();
            }
            obs.Add(forceAllowed
                ? O("branch-protection", PostureStatus.Fail, $"branch protected but force-push allowed; {checks} status checks required")
                : O("branch-protection", PostureStatus.Pass, $"force-push blocked; {checks} status checks required"));

            var reviews = 0;
            var hasReviews = r.TryGetProperty("required_pull_request_reviews", out var rpr) && rpr.ValueKind == JsonValueKind.Object;
            if (hasReviews && rpr.TryGetProperty("required_approving_review_count", out var cnt) && cnt.TryGetInt32(out var c)) reviews = c;
            obs.Add(hasReviews && reviews >= 1
                ? O("pr-reviews-required", PostureStatus.Pass, $"{reviews} approving review{(reviews == 1 ? "" : "s")} required")
                : O("pr-reviews-required", PostureStatus.Fail, "no approving review required"));
        }
        catch
        {
            obs.Add(O("branch-protection", PostureStatus.Unknown, "protection payload unparseable"));
            obs.Add(O("pr-reviews-required", PostureStatus.Unknown, "protection payload unparseable"));
        }
    else
    {
        var st = GhHttpStatus(protErr);
        if (st == 404)
        {
            obs.Add(O("branch-protection", PostureStatus.Fail, $"branch '{brch}' not protected"));
            obs.Add(O("pr-reviews-required", PostureStatus.Fail, $"branch '{brch}' not protected"));
        }
        else
        {
            var why = $"cannot read branch protection{(st > 0 ? $" (HTTP {st})" : "")}";
            obs.Add(O("branch-protection", PostureStatus.Unknown, why));
            obs.Add(O("pr-reviews-required", PostureStatus.Unknown, why));
        }
    }

    // 3) signed-commits — dedicated sub-resource (unambiguous vs. digging in the protection blob).
    var (sigOut, sigErr, sigExit) = Gh($"api repos/{owner}/{name}/branches/{brch}/protection/required_signatures", repoRoot);
    if (sigExit == 0)
        try
        {
            using var d = JsonDocument.Parse(sigOut);
            var enabled = d.RootElement.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
            obs.Add(O("signed-commits", enabled ? PostureStatus.Pass : PostureStatus.Fail, enabled ? "required" : "not required"));
        }
        catch { obs.Add(O("signed-commits", PostureStatus.Unknown, "required_signatures payload unparseable")); }
    else
    {
        var st = GhHttpStatus(sigErr);
        obs.Add(st == 404
            ? O("signed-commits", PostureStatus.Fail, "not required")
            : O("signed-commits", PostureStatus.Unknown, $"cannot read required_signatures{(st > 0 ? $" (HTTP {st})" : "")}"));
    }

    // 4) codeowners — present at any of the three canonical locations.
    bool coFound = false, coForbidden = false;
    var coWhere = "";
    foreach (var p in new[] { ".github/CODEOWNERS", "CODEOWNERS", "docs/CODEOWNERS" })
    {
        var (_, coErr, coExit) = Gh($"api repos/{owner}/{name}/contents/{p}", repoRoot);
        if (coExit == 0) { coFound = true; coWhere = p; break; }
        if (GhHttpStatus(coErr) is 401 or 403) coForbidden = true;
    }
    obs.Add(coFound ? O("codeowners", PostureStatus.Pass, $"present at {coWhere}")
        : coForbidden ? O("codeowners", PostureStatus.Unknown, "cannot read repo contents")
        : O("codeowners", PostureStatus.Fail, "no CODEOWNERS file"));

    // 5) org-2fa — n/a for a user-owned repo; otherwise needs a read:org token to be visible.
    if (string.Equals(ownerType, "User", StringComparison.OrdinalIgnoreCase))
        obs.Add(O("org-2fa", PostureStatus.NotApplicable, "user-owned repo"));
    else
    {
        var (orgOut, orgErr, orgExit) = Gh($"api orgs/{owner}", repoRoot);
        if (orgExit == 0)
            try
            {
                using var d = JsonDocument.Parse(orgOut);
                if (d.RootElement.TryGetProperty("two_factor_requirement_enabled", out var tf) && tf.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    obs.Add(O("org-2fa", tf.GetBoolean() ? PostureStatus.Pass : PostureStatus.Fail, tf.GetBoolean() ? "org requires 2FA" : "org does not require 2FA"));
                else
                    obs.Add(O("org-2fa", PostureStatus.Unknown, "org 2FA requirement not visible to this token (needs read:org)"));
            }
            catch { obs.Add(O("org-2fa", PostureStatus.Unknown, "org payload unparseable")); }
        else
        {
            var st = GhHttpStatus(orgErr);
            obs.Add(O("org-2fa", PostureStatus.Unknown, $"cannot read org{(st > 0 ? $" (HTTP {st})" : "")} (needs read:org)"));
        }
    }

    foreach (var o in obs)
    {
        var mark = o.Status switch
        {
            PostureStatus.Pass => "PASS",
            PostureStatus.Fail => "FAIL",
            PostureStatus.NotApplicable => "n/a ",
            _ => "????",
        };
        Console.WriteLine($"  {mark} {o.CheckId,-20} {o.Detail}");
    }

    using var ingest = new TampIngestClient(new Uri(url), new Secret("ingest-token", token));
    try
    {
        await RetryVoidAsync(() => ingest.PostPostureAsync(new PostureIngestRequest
        {
            Client = client, Project = project, Observations = obs,
        }), "POSTURE");
        Console.WriteLine($"\nPOSTURE  → {obs.Count} observations posted ({client}/{project}) ✅");
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine($"\nPOSTURE  ✗ {Trunc(ex.Message)}"); return 1; }
}

// POST /ingest/scan-usage — token usage for a build's scan run (replace-on-ingest). Empty `usage`
// is meaningful: "scan ran, spent no model tokens" (the deterministic check). findings owns pricing.
async Task PostScanUsageAsync(IEnumerable<object> usage)
{
    var req = new
    {
        client, project, version, commitSha = commit, branch,
        buildId = (string?)null, pullRequestRef = (string?)null,
        usage = usage.ToArray(),
    };
    var (code, resp) = await PostStringAsync("/ingest/scan-usage", JsonSerializer.Serialize(req), "application/json");
    if (code is not (200 or 201)) throw new Exception($"scan-usage {code}: {Trunc(resp)}");
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
    return await SendWithRetryAsync(async () =>
    {
        using var content = new ByteArrayContent(await File.ReadAllBytesAsync(file));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        var resp = await http.PostAsync(q, content);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }, $"POST {path}");
}

async Task<(int, string)> PostStringAsync(string path, string bodyStr, string contentType)
    => await SendWithRetryAsync(async () =>
    {
        using var content = new StringContent(bodyStr, Encoding.UTF8, contentType);
        var resp = await http.PostAsync($"{url}{path}", content);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }, $"POST {path}");

// Parse a grype JSON report → (vulnerabilities, DB-provenance notes, grype version).
static (List<SbomVulnerability> Vulns, string Notes, string ToolVersion) ParseGrype(string path, int components)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    var root = doc.RootElement;
    var vulns = new List<SbomVulnerability>();
    if (root.TryGetProperty("matches", out var matches) && matches.ValueKind == JsonValueKind.Array)
        foreach (var m in matches.EnumerateArray())
        {
            if (!m.TryGetProperty("vulnerability", out var v) || !m.TryGetProperty("artifact", out var a)) continue;
            vulns.Add(new SbomVulnerability
            {
                PackageName = Str(a, "name") ?? "",
                PackageVersion = Str(a, "version") ?? "",
                AdvisoryId = Str(v, "id") ?? "",
                Severity = MapSeverity(Str(v, "severity")),
                Description = Str(v, "description"),
                ReferenceUrl = Str(v, "dataSource"),
            });
        }
    // DB provenance from descriptor.db(.status)
    string built = "?", schema = "?", tool = "?";
    if (root.TryGetProperty("descriptor", out var desc))
    {
        tool = Str(desc, "version") ?? "?";
        if (desc.TryGetProperty("db", out var db))
        {
            var status = db.TryGetProperty("status", out var st) ? st : db;
            built = Str(status, "built") ?? "?";
            if (status.TryGetProperty("schemaVersion", out var sv))
                schema = sv.ValueKind == JsonValueKind.Number ? sv.GetInt32().ToString() : (sv.GetString() ?? "?");
        }
    }
    var schemaLabel = schema.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? schema : $"v{schema}";
    var notes = $"components={components}; db=grype-db/{schemaLabel}; db_built={built}; matched={vulns.Count}; cves={vulns.Count}";
    return (vulns, notes, tool);

    static string? Str(JsonElement e, string prop) => e.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}

static Severity MapSeverity(string? s) => (s ?? "").ToLowerInvariant() switch
{
    "critical" => Severity.Critical,
    "high" => Severity.High,
    "medium" => Severity.Medium,
    "low" => Severity.Low,
    _ => Severity.Info, // negligible / unknown / null
};

// Shipping dependency closure = union of resolved packages (Name|Version) across packable src/* projects,
// EXCLUDING Tamp.Analyzers (DevelopmentDependency, netstandard2.0 — its closure is build-time-only).
// A component NOT in this set is dev/build-only (test SDKs, the analyzer's netstandard shims, etc.).
// Computed from per-project restore graphs (project.assets.json) — the build's own ship/no-ship truth.
static HashSet<string> ShippingClosure(string repoRoot)
{
    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var srcDir = Path.Combine(repoRoot, "src");
    if (!Directory.Exists(srcDir)) return set;
    foreach (var assets in Directory.GetFiles(srcDir, "project.assets.json", SearchOption.AllDirectories))
    {
        var proj = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(assets)) ?? ""); // src/<Proj>/obj/.. → <Proj>
        if (string.Equals(proj, "Tamp.Analyzers", StringComparison.OrdinalIgnoreCase)) continue;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(assets));
            if (!doc.RootElement.TryGetProperty("targets", out var targets)) continue;
            foreach (var tfm in targets.EnumerateObject())
                foreach (var lib in tfm.Value.EnumerateObject())
                {
                    if (!(lib.Value.TryGetProperty("type", out var t) && t.GetString() == "package")) continue;
                    var slash = lib.Name.IndexOf('/');
                    if (slash > 0) set.Add($"{lib.Name[..slash]}|{lib.Name[(slash + 1)..]}");
                }
        }
        catch { /* best-effort; a missing assets file just means fewer shipping entries */ }
    }
    return set;
}

// lic tool — resolve a CycloneDX SBOM's legacy `licenseUrl` / "Unknown - See URL" components to
// SPDX ids in place, so license facts travel inside the SBOM evidence (no license-specific endpoint).
// Two resolvers: self-describing license URLs (generic), then a curated purl→SPDX map for well-known
// packages whose URL is a repo LICENSE blob / MS fwlink (not self-describing). Pure evidence — findings
// still owns license policy. Returns the path to load (corrected copy if anything changed) + count.
static (string Path, int Resolved) ResolveLicenses(string sbomPath)
{
    JsonNode? root;
    try { root = JsonNode.Parse(File.ReadAllText(sbomPath)); }
    catch { return (sbomPath, 0); }
    var comps = root?["components"]?.AsArray();
    if (comps is null) return (sbomPath, 0);
    var n = 0;
    foreach (var c in comps)
    {
        var purl = c?["purl"]?.GetValue<string>();
        var lics = c?["licenses"]?.AsArray();
        if (lics is null) continue;
        foreach (var le in lics)
        {
            var lic = le?["license"]?.AsObject();
            if (lic is null) continue; // SPDX `expression` form — nothing to resolve
            var id = lic["id"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(id) && !id.Contains("unknown", StringComparison.OrdinalIgnoreCase)) continue;
            var url = lic["url"]?.GetValue<string>();
            var spdx = ResolveUrlToSpdx(url) ?? ResolvePurlToSpdx(purl);
            if (spdx is null) continue;
            lic.Remove("name");
            lic.Remove("url");
            lic["id"] = spdx;
            n++;
        }
    }
    if (n == 0) return (sbomPath, 0);
    var outPath = sbomPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        ? sbomPath[..^5] + ".resolved.json" : sbomPath + ".resolved.json";
    File.WriteAllText(outPath, root!.ToJsonString());
    return (outPath, n);
}

// Self-describing license URLs → SPDX id (generic; no hardcoded packages).
static string? ResolveUrlToSpdx(string? url)
{
    if (string.IsNullOrEmpty(url)) return null;
    var u = url.ToLowerInvariant();
    string? Seg(string marker)
    {
        var i = u.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return null;
        var seg = url[(i + marker.Length)..].TrimEnd('/').Split('/', '?', '#')[0].Replace(".html", "");
        return string.IsNullOrEmpty(seg) ? null : seg;
    }
    if (u.Contains("licenses.nuget.org/")) return Seg("licenses.nuget.org/");
    if (u.Contains("spdx.org/licenses/")) return Seg("spdx.org/licenses/");
    if (u.Contains("opensource.org/licenses/")) return Seg("opensource.org/licenses/");
    if (u.Contains("opensource.org/license/")) return Seg("opensource.org/license/");
    if (u.Contains("apache.org/licenses/license-2.0")) return "Apache-2.0";
    return null;
}

// Curated purl→SPDX for well-known packages whose licenseUrl is a repo LICENSE blob / MS fwlink
// (not self-describing). Keyed by package name so it holds across versions. Seeded from tamp#98.
static string? ResolvePurlToSpdx(string? purl)
{
    if (string.IsNullOrEmpty(purl)) return null;
    var name = purl;
    var at = name.IndexOf('@'); if (at >= 0) name = name[..at];
    var slash = name.LastIndexOf('/'); if (slash >= 0) name = name[(slash + 1)..];
    return name.ToLowerInvariant() switch
    {
        "bogus" => "MIT",
        "microsoft.netcore.platforms" => "MIT",                 // MS relicensed .NET Core to MIT (legacy fwlink EULA in old nuspec)
        "netstandard.library" => "MIT",
        "system.buffers" => "MIT",
        "system.memory" => "MIT",
        "system.numerics.vectors" => "MIT",
        "system.threading.tasks.extensions" => "MIT",
        "xunit.abstractions" => "Apache-2.0",
        _ => null,
    };
}

static string? FirstExisting(params string[] paths) => Array.Find(paths, File.Exists);
static string Short(string sha) => sha.Length >= 7 ? sha[..7] : sha;

// Run `gh <args>` (the authenticated GitHub CLI) and capture (stdout, stderr, exitCode). Mirrors the
// Git() shell-out above — findings-ingest shells out rather than taking a GitHub-client package
// dependency. gh exits non-zero on an HTTP error and prints "... (HTTP <code>)" to stderr.
static (string Out, string Err, int Exit) Gh(string args, string cwd)
{
    try
    {
        var psi = new ProcessStartInfo(ResolveExe("gh"), args)
        { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (o, e, p.ExitCode);
    }
    catch (Exception ex) { return ("", ex.Message, -1); }
}

// Pull the HTTP status out of a gh error line like "gh: Branch not protected (HTTP 404)"; 0 if none.
static int GhHttpStatus(string stderr)
{
    var m = System.Text.RegularExpressions.Regex.Match(stderr ?? "", @"\(HTTP (\d{3})\)");
    return m.Success ? int.Parse(m.Groups[1].Value) : 0;
}

static string? Prop(JsonElement e, string name)
    => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
static string Trunc(string s) { s = s.Replace('\n', ' ').Trim(); return s.Length > 200 ? s[..200] + "…" : s; }

// --- Transient-failure resilience ---------------------------------------------------------------
// A momentary findings outage (Cloudflare 530/1033 tunnel flap, a 502/503/504, a connection reset or
// timeout) used to fail the whole nightly even though build+tests+coverage were green. These helpers
// retry such transient faults with exponential backoff so a brief blip self-heals mid-run. A *sustained*
// outage is handled upstream by the workflow's findings health precheck (skip, don't fail); a reachable
// endpoint returning a non-transient status (4xx, 200) is returned immediately — real problems stay loud.
// (RetryMaxAttempts is declared in the top-level region above so the local helpers can capture it.)

static bool IsTransientStatus(int code) =>
    code is 0 or 408 or 425 or 429 or 500 or 502 or 503 or 504
         or 520 or 521 or 522 or 523 or 524 or 525 or 526 or 527 or 530; // 52x = Cloudflare origin/tunnel

static bool IsTransientException(Exception ex)
{
    if (ex is HttpRequestException or TaskCanceledException or IOException) return true;
    var m = ex.Message;
    // TampIngestClient surfaces the HTTP status in the message — treat transient codes as retryable.
    return m.Contains("530") || m.Contains("502") || m.Contains("503") || m.Contains("504")
        || m.Contains("timed out", StringComparison.OrdinalIgnoreCase)
        || m.Contains("connection", StringComparison.OrdinalIgnoreCase);
}

// Retry a raw (statusCode, body) POST/GET when the status is transient or the send throws transiently.
static async Task<(int, string)> SendWithRetryAsync(Func<Task<(int, string)>> send, string label)
{
    (int, string) last = (0, "");
    for (var attempt = 1; attempt <= RetryMaxAttempts; attempt++)
    {
        try
        {
            last = await send();
            if (!IsTransientStatus(last.Item1)) return last; // success or a real (non-transient) error
        }
        catch (Exception ex) when (IsTransientException(ex))
        {
            last = (0, ex.Message);
        }
        if (attempt < RetryMaxAttempts)
        {
            var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
            Console.Error.WriteLine($"  ⟳ {label}: transient {last.Item1} — retry {attempt}/{RetryMaxAttempts - 1} in {delay.TotalSeconds:N0}s");
            await Task.Delay(delay);
        }
    }
    return last;
}

// Retry a typed client call (TampIngestClient throws on failure) when the thrown fault is transient.
static async Task<T> RetryOnExAsync<T>(Func<Task<T>> op, string label)
{
    for (var attempt = 1; ; attempt++)
    {
        try { return await op(); }
        catch (Exception ex) when (attempt < RetryMaxAttempts && IsTransientException(ex))
        {
            var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
            Console.Error.WriteLine($"  ⟳ {label}: {Trunc(ex.Message)} — retry {attempt}/{RetryMaxAttempts - 1} in {delay.TotalSeconds:N0}s");
            await Task.Delay(delay);
        }
    }
}

// Non-generic variant for client calls that return a plain Task (e.g. PostScanRunsAsync).
static Task RetryVoidAsync(Func<Task> op, string label)
    => RetryOnExAsync(async () => { await op(); return 0; }, label);

// An IChatCompletion (the Tamp.Conformance BYOK seam) over the Anthropic Messages API that
// accumulates token usage — used by `generate` to report real rule-extraction cost.
sealed class UsageChat : Tamp.Conformance.IChatCompletion, IDisposable
{
    private readonly System.Net.Http.HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private readonly string _key, _model;
    private readonly int _maxTokens;
    public long InputTokens, OutputTokens;
    public int Calls;
    public UsageChat(string key, string model, int maxTokens = 8192) { _key = key; _model = model; _maxTokens = maxTokens; }
    public string ModelId => $"anthropic/{_model}";

    public string Complete(string system, string user)
    {
        var body = JsonSerializer.Serialize(new { model = _model, max_tokens = _maxTokens, system, messages = new[] { new { role = "user", content = user } } });
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("x-api-key", _key);
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = _http.Send(req);
        var payload = new StreamReader(resp.Content.ReadAsStream()).ReadToEnd();
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Anthropic {(int)resp.StatusCode}: {payload}");
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        Calls++;
        if (root.TryGetProperty("usage", out var u))
        {
            if (u.TryGetProperty("input_tokens", out var it)) InputTokens += it.GetInt64();
            if (u.TryGetProperty("output_tokens", out var ot)) OutputTokens += ot.GetInt64();
        }
        var sb = new StringBuilder();
        if (root.TryGetProperty("content", out var content))
            foreach (var b in content.EnumerateArray())
                if (b.TryGetProperty("type", out var t) && t.GetString() == "text" && b.TryGetProperty("text", out var tx))
                    sb.Append(tx.GetString());
        return sb.ToString();
    }
    public void Dispose() => _http.Dispose();
}
