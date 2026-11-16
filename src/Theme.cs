using System.Drawing;

namespace NvHud
{
    /// <summary>
    /// 深/浅两套配色，集中在这里，绘制处只取色、不再散落硬编码。
    /// 切主题只改 Theme.Light，然后让各窗口重绘一次即可。
    /// </summary>
    internal static class Theme
    {
        /// <summary>true = 浅色（白色）风格。</summary>
        public static bool Light = false;

        private static Color C(int r, int g, int b, int a) { return Color.FromArgb(a, r, g, b); }

        // ---------------- 通用前景 ----------------

        /// <summary>前景基色：深色主题为白，浅色主题为近黑。图标、描边、分隔线、悬停底色都由它加 alpha 得到。</summary>
        public static Color Ink
        {
            get { return Light ? C(20, 22, 30, 255) : C(255, 255, 255, 255); }
        }

        /// <summary>按 alpha 取前景色。</summary>
        public static Color Fg(int a)
        {
            Color i = Ink;
            return Color.FromArgb(a, i.R, i.G, i.B);
        }

        // ---------------- 卡片 ----------------

        public static Color CardBg { get { return Light ? C(250, 251, 254, 250) : C(27, 28, 34, 244); } }
        public static Color CardBgOffline { get { return Light ? C(240, 241, 246, 245) : C(24, 24, 28, 235); } }
        public static Color CardGloss { get { return Light ? C(255, 255, 255, 120) : C(255, 255, 255, 5); } }
        public static Color CardStroke { get { return Light ? C(20, 22, 30, 26) : C(255, 255, 255, 13); } }
        public static Color AvatarStroke { get { return Light ? C(20, 22, 30, 46) : C(255, 255, 255, 58); } }
        public static Color AvatarPlaceholder { get { return Light ? C(226, 228, 236, 255) : C(52, 52, 60, 255); } }
        public static Color AvatarDim { get { return Light ? C(244, 245, 249, 200) : C(40, 40, 46, 190); } }

        // ---------------- 文字 ----------------

        public static Color Title { get { return Light ? C(22, 24, 32, 255) : C(232, 234, 242, 255); } }
        public static Color Value { get { return Light ? C(24, 26, 35, 255) : C(246, 246, 250, 255); } }
        public static Color Dim { get { return Light ? C(104, 108, 124, 255) : C(176, 180, 193, 255); } }
        public static Color DimSoft { get { return Light ? C(134, 138, 154, 255) : C(146, 150, 164, 255); } }
        public static Color MuteText { get { return Light ? C(208, 52, 74, 255) : C(255, 110, 110, 255); } }
        public static Color OfflineText { get { return Light ? C(118, 122, 136, 255) : C(126, 128, 140, 255); } }

        // ---------------- 频谱 ----------------

        public static Color SpectrumTop(int a) { return Light ? C(58, 62, 78, a) : C(246, 246, 251, a); }
        public static Color SpectrumBottom(int a) { return Light ? C(132, 136, 152, a) : C(142, 146, 161, a); }

        /// <summary>浅色底上柱子需要稍微加深，观感才和深色一致。</summary>
        public static float SpectrumBoost { get { return Light ? 1.22f : 1f; } }

        // ---------------- 弹层（右键菜单 / 参数面板 / 头像选择器）----------------

        public static Color PanelTop { get { return Light ? C(253, 253, 255, 249) : C(34, 34, 40, 246); } }
        public static Color PanelBottom { get { return Light ? C(242, 243, 249, 249) : C(24, 24, 29, 246); } }
        public static Color PanelStroke { get { return Light ? C(20, 22, 30, 26) : C(255, 255, 255, 30); } }

        /// <summary>面板顶部高光（浅色主题几乎不可见，但仍保留一点点纸面感）。</summary>
        public static Color PanelGloss { get { return Light ? C(255, 255, 255, 150) : C(255, 255, 255, 14); } }

        public static Color PillOff { get { return Light ? C(204, 206, 215, 255) : C(70, 70, 80, 255); } }
        public static Color Knob { get { return Light ? C(255, 255, 255, 250) : C(255, 255, 255, 245); } }
        public static Color CellEmpty { get { return Light ? C(20, 22, 30, 12) : C(255, 255, 255, 8); } }
        public static Color CellFallback { get { return Light ? C(212, 214, 222, 255) : C(60, 60, 70, 255); } }
        public static Color Accent { get { return C(255, 74, 105, 255); } }
        public static Color AccentSoft { get { return Light ? C(226, 62, 88, 255) : C(255, 92, 108, 255); } }
    }
}
