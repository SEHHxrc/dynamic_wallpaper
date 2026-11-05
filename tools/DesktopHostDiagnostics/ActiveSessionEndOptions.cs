namespace LiveWall.DesktopHostDiagnostics;

internal sealed record ActiveSessionEndOptions(string RunDirectory)
{
    internal const string Mode = "--raised-desktop-host-session-end";

    public static ActiveSessionEndOptions Parse(IReadOnlyList<string> args)
    {
        bool enabled = false;
        string? directory = null;
        for (int index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case Mode when !enabled: enabled = true; break;
                case "--run-directory" when directory is null && index + 1 < args.Count:
                    directory = args[++index]; break;
                default: throw new ArgumentException("Active session-end accepts only its explicit mode and --run-directory.");
            }
        }
        if (!enabled || string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Active session-end requires a fresh --run-directory.");
        return new(Path.GetFullPath(directory));
    }
}
