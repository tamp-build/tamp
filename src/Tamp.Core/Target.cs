using System.Runtime.CompilerServices;

namespace Tamp;

/// <summary>
/// A target authoring delegate. The build class declares targets as
/// properties of this type:
/// <code>
/// Target Compile => _ => _.Phase(Phase.Build).Executes(() => DotNet.Build());
/// </code>
/// At schedule time, the executor invokes the delegate with a fresh
/// <see cref="ITargetDefinition"/> and reads the configured target back out.
/// </summary>
public delegate ITargetDefinition Target(ITargetDefinition definition);

/// <summary>
/// A condition the executor evaluates at runtime to decide whether a target
/// runs. The expression text is captured at the call site so dry-run output
/// and skip messages can explain <em>why</em> a target was skipped.
/// </summary>
public sealed record TargetCondition(Func<bool> Predicate, string ExpressionText);

/// <summary>
/// Fluent surface for declaring everything Tamp needs to know about a target:
/// phase, dependencies, resources, capability requirements, idempotency,
/// failure handling, telemetry, and the work to perform.
/// </summary>
public interface ITargetDefinition
{
    // Lifecycle / metadata
    ITargetDefinition Phase(Phase phase);
    ITargetDefinition Phase(Phase phase, PhaseDescriptor descriptor);
    ITargetDefinition Description(string description);
    ITargetDefinition Tag(params string[] tags);

    /// <summary>
    /// Marks the target as a top-level entry point — the kind of thing
    /// users invoke directly. Surfaces in <c>--list</c> and IDE runner
    /// menus. Internal helper targets (left unmarked) stay invocable by
    /// name but are hidden from default listings to keep the menu signal-
    /// to-noise high.
    /// </summary>
    /// <remarks>
    /// **Deprecated in 1.1.0.** Targets are now listable + callable by default; this method
    /// is a no-op kept for back-compat. Use <see cref="Internal"/> to opt out (hide from
    /// listings AND prevent direct CLI invocation). The TAMP001 analyzer offers a code-fix
    /// to delete this call.
    /// </remarks>
    [Obsolete("Targets are listable + callable by default in Tamp.Core 1.1.0+. Call is a no-op; remove it. Use .Internal() to opt out.", error: false)]
    ITargetDefinition TopLevel();

    /// <summary>
    /// Mark this target as INTERNAL: hidden from <c>--list</c> AND NOT directly callable
    /// from the CLI. Pure dependency-graph node — runs only as a dependency of another
    /// target. Direct invocation (<c>dotnet tamp ThisTarget</c>) fails with a friendly
    /// error listing which targets depend on it.
    /// </summary>
    /// <remarks>
    /// Mutually exclusive with <see cref="Default"/> — combining them throws at startup.
    /// If an Internal-marked target has no incoming dependency edges, a startup warning
    /// notes that it will never run.
    /// </remarks>
    ITargetDefinition Internal();

    /// <summary>
    /// Mark this target as the default when <c>dotnet tamp</c> is invoked without
    /// any target name. At most one target per build may be marked default; declaring
    /// it on a second target throws <see cref="InvalidOperationException"/> at startup
    /// with the list of marked-default targets. Mutually exclusive with <see cref="Internal"/>.
    /// </summary>
    /// <remarks>
    /// When no target is marked <c>Default()</c>, Tamp falls back to a target literally
    /// named <c>Default</c>, then one literally named <c>Ci</c>, then errors. The decorator
    /// supersedes the name-based fallback, so a target named anything (<c>Compile</c>,
    /// <c>Pack</c>, <c>All</c>) can be the default invocation target.
    /// </remarks>
    ITargetDefinition Default();

    // Dependencies — string-typed (back-compat; users can pass `nameof(X)` or `"X"`).
    ITargetDefinition DependsOn(params string[] targetNames);

