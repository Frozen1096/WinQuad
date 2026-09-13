// 参数设置界面。
//
// ── 为什么整个重写 ──
// 上一版打开后几乎是一片空白：每组只剩一个十几像素宽的长条，标题竖着排。
// 原因是**循环 AutoSize**：
//   GroupBox.AutoSize 要问里面的 TableLayoutPanel「你想多宽」，
//   而那个 TLP 有一列是 Percent（百分比），百分比列在没有确定总宽时算不出首选宽度，
//   于是 TLP 报回来的宽度约等于 0 → GroupBox 缩成一条 → TLP 真的变成 0 宽 → 更算不出来。
// 再叠加 FlowLayoutPanel(Dock=Top, AutoSize) 也来凑宽度，整块就塌了。
//
// ── 现在的做法 ──
// 一条铁律：**凡是含百分比列的容器，宽度必须由外面显式给死，绝不 AutoSize。**
//   * 组高在构造时就算死（标题 + 各行高之和 + 内边距），之后改宽度不会改高度，不存在循环；
//   * 组宽、TLP 宽统一由 LayoutAll() 按窗口宽度算出来再赋进去；
//   * 没有任何一个容器依赖 AutoSize 去推宽度。
// 这套「显式像素定位」和主界面第二列用的是同一种办法，那边已经被验证是可靠的。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;

namespace WinQuad.Manager
{
    internal sealed class SettingsForm : Form
    {
        #region 布局常量

        const int M = 12;              // 内容四周外边距
        const int GapY = 10;           // 组与组之间的间距
        const int TitleH = 20;         // GroupBox 标题占用高度
        const int PadBot = 8;          // GroupBox 底部内边距
        const int RowH = 30;           // 普通行高
        const int NoteH = 34;          // 说明行高
        const int LblW = 152;          // 标签列宽
        const int NumW = 76;           // 数字框宽度
        const int GroupInner = 24;     // GroupBox 左右内边距合计
        const int ScrollReserve = 18;  // 恒定给竖向滚动条留位，否则出/收滚动条会让宽度来回抖
        const float Scale = 2.0f;      // 预览放大倍数（比主界面的 1.5 大，设置界面以看清为主）

        #endregion

        readonly AppConfig _target;    // 主界面持有的那个对象，点「确定」才写回
        readonly AppConfig _cfg;       // 编辑副本
        readonly List<GroupBox> _stack = new List<GroupBox>();
        readonly List<GroupMeta> _meta = new List<GroupMeta>();

        /// <summary>每个组的行高基准 + 哪些行是「说明行」。</summary>
        sealed class GroupMeta
        {
            public GroupBox Box;
            public TableLayoutPanel Table;
            public int[] RowH;
            public readonly Dictionary<int, Label> Notes = new Dictionary<int, Label>();
        }

        Panel _scroll, _body;
        GroupBox _previewGroup;
        PreviewBox _preview;
        Label _lblCheck;
        List<GroupItem> _items;

        int _lastSig = -1;
        bool _laying;
        bool _loading = true;

        public SettingsForm(AppConfig cfg)
        {
            _target = cfg;
            _cfg = Clone(cfg);

            Text = "WinQuad 参数设置";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(700, 760);
            MinimumSize = new Size(620, 460);
            Font = new Font("Microsoft YaHei UI", 9f);
            DoubleBuffered = true;      // 缩放着色不留残影
            try { Icon = Icon.ExtractAssociatedIcon(System.IO.Path.Combine(Cfg.AppDir, "WinQuad.exe")); } catch { }

            _items = LoadPreviewItems();

            BuildUi();
            _loading = false;
            RefreshSummary();
        }

        /// <summary>
        /// 深拷贝一份来改。直接改原对象的话，点「取消」根本退不回去（上一版就是这个毛病）。
        /// </summary>
        static AppConfig Clone(AppConfig src)
        {
            if (src == null) return new AppConfig();
            try { return JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(src, Cfg.Json), Cfg.Json); }
            catch (Exception ex) { Cfg.Log("设置界面克隆配置失败: " + ex.Message); return new AppConfig(); }
        }

