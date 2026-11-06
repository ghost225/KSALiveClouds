// KSALiveClouds: just enough KTX2 for KSA's cloud coverage maps: BC4 UNORM cubemaps, whole mip chain, levels
// zstd-supercompressed one by one (what KSA's own Earth_clouds_*_4096.ktx2 are), plus a CPU BC4 codec.

using System.Buffers.Binary;
using ZstdSharp;

namespace KSALiveClouds;

public static class Ktx2
{
    public const uint VkBc4UNorm = 139;
    private static readonly byte[] Magic = { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A };

    public sealed class Cube
    {
        public int Size, Levels;
        public byte[] Dfd = Array.Empty<byte>();
        private byte[] _file = Array.Empty<byte>();
        private (ulong Off, ulong Len, ulong Raw)[] _index = Array.Empty<(ulong, ulong, ulong)>();
        private uint _scheme;

        public static Cube Open(string path)
        {
            byte[] d = File.ReadAllBytes(path);
            if (!d.AsSpan(0, 12).SequenceEqual(Magic))
                throw new InvalidDataException(path + " is not KTX2");
            uint vk = U32(d, 12), w = U32(d, 20), h = U32(d, 24), faces = U32(d, 36), levels = U32(d, 40);
            if (vk != VkBc4UNorm || faces != 6 || w != h)
                throw new InvalidDataException($"{path}: not a BC4 cubemap (vkFormat {vk}, {faces} faces, {w}x{h})");
            var c = new Cube { Size = (int)w, Levels = (int)levels, _file = d, _scheme = U32(d, 44) };
            c.Dfd = d.AsSpan((int)U32(d, 48), (int)U32(d, 52)).ToArray();
            c._index = new (ulong, ulong, ulong)[levels];
            for (int i = 0; i < levels; i++)
                c._index[i] = (U64(d, 80 + 24 * i), U64(d, 88 + 24 * i), U64(d, 96 + 24 * i));
            return c;
        }

        /// <summary>The BC4 blocks of one mip level, all six faces in order.</summary>
        public byte[] LevelBlocks(int level)
        {
            (ulong off, ulong len, ulong raw) = _index[level];
            ReadOnlySpan<byte> src = _file.AsSpan((int)off, (int)len);
            if (_scheme == 0)
                return src.ToArray();
            if (_scheme != 2)
                throw new NotSupportedException($"KTX2 supercompression {_scheme}");
            using var dz = new Decompressor();
            return dz.Unwrap(src).ToArray();
        }

        /// <summary>Every level as BC4 blocks, [level][face].</summary>
        public byte[][][] AllLevels()
        {
            var r = new byte[Levels][][];
            for (int l = 0; l < Levels; l++)
            {
                byte[] all = LevelBlocks(l);
                int per = all.Length / 6;
                r[l] = new byte[6][];
                for (int f = 0; f < 6; f++)
                    r[l][f] = all.AsSpan(f * per, per).ToArray();
            }
            return r;
        }

        /// <summary>One face of one level decoded to bytes.</summary>
        public byte[] Face(int level, int face, byte[]? levelBlocks = null)
        {
            int size = Math.Max(1, Size >> level), nb = Math.Max(1, (size + 3) / 4);
            byte[] blocks = levelBlocks ?? LevelBlocks(level);
            var px = new byte[size * size];
            Bc4.Decode(blocks.AsSpan(face * nb * nb * 8, nb * nb * 8), size, px);
            return px;
        }
    }