    /// <summary>
    /// Dependency by Target reference. The compiler captures the source expression
    /// (e.g. <c>Restore</c>) and the framework records it as the dependency name.
    /// No <c>nameof()</c>; IntelliSense filters to Target-typed members of the build class.
    /// For multiple dependencies, chain: <c>.DependsOn(Restore).DependsOn(Compile)</c>.
    /// </summary>
    ITargetDefinition DependsOn(Target target,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(target))] string? name = null);

    /// <summary>
    /// Multi-target dependency by Target reference (1.3.0+). Equivalent to
    /// chaining <c>.DependsOn(X).DependsOn(Y)...</c>. Names are resolved by
    /// matching each delegate's underlying method against the build class's
    /// Target-typed properties — the same reflection pass that registers them.
    /// </summary>
    /// <remarks>
    /// Use the bare-identifier varargs shape: <c>.DependsOn(Test, Publish, FrontendBuild)</c>.
    /// For dynamically-computed names use the <c>params string[]</c> overload.
    /// </remarks>
    ITargetDefinition DependsOn(Target t1, Target t2, params Target[] more);

    /// <summary>
    /// Order constraint: this target runs after <paramref name="targetNames"/>
    /// when both happen to be in the plan. Does NOT pull them in.
    /// </summary>
    ITargetDefinition After(params string[] targetNames);

    /// <summary>Target-typed equivalent. See <see cref="DependsOn(Target, string?)"/> for the convention.</summary>
    ITargetDefinition After(Target target,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(target))] string? name = null);

    /// <summary>Multi-target order constraint (1.3.0+). See <see cref="DependsOn(Target, Target, Target[])"/>.</summary>
    ITargetDefinition After(Target t1, Target t2, params Target[] more);

    /// <summary>
    /// Order constraint: this target runs before <paramref name="targetNames"/>
    /// when both happen to be in the plan. Does NOT pull them in.
    /// </summary>
    ITargetDefinition Before(params string[] targetNames);

    /// <summary>Target-typed equivalent. See <see cref="DependsOn(Target, string?)"/> for the convention.</summary>
    ITargetDefinition Before(Target target,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(target))] string? name = null);

    /// <summary>Multi-target order constraint (1.3.0+). See <see cref="DependsOn(Target, Target, Target[])"/>.</summary>
    ITargetDefinition Before(Target t1, Target t2, params Target[] more);

    /// <summary>
    /// Outgoing trigger: when this target runs, also pull in
    /// <paramref name="targetNames"/>. Distinct from <see cref="DependsOn(string[])"/>
    /// — triggers add downstream targets to the plan.
    /// </summary>
    ITargetDefinition Triggers(params string[] targetNames);

    /// <summary>Target-typed equivalent. See <see cref="DependsOn(Target, string?)"/> for the convention.</summary>
    ITargetDefinition Triggers(Target target,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(target))] string? name = null);

    /// <summary>Multi-target outgoing trigger (1.3.0+). See <see cref="DependsOn(Target, Target, Target[])"/>.</summary>
    ITargetDefinition Triggers(Target t1, Target t2, params Target[] more);

    /// <summary>
    /// Incoming trigger: this target runs whenever any of
    /// <paramref name="targetNames"/> runs. Equivalent to declaring
    /// <see cref="Triggers(string[])"/> on each of those targets.
    /// </summary>
    ITargetDefinition TriggeredBy(params string[] targetNames);

    /// <summary>Target-typed equivalent. See <see cref="DependsOn(Target, string?)"/> for the convention.</summary>
    ITargetDefinition TriggeredBy(Target target,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(target))] string? name = null);

    /// <summary>Multi-target incoming trigger (1.3.0+). See <see cref="DependsOn(Target, Target, Target[])"/>.</summary>
    ITargetDefinition TriggeredBy(Target t1, Target t2, params Target[] more);

    /// <summary>
    /// Catch-style handler: this target runs only when one of
    /// <paramref name="targetNames"/> failed. Failure handlers are not part
    /// of the normal plan; their own dep tree runs first; their failure does
    /// not reverse the original failure's exit code.
    /// </summary>
    ITargetDefinition OnFailureOf(params string[] targetNames);

    /// <summary>Target-typed equivalent. See <see cref="DependsOn(Target, string?)"/> for the convention.</summary>
    ITargetDefinition OnFailureOf(Target target,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(target))] string? name = null);

    /// <summary>Multi-target failure handler (1.3.0+). See <see cref="DependsOn(Target, Target, Target[])"/>.</summary>
    ITargetDefinition OnFailureOf(Target t1, Target t2, params Target[] more);

    /// <summary>
    /// Conditional skip. The <paramref name="expressionText"/> is captured
    /// at the call site so dry-run output and skip messages explain the
    /// reason. Multiple <c>OnlyWhen</c> calls accumulate (all must hold).
    /// </summary>
    ITargetDefinition OnlyWhen(
        Func<bool> condition,
        [CallerArgumentExpression(nameof(condition))] string expressionText = "");

    /// <summary>
    /// Hard precondition. Distinct from <see cref="OnlyWhen"/>: a failing
    /// <c>Requires</c> aborts the build with the captured expression text,
    /// rather than silently skipping. Multiple calls accumulate.
    /// </summary>
    ITargetDefinition Requires(
        Func<bool> condition,
        [CallerArgumentExpression(nameof(condition))] string expressionText = "");

    /// <summary>
    /// Marks the target as cleanup that should always run, even after
    /// earlier targets in the plan have failed. Distinct from
    /// <see cref="OnFailureOf"/>: OnFailureOf is conditional on a specific
    /// target failing, AssuredAfterFailure is unconditional — the target
    /// runs whether the build succeeded or failed, as long as it appears
    /// in the plan.
    /// </summary>
    ITargetDefinition AssuredAfterFailure();

    // Resources
    ITargetDefinition Consumes(Resource resource, ConsumeMode mode);

    // Capability preflight
    ITargetDefinition RequiresNetwork();
    ITargetDefinition RequiresDocker();
    ITargetDefinition RequiresAdmin();

    /// <summary>
    /// Declare the capability tier this target requires (#20). Use for library-mode
    /// side effects that don't go through a <see cref="CommandPlan"/> (e.g. a REST client
    /// that mutates a remote system) — mark the target
    /// <see cref="CapabilityTier.SideEffectful"/> so it's gated in agent mode. Command-based
    /// side effects are covered automatically by <see cref="CommandPlan.RequiredCapability"/>.
    /// </summary>
    ITargetDefinition Capability(CapabilityTier tier);
    ITargetDefinition RequiresTool(string toolName, string? minVersion = null);

    // Time
    ITargetDefinition Timeout(TimeSpan timeout);
    ITargetDefinition ExpectedDuration(TimeSpan expected);

    // Memory
    ITargetDefinition MemoryBudget(int megabytes);
    ITargetDefinition MemoryHardLimit(int megabytes);

    // Parallelism
    ITargetDefinition MaxParallelism(int copies);
    ITargetDefinition MaxHostParallelism(int copies);

    // Idempotency / caching
    ITargetDefinition Idempotent();
    ITargetDefinition InputHash(Func<string> hashProducer);
    ITargetDefinition Produces(string globPattern);
    ITargetDefinition RunMode(RunMode mode);

    // Failure handling
    ITargetDefinition FailureMode(FailureMode mode);
    ITargetDefinition Retry(int count, Backoff backoff, params int[] retryableExitCodes);

    // The work

    /// <summary>
    /// Run a void-returning block — for non-command work (file I/O, log lines, computing
    /// values). The lambda body executes; its return value (none) is discarded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Footgun warning</b>: this overload is selected when the lambda body returns nothing.
    /// If you call <c>DotNet.Build(...)</c>, <c>Yarn.Install(...)</c>, etc. inside a block-body
    /// lambda without returning their <see cref="CommandPlan"/>s, those plans are constructed
    /// but never executed — the target reports success in zero milliseconds with no output.
    /// </para>
    /// <para>
    /// Wrong (silent no-op):
    /// </para>
    /// <code>
    /// Target Build => _ => _.Executes(() =&gt; { DotNet.Restore(...); DotNet.Build(...); });
    /// </code>
    /// <para>
    /// Right (returns an enumerable of plans):
    /// </para>
    /// <code>
    /// Target Build => _ => _.Executes(() =&gt; new[] { DotNet.Restore(...), DotNet.Build(...) });
    /// </code>
    /// <para>
    /// Or chain multiple <c>.Executes(...)</c> calls instead of putting them in one block.
    /// The <see cref="Tamp.Core"/> analyzer (rule <c>TAMP001</c>) emits a compile-time warning
    /// when this overload's body contains unobserved <see cref="CommandPlan"/> return values.
    /// </para>
    /// </remarks>
    ITargetDefinition Executes(Action action);

    /// <summary>
    /// Run a single <see cref="CommandPlan"/> produced by the factory. The plan's process is
    /// spawned, monitored, and reported in the build summary.
    /// </summary>
    ITargetDefinition Executes(Func<CommandPlan> planFactory);

    /// <summary>
    /// Run an enumerable of <see cref="CommandPlan"/>s in order. Use this overload when a
    /// single target needs to invoke multiple commands sequentially (typical: Restore +
    /// Build + Test). Each plan runs in its own process; failure of one stops the chain.
    /// </summary>
    ITargetDefinition Executes(Func<IEnumerable<CommandPlan>> planFactory);

    /// <summary>
    /// Async sibling of <see cref="Executes(Action)"/> — for target bodies that need
    /// to <c>await</c> something (HTTP calls, async file I/O, REST diagnostics, etc.).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Executes(async () =&gt; { await SomethingAsync(); })</c> matches THIS overload
    /// because the lambda returns <see cref="Task"/>, not <see cref="Action"/>. The
    /// framework awaits the returned Task synchronously at the target boundary, so
    /// the target completes only after the async work is genuinely done — no silent
    /// no-op (TAM-181 / TAMP003).
    /// </para>
    /// <para>
    /// If you write an <c>async</c> lambda without returning a Task (i.e., it matches
    /// <see cref="Executes(Action)"/>), the body becomes <c>async void</c>, control
    /// returns immediately, and the framework cannot wait. TAMP003 fires at compile
    /// time for this pattern.
    /// </para>
    /// </remarks>
    ITargetDefinition Executes(Func<Task> asyncAction);

    /// <summary>Async sibling of <see cref="Executes(Func{CommandPlan})"/>.</summary>
    ITargetDefinition Executes(Func<Task<CommandPlan>> asyncPlanFactory);

    /// <summary>Async sibling of <see cref="Executes(Func{IEnumerable{CommandPlan}})"/>.</summary>
    ITargetDefinition Executes(Func<Task<IEnumerable<CommandPlan>>> asyncPlanFactory);
}

