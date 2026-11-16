using System;
using System.Drawing;
using System.Reflection;

namespace NvHud
{
    /// <summary>
    /// 卡片顶边上的小鲸鱼娘（社区二创形象「大肥鱼」/ 鲸鱼娘）。
    ///
    /// 形象出处：原型「溟月」@上善无形；DeepSeek 女仆版 @ZipZipPipe；Q 版 @这个刀子真甜。
    /// 素材来自开源桌宠 PC2005-cloud/dsh-pet，授权 CC BY-NC-SA 4.0（署名 · 非商业 · 同协议）。
    /// 这不是 DeepSeek 官方形象。
    ///
    /// 一张 31 帧横排精灵图（每帧 48x54），按帧区间划分成 6 个动作：
    ///   0-1    Idle     站姿：睁眼 / 闭眼
    ///   2-9    Walk     原地踏步循环，8 帧 = 源素材的一个完整循环（自相关测得周期 8 帧）
    ///   10-15  Hum      悠闲哼歌，最后两帧是睁眼收势
    ///   16-20  Yawn     打哈欠，末帧回到睁眼
    ///   21-25  Startle  被吓一跳，末帧回到睁眼
    ///   26-30  Doze     打盹，末帧回到睁眼
    ///
    /// 「末帧回到睁眼」是关键：动作播完会直接切回 Idle 的睁眼帧，
    /// 如果末帧还闭着眼/张着嘴，那一下就是硬切，看着很突兀。
    ///
    /// PNG 以**托管资源**形式编进 exe（csc /resource:whale.png,NvHud.whale.png），
    /// 不走 base64 常量：同样内容 base64 + UTF-16 存储会让 exe 膨胀到原始字节的 5.5 倍，
    /// 而托管资源原样存二进制，只多一次 PE 段对齐。
    ///
    /// 编译期若定义 NOWHALE（build.ps1 -NoWhale），本文件整体不参与编译、
    /// 资源也不嵌入，产出的 LevelBand-lite.exe 完全不含这一切。
    /// </summary>
    internal static class WhaleSprite
    {
        public const int FrameW = 48;
        public const int FrameH = 54;
        public const int FrameCount = 31;

        // ---- 动作帧区间：改动作顺序时只改这里，Overlay 的状态机读这些常量 ----
        public const int IdleStart = 0;    public const int IdleCount = 2;
        public const int WalkStart = 2;    public const int WalkCount = 8;
        public const int HumStart = 10;    public const int HumCount = 6;
        public const int YawnStart = 16;   public const int YawnCount = 5;
        public const int StartleStart = 21; public const int StartleCount = 5;
        public const int DozeStart = 26;   public const int DozeCount = 5;

        /// <summary>资源名必须和 build.ps1 里 /resource:...,NvHud.whale.png 的后半段完全一致。</summary>
        private const string ResName = "NvHud.whale.png";

        private static Bitmap _sheet;
        private static bool _tried;

        public static Bitmap Sheet
        {
            get
            {
                if (_tried) return _sheet;
                _tried = true;
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream(ResName))
                    {
                        if (s == null) return null;
                        using (var tmp = new Bitmap(s))
                            _sheet = new Bitmap(tmp);      // 复制一份，避免流释放后 GDI+ 失效
                    }
                }
                catch { _sheet = null; }
                return _sheet;
            }
        }
    }
}
