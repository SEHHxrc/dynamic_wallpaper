namespace LiveWall.Infrastructure.FileSystem;

/// <summary>
/// Resolves untrusted package paths beneath one existing, non-reparse-point root.
/// </summary>
public sealed class CanonicalPathResolver
{
    private static readonly HashSet<string> ReservedWindowsNames = BuildReservedWindowsNames();
    private readonly string rootWithSeparator;

    public CanonicalPathResolver(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        RootPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(RootPath))
        {
            throw new DirectoryNotFoundException($"The trusted root does not exist: '{RootPath}'.");
        }

        EnsureNotReparsePoint(RootPath);
        rootWithSeparator = Path.EndsInDirectorySeparator(RootPath)
            ? RootPath
            : RootPath + Path.DirectorySeparatorChar;
    }

    public string RootPath { get; }

    public string ResolvePackagePath(string packagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);

        ValidatePackagePath(packagePath);
        string[] segments = packagePath.Split('/');
        string candidate = Path.GetFullPath(Path.Combine([RootPath, .. segments]));

        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new PathSecurityException($"Package path escapes the trusted root: '{packagePath}'.");
        }

        EnsureExistingComponentsAreNotReparsePoints(segments);
        return candidate;
    }

    private static void ValidatePackagePath(string packagePath)
    {
        if (Path.IsPathFullyQualified(packagePath) ||
            packagePath.StartsWith('/') ||
            packagePath.Contains('\\') ||
            packagePath.Contains(':'))
        {
            throw new PathSecurityException($"Package path must be a relative forward-slash path: '{packagePath}'.");
        }

        string[] segments = packagePath.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                throw new PathSecurityException($"Package path contains an unsafe segment: '{packagePath}'.");
            }

            if (segment.Any(char.IsControl) || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new PathSecurityException($"Package path contains invalid characters: '{packagePath}'.");
            }

            if (segment.EndsWith(' ') || segment.EndsWith('.') || IsReservedWindowsName(segment))
            {
                throw new PathSecurityException($"Package path contains a Windows-reserved segment: '{packagePath}'.");
            }
        }
    }

    private void EnsureExistingComponentsAreNotReparsePoints(IEnumerable<string> segments)
    {
        string current = RootPath;
        foreach (string segment in segments)
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                return;
            }

            EnsureNotReparsePoint(current);
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new PathSecurityException($"Reparse points are not allowed in trusted paths: '{path}'.");
            }
        }
        catch (PathSecurityException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PathSecurityException($"Unable to verify path safety: '{path}'.", exception);
        }
    }

    private static bool IsReservedWindowsName(string segment)
    {
        string name = segment.Split('.')[0];
        return ReservedWindowsNames.Contains(name);
    }

    private static HashSet<string> BuildReservedWindowsNames()
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "CLOCK$",
        };

        for (int number = 1; number <= 9; number++)
        {
            names.Add($"COM{number}");
            names.Add($"LPT{number}");
        }

        return names;
    }
}
