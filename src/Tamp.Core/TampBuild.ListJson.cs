using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamp;

/// <summary>
/// Structured JSON output for <c>--list --format=json</c> (TAM-139).
/// Required by VS Code / Fleet extensions to introspect a build's target
/// catalog + parameter inventory without parsing human-readable text.
/// </summary>
public abstract partial class TampBuild
{
    /// <summary>Output format for <c>--list</c>.</summary>
    internal enum OutputFormat { Text, Json }

    private static readonly JsonSerializerOptions s_catalogJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static void PrintTargetCatalogJson(
        IReadOnlyDictionary<string, TargetSpec> targets,
        TampBuild build,
        bool showAll)
    {
        var visibleTargets = (showAll ? targets.Values : targets.Values.Where(t => !t.IsInternal))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(BuildTargetDto)
            .ToList();

        var defaults = targets.Values.Where(t => t.IsDefault).Select(t => t.Name).ToList();
        if (defaults.Count == 0 && targets.ContainsKey("Default")) defaults.Add("Default");
        else if (defaults.Count == 0 && targets.ContainsKey("Ci")) defaults.Add("Ci");

        var catalog = new TargetCatalogDto
        {
            TampVersion = typeof(TampBuild).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            BuildAssembly = System.IO.Path.GetRelativePath(
                Environment.CurrentDirectory,
                build.GetType().Assembly.Location ?? "<unknown>"),
            Defaults = defaults,
            Targets = visibleTargets,
            Parameters = CollectParameters(build),
        };

        Console.WriteLine(JsonSerializer.Serialize(catalog, s_catalogJsonOptions));
    }

    /// <summary>JSON rendering of a plan (<c>--plan --format json</c>): the resolved execution order for the requested roots. Runs nothing. Powers the MCP <c>plan</c> tool (#22).</summary>
    private static void PrintPlanJson(IReadOnlyList<string> roots, IReadOnlyList<TargetSpec> order)
    {
        var dto = new PlanDto
        {
            Roots = roots,
            Order = order.Select(t => new PlanStepDto
            {
                Name = t.Name,
                Phase = t.Phase == Phase.None ? null : t.Phase.ToString(),
                DependsOn = t.Dependencies,
                Capability = t.Capability.ToString(),
            }).ToList(),
        };
        Console.WriteLine(JsonSerializer.Serialize(dto, s_catalogJsonOptions));
    }

    private static TargetDto BuildTargetDto(TargetSpec t) => new()
    {
        Name = t.Name,
        Description = t.Description,
        Phase = t.Phase == Phase.None ? null : t.Phase.ToString(),
        Tags = t.Tags,
        TopLevel = !t.IsInternal,           // Internal opts OUT of TopLevel (1.1.0+)
        IsDefault = t.IsDefault,
        DependsOn = t.Dependencies,
        Produces = t.ProducedGlobs,
        Capability = t.Capability.ToString(),
        OrderAfter = t.OrderAfter,
        OrderBefore = t.OrderBefore,
        Triggers = t.Triggers,
        TriggeredBy = t.TriggeredBy,
        OnFailureOf = t.OnFailureOf,
        RequiresNetwork = t.RequiresNetwork,
        RequiresDocker = t.RequiresDocker,
        RequiresAdmin = t.RequiresAdmin,
        ToolRequirements = t.ToolRequirements.Select(tr => new ToolRequirementDto
        {
            Tool = tr.Tool,
            MinVersion = tr.MinVersion,
        }).ToList(),
        FailureMode = t.FailureMode.ToString(),
        Idempotent = t.Idempotent,
        TimeoutMs = t.Timeout?.TotalMilliseconds is { } ms ? (long?)ms : null,
    };

    /// <summary>
    /// Reflect over <paramref name="build"/> to surface every
    /// <see cref="ParameterAttribute"/>-decorated field/property. Default
    /// values are captured by reading the current bound value (parameter
    /// binding has already run by the time --list-with-catalog fires).
    /// </summary>
    private static List<ParameterDto> CollectParameters(TampBuild build)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var result = new List<ParameterDto>();

