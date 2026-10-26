using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NvHud
{
    /// <summary>
    /// 自绘右键菜单：与小窗同款玻璃拟态，只保留最常用的几项。
    /// 数值项用 ◀ ▶ 就地调节，避免二级菜单。
    /// </summary>
    internal sealed class MenuPanel : Form
    {
        private readonly LayeredSurface _surface = new LayeredSurface();
        private readonly Timer _leaveGuard = new Timer();
        private readonly Timer _raise = new Timer();
        private int _wx, _wy;

        public const float W_LOGICAL = 208f;
        public const float ROW_H = 30f;
        public const float PAD_Y = 7f;
        public const float SEP_H = 10f;
        public const int ACTION_ROWS = 7;      // 0静音 1自动增益 2界面大小 3不透明度 4开机自启 5频谱浓度 6退出








public float H_LOGICAL { get { return PAD_Y * 2f + ROW_H * ACTION_ROWS + SEP_H; } }

/// <summary>预设档位，用箭头循环切换。</summary>



        public bool Muted;
        public bool AutoGainOn = true;
        public bool AutoStartOn;
        public float ScaleValue = 1f;
        public float OpacityValue = 0.88f;

        public int SpectrumPct = 28;

        public int HoverRow = -1;
        public int HoverZone;   // 0 无, 1 左箭头, 2 右箭头, 3 整行

        public event Action MuteToggle;
        public event Action AutoGainToggle;
        public event Action<int> ScaleStep;
        public event Action<int> OpacityStep;
        public event Action AutoStartToggle;

        public event Action<int> SpectrumStep;

        public event Action ExitRequested;

        private Font _fLabel, _fValue;
        private StringFormat _sfL, _sfR, _sfC;

        public MenuPanel()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.Black;
            SetStyle(ControlStyles.Opaque, true);

            _leaveGuard.Interval = 160;
            _leaveGuard.Tick += delegate { if (!IsOnPanel()) { HideMe(); } };
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

        private bool IsOnPanel()
        {
            // 加一圈宽容区：菜单右下角贴着鼠标时指针会落在边界外，否则会被立刻收起
            Point p = Cursor.Position;
            Rectangle r = Bounds;
            r.Inflate(28, 28);
            return r.Contains(p);
        }

        /// <summary>在鼠标处弹出（自动避开屏幕边缘）。</summary>
        public void ShowAtCursor()
        {
            float s = Handle != IntPtr.Zero ? DpiScale : 1f;
            if (!Visible) Show();
            s = DpiScale;

            int w = (int)Math.Round(W_LOGICAL * s);
            int h = (int)Math.Round(H_LOGICAL * s);

            // 与参数面板一致：菜单右下角贴住鼠标，向左上展开
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
            HoverRow = -1; HoverZone = 0;
            Render();
            _leaveGuard.Start();
            _raise.Start();
        }


        public void HideMe()
        {
            _leaveGuard.Stop();
            _raise.Stop();
            HoverRow = -1; HoverZone = 0;
            Hide();
        }

        private void RaiseTopmost()
        {
            if (!Visible || Handle == IntPtr.Zero) return;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
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
            if (_fLabel != null) return;
            _fLabel = new Font("Microsoft YaHei UI", 11.5f, FontStyle.Regular, GraphicsUnit.Pixel);
            _fValue = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
            _sfL = new StringFormat();
            _sfL.LineAlignment = StringAlignment.Center;
            _sfR = new StringFormat();
            _sfR.Alignment = StringAlignment.Far;
            _sfR.LineAlignment = StringAlignment.Center;
            _sfC = new StringFormat();
            _sfC.Alignment = StringAlignment.Center;
            _sfC.LineAlignment = StringAlignment.Center;
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

        /// <summary>第 i 行（0..5）的逻辑 y；第 5 行在分隔线之后。</summary>
        private float RowY(int i)
        {
            if (i <= 5) return PAD_Y + i * ROW_H;
            return PAD_Y + 6 * ROW_H + SEP_H;
        }

        private void Draw(Graphics g, float s)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.ScaleTransform(s, s);

            float w = W_LOGICAL;
            float h = H_LOGICAL;

            // 玻璃底板
            using (var path = Round(0, 0, w, h, 12f))
            {
                using (var bg = new LinearGradientBrush(new RectangleF(0, 0, w, h),
                    C(34, 34, 40, 246), C(24, 24, 29, 246), LinearGradientMode.Vertical))
                    g.FillPath(bg, path);
                using (var pen = new Pen(C(255, 255, 255, 30), 0.9f))
                    g.DrawPath(pen, path);
            }

            EnsureFonts();

            for (int i = 0; i < ACTION_ROWS; i++)
            {
                float y = RowY(i);
                bool hover = (HoverRow == i);

                if (hover)
                {
                    using (var hp = Round(4, y + 1, w - 8, ROW_H - 2, 7f))
                    using (var hb = new SolidBrush(C(255, 255, 255, 14)))
                        g.FillPath(hb, hp);
                }

                if (i == 0)
                {
                    DrawText(g, Muted ? "取消静音" : "静音", y);
                }
                else if (i == 1)
                {
                    DrawText(g, "自动增益", y);
                    DrawPill(g, w, y, AutoGainOn);
                }
                else if (i == 2)
                {
                    DrawText(g, "界面大小", y);
                    DrawValue(g, w, y, ((int)Math.Round(ScaleValue * 100)).ToString() + "%");
                    DrawChevron(g, w - 40, y + ROW_H / 2f, false, hover && HoverZone == 1);
                    DrawChevron(g, w - 18, y + ROW_H / 2f, true, hover && HoverZone == 2);
                }
                else if (i == 3)
                {
                    DrawText(g, "不透明度", y);
                    DrawValue(g, w, y, ((int)Math.Round(OpacityValue * 100)).ToString() + "%");
                    DrawChevron(g, w - 40, y + ROW_H / 2f, false, hover && HoverZone == 1);
                    DrawChevron(g, w - 18, y + ROW_H / 2f, true, hover && HoverZone == 2);
                }
                else if (i == 4)
                {
                    DrawText(g, "开机自启", y);
                    DrawPill(g, w, y, AutoStartOn);
                }
                else if (i == 5)
                {
                    DrawText(g, "频谱浓度", y);
                    DrawValue(g, w, y, SpectrumPct + "%");
                    DrawChevron(g, w - 40, y + ROW_H / 2f, false, hover && HoverZone == 1);
                    DrawChevron(g, w - 18, y + ROW_H / 2f, true, hover && HoverZone == 2);
                }
                else
                {
                    using (var b = new SolidBrush(hover ? C(255, 120, 130, 255) : C(226, 120, 128, 235)))
                        g.DrawString("退出", _fLabel, b, new RectangleF(14, y + 2, w - 28, ROW_H - 4), _sfL);
                }
            }

            // 分隔线
            float sy = RowY(6) - SEP_H / 2f;
            using (var pen = new Pen(C(255, 255, 255, 26), 0.8f))
                g.DrawLine(pen, 12, sy, w - 12, sy);
        }


        private void DrawText(Graphics g, string text, float y)
        {
            using (var b = new SolidBrush(C(232, 234, 242, 255)))
                g.DrawString(text, _fLabel, b, new RectangleF(14, y + 2, W_LOGICAL - 28, ROW_H - 4), _sfL);
        }

        private void DrawValue(Graphics g, float w, float y, string text)
        {
            using (var b = new SolidBrush(C(246, 246, 250, 255)))
                g.DrawString(text, _fValue, b, new RectangleF(w - 112, y + 2, 56, ROW_H - 4), _sfR);
        }

        private void DrawPill(Graphics g, float w, float y, bool on)
        {
            float pw = 34f, ph = 17f;
            float px = w - 16 - pw, py = y + (ROW_H - ph) / 2f;
            using (var pp = Round(px, py, pw, ph, ph / 2f))
            using (var pb = new SolidBrush(on ? C(255, 74, 105, 235) : C(70, 70, 80, 255)))
                g.FillPath(pb, pp);
            float knob = ph - 4f;
            float kx = on ? (px + pw - knob - 2f) : (px + 2f);
            using (var kb = new SolidBrush(C(255, 255, 255, 245)))
                g.FillEllipse(kb, kx, py + 2f, knob, knob);
        }

        private static void DrawChevron(Graphics g, float cx, float cy, bool right, bool hot)
        {
            int a = hot ? 235 : 150;
            using (var pen = new Pen(Color.FromArgb(a, 255, 255, 255), 1.4f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                float d = right ? 1f : -1f;
                g.DrawLine(pen, cx - 2.6f * d, cy - 4.6f, cx + 2.6f * d, cy);
                g.DrawLine(pen, cx + 2.6f * d, cy, cx - 2.6f * d, cy + 4.6f);
            }
        }

        #region 交互

        private int RowAt(Point p)
        {
            float s = DpiScale;
            float ly = p.Y / s;
            for (int i = 0; i < ACTION_ROWS; i++)
            {
                float top = RowY(i);
                if (ly >= top && ly < top + ROW_H) return i;
            }
            return -1;
        }

        private int ZoneAt(Point p, int row)
        {
            if (row < 0) return 0;
            float s = DpiScale;
            float lx = p.X / s;
            if (row == 2 || row == 3 || row == 5)
            {
                if (lx >= W_LOGICAL - 48 && lx < W_LOGICAL - 28) return 1;
                if (lx >= W_LOGICAL - 28) return 2;
                return 3;
            }
            return 3;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int r = RowAt(e.Location);
            int z = ZoneAt(e.Location, r);
            if (r != HoverRow || z != HoverZone)
            {
                HoverRow = r; HoverZone = z;
                Render();
                Cursor = (r >= 0) ? Cursors.Hand : Cursors.Default;
            }
            _leaveGuard.Stop();
            _leaveGuard.Start();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            int row = RowAt(e.Location);
            if (row < 0) return;
            int zone = ZoneAt(e.Location, row);

            if (row == 6)
            {
                if (ExitRequested != null) ExitRequested();
                return;
            }

            switch (row)
            {
                case 0:
                    if (MuteToggle != null) MuteToggle();
                    break;
                case 1:
                    if (AutoGainToggle != null) AutoGainToggle();
                    break;
                case 2:
                    if (zone == 1 && ScaleStep != null) ScaleStep(-1);
                    else if (zone == 2 && ScaleStep != null) ScaleStep(1);
                    break;
                case 3:
                    if (zone == 1 && OpacityStep != null) OpacityStep(-1);
                    else if (zone == 2 && OpacityStep != null) OpacityStep(1);
                    break;
                case 4:
                    if (AutoStartToggle != null) AutoStartToggle();
                    break;
                case 5:
                    if (zone == 1 && SpectrumStep != null) SpectrumStep(-5);
                    else if (zone == 2 && SpectrumStep != null) SpectrumStep(5);
                    break;
            }
            Render();
        }

        #endregion

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _leaveGuard.Dispose();
                _raise.Dispose();
                _surface.Dispose();
                if (_fLabel != null) { _fLabel.Dispose(); _fLabel = null; }
                if (_fValue != null) { _fValue.Dispose(); _fValue = null; }
            }
            base.Dispose(disposing);
        }
    }
}
