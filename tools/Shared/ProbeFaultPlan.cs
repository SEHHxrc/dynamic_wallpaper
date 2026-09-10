using System.Text.Json;

namespace LiveWall.Diagnostics;

public enum ProbeFaultPhase
{
    SurfaceAttached,
    ContentLoaded,
    FirstFramePresented,
    ShutdownCompleted,
}

public enum ProbeFaultAction
{
    FatalEvent,
    ExitProcess,
    SuppressEvent,
}

public sealed record ProbeFaultPlan(
    int TargetIteration,
    int TargetSurfaceOrdinal,
    ProbeFaultPhase Phase,
    ProbeFaultAction Action)
{
    public const string EnvironmentVariableName = "LIVEWALL_RENDERER_PROBE_FAULT";

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TargetIteration);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TargetSurfaceOrdinal);
        if (!Enum.IsDefined(Phase))
        {
            throw new ArgumentOutOfRangeException(nameof(Phase));
        }
        if (!Enum.IsDefined(Action))
        {
            throw new ArgumentOutOfRangeException(nameof(Action));
        }
    }

    public string Serialize()
    {
        Validate();
        return JsonSerializer.Serialize(this, SerializerOptions);
    }

    public static ProbeFaultPlan? ReadAndClearFromEnvironment()
    {
        string? serialized = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        Environment.SetEnvironmentVariable(EnvironmentVariableName, null);
        if (string.IsNullOrWhiteSpace(serialized))
        {
            return null;
        }

        ProbeFaultPlan plan = JsonSerializer.Deserialize<ProbeFaultPlan>(
                serialized,
                SerializerOptions) ??
            throw new InvalidOperationException("Renderer probe fault plan was empty.");
        plan.Validate();
        return plan;
    }
}
