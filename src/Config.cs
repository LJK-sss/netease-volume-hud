using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace NvHud
{
    /// <summary>极简 key=value 配置，存 %APPDATA%\LevelBand\config.ini</summary>
    internal sealed class Config
    {
        public int X = int.MinValue;
        public int Y = int.MinValue;
        public float Scale = 1.0f;
        public float Opacity = 0.88f;

        public int DefaultVolume = 40;      // ★ 基准音量：启动时 & 切歌时应用
        public bool ResetOnSongChange = true;

        public bool AutoStart = false;
        public bool AutoGain = true;        // 自动增益（按响度对齐）
        public float TargetDb = -26f;       // 目标响度：P90 RMS dBFS（可用"校准"重设）
        public int MeasureSeconds = 12;      // 每首歌测量时长（秒）
        public int CacheTrustSec = 20;       // 缓存信任期（秒）：开头这段时间内不自动改音量
        public int Spectrum = 28;            // 频谱柱子浓度（0~50）
        public int MarqueeStep = 1;          // 跑马灯每帧位移（逻辑像素）
        public int MarqueeFps = 15;           // 跑马灯刷新率
        public int MarqueeHoldMs = 2000;     // 开头/结尾停顿（毫秒）
        public float ToleranceDb = 1.5f;    // 目标区间半宽（±dB）
        public float LimitMarginDb = 4f;    // 限幅余量：超过"基准响度 + 该值"才限幅

        public static string Dir
        {
            get
            {
                string primary = null;
                try
                {
                    string ad = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    if (string.IsNullOrEmpty(ad)) ad = Environment.GetEnvironmentVariable("APPDATA");
                    if (!string.IsNullOrEmpty(ad)) primary = Path.Combine(ad, "LevelBand");
                }
                catch { }

                if (primary != null && TryEnsure(primary)) return primary;

                // 兜底：APPDATA 不可用时放到 exe 旁边
                string alt = null;
                try
                {
                    string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    if (string.IsNullOrEmpty(exe)) exe = Environment.GetCommandLineArgs()[0];
                    alt = Path.Combine(Path.GetDirectoryName(exe), "LevelBand");
                }
                catch { alt = "LevelBand"; }

                if (TryEnsure(alt)) return alt;
                return primary != null ? primary : alt;
            }
        }

        private static bool TryEnsure(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                // 真正确认可写：写一个探针文件再删掉
                string probe = Path.Combine(dir, ".write_probe");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        public static string ConfigPath { get { return Path.Combine(Dir, "config.ini"); } }
        public static string LogPath { get { return Path.Combine(Dir, "log.txt"); } }

        public static Config Load()
        {
            var c = new Config();
            try
            {
                if (!File.Exists(ConfigPath)) return c;
                foreach (string raw in File.ReadAllLines(ConfigPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "x": c.X = Int(v, c.X); break;
                        case "y": c.Y = Int(v, c.Y); break;
                        case "scale": c.Scale = Flt(v, c.Scale); break;
                        case "opacity": c.Opacity = Flt(v, c.Opacity); break;
                        case "defaultVolume": c.DefaultVolume = Int(v, c.DefaultVolume); break;
                        case "resetOnSongChange": c.ResetOnSongChange = Bool(v, c.ResetOnSongChange); break;
                        case "autoStart": c.AutoStart = Bool(v, c.AutoStart); break;
                        case "autoGain": c.AutoGain = Bool(v, c.AutoGain); break;
                        case "targetDb": c.TargetDb = Flt(v, c.TargetDb); break;
                        case "measureSeconds": c.MeasureSeconds = Int(v, c.MeasureSeconds); break;
                        case "cacheTrustSec": c.CacheTrustSec = Int(v, c.CacheTrustSec); break;
                        case "spectrum": c.Spectrum = Int(v, c.Spectrum); break;
                        case "marqueeStep": c.MarqueeStep = Int(v, c.MarqueeStep); break;
                        case "marqueeFps": c.MarqueeFps = Int(v, c.MarqueeFps); break;
                        case "marqueeHoldMs": c.MarqueeHoldMs = Int(v, c.MarqueeHoldMs); break;
                        case "limitMarginDb": c.LimitMarginDb = Flt(v, c.LimitMarginDb); break;
                        case "toleranceDb": c.ToleranceDb = Flt(v, c.ToleranceDb); break;
                    }
                }
            }
            catch { }
            if (c.Scale < 0.5f) c.Scale = 0.5f;
            if (c.Scale > 2.0f) c.Scale = 2.0f;
            if (c.Opacity < 0.3f) c.Opacity = 0.3f;
            if (c.Opacity > 1.0f) c.Opacity = 1.0f;
            if (c.DefaultVolume < 0) c.DefaultVolume = 0;
            if (c.DefaultVolume > 100) c.DefaultVolume = 100;

            // 手改 config 也可能写入离谱的值，这里统一夹一次范围（与参数面板一致）
            if (c.TargetDb < -30f) c.TargetDb = -30f;
            if (c.TargetDb > -14f) c.TargetDb = -14f;
            if (c.ToleranceDb < 0.5f) c.ToleranceDb = 0.5f;
            if (c.ToleranceDb > 4f) c.ToleranceDb = 4f;
            if (c.LimitMarginDb < 2f) c.LimitMarginDb = 2f;
            if (c.LimitMarginDb > 8f) c.LimitMarginDb = 8f;
            if (c.MeasureSeconds < 4) c.MeasureSeconds = 4;
            if (c.MeasureSeconds > 12) c.MeasureSeconds = 12;
            if (c.CacheTrustSec < 0) c.CacheTrustSec = 0;
            if (c.CacheTrustSec > 60) c.CacheTrustSec = 60;
            if (c.Spectrum < 0) c.Spectrum = 0;
            if (c.Spectrum > 50) c.Spectrum = 50;
            if (c.MarqueeStep < 1) c.MarqueeStep = 1;
            if (c.MarqueeStep > 16) c.MarqueeStep = 16;
            if (c.MarqueeFps < 2) c.MarqueeFps = 2;
            if (c.MarqueeFps > 60) c.MarqueeFps = 60;
            if (c.MarqueeHoldMs < 0) c.MarqueeHoldMs = 0;
            if (c.MarqueeHoldMs > 3000) c.MarqueeHoldMs = 3000;
            return c;
        }

        public void Save()
        {
            try
            {
                var lines = new List<string>();
                lines.Add("# LevelBand 配置");
                lines.Add("x=" + X.ToString(CultureInfo.InvariantCulture));
                lines.Add("y=" + Y.ToString(CultureInfo.InvariantCulture));
                lines.Add("scale=" + Scale.ToString("0.00", CultureInfo.InvariantCulture));
                lines.Add("opacity=" + Opacity.ToString("0.00", CultureInfo.InvariantCulture));
                lines.Add("defaultVolume=" + DefaultVolume.ToString(CultureInfo.InvariantCulture));
                lines.Add("resetOnSongChange=" + (ResetOnSongChange ? "1" : "0"));
                lines.Add("autoStart=" + (AutoStart ? "1" : "0"));
                lines.Add("autoGain=" + (AutoGain ? "1" : "0"));
                lines.Add("targetDb=" + TargetDb.ToString("0.0", CultureInfo.InvariantCulture));
                lines.Add("measureSeconds=" + MeasureSeconds.ToString(CultureInfo.InvariantCulture));
                lines.Add("cacheTrustSec=" + CacheTrustSec.ToString(CultureInfo.InvariantCulture));
                lines.Add("spectrum=" + Spectrum.ToString(CultureInfo.InvariantCulture));
                lines.Add("marqueeStep=" + MarqueeStep.ToString(CultureInfo.InvariantCulture));
                lines.Add("marqueeFps=" + MarqueeFps.ToString(CultureInfo.InvariantCulture));
                lines.Add("marqueeHoldMs=" + MarqueeHoldMs.ToString(CultureInfo.InvariantCulture));
                lines.Add("limitMarginDb=" + LimitMarginDb.ToString("0.0", CultureInfo.InvariantCulture));
                lines.Add("toleranceDb=" + ToleranceDb.ToString("0.0", CultureInfo.InvariantCulture));
                File.WriteAllLines(ConfigPath, lines.ToArray(), System.Text.Encoding.UTF8);
            }
            catch { }
        }

        private static int Int(string s, int d)
        {
            int v; return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : d;
        }
        private static float Flt(string s, float d)
        {
            float v; return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : d;
        }
        private static bool Bool(string s, bool d)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            if (s == "1" || s == "true" || s == "yes" || s == "on") return true;
            if (s == "0" || s == "false" || s == "no" || s == "off") return false;
            return d;
        }
    }

    internal static class Log
    {
        private static readonly object _lock = new object();

        /// <summary>日志上限：超过就自动清空，避免长期运行后无限增长。</summary>
        private const long MaxBytes = 1024 * 1024;   // 1 MB

        public static void W(string msg)
        {
            lock (_lock)
            {
                try
                {
                    // 超过 1 MB 就先清空（日志稀疏，stat 一次开销可忽略）
                    var fi = new FileInfo(Config.LogPath);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        File.WriteAllText(Config.LogPath,
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  === 日志超过 1 MB，已自动清空 ===" + Environment.NewLine,
                            System.Text.Encoding.UTF8);
                    }

                    File.AppendAllText(Config.LogPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg + Environment.NewLine,
                        System.Text.Encoding.UTF8);
                }
                catch { }
            }
        }

        public static void Reset()
        {
            try { File.WriteAllText(Config.LogPath, "", System.Text.Encoding.UTF8); } catch { }
        }

        /// <summary>启动时把上一次的日志留存为 log.prev.txt，再清空当前日志。
        /// 这样即使进程被静默终止，也能看到它最后记录到哪一步。</summary>
        public static void Rotate()
        {
            try
            {
                if (File.Exists(Config.LogPath))
                {
                    var fi = new FileInfo(Config.LogPath);
                    if (fi.Length > 0)
                    {
                        string prev = Path.Combine(Config.Dir, "log.prev.txt");
                        try { File.Copy(Config.LogPath, prev, true); } catch { }
                    }
                }
            }
            catch { }
            Reset();
        }
    }
}
