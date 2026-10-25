using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NvHud
{
    /// <summary>
    /// 自动增益：把每首歌的响度对齐到同一个「基准响度」。
    ///
    /// 统计口径（踩坑后定下来的）：
    /// 1. 抓到的信号在会话音量之后，所以每个样本先归一化成"源响度"：
    ///       src = 测得dB - 20*log10(当前推子)
    ///    中途改推子不会污染历史样本，避免正反馈把音量压到 0。
    /// 2. 一首歌的响度 = 本曲所有源响度样本的 **P80**。
    ///    不用"历史最大值"，否则一段响的副歌会把整首歌永久压低。
    /// 3. 期望推子 = 10^((基准 - 源响度P80)/20)。
    /// </summary>
    internal sealed class AutoGain
    {
        public const float MinDb = -60f;
        public int MeasureSamples = 80;           // 定档所需样本数（100ms 一个；80 = 8 秒）
        private const int MaxSamples = 36000;     // 一首歌最多记 1 小时

        public float TargetDb = -21f;
        /// <summary>置 true 时：照常采样、照常刷新读数，但不做定档/微调决策，也不写缓存。</summary>
        public bool SuppressCommit;

        public float ToleranceDb = 1.5f;    // 目标区间半宽：落在 [TargetDb−tol, TargetDb+tol] 内就不动

        private readonly Dictionary<string, float> _cache = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly List<float> _src = new List<float>();
        private readonly List<float> _recent = new List<float>();   // 近期窗口（约 45 秒），用于"现在这一段多响"
        private string _song = "";
        private bool _userTouched;

        private bool _locked;
        private int _dispTick;
        private DateTime _lastRecheck = DateTime.MinValue;
        private DateTime _settleUntil = DateTime.MinValue;

        public float LastSourceDb = float.NaN;
        public float LastMeasuredDb = float.NaN;
        public bool LastWasRecheck;   // 本次结果是"后续修正"而非初次定档

        public string CurrentSong { get { return _song; } }
        public int SampleCount { get { return _src.Count; } }
        public bool IsLocked { get { return _locked; } }
        public bool UserTouched { get { return _userTouched; } }

        public void BeginSong(string title, float currentSession)
        {
            _song = title ?? "";
            _src.Clear();
            _recent.Clear();   // 近期窗口也要清，否则新歌头几秒会混入上一首的样本
            _userTouched = false;
            LastSourceDb = float.NaN;
            LastMeasuredDb = float.NaN;
            _locked = false;
            LockedSession = -1f;

            // 认识的歌 → 直接定档，不再测量
            float known;
            if (_song.Length > 0 && _cache.TryGetValue(_song, out known))
            {
                LockedSession = SessionFor(known);
                _src.Add(known);
                _locked = true;   // 先按缓存定档，之后仍可"只降不升"地修正

                // 立刻填好读数，否则前 4 秒（要攒 RecheckSamples 才采到样本）窗口里没有 dB 显示
                LastSourceDb = known;
                float sess = LockedSession < 0.02f ? 0.02f : LockedSession;
                LastMeasuredDb = known + (float)(20.0 * Math.Log10(sess));
            }
        }

        /// <summary>本曲已定档（不再自动调整）。</summary>
        public bool Locked { get { return _locked; } }

        /// <summary>本曲定下的推子（未定档时为 -1）。</summary>
        public float LockedSession = -1f;

        public void NotifyManual() { _userTouched = true; }

        /// <summary>刚改过推子：等音频真正跟上再采样，否则会把旧响度按新推子反推，得到虚高的源响度。</summary>
        public void NotifyApplied()
        {
            _settleUntil = DateTime.UtcNow.AddMilliseconds(450);
        }

        /// <summary>开始校准：保持当前音量测 8 秒，把你现在听到的响度定为基准。</summary>
        /// <summary>
        /// 把"实际比估计更响"的差额补进源响度，并写回缓存。
        /// 用于限幅器持续触发时：说明这首被估低了，改一次就不用再压。
        /// </summary>
        public void CorrectSource(float deltaDb)
        {
            if (_song.Length == 0 || float.IsNaN(LastSourceDb)) return;
            float src = LastSourceDb + deltaDb;
            LastSourceDb = src;
            _cache[_song] = src;
            LockedSession = SessionFor(src);
            float s = LockedSession < 0.02f ? 0.02f : LockedSession;
            LastMeasuredDb = src + (float)(20.0 * Math.Log10(s));
        }

        /// <summary>音量被改动后立刻重算读数（否则要等下一个样本才更新）。</summary>
        public void UpdateDisplayForSession(float session)
        {
            if (float.IsNaN(LastSourceDb)) return;
            float s = session < 0.02f ? 0.02f : session;
            LastMeasuredDb = LastSourceDb + (float)(20.0 * Math.Log10(s));
        }

        /// <summary>取某首歌缓存的源响度。</summary>
        public bool TryGetSource(string title, out float sourceDb)
        {
            sourceDb = 0f;
            return title != null && _cache.TryGetValue(title, out sourceDb);
        }

        public bool TryGetCached(string title, out float session)
        {
            session = 0f;
            float src;
            if (title != null && _cache.TryGetValue(title, out src))
            {
                session = SessionFor(src);
                return true;
            }
            return false;
        }

        public float SessionFor(float sourceDb)
        {
            double gainDb = (double)TargetDb - sourceDb;
            double s = Math.Pow(10.0, gainDb / 20.0);
            float v = (float)s;
            if (v < 0.02f) v = 0.02f;
            if (v > 1f) v = 1f;
            return v;
        }

        private static float Percentile(List<float> list, float p)
        {
            if (list.Count == 0) return 0f;
            var sorted = new List<float>(list);
            sorted.Sort();
            int idx = (int)(sorted.Count * p);
            if (idx >= sorted.Count) idx = sorted.Count - 1;
            return sorted[idx];
        }

        /// <summary>喂一个 ~100ms 的 RMS 样本；返回 true 表示算出了建议推子。</summary>
        public bool OnSample(float db, float currentSession, out float desiredSession)
        {
            desiredSession = 0f;
            if (db < MinDb) return false;

            float sess = currentSession < 0.02f ? 0.02f : currentSession;

            // 校准：冻结音量，只收集你当前听到的电平
            // 注意：手动接管不再阻断采样——继续学习，只是不应用（应用由 App 决定）。
            if (DateTime.UtcNow < _settleUntil) return false;

            float src = db - (float)(20.0 * Math.Log10(sess));
            _src.Add(src);
            if (_src.Count > MaxSamples) _src.RemoveAt(0);
            _recent.Add(src);
            if (_recent.Count > 450) _recent.RemoveAt(0);

            // 界面读数：每 5 个样本刷新一次，与"是否锁定/是否修正"无关。
            // 之前只在定档和修正时更新，导致锁定后读数卡在旧值（屏幕上显示的还是压低前的那条）。
            if (++_dispTick >= 5 || _src.Count <= 6)
            {
                _dispTick = 0;
                float dsp = _recent.Count > 20 ? Percentile(_recent, 0.80f) : Percentile(_src, 0.80f);
                LastSourceDb = dsp;
                LastMeasuredDb = dsp + (float)(20.0 * Math.Log10(sess));
            }
            // 样本还不够：不动作
            // 缓存信任期内：样本已经进了 _src/_recent，读数也刷新过了，但暂不做决策、不写缓存
            if (SuppressCommit) return false;

            if (_src.Count < MeasureSamples) return false;

            // 节流：每 6 秒最多评估一次（温和）
            if ((DateTime.UtcNow - _lastRecheck).TotalSeconds < 6) return false;

            float p = Percentile(_recent, 0.80f);
            float level = p + (float)(20.0 * Math.Log10(sess));
            LastSourceDb = p;
            LastMeasuredDb = level;

            // 首次定档：直接给出结果（粗调，一次到位）
            if (!_locked)
            {
                _locked = true;
                LastWasRecheck = false;
                _lastRecheck = DateTime.UtcNow;
                LockedSession = SessionFor(p);
                desiredSession = LockedSession;
                if (_song.Length > 0) _cache[_song] = p;
                return true;
            }

            // 之后：只有"现在这一段"跑出目标区间才微调，可升可降
            float lo = TargetDb - ToleranceDb;
            float hi = TargetDb + ToleranceDb;
            if (level <= hi && level >= lo)
            {
                _lastRecheck = DateTime.UtcNow;
                return false;
            }

            LastWasRecheck = true;
            _lastRecheck = DateTime.UtcNow;
            LockedSession = SessionFor(p);
            desiredSession = LockedSession;
            if (_song.Length > 0) _cache[_song] = p;
            return true;
        }

        #region 缓存读写

        public void LoadCache(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int bar = raw.IndexOf('|');
                    if (bar <= 0) continue;
                    float db;
                    if (!float.TryParse(raw.Substring(0, bar), NumberStyles.Float, CultureInfo.InvariantCulture, out db)) continue;
                    string title = raw.Substring(bar + 1);
                    if (title.Length == 0) continue;
                    _cache[title] = db;
                }
            }
            catch { }
        }

        public void SaveCache(string path)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in _cache)
                {
                    sb.Append(kv.Value.ToString("F2", CultureInfo.InvariantCulture));
                    sb.Append('|');
                    sb.Append(kv.Key);
                    sb.Append("\r\n");
                }
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            }
            catch { }
        }

        public int CacheCount { get { return _cache.Count; } }

        #endregion
    }
}
