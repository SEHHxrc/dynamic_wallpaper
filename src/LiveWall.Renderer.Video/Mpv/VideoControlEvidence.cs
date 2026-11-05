using System.Globalization;
using System.Text.Json;

namespace LiveWall.Renderer.Video.Mpv;

internal sealed record VideoControlSnapshot(bool Paused, double? Position, string? Aspect,
    string? Unscaled, double? PanScan, double? Volume, bool Muted)
{
    public static VideoControlSnapshot Read(Func<string, string?> property)
    {
        return new(property("pause") == "yes", Number("time-pos"), property("keepaspect"),
            property("video-unscaled"), Number("panscan"), Number("volume"), property("mute") == "yes");
        double? Number(string name) => Parse(property(name));
    }
        // Native strings are invariant irrespective of the UI locale.
    private static double? Parse(string? value) => double.TryParse(value, NumberStyles.Float,
        CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) ? number : null;
}

// Opt-in, bounded, process-local developer evidence. Never a protocol/telemetry capability.
internal sealed class VideoControlEvidence : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const string EnvironmentVariable = "LIVEWALL_VIDEO_CONTROL_EVIDENCE";
    private readonly StreamWriter writer;
    private readonly System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
    private int count;
    private VideoControlEvidence(string path) => writer = new(new FileStream(path,
        FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    public static VideoControlEvidence? FromEnvironment()
    {
        string? path = Environment.GetEnvironmentVariable(EnvironmentVariable);
        Environment.SetEnvironmentVariable(EnvironmentVariable, null);
        if (path is null) return null;
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Use a fresh absolute local evidence file.");
        for (string? parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Evidence directories cannot traverse reparse points.");
        return new(path);
    }
    public void Record(string phase, VideoControlSnapshot? snapshot, int width = 0, int height = 0)
    {
        if (count >= 512) return;
        writer.WriteLine(JsonSerializer.Serialize(new { ordinal = ++count, phase,
            elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds, snapshot, width, height },
            Json));
    }
    public void Dispose() => writer.Dispose();
}
