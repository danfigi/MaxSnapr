using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MaxSnapr.Services;

namespace MaxSnapr.Video;

/// <summary>Camera background choice, stored in <see cref="VideoSettings.WebcamBackground"/>.</summary>
public static class CameraBackgrounds
{
    public const string None = "none";
    public const string BlurLight = "blur-light";
    public const string BlurStrong = "blur-strong";

    /// <summary>Original backgrounds drawn by code.</summary>
    public static readonly (string Id, string Name)[] Presets =
    {
        ("preset:estudio", "Estudio"),
        ("preset:atardecer", "Atardecer"),
        ("preset:bokeh", "Luces nocturnas"),
        ("preset:menta", "Menta"),
        ("preset:maxsnapr", "MaxSnapr"),
    };

    public static string Describe(string? id) => id switch
    {
        null or None => "Sin fondo",
        BlurLight => "Desenfoque suave",
        BlurStrong => "Desenfoque intenso",
        _ when id.StartsWith("image:") => "Imagen: " + Path.GetFileNameWithoutExtension(id[6..]),
        _ => Presets.FirstOrDefault(p => p.Id == id).Name ?? "Sin fondo",
    };

    public static bool NeedsSegmentation(string? id) => id is not (null or None);

    /// <summary>Draws a preset background at the given size.</summary>
    public static Bitmap RenderPreset(string id, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var all = new Rectangle(0, 0, w, h);
        switch (id)
        {
            case "preset:atardecer":
                using (var b = new LinearGradientBrush(all, Color.FromArgb(0xFF, 0xB3, 0x6B), Color.FromArgb(0x6A, 0x3D, 0x9A), 90f))
                {
                    b.InterpolationColors = new ColorBlend { Colors = new[] { Color.FromArgb(0xFF, 0xC9, 0x7A), Color.FromArgb(0xF2, 0x6B, 0x6B), Color.FromArgb(0x5B, 0x3A, 0x8E) }, Positions = new[] { 0f, 0.55f, 1f } };
                    g.FillRectangle(b, all);
                }
                using (var sun = new SolidBrush(Color.FromArgb(110, 255, 236, 180)))
                    g.FillEllipse(sun, w * 0.62f, h * 0.38f, h * 0.36f, h * 0.36f);
                break;
            case "preset:bokeh":
                using (var b = new LinearGradientBrush(all, Color.FromArgb(0x10, 0x18, 0x30), Color.FromArgb(0x24, 0x10, 0x34), 60f)) g.FillRectangle(b, all);
                var rnd = new Random(7);
                for (int i = 0; i < 38; i++)
                {
                    float d = h * (0.06f + (float)rnd.NextDouble() * 0.16f);
                    var c = new[] { Color.FromArgb(0xFF, 0xC8, 0x6B), Color.FromArgb(0x7E, 0xC8, 0xFF), Color.FromArgb(0xFF, 0x8F, 0xB1) }[rnd.Next(3)];
                    using var dot = new SolidBrush(Color.FromArgb(40 + rnd.Next(70), c));
                    g.FillEllipse(dot, (float)rnd.NextDouble() * w - d / 2, (float)rnd.NextDouble() * h - d / 2, d, d);
                }
                break;
            case "preset:menta":
                using (var b = new LinearGradientBrush(all, Color.FromArgb(0xD8, 0xF5, 0xEA), Color.FromArgb(0x7C, 0xC9, 0xB0), 120f)) g.FillRectangle(b, all);
                break;
            case "preset:maxsnapr":
                using (var b = new LinearGradientBrush(all, Color.FromArgb(0xFF, 0xB1, 0x3D), Color.FromArgb(0xEE, 0x4B, 0x5E), 45f)) g.FillRectangle(b, all);
                using (var paw = AppIcon.Render(Math.Max(16, h / 5)))
                using (var ia = new ImageAttributes())
                {
                    ia.SetColorMatrix(new ColorMatrix { Matrix33 = 0.35f });
                    g.DrawImage(paw, new Rectangle(w - paw.Width - h / 20, h / 20, paw.Width, paw.Height), 0, 0, paw.Width, paw.Height, GraphicsUnit.Pixel, ia);
                }
                break;
            default: // estudio: soft light behind the speaker
                g.Clear(Color.FromArgb(0x3C, 0x48, 0x58));
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(-w * 0.2f, -h * 0.35f, w * 1.4f, h * 1.5f);
                    using var pb = new PathGradientBrush(path) { CenterColor = Color.FromArgb(0x9F, 0xB2, 0xC6), SurroundColors = new[] { Color.FromArgb(0x3C, 0x48, 0x58) } };
                    g.FillRectangle(pb, all);
                }
                break;
        }
        return bmp;
    }

    /// <summary>Loads a user image, cropped to cover w×h like a wallpaper.</summary>
    public static Bitmap? LoadImage(string path, int w, int h)
    {
        try
        {
            using var src = new Bitmap(path);
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float s = Math.Max(w / (float)src.Width, h / (float)src.Height);
            g.DrawImage(src, (w - src.Width * s) / 2, (h - src.Height * s) / 2, src.Width * s, src.Height * s);
            return bmp;
        }
        catch (Exception ex) { Log.Error("Fondo de la cámara", ex); return null; }
    }
}