    /// <summary>Writes a zstd-supercompressed BC4 cubemap. levels[l][f] = face f of level l as BC4 blocks.</summary>
    public static void WriteCube(string path, int size, byte[][][] levels, byte[] dfd, int zstdLevel = 6)
    {
        int n = levels.Length;
        var payload = new byte[n][];
        var raw = new ulong[n];
        Parallel.For(0, n, l =>
        {
            byte[] all = levels[l].SelectMany(f => f).ToArray();
            raw[l] = (ulong)all.Length;
            using var cz = new Compressor(zstdLevel);
            payload[l] = cz.Wrap(all).ToArray();
        });
        int dfdOff = 80 + 24 * n;
        long dataStart = dfdOff + dfd.Length;
        var offsets = new ulong[n];
        long pos = dataStart;
        for (int l = n - 1; l >= 0; l--)        // smallest level first in the file, as the spec prefers
        {
            offsets[l] = (ulong)pos;
            pos += payload[l].Length;
        }
        var head = new byte[dataStart];
        Magic.CopyTo(head, 0);
        W32(head, 12, VkBc4UNorm);
        W32(head, 16, 1);
        W32(head, 20, (uint)size);
        W32(head, 24, (uint)size);
        W32(head, 28, 0);
        W32(head, 32, 0);
        W32(head, 36, 6);
        W32(head, 40, (uint)n);
        W32(head, 44, 2);                     // zstd
        W32(head, 48, (uint)dfdOff);
        W32(head, 52, (uint)dfd.Length);
        for (int l = 0; l < n; l++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(80 + 24 * l), offsets[l]);
            BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(88 + 24 * l), (ulong)payload[l].Length);
            BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(96 + 24 * l), raw[l]);
        }
        dfd.CopyTo(head, dfdOff);
        string tmp = path + ".part";
        using (FileStream fs = File.Create(tmp))
        {
            fs.Write(head);
            for (int l = n - 1; l >= 0; l--)
                fs.Write(payload[l]);
        }
        File.Move(tmp, path, true);
    }

    private static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
    private static ulong U64(byte[] d, int o) => BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(o));
    private static void W32(byte[] d, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), v);
}

/// <summary>KSA's 2D BC4 DDS maps (EarthTypeMap4096.dds): level 0 decoded.</summary>
public static class Dds
{
    public static (byte[] Px, int W, int H) ReadBc4Level0(string path)
    {
        byte[] d = File.ReadAllBytes(path);
        if (d.Length < 128 || d[0] != 'D' || d[1] != 'D' || d[2] != 'S')
            throw new InvalidDataException(path + " is not DDS");
        int h = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(12)), w = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(16));
        string four = System.Text.Encoding.ASCII.GetString(d, 84, 4);
        int off = 128;
        if (four == "DX10")
        {
            uint dxgi = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(128));
            if (dxgi is not (79 or 80))                  // DXGI_FORMAT_BC4_TYPELESS / BC4_UNORM
                throw new InvalidDataException($"{path}: DXGI format {dxgi}, not BC4");
            off = 148;
        }
        else if (four is not ("BC4U" or "ATI1"))
            throw new InvalidDataException($"{path}: {four}, not BC4");
        var px = new byte[w * h];
        Bc4.Decode(d.AsSpan(off, ((w + 3) / 4) * ((h + 3) / 4) * 8), w, h, px);
        return (px, w, h);
    }
}

public static class Bc4
{
    public static void Decode(ReadOnlySpan<byte> blocks, int size, byte[] px) => Decode(blocks, size, size, px);

    public static void Decode(ReadOnlySpan<byte> blocks, int width, int height, byte[] px)
    {
        int nbx = Math.Max(1, (width + 3) / 4), nby = Math.Max(1, (height + 3) / 4);
        Span<int> pal = stackalloc int[8];
        for (int by = 0; by < nby; by++)
        for (int bx = 0; bx < nbx; bx++)
        {
            ReadOnlySpan<byte> b = blocks.Slice((by * nbx + bx) * 8, 8);
            int r0 = b[0], r1 = b[1];
            pal[0] = r0;
            pal[1] = r1;
            if (r0 > r1)
                for (int i = 2; i < 8; i++)
                    pal[i] = ((8 - i) * r0 + (i - 1) * r1) / 7;
            else
            {
                for (int i = 2; i < 6; i++)
                    pal[i] = ((6 - i) * r0 + (i - 1) * r1) / 5;
                pal[6] = 0;
                pal[7] = 255;
            }
            ulong bits = 0;
            for (int i = 0; i < 6; i++)
                bits |= (ulong)b[2 + i] << (8 * i);
            for (int k = 0; k < 16; k++)
            {
                int x = bx * 4 + (k & 3), y = by * 4 + (k >> 2);
                if (x < width && y < height)
                    px[y * width + x] = (byte)pal[(int)((bits >> (3 * k)) & 7)];
            }
        }
    }

