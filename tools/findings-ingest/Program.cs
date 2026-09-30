using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tamp;
using Tamp.Conformance;
using Tamp.Conformance.Quality;
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
if (cmd == "quality") return await QualityAsync();
if (cmd == "generate") return await GenerateAsync();
Console.Error.WriteLine($"unknown command '{cmd}' (expected: ingest | quality | gate | generate)");
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
    Guid? sbomSnapshotId = null;
    var sbomComponents = 0;
    var sbomPath = Path.Combine(evidence, "sbom.cdx.json");
    if (File.Exists(sbomPath))
        try
        {
            // lic tool: resolve legacy licenseUrl / "Unknown - See URL" → SPDX before emitting, so
            // license facts ride the SBOM (one bundle, no license-specific endpoint). Pure evidence
            // enrichment — findings still owns license policy.
            var (sbomToLoad, licResolved) = ResolveLicenses(sbomPath);
            var bom = SbomReader.LoadFromFile(AbsolutePath.Create(sbomToLoad));
            sbomComponents = bom.Components?.Count ?? 0;
            var r = await ingest.PostSbomAsync(hier, bom, toolName: "CycloneDX", toolVersion: "6.2.0");
            sbomSnapshotId = r.SbomSnapshotId;
            Console.WriteLine($"SBOM     → snapshot={r.SbomSnapshotId} components={sbomComponents} licenses-resolved={licResolved}");
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

    // 5b) SCA vulnerabilities (grype against the SBOM) → /sbom-vulnerabilities/upsert + a Grype
    // scan-run receipt whose Notes carry the advisory-DB provenance. This is what makes "0 CVEs"
    // mean "scanned clean against grype-db <build>" instead of "no scan ran".
    var grypePath = Path.Combine(evidence, "grype.json");
    if (sbomSnapshotId is { } snap && File.Exists(grypePath))
        try
        {
            var (vulns, dbNotes, toolVer) = ParseGrype(grypePath, sbomComponents);
            var resp = await ingest.PostSbomVulnerabilitiesUpsertAsync(new SbomVulnerabilitiesUpsertRequest
            {
                SnapshotId = snap, Vulnerabilities = vulns,
            });
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
static string Trunc(string s) { s = s.Replace('\n', ' ').Trim(); return s.Length > 200 ? s[..200] + "…" : s; }

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
