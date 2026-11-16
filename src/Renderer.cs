using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace NvHud
{
    internal sealed class UiState
    {
        public float Scale = 1f;
        public float Opacity = 0.88f;
        public bool Hover = false;
        public bool Muted = false;
        public bool Offline = false;
        public int Volume = 55;
        public string Title = "";
        public float LoudDb = float.NaN;   // 当前曲目实测响度（P80），NaN 表示暂无
        public float TitleOffset = 0f;   // 长标题滚动偏移（逻辑像素）
        public bool TitleScrolls = false;   // 标题在滚动（溢出）：此时不画省略号
        public bool ShowControls = false;   // 悬停时显示菜单/关闭按钮
        public float ClockAngle = 0f;   // 吊钟摆角（度）：由播放活动驱动，空闲时收回到 0
        public float WhaleX = 116f;     // 顶边小人：脚底中心的 x（卡片逻辑坐标）
        public float WhaleBob = 0f;     // 顶边小人：上下浮动偏移
        public float WhaleFaceX = 1f;   // 顶边小人：水平缩放，-1 表示朝左（镜像），过渡时取中间值
        public int WhaleFrame = 0;      // 顶边小人：精灵图帧号
        public float[] Spectrum;   // 44 个 0~1 的柱子高度（null = 不画）
    }

    internal static class Renderer
    {
        public const float W_NORMAL = 196f;
        public const float H_NORMAL = 50f;
        public const float W_HOVER = 196f;   // 与常态同宽：展开只增高，避免右侧出屏
        public const float H_HOVER = 76f;
        /// <summary>
        /// 卡片顶边上方留给小人的高度。窗口比卡片高这么多，这一段全透明。
        /// NOWHALE 编译开关下取 0：所有依赖 TopPad 的几何计算（窗口高度、命中测试、
        /// 绘制平移）都会自动退化，不需要逐处加条件编译。
        /// </summary>
#if NOWHALE
        public const float TopPad = 0f;
#else
        public const float TopPad = 54f;
#endif
        public const float IconCyRatio = 0.62f;   // 图标/数字的垂直中心（相对卡片高度）
        public const float AvatarRadiusRatio = 0.30f;   // 头像圆角半径（相对边长）

        // ---- 内置头像（从 exe 资源加载，artwork 文件夹为空时使用）----
        private static Bitmap _avatar;
        private static bool _artTried;


        /// <summary>内置头像（exe 资源），artwork 文件夹为空时使用。</summary>
        public static Bitmap EmbeddedAvatar { get { EnsureArt(); return _avatar; } }

        /// <summary>频谱柱子浓度 0~1（参数面板可调）。</summary>
        public static float SpectrumAlpha = 0.28f;
        public static bool SpectrumGridMode = true;   // true 方格 / false 连续柱

        private static void EnsureArt()
        {
            if (_artTried) return;
            _artTried = true;
            try
            {
                var asm = typeof(Renderer).Assembly;
                using (var s = asm.GetManifestResourceStream("avatar.png"))
                    if (s != null) _avatar = new Bitmap(s);
            }
            catch { }
        }

        public const string TitleFontName = "Microsoft YaHei UI";
        public const float TitleFontPx = 12.5f;

        /// <summary>标题可用的宽度（逻辑像素）：右侧要留给音量/dB 和悬停按钮。</summary>
        public const float TitleViewportW = 96f;   // 标题区从左边距 12 开始

        /// <summary>按绘制用的同款字体测量标题宽度（逻辑像素）。</summary>
        public static float MeasureTitleWidth(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            try
            {
                using (var tmp = new Bitmap(1, 1))
                using (var g = Graphics.FromImage(tmp))
                using (var f = new Font(TitleFontName, TitleFontPx, FontStyle.Regular, GraphicsUnit.Pixel))
                    return g.MeasureString(text, f).Width;
            }
            catch { return 0f; }
        }

        /// <summary>卡片本体的尺寸（不含顶部的透明带）。</summary>
        public static SizeF CardSize(bool hover)
        {
            return hover ? new SizeF(W_HOVER, H_HOVER) : new SizeF(W_NORMAL, H_NORMAL);
        }

        /// <summary>整个窗口的尺寸 = 卡片 + 顶部小人用的透明带。</summary>
        public static SizeF LogicalSize(bool hover)
        {
            SizeF c = CardSize(hover);
            return new SizeF(c.Width, c.Height + TopPad);
        }

        /// <summary>往已有路径里加一个圆角矩形。</summary>
        private static void AddRoundedRect(GraphicsPath path, float x, float y, float w, float h, float r)
        {
            if (r < 0.5f) r = 0.5f;
            if (r > w / 2f) r = w / 2f;
            if (r > h / 2f) r = h / 2f;
            float d = r * 2f;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();
        }

        private static GraphicsPath Round(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath();
            float d = r * 2f;
            if (d > h) d = h;
            if (d > w) d = w;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static Color C(int r, int g, int b, int a)
        {
            return Color.FromArgb(a, r, g, b);
        }

        private static LinearGradientBrush Grad(float x1, float y1, float x2, float y2, Color a, Color b)
        {
            return new LinearGradientBrush(new PointF(x1, y1), new PointF(x2, y2), a, b);
        }

        private static StringFormat Near()
        {
            var sf = new StringFormat();
            sf.Alignment = StringAlignment.Near;
            sf.LineAlignment = StringAlignment.Center;
            sf.FormatFlags = StringFormatFlags.NoWrap;
            return sf;
        }

        private static StringFormat Center()
        {
            var sf = new StringFormat();
            sf.Alignment = StringAlignment.Center;
            sf.LineAlignment = StringAlignment.Center;
            return sf;
        }

        /// <summary>按 alpha 画一张图（用于头像淡入淡出）。</summary>
        /// <summary>
        /// 频谱只承担氛围：位于封面右侧，避开标题，视觉亮度低于主要内容。
        /// visualGain 只影响绘制，不改写用户的频谱浓度配置。
        /// </summary>
        private static void DrawSpectrum(Graphics g, float[] bars, float h, bool controlsVisible)
        {
            const float X0 = 64f;
            const float X1 = 184f;
            const float TOP = 33f;
            float bottom = h - 6f;
            float visualGain = controlsVisible ? 0.61f : 0.70f;
            float w = X1 - X0;
            float maxH = bottom - TOP;
            if (maxH <= 2f) return;

            int n = bars.Length;
            if (n <= 0) return;
            float pitch = w / n;
            float bw = Math.Max(1.2f, pitch - 1.1f);

            int a = (int)(255 * Math.Max(0f, Math.Min(1f, SpectrumAlpha)) * visualGain * Theme.SpectrumBoost);
            if (a > 255) a = 255;
            var st0 = g.Save();
            g.SetClip(Round(0, 0, W_HOVER, h, 18f), CombineMode.Intersect);
            if (SpectrumGridMode)
            {
                DrawSpectrumGrid(g, bars, X0, X1, TOP, bottom, visualGain);
                g.Restore(st0);
                return;
            }
            g.SmoothingMode = SmoothingMode.None;

            using (var br = new LinearGradientBrush(
                new PointF(X0, TOP), new PointF(X0, bottom),
                Theme.SpectrumTop(a), Theme.SpectrumBottom((int)(a * 0.72f))))
            {
                for (int i = 0; i < n; i++)
                {
                    float v = bars[i];
                    if (v < 0.02f) continue;
                    float bh = Math.Max(1.5f, v * maxH);
                    float x = X0 + i * pitch;
                    g.FillRectangle(br, x, bottom - bh, bw, bh);
                }
            }

            g.Restore(st0);
        }

        /// <summary>方格版频谱：每列由若干个小方格堆起来（3px 格 + 1px 缝）。</summary>
        private static void DrawSpectrumGrid(Graphics g, float[] bars, float x0, float x1, float top, float bottom, float visualGain)
        {
            int n = bars.Length;
            if (n <= 0) return;
            float w = x1 - x0;
            float maxH = bottom - top;
            if (maxH <= 2f) return;

            const float cell = 3f;      // 方格边长（逻辑像素）
            const float gap = 1f;
            float pitchY = cell + gap;
            int levels = (int)Math.Floor((maxH + gap) / pitchY);
            if (levels < 1) levels = 1;

            float pitchX = w / n;
            float cellW = Math.Min(cell + 0.6f, pitchX - 1f);
            if (cellW < 1.2f) cellW = 1.2f;

            int a = (int)(255 * Math.Max(0f, Math.Min(1f, SpectrumAlpha)) * visualGain * Theme.SpectrumBoost);
            if (a > 255) a = 255;
            g.SmoothingMode = SmoothingMode.None;

            // 先数出要画多少格，再一次性批量填充（FillRectangles 一次调用比循环快很多）
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                float v = bars[i];
                if (v < 0.02f) continue;
                int lit = (int)Math.Round(v * levels);
                if (lit < 1) lit = 1;
                count += lit;
            }
            if (count <= 0) return;

            var rects = new RectangleF[count];
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                float v = bars[i];
                if (v < 0.02f) continue;
                int lit = (int)Math.Round(v * levels);
                if (lit < 1) lit = 1;
                float x = x0 + i * pitchX;
                for (int lv = 0; lv < lit; lv++)
                {
                    float y = bottom - (lv + 1) * pitchY + gap;
                    rects[k++] = new RectangleF(x, y, cellW, cell);
                }
            }

            using (var br = new LinearGradientBrush(
                new PointF(x0, top), new PointF(x0, bottom),
                Theme.SpectrumTop(a), Theme.SpectrumBottom((int)(a * 0.72f))))
            {
                g.FillRectangles(br, rects);
            }
        }

        private static void DrawWithAlpha(Graphics g, Image img, RectangleF dst, float alpha)
        {
            if (img == null || alpha <= 0.01f) return;
            if (alpha > 0.99f) { g.DrawImage(img, dst); return; }
            var cm = new ColorMatrix();
            cm.Matrix33 = alpha;
            using (var ia = new ImageAttributes())
            {
                ia.SetColorMatrix(cm);
                g.DrawImage(img, new Rectangle((int)dst.X, (int)dst.Y, (int)dst.Width, (int)dst.Height),
                    0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
            }
        }

        // ---- 吊钟（纯装饰）----
        // 挂在频谱区顶边、右上角：表盘 18px，指针走真实时间，摆角由 Overlay 每帧给。
        private const float ClockPivotX = 168f;
        private const float ClockPivotY = 29f;
        private const float ClockRod = 5f;
        private const float ClockR = 9f;

        private static void DrawClock(Graphics g, float angleDeg, bool offline)
        {
            GraphicsState save = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(ClockPivotX, ClockPivotY);

            // 挂点：一小段横杆，暗示"天花板"
            using (var pen = new Pen(Theme.Fg(58), 1f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, -3.2f, 0f, 3.2f, 0f);
            }

            g.RotateTransform(angleDeg);

            using (var pen = new Pen(Theme.Fg(132), 1f))
                g.DrawLine(pen, 0f, 0f, 0f, ClockRod + ClockR);

            float cy = ClockRod + ClockR;
            using (var b = new SolidBrush(offline ? Theme.CardBgOffline : Theme.CardBg))
                g.FillEllipse(b, -ClockR, cy - ClockR, ClockR * 2f, ClockR * 2f);
            using (var b = new SolidBrush(Theme.Fg(12)))
                g.FillEllipse(b, -ClockR, cy - ClockR, ClockR * 2f, ClockR * 2f);
            using (var pen = new Pen(Theme.Fg(150), 1f))
                g.DrawEllipse(pen, -ClockR, cy - ClockR, ClockR * 2f, ClockR * 2f);

            // 只有时针分针：18px 表盘上秒针每格只挪 0.6px，看不出来，不如不画
            DateTime now = DateTime.Now;
            float hourAng = ((now.Hour % 12) + now.Minute / 60f) * 30f;
            float minAng = (now.Minute + now.Second / 60f) * 6f;
            DrawClockHand(g, cy, hourAng, ClockR * 0.52f, 1.15f, 215);
            DrawClockHand(g, cy, minAng, ClockR * 0.78f, 0.9f, 175);

            using (var b = new SolidBrush(Theme.Fg(220)))
                g.FillEllipse(b, -0.9f, cy - 0.9f, 1.8f, 1.8f);

            g.Restore(save);
        }

        private static void DrawClockHand(Graphics g, float cy, float deg, float len, float width, int alpha)
        {
            double rad = (deg - 90.0) * Math.PI / 180.0;
            using (var pen = new Pen(Theme.Fg(alpha), width))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, 0f, cy,
                    (float)(Math.Cos(rad) * len), cy + (float)(Math.Sin(rad) * len));
            }
        }

