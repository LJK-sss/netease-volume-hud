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
    }

    internal static class Renderer
    {
        public const float W_NORMAL = 196f;
        public const float H_NORMAL = 50f;
        public const float W_HOVER = 196f;   // 与常态同宽：展开只增高，避免右侧出屏
        public const float H_HOVER = 76f;

        // ---- 内置画作（头像 + 线稿水印），从 exe 资源加载 ----
        private static Bitmap _avatar;
        private static Bitmap _lines;
        private static ImageAttributes _linesAttr;
        private static float _linesAttrAlpha = -1f;
        private static bool _artTried;

        /// <summary>线稿水印浓度 0~1（可由参数面板调整）。</summary>
        public static float LineAlpha = 0.22f;

        private static void EnsureArt()
        {
            if (_artTried) return;
            _artTried = true;
            try
            {
                var asm = typeof(Renderer).Assembly;
                using (var s = asm.GetManifestResourceStream("avatar.png"))
                    if (s != null) _avatar = new Bitmap(s);
                using (var s = asm.GetManifestResourceStream("lines.png"))
                    if (s != null) _lines = new Bitmap(s);
            }
            catch { }
        }

        public const string TitleFontName = "Microsoft YaHei UI";
        public const float TitleFontPx = 11.5f;

        /// <summary>悬停时标题可用的宽度（逻辑像素）。</summary>
        public const float TitleViewportW = W_HOVER - 12f - 52f;   // 标题区从左边距 12 开始

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

        public static SizeF LogicalSize(bool hover)
        {
            return hover ? new SizeF(W_HOVER, H_HOVER) : new SizeF(W_NORMAL, H_NORMAL);
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

        public static void Draw(Graphics g, UiState st)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.ScaleTransform(st.Scale, st.Scale);
            EnsureArt();

            SizeF sz = LogicalSize(st.Hover);
            float w = sz.Width, h = sz.Height;
            float r = st.Hover ? 16f : 14f;

            bool off = st.Offline;
            bool mute = st.Muted;
            bool hover = st.Hover;

            // 卡片底
            using (var cp = Round(0, 0, w, h, r))
            {
                using (var fill = new SolidBrush(off ? C(24, 24, 28, 235) : ((st.ShowControls ? C(33, 33, 40, 246) : C(28, 28, 34, 244)))))
                    g.FillPath(fill, cp);

                // 线稿水印（内置画作），浓度可调
                if (_lines != null && LineAlpha > 0.01f)
                {
                    try
                    {
                        if (_linesAttr == null || Math.Abs(_linesAttrAlpha - LineAlpha) > 0.001f)
                        {
                            _linesAttrAlpha = LineAlpha;
                            var cm = new System.Drawing.Imaging.ColorMatrix();
                            cm.Matrix33 = LineAlpha;
                            var ia = new System.Drawing.Imaging.ImageAttributes();
                            ia.SetColorMatrix(cm);
                            _linesAttr = ia;
                        }
                        var st0 = g.Save();
                        g.SetClip(cp, CombineMode.Intersect);
                        g.DrawImage(_lines, new Rectangle(0, 0, (int)w, (int)h),
                            0, 0, _lines.Width, _lines.Height, GraphicsUnit.Pixel, _linesAttr);
                        g.Restore(st0);
                    }
                    catch { }
                }

                // 顶部高光
                using (var hp = Round(0, 0, w, h * 0.6f, r))
                using (var hg = Grad(0, 0, 0, h * 0.6f, C(255, 255, 255, 14), C(255, 255, 255, 0)))
                    g.FillPath(hg, hp);

                // 内描边
                using (var pen = new Pen(C(255, 255, 255, 26), 0.8f))
                    g.DrawPath(pen, cp);
            }

            float numCy, iconD, numPt;
            if (hover) { numCy = h * 0.62f; iconD = 40f; numPt = 22f; }
            else { numCy = h * 0.42f; iconD = 28f; numPt = 17f; }

            // 图标位：有内置画作时用圆形头像，否则退回渐变圆 + 音符
            float iconX = 9f;
            float iconY = numCy - iconD / 2f;
            if (_avatar != null)
            {
                var st1 = g.Save();
                using (var ip2 = new GraphicsPath())
                {
                    ip2.AddEllipse(iconX, iconY, iconD, iconD);
                    g.SetClip(ip2);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(_avatar, new RectangleF(iconX, iconY, iconD, iconD));
                    if (off)   /* 离线时压暗 */
                    {
                        using (var dim = new SolidBrush(C(40, 40, 46, 190))) g.FillPath(dim, ip2);
                    }
                    g.Restore(st1);
                    using (var pen = new Pen(C(255, 255, 255, 70), 0.8f)) g.DrawPath(pen, ip2);
                }
            }
            else
            {
                using (var ip = new GraphicsPath())
                {
                    ip.AddEllipse(iconX, iconY, iconD, iconD);
                    if (off)
                    {
                        using (var b = new SolidBrush(C(52, 52, 60, 255))) g.FillPath(b, ip);
                    }
                    else
                    {
                        using (var b = Grad(iconX, iconY, iconX + iconD, iconY + iconD, C(255, 70, 105, 255), C(255, 143, 92, 255)))
                            g.FillPath(b, ip);
                    }
                    using (var pen = new Pen(C(255, 255, 255, 46), 0.7f)) g.DrawPath(pen, ip);
                }
                using (var fNote = new Font("Segoe UI Symbol", iconD * 0.50f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var bNote = new SolidBrush(C(255, 255, 255, 242)))
                using (var sf = Center())
                    g.DrawString("\u266A", fNote, bNote, new RectangleF(iconX, iconY, iconD, iconD), sf);
            }

            // 文字统一用 ClearType（与参数面板/右键菜单一致），边缘更实
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            float cx = iconX + iconD + 9f;

            // 悬停态的曲目名（ClearType，与参数面板/右键菜单一致；超长时可滚动）
            float tvX = 12f, tvY = 10f, tvW = w - 12f - 52f, tvH = 16f;   // 从图标正上方开始，把那块区域也用上
            if (hover)
            {
                TextRenderingHint oldHint = g.TextRenderingHint;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                using (var fT = new Font(TitleFontName, TitleFontPx, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var bT = new SolidBrush(C(232, 234, 242, 255)))
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
                            sf.Trimming = StringTrimming.EllipsisCharacter;
                            g.DrawString(st.Title, fT, bT, viewport, sf);
                        }
                    }
                }
                g.TextRenderingHint = oldHint;
            }

            // 主体文字
            if (mute)
            {
                using (var fM = new Font("Microsoft YaHei UI", 15.5f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var bM = new SolidBrush(C(255, 110, 110, 255)))
                using (var sf = Near())
                    g.DrawString("静音", fM, bM, new RectangleF(cx, numCy - 12f, 100f, 24f), sf);
            }
            else if (off)
            {
                using (var fO = new Font("Microsoft YaHei UI", 13.5f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var bO = new SolidBrush(C(126, 128, 140, 255)))
                using (var sf = Near())
                    g.DrawString("未连接", fO, bO, new RectangleF(cx, numCy - 11f, 100f, 22f), sf);
            }
            else
            {
                int pct = st.Volume;
                if (pct < 0) pct = 0;
                if (pct > 100) pct = 100;
                string num = pct.ToString();

                float numPx = numPt * 1.28f;
                using (var fN = new Font("Segoe UI", numPx, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var bN = new SolidBrush(C(246, 246, 250, 253)))
                using (var sf = Near())
                {
                    g.DrawString(num, fN, bN, new RectangleF(cx, numCy - numPx, 120f, numPx * 2f), sf);
                    SizeF nsz = g.MeasureString(num, fN);
                    using (var fP = new Font("Segoe UI", numPx * 0.40f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var bP = new SolidBrush(C(198, 201, 212, 255)))
                    using (var sf2 = Near())
                    {
                        float pctCy = numCy + numPx * 0.26f;
                        g.DrawString("%", fP, bP, new RectangleF(cx + nsz.Width - 4f, pctCy - numPx * 0.5f, 40f, numPx), sf2);
                    }
                }
            }

            float bx_ = cx + 34f;   // dB 文本的左边界（避开百分比）

            // 当前曲目响度（右侧小字）
            if (!off && !float.IsNaN(st.LoudDb))
            {
                float dbPx = st.Hover ? 13.5f : 12f;
                using (var fDb = new Font("Segoe UI", dbPx, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var bDb = new SolidBrush(C(198, 201, 212, 255)))
                using (var sfDb = new StringFormat())
                {
                    sfDb.Alignment = StringAlignment.Far;
                    sfDb.LineAlignment = StringAlignment.Center;
                    string txt = st.LoudDb.ToString("0.0") + " dB";
                    g.DrawString(txt, fDb, bDb, new RectangleF(bx_, numCy - dbPx, w - bx_ - 12f, dbPx * 2f), sfDb);
                }
            }

            // 进度条
            float bx = cx;
            float bw = w - bx - 11f;
            float bh = 3.6f;
            float by = h - 10f;

            using (var tp = Round(bx, by, bw, bh, bh / 2f))
            using (var tb = new SolidBrush(C(255, 255, 255, 26)))
                g.FillPath(tb, tp);

            float fw = bw * (st.Volume / 100f);
            if (fw > 0.5f && !off)
            {
                if (fw < bh) fw = bh;
                using (var fp = Round(bx, by, fw, bh, bh / 2f))
                {
                    if (mute)
                    {
                        using (var b = new SolidBrush(C(122, 46, 46, 255))) g.FillPath(b, fp);
                    }
                    else
                    {
                        using (var b = Grad(bx, by, bx + bw, by, C(255, 70, 105, 255), C(255, 152, 100, 255)))
                            g.FillPath(b, fp);
                    }
                }
                if (!mute)
                {
                    float kr = 4.2f;
                    using (var kp = new GraphicsPath())
                    {
                        kp.AddEllipse(bx + fw - kr, by + bh / 2f - kr, kr * 2f, kr * 2f);
                        using (var kb = new SolidBrush(C(255, 255, 255, 242))) g.FillPath(kb, kp);
                    }
                }
            }
            // 参数按钮（三条滑杆），只在悬停时出现，位于 × 左侧
            if (st.ShowControls)   // 离线时也要能开菜单 / 退出，否则窗口关不掉
            {
                float gx = w - 40f, gy = 17f;
                int ga = 190;
                using (var pen = new Pen(C(255, 255, 255, ga), 1.1f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, gx - 6f, gy - 4.5f, gx + 6f, gy - 4.5f);
                    g.DrawLine(pen, gx - 6f, gy, gx + 6f, gy);
                    g.DrawLine(pen, gx - 6f, gy + 4.5f, gx + 6f, gy + 4.5f);
                }
                using (var dot = new SolidBrush(C(255, 255, 255, ga)))
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
                using (var pen = new Pen(C(255, 255, 255, alpha), 1.15f))
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
