using System.Text;

namespace LiveWall.VideoPresentationProbe;

// Original deterministic pixels: moving cyan stripe, orange/yellow background,
// top/bottom orientation markers. No decoder/encoder download or external media.
internal static class SyntheticVideo
{
    public const int Width = 320;
    public const int Height = 180;
    public const int Fps = 24;
    public const int Frames = 72;

    public static void Write(string path)
    {
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.ReadWrite);
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
        long riff = Begin(writer, "RIFF", "AVI ");
        long header = Begin(writer, "LIST", "hdrl");
        long main = Begin(writer, "avih");
        foreach (uint value in new uint[] { 1_000_000 / Fps, Width * Height * 3 * Fps, 0, 0x10, Frames, 0, 1, Width * Height * 3, Width, Height, 0, 0, 0, 0 }) writer.Write(value);
        End(writer, main);
        long streams = Begin(writer, "LIST", "strl");
        long streamHeader = Begin(writer, "strh");
        Four(writer, "vids"); Four(writer, "DIB ");
        writer.Write(0u); writer.Write((ushort)0); writer.Write((ushort)0);
        foreach (uint value in new uint[] { 0, 1, Fps, 0, Frames, Width * Height * 3, uint.MaxValue, 0 }) writer.Write(value);
        writer.Write((short)0); writer.Write((short)0); writer.Write((short)Width); writer.Write((short)Height);
        End(writer, streamHeader);
        long format = Begin(writer, "strf");
        writer.Write(40u); writer.Write(Width); writer.Write(Height); writer.Write((ushort)1); writer.Write((ushort)24);
        foreach (uint value in new uint[] { 0, Width * Height * 3, 0, 0, 0, 0 }) writer.Write(value);
        End(writer, format); End(writer, streams); End(writer, header);
        long movie = Begin(writer, "LIST", "movi");
        List<uint> offsets = [];
        for (int frame = 0; frame < Frames; frame++)
        {
            offsets.Add(checked((uint)(stream.Position - (movie + 4))));
            long chunk = Begin(writer, "00db");
            for (int y = Height - 1; y >= 0; y--)
                for (int x = 0; x < Width; x++)
                {
                    bool stripe = (x + frame * 5) % Width < 42;
                    writer.Write((byte)(stripe ? 240 : 0));
                    writer.Write((byte)(stripe ? 240 : y < 12 ? 255 : 96 + frame * 2));
                    writer.Write((byte)(stripe ? 0 : y >= Height - 12 ? 100 : 255));
                }
            End(writer, chunk);
        }
        End(writer, movie);
        long index = Begin(writer, "idx1");
        foreach (uint offset in offsets) { Four(writer, "00db"); writer.Write(0x10u); writer.Write(offset); writer.Write(Width * Height * 3); }
        End(writer, index); End(writer, riff);
    }

    private static long Begin(BinaryWriter writer, string name, string? type = null)
    {
        Four(writer, name); long size = writer.BaseStream.Position; writer.Write(0u);
        if (type is not null) Four(writer, type);
        return size;
    }
    private static void End(BinaryWriter writer, long offset)
    {
        long end = writer.BaseStream.Position;
        writer.BaseStream.Position = offset; writer.Write(checked((uint)(end - offset - 4)));
        writer.BaseStream.Position = end;
    }
    private static void Four(BinaryWriter writer, string value) => writer.Write(Encoding.ASCII.GetBytes(value));
}