/// <summary>
/// Default <see cref="ITargetDefinition"/> implementation. Mutable while the
/// fluent chain is being built; freezes via <see cref="Build"/> into an
/// immutable <see cref="TargetSpec"/> the executor consumes.
/// </summary>
internal sealed class TargetDefinition : ITargetDefinition
{
    private readonly IReadOnlyDictionary<System.Reflection.MethodInfo, string>? _targetMethodMap;

    public TargetDefinition() { }

    public TargetDefinition(IReadOnlyDictionary<System.Reflection.MethodInfo, string>? targetMethodMap)
    {
        _targetMethodMap = targetMethodMap;
    }

    private readonly List<string> _dependencies = [];
    private readonly List<string> _orderAfter = [];
    private readonly List<string> _orderBefore = [];
    private readonly List<string> _triggers = [];
    private readonly List<string> _triggeredBy = [];
    private readonly List<string> _onFailureOf = [];
    private readonly List<TargetCondition> _onlyWhen = [];
    private readonly List<TargetCondition> _requires = [];
    private readonly List<(Resource Resource, ConsumeMode Mode)> _resources = [];
    private readonly List<(string Tool, string? MinVersion)> _toolRequirements = [];
    private readonly List<string> _tags = [];
    private readonly List<string> _producedGlobs = [];
    private readonly List<Action> _actions = [];
    private readonly List<Func<IEnumerable<CommandPlan>>> _planFactories = [];
    private readonly List<Func<Task>> _asyncActions = [];
    private readonly List<Func<Task<IEnumerable<CommandPlan>>>> _asyncPlanFactories = [];

