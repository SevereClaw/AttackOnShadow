using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace AttackOnShadow.Device
{
    /// <summary>One picture on the keys: 3 bytes (R, G, B) per matrix index, and how long it stays.</summary>
    public sealed class KeyFrame
    {
        public byte[] Rgb = new byte[3 * TravelMap.Size];
        public int DelayMs = 100;
    }

    /// <summary>Turns images and GIFs into key colours (the picture is stretched over the whole keyboard).</summary>
    public static class KeyImage
    {
        const int Px = 12;                                   // pixels per key unit when sampling
        const int W = (int)(K85Travel.LayoutWidth * Px), H = (int)(K85Travel.LayoutRows * Px);
        public const int StepMs = 125;                       // at most 8 frames per second on the keyboard
        public const int MaxFrames = 40;                     // 5 seconds

        public static byte[] Black() => new byte[3 * TravelMap.Size];

        /// <summary>All frames of an image file, resampled to a steady 8 fps timeline (a still image gives one frame).</summary>
        public static List<KeyFrame> Load(string path)
        {
            var bytes = File.ReadAllBytes(path);
            using (var ms = new MemoryStream(bytes))
            using (var img = Image.FromStream(ms))
            {
                var dim = new FrameDimension(img.FrameDimensionsList[0]);
                int n = Math.Max(1, img.GetFrameCount(dim));
                var delays = new int[n];
                try
                {
                    var prop = img.GetPropertyItem(0x5100); // frame delays, 1/100 s each
                    for (int i = 0; i < n && 4 * i + 3 < prop.Value.Length; i++) delays[i] = BitConverter.ToInt32(prop.Value, 4 * i) * 10;
                }
                catch { }
                for (int i = 0; i < n; i++) if (delays[i] < 20) delays[i] = 100; // browsers treat 0-10 ms as 100 ms

                var src = new List<KeyFrame>();
                for (int i = 0; i < n; i++)
                {
                    if (n > 1) img.SelectActiveFrame(dim, i);
                    src.Add(new KeyFrame { Rgb = Sample(img), DelayMs = delays[i] });
                }
                if (n == 1) { src[0].DelayMs = 1500; return src; }
                return Retime(src);
            }
        }

        /// <summary>Picks the frame shown at every 125 ms step, joining repeats.</summary>
        public static List<KeyFrame> Retime(List<KeyFrame> src)
        {
            int total = src.Sum(f => f.DelayMs);
            var outp = new List<KeyFrame>();
            for (int t = 0; t < total && outp.Count < MaxFrames; t += StepMs)
            {
                int acc = 0, idx = 0;
                while (idx < src.Count - 1 && acc + src[idx].DelayMs <= t) { acc += src[idx].DelayMs; idx++; }
                var rgb = src[idx].Rgb;
                if (outp.Count > 0 && outp[outp.Count - 1].Rgb.SequenceEqual(rgb)) outp[outp.Count - 1].DelayMs += StepMs;
                else outp.Add(new KeyFrame { Rgb = rgb, DelayMs = StepMs });
            }
            return outp;
        }

        /// <summary>Draws the image to cover the keyboard (cropping the overflow) and averages the pixels under each key.</summary>
        public static byte[] Sample(Image img)
        {
            using (var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Black);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    double scale = Math.Max((double)W / img.Width, (double)H / img.Height);
                    float dw = (float)(img.Width * scale), dh = (float)(img.Height * scale);
                    g.DrawImage(img, (W - dw) / 2, (H - dh) / 2, dw, dh);
                }
                var data = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var px = new byte[data.Stride * H];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, px, 0, px.Length);
                int stride = data.Stride;
                bmp.UnlockBits(data);

                var rgb = new byte[3 * TravelMap.Size];
                foreach (var k in K85Travel.Layout)
                {
                    int x0 = (int)(k.X * Px), x1 = (int)((k.X + k.W) * Px), y0 = k.Row * Px, y1 = y0 + Px;
                    long r = 0, gg = 0, b = 0, cnt = 0;
                    for (int y = y0 + 1; y < y1 - 1; y++)
                        for (int x = x0 + 1; x < x1 - 1; x++)
                        {
                            int o = y * stride + 4 * x;
                            b += px[o]; gg += px[o + 1]; r += px[o + 2]; cnt++;
                        }
                    if (cnt == 0) continue;
                    rgb[3 * k.Matrix] = (byte)(r / cnt); rgb[3 * k.Matrix + 1] = (byte)(gg / cnt); rgb[3 * k.Matrix + 2] = (byte)(b / cnt);
                }
                return rgb;
            }
        }

        /// <summary>Built-in animation: a cyan ring spreading from the pressed key.</summary>
        public static List<KeyFrame> Ripple(int matrix)
        {
            var from = K85Travel.Layout.FirstOrDefault(k => k.Matrix == matrix) ?? K85Travel.Layout.First(k => k.Label == "G");
            double cx = from.X + from.W / 2, cy = from.Row + 0.5;
            var frames = new List<KeyFrame>();
            const int n = 9;
            for (int f = 0; f < n; f++)
            {
                var fr = new KeyFrame { DelayMs = StepMs };
                double radius = f * 1.6, fade = 1 - (double)f / n;
                foreach (var k in K85Travel.Layout)
                {
                    double dx = k.X + k.W / 2 - cx, dy = (k.Row + 0.5 - cy) * 1.1;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    double ring = Math.Max(0, 1 - Math.Abs(d - radius) / 1.3) * fade;
                    double core = f < 2 && d < 0.6 ? 1 : 0;
                    double white = Math.Max(core, ring * ring * 0.5);
                    fr.Rgb[3 * k.Matrix] = Clamp(0x29 * ring + 255 * white);
                    fr.Rgb[3 * k.Matrix + 1] = Clamp(0xD3 * ring + 255 * white);
                    fr.Rgb[3 * k.Matrix + 2] = Clamp(0xFF * Math.Max(ring, white));
                }
                frames.Add(fr);
            }
            return frames;
        }

        /// <summary>Sample picture for the preview before a file is chosen: the P3R night gradient.</summary>
        public static byte[] Sample()
        {
            var rgb = new byte[3 * TravelMap.Size];
            foreach (var k in K85Travel.Layout)
            {
                double t = (k.X + k.W / 2) / K85Travel.LayoutWidth, v = k.Row / (K85Travel.LayoutRows - 1);
                rgb[3 * k.Matrix] = Clamp(0x0B + 0x1E * t + 0x60 * (1 - v) * t);
                rgb[3 * k.Matrix + 1] = Clamp(0x2D + 0xA6 * (1 - v) * (0.4 + 0.6 * t));
                rgb[3 * k.Matrix + 2] = Clamp(0x7D + 0x82 * (1 - v * 0.6));
            }
            return rgb;
        }

        public static void SelfTest(List<string> fail)
        {
            byte[] C(byte v) { var r = Black(); r[0] = v; return r; }
            var tl = Retime(new List<KeyFrame> { new KeyFrame { Rgb = C(1), DelayMs = 100 }, new KeyFrame { Rgb = C(2), DelayMs = 100 }, new KeyFrame { Rgb = C(3), DelayMs = 300 } });
            string got = string.Join(",", tl.Select(f => f.Rgb[0] + ":" + f.DelayMs));
            if (got != "1:125,2:125,3:250") fail.Add("retime: " + got);
            using (var bmp = new Bitmap(320, 120))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.FillRectangle(Brushes.Red, 0, 0, 160, 120);
                    g.FillRectangle(Brushes.Blue, 160, 0, 160, 120);
                }
                var rgb = Sample(bmp);
                int esc = K85Travel.Layout.First(k => k.Label == "Esc").Matrix, del = K85Travel.Layout.First(k => k.Label == "Del").Matrix;
                if (rgb[3 * esc] < 200 || rgb[3 * esc + 2] > 50) fail.Add($"sample esc {rgb[3 * esc]},{rgb[3 * esc + 2]}");
                if (rgb[3 * del + 2] < 200 || rgb[3 * del] > 50) fail.Add($"sample del {rgb[3 * del]},{rgb[3 * del + 2]}");
            }
            var rip = Ripple(K85Travel.Layout.First(k => k.Label == "G").Matrix);
            if (rip.Count != 9 || rip.All(f => f.Rgb.All(b => b == 0))) fail.Add("ripple empty");
        }

        static byte Clamp(double v) => (byte)Math.Max(0, Math.Min(255, Math.Round(v)));
    }
}
