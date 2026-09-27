namespace Tamp;

/// <summary>
/// Marks a build-class <see cref="Target"/> property as a <b>deliberate override</b> of a same-named
/// component (interface) target (ADR 0020). Class targets always win over component targets by
/// class-precedence; this attribute records that the override is intended, so the <c>TAMP007</c>
/// analyzer stays silent. Without it, TAMP007 warns that a name collision with a component target
/// might be an accidental shadow — so an override is never silent.
/// </summary>
/// <example>
/// <code>
/// class Build : TampBuild, ICompile
/// {
///     [OverridesComponent]
///     public Target Compile => _ => _.DependsOn(nameof(IRestore.Restore)).Executes(() => { /* custom */ });
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class OverridesComponentAttribute : Attribute
{
}