    private Phase _phase;
    private PhaseDescriptor? _phaseDescriptor;
    private string? _description;
    private bool _topLevel;
    private bool _isDefault;
    private bool _isInternal;
    private bool _requiresNetwork;
    private bool _requiresDocker;
    private bool _requiresAdmin;
    private CapabilityTier _capability = CapabilityTier.Safe;
    private TimeSpan? _timeout;
    private TimeSpan? _expectedDuration;
    private int? _memoryBudgetMb;
    private int? _memoryHardLimitMb;
    private int? _maxParallelism;
    private int? _maxHostParallelism;
    private bool _idempotent;
    private bool _assuredAfterFailure;
    private Func<string>? _inputHashProducer;
    private RunMode _runMode = Tamp.RunMode.Always;
    private FailureMode _failureMode = Tamp.FailureMode.Fatal;
    private int? _retryCount;
    private Backoff? _retryBackoff;
    private int[]? _retryableExitCodes;

    public ITargetDefinition Phase(Phase phase) { _phase = phase; return this; }

    public ITargetDefinition Phase(Phase phase, PhaseDescriptor descriptor)
    {
        _phase = phase;
        _phaseDescriptor = descriptor;
        return this;
    }

    public ITargetDefinition Description(string description)
    {
        _description = description;
        return this;
    }

