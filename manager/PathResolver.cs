// 程序路径自动追溯。
//
// 为什么要做这个：四宫格本来就是为了省桌面空间 —— 用户把程序放进格子之后，
// 桌面上那个快捷方式肯定要删掉。如果配置里存的是 .lnk 的路径，删掉快捷方式
// 就等于把格子弄坏了。所以一律追溯到真正的目标程序。
using System;
using System.IO;
using Microsoft.Win32;

namespace WinQuad.Manager
{
    internal static class PathResolver
    {
        /// <summary>把无扩展名的命令名解析成完整路径（查 PATH 与 App Paths 注册表）。</summary>
        public static string ResolveCommand(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return null;

            // 已经是完整路径
            if (cmd.IndexOf('\\') >= 0 || cmd.IndexOf('/') >= 0)
                return File.Exists(cmd) ? cmd : null;

            string name = cmd;
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";

            // 1) App Paths 注册表（最可靠，能解析出完整路径）
            foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
            {
                foreach (var sub in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\",
                                            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\" })
                {
                    try
                    {
                        using (var k = hive.OpenSubKey(sub + name))
                        {
                            var v = k?.GetValue(null) as string;
                            if (!string.IsNullOrEmpty(v) && File.Exists(v)) return v;
                        }
                    }
                    catch { }
                }
            }

            // 2) PATH 环境变量
            try
            {
                foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    try
                    {
                        string p = Path.Combine(dir.Trim(), name);
                        if (File.Exists(p)) return p;
                    }
                    catch { }
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// 把一项的 Path 尽量追溯成真正的程序文件。
        /// .lnk → 解析目标；.url → 取出协议 URL；命令行带参数 → 取出可执行部分；
        /// 相对命令名 → 查 PATH / App Paths。
        /// 追溯不到就原样保留，绝不乱改用户填的东西。
        /// </summary>
        public static bool Upgrade(GroupItem it)
        {
            if (it == null || string.IsNullOrWhiteSpace(it.Path)) return false;

            string before = it.Path;
            string p = Environment.ExpandEnvironmentVariables(it.Path.Trim());

            // .url（Steam / Epic 之类的商店在桌面上建的就是这个）→ 取出里面的 URL。
            //
            // 为什么不能原样留着：.url 和 .lnk 一样，用户把游戏放进格子之后
            // 桌面上那个文件肯定要删 —— 路径还指向它的话格子就废了。
            // 把 steam://rungameid/xxx 这种协议地址直接存下来，由外壳负责启动，
            // 桌面文件删掉也不影响。
            //
            // 顺带：图标单独存到 icon 字段。.url 的图标不在文件里，靠 IconFile= 指过去，
            // 直接读它才不会叠上快捷方式小箭头（SHGetFileInfo 读 .url 会叠）。
            if (p.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
            {
                var u = UrlShortcut.Parse(p);
                if (u == null || string.IsNullOrWhiteSpace(u.Url)) return false;

                it.Path = u.Url.Trim();
                if (string.IsNullOrWhiteSpace(it.Icon) && !string.IsNullOrWhiteSpace(u.IconFile))
                    it.Icon = u.IconFile.Trim();
                return it.Path != before;
            }

            if (p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                string target = Icons.ResolveShortcut(p);
                if (!string.IsNullOrWhiteSpace(target))
                {
                    string exe = FirstToken(target);
                    if (File.Exists(exe)) { it.Path = exe; return it.Path != before; }
                }
                // 快捷方式解析不出来（可能目标已删除），保留原路径
                return false;
            }

            // 带参数的路径，例如 "C:\...\a.exe" -flag 或 C:\...\a.exe /silent
            string token = FirstToken(p);
            if (token != p && File.Exists(token)) { it.Path = token; return it.Path != before; }

            // 相对命令名，例如 notepad.exe
            if (!File.Exists(p))
            {
                string full = ResolveCommand(token);
                if (full != null) { it.Path = full; return it.Path != before; }
            }

            return false;
        }

        /// <summary>从可能带参数的命令行里取出可执行文件部分。</summary>
        static string FirstToken(string s)
        {
            s = s.Trim();
            if (s.Length == 0) return s;

            if (s[0] == '"')
            {
                int end = s.IndexOf('"', 1);
                return end > 1 ? s.Substring(1, end - 1) : s.Trim('"');
            }

            // 无引号：若整串存在就直接用；否则按 .exe 截断
            if (File.Exists(s)) return s;
            int i = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (i > 0) return s.Substring(0, i + 4);
            int sp = s.IndexOf(' ');
            return sp > 0 ? s.Substring(0, sp) : s;
        }
    }

    /// <summary>
    /// .url（Internet 快捷方式）解析。
    ///
    /// Steam、Epic 之类的商店在桌面上建的就是这个格式。它是纯文本的 INI，长这样：
    ///
    ///     [InternetShortcut]
    ///     URL=steam://rungameid/1172470
    ///     IconFile=D:\steam\steam\games\8986dd....ico
    ///     IconIndex=0
    ///
    /// 两个字段各有用处：
    ///   URL       —— 真正的启动目标。存它而不是存 .url 的路径，桌面文件删掉也不怕。
    ///   IconFile  —— 图标。可能指向 .ico，也可能指向 .exe（战地 6 那种），
    ///                两种都由调用方按扩展名处理。
    /// </summary>
    internal static class UrlShortcut
    {
        public sealed class Info
        {
            public string Url;
            public string IconFile;
            public int IconIndex;
        }

        /// <summary>解析 .url 文件。读不了或格式不对就返回 null。</summary>
        public static Info Parse(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;

                var info = new Info();
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '[' || line[0] == ';') continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    if (val.Length == 0) continue;

                    // INI 的键名不区分大小写
                    if (key.Equals("URL", StringComparison.OrdinalIgnoreCase)) info.Url = val;
                    else if (key.Equals("IconFile", StringComparison.OrdinalIgnoreCase)) info.IconFile = val;
                    else if (key.Equals("IconIndex", StringComparison.OrdinalIgnoreCase))
                    {
                        int idx;
                        if (int.TryParse(val, out idx) && idx >= 0) info.IconIndex = idx;
                    }
                }

                return string.IsNullOrWhiteSpace(info.Url) && string.IsNullOrWhiteSpace(info.IconFile)
                    ? null : info;
            }
            catch { return null; }
        }
    }

