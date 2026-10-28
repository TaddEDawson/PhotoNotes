using System.Buffers.Binary;
using System.Text;

namespace PhotoNotes.Core.Tests;

internal static class TestImages
{
    public static byte[] Tiff(string date = "2024:07:18 12:34:56")
    {
        var make = Encoding.ASCII.GetBytes("PhotoNotes\0");
        var dateBytes = Encoding.ASCII.GetBytes(date + "\0");
        const uint makeOffset = 8 + 2 + 11 * 12 + 4;
        var subIfdOffset = makeOffset + (uint)make.Length;
        var dateOffset = subIfdOffset + 2 + 12 + 4;
        var pixelOffset = dateOffset + (uint)dateBytes.Length;
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("II"));
        writer.Write((ushort)42);
        writer.Write((uint)8);
        writer.Write((ushort)11);
        Entry(256, 4, 1, 1);
        Entry(257, 4, 1, 1);
        Entry(258, 3, 1, 8);
        Entry(259, 3, 1, 1);
        Entry(262, 3, 1, 1);
        Entry(271, 2, (uint)make.Length, makeOffset);
        Entry(273, 4, 1, pixelOffset);
        Entry(277, 3, 1, 1);
        Entry(278, 4, 1, 1);
        Entry(279, 4, 1, 1);
        Entry(34665, 4, 1, subIfdOffset);
        writer.Write((uint)0);
        writer.Write(make);
        writer.Write((ushort)1);
        Entry(36867, 2, (uint)dateBytes.Length, dateOffset);
        writer.Write((uint)0);
        writer.Write(dateBytes);
        writer.Write((byte)127);
        return buffer.ToArray();