    public ITargetDefinition Tag(params string[] tags)
    {
        _tags.AddRange(tags);
        return this;
    }

    public ITargetDefinition TopLevel()
    {
        _topLevel = true;
        return this;
    }

    public ITargetDefinition Default()
    {
        _isDefault = true;
        return this;
    }

    public ITargetDefinition Internal()
    {
        _isInternal = true;
        return this;
    }

    public ITargetDefinition DependsOn(params string[] targetNames)
    {
        _dependencies.AddRange(targetNames);
        return this;
    }

    public ITargetDefinition DependsOn(Target target, string? name = null)
    {
        _dependencies.Add(NormalizeCapturedTargetName(name, nameof(DependsOn)));
        return this;
    }

    public ITargetDefinition DependsOn(Target t1, Target t2, params Target[] more)
    {
        _dependencies.Add(ResolveTargetName(t1, nameof(DependsOn)));
        _dependencies.Add(ResolveTargetName(t2, nameof(DependsOn)));
        foreach (var t in more) _dependencies.Add(ResolveTargetName(t, nameof(DependsOn)));
        return this;
    }

    public ITargetDefinition After(params string[] targetNames)
    {
        _orderAfter.AddRange(targetNames);
        return this;
    }

    public ITargetDefinition After(Target target, string? name = null)
    {
        _orderAfter.Add(NormalizeCapturedTargetName(name, nameof(After)));
        return this;
    }

    public ITargetDefinition After(Target t1, Target t2, params Target[] more)
    {
        _orderAfter.Add(ResolveTargetName(t1, nameof(After)));
        _orderAfter.Add(ResolveTargetName(t2, nameof(After)));
        foreach (var t in more) _orderAfter.Add(ResolveTargetName(t, nameof(After)));
        return this;
    }

    public ITargetDefinition Before(params string[] targetNames)
    {
        _orderBefore.AddRange(targetNames);
        return this;
    }

    public ITargetDefinition Before(Target target, string? name = null)
    {
        _orderBefore.Add(NormalizeCapturedTargetName(name, nameof(Before)));
        return this;
    }

    public ITargetDefinition Before(Target t1, Target t2, params Target[] more)
    {
        _orderBefore.Add(ResolveTargetName(t1, nameof(Before)));
        _orderBefore.Add(ResolveTargetName(t2, nameof(Before)));
        foreach (var t in more) _orderBefore.Add(ResolveTargetName(t, nameof(Before)));
        return this;
    }

    public ITargetDefinition Triggers(params string[] targetNames)
    {
        _triggers.AddRange(targetNames);
        return this;
    }

    public ITargetDefinition Triggers(Target target, string? name = null)
    {
        _triggers.Add(NormalizeCapturedTargetName(name, nameof(Triggers)));
        return this;
    }

    public ITargetDefinition Triggers(Target t1, Target t2, params Target[] more)
    {
        _triggers.Add(ResolveTargetName(t1, nameof(Triggers)));
        _triggers.Add(ResolveTargetName(t2, nameof(Triggers)));
        foreach (var t in more) _triggers.Add(ResolveTargetName(t, nameof(Triggers)));
        return this;
    }

    public ITargetDefinition TriggeredBy(params string[] targetNames)
    {
        _triggeredBy.AddRange(targetNames);
        return this;
    }

    public ITargetDefinition TriggeredBy(Target target, string? name = null)
    {
        _triggeredBy.Add(NormalizeCapturedTargetName(name, nameof(TriggeredBy)));
        return this;
    }

