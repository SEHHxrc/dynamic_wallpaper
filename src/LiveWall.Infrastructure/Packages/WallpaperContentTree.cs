using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace LiveWall.Infrastructure.Packages;

/// <summary>
/// Walks and hashes a normalized wallpaper content tree. Reparse points are rejected, never followed.
/// </summary>
public static class WallpaperContentTree
{
    public const int MaximumFileCount = 10_000;

    private static readonly EnumerationOptions Enumeration = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// SHA-256 over, for each file in ordinal order of its forward-slash relative path:
    /// UTF-8 path, one zero byte, 8-byte little-endian length, raw SHA-256 of the file.
    /// Returns upper-case hex.
    /// </summary>
    public static async Task<string> ComputeHashAsync(string rootPath, CancellationToken cancellationToken)
    {
        using IncrementalHash tree = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] length = new byte[sizeof(long)];
        foreach (string relativePath in EnumerateRelativeFiles(rootPath))
        {
            string path = Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            await using FileStream stream = OpenRead(path);
            byte[] fileHash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            BinaryPrimitives.WriteInt64LittleEndian(length, stream.Length);
            tree.AppendData(Encoding.UTF8.GetBytes(relativePath));
            tree.AppendData(new byte[1]);
            tree.AppendData(length);
            tree.AppendData(fileHash);
        }

        return Convert.ToHexString(tree.GetHashAndReset());
    }

    /// <summary>Copies into an existing empty directory; the copy holds plain files only.</summary>
    public static async Task CopyAsync(
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        foreach (string relativePath in EnumerateRelativeFiles(sourceRoot))
        {
            string nativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
            string destination = Path.Combine(destinationRoot, nativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using FileStream input = OpenRead(Path.Combine(sourceRoot, nativePath));
            await using FileStream output = new(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
    }

    /// <exception cref="InvalidDataException">
    /// The tree holds a reparse point, a non-regular entry, or more than the allowed file count.
    /// </exception>
    public static IReadOnlyList<string> EnumerateRelativeFiles(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        string root = Path.GetFullPath(rootPath);
        RejectReparsePoint(new DirectoryInfo(root));
        List<string> files = [];
        Stack<string> pending = new([root]);
        while (pending.TryPop(out string? directory))
        {
            foreach (FileSystemInfo entry in new DirectoryInfo(directory)
                         .EnumerateFileSystemInfos("*", Enumeration))
            {
                RejectReparsePoint(entry);
                if (entry is DirectoryInfo)
                {
                    pending.Push(entry.FullName);
                    continue;
                }

                if (files.Count == MaximumFileCount)
                {
                    throw new InvalidDataException("Wallpaper content holds too many files.");
                }

                files.Add(Path.GetRelativePath(root, entry.FullName).Replace(Path.DirectorySeparatorChar, '/'));
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static void RejectReparsePoint(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Reparse points are not allowed in wallpaper content.");
        }
    }

    private static FileStream OpenRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 81920,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
}
