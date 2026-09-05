namespace LiveWall.Domain.Displays;

public readonly record struct DisplayId
{
    public DisplayId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record DisplayBounds
{
    public DisplayBounds(int x, int y, int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive.");
        }

        if ((long)x + width > int.MaxValue || (long)y + height > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Bounds exceed the supported coordinate range.");
        }

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public int X { get; }

    public int Y { get; }

    public int Width { get; }

    public int Height { get; }

    public int Right => X + Width;

    public int Bottom => Y + Height;

    public static DisplayBounds Union(IEnumerable<DisplayBounds> bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        DisplayBounds[] items = bounds.ToArray();
        if (items.Length == 0)
        {
            throw new ArgumentException("At least one bounds value is required.", nameof(bounds));
        }

        int left = items.Min(item => item.X);
        int top = items.Min(item => item.Y);
        int right = items.Max(item => item.Right);
        int bottom = items.Max(item => item.Bottom);
        return new DisplayBounds(left, top, checked(right - left), checked(bottom - top));
    }
}

public sealed record DisplayDescriptor(
    DisplayId Id,
    string DevicePath,
    DisplayBounds Bounds,
    double ScaleFactor,
    int RefreshRateHz,
    bool IsPrimary);

public sealed record DisplayTopology(
    long Revision,
    IReadOnlyList<DisplayDescriptor> Displays);
