// WinQuad — 把自绘的 2×2 迷你图标网格嵌进 Windows 桌面层，可拖动并吸附到桌面图标网格。
//
// 配置文件（都可手改，改完右键「重新载入分组」即时生效，不必重启）：
//   config.json           总配置：尺寸 / 配色 / 字体 / 行为 —— 所有宫格共享
//   group-<名称>.json     每宫格配置：4 个格子的程序与外观
// 以 "_" 开头的字段一律是注释，程序忽略，方便把文件发给他人或 AI 二次修改。
//
// 关键实现约束（都是从实测失败里学来的，别改回去）：
//   1. 扩展样式必须在 SetParent 之后重设 —— SetParent 会重置一部分 exstyle。
//   2. 几何必须用纯 Win32 SetWindowPos 落位 —— WinForms 布局会在 SetParent 后把窗口推回默认尺寸。
//   3. 透明必须用 SetWindowRgn，不能用色键 —— 跨进程 SetParent 后 WS_EX_LAYERED 不被可靠采纳。
//   4. 窗口区域要外扩 bleed，但这个外扩量直接决定「判定框」大小，
//      而判定框必须能塞进图标格位（83×114），否则拖不进图标之间、还会遮住相邻图标。
//   5. 判定框四边必须对称外扩 —— 曾经上左下各扩 4px、下边 0px，是个不对称的 bug。
//
// 交互约定（重要，别改坏）：
//   整个网格最外圈 ringSize 像素 = 「拖动把手」；内区 = 「双击启动区」。
//   两者互不重叠，所以拖动永远不会误触发启动，双击也永远不会误触发拖动。
//
// 关于快捷方式小箭头：
//   箭头是 SHGetFileInfo 处理 .lnk 时叠加的，不是本程序画的。
//   去掉的办法是先把 .lnk 解析到真正的目标 .exe，再从 .exe 提取图标。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace WinQuad
{
    #region 原生互操作

    internal static class Native
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr FindWindow(string cls, string win);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string win);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr h, int index);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr h, int index, int value);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr h, out RECT r);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr h);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT p);

        // --- 窗口区域（比色键可靠：不依赖 WS_EX_LAYERED 跨进程生效）---
        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowRgn(IntPtr h, IntPtr hRgn, bool redraw);

        // --- 右键菜单「属性」：调系统的文件属性对话框 ---
        // 必须走 ShellExecuteEx + SEE_MASK_INVOKEIDLIST，lpVerb 传 "properties"。
        // 这是资源管理器右键「属性」用的同一条路，弹出来的就是原生那个多标签属性页。
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpVerb;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpParameters;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr hProcess;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRectRgn(int l, int t, int r, int b);

        [DllImport("gdi32.dll")]
        public static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr obj);

        // --- 图标提取 ---
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfo(string path, uint fileAttr, ref SHFILEINFO info,
                                                  uint cbInfo, uint flags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        // --- 跨进程读桌面图标（SysListView32 在 explorer.exe 里）---
        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr addr, uint size, uint type, uint prot);

        [DllImport("kernel32.dll")]
        public static extern bool VirtualFreeEx(IntPtr p, IntPtr addr, uint size, uint type);

        [DllImport("kernel32.dll")]
        public static extern bool ReadProcessMemory(IntPtr p, IntPtr addr, byte[] buf, uint size, out uint read);

        [DllImport("kernel32.dll")]
        public static extern bool WriteProcessMemory(IntPtr p, IntPtr addr, byte[] buf, uint size, out uint written);

        public const int RGN_OR = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int L, T, R, B; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const uint SWP_FRAMECHANGED = 0x0020;

        public static readonly IntPtr HWND_TOP = IntPtr.Zero;

        public const uint SHGFI_ICON = 0x000000100;
        public const uint SHGFI_LARGEICON = 0x000000000;

        public const uint LVM_GETITEMCOUNT = 0x1004;
        public const uint LVM_GETITEMPOSITION = 0x1010;
        public const uint LVM_GETITEMTEXTW = 0x1073;

        public const uint PROCESS_VM_OPERATION = 0x0008;
        public const uint PROCESS_VM_READ = 0x0010;
        public const uint PROCESS_VM_WRITE = 0x0020;
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;

        public const uint MEM_COMMIT = 0x1000;
        public const uint MEM_RESERVE = 0x2000;
        public const uint MEM_RELEASE = 0x8000;
        public const uint PAGE_READWRITE = 0x04;

        [StructLayout(LayoutKind.Sequential)]
        public struct LVITEM
        {
            public uint mask;
            public int iItem;
            public int iSubItem;
            public uint state;
            public uint stateMask;
            public IntPtr pszText;
            public int cchTextMax;
            public int iImage;
            public IntPtr lParam;
            public int iIndent;
            public int iGroupId;
            public uint cColumns;
            public IntPtr puColumns;
            public IntPtr piColFmt;
            public int iGroup;
        }

        /// <summary>从任意外壳对象取图标（.exe/.dll/.ico 都可以）。</summary>
        public static Icon LoadShellIcon(string path)
        {
            var info = new SHFILEINFO();
            IntPtr res = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info),
                                       SHGFI_ICON | SHGFI_LARGEICON);
            if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try { return (Icon)Icon.FromHandle(info.hIcon).Clone(); }
            finally { DestroyIcon(info.hIcon); }
        }

        /// <summary>用 ExtractIconEx 取 .exe/.dll 里的第 index 个图标（不叠加任何外壳装饰）。</summary>
        public static Icon ExtractIconAt(string file, int index)
        {
            var large = new IntPtr[1];
            uint n = ExtractIconEx(file, index, large, null, 1);
            if (n == 0 || large[0] == IntPtr.Zero) return null;
            try { return (Icon)Icon.FromHandle(large[0]).Clone(); }
            finally { DestroyIcon(large[0]); }
        }
    }

    /// <summary>
    /// 解析 .lnk 快捷方式的目标路径。
    /// 这是「去掉快捷方式小箭头」的关键：箭头是外壳在处理 .lnk 时叠加的，
    /// 拿到真正的 .exe 之后再取图标就没有箭头了。
    /// </summary>
    internal static class ShortcutResolver
    {
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLinkCoClass { }

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
                         int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
                                 int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        /// <summary>把 .lnk 解析成目标路径。失败返回 null。</summary>
        public static string Resolve(string lnkPath)
        {
            object obj = null;
            try
            {
                obj = new ShellLinkCoClass();
                var link = (IShellLinkW)obj;
                ((IPersistFile)obj).Load(lnkPath, 0);

                var sb = new StringBuilder(1024);
                link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                string target = sb.ToString();
                return string.IsNullOrWhiteSpace(target) ? null : target;
            }
            catch { return null; }
            finally
            {
                if (obj != null) Marshal.ReleaseComObject(obj);
            }
        }

        [ComImport, Guid("0000010b-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }
    }

    #endregion

    #region 配置模型

    /// <summary>总配置：所有宫格共享的外观与布局。</summary>
    internal sealed class AppConfig
    {
        public int Version { get; set; } = 1;
        public SizeSection Size { get; set; } = new SizeSection();
        public StyleSection Style { get; set; } = new StyleSection();
        public BehaviourSection Behaviour { get; set; } = new BehaviourSection();
        public List<GroupRef> Groups { get; set; } = new List<GroupRef>();
    }

    internal sealed class SizeSection
    {
        public int Cols { get; set; } = 2;
        public int Rows { get; set; } = 2;
        public int CellWidth { get; set; } = 37;
        public int CellHeight { get; set; } = 48;
        public int PadX { get; set; } = 2;
        public int PadY { get; set; } = 2;
        public int Gap { get; set; } = 1;
        public int IconSize { get; set; } = 24;
        public int LabelHeight { get; set; } = 16;
        public int RingSize { get; set; } = 3;
        public int Bleed { get; set; } = 2;
    }

    internal sealed class StyleSection
    {
        public int[] PlateColor { get; set; } = { 255, 255, 255 };
        public int PlateAlpha { get; set; } = 40;
        public int PlateAlphaHover { get; set; } = 96;
        public int BorderAlpha { get; set; } = 40;
        public int[] BorderColor { get; set; } = { 255, 255, 255 };
        public int[] BackdropColor { get; set; } = { 24, 26, 32 };
        public string FontFamily { get; set; } = "Microsoft YaHei UI";
        public float FontSizePt { get; set; } = 8f;
        public bool FontBold { get; set; }
        public int[] TextColor { get; set; } = { 255, 255, 255 };
        public int[] TextColorHover { get; set; } = { 255, 255, 255 };
        public int[] OutlineColor { get; set; } = { 0, 0, 0 };
        public int OutlineAlpha { get; set; } = 245;
        public int OutlineWidth { get; set; } = 1;

        /// <summary>
        /// 文字投影的偏移像素数。0 = 关闭。
        ///
        /// 和 OutlineWidth 是**二选一**的关系，投影优先。
        /// 这是 Windows 自己画桌面图标标签的手法：只在右下垫一份暗色副本，
        /// 而不是四面八方围一圈。描边会把笔画"撑胖"、把字腔挤死；
        /// 投影不改变字的形状，只在一侧补对比度 —— 小字号下差别很明显。
        /// </summary>
        public int ShadowOffset { get; set; }

        /// <summary>投影的透明度（0~255）。只在 ShadowOffset > 0 时起作用。</summary>
        public int ShadowAlpha { get; set; } = 200;

        /// <summary>投影颜色。留空则复用 OutlineColor。</summary>
        public int[] ShadowColor { get; set; }

        public bool IconShadow { get; set; }
    }

    internal sealed class BehaviourSection
    {
        public bool DoubleClickToLaunch { get; set; } = true;
        public bool ShowTooltip { get; set; } = true;
        public bool AvoidDesktopIcons { get; set; } = true;
        public bool SnapToIconGrid { get; set; } = true;

        /// <summary>
        /// 自动避让时允许挪动的最大距离（像素）。0 = 不限制。
        /// 有上限才符合直觉：避让是为了防止你"无意间"盖住图标，
        /// 而不是把你明确放下的位置搬到几百像素外。附近真没空位就停在原地、只报警告。
        /// </summary>
        public int MaxNudgeDistance { get; set; } = 300;

        /// <summary>
        /// 遮挡判定用的图标碰撞盒（像素）。
        ///
        /// 桌面图标在网格里占的理论格位是 83×114，但**图标真正画出来的内容远小于它**：
        /// 图形本身约 48px 宽、48px 高，水平居中（左右各约 17px 空白），下面是文字，再往下是空白。
        ///
        /// 用整个格位去判定会大量误报：实测相邻两列的图标盒边缘总是恰好擦到 2px，
        /// 于是宫格落在完全空着的地方，也会被判成"被左边或右边的图标遮挡"。
        /// 所以这里取比真实图形再小一圈的值，给判定留余量。
        /// </summary>
        public int CollisionW { get; set; } = 44;

        /// <summary>碰撞盒高度。真实图形约 48px，加文字约 64px；取 56 兼顾。</summary>
        public int CollisionH { get; set; } = 56;

        public bool StartWithWindows { get; set; }

        /// <summary>
        /// 位置优先级：
        ///   "anchor"  —— 锚点优先。每次启动都按锚点重新算（推荐，位置会跟着屏幕走）
        ///   "manual"  —— 格位优先。尊重 col/row，锚点只在没有格位时用
        /// 之所以要做成选项：col/row 是"上一次的结果"，而锚点是"意图"。
        /// 两者混在一起时必须有明确的优先级，否则会出现"我明明选了右下角，它却停在别处"。
        /// </summary>
        public string PositionPriority { get; set; } = "anchor";

        /// <summary>桌面图标网格的横向间距（像素）。拖动吸附与碰撞判定都用它。
        /// 各机器可能不同（取决于图标大小与"图标间距"设置），
        /// 所以提供 --detect 让程序实测一次再写回这里。
        /// </summary>
        public int GridStepX { get; set; } = 83;

        /// <summary>
        /// 桌面图标网格的纵向间距（像素）。
        /// 注意：注册表的 IconVerticalSpacing 换算后通常和实际不符
        /// （本机注册表给 90，实测实际是 114），所以必须实测而不是读注册表。
        /// </summary>
        public int GridStepY { get; set; } = 114;
    }

    internal sealed class GroupRef
    {
        public string GroupFile { get; set; }
    }

    /// <summary>每宫格配置：4 个格子的程序与外观。</summary>
    internal sealed class GroupFile
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("position")] public PositionSection Position { get; set; } = new PositionSection();
        [JsonPropertyName("layout")] public LayoutSection Layout { get; set; }
        [JsonPropertyName("defaults")] public DefaultsSection Defaults { get; set; } = new DefaultsSection();
        [JsonPropertyName("items")] public List<GroupItem> Items { get; set; } = new List<GroupItem>();
    }

    /// <summary>
    /// 单个宫格自己的形状与占地。全部留空（null）就跟随总配置 config.json 的
    /// size.cols / size.rows，且外框按标准格位算。
    ///
    /// 两层是**独立**的：
    ///   · FootprintCols/Rows —— 宫格**占几个桌面图标格**（每格 = 图标格距 83×114）。
    ///     这就是"不局限于一个图标的宽度"。
    ///   · Cols/Rows —— 宫格**里面装几列几行格子**。
    ///
    /// 所以可以"占 2×2 个图标格，里面放 3×3 共 9 个格子"。
    /// </summary>
    internal sealed class LayoutSection
    {
        [JsonPropertyName("footprintCols")] public int? FootprintCols { get; set; }
        [JsonPropertyName("footprintRows")] public int? FootprintRows { get; set; }
        [JsonPropertyName("cols")] public int? Cols { get; set; }
        [JsonPropertyName("rows")] public int? Rows { get; set; }

        /// <summary>有没有指定占地。指定了才走"按图标格算外框"那条路。</summary>
        public bool HasFootprint =>
            FootprintCols.HasValue && FootprintRows.HasValue &&
            FootprintCols.Value > 0 && FootprintRows.Value > 0;
    }

    internal sealed class PositionSection
    {
        /// <summary>未设置的哨兵值。网格坐标里表示"没定过位"，像素坐标里表示"用锚点"。</summary>
        public const int Unset = int.MinValue;

        [JsonPropertyName("anchor")] public string Anchor { get; set; }

        /// <summary>
        /// 图标网格坐标。这是**首选**的记录方式 —— 分辨率无关。
        /// 换屏幕或改分辨率后仍能换算到对应的格位，而像素坐标会直接失效。
        /// </summary>
        [JsonPropertyName("col")] public int Col { get; set; } = Unset;
        [JsonPropertyName("row")] public int Row { get; set; } = Unset;

        /// <summary>
        /// 屏幕像素坐标。由网格坐标换算而来（不落盘，只作运行时缓存），
        /// 或在 grid 为 null 时作为用户手填的绝对坐标来源。
        /// </summary>
        [JsonPropertyName("x")] public int X { get; set; } = Unset;
        [JsonPropertyName("y")] public int Y { get; set; } = Unset;
    }

    internal sealed class DefaultsSection
    {
        [JsonPropertyName("icon")] public string Icon { get; set; }
    }

    internal sealed class GroupItem
    {
        [JsonPropertyName("caption")] public string Caption { get; set; }
        [JsonPropertyName("path")] public string Path { get; set; }
        [JsonPropertyName("icon")] public string Icon { get; set; }
        [JsonPropertyName("bgColor")] public int[] BgColor { get; set; }
        [JsonPropertyName("bgAlpha")] public int? BgAlpha { get; set; }
    }

    #endregion

    /// <summary>
    /// 版面的纯几何度量。抽出来是为了在创建窗口之前就能算出网格尺寸，
    /// 从而完成锚点解析与避让（避让需要知道网格有多大）。
    /// </summary>
    internal sealed class LayoutMetrics
    {
        public int Cols, Rows, CellW, CellH, IconSize, LabelH, PadX, PadY, Gap, W, H, RingSize, Bleed;

        /// <summary>
        /// 桌面图标网格间距（像素）。启动时由 config.json 的 behaviour 段注入。
        /// 本机实测是 83×114，但各机器可能不同（取决于图标大小与"图标间距"设置），
        /// 所以做成可配置 + 可用 --detect 自动探测。
        /// </summary>
        public static int IconStepX = 83;
        public static int IconStepY = 114;

        /// <summary>从配置注入网格间距。必须在任何位置计算之前调用。</summary>
        /// <summary>
        /// 图标碰撞盒尺寸。由配置注入，默认 48×64（图标真正画出来的范围，不是理论格位 78×113）。
        /// </summary>
        public static int CollisionW = 44;
        public static int CollisionH = 56;

        public static void ApplyGridStep(BehaviourSection b)
        {
            if (b == null) return;
            if (b.GridStepX >= 20 && b.GridStepX <= 400) IconStepX = b.GridStepX;
            if (b.GridStepY >= 20 && b.GridStepY <= 400) IconStepY = b.GridStepY;
            if (b.CollisionW >= 8 && b.CollisionW <= 200) CollisionW = b.CollisionW;
            if (b.CollisionH >= 8 && b.CollisionH <= 200) CollisionH = b.CollisionH;
        }

        public static LayoutMetrics From(SizeSection s, LayoutSection own = null)
        {
            // 宫格自己的行列数优先；没写就跟随总配置
            int cols = Math.Max(1, own?.Cols ?? s.Cols);
            int rows = Math.Max(1, own?.Rows ?? s.Rows);
            bool customCells = cols != s.Cols || rows != s.Rows;

            int cw, ch, fpW, fpH;

            if (own != null && own.HasFootprint)
            {
                // ── ① 按桌面图标格算外框（"不局限于一个图标的宽度"）──
                // 外框 = 占用格数 × 图标格距，再让出 bleed，使"外框 + bleed"正好等于整数个图标格。
                // 这样宫格占的位置和桌面图标格对齐，相邻格位不会被压到。
                fpW = own.FootprintCols.Value * IconStepX;
                fpH = own.FootprintRows.Value * IconStepY;
                int W = Math.Max(16, fpW - s.Bleed * 2);
                int H = Math.Max(16, fpH - s.Bleed * 2);
                cw = Math.Max(8, (W - s.PadX * 2 - s.Gap * (cols - 1)) / cols);
                ch = Math.Max(8, (H - s.PadY * 2 - s.Gap * (rows - 1)) / rows);
            }
            else
            {
                // ── ② 没指定占地：外框按总配置那套行列数算出的"标准格位"，格子数少了就摊开 ──
                //   2×2 -> 每格 37×48（和以前一样）
                //   1×2 -> 每格 75×48（宽格子，长名字放得下）
                //   2×1 -> 每格 37×97（高格子）
                int gc = Math.Max(1, s.Cols), gr = Math.Max(1, s.Rows);
                fpW = s.PadX * 2 + s.CellWidth * gc + s.Gap * (gc - 1);
                fpH = s.PadY * 2 + s.CellHeight * gr + s.Gap * (gr - 1);

                if (customCells)
                {
                    cw = Math.Max(8, (fpW - s.PadX * 2 - s.Gap * (cols - 1)) / cols);
                    ch = Math.Max(8, (fpH - s.PadY * 2 - s.Gap * (rows - 1)) / rows);
                }
                else
                {
                    cw = Math.Max(8, s.CellWidth);
                    ch = Math.Max(8, s.CellHeight);
                }
            }

            var m = new LayoutMetrics
            {
                Cols = cols,
                Rows = rows,
                CellW = cw,
                CellH = ch,
                PadX = Math.Max(0, s.PadX),
                PadY = Math.Max(0, s.PadY),
                Gap = Math.Max(0, s.Gap),
                IconSize = Math.Max(4, s.IconSize),
                LabelH = Math.Max(0, s.LabelHeight),
                RingSize = Math.Max(1, s.RingSize),
                Bleed = Math.Max(0, s.Bleed)
            };
            m.W = m.PadX * 2 + m.CellW * m.Cols + m.Gap * (m.Cols - 1);
            m.H = m.PadY * 2 + m.CellH * m.Rows + m.Gap * (m.Rows - 1);
            return m;
        }

        /// <summary>判定框尺寸。必须小于图标格距 83×114，否则拖不进图标之间。</summary>
        public Size Footprint => new Size(W + Bleed * 2, H + Bleed * 2);

        public Rectangle CellRect(int i)
        {
            int col = i % Cols, row = i / Cols;
            return new Rectangle(
                PadX + col * (CellW + Gap),
                PadY + row * (CellH + Gap),
                CellW, CellH);
        }

        public Rectangle InnerRect() => new Rectangle(RingSize, RingSize, W - RingSize * 2, H - RingSize * 2);

        public bool HitRing(Point p)
        {
            if (!new Rectangle(0, 0, W, H).Contains(p)) return false;
            return !InnerRect().Contains(p);
        }

        public int HitLaunch(Point p)
        {
            if (HitRing(p)) return -1;
            if (!new Rectangle(0, 0, W, H).Contains(p)) return -1;
            return CellAt(p);
        }

        /// <summary>把任意点归到最近的一格（缝隙也归入相邻格，避免死区）。</summary>
        public int CellAt(Point p)
        {
            int best = -1;
            long bestD = long.MaxValue;
            for (int i = 0; i < Cols * Rows; i++)
            {
                var c = CellRect(i);
                int dx = p.X < c.Left ? c.Left - p.X : (p.X > c.Right ? p.X - c.Right : 0);
                int dy = p.Y < c.Top ? c.Top - p.Y : (p.Y > c.Bottom ? p.Y - c.Bottom : 0);
                long d = (long)dx * dx + (long)dy * dy;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        public static Point SnapToIconGrid(int x, int y)
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            int sx = (int)Math.Round((double)(x - wa.Left) / IconStepX) * IconStepX + wa.Left;
            int sy = (int)Math.Round((double)(y - wa.Top) / IconStepY) * IconStepY + wa.Top;
            return new Point(sx, sy);
        }

        /// <summary>屏幕像素 → 图标网格序号。</summary>
        public static Point PixelsToGrid(int x, int y)
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            return new Point(
                (int)Math.Round((double)(x - wa.Left) / IconStepX),
                (int)Math.Round((double)(y - wa.Top) / IconStepY));
        }

        /// <summary>图标网格序号 → 屏幕像素。</summary>
        public static Point GridToPixels(int col, int row)
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            return new Point(col * IconStepX + wa.Left, row * IconStepY + wa.Top);
        }

        /// <summary>当前屏幕能容纳的列数 / 行数（给定网格尺寸）。</summary>
        public static Point GridCapacity(LayoutMetrics m)
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            return new Point(
                Math.Max(1, (wa.Width - m.W) / IconStepX + 1),
                Math.Max(1, (wa.Height - m.H) / IconStepY + 1));
        }
    }

    /// <summary>迷你图标网格，嵌在桌面层里，可拖动并吸附到桌面图标网格。</summary>
    internal sealed class GridForm : Form
    {
        readonly AppConfig _cfg;
        readonly GroupFile _grp;
        readonly LayoutMetrics _m;
        readonly string _groupPath;

        readonly Icon[] _icons = new Icon[4];
        readonly Rectangle[] _iconContent = new Rectangle[4];
        Font _labelFont;

        readonly ToolTip _tip = new ToolTip
        {
            InitialDelay = 500,
            ReshowDelay = 200,
            AutoPopDelay = 6000,
            ShowAlways = true
        };

        int _hoverCell = -1;
        bool _hoverRing;

        bool _dragging;
        Point _dragGrabScreen;
        Point _dragWinOrigin;

        public GridForm(AppConfig cfg, GroupFile grp, string groupPath)
        {
            _cfg = cfg;
            _grp = grp;
            _groupPath = groupPath;
            _m = LayoutMetrics.From(cfg.Size, grp.Layout);

            RebuildFont();

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = ToColorSafe(cfg.Style.BackdropColor, 255);
            Size = new Size(_m.W, _m.H);
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);

            LoadIcons();
            BuildContextMenu();

            var fp = _m.Footprint;
            bool explicitFootprint = grp.Layout != null && grp.Layout.HasFootprint;
            Program.Log("版面 " + _m.W + "x" + _m.H + "  判定框 " + fp.Width + "x" + fp.Height
                        + "  (图标格距 " + LayoutMetrics.IconStepX + "x" + LayoutMetrics.IconStepY + ")"
                        + "  占用 " + Math.Round((double)fp.Width / LayoutMetrics.IconStepX, 2)
                        + "x" + Math.Round((double)fp.Height / LayoutMetrics.IconStepY, 2) + " 个图标格"
                        + "  每格 " + _m.CellW + "x" + _m.CellH + " x" + (_m.Cols * _m.Rows)
                        + "  图标 " + _m.IconSize + "px  标签 " + _m.LabelH + "px  "
                        + cfg.Style.FontSizePt + "pt  拖动环 " + _m.RingSize + "px");

            // 只有"没显式指定占地"时才警告。显式指定了占用几格，跨格是故意的。
            if (!explicitFootprint &&
                (fp.Width > LayoutMetrics.IconStepX || fp.Height > LayoutMetrics.IconStepY))
                Program.Log("[警告] 判定框 " + fp.Width + "x" + fp.Height
                            + " 大于图标格距，将无法拖进图标之间、且会遮住相邻图标！"
                            + "（想跨多格请在管理器里设「占用」）");
        }

        void RebuildFont()
        {
            _labelFont?.Dispose();
            try
            {
                _labelFont = new Font(_cfg.Style.FontFamily, _cfg.Style.FontSizePt,
                    _cfg.Style.FontBold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
            }
            catch
            {
                Program.Log("[警告] 字体 '" + _cfg.Style.FontFamily + "' 不可用，退回微软雅黑");
                _labelFont = new Font("Microsoft YaHei UI", _cfg.Style.FontSizePt,
                    FontStyle.Regular, GraphicsUnit.Point);
            }
        }

        GroupItem ItemAt(int i) => (i >= 0 && i < _grp.Items.Count) ? _grp.Items[i] : null;

        void LoadIcons()
        {
            for (int i = 0; i < 4; i++) { _icons[i]?.Dispose(); _icons[i] = null; }

            for (int i = 0; i < 4 && i < _grp.Items.Count; i++)
            {
                var it = _grp.Items[i];
                string want = it.Icon ?? _grp.Defaults?.Icon;
                _icons[i] = Program.LoadIconFor(want, it.Path, out string how);
                if (_icons[i] == null) continue;
                _iconContent[i] = TightContent(_icons[i]);
                Program.Log("  格" + i + " " + (it.Caption ?? "") + " 图标来源：" + how);
            }
        }

        #region 嵌入桌面

        public bool EmbedIntoDesktop()
        {
            IntPtr progman = Native.FindWindow("Progman", null);
            IntPtr defView = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView == IntPtr.Zero) { Program.Log("找不到 SHELLDLL_DefView"); return false; }

            IntPtr me = Handle;
            IntPtr prev = Native.SetParent(me, defView);
            Program.Log("SetParent -> prev=" + prev + " lasterr=" + Marshal.GetLastWin32Error());

            // 约束 1：SetParent 之后再设扩展样式，否则会被重置
            int ex = Native.GetWindowLong(me, Native.GWL_EXSTYLE);
            Native.SetWindowLong(me, Native.GWL_EXSTYLE,
                ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            Program.Log("exstyle " + ex.ToString("X8") + " -> "
                        + Native.GetWindowLong(me, Native.GWL_EXSTYLE).ToString("X8"));

            ApplyRegion();
            ApplyGeometry();
            return true;
        }

        /// <summary>约束 3/4：透明用窗口区域；区域外扩量 Bleed 由配置控制。</summary>
        public void ApplyRegion()
        {
            IntPtr me = Handle;
            IntPtr total = Native.CreateRectRgn(0, 0, 0, 0);
            for (int i = 0; i < _m.Cols * _m.Rows; i++)
            {
                if (i >= _grp.Items.Count) continue;
                var c = _m.CellRect(i);
                IntPtr one = Native.CreateRectRgn(
                    c.Left - _m.Bleed, c.Top - _m.Bleed, c.Right + _m.Bleed, c.Bottom + _m.Bleed);
                Native.CombineRgn(total, total, one, Native.RGN_OR);
                Native.DeleteObject(one);
            }
            int r = Native.SetWindowRgn(me, total, true);
            Program.Log("SetWindowRgn -> " + r + " lasterr=" + Marshal.GetLastWin32Error());
        }

        /// <summary>约束 2：纯 Win32 落位，绝不碰 Form 的 Left/Top。</summary>
        public void ApplyGeometry()
        {
            IntPtr me = Handle;
            bool ok = Native.SetWindowPos(me, Native.HWND_TOP, _grp.Position.X, _grp.Position.Y, _m.W, _m.H,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | Native.SWP_FRAMECHANGED);

            Native.RECT r;
            Native.GetWindowRect(me, out r);
            Program.Log("geometry ok=" + ok + " rect=(" + r.L + "," + r.T + ")-(" + r.R + "," + r.B + ")"
                        + " size=" + (r.R - r.L) + "x" + (r.B - r.T)
                        + " visible=" + Native.IsWindowVisible(me));
        }

        void MoveTo(int x, int y)
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            if (x + _m.W > wa.Right) x = wa.Right - _m.W;
            if (y + _m.H > wa.Bottom) y = wa.Bottom - _m.H;
            if (x < wa.Left) x = wa.Left;
            if (y < wa.Top) y = wa.Top;

            _grp.Position.X = x;
            _grp.Position.Y = y;
            Native.SetWindowPos(Handle, Native.HWND_TOP, x, y, _m.W, _m.H,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        #endregion

        #region 跨进程读桌面图标 / 碰撞检查

        public static List<KeyValuePair<string, Rectangle>> ReadDesktopIcons()
        {
            var list = new List<KeyValuePair<string, Rectangle>>();
            IntPtr lv = Native.FindWindowEx(
                Native.FindWindowEx(Native.FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null),
                IntPtr.Zero, "SysListView32", null);
            if (lv == IntPtr.Zero) return null;

            uint pid;
            Native.GetWindowThreadProcessId(lv, out pid);
            IntPtr proc = Native.OpenProcess(
                Native.PROCESS_VM_OPERATION | Native.PROCESS_VM_READ |
                Native.PROCESS_VM_WRITE | Native.PROCESS_QUERY_INFORMATION, false, pid);
            if (proc == IntPtr.Zero) return null;

            try
            {
                int count = (int)Native.SendMessage(lv, Native.LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
                int sz = Marshal.SizeOf(typeof(Native.LVITEM));
                IntPtr remote = Native.VirtualAllocEx(proc, IntPtr.Zero, (uint)(sz + 1024),
                    Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_READWRITE);
                if (remote == IntPtr.Zero) return null;

                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        var it = new Native.LVITEM();
                        it.mask = 1; it.iItem = i; it.iSubItem = 0;
                        it.pszText = (IntPtr)((long)remote + sz); it.cchTextMax = 260;

                        uint w;
                        Native.WriteProcessMemory(proc, remote, ToBytes(it), (uint)sz, out w);
                        Native.SendMessage(lv, Native.LVM_GETITEMPOSITION,
                            (IntPtr)i, (IntPtr)((long)remote + sz + 512));

                        var pt = new byte[8]; uint r;
                        Native.ReadProcessMemory(proc, (IntPtr)((long)remote + sz + 512), pt, 8, out r);
                        int x = BitConverter.ToInt32(pt, 0), y = BitConverter.ToInt32(pt, 4);

                        Native.SendMessage(lv, Native.LVM_GETITEMTEXTW, (IntPtr)i, remote);
                        var txt = new byte[520];
                        Native.ReadProcessMemory(proc, (IntPtr)((long)remote + sz), txt, 520, out r);
                        string name = Encoding.Unicode.GetString(txt);
                        int z = name.IndexOf('\0'); if (z >= 0) name = name.Substring(0, z);

                        // 碰撞盒：图标在格位里水平居中、内容从格位顶部开始。
                        // 不是整个 78×113 格位 —— 那样会把"图标盒下方的空白"也算成占用，
                        // 于是宫格落在空处也会因为和空白重叠 3px 而被判冲突。
                        int offX = (LayoutMetrics.IconStepX - LayoutMetrics.CollisionW) / 2;
                        list.Add(new KeyValuePair<string, Rectangle>(
                            name, new Rectangle(x + offX, y,
                                                LayoutMetrics.CollisionW, LayoutMetrics.CollisionH)));
                    }
                }
                finally { Native.VirtualFreeEx(proc, remote, 0, Native.MEM_RELEASE); }
            }
            finally { Native.CloseHandle(proc); }

            return list;
        }

        static byte[] ToBytes(object o)
        {
            int n = Marshal.SizeOf(o);
            byte[] b = new byte[n];
            IntPtr p = Marshal.AllocHGlobal(n);
            Marshal.StructureToPtr(o, p, false);
            Marshal.Copy(p, b, 0, n);
            Marshal.FreeHGlobal(p);
            return b;
        }

        Rectangle FootprintBox(int x, int y) =>
            new Rectangle(x - _m.Bleed, y - _m.Bleed, _m.W + _m.Bleed * 2, _m.H + _m.Bleed * 2);

        public List<string> FindIconCollisions()
        {
            var hits = new List<string>();
            var icons = ReadDesktopIcons();
            if (icons == null) return hits;
            var box = FootprintBox(_grp.Position.X, _grp.Position.Y);
            foreach (var kv in icons)
                if (kv.Value.IntersectsWith(box))
                    hits.Add(kv.Key + " @ (" + kv.Value.X + "," + kv.Value.Y + ")");
            return hits;
        }

        public void ReportCollisions()
        {
            List<string> hits;
            try { hits = FindIconCollisions(); }
            catch (Exception ex) { Program.Log("[碰撞检查异常] " + ex.Message); return; }

            if (hits.Count == 0)
            {
                Program.Log("[碰撞检查] 通过：(" + _grp.Position.X + "," + _grp.Position.Y + ") 处无图标被遮挡");
                return;
            }
            Program.Log("[碰撞检查] 警告：压住了 " + hits.Count + " 个桌面图标，它们会被遮挡且点不到：");
            foreach (var h in hits) Program.Log("            - " + h);
        }

        #endregion

        #region 锚点与避让

        public static void ClampToWorkArea(PositionSection p, LayoutMetrics m)
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            int x = p.X == PositionSection.Unset ? wa.Left : p.X;
            int y = p.Y == PositionSection.Unset ? wa.Top : p.Y;
            if (x + m.W > wa.Right) x = wa.Right - m.W;
            if (y + m.H > wa.Bottom) y = wa.Bottom - m.H;
            if (x < wa.Left) x = wa.Left;
            if (y < wa.Top) y = wa.Top;
            p.X = x; p.Y = y;
        }

        /// <summary>
        /// 确定宫格位置。
        ///
        /// 只有一个来源：宫格文件里的 col/row（图标网格坐标）。
        /// 曾经有"锚点自动放到某个角落"的功能，删掉了 ——
        /// 它和手动拖动混在一起会产生"到底该听谁"的歧义，而手动拖动已经够方便。
        /// 没设过 col/row 时退回像素坐标（兼容手写配置）。
        /// </summary>
        public static void ResolvePosition(GroupFile g, LayoutMetrics m, int maxNudge)
        {
            var p = g.Position ?? (g.Position = new PositionSection());

            if (p.Col == PositionSection.Unset || p.Row == PositionSection.Unset)
            {
                ClampToWorkArea(p, m);
                SyncGridFromPixels(p);
                Program.Log("[位置] 未指定格位，使用像素坐标 (" + p.X + "," + p.Y + ")");
                return;
            }

            List<KeyValuePair<string, Rectangle>> icons = null;
            try { icons = ReadDesktopIcons(); }
            catch (Exception ex) { Program.Log("读取桌面图标失败: " + ex.Message); }

            var cap = LayoutMetrics.GridCapacity(m);
            int col = Math.Clamp(p.Col, 0, cap.X - 1);
            int row = Math.Clamp(p.Row, 0, cap.Y - 1);

            var px = LayoutMetrics.GridToPixels(col, row);
            Program.Log("[位置] 格位 col=" + col + " row=" + row
                        + " -> 像素 (" + px.X + "," + px.Y + ")");

            // 用户指定了格位就一定要停在那儿；只有该格位被图标占了才在允许范围内微调
            if (icons != null && icons.Count > 0 && Clashes(px.X, px.Y, m, icons))
            {
                int slotLimit = maxNudge <= 0 ? 0 : Math.Max(1, maxNudge / LayoutMetrics.IconStepX);
                var free = FindFreeNear(px.X, px.Y, m, icons, slotLimit);
                if (free.HasValue)
                {
                    Program.Log("[位置] 该格位被占用，在 " + slotLimit + " 格内挪到 ("
                                + free.Value.X + "," + free.Value.Y + ")");
                    px = free.Value;
                }
                else
                {
                    Program.Log("[位置] 该格位被占用，且 " + slotLimit + " 格内无空位 —— 保持不动，仅警告");
                }
            }

            p.X = px.X; p.Y = px.Y;
            p.Col = col; p.Row = row;
        }

        /// <summary>
        /// 挑一个空闲格位。
        /// 位置是离散格位，所以避让不需要复杂评分 —— 但排序规则必须"贴边优先"：
        ///   1. 先试同行同列（也就是沿着当前所在的边滑过去），只按让出的格数排；
        ///   2. 同行同列都没有，才允许离开这条边（按到原点的格数距离排）。
        /// 不这样做的话，斜着跳（dc=-3,dr=-3）会被判定为和同行滑 6 格"一样近"，
        /// 于是"右下角"会跳到屏幕中下部 —— 实测就是这样跑偏的。
        /// </summary>
        static Point? FindFreeNear(int x, int y, LayoutMetrics m,
                                   List<KeyValuePair<string, Rectangle>> icons, int maxNudgeSlots)
        {
            var here = LayoutMetrics.PixelsToGrid(x, y);
            var cap = LayoutMetrics.GridCapacity(m);
            if (maxNudgeSlots <= 0) maxNudgeSlots = 0;

            var cands = new List<(int onEdge, int cost, int col, int row)>();

            for (int dc = -maxNudgeSlots; dc <= maxNudgeSlots; dc++)
            {
                for (int dr = -maxNudgeSlots; dr <= maxNudgeSlots; dr++)
                {
                    if (dc == 0 && dr == 0) continue;
                    int col = here.X + dc, row = here.Y + dr;
                    if (col < 0 || row < 0 || col >= cap.X || row >= cap.Y) continue;

                    // 是否还在原来那条边上（同行或同列）
                    int onEdge = (dc == 0 || dr == 0) ? 0 : 1;
                    int cost = onEdge == 0 ? Math.Abs(dc) + Math.Abs(dr)   // 沿边滑：让出几格
                                           : Math.Max(Math.Abs(dc), Math.Abs(dr)) * 100; // 离开边：重罚
                    cands.Add((onEdge, cost, col, row));
                }
            }

            // 原地也要参与比较（可能它自己就空着）
            if (here.X >= 0 && here.Y >= 0 && here.X < cap.X && here.Y < cap.Y)
                cands.Add((0, 0, here.X, here.Y));

            cands.Sort((a, b) =>
            {
                int c = a.onEdge.CompareTo(b.onEdge);
                if (c != 0) return c;
                c = a.cost.CompareTo(b.cost);
                if (c != 0) return c;
                c = a.row.CompareTo(b.row);
                return c != 0 ? c : a.col.CompareTo(b.col);
            });

            foreach (var c in cands)
            {
                var px = LayoutMetrics.GridToPixels(c.col, c.row);
                if (!Clashes(px.X, px.Y, m, icons)) return px;
            }
            return null;
        }

        static void SyncGridFromPixels(PositionSection p)
        {
            var gp = LayoutMetrics.PixelsToGrid(p.X, p.Y);
            p.Col = gp.X; p.Row = gp.Y;
        }

        static bool Clashes(int x, int y, LayoutMetrics m, List<KeyValuePair<string, Rectangle>> icons)
        {
            var box = new Rectangle(x - m.Bleed, y - m.Bleed, m.W + m.Bleed * 2, m.H + m.Bleed * 2);
            foreach (var kv in icons)
                if (kv.Value.IntersectsWith(box)) return true;
            return false;
        }

        /// <summary>
        /// 在桌面图标网格的格位里找离给定点最近的**空闲**位置。
        /// within 是允许挪动的最大距离 —— 超出就返回 null，让调用方停在原地。
        /// 这条限制很关键：桌面图标多的时候，"完全不碰任何图标"的位置可能在天边，
        /// 无限制地找就会把用户明确放下的宫格搬到几百像素外。
        /// </summary>
        Point? FindNearestFreeSlot(int x, int y, int within)
        {
            var icons = ReadDesktopIcons();
            if (icons == null) return null;

            var wa = Screen.PrimaryScreen.WorkingArea;
            var cands = new List<Point>();
            for (int cx = wa.Left; cx + _m.W <= wa.Right; cx += LayoutMetrics.IconStepX)
                for (int cy = wa.Top; cy + _m.H <= wa.Bottom; cy += LayoutMetrics.IconStepY)
                    cands.Add(new Point(cx, cy));

            cands.Sort((a, b) =>
            {
                long da = (long)(a.X - x) * (a.X - x) + (long)(a.Y - y) * (a.Y - y);
                long db = (long)(b.X - x) * (b.X - x) + (long)(b.Y - y) * (b.Y - y);
                return da.CompareTo(db);
            });

            foreach (var c in cands)
            {
                if (within > 0)
                {
                    double d = Math.Sqrt((double)(c.X - x) * (c.X - x) + (double)(c.Y - y) * (c.Y - y));
                    if (d > within) return null;   // 已按距离排序，后面的只会更远
                }

                var box = FootprintBox(c.X, c.Y);
                bool clash = false;
                foreach (var kv in icons)
                    if (kv.Value.IntersectsWith(box)) { clash = true; break; }
                if (!clash) return c;
            }
            return null;
        }

        #endregion

        #region 绘制

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            RenderCells(g, _m, _grp.Items, _cfg.Style, _m.CellRect,
                        _icons, _iconContent, _hoverCell, _hoverRing, DrawOutlinedText);

            if (_hoverRing)
            {
                using (var path = Round(new Rectangle(0, 0, _m.W - 1, _m.H - 1), 8))
                using (var pen = new Pen(Color.FromArgb(235, 255, 255, 255), 2f))
                    g.DrawPath(pen, path);
            }
        }

        /// <summary>
        /// 把一组程序画进任意 Graphics。
        ///
        /// 抽成静态函数的目的：覆盖层的 OnPaint 与管理器的「预计效果」预览
        /// **走同一条绘制路径**，这样预览才真正是"所见即所得"——
        /// 而不是在管理器里另写一套近似逻辑（那样迟早会不一致）。
        /// </summary>
        public static void RenderCells(
            Graphics g, LayoutMetrics m, List<GroupItem> items, StyleSection st,
            Func<int, Rectangle> cellRect,
            Icon[] icons, Rectangle[] contents,
            int hoverCell, bool hoverRing,
            Action<Graphics, string, Rectangle, Color> drawText)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            for (int i = 0; i < m.Cols * m.Rows; i++)
            {
                if (items == null || i >= items.Count) continue;
                var item = items[i];
                if (item == null) continue;

                // 空置的格子**什么都不画** —— 底色、边框、图标、文字全部跳过，整块透出桌面。
                // 之前是"空格子只画底板"，于是 2×2 里空的那两格仍然顶着两块半透明底，
                // 看着像四个格子却只有两个能用。现在没放程序的位置就是彻底不存在。
                // 注意：窗口区域（SetWindowRgn）仍然覆盖整块，这样最外圈的拖动环在哪都能拖。
                if (string.IsNullOrWhiteSpace(item.Path)) continue;

                var cell = cellRect(i);
                bool hot = i == hoverCell && !hoverRing;

                // 底板颜色/透明度：单项覆盖总配置
                Color plate = item.BgColor != null && item.BgColor.Length >= 3
                    ? ToColorSafe(item.BgColor, 255)
                    : ToColorSafe(st.PlateColor, 255);
                int alpha = item.BgAlpha ?? st.PlateAlpha;
                if (hot) alpha = st.PlateAlphaHover;

                using (var path = Round(cell, 5))
                {
                    using (var br = new LinearGradientBrush(
                        Rectangle.Inflate(cell, 2, 2),
                        WithAlpha(plate, (int)(alpha * 1.15)),
                        WithAlpha(plate, (int)(alpha * 0.55)), 90f))
                        g.FillPath(br, path);

                    int ba = hot ? Math.Min(255, st.BorderAlpha + 90) : st.BorderAlpha;
                    using (var pen = new Pen(WithAlpha(ToColorSafe(st.BorderColor, 255), ba), hot ? 1.2f : 1f))
                        g.DrawPath(pen, path);
                }

                int blockH = m.IconSize + 1 + m.LabelH;
                int top = cell.Y + Math.Max(0, (cell.Height - blockH) / 2);
                var iconBox = new Rectangle(cell.X + (cell.Width - m.IconSize) / 2, top,
                                            m.IconSize, m.IconSize);
                var labelBox = new Rectangle(cell.X, iconBox.Bottom + 1, cell.Width, m.LabelH);

                var ic = (icons != null && i < icons.Length) ? icons[i] : null;
                if (ic == null) ic = Native.LoadShellIcon(item.Icon ?? item.Path);

                if (ic != null)
                {
                    var ct = (contents != null && i < contents.Length) ? contents[i] : Rectangle.Empty;
                    if (ct.Width <= 0) ct = new Rectangle(0, 0, ic.Width, ic.Height);
                    DrawIconTight(g, ic, iconBox, ct);
                }

                Color text = hot ? ToColorSafe(st.TextColorHover, 255) : ToColorSafe(st.TextColor, 255);
                drawText(g, item.Caption ?? "", labelBox, text);
            }
        }

        internal static Color ToColorSafe(int[] rgb, int alpha)
        {
            if (rgb == null || rgb.Length < 3) return Color.White;
            return Color.FromArgb(Math.Max(0, Math.Min(255, alpha)),
                                  Math.Max(0, Math.Min(255, rgb[0])),
                                  Math.Max(0, Math.Min(255, rgb[1])),
                                  Math.Max(0, Math.Min(255, rgb[2])));
        }

        internal static Color WithAlpha(Color c, int alpha) =>
            Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), c);

        /// <summary>
        /// 小字在壁纸上容易糊。这里提供两种补对比度的手法，**可以单独用，也可以叠加**：
        ///
        ///   · 投影（ShadowOffset > 0）—— Windows 画桌面图标标签用的就是这个。
        ///     只在右下方向垫一份暗色副本，字的形状完整保留，只在一侧补对比度。
        ///   · 描边（OutlineWidth > 0）—— 四/八个方向各画一遍，等于给字围一圈暗色。
        ///     四周都有边，边缘更"实"；代价是把笔画撑胖、把字腔挤死，
        ///     7pt 这种小字号下尤其明显，"搜索""磁盘"这类笔画密的字容易被填满。
        ///
        /// 两个都开会按「投影 → 描边 → 正文」的顺序叠，投影在最底层。
        /// 这样一来既有投影的对比度，又有描边的边缘定义，且描边只压一圈、不至于太糊。
        ///
        /// 中心那一遍永远是真正的正文，其余都是衬托。
        ///
        /// 【为什么整段先画到离屏位图再贴】
        /// 直接画到窗口 DC 上时，GDI 会用 ClearType 做次像素渲染 —— 笔画边缘带上
        /// 黄/青/品红的彩边。7pt 的中文本来就小，这些彩边糊在笔画上反而更难认
        /// （放大 10 倍看非常明显）。
        ///
        /// 而画到 32bpp 带 Alpha 的位图上时，GDI 没法做次像素渲染，会自动退化成
        /// **灰度抗锯齿**，彩边就没了。绕这一圈的好处是 TextRenderer 的字符度量、
        /// 断行、省略号行为全都保持不变，只是颜色干净了。
        /// </summary>
        void DrawOutlinedText(Graphics g, string text, Rectangle box, Color color)
        {
            if (string.IsNullOrEmpty(text)) return;

            const TextFormatFlags flags =
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix;

            int so = _cfg.Style.ShadowOffset;
            int sa = _cfg.Style.ShadowAlpha;
            int ow = _cfg.Style.OutlineWidth;
            int oa = _cfg.Style.OutlineAlpha;

            // 投影和描边都会画到正文框外面去，离屏位图得留出余量
            int pad = Math.Max(so, ow);
            int bw = box.Width + pad * 2;
            int bh = box.Height + pad * 2;
            if (bw <= 0 || bh <= 0) return;

            using (var bmp = new Bitmap(bw, bh, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var bg = Graphics.FromImage(bmp))
                {
                    bg.Clear(Color.Transparent);

                    // ★ 关键的一行：强制灰度抗锯齿。
                    // 不设它的话，TextRenderer 在带 Alpha 的位图上会走 ClearType，
                    // 笔画末端出现黄/青/品红的彩边 —— 白字尤其明显（实测白字 148 个彩色像素、
                    // 最大 RGB 极差 187；设成 AntiAliasGridFit 之后是 0 个、极差 4）。
                    // 试过 SingleBitPerPixelGridFit 和换成不透明的 32bppRgb，都没用。
                    bg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                    var inner = new Rectangle(pad, pad, box.Width, box.Height);

                    // 1) 投影：从最远的一层往里画，最后一层紧贴正文，边缘才不会发虚
                    if (so > 0 && sa > 0)
                    {
                        int[] rgb = _cfg.Style.ShadowColor != null && _cfg.Style.ShadowColor.Length >= 3
                            ? _cfg.Style.ShadowColor
                            : _cfg.Style.OutlineColor;
                        Color sc = ToColorSafe(rgb, sa);
                        for (int i = so; i >= 1; i--)
                            TextRenderer.DrawText(bg, text, _labelFont,
                                new Rectangle(inner.X + i, inner.Y + i, inner.Width, inner.Height), sc, flags);
                    }

                    // 2) 描边：压在投影上面
                    if (ow > 0 && oa > 0)
                    {
                        Color oc = ToColorSafe(_cfg.Style.OutlineColor, oa);
                        // 四方向 + 四对角，描边更均匀
                        for (int dy = -ow; dy <= ow; dy += ow)
                            for (int dx = -ow; dx <= ow; dx += ow)
                            {
                                if (dx == 0 && dy == 0) continue;
                                TextRenderer.DrawText(bg, text, _labelFont,
                                    new Rectangle(inner.X + dx, inner.Y + dy, inner.Width, inner.Height), oc, flags);
                            }
                    }

                    // 3) 正文
                    TextRenderer.DrawText(bg, text, _labelFont, inner, color, flags);
                }

                // 贴回原处。用 Pixel 单位的显式矩形，避免 GDI+ 按 DPI 重采样
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(bmp,
                    new Rectangle(box.X - pad, box.Y - pad, bw, bh),
                    new Rectangle(0, 0, bw, bh),
                    GraphicsUnit.Pixel);
            }
        }

        static GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = Math.Max(2, rad) * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>量出图标里真正有内容的不透明边界（外壳图标四周可能带透明边距）。</summary>
        static Rectangle TightContent(Icon icon)
        {
            try
            {
                using (var bmp = icon.ToBitmap())
                {
                    int minX = bmp.Width, minY = bmp.Height, maxX = -1, maxY = -1;
                    int step = bmp.Width > 64 ? 2 : 1;
                    for (int y = 0; y < bmp.Height; y += step)
                        for (int x = 0; x < bmp.Width; x += step)
                        {
                            if (bmp.GetPixel(x, y).A < 16) continue;
                            if (x < minX) minX = x;
                            if (y < minY) minY = y;
                            if (x > maxX) maxX = x;
                            if (y > maxY) maxY = y;
                        }
                    if (maxX < 0 || maxY < 0) return new Rectangle(0, 0, bmp.Width, bmp.Height);
                    return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
                }
            }
            catch { return new Rectangle(0, 0, icon.Width, icon.Height); }
        }

        /// <summary>把图标的实际内容等比缩放后填进目标框，尽量占满。</summary>
        static void DrawIconTight(Graphics g, Icon icon, Rectangle box, Rectangle content)
        {
            if (icon == null || content.Width <= 0 || content.Height <= 0) return;

            float s = Math.Min((float)box.Width / content.Width, (float)box.Height / content.Height);
            float w = content.Width * s, h = content.Height * s;
            float dx = box.X + (box.Width - w) / 2f;
            float dy = box.Y + (box.Height - h) / 2f;

            var pm = g.InterpolationMode; var pp = g.PixelOffsetMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            try
            {
                // 用 ToBitmap + DrawImage，而不是 g.DrawIcon。
                //
                // 两个原因：
                //  1) DrawIcon 会把**整个**图标缩放到目标矩形，传进去的源范围没人理 ——
                //     所以上面辛苦算出来的 content 紧边界其实一直是白算的，
                //     图标四周自带的透明边距照旧占着地方，内容看着比实际小一圈。
                //  2) DrawIcon 对带 Alpha 的图标处理不可靠，颜色可能失真。
                //     DrawImage 显式给源矩形，缩放和透明都走 GDI+ 的正常路径。
                using (var bmp = icon.ToBitmap())
                {
                    g.DrawImage(bmp,
                        new Rectangle((int)Math.Round(dx), (int)Math.Round(dy),
                                      (int)Math.Round(w), (int)Math.Round(h)),
                        content,
                        GraphicsUnit.Pixel);
                }
            }
            finally { g.InterpolationMode = pm; g.PixelOffsetMode = pp; }
        }

        #endregion

        #region 鼠标

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging)
            {
                Native.GetCursorPos(out var cp);
                MoveTo(_dragWinOrigin.X + (cp.X - _dragGrabScreen.X),
                       _dragWinOrigin.Y + (cp.Y - _dragGrabScreen.Y));
                return;
            }

            int cell = _m.CellAt(e.Location);
            int launchCell = _m.HitLaunch(e.Location);
            bool ring = _m.HitRing(e.Location);

            if (cell != _hoverCell || ring != _hoverRing)
            {
                _hoverCell = cell;
                _hoverRing = ring;
                Cursor = ring ? Cursors.SizeAll : (launchCell >= 0 ? Cursors.Hand : Cursors.Default);
                UpdateTooltip(ring ? -1 : launchCell);
                Invalidate();
            }
        }

        void UpdateTooltip(int cell)
        {
            if (!_cfg.Behaviour.ShowTooltip || cell < 0 || cell >= _grp.Items.Count)
            {
                if (_tip.Active) { _tip.Active = false; _tip.SetToolTip(this, ""); }
                return;
            }
            var it = _grp.Items[cell];
            _tip.SetToolTip(this, (it.Caption ?? "") +
                (string.IsNullOrEmpty(it.Path) ? "" : "\n" + it.Path));
            _tip.Active = true;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_dragging) return;
            _hoverCell = -1; _hoverRing = false;
            Cursor = Cursors.Default;
            UpdateTooltip(-1);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            // 右键：先记下点在哪一格，菜单弹出时会按这一格重建条目
            if (e.Button == MouseButtons.Right)
            {
                _menuCell = _m.HitLaunch(e.Location);
                return;
            }

            if (e.Button != MouseButtons.Left) return;
            // 唯一拖动触发点：整个网格最外圈的环。内部留给双击，绝不冲突。
            if (!_m.HitRing(e.Location)) return;

            Native.GetCursorPos(out var cp);
            _dragGrabScreen = new Point(cp.X, cp.Y);
            _dragWinOrigin = new Point(_grp.Position.X, _grp.Position.Y);
            _dragging = true;
            Program.Log("[拖动开始] 窗口=(" + _grp.Position.X + "," + _grp.Position.Y + ")");
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            SettleAndSave();
            Invalidate();
        }

        public void SettleAndSave()
        {
            if (_cfg.Behaviour.SnapToIconGrid)
            {
                var snapped = LayoutMetrics.SnapToIconGrid(_grp.Position.X, _grp.Position.Y);
                _grp.Position.X = snapped.X;
                _grp.Position.Y = snapped.Y;
            }
            ClampToWorkArea(_grp.Position, _m);

            if (_cfg.Behaviour.AvoidDesktopIcons)
            {
                var hits = FindIconCollisions();
                if (hits.Count > 0)
                {
                    int within = _cfg.Behaviour.MaxNudgeDistance;
                    Program.Log("[拖动] 落点 (" + _grp.Position.X + "," + _grp.Position.Y
                                + ") 压住 " + hits.Count + " 个图标，在 " + within + "px 内找空位…");
                    var nudged = FindNearestFreeSlot(_grp.Position.X, _grp.Position.Y, within);
                    if (nudged.HasValue)
                    {
                        Program.Log("[拖动] 附近有空位，挪到 (" + nudged.Value.X + "," + nudged.Value.Y + ")");
                        _grp.Position.X = nudged.Value.X;
                        _grp.Position.Y = nudged.Value.Y;
                    }
                    else
                    {
                        // 附近没有空位 —— 尊重你的落点，不搬到天边去，只报警告
                        Program.Log("[拖动] " + within + "px 内没有空位，保持你的位置不动（仅警告）");
                    }
                }
            }

            ApplyGeometry();
            SyncGridFromPixels(_grp.Position);   // 网格坐标是首选记录方式
            Program.Log("[拖动结束] 最终位置 (" + _grp.Position.X + "," + _grp.Position.Y + ")"
                        + " = 网格 col=" + _grp.Position.Col + " row=" + _grp.Position.Row);
            ReportCollisions();
            Program.SavePosition(_groupPath, _grp);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left) return;

            int i = _m.HitLaunch(e.Location);
            var it = ItemAt(i);
            Program.Log("[DBLCLICK] 格子=" + i + " -> " + (it?.Caption ?? "空"));

            if (!_cfg.Behaviour.DoubleClickToLaunch) return;   // 配置成单击时，双击不再重复启动
            if (it == null || string.IsNullOrEmpty(it.Path)) return;
            Program.Launch(it);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            if (_cfg.Behaviour.DoubleClickToLaunch) return;

            int i = _m.HitLaunch(e.Location);
            var it = ItemAt(i);
            if (it == null || string.IsNullOrEmpty(it.Path)) return;
            Program.Log("[CLICK] 格子=" + i + " -> " + (it.Caption ?? ""));
            Program.Launch(it);
        }

        public void FireSyntheticDoubleClick(Point p) =>
            OnMouseDoubleClick(new MouseEventArgs(MouseButtons.Left, 2, p.X, p.Y, 0));

        public string FireProbeHit(Point p)
        {
            bool ring = _m.HitRing(p);
            int cell = _m.HitLaunch(p);
            return "点(" + p.X + "," + p.Y + ") -> " + (ring ? "拖动环" : ("启动区格" + cell));
        }

        public string FireSyntheticDrag(int dx, int dy)
        {
            var grab = new Point(1, 1);
            int bx = _grp.Position.X, by = _grp.Position.Y;
            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, grab.X, grab.Y, 0));
            if (!_dragging) return "失败：外圈没有触发拖动";
            MoveTo(bx + dx, by + dy);
            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, grab.X, grab.Y, 0));

            bool moved = _grp.Position.X != bx || _grp.Position.Y != by;
            return "拖动 (" + bx + "," + by + ") -> 目标(" + (bx + dx) + "," + (by + dy)
                   + ") -> 落点(" + _grp.Position.X + "," + _grp.Position.Y + ")  移动=" + moved;
        }

        #endregion

        #region 右键菜单

        ContextMenuStrip _menu;
        int _menuCell = -1;   // 右键时命中的格子下标；-1 = 没落在格子上

        public void BuildContextMenu()
        {
            _menu = new ContextMenuStrip();
            // 菜单内容随"右键点在哪一格"变，所以每次弹出前重建
            _menu.Opening += (s, e) => RebuildMenuItems();
            ContextMenuStrip = _menu;
        }

        void RebuildMenuItems()
        {
            var menu = _menu;
            if (menu == null) return;
            menu.Items.Clear();

            // ── 当前格子 ──
            // 右键落在某个格子上、且那一格有程序，就按"像正常快捷方式一样"给它一套操作。
            var cur = ItemAt(_menuCell);
            var curPath = ResolveExistingPath(cur);
            if (curPath != null)
            {
                string name = string.IsNullOrWhiteSpace(cur.Caption)
                    ? Path.GetFileName(curPath) : cur.Caption;

                var open = new ToolStripMenuItem("打开 " + name, null, (s, e) => Program.Launch(cur));
                open.Font = new Font(menu.Font, FontStyle.Bold);
                menu.Items.Add(open);

                menu.Items.Add("打开文件位置", null, (s, e) => RevealInExplorer(curPath));
                menu.Items.Add("属性", null, (s, e) => ShowFileProperties(curPath));
                menu.Items.Add(new ToolStripSeparator());
            }

            // ── 位置 ──
            // 位置只由手动拖动决定。这里只提供"记住当前位置"，不再有自动角落。
            var posMenu = new ToolStripMenuItem("位置");
            posMenu.DropDownItems.Add("记住当前位置（写入配置文件）", null, (s, e) => RememberPosition());
            menu.Items.Add(posMenu);

            // ── 配置 ──
            menu.Items.Add("用 WinQuad 管理器打开配置", null, (s, e) => OpenManager());
            menu.Items.Add("编辑本宫格内容（" + Path.GetFileName(_groupPath) + "）",
                null, (s, e) => OpenPath(_groupPath));
            menu.Items.Add("编辑总配置（config.json）", null, (s, e) => OpenPath(Program.ConfigPath));
            menu.Items.Add("打开配置所在文件夹", null, (s, e) => OpenPath(AppContext.BaseDirectory));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("重新载入全部配置", null, (s, e) => Program.ReloadAll());
            menu.Items.Add("打开桌面文件夹", null, (s, e) =>
                OpenPath(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)));
            menu.Items.Add("打开日志", null, (s, e) => OpenPath(Program.LogPath));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出 WinQuad", null, (s, e) => Application.Exit());
        }

        /// <summary>
        /// 取这一格真正能操作的文件路径。
        /// .url / 命令行的条目不能直接拿去"打开文件位置"或"属性"，所以只认存在的文件；
        /// 路径不存在（程序被卸载/挪走）就返回 null，菜单里那三项干脆不出现。
        /// </summary>
        static string ResolveExistingPath(GroupItem it)
        {
            if (it == null || string.IsNullOrWhiteSpace(it.Path)) return null;
            try { return File.Exists(it.Path) ? it.Path : null; }
            catch { return null; }
        }

        /// <summary>在资源管理器里定位到这个文件（选中状态），等价于快捷方式的「打开文件位置」。</summary>
        static void RevealInExplorer(string path)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + path + "\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { Program.Log("[打开文件位置失败] " + path + " : " + ex.Message); }
        }

        /// <summary>
        /// 弹系统的文件属性对话框 —— 和资源管理器右键「属性」是同一个。
        /// 必须是 STA 线程；覆盖层本来就是 [STAThread]，没问题。
        /// </summary>
        static void ShowFileProperties(string path)
        {
            try
            {
                var sei = new Native.SHELLEXECUTEINFO();
                sei.cbSize = Marshal.SizeOf(typeof(Native.SHELLEXECUTEINFO));
                sei.fMask = 0x0000000C;   // SEE_MASK_INVOKEIDLIST：要 Shell 的详细属性页
                sei.lpVerb = "properties";
                sei.lpFile = path;
                sei.nShow = 5;            // SW_SHOW
                if (!Native.ShellExecuteEx(ref sei))
                    Program.Log("[属性] 打开失败 " + path + " err=" + Marshal.GetLastWin32Error());
            }
            catch (Exception ex) { Program.Log("[属性] 异常 " + path + " : " + ex.Message); }
        }

        /// <summary>启动管理器（和覆盖层同目录的 WinQuad.Manager.exe）。</summary>
        static void OpenManager()
        {
            try
            {
                string exe = Path.Combine(AppContext.BaseDirectory, "WinQuad.Manager.exe");
                if (!File.Exists(exe))
                {
                    Program.Log("[管理器] 找不到 " + exe);
                    MessageBox.Show("同目录下找不到 WinQuad.Manager.exe。\n\n" + exe,
                        "WinQuad", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                { FileName = exe, WorkingDirectory = AppContext.BaseDirectory, UseShellExecute = true });
            }
            catch (Exception ex) { Program.Log("[管理器] 启动失败: " + ex.Message); }
        }

        /// <summary>把当前位置（网格坐标）写进配置文件，让下次启动停在同一格。</summary>
        void RememberPosition()
        {
            SyncGridFromPixels(_grp.Position);
            _grp.Position.Anchor = null;
            Program.SavePosition(_groupPath, _grp);
            Program.Log("[菜单] 已记住当前位置 col=" + _grp.Position.Col + " row=" + _grp.Position.Row);
        }

        static void OpenPath(string path)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                { FileName = path, UseShellExecute = true });
            }
            catch (Exception ex) { Program.Log("[打开失败] " + path + " : " + ex.Message); }
        }

        #endregion

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var ic in _icons) ic?.Dispose();
                _labelFont?.Dispose();
                _tip.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal static class Program
    {
        public static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "winquad.log");
        public static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "config.json");

        static readonly object LogLock = new object();
        static readonly List<GridForm> Forms = new List<GridForm>();

        static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,          // 手写 json 时末尾多个逗号不该报错
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping   // 中文不转义
        };

        public static void Log(string msg)
        {
            try
            {
                lock (LogLock)
                    File.AppendAllText(LogPath,
                        DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }

        public static T LoadJson<T>(string path) where T : class
        {
            try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOpts); }
            catch (Exception ex) { Log("配置读取失败 " + path + " : " + ex.Message); return null; }
        }

        public static void SaveJson<T>(string path, T obj)
        {
            try { File.WriteAllText(path, JsonSerializer.Serialize(obj, JsonOpts), new UTF8Encoding(false)); }
            catch (Exception ex) { Log("配置写入失败 " + path + " : " + ex.Message); }
        }

        /// <summary>
        /// 取图标。want 为空时按 path 自动解析：
        ///   .lnk -> 解析到目标 .exe -> ExtractIconEx（这样没有快捷方式小箭头）
        ///   .exe/.dll/.ico/.png -> 直接取
        /// </summary>
        public static Icon LoadIconFor(string want, string itemPath, out string how)
        {
            // 1) 显式指定了自定义图标
            if (!string.IsNullOrWhiteSpace(want))
            {
                string w = Environment.ExpandEnvironmentVariables(want.Trim());
                if (File.Exists(w))
                {
                    string ext = Path.GetExtension(w).ToLowerInvariant();
                    if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp")
                    {
                        try
                        {
                            using (var bmp = new Bitmap(w))
                                how = "自定义图片 " + Path.GetFileName(w);
                            // Bitmap 需要包成 Icon，这里直接返回一个带位图的图标不便，
                            // 改为返回 null 让调用方用图片路径；简化处理：转成 Icon
                            IntPtr h = new Bitmap(w).GetHicon();
                            try { return (Icon)Icon.FromHandle(h).Clone(); }
                            finally { Native.DestroyIcon(h); }
                        }
                        catch (Exception ex) { Log("  自定义图片读取失败 " + w + " : " + ex.Message); }
                    }
                    else
                    {
                        var ic = Native.ExtractIconAt(w, 0) ?? Native.LoadShellIcon(w);
                        if (ic != null) { how = "自定义图标 " + Path.GetFileName(w); return ic; }
                        Log("  自定义图标提取失败 " + w);
                    }
                }
                else Log("  自定义图标不存在，回退自动解析：" + w);
            }

            if (string.IsNullOrWhiteSpace(itemPath)) { how = "无路径"; return null; }

            string p = Environment.ExpandEnvironmentVariables(itemPath.Trim());
            string e = Path.GetExtension(p).ToLowerInvariant();

            // 2) .lnk —— 解析到真目标，避免快捷方式小箭头
            if (e == ".lnk")
            {
                string target = ShortcutResolver.Resolve(p);
                if (!string.IsNullOrEmpty(target) && File.Exists(target))
                {
                    var ic = Native.ExtractIconAt(target, 0) ?? Native.LoadShellIcon(target);
                    if (ic != null)
                    {
                        how = "解析快捷方式 -> " + Path.GetFileName(target) + "（无小箭头）";
                        return ic;
                    }
                }
                // 解析不出来就只能用外壳图标（会带小箭头）
                how = "快捷方式（解析失败，可能带小箭头）" + Path.GetFileName(p);
                return Native.LoadShellIcon(p);
            }

            // 3) 本身就是可提取图标的文件
            if (e == ".exe" || e == ".dll" || e == ".ico")
            {
                var ic = Native.ExtractIconAt(p, 0) ?? Native.LoadShellIcon(p);
                how = Path.GetFileName(p);
                return ic;
            }

            // 4) 其他（.url / .bat 等）只能靠外壳
            how = "外壳图标 " + Path.GetFileName(p);
            return Native.LoadShellIcon(p);
        }

        public static void Launch(GroupItem it)
        {
            if (Environment.GetEnvironmentVariable("WINQUAD_SAFEMODE") == "1")
            {
                Log("[SAFEMODE] 未启动：" + it.Path);
                return;
            }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                { FileName = Environment.ExpandEnvironmentVariables(it.Path), UseShellExecute = true });
                Log("[LAUNCH] " + (it.Caption ?? "") + "  " + it.Path);
            }
            catch (Exception ex) { Log("[LAUNCH FAIL] " + (it.Caption ?? "") + " : " + ex.Message); }
        }

        /// <summary>
        /// 拖动后把坐标写回该宫格自己的 json。
        /// 这里刻意**不做反序列化再序列化**：那会把 "_" 注释全部丢掉、键名也会变成
        /// PascalCase，而注释正是这些配置文件的价值所在（方便发给他人或 AI 改）。
        /// 改为只替换 x / y 两个数字、清空 anchor，文件其余部分一个字节都不动。
        /// </summary>
        public static void SavePosition(string groupPath, GroupFile g)
        {
            try
            {
                if (!File.Exists(groupPath)) { Log("[保存位置] 找不到 " + groupPath); return; }
                string raw = File.ReadAllText(groupPath);
                string original = raw;

                // 网格坐标是首选记录方式；x/y 也一并写回，方便人肉核对
                raw = ReplaceNumber(raw, "col", g.Position.Col);
                raw = ReplaceNumber(raw, "row", g.Position.Row);
                raw = ReplaceNumber(raw, "x", g.Position.X);
                raw = ReplaceNumber(raw, "y", g.Position.Y);
                raw = System.Text.RegularExpressions.Regex.Replace(
                    raw, "(\"anchor\"\\s*:\\s*)\"[^\"]*\"", "$1null");

                if (raw == original) { Log("[保存位置] 内容无变化，未写入"); return; }

                File.WriteAllText(groupPath, raw, new UTF8Encoding(false));
                Log("[保存位置] 网格 col=" + g.Position.Col + " row=" + g.Position.Row
                    + " (像素 " + g.Position.X + "," + g.Position.Y + ") -> "
                    + Path.GetFileName(groupPath) + "（已保留注释）");
            }
            catch (Exception ex) { Log("[保存位置失败] " + ex.Message); }
        }

        /// <summary>只替换 "key": 数字 里的数字，保留缩进、注释、键名大小写。</summary>
        static string ReplaceNumber(string json, string key, int value)
        {
            return System.Text.RegularExpressions.Regex.Replace(
                json,
                "(\"" + System.Text.RegularExpressions.Regex.Escape(key) + "\"\\s*:\\s*)-?\\d+",
                "${1}" + value,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        /// <summary>右键「重新载入全部配置」：重建所有窗口，改完 json 不必重启。</summary>
        public static void ReloadAll()
        {
            Log("[重载] 开始重新载入全部配置");
            var old = Forms.ToArray();
            Forms.Clear();
            foreach (var f in old)
            {
                try { f.Hide(); f.Dispose(); } catch { }
            }
            Start(Array.Empty<string>());
            Log("[重载] 完成");
        }

        static void BuildWindows(AppConfig cfg)
        {
            foreach (var g in cfg.Groups)
            {
                if (string.IsNullOrWhiteSpace(g.GroupFile)) continue;
                string path = Path.Combine(AppContext.BaseDirectory, g.GroupFile);
                var grp = LoadJson<GroupFile>(path);
                if (grp?.Items == null || grp.Items.Count == 0)
                {
                    Log("跳过空宫格 " + g.GroupFile);
                    continue;
                }

                GridForm.ResolvePosition(grp, LayoutMetrics.From(cfg.Size), cfg.Behaviour.MaxNudgeDistance);
                var f = new GridForm(cfg, grp, path);
                string tag = Path.GetFileName(path);
                f.HandleCreated += (s, e) => Program.Log("[生命周期] " + tag + " HandleCreated");
                f.Shown += (s, e) =>
                {
                    Program.Log("[生命周期] " + tag + " Shown 触发，开始嵌入");
                    if (!f.EmbedIntoDesktop())
                    {
                        Program.Log("[生命周期] " + tag + " 嵌入失败");
                        return;
                    }
                    Program.Log("[生命周期] " + tag + " 嵌入成功");
                    f.BeginInvoke(new Action(() =>
                    {
                        f.ApplyGeometry();
                        var t = new Timer { Interval = 800 };
                        t.Tick += (a, b) => { t.Stop(); t.Dispose(); f.ApplyGeometry(); f.ReportCollisions(); };
                        t.Start();
                    }));
                };
                Forms.Add(f);
            }
            Log("窗口数=" + Forms.Count);
        }

        [STAThread]
        static void Main(string[] args)
        {
            bool wantOverlay = false;
            bool selfTest = false;
            bool detect = false;
            foreach (var a in args)
            {
                if (string.Equals(a, "--overlay", StringComparison.OrdinalIgnoreCase)) wantOverlay = true;
                if (string.Equals(a, "--detect", StringComparison.OrdinalIgnoreCase)) detect = true;
                if (a.StartsWith("--selftest", StringComparison.OrdinalIgnoreCase)) selfTest = true;
            }

            // 探测器：实测桌面图标网格间距并写进 config.json。
            // 由管理器「刷新行列距」按钮调用；不常驻、不开窗口、立即退出。
            if (detect) { DetectGridStep(); return; }

            // 管理器（WinQuad.Manager.exe）负责启动覆盖层并传 --overlay。
            // 没有参数直接双击时不做任何事，避免"点一下桌面上就冒出个东西"的困惑。
            if (!wantOverlay && !selfTest)
            {
                MessageBox.Show(
                    "WinQuad 是桌面四宫格的覆盖层，需要用参数启动。\n\n" +
                    "要配置和启动它，请运行同目录下的：WinQuad.Manager.exe\n\n" +
                    "（管理器里有「启动覆盖层 / 停止覆盖层 / 重启覆盖层」按钮）",
                    "WinQuad", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Start(args);
        }

        /// <summary>
        /// 实测桌面图标网格间距。
        /// 不读注册表 —— 本机注册表 IconVerticalSpacing 换算成 90px，而实测是 114px，
        /// 因为实际行距还取决于图标尺寸。所以直接量图标的真实坐标，取相邻行列的最小差值。
        /// 结果写进 config.json 的 behaviour.gridStepX / gridStepY。
        /// </summary>
        static void DetectGridStep()
        {
            try { File.Delete(LogPath); } catch { }
            Log("=== 网格间距探测 ===");

            var cfg = LoadJson<AppConfig>(ConfigPath);
            if (cfg == null) { Log("读不到 config.json"); return; }

            var icons = GridForm.ReadDesktopIcons();
            if (icons == null || icons.Count < 2)
            {
                Log("读不到桌面图标位置（数量=" + (icons?.Count ?? 0) + "），保持原值");
                return;
            }

            int stepX = DetectStep(icons.Select(kv => kv.Value.X).ToList());
            int stepY = DetectStep(icons.Select(kv => kv.Value.Y).ToList());

            if (stepX <= 0) stepX = LayoutMetrics.IconStepX;
            if (stepY <= 0) stepY = LayoutMetrics.IconStepY;

            Log("探测结果：横向 " + stepX + "px  纵向 " + stepY + "px"
                + "  （旧值 " + cfg.Behaviour.GridStepX + " / " + cfg.Behaviour.GridStepY + "）");
            Log("参考：注册表 IconSpacing=" + ReadRegPx("IconSpacing")
                + "px  IconVerticalSpacing=" + ReadRegPx("IconVerticalSpacing") + "px（通常与实际不符）");

            cfg.Behaviour.GridStepX = stepX;
            cfg.Behaviour.GridStepY = stepY;
            SaveConfigPreservingComments(cfg, stepX, stepY);

            // 给管理器回读的结果文件（管理器读不到控制台输出，靠这个文件通信）
            try
            {
                string probe = Path.Combine(AppContext.BaseDirectory, "spacing-detect.json");
                File.WriteAllText(probe,
                    "{\r\n" +
                    "  \"stepX\": " + stepX + ",\r\n" +
                    "  \"stepY\": " + stepY + ",\r\n" +
                    "  \"source\": \"实测桌面图标坐标（相邻格位最小差值）\",\r\n" +
                    "  \"screen\": \"" + Screen.PrimaryScreen.Bounds.Width + "x" + Screen.PrimaryScreen.Bounds.Height + "\",\r\n" +
                    "  \"workArea\": \"" + Screen.PrimaryScreen.WorkingArea.Width + "x" + Screen.PrimaryScreen.WorkingArea.Height + "\",\r\n" +
                    "  \"icons\": " + icons.Count + ",\r\n" +
                    "  \"regIconSpacing\": " + ReadRegPx("IconSpacing") + ",\r\n" +
                    "  \"regIconVerticalSpacing\": " + ReadRegPx("IconVerticalSpacing") + "\r\n" +
                    "}\r\n", new UTF8Encoding(false));
                Log("探测结果已写 " + probe);
            }
            catch (Exception ex) { Log("写探测结果失败: " + ex.Message); }

            Log("已写入 " + ConfigPath);
        }

        /// <summary>在坐标系里找相邻两值的最小正差值 —— 那就是网格步长。</summary>
        static int DetectStep(List<int> values)
        {
            var uniq = values.Distinct().OrderBy(v => v).ToList();
            int best = int.MaxValue;
            for (int i = 1; i < uniq.Count; i++)
            {
                int d = uniq[i] - uniq[i - 1];
                if (d > 4 && d < best) best = d;
            }
            return best == int.MaxValue ? -1 : best;
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
                    int raw = int.Parse(v.ToString());
                    return Math.Abs(raw) / 15;
                }
            }
            catch { return -1; }
        }

        /// <summary>
        /// 只改两个数字，保留 config.json 里的全部注释 ——
        /// 与 SavePosition 同样的思路，绝不能反序列化再序列化。
        /// </summary>
        static void SaveConfigPreservingComments(AppConfig cfg, int stepX, int stepY)
        {
            try
            {
                string raw = File.ReadAllText(ConfigPath);
                raw = System.Text.RegularExpressions.Regex.Replace(
                    raw, "(\"gridStepX\"\\s*:\\s*)-?\\d+", "${1}" + stepX);
                raw = System.Text.RegularExpressions.Regex.Replace(
                    raw, "(\"gridStepY\"\\s*:\\s*)-?\\d+", "${1}" + stepY);
                File.WriteAllText(ConfigPath, raw, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log("写回失败，退回整体序列化: " + ex.Message);
                try
                {
                    File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, JsonOpts),
                                      new UTF8Encoding(false));
                }
                catch { }
            }
        }

        static void Start(string[] args)
        {
            if (Forms.Count == 0)
            {
                try { File.Delete(LogPath); } catch { }
                Log("=== WinQuad 启动 pid=" + Environment.ProcessId + " ===");

                var cfg = LoadJson<AppConfig>(ConfigPath);
                if (cfg == null) { Log("读不到总配置 " + ConfigPath); return; }

                // 网格间距必须在使用之前从配置注入，
                // 否则 --detect 探测出的值会被编译期默认值覆盖。
                LayoutMetrics.ApplyGridStep(cfg.Behaviour);
                Log("总配置：" + ConfigPath + "  version=" + cfg.Version);

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

                BuildWindows(cfg);
                if (Forms.Count == 0) { Log("没有任何可用宫格"); return; }
                SetupSelfTest(args, cfg);
            }

            // 必须把**每一个**窗体都显示出来。
            // Application.Run(Forms[0]) 只会显示第一个，其余窗体不会创建窗口句柄，
            // 于是它们的 Shown 事件不触发、也就永远不会被嵌入桌面 ——
            // 症状就是"只有第一个四宫格生效"。嵌入动作统一在各自 Shown 里做，这里不重复。
            foreach (var f in Forms)
            {
                if (!f.Visible) f.Show();
            }

            Application.Run(Forms[0]);
        }

        static void SetupSelfTest(string[] args, AppConfig cfg)
        {
            if (args.Length >= 1 && args[0] == "--selftest-drag")
            {
                var t = new Timer { Interval = 2500 };
                t.Tick += (a, b) =>
                {
                    t.Stop(); t.Dispose();
                    var m = LayoutMetrics.From(cfg.Size);
                    Log("[自检] 网格 " + m.W + "x" + m.H + "  判定框 " + m.Footprint.Width + "x" + m.Footprint.Height
                        + "  拖动环 " + m.RingSize + "px  内区 " + m.InnerRect());
                    Log("  " + Forms[0].FireProbeHit(new Point(0, 0)));
                    Log("  " + Forms[0].FireProbeHit(new Point(m.W - 1, m.H - 1)));
                    for (int i = 0; i < m.Cols * m.Rows; i++)
                    {
                        var c = m.CellRect(i);
                        Log("  " + Forms[0].FireProbeHit(new Point(c.X + c.Width / 2, c.Y + c.Height / 2)));
                    }
                    Log("  " + Forms[0].FireSyntheticDrag(70, -120));
                };
                t.Start();
                var k = new Timer { Interval = 7000 };
                k.Tick += (x, y) => { k.Stop(); k.Dispose(); Log("[自检] 退出"); Application.Exit(); };
                k.Start();
            }

            if (args.Length >= 2 && args[0] == "--selftest")
            {
                int qi = int.Parse(args[1]);
                var t = new Timer { Interval = 2500 };
                t.Tick += (a, b) =>
                {
                    t.Stop(); t.Dispose();
                    var m = LayoutMetrics.From(cfg.Size);
                    var c = m.CellRect(qi);
                    Log("[自检] 模拟双击格子 " + qi);
                    Forms[0].FireSyntheticDoubleClick(new Point(c.X + c.Width / 2, c.Y + c.Height / 2));
                };
                t.Start();
                var k = new Timer { Interval = 6000 };
                k.Tick += (x, y) => { k.Stop(); k.Dispose(); Log("[自检] 退出"); Application.Exit(); };
                k.Start();
            }
        }
    }
}
