namespace Tamp;

/// <summary>
/// Read-only view of a build's target dependency graph.
/// </summary>
/// <remarks>
/// Carries four edge kinds, mirroring NUKE's model and extending it with a
/// Tamp-specific failure-handler relationship that lives at runtime only:
/// <list type="bullet">
///   <item><b>DependsOn</b> (execution dep) — transitively pulls the named
///         target into the plan; orders this target after it.</item>
///   <item><b>After</b> / <b>Before</b> (order dep) — orders only when both
///         targets are already in the plan; does NOT pull either in.</item>
///   <item><b>Triggers</b> / <b>TriggeredBy</b> (trigger dep) — outgoing or
///         incoming fan-out; if I run, the named targets also run.
///         <c>TriggeredBy(X)</c> on T is internally equivalent to
///         <c>X.Triggers(T)</c>.</item>
///   <item><b>OnFailureOf</b> — runtime-only catch handler. Not part of the
///         normal plan and intentionally NOT in cycle detection.</item>
/// </list>
/// </remarks>
public sealed class TargetGraph
{
    private readonly IReadOnlyDictionary<string, TargetSpec> _targets;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _effectiveTriggers;

    public TargetGraph(IReadOnlyDictionary<string, TargetSpec> targets)
    {
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        ValidateAllEdgeReferences();
        _effectiveTriggers = ComputeEffectiveTriggers();
        DetectCycles();
    }

    public IReadOnlyDictionary<string, TargetSpec> Targets => _targets;

    public bool Contains(string targetName) => _targets.ContainsKey(targetName);

    /// <summary>
    /// Compute the execution plan for one or more invoked targets. Expands
    /// the set transitively via <c>DependsOn</c>, then iteratively via
    /// <c>Triggers</c> (and reverse-resolved <c>TriggeredBy</c>) until
    /// stable. Returns the resulting targets in topological order, where
    /// the order respects DependsOn, After, Before, and TriggeredBy edges
    /// for any pair of targets both present in the plan.
    /// </summary>
    public IReadOnlyList<TargetSpec> ComputeExecutionOrder(params string[] rootNames)
        => ComputeExecutionOrder((IEnumerable<string>)rootNames);

    /// <inheritdoc cref="ComputeExecutionOrder(string[])"/>
    public IReadOnlyList<TargetSpec> ComputeExecutionOrder(IEnumerable<string> rootNames)
    {
        if (rootNames is null) throw new ArgumentNullException(nameof(rootNames));
        var roots = rootNames.ToList();
        if (roots.Count == 0)
            throw new InvalidOperationException("ComputeExecutionOrder requires at least one root target name.");
        foreach (var r in roots)
            if (!_targets.ContainsKey(r))
                throw new InvalidOperationException(
                    $"Target '{r}' not found. Known: {string.Join(", ", _targets.Keys.OrderBy(n => n))}");

        var invoked = new HashSet<string>(roots, StringComparer.Ordinal);
        HashSet<string> planSet;

        // Trigger-stable loop: expand DependsOn closure, then look at Triggers
        // outgoing from any plan member. New triggered targets become invoked
        // and the loop runs again until the plan stops growing.
        while (true)
        {
            planSet = ComputeDependsOnClosure(invoked);
            var newlyTriggered = new List<string>();
            foreach (var name in planSet)
            {
                if (!_effectiveTriggers.TryGetValue(name, out var triggered)) continue;
                foreach (var t in triggered)
                    if (!planSet.Contains(t) && invoked.Add(t))
                        newlyTriggered.Add(t);
            }
            if (newlyTriggered.Count == 0) break;
        }

        return TopologicalSort(planSet);
    }

