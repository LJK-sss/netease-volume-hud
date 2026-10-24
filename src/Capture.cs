using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace NvHud
{
    #region 进程级环回抓流所需的 COM 互操作

    [StructLayout(LayoutKind.Sequential)]
    internal struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
    {
        public uint TargetProcessId;
        public int ProcessLoopbackMode; // 0 = 包含目标进程树
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public int ActivationType; // 1 = PROCESS_LOOPBACK
        public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PROPVARIANT_BLOB
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint cbSize;
        [FieldOffset(16)] public IntPtr pBlobData;
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    /// <summary>空标记接口：completionHandler 必须实现它，否则 ActivateAudioInterfaceAsync 会返回误导性的 E_UNEXPECTED。</summary>
    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAgileObject { }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    internal sealed class CaptureActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation) { Done.Set(); }
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICaptureAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity,
            IntPtr pFormat, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr pFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr h);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr pData, out uint frames, out uint flags,
            out long devicePosition, out long qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    #endregion

    /// <summary>
    /// 抓取"指定进程这一路"的音频（WASAPI 进程级环回），
    /// 在独立 MTA 线程上运行，每约 100ms 产出一个 RMS dBFS 样本。
    /// 只会拿到网易云的声音，游戏声不会掺进来。
    /// </summary>
    internal sealed class ProcessCapture : IDisposable
    {
        private const string VAD_PROCESS_LOOPBACK = "VAD\\Process_Loopback";
        private const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        private const int AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = unchecked((int)0x80000000);
        private const int AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
        private const int AUDCLNT_BUFFERFLAGS_SILENT = 0x00000002;

        [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            ref Guid riid,
            IntPtr activationParams,
            IntPtr completionHandler,
            out IntPtr activationOperation);

        [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr pv, int coInit);
        [DllImport("ole32.dll")] private static extern void CoUninitialize();

        private readonly ConcurrentQueue<float> _queue = new ConcurrentQueue<float>();
        private readonly object _sync = new object();
        private volatile bool _running;
        private Thread _thread;
        private uint _pid;
        private string _lastError = "";

        public string LastError { get { return _lastError; } }
        public bool IsRunning { get { return _running && _thread != null && _thread.IsAlive; } }

        public bool TryDequeue(out float db)
        {
            return _queue.TryDequeue(out db);
        }

        public void Start(uint pid)
        {
            Stop();
            _pid = pid;
            _running = true;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "NvCapture";
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            Thread t = _thread;
            _thread = null;
            if (t != null && t.IsAlive)
            {
                try { t.Join(1500); } catch { }
            }
            float junk;
            while (_queue.TryDequeue(out junk)) { }
        }

        private void Loop()
        {
            try { CoInitializeEx(IntPtr.Zero, 0x0); } catch { }   // MTA
            try
            {
                RunCapture();
            }
            catch (Exception ex)
            {
                _lastError = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                try { CoUninitialize(); } catch { }
                _running = false;
            }
        }

        private void RunCapture()
        {
            uint pid = _pid;
            if (pid == 0) { _lastError = "pid=0"; return; }

            // 1) 激活进程环回
            var prm = new AUDIOCLIENT_ACTIVATION_PARAMS();
            prm.ActivationType = 1;
            prm.ProcessLoopbackParams.TargetProcessId = pid;
            prm.ProcessLoopbackParams.ProcessLoopbackMode = 0;

            int sz = Marshal.SizeOf(typeof(AUDIOCLIENT_ACTIVATION_PARAMS));
            IntPtr pParams = Marshal.AllocHGlobal(sz);
            Marshal.StructureToPtr(prm, pParams, false);

            var pv = new PROPVARIANT_BLOB();
            pv.vt = 65; // VT_BLOB
            pv.cbSize = (uint)sz;
            pv.pBlobData = pParams;
            IntPtr pProp = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(PROPVARIANT_BLOB)));
            Marshal.StructureToPtr(pv, pProp, false);

            ICaptureAudioClient client = null;
            IntPtr pOp = IntPtr.Zero;
            try
            {
                var handler = new CaptureActivationHandler();
                var iid = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
                IntPtr pHandler = Marshal.GetComInterfaceForObject(handler, typeof(IActivateAudioInterfaceCompletionHandler));
                int hr = ActivateAudioInterfaceAsync(VAD_PROCESS_LOOPBACK, ref iid, pProp, pHandler, out pOp);
                Marshal.Release(pHandler);
                if (hr != 0) { _lastError = "Activate hr=0x" + hr.ToString("X8"); return; }

                if (!handler.Done.Wait(5000)) { _lastError = "激活超时"; return; }

                var op = (IActivateAudioInterfaceAsyncOperation)Marshal.GetObjectForIUnknown(pOp);
                int res; object obj;
                int hrRes = op.GetActivateResult(out res, out obj);
                if (hrRes != 0 || res != 0 || obj == null) { _lastError = "激活结果 hr=0x" + res.ToString("X8"); return; }
                client = (ICaptureAudioClient)obj;

                // 2) 初始化（16bit/44.1k/立体声）
                IntPtr wfx = MakeWaveFormat(44100, 2, 16);
                try
                {
                    hr = client.Initialize(0, AUDCLNT_STREAMFLAGS_LOOPBACK, 200000, 0, wfx, IntPtr.Zero);
                    if (hr != 0)
                        hr = client.Initialize(0, AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                            200000, 0, wfx, IntPtr.Zero);
                    if (hr != 0) { _lastError = "Initialize hr=0x" + hr.ToString("X8"); return; }
                }
                finally { Marshal.FreeHGlobal(wfx); }

                var capIid = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
                object capObj;
                hr = client.GetService(ref capIid, out capObj);
                if (hr != 0) { _lastError = "GetService hr=0x" + hr.ToString("X8"); return; }
                var cap = (ICaptureClient)capObj;

                hr = client.Start();
                if (hr != 0) { _lastError = "Start hr=0x" + hr.ToString("X8"); return; }

                // 3) 采集循环
                double sumSq = 0;
                long samples = 0;
                var lastEmit = DateTime.UtcNow;

                while (_running)
                {
                    Thread.Sleep(20);

                    uint next;
                    while (cap.GetNextPacketSize(out next) == 0 && next > 0)
                    {
                        IntPtr data; uint frames, flags; long dp, qp;
                        if (cap.GetBuffer(out data, out frames, out flags, out dp, out qp) != 0) break;

                        if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0 && data != IntPtr.Zero)
                        {
                            int n = (int)frames * 2;
                            for (int i = 0; i < n; i++)
                            {
                                short s = Marshal.ReadInt16(data, i * 2);
                                double v = s / 32768.0;
                                sumSq += v * v;
                            }
                            samples += n;
                        }
                        else
                        {
                            // 静音包：按零处理，仍然推进采样数
                            samples += (long)frames * 2;
                        }
                        cap.ReleaseBuffer(frames);
                    }

                    var now = DateTime.UtcNow;
                    if ((now - lastEmit).TotalMilliseconds >= 100)
                    {
                        double rms = samples > 0 ? Math.Sqrt(sumSq / samples) : 0;
                        float db = rms > 1e-7 ? (float)(20 * Math.Log10(rms)) : -120f;
                        _queue.Enqueue(db);
                        sumSq = 0; samples = 0;
                        lastEmit = now;
                    }
                }

                try { client.Stop(); } catch { }
            }
            finally
            {
                Marshal.FreeHGlobal(pProp);
                Marshal.FreeHGlobal(pParams);
                if (pOp != IntPtr.Zero) { try { Marshal.Release(pOp); } catch { } }
                if (client != null) { try { Marshal.ReleaseComObject(client); } catch { } }
            }
        }

        private static IntPtr MakeWaveFormat(int rate, short channels, short bits)
        {
            int blockAlign = channels * bits / 8;
            IntPtr p = Marshal.AllocHGlobal(18);
            for (int i = 0; i < 18; i++) Marshal.WriteByte(p, i, 0);
            Marshal.WriteInt16(p, 0, 1);
            Marshal.WriteInt16(p, 2, channels);
            Marshal.WriteInt32(p, 4, rate);
            Marshal.WriteInt32(p, 8, rate * blockAlign);
            Marshal.WriteInt16(p, 12, (short)blockAlign);
            Marshal.WriteInt16(p, 14, bits);
            return p;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
