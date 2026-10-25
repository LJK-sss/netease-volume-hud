using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NvHud
{
    /// <summary>
    /// 自绘参数面板：玻璃拟态，从按钮的左上方弹出（右下角对齐按钮），不覆盖小窗本身。
    /// </summary>
    internal sealed class ParamPanel : Form
    {
        private readonly LayeredSurface _surface = new LayeredSurface();
        private readonly Timer _raise = new Timer();
        private int _wx, _wy;

        public const float W_LOGICAL = 216f;
        public const float ROW_H = 30f;
        public const float PAD_Y = 7f;
        public const int ROWS = 9;

        public float H_LOGICAL { get { return PAD_Y * 2f + ROW_H * ROWS; } }

        public int Volume = 45;
        public float TargetDb = -20f;
        public int MeasureSec = 8;
        public int CacheTrustSec = 20;
        public int BgLines = 22;
        public float LimitMargin = 4f;
        public float Tolerance = 1.5f;
        public bool AutoGainOn = true;

        public int HoverRow = -1;
        public int HoverZone;   // 0 无, 1 左箭头, 2 右箭头, 3 整行

        public event Action<int> VolumeStep;
        public event Action<int> TargetStep;
        public event Action<int> MeasureStep;
        public event Action<int> TrustStep;
        public event Action<int> BgLinesStep;
        public event Action<int> MarginStep;
        public event Action<int> ToleranceStep;
        public event Action ToggleAutoGain;
        public event Action ResetDefaults;

        public ParamPanel()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.Black;
            SetStyle(ControlStyles.Opaque, true);
            _raise.Interval = 150;
            _raise.Tick += delegate { RaiseTopmost(); };
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

        /// <summary>锚点（屏幕坐标，通常是参数按钮的中心）；面板右下角对齐到它左上方。</summary>
        public void ShowAt(int anchorX, int anchorY)
        {
            float s = Handle != IntPtr.Zero ? DpiScale : 1f;
            if (!Visible) Show();
            s = DpiScale;

            int w = (int)Math.Round(W_LOGICAL * s);
            int h = (int)Math.Round(H_LOGICAL * s);

            int x = anchorX - w + (int)Math.Round(12 * s);
            int y = anchorY - h - (int)Math.Round(2 * s);

            // 贴边修正
            Screen scr = Screen.FromPoint(new Point(anchorX, anchorY));
            Rectangle wa = scr.WorkingArea;
            if (x < wa.Left) x = wa.Left;
            if (y < wa.Top) y = anchorY + (int)Math.Round(6 * s);   // 上方放不下就往下弹
            if (x + w > wa.Right) x = wa.Right - w;

            _wx = x; _wy = y;
            SetBounds(x, y, w, h, BoundsSpecified.All);
            Render();
            _raise.Start();
        }

        private void RaiseTopmost()
        {
            if (!Visible || Handle == IntPtr.Zero) return;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) _raise.Start(); else _raise.Stop();
        }

        public void Refresh_()
        {
            if (Visible) Render();
        }

        private void Render()
        {
            if (!Visible || Handle == IntPtr.Zero) { _raise.Stop(); return; }

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

        private Font _fLabel, _fValue, _fBtn;
        private StringFormat _sfL, _sfR, _sfC;

        private void EnsureFonts()
        {
            if (_fLabel != null) return;
            _fLabel = new Font("Microsoft YaHei UI", 11.5f, FontStyle.Regular, GraphicsUnit.Pixel);
            _fValue = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
            _fBtn = new Font("Microsoft YaHei UI", 11f, FontStyle.Regular, GraphicsUnit.Pixel);
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

        private void Draw(Graphics g, float s)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;   // 与右键菜单一致，深色玻璃上更锐利
            g.ScaleTransform(s, s);

            float w = W_LOGICAL, h = H_LOGICAL;

            using (var path = Round(0, 0, w, h, 13f))
            {
                using (var fill = new SolidBrush(C(24, 24, 29, 250)))
                    g.FillPath(fill, path);
                using (var hg = new LinearGradientBrush(new PointF(0, 0), new PointF(0, h * 0.5f),
                    C(255, 255, 255, 14), C(255, 255, 255, 0)))
                using (var hp = Round(0, 0, w, h * 0.5f, 13f))
                    g.FillPath(hg, hp);
                using (var pen = new Pen(C(255, 255, 255, 30), 0.8f))
                    g.DrawPath(pen, path);
            }

            EnsureFonts();
            {
                var sfL = _sfL;
                var sfR = _sfR;

                for (int i = 0; i < ROWS; i++)
                {
                    float y = PAD_Y + i * ROW_H;
                    bool hoverRow = (HoverRow == i);

                    if (hoverRow)
                    {
                        using (var hp = Round(4, y + 1, w - 8, ROW_H - 2, 8f))
                        using (var hb = new SolidBrush(C(255, 255, 255, 12)))
                            g.FillPath(hb, hp);
                    }

                    string label;
                    string value = "";
                    bool arrows = true;

                    switch (i)
                    {
                        case 0: label = "基准音量"; value = Volume + "%"; break;
                        case 1: label = "基准响度"; value = TargetDb.ToString("0.0") + " dB"; break;
                        case 2: label = "容差"; value = "±" + Tolerance.ToString("0.0") + " dB"; break;
                        case 3: label = "测量时长"; value = MeasureSec + " 秒"; break;
                        case 4: label = "缓存信任"; value = CacheTrustSec + " 秒"; break;
                        case 5: label = "线稿水印"; value = BgLines + "%"; break;
                        case 6: label = "限幅余量"; value = LimitMargin.ToString("0.0") + " dB"; break;
                        case 7: label = "自动增益"; value = AutoGainOn ? "开" : "关"; arrows = false; break;
                        default: label = "恢复默认"; arrows = false; break;
                    }

                    if (i == ROWS - 1)
                    {
                        // 规整的按钮行：整条胶囊，悬停提亮
                        float bx = 10f, bw = w - 20f, bh = ROW_H - 10f, by = y + 5f;
                        using (var bp = Round(bx, by, bw, bh, bh / 2f))
                        {
                            using (var bb = new SolidBrush(hoverRow ? C(255, 255, 255, 26) : C(255, 255, 255, 10)))
                                g.FillPath(bb, bp);
                            using (var pen = new Pen(C(255, 255, 255, hoverRow ? 46 : 26), 0.8f))
                                g.DrawPath(pen, bp);
                        }
                        using (var tb = new SolidBrush(hoverRow ? C(232, 234, 242, 255) : C(158, 160, 176, 255)))
                            g.DrawString("恢复默认参数", _fBtn, tb, new RectangleF(bx, by, bw, bh), _sfC);
                        continue;
                    }

                    using (var b = new SolidBrush(C(232, 234, 242, 255)))   // 与右键菜单同亮度
                        g.DrawString(label, _fLabel, b, new RectangleF(14, y + 2, 90, ROW_H - 4), sfL);

                    Color vc = (i == 6 && !AutoGainOn) ? C(140, 142, 156, 255) : C(246, 246, 250, 255);
                    using (var b = new SolidBrush(vc))
                        g.DrawString(value, _fValue, b, new RectangleF(w - 132, y + 2, 76, ROW_H - 4), sfR);

                    if (arrows)
                    {
                        DrawChevron(g, w - 40, y + ROW_H / 2f, false, HoverRow == i && HoverZone == 1);
                        DrawChevron(g, w - 18, y + ROW_H / 2f, true, HoverRow == i && HoverZone == 2);
                    }
                    else if (i == 7)
                    {
                        // 开关小胶囊
                        float pw2 = 34f, ph2 = 17f;
                        float px = w - 16 - pw2, py = y + (ROW_H - ph2) / 2f;
                        using (var pp = Round(px, py, pw2, ph2, ph2 / 2f))
                        using (var pb = new SolidBrush(AutoGainOn ? C(255, 74, 105, 235) : C(70, 70, 80, 255)))
                            g.FillPath(pb, pp);
                        float knob = ph2 - 4f;
                        float kx = AutoGainOn ? (px + pw2 - knob - 2f) : (px + 2f);
                        using (var kb = new SolidBrush(C(255, 255, 255, 245)))
                            g.FillEllipse(kb, kx, py + 2f, knob, knob);
                    }
                }
            }
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
            float lx = p.X / s;
            if (lx < 0 || lx > W_LOGICAL) return -1;
            int row = (int)((ly - PAD_Y) / ROW_H);
            if (row < 0 || row >= ROWS) return -1;
            return row;
        }

        private int ZoneAt(Point p, int row)
        {
            float s = DpiScale;
            float lx = p.X / s;
            if (row == 7) return 3;
            if (row == ROWS - 1) return 3;
            if (lx >= W_LOGICAL - 56f && lx < W_LOGICAL - 33f) return 1;
            if (lx >= W_LOGICAL - 33f && lx <= W_LOGICAL - 8f) return 2;
            return 0;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_MOUSEACTIVATE)
            {
                m.Result = (IntPtr)Native.MA_NOACTIVATE;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int r = RowAt(e.Location);
            int z = r >= 0 ? ZoneAt(e.Location, r) : 0;
            if (r != HoverRow || z != HoverZone)
            {
                HoverRow = r; HoverZone = z;
                Render();                          // 立即重绘，不要延迟
                Cursor = (r >= 0) ? Cursors.Hand : Cursors.Default;
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            HoverRow = -1; HoverZone = 0;
            Render();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            int row = RowAt(e.Location);
            if (row < 0) return;
            int zone = ZoneAt(e.Location, row);

            switch (row)
            {
                case 0:
                    if (zone == 1 && VolumeStep != null) VolumeStep(-5);
                    else if (zone == 2 && VolumeStep != null) VolumeStep(5);
                    break;
                case 1:
                    if (zone == 1 && TargetStep != null) TargetStep(-1);
                    else if (zone == 2 && TargetStep != null) TargetStep(1);
                    break;
                case 3:
                    if (zone == 1 && MeasureStep != null) MeasureStep(-2);
                    else if (zone == 2 && MeasureStep != null) MeasureStep(2);
                    break;
                case 2:
                    if (zone == 1 && ToleranceStep != null) ToleranceStep(-1);
                    else if (zone == 2 && ToleranceStep != null) ToleranceStep(1);
                    break;
                case 4:
                    if (zone == 1 && TrustStep != null) TrustStep(-5);
                    else if (zone == 2 && TrustStep != null) TrustStep(5);
                    break;
                case 5:
                    if (zone == 1 && BgLinesStep != null) BgLinesStep(-5);
                    else if (zone == 2 && BgLinesStep != null) BgLinesStep(5);
                    break;
                case 6:
                    if (zone == 1 && MarginStep != null) MarginStep(-1);
                    else if (zone == 2 && MarginStep != null) MarginStep(1);
                    break;
                case 7:
                    if (ToggleAutoGain != null) ToggleAutoGain();
                    break;
                case 8:
                    if (ResetDefaults != null) ResetDefaults();
                    break;
            }
            Render();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _surface.Dispose();
                if (_fLabel != null) { _fLabel.Dispose(); _fLabel = null; }
                if (_fValue != null) { _fValue.Dispose(); _fValue = null; }
                if (_fBtn != null) { _fBtn.Dispose(); _fBtn = null; }
            }
            base.Dispose(disposing);
        }

        #endregion
    }


}