    public ITargetDefinition TriggeredBy(Target t1, Target t2, params Target[] more)
    {
        _triggeredBy.Add(ResolveTargetName(t1, nameof(TriggeredBy)));
        _triggeredBy.Add(ResolveTargetName(t2, nameof(TriggeredBy)));
        foreach (var t in more) _triggeredBy.Add(ResolveTargetName(t, nameof(TriggeredBy)));
        return this;
    }

    public ITargetDefinition OnFailureOf(params string[] targetNames)
    {
        _onFailureOf.AddRange(targetNames);
        return this;
    }

    public ITargetDefinition OnFailureOf(Target target, string? name = null)
    {
        _onFailureOf.Add(NormalizeCapturedTargetName(name, nameof(OnFailureOf)));
        return this;
    }

    public ITargetDefinition OnFailureOf(Target t1, Target t2, params Target[] more)
    {
        _onFailureOf.Add(ResolveTargetName(t1, nameof(OnFailureOf)));
        _onFailureOf.Add(ResolveTargetName(t2, nameof(OnFailureOf)));
        foreach (var t in more) _onFailureOf.Add(ResolveTargetName(t, nameof(OnFailureOf)));
        return this;
    }

    /// <summary>
    /// Resolve a Target delegate to its property name via the method-handle map
    /// the framework built during target collection. Single-arg overloads use
    /// <see cref="NormalizeCapturedTargetName"/> with the compiler-captured
    /// expression; multi-arg varargs uses this reflective path because
    /// CallerArgumentExpression cannot capture per-element params source.
    /// </summary>
    private string ResolveTargetName(Target target, string callingMethod)
    {
        if (target is null)
            throw new ArgumentNullException(nameof(target),
                $"{callingMethod}(...) was passed a null Target.");

        if (_targetMethodMap is null)
            throw new InvalidOperationException(
                $"{callingMethod}(Target, Target, params Target[]) called on a TargetDefinition with no target-method map. " +
                $"This is only supported inside a TampBuild target-property body. " +
                $"For direct construction (tests, framework internals), use the string overload.");

        if (!_targetMethodMap.TryGetValue(target.Method, out var name))
            throw new InvalidOperationException(
                $"{callingMethod}(...): Target delegate doesn't map to a Target-typed property on the build class. " +
                $"Pass the dependency by string name, or use the single-arg form which captures the source identifier via [CallerArgumentExpression].");

        return name;
    }

    private static readonly System.Text.RegularExpressions.Regex SimpleIdentifier
        = new(@"^[A-Za-z_][A-Za-z0-9_]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Validates and normalizes a CallerArgumentExpression-captured target reference.
    /// Strips a leading <c>this.</c> prefix; accepts only simple identifiers.
    /// </summary>
    internal static string NormalizeCapturedTargetName(string? expression, string callingMethod)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new ArgumentException(
                $"{callingMethod}() requires a Target expression like '{callingMethod}(Restore)'.",
                "target");
        var trimmed = expression.Trim();
        if (trimmed.StartsWith("this.", StringComparison.Ordinal))
            trimmed = trimmed.Substring(5);
        if (!SimpleIdentifier.IsMatch(trimmed))
            throw new ArgumentException(
                $"{callingMethod}() expects a simple target reference like 'Restore', got '{expression}'. " +
                "Use the (string[]) overload for computed names.",
                "target");
        return trimmed;
    }

    public ITargetDefinition OnlyWhen(
        Func<bool> condition,
        [CallerArgumentExpression(nameof(condition))] string expressionText = "")
    {
        _onlyWhen.Add(new TargetCondition(condition, expressionText));
        return this;
    }

    public ITargetDefinition Requires(
        Func<bool> condition,
        [CallerArgumentExpression(nameof(condition))] string expressionText = "")
    {
        _requires.Add(new TargetCondition(condition, expressionText));
        return this;
    }

    public ITargetDefinition AssuredAfterFailure()
    {
        _assuredAfterFailure = true;
        return this;
    }

    public ITargetDefinition Consumes(Resource resource, ConsumeMode mode)
    {
        _resources.Add((resource, mode));
        return this;
    }

    public ITargetDefinition RequiresNetwork() { _requiresNetwork = true; return this; }
    public ITargetDefinition RequiresDocker() { _requiresDocker = true; return this; }
    public ITargetDefinition RequiresAdmin() { _requiresAdmin = true; return this; }
    public ITargetDefinition Capability(CapabilityTier tier) { _capability = tier; return this; }

