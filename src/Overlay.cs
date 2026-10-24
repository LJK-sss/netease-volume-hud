using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NvHud
{
    /// <summary>
    /// 置顶小窗：逐像素 alpha 的分层窗口，绝不抢焦点。
    /// </summary>
    internal sealed class Overlay : Form
    {
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private const int GWL_EXSTYLE = -20;

        private readonly LayeredSurface _surface = new LayeredSurface();
        private readonly Timer _anim;
        private readonly Timer _leaveGuard;

        private float _animT;          // 0 = 常态, 1 = 展开
        private bool _hover;
        private bool _draggingLeft;
        private bool _moving;
        private bool _rightMoved;
        private bool _rightOnGear;   // 右键是否落在"菜单"按钮上（只有按钮才弹菜单）
        private bool _tracking;
        private Point _moveCursorStart;
        private Point _moveWindowStart;
        private bool _anchorBottom;

        private int _wx, _wy;
        private bool _posReady;

        // 显示状态
        private int _volume = 55;
        private bool _muted;
        private bool _offline = true;
        private string _title = "";
        private float _loudDb = float.NaN;

        public float UserScale = 1.0f;
        public float Fade = 0.88f;

        public event Action<int> VolumeChanged;     // 0..100
        public event Action MuteToggleRequested;
        public event Action MenuRequested;
        public event Action PositionChanged;
        public event Action ExitRequested;
        public event Action SettingsRequested;

        public int Volume { get { return _volume; } }

        public Overlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.Black;
            DoubleBuffered = false;
            SetStyle(ControlStyles.Opaque, true);

            _anim = new Timer();
            _anim.Interval = 15;
            _anim.Tick += new EventHandler(AnimTick);

            _leaveGuard = new Timer();
            _leaveGuard.Interval = 120;
            _leaveGuard.Tick += new EventHandler(LeaveGuardTick);

            Native.EnableDpiAware();
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

        public void SetPosition(int x, int y, bool save)
        {
            _wx = x; _wy = y;
            _posReady = true;
            if (Handle != IntPtr.Zero)
                PushTop(_wx, _wy, Width, Height,
                    Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            if (save && PositionChanged != null) PositionChanged();
        }


        /// <summary>有弹出面板/菜单时，回调返回 true，小窗就不再抢占 Z 序。</summary>
        public Func<bool> PopupVisible;

        private void PushTop(int x, int y, int w, int h, uint flags)
        {
            if (Handle == IntPtr.Zero) return;
            if (PopupVisible != null && PopupVisible())
            {
                // 只改位置/尺寸，不动 Z 序，避免把菜单压到下面
                Native.SetWindowPos(Handle, IntPtr.Zero, x, y, w, h, flags | Native.SWP_NOZORDER);
                return;
            }
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, w, h, flags);
        }

        public void RaiseTopmost()
        {
            if (Handle != IntPtr.Zero)
                PushTop(0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        public void SetState(int volume, bool muted, bool offline, string title, float loudDb)
        {
            float dbOld = _loudDb;
            _loudDb = loudDb;
            bool changed = volume != _volume || muted != _muted || offline != _offline
                           || (title ?? "") != (_title ?? "")
                           || Math.Abs(FormatDb(dbOld) - FormatDb(loudDb)) > 0.049f;
            _volume = volume; _muted = muted; _offline = offline;
            if (title == null) title = "";
            _title = title;
            if (changed) Render();
        }

        public void ShowOverlay()
        {
            Show();
            _posReady = false;
            Native.EnableDpiAware();
            Render();
        }

        private static float FormatDb(float v)
        {
            return float.IsNaN(v) ? -999f : (float)Math.Round(v, 1);
        }

        #region 绘制

        private float BaseScale
        {
            get
            {
                float s = Handle != IntPtr.Zero ? Native.GetScale(Handle) : 1f;
                return s * UserScale;
            }
        }

        private void Render()
        {
            if (Handle == IntPtr.Zero || !Visible) return;

            float scale = BaseScale;
            bool hoverNow = _animT > 0.5f;

            // 展开时高度按动画插值
            float hLogical = Renderer.H_NORMAL + (Renderer.H_HOVER - Renderer.H_NORMAL) * _animT;
            float wLogical = Renderer.W_NORMAL + (Renderer.W_HOVER - Renderer.W_NORMAL) * _animT;

            int pw = Math.Max(8, (int)Math.Round(wLogical * scale));
            int ph = Math.Max(8, (int)Math.Round(hLogical * scale));

            var st = new UiState();
            st.Scale = scale;
            st.Opacity = Fade;
            st.Hover = hoverNow;
            st.Muted = _muted;
            st.Offline = _offline;
            st.Volume = _volume;
            st.Title = _title;
            st.LoudDb = _loudDb;

            // 位置与尺寸
            if (!_posReady) { _wx = Location.X; _wy = Location.Y; _posReady = true; }
            ApplyGeometry(pw, ph);

            using (var bmp2 = new Bitmap(pw, ph, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var g2 = Graphics.FromImage(bmp2))
            {
                g2.Clear(Color.Transparent);
                DrawInterpolated(g2, st, wLogical, hLogical);
                _surface.CopyFrom(bmp2, Fade);
            }

            _surface.Push(Handle, _wx, _wy);
        }

        /// <summary>按插值尺寸绘制（复用 Renderer 的逻辑，但宽高为动画中间值）。</summary>
        private static void DrawInterpolated(Graphics g, UiState st, float w, float h)
        {
            // Renderer.Draw 内部按 LogicalSize(hover) 取尺寸；这里用临时状态让它画到中间尺寸：
            // 把 hover 视为 true 只有当插值过半，并额外用裁剪避免超界。
            float scale = st.Scale;
            g.ScaleTransform(scale, scale);
            var forced = new UiState();
            forced.Scale = 1f;
            forced.Opacity = st.Opacity;
            forced.Hover = st.Hover;
            forced.Muted = st.Muted;
            forced.Offline = st.Offline;
            forced.Volume = st.Volume;
            forced.Title = st.Title;
            forced.LoudDb = st.LoudDb;

            // 以目标尺寸为画布，绘制后若中间尺寸更小则整体缩放，保证不裁切
            SizeF target = Renderer.LogicalSize(st.Hover);
            float px = w / target.Width;
            float py = h / target.Height;
            g.ScaleTransform(px, py);
            g.SetClip(new RectangleF(0, 0, target.Width, target.Height));
            Renderer.Draw(g, forced);
        }

        private void ApplyGeometry(int pw, int ph)
        {
            if (Width != pw || Height != ph)
            {
                bool anchorBottom = _anchorBottom;
                int newY = anchorBottom ? (_wy + Height - ph) : _wy;
                SetBounds(_wx, newY, pw, ph, BoundsSpecified.All);
                _wy = newY;
                PushTop(_wx, _wy, pw, ph,
                    Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            }
        }

        #endregion

        #region 鼠标交互

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312)   // WM_HOTKEY：Ctrl+↑/↓（仅鼠标悬停时注册）
            {
                int id = m.WParam.ToInt32();
                EmitVolume(Clamp(_volume + (id == 1 ? 2 : -2)));
                return;
            }

            if (m.Msg == Native.WM_MOUSEACTIVATE)
            {
                m.Result = (IntPtr)Native.MA_NOACTIVATE;
                return;
            }
            if (m.Msg == Native.WM_MOUSEMOVE && !_tracking)
            {
                var tme = new Native.TRACKMOUSEEVENT();
                tme.cbSize = Marshal.SizeOf(typeof(Native.TRACKMOUSEEVENT));
                tme.dwFlags = Native.TME_LEAVE;
                tme.hwndTrack = Handle;
                Native.TrackMouseEvent(ref tme);
                _tracking = true;
            }
            base.WndProc(ref m);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            SetHover(true);
            SetHotkeys(true);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            SetHotkeys(false);
            _tracking = false;
            _leaveGuard.Stop();
            _leaveGuard.Start();
        }

        private void LeaveGuardTick(object sender, EventArgs e)
        {
            _leaveGuard.Stop();
            if (_draggingLeft || _moving) return;
            if (ClientRectangle.Contains(PointToClient(Cursor.Position))) return;
            SetHover(false);
        }

        private void SetHover(bool on)
        {
            if (_hover == on) return;
            _hover = on;
            if (on)
            {
                // 判断是否要向上展开，避免超出工作区
                Screen scr = Screen.FromPoint(new Point(_wx, _wy));
                float scale = BaseScale;
                int futureH = (int)(Renderer.H_HOVER * scale);
                _anchorBottom = (_wy + futureH) > scr.WorkingArea.Bottom;
            }
            _anim.Start();
        }

        private void AnimTick(object sender, EventArgs e)
        {
            float target = _hover ? 1f : 0f;
            float dt = 0.22f;
            if (Math.Abs(_animT - target) <= dt)
            {
                _animT = target;
                _anim.Stop();
            }
            else
            {
                _animT += (target > _animT) ? dt : -dt;
            }
            Render();
        }

        private float BarStartX
        {
            get
            {
                float iconD = _animT > 0.5f ? 40f : 28f;
                return 9f + iconD + 9f;
            }
        }

        private float BarWidth
        {
            get
            {
                float w = Renderer.W_NORMAL + (Renderer.W_HOVER - Renderer.W_NORMAL) * _animT;
                return w - BarStartX - 11f;
            }
        }

        private int VolumeAt(Point p)
        {
            float scale = BaseScale;
            float lx = p.X / scale;
            float t = (lx - BarStartX) / BarWidth;
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;
            return (int)Math.Round(t * 100f);
        }

        // 拖动区：左侧图标列（光标会变成四向箭头）；调音量：条子；右上角小叉：退出
        private bool IsOnIcon(Point p)
        {
            float s = BaseScale;
            float lx = p.X / s;
            float iconD = _animT > 0.5f ? 40f : 28f;
            return lx <= 9f + iconD + 3f;
        }

        private bool IsOnGear(Point p)
        {
            if (_animT < 0.7f) return false;
            float s = BaseScale;
            float lx = p.X / s, ly = p.Y / s;
            return lx >= Renderer.W_HOVER - 52f && lx <= Renderer.W_HOVER - 28f && ly >= 3f && ly <= 30f;
        }

        private bool IsOnClose(Point p)
        {
            if (_animT < 0.7f) return false;   // 只在展开后才有 ×
            float s = BaseScale;
            float lx = p.X / s, ly = p.Y / s;
            float w = Renderer.W_HOVER;
            return lx >= w - 28f && lx <= w - 3f && ly >= 3f && ly <= 30f;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                if (IsOnClose(e.Location))
                {
                    if (ExitRequested != null) ExitRequested();
                    return;
                }
                if (IsOnGear(e.Location))
                {
                    if (SettingsRequested != null) SettingsRequested();
                    return;
                }
                if (IsOnIcon(e.Location))
                {
                    StartMove();
                    return;
                }
                _draggingLeft = true;
                EmitVolume(VolumeAt(e.Location));
                Capture = true;
            }
            else if (e.Button == MouseButtons.Right)
            {
                _rightOnGear = IsOnGear(e.Location);
                StartMove();
            }
            else if (e.Button == MouseButtons.Middle)
            {
                if (MuteToggleRequested != null) MuteToggleRequested();
            }
        }

        private void StartMove()
        {
            _moving = true;
            _rightMoved = false;
            _moveCursorStart = Cursor.Position;
            _moveWindowStart = new Point(_wx, _wy);
            Capture = true;
            Cursor = Cursors.SizeAll;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_moving)
            {
                Point cur = Cursor.Position;
                int dx = cur.X - _moveCursorStart.X;
                int dy = cur.Y - _moveCursorStart.Y;
                if (!_rightMoved && (Math.Abs(dx) > 3 || Math.Abs(dy) > 3)) _rightMoved = true;
                if (_rightMoved)
                {
                    _wx = _moveWindowStart.X + dx;
                    _wy = _moveWindowStart.Y + dy;
                PushTop(_wx, _wy, Width, Height,
                    Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
                }
                Cursor = Cursors.SizeAll;
                return;
            }

            if (_draggingLeft)
            {
                EmitVolume(VolumeAt(e.Location));
                return;
            }

            if (IsOnClose(e.Location)) Cursor = Cursors.Hand;
            else if (IsOnGear(e.Location)) Cursor = Cursors.Hand;
            else if (IsOnIcon(e.Location)) Cursor = Cursors.SizeAll;
            else Cursor = Cursors.Hand;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                if (_draggingLeft)
                {
                    _draggingLeft = false;
                    Capture = false;
                }
                else if (_moving)
                {
                    _moving = false;
                    Capture = false;
                    ClampToScreen();
                    if (PositionChanged != null) PositionChanged();
                }
            }
            else if (e.Button == MouseButtons.Right)
            {
                if (_moving)
                {
                    _moving = false;
                    Capture = false;
                    if (!_rightMoved)
                    {
                        if (_rightOnGear && MenuRequested != null) MenuRequested();
                    }
                    else
                    {
                        ClampToScreen();
                        if (PositionChanged != null) PositionChanged();
                    }
                }
            }
        }


        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            // 滚轮不再改音量（会与 Windows 的 Ctrl+滚轮缩放冲突）。
            // 调音量请用 Ctrl+↑ / ↓（鼠标停在小窗上时生效）。
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const uint MOD_CONTROL = 0x0002;
        private const uint VK_UP = 0x26;
        private const uint VK_DOWN = 0x28;
        private bool _hotkeysOn;

        /// <summary>
        /// 只在鼠标悬停于卡片时注册 Ctrl+↑/↓。
        /// 这样不会占用其它程序的组合键，离开小窗立刻归还。
        /// </summary>
        private void SetHotkeys(bool on)
        {
            if (on == _hotkeysOn) return;
            if (on)
            {
                bool a = RegisterHotKey(Handle, 1, MOD_CONTROL, VK_UP);
                bool b = RegisterHotKey(Handle, 2, MOD_CONTROL, VK_DOWN);
                _hotkeysOn = a || b;
            }
            else
            {
                UnregisterHotKey(Handle, 1);
                UnregisterHotKey(Handle, 2);
                _hotkeysOn = false;
            }
        }

        private static int Clamp(int v)
        {
            if (v < 0) return 0;
            if (v > 100) return 100;
            return v;
        }

        private void EmitVolume(int v)
        {
            v = Clamp(v);
            if (v == _volume) return;
            _volume = v;
            Render();
            if (VolumeChanged != null) VolumeChanged(v);
        }

        public void ClampToScreen()
        {
            Screen scr = Screen.FromPoint(new Point(_wx + Width / 2, _wy + Height / 2));
            Rectangle wa = scr.WorkingArea;
            int x = _wx, y = _wy;
            if (x < wa.Left) x = wa.Left;
            if (y < wa.Top) y = wa.Top;
            if (x + Width > wa.Right) x = wa.Right - Width;
            if (y + Height > wa.Bottom) y = wa.Bottom - Height;
            if (x != _wx || y != _wy)
            {
                _wx = x; _wy = y;
                PushTop(_wx, _wy, Width, Height,
                    Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            }
        }

        /// <summary>参数按钮中心点的屏幕坐标。</summary>
        public Point GearScreenPos()
        {
            float s = BaseScale;
            return new Point(_wx + (int)Math.Round((Renderer.W_HOVER - 40f) * s),
                             _wy + (int)Math.Round(17f * s));
        }

        public void ForceRender()
        {
            Render();
        }

        #endregion

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _anim.Dispose();
                _leaveGuard.Dispose();
                SetHotkeys(false);
                _surface.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
