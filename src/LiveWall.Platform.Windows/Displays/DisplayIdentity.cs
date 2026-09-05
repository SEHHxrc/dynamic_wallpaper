using System.Security.Cryptography;
using System.Text;
using LiveWall.Domain.Displays;

namespace LiveWall.Platform.Windows.Displays;

internal static class DisplayIdentity
{
    internal static DisplayId FromDevicePath(string devicePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        string normalizedPath = devicePath.Trim().ToUpperInvariant();
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath));
        return new DisplayId($"win-display-{Convert.ToHexStringLower(digest)}");
    }
}