    public ITargetDefinition RequiresTool(string toolName, string? minVersion = null)
    {
        _toolRequirements.Add((toolName, minVersion));
        return this;
    }

    public ITargetDefinition Timeout(TimeSpan timeout) { _timeout = timeout; return this; }
    public ITargetDefinition ExpectedDuration(TimeSpan expected) { _expectedDuration = expected; return this; }

    public ITargetDefinition MemoryBudget(int megabytes) { _memoryBudgetMb = megabytes; return this; }
    public ITargetDefinition MemoryHardLimit(int megabytes) { _memoryHardLimitMb = megabytes; return this; }

    public ITargetDefinition MaxParallelism(int copies) { _maxParallelism = copies; return this; }
    public ITargetDefinition MaxHostParallelism(int copies) { _maxHostParallelism = copies; return this; }

    public ITargetDefinition Idempotent() { _idempotent = true; return this; }

    public ITargetDefinition InputHash(Func<string> hashProducer)
    {
        _inputHashProducer = hashProducer;
        return this;
    }

    public ITargetDefinition Produces(string globPattern)
    {
        _producedGlobs.Add(globPattern);
        return this;
    }

    public ITargetDefinition RunMode(RunMode mode) { _runMode = mode; return this; }
    public ITargetDefinition FailureMode(FailureMode mode) { _failureMode = mode; return this; }

    public ITargetDefinition Retry(int count, Backoff backoff, params int[] retryableExitCodes)
    {
        _retryCount = count;
        _retryBackoff = backoff;
        _retryableExitCodes = retryableExitCodes;
        _failureMode = Tamp.FailureMode.Retry;
        return this;
    }

    public ITargetDefinition Executes(Action action)
    {
        _actions.Add(action);
        return this;
    }

    public ITargetDefinition Executes(Func<CommandPlan> planFactory)
    {
        _planFactories.Add(() => [planFactory()]);
        return this;
    }

    public ITargetDefinition Executes(Func<IEnumerable<CommandPlan>> planFactory)
    {
        _planFactories.Add(planFactory);
        return this;
    }

    public ITargetDefinition Executes(Func<Task> asyncAction)
    {
        _asyncActions.Add(asyncAction);
        return this;
    }

    public ITargetDefinition Executes(Func<Task<CommandPlan>> asyncPlanFactory)
    {
        _asyncPlanFactories.Add(async () => new[] { await asyncPlanFactory().ConfigureAwait(false) });
        return this;
    }

    public ITargetDefinition Executes(Func<Task<IEnumerable<CommandPlan>>> asyncPlanFactory)
    {
        _asyncPlanFactories.Add(asyncPlanFactory);
        return this;
    }

    /// <summary>Freeze the definition into an immutable <see cref="TargetSpec"/>.</summary>
    internal TargetSpec Build(string name) => new()
    {
        Name = name,
        Phase = _phase,
        PhaseDescriptor = _phaseDescriptor,
        Description = _description,
        TopLevel = _topLevel,
        IsDefault = _isDefault,
        IsInternal = _isInternal,
        Tags = _tags.ToArray(),
        Dependencies = _dependencies.ToArray(),
        OrderAfter = _orderAfter.ToArray(),
        OrderBefore = _orderBefore.ToArray(),
        Triggers = _triggers.ToArray(),
        TriggeredBy = _triggeredBy.ToArray(),
        OnFailureOf = _onFailureOf.ToArray(),
        OnlyWhenConditions = _onlyWhen.ToArray(),
        Requirements = _requires.ToArray(),
        AssuredAfterFailure = _assuredAfterFailure,
        Resources = _resources.ToArray(),
        RequiresNetwork = _requiresNetwork,
        RequiresDocker = _requiresDocker,
        RequiresAdmin = _requiresAdmin,
        Capability = _capability,
        ToolRequirements = _toolRequirements.ToArray(),
        Timeout = _timeout,
        ExpectedDuration = _expectedDuration,
        MemoryBudgetMb = _memoryBudgetMb,
        MemoryHardLimitMb = _memoryHardLimitMb,
        MaxParallelism = _maxParallelism,
        MaxHostParallelism = _maxHostParallelism,
        Idempotent = _idempotent,
        InputHashProducer = _inputHashProducer,
        ProducedGlobs = _producedGlobs.ToArray(),
        RunMode = _runMode,
        FailureMode = _failureMode,
        RetryCount = _retryCount,
        RetryBackoff = _retryBackoff,
        RetryableExitCodes = _retryableExitCodes,
        Actions = _actions.ToArray(),
        PlanFactories = _planFactories.ToArray(),
        AsyncActions = _asyncActions.ToArray(),
        AsyncPlanFactories = _asyncPlanFactories.ToArray(),
    };
}

