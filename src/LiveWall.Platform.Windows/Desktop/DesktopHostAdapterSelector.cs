using LiveWall.Application.Sessions;

namespace LiveWall.Platform.Windows.Desktop;

internal sealed class DesktopHostAdapterSelector
{
    private readonly IDesktopHostAdapter[] adapters;

    public DesktopHostAdapterSelector(IEnumerable<IDesktopHostAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        this.adapters = adapters.ToArray();
        if (this.adapters.Length == 0)
        {
            throw new ArgumentException("At least one desktop adapter is required.", nameof(adapters));
        }
    }

    public async Task<SelectedDesktopAdapter> DiscoverAsync(
        WindowsShellSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        List<Exception> failures = [];
        foreach (IDesktopHostAdapter adapter in adapters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!adapter.IsSupported(snapshot))
            {
                continue;
            }

            try
            {
                DesktopAttachmentLease attachmentLease = await adapter.DiscoverAsync(cancellationToken)
                    .ConfigureAwait(false);
                return new SelectedDesktopAdapter(adapter, attachmentLease);
            }
            catch (DesktopAttachPointUnavailableException exception)
            {
                failures.Add(exception);
            }
        }

        throw new DesktopAttachPointUnavailableException(
            failures.Count == 0
                ? "No desktop adapter supports the current Windows Shell."
                : $"No supported desktop attach point was available: {string.Join(" | ", failures.Select(failure => failure.Message))}");
    }
}

internal sealed record SelectedDesktopAdapter(
    IDesktopHostAdapter Adapter,
    DesktopAttachmentLease AttachmentLease);
