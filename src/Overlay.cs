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
        private float _marqueePos;
        private float _marqueeMax;
        private bool _marqueeAtEnd;
        private bool _marqueeActive;
        private DateTime _marqueeLastStep = DateTime.MinValue;
        private DateTime _marqueeHoldUntil = DateTime.MinValue;
        // 跑马灯参数（右键菜单「走马灯参数」里可调）
        public int MarqueeStep = 1;        // 每帧位移（逻辑像素）
        public int MarqueeFps = 15;         // 刷新率
        public int MarqueeHoldMs = 2000;   // 开头/结尾停顿

        private readonly Timer _leaveGuard;

        // 吊钟：相位一直前进，幅度跟播放活动走；空闲时收幅到 0，像钟摆自然停下
        private double _clockPhaseMs;
        private float _clockAmp;
        private DateTime _clockLast = DateTime.Now;
        private const float ClockMaxAmp = 12f;      // ±12°
        private const double ClockPeriodMs = 2400.0;  // 一个来回（放慢：1600 → 2400）

        // 顶边小人：站在卡片上边缘那块 196px 的“地面”上来回漂
        private float _whaleX = 40f;           // 脚底中心（卡片逻辑坐标）：默认贴左边站
        private float _whaleFrom;              // 本段漂移的起点
        private float _whaleTo;                // 这一段漂移的终点
        private float _whaleBob;               // 上下浮动
        private float _whaleDir = 1f;          // 行进方向：+1 向右 / -1 向左
        private float _whaleFace = 1f;         // 视觉水平缩放，朝 _whaleDir 平滑过渡（转身动画）
        private int _whaleFrame;               // 精灵图帧号（整张 sheet 的绝对帧号）
        private bool _whaleWalking;
        private double _whaleWalkMs;           // 本段已走时间
        private double _whaleWalkDur;          // 本段总时长（按距离/速度算）

        // ---- 小动作状态机 ----
        // 动作只在这里决定"画第几帧"，最终仍只写 UiState.WhaleFrame 一个字段，
        // 所以 DrawInterpolated 那份手抄的 forced 状态不需要新增同步项。
        private const int ActIdle = 0;          // 站（含眨眼）
        private const int ActHum = 1;           // 悠闲哼歌
        private const int ActYawn = 2;          // 打哈欠
        private const int ActDoze = 3;          // 打盹
        private const int ActStartle = 4;       // 被吓一跳（切歌触发）
        private int _whaleAct = ActIdle;
        private int _whaleActFrame;             // 动作内帧序号（0 起）
        private double _whaleActFrameMs;        // 当前帧已持续
        private double _whaleActLeftMs;         // 本动作剩余总时长
        private volatile bool _whaleStartleReq; // 切歌时置位，下一帧转入 Startle
        private volatile bool _whaleStartlePending; // 切歌时正忙着（走路/演别的）：回到 Idle 后补演
        private double _whaleWaitMs;           // 还站多久才开始走
        private double _whalePhaseMs;          // 浮动相位
        private double _whaleBlinkMs;          // 距下次眨眼
        private double _whaleBlinkLeft;        // 本次眨眼剩余时间
        private DateTime _whaleLast = DateTime.Now;
        private readonly Random _whaleRnd = new Random();
        private const float WhaleSpeed = 27f;      // 平均漂移速度（逻辑像素/秒）
        private const float WhaleStepPx = 3.4f;    // 每走这么多像素换一帧踏步：脚步跟着地面走，不会「原地暴走」
        private const double WhaleTurnMs = 100.0;  // 转身（水平镜像翻过去）用时，太长会被看出「压扁」
        // 地面左右界：卡片四角是 18px 圆角，脚底中心要留出半个身位，不然会悬在弧线外
        private const float WhaleMinX = 34f;
        private const float WhaleMaxX = 162f;

        private float _animT = 1f;    // 永久展开（不再做高度动画）
        private bool _hover;
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
        public event Action AvatarPickerRequested;   // 右键头像：打开选择器

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
            _leaveGuard.Interval = 80;   // 悬停期间持续巡检；截图工具吞掉 MouseLeave 时也能恢复
            _leaveGuard.Tick += new EventHandler(LeaveGuardTick);


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
            // 标题变了（切歌）：悬停中要重新判断是否需要滚动，顺便让小人吓一跳
            if (_title != lastTitle)
            {
                StartMarquee();            // 常驻：切歌就重新判断长度
                _whaleStartleReq = true;   // TickWhale 里消费，转入 Startle
            }
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

            // 展开时高度按动画插值（+TopPad：顶部那条透明带是给小人的）
            float hLogical = Renderer.H_NORMAL + (Renderer.H_HOVER - Renderer.H_NORMAL) * _animT + Renderer.TopPad;
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
            st.ClockAngle = (float)(_clockAmp * Math.Sin(_clockPhaseMs * Math.PI * 2.0 / ClockPeriodMs));
            st.WhaleX = _whaleX;
            st.WhaleBob = _whaleBob;
            st.WhaleFaceX = _whaleFace;
            st.WhaleFrame = _whaleFrame;
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
            forced.ClockAngle = st.ClockAngle;   // 漏这一行钟就是死的：绘制永远走 forced
            forced.WhaleX = st.WhaleX;           // 同理，这几个不抄过去小人就不动/不转身
            forced.WhaleBob = st.WhaleBob;
            forced.WhaleFaceX = st.WhaleFaceX;
            forced.WhaleFrame = st.WhaleFrame;
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
                // 高度变化时保持底边不动：窗口增高（顶部多了小人的透明带）时卡片看着没挪地方，
                // 小人长在卡片上边缘上方。原来只有 hover 展开才需要 anchorBottom，这里统一按底边锚。
                int oldH = Height;
                int newY = (oldH > 0 && oldH != ph) ? (_wy + oldH - ph) : _wy;

                Screen scr = Screen.FromPoint(new Point(_wx, _wy));
                if (newY < scr.WorkingArea.Top) newY = scr.WorkingArea.Top;

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

            // 顶部那条 54px 的透明带不该挡住桌面上其他窗口：按像素 alpha 决定是否穿透。
            // 卡片本体一律 HTCLIENT，避免每次移动都要查像素。
            if (m.Msg == 0x0084)   // WM_NCHITTEST
            {
                long lp = m.LParam.ToInt64();
                int sx = unchecked((short)(lp & 0xFFFF));
                int sy = unchecked((short)((lp >> 16) & 0xFFFF));
                Point cp = PointToClient(new Point(sx, sy));

                bool inTop = cp.Y >= 0 && cp.X >= 0
                          && cp.X < ClientSize.Width && cp.Y < (int)Math.Round(Renderer.TopPad * BaseScale);

                if (!inTop)
                {
                    m.Result = (IntPtr)1;      // HTCLIENT
                    return;
                }

                bool solid = false;
                if (_buf != null && cp.X < _buf.Width && cp.Y < _buf.Height)
                {
                    try { solid = _buf.GetPixel(cp.X, cp.Y).A > 10; }
                    catch { solid = false; }
                }
                m.Result = (IntPtr)(solid ? 1 : -1);   // 角色身上可点，其余穿透
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
            // 顶部 54px 是透明带：从那里进入窗口不算悬停，不然路过小人头顶按钮就冒出来
            if (IsOverCard())
            {
                SetHover(true);
                SetHotkeys(true);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            SetHotkeys(false);
            _tracking = false;
            // 巡检器由 SetHover 统一管理；这里不重复启动，避免悬停关闭后仍空转。
        }

        private void LeaveGuardTick(object sender, EventArgs e)
        {
            // 微信/QQ 截图等全局工具可能吞掉 MouseLeave；悬停期间持续核对真实光标位置。
            if (_moving) return;
            if (IsOverCard()) return;
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
                int futureH = (int)Math.Round(Renderer.LogicalSize(true).Height * scale);
                _anchorBottom = (_wy + futureH) > scr.WorkingArea.Bottom;
                _leaveGuard.Start();
            }
            else
            {
                _leaveGuard.Stop();
            }
            Render();   // 尺寸不再变，只需重绘（决定按钮是否出现）
        }


        /// <summary>仅当悬停且标题确实溢出时，才启动滚动。</summary>
        private void StartMarquee()
        {
            StopMarquee();
            if (string.IsNullOrEmpty(_title)) return;
            float textW = Renderer.MeasureTitleWidth(_title);
            float avail = Renderer.TitleViewportW;
            _marqueeMax = textW - avail + 8f;      // 8px 尾巴余量
            if (_marqueeMax < 6f) return;          // 没溢出就不动
            _marqueePos = 0f;
            _marqueeAtEnd = false;
            _marqueeActive = true;
            _marqueeLastStep = DateTime.Now;
            _marqueeHoldUntil = DateTime.Now.AddMilliseconds(MarqueeHoldMs);   // 开头停顿
        }

        private void StopMarquee()
        {
            _marqueeActive = false;
            _marqueeAtEnd = false;
            _marqueeMax = 0f;
            if (_marqueePos != 0f) { _marqueePos = 0f; Render(); }
        }

        /// <summary>
        /// 由外部统一调度（和频谱共用一个定时器）：到点就前进一个步长。
        /// 返回 true 表示这一帧位置变了、需要重绘。
        /// </summary>
        public bool TickMarquee()
        {
            if (!_marqueeActive) return false;
            DateTime now = DateTime.Now;
            if (now < _marqueeHoldUntil) return false;

            if (_marqueeAtEnd)
            {
                _marqueePos = 0f;
                _marqueeAtEnd = false;
                _marqueeLastStep = now;
                _marqueeHoldUntil = now.AddMilliseconds(MarqueeHoldMs);
                return true;
            }

            int fps = MarqueeFps;
            if (fps < 2) fps = 2;
            if (fps > 60) fps = 60;
            if ((now - _marqueeLastStep).TotalMilliseconds < 1000.0 / fps - 10) return false;   // 容差：定时器精度有限
            _marqueeLastStep = now;

            float step = MarqueeStep;
            if (step < 1f) step = 1f;
            _marqueePos += step;
            if (_marqueePos >= _marqueeMax)
            {
                _marqueePos = _marqueeMax;
                _marqueeAtEnd = true;
                _marqueeHoldUntil = now.AddMilliseconds(MarqueeHoldMs);
            }
            return true;
        }

        /// <summary>
        /// 吊钟摆动的推进：跟着统一渲染调度走，不额外开定时器。
        /// active = 这一帧本来就有东西要重绘（频谱/走马灯在动）。
        /// 返回 true 表示钟还在动、这一帧需要重绘。
        /// </summary>
        public bool TickClock(bool active)
        {
            DateTime now = DateTime.Now;
            double dt = (now - _clockLast).TotalMilliseconds;
            _clockLast = now;
            if (dt < 0) dt = 0;
            if (dt > 120) dt = 120;      // 掉帧或休眠回来时别让相位跳一大截

            _clockPhaseMs += dt;

            if (active)
            {
                // 起摆约 0.7 秒到满幅，避免一出声就猛地甩起来
                _clockAmp += (float)(dt * (ClockMaxAmp / 700.0));
                if (_clockAmp > ClockMaxAmp) _clockAmp = ClockMaxAmp;
                return true;
            }

            if (_clockAmp <= 0.12f)
            {
                _clockAmp = 0f;
                return false;                // 收干净了：回到"空闲不重绘"
            }

            // 收幅：约 1.2 秒降到 30%，观感就是钟摆慢慢停下
            _clockAmp = (float)(_clockAmp * Math.Pow(0.30, dt / 1000.0));
            return true;
        }