#if !NOWHALE
        /// <summary>
        /// 卡片顶边上的小鲸鱼娘：脚底踩在卡片上边缘（y = TopPad），走在这条 196px 的“地面”上。
        /// 素材是像素画，用双三次缩放比最近邻更稳（原图已做过一次非整数倍缩小）。
        /// </summary>
        private static void DrawWhale(Graphics g, UiState st)
        {
            Bitmap bmp = WhaleSprite.Sheet;
            if (bmp == null) return;

            int f = st.WhaleFrame;
            if (f < 0) f = 0;
            if (f >= WhaleSprite.FrameCount) f = WhaleSprite.FrameCount - 1;

            // 想出大小只改这一个数（上限 TopPad，超了会被窗口顶边裁掉）
            const float DrawH = 54f;
            float k = DrawH / WhaleSprite.FrameH;
            float fw = WhaleSprite.FrameW * k;

            var src = new Rectangle(f * WhaleSprite.FrameW, 0, WhaleSprite.FrameW, WhaleSprite.FrameH);

            // 朝向：素材只有正面，靠水平镜像让"脸朝着走的方向"。
            // 这里直接量化成 ±1：15fps 下把 100ms 的压缩过渡画出来就是一条细线，
            // 反而像掉帧；正面构图左右翻转本身几乎看不出，硬切最干净。
            float face = st.WhaleFaceX < 0f ? -1f : 1f;

            var old = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            GraphicsState gs = g.Save();          // 一并记住插值模式，Restore 时自动还原
            try
            {
                // 锚点放在脚底中心：缩放/镜像都不会让脚离开卡片顶边
                g.TranslateTransform(st.WhaleX, TopPad + st.WhaleBob);
                g.ScaleTransform(face, 1f);
                g.DrawImage(bmp, new RectangleF(-fw * 0.5f, -DrawH, fw, DrawH), src, GraphicsUnit.Pixel);
            }
            finally { g.Restore(gs); g.InterpolationMode = old; }
        }
