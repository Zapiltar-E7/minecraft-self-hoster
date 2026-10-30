using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace MinecraftHost.Core;

/// <summary>
/// Lossless repacking of Minecraft region files (.mca) for transfer.
/// A region file compresses every chunk on its own (zlib), so zipping it gains little. Here each chunk is
/// unpacked and the whole region is compressed in one Brotli stream, which is about 40% smaller.
/// Unpacking rebuilds a normal region file. The bytes differ (zlib output isn't fixed) but every chunk's
/// data is identical, and <see cref="Unpack"/> proves it with a SHA-256 of all chunk data taken at export.
/// </summary>
public static class RegionPack
{
    static readonly byte[] Magic = "MCZ1"u8.ToArray();
    const int Sector = 4096;
    const int Quality = 5;          // Brotli 0-11: 5 is ~2% bigger than 9 but 4x faster
    const int Window = 24;
    const int MaxRepackSectors = 200; // bigger chunks are kept as stored, so re-compressing can't overflow 255 sectors

    // how a chunk is kept: re-compressed on unpack, or its stored bytes kept as they are
    const byte ModeInflated = 0, ModeStored = 1;

    sealed record Chunk(int Index, byte Type, byte Mode, byte[] Data);

    /// <summary>Returns the packed bytes, or null if the file isn't a region file this can read (then copy it as-is).</summary>
    public static byte[]? Pack(byte[] mca)
    {
        var chunks = Read(mca);
        if (chunks == null) return null;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var raw = new MemoryStream();
        foreach (var c in chunks)
        {
            Hash(hash, c);
            raw.Write(c.Data);
        }

        var output = new MemoryStream();
        var w = new BinaryWriter(output);
        w.Write(Magic);
        w.Write(mca, Sector, Sector);   // timestamps table
        w.Write(chunks.Count);
        foreach (var c in chunks)
        {
            w.Write((ushort)c.Index);
            w.Write(c.Type);
            w.Write(c.Mode);
            w.Write(c.Data.Length);
        }
        w.Write(hash.GetHashAndReset());
        w.Flush();
        BrotliCompress(raw.GetBuffer().AsSpan(0, (int)raw.Length), output);
        return output.ToArray();
    }

    /// <summary>Rebuilds the region file and checks every chunk against the hash taken at export.</summary>
    public static byte[] Unpack(byte[] mcz)
    {
        var input = new MemoryStream(mcz);
        var r = new BinaryReader(input);
        if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("Not a packed region file.");
        var timestamps = r.ReadBytes(Sector);
        var count = r.ReadInt32();
        var heads = new List<(int Index, byte Type, byte Mode, int Length)>(count);
        for (var i = 0; i < count; i++) heads.Add((r.ReadUInt16(), r.ReadByte(), r.ReadByte(), r.ReadInt32()));
        var expected = r.ReadBytes(32);

        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        var chunks = heads.Select(h =>
        {
            var data = new byte[h.Length];
            brotli.ReadExactly(data);
            return new Chunk(h.Index, h.Type, h.Mode, data);
        }).ToList();

        var mca = Write(timestamps, chunks);

        // read the rebuilt file back exactly as the export read the original, and compare
        var again = Read(mca) ?? throw new InvalidDataException("The rebuilt region file could not be read back.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var c in again) Hash(hash, c);
        if (!hash.GetHashAndReset().AsSpan().SequenceEqual(expected))
            throw new InvalidDataException("A rebuilt region file doesn't match the original chunk data.");
        return mca;
    }

    static void Hash(IncrementalHash hash, Chunk c)
    {
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(head, c.Index);
        head[4] = c.Type; head[5] = c.Mode;
        hash.AppendData(head);
        hash.AppendData(c.Data);
    }

    // ------------------------------------------------------------ region file format

    /// <summary>Lists the chunks; zlib/gzip chunks come back decompressed. Null if anything looks wrong.</summary>
    static List<Chunk>? Read(byte[] f)
    {
        if (f.Length < 2 * Sector) return null;
        var list = new List<Chunk>();
        for (var i = 0; i < 1024; i++)
        {
            var loc = (f[i * 4] << 16) | (f[i * 4 + 1] << 8) | f[i * 4 + 2];
            var sectors = f[i * 4 + 3];
            if (loc == 0 && sectors == 0) continue;
            long off = (long)loc * Sector;
            if (loc < 2 || off + 5 > f.Length) return null;
            var len = BinaryPrimitives.ReadInt32BigEndian(f.AsSpan((int)off, 4));
            if (len < 1 || off + 4 + len > f.Length) return null;
            var type = f[off + 4];
            var payload = f.AsSpan((int)off + 5, len - 1);

            var external = (type & 0x80) != 0;
            if (!external && (type is 1 or 2) && sectors <= MaxRepackSectors)
            {
                try
                {
                    using var src = new MemoryStream(f, (int)off + 5, len - 1);
                    using Stream z = type == 2 ? new ZLibStream(src, CompressionMode.Decompress) : new GZipStream(src, CompressionMode.Decompress);
                    var raw = new MemoryStream();
                    z.CopyTo(raw);
                    list.Add(new Chunk(i, type, ModeInflated, raw.ToArray()));
                    continue;
                }
                catch { return null; }   // a damaged chunk: keep the whole file as it is
            }
            list.Add(new Chunk(i, type, ModeStored, payload.ToArray()));
        }
        return list;
    }

    static byte[] Write(byte[] timestamps, List<Chunk> chunks)
    {
        var body = new MemoryStream();
        var header = new byte[2 * Sector];
        timestamps.CopyTo(header, Sector);
        var sector = 2;
        foreach (var c in chunks.OrderBy(c => c.Index))
        {
            var payload = c.Mode == ModeStored ? c.Data : Compress(c.Type, c.Data);
            var total = 5 + payload.Length;
            var sectors = (total + Sector - 1) / Sector;
            if (sectors > 255) throw new InvalidDataException($"Chunk {c.Index} is too large to rebuild.");

            Span<byte> head = stackalloc byte[5];
            BinaryPrimitives.WriteInt32BigEndian(head, payload.Length + 1);
            head[4] = c.Type;
            body.Write(head);
            body.Write(payload);
            body.Write(new byte[sectors * Sector - total]);   // pad to whole sectors

            header[c.Index * 4] = (byte)(sector >> 16);
            header[c.Index * 4 + 1] = (byte)(sector >> 8);
            header[c.Index * 4 + 2] = (byte)sector;
            header[c.Index * 4 + 3] = (byte)sectors;
            sector += sectors;
        }
        var result = new byte[header.Length + body.Length];
        header.CopyTo(result, 0);
        body.GetBuffer().AsSpan(0, (int)body.Length).CopyTo(result.AsSpan(header.Length));
        return result;
    }

    static byte[] Compress(byte type, byte[] raw)
    {
        var ms = new MemoryStream();
        using (Stream z = type == 2 ? new ZLibStream(ms, CompressionLevel.Optimal, true) : new GZipStream(ms, CompressionLevel.Optimal, true))
            z.Write(raw);
        return ms.ToArray();
    }

    static void BrotliCompress(ReadOnlySpan<byte> data, Stream output)
    {
        using var encoder = new BrotliEncoder(Quality, Window);
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            while (true)
            {
                var status = encoder.Compress(data, buffer, out var consumed, out var written, isFinalBlock: true);
                output.Write(buffer, 0, written);
                data = data[consumed..];
                if (status == OperationStatus.Done) return;
                if (status == OperationStatus.InvalidData) throw new InvalidDataException("Brotli compression failed.");
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
