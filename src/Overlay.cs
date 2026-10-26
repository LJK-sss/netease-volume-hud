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
        private Bitmap _buf;            // 复用绘制缓冲，避免每帧分配
        private Graphics _bufG;
        // 长标题滚动（仅悬停且文字溢出时启用，30fps）
        private readonly Timer _marquee = new Timer();
        private float _marqueePos;
        private float _marqueeMax;
        private bool _marqueeAtEnd;
        private DateTime _marqueeHoldUntil = DateTime.MinValue;
        private DateTime _marqueeEpoch = DateTime.MinValue;   // 本段滚动的起点（时间基准，避免抖动累积）
        private float _marqueeTravel = 1f;                   // 本段滚动总时长（秒）

        private readonly Timer _leaveGuard;

        private float _animT = 1f;    // 永久展开（不再做高度动画）
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
        public event Action AvatarNextRequested;    // 点一下圆形头像：换下一张

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


            _leaveGuard = new Timer();
            _leaveGuard.Interval = 120;
            _leaveGuard.Tick += new EventHandler(LeaveGuardTick);

            _marquee.Interval = 16;             // 60 fps：运动更细腻
            _marquee.Tick += MarqueeTick;

            Native.EnableDpiAware();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // 注意：这里刻意不加 WS_EX_TOOLWINDOW。
                // 直播姬等采集器的枚举条件是"可见 + 非工具窗口"，加了 TOOLWINDOW 就抓不到；
                // 不占任务栏靠 WS_EX_NOACTIVATE + WinForms 的隐藏 owner 窗口保证（见 SuppressTaskbar）。
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        /// <summary>确保不出现在任务栏（去掉 TOOLWINDOW 后需要显式摘一次）。</summary>
        public void SuppressTaskbar()
        {
            if (Handle != IntPtr.Zero) Taskbar.HideFromTaskbar(Handle);
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
                // 有弹出面板/菜单时不抢 Z 序，否则会把它们压到小窗下面
                Native.SetWindowPos(Handle, IntPtr.Zero, x, y, w, h, flags | Native.SWP_NOZORDER);
                return;
            }
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, w, h, flags);
        }

        /// <summary>重申置顶（不抢焦点）。由 15 秒的定时器调用。</summary>
        public void RaiseNoActivate()
        {
            if (Handle == IntPtr.Zero) return;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        public void SetState(int volume, bool muted, bool offline, string title, float loudDb)
        {
            string lastTitle = _title;
            float dbOld = _loudDb;
            _loudDb = loudDb;
            bool changed = volume != _volume || muted != _muted || offline != _offline
                           || (title ?? "") != (_title ?? "")
                           || Math.Abs(FormatDb(dbOld) - FormatDb(loudDb)) > 0.049f;
            _volume = volume; _muted = muted; _offline = offline;
            if (title == null) title = "";
            _title = title;
            // 标题变了（切歌）：悬停中要重新判断是否需要滚动
            if (_title != lastTitle) StartMarquee();   // 常驻：切歌就重新判断长度
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

        /// <summary>外部触发重绘（头像轮换的淡入淡出用）。</summary>
        public void Repaint() { Render(); }

        private void Render()
        {
            if (Handle == IntPtr.Zero || !Visible) return;

            float scale = BaseScale;
            bool hoverNow = true;                 // 卡片永久保持展开布局

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
            st.TitleOffset = _marqueePos;
            st.ShowControls = _hover;             // 只有悬停时才画菜单/关闭按钮
            st.TitleScrolls = _marqueeMax > 0f;    // 溢出滚动中：不显示省略号
            st.Spectrum = Spectrum.Bars;

            // 位置与尺寸
            if (!_posReady) { _wx = Location.X; _wy = Location.Y; _posReady = true; }
            ApplyGeometry(pw, ph);

            if (_buf == null || _buf.Width != pw || _buf.Height != ph)
            {
                if (_bufG != null) { _bufG.Dispose(); _bufG = null; }
                if (_buf != null) { _buf.Dispose(); _buf = null; }
                _buf = new Bitmap(pw, ph, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                _bufG = Graphics.FromImage(_buf);
            }
            _bufG.ResetTransform();
            _bufG.ResetClip();
            _bufG.Clear(Color.Transparent);
            DrawInterpolated(_bufG, st, wLogical, hLogical);
            _surface.CopyFrom(_buf, Fade);

            _surface.Push(Handle, _wx, _wy);

        }

        /// <summary>按插值尺寸绘制（复用 Renderer 的逻辑，但宽高为动画中间值）。</summary>
        private static void DrawInterpolated(Graphics g, UiState st, float w, float h)
        {
            // 用当前状态的中插尺寸绘制（卡片常驻展开，_animT 恒为 1）
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
            forced.TitleOffset = st.TitleOffset;
            forced.ShowControls = st.ShowControls;
            forced.TitleScrolls = st.TitleScrolls;
            forced.Spectrum = st.Spectrum;

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
            RaiseNoActivate();
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
            Render();   // 尺寸不再变，只需重绘（决定按钮是否出现）
        }


        /// <summary>仅当悬停且标题确实溢出时，才启动滚动。</summary>
        private void StartMarquee()
        {
            StopMarquee();
            if (string.IsNullOrEmpty(_title)) return;   // 常驻滚动：不再要求悬停
            float textW = Renderer.MeasureTitleWidth(_title);
            float avail = Renderer.TitleViewportW;
            _marqueeMax = textW - avail + 8f;      // 8px 尾巴余量
            if (_marqueeMax < 6f) return;          // 没溢出就不动
            // 平滑：以 30px/s 为基准，smoothstep 缓入缓出让两端不生硬，
            // 平均速度因此略低，所以总时长按 1.25 倍放宽
            _marqueeTravel = Math.Max(0.8f, _marqueeMax / 30f * 1.25f);
            _marqueePos = 0f;
            _marqueeAtEnd = false;
            _marqueeEpoch = DateTime.Now;
            _marqueeHoldUntil = DateTime.Now.AddSeconds(1);   // 开头停 1 秒
            _marquee.Start();
        }

        private void StopMarquee()
        {
            if (_marquee.Enabled) _marquee.Stop();
            _marqueeAtEnd = false;
            _marqueeMax = 0f;
            if (_marqueePos != 0f) { _marqueePos = 0f; Render(); }
        }

        /// <summary>60fps + 时间基准 + smoothstep 缓入缓出（平均约 24px/s，中段约 36px/s）。</summary>
        private void MarqueeTick(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_title)) { StopMarquee(); return; }
            if (DateTime.Now < _marqueeHoldUntil)
            {
                // 停顿结束时把时间基准对齐，避免停顿被算进滚动时间
                _marqueeEpoch = DateTime.Now;
                return;
            }

            if (_marqueeAtEnd)
            {
                // 尾部停满 1 秒 → 回到开头，重新计时
                _marqueePos = 0f;
                _marqueeAtEnd = false;
                _marqueeEpoch = DateTime.Now;
                _marqueeHoldUntil = DateTime.Now.AddSeconds(1);
                Render();
                return;
            }

            // 按"已经过去多少时间"算位置：定时器抖动不会累积成忽快忽慢
            float t = (float)((DateTime.Now - _marqueeEpoch).TotalSeconds / _marqueeTravel);
            if (t >= 1f)
            {
                _marqueePos = _marqueeMax;
                _marqueeAtEnd = true;
                _marqueeHoldUntil = DateTime.Now.AddSeconds(1);   // 尾部停 1 秒
                Render();
                return;
            }

            // smoothstep：起步和收尾各有一段缓动，中段接近匀速
            float u = t * t * (3f - 2f * t);
            _marqueePos = u * _marqueeMax;
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
        /// <summary>Volume drag area: only the bottom bar strip (with a little grab margin).</summary>
        private bool IsOnBar(Point p)
        {
            float s = BaseScale;
            float lx = p.X / s, ly = p.Y / s;
            float h = Renderer.H_HOVER;
            float bx = BarStartX;
            float bw = BarWidth;
            return ly >= h - 20f && ly <= h            // 只到底部这条（条子 y≈66，上下各留余量）
                && lx >= bx - 8f && lx <= bx + bw + 8f;
        }

        private bool IsOnIcon(Point p)
        {
            float s = BaseScale;
            float lx = p.X / s;
            float iconD = _animT > 0.5f ? 40f : 28f;
            return lx <= 9f + iconD + 3f;
        }


        /// <summary>是否点在圆形头像内部（用于"点一下换下一张"）。</summary>
        private bool IsOnAvatar(Point p)
        {
            float s = BaseScale;
            float lx = p.X / s, ly = p.Y / s;
            float iconD = _animT > 0.5f ? 40f : 28f;
            float cx = 9f + iconD / 2f;
            float cy = Renderer.H_HOVER * Renderer.IconCyRatio;
            float dx = lx - cx, dy = ly - cy;
            return dx * dx + dy * dy <= (iconD / 2f) * (iconD / 2f);
        }
        private bool IsOnGear(Point p)
        {
            if (!_hover) return false;   // 按钮只在悬停时可见，也就只在悬停时可点
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
                StartMove();   // 滑条已移入参数面板：整张卡片都能拖动窗口
                return;
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

            if (_hover && IsOnClose(e.Location)) Cursor = Cursors.Hand;
            else if (_hover && IsOnGear(e.Location)) Cursor = Cursors.Hand;
            else if (IsOnIcon(e.Location)) Cursor = Cursors.SizeAll;
            else Cursor = Cursors.SizeAll;   // 整张卡片可拖动

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
                    bool wasDrag = _rightMoved;
                    if (!wasDrag && IsOnAvatar(e.Location) && AvatarNextRequested != null)
                    {
                        _moving = false;
                        Capture = false;
                        AvatarNextRequested();
                        return;
                    }
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
                _leaveGuard.Dispose();
                SetHotkeys(false);
                if (_bufG != null) { _bufG.Dispose(); _bufG = null; }
                if (_buf != null) { _buf.Dispose(); _buf = null; }
                _surface.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
