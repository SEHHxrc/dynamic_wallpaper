using System.Text;
using System.Text.Json;

namespace LiveWall.Infrastructure.FileSystem;

public static class AtomicFileWriter
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task WriteAllTextAsync(
        string destinationPath,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(content);

        string destination = Path.GetFullPath(destinationPath);
        string? directory = Path.GetDirectoryName(destination);
        if (directory is null)
        {
            throw new ArgumentException("Destination must have a parent directory.", nameof(destinationPath));
        }

        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (FileStream stream = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (StreamWriter writer = new(stream, Utf8WithoutBom, bufferSize: 16 * 1024, leaveOpen: true))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            Commit(temporary, destination);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static Task WriteJsonAsync<T>(
        string destinationPath,
        T value,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string json = JsonSerializer.Serialize(value, options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return WriteAllTextAsync(destinationPath, json, cancellationToken);
    }

    private static void Commit(string temporary, string destination)
    {
        if (File.Exists(destination))
        {
            File.Replace(temporary, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return;
        }

        try
        {
            File.Move(temporary, destination);
        }
        catch (IOException) when (File.Exists(destination))
        {
            File.Replace(temporary, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
    }
}
