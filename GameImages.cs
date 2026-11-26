using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

// Character portraits and the keyword icon atlas, read from the game's Unity files and re-encoded as PNG.
public sealed class GameImages
{
    // Order of the 3-wide cells in Spell/kw_icons, from the game's SpellIcons.Names.
    private static readonly string[] IconNames =
    {
        "spirit", "freeze", "ward", "barrier", "leap", "timestop", "turns", "summon",
        "ultimate", "disarm", "pierce", "slip", "counter", "doom", "phantom",
    };
    private const int ResourceManagerClass = 147;
    private const int Texture2DClass = 28;
    private const int Rgba32 = 4;

    public Dictionary<string, byte[]> Avatars { get; } = new();
    public Dictionary<string, byte[]> Icons { get; } = new();

    public static GameImages Load(string dataDir)
    {
        var images = new GameImages();
        try
        {
            var container = ReadContainer(Path.Combine(dataDir, "globalgamemanagers"));
            var resources = new AssetFile(Path.Combine(dataDir, "resources.assets"));
            foreach (var (path, pathId) in container)
            {
                string[] parts = path.Split('/');
                if (parts.Length == 3 && parts[0] == "characters" && parts[2] == "icon"
                    && resources.ReadTexture(pathId, dataDir) is { } icon)
                {
                    images.Avatars[parts[1] == "rinnosuke" ? "shop" : parts[1]] = Png(icon.Width, icon.Height, icon.Crop(0, 0, icon.Width, icon.Height));
                }
                else if (path == "spell/kw_icons" && resources.ReadTexture(pathId, dataDir) is { } atlas)
                {
                    int cell = atlas.Width / 3;
                    for (int i = 0; i < IconNames.Length; i++)
                    {
                        images.Icons[IconNames[i]] = Png(cell, cell, atlas.Crop(i % 3 * cell, i / 3 * cell, cell, cell));
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or NotSupportedException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not read game images: {e.Message}");
        }
        return images;
    }

    private static List<(string Path, long PathId)> ReadContainer(string globalGameManagers)
    {
        var file = new AssetFile(globalGameManagers);
        var list = new List<(string, long)>();
        foreach (var (pathId, obj) in file.Objects)
        {
            if (obj.ClassId != ResourceManagerClass)
            {
                continue;
            }
            var r = new SpanReader(file.ReadObject(pathId));
            int count = r.Int32();
            for (int i = 0; i < count; i++)
            {
                string path = r.AlignedString();
                r.Int32();
                list.Add((path, r.Int64()));
            }
        }
        return list;
    }

    private sealed record Texture(int Width, int Height, byte[] Rgba)
    {
        // Unity stores rows bottom-up; this returns the region top-down, with y measured from the top.
        public byte[] Crop(int x, int y, int w, int h)
        {
            var dst = new byte[w * h * 4];
            for (int row = 0; row < h; row++)
            {
                int src = ((Height - 1 - (y + row)) * Width + x) * 4;
                Array.Copy(Rgba, src, dst, row * w * 4, w * 4);
            }
            return dst;
        }
    }

    // Minimal reader for Unity 2022+ serialized files (format 22+) built without type trees.
    private sealed class AssetFile
    {
        private readonly string _path;
        private readonly long _dataOffset;
        public readonly Dictionary<long, (long Start, int Size, int ClassId)> Objects = new();

        public AssetFile(string path)
        {
            _path = path;
            using var fs = File.OpenRead(path);
            var header = new byte[48];
            fs.ReadExactly(header);
            uint version = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
            if (version < 22 || header[16] != 0)
            {
                throw new NotSupportedException($"{Path.GetFileName(path)} uses asset format {version}.");
            }
            int metadataSize = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20));
            _dataOffset = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(32));
            var meta = new byte[metadataSize];
            fs.ReadExactly(meta);

            // The metadata starts 4-byte aligned in the file, so aligning within the buffer matches Unity's alignment.
            var r = new SpanReader(meta);
            r.CString();
            r.Int32();
            if (r.Byte() != 0)
            {
                throw new NotSupportedException("Asset files with type trees are not supported.");
            }
            var classIds = new int[r.Int32()];
            for (int i = 0; i < classIds.Length; i++)
            {
                classIds[i] = r.Int32();
                r.Skip(3);
                r.Skip(classIds[i] == 114 ? 32 : 16);
            }
            int objectCount = r.Int32();
            for (int i = 0; i < objectCount; i++)
            {
                r.Align();
                long pathId = r.Int64();
                long start = r.Int64();
                int size = r.Int32();
                Objects[pathId] = (start, size, classIds[r.Int32()]);
            }
        }

        public byte[] ReadObject(long pathId)
        {
            var (start, size, _) = Objects[pathId];
            using var fs = File.OpenRead(_path);
            fs.Position = _dataOffset + start;
            var bytes = new byte[size];
            fs.ReadExactly(bytes);
            return bytes;
        }

        public Texture ReadTexture(long pathId, string dataDir)
        {
            if (!Objects.TryGetValue(pathId, out var obj) || obj.ClassId != Texture2DClass)
            {
                return null;
            }
            byte[] bytes = ReadObject(pathId);
            var r = new SpanReader(bytes);
            r.AlignedString();
            r.Skip(4);
            int width = r.Int32();
            int height = r.Int32();
            int imageSize = r.Int32();
            r.Skip(4);
            int length = width * height * 4;
            if (r.Int32() != Rgba32 || imageSize != length)
            {
                return null;
            }
            // The pixels live in a .resS file named by the StreamingInfo at the end of the object:
            // offset (u64), size (u32), then the file name as an aligned string.
            string resS = Path.GetFileName(_path) + ".resS";
            int nameAt = bytes.AsSpan().LastIndexOf(Encoding.UTF8.GetBytes(resS));
            if (nameAt < 16)
            {
                return null;
            }
            long offset = BitConverter.ToInt64(bytes, nameAt - 16);
            var rgba = new byte[length];
            using var fs = File.OpenRead(Path.Combine(dataDir, resS));
            fs.Position = offset;
            fs.ReadExactly(rgba);
            return new Texture(width, height, rgba);
        }
    }