/// <summary>
/// Frozen, immutable description of a target after the fluent chain runs.
/// This is what the executor consumes; it does not see the mutable
/// <see cref="TargetDefinition"/> at any point.
/// </summary>
public sealed record TargetSpec
{
    public required string Name { get; init; }
    public Phase Phase { get; init; }
    public PhaseDescriptor? PhaseDescriptor { get; init; }
    public string? Description { get; init; }
    public bool TopLevel { get; init; }
    public bool IsDefault { get; init; }
    public bool IsInternal { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> OrderAfter { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> OrderBefore { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Triggers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> TriggeredBy { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> OnFailureOf { get; init; } = Array.Empty<string>();
    public IReadOnlyList<TargetCondition> OnlyWhenConditions { get; init; } = Array.Empty<TargetCondition>();
    public IReadOnlyList<TargetCondition> Requirements { get; init; } = Array.Empty<TargetCondition>();
    public bool AssuredAfterFailure { get; init; }
    public IReadOnlyList<(Resource Resource, ConsumeMode Mode)> Resources { get; init; }
        = Array.Empty<(Resource, ConsumeMode)>();
    public bool RequiresNetwork { get; init; }
    public bool RequiresDocker { get; init; }
    public bool RequiresAdmin { get; init; }

    /// <summary>The capability tier this target declares (#20). Default <see cref="CapabilityTier.Safe"/>.</summary>
    public CapabilityTier Capability { get; init; } = CapabilityTier.Safe;
    public IReadOnlyList<(string Tool, string? MinVersion)> ToolRequirements { get; init; }
        = Array.Empty<(string, string?)>();
    public TimeSpan? Timeout { get; init; }
    public TimeSpan? ExpectedDuration { get; init; }
    public int? MemoryBudgetMb { get; init; }
    public int? MemoryHardLimitMb { get; init; }
    public int? MaxParallelism { get; init; }
    public int? MaxHostParallelism { get; init; }
    public bool Idempotent { get; init; }
    public Func<string>? InputHashProducer { get; init; }
    public IReadOnlyList<string> ProducedGlobs { get; init; } = Array.Empty<string>();
    public RunMode RunMode { get; init; } = RunMode.Always;
    public FailureMode FailureMode { get; init; } = FailureMode.Fatal;
    public int? RetryCount { get; init; }
    public Backoff? RetryBackoff { get; init; }
    public IReadOnlyList<int>? RetryableExitCodes { get; init; }
    public IReadOnlyList<Action> Actions { get; init; } = Array.Empty<Action>();
    public IReadOnlyList<Func<IEnumerable<CommandPlan>>> PlanFactories { get; init; }
        = Array.Empty<Func<IEnumerable<CommandPlan>>>();

    /// <summary>Async actions registered via <see cref="ITargetDefinition.Executes(Func{Task})"/>.
    /// Awaited synchronously at the target boundary in the executor (TAM-181 — eliminates the
    /// async-void silent-no-op symptom).</summary>
    public IReadOnlyList<Func<Task>> AsyncActions { get; init; } = Array.Empty<Func<Task>>();

    /// <summary>Async plan factories registered via the
    /// <see cref="ITargetDefinition.Executes(Func{Task{CommandPlan}})"/> /
    /// <see cref="ITargetDefinition.Executes(Func{Task{System.Collections.Generic.IEnumerable{CommandPlan}}})"/>
    /// overloads. Awaited synchronously then dispatched like the sync PlanFactories.</summary>
    public IReadOnlyList<Func<Task<IEnumerable<CommandPlan>>>> AsyncPlanFactories { get; init; }
        = Array.Empty<Func<Task<IEnumerable<CommandPlan>>>>();
}
