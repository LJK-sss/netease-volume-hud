using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace NvHud
{
    /// <summary>
    /// 头像集合：按"新增时间倒序"排列，洗牌队列播放，支持运行中重扫目录。
    /// </summary>
    internal static class Artwork
    {
        public const int Size = 240;
        private const int CacheLimit = 4;

        private static string _folder;
        private static readonly List<string> _paths = new List<string>();          // 已排序（最新在前）
        private static readonly Dictionary<string, PointF> _focus = new Dictionary<string, PointF>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _bad = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);  // path -> 指纹
        private static readonly Dictionary<string, Bitmap> _cache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> _cacheOrder = new List<string>();
        private static readonly Dictionary<string, Bitmap> _thumbs = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

        // 洗牌队列
        private static readonly List<string> _queue = new List<string>();
        private static int _queuePos;
        private static readonly Random _rnd = new Random();

        private static Bitmap _embedded;
        private static string _current;      // 当前显示的图片路径
        private static string _prev;         // 淡出中的上一张
        private static DateTime _fadeStart = DateTime.MinValue;
        private static readonly TimeSpan FadeLen = TimeSpan.FromMilliseconds(300);

        public static float Fade = 1f;

        public static int Count { get { return _paths.Count; } }
        public static IList<string> Paths { get { return _paths; } }
        public static string CurrentPath { get { return _current; } }

        public static int CurrentIndex
        {
            get { return _current == null ? -1 : _paths.IndexOf(_current); }
        }

        public static Bitmap Current
        {
            get
            {
                Bitmap b = CurrentSkippingBad();
                return b != null ? b : _embedded;
            }
        }

        public static Bitmap Previous { get { return _prev != null ? Get(_prev) : null; } }
        public static bool HasFade { get { return Fade < 0.999f && _prev != null; } }

        /// <summary>取当前头像；解不出来就沿队列找下一张能用的。</summary>
        private static Bitmap CurrentSkippingBad()
        {
            if (_paths.Count == 0) return _embedded;
            for (int attempt = 0; attempt < _paths.Count; attempt++)
            {
                Bitmap b = Get(_current);
                if (b != null) return b;
                Advance(false);
            }
            return _embedded;
        }

        public static void Load(string folder, Bitmap embedded)
        {
            _embedded = embedded;
            _folder = folder;

            _paths.Clear(); _focus.Clear(); _cache.Clear(); _cacheOrder.Clear();
            _bad.Clear(); _thumbs.Clear(); _queue.Clear(); _queuePos = 0;
            _current = null; _prev = null; Fade = 1f;

            ReadCropConfig();
            _paths.AddRange(EnumerateFiles());

            if (_paths.Count > 0)
            {
                _current = _paths[0];
                Get(_current);
            }
            RebuildQueue();
        }

        /// <summary>重新扫描目录；有新图就插到队首（下一次轮换就能看到）。返回是否有变化。</summary>
        public static bool Rescan()
        {
            if (string.IsNullOrEmpty(_folder)) return false;

            List<string> fresh;
            try { fresh = EnumerateFiles(); }
            catch { return false; }

            var oldSet = new HashSet<string>(_paths, StringComparer.OrdinalIgnoreCase);
            var newSet = new HashSet<string>(fresh, StringComparer.OrdinalIgnoreCase);

            var added = new List<string>();
            foreach (string p in fresh) if (!oldSet.Contains(p)) added.Add(p);

            bool changed = added.Count > 0;
            if (!changed)
            {
                foreach (string p in _paths) if (!newSet.Contains(p)) { changed = true; break; }
            }
            if (!changed) return false;

            // 恢复"可能只是复制到一半"的坏图：指纹变了就再试一次
            var retry = new List<string>();
            foreach (var kv in _bad)
            {
                if (!newSet.Contains(kv.Key)) continue;
                if (Fingerprint(kv.Key) != kv.Value) retry.Add(kv.Key);
            }
            foreach (string p in retry) _bad.Remove(p);
            foreach (string p in added) _bad.Remove(p);

            // 丢掉已删除的
            _paths.RemoveAll(delegate(string p) { return !newSet.Contains(p); });
            _queue.RemoveAll(delegate(string p) { return !newSet.Contains(p); });
            PruneCache(newSet);

            // 合并并按"新增时间倒序"
            _paths.AddRange(added);
            _paths.Sort(NewestFirst);

            // 新图插到"剩余队列"的最前面：下一次就轮到
            if (added.Count > 0)
            {
                added.Sort(NewestFirst);
                if (_queuePos > _queue.Count) _queuePos = _queue.Count;
                _queue.InsertRange(_queuePos, added);
            }

            // 当前图被删了 → 换成第一张
            if (_current == null || !newSet.Contains(_current))
            {
                _current = _paths.Count > 0 ? _paths[0] : null;
                _prev = null; Fade = 1f;
            }
            return true;
        }

        public static void Next()
        {
            Advance(true);
        }

        private static void Advance(bool animate)
        {
            if (_paths.Count <= 1) return;
            if (_queuePos >= _queue.Count) RebuildQueue();
            if (_queue.Count == 0) return;

            string next = _queue[_queuePos % _queue.Count];
            _queuePos++;
            if (string.Equals(next, _current, StringComparison.OrdinalIgnoreCase)) return;

            if (animate)
            {
                _prev = _current;
                _fadeStart = DateTime.Now;
                Fade = 0f;
            }
            _current = next;
            Get(_current);
        }

        /// <summary>直接跳到某张（选择器用）。</summary>
        public static bool SetCurrent(string path, bool animate)
        {
            if (string.IsNullOrEmpty(path) || !_paths.Contains(path)) return false;
            if (string.Equals(path, _current, StringComparison.OrdinalIgnoreCase)) return true;
            if (animate)
            {
                _prev = _current;
                _fadeStart = DateTime.Now;
                Fade = 0f;
            }
            _current = path;
            Get(_current);
            int q = _queue.IndexOf(path);
            if (q >= 0) _queuePos = q + 1;
            return true;
        }

        public static bool TickFade()
        {
            if (Fade >= 1f) return false;
            double ms = (DateTime.Now - _fadeStart).TotalMilliseconds;
            Fade = (float)Math.Min(1.0, ms / FadeLen.TotalMilliseconds);
            if (Fade >= 1f) { Fade = 1f; _prev = null; return false; }
            return true;
        }

        /// <summary>选择器用的缩略图（懒解码 + 缓存）。</summary>
        public static Bitmap Thumb(string path, int side)
        {
            if (string.IsNullOrEmpty(path)) return null;
            Bitmap b;
            if (_thumbs.TryGetValue(path, out b)) return b;
            try { b = MakeThumb(path, side); }
            catch { return null; }
            if (b != null) _thumbs[path] = b;
            return b;
        }

        private static Bitmap MakeThumb(string path, int side)
        {
            using (var img = Image.FromFile(path))
            {
                int s = Math.Min(img.Width, img.Height);
                int sx = (img.Width - s) / 2;
                int sy = (img.Height - s) / 2;
                var outp = new Bitmap(side, side, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(outp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(img, new Rectangle(0, 0, side, side), new Rectangle(sx, sy, s, s), GraphicsUnit.Pixel);
                }
                return outp;
            }
        }

        // ---------- 内部 ----------

        private static int NewestFirst(string a, string b)
        {
            DateTime ta = SafeTime(a), tb = SafeTime(b);
            int c = tb.CompareTo(ta);
            if (c != 0) return c;
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static DateTime SafeTime(string p)
        {
            try
            {
                var fi = new FileInfo(p);
                if (!fi.Exists) return DateTime.MinValue;
                return fi.CreationTimeUtc > fi.LastWriteTimeUtc ? fi.CreationTimeUtc : fi.LastWriteTimeUtc;
            }
            catch { return DateTime.MinValue; }
        }

        private static string Fingerprint(string p)
        {
            try
            {
                var fi = new FileInfo(p);
                return fi.Exists ? (fi.Length + "@" + fi.LastWriteTimeUtc.Ticks) : "";
            }
            catch { return ""; }
        }

        private static List<string> EnumerateFiles()
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(_folder) || !Directory.Exists(_folder)) return list;
            foreach (string pat in new[] { "*.jpg", "*.jpeg", "*.png", "*.bmp", "*.gif" })
            {
                try { list.AddRange(Directory.GetFiles(_folder, pat)); } catch { }
            }
            list.Sort(NewestFirst);
            return list;
        }

        private static void ReadCropConfig()
        {
            try
            {
                if (string.IsNullOrEmpty(_folder)) return;
                string cfg = Path.Combine(_folder, "crop.txt");
                if (!File.Exists(cfg)) return;
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
            catch { }
        }

        private static void RebuildQueue()
        {
            _queue.Clear();
            _queuePos = 0;
            if (_paths.Count == 0) return;
            _queue.AddRange(_paths);
            for (int i = _queue.Count - 1; i > 0; i--)   // Fisher-Yates
            {
                int j = _rnd.Next(i + 1);
                string t = _queue[i]; _queue[i] = _queue[j]; _queue[j] = t;
            }
            if (_queue.Count > 1 && string.Equals(_queue[0], _current, StringComparison.OrdinalIgnoreCase))
            {
                int j = _rnd.Next(1, _queue.Count);
                string t = _queue[0]; _queue[0] = _queue[j]; _queue[j] = t;
            }
        }

        private static void PruneCache(HashSet<string> alive)
        {
            var dead = new List<string>();
            foreach (var kv in _cache) if (!alive.Contains(kv.Key)) dead.Add(kv.Key);
            foreach (string k in dead)
            {
                Bitmap b;
                if (_cache.TryGetValue(k, out b)) { try { b.Dispose(); } catch { } }
                _cache.Remove(k);
                _cacheOrder.Remove(k);
            }
            var deadThumb = new List<string>();
            foreach (var kv in _thumbs) if (!alive.Contains(kv.Key)) deadThumb.Add(kv.Key);
            foreach (string k in deadThumb)
            {
                Bitmap b;
                if (_thumbs.TryGetValue(k, out b)) { try { b.Dispose(); } catch { } }
                _thumbs.Remove(k);
            }
        }

        private static Bitmap Get(string path)
        {
            if (string.IsNullOrEmpty(path)) return _embedded;
            string fp = Fingerprint(path);
            if (fp.Length == 0) return null;
            string mark;
            if (_bad.TryGetValue(path, out mark) && mark == fp) return null;
            if (_bad.ContainsKey(path)) _bad.Remove(path);

            Bitmap bmp;
            if (_cache.TryGetValue(path, out bmp)) { Touch(path); return bmp; }

            try
            {
                PointF fpt;
                if (!_focus.TryGetValue(Path.GetFileName(path), out fpt)) fpt = new PointF(0.5f, 0.5f);
                bmp = Prepare(path, fpt);
            }
            catch (Exception ex)
            {
                _bad[path] = fp;
                try { Log.W("头像无法解码，已跳过: " + Path.GetFileName(path) + " (" + ex.GetType().Name + ")"); } catch { }
                return null;
            }

            if (bmp == null) return null;
            _cache[path] = bmp;
            Touch(path);
            Evict();
            return bmp;
        }

        private static void Touch(string path)
        {
            _cacheOrder.Remove(path);
            _cacheOrder.Add(path);
        }

        private static void Evict()
        {
            while (_cacheOrder.Count > CacheLimit)
            {
                string victim = _cacheOrder[0];
                _cacheOrder.RemoveAt(0);
                if (string.Equals(victim, _current, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(victim, _prev, StringComparison.OrdinalIgnoreCase)) continue;
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
                            float rr = (Size - 1) * 0.30f;   // 和 Renderer.AvatarRadiusRatio 保持一致
                            mask.AddArc(0, 0, rr * 2f, rr * 2f, 180, 90);
                            mask.AddArc(Size - 1 - rr * 2f, 0, rr * 2f, rr * 2f, 270, 90);
                            mask.AddArc(Size - 1 - rr * 2f, Size - 1 - rr * 2f, rr * 2f, rr * 2f, 0, 90);
                            mask.AddArc(0, Size - 1 - rr * 2f, rr * 2f, rr * 2f, 90, 90);
                            mask.CloseFigure();
                            g.SetClip(mask);
                            g.DrawImage(sq, 0, 0, Size, Size);
                            g.ResetClip();
                            float penW = Math.Max(1f, Size / 40f);
                            using (var pen = new Pen(Color.FromArgb(120, 255, 255, 255), penW))
                                g.DrawPath(pen, mask);
                        }
                    }
                    return outp;
                }
            }
        }
    }
}
