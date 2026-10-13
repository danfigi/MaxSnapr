using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MaxSnapr.Video;

/// <summary>
/// Animated GIF89a writer: per-frame median-cut palette (255 colours + transparent), unchanged pixels
/// become transparent so later frames only store what moved, and standard variable-width LZW.
/// </summary>
public sealed class GifEncoder : IDisposable
{
    readonly Stream _s;
    readonly int _w, _h;
    byte[]? _prev;
    bool _headerWritten;
    readonly int _loop;

    public GifEncoder(Stream s, int width, int height, int loop = 0)
    {
        _s = s;
        _w = width;
        _h = height;
        _loop = loop;
    }

    /// <summary>Adds one BGRA frame (width*height*4 bytes). Delay in hundredths of a second.</summary>
    public void AddFrame(byte[] bgra, int delayCs, bool dither = false)
    {
        if (!_headerWritten) WriteHeader();
        // Pixels identical to the previous frame are encoded as transparent to shrink the file.
        var changed = new bool[_w * _h];
        int minX = _w, minY = _h, maxX = -1, maxY = -1;
        for (int y = 0; y < _h; y++)
            for (int x = 0; x < _w; x++)
            {
                int i = (y * _w + x) * 4;
                bool c = _prev == null || Math.Abs(bgra[i] - _prev[i]) > 2 || Math.Abs(bgra[i + 1] - _prev[i + 1]) > 2 || Math.Abs(bgra[i + 2] - _prev[i + 2]) > 2;
                changed[y * _w + x] = c;
                if (c) { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
            }
        if (maxX < 0) { minX = minY = 0; maxX = maxY = 0; } // nothing changed: 1px frame keeps the delay
        int fw = maxX - minX + 1, fh = maxY - minY + 1;

        var hist = new int[32768];
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                if (!changed[y * _w + x]) continue;
                int i = (y * _w + x) * 4;
                hist[Key(bgra[i + 2], bgra[i + 1], bgra[i])]++;
            }
        var palette = MedianCut(hist, 255);
        var lookup = new byte[32768];
        var cache = new short[32768];
        Array.Fill(cache, (short)-1);

        int Nearest(int key)
        {
            if (cache[key] >= 0) return cache[key];
            int r = (key >> 10) << 3, g = ((key >> 5) & 31) << 3, b = (key & 31) << 3;
            int best = 0, bd = int.MaxValue;
            for (int p = 0; p < palette.Count; p++)
            {
                var c = palette[p];
                int d = (c.r - r) * (c.r - r) * 3 + (c.g - g) * (c.g - g) * 4 + (c.b - b) * (c.b - b) * 2;
                if (d < bd) { bd = d; best = p; }
            }
            cache[key] = (short)best;
            return best;
        }

        var indices = new byte[fw * fh];
        const int transparent = 255;
        var err = dither ? new float[(fw + 2) * 3 * 2] : null;
        for (int y = 0; y < fh; y++)
            for (int x = 0; x < fw; x++)
            {
                int gx = x + minX, gy = y + minY;
                if (!changed[gy * _w + gx] && _prev != null) { indices[y * fw + x] = transparent; continue; }
                int i = (gy * _w + gx) * 4;
                indices[y * fw + x] = (byte)Nearest(Key(bgra[i + 2], bgra[i + 1], bgra[i]));
            }

        // Graphic control extension: disposal 1 (keep), transparency on.
        _s.Write(new byte[] { 0x21, 0xF9, 4, (1 << 2) | 1, (byte)delayCs, (byte)(delayCs >> 8), transparent, 0 });
        // Image descriptor with a local 256-entry colour table.
        _s.WriteByte(0x2C);
        W16(minX); W16(minY); W16(fw); W16(fh);
        _s.WriteByte(0x80 | 7);
        for (int p = 0; p < 256; p++)
        {
            var c = p < palette.Count ? palette[p] : (r: 0, g: 0, b: 0);
            _s.WriteByte((byte)c.r); _s.WriteByte((byte)c.g); _s.WriteByte((byte)c.b);
        }
        Lzw(indices, 8);
        _prev = bgra;
    }

