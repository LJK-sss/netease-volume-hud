using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace NvHud
{
    internal sealed class App : IDisposable
    {
        private const string ProccessName = "cloudmusic.exe";

        private Config _cfg;
        private Overlay _overlay;
        private AudioSession _session;
        private ProcessCapture _cap;
        private uint _capturePid;
        private DateTime _lastPidCheck = DateTime.MinValue;   // 绑定的进程是否还活着
        private DateTime _cacheTrustUntil = DateTime.MinValue;   // 缓存歌的"信任期"：这段时间内不自动改音量
        private DateTime _lastCapTry = DateTime.MinValue;
        private AutoGain _gain;
        private readonly Timer _audioTimer = new Timer();
        private readonly Timer _panelGuard = new Timer();
        private ParamPanel _panel;
        private DateTime _lastGainLog = DateTime.MinValue;
        private string _lastDefaultId = "";
        private DateTime _driftUntil = DateTime.MinValue;
        private float _fastDb = -120f;            // 快速电平（限幅器用）
        private float _committed = 0.40f;           // 本曲"应该"的音量
        private float _limDb = 0f;
        private DateTime _holdUntil = DateTime.MinValue;
        private int _limHits;
        private bool _firstApplied;
        private bool _limFeedbackUsed;
        private DateTime _lastLimHit = DateTime.MinValue;
        private DateTime _limCooldownUntil = DateTime.MinValue;
        private double _limExcess;
        private DateTime _limWindow = DateTime.MinValue;                  // 限幅器临时偏移（≤0）
        private DateTime _measureMuteUntil = DateTime.MinValue;
        private DateTime _lastDbg = DateTime.MinValue;
        private static string SongsPath { get { return Path.Combine(Config.Dir, "songs.ini"); } }

        private readonly Timer _tick = new Timer();
        private readonly Timer _topmost = new Timer();   // 15 秒重申一次置顶（避免截图时频繁跳出）
        private readonly Timer _ramp = new Timer();
        private readonly Timer _hb = new Timer();      // 2 分钟一次心跳（内存/句柄趋势，排查静默退出）

        private string _title = "";

        // 音量斜坡
        private float _rampFrom;
        private float _rampTo;
        private int _rampStep;
        private const int RampSteps = 8;

        private System.Threading.EventWaitHandle _showSignal;

        /// <summary>监听"另一个实例启动"的信号，把窗口叫回前台。</summary>
        public void AttachShowSignal(System.Threading.EventWaitHandle ev)
        {
            _showSignal = ev;
            var th = new Thread(delegate()
            {
                while (true)
                {
                    try
                    {
                        if (!ev.WaitOne(800)) continue;
                    }
                    catch { break; }
                    try
                    {
                        if (_overlay != null && _overlay.IsHandleCreated)
                            _overlay.BeginInvoke(new Action(delegate
                            {
                                Log.W("收到唤醒信号，显示窗口");
                                ShowOverlay();
                            }));
                    }
                    catch { }
                }
            });
            th.IsBackground = true;
            th.Name = "NvShowSignal";
            th.Start();
        }

        public void Start()
        {
            _cfg = Config.Load();

            _gain = new AutoGain();
            _gain.TargetDb = _cfg.TargetDb;
            _gain.ToleranceDb = _cfg.ToleranceDb;
            _gain.MeasureSamples = Math.Max(20, _cfg.MeasureSeconds * 10);
            _gain.LoadCache(SongsPath);

            _overlay = new Overlay();
            _overlay.UserScale = _cfg.Scale;
            _overlay.Fade = _cfg.Opacity;

            _overlay.VolumeChanged += OnVolumeChanged;
            _overlay.MuteToggleRequested += OnMuteToggle;
            _overlay.MenuRequested += ShowMenu;
            BuildMenu();
            _overlay.PositionChanged += SavePosition;
            _overlay.ExitRequested += delegate { Log.W("退出: 点了 × 按钮"); Application.Exit(); };
            _overlay.SettingsRequested += TogglePanel;
            // 弹出面板/菜单可见时，小窗不再抢占 Z 序
            _overlay.PopupVisible = delegate { return (_panel != null && _panel.Visible) || (_menu != null && _menu.Visible); };

            _overlay.ShowOverlay();
            InitPosition();

            _tick.Interval = 500;
            _tick.Tick += OnTick;

            _ramp.Interval = 18;
            _ramp.Tick += OnRampTick;

            _tick.Start();

            // 心跳：记录内存与 GDI/USER 句柄数，便于发现泄漏或确认"最后活到什么时候"
            LogHeartbeat();
            _hb.Interval = 120000;   // 2 分钟
            _hb.Tick += delegate { LogHeartbeat(); };
            _hb.Start();

            // 15 秒重申一次置顶：比原来 2 秒宽松，微信截图时不会频繁跳出来挡住画面
            _topmost.Interval = 15000;
            _topmost.Tick += delegate
            {
                if ((_panel != null && _panel.Visible) || (_menu != null && _menu.Visible)) return;
                if (_overlay.Visible) _overlay.RaiseNoActivate();
            };
            _topmost.Start();

            _panelGuard.Interval = 150;
            _panelGuard.Tick += OnPanelGuard;

            _audioTimer.Interval = 120;
            _audioTimer.Tick += OnAudioTick;
            _audioTimer.Start();

            BindSession(true);
            Log.W("启动完成 version=" + Version + " pid=" + Process.GetCurrentProcess().Id);
        }

        public static string Version
        {
            get { try { return Assembly.GetExecutingAssembly().GetName().Version.ToString(); } catch { return "1.0"; } }
        }

        #region 位置

        private void InitPosition()
        {
            Screen target = null;
            if (_cfg.X != int.MinValue && _cfg.Y != int.MinValue)
            {
                foreach (Screen s in Screen.AllScreens)
                {
                    if (s.WorkingArea.Contains(new Point(_cfg.X + 10, _cfg.Y + 10))) { target = s; break; }
                }
            }
            if (target != null)
            {
                _overlay.SetPosition(_cfg.X, _cfg.Y, false);
                _overlay.ClampToScreen();
                return;
            }
            Screen p = Screen.PrimaryScreen;
            int w = _overlay.Width;
            // 纵向按"展开后的高度"预留，避免悬停展开时超出屏幕底部
            float s2 = (float)w / Renderer.W_NORMAL;
            int hHover = (int)Math.Round(Renderer.H_HOVER * s2);
            int m = (int)Math.Round(28f * s2);
            int x = p.WorkingArea.Right - w - m;
            int y = p.WorkingArea.Bottom - hHover - m;
            _overlay.SetPosition(x, y, false);
        }
        private void SavePosition()
        {
            _cfg.X = _overlay.Left;
            _cfg.Y = _overlay.Top;
            _cfg.Save();
        }

        #endregion

        #region 会话绑定

        private void BindSession(bool applyDefault)
        {
            AudioSession s = AudioSession.Find(ProccessName);
            if (s == null)
            {
                _session = null;
                UpdateUi(true);
                return;
            }
            _session = s;
            Log.W("已绑定会话 pid=" + s.Pid + " dev=" + ShortId(s.DeviceId) + " state=" + s.State + " (默认设备=" + ShortId(AudioSession.DefaultDeviceId()) + ")");

            bool wasOffline = _overlay != null && false;
            if (applyDefault)
            {
                // 首次绑定 / 重新绑定：设到基准音量
                SetSessionVolume(_cfg.DefaultVolume / 100f, true);
            }
            UpdateUi(false);
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            int i = id.LastIndexOf('\\');
            return i >= 0 ? id.Substring(i) : id;
        }

        private void SetSessionVolume(float v, bool ramp)
        {
            if (_session == null) return;
            if (!ramp)
            {
                _ramp.Stop();
                _session.SetVolume(v);
                if (_gain != null) _gain.UpdateDisplayForSession(v);
                return;
            }
            float cur = _session.GetVolume();
            if (cur < 0f) cur = v;
            _rampFrom = cur;
            _rampTo = v;
            _rampStep = 0;
            _ramp.Start();
        }

        private void OnRampTick(object sender, EventArgs e)
        {
            if (_session == null) { _ramp.Stop(); return; }
            _rampStep++;
            float t = _rampStep / (float)RampSteps;
            if (t >= 1f) { t = 1f; _ramp.Stop(); }
            float v = _rampFrom + (_rampTo - _rampFrom) * t;
            _session.SetVolume(v);
                if (_gain != null) _gain.UpdateDisplayForSession(v);
            if (t >= 1f)
            {

                UpdateUi(false);

            }
        }

        #endregion

        #region 定时刷新

        private void OnTick(object sender, EventArgs e)
        {
            try
            {
                // 会话健康检查：网易云退出/重启后 pid 会变，而它留下的 COM 会话不会立刻失效
                //（GetMasterVolume 依旧返回成功），只靠 IsAlive 会永久绑在死会话上。
                // 这里改为每 5 秒重新枚举一次会话、比较 pid——不依赖进程句柄（本机拿不到 cloudmusic 的进程句柄）。
                if (_session != null && (DateTime.Now - _lastPidCheck).TotalSeconds > 5)
                {
                    _lastPidCheck = DateTime.Now;
                    AudioSession fresh = AudioSession.Find(ProccessName);
                    if (fresh == null)
                    {
                        Log.W("会话已消失，解绑 (pid=" + _session.Pid + ")");
                        if (_cap != null && _cap.IsRunning) { _cap.Stop(); Log.W("抓流已停止"); }
                        _capturePid = 0;
                        _session = null;
                    }
                    else if (fresh.Pid != _session.Pid)
                    {
                        Log.W("会话进程变化 " + _session.Pid + " -> " + fresh.Pid + "，准备重新绑定");
                        if (_cap != null && _cap.IsRunning) { _cap.Stop(); Log.W("抓流已停止"); }
                        _capturePid = 0;
                        _session = null;
                    }
                }

                if (_session == null || !_session.IsAlive)
                {
                    // 重新找，失败就显示未连接
                    AudioSession s = AudioSession.Find(ProccessName);
                    if (s != null)
                    {
                        _session = s;
                        Log.W("重新绑定会话 pid=" + s.Pid);
                        // 重新绑定后强制把当前曲目当成"新歌"重新定档，避免沿用死会话时的旧状态
                        _committed = _cfg.DefaultVolume / 100f;
                        _firstApplied = false;
                        _limDb = 0f;
                        _fastDb = -120f;
                        _measureMuteUntil = DateTime.Now.AddMilliseconds(600);
                        _title = "";
                        SetSessionVolume(_cfg.DefaultVolume / 100f, true);
                    }
                    else
                    {
                        _session = null;
                    }
                }
                // 设备漂移检测：只在"默认设备真的变了"之后 10 秒内检查，避免每 tick 枚举会话
                string defNow = AudioSession.DefaultDeviceId();
                if (!string.IsNullOrEmpty(defNow) && defNow != _lastDefaultId)
                {
                    _lastDefaultId = defNow;
                    _driftUntil = DateTime.Now.AddSeconds(10);
                }
                if (_session != null && DateTime.Now < _driftUntil && !string.IsNullOrEmpty(defNow))
                {
                    if (!string.IsNullOrEmpty(_session.DeviceId) && _session.DeviceId != defNow)
                    {
                        AudioSession better = AudioSession.Find(ProccessName);
                        if (better != null && better.DeviceId == defNow && better.DeviceId != _session.DeviceId)
                        {
                            Log.W("输出设备变化，改绑到默认设备 pid=" + better.Pid);
                            _session = better;
                            float reapply = (_gain != null && _gain.LockedSession > 0f)
                                ? ClampToBase(_gain.LockedSession) : _cfg.DefaultVolume / 100f;
                            SetSessionVolume(reapply, true);
                            _committed = reapply;
                            _measureMuteUntil = DateTime.Now.AddMilliseconds(600);
                            _fastDb = -120f;
                            _limDb = 0f;
                            Log.W("已恢复本曲音量 " + (reapply * 100f).ToString("F0") + "%");
                        }
                    }
                    else
                    {
                        _driftUntil = DateTime.MinValue;
                    }
                }
                // 自动增益：确保抓流在跑（失败每 5 秒重试一次）
                if (_cfg.AutoGain && _session != null && _session.IsAlive)
                {
                    if (_cap == null) _cap = new ProcessCapture();
                    // 绑定的进程变了（网易云重启）→ 必须重启抓流，否则会一直抓死进程拿到静音
                    if (_cap.IsRunning && _capturePid != _session.Pid)
                    {
                        Log.W("抓流进程变化 " + _capturePid + " -> " + _session.Pid + "，重启抓流");
                        _cap.Stop();
                        _capturePid = 0;
                    }
                    bool needStart = !_cap.IsRunning &&
                                     (_capturePid != _session.Pid || (DateTime.Now - _lastCapTry).TotalSeconds > 5);
                    if (needStart)
                    {
                        _capturePid = _session.Pid;
                        _lastCapTry = DateTime.Now;
                        _cap.Start(_session.Pid);
                        Log.W("启动抓流 pid=" + _session.Pid);
                    }
                    else if (!_cap.IsRunning && _cap.LastError != "" && (DateTime.Now - _lastCapTry).TotalSeconds > 4)
                    {
                        Log.W("抓流异常: " + _cap.LastError);
                        _lastCapTry = DateTime.Now;
                    }
                }
                else if (_cap != null && _cap.IsRunning)
                {
                    _cap.Stop();
                    _capturePid = 0;
                }

                bool songChangedNow = false;
                string t = SongTitle.Current();
                if (t != _title)
                {
                    songChangedNow = true;
                    string prev = _title;
                    _title = t;
                    if (_cfg.AutoGain && t != "" && _session != null)
                    {
                        _gain.TargetDb = _cfg.TargetDb;
            _gain.ToleranceDb = _cfg.ToleranceDb;
                        _gain.SaveCache(SongsPath);      // 先把上一首学到的东西存盘
                        _gain.BeginSong(t, CurrentSession());
                        if (prev == "")
                        {
                            float first;
                            if (_gain.TryGetCached(t, out first))
                            {
                                first = ClampToBase(first);
                                Log.W("首曲(已认识) " + t + "  直接设为 " + (first * 100f).ToString("F0") + "%");
                                _cacheTrustUntil = DateTime.Now.AddSeconds(_cfg.CacheTrustSec);
                                SetSessionVolume(first, true);
                                _committed = first; _limDb = 0f; _fastDb = -120f;
                                _firstApplied = true; _limFeedbackUsed = false;
                            }
                            else
                            {
                                Log.W("首曲开始测量: " + t);
                            _cacheTrustUntil = DateTime.MinValue;
                            }
                        }
                        else
                        {
                            float cached;
                            if (_gain.TryGetCached(t, out cached))
                            {
                                cached = ClampToBase(cached);
                                Log.W("切歌(已认识) " + prev + " -> " + t + "  直接设为 " + (cached * 100f).ToString("F0") + "%");
                                _cacheTrustUntil = DateTime.Now.AddSeconds(_cfg.CacheTrustSec);
                                SetSessionVolume(cached, true);
                                _committed = cached; _limDb = 0f; _fastDb = -120f;
                                _firstApplied = true; _limFeedbackUsed = false;
                            }
                            else
                            {
                                float start = _cfg.DefaultVolume / 100f;
                                if (start < 0.05f) start = 0.05f;
                                Log.W("切歌(新歌) " + prev + " -> " + t + "  先回到基准 " + (start * 100f).ToString("F0") + "% 再测 " + _cfg.MeasureSeconds + " 秒");
                                _cacheTrustUntil = DateTime.MinValue;
                                SetSessionVolume(start, true);
                                _committed = start; _limDb = 0f; _fastDb = -120f;
                                _firstApplied = false; _limFeedbackUsed = false;
                            }
                        }
                    }
                    else if (prev != "" && t != "" && _cfg.ResetOnSongChange && _session != null)
                    {
                        Log.W("切歌: " + prev + "  ->  " + t + "  (复位到 " + _cfg.DefaultVolume + "%)");
                        SetSessionVolume(_cfg.DefaultVolume / 100f, true);
                    }
                }


                // 切歌收尾：丢掉队列里"上一首/切换瞬间"的残留样本，并静默 600ms（覆盖音量渐变）
                if (songChangedNow && t != "")
                {
                    FlushCapture();
                    _measureMuteUntil = DateTime.Now.AddMilliseconds(600);
                }

                UpdateUi(false);
            }
            catch (Exception ex)
            {
                Log.W("tick 异常: " + ex.Message);
            }
        }

        private void UpdateUi(bool force)
        {
            int vol = _cfg.DefaultVolume;
            bool muted = false;
            bool offline = true;

            if (_session != null && _session.IsAlive)
            {
                offline = false;
                float v = _session.GetVolume();
                if (v >= 0f) vol = (int)Math.Round(v * 100f);
                muted = _session.GetMute();
            }

            float loudDb = float.NaN;
            if (!offline && _gain != null && !float.IsNaN(_gain.LastMeasuredDb)) loudDb = _gain.LastMeasuredDb;
            _overlay.SetState(vol, muted, offline, _title, loudDb);
        }

        /// <summary>自动增益只允许把音量压到"基准音量"以下，绝不抬高。</summary>
        private float ClampToBase(float s)
        {
            float baseS = _cfg.DefaultVolume / 100f;
            if (baseS < 0.05f) baseS = 0.05f;
            return s > baseS ? baseS : s;
        }

        /// <summary>丢弃抓流队列里的残留样本（切歌/重绑时调用）。</summary>
        private void FlushCapture()
        {
            if (_cap == null) return;
            float junk;
            int n = 0;
            while (_cap.TryDequeue(out junk)) n++;
            if (n > 0) Log.W("已丢弃 " + n + " 个切换瞬间的残留样本");
        }

        private float CurrentSession()
        {
            if (_session == null) return _cfg.DefaultVolume / 100f;
            float v = _session.GetVolume();
            return v < 0f ? _cfg.DefaultVolume / 100f : v;
        }

        private void OnAudioTick(object sender, EventArgs e)
        {
            try
            {
                if (_cap == null || _session == null || _gain == null) return;

                if (!_cfg.AutoGain)
                {
                    float junk;
                    while (_cap.TryDequeue(out junk)) { }
                    return;
                }

                float db, cur = CurrentSession();
                float desired = 0f;
                bool got = false;
                int n = 0;

                // 缓存信任期：照常采样、照常显示，但不做自动音量决策（限幅器不受影响）
                if (_gain != null)
                {
                    bool trusting = DateTime.Now < _cacheTrustUntil;
                    if (_gain.SuppressCommit && !trusting) Log.W("缓存信任期结束，开始按实测校准 [" + _title + "]");
                    _gain.SuppressCommit = trusting;
                }

                while (_cap.TryDequeue(out db))
                {
                    n++;

                    // ① 快速电平（约 300ms 平滑），只给限幅器用
                    if (_fastDb < -100f) _fastDb = db;
                    else _fastDb = 0.92f * _fastDb + 0.08f * db;   // 约 1.2 秒，只反映持续响度

                    // ② 长窗口估计：正在"静默观察"时不喂样本，避免过渡污染
                    if (DateTime.Now >= _measureMuteUntil)
                    {
                        float tmp;
                        if (_gain.OnSample(db, cur, out tmp)) { desired = tmp; got = true; }
                    }
                }
                if (n == 0) return;

                // 定档 / 后续修正
                if (got)
                {
                    float goal = ClampToBase(desired);
                    if (!_firstApplied)
                    {
                        _committed = goal;                 // 首次：粗调，一次到位
                        _firstApplied = true;
                    }
                    else
                    {
                        // 之后：每次最多 ±1 dB，朝目标温和靠拢（可升可降）
                        float dbDelta = (float)(20.0 * Math.Log10(Math.Max(0.001f, goal) / Math.Max(0.001f, _committed)));
                        if (dbDelta > 1f) dbDelta = 1f;
                        if (dbDelta < -1f) dbDelta = -1f;
                        _committed = _committed * (float)Math.Pow(10.0, dbDelta / 20.0);
                        if (_committed < 0.02f) _committed = 0.02f;
                        _committed = ClampToBase(_committed);
                        goal = _committed;
                    }
                    _limDb = 0f;
                    _fastDb = -120f;
                    // 定档后重新记账：之前的限幅统计是在旧音量下得到的，不能拿来修正估计
                    _limHits = 0;
                    _limExcess = 0;
                    _limWindow = DateTime.Now;
                    _limCooldownUntil = DateTime.Now.AddSeconds(5);   // 等音量稳定再判断
                    LogGain(cur, goal, _gain.LastWasRecheck, _gain.UserTouched);
                }

                // 手动接管：本次不自动改音量（但上面的学习结果已写入缓存）
                if (_gain.UserTouched) return;

                // 限幅器：短时电平一旦超过"目标 + 4 dB"，立刻压下来
                float ceiling = _cfg.TargetDb + _cfg.LimitMarginDb;
                if (_fastDb > ceiling + 1f)
                {
                    // 只有"持续 1 秒以上"整体超过门限 1 dB 才压，避免跟着乐句起伏抖
                    float need = ceiling - _fastDb;
                    if (need < _limDb) _limDb = need;
                    if (_limDb < -12f) _limDb = -12f;
                    _holdUntil = DateTime.Now.AddSeconds(3);   // 压下去至少保持 3 秒

                    // 持续触发说明"这首歌比估计的更响"：把差额喂回学习器，改一次就不用再压
                    // 只在"新的限幅事件"时计数：1.5 秒内不重复计，避免几个 tick 就被当成 3 次
                    if ((DateTime.Now - _lastLimHit).TotalSeconds >= 1.5)
                    {
                        _lastLimHit = DateTime.Now;
                        _limHits++;
                        _limExcess += (_fastDb - _cfg.TargetDb);
                    }
                    if ((DateTime.Now - _limWindow).TotalSeconds > 30)
                    {
                        _limHits = 1;
                        _limExcess = _fastDb - _cfg.TargetDb;
                        _limWindow = DateTime.Now;
                    }
                    if (!_limFeedbackUsed && _gain.IsLocked && _limHits >= 3 && DateTime.Now >= _limCooldownUntil)
                    {
                        float excess = (float)(_limExcess / _limHits);
                        if (excess > 1f)
                        {
                            _gain.CorrectSource(excess);
                            _committed = ClampToBase(_gain.LockedSession);
                            Log.W(string.Format("自动增益(限幅反馈) [{0}] 持续超基准 {1:F1}dB → 源响度修正为 {2:F1}dB，音量 {3:F0}%",
                                _gain.CurrentSong, excess, _gain.LastSourceDb, _committed * 100f));

                            // 只有真正修正了，才消耗这"每首一次"的粗调机会
                            _limFeedbackUsed = true;
                            _limCooldownUntil = DateTime.Now.AddSeconds(20);
                            _limDb = 0f;
                            _fastDb = -120f;
                            _holdUntil = DateTime.Now.AddSeconds(4);
                        }
                        else
                        {
                            // 超出幅度太小，不值得改估计：不消耗机会，只冷静 8 秒，留到后面更响的段落再判断
                            _limCooldownUntil = DateTime.Now.AddSeconds(8);
                        }
                        _limHits = 0;
                        _limExcess = 0;
                        _limWindow = DateTime.Now;
                    }
                }
                else if (DateTime.Now > _holdUntil && _fastDb < ceiling - 1f)
                {
                    float rel = _limDb + 1.5f * 0.12f;         // 每秒放开 1.5 dB
                    _limDb = rel > 0f ? 0f : rel;
                }
                float target = ClampToBase(_committed * (float)Math.Pow(10.0, _limDb / 20.0));
                float delta = target - cur;
                // 死区 0.7 dB：门限附近的小幅来回不动作，否则会听到持续微抖
                if (Math.Abs(delta) > 0.08f * Math.Max(cur, 0.05f))
                {
                    _session.SetVolume(target);
                    if (_gain != null) _gain.UpdateDisplayForSession(target);
                    _measureMuteUntil = DateTime.Now.AddMilliseconds(350);   // 过渡期不采样
                    if (_limDb < -0.2f && (DateTime.Now - _lastGainLog).TotalSeconds > 3)
                    {
                        _lastGainLog = DateTime.Now;
                        Log.W(string.Format("限幅 [{0}] 即时电平={1:F1}dB 上限={2:F1}dB 偏移={3:F1}dB → {4:F0}%",
                            _gain.CurrentSong, _fastDb, ceiling, _limDb, target * 100f));
                    }
                }
            }
            catch (Exception ex) { Log.W("audio tick 异常: " + ex.Message); }
        }

        private void LogGain(float cur, float limited, bool recheck, bool manual)
        {
            float d = (float)(20.0 * Math.Log10(Math.Max(0.001f, limited) / Math.Max(0.001f, cur)));
            // 定档必定记录（哪怕幅度很小）；微调只在 ≥0.4 dB 时记录
            if (recheck && Math.Abs(d) < 0.4f) return;
            Log.W(string.Format("学习器{0} [{1}] 实测={2:F1}dB 源={3:F1}dB 区间={4:F1}~{5:F1}dB {6}{7:F1}dB → 音量 {8:F0}%{9}",
                recheck ? "(微调)" : "(定档)",
                _gain.CurrentSong, _gain.LastMeasuredDb, _gain.LastSourceDb,
                _cfg.TargetDb - _gain.ToleranceDb, _cfg.TargetDb + _gain.ToleranceDb,
                d < 0 ? "降" : "升", Math.Abs(d), limited * 100f,
                manual ? "（手动接管中，本轮不应用）" : ""));
        }
        #region 参数面板

        private void TogglePanel()
        {
            if (_panel != null && _panel.Visible) { HidePanel(); return; }
            Point anchor = _overlay.Visible ? _overlay.GearScreenPos() : Cursor.Position;
            ShowPanelAt(anchor);
        }

        private void ShowPanelAt(Point anchor)
        {
            if (_panel == null)
            {
                _panel = new ParamPanel();
                _panel.Owner = _overlay;   // 由系统保证：被拥有的窗口永远在所有者之上
                _panel.VolumeStep += delegate(int d) { SetBaseVolume(_cfg.DefaultVolume + d); };
                _panel.TargetStep += delegate(int d) { SetTargetDb(_cfg.TargetDb + d); };
                _panel.MeasureStep += delegate(int d) { SetMeasure(_cfg.MeasureSeconds + d); };
            _panel.TrustStep += delegate(int d) { SetCacheTrust(_cfg.CacheTrustSec + d); };
                _panel.MarginStep += delegate(int d) { SetLimitMargin(_cfg.LimitMarginDb + d); };
                _panel.ToleranceStep += delegate(int d) { SetTolerance(_cfg.ToleranceDb + d * 0.5f); };
                _panel.ToggleAutoGain += delegate { SetAutoGain(!_cfg.AutoGain); };
                _panel.ResetDefaults += delegate { ResetParams(); };
            }
            _panel.Volume = _cfg.DefaultVolume;
            _panel.TargetDb = _cfg.TargetDb;
            _panel.MeasureSec = _cfg.MeasureSeconds;
            _panel.CacheTrustSec = _cfg.CacheTrustSec;
            _panel.LimitMargin = _cfg.LimitMarginDb;
            _panel.Tolerance = _cfg.ToleranceDb;
            _panel.AutoGainOn = _cfg.AutoGain;
            _panel.ShowAt(anchor.X, anchor.Y);
            _panelGuard.Start();
            Log.W("打开参数面板");
        }

        private void HidePanel()
        {
            _panelGuard.Stop();
            if (_panel != null && _panel.Visible) _panel.Hide();
        }

        private void OnPanelGuard(object sender, EventArgs e)
        {
            if (_panel == null || !_panel.Visible) { _panelGuard.Stop(); return; }
            Point cp = Cursor.Position;
            Rectangle pr = _panel.Bounds; pr.Inflate(28, 28);   // 宽容区，避免边缘误收
            bool inPanel = pr.Contains(cp);
            Rectangle or = _overlay.Bounds; or.Inflate(12, 12);
            bool inOverlay = _overlay.Visible && or.Contains(cp);
            if (!inPanel && !inOverlay)
            {
                HidePanel();
                _overlay.ForceRender();
            }
        }

        private void SetBaseVolume(int v)
        {
            if (v < 25) v = 25;
            if (v > 70) v = 70;
            if (v == _cfg.DefaultVolume) return;
            _cfg.DefaultVolume = v;
            _cfg.Save();
            SetSessionVolume(v / 100f, true);
            if (_panel != null) { _panel.Volume = v; _panel.Refresh_(); }
            Log.W("参数: 基准音量 = " + v + "%");
        }

        private void SetTargetDb(float v)
        {
            if (v < -30f) v = -30f;
            if (v > -14f) v = -14f;
            _cfg.TargetDb = (float)Math.Round(v, 1);
            _cfg.Save();
            if (_gain != null)
            {
                _gain.TargetDb = _cfg.TargetDb;
            _gain.ToleranceDb = _cfg.ToleranceDb;
                float src;
                if (_gain.TryGetSource(_gain.CurrentSong, out src))
                    SetSessionVolume(ClampToBase(_gain.SessionFor(src)), true);
            }
            if (_panel != null) { _panel.TargetDb = _cfg.TargetDb; _panel.Refresh_(); }
            Log.W("参数: 基准响度 = " + _cfg.TargetDb.ToString("0.0") + " dB");
        }

        private void SetMeasure(int v)
        {
            if (v < 4) v = 4;
            if (v > 12) v = 12;
            _cfg.MeasureSeconds = v;
            _cfg.Save();
            if (_gain != null) _gain.MeasureSamples = v * 10;
            if (_panel != null) { _panel.MeasureSec = v; _panel.Refresh_(); }
            Log.W("参数: 测量时长 = " + v + " 秒");
        }

        private void SetCacheTrust(int v)
        {
            if (v < 0) v = 0;
            if (v > 60) v = 60;
            _cfg.CacheTrustSec = v;
            _cfg.Save();
            if (_panel != null) { _panel.CacheTrustSec = v; _panel.Refresh_(); }
            Log.W("参数: 缓存信任 = " + v + " 秒" + (v == 0 ? "（关闭）" : ""));
        }

        private void SetTolerance(float v)
        {
            if (v < 0.5f) v = 0.5f;
            if (v > 4f) v = 4f;
            _cfg.ToleranceDb = (float)Math.Round(v, 1);
            _cfg.Save();
            if (_gain != null) _gain.ToleranceDb = _cfg.ToleranceDb;
            if (_panel != null) { _panel.Tolerance = _cfg.ToleranceDb; _panel.Refresh_(); }
            Log.W("参数: 容差 = ±" + _cfg.ToleranceDb.ToString("0.0") + " dB");
        }

        private void SetLimitMargin(float v)
        {
            if (v < 2f) v = 2f;
            if (v > 8f) v = 8f;
            _cfg.LimitMarginDb = (float)Math.Round(v, 1);
            _cfg.Save();
            if (_panel != null) { _panel.LimitMargin = _cfg.LimitMarginDb; _panel.Refresh_(); }
            Log.W("参数: 限幅余量 = " + _cfg.LimitMarginDb.ToString("0.0") + " dB");
        }

        private void SetAutoGain(bool on)
        {
            _cfg.AutoGain = on;
            _cfg.Save();
            if (!on && _cap != null) { _cap.Stop(); _capturePid = 0; }
            if (_panel != null) { _panel.AutoGainOn = on; _panel.Refresh_(); }
            Log.W("参数: 自动增益 " + (on ? "开" : "关"));
        }

        private void ResetParams()
        {
            _cfg.DefaultVolume = 40;
            _cfg.TargetDb = -26f;
            _cfg.MeasureSeconds = 12;
            _cfg.CacheTrustSec = 20;
            _cfg.LimitMarginDb = 4f;
            _cfg.ToleranceDb = 1.5f;
            _cfg.Save();
            if (_gain != null) { _gain.TargetDb = -26f; _gain.MeasureSamples = 120; }
            SetSessionVolume(0.40f, true);
            if (_panel != null)
            {
                _panel.Volume = _cfg.DefaultVolume;
                _panel.TargetDb = _cfg.TargetDb;
                _panel.MeasureSec = _cfg.MeasureSeconds;
                _panel.CacheTrustSec = _cfg.CacheTrustSec;
                _panel.LimitMargin = _cfg.LimitMarginDb;
                _panel.Tolerance = _cfg.ToleranceDb;
                _panel.AutoGainOn = _cfg.AutoGain;
                _panel.Refresh_();
            }
            Log.W("参数已恢复默认");
        }

        #endregion
        #endregion

        #region 交互

        private void OnVolumeChanged(int v)
        {
            if (_gain != null) _gain.NotifyManual();
            if (_session != null)
            {
                _ramp.Stop();
                _session.SetVolume(v / 100f);
                if (_gain != null) _gain.UpdateDisplayForSession(v / 100f);
            }
            UpdateUi(false);
        }

        private void OnMuteToggle()
        {
            if (_session == null) return;
            bool m = _session.GetMute();
            _session.SetMute(!m);
            _cfg.Save();
            UpdateUi(true);
        }

        private void ShowOverlay()
        {
            _overlay.ShowOverlay();
            _overlay.RaiseNoActivate();
            UpdateUi(true);
        }

        #endregion

        #region 菜单

        private MenuPanel _menu;

        private void BuildMenu()
        {
            _menu = new MenuPanel();
            _menu.Owner = _overlay;   // 同上
            _menu.MuteToggle += delegate { OnMuteToggle(); RefreshMenu(); };
            _menu.AutoGainToggle += delegate { SetAutoGain(!_cfg.AutoGain); RefreshMenu(); };
            _menu.ScaleStep += delegate(int d) { StepScale(d); RefreshMenu(); };
            _menu.OpacityStep += delegate(int d) { StepOpacity(d); RefreshMenu(); };
            _menu.AutoStartToggle += delegate
            {
                _cfg.AutoStart = !_cfg.AutoStart;
                ApplyAutoStart(_cfg.AutoStart);
                _cfg.Save();
                RefreshMenu();
            };
            _menu.ExitRequested += delegate { Log.W("退出: 右键菜单里选了退出"); Application.Exit(); };
        }

        private void SyncMenuState()
        {
            if (_menu == null) return;
            _menu.Muted = _session != null && _session.GetMute();
            _menu.AutoGainOn = _cfg.AutoGain;
            _menu.AutoStartOn = _cfg.AutoStart;
            _menu.ScaleValue = _cfg.Scale;
            _menu.OpacityValue = _cfg.Opacity;
        }

        private void RefreshMenu()
        {
            if (_menu == null || !_menu.Visible) return;
            SyncMenuState();
            _menu.Refresh_();
        }

        private void ShowMenu()
        {
            if (_menu == null) return;
            SyncMenuState();
            _menu.ShowAtCursor();
        }

        private static readonly float[] ScaleSteps = { 0.7f, 0.85f, 1.0f, 1.2f, 1.5f };
        private static readonly float[] OpacitySteps = { 0.55f, 0.7f, 0.82f, 0.88f, 0.95f, 1.0f };

        private void StepScale(int dir)
        {
            int i = NearestIndex(ScaleSteps, _cfg.Scale) + dir;
            if (i < 0) i = 0;
            if (i >= ScaleSteps.Length) i = ScaleSteps.Length - 1;
            _cfg.Scale = ScaleSteps[i];
            _overlay.UserScale = _cfg.Scale;
            _cfg.Save();
            _overlay.ForceRender();
            _overlay.ClampToScreen();
        }

        private void StepOpacity(int dir)
        {
            int i = NearestIndex(OpacitySteps, _cfg.Opacity) + dir;
            if (i < 0) i = 0;
            if (i >= OpacitySteps.Length) i = OpacitySteps.Length - 1;
            _cfg.Opacity = OpacitySteps[i];
            _overlay.Fade = _cfg.Opacity;
            _cfg.Save();
            _overlay.ForceRender();
        }

        private static int NearestIndex(float[] arr, float v)
        {
            int best = 0; float bd = float.MaxValue;
            for (int i = 0; i < arr.Length; i++)
            {
                float d = Math.Abs(arr[i] - v);
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        #endregion

        private static void ApplyAutoStart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k == null) return;
                    if (on) k.SetValue("LevelBand", "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue("LevelBand", false);
                }
            }
            catch { }
        }


        // 全局热键按用户要求移除：会抢占其它程序的组合键，且用户不常用。


        private void LogHeartbeat()
        {
            try
            {
                var pr = Process.GetCurrentProcess();
                uint gdi = Native.GetGuiResources(pr.Handle, 0);
                uint usr = Native.GetGuiResources(pr.Handle, 1);
                Log.W(string.Format("心跳 内存={0}MB GDI={1} USER={2} 线程={3} 歌={4} 音量={5}%",
                    pr.WorkingSet64 / 1048576, gdi, usr, pr.Threads.Count, _title,
                    (int)Math.Round(CurrentSession() * 100f)));
            }
            catch (Exception ex) { Log.W("心跳异常: " + ex.Message); }
        }

        public void Dispose()
        {
            try { if (_gain != null) _gain.SaveCache(SongsPath); } catch { }
            try { if (_cap != null) _cap.Dispose(); } catch { }
            try { _audioTimer.Stop(); _audioTimer.Dispose(); } catch { }
            try
            {
                _cfg.X = _overlay.Left; _cfg.Y = _overlay.Top;
                _cfg.Save();
            }
            catch { }
            Log.W("退出");
        }
    }

    /// <summary>读取网易云当前曲目（主窗口标题 = "歌名 - 歌手"）。</summary>
    internal static class SongTitle
    {
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
        private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder sb, int max);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        private static uint[] _pids = new uint[0];
        private static DateTime _pidAt = DateTime.MinValue;

        public static string Current()
        {
            try
            {
                if ((DateTime.Now - _pidAt).TotalSeconds > 5)
                {
                    RefreshPids();
                    _pidAt = DateTime.Now;
                }
                // 即使进程枚举失败也继续（FindTitle 有类名兜底）
                return FindTitle(_pids);
            }
            catch { return ""; }
        }

        private static void RefreshPids()
        {
            try
            {
                Process[] ps = Process.GetProcessesByName("cloudmusic");
                var list = new uint[ps.Length];
                for (int i = 0; i < ps.Length; i++) list[i] = (uint)ps[i].Id;
                _pids = list;
            }
            catch { _pids = new uint[0]; }
        }

        private static string FindTitle(uint[] pids)
        {
            string best = "";
            int bestRank = -1;
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);

                var cls = new System.Text.StringBuilder(128);
                GetClassNameW(h, cls, 128);
                string c = cls.ToString();

                bool isMain = (c == "OrpheusBrowserHost");
                bool isIcon = (c == "icon");
                if (!isMain && !isIcon) return true;

                bool pidKnown = false;
                for (int i = 0; i < pids.Length; i++) if (pids[i] == pid) { pidKnown = true; break; }

                // 主窗口：就算进程枚举失败也接受；icon 类窗口必须 pid 对得上
                if (!isMain && !pidKnown) return true;

                var sb = new System.Text.StringBuilder(512);
                GetWindowTextW(h, sb, 512);
                string t = sb.ToString().Trim();
                if (t.Length == 0) return true;

                int rank = isMain ? (pidKnown ? 3 : 2) : 1;
                if (rank > bestRank) { bestRank = rank; best = t; }
                return true;
            }, IntPtr.Zero);
            return best;
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            try
            {
                Native.EnableDpiAware();
            }
            catch { }

            if (args.Length > 0 && args[0] == "--render")
            {
                string dir = args.Length > 1 ? args[1] : ".";
                RenderToFiles(dir);
                return;
            }
            if (args.Length > 0 && args[0] == "--selftest")
            {
                SelfTest(args.Length > 1 ? args[1] : Config.LogPath);
                return;
            }

            bool created;
            using (var showSignal = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, "LevelBand.Show"))
            using (var mutex = new Mutex(true, "LevelBand.SingleInstance", out created))
            {
                if (!created)
                {
                    // 已经有实例在跑：把它的窗口叫出来，而不是默默退出
                    try { showSignal.Set(); } catch { }
                    return;
                }

                // 任何未处理异常都写进日志，避免"静默消失"
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    Log.W("!!! UI 线程未处理异常: " + e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Log.W("!!! 未处理异常 (IsTerminating=" + e.IsTerminating + "): " + e.ExceptionObject);
                };
                AppDomain.CurrentDomain.ProcessExit += delegate
                {
                    Log.W("=== ProcessExit 事件（进程即将结束）===");
                };

                Log.Rotate();   // 保留上一次日志为 log.prev.txt
                Log.W("=== LevelBand 启动 ===");

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                var app = new App();
                app.Start();
                app.AttachShowSignal(showSignal);
                Application.ApplicationExit += delegate { Log.W("=== ApplicationExit 事件 ==="); app.Dispose(); };
                Application.Run();
                Log.W("=== LevelBand 正常退出（消息循环结束）===");
            }
        }

        private static void RenderToFiles(string dir)
        {
            try { Directory.CreateDirectory(dir); } catch { }
            float[] scales = new float[] { 1f, 2f };
            foreach (float sc in scales)
            {
                RenderState(dir, "normal", sc, false, false, false, 42, "灰色轨迹 - Beyond", -27.8f);
                RenderState(dir, "hover", sc, true, false, false, 42, "灰色轨迹 - Beyond", -27.3f);
                RenderState(dir, "mute", sc, false, true, false, 42, "灰色轨迹 - Beyond", -27.3f);
                RenderState(dir, "offline", sc, false, false, true, 0, "灰色轨迹 - Beyond", float.NaN);
            }
            Log.W("render -> " + dir);
        }

        private static void RenderState(string dir, string name, float scale, bool controls,
            bool mute, bool offline, int vol, string title, float loudDb)
        {
            SizeF logical = Renderer.LogicalSize(true);   // 卡片永久展开
            int w = (int)Math.Round(logical.Width * scale);
            int h = (int)Math.Round(logical.Height * scale);
            int pad = 24;
            using (var bmp = new Bitmap(w + pad * 2, h + pad * 2, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                // 模拟深色游戏背景
                using (var bg = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new PointF(0, 0), new PointF(bmp.Width, bmp.Height),
                    Color.FromArgb(255, 26, 28, 38), Color.FromArgb(255, 9, 10, 14)))
                    g.FillRectangle(bg, 0, 0, bmp.Width, bmp.Height);

                var st = new UiState();
                st.Scale = scale;
                st.Hover = true;
                st.Muted = mute;
                st.Offline = offline;
                st.Volume = vol;
                st.Title = title;
                st.LoudDb = loudDb;
                st.ShowControls = controls;

                var save = g.Save();
                g.TranslateTransform(pad, pad);
                Renderer.Draw(g, st);
                g.Restore(save);

                bmp.Save(Path.Combine(dir, name + "@" + scale.ToString("0") + "x.png"),
                    System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        private static void SelfTest(string logPath)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== SelfTest " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
            sb.AppendLine("64bit=" + Environment.Is64BitProcess + " os=" + Environment.OSVersion);
            sb.AppendLine("GetFolderPath(ApplicationData)='" + Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "'");
            sb.AppendLine("env APPDATA='" + Environment.GetEnvironmentVariable("APPDATA") + "'");
            sb.AppendLine("Config.Dir='" + Config.Dir + "'");
            sb.AppendLine("Dir 存在=" + Directory.Exists(Config.Dir));
            try
            {
                string probe = Path.Combine(Config.Dir, "probe.txt");
                File.WriteAllText(probe, "ok", System.Text.Encoding.UTF8);
                sb.AppendLine("写入测试=成功 -> " + probe);
            }
            catch (Exception ex) { sb.AppendLine("写入测试=失败 " + ex.GetType().Name + " " + ex.Message); }
            try
            {
                var all = AudioSession.Dump();
                sb.AppendLine("会话总数=" + all.Count);
                foreach (string s in all) sb.AppendLine("  " + s);
            }
            catch (Exception ex) { sb.AppendLine("Dump 异常: " + ex.Message); }

            try
            {
                AudioSession s = AudioSession.Find("cloudmusic.exe");
                if (s == null) sb.AppendLine("未找到 cloudmusic 会话");
                else
                {
                    sb.AppendLine("找到 cloudmusic 会话 pid=" + s.Pid);
                    float v = s.GetVolume();
                    sb.AppendLine("  当前音量=" + (v * 100f).ToString("F1") + "%");
                    bool mute = s.GetMute();
                    sb.AppendLine("  静音=" + mute);
                    bool ok = s.SetVolume(v);
                    float v2 = s.GetVolume();
                    sb.AppendLine("  写回同值 ok=" + ok + " 读回=" + (v2 * 100f).ToString("F1") + "%");
                }
            }
            catch (Exception ex) { sb.AppendLine("绑定异常: " + ex); }

            try { sb.AppendLine("标题=" + SongTitle.Current()); }
            catch (Exception ex) { sb.AppendLine("标题异常: " + ex.Message); }

            try { File.WriteAllText(logPath, sb.ToString(), System.Text.Encoding.UTF8); } catch { }
        }
    }
}