        /// <summary>预览用真实内容才有意义 —— 取第一个四宫格里的程序。</summary>
        List<GroupItem> LoadPreviewItems()
        {
            try
            {
                if (_cfg.Groups != null && _cfg.Groups.Count > 0)
                {
                    var g = Cfg.LoadGroup(_cfg.Groups[0].GroupFile);
                    if (g != null) return g.Items;
                }
            }
            catch { }
            return new List<GroupItem>();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 只有点「确定」才把改动写回主界面持有的对象
            if (DialogResult == DialogResult.OK && _target != null && _cfg != null)
            {
                _target.Size = _cfg.Size;
                _target.Style = _cfg.Style;
                _target.Behaviour = _cfg.Behaviour;
                Cfg.Log("[设置] 已应用：宫格 " + _cfg.Size.Width + "×" + _cfg.Size.Height
                        + " 字体 " + _cfg.Style.FontFamily + " " + _cfg.Style.FontSizePt + "pt");
            }
            base.OnFormClosing(e);
        }

        #region 界面搭建

        void BuildUi()
        {
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 56 };
            Controls.Add(bar);

            var hint = new Label
            {
                Text = "改完点「确定」，再回主界面点「保存并重启」才会让桌面生效。",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.DimGray,
                Padding = new Padding(16, 0, 0, 0)
            };
            bar.Controls.Add(hint);

