using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace PDFPlus.Core;

/// <summary>
/// Shrinks a TrueType font for embedding. Glyph ids are kept, but every glyph that isn't needed is emptied,
/// the cmap only lists the requested characters, and tables PDF renderers ignore are dropped.
/// A 1.6 MB Windows font typically ends up around 30 KB.
/// </summary>
internal static class TrueTypeSubsetter
{
    private static readonly string[] CopiedTables = ["head", "hhea", "maxp", "hmtx", "OS/2", "name", "cvt ", "fpgm", "prep", "gasp"];

    /// <summary>Returns null for fonts without TrueType outlines (CFF-based .otf), bad face indices or malformed data.</summary>
    public static byte[]? Subset(byte[] data, int faceIndex, IEnumerable<int> codepoints)
    {
        try
        {
            return SubsetCore(data, faceIndex, codepoints);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidDataException or OverflowException)
        {
            return null;
        }
    }

    private static byte[]? SubsetCore(byte[] data, int faceIndex, IEnumerable<int> codepoints)
    {
        var start = 0;
        if (Tag(data, 0) == "ttcf")
        {
            var faces = (int)U32(data, 8);
            if (faceIndex < 0 || faceIndex >= faces) return null;
            start = checked((int)U32(data, 12 + 4 * faceIndex));
        }

        var tables = new Dictionary<string, (int Offset, int Length)>();
        int numTables = U16(data, start + 4);
        for (var i = 0; i < numTables; i++)
        {
            var record = start + 12 + 16 * i;
            var offset = checked((int)U32(data, record + 8));
            var length = checked((int)U32(data, record + 12));
            if (offset < 0 || length < 0 || (long)offset + length > data.Length) throw new InvalidDataException();
            tables[Tag(data, record)] = (offset, length);
        }
        foreach (var required in new[] { "head", "hhea", "maxp", "hmtx", "cmap", "loca", "glyf" })
            if (!tables.ContainsKey(required)) return null;

        var head = Table(data, tables, "head");
        var longLoca = I16(head, 50) != 0;
        int numGlyphs = U16(Table(data, tables, "maxp"), 4);
        var loca = Table(data, tables, "loca");
        var glyf = Table(data, tables, "glyf");

        var offsets = new int[numGlyphs + 1];
        for (var g = 0; g <= numGlyphs; g++)
            offsets[g] = longLoca ? checked((int)U32(loca, g * 4)) : U16(loca, g * 2) * 2;

        // Character -> glyph for the characters we need.
        var cmap = Table(data, tables, "cmap");
        var mapped = new SortedDictionary<int, int>();
        foreach (var cp in codepoints.Distinct())
        {
            var glyph = LookupGlyph(cmap, cp);
            if (glyph > 0 && glyph < numGlyphs) mapped[cp] = glyph;
        }

        // Keep .notdef, the mapped glyphs, and every glyph a composite glyph is built from.
        var keep = new HashSet<int>();
        var pending = new Stack<int>(mapped.Values.Append(0));
        while (pending.Count > 0)
        {
            var g = pending.Pop();
            if (!keep.Add(g) || !ValidGlyph(offsets, g, glyf.Length)) continue;
            var p = offsets[g];
            if (offsets[g + 1] - p < 10 || I16(glyf, p) >= 0) continue;
            p += 10;
            while (true)
            {
                int flags = U16(glyf, p);
                int component = U16(glyf, p + 2);
                if (component < numGlyphs) pending.Push(component);
                p += 4 + ((flags & 0x1) != 0 ? 4 : 2);
                if ((flags & 0x8) != 0) p += 2;
                else if ((flags & 0x40) != 0) p += 4;
                else if ((flags & 0x80) != 0) p += 8;
                if ((flags & 0x20) == 0) break;
            }
        }

        using var glyfOut = new MemoryStream();
        var locaOut = new byte[(numGlyphs + 1) * 4];
        for (var g = 0; g < numGlyphs; g++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(locaOut.AsSpan(g * 4), (uint)glyfOut.Length);
            if (!keep.Contains(g) || !ValidGlyph(offsets, g, glyf.Length)) continue;
            glyfOut.Write(glyf.Slice(offsets[g], offsets[g + 1] - offsets[g]));
            while (glyfOut.Length % 4 != 0) glyfOut.WriteByte(0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(locaOut.AsSpan(numGlyphs * 4), (uint)glyfOut.Length);

        var output = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var tag in CopiedTables)
            if (tables.ContainsKey(tag)) output[tag] = Table(data, tables, tag).ToArray();
        var newHead = output["head"];
        BinaryPrimitives.WriteUInt32BigEndian(newHead.AsSpan(8), 0);
        BinaryPrimitives.WriteInt16BigEndian(newHead.AsSpan(50), 1);
        output["loca"] = locaOut;
        output["glyf"] = glyfOut.ToArray();
        output["cmap"] = BuildCmap(mapped);
        if (tables.TryGetValue("post", out var post) && post.Length >= 32)
        {
            // Version 3 drops the (large) glyph name list but keeps italic angle, underline and pitch.
            var newPost = data.AsSpan(post.Offset, 32).ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(newPost, 0x00030000);
            output["post"] = newPost;
        }
        return Assemble(output);
    }

    private static bool ValidGlyph(int[] offsets, int glyph, int glyfLength) =>
        offsets[glyph] >= 0 && offsets[glyph + 1] >= offsets[glyph] && offsets[glyph + 1] <= glyfLength;

    // ---------------------------------------------------------------- cmap

    private static int LookupGlyph(ReadOnlySpan<byte> cmap, int codepoint)
    {
        int count = U16(cmap, 2);
        int best = -1, bestScore = 0;
        for (var i = 0; i < count; i++)
        {
            int platform = U16(cmap, 4 + i * 8), encoding = U16(cmap, 6 + i * 8);
            var offset = (int)U32(cmap, 8 + i * 8);
            if (offset + 4 > cmap.Length) continue;
            int format = U16(cmap, offset);
            var score = (platform, encoding, format) switch
            {
                (3, 10, 12) => 5,
                (0, _, 12) => 4,
                (3, 1, 4) => 3,
                (0, _, 4) => 2,
                (3, 0, 4) => 1,
                _ => 0,
            };
            if (score > bestScore)
            {
                bestScore = score;
                best = offset;
            }
        }
        if (best < 0) return 0;

        var sub = cmap[best..];
        if (U16(sub, 0) == 12)
        {
            var groups = (int)U32(sub, 12);
            for (var i = 0; i < groups; i++)
            {
                var g = 16 + i * 12;
                long first = U32(sub, g), last = U32(sub, g + 4);
                if (codepoint >= first && codepoint <= last) return (int)(U32(sub, g + 8) + (codepoint - first));
            }
            return 0;
        }

        // Format 4. Symbol fonts (3,0) put their characters at U+F000..U+F0FF.
        var lookup = bestScore == 1 && codepoint < 0x100 ? codepoint + 0xF000 : codepoint;
        if (lookup > 0xFFFF) return 0;
        int segX2 = U16(sub, 6);
        for (var i = 0; i < segX2 / 2; i++)
        {
            int end = U16(sub, 14 + i * 2);
            if (lookup > end) continue;
            int first = U16(sub, 16 + segX2 + i * 2);
            if (lookup < first) return 0;
            int delta = U16(sub, 16 + segX2 * 2 + i * 2);
            var rangeOffsetPosition = 16 + segX2 * 3 + i * 2;
            int rangeOffset = U16(sub, rangeOffsetPosition);
            if (rangeOffset == 0) return (lookup + delta) & 0xFFFF;
            int glyph = U16(sub, rangeOffsetPosition + rangeOffset + (lookup - first) * 2);
            return glyph == 0 ? 0 : (glyph + delta) & 0xFFFF;
        }
        return 0;
    }

    private static byte[] BuildCmap(SortedDictionary<int, int> mapped)
    {
        var runs = new List<(int First, int Last, int Glyph)>();
        foreach (var (cp, glyph) in mapped)
        {
            if (runs.Count > 0 && runs[^1].Last + 1 == cp && runs[^1].Glyph + (cp - runs[^1].First) == glyph && (cp <= 0xFFFF) == (runs[^1].First <= 0xFFFF))
                runs[^1] = (runs[^1].First, cp, runs[^1].Glyph);
            else
                runs.Add((cp, cp, glyph));
        }

        // Format 4 for the Basic Multilingual Plane (ends with the required 0xFFFF segment).
        var bmp = runs.Where(r => r.Last <= 0xFFFF).ToList();
        bmp.Add((0xFFFF, 0xFFFF, 0));
        int segCount = bmp.Count;
        var entrySelector = (int)Math.Floor(Math.Log2(segCount));
        var searchRange = 2 << entrySelector;
        var format4 = new byte[16 + segCount * 8];
        var s = format4.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(s, 4);
        BinaryPrimitives.WriteUInt16BigEndian(s[2..], (ushort)format4.Length);
        BinaryPrimitives.WriteUInt16BigEndian(s[6..], (ushort)(segCount * 2));
        BinaryPrimitives.WriteUInt16BigEndian(s[8..], (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(s[10..], (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(s[12..], (ushort)(segCount * 2 - searchRange));
        for (var i = 0; i < segCount; i++)
        {
            var (first, last, glyph) = bmp[i];
            var delta = first == 0xFFFF ? 1 : (glyph - first) & 0xFFFF;
            BinaryPrimitives.WriteUInt16BigEndian(s[(14 + i * 2)..], (ushort)last);
            BinaryPrimitives.WriteUInt16BigEndian(s[(16 + segCount * 2 + i * 2)..], (ushort)first);
            BinaryPrimitives.WriteUInt16BigEndian(s[(16 + segCount * 4 + i * 2)..], (ushort)delta);
        }

        var astral = runs.Where(r => r.First > 0xFFFF).ToList();
        byte[]? format12 = null;
        if (astral.Count > 0)
        {
            format12 = new byte[16 + astral.Count * 12];
            var t = format12.AsSpan();
            BinaryPrimitives.WriteUInt16BigEndian(t, 12);
            BinaryPrimitives.WriteUInt32BigEndian(t[4..], (uint)format12.Length);
            BinaryPrimitives.WriteUInt32BigEndian(t[12..], (uint)astral.Count);
            for (var i = 0; i < astral.Count; i++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(t[(16 + i * 12)..], (uint)astral[i].First);
                BinaryPrimitives.WriteUInt32BigEndian(t[(20 + i * 12)..], (uint)astral[i].Last);
                BinaryPrimitives.WriteUInt32BigEndian(t[(24 + i * 12)..], (uint)astral[i].Glyph);
            }
        }

        var subtables = format12 == null ? 1 : 2;
        var headerLength = 4 + subtables * 8;
        using var stream = new MemoryStream();
        var header = new byte[headerLength];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)subtables);
        WriteEncodingRecord(header, 4, 1, (uint)headerLength);
        if (format12 != null) WriteEncodingRecord(header, 12, 10, (uint)(headerLength + format4.Length));
        stream.Write(header);
        stream.Write(format4);
        if (format12 != null) stream.Write(format12);
        return stream.ToArray();
    }

    private static void WriteEncodingRecord(byte[] header, int at, ushort encoding, uint offset)
    {
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(at), 3);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(at + 2), encoding);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(at + 4), offset);
    }

