using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace NvHud
{
    internal static class Artwork
    {
        public const int Size = 240;
        private const int CacheLimit = 4;

        private static readonly List<string> _paths = new List<string>();
        private static readonly Dictionary<string, PointF> _focus = new Dictionary<string, PointF>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<int, Bitmap> _cache = new Dictionary<int, Bitmap>();
        private static readonly List<int> _cacheOrder = new List<int>();

        private static Bitmap _embedded;
        private static int _index;
        private static int _prevIndex = -1;
        private static DateTime _fadeStart = DateTime.MinValue;
        private static readonly TimeSpan FadeLen = TimeSpan.FromMilliseconds(300);

        public static float Fade = 1f;
        public static int Count { get { return _paths.Count; } }
        public static Bitmap Current { get { return Get(_index); } }
        public static Bitmap Previous { get { return _prevIndex >= 0 ? Get(_prevIndex) : null; } }
        public static bool HasFade { get { return Fade < 0.999f && _prevIndex >= 0; } }

        public static void Load(string folder, Bitmap embedded)
        {
            _embedded = embedded;
            _paths.Clear(); _focus.Clear(); _cache.Clear(); _cacheOrder.Clear();
            _index = 0; _prevIndex = -1; Fade = 1f;
            try
            {
                if (!Directory.Exists(folder)) return;
                var files = new List<string>();
                foreach (string pat in new[] { "*.jpg", "*.jpeg", "*.png", "*.bmp", "*.gif" })
                    files.AddRange(Directory.GetFiles(folder, pat));
                files.Sort(StringComparer.OrdinalIgnoreCase);
                _paths.AddRange(files);

                try
                {
                    string cfg = Path.Combine(folder, "crop.txt");
                    if (File.Exists(cfg))
                    {
                        foreach (string raw in File.ReadAllLines(cfg))
                        {
                            string line = raw.Trim();
                            int eq = line.IndexOf('=');
                            if (eq <= 0) continue;
                            string name = Path.GetFileName(line.Substring(0, eq).Trim());
                            string[] parts = line.Substring(eq + 1).Split(',');
                            if (parts.Length < 2) continue;
                            float cx, cy;
                            if (float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out cx)
                                && float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out cy))
                                _focus[name] = new PointF(cx, cy);
                        }
                    }
                }
                catch { }
            }
            catch { }
        }

        public static void Next()
        {
            if (_paths.Count <= 1) return;
            _prevIndex = _index;
            _index = (_index + 1) % _paths.Count;
            Get(_index);
            _fadeStart = DateTime.Now;
            Fade = 0f;
        }

        public static bool TickFade()
        {
            if (Fade >= 1f) return false;
            double ms = (DateTime.Now - _fadeStart).TotalMilliseconds;
            Fade = (float)Math.Min(1.0, ms / FadeLen.TotalMilliseconds);
            if (Fade >= 1f) { Fade = 1f; _prevIndex = -1; return false; }
            return true;
        }

        private static Bitmap Get(int i)
        {
            if (_paths.Count == 0) return _embedded;
            if (i < 0 || i >= _paths.Count) i = 0;

            Bitmap bmp;
            if (_cache.TryGetValue(i, out bmp)) { Touch(i); return bmp; }

            try
            {
                PointF fpt;
                if (!_focus.TryGetValue(Path.GetFileName(_paths[i]), out fpt)) fpt = new PointF(0.5f, 0.5f);
                bmp = Prepare(_paths[i], fpt);
            }
            catch { return _embedded; }

            if (bmp == null) return _embedded;
            _cache[i] = bmp;
            Touch(i);
            Evict();
            return bmp;
        }

        private static void Touch(int i) { _cacheOrder.Remove(i); _cacheOrder.Add(i); }

        private static void Evict()
        {
            while (_cacheOrder.Count > CacheLimit)
            {
                int victim = _cacheOrder[0];
                _cacheOrder.RemoveAt(0);
                if (victim == _index || victim == _prevIndex) continue;
                Bitmap b;
                if (_cache.TryGetValue(victim, out b)) { _cache.Remove(victim); try { b.Dispose(); } catch { } }
            }
        }

        private static Bitmap Prepare(string path, PointF focus)
        {
            using (var img = Image.FromFile(path))
            {
                int side = Math.Min(img.Width, img.Height);
                float cx = Math.Max(0f, Math.Min(1f, focus.X)) * img.Width;
                float cy = Math.Max(0f, Math.Min(1f, focus.Y)) * img.Height;
                int sx = (int)Math.Round(cx - side / 2f);
                int sy = (int)Math.Round(cy - side / 2f);
                if (sx < 0) sx = 0;
                if (sy < 0) sy = 0;
                if (sx > img.Width - side) sx = img.Width - side;
                if (sy > img.Height - side) sy = img.Height - side;

                using (var sq = new Bitmap(Size, Size, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(sq))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(img, new Rectangle(0, 0, Size, Size), new Rectangle(sx, sy, side, side), GraphicsUnit.Pixel);
                    }
                    var outp = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(outp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);
                        using (var mask = new GraphicsPath())
                        {
                            mask.AddEllipse(0, 0, Size - 1, Size - 1);
                            g.SetClip(mask);
                            g.DrawImage(sq, 0, 0, Size, Size);
                            g.ResetClip();
                            float penW = Math.Max(1f, Size / 40f);
                            using (var pen = new Pen(Color.FromArgb(120, 255, 255, 255), penW))
                                g.DrawEllipse(pen, penW / 2f, penW / 2f, Size - 1 - penW, Size - 1 - penW);
                        }
                    }
                    return outp;
                }
            }
        }
    }
}