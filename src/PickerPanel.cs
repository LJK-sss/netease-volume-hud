using System;
using System.Collections.Generic;
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

        private int _page;
        private int _hover = -1;

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

            using (var bmp = new Bitmap(pw, ph, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                Draw(g, s);
                _surface.CopyFrom(bmp, 0.97f);
            }
            _surface.Push(Handle, _wx, _wy);
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
                    C(34, 34, 40, 246), C(24, 24, 29, 246), LinearGradientMode.Vertical))
                    g.FillPath(bg, path);
                using (var pen = new Pen(C(255, 255, 255, 30), 0.9f))
                    g.DrawPath(pen, path);
            }

            EnsureFonts();
            int total = Artwork.Count;
            string cur = Artwork.CurrentPath;

            using (var b = new SolidBrush(C(235, 236, 244, 255)))
                g.DrawString("头像选择", _fTitle, b, new RectangleF(18, 12, 200, 26), _sfL);

            string meta = total + " 张 · " + (PageCount > 1 ? ("第 " + (_page + 1) + "/" + PageCount + " 页") : "最新在前");
            using (var b = new SolidBrush(C(150, 152, 166, 255)))
                g.DrawString(meta, _fMeta, b, new RectangleF(w - 18 - 190, 13, 190, 24), _sfR);

            int start = _page * PerPage;
            for (int k = 0; k < PerPage; k++)
            {
                int idx = start + k;
                float x, y;
                CellRect(k, out x, out y);

                using (var cp = Round(x, y, Cell, Cell, 10f))
                {
                    if (idx >= total)
                    {
                        using (var eb = new SolidBrush(C(255, 255, 255, 8)))
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
                        using (var eb = new SolidBrush(C(60, 60, 70, 255)))
                            g.FillPath(eb, cp);
                    }

                    bool isCur = !string.IsNullOrEmpty(cur) && string.Equals(cur, Artwork.Paths[idx], StringComparison.OrdinalIgnoreCase);
                    if (isCur)
                    {
                        using (var pen = new Pen(C(255, 92, 108, 255), 2.4f))
                            g.DrawPath(pen, cp);
                    }
                    else if (_hover == idx)
                    {
                        using (var pen = new Pen(C(255, 255, 255, 120), 1.4f))
                            g.DrawPath(pen, cp);
                    }
                    else
                    {
                        using (var pen = new Pen(C(255, 255, 255, 26), 1f))
                            g.DrawPath(pen, cp);
                    }
                }
            }

            string hint = "点击选择 · 滚轮翻页 · 移开鼠标自动关闭";
            if (total == 0) hint = "artwork 文件夹里没有图片";
            using (var b = new SolidBrush(C(140, 142, 156, 255)))
                g.DrawString(hint, _fHint, b, new RectangleF(18, h - 30, w - 36, 22), _sfL);

            // 页码圆点
            int pages = PageCount;
            if (pages > 1)
            {
                float dx = w / 2f - (pages - 1) * 7f;
                for (int i = 0; i < pages; i++)
                {
                    bool on = i == _page;
                    using (var b = new SolidBrush(on ? C(255, 92, 108, 235) : C(255, 255, 255, 45)))
                        g.FillEllipse(b, dx + i * 14f - 2.5f, h - 44f, 5f, 5f);
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            float s = DpiScale;
            float lx = e.X / s, ly = e.Y / s;

            int hit = -1;
            int start = _page * PerPage;
            for (int k = 0; k < PerPage; k++)
            {
                float x, y;
                CellRect(k, out x, out y);
                if (lx >= x && lx <= x + Cell && ly >= y && ly <= y + Cell)
                {
                    if (start + k < Artwork.Count) hit = start + k;
                    break;
                }
            }
            if (hit != _hover) { _hover = hit; Render(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right) { HideMe(); return; }
            if (e.Button != MouseButtons.Left) return;
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
            int next = _page + (e.Delta > 0 ? -1 : 1);
            if (next < 0) next = pages - 1;
            if (next >= pages) next = 0;
            _page = next;
            _hover = -1;
            Render();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _leaveGuard.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