            var btns = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                WrapContents = false,
                Padding = new Padding(0, 12, 16, 0)
            };
            bar.Controls.Add(btns);
            var ok = new Button { Text = "确定", Width = 92, Height = 30, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "取消", Width = 92, Height = 30, DialogResult = DialogResult.Cancel };
            cancel.Margin = new Padding(8, 0, 0, 0);
            btns.Controls.Add(ok);
            btns.Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;

            _scroll = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true };
            Controls.Add(_scroll);
            _scroll.BringToFront();

            _body = new Panel { Location = new Point(0, 0) };
            _scroll.Controls.Add(_body);

            BuildPreviewGroup();
            BuildSizeGroup();
            BuildStyleGroup();
            BuildFontGroup();
            BuildRunGroup();
            BuildAvoidGroup();
            BuildFootNote();

            foreach (var gb in _stack) _body.Controls.Add(gb);

            Resize += (s, e) => LayoutAll();
            Shown += (s, e) =>
            {
                _lastSig = -1;
                LayoutAll();
                // 一律从顶部开始看。不强制复位的话，窗体显示时会把第一个可获焦点的
                // 控件滚进可视区，结果一打开就停在中间某处，像是"上面缺了一块"。
                _scroll.AutoScrollPosition = new Point(0, 0);
            };
        }

        /// <summary>
        /// 建一个组。各行的行高在这里就给死 —— 组高因此是个定值，
        /// 后面 LayoutAll 只改宽度不改高度，不会形成"改宽度→高度变→再改宽度"的循环。
        /// </summary>
        GroupBox MakeGroup(string title, params int[] rowHeights)
        {
            int contentH = 0;
            foreach (int h in rowHeights) contentH += h;

            var t = new TableLayoutPanel
            {
                ColumnCount = 2,
                RowCount = rowHeights.Length,
                Location = new Point(12, TitleH),
                Size = new Size(400, contentH),   // 宽度是占位，LayoutAll 会按窗口重算
                Margin = new Padding(0)
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LblW));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < rowHeights.Length; i++)
                t.RowStyles.Add(new RowStyle(SizeType.Absolute, rowHeights[i]));

            var gb = new GroupBox
            {
                Text = title,
                Width = 400,
                Height = TitleH + contentH + PadBot,
                Padding = new Padding(0)
            };
            gb.Controls.Add(t);
            gb.Tag = t;
            _stack.Add(gb);
            _meta.Add(new GroupMeta { Box = gb, Table = t, RowH = (int[])rowHeights.Clone() });
            return gb;
        }

        /// <summary>
        /// 开关双缓冲的容器。AutoScroll 面板在缩放后会留下上一帧的残影
        /// （表现为同一组在屏幕上出现两次），开双缓冲 + 布局后整块重画即可。
        /// </summary>
        sealed class BufferedPanel : Panel
        {
            public BufferedPanel() { DoubleBuffered = true; }
        }

        static TableLayoutPanel T(GroupBox gb) => (TableLayoutPanel)gb.Tag;

        /// <summary>标签 + 控件（控件宽度自持，靠左、垂直居中）。</summary>
        void Row(GroupBox gb, int r, string label, Control c)
        {
            var t = T(gb);
            t.Controls.Add(new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0)
            }, 0, r);

            c.Margin = new Padding(0);
            t.Controls.Add(LeftAligned(c), 1, r);
        }

        /// <summary>标签 + 会自己铺满整行的控件（复选框、说明文字）。</summary>
        void RowFill(GroupBox gb, int r, string label, Control c)
        {
            var t = T(gb);
            if (label != null)
                t.Controls.Add(new Label
                {
                    Text = label,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Margin = new Padding(0)
                }, 0, r);

            c.Dock = DockStyle.Fill;
            c.Margin = new Padding(0);
            t.Controls.Add(c, 1, r);

            // 说明行：文字会随窗口宽度换行，行高必须按**当前实际宽度**量出来。
            // 用固定行高的话，窄窗口下说明折成两行就会溢出到下一组上面去。
            var note = c as Label;
            if (label == null && note != null)
                foreach (var m in _meta)
                    if (m.Box == gb) m.Notes[r] = note;
        }

        /// <summary>
        /// 固定宽度、靠左、垂直居中地塞进单元格。
        /// 不能直接给 Anchor=None —— 那是"居中"，76px 的数字框会飘到几百像素外，离标签很远。
        /// </summary>
        static Panel LeftAligned(Control c)
        {
            var p = new Panel { Margin = new Padding(0), Dock = DockStyle.Fill };
            c.Left = 0;
            p.Controls.Add(c);
            p.Layout += (s, e) => c.Top = Math.Max(0, (p.Height - c.Height) / 2);
            return p;
        }

        /// <summary>「A × B」两个数字框并排。</summary>
        static Panel Pair(Control a, Control b, int wa = NumW, int wb = NumW)
        {
            var p = new Panel { Margin = new Padding(0), Dock = DockStyle.Fill };
            var sep = new Label
            {
                Text = "×",
                Left = wa,
                Width = 22,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.DimGray
            };
            a.Left = 0;
            b.Left = wa + 22;
            p.Controls.Add(a);
            p.Controls.Add(sep);
            p.Controls.Add(b);
            p.Layout += (s, e) =>
            {
                a.Top = Math.Max(0, (p.Height - a.Height) / 2);
                b.Top = Math.Max(0, (p.Height - b.Height) / 2);
                sep.Top = Math.Max(0, (p.Height - sep.Height) / 2);
            };
            return p;
        }

        static Label NoteLabel(string text) => new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
            Font = new Font("Microsoft YaHei UI", 8f),
            Margin = new Padding(0)
        };

        #endregion

        #region 控件工厂

        NumericUpDown Num(int min, int max, int val, Action<int> set, bool affectsLayout, int step = 1)
        {
            var n = new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                Value = Math.Max(min, Math.Min(max, val)),
                Increment = step,
                Width = NumW
            };
            n.ValueChanged += (s, e) =>
            {
                if (_loading) return;
                set((int)n.Value);
                if (affectsLayout) TouchLayout(); else Touch();
            };
            return n;
        }

        CheckBox Chk(string text, bool val, Action<bool> set)
        {
            var c = new CheckBox { Text = text, Checked = val, Height = RowH - 6, AutoSize = false };
            c.CheckedChanged += (s, e) => { if (_loading) return; set(c.Checked); Touch(); };
            return c;
        }

        Panel ColorRow(int[] rgb, Action<int[]> set)
        {
            var cur = PreviewPainter.Safe(rgb);
            var p = new Panel { Margin = new Padding(0), Width = 300, Height = 24 };
            var sw = new Panel
            {
                Left = 0,
                Top = 1,
                Width = 46,
                Height = 22,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = cur
            };
            var hex = new Label
            {
                Left = 54,
                Top = 1,
                Width = 92,
                Height = 22,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.DimGray,
                Text = Hex(cur)
            };
            var b = new Button { Left = 152, Top = 0, Width = 92, Height = 24, Text = "选择颜色…" };
            b.Click += (s, e) =>
            {
                using (var d = new ColorDialog { FullOpen = true, AnyColor = true, Color = sw.BackColor })
                    if (d.ShowDialog(this) == DialogResult.OK)
                    {
                        sw.BackColor = d.Color;
                        hex.Text = Hex(d.Color);
                        set(new[] { (int)d.Color.R, (int)d.Color.G, (int)d.Color.B });
                        Touch();
                    }
            };
            p.Controls.Add(sw);
            p.Controls.Add(hex);
            p.Controls.Add(b);
            return p;
        }

        static string Hex(Color c) => "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");

        #endregion

        #region 各组

        void BuildPreviewGroup()
        {
            _preview = new PreviewBox(this);
            var gb = new GroupBox { Text = "实时预览", Width = 400, Padding = new Padding(0) };
            _preview.Location = new Point(12, TitleH);
            gb.Controls.Add(_preview);
            gb.Tag = _preview;
            _previewGroup = gb;
            _stack.Add(gb);
        }

        void BuildSizeGroup()
        {
            var s = _cfg.Size;
            var b = _cfg.Behaviour;

            var gb = MakeGroup("尺寸与图标网格",
                RowH, RowH, RowH, RowH, RowH, RowH, RowH, RowH,   // 列行 / 单格 / 外边距 / 缝隙 / 图标 / 文字 / 环 / 外扩
                RowH, RowH,                                        // 判定框 / 图标网格间距
                34, NoteH);                                        // 探测按钮 / 检查结果

            Row(gb, 0, "列数 × 行数",
                Pair(Num(1, 8, s.Cols, v => s.Cols = v, true),
                     Num(1, 8, s.Rows, v => s.Rows = v, true)));
            Row(gb, 1, "单格宽 × 高",
                Pair(Num(10, 200, s.CellWidth, v => s.CellWidth = v, true),
                     Num(10, 200, s.CellHeight, v => s.CellHeight = v, true)));
            Row(gb, 2, "外边距 左右 × 上下",
                Pair(Num(0, 40, s.PadX, v => s.PadX = v, true),
                     Num(0, 40, s.PadY, v => s.PadY = v, true)));
            Row(gb, 3, "格子之间的缝隙", Num(0, 40, s.Gap, v => s.Gap = v, true));
            Row(gb, 4, "图标边长", Num(4, 128, s.IconSize, v => s.IconSize = v, true));
            Row(gb, 5, "文字区高度", Num(0, 100, s.LabelHeight, v => s.LabelHeight = v, true));
            Row(gb, 6, "拖动环厚度", Num(1, 20, s.RingSize, v => s.RingSize = v, true));
            Row(gb, 7, "区域外扩", Num(0, 10, s.Bleed, v => s.Bleed = v, true));

            Row(gb, 8, "判定框 宽 × 高",
                Pair(Num(4, 200, b.CollisionW, v => b.CollisionW = v, true),
                     Num(4, 200, b.CollisionH, v => b.CollisionH = v, true)));

            Row(gb, 9, "图标网格间距 横 × 纵",
                Pair(Num(20, 400, b.GridStepX, v => b.GridStepX = v, true),
                     Num(20, 400, b.GridStepY, v => b.GridStepY = v, true)));

            var nsX = FindNum(gb, 9, 0);
            var nsY = FindNum(gb, 9, 1);

            var det = new Panel { Margin = new Padding(0), Dock = DockStyle.Fill };
            var btnDetect = new Button { Left = 0, Width = 168, Height = 26, Text = "读取当前图标间距" };
            btnDetect.Click += (s2, e2) => DetectSpacing(nsX, nsY);
            var btnReset = new Button { Left = 176, Width = 168, Height = 26, Text = "恢复常用值 83 × 114" };
            btnReset.Click += (s2, e2) =>
            {
                nsX.Value = 83; nsY.Value = 114;
                b.GridStepX = 83; b.GridStepY = 114;
                Touch();
            };
            det.Controls.Add(btnDetect);
            det.Controls.Add(btnReset);
            det.Layout += (s2, e2) => { btnDetect.Top = btnReset.Top = Math.Max(0, (det.Height - 26) / 2); };
            RowFill(gb, 10, "图标间距", det);

            _lblCheck = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            RowFill(gb, 11, "检查", _lblCheck);
        }

        /// <summary>从某个「A × B」行里把第 idx 个数字框捞出来（探测按钮要往里面写值）。</summary>
        static NumericUpDown FindNum(GroupBox gb, int row, int idx)
        {
            var cell = T(gb).GetControlFromPosition(1, row) as Panel;
            if (cell == null) return null;
            int seen = 0;
            foreach (Control c in cell.Controls)
            {
                var n = c as NumericUpDown;
                if (n == null) continue;
                if (seen++ == idx) return n;
            }
            return null;
        }

        void BuildStyleGroup()
        {
            var st = _cfg.Style;
            var gb = MakeGroup("外观",
                RowH, RowH, RowH, RowH, RowH, RowH, RowH, RowH, RowH, RowH, NoteH);

            Row(gb, 0, "底板颜色", ColorRow(st.PlateColor, v => st.PlateColor = v));
            Row(gb, 1, "底板透明度", Num(0, 255, st.PlateAlpha, v => st.PlateAlpha = v, false, 5));
            Row(gb, 2, "悬停时底板透明度", Num(0, 255, st.PlateAlphaHover, v => st.PlateAlphaHover = v, false, 5));
            Row(gb, 3, "底板边框颜色", ColorRow(st.BorderColor, v => st.BorderColor = v));
            Row(gb, 4, "边框透明度", Num(0, 255, st.BorderAlpha, v => st.BorderAlpha = v, false, 5));
            Row(gb, 5, "文字颜色", ColorRow(st.TextColor, v => st.TextColor = v));
            Row(gb, 6, "悬停时文字颜色", ColorRow(st.TextColorHover, v => st.TextColorHover = v));
            Row(gb, 7, "描边颜色", ColorRow(st.OutlineColor, v => st.OutlineColor = v));
            Row(gb, 8, "描边透明度", Num(0, 255, st.OutlineAlpha, v => st.OutlineAlpha = v, false, 5));
            Row(gb, 9, "描边宽度", Num(0, 3, st.OutlineWidth, v => st.OutlineWidth = v, false));
            RowFill(gb, 10, null, NoteLabel("文字看不清时：先把「底板透明度」提到 80~110（最有效），再把「描边透明度」拉到 255。"));
        }

        void BuildFontGroup()
        {
            var st = _cfg.Style;
            var gb = MakeGroup("字体", RowH, RowH, RowH, NoteH);

            var cmb = new ComboBox { Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
            cmb.Items.AddRange(new object[]
                { "Microsoft YaHei UI", "Microsoft YaHei", "SimSun", "SimHei", "Segoe UI", "Arial" });
            if (!string.IsNullOrEmpty(st.FontFamily) && !cmb.Items.Contains(st.FontFamily))
                cmb.Items.Add(st.FontFamily);
            cmb.SelectedItem = st.FontFamily;
            cmb.SelectedIndexChanged += (s, e) =>
            {
                if (_loading || cmb.SelectedItem == null) return;
                st.FontFamily = cmb.SelectedItem.ToString();
                Touch();
            };
            Row(gb, 0, "字体", cmb);

            Row(gb, 1, "字号（磅）", Num(5, 24, (int)Math.Round(st.FontSizePt), v => st.FontSizePt = v, false));

            var bold = Chk("加粗（会明显变宽，三字名可能就放不下了）", st.FontBold, v => st.FontBold = v);
            bold.Width = 420;
            RowFill(gb, 2, "加粗", bold);

            RowFill(gb, 3, null, NoteLabel("实测：7pt 微软雅黑下三个汉字约 37px，正好等于 37px 的单格宽；8pt 需要 40px，就只能显示两个字了。"));
        }

        void BuildRunGroup()
        {
            var b = _cfg.Behaviour;
            var gb = MakeGroup("启动与交互", RowH, RowH, RowH, RowH, NoteH);

            var c1 = Chk("随 Windows 启动（写 HKCU 的 Run 项，不需要管理员权限）", b.StartWithWindows, v => b.StartWithWindows = v);
            RowFill(gb, 0, "开机自动启动", c1);

            var c2 = Chk("双击格子启动程序；取消勾选 = 单击就启动", b.DoubleClickToLaunch, v => b.DoubleClickToLaunch = v);
            RowFill(gb, 1, "启动方式", c2);

            var c3 = Chk("鼠标悬停时弹出气泡，显示完整名称与程序路径", b.ShowTooltip, v => b.ShowTooltip = v);
            RowFill(gb, 2, "名称气泡", c3);

            var c4 = Chk("拖动松手时自动吸附到桌面图标网格", b.SnapToIconGrid, v => b.SnapToIconGrid = v);
            RowFill(gb, 3, "网格吸附", c4);

            RowFill(gb, 4, null, NoteLabel("拖动把手是整块四宫格最外圈的细环，内部是启动区，两者不重叠，所以拖的时候不会误启动。"));
        }

        void BuildAvoidGroup()
        {
            var b = _cfg.Behaviour;
            var gb = MakeGroup("避让桌面图标", RowH, RowH, NoteH);

            var chk = Chk("目标格位被别的图标占用时，自动挪到最近的空格位", b.AvoidDesktopIcons, v => b.AvoidDesktopIcons = v);
            RowFill(gb, 0, "启用避让", chk);

            var num = Num(0, 20, b.MaxNudgeDistance / 83, v => b.MaxNudgeDistance = v * 83, false);
            Row(gb, 1, "最多挪动（格位）", num);

            var lbl = NoteLabel("");
            RowFill(gb, 2, null, lbl);

            Action refresh = () =>
            {
                num.Enabled = chk.Checked;
                lbl.Text = !chk.Checked
                    ? "不避让：你把它拖到哪，它就待在哪，压住图标也不管。"
                    : (num.Value == 0
                        ? "只能在当前格位；被占用就保持不动并警告。"
                        : "最多挪 " + num.Value + " 个格位（约 " + b.MaxNudgeDistance + "px），优先沿同一行/列滑动。");
            };
            chk.CheckedChanged += (s, e) => refresh();
            num.ValueChanged += (s, e) => refresh();
            refresh();
        }

        void BuildFootNote()
        {
            var gb = MakeGroup("怎么让改动生效", NoteH, NoteH);

            RowFill(gb, 0, null, NoteLabel("① 这里点「确定」→ ② 回主界面点「保存并重启」。管理器不会自己同步，也不会偷偷改 json。"));
            RowFill(gb, 1, null, NoteLabel("③ 位置是按图标格位存的（列/行），换分辨率也不会跑偏。"));
        }

        #endregion

        #region 布局与刷新

        /// <summary>
        /// 按窗口宽度重算所有组的位置和宽度。
        ///
        /// 只改宽度、不改高度（每个组的高度在 MakeGroup 时就已经定死了），
        /// 所以这里不会触发"改尺寸→又回调 LayoutAll"的循环。
        /// 仍然加 _laying 兜底，并用 _lastSig 去重，避免每次重绘都白算一遍。
        /// </summary>
        void LayoutAll()
        {
            if (_laying || _body == null || _scroll == null) return;
            _laying = true;
            try
            {
                int w = Math.Max(360, _scroll.ClientSize.Width - ScrollReserve - M * 2);
                int inner = w - GroupInner;

                // 先把宽度全部赋到位，再算高度。
                // 顺序反了的话预览控件会停留在上一次的宽度上 —— 首次就是构造时的 400，
                // 表现为窗口明明很宽，预览却挤在左边一小块里，右边留一大片空白。
                foreach (var gb in _stack)
                {
                    gb.Width = w;
                    var t = gb.Tag as TableLayoutPanel;
                    if (t != null) t.Width = inner;
                }

                // 按当前宽度把说明行的高度量出来，反过来决定组高。
                // 这一步只依赖宽度，不依赖任何高度，所以不会形成循环。
                ResizeNoteRows(inner - LblW);
                SyncPreviewHeight();

                int hSum = 0;
                foreach (var gb in _stack) hSum += gb.Height;
                int sig = w * 100000 + hSum;
                if (sig == _lastSig) return;
                _lastSig = sig;

                int y = M;
                foreach (var gb in _stack)
                {
                    gb.Location = new Point(M, y);
                    y += gb.Height + GapY;
                }
                _body.Size = new Size(w + M * 2, y + M);

                // 整块重画：不然缩放后下方会留下上一帧的残影（同一组画两遍）
                _scroll.Invalidate(true);
            }
            finally { _laying = false; }
        }

        /// <summary>
        /// 说明行按**实际可用宽度**量高度：文字折成几行就给它几行的高度。
        /// 上一版所有行高都写死，窄窗口下说明折行后直接糊到下一组上面。
        /// </summary>
        void ResizeNoteRows(int noteW)
        {
            noteW = Math.Max(80, noteW);
            foreach (var m in _meta)
            {
                int total = 0;
                for (int i = 0; i < m.RowH.Length; i++)
                {
                    int h = m.RowH[i];

                    Label note;
                    if (m.Notes.TryGetValue(i, out note) && !string.IsNullOrEmpty(note.Text))
                    {
                        int need = TextRenderer.MeasureText(
                            note.Text, note.Font,
                            new Size(noteW, int.MaxValue),
                            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + 10;
                        if (need > h) h = need;
                    }

                    m.Table.RowStyles[i].Height = h;
                    total += h;
                }
                m.Table.Height = total;
                m.Box.Height = TitleH + total + PadBot;
            }
        }

        /// <summary>预览高度跟着宫格尺寸走 —— 用户把行数或单格高调大，预览块也要变高。</summary>
        void SyncPreviewHeight()
        {
            if (_preview == null || _previewGroup == null) return;
            int h = PreviewPainter.ContentHeight(_cfg.Size, Scale);
            _preview.Width = Math.Max(120, _previewGroup.Width - GroupInner);
            _preview.Height = h;
            _previewGroup.Height = TitleH + h + PadBot;
        }

        /// <summary>只影响外观的参数：重画预览 + 更新检查结果。</summary>
        void Touch()
        {
            _preview?.Invalidate();
            RefreshSummary();
        }

        /// <summary>会改变尺寸的参数：先重算预览块高度，再整体重新布局。</summary>
        void TouchLayout()
        {
            SyncPreviewHeight();
            _lastSig = -1;
            LayoutAll();
            Touch();
        }

        void RefreshSummary()
        {
            if (_lblCheck == null) return;
            var s = _cfg.Size;
            var b = _cfg.Behaviour;
            var msgs = new List<string>();

            if (s.FootprintWidth > b.GridStepX)
                msgs.Add("判定框宽 " + s.FootprintWidth + " > 图标横向间距 " + b.GridStepX + "，会拖不进图标之间");
            if (s.FootprintHeight > b.GridStepY)
                msgs.Add("判定框高 " + s.FootprintHeight + " > 图标纵向间距 " + b.GridStepY + "，会压住相邻行的图标");
            if (s.IconSize + 1 + s.LabelHeight > s.CellHeight)
                msgs.Add("图标 " + s.IconSize + " + 1 + 文字 " + s.LabelHeight + " > 单格高 " + s.CellHeight + "，文字会被裁掉");
            if (b.CollisionW > b.GridStepX)
                msgs.Add("判定框宽 " + b.CollisionW + " > 图标横向间距 " + b.GridStepX);
            if (b.CollisionH > b.GridStepY)
                msgs.Add("判定框高 " + b.CollisionH + " > 图标纵向间距 " + b.GridStepY);

            if (msgs.Count == 0)
            {
                _lblCheck.Text = "✓ 通过　宫格 " + s.Width + "×" + s.Height
                               + "，判定框 " + s.FootprintWidth + "×" + s.FootprintHeight + "，可以正常拖放。";
                _lblCheck.ForeColor = Color.SeaGreen;
            }
            else
            {
                _lblCheck.Text = "⚠ " + string.Join("；", msgs);
                _lblCheck.ForeColor = Color.FromArgb(200, 60, 0);
            }
        }

        #endregion

        #region 预览控件

        /// <summary>
        /// 预览面板。绘制全部交给 PreviewPainter —— 和主界面「预计效果」是同一份代码，
        /// 所以设置里看到什么样，主界面和桌面上就是什么样。
        /// </summary>
        sealed class PreviewBox : Control
        {
            readonly SettingsForm _owner;

            public PreviewBox(SettingsForm owner)
            {
                _owner = owner;
                DoubleBuffered = true;
                BackColor = Color.FromArgb(70, 80, 100);
                SetStyle(ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var cfg = _owner._cfg;
                if (cfg == null) return;
                // 写成 SettingsForm.Scale：Control 自己有个 Scale() 方法，不限定会被它遮蔽
                PreviewPainter.DrawGrid(e.Graphics, ClientSize, cfg.Size, cfg.Style,
                                        _owner._items, SettingsForm.Scale);
            }
        }

        #endregion

        #region 图标间距探测

        string _regHint = "";

        /// <summary>
        /// 刷新图标网格间距：优先用覆盖层 --detect 实测出来的结果（最准），
        /// 读不到才退回注册表换算（本机实测注册表纵向值不准，只能兜底）。
        /// </summary>
        void DetectSpacing(NumericUpDown nsX, NumericUpDown nsY)
        {
            if (nsX == null || nsY == null) return;

            string probe = System.IO.Path.Combine(Cfg.AppDir, "spacing-detect.json");
            try { if (System.IO.File.Exists(probe)) System.IO.File.Delete(probe); } catch { }

            try
            {
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = System.IO.Path.Combine(Cfg.AppDir, "WinQuad.exe"),
                    Arguments = "--detect",
                    WorkingDirectory = Cfg.AppDir,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                p?.WaitForExit(8000);
            }
            catch (Exception ex) { Cfg.Log("调用探测器失败: " + ex.Message); }
            System.Threading.Thread.Sleep(300);

            int x = -1, y = -1;
            try
            {
                if (System.IO.File.Exists(probe))
                {
                    var txt = System.IO.File.ReadAllText(probe);
                    var m1 = System.Text.RegularExpressions.Regex.Match(txt, "\"stepX\"\\s*:\\s*(\\d+)");
                    var m2 = System.Text.RegularExpressions.Regex.Match(txt, "\"stepY\"\\s*:\\s*(\\d+)");
                    if (m1.Success) x = int.Parse(m1.Groups[1].Value);
                    if (m2.Success) y = int.Parse(m2.Groups[1].Value);
                    var m3 = System.Text.RegularExpressions.Regex.Match(txt, "\"source\"\\s*:\\s*\"([^\"]*)\"");
                    if (m3.Success) _regHint = "（来源：" + m3.Groups[1].Value + "）";
                }
            }
            catch (Exception ex) { Cfg.Log("读探测结果失败: " + ex.Message); }

            if (x <= 0 || y <= 0)
            {
                x = ReadRegPx("IconSpacing");
                y = ReadRegPx("IconVerticalSpacing");
                _regHint = "（来源：注册表换算，纵向值常常不准，建议以实测为准）";
            }

            if (x > 0) nsX.Value = Math.Max(nsX.Minimum, Math.Min(nsX.Maximum, x));
            if (y > 0) nsY.Value = Math.Max(nsY.Minimum, Math.Min(nsY.Maximum, y));
            Touch();

            MessageBox.Show(this,
                "图标网格间距已刷新为 " + nsX.Value + " × " + nsY.Value + " px\n\n" + _regHint
                + "\n\n点「确定」后，回主界面再点「保存并重启」才会生效。",
                "WinQuad 参数设置");
        }

        static int ReadRegPx(string name)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                           @"Control Panel\Desktop\WindowMetrics"))
                {
                    var v = k?.GetValue(name);
                    if (v == null) return -1;
                    return Math.Abs(int.Parse(v.ToString())) / 15;
                }
            }
            catch { return -1; }
        }

        #endregion
    }
}
