using LiveWall.Domain.Displays;

namespace LiveWall.Platform.Windows.Displays;

internal sealed record DisplaySnapshot(
    string DevicePath,
    DisplayBounds Bounds,
    double ScaleFactor,
    int RefreshRateHz,
    bool IsPrimary);

internal interface IDisplaySnapshotProvider
{
    IReadOnlyList<DisplaySnapshot> Read();
}