    static int Key(byte r, byte g, byte b) => ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);

    void WriteHeader()
    {
        _headerWritten = true;
        _s.Write("GIF89a"u8);
        W16(_w); W16(_h);
        _s.WriteByte(0x70); // no global colour table, 8-bit colour resolution
        _s.WriteByte(0); _s.WriteByte(0);
        // NETSCAPE2.0 loop extension.
        _s.Write(new byte[] { 0x21, 0xFF, 11 });
        _s.Write("NETSCAPE2.0"u8);
        _s.Write(new byte[] { 3, 1, (byte)_loop, (byte)(_loop >> 8), 0 });
    }

    void W16(int v) { _s.WriteByte((byte)v); _s.WriteByte((byte)(v >> 8)); }

    static List<(int r, int g, int b)> MedianCut(int[] hist, int max)
    {
        var colors = new List<int>();
        for (int k = 0; k < hist.Length; k++) if (hist[k] > 0) colors.Add(k);
        if (colors.Count == 0) return new List<(int, int, int)> { (0, 0, 0) };
        var boxes = new List<List<int>> { colors };
        while (boxes.Count < max)
        {
            // Split the box with the largest channel range weighted by pixel count.
            int bi = -1; long bestScore = 0; int bestCh = 0;
            for (int i = 0; i < boxes.Count; i++)
            {
                var b = boxes[i];
                if (b.Count < 2) continue;
                for (int ch = 0; ch < 3; ch++)
                {
                    int lo = 31, hi = 0; long cnt = 0;
                    foreach (var k in b) { int v = Ch(k, ch); if (v < lo) lo = v; if (v > hi) hi = v; cnt += hist[k]; }
                    long score = (hi - lo) * (long)Math.Sqrt(cnt);
                    if (score > bestScore) { bestScore = score; bi = i; bestCh = ch; }
                }
            }
            if (bi < 0) break;
            var box = boxes[bi];
            box.Sort((a, c) => Ch(a, bestCh).CompareTo(Ch(c, bestCh)));
            long total = box.Sum(k => (long)hist[k]), acc = 0;
            int split = 1;
            for (int i = 0; i < box.Count - 1; i++) { acc += hist[box[i]]; if (acc * 2 >= total) { split = i + 1; break; } }
            boxes[bi] = box.GetRange(0, split);
            boxes.Add(box.GetRange(split, box.Count - split));
        }
        return boxes.Select(b =>
        {
            long r = 0, g = 0, bl = 0, n = 0;
            foreach (var k in b) { long w = hist[k]; r += ((k >> 10) << 3 | 4) * w; g += (((k >> 5) & 31) << 3 | 4) * w; bl += ((k & 31) << 3 | 4) * w; n += w; }
            return ((int)(r / n), (int)(g / n), (int)(bl / n));
        }).ToList();
    }

    static int Ch(int k, int ch) => ch == 0 ? k >> 10 : ch == 1 ? (k >> 5) & 31 : k & 31;

    void Lzw(byte[] data, int minCodeSize)
    {
        _s.WriteByte((byte)minCodeSize);
        int clear = 1 << minCodeSize, eoi = clear + 1;
        var dict = new Dictionary<int, int>();
        int codeSize = minCodeSize + 1, next = eoi + 1;
        var block = new List<byte>(255);
        int bitBuf = 0, bitCount = 0;

        void Emit(int code)
        {
            bitBuf |= code << bitCount;
            bitCount += codeSize;
            while (bitCount >= 8)
            {
                block.Add((byte)bitBuf);
                bitBuf >>= 8;
                bitCount -= 8;
                if (block.Count == 255) { _s.WriteByte(255); _s.Write(block.ToArray()); block.Clear(); }
            }
        }

        Emit(clear);
        int prefix = data.Length > 0 ? data[0] : 0;
        for (int i = 1; i < data.Length; i++)
        {
            int c = data[i];
            int key = (prefix << 8) | c;
            if (dict.TryGetValue(key, out var code)) { prefix = code; continue; }
            Emit(prefix);
            if (next < 4096)
            {
                dict[key] = next++;
                if (next > (1 << codeSize) && codeSize < 12) codeSize++;
            }
            else
            {
                Emit(clear);
                dict.Clear();
                codeSize = minCodeSize + 1;
                next = eoi + 1;
            }
            prefix = c;
        }
        Emit(prefix);
        Emit(eoi);
        if (bitCount > 0) { block.Add((byte)bitBuf); }
        if (block.Count > 0) { _s.WriteByte((byte)block.Count); _s.Write(block.ToArray()); }
        _s.WriteByte(0);
    }

    public void Dispose()
    {
        if (!_headerWritten) WriteHeader();
        _s.WriteByte(0x3B);
        _s.Flush();
    }
}