#endif

        public static void Draw(Graphics g, UiState st)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.ScaleTransform(st.Scale, st.Scale);
            EnsureArt();

            // 顶边小人画在卡片之外，必须在卡片裁剪之前画
#if !NOWHALE
            DrawWhale(g, st);
#endif

            // 之后所有卡片绘制整体下移 TopPad：原有坐标一个都不用改
            g.TranslateTransform(0f, TopPad);

            SizeF sz = CardSize(st.Hover);
            float w = sz.Width, h = sz.Height;
            float r = 18f;

            bool off = st.Offline;
            bool mute = st.Muted;

            // 卡片底
            using (var cp = Round(0, 0, w, h, r))
            {
                using (var fill = new SolidBrush(off ? Theme.CardBgOffline : Theme.CardBg))
                    g.FillPath(fill, cp);


                // 极轻的高光与描边：保持挂件的轮廓，不做发亮玻璃效果
                using (var hp = Round(0, 0, w, h * 0.55f, r))
                using (var hg = Grad(0, 0, 0, h * 0.55f, Theme.CardGloss, Color.FromArgb(0, 255, 255, 255)))
                    g.FillPath(hg, hp);

                using (var pen = new Pen(Theme.CardStroke, 1f))
                    g.DrawPath(pen, cp);
            }

            // ---- 频谱背景（画在内容下面）----
            if (st.Spectrum != null && SpectrumAlpha > 0.01f && !off)
            {
                DrawSpectrum(g, st.Spectrum, h, st.ShowControls);
            }

            float numCy = h * IconCyRatio;
            float iconD = 44f;

            // 封面是挂件的视觉锚点；常态与悬停保持固定位置，避免布局跳动
            float iconX = 10f;
            float iconY = 26f;
            Bitmap face = Artwork.Current;
            if (face != null)
            {
                var st1 = g.Save();
                using (var ip2 = new GraphicsPath())
                {
                    AddRoundedRect(ip2, iconX, iconY, iconD, iconD, iconD * AvatarRadiusRatio);
                    g.SetClip(ip2);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    // 轮换时淡入淡出：上一张逐渐透明，当前张逐渐显现
                    if (Artwork.HasFade && Artwork.Previous != null)
                    {
                        DrawWithAlpha(g, Artwork.Previous, new RectangleF(iconX, iconY, iconD, iconD), 1f - Artwork.Fade);
                        DrawWithAlpha(g, face, new RectangleF(iconX, iconY, iconD, iconD), Artwork.Fade);
                    }
                    else
                    {
                        g.DrawImage(face, new RectangleF(iconX, iconY, iconD, iconD));
                    }
                    if (off)
                    {
                        using (var dim = new SolidBrush(Theme.AvatarDim)) g.FillPath(dim, ip2);
                    }
                    g.Restore(st1);
                    using (var pen = new Pen(Theme.AvatarStroke, 0.7f)) g.DrawPath(pen, ip2);
                }
            }
            else
            {
                using (var ip = new GraphicsPath())
                {
                    AddRoundedRect(ip, iconX, iconY, iconD, iconD, iconD * AvatarRadiusRatio);
                    if (off)
                    {
                        using (var b = new SolidBrush(Theme.AvatarPlaceholder)) g.FillPath(b, ip);
                    }
                    else
                    {
                        using (var b = Grad(iconX, iconY, iconX + iconD, iconY + iconD, C(255, 70, 105, 255), C(255, 143, 92, 255)))
                            g.FillPath(b, ip);
                    }
                    using (var pen = new Pen(Theme.Fg(46), 0.7f)) g.DrawPath(pen, ip);
                }
                using (var fNote = new Font("Segoe UI Symbol", iconD * 0.50f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var bNote = new SolidBrush(C(255, 255, 255, 242)))
                using (var sf = Center())
                    g.DrawString("\u266A", fNote, bNote, new RectangleF(iconX, iconY, iconD, iconD), sf);
            }

            // 文字统一用 ClearType（与参数面板/右键菜单一致），边缘更实
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            float cx = iconX + iconD + 9f;

            // 曲目名：挂件主角，任何状态下都常驻（卡片本身永久展开，不再区分常态/悬停），超长时滚动
            float tvX = 12f, tvY = 10f, tvW = TitleViewportW, tvH = 16f;
            using (var fT = new Font(TitleFontName, TitleFontPx, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var bT = new SolidBrush(Theme.Title))
            {
                var viewport = new RectangleF(tvX, tvY, tvW, tvH);
                if (st.TitleScrolls)
                {
                    // 滚动中：裁剪到视口，整段文字按偏移左移
                    var oldClip = g.Clip;
                    g.SetClip(viewport, CombineMode.Intersect);
                    using (var sf2 = Near())
                        g.DrawString(st.Title, fT, bT, new RectangleF(tvX - st.TitleOffset, tvY, 2000f, tvH), sf2);
                    g.Clip = oldClip;
                }
                else
                {
                    using (var sf = new StringFormat())
                    {
                        sf.Alignment = StringAlignment.Near;
                        sf.LineAlignment = StringAlignment.Center;
                        sf.FormatFlags = StringFormatFlags.NoWrap;
                        sf.Trimming = StringTrimming.None;   // 不用省略号：右端交给渐隐
                        g.DrawString(st.Title, fT, bT, viewport, sf);
                    }
                }
            }

            // 右端渐隐：用卡片自身底色做一层水平渐变，盖住标题最后 22px，避免硬切
            const float fadeW = 22f;
            Color cb = Theme.CardBg;
            var fadeRect = new RectangleF(tvX + tvW - fadeW, tvY - 1f, fadeW, tvH + 2f);
            using (var fb = new LinearGradientBrush(fadeRect,
                Color.FromArgb(0, cb.R, cb.G, cb.B),
                Color.FromArgb(cb.A, cb.R, cb.G, cb.B),
                LinearGradientMode.Horizontal))
                g.FillRectangle(fb, fadeRect);

            // 异常状态优先；否则音量与 dB 常驻在标题行右侧，不压下面的频谱
            if (mute)
            {
                using (var fM = new Font("Microsoft YaHei UI", 15.5f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var bM = new SolidBrush(Theme.MuteText))
                using (var sf = Near())
                    g.DrawString("已静音", fM, bM, new RectangleF(cx, numCy - 12f, 112f, 24f), sf);
            }
            else if (off)
            {
                using (var fO = new Font("Microsoft YaHei UI", 13.5f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var bO = new SolidBrush(Theme.OfflineText))
                using (var sf = Near())
                    g.DrawString("未连接", fO, bO, new RectangleF(cx, numCy - 11f, 112f, 22f), sf);
            }
            else
            {
                int pct = st.Volume;
                if (pct < 0) pct = 0;
                if (pct > 100) pct = 100;

                // 悬停时齿轮/关闭占用右上角，读数整体左移让位；dB 只在常态显示，避免挤掉歌名
                float rightEdge = st.ShowControls ? 138f : 184f;
                bool showDb = !st.ShowControls && !float.IsNaN(st.LoudDb);

                using (var fP = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var fD = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var sfR = new StringFormat())
                {
                    sfR.Alignment = StringAlignment.Far;
                    sfR.LineAlignment = StringAlignment.Center;
                    sfR.FormatFlags = StringFormatFlags.NoWrap;

                    float boxY = tvY - 1f, boxH = tvH + 2f;
                    float edge = rightEdge;

                    if (showDb)
                    {
                        string dbText = st.LoudDb.ToString("0.0") + " dB";
                        SizeF ds = g.MeasureString(dbText, fD);
                        using (var bD = new SolidBrush(Theme.DimSoft))
                            g.DrawString(dbText, fD, bD, new RectangleF(edge - ds.Width, boxY, ds.Width, boxH), sfR);
                        edge -= ds.Width;
                    }

                    string pctText = pct + "%";
                    SizeF ps = g.MeasureString(pctText, fP);
                    using (var bP = new SolidBrush(Theme.Dim))
                        g.DrawString(pctText, fP, bP, new RectangleF(edge - ps.Width, boxY, ps.Width, boxH), sfR);
                }
            }

            // 吊钟：画在频谱之上、悬停控件之下
            DrawClock(g, st.ClockAngle, off);

            // 参数按钮（三条滑杆），只在悬停时出现，位于 × 左侧
            if (st.ShowControls)   // 离线时也要能开菜单 / 退出，否则窗口关不掉
            {
                float gx = w - 40f, gy = 17f;
                int ga = 190;
                using (var pen = new Pen(Theme.Fg(ga), 1.1f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, gx - 6f, gy - 4.5f, gx + 6f, gy - 4.5f);
                    g.DrawLine(pen, gx - 6f, gy, gx + 6f, gy);
                    g.DrawLine(pen, gx - 6f, gy + 4.5f, gx + 6f, gy + 4.5f);
                }
                using (var dot = new SolidBrush(Theme.Fg(ga)))
                {
                    g.FillEllipse(dot, gx - 2f, gy - 6.3f, 3.4f, 3.4f);
                    g.FillEllipse(dot, gx + 2.6f, gy - 1.8f, 3.4f, 3.4f);
                    g.FillEllipse(dot, gx - 4.6f, gy + 2.7f, 3.4f, 3.4f);
                }
            }
            // 关闭按钮：只在悬停展开时出现（位置固定，不会让鼠标追着跑）
            if (st.ShowControls)   // 离线时也要能开菜单 / 退出，否则窗口关不掉
            {
                float ccx = w - 15f;
                float ccy = 17f;
                float half = 5.2f;
                int alpha = 210;
                using (var pen = new Pen(Theme.Fg(alpha), 1.15f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, ccx - half, ccy - half, ccx + half, ccy + half);
                    g.DrawLine(pen, ccx + half, ccy - half, ccx - half, ccy + half);
                }
            }
        }

    }
}
