using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace NvHud
{
    /// <summary>
    /// 头像轮换：从 exe 同级的 artwork\ 文件夹读取图片，预处理成圆形头像，按时间轮换。
    /// 文件夹为空时退回内置的那张。
    /// </summary>
    internal static class Artwork
    {
        public const int Size = 240;

        private static readonly List<Bitmap> _faces = new List<Bitmap>();
        private static Bitmap _embedded;

        private static int _index;
        private static Bitmap _prev;
        private static DateTime _fadeStart = DateTime.MinValue;
        private static readonly TimeSpan FadeLen = TimeSpan.FromMilliseconds(300);

        /// <summary>0 = 完全是上一张，1 = 完全是当前这张。</summary>
        public static float Fade = 1f;

        public static int Count { get { return _faces.Count; } }

        public static Bitmap Current
        {
            get
            {
                if (_faces.Count == 0) return _embedded;
                return _faces[_index % _faces.Count];
            }
        }

        public static Bitmap Previous { get { return _prev; } }

        public static bool HasFade { get { return Fade < 0.999f && _prev != null; } }

        /// <summary>扫描文件夹并预处理。folder 不存在或为空时只用内置图。</summary>
        public static void Load(string folder, Bitmap embedded)
        {
            _embedded = embedded;
            _faces.Clear();
            _index = 0;
            _prev = null;
            Fade = 1f;

            try
            {
                if (!Directory.Exists(folder)) return;
                var files = new List<string>();
                foreach (string pat in new[] { "*.jpg", "*.jpeg", "*.png", "*.bmp", "*.gif" })
                    files.AddRange(Directory.GetFiles(folder, pat));
                files.Sort(StringComparer.OrdinalIgnoreCase);

                foreach (string f in files)
                {
                    try
                    {
                        Bitmap b = Prepare(f);
                        if (b != null) _faces.Add(b);
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>切到下一张并开始淡入。</summary>
        public static void Next()
        {
            if (_faces.Count <= 1) return;
            _prev = Current;
            _index = (_index + 1) % _faces.Count;
            _fadeStart = DateTime.Now;
            Fade = 0f;
        }

        /// <summary>推进淡入进度；返回 true 表示还在过渡中。</summary>
        public static bool TickFade()
        {
            if (Fade >= 1f) return false;
            double ms = (DateTime.Now - _fadeStart).TotalMilliseconds;
            Fade = (float)Math.Min(1.0, ms / FadeLen.TotalMilliseconds);
            if (Fade >= 1f)
            {
                Fade = 1f;
                _prev = null;
                return false;
            }
            return true;
        }

        /// <summary>居中方形裁切 → 240×240 → 对比增强 → 圆形遮罩 + 细白边。</summary>
        private static Bitmap Prepare(string path)
        {
            using (var img = Image.FromFile(path))
            {
                int side = Math.Min(img.Width, img.Height);
                int sx = (img.Width - side) / 2;
                int sy = (img.Height - side) / 2;

                using (var sq = new Bitmap(Size, Size, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(sq))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(img, new Rectangle(0, 0, Size, Size),
                            new Rectangle(sx, sy, side, side), GraphicsUnit.Pixel);
                    }

                    // 灰度 + 对比增强：让画面在深色卡片上更立得住
                    for (int y = 0; y < Size; y++)
                    {
                        for (int x = 0; x < Size; x++)
                        {
                            Color c = sq.GetPixel(x, y);
                            int v = (int)((c.R * 0.3 + c.G * 0.59 + c.B * 0.11 - 128) * 1.35 + 128);
                            if (v < 0) v = 0;
                            if (v > 255) v = 255;
                            sq.SetPixel(x, y, Color.FromArgb(255, v, v, v));
                        }
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