/// <summary>PP-HumanSeg (OpenCV Model Zoo, Apache-2.0): 192×192 RGB in [-1, 1] → person probability.</summary>
public sealed class PersonSegmenter : IDisposable
{
    public const int Size = 192;
    readonly InferenceSession _session;
    readonly string _input;
    float[]? _previous;

    PersonSegmenter()
    {
        using var s = typeof(PersonSegmenter).Assembly.GetManifestResourceStream("pphumanseg.onnx") ?? throw new InvalidOperationException("Falta el modelo de segmentación");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        // Two threads keep the recording smooth while the model runs.
        var opts = new SessionOptions { IntraOpNumThreads = 2, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(ms.ToArray(), opts);
        _input = _session.InputMetadata.Keys.First();
    }

    static readonly Lazy<PersonSegmenter?> Shared = new(() =>
    {
        try { return new PersonSegmenter(); }
        catch (Exception ex) { Log.Error("Modelo de segmentación", ex); return null; }
    });

    /// <summary>One model per process; null when it cannot run on this machine.</summary>
    public static PersonSegmenter? Instance => Shared.Value;

    /// <summary>Person probability per cell of a 192×192 grid, smoothed over time to avoid flicker.</summary>
    public unsafe float[] Run(Bitmap small)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 3, Size, Size });
        var data = tensor.Buffer.Span;
        var bd = small.LockBits(new Rectangle(0, 0, Size, Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            const int plane = Size * Size;
            for (int y = 0; y < Size; y++)
            {
                byte* row = (byte*)bd.Scan0 + y * bd.Stride;
                for (int x = 0; x < Size; x++)
                {
                    int i = y * Size + x;
                    // Pixels are BGRA; the model wants RGB planes.
                    data[i] = row[x * 4 + 2] / 127.5f - 1f;
                    data[plane + i] = row[x * 4 + 1] / 127.5f - 1f;
                    data[2 * plane + i] = row[x * 4] / 127.5f - 1f;
                }
            }
        }
        finally { small.UnlockBits(bd); }

        float[] mask = new float[Size * Size];
        lock (_session)
        {
            using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_input, tensor) });
            var o = results.First().AsTensor<float>();
            var span = o is DenseTensor<float> dt ? dt.Buffer.Span : o.ToArray().AsSpan();
            int plane = Size * Size;
            // The output is two channels (background, person); accept probabilities or logits.
            bool probabilities = Math.Abs(span[0] + span[plane] - 1f) < 1e-3f;
            for (int i = 0; i < plane; i++)
            {
                float bg = span[i], fg = span[plane + i];
                mask[i] = probabilities ? fg : 1f / (1f + MathF.Exp(bg - fg));
            }
        }
        var prev = _previous;
        if (prev != null)
            for (int i = 0; i < mask.Length; i++) mask[i] = mask[i] * 0.65f + prev[i] * 0.35f;
        _previous = mask;
        return mask;
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>Replaces or blurs the background of camera frames using a person mask.</summary>
public static class BackgroundCompositor
{
    /// <summary>Writes the mask and every background applied to <paramref name="input"/>; returns timings.</summary>
    public static string SegmentTest(string input, string outDir)
    {
        var seg = PersonSegmenter.Instance ?? throw new InvalidOperationException("El modelo no se pudo cargar");
        using var src = new Bitmap(input);
        using var frame = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(frame)) g.DrawImage(src, 0, 0, src.Width, src.Height);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        float[] mask = Array.Empty<float>();
        using (var small = Downscale(frame)) { for (int i = 0; i < 10; i++) mask = seg.Run(small); }
        double inferMs = sw.Elapsed.TotalMilliseconds / 10;
        using (var m = new Bitmap(PersonSegmenter.Size, PersonSegmenter.Size, PixelFormat.Format32bppRgb))
        {
            for (int y = 0; y < PersonSegmenter.Size; y++)
                for (int x = 0; x < PersonSegmenter.Size; x++) { int v = (int)(mask[y * PersonSegmenter.Size + x] * 255); m.SetPixel(x, y, Color.FromArgb(v, v, v)); }
            m.Save(Path.Combine(outDir, "mask.png"), ImageFormat.Png);
        }
        var sb = new System.Text.StringBuilder().AppendLine($"inferencia {inferMs:0.0} ms");
        // Per-step cost at camera resolution.
        using (var hd = new Bitmap(1280, 720, PixelFormat.Format32bppRgb))
        {
            using (var g = Graphics.FromImage(hd)) g.DrawImage(frame, 0, 0, 1280, 720);
            double T(Action a) { var t = System.Diagnostics.Stopwatch.StartNew(); for (int i = 0; i < 10; i++) a(); return t.Elapsed.TotalMilliseconds / 10; }
            sb.AppendLine($"720p: reducir para el modelo {T(() => Downscale(hd).Dispose()):0.0} ms");
            sb.AppendLine($"720p: desenfoque {T(() => Blur(hd, true).Dispose()):0.0} ms");
            using var blurred = Blur(hd, true);
            using var preset = CameraBackgrounds.RenderPreset("preset:estudio", 1280, 720);
            sb.AppendLine($"720p: componer con desenfoque {T(() => Compose(hd, blurred, mask)):0.0} ms");
            sb.AppendLine($"720p: componer con imagen {T(() => Compose(hd, preset, mask)):0.0} ms");
        }
        foreach (var id in new[] { CameraBackgrounds.BlurLight, CameraBackgrounds.BlurStrong }.Concat(CameraBackgrounds.Presets.Select(p => p.Id)))
        {
            using var copy = (Bitmap)frame.Clone();
            sw.Restart();
            using (var bg = id.StartsWith("blur") ? Blur(copy, id == CameraBackgrounds.BlurStrong) : CameraBackgrounds.RenderPreset(id, copy.Width, copy.Height))
                Compose(copy, bg, mask);
            sb.AppendLine($"{id}: {sw.Elapsed.TotalMilliseconds:0.0} ms");
            copy.Save(Path.Combine(outDir, id.Replace(':', '-') + ".png"), ImageFormat.Png);
        }
        return sb.ToString();
    }

    /// <summary>192×192 copy of the frame for the segmenter.</summary>
    public static Bitmap Downscale(Bitmap frame)
    {
        var small = new Bitmap(PersonSegmenter.Size, PersonSegmenter.Size, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(small);
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.DrawImage(frame, 0, 0, PersonSegmenter.Size, PersonSegmenter.Size);
        return small;
    }

    /// <summary>
    /// Small blurred copy of the frame (about 1/14 or 1/28 of its size). <see cref="Compose"/> enlarges it
    /// on the fly, which is much cheaper than scaling a full-size bitmap every frame.
    /// </summary>
    public static Bitmap Blur(Bitmap frame, bool strong)
    {
        int f = strong ? 28 : 14;
        int sw = Math.Max(4, frame.Width / f), sh = Math.Max(4, frame.Height / f);
        var small = new Bitmap(sw, sh, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(small))
        using (var ia = new ImageAttributes())
        {
            // Mirrored edges stop GDI+ from blending transparent black into the borders.
            ia.SetWrapMode(WrapMode.TileFlipXY);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(frame, new Rectangle(0, 0, sw, sh), 0, 0, frame.Width, frame.Height, GraphicsUnit.Pixel, ia);
        }
        BoxBlur(small, strong ? 3 : 2);
        return small;
    }

    static unsafe void BoxBlur(Bitmap bmp, int passes)
    {
        int w = bmp.Width, h = bmp.Height;
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppRgb);
        try
        {
            var tmp = new byte[w * h * 4];
            for (int p = 0; p < passes; p++)
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    byte* b = (byte*)bd.Scan0;
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                            for (int c = 0; c < 3; c++)
                            {
                                int sum = 0, n = 0;
                                for (int k = -1; k <= 1; k++)
                                {
                                    int xx = pass == 0 ? Math.Clamp(x + k, 0, w - 1) : x, yy = pass == 1 ? Math.Clamp(y + k, 0, h - 1) : y;
                                    sum += b[yy * bd.Stride + xx * 4 + c]; n++;
                                }
                                tmp[(y * w + x) * 4 + c] = (byte)(sum / n);
                            }
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                            for (int c = 0; c < 3; c++) b[y * bd.Stride + x * 4 + c] = tmp[(y * w + x) * 4 + c];
                }
            }
        }
        finally { bmp.UnlockBits(bd); }
    }

    /// <summary>
    /// frame = person·frame + (1 − person)·background. The mask is sampled bilinearly with its edge tightened;
    /// a background smaller than the frame (a blur) is enlarged bilinearly at the same time.
    /// </summary>
    public static unsafe void Compose(Bitmap frame, Bitmap background, float[] mask)
    {
        int w = frame.Width, h = frame.Height, n = PersonSegmenter.Size;
        int bw = background.Width, bh = background.Height;
        bool scaled = bw != w || bh != h;
        var fd = frame.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppRgb);
        var bd = background.LockBits(new Rectangle(0, 0, bw, bh), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            var x0 = new int[w];
            var fx = new float[w];
            var bx0 = new int[w];
            var bfx = new float[w];
            for (int x = 0; x < w; x++)
            {
                float mx = Math.Clamp((x + 0.5f) * n / w - 0.5f, 0, n - 1.001f);
                x0[x] = (int)mx; fx[x] = mx - x0[x];
                float sx = Math.Clamp((x + 0.5f) * bw / w - 0.5f, 0, bw - 1.001f);
                bx0[x] = (int)sx; bfx[x] = sx - bx0[x];
            }
            for (int y = 0; y < h; y++)
            {
                float my = Math.Clamp((y + 0.5f) * n / h - 0.5f, 0, n - 1.001f);
                int y0 = (int)my; float fy = my - y0;
                float sy = Math.Clamp((y + 0.5f) * bh / h - 0.5f, 0, bh - 1.001f);
                int by0 = (int)sy; float bfy = sy - by0;
                byte* fr = (byte*)fd.Scan0 + y * fd.Stride;
                byte* b0 = (byte*)bd.Scan0 + (scaled ? by0 : y) * bd.Stride;
                byte* b1 = scaled ? b0 + bd.Stride : b0;
                int r0 = y0 * n, r1 = (y0 + 1) * n;
                for (int x = 0; x < w; x++)
                {
                    int a = x0[x];
                    float top = mask[r0 + a] + (mask[r0 + a + 1] - mask[r0 + a]) * fx[x];
                    float bot = mask[r1 + a] + (mask[r1 + a + 1] - mask[r1 + a]) * fx[x];
                    float p = top + (bot - top) * fy;
                    // Smoothstep between 0.35 and 0.75 gives a clean but soft edge.
                    float t = Math.Clamp((p - 0.35f) / 0.4f, 0, 1);
                    float alpha = t * t * (3 - 2 * t);
                    if (alpha >= 0.999f) continue;
                    int i = x * 4;
                    for (int c = 0; c < 3; c++)
                    {
                        float bg;
                        if (scaled)
                        {
                            int j = bx0[x] * 4 + c;
                            float u = b0[j] + (b0[j + 4] - b0[j]) * bfx[x];
                            float v = b1[j] + (b1[j + 4] - b1[j]) * bfx[x];
                            bg = u + (v - u) * bfy;
                        }
                        else bg = b0[i + c];
                        fr[i + c] = (byte)(fr[i + c] * alpha + bg * (1 - alpha));
                    }
                }
            }
        }
        finally
        {
            background.UnlockBits(bd);
            frame.UnlockBits(fd);
        }
    }
}
