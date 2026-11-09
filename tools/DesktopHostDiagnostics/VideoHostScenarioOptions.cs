namespace LiveWall.DesktopHostDiagnostics;

internal enum VideoHostScenario
{
    Restore,
    CrashRecovery,
}

internal sealed record VideoHostScenarioOptions(VideoHostScenario Scenario, string RendererExecutable, string LibraryPath,
    string SourceVideo, string RunDirectory, int DurationSeconds, int Crashes, bool PreflightOnly)
{
    public const string RestoreMode = "--video-host-restore";
    public const string CrashRecoveryMode = "--video-host-crash-recovery";

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Contains(RestoreMode, StringComparer.Ordinal) || args.Contains(CrashRecoveryMode, StringComparer.Ordinal);

    public static VideoHostScenarioOptions Parse(IReadOnlyList<string> args)
    {
        VideoHostScenario? scenario = null;
        bool confirmed = false, preflight = false;
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        for (int index = 0; index < args.Count; index++)
        {
            string option = args[index];
            if (option is RestoreMode or CrashRecoveryMode && scenario is null)
            {
                scenario = option == RestoreMode ? VideoHostScenario.Restore : VideoHostScenario.CrashRecovery;
                continue;
            }
            if (option == "--confirm-desktop-presentation" && !confirmed) { confirmed = true; continue; }
            if (option == "--preflight-only" && !preflight) { preflight = true; continue; }
            if (option is not ("--renderer-executable" or "--libmpv" or "--source-video" or "--run-directory" or
                    "--duration-seconds" or "--crashes") ||
                index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal) ||
                !values.TryAdd(option, args[++index]))
                throw new ArgumentException("Unknown, duplicate or incomplete Host scenario option.");
        }
        if (scenario is null || preflight == confirmed)
            throw new ArgumentException("Use exactly one of --preflight-only or --confirm-desktop-presentation.");
        if (values.ContainsKey("--crashes") && scenario != VideoHostScenario.CrashRecovery)
            throw new ArgumentException("--crashes only applies to --video-host-crash-recovery.");
        string PathValue(string key) => values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) &&
            Path.IsPathFullyQualified(value) && !value.StartsWith(@"\\", StringComparison.Ordinal)
                ? Path.GetFullPath(value) : throw new ArgumentException($"An absolute local {key} is required.");
        int duration = scenario == VideoHostScenario.Restore ? 8 : 6;
        if (values.TryGetValue("--duration-seconds", out string? seconds) &&
            (!int.TryParse(seconds, out duration) || duration is < 3 or > 30))
            throw new ArgumentException("Each observation lasts 3..30 seconds.");
        int crashes = 2;
        if (values.TryGetValue("--crashes", out string? count) && (!int.TryParse(count, out crashes) || crashes is < 1 or > 4))
            throw new ArgumentException("Crash recovery kills the Renderer 1..4 times.");
        return new(scenario.Value, PathValue("--renderer-executable"), PathValue("--libmpv"), PathValue("--source-video"),
            PathValue("--run-directory"), duration, crashes, preflight);
    }
}