    // ---------------------------------------------------------------- file assembly

    private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        var count = tables.Count;
        var entrySelector = (int)Math.Floor(Math.Log2(count));
        var searchRange = 16 << entrySelector;
        var directoryLength = 12 + 16 * count;
        var total = directoryLength + tables.Values.Sum(t => (t.Length + 3) & ~3);
        var file = new byte[total];
        var span = file.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], (ushort)(16 * count - searchRange));

        var position = directoryLength;
        var headOffset = -1;
        var i = 0;
        foreach (var (tag, body) in tables)
        {
            var record = 12 + 16 * i++;
            Encoding.ASCII.GetBytes(tag, span[record..]);
            body.CopyTo(span[position..]);
            BinaryPrimitives.WriteUInt32BigEndian(span[(record + 4)..], Checksum(span.Slice(position, (body.Length + 3) & ~3)));
            BinaryPrimitives.WriteUInt32BigEndian(span[(record + 8)..], (uint)position);
            BinaryPrimitives.WriteUInt32BigEndian(span[(record + 12)..], (uint)body.Length);
            if (tag == "head") headOffset = position;
            position += (body.Length + 3) & ~3;
        }
        if (headOffset >= 0)
            BinaryPrimitives.WriteUInt32BigEndian(span[(headOffset + 8)..], unchecked(0xB1B0AFBA - Checksum(span)));
        return file;
    }

    private static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i + 4 <= data.Length; i += 4) sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(data[i..]));
        return sum;
    }

    // ---------------------------------------------------------------- readers

    private static ReadOnlySpan<byte> Table(byte[] data, Dictionary<string, (int Offset, int Length)> tables, string tag) =>
        data.AsSpan(tables[tag].Offset, tables[tag].Length);

    private static string Tag(ReadOnlySpan<byte> data, int at) => Encoding.ASCII.GetString(data.Slice(at, 4));
    private static ushort U16(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
    private static short I16(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadInt16BigEndian(data[at..]);
    private static uint U32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
}
