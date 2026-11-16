using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NvHud
{
    /// <summary>
    /// 头像选择器：右键头像弹出，6×24 网格 + 缩略图，滚轮翻页，点一下就切换。
    /// 风格与右键菜单一致（同一套圆角玻璃底板）。
    /// </summary>
    internal sealed class PickerPanel : Form
    {
        private readonly LayeredSurface _surface = new LayeredSurface();
        private readonly Timer _leaveGuard = new Timer();
        private int _wx, _wy;

        public const float W_LOGICAL = 460f;
        public const float H_LOGICAL = 400f;
        private const int Cols = 6;
        private const int Rows = 4;
        private const float Cell = 58f;
        private const float Gap = 8f;
        private const float PadX = 18f;
        private const float GridY = 52f;
        private const int PerPage = Cols * Rows;

        // 网格整体尺寸（与 CellRect 一致），用于裁剪与翻页平移
        private const float GridW = Cols * Cell + (Cols - 1) * Gap;
        private const float GridH = Rows * Cell + (Rows - 1) * Gap;
        private const float GridX = (W_LOGICAL - GridW) / 2f;

        // 翻页动画：只在滚轮翻页的那一小段时间内运行
        private const int FlipMs = 260;
        private const int FlipInterval = 16;

        private int _page;
        private int _hover = -1;

        private readonly Timer _flipTimer = new Timer();
        private readonly Stopwatch _flipClock = new Stopwatch();
        private Bitmap _flipFrom, _flipTo;   // 翻页前/后两页的合成位图，动画结束即释放
        private float _flipP;
        private int _flipDir = 1;            // +1 下一页（从右进），-1 上一页（从左进）
        private Bitmap _canvas;              // 复用渲染画布，避免每帧新建 Bitmap

        public event Action<string> PickRequested;

        private Font _fTitle, _fMeta, _fHint;
        private StringFormat _sfL, _sfR;

        public PickerPanel()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.Black;
            SetStyle(ControlStyles.Opaque, true);

            _leaveGuard.Interval = 500;
            _leaveGuard.Tick += delegate { if (!IsOnPanel()) HideMe(); };

            _flipTimer.Interval = FlipInterval;
            _flipTimer.Tick += FlipTick;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW
                            | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        private float DpiScale
        {
            get { return Handle != IntPtr.Zero ? Native.GetScale(Handle) : 1f; }
        }

        public int PageCount
        {
            get
            {
                int n = Artwork.Count;
                if (n <= 0) return 1;
                return (n + PerPage - 1) / PerPage;
            }
        }

        private bool IsOnPanel()
        {
            Point p = Cursor.Position;
            Rectangle r = Bounds;
            r.Inflate(24, 24);
            return r.Contains(p);
        }

        public void ShowNearCursor()
        {
            float s = Handle != IntPtr.Zero ? DpiScale : 1f;
            if (!Visible) Show();
            s = DpiScale;

            // 打开时把当前显示的那张所在的页翻出来
            int cur = Artwork.CurrentIndex;
            _page = cur >= 0 ? cur / PerPage : 0;
            _hover = -1;
            StopFlip();

            int w = (int)Math.Round(W_LOGICAL * s);
            int h = (int)Math.Round(H_LOGICAL * s);
            Point c = Cursor.Position;
            int x = c.X - w + (int)Math.Round(12 * s);
            int y = c.Y - h - (int)Math.Round(2 * s);

            Screen scr = Screen.FromPoint(c);
            Rectangle wa = scr.WorkingArea;
            if (x < wa.Left) x = wa.Left;
            if (y < wa.Top) y = c.Y + (int)Math.Round(6 * s);
            if (x + w > wa.Right) x = wa.Right - w;
            if (y + h > wa.Bottom) y = wa.Bottom - h;

            _wx = x; _wy = y;
            SetBounds(x, y, w, h, BoundsSpecified.All);
            Render();
            _leaveGuard.Start();
        }

        public void HideMe()
        {
            StopFlip();
            _leaveGuard.Stop();
            _hover = -1;
            Hide();
        }

        public void Refresh_()
        {
            if (Visible) Render();
        }

        private void Render()
        {
            if (!Visible || Handle == IntPtr.Zero) return;

            float s = DpiScale;
            int pw = (int)Math.Round(W_LOGICAL * s);
            int ph = (int)Math.Round(H_LOGICAL * s);

            Bitmap bmp = GetCanvas(pw, ph);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                Draw(g, s);
                _surface.CopyFrom(bmp, 0.97f);
            }
            _surface.Push(Handle, _wx, _wy);
        }

        /// <summary>渲染画布按尺寸复用，翻页动画期间不再每帧分配 460×400 位图。</summary>
        private Bitmap GetCanvas(int w, int h)
        {
            if (_canvas == null || _canvas.Width != w || _canvas.Height != h)
            {
                if (_canvas != null) _canvas.Dispose();
                _canvas = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            }
            return _canvas;
        }

        private void EnsureFonts()
        {
            if (_fTitle != null) return;
            _fTitle = new Font("Microsoft YaHei UI", 12.5f, FontStyle.Bold, GraphicsUnit.Pixel);
            _fMeta = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Pixel);
            _fHint = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Pixel);
            _sfL = new StringFormat();
            _sfL.LineAlignment = StringAlignment.Center;
            _sfR = new StringFormat();
            _sfR.Alignment = StringAlignment.Far;
            _sfR.LineAlignment = StringAlignment.Center;
        }

        private static Color C(int r, int gg, int b, int a) { return Color.FromArgb(a, r, gg, b); }

        private static GraphicsPath Round(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath();
            float d = r * 2f;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>第 i 个单元（当前页内）的逻辑位置。</summary>
        private static void CellRect(int i, out float x, out float y)
        {
            int c = i % Cols, r = i / Cols;
            float gridW = Cols * Cell + (Cols - 1) * Gap;
            float x0 = (W_LOGICAL - gridW) / 2f;
            x = x0 + c * (Cell + Gap);
            y = GridY + r * (Cell + Gap);
        }

        private void Draw(Graphics g, float s)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.ScaleTransform(s, s);

            float w = W_LOGICAL, h = H_LOGICAL;

            using (var path = Round(0, 0, w, h, 14f))
            {
                using (var bg = new LinearGradientBrush(new RectangleF(0, 0, w, h),
                    Theme.PanelTop, Theme.PanelBottom, LinearGradientMode.Vertical))
                    g.FillPath(bg, path);
                using (var pen = new Pen(Theme.PanelStroke, 0.9f))
                    g.DrawPath(pen, path);
            }

            EnsureFonts();
            int total = Artwork.Count;

            using (var b = new SolidBrush(Theme.Title))
                g.DrawString("头像选择", _fTitle, b, new RectangleF(18, 12, 200, 26), _sfL);

            string meta = total + " 张 · " + (PageCount > 1 ? ("第 " + (_page + 1) + "/" + PageCount + " 页") : "最新在前");
            using (var b = new SolidBrush(Theme.DimSoft))
                g.DrawString(meta, _fMeta, b, new RectangleF(w - 18 - 190, 13, 190, 24), _sfR);

            if (_flipFrom != null && _flipTo != null)
                DrawFlip(g);
            else
                DrawGridPage(g, _page, 0f);

            string hint = "点击选择 · 滚轮翻页 · 移开鼠标自动关闭";
            if (total == 0) hint = "artwork 文件夹里没有图片";
            using (var b = new SolidBrush(Theme.Dim))
                g.DrawString(hint, _fHint, b, new RectangleF(18, h - 30, w - 36, 22), _sfL);

            // 页码圆点
            int pages = PageCount;
            if (pages > 1)
            {
                float dx = w / 2f - (pages - 1) * 7f;
                for (int i = 0; i < pages; i++)
                {
                    bool on = i == _page;
                    using (var b = new SolidBrush(on ? Theme.AccentSoft : Theme.Fg(45)))
                        g.FillEllipse(b, dx + i * 14f - 2.5f, h - 44f, 5f, 5f);
                }
            }
        }

        /// <summary>画某一页的 24 个格子；offX 为翻页时的水平位移（逻辑像素）。</summary>
        private void DrawGridPage(Graphics g, int page, float offX)
        {
            int total = Artwork.Count;
            string cur = Artwork.CurrentPath;
            int start = page * PerPage;

            for (int k = 0; k < PerPage; k++)
            {
                int idx = start + k;
                float x, y;
                CellRect(k, out x, out y);
                x += offX;

                using (var cp = Round(x, y, Cell, Cell, 10f))
                {
                    if (idx >= total)
                    {
                        using (var eb = new SolidBrush(Theme.CellEmpty))
                            g.FillPath(eb, cp);
                        continue;
                    }

                    Bitmap th = Artwork.Thumb(Artwork.Paths[idx], 96);
                    if (th != null)
                    {
                        var st = g.Save();
                        g.SetClip(cp);
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.DrawImage(th, x, y, Cell, Cell);
                        g.Restore(st);
                    }
                    else
                    {
                        using (var eb = new SolidBrush(Theme.CellFallback))
                            g.FillPath(eb, cp);
                    }

                    bool isCur = !string.IsNullOrEmpty(cur) && string.Equals(cur, Artwork.Paths[idx], StringComparison.OrdinalIgnoreCase);
                    if (isCur)
                    {
                        using (var pen = new Pen(Theme.Accent, 2.4f))
                            g.DrawPath(pen, cp);
                    }
                    else if (_hover == idx)
                    {
                        using (var pen = new Pen(Theme.Fg(130), 1.4f))
                            g.DrawPath(pen, cp);
                    }
                    else
                    {
                        using (var pen = new Pen(Theme.Fg(26), 1f))
                            g.DrawPath(pen, cp);
                    }
                }
            }
        }

        /// <summary>翻页中：直接平移两张预合成页图，不逐格重画缩略图。</summary>
        private void DrawFlip(Graphics g)
        {
            float p = EaseInOut(_flipP);
            float offFrom = -_flipDir * p * GridW;
            float offTo = _flipDir * (GridW - p * GridW);

            GraphicsState st = g.Save();
            GraphicsPath clip = Round(GridX - 8f, GridY - 6f, GridW + 16f, GridH + 12f, 10f);
            g.SetClip(clip, CombineMode.Intersect);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(_flipFrom, new RectangleF(GridX + offFrom, GridY, GridW, GridH));
            g.DrawImage(_flipTo, new RectangleF(GridX + offTo, GridY, GridW, GridH));
            g.Restore(st);
            clip.Dispose();
        }

        /// <summary>把一页合成为一张静态位图（设备像素），供翻页时整块平移。</summary>
        private Bitmap ComposePage(int page, float s)
        {
            int bw = Math.Max(1, (int)Math.Round(GridW * s));
            int bh = Math.Max(1, (int)Math.Round(GridH * s));
            var bmp = new Bitmap(bw, bh, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.ScaleTransform(s, s);
                g.TranslateTransform(-GridX, -GridY);
                DrawGridPage(g, page, 0f);
            }
            return bmp;
        }

        private void StartFlip(int fromPage, int toPage, int dir)
        {
            StopFlip();

            float s = DpiScale;
            _flipDir = dir;
            _page = toPage;
            _hover = -1;

            try
            {
                _flipFrom = ComposePage(fromPage, s);
                _flipTo = ComposePage(toPage, s);
            }
            catch
            {
                StopFlip();
                Render();
                return;
            }

            _flipP = 0f;
            _flipClock.Restart();
            _flipTimer.Start();
            Render();
        }

        private void FlipTick(object sender, EventArgs e)
        {
            float ms = (float)_flipClock.Elapsed.TotalMilliseconds;
            _flipP = ms / FlipMs;
            if (_flipP >= 1f)
            {
                StopFlip();
                SyncHover();   // 光标没动时也能恢复悬停高亮
                Render();
                return;
            }
            Render();
        }

        /// <summary>按光标位置重新计算悬停项（翻页结束后用）。</summary>
        private void SyncHover()
        {
            if (!Visible || _flipFrom != null) return;
            float s = DpiScale;
            Point p = PointToClient(Cursor.Position);
            int hit = HitTest(p.X / s, p.Y / s);
            if (hit != _hover) _hover = hit;
        }

        private int HitTest(float lx, float ly)
        {
            int start = _page * PerPage;
            for (int k = 0; k < PerPage; k++)
            {
                float x, y;
                CellRect(k, out x, out y);
                if (lx >= x && lx <= x + Cell && ly >= y && ly <= y + Cell)
                    return start + k < Artwork.Count ? start + k : -1;
            }
            return -1;
        }

        /// <summary>结束翻页：停表并立刻释放两张页图，之后回到常态绘制。</summary>
        private void StopFlip()
        {
            _flipTimer.Stop();
            _flipClock.Reset();
            _flipP = 0f;
            if (_flipFrom != null) { _flipFrom.Dispose(); _flipFrom = null; }
            if (_flipTo != null) { _flipTo.Dispose(); _flipTo = null; }
        }

        /// <summary>缓入缓出（三次）：起步和收尾都不生硬，中段保持速度。</summary>
        private static float EaseInOut(float t)
        {
            if (t < 0.5f) return 4f * t * t * t;
            float u = -2f * t + 2f;
            return 1f - u * u * u / 2f;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            // 翻页动画期间不参与命中，避免滑过的格子被误判为悬停
            if (_flipFrom != null) return;

            float s = DpiScale;
            int hit = HitTest(e.X / s, e.Y / s);
            if (hit != _hover) { _hover = hit; Render(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right) { HideMe(); return; }
            if (e.Button != MouseButtons.Left) return;
            if (_flipFrom != null)
            {
                // 滑动途中点击：立即收尾，但不选中任何格子，避免误选滑过的封面
                StopFlip();
                SyncHover();
                Render();
                return;
            }
            if (_hover < 0) return;
            if (_hover >= Artwork.Count) return;

            string path = Artwork.Paths[_hover];
            HideMe();
            if (PickRequested != null) PickRequested(path);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int pages = PageCount;
            if (pages <= 1) return;

            int dir = e.Delta > 0 ? -1 : 1;

            // 上一次翻页还没走完：先收尾，再以当前目标页作为新的起点
            StopFlip();
            int from = _page;
            int to = from + dir;
            if (to < 0) to = pages - 1;
            if (to >= pages) to = 0;

            StartFlip(from, to, dir);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopFlip();
                _flipTimer.Dispose();
                if (_canvas != null) { _canvas.Dispose(); _canvas = null; }
                _leaveGuard.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