    public static void DecodeBlock(ReadOnlySpan<byte> b, Span<byte> v)
    {
        int r0 = b[0], r1 = b[1];
        Span<int> pal = stackalloc int[8];
        pal[0] = r0;
        pal[1] = r1;
        if (r0 > r1)
            for (int i = 2; i < 8; i++)
                pal[i] = ((8 - i) * r0 + (i - 1) * r1) / 7;
        else
        {
            for (int i = 2; i < 6; i++)
                pal[i] = ((6 - i) * r0 + (i - 1) * r1) / 5;
            pal[6] = 0;
            pal[7] = 255;
        }
        ulong bits = 0;
        for (int i = 0; i < 6; i++)
            bits |= (ulong)b[2 + i] << (8 * i);
        for (int k = 0; k < 16; k++)
            v[k] = (byte)pal[(int)((bits >> (3 * k)) & 7)];
    }

    /// <summary>Min/max endpoints in 8-value mode; good enough for soft coverage maps.</summary>
    public static void EncodeBlock(ReadOnlySpan<byte> v, Span<byte> o)
    {
        int lo = 255, hi = 0;
        for (int k = 0; k < 16; k++)
        {
            lo = Math.Min(lo, v[k]);
            hi = Math.Max(hi, v[k]);
        }
        ulong bits = 0;
        o[0] = (byte)hi;                       // r0 > r1: 8-value mode, 0 = hi, 1 = lo, 2..7 between
        o[1] = (byte)lo;
        if (hi != lo)
            for (int k = 0; k < 16; k++)
            {
                int p = (int)Math.Round((hi - v[k]) * 7.0 / (hi - lo));
                ulong idx = p == 0 ? 0UL : p == 7 ? 1UL : (ulong)(p + 1);
                bits |= idx << (3 * k);
            }
        for (int i = 0; i < 6; i++)
            o[2 + i] = (byte)(bits >> (8 * i));
    }

    public static byte[] Encode(byte[] px, int size)
    {
        int nb = Math.Max(1, (size + 3) / 4);
        var outp = new byte[nb * nb * 8];
        Parallel.For(0, nb, by =>
        {
            Span<byte> v = stackalloc byte[16];
            for (int bx = 0; bx < nb; bx++)
            {
                for (int k = 0; k < 16; k++)
                {
                    int x = Math.Min(size - 1, bx * 4 + (k & 3)), y = Math.Min(size - 1, by * 4 + (k >> 2));
                    v[k] = px[y * size + x];
                }
                EncodeBlock(v, outp.AsSpan((by * nb + bx) * 8, 8));
            }
        });
        return outp;
    }

    /// <summary>Crossfade two BC4 block runs of the same shape: decode both, lerp by t, re-encode. Mips blend
    /// linearly too, so each level is blended as is (no mip rebuild).</summary>
    public static void Blend(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, float t, Span<byte> o)
    {
        Span<byte> va = stackalloc byte[16], vb = stackalloc byte[16];
        int n = a.Length / 8;
        for (int i = 0; i < n; i++)
        {
            DecodeBlock(a.Slice(i * 8, 8), va);
            DecodeBlock(b.Slice(i * 8, 8), vb);
            for (int k = 0; k < 16; k++)
                va[k] = (byte)(va[k] + (vb[k] - va[k]) * t + 0.5f);
            EncodeBlock(va, o.Slice(i * 8, 8));
        }
    }
}
