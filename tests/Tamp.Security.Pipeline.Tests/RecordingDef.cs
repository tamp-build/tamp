using Tamp;

namespace Tamp.Security.Pipeline.Tests;

/// <summary>
/// Recording <see cref="ITargetDefinition"/>: captures Description / DependsOn / AssuredAfterFailure
/// and runs whatever the target registered with <c>Executes</c>.
/// </summary>
/// <remarks>
/// Shared between the build-wiring tests and the #151 gate tests, which both need to materialise a
/// <see cref="Target"/> without an executor.
/// </remarks>
internal sealed class RecordingDef : ITargetDefinition
{
    public string? RecordedDescription { get; private set; }
    public List<string> RecordedDependencies { get; } = new();

    private Action? _action;
    private Func<CommandPlan>? _plan;
    private Func<IEnumerable<CommandPlan>>? _plans;
    private Func<Task>? _asyncAction;
    private Func<Task<CommandPlan>>? _asyncPlan;
    private Func<Task<IEnumerable<CommandPlan>>>? _asyncPlans;

    public bool HasExecutes => _action is not null || _plan is not null || _plans is not null
        || _asyncAction is not null || _asyncPlan is not null || _asyncPlans is not null;

    /// <summary>Run whatever the target registered; returns produced plans (may be empty).</summary>
    public IReadOnlyList<CommandPlan> RunExecutes()
    {
        var produced = new List<CommandPlan>();
        _action?.Invoke();
        if (_plan is not null) produced.Add(_plan());
        if (_plans is not null) produced.AddRange(_plans());
        if (_asyncAction is not null) _asyncAction().GetAwaiter().GetResult();
        if (_asyncPlan is not null) produced.Add(_asyncPlan().GetAwaiter().GetResult());
        if (_asyncPlans is not null) produced.AddRange(_asyncPlans().GetAwaiter().GetResult());
        return produced;
    }

    public ITargetDefinition Description(string description) { RecordedDescription = description; return this; }
    public ITargetDefinition DependsOn(params string[] targetNames) { RecordedDependencies.AddRange(targetNames); return this; }
    public ITargetDefinition DependsOn(Target target, string? name = null) { if (name is not null) RecordedDependencies.Add(name); return this; }
    public ITargetDefinition DependsOn(Target t1, Target t2, params Target[] more) => this;

    public ITargetDefinition Executes(Action action) { _action = action; return this; }
    public ITargetDefinition Executes(Func<CommandPlan> planFactory) { _plan = planFactory; return this; }
    public ITargetDefinition Executes(Func<IEnumerable<CommandPlan>> planFactory) { _plans = planFactory; return this; }
    public ITargetDefinition Executes(Func<Task> asyncAction) { _asyncAction = asyncAction; return this; }
    public ITargetDefinition Executes(Func<Task<CommandPlan>> asyncPlanFactory) { _asyncPlan = asyncPlanFactory; return this; }
    public ITargetDefinition Executes(Func<Task<IEnumerable<CommandPlan>>> asyncPlanFactory) { _asyncPlans = asyncPlanFactory; return this; }

    // Everything else is a no-op recorder returning this.
    public ITargetDefinition Phase(Phase phase) => this;
    public ITargetDefinition Phase(Phase phase, PhaseDescriptor descriptor) => this;
    public ITargetDefinition Tag(params string[] tags) => this;
    public ITargetDefinition TopLevel() => this;
    public ITargetDefinition Internal() => this;
    public ITargetDefinition Default() => this;
    public ITargetDefinition After(params string[] targetNames) => this;
    public ITargetDefinition After(Target target, string? name = null) => this;
    public ITargetDefinition After(Target t1, Target t2, params Target[] more) => this;
    public ITargetDefinition Before(params string[] targetNames) => this;
    public ITargetDefinition Before(Target target, string? name = null) => this;
    public ITargetDefinition Before(Target t1, Target t2, params Target[] more) => this;
    public ITargetDefinition Triggers(params string[] targetNames) => this;
    public ITargetDefinition Triggers(Target target, string? name = null) => this;
    public ITargetDefinition Triggers(Target t1, Target t2, params Target[] more) => this;
    public ITargetDefinition TriggeredBy(params string[] targetNames) => this;
    public ITargetDefinition TriggeredBy(Target target, string? name = null) => this;
    public ITargetDefinition TriggeredBy(Target t1, Target t2, params Target[] more) => this;
    public ITargetDefinition OnFailureOf(params string[] targetNames) => this;
    public ITargetDefinition OnFailureOf(Target target, string? name = null) => this;
    public ITargetDefinition OnFailureOf(Target t1, Target t2, params Target[] more) => this;
    public ITargetDefinition OnlyWhen(Func<bool> condition, string expressionText = "") => this;
    public ITargetDefinition Requires(Func<bool> condition, string expressionText = "") => this;
    public bool RecordedAssuredAfterFailure { get; private set; }
    public ITargetDefinition AssuredAfterFailure() { RecordedAssuredAfterFailure = true; return this; }
    public ITargetDefinition Consumes(Resource resource, ConsumeMode mode) => this;
    public ITargetDefinition RequiresNetwork() => this;
    public ITargetDefinition RequiresDocker() => this;
    public ITargetDefinition RequiresAdmin() => this;
    public ITargetDefinition Capability(CapabilityTier tier) => this;
    public ITargetDefinition RequiresTool(string toolName, string? minVersion = null) => this;
    public ITargetDefinition Timeout(TimeSpan timeout) => this;
    public ITargetDefinition ExpectedDuration(TimeSpan expected) => this;
    public ITargetDefinition MemoryBudget(int megabytes) => this;
    public ITargetDefinition MemoryHardLimit(int megabytes) => this;
    public ITargetDefinition MaxParallelism(int copies) => this;
    public ITargetDefinition MaxHostParallelism(int copies) => this;
    public ITargetDefinition Idempotent() => this;
    public ITargetDefinition InputHash(Func<string> hashProducer) => this;
    public ITargetDefinition Produces(string globPattern) => this;
    public ITargetDefinition RunMode(RunMode mode) => this;
    public ITargetDefinition FailureMode(FailureMode mode) => this;
    public ITargetDefinition Retry(int count, Backoff backoff, params int[] retryableExitCodes) => this;
}
