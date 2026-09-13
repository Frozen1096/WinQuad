// 宫格预览绘制 —— 主界面的「预计效果」和设置界面的「实时预览」共用这一份代码。
//
// 为什么要抽出来：两处必须画得**完全一样**。否则用户在设置里调字号、调格子尺寸，
// 看到的是一种效果，回到主界面又是另一种，只会更困惑。
//
// 原来这套代码写死在 MainForm.CmbPreview 里面，而且 DrawOutlinedText 靠
// `Application.OpenForms[0] as MainForm` 反查字体样式 —— 设置界面既够不着、
// 就算够着了也会串味（拿的是主界面的样式）。现在样式一律当参数传进来。
//
// 绘制规则与覆盖层 GridForm.RenderCells 逐行对应：
// 覆盖层负责桌面上真实的渲染，这里负责预览，两者必须保持一致。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WinQuad.Manager
{
    internal static class PreviewPainter
    {
        /// <summary>标题条高度（"实际 79 × 101　放大 1.5×" 那行）。</summary>
        public const int StripH = 20;

        /// <summary>第 i 格在宫格内的矩形（不含外扩）。</summary>
        public static Rectangle RectOf(SizeSection s, int i)
        {
            int col = i % s.Cols, row = i / s.Cols;
            return new Rectangle(
                s.PadX + col * (s.CellWidth + s.Gap),
                s.PadY + row * (s.CellHeight + s.Gap),
                s.CellWidth, s.CellHeight);
        }

        /// <summary>宫格画面居中铺开时需要的高度（不含 GroupBox 标题与内边距）。</summary>
        public static int ContentHeight(SizeSection s, float scale)
        {
            if (s == null) return 200;
            return StripH + (int)Math.Round(s.Height * scale) + 14;   // 14 = 底部留白
        }

        /// <summary>
        /// 把整个宫格画到目标 Graphics 上：先按 1:1 画进离屏位图，再整体等比放大贴上去。
        ///
        /// 为什么必须这样：直接按放大后的坐标画几何、字号却不跟着放大，比例就会失真
        /// （看着像"一行四个"）。画成位图再整体缩放，图标和文字必然一起缩放。
        /// </summary>
        /// <returns>宫格在画面里实际占用的矩形，调用方可用它判断有没有溢出。</returns>
        public static Rectangle DrawGrid(Graphics gTarget, Size area, SizeSection s, StyleSection st,
                                        List<GroupItem> items, float scale)
        {
            if (s == null || st == null) return Rectangle.Empty;

            using (var bmp = new Bitmap(Math.Max(1, s.Width), Math.Max(1, s.Height)))
            {
                using (var bg = Graphics.FromImage(bmp))
                {
                    bg.Clear(Color.Transparent);
                    DrawCells(bg, s, st, items);
                }

                if (scale <= 0f) scale = 1f;

                int dw = (int)Math.Round(s.Width * scale);
                int dh = (int)Math.Round(s.Height * scale);
                int dx = (area.Width - dw) / 2;
                int dy = StripH + 8;

                gTarget.SmoothingMode = SmoothingMode.None;
                gTarget.InterpolationMode = scale >= 2f
                    ? InterpolationMode.NearestNeighbor
                    : InterpolationMode.HighQualityBicubic;
                gTarget.PixelOffsetMode = PixelOffsetMode.Half;

                // 不画"模拟壁纸"的深色底 —— 那块色块比面板小，四周留出的边看起来
                // 就是"一大片深色空白"。直接用面板自身背景当壁纸，宫格浮在上面，
                // 与桌面上的实际观感也更接近。
                gTarget.DrawImage(bmp, new Rectangle(dx, dy, dw, dh));

                using (var f = new Font("Microsoft YaHei UI", 7.5f))
                using (var b = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
                {
                    gTarget.DrawString("实际 " + s.Width + " × " + s.Height
                                       + "　放大 " + scale.ToString("0.#") + "× 　共 "
                                       + s.Cols + " 列 × " + s.Rows + " 行"
                                       + "　单格 " + s.CellWidth + "×" + s.CellHeight,
                                       f, b, 6, 3);
                }

                return new Rectangle(dx, dy, dw, dh);
            }
        }

        /// <summary>
        /// 逐格绘制。这是覆盖层 GridForm.RenderCells 的**逐行对应移植版**。
        /// 只有"空格子不画图标文字"这一条是本版特有的。
        /// </summary>
        public static void DrawCells(Graphics g, SizeSection s, StyleSection st, List<GroupItem> items)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            for (int i = 0; i < s.Cols * s.Rows; i++)
            {
                if (items == null || i >= items.Count) continue;
                var item = items[i];
                if (item == null) continue;

                var cell = RectOf(s, i);

                Color plate = (item.BgColor != null && item.BgColor.Length >= 3)
                    ? Color.FromArgb(item.BgColor[0], item.BgColor[1], item.BgColor[2])
                    : Safe(st.PlateColor);
                int alpha = item.BgAlpha ?? st.PlateAlpha;

                int rad = 5, d = rad * 2;
                using (var path = new GraphicsPath())
                {
                    path.AddArc(cell.X, cell.Y, d, d, 180, 90);
                    path.AddArc(cell.Right - d, cell.Y, d, d, 270, 90);
                    path.AddArc(cell.Right - d, cell.Bottom - d, d, d, 0, 90);
                    path.AddArc(cell.X, cell.Bottom - d, d, d, 90, 90);
                    path.CloseFigure();

                    using (var br = new LinearGradientBrush(
                        Rectangle.Inflate(cell, 2, 2),
                        Color.FromArgb(Math.Min(255, (int)(alpha * 1.15)), plate),
                        Color.FromArgb(Math.Min(255, (int)(alpha * 0.55)), plate), 90f))
                        g.FillPath(br, path);
                    using (var pen = new Pen(Color.FromArgb(st.BorderAlpha, Safe(st.BorderColor)), 1f))
                        g.DrawPath(pen, path);
                }

                if (string.IsNullOrWhiteSpace(item.Path)) continue;

                int blockH = s.IconSize + 1 + s.LabelHeight;
                int top = cell.Y + Math.Max(0, (cell.Height - blockH) / 2);
                var iconBox = new Rectangle(cell.X + (cell.Width - s.IconSize) / 2, top,
                                            s.IconSize, s.IconSize);
                var labelBox = new Rectangle(cell.X, iconBox.Bottom + 1, cell.Width, s.LabelHeight);

                var ico = Icons.Load(string.IsNullOrWhiteSpace(item.Icon) ? item.Path : item.Icon);
                if (ico != null)
                {
                    TightDrawIcon(g, ico, iconBox);
                    ico.Dispose();
                }

                DrawOutlinedText(g, item.Caption ?? "", labelBox, Safe(st.TextColor), st);
            }
        }

        /// <summary>把图标里真正有内容的部分等比填满目标框（去掉自带透明边距）。</summary>
        static void TightDrawIcon(Graphics g, Icon icon, Rectangle box)
        {
            try
            {
                using (var bmp = icon.ToBitmap())
                {
                    int minX = bmp.Width, minY = bmp.Height, maxX = -1, maxY = -1;
                    for (int y = 0; y < bmp.Height; y++)
                        for (int x = 0; x < bmp.Width; x++)
                        {
                            if (bmp.GetPixel(x, y).A < 16) continue;
                            if (x < minX) minX = x;
                            if (y < minY) minY = y;
                            if (x > maxX) maxX = x;
                            if (y > maxY) maxY = y;
                        }
                    if (maxX < 0) { g.DrawIcon(icon, box); return; }

                    int cw = maxX - minX + 1, ch = maxY - minY + 1;
                    float sc = Math.Min((float)box.Width / cw, (float)box.Height / ch);
                    float dw = cw * sc, dh = ch * sc;
                    g.DrawImage(bmp,
                        new RectangleF(box.X + (box.Width - dw) / 2, box.Y + (box.Height - dh) / 2, dw, dh),
                        new RectangleF(minX, minY, cw, ch), GraphicsUnit.Pixel);
                }
            }
            catch { g.DrawIcon(icon, box); }
        }

        /// <summary>
        /// 与覆盖层 DrawOutlinedText 同一套逻辑：投影与描边可以单独用也可以叠加，
        /// 叠加时按「投影 → 描边 → 正文」的顺序，投影在最底层。
        /// 样式由调用方传入，不反查窗体。
        /// </summary>
        public static void DrawOutlinedText(Graphics g, string text, Rectangle box, Color color, StyleSection st)
        {
            if (string.IsNullOrEmpty(text)) return;

            Font f;
            float pt = st?.FontSizePt ?? 7f;
            try { f = new Font(st?.FontFamily ?? "Microsoft YaHei UI", pt, FontStyle.Regular); }
            catch { f = new Font("Microsoft YaHei UI", pt); }

            const TextFormatFlags flags =
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

            int so = st?.ShadowOffset ?? 0;
            int sa = st?.ShadowAlpha ?? 200;
            int ow = st?.OutlineWidth ?? 1;
            int oa = st?.OutlineAlpha ?? 245;

            // 1) 投影：只在右下垫暗色副本，字的形状完整保留
            if (so > 0 && sa > 0)
            {
                var rgb = (st?.ShadowColor != null && st.ShadowColor.Length >= 3)
                    ? st.ShadowColor : st?.OutlineColor;
                var sc = Color.FromArgb(sa, Safe(rgb));
                for (int i = so; i >= 1; i--)
                    TextRenderer.DrawText(g, text, f,
                        new Rectangle(box.X + i, box.Y + i, box.Width, box.Height), sc, flags);
            }

            // 2) 描边：压在投影上面
            if (ow > 0 && oa > 0)
            {
                var oc = Color.FromArgb(oa, Safe(st.OutlineColor));
                for (int dy = -ow; dy <= ow; dy += ow)
                    for (int dx = -ow; dx <= ow; dx += ow)
                    {
                        if (dx == 0 && dy == 0) continue;
                        TextRenderer.DrawText(g, text, f,
                            new Rectangle(box.X + dx, box.Y + dy, box.Width, box.Height), oc, flags);
                    }
            }

            // 3) 正文
            TextRenderer.DrawText(g, text, f, box, color, flags);
            f.Dispose();
        }

        public static Color Safe(int[] rgb) =>
            rgb != null && rgb.Length >= 3 ? Color.FromArgb(rgb[0], rgb[1], rgb[2]) : Color.White;
    }
}