    /// <summary>
    /// The downstream slice for <paramref name="name"/> (#15): <paramref name="name"/> plus
    /// every target that transitively <c>DependsOn</c> it. Pure graph traversal (reverse
    /// dependency edges) — no hashing. Powers <c>--from</c> / <c>--downstream</c>.
    /// </summary>
    public IReadOnlyList<string> DependentsClosure(string name)
    {
        if (!_targets.ContainsKey(name))
            throw new InvalidOperationException(
                $"Target '{name}' not found. Known: {string.Join(", ", _targets.Keys.OrderBy(n => n))}");

        var result = new HashSet<string>(StringComparer.Ordinal) { name };
        var queue = new Queue<string>();
        queue.Enqueue(name);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var (other, spec) in _targets)
                if (spec.Dependencies.Contains(current, StringComparer.Ordinal) && result.Add(other))
                    queue.Enqueue(other);
        }
        return result.ToList();
    }

    /// <summary>
    /// Failure handlers registered for <paramref name="failedTargetName"/>.
    /// These are NOT part of the regular plan; the executor invokes them
    /// (and their own dep trees) only when the named target actually fails.
    /// </summary>
    public IReadOnlyList<TargetSpec> HandlersFor(string failedTargetName)
        => _targets.Values
            .Where(t => t.OnFailureOf.Contains(failedTargetName, StringComparer.Ordinal))
            .ToList();

    /// <summary>
    /// Backward-compatibility shim for the v0 single-root API.
    /// </summary>
    public IReadOnlyList<TargetSpec> TopologicalOrderFor(string rootTargetName)
        => ComputeExecutionOrder(rootTargetName);

    // ---------- internals ----------

    private void ValidateAllEdgeReferences()
    {
        foreach (var (name, spec) in _targets)
        {
            void Check(string edge, IEnumerable<string> refs)
            {
                foreach (var r in refs)
                    if (!_targets.ContainsKey(r))
                        throw new InvalidOperationException(
                            $"Target '{name}' declares {edge} on '{r}' which is not defined.");
            }
            Check("DependsOn", spec.Dependencies);
            Check("After", spec.OrderAfter);
            Check("Before", spec.OrderBefore);
            Check("Triggers", spec.Triggers);
            Check("TriggeredBy", spec.TriggeredBy);
            Check("OnFailureOf", spec.OnFailureOf);
        }
    }

    private Dictionary<string, IReadOnlyList<string>> ComputeEffectiveTriggers()
    {
        // For each source target, accumulate its outgoing triggers from
        // .Triggers plus the reverse of every other target's .TriggeredBy
        // that names this one.
        var lookup = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (name, spec) in _targets)
        {
            if (spec.Triggers.Count == 0) continue;
            if (!lookup.TryGetValue(name, out var list))
                lookup[name] = list = new List<string>();
            list.AddRange(spec.Triggers);
        }
        foreach (var (name, spec) in _targets)
        {
            foreach (var trigger in spec.TriggeredBy)
            {
                if (!lookup.TryGetValue(trigger, out var list))
                    lookup[trigger] = list = new List<string>();
                if (!list.Contains(name, StringComparer.Ordinal)) list.Add(name);
            }
        }
        return lookup.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal);
    }

    private void DetectCycles()
    {
        // Cycle detection over DependsOn ∪ After ∪ TriggeredBy plus the
        // reversal of Before (B.Before(A) means A comes after B → same as
        // A.After(B) for ordering purposes). OnFailureOf intentionally
        // excluded — handlers are runtime-only.
        var permanent = new HashSet<string>(StringComparer.Ordinal);
        var temporary = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in _targets.Keys)
            if (!permanent.Contains(name))
                Visit(name, permanent, temporary, []);
        return;

        void Visit(string name, HashSet<string> p, HashSet<string> t, List<string> path)
        {
            if (p.Contains(name)) return;
            if (t.Contains(name))
                throw new InvalidOperationException(
                    $"Cycle detected in target graph: {string.Join(" -> ", path.Concat([name]))}");
            t.Add(name);
            path.Add(name);
            foreach (var prev in OrderingPredecessors(name))
                Visit(prev, p, t, path);
            path.RemoveAt(path.Count - 1);
            t.Remove(name);
            p.Add(name);
        }
    }

    /// <summary>
    /// All targets that must come before <paramref name="name"/> in any
    /// valid ordering — used for both cycle detection and topological sort.
    /// </summary>
    private IEnumerable<string> OrderingPredecessors(string name)
    {
        var spec = _targets[name];
        foreach (var d in spec.Dependencies) yield return d;
        foreach (var a in spec.OrderAfter) yield return a;
        foreach (var t in spec.TriggeredBy) yield return t;
        // Before(X) on this target means X must come AFTER this target,
        // i.e., this target is a predecessor of X. Reverse-wire from the
        // perspective of every other target's OrderBefore lists.
        foreach (var (other, otherSpec) in _targets)
            if (otherSpec.OrderBefore.Contains(name, StringComparer.Ordinal))
                yield return other;
    }

    private HashSet<string> ComputeDependsOnClosure(IEnumerable<string> roots)
    {
        var closure = new HashSet<string>(roots, StringComparer.Ordinal);
        var queue = new Queue<string>(closure);
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            foreach (var dep in _targets[name].Dependencies)
                if (closure.Add(dep)) queue.Enqueue(dep);
        }
        return closure;
    }

    private IReadOnlyList<TargetSpec> TopologicalSort(HashSet<string> planSet)
    {
        var order = new List<TargetSpec>(planSet.Count);
        var permanent = new HashSet<string>(StringComparer.Ordinal);
        var temporary = new HashSet<string>(StringComparer.Ordinal);
        // Sort the entry points for deterministic output across runs.
        foreach (var name in planSet.OrderBy(n => n, StringComparer.Ordinal))
            Visit(name);
        return order;

        void Visit(string name)
        {
            if (permanent.Contains(name)) return;
            if (temporary.Contains(name))
                throw new InvalidOperationException(
                    $"Cycle detected during plan ordering at '{name}'.");
            temporary.Add(name);
            foreach (var pred in OrderingPredecessors(name))
                if (planSet.Contains(pred))  // order constraints only apply when both in plan
                    Visit(pred);
            temporary.Remove(name);
            permanent.Add(name);
            order.Add(_targets[name]);
        }
    }
}