        foreach (var member in build.GetType()
                     .GetMembers(flags)
                     .Where(m => m is FieldInfo or PropertyInfo))
        {
            var attr = member.GetCustomAttribute<ParameterAttribute>();
            if (attr is null) continue;

            var memberType = member switch
            {
                FieldInfo f => f.FieldType,
                PropertyInfo p => p.PropertyType,
                _ => typeof(object),
            };

            object? currentValue;
            try
            {
                currentValue = member switch
                {
                    FieldInfo f => f.GetValue(build),
                    PropertyInfo p when p.CanRead => p.GetValue(build),
                    _ => null,
                };
            }
            catch
            {
                currentValue = null;
            }

            var envVar = attr.EnvironmentVariable ?? ToUpperSnakeCase(member.Name);

            result.Add(new ParameterDto
            {
                Name = member.Name,
                Type = memberType.Name,
                Description = attr.Description,
                Default = currentValue?.ToString(),
                EnvVar = envVar,
                Required = false,    // Tamp's binder doesn't expose a required flag today
            });
        }

        return result.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
    }

    private static string ToUpperSnakeCase(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]))
                sb.Append('_');
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }

    // ─── JSON DTOs ──────────────────────────────────────────────────────

    internal sealed class TargetCatalogDto
    {
        [JsonPropertyName("tamp_version")] public string TampVersion { get; init; } = "";
        [JsonPropertyName("build_assembly")] public string BuildAssembly { get; init; } = "";
        [JsonPropertyName("defaults")] public IReadOnlyList<string> Defaults { get; init; } = Array.Empty<string>();
        [JsonPropertyName("targets")] public IReadOnlyList<TargetDto> Targets { get; init; } = Array.Empty<TargetDto>();
        [JsonPropertyName("parameters")] public IReadOnlyList<ParameterDto> Parameters { get; init; } = Array.Empty<ParameterDto>();
    }

    internal sealed class TargetDto
    {
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("phase")] public string? Phase { get; init; }
        [JsonPropertyName("tags")] public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
        [JsonPropertyName("top_level")] public bool TopLevel { get; init; }
        [JsonPropertyName("is_default")] public bool IsDefault { get; init; }
        [JsonPropertyName("depends_on")] public IReadOnlyList<string> DependsOn { get; init; } = Array.Empty<string>();
        [JsonPropertyName("produces")] public IReadOnlyList<string> Produces { get; init; } = Array.Empty<string>();
        [JsonPropertyName("capability")] public string Capability { get; init; } = "Safe";
        [JsonPropertyName("order_after")] public IReadOnlyList<string> OrderAfter { get; init; } = Array.Empty<string>();
        [JsonPropertyName("order_before")] public IReadOnlyList<string> OrderBefore { get; init; } = Array.Empty<string>();
        [JsonPropertyName("triggers")] public IReadOnlyList<string> Triggers { get; init; } = Array.Empty<string>();
        [JsonPropertyName("triggered_by")] public IReadOnlyList<string> TriggeredBy { get; init; } = Array.Empty<string>();
        [JsonPropertyName("on_failure_of")] public IReadOnlyList<string> OnFailureOf { get; init; } = Array.Empty<string>();
        [JsonPropertyName("requires_network")] public bool RequiresNetwork { get; init; }
        [JsonPropertyName("requires_docker")] public bool RequiresDocker { get; init; }
        [JsonPropertyName("requires_admin")] public bool RequiresAdmin { get; init; }
        [JsonPropertyName("tool_requirements")] public IReadOnlyList<ToolRequirementDto> ToolRequirements { get; init; } = Array.Empty<ToolRequirementDto>();
        [JsonPropertyName("failure_mode")] public string FailureMode { get; init; } = "Fatal";
        [JsonPropertyName("idempotent")] public bool Idempotent { get; init; }
        [JsonPropertyName("timeout_ms")] public long? TimeoutMs { get; init; }
    }

    internal sealed class PlanDto
    {
        [JsonPropertyName("roots")] public IReadOnlyList<string> Roots { get; init; } = Array.Empty<string>();
        [JsonPropertyName("order")] public IReadOnlyList<PlanStepDto> Order { get; init; } = Array.Empty<PlanStepDto>();
    }

    internal sealed class PlanStepDto
    {
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("phase")] public string? Phase { get; init; }
        [JsonPropertyName("depends_on")] public IReadOnlyList<string> DependsOn { get; init; } = Array.Empty<string>();
        [JsonPropertyName("capability")] public string Capability { get; init; } = "Safe";
    }

    internal sealed class ToolRequirementDto
    {
        [JsonPropertyName("tool")] public string Tool { get; init; } = "";
        [JsonPropertyName("min_version")] public string? MinVersion { get; init; }
    }

    internal sealed class ParameterDto
    {
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("type")] public string Type { get; init; } = "";
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("default")] public string? Default { get; init; }
        [JsonPropertyName("env_var")] public string EnvVar { get; init; } = "";
        [JsonPropertyName("required")] public bool Required { get; init; }
    }
}