    /// <summary>开机自启：写 HKCU 的 Run 项，不需要管理员。</summary>
    internal static class AutoStart
    {
        const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "WinQuad";

        /// <summary>
        /// 程序改名前用的项名。留着是为了清理 —— 改名前设置过开机自启的机器上，
        /// 这个旧项会一直指向老路径（老目录一旦删掉就成了每次开机弹一个找不到文件的错）。
        /// </summary>
        const string LegacyValueName = "DeskGrid";

        public static bool IsEnabled()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                    return k?.GetValue(ValueName) != null;
            }
            catch { return false; }
        }

        public static void Apply(bool enabled)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;

                    // 无论开还是关，都顺手把改名前的旧项清掉
                    if (k.GetValue(LegacyValueName) != null)
                    {
                        k.DeleteValue(LegacyValueName, false);
                        Cfg.Log("[注册表] 已清除改名前的旧自启项 " + LegacyValueName);
                    }

                    if (enabled)
                    {
                        string exe = Path.Combine(Cfg.AppDir, "WinQuad.exe");
                        k.SetValue(ValueName, "\"" + exe + "\" --overlay");
                        Cfg.Log("[注册表] 已设置开机自启: " + exe);
                    }
                    else
                    {
                        if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName, false);
                        Cfg.Log("[注册表] 已取消开机自启");
                    }
                }
            }
            catch (Exception ex) { Cfg.Log("写注册表失败: " + ex.Message); }
        }
    }
}
