using System;

namespace NvHud
{
    /// <summary>
    /// 频谱分析：1024 点 FFT → 按对数频率合成 44 个频段 → 平滑成柱子高度。
    /// 采样格式固定为 44100Hz / 16bit / 立体声（见 ProcessCapture）。
    /// </summary>
    internal static class Spectrum
    {
        public const int Bands = 28;
        public const int FftSize = 1024;
        private const int SampleRate = 44100;

        private const float FreqMin = 50f;
        private const float FreqMax = 16000f;

        // 显示范围（dB）：低于 Floor 为 0，高于 Ceil 为满格
        private const float FloorDb = -72f;
        private const float CeilDb = -10f;
        private const float Gamma = 1.4f;      // 高度曲线：>1 让中段更矮

        private static readonly float[] _window = new float[FftSize];
        private static readonly double[] _re = new double[FftSize];
        private static readonly double[] _im = new double[FftSize];
        private static readonly int[] _binStart = new int[Bands];
        private static readonly int[] _binEnd = new int[Bands];
        private static readonly float[] _bars = new float[Bands];

        static Spectrum()
        {
            // Hann 窗
            for (int i = 0; i < FftSize; i++)
                _window[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (FftSize - 1)));

            // 对数频率 → bin 区间（每段至少 1 个 bin）
            double binHz = (double)SampleRate / FftSize;
            for (int b = 0; b < Bands; b++)
            {
                double f0 = FreqMin * Math.Pow(FreqMax / FreqMin, (double)b / Bands);
                double f1 = FreqMin * Math.Pow(FreqMax / FreqMin, (double)(b + 1) / Bands);
                int s = (int)Math.Floor(f0 / binHz);
                int e = (int)Math.Ceiling(f1 / binHz);
                if (s < 1) s = 1;
                if (e <= s) e = s + 1;
                if (e > FftSize / 2) e = FftSize / 2;
                _binStart[b] = s;
                _binEnd[b] = e;
            }
        }

        public static float[] Bars { get { return _bars; } }

        /// <summary>没有音频时把柱子慢慢收回去。</summary>
        public static void Decay()
        {
            for (int i = 0; i < Bands; i++) _bars[i] *= 0.90f;
        }

        public static void Clear()
        {
            for (int i = 0; i < Bands; i++) _bars[i] = 0f;
        }

        /// <summary>分析一帧。volumePercent 用于抵消"音量调低导致频谱整体变矮"。</summary>
        public static void Analyze(float[] samples, float volumePercent)
        {
            if (samples == null || samples.Length < FftSize) return;

            int off = samples.Length - FftSize;
            for (int i = 0; i < FftSize; i++)
            {
                _re[i] = samples[off + i] * _window[i];
                _im[i] = 0.0;
            }
            Fft(_re, _im);

            // 音量归一化：环回是音量处理之后的信号，除回去才反映歌曲本身
            float vol = volumePercent;
            if (vol < 5f) vol = 5f;
            if (vol > 100f) vol = 100f;
            float gain = 100f / vol;
            if (gain > 8f) gain = 8f;

            double norm = 1.0 / (FftSize * 0.5);
            for (int b = 0; b < Bands; b++)
            {
                double peak = 0.0;
                for (int k = _binStart[b]; k < _binEnd[b]; k++)
                {
                    double mag = Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]) * norm * gain;
                    if (mag > peak) peak = mag;
                }

                double db = peak > 1e-9 ? 20.0 * Math.Log10(peak) : -120.0;
                float v = (float)((db - FloorDb) / (CeilDb - FloorDb));
                if (v < 0f) v = 0f;
                if (v > 1f) v = 1f;
                v = (float)Math.Pow(v, Gamma);   // 中段压矮、峰值保留

                // 上升快、下落慢，跳起来有弹性但不抖
                float prev = _bars[b];
                _bars[b] = v > prev ? prev + (v - prev) * 0.65f : prev * 0.85f + v * 0.15f;
            }
        }

        /// <summary>迭代式 radix-2 FFT（原地，长度必须是 2 的幂）。</summary>
        private static void Fft(double[] re, double[] im)
        {
            int n = re.Length;

            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    double tr = re[i]; re[i] = re[j]; re[j] = tr;
                    double ti = im[i]; im[i] = im[j]; im[j] = ti;
                }
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2.0 * Math.PI / len;
                double wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1.0, ci = 0.0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = i + k + len / 2;
                        double xr = re[b] * cr - im[b] * ci;
                        double xi = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - xr; im[b] = im[a] - xi;
                        re[a] += xr;        im[a] += xi;
                        double ncr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = ncr;
                    }
                }
            }
        }
    }
}
