// 管理器主界面。三列布局：四宫格列表 | 格子内容 | 程序与外观 + 位置 + 预览
//
// 职责边界（按用户要求）：只负责给参数一个图形界面，不做自动同步、不轮询。
// 只有点「保存并重启」时才写 json 并重启覆盖层进程。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace WinQuad.Manager
{
    internal sealed class MainForm : Form
    {
        const string OverlayExe = "WinQuad.exe";
        const string OverlayProcName = "WinQuad";

        /// <summary>图标网格间距（可从配置覆盖）。UI 与坐标换算都走这两个字段。</summary>
        int IconStepX = 83, IconStepY = 114;

        AppConfig _cfg;
        readonly List<GroupFile> _groups = new List<GroupFile>();
        bool _loading;

        /// <summary>用户在界面上手动改过位置的那个宫格。只有它才允许用界面值覆盖磁盘值。</summary>
        GroupFile _positionTouched;

        // 第一列
        ListBox _groupList;
        Button _btnNewGroup, _btnRenameGroup, _btnDelGroup;

        // 第二列
        ListBox _itemList;
        Button _btnAddItem, _btnDelItem;

        // 第三列
        TextBox _txtCaption, _txtPath, _txtIcon;
        Button _btnBrowsePath, _btnBrowseIcon;
        Panel _bgSwatch;
        Button _btnBgColor, _btnBgClear;
        TrackBar _bgAlpha;
        Label _lblBgAlpha;

        // 「跟随总配置」还是「本格单独设置」。
        // true 时上面那几个控件改的是 _cfg.Style 里的默认值，false 时改的是当前格。
        bool _bgColorFollow = true;
        bool _bgAlphaFollow = true;
        Label _lblBgMode;

        // 位置只由手动拖动决定，没有锚点下拉框了
        NumericUpDown _numCol, _numRow;

        // 这个宫格自己的占地（占几个桌面图标格，null = 自动）和行列数（null = 跟随总配置）
        NumericUpDown _numFpCols, _numFpRows, _numCols, _numRows;
        Button _btnFpFollow, _btnShapeFollow;
        Label _lblShapeInfo;
        Label _lblPosInfo;
        CmbPreview _preview;

        // 底部
        Button _btnSave, _btnReload, _btnRestart, _btnOpenFolder, _btnSettings;
        Label _lblStatus;
        CheckBox _chkAutoStart;
        Label _lblRunState;
        FlowLayoutPanel _bottomButtons, _bottomRun;   // ApplyMinSize 要拿它们的实际宽度算最小窗口
        ToolTip _tip;

        public MainForm()
        {
            // 标题里带上中文说明：exe 名是英文的 WinQuad，光看这个名字看不出是干什么的，
            // 任务栏和窗口标题是用户最常看到的地方，在这里点明用途。
            Text = "WinQuad 管理器 — Windows 桌面四宫格";
            StartPosition = FormStartPosition.CenterScreen;

            // 最小尺寸在 LoadAll() 里按配置算出来（见 ApplyMinSize）—— 由程序算，不靠手工估。
            // 这里先给占位值，避免构造期间尺寸为 0。
            MinimumSize = new Size(920, 560);
            Size = new Size(1010, 620);
            Font = new Font("Microsoft YaHei UI", 9f);
            try { Icon = Icon.ExtractAssociatedIcon(Path.Combine(Cfg.AppDir, OverlayExe)); } catch { }

            BuildUi();
            LoadAll();
            _posTimer.Tick += (s, e) => ReloadPositionsFromDisk();
        }

        #region 界面搭建

        void BuildUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                Padding = new Padding(10, 10, 10, 0)
            };
            // 三列：
            //   1) 宫格列表 —— 放得下名字即可
            //   2) 格子内容 + 投放区 + 预计效果（预览放这里）
            //   3) 程序与外观 + 位置
            // 第二列收窄到 316：程序列表本来会截断长路径（有悬停提示），
            // 而收窄省下的宽度正好让整体更紧凑。
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 154));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 316));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(root);

            root.Controls.Add(BuildColumn1(), 0, 0);
            root.Controls.Add(BuildColumn2(), 1, 0);
            root.Controls.Add(BuildColumn3(), 2, 0);

            BuildBottomBar();
        }

        static GroupBox Box(string title) => new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 6, 8, 8),
            Margin = new Padding(0, 0, 8, 0)
        };

        // ─────────── 第一列：四宫格列表 ───────────

        Control BuildColumn1()
        {
            // 用外层 Panel + 内层 GroupBox（固定高度）包住，
            // 让"列表 + 按钮"紧挨在一起，而不是各自贴到整列的顶部和底部。
            // 之前按钮 Dock=Bottom 贴的是整列底部，与列表之间隔着一大片空白。
            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0) };

            const int listH = 168;      // 列表高度（约 6 个宫格）
            const int barH = 108;       // 按钮区：40(新建) + 6间距 + 30(改名/删除) + 上下留白
            int gbH = 22 + listH + barH + 14;

            var gb = new GroupBox
            {
                Text = "四宫格",
                Dock = DockStyle.Top,
                Height = gbH,
                Padding = new Padding(8, 4, 8, 6),
                Margin = new Padding(0, 0, 6, 0)
            };

            _groupList = new ListBox
            {
                Dock = DockStyle.Top,
                Height = listH,
                IntegralHeight = false
            };
            _groupList.SelectedIndexChanged += (s, e) => OnGroupSelected();
            gb.Controls.Add(_groupList);

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = barH,
                WrapContents = true,
                Padding = new Padding(0, 4, 0, 2)
            };
            gb.Controls.Add(bar);

            // 宽度交给 WinForms 自己按文本量（AutoSize），不再靠我估算 ——
            // 之前估窄了导致文本折行、然后被按钮高度裁掉，看上去就是"显示不全"。
            // 高度也加高一档，让它明显是这一列的主操作。
            _btnNewGroup = new Button
            {
                Text = "＋ 新建四宫格",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(120, 40),
                Height = 40,
                FlatStyle = FlatStyle.System,
                Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
                Padding = new Padding(10, 0, 10, 0),
                Margin = new Padding(0, 0, 0, 6)
            };
            _btnNewGroup.Click += (s, e) => AddGroup();
            bar.Controls.Add(_btnNewGroup);
            bar.SetFlowBreak(_btnNewGroup, true);

            _btnRenameGroup = MkButton("改名", 68, 30, false);
            _btnRenameGroup.Click += (s, e) => RenameGroup();
            _btnDelGroup = MkButton("删除", 68, 30, false);
            _btnDelGroup.Click += (s, e) => DeleteGroup();
            bar.Controls.Add(_btnRenameGroup);
            bar.Controls.Add(_btnDelGroup);

            host.Controls.Add(gb);
            return host;
        }

        // ─────────── 第二列：格子内容 ───────────

        /// <summary>
        /// 列表行的度量。集中定义，避免列表高度和行高各写一个数导致对不上。
        /// 注意：ListBox 有 2px 边框（上下各 2px），算高度时必须把它加进去，
        /// 否则第 4 行会被挤出可视区，反而出现滚动条 —— 四宫格只有四行，
        /// 本来就该一屏完整显示。
        /// </summary>
        const int RowHeight = 38;      // 单行高度
        const int RowsShown = 4;       // 列表可见行数（四宫格 4 项正好一屏，不滚动）
        const int ListBorder = 4;      // ListBox 上下边框合计
        const int ButtonBarH = 34;     // 按钮条
        const int DropZoneH = 46;      // 投放区
        const int GroupTitleH = 22;    // GroupBox 标题占用

        Control BuildColumn2()
        {
            int listH = RowHeight * RowsShown + ListBorder;
            int totalH = GroupTitleH + listH + ButtonBarH + DropZoneH + 12;
            int width = 316;

            var gb = new GroupBox
            {
                Text = "格子内容（顺序 = 左上 / 右上 / 左下 / 右下）",
                Location = new Point(0, 0),
                Size = new Size(width, totalH),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Padding = new Padding(8, 4, 8, 6),
                Margin = new Padding(0, 0, 6, 0)
            };

            _itemList = new ListBox
            {
                Location = new Point(8, GroupTitleH),
                Size = new Size(width - 16, listH),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                IntegralHeight = false,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = RowHeight,
                Margin = new Padding(0)
            };
            _itemList.AllowDrop = true;
            _itemList.DrawItem += DrawItemRow;
            _itemList.SelectedIndexChanged += (s, e) => OnItemSelected();
            gb.Controls.Add(_itemList);

            // 四个按钮改成按百分比平分整条，不再各自写死宽度。
            // 原因：写死的 84+62+72+72 = 290，加上间距 24 = 314，而这条只有 300 宽，
            // FlowLayoutPanel 不换行，多出来的「↓ 下移」被直接裁掉（界面上看不出少了东西）。
            // 平分之后不管列宽、DPI、字体怎么变，四个按钮永远正好占满，不会再被裁。
            var bar = new TableLayoutPanel
            {
                Location = new Point(8, GroupTitleH + listH + 2),
                Size = new Size(width - 16, ButtonBarH - 2),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                ColumnCount = 4,
                RowCount = 1,
                Margin = new Padding(0)
            };
            for (int i = 0; i < 4; i++) bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
            bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            gb.Controls.Add(bar);

            _btnAddItem = MkButton("＋ 添加", 60, 28, true);
            _btnAddItem.Click += (s, e) => AddItem();
            _btnDelItem = MkButton("移除", 52, 28, false);
            _btnDelItem.Click += (s, e) => DeleteItem();
            var btnUp = MkButton("↑ 上移", 56, 28, false);
            btnUp.Click += (s, e) => MoveItem(-1);
            var btnDown = MkButton("↓ 下移", 56, 28, false);
            btnDown.Click += (s, e) => MoveItem(1);

            foreach (var b in new[] { _btnAddItem, _btnDelItem, btnUp, btnDown })
            {
                b.Dock = DockStyle.Fill;              // 宽度交给表格平分，Height 由行高决定
                b.Margin = new Padding(2, 0, 2, 0);
            }
            bar.Controls.Add(_btnAddItem, 0, 0);
            bar.Controls.Add(_btnDelItem, 1, 0);
            bar.Controls.Add(btnUp, 2, 0);
            bar.Controls.Add(btnDown, 3, 0);

            var dz = BuildDropZone();
            dz.Location = new Point(8, GroupTitleH + listH + ButtonBarH);
            dz.Size = new Size(width - 16, DropZoneH);
            dz.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            gb.Controls.Add(dz);

            // 「预计效果」
            var gbPrev = new GroupBox
            {
                Text = "预计效果",
                Padding = new Padding(6, 4, 6, 6)
            };
            _preview = new CmbPreview();
            _preview.Location = new Point(6, 22);
            _preview.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            gbPrev.Controls.Add(_preview);

            // ── 第二列整体：全部用**固定高度 + 精确像素定位**，不用 Dock、不用表格 ──
            //
            // 为什么这么做：这块区域每个元素的高度都是已知的（列表 156、按钮条 34、
            // 投放区 46、预览 = 内容高度 + 标题），硬算出来反而最可靠。
            // 试过 Dock 堆叠和 TableLayoutPanel，前者被 z 序坑、后者的 AutoSize 行
            // 与 Percent 行会互相冲突（实测内容比窗口还高 88px 被裁掉）。
            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0), AutoScroll = false };
            _contentHost = host;

            // 预览高度先给个保守值，等控件拿到真实宽度后再由 SyncPreviewHeight 修正
            int gap = 6;
            gb.Location = new Point(0, 0);
            gb.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            host.Controls.Add(gb);
            _contentBox = gb;

            int prevTop = totalH + gap;
            gbPrev.Location = new Point(0, prevTop);
            gbPrev.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            host.Controls.Add(gbPrev);
            _previewBox = gbPrev;

            host.Resize += (s, e) =>
            {
                int w = Math.Max(80, host.ClientSize.Width);
                if (w == _lastHostW) return;      // 宽度没变就别重排，否则会和下面的尺寸修改互相触发
                _lastHostW = w;
                gb.Width = w;
                gbPrev.Width = w;
                LayoutPreview(w);
            };
            // 真实宽度要到窗体显示后才有，所以显示后再算一次
            Shown += (s, e) => LayoutPreview(Math.Max(80, host.ClientSize.Width));

            return host;
        }

        /// <summary>
        /// 按第二列宽度摆好预览框。宽度变了才需要重排 ——
        /// 这个方法内部会改控件尺寸，那些改动又会触发 host.Resize，
        /// 不做判重就会自己触发自己（实测日志被刷了上百次）。
        /// </summary>
        void LayoutPreview(int hostW, bool force = false)
        {
            if (_preview == null || _previewBox == null) return;
            if (!force && hostW == _lastLayoutW) return;
            _lastLayoutW = hostW;

            _previewBox.Width = hostW;
            _preview.Width = Math.Max(60, hostW - 12);

            int contentH = _preview.ContentHeight();
            _previewBox.Height = contentH + 26;
            _preview.Height = contentH;
        }

        /// <summary>
        /// 宫格形状（几列几行）变了之后重排预览。
        /// 必须走强制分支 —— LayoutPreview 平时靠宽度判重防抖，
        /// 而改行列数时宽度没变，不强制就会被直接跳过，预览框高度不跟着更新。
        /// </summary>
        void RelayoutPreview()
        {
            int w = (_contentHost != null && _contentHost.ClientSize.Width > 0)
                ? Math.Max(80, _contentHost.ClientSize.Width)
                : Math.Max(80, _lastLayoutW);
            LayoutPreview(w, true);
        }
        Panel _contentHost;

        int _lastHostW = -1;
        int _lastLayoutW = -1;
        string _lastPreviewSig;

        Panel _dropZone;
        GroupBox _contentBox, _previewBox;
        Panel _bottomBar;

        Control BuildDropZone()
        {
            bool elevated = Cfg.IsElevated();

            _dropZone = new Panel
            {
                AllowDrop = true,
                BackColor = elevated ? Color.FromArgb(255, 247, 240) : Color.FromArgb(246, 248, 252),
                Margin = new Padding(0)
            };

            _dropZone.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, _dropZone.Width - 1, _dropZone.Height - 1);
                using (var pen = new Pen(elevated ? Color.FromArgb(220, 150, 110) : Color.FromArgb(150, 170, 200), 1f)
                       { DashStyle = DashStyle.Dash })
                    g.DrawRectangle(pen, r);

                // 紧凑排布：一行主提示 + 一行副提示，正好落在 46px 内，不换行也不溢出
                using (var f = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold))
                using (var b = new SolidBrush(elevated ? Color.FromArgb(180, 80, 30) : Color.FromArgb(90, 110, 150)))
                    g.DrawString(elevated
                        ? "拖放不可用：管理器正以管理员身份运行"
                        : "把快捷方式 / 程序拖到这里加入格子", f, b, 7, 4);

                using (var f2 = new Font("Microsoft YaHei UI", 7.5f))
                using (var b2 = new SolidBrush(elevated ? Color.FromArgb(200, 120, 60) : Color.Gray))
                    g.DrawString(elevated
                        ? "Windows 禁止向管理员程序拖放，用普通权限重开即可"
                        : "快捷方式会自动追溯成真正的 .exe", f2, b2, 7, 23);
            };

            WireDrop(_dropZone);
            return _dropZone;
        }

        // ─────────── 第三列：外观 + 位置 ───────────

        Control BuildColumn3()
        {
            var outer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Margin = new Padding(0)
            };
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // 程序与外观
            var gbLook = new GroupBox
            {
                Text = "程序与外观",
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8, 4, 8, 4),
                Margin = new Padding(0, 0, 0, 6)
            };
            BuildLookFields(gbLook);
            outer.Controls.Add(gbLook, 0, 0);

            // 位置
            var gbPos = new GroupBox
            {
                Text = "位置（网格坐标，分辨率无关）",
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8, 4, 8, 4),
                Margin = new Padding(0, 0, 0, 6)
            };
            BuildPositionFields(gbPos);
            outer.Controls.Add(gbPos, 0, 1);

            return outer;
        }

        void BuildLookFields(GroupBox parent)
        {
            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                Padding = new Padding(2)
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));   // 标签列收窄，给输入框留宽
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            parent.Controls.Add(t);

            int r = 0;
            // 输入框限制最大宽度：不加限制时它们会拉伸到整列宽，
            // 看着又长又空。限宽后右侧留白反而更清爽。
            const int FieldMaxW = 380;

            _txtCaption = new TextBox { MaximumSize = new Size(FieldMaxW, 0) };
            AddRow(t, r++, "显示文字", _txtCaption);
            _txtCaption.TextChanged += (s, e) => { MarkDirty(); _itemList.Invalidate(); _preview.Invalidate(); };

            var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0), MaximumSize = new Size(FieldMaxW, 0) };
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
            _txtPath = new TextBox { Dock = DockStyle.Fill };
            _txtPath.TextChanged += (s, e) => { MarkDirty(); _preview.Invalidate(); };
            _btnBrowsePath = new Button { Text = "…", Dock = DockStyle.Fill };
            _btnBrowsePath.Click += (s, e) => BrowsePath();
            pathRow.Controls.Add(_txtPath, 0, 0);
            pathRow.Controls.Add(_btnBrowsePath, 1, 0);
            AddRow(t, r++, "程序路径", pathRow);

            var iconRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0), MaximumSize = new Size(FieldMaxW, 0) };
            iconRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            iconRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
            _txtIcon = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "留空 = 自动用原程序图标" };
            _txtIcon.TextChanged += (s, e) => { MarkDirty(); _preview.Invalidate(); };
            _btnBrowseIcon = new Button { Text = "…", Dock = DockStyle.Fill };
            _btnBrowseIcon.Click += (s, e) => BrowseIcon();
            iconRow.Controls.Add(_txtIcon, 0, 0);
            iconRow.Controls.Add(_btnBrowseIcon, 1, 0);
            AddRow(t, r++, "图标", iconRow);

            // 底色与透明度分两行：
            //   底色行 = 色块 + 选色 + 模式切换
            //   透明度行 = 整行滑块（像初版那样单开一小行，拖起来才够长、够准）
            //
            // ★ 关键设计：当这一格处于「跟随总配置」时，这里的色块和滑条改的是
            //   **总配置里的默认值**（config.json 的 style.plateColor / plateAlpha），
            //   而不是这一格的覆盖值。这样不用打开参数设置就能快速调默认值 ——
            //   否则默认值只能靠「跟随总配置」按钮单向同步过去，改不动。
            //   处于「本格单独设置」时，改的才是这一格自己的值。
            var colorRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = false,
                Margin = new Padding(0),
                MaximumSize = new Size(FieldMaxW, 0)
            };
            _bgSwatch = new Panel { Width = 40, Height = 24, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 2, 6, 0) };
            _btnBgColor = new Button { Text = "选色", Width = 76, Height = 26, Margin = new Padding(0, 1, 6, 0) };
            _btnBgColor.Click += (s, e) => PickBgColor();
            _btnBgClear = new Button { Text = "改为本格单独设置", Width = 148, Height = 26, Margin = new Padding(0, 1, 6, 0) };
            _btnBgClear.Click += (s, e) => ToggleBgMode();
            colorRow.Controls.Add(_bgSwatch);
            colorRow.Controls.Add(_btnBgColor);
            colorRow.Controls.Add(_btnBgClear);
            AddRow(t, r++, "底色", colorRow);

            var alphaRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0), MaximumSize = new Size(FieldMaxW, 0) };
            alphaRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            alphaRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
            _bgAlpha = new TrackBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 255, TickFrequency = 32, Value = 40, Height = 26, Margin = new Padding(0) };
            _bgAlpha.ValueChanged += (s, e) =>
            {
                _lblBgAlpha.Text = _bgAlpha.Value.ToString();
                if (_loading) return;
                // 跟随总配置时改的是默认值，否则改这一格
                if (_bgAlphaFollow) _cfg.Style.PlateAlpha = _bgAlpha.Value;
                MarkDirty();
                _preview.Invalidate();
            };
            _lblBgAlpha = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Text = "40", Margin = new Padding(0) };
            alphaRow.Controls.Add(_bgAlpha, 0, 0);
            alphaRow.Controls.Add(_lblBgAlpha, 1, 0);
            AddRow(t, r++, "透明度", alphaRow);

            // 模式提示：一眼看出现在改的是"所有格子的默认值"还是"这一格"
            _lblBgMode = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 30,
                ForeColor = Color.DimGray,
                Font = new Font("Microsoft YaHei UI", 8f),
                Text = ""
            };
            parent.Controls.Add(_lblBgMode);
            _lblBgMode.BringToFront();

            // 提示图标来源
            var hint = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 34,
                ForeColor = Color.DimGray,
                Font = new Font("Microsoft YaHei UI", 8f),
                Text = "程序路径会自动追溯到真正的 .exe，所以快捷方式从桌面删掉也不影响。"
            };
            parent.Controls.Add(hint);
            hint.BringToFront();
        }

        void BuildPositionFields(GroupBox parent)
        {
            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                Padding = new Padding(2)
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            parent.Controls.Add(t);

            // 位置只由手动拖动决定，这里放一句说明即可。
            // 原来做成禁用的下拉框，Dock=Fill 会被行高撑成 40px 的大方块，很难看。
            var hint = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.DimGray,
                Font = new Font("Microsoft YaHei UI", 8f),
                Text = "在桌面上把宫格拖到位 → 右键「记住当前位置」",
                Margin = new Padding(0)
            };
            AddRow(t, 0, "摆放方式", hint);

            var posRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            posRow.Controls.Add(new Label { Text = "列", Width = 20, Height = 26, TextAlign = ContentAlignment.MiddleLeft });
            _numCol = new NumericUpDown { Width = 60, Height = 26, Minimum = 0, Maximum = 999 };
            _numCol.ValueChanged += (s, e) => OnPositionEdited();
            posRow.Controls.Add(_numCol);
            posRow.Controls.Add(new Label { Text = " 行", Width = 26, Height = 26, TextAlign = ContentAlignment.MiddleLeft });
            _numRow = new NumericUpDown { Width = 60, Height = 26, Minimum = 0, Maximum = 999 };
            _numRow.ValueChanged += (s, e) => OnPositionEdited();
            posRow.Controls.Add(_numRow);
            AddRow(t, 1, "格位", posRow);

            var actRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            var btnSync = new Button { Text = "从桌面同步位置", Width = 140, Height = 26 };
            btnSync.Click += (s, e) =>
            {
                _positionTouched = null;
                ReloadPositionsFromDisk();
                SyncPositionControls();
                Cfg.Log("[管理器] 手动从桌面同步位置");
            };
            actRow.Controls.Add(btnSync);
            AddRow(t, 2, "", actRow);

            // ── 这个宫格自己的占地与形状 ──
            // 放在「位置」组里，因为它和位置一样是**宫格级**的设置，
            // 而上面那个「程序与外观」组里的每一行都是**选中格**的设置，两者不能混。
            //
            // 「占用」和「形状」是两层独立的设置：
            //   占用 = 宫格占几个桌面图标格（每格 83×114）—— 决定外框多大
            //   形状 = 宫格里面装几列几行格子              —— 决定外框怎么分
            // 所以可以"占 2×2 个图标格，里面放 3×3 共 9 个格子"。
            var fpRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            _numFpCols = new NumericUpDown { Width = 52, Height = 26, Minimum = 1, Maximum = 8 };
            _numFpCols.ValueChanged += (s, e) => OnLayoutEdited();
            fpRow.Controls.Add(_numFpCols);
            fpRow.Controls.Add(new Label { Text = " 格宽 × ", Width = 54, Height = 26, TextAlign = ContentAlignment.MiddleLeft });
            _numFpRows = new NumericUpDown { Width = 52, Height = 26, Minimum = 1, Maximum = 8 };
            _numFpRows.ValueChanged += (s, e) => OnLayoutEdited();
            fpRow.Controls.Add(_numFpRows);
            fpRow.Controls.Add(new Label { Text = " 格高", Width = 40, Height = 26, TextAlign = ContentAlignment.MiddleLeft });
            _btnFpFollow = new Button { Text = "自动", Width = 60, Height = 26, Margin = new Padding(8, 0, 0, 0) };
            _btnFpFollow.Click += (s, e) => SetFootprintAuto();
            fpRow.Controls.Add(_btnFpFollow);
            AddRow(t, 3, "占用", fpRow);

            var shapeRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            _numCols = new NumericUpDown { Width = 52, Height = 26, Minimum = 1, Maximum = 8 };
            _numCols.ValueChanged += (s, e) => OnLayoutEdited();
            shapeRow.Controls.Add(_numCols);
            shapeRow.Controls.Add(new Label { Text = " 列 × ", Width = 42, Height = 26, TextAlign = ContentAlignment.MiddleLeft });
            _numRows = new NumericUpDown { Width = 52, Height = 26, Minimum = 1, Maximum = 8 };
            _numRows.ValueChanged += (s, e) => OnLayoutEdited();
            shapeRow.Controls.Add(_numRows);
            shapeRow.Controls.Add(new Label { Text = " 行", Width = 34, Height = 26, TextAlign = ContentAlignment.MiddleLeft });
            _btnShapeFollow = new Button { Text = "跟随总配置", Width = 88, Height = 26, Margin = new Padding(8, 0, 0, 0) };
            _btnShapeFollow.Click += (s, e) => SetShapeFollow();
            shapeRow.Controls.Add(_btnShapeFollow);
            AddRow(t, 4, "形状", shapeRow);

            _lblShapeInfo = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 46,
                ForeColor = Color.DimGray,
                Font = new Font("Microsoft YaHei UI", 8f)
            };
            parent.Controls.Add(_lblShapeInfo);
            _lblShapeInfo.BringToFront();

            _lblPosInfo = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 40,
                ForeColor = Color.DimGray,
                Font = new Font("Microsoft YaHei UI", 8f)
            };
            parent.Controls.Add(_lblPosInfo);
            _lblPosInfo.BringToFront();
        }

        static void AddRow(TableLayoutPanel t, int row, string label, Control c)
        {
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            t.Controls.Add(new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0)
            }, 0, row);
            c.Dock = DockStyle.Fill;
            c.Margin = new Padding(0, 2, 0, 2);
            t.Controls.Add(c, 1, row);
        }

        void BuildBottomBar()
        {
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(12, 8, 12, 8) };
            bottom.Name = "bottomBar";
            _bottomBar = bottom;
            Controls.Add(bottom);
            // 必须先 BringToFront，否则内容区（Dock=Fill）盖住底栏 —— 按钮就被遮了。
            bottom.BringToFront();

            // Dock=Fill 而不是 Dock=Left+固定宽：左边这块只放状态文字，宽度不需要固定。
            // 固定 300 时，右侧按钮排被挤出去的部分会被直接裁掉（FlowLayoutPanel 不开换行，
            // 溢出即消失，界面上根本看不出来少了按钮）。改成 Fill，长度不够时由它让位。
            _lblStatus = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                ForeColor = Color.DimGray
            };
            bottom.Controls.Add(_lblStatus);

            var right = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                WrapContents = false
            };
            _bottomButtons = right;
            bottom.Controls.Add(right);

            // 底部按钮用完整词条，不再简写：两字写法（配置 / 重启 / 放弃）看不出到底做什么。
            // 宽度由 MkButton 按实际文字算，不写死 —— 写死正是「配置」被裁掉的原因。
            _btnSave = MkButton("保存并重启", 88, 30, true);
            _btnSave.Click += (s, e) => SaveAll(true);
            _btnReload = MkButton("放弃修改", 68, 30, false);
            _btnReload.Click += (s, e) => { if (ConfirmDiscard()) LoadAll(); };
            _btnRestart = MkButton("重启覆盖层", 80, 30, false);
            _btnRestart.Click += (s, e) => { RestartOverlay(); RefreshRunState(); };
            _btnSettings = MkButton("参数设置", 68, 30, false);
            _btnSettings.Click += (s, e) => OpenSettings();
            _btnOpenFolder = MkButton("打开配置文件夹", 96, 30, false);
            _btnOpenFolder.Click += (s, e) => OpenPath(Cfg.AppDir);

            right.Controls.Add(_btnSave);
            right.Controls.Add(_btnReload);
            right.Controls.Add(_btnRestart);
            right.Controls.Add(_btnSettings);
            right.Controls.Add(_btnOpenFolder);

            // 覆盖层状态 + 开机自启，直接放底栏，不必进设置
            var run = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 0, 12, 0)
            };
            _bottomRun = run;
            bottom.Controls.Add(run);
            _lblRunState = new Label { Height = 30, TextAlign = ContentAlignment.MiddleLeft };
            // 宽度按两种状态里更长的那个量出来，不写死：
            // 写死会截字，用 AutoSize 又会在「运行中 / 未运行」切换时让整排按钮左右跳。
            _lblRunState.Width = Math.Max(
                TextRenderer.MeasureText(RunTextOn, Font).Width,
                TextRenderer.MeasureText(RunTextOff, Font).Width) + 6;
            run.Controls.Add(_lblRunState);

            _chkAutoStart = new CheckBox { Text = "开机自动启动", Height = 30, TextAlign = ContentAlignment.MiddleLeft };
            // +24 = 勾选框本身占的宽度（文字宽度里不含它）
            _chkAutoStart.Width = TextRenderer.MeasureText(_chkAutoStart.Text, Font).Width + 24;
            _chkAutoStart.CheckedChanged += (s, e) =>
            {
                if (_loading) return;
                _cfg.Behaviour.StartWithWindows = _chkAutoStart.Checked;
                MarkDirty();
            };
            run.Controls.Add(_chkAutoStart);

            // 词条写全之后按钮变宽，光看字也未必知道和「保存」的区别，所以补悬停说明。
            _tip = new ToolTip { AutoPopDelay = 15000, InitialDelay = 350, ReshowDelay = 100 };
            _tip.SetToolTip(_btnSave, "把当前修改写进 json，并重启桌面覆盖层让它立刻生效");
            _tip.SetToolTip(_btnReload, "丢掉还没保存的修改，重新从磁盘上的 json 载入");
            _tip.SetToolTip(_btnRestart, "只重启桌面覆盖层：不保存、不写 json，用来让手动改过的 json 生效");
            _tip.SetToolTip(_btnSettings, "调整四宫格的格子尺寸、间距、字号等外观参数");
            _tip.SetToolTip(_btnOpenFolder, "在资源管理器里打开程序与 json 所在的文件夹");
            _tip.SetToolTip(_chkAutoStart, "登录 Windows 后自动启动桌面覆盖层");
        }

        static Button MkButton(string text, int w, int h, bool primary)
        {
            var b = new Button
            {
                Text = text,
                Width = w,
                Height = h,
                FlatStyle = FlatStyle.System,
                Margin = new Padding(3, 0, 3, 0)
            };
            if (primary) b.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            // 宽度按真实文字量算，w 只当最小值。
            // 中文字宽随 DPI / 系统缩放变化很大，写死宽度就会截字或截按钮。
            int need = TextRenderer.MeasureText(text, b.Font).Width + 24;   // 24 = 左右内边距
            if (need > b.Width) b.Width = need;
            return b;
        }

        #endregion

        #region 数据

        void LoadAll()
        {
            _loading = true;
            try
            {
                _cfg = Cfg.LoadConfig();
                _groups.Clear();
                _groupList.Items.Clear();

                if (_cfg != null)
                {
                    int fixedCount = 0;
                    foreach (var r in _cfg.Groups)
                    {
                        var g = Cfg.LoadGroup(r.GroupFile);
                        if (g == null) continue;
                        // 程序路径自动追溯：快捷方式指向的 exe 才是真正的程序。
                        // 这样用户把桌面快捷方式删掉（放四宫格本来就是为了省桌面）也不会弄坏格子。
                        foreach (var it in g.Items)
                            if (PathResolver.Upgrade(it)) fixedCount++;
                        _groups.Add(g);
                    }
                    if (fixedCount > 0)
                        Cfg.Log("[管理器] 自动追溯了 " + fixedCount + " 条程序路径（快捷方式 -> 真正的 exe）");
                    _chkAutoStart.Checked = _cfg.Behaviour.StartWithWindows;
                    UpdatePosInfo();
                }

                foreach (var g in _groups) _groupList.Items.Add(g.Name ?? "(未命名)");
                if (_groupList.Items.Count > 0) _groupList.SelectedIndex = 0;

                RefreshRunState();
                _lblStatus.Text = "已载入 " + _groups.Count + " 个四宫格";
                _lblStatus.ForeColor = Color.DimGray;
            }
            finally { _loading = false; }

            ApplyMinSize();
        }

        /// <summary>
        /// 按当前配置算出"保证预览完整显示"所需的最小窗口尺寸，并真正应用它。
        ///
        /// 为什么要算而不是拍：预览高度 = 宫格高 × 倍数，而宫格尺寸、行列数都来自配置
        /// （用户随时可能改）。写死一个最小值，改了配置就会被裁。
        ///
        /// 高度构成：
        ///   第二列 = 内容GB + 列间距 + 预览GB
        ///   窗口   = 第二列 + 底部按钮栏 + 上下留白
        ///
        /// 注意：必须在窗体显示之后调用 —— 构造/载入阶段句柄还没建好，
        /// 那时设置的尺寸会被布局覆盖掉（实测设了 564 仍是 581 的旧值）。
        /// </summary>
        void ApplyMinSize()
        {
            if (_cfg == null || _preview == null) return;

            int listH = RowHeight * RowsShown + ListBorder;
            int contentGb = GroupTitleH + listH + ButtonBarH + DropZoneH + 12;
            int previewH = 22 + _preview.ContentHeight();     // 22 = GroupBox 标题
            int col2 = contentGb + 6 + previewH;

            int minW = 154 + 316 + 372 + 20;                 // 三列 + 左右边距（第二列已收窄到 316）
            int minH = col2 + BottomBarH + 70;               // 70 = 表格上下留白 + 余量（实测 34 太紧、62 仍差一点，取 70 得到 600）

            // 底栏宽度也必须满足：状态文字 + 整排按钮 + 覆盖层状态/开机自启。
            // 不算这一项，窗口一窄，按钮排溢出部分就被 FlowLayoutPanel 直接裁掉 ——
            // 界面上看不出少了按钮，点也点不到（「打开配置文件夹」就这样消失过）。
            int bottomNeed = StatusMinW
                           + _bottomButtons.PreferredSize.Width
                           + _bottomRun.PreferredSize.Width
                           + 24      // 底栏左右内边距
                           + 8;      // 余量
            minW = Math.Max(minW, bottomNeed);

            var wa = Screen.PrimaryScreen.WorkingArea;
            minW = Math.Min(minW, wa.Width - 20);
            minH = Math.Min(minH, wa.Height - 20);

            MinimumSize = new Size(minW, minH);

            // 真正把窗口撑到最小值：只设 MinimumSize 不会自动放大当前窗口
            int w = Math.Max(Width, minW);
            int h = Math.Max(Height, minH);
            if (w != Width || h != Height) ClientSize = new Size(w, h);

            Cfg.Log("[尺寸] 最小窗口 = " + minW + "x" + minH
                    + "（内容GB=" + contentGb + " 预览GB=" + previewH
                    + " 第二列=" + col2 + " 底栏=" + BottomBarH
                    + " 底栏需要宽=" + bottomNeed
                    + "(按钮排=" + _bottomButtons.PreferredSize.Width
                    + " 状态区=" + _bottomRun.PreferredSize.Width + ")）"
                    + "  已应用 " + Width + "x" + Height);
        }

        const int BottomBarH = 46;   // 底部按钮栏高度
        const int StatusMinW = 120;  // 左下角状态文字最少要留多宽（"已载入 N 个四宫格" 实测约 110px）

        // 覆盖层运行状态文案：两处共用（量宽度 / 显示），避免改了文案忘了改宽度。
        const string RunTextOn = "● 四宫格运行中";
        const string RunTextOff = "○ 四宫格未运行";

        void MarkDirty()
        {
            if (_loading) return;
            _lblStatus.Text = "有未保存的修改";
            _lblStatus.ForeColor = Color.FromArgb(200, 92, 0);
        }

        bool ConfirmDiscard()
        {
            if (_lblStatus.Text.StartsWith("有未保存") &&
                MessageBox.Show("有未保存的修改，确定放弃吗？", "WinQuad 管理器",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return false;
            return true;
        }

        bool SaveAll(bool restart)
        {
            if (_cfg == null) return false;
            try
            {
                ApplyFieldsToCurrentItem();

                // 位置要合并而不是覆盖：
                // 用户很可能在桌面上拖动过宫格（覆盖层会把新位置写进 json），
                // 而管理器内存里还是打开时的旧位置。若直接保存，就把拖动结果冲掉了。
                // 这里的规则是：只要用户在界面上没手动改过位置，就以磁盘上的为准。
                foreach (var g in _groups)
                {
                    if (g == _positionTouched || g == null) continue;

                    var onDisk = Cfg.LoadGroup(Path.GetFileName(g.FilePath));
                    if (onDisk?.Position == null) continue;

                    bool diskHasPos = onDisk.Position.Col != PositionSection.Unset ||
                                      onDisk.Position.Row != PositionSection.Unset;
                    if (!diskHasPos) continue;

                    bool same = g.Position != null &&
                                g.Position.Col == onDisk.Position.Col &&
                                g.Position.Row == onDisk.Position.Row;
                    if (!same)
                    {
                        Cfg.Log("[管理器] 采纳磁盘上的新位置 " + (g.Name ?? "") + "：col="
                                + onDisk.Position.Col + " row=" + onDisk.Position.Row + "（桌面拖动过）");
                        g.Position.Col = onDisk.Position.Col;
                        g.Position.Row = onDisk.Position.Row;
                        g.Position.X = onDisk.Position.X;
                        g.Position.Y = onDisk.Position.Y;
                        g.Position.Anchor = onDisk.Position.Anchor;
                    }
                }

                ApplyPositionToCurrentGroup();

                if (!Cfg.SaveConfig(_cfg)) throw new Exception("写 config.json 失败");
                foreach (var g in _groups)
                    if (!Cfg.SaveGroup(g)) throw new Exception("写 " + Path.GetFileName(g.FilePath) + " 失败");

                AutoStart.Apply(_cfg.Behaviour.StartWithWindows);
                _positionTouched = null;

                _lblStatus.Text = "已保存";
                _lblStatus.ForeColor = Color.SeaGreen;
                Cfg.Log("[管理器] 已保存 " + _groups.Count + " 个四宫格");

                if (restart) { RestartOverlay(); RefreshRunState(); }
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存失败：\n" + ex.Message, "WinQuad 管理器",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        #endregion

        #region 第一列

        GroupFile Current => _groupList != null && _groupList.SelectedIndex >= 0 &&
                             _groupList.SelectedIndex < _groups.Count
            ? _groups[_groupList.SelectedIndex] : null;

        void OnGroupSelected()
        {
            RefreshItemList();
            SyncPositionControls();
            _preview.Invalidate();
        }

        void AddGroup()
        {
            string name = Prompt.Show("新建四宫格", "给这个四宫格起个名字：", "新宫格");
            if (string.IsNullOrWhiteSpace(name)) return;

            string file = "group-" + Sanitize(name) + ".json";
            if (!Cfg.CreateGroupFile(file, name))
            {
                MessageBox.Show("创建失败，可能同名文件已存在。", "WinQuad 管理器");
                return;
            }
            var g = Cfg.LoadGroup(file);
            if (g == null) return;

            // 给新宫格挑一个不和现有宫格重叠的格位，
            // 否则它会带着"未设置"的占位值跑到屏幕左上角去。
            Cfg.PickInitialPosition(g, _cfg, _groups);
            Cfg.SaveGroup(g);

            _groups.Add(g);
            _cfg.Groups.Add(new GroupRef { GroupFile = file });
            _groupList.Items.Add(g.Name);
            _groupList.SelectedIndex = _groupList.Items.Count - 1;
            MarkDirty();
            Cfg.Log("[管理器] 新建四宫格 " + file);
        }

        static string Sanitize(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim();
        }

        void RenameGroup()
        {
            var g = Current;
            if (g == null) return;
            string name = Prompt.Show("重命名", "新的名字：", g.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            g.Name = name.Trim();
            _groupList.Items[_groupList.SelectedIndex] = g.Name;
            MarkDirty();
        }

        void DeleteGroup()
        {
            var g = Current;
            if (g == null) return;
            if (MessageBox.Show("把「" + g.Name + "」从列表移除？\n\n" +
                    "只会从列表移除，对应的 json 文件会保留（不删文件，更不删你的程序）。",
                    "WinQuad 管理器", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            int i = _groupList.SelectedIndex;
            _cfg.Groups.RemoveAll(r => string.Equals(r.GroupFile, Path.GetFileName(g.FilePath),
                                                     StringComparison.OrdinalIgnoreCase));
            _groups.RemoveAt(i);
            _groupList.Items.RemoveAt(i);
            if (_groupList.Items.Count > 0) _groupList.SelectedIndex = 0;
            else OnGroupSelected();
            MarkDirty();
        }

        #endregion

        #region 第二列

        GroupItem CurrentItem => _itemList != null && _itemList.SelectedIndex >= 0 && Current != null &&
                                 _itemList.SelectedIndex < Current.Items.Count
            ? Current.Items[_itemList.SelectedIndex] : null;

        /// <summary>
        /// 当前宫格实际生效的尺寸 = 总配置的尺寸 + 这个宫格自己的占地与行列数。
        /// 宫格宽高、判定框、预览尺寸全部从它算，保证三处永远一致。
        /// </summary>
        SizeSection EffectiveSize => _cfg?.Size?.WithLayout(
            Current?.Layout,
            _cfg?.Behaviour?.GridStepX ?? 83,
            _cfg?.Behaviour?.GridStepY ?? 114);

        /// <summary>当前宫格一共几个格子（按它自己的行列数）。</summary>
        int CellCount(GroupFile g)
        {
            if (g == null || _cfg?.Size == null) return 4;
            var own = g.Layout ?? new LayoutSection();
            return own.CellCount(_cfg.Size.Cols, _cfg.Size.Rows);
        }

        /// <summary>把 items 补够到当前行列数，多出来的留着不删（切回 2×2 还能看到）。</summary>
        static void EnsureItemSlots(GroupFile g, int need)
        {
            if (g.Items == null) g.Items = new List<GroupItem>();
            while (g.Items.Count < need) g.Items.Add(new GroupItem());
        }

        /// <summary>列表里显示的一行文字。空槽显示"(空)"，和已有的写法保持一致。</summary>
        static string SlotLabel(GroupItem it) =>
            (it == null || (string.IsNullOrWhiteSpace(it.Path) && string.IsNullOrWhiteSpace(it.Caption)))
                ? "(空)" : (it.Caption ?? "(未命名)");

        /// <summary>
        /// 按当前宫格的行列数重建第三列的格子列表。
        /// 设成 1×2 就只列两行，不再固定显示四行 —— 否则后两行点了没反应，像是坏了。
        /// </summary>
        void RefreshItemList()
        {
            var g = Current;
            int sel = _itemList.SelectedIndex;
            _loading = true;
            try
            {
                _itemList.Items.Clear();
                if (g == null) { _itemList.Enabled = false; return; }
                _itemList.Enabled = true;

                int n = CellCount(g);
                EnsureItemSlots(g, n);
                for (int i = 0; i < n; i++) _itemList.Items.Add(SlotLabel(g.Items[i]));

                if (_itemList.Items.Count > 0)
                    _itemList.SelectedIndex = Math.Max(0, Math.Min(sel, _itemList.Items.Count - 1));
            }
            finally { _loading = false; }
        }

        void OnItemSelected()
        {
            var it = CurrentItem;
            _loading = true;
            try
            {
                if (it == null)
                {
                    _txtCaption.Text = ""; _txtPath.Text = ""; _txtIcon.Text = "";
                    _bgSwatch.Tag = null; _bgSwatch.BackColor = SystemColors.Control;
                    _bgAlpha.Value = _cfg?.Style.PlateAlpha ?? 40;
                    return;
                }
                _txtCaption.Text = it.Caption ?? "";
                _txtPath.Text = it.Path ?? "";
                _txtIcon.Text = it.Icon ?? "";
                if (it.BgColor != null && it.BgColor.Length >= 3)
                {
                    var c = Color.FromArgb(it.BgColor[0], it.BgColor[1], it.BgColor[2]);
                    _bgColorFollow = false;
                    _bgSwatch.Tag = c; _bgSwatch.BackColor = c;
                }
                else
                {
                    _bgColorFollow = true;
                    _bgSwatch.Tag = null;
                    _bgSwatch.BackColor = MasterPlateColor();
                }
                _bgAlphaFollow = it.BgAlpha == null;
                _bgAlpha.Value = Math.Max(0, Math.Min(255, it.BgAlpha ?? (_cfg?.Style.PlateAlpha ?? 40)));
                UpdateBgModeHint();
            }
            finally { _loading = false; }
            _preview.Invalidate();
        }

        void ApplyFieldsToCurrentItem()
        {
            var it = CurrentItem;
            if (it == null) return;
            it.Caption = _txtCaption.Text.Trim();
            it.Path = _txtPath.Text.Trim();
            it.Icon = string.IsNullOrWhiteSpace(_txtIcon.Text) ? null : _txtIcon.Text.Trim();

            // 跟随总配置时必须写回 null，不能把当前显示值固化成覆盖值。
            // （原来的实现无条件写 it.BgAlpha = _bgAlpha.Value，保存一次「跟随」就没了。）
            it.BgColor = _bgColorFollow ? null : new[] { (int)_bgSwatch.BackColor.R, (int)_bgSwatch.BackColor.G, (int)_bgSwatch.BackColor.B };
            it.BgAlpha = _bgAlphaFollow ? (int?)null : _bgAlpha.Value;
        }

        /// <summary>总配置里的底板颜色。取不到就给个白色兜底。</summary>
        Color MasterPlateColor()
        {
            var c = _cfg?.Style?.PlateColor;
            return (c != null && c.Length >= 3) ? Color.FromArgb(c[0], c[1], c[2]) : Color.White;
        }

        /// <summary>刷新「现在改的是默认值还是这一格」的提示与按钮文字。</summary>
        void UpdateBgModeHint()
        {
            if (_lblBgMode == null || _btnBgClear == null) return;

            bool follow = _bgColorFollow && _bgAlphaFollow;
            if (follow)
            {
                _btnBgClear.Text = "改为本格单独设置";
                _lblBgMode.Text = "这一格跟随总配置：上面改的是所有格子的默认色与默认透明度。";
                _lblBgMode.ForeColor = Color.FromArgb(0, 100, 170);
            }
            else
            {
                _btnBgClear.Text = "改回跟随总配置";
                _lblBgMode.Text = "这一格有单独设置：上面只改这一格，不影响其他格子。";
                _lblBgMode.ForeColor = Color.FromArgb(170, 90, 0);
            }
        }

        /// <summary>
        /// 在「跟随总配置」和「本格单独设置」之间切换。
        /// 切回跟随时把这一格的覆盖值清掉；切到单独设置时用当前总配置值当起点。
        /// </summary>
        void ToggleBgMode()
        {
            var it = CurrentItem;
            if (it == null) return;

            bool nowFollow = _bgColorFollow && _bgAlphaFollow;
            if (nowFollow)
            {
                // 跟随 -> 单独设置：把当前显示值固化成这一格的覆盖值
                _bgColorFollow = false;
                _bgAlphaFollow = false;
                it.BgColor = new[] { (int)_bgSwatch.BackColor.R, (int)_bgSwatch.BackColor.G, (int)_bgSwatch.BackColor.B };
                it.BgAlpha = _bgAlpha.Value;
            }
            else
            {
                // 单独设置 -> 跟随：清掉覆盖值，显示回总配置的值
                _bgColorFollow = true;
                _bgAlphaFollow = true;
                it.BgColor = null;
                it.BgAlpha = null;
                var c = MasterPlateColor();
                _bgSwatch.Tag = null;
                _bgSwatch.BackColor = c;
                _loading = true;
                try { _bgAlpha.Value = Math.Max(0, Math.Min(255, _cfg?.Style.PlateAlpha ?? 40)); }
                finally { _loading = false; }
                _lblBgAlpha.Text = _bgAlpha.Value.ToString();
            }

            UpdateBgModeHint();
            MarkDirty();
            _itemList.Invalidate();
            _preview.Invalidate();
        }

        #region 拖放添加

        /// <summary>
        /// 给控件挂上拖放处理。三个事件都要挂：
        ///   DragEnter 只决定光标第一次进来时的样子；
        ///   DragOver  必须持续返回 Copy，否则光标会一直是禁止号；
        ///   DragDrop  才是真正取数据。
        /// 曾经只挂 DragEnter + DragDrop，结果光标始终禁止 —— 缺的就是 DragOver。
        /// </summary>
        void WireDrop(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += (s, e) =>
            {
                bool ok = e.Data.GetDataPresent(DataFormats.FileDrop);
                Cfg.Log("[拖放] DragEnter on " + c.GetType().Name + " 含文件=" + ok);
                e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
            };
            c.DragOver += (s, e) =>
            {
                e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop)
                    ? DragDropEffects.Copy : DragDropEffects.None;
            };
            c.DragDrop += (s, e) => HandleFileDrop(e);
        }

        void HandleFileDrop(DragEventArgs e)
        {
            Cfg.Log("[拖放] DragDrop 触发");
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null || files.Length == 0) return;
            AddFiles(files);
        }

        /// <summary>
        /// 把若干文件加进当前宫格。
        /// 这是「＋ 添加程序」按钮与投放区**共用**的唯一入口 ——
        /// 两条路径走同一套容量判定与路径追溯逻辑，不会出现行为不一致。
        /// </summary>
        void AddFiles(IEnumerable<string> files)
        {
            var g = Current;
            if (g == null) { MessageBox.Show("请先选择一个四宫格。", "WinQuad 管理器"); return; }

            // 容量看**这个宫格自己的**行列数，不是总配置的
            int cap = CellCount(g);
            int filled = FilledCount(g);
            int added = 0, skippedFull = 0, skippedMissing = 0, lastSlot = -1;

            EnsureItemSlots(g, cap);

            foreach (var f in files)
            {
                if (filled + added >= cap) { skippedFull++; continue; }
                if (!File.Exists(f)) { skippedMissing++; continue; }

                var it = new GroupItem { Path = f, Icon = null };
                PathResolver.Upgrade(it);                  // 快捷方式自动追溯成真正的 exe
                it.Caption = SuggestCaption(it.Path);

                // 只在当前宫格的格子范围内找空位，不往容量外追加
                int slot = -1;
                for (int k = 0; k < cap && k < g.Items.Count; k++)
                    if (IsEmpty(g.Items[k])) { slot = k; break; }

                if (slot < 0) { skippedFull++; continue; }
                g.Items[slot] = it;
                lastSlot = slot;
                added++;
            }

            if (added > 0)
            {
                RefreshItemList();
                if (lastSlot >= 0 && lastSlot < _itemList.Items.Count)
                    _itemList.SelectedIndex = lastSlot;
                MarkDirty();
                _preview.Invalidate();
                Cfg.Log("[管理器] 加入 " + added + " 项");
            }

            if (skippedFull > 0 || skippedMissing > 0)
            {
                var msg = new List<string>();
                if (skippedFull > 0) msg.Add("格子已满（上限 " + cap + " 个），跳过 " + skippedFull + " 项");
                if (skippedMissing > 0) msg.Add("文件不存在，跳过 " + skippedMissing + " 项");
                MessageBox.Show(string.Join("\n", msg), "WinQuad 管理器");
            }
        }

        // 顺序调整只用「↑ 上移 / ↓ 下移」按钮。
        // 曾经做过"在列表里拖动某行来交换"，但那套手工处理鼠标的方式会干扰
        // 资源管理器的 OLE 拖放（拖文件进来时显示禁止号），所以砍掉了 ——
        // 拖放添加程序比拖动换行更有价值。

        /// <summary>交换两个格子的内容（数据 + 列表显示）。</summary>
        void SwapItems(int i, int j)
        {
            var g = Current;
            if (g == null || i < 0 || j < 0 || i >= g.Items.Count || j >= g.Items.Count) return;

            var tmp = g.Items[i]; g.Items[i] = g.Items[j]; g.Items[j] = tmp;
            object o = _itemList.Items[i]; _itemList.Items[i] = _itemList.Items[j]; _itemList.Items[j] = o;

            MarkDirty();
            _itemList.Invalidate();
            _preview.Invalidate();
            Cfg.Log("[管理器] 交换格位 " + i + " <-> " + j);
        }

        #endregion

        /// <summary>
        /// 真正装了程序的格数。
        ///
        /// 不能用 items.Count —— 模板给新宫格生成的是 4 个空占位项（path 为空），
        /// 那时 items.Count 已经是 4，会直接判定"已满"，用户一个程序都加不进去。
        /// 必须数"path 非空"的项。
        /// </summary>
        int FilledCount(GroupFile g) =>
            g?.Items == null ? 0 : g.Items.Count(x => !string.IsNullOrWhiteSpace(x.Path));

        static bool IsEmpty(GroupItem it) =>
            it == null || string.IsNullOrWhiteSpace(it.Path);

        void AddItem()
        {
            var g = Current;
            if (g == null) { MessageBox.Show("请先选择一个四宫格。", "WinQuad 管理器"); return; }

            int cap = _cfg.Size.Cols * _cfg.Size.Rows;
            int filled = FilledCount(g);
            if (filled >= cap)
            {
                MessageBox.Show("这个四宫格已经放满 " + cap + " 个程序了。", "WinQuad 管理器");
                return;
            }

            using (var dlg = new OpenFileDialog
            {
                Title = "选择要放进格子的程序（快捷方式删掉也没关系，会自动记住原程序）",
                Multiselect = true,
                Filter = "快捷方式与程序|*.lnk;*.exe;*.url;*.bat;*.cmd|" +
                         "快捷方式 (*.lnk)|*.lnk|程序 (*.exe)|*.exe|网址 (*.url)|*.url|所有文件 (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                // 走和投放区完全相同的添加逻辑
                AddFiles(dlg.FileNames);
            }
        }

        static string SuggestCaption(string path)
        {
            string n = Path.GetFileNameWithoutExtension(path ?? "");
            if (string.IsNullOrEmpty(n)) n = "程序";
            return n.Length > 12 ? n.Substring(0, 12) : n;
        }

        /// <summary>
        /// 移除选中格的内容。
        /// 现在一律"清空该格"而不是把这一行删掉 —— 格子数由宫格的行列数决定，
        /// 删行会让列表短于宫格形状。空出来的行显示 "(空)"，宫格上就是一块透明区域。
        /// </summary>
        void DeleteItem()
        {
            var g = Current; int i = _itemList.SelectedIndex;
            if (g == null || i < 0 || i >= g.Items.Count) return;

            g.Items[i] = new GroupItem();
            RefreshItemList();

            if (_itemList.Items.Count > 0)
                _itemList.SelectedIndex = Math.Min(i, _itemList.Items.Count - 1);
            else
                OnItemSelected();

            MarkDirty();
            _preview.Invalidate();
        }

        void MoveItem(int d)
        {
            var g = Current; int i = _itemList.SelectedIndex;
            if (g == null || i < 0) return;
            int j = i + d;
            // 上界用当前宫格的格子数，不能换到被行列数藏起来的槽位里去
            if (j < 0 || j >= CellCount(g) || j >= g.Items.Count) return;

            var tmp = g.Items[i]; g.Items[i] = g.Items[j]; g.Items[j] = tmp;

            RefreshRowCaptions();
            _loading = true; _itemList.SelectedIndex = j; _loading = false;
            MarkDirty();
            _preview.Invalidate();
        }

        /// <summary>按 Items 重算列表里每行显示的字（空项显示 "(空)"，避免交换后残留旧名字）。</summary>
        void RefreshRowCaptions()
        {
            var g = Current;
            if (g == null) return;
            for (int k = 0; k < _itemList.Items.Count && k < g.Items.Count; k++)
                _itemList.Items[k] = SlotLabel(g.Items[k]);
        }

        void BrowsePath()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "选择程序",
                Filter = "快捷方式与程序|*.lnk;*.exe;*.url;*.bat;*.cmd|所有文件 (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _txtPath.Text = dlg.FileName;
            }
        }

        void BrowseIcon()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "选择图标",
                Filter = "图标|*.ico;*.png;*.jpg;*.bmp;*.exe;*.dll|所有文件 (*.*)|*.*"
            })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) _txtIcon.Text = dlg.FileName;
            }
        }

        void PickBgColor()
        {
            using (var dlg = new ColorDialog { FullOpen = true, AnyColor = true, Color = _bgSwatch.BackColor })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                if (_bgColorFollow)
                {
                    // 跟随总配置：改的是默认色，所有没单独设色的格子都会跟着变
                    _bgSwatch.Tag = null;
                    _bgSwatch.BackColor = dlg.Color;
                    _cfg.Style.PlateColor = new[] { (int)dlg.Color.R, (int)dlg.Color.G, (int)dlg.Color.B };
                }
                else
                {
                    _bgSwatch.Tag = dlg.Color;
                    _bgSwatch.BackColor = dlg.Color;
                }
                MarkDirty();
                _itemList.Invalidate();
                _preview.Invalidate();
            }
        }

        void DrawItemRow(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            e.DrawBackground();

            var g = Current;
            bool hasItem = g != null && e.Index < g.Items.Count;
            var item = hasItem ? g.Items[e.Index] : null;
            bool empty = IsEmpty(item);

            // 不再加 [左上]/[右上] 前缀 —— 顺序本来就可以拖动互换，
            // 标死位置反而误导；对应关系交给「预计效果」预览去看。
            string caption = empty ? "(空)" : (item.Caption ?? "(未命名)");
            string path = empty ? "把程序或快捷方式拖到这里" : (item.Path ?? "");
            string[] where = { "左上", "右上", "左下", "右下" };

            var r = e.Bounds;
            var ico = empty ? null : Icons.Load(string.IsNullOrWhiteSpace(item.Icon) ? item.Path : item.Icon);
            if (ico != null)
            {
                e.Graphics.DrawIcon(ico, new Rectangle(r.X + 6, r.Y + 10, 24, 24));
                ico.Dispose();
            }

            bool sel = (e.State & DrawItemState.Selected) != 0;
            Color mainColor = sel ? e.ForeColor : (empty ? Color.Gray : e.ForeColor);
            Color subColor = sel ? Color.FromArgb(200, e.ForeColor) : Color.Gray;

            using (var b = new SolidBrush(mainColor))
            using (var dim = new SolidBrush(subColor))
            using (var f2 = new Font("Microsoft YaHei UI", 7.8f))
            {
                e.Graphics.DrawString(caption, e.Font, b, r.X + 38, r.Y + 5);
                string p = path ?? "";
                if (p.Length > 46) p = "…" + p.Substring(p.Length - 45);
                e.Graphics.DrawString(p, f2, dim, r.X + 38, r.Y + 24);
            }
            GC.KeepAlive(where);
            e.DrawFocusRectangle();
        }

        #endregion

        #region 位置

        /// <summary>
        /// 从磁盘重读当前宫格的位置，刷新界面上的锚点/格位控件。
        ///
        /// 为什么需要这个：用户在桌面上拖动宫格时，覆盖层会把新位置写进 json，
        /// 但管理器不知道。若不重读，管理器保存时就把拖动结果冲掉了 ——
        /// 也就是"手动拖动无法用来快速改位置"。
        ///
        /// 触发时机刻意只有两个：窗口获得焦点时、保存前。
        /// 不做轮询、不监视文件 —— 符合"管理器不做自动同步"的约定。
        /// </summary>
        public void ReloadPositionsFromDisk()
        {
            if (_cfg == null) return;
            int adopted = 0;

            if (Environment.GetEnvironmentVariable("WINQUAD_SYNC_DEBUG") == "1")
                Cfg.Log("[同步诊断] 检查 " + _groups.Count + " 个宫格"
                        + "  _positionTouched=" + (_positionTouched == null
                            ? "null" : Path.GetFileName(_positionTouched.FilePath)));

            foreach (var g in _groups)
            {
                // 用户正在界面上手动改位置的那个，不要被磁盘值覆盖
                if (g == _positionTouched) continue;

                var disk = Cfg.LoadGroup(Path.GetFileName(g.FilePath));
                if (disk?.Position == null) continue;

                if (Environment.GetEnvironmentVariable("WINQUAD_SYNC_DEBUG") == "1")
                    Cfg.Log("[同步诊断] " + Path.GetFileName(g.FilePath)
                            + "  内存 col=" + (g.Position?.Col.ToString() ?? "null")
                            + " row=" + (g.Position?.Row.ToString() ?? "null")
                            + "  |  磁盘 col=" + disk.Position.Col + " row=" + disk.Position.Row);

                bool changed = g.Position == null ||
                               g.Position.Col != disk.Position.Col ||
                               g.Position.Row != disk.Position.Row ||
                               g.Position.X != disk.Position.X ||
                               g.Position.Y != disk.Position.Y ||
                               g.Position.Anchor != disk.Position.Anchor;

                if (!changed) continue;

                g.Position = disk.Position;
                adopted++;
                Cfg.Log("[管理器] 同步磁盘位置 " + (g.Name ?? "") + "：anchor=" + (disk.Position.Anchor ?? "null")
                        + " col=" + disk.Position.Col + " row=" + disk.Position.Row);
            }

            if (adopted > 0)
            {
                SyncPositionControls();
                if (_lblStatus.Text == "已保存" || _lblStatus.Text.StartsWith("已载入"))
                {
                    _lblStatus.Text = "已从桌面同步 " + adopted + " 个宫格的位置";
                    _lblStatus.ForeColor = Color.SteelBlue;
                }
            }
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            ReloadPositionsFromDisk();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            // 窗口可见时才轮询；最小化/隐藏时不起作用。
            if (_posTimer != null) _posTimer.Enabled = Visible;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 轮询位置：1.5 秒一次，只读一个几十行的小 json，开销可忽略。
            // 换来的是"在桌面上拖完宫格，切回管理器立刻就能看到新位置"。
            // 注意 OnVisibleChanged 在构造期间就触发过（那时 Visible 还是 false），
            // 所以定时器必须在 OnShown 里启动，不能只靠它。
            _posTimer.Enabled = true;
            ReloadPositionsFromDisk();

            // 尺寸必须在显示之后才能可靠应用（构造阶段句柄未建好，设了会被覆盖）
            ApplyMinSize();

            Cfg.Log("[管理器] 位置轮询已启动");
        }

        readonly Timer _posTimer = new Timer { Interval = 1500 };

        /// <summary>
        /// 位置控件被改动的统一入口。
        ///
        /// 关键：必须用 _loading 挡住程序性赋值。
        /// SyncPositionControls() 在刷新界面时会去写 _numCol.Value，那同样会触发 ValueChanged；
        /// 如果无条件把它当成"用户手动改过位置"，_positionTouched 就会在载入数据时被置上，
        /// 于是之后所有的"从桌面同步位置"都会被跳过 —— 这正是"拖动后 GUI 不同步"的根因。
        /// </summary>
        void OnPositionEdited()
        {
            if (_loading) return;
            _positionTouched = Current;
            MarkDirty();
            UpdatePosInfo();
            _preview.Invalidate();
        }

        /// <summary>从磁盘重读当前宫格的位置，刷新界面上的锚点/格位控件。</summary>
        void SyncPositionControls()
        {
            var g = Current;
            if (g == null) return;
            var p = g.Position ?? new PositionSection();

            _loading = true;
            try
            {
                _numCol.Value = ClampNum(p.Col, 0, 999);
                _numRow.Value = ClampNum(p.Row, 0, 999);
                SyncShapeControls(g);
            }
            finally { _loading = false; }
            UpdateShapeInfo();
            UpdatePosInfo();
        }

        /// <summary>把「占用」「形状」两组控件刷成当前宫格的值。</summary>
        void SyncShapeControls(GroupFile g)
        {
            var own = g?.Layout ?? new LayoutSection();
            int gc = _cfg?.Size?.Cols ?? 2;
            int gr = _cfg?.Size?.Rows ?? 2;

            // 占用没设时，界面显示"实际等效几格"（按当前外框折算），让人看得懂现状
            var eff0 = EffectiveSize;
            int fpc = own.FootprintCols ?? Math.Max(1, (int)Math.Round(
                (double)eff0.Width / Math.Max(1, _cfg?.Behaviour?.GridStepX ?? 83)));
            int fpr = own.FootprintRows ?? Math.Max(1, (int)Math.Round(
                (double)eff0.Height / Math.Max(1, _cfg?.Behaviour?.GridStepY ?? 114)));

            _numFpCols.Value = Math.Max(1, Math.Min(8, fpc));
            _numFpRows.Value = Math.Max(1, Math.Min(8, fpr));
            _numCols.Value = Math.Max(1, Math.Min(8, own.Cols ?? gc));
            _numRows.Value = Math.Max(1, Math.Min(8, own.Rows ?? gr));
        }

        /// <summary>
        /// 用户改了「占用」或「形状」。两项都写进**这个宫格自己的** layout，
        /// 不去动总配置 —— 别的宫格不受影响。
        /// </summary>
        void OnLayoutEdited()
        {
            if (_loading) return;
            var g = Current;
            if (g == null) return;

            g.Layout ??= new LayoutSection();
            g.Layout.FootprintCols = (int)_numFpCols.Value;
            g.Layout.FootprintRows = (int)_numFpRows.Value;
            g.Layout.Cols = (int)_numCols.Value;
            g.Layout.Rows = (int)_numRows.Value;

            AfterLayoutChange();
        }

        /// <summary>把这一宫格的占地清空，改回"自动按标准格位算"。</summary>
        void SetFootprintAuto()
        {
            var g = Current;
            if (g == null) return;
            g.Layout ??= new LayoutSection();
            g.Layout.FootprintCols = null;
            g.Layout.FootprintRows = null;
            AfterLayoutChange();
        }

        void AfterLayoutChange()
        {
            var g = Current;
            _loading = true;
            try { SyncShapeControls(g); } finally { _loading = false; }

            RefreshItemList();
            UpdateShapeInfo();
            MarkDirty();
            _preview.Invalidate();
            RelayoutPreview();
        }

        /// <summary>把这一宫格的行列数清空，改回跟随总配置。</summary>
        void SetShapeFollow()
        {
            var g = Current;
            if (g == null) return;

            g.Layout ??= new LayoutSection();
            g.Layout.Cols = null;
            g.Layout.Rows = null;

            AfterLayoutChange();
        }

        /// <summary>刷新「形状」下面那行说明：占地是自动还是指定，格子是跟随还是单独。</summary>
        void UpdateShapeInfo()
        {
            if (_lblShapeInfo == null || _btnShapeFollow == null || _btnFpFollow == null) return;

            var g = Current;
            int gc = _cfg?.Size?.Cols ?? 2;
            int gr = _cfg?.Size?.Rows ?? 2;
            var own = g?.Layout;

            bool fpAuto = own == null || !own.HasFootprint;
            bool shapeFollow = own == null || (!own.Cols.HasValue && !own.Rows.HasValue);

            _btnFpFollow.Text = fpAuto ? "指定" : "自动";

            var eff = EffectiveSize;
            string size = (eff != null)
                ? ("　外框 " + eff.Width + " × " + eff.Height
                   + "　单格 " + eff.CellWidth + "×" + eff.CellHeight
                   + " × " + (eff.Cols * eff.Rows) + " 格")
                : "";

            string l1 = fpAuto
                ? "占用：自动（按标准格位）"
                : ("占用：" + own.FootprintCols + " × " + own.FootprintRows + " 个图标格");
            string l2 = shapeFollow
                ? ("形状：跟随总配置（" + gc + " 列 × " + gr + " 行）")
                : ("形状：本宫格单独设置（" + own.Cols + " 列 × " + own.Rows + " 行）");

            _lblShapeInfo.Text = l1 + "　" + l2 + "\r\n" + size.TrimStart();
            _lblShapeInfo.ForeColor = (fpAuto && shapeFollow)
                ? Color.FromArgb(0, 100, 170)
                : Color.FromArgb(170, 90, 0);
        }

        static decimal ClampNum(int v, int lo, int hi) =>
            v == PositionSection.Unset ? lo : Math.Max(lo, Math.Min(hi, v));

        static int AnchorToIndex(string a)
        {
            switch ((a ?? "").ToLowerInvariant())
            {
                case "bottom-right": return 0;
                case "bottom-left": return 1;
                case "top-right": return 2;
                case "top-left": return 3;
                default: return 4;
            }
        }

        static string IndexToAnchor(int i)
        {
            switch (i)
            {
                case 0: return "bottom-right";
                case 1: return "bottom-left";
                case 2: return "top-right";
                case 3: return "top-left";
                default: return null;
            }
        }

        void ApplyPositionToCurrentGroup()
        {
            var g = Current;
            if (g == null) return;
            g.Position ??= new PositionSection();

            // 位置只由手动拖动 + 这里的列/行决定，不再有"自动角落"
            g.Position.Anchor = null;
            g.Position.Col = (int)_numCol.Value;
            g.Position.Row = (int)_numRow.Value;
            var px = GridToPixels((int)_numCol.Value, (int)_numRow.Value);
            g.Position.X = px.X;
            g.Position.Y = px.Y;
        }

        void UpdatePosInfo()
        {
            // 构造期间 ComboBox 等控件的 SelectedIndexChanged 会早于其他控件创建，
            // 这里必须防御 —— 否则空引用崩在启动阶段。
            if (_lblPosInfo == null || _cfg == null || _numCol == null || _numRow == null) return;

            var wa = Screen.PrimaryScreen.WorkingArea;
            var px = GridToPixels((int)_numCol.Value, (int)_numRow.Value);
            int maxCol = Math.Max(0, (wa.Width - _cfg.Size.Width) / IconStepX);
            int maxRow = Math.Max(0, (wa.Height - _cfg.Size.Height) / IconStepY);

            _lblPosInfo.Text =
                "列 " + _numCol.Value + " × " + IconStepX + " = 屏幕 x " + px.X
                + " ；行 " + _numRow.Value + " × " + IconStepY + " = 屏幕 y " + px.Y + "\r\n"
                + "当前屏幕可容纳 0~" + maxCol + " 列 / 0~" + maxRow + " 行";
        }

        Point GridToPixels(int col, int row)
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            return new Point(col * IconStepX + wa.Left, row * IconStepY + wa.Top);
        }

        #endregion

        #region 覆盖层进程

        static Process FindOverlay()
        {
            try { return Process.GetProcessesByName(OverlayProcName).FirstOrDefault(); }
            catch { return null; }
        }

        void RefreshRunState()
        {
            var p = FindOverlay();
            if (p != null)
            {
                _lblRunState.Text = RunTextOn;
                _lblRunState.ForeColor = Color.SeaGreen;
            }
            else
            {
                _lblRunState.Text = RunTextOff;
                _lblRunState.ForeColor = Color.Gray;
            }
        }

        static void StopOverlay()
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(OverlayProcName))
                {
                    p.Kill();
                    p.WaitForExit(3000);
                }
                Cfg.Log("[管理器] 已停止覆盖层");
            }
            catch (Exception ex) { Cfg.Log("停止覆盖层失败: " + ex.Message); }
        }

        static void StartOverlay()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Cfg.AppDir, OverlayExe),
                    Arguments = "--overlay",
                    WorkingDirectory = Cfg.AppDir,
                    UseShellExecute = true
                });
                Cfg.Log("[管理器] 已启动覆盖层");
            }
            catch (Exception ex) { Cfg.Log("启动覆盖层失败: " + ex.Message); }
        }

        static void RestartOverlay()
        {
            StopOverlay();
            System.Threading.Thread.Sleep(600);
            StartOverlay();
        }

        #endregion

        #region 杂项

        void OpenSettings()
        {
            int oldW = _cfg.Size.Width, oldH = _cfg.Size.Height;
            using (var dlg = new SettingsForm(_cfg))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                AutoStart.Apply(_cfg.Behaviour.StartWithWindows);
                MarkDirty();
                _preview.Invalidate();

                // 尺寸变了的话，磁盘上的 col/row 是按旧尺寸算的，会对不上号。
                // 清掉 _positionTouched，让保存时以位置信息重新换算。
                if (_cfg.Size.Width != oldW || _cfg.Size.Height != oldH)
                {
                    _positionTouched = null;
                    Cfg.Log("[管理器] 宫格尺寸由 " + oldW + "×" + oldH + " 变为 "
                            + _cfg.Size.Width + "×" + _cfg.Size.Height + "，位置将在保存时按新尺寸重算");
                }
                UpdatePosInfo();
            }
        }

        static void OpenPath(string p)
        {
            try { Process.Start(new ProcessStartInfo { FileName = p, UseShellExecute = true }); }
            catch (Exception ex) { Cfg.Log("打开失败 " + p + ": " + ex.Message); }
        }

        /// <summary>
        /// 导出当前预览为 PNG 并打开。
        /// 用途：不靠肉眼反复描述"哪里不对"，直接看渲染结果 ——
        /// 导出的图和面板上看到的走同一套绘制代码，必然一致。
        /// </summary>
        void ExportPreview()
        {
            try
            {
                if (_preview == null) return;
                string path = Path.Combine(Cfg.AppDir, "preview-export.png");
                using (var bmp = _preview.RenderToBitmap(new Size(360, 560)))
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                Cfg.Log("[导出] 预览图 -> " + path);
                OpenPath(path);
            }
            catch (Exception ex) { Cfg.Log("[导出] 失败: " + ex.Message); }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!ConfirmDiscard()) { e.Cancel = true; return; }
            base.OnFormClosing(e);
        }

        #endregion

        /// <summary>
        /// 「预计效果」。
        ///
        /// 做法：**先用覆盖层的同一套绘制函数，按 1:1 真实尺寸画进一张离屏位图，
        /// 再把整张位图等比放大**贴到面板上。
        ///
        /// 为什么必须这样：之前直接按放大的坐标绘制几何、但字号没跟着放大，
        /// 结果格子变大了字还是 7pt，比例失真（看着像"一行四个"）。
        /// 画成位图再整体缩放，图标和文字就一起缩放，比例永远正确。
        /// </summary>
        sealed class CmbPreview : Control
        {
            public CmbPreview()
            {
                DoubleBuffered = true;
                BackColor = Color.FromArgb(70, 80, 100);
                SetStyle(ControlStyles.ResizeRedraw, true);
            }

            /// <summary>
            /// 预览的固定放大倍数。改成别的值即可整体缩放预览，
            /// 高度会跟着自动重算（见 LayoutPreview / ContentHeight）。
            /// </summary>
            public const float FixedScale = 1.5f;

            /// <summary>
            /// 预览里"宫格画面"需要的高度（不含 GroupBox 标题与内边距）。
            /// 用**固定倍数**算，不再依赖实际宽度 —— 这样预览尺寸完全可预期。
            /// </summary>
            public int ContentHeight()
            {
                var main = FindForm() as MainForm;
                return PreviewPainter.ContentHeight(main?.EffectiveSize, FixedScale);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var main = FindForm() as MainForm;
                var cfg = main?._cfg;
                var grp = main?.Current;
                if (cfg == null || grp == null) return;

                var size = main.EffectiveSize;   // 总配置 + 这个宫格自己的行列数

                // 只在控件尺寸或画面尺寸变化时记录一次，避免重绘刷屏
                string sig = ClientSize.Width + "x" + ClientSize.Height + "/"
                           + size.Width + "x" + size.Height;
                if (main._lastPreviewSig != sig)
                {
                    main._lastPreviewSig = sig;
                    Cfg.Log("[预览] 控件=" + ClientSize.Width + "x" + ClientSize.Height
                            + " 宫格=" + size.Width + "x" + size.Height
                            + " (" + size.Cols + "列x" + size.Rows + "行)"
                            + " 放大=" + FixedScale.ToString("0.#"));
                }

                PreviewPainter.DrawGrid(e.Graphics, ClientSize, size, cfg.Style,
                                        grp.Items, FixedScale);
            }

            /// <summary>导出用：把预览按指定尺寸画到独立位图上。</summary>
            public Bitmap RenderToBitmap(Size size)
            {
                var main = FindForm() as MainForm;
                var cfg = main?._cfg;
                var grp = main?.Current;
                var bmp = new Bitmap(Math.Max(40, size.Width), Math.Max(40, size.Height));
                if (cfg == null || grp == null) return bmp;

                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.FromArgb(70, 80, 100));
                    PreviewPainter.DrawGrid(g, bmp.Size, main.EffectiveSize, cfg.Style, grp.Items, FixedScale);
                }
                return bmp;
            }

            /// <summary>
            /// 预览的实际绘制。已抽到 PreviewPainter ——
            /// 设置界面的「实时预览」和这里共用同一份代码，两边画出来必然一致。
            /// </summary>
        }
    }

    /// <summary>简易输入框（WinForms 没有内置的）。</summary>
    internal static class Prompt
    {
        public static string Show(string title, string label, string def)
        {
            using (var f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(410, 146);
                f.MinimizeBox = false; f.MaximizeBox = false;
                f.Font = new Font("Microsoft YaHei UI", 9f);

                f.Controls.Add(new Label { Text = label, Left = 16, Top = 18, Width = 370, Height = 22 });
                var tb = new TextBox { Left = 16, Top = 46, Width = 378, Text = def ?? "" };
                f.Controls.Add(tb);
                var ok = new Button { Text = "确定", Left = 226, Top = 92, Width = 80, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "取消", Left = 314, Top = 92, Width = 80, DialogResult = DialogResult.Cancel };
                f.Controls.Add(ok); f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                tb.SelectAll();
                return f.ShowDialog() == DialogResult.OK ? tb.Text : null;
            }
        }
    }
}
