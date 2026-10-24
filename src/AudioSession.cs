using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace NvHud
{
    #region COM 互操作（Core Audio / WASAPI）

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr audioSessionGuid, uint flags, out IntPtr sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr audioSessionGuid, uint flags, out ISimpleAudioVolume volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
        [PreserveSig] int RegisterSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
        [PreserveSig] int GetProcessId(out uint retVal);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute(bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute(out bool mute);
    }

    #endregion

    /// <summary>绑定到网易云的那一路音频会话（音量合成器里的那一根推子）。</summary>
    internal sealed class AudioSession
    {
        public ISimpleAudioVolume Api;
        public uint Pid;
        public string SessionId = "";
        public string ExeName = "";
        public string DeviceId = "";
        public int State = -1;   // 1 = Active

        public bool IsAlive
        {
            get
            {
                if (Api == null) return false;
                float v;
                try { return Api.GetMasterVolume(out v) == 0; }
                catch { return false; }
            }
        }

        public float GetVolume()
        {
            if (Api == null) return -1f;
            float v;
            try { return Api.GetMasterVolume(out v) == 0 ? v : -1f; }
            catch { return -1f; }
        }

        public bool SetVolume(float v)
        {
            if (Api == null) return false;
            if (v < 0f) v = 0f;
            if (v > 1f) v = 1f;
            Guid ctx = Guid.Empty;
            try { return Api.SetMasterVolume(v, ref ctx) == 0; }
            catch { return false; }
        }

        public bool GetMute()
        {
            if (Api == null) return false;
            bool m;
            try { return Api.GetMute(out m) == 0 && m; }
            catch { return false; }
        }

        public bool SetMute(bool m)
        {
            if (Api == null) return false;
            Guid ctx = Guid.Empty;
            try { return Api.SetMute(m, ref ctx) == 0; }
            catch { return false; }
        }

        /// <summary>当前默认渲染设备的 ID。</summary>
        public static string DefaultDeviceId()
        {
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                IMMDevice dev;
                if (en.GetDefaultAudioEndpoint(0, 0, out dev) == 0)
                {
                    string id; if (dev.GetId(out id) == 0) return id;
                }
                if (en.GetDefaultAudioEndpoint(0, 1, out dev) == 0)
                {
                    string id; if (dev.GetId(out id) == 0) return id;
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// 在所有渲染设备上查找指定进程的音频会话。
        /// 重要：网易云切换输出设备后，旧设备上会残留一个不再出声的会话。
        /// 必须优先选「默认设备上 + Active」的那一个，否则就是在调僵尸会话（表现为拖动滑块无反应）。
        /// </summary>
        public static AudioSession Find(string exeName)
        {
            string needle = exeName.ToLowerInvariant();
            string def = DefaultDeviceId();
            var found = new List<AudioSession>();

            IMMDeviceEnumerator en;
            try { en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom(); }
            catch { return null; }

            IMMDeviceCollection col;
            if (en.EnumAudioEndpoints(0 /*eRender*/, 1 /*ACTIVE*/, out col) != 0) return null;

            int devCount;
            if (col.GetCount(out devCount) != 0) return null;

            for (int d = 0; d < devCount; d++)
            {
                IMMDevice dev;
                if (col.Item(d, out dev) != 0) continue;

                string devId = "";
                try { dev.GetId(out devId); } catch { }

                object mgrObj;
                Guid iid = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
                if (dev.Activate(ref iid, 23, IntPtr.Zero, out mgrObj) != 0) continue;

                var mgr = (IAudioSessionManager2)mgrObj;
                IAudioSessionEnumerator sesEnum;
                if (mgr.GetSessionEnumerator(out sesEnum) != 0) continue;

                int n;
                if (sesEnum.GetCount(out n) != 0) continue;

                for (int i = 0; i < n; i++)
                {
                    IAudioSessionControl ctl;
                    if (sesEnum.GetSession(i, out ctl) != 0) continue;

                    string sid = "";
                    uint pid = 0;
                    int state = -1;
                    try
                    {
                        var c2 = (IAudioSessionControl2)ctl;
                        c2.GetSessionIdentifier(out sid);
                        c2.GetProcessId(out pid);
                        ctl.GetState(out state);
                    }
                    catch { }

                    if (sid == null) sid = "";
                    if (sid.ToLowerInvariant().IndexOf(needle, StringComparison.Ordinal) < 0) continue;

                    ISimpleAudioVolume api;
                    try { api = (ISimpleAudioVolume)ctl; }
                    catch { continue; }
                    if (api == null) continue;

                    var s = new AudioSession();
                    s.Pid = pid;
                    s.SessionId = sid;
                    s.ExeName = exeName;
                    s.DeviceId = devId;
                    s.State = state;
                    s.Api = api;
                    found.Add(s);
                }
            }

            if (found.Count == 0) return null;
            foreach (var s in found) if (s.DeviceId == def && s.State == 1) return s;
            foreach (var s in found) if (s.DeviceId == def) return s;
            foreach (var s in found) if (s.State == 1) return s;
            return found[0];
        }
        /// <summary>列出所有会话（调试用）。</summary>
        public static List<string> Dump()
        {
            var outp = new List<string>();
            IMMDeviceEnumerator en;
            try { en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom(); }
            catch { return outp; }

            IMMDeviceCollection col;
            if (en.EnumAudioEndpoints(0, 1, out col) != 0) return outp;
            int devCount;
            if (col.GetCount(out devCount) != 0) return outp;

            for (int d = 0; d < devCount; d++)
            {
                IMMDevice dev;
                if (col.Item(d, out dev) != 0) continue;
                object mgrObj;
                Guid iid = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
                if (dev.Activate(ref iid, 23, IntPtr.Zero, out mgrObj) != 0) continue;
                var mgr = (IAudioSessionManager2)mgrObj;
                IAudioSessionEnumerator sesEnum;
                if (mgr.GetSessionEnumerator(out sesEnum) != 0) continue;
                int n;
                if (sesEnum.GetCount(out n) != 0) continue;
                for (int i = 0; i < n; i++)
                {
                    IAudioSessionControl ctl;
                    if (sesEnum.GetSession(i, out ctl) != 0) continue;
                    string sid = ""; uint pid = 0;
                    try
                    {
                        var c2 = (IAudioSessionControl2)ctl;
                        c2.GetSessionIdentifier(out sid);
                        c2.GetProcessId(out pid);
                    }
                    catch { }
                    float vol = -1; bool mute = false;
                    try
                    {
                        var sav = (ISimpleAudioVolume)ctl;
                        sav.GetMasterVolume(out vol);
                        sav.GetMute(out mute);
                    }
                    catch { }
                    outp.Add(string.Format("dev[{0}] pid={1} vol={2:P0} mute={3} id={4}", d, pid, vol, mute, sid));
                }
            }
            return outp;
        }
    }
}