        void Entry(ushort tag, ushort type, uint count, uint value)
        {
            writer.Write(tag);
            writer.Write(type);
            writer.Write(count);
            writer.Write(value);
        }
    }

    public static byte[] Jpeg(string? xmp = null, string exifDate = "2024:07:18 12:34:56", bool includeExif = true)
    {
        using var buffer = new MemoryStream();
        buffer.Write([0xff, 0xd8]);
        var exif = Encoding.ASCII.GetBytes("Exif\0\0").Concat(Tiff(exifDate)).ToArray();
        if (includeExif) Segment(exif);
        if (xmp is not null)
            Segment(Encoding.UTF8.GetBytes("http://ns.adobe.com/xap/1.0/\0" + xmp));
        buffer.Write([0xff, 0xd9]);
        return buffer.ToArray();

        void Segment(byte[] bytes)
        {
            buffer.Write([0xff, 0xe1]);
            Span<byte> size = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(size, checked((ushort)(bytes.Length + 2)));
            buffer.Write(size);
            buffer.Write(bytes);
        }
    }

    public static byte[] TiffWithDetails(bool dng = false, char latitudeRef = 'N', char longitudeRef = 'W')
    {
        var original = Tiff();
        using var buffer = new MemoryStream();
        buffer.Write(original);
        using var writer = new BinaryWriter(buffer, Encoding.ASCII, leaveOpen: true);
        var model = Text("PN-1");
        var lens = Text("Example 35mm");
        var latitude = Coordinates(43, 30, 0);
        var longitude = Coordinates(80, 15, 0);
        var subIfd = (uint)buffer.Position;
        writer.Write((ushort)2);
        var originalSubIfd = BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(8 + 2 + 10 * 12 + 8));
        var originalDate = BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan((int)originalSubIfd + 2 + 8));
        Entry(36867, 2, 20, originalDate);
        Entry(42036, 2, lens.Length, lens.Offset);
        writer.Write((uint)0);
        var gpsIfd = (uint)buffer.Position;
        writer.Write((ushort)4);
        Entry(1, 2, 2, latitudeRef);
        Entry(2, 5, 3, latitude);
        Entry(3, 2, 2, longitudeRef);
        Entry(4, 5, 3, longitude);
        writer.Write((uint)0);
        var rootIfd = (uint)buffer.Position;
        var entries = new List<(ushort Tag, byte[] Bytes)>();
        for (var index = 0; index < 11; index++)
        {
            var entry = original.AsSpan(10 + index * 12, 12).ToArray();
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(entry);
            if (tag == 34665) BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(8), subIfd);
            entries.Add((tag, entry));
        }
        Add(272, 2, model.Length, model.Offset);
        Add(34853, 4, 1, gpsIfd);
        if (dng) Add(50706, 1, 4, 0x00000401);
        writer.Write((ushort)entries.Count);
        foreach (var entry in entries.OrderBy(e => e.Tag)) writer.Write(entry.Bytes);
        writer.Write((uint)0);
        var bytes = buffer.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), rootIfd);
        return bytes;

        (uint Offset, uint Length) Text(string value)
        {
            var text = Encoding.ASCII.GetBytes(value + "\0");
            var offset = (uint)buffer.Position;
            writer.Write(text);
            return (offset, (uint)text.Length);
        }

        uint Coordinates(params uint[] values)
        {
            var offset = (uint)buffer.Position;
            foreach (var value in values) { writer.Write(value); writer.Write((uint)1); }
            return offset;
        }

        void Entry(ushort tag, ushort type, uint count, uint value)
        {
            writer.Write(tag);
            writer.Write(type);
            writer.Write(count);
            writer.Write(value);
        }

        void Add(ushort tag, ushort type, uint count, uint value)
        {
            var bytes = new byte[12];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, tag);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), type);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), count);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), value);
            entries.Add((tag, bytes));
        }
    }

    public static byte[] Cr2()
    {
        var original = Tiff();
        var bytes = new byte[original.Length + 8];
        original.AsSpan(0, 8).CopyTo(bytes);
        original.AsSpan(8).CopyTo(bytes.AsSpan(16));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 16);
        bytes[8] = (byte)'C';
        bytes[9] = (byte)'R';
        bytes[10] = 2;
        for (var index = 0; index < 11; index++)
        {
            var entry = bytes.AsSpan(18 + index * 12, 12);
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(entry);
            if (tag is 271 or 273 or 34665)
                BinaryPrimitives.WriteUInt32LittleEndian(entry[8..],
                    BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]) + 8);
        }
        var subIfd = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(18 + 10 * 12 + 8));
        var dateOffset = bytes.AsSpan((int)subIfd + 2 + 8, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(dateOffset, BinaryPrimitives.ReadUInt32LittleEndian(dateOffset) + 8);
        return bytes;
    }

    public static byte[] Png()
    {
        // A complete, independently encoded 1x1 grayscale PNG.
        using var buffer = new MemoryStream();
        buffer.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Chunk("IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 0, 0, 0, 0]);
        Chunk("tEXt", Encoding.ASCII.GetBytes("Author\0PhotoNotes test"));
        Chunk("eXIf", Tiff());
        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed,
                   System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write([0, 127]);
        Chunk("IDAT", compressed.ToArray());
        Chunk("IEND", []);
        return buffer.ToArray();

        void Chunk(string type, byte[] bytes)
        {
            var typeBytes = Encoding.ASCII.GetBytes(type);
            Span<byte> word = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(word, (uint)bytes.Length);
            buffer.Write(word);
            buffer.Write(typeBytes);
            buffer.Write(bytes);
            var crc = uint.MaxValue;
            foreach (var b in typeBytes.Concat(bytes))
            {
                crc ^= b;
                for (var bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
            }
            BinaryPrimitives.WriteUInt32BigEndian(word, ~crc);
            buffer.Write(word);
        }
    }

    public static byte[] Heic(bool brokenReference = false)
    {
        var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("heic\0\0\0\0heicmif1"));
        var exif = new byte[4].Concat(Tiff()).ToArray();
        var infe = Box("infe", Join([2, 0, 0, 0, 0, brokenReference ? (byte)3 : (byte)2, 0, 0],
            Encoding.ASCII.GetBytes("Exif"), [0]));
        var iinf = Box("iinf", Join([0, 0, 0, 0, 0, 1], infe));
        var iref = Box("iref", Join([0, 0, 0, 0], Box("cdsc", [0, 2, 0, 1, 0, 1])));
        var pitm = Box("pitm", [0, 0, 0, 0, 0, 1]);
        byte[] Meta(uint offset)
        {
            using var location = new MemoryStream();
            location.Write([0, 0, 0, 0, 0x44, 0, 0, 1, 0, 2, 0, 0, 0, 1]);
            Span<byte> word = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(word, offset);
            location.Write(word);
            BinaryPrimitives.WriteUInt32BigEndian(word, (uint)exif.Length);
            location.Write(word);
            return Box("meta", Join([0, 0, 0, 0], pitm, iinf, Box("iloc", location.ToArray()), iref));
        }
        var meta = Meta(0);
        meta = Meta((uint)(ftyp.Length + meta.Length + 8));
        return Join(ftyp, meta, Box("mdat", exif));
    }

    private static byte[] Box(string type, byte[] content)
    {
        var bytes = new byte[8 + content.Length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(bytes, 4);
        content.CopyTo(bytes, 8);
        return bytes;
    }

    private static byte[] Join(params byte[][] arrays) => arrays.SelectMany(bytes => bytes).ToArray();
}