#if !NOWHALE
        /// <summary>
        /// 顶边小人的推进：站着呼吸眨眼 → 漂到新位置 → 再站着，循环。
        /// 挂在 15fps 统一调度上，不额外开定时器。她一直在动，所以恒返回 true。
        /// </summary>
        public bool TickWhale()
        {
            DateTime now = DateTime.Now;
            double dt = (now - _whaleLast).TotalMilliseconds;
            _whaleLast = now;
            if (dt < 0) dt = 0;
            if (dt > 200) dt = 200;     // 休眠回来别让位移跳一大截

            _whalePhaseMs += dt;
            _whaleBlinkMs -= dt;

            // 切歌：正在走就不打断（踩空很怪），正在演打哈欠/打盹也不硬切，
            // 先记下来，等回到 Idle 再补演一次「被吓一跳」
            if (_whaleStartleReq)
            {
                _whaleStartleReq = false;
                if (!_whaleWalking && _whaleAct == ActIdle) StartWhaleAction(ActStartle);
                else _whaleStartlePending = true;
            }
            else if (_whaleStartlePending && !_whaleWalking && _whaleAct == ActIdle)
            {
                _whaleStartlePending = false;
                StartWhaleAction(ActStartle);
            }

            // 眨眼只在 Idle 里计时，否则小动作播完回到 Idle 会立刻闭一次眼
            if (_whaleAct == ActIdle)
            {
                if (_whaleBlinkLeft > 0)
                {
                    _whaleBlinkLeft -= dt;
                    if (_whaleBlinkLeft <= 0)
                    {
                        _whaleBlinkLeft = 0;
                        _whaleBlinkMs = 2600 + _whaleRnd.Next(4200);
                    }
                }
                else if (_whaleBlinkMs <= 0)
                {
                    _whaleBlinkLeft = 170;
                }
            }

            // 素材原图偏朝画面左侧（长发拖在右侧 = 身后），所以「向右走」才需要镜像。
            // 即视觉缩放的目标值和行进方向相反：dir=+1（往右）→ face=-1（翻过去）。
            float faceTarget = -_whaleDir;

            // 转身：130ms 内线性翻过去，中间两帧像侧身
            float turn = (float)(dt / WhaleTurnMs);
            if (_whaleFace < faceTarget)
            {
                _whaleFace += turn;
                if (_whaleFace > faceTarget) _whaleFace = faceTarget;
            }
            else if (_whaleFace > faceTarget)
            {
                _whaleFace -= turn;
                if (_whaleFace < faceTarget) _whaleFace = faceTarget;
            }

            if (_whaleWalking)
            {
                _whaleWalkMs += dt;
                float t = _whaleWalkDur <= 0 ? 1f : (float)(_whaleWalkMs / _whaleWalkDur);
                if (t > 1f) t = 1f;

                // smoothstep：起步加速、到站减速。匀速平移看着像「滑」，有加减速才像在走。
                float e = t * t * (3f - 2f * t);
                _whaleX = _whaleFrom + (_whaleTo - _whaleFrom) * e;

                if (t >= 1f)
                {
                    _whaleX = _whaleTo;
                    _whaleWalking = false;
                    _whaleFrame = WhaleSprite.IdleStart;
                    _whaleWaitMs = 1500 + _whaleRnd.Next(2600);   // 到站站 1.5~4.1 秒
                }
                else
                {
                    // 踏步帧由「已走过的距离」决定，而不是固定时间：
                    // 加减速时脚步自动跟着变快变慢，不会脚在原地快踏、人却在滑
                    float travelled = Math.Abs(_whaleX - _whaleFrom);
                    int k = (int)(travelled / WhaleStepPx) % WhaleSprite.WalkCount;
                    _whaleFrame = WhaleSprite.WalkStart + k;

                    // 上下起伏跟踏步同相位：一步一个起伏，不会出现「人抖脚不抖」的错拍
                    float phase = travelled / (WhaleStepPx * WhaleSprite.WalkCount);
                    _whaleBob = (float)Math.Sin(phase * Math.PI * 2.0) * 0.9f;
                }
            }
            else if (_whaleAct != ActIdle)
            {
                // ---- 站着演小动作（哼歌 / 打哈欠 / 打盹 / 受惊）----
                int count; double totalMs;
                WhaleActionSpec(_whaleAct, out count, out totalMs);
                double frameMs = totalMs / count;

                _whaleActLeftMs -= dt;
                _whaleActFrameMs += dt;
                if (_whaleActFrameMs >= frameMs)
                {
                    _whaleActFrameMs -= frameMs;
                    if (_whaleActFrame < count - 1) _whaleActFrame++;
                }

                if (_whaleActLeftMs <= 0)
                {
                    _whaleAct = ActIdle;
                    _whaleActFrame = 0;
                    _whaleBlinkMs = 1400 + _whaleRnd.Next(2400);
                    // 演完多站一会儿再决定下一步：动作连着来会显得很碎
                    _whaleWaitMs = 2200 + _whaleRnd.Next(3200);
                }

                _whaleFrame = WhaleActionFrame();
                _whaleBob = (float)Math.Sin(_whalePhaseMs * Math.PI * 2.0 / 3400.0) * 0.45f;
            }
            else
            {
                // ---- Idle：等一会儿，然后要么演个小动作，要么漂走 ----
                _whaleWaitMs -= dt;
                _whaleFrame = WhaleActionFrame();
                _whaleBob = (float)Math.Sin(_whalePhaseMs * Math.PI * 2.0 / 3400.0) * 0.45f;

                if (_whaleWaitMs <= 0)
                {
                    if (_whaleRnd.NextDouble() < 0.55)
                    {
                        // 哼歌 / 打哈欠 / 打盹 三选一
                        StartWhaleAction(ActHum + _whaleRnd.Next(3));
                    }
                    else
                    {
                        // 挑个新目标，至少挪 28px，避免原地微动
                        float target = WhaleMinX + (float)_whaleRnd.NextDouble() * (WhaleMaxX - WhaleMinX);
                        for (int i = 0; i < 8 && Math.Abs(target - _whaleX) < 28f; i++)
                            target = WhaleMinX + (float)_whaleRnd.NextDouble() * (WhaleMaxX - WhaleMinX);

                        _whaleFrom = _whaleX;
                        _whaleTo = target;
                        _whaleDir = target > _whaleX ? 1f : -1f;   // 先决定朝向，转身动画随即开始
                        float dist = Math.Abs(target - _whaleX);
                        _whaleWalkDur = Math.Max(520.0, dist / WhaleSpeed * 1000.0);
                        _whaleWalkMs = 0;
                        _whaleWalking = true;
                        _whaleFrame = WhaleSprite.WalkStart;
                    }
                }
            }
            return true;
        }

        /// <summary>动作的帧数与每帧时长（毫秒）。改节奏就改这里。</summary>
        private static void WhaleActionSpec(int act, out int count, out double totalMs)
        {
            switch (act)
            {
                case ActHum: count = WhaleSprite.HumCount; totalMs = 3200; break;
                case ActYawn: count = WhaleSprite.YawnCount; totalMs = 2600; break;
                case ActDoze: count = WhaleSprite.DozeCount; totalMs = 3600; break;
                case ActStartle: count = WhaleSprite.StartleCount; totalMs = 1900; break;
                default: count = WhaleSprite.IdleCount; totalMs = 2000; break;
            }
        }

        private void StartWhaleAction(int act)
        {
            int count; double totalMs;
            WhaleActionSpec(act, out count, out totalMs);
            _whaleAct = act;
            _whaleActFrame = 0;
            _whaleActFrameMs = 0;
            _whaleActLeftMs = totalMs;
        }

        /// <summary>动作 + 动作内帧序号 → 整张精灵图的绝对帧号。</summary>
        private int WhaleActionFrame()
        {
            switch (_whaleAct)
            {
                case ActHum: return WhaleSprite.HumStart + _whaleActFrame;
                case ActYawn: return WhaleSprite.YawnStart + _whaleActFrame;
                case ActDoze: return WhaleSprite.DozeStart + _whaleActFrame;
                case ActStartle: return WhaleSprite.StartleStart + _whaleActFrame;
                default:
                    return WhaleSprite.IdleStart + (_whaleBlinkLeft > 0 ? 1 : 0);
            }
        }