    private ref struct SpanReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _pos;

        public byte Byte() => _data[_pos++];
        public int Int32() { int v = BinaryPrimitives.ReadInt32LittleEndian(_data[_pos..]); _pos += 4; return v; }
        public long Int64() { long v = BinaryPrimitives.ReadInt64LittleEndian(_data[_pos..]); _pos += 8; return v; }
        public void Skip(int n) => _pos += n;
        public void Align() => _pos = (_pos + 3) & ~3;

        public string CString()
        {
            int end = _data[_pos..].IndexOf((byte)0);
            string s = Encoding.UTF8.GetString(_data.Slice(_pos, end));
            _pos += end + 1;
            return s;
        }

        public string AlignedString()
        {
            int len = Int32();
            string s = Encoding.UTF8.GetString(_data.Slice(_pos, len));
            _pos += len;
            Align();
            return s;
        }
    }

    private static byte[] Png(int width, int height, byte[] rgbaTopDown)
    {
        int stride = width * 4;
        var raw = new byte[height * (stride + 1)];
        for (int y = 0; y < height; y++)
        {
            Array.Copy(rgbaTopDown, y * stride, raw, y * (stride + 1) + 1, stride);
        }
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(raw);
        }
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;
        ihdr[9] = 6;

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] body)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, body.Length);
        s.Write(buf);
        byte[] typeAndBody = [.. Encoding.ASCII.GetBytes(type), .. body];
        s.Write(typeAndBody);
        BinaryPrimitives.WriteUInt32BigEndian(buf, Crc32(typeAndBody));
        s.Write(buf);
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc >> 1) ^ (0xEDB88320 & (uint)-(int)(crc & 1));
            }
        }
        return ~crc;
    }
}
