namespace LiveWall.DesktopHostDiagnostics;

internal sealed record VideoCandidateOptions(string RendererExecutable, string ContentRoot, string LibraryPath,
    string ResultJson, int DurationSeconds, bool PreflightOnly, bool ControlRecovery = false)
{
    public const string Mode = "--video-product-candidate";
    public static VideoCandidateOptions Parse(IReadOnlyList<string> args)
    {
        bool enabled = false, confirmed = false, preflight = false, controls = false;
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        for (int index = 0; index < args.Count; index++)
        {
            string option = args[index];
            if (option == Mode && !enabled) { enabled = true; continue; }
            if (option == "--confirm-desktop-presentation" && !confirmed) { confirmed = true; continue; }
            if (option == "--preflight-only" && !preflight) { preflight = true; continue; }
            if (option == "--control-recovery" && !controls) { controls = true; continue; }
            if (option is not ("--renderer-executable" or "--content-root" or "--libmpv" or "--result-json" or "--duration-seconds") ||
                index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal) ||
                !values.TryAdd(option, args[++index])) throw new ArgumentException("Unknown, duplicate or incomplete video candidate option.");
        }
        if (!enabled || !preflight && !confirmed) throw new ArgumentException("Video desktop presentation requires --confirm-desktop-presentation.");
        string PathValue(string key) => values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) &&
            Path.IsPathFullyQualified(value) && !value.StartsWith(@"\\", StringComparison.Ordinal)
                ? Path.GetFullPath(value) : throw new ArgumentException($"An absolute local {key} is required.");
        int duration = 10;
        if (values.TryGetValue("--duration-seconds", out string? seconds) &&
            (!int.TryParse(seconds, out duration) || duration is < 1 or > 30))
            throw new ArgumentException("Video duration must be 1..30 seconds.");
        if (controls)
        {
            if (values.ContainsKey("--duration-seconds") && duration != 20)
                throw new ArgumentException("Control acceptance uses four fixed 5-second stages (20 seconds total).");
            duration = 20;
        }
        return new(PathValue("--renderer-executable"), PathValue("--content-root"), PathValue("--libmpv"), PathValue("--result-json"), duration, preflight, controls);
    }
}