#endif

        private float BarStartX
        {
            get
            {
                float iconD = 44f;
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


        // 拖动区：左侧图标列（光标会变成四向箭头）；调音量：条子；右上角小叉：退出
        /// <summary>Volume drag area: only the bottom bar strip (with a little grab margin).</summary>

        // 客户端坐标 → 卡片逻辑坐标。y 要减掉顶部那条透明带，否则所有命中区都会错 54px
        private PointF LogicalPoint(Point p)
        {
            float s = BaseScale;
            return new PointF(p.X / s, p.Y / s - Renderer.TopPad);
        }

        private bool IsOnIcon(Point p)
        {
            float lx = LogicalPoint(p).X;
            float iconD = 44f;
            return lx <= 10f + iconD + 3f;
        }


        /// <summary>是否点在圆形头像内部（用于"点一下换下一张"）。</summary>
        private bool IsOnAvatar(Point p)
        {
            PointF q = LogicalPoint(p);
            float lx = q.X, ly = q.Y;
            float iconD = 44f;
            float x0 = 10f, y0 = 26f;
            return lx >= x0 && lx <= x0 + iconD && ly >= y0 && ly <= y0 + iconD;   // 圆角方形：整个框都算
        }
        private bool IsOnGear(Point p)
        {
            if (!_hover) return false;   // 按钮只在悬停时可见，也就只在悬停时可点
            PointF q = LogicalPoint(p);
            float lx = q.X, ly = q.Y;
            return lx >= Renderer.W_HOVER - 52f && lx <= Renderer.W_HOVER - 28f && ly >= 3f && ly <= 30f;
        }

        private bool IsOnClose(Point p)
        {
            if (_animT < 0.7f) return false;   // 只在展开后才有 ×
            PointF q = LogicalPoint(p);
            float lx = q.X, ly = q.Y;
            float w = Renderer.W_HOVER;
            return lx >= w - 28f && lx <= w - 3f && ly >= 3f && ly <= 30f;
        }

        /// <summary>鼠标是否在卡片本体上（顶部的透明带不算，避免路过小人上方就弹出按钮）。</summary>
        private bool IsOverCard()
        {
            Point p = PointToClient(Cursor.Position);
            if (p.X < 0 || p.Y < 0) return false;
            if (p.X >= ClientSize.Width || p.Y >= ClientSize.Height) return false;
            return p.Y >= (int)Math.Round(Renderer.TopPad * BaseScale);
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

            // 从顶部透明带走进卡片区时 OnMouseEnter 不会再触发，这里补一次
            if (!_hover && IsOverCard())
            {
                SetHover(true);
                SetHotkeys(true);
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
                if (_moving)
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
                        if (IsOnAvatar(e.Location)) { if (AvatarPickerRequested != null) AvatarPickerRequested(); }
                        else if (_rightOnGear && MenuRequested != null) MenuRequested();
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
                             _wy + (int)Math.Round((17f + Renderer.TopPad) * s));
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
