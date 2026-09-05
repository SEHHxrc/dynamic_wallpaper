using System.Security.Cryptography;

namespace LiveWall.Infrastructure.Ipc;

/// <summary>
/// Validates one renderer handshake token exactly once and clears the expected secret.
/// </summary>
public sealed class OneTimeAuthenticationToken : IDisposable
{
    private readonly Lock gate = new();
    private byte[]? expectedToken;

    public OneTimeAuthenticationToken(string expectedToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedToken);
        try
        {
            this.expectedToken = Convert.FromHexString(expectedToken);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Authentication token must be hexadecimal.", nameof(expectedToken), exception);
        }

        if (this.expectedToken.Length != 32)
        {
            CryptographicOperations.ZeroMemory(this.expectedToken);
            this.expectedToken = null;
            throw new ArgumentException("Authentication token must contain 256 bits.", nameof(expectedToken));
        }
    }

    public bool TryConsume(string suppliedToken)
    {
        ArgumentNullException.ThrowIfNull(suppliedToken);

        byte[] suppliedBytes = new byte[32];
        byte[] parsedBytes;
        try
        {
            parsedBytes = Convert.FromHexString(suppliedToken);
        }
        catch (FormatException)
        {
            parsedBytes = [];
        }

        bool hasExpectedLength = parsedBytes.Length == suppliedBytes.Length;
        if (hasExpectedLength)
        {
            parsedBytes.CopyTo(suppliedBytes, 0);
        }

        CryptographicOperations.ZeroMemory(parsedBytes);

        lock (gate)
        {
            if (expectedToken is null)
            {
                CryptographicOperations.ZeroMemory(suppliedBytes);
                return false;
            }

            bool matches = CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedToken);

            CryptographicOperations.ZeroMemory(suppliedBytes);
            CryptographicOperations.ZeroMemory(expectedToken);
            expectedToken = null;
            return matches & hasExpectedLength;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (expectedToken is not null)
            {
                CryptographicOperations.ZeroMemory(expectedToken);
                expectedToken = null;
            }
        }
    }
}
