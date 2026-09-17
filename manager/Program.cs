// WinQuad 管理器 —— 独立于桌面覆盖层（WinQuad.exe）。
//
// 设计边界（按用户要求）：
//   * 管理器只负责「给参数一个图形界面」，不做自动同步。
//   * 只有点「保存并重启」时才写 json 并重启覆盖层进程。
//   * 覆盖层已经验证稳定，本项目不改动它一行代码；两者通过 json 文件这个契约通信。
using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace WinQuad.Manager
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // 修复模式：把被改坏、注释丢失的 group-*.json 用当前模板重建
            if (args.Length >= 2 && args[0] == "--fix-group")
            {
                GroupFixer.Run(args[1]);
                return;
            }

            // 回归测试：验证保存不会再丢注释
            if (args.Length >= 2 && args[0] == "--test-save")
            {
                SaveRegressionTest.Run(args[1]);
                return;
            }

            // 回归测试：验证总配置保存也不会丢注释
            if (args.Length >= 1 && args[0] == "--test-config")
            {
                SaveRegressionTest.RunConfig();
                return;
            }

            // 迁移：给所有 group-*.json 补齐注释
            if (args.Length >= 1 && args[0] == "--restore-comments")
            {
                CommentRestorer.Run();
                return;
            }

            // 调试：把路径溯源的结果打出来（.lnk / .url / 命令行 分别被解析成了什么）。
            // 用法：WinQuad.Manager.exe --resolve "C:\Users\x\Desktop\某游戏.url"
            if (args.Length >= 2 && args[0] == "--resolve")
            {
                for (int i = 1; i < args.Length; i++)
                {
                    var it = new GroupItem { Path = args[i], Icon = null };
                    bool changed = PathResolver.Upgrade(it);
                    Console.WriteLine("输入  : " + args[i]);
                    Console.WriteLine("  path: " + it.Path + (changed ? "   <- 被溯源改写" : "   （未改动）"));
                    Console.WriteLine("  icon: " + (it.Icon ?? "（无）"));
                    Console.WriteLine("  文件存在: " + System.IO.File.Exists(it.Path));
                    Console.WriteLine();
                }
                return;
            }

            // 调试：把界面离屏渲染成 PNG，不显示任何窗口。
            // 改完界面想确认排版对不对，又不想弹窗打断正在用电脑的人时，用这个。
            if (args.Length >= 3 && args[0] == "--shot")
            {
                int h = 0, sy = 0;
                if (args.Length >= 4) int.TryParse(args[3], out h);
                if (args.Length >= 5) int.TryParse(args[4], out sy);
                Shot(args[1], args[2], h, sy);
                return;
            }

            try { File.Delete(Cfg.LogPath); } catch { }
            Cfg.Log("=== 管理器启动 pid=" + Environment.ProcessId + " ===");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

            // 调试：只开设置窗口（--settings），用来单独查看设置界面。
            // 放在提权检查**之前** —— 否则从管理员终端里跑会先弹提权提示，挡住设置窗口。
            if (args.Length >= 1 && args[0] == "--settings")
            {
                RunSettingsOnly();
                return;
            }

            // 启动时检查提权：被提权会直接废掉"从资源管理器拖放程序"。
            // 若原因是兼容性里的"以管理员身份运行"勾选，会询问是否清除。
            if (ElevationFix.CheckAndOfferFix())
            {
                Cfg.Log("已清除提权标志，退出等待用户重启");
                return;
            }

            if (!File.Exists(Cfg.ConfigPath))
            {
                Cfg.Log("找不到 " + Cfg.ConfigPath);
                MessageBox.Show("同目录下找不到 config.json。\n\n" +
                                "管理器需要和 WinQuad.exe、config.json 放在同一个文件夹里。\n" +
                                "当前目录：" + Cfg.AppDir,
                                "WinQuad 管理器", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Application.Run(new MainForm());
        }

        /// <summary>
        /// 调试用：单独打开设置窗口，不启动主界面。
        /// 设置界面平时只能从主界面点「参数设置」进，出了问题没法单独看，加这个口子方便排查。
        /// </summary>
        internal static void RunSettingsOnly()
        {
            Application.Run(new SettingsForm(Cfg.LoadConfig()));
        }

        /// <summary>
        /// 调试用：把某个界面离屏画成 PNG。
        ///
        /// 用法：WinQuad.Manager.exe --shot main|settings 输出.png
        ///
        /// 关键是**不显示窗口** —— 用 CreateControl 强制建好句柄和子控件，
        /// 再 DrawToBitmap 直接画到位图上。这样检查排版不会弹窗打断别人用电脑。
        /// </summary>
        internal static void Shot(string which, string outPath, int wantH = 0, int scrollY = 0)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

            Form f;
            if (which == "settings")
            {
                f = new SettingsForm(Cfg.LoadConfig());
            }
            else
            {
                f = new MainForm();
            }

            f.StartPosition = FormStartPosition.Manual;
            f.Location = new System.Drawing.Point(-4000, -4000);   // 挪到屏幕外，彻底不打扰

            // 可选参数：渲染高度。设置界面内容比屏幕高，要一次看全就把高度给大些。
            // 窗口从不显示，所以不受屏幕尺寸限制。
            if (wantH > 200) f.Height = wantH;

            // DrawToBitmap 只画"已经有句柄"的控件。窗口没显示过时子控件往往还没建句柄，
            // 画出来就是一片空白 —— 所以这里递归把整棵树的句柄都强制建出来。
            ForceCreate(f);
            f.PerformLayout();

            // 关键：这两个窗口的排版都不是 WinForms 自动做的，而是各自的私有方法按窗口宽度算的
            // （设置界面是 LayoutAll()，主界面是 LoadAll()），而它们只在 Load/Resize 里被调用，
            // 窗口不显示就永远不触发。用反射直接叫一次。
            Invoke(f, "LayoutAll");
            Invoke(f, "LoadAll");

            f.PerformLayout();
            Application.DoEvents();
            Invoke(f, "LayoutAll");
            f.PerformLayout();

            // 窗口高度被 MaxWindowTrackSize（屏幕高 + 边框）卡住，滚又滚不动（AutoScroll 要窗口可见才生效）。
            // 所以传了高度就干脆绕过窗口：直接渲染滚动区里那个内容面板，它的尺寸就是完整内容高度。
            System.Windows.Forms.Control target = f;
            if (wantH > 0)
            {
                var sc = FindScroller(f);
                if (sc != null && sc.Controls.Count > 0) target = sc.Controls[0];
            }
            Console.WriteLine("渲染目标：" + target.GetType().Name + " " + target.Size);

            using (var bmp = new System.Drawing.Bitmap(target.Width, target.Height))
            {
                target.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, target.Width, target.Height));
                bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine("已生成 " + outPath + "  " + target.Width + "x" + target.Height);
            }
            f.Dispose();
        }

        static void Invoke(System.Windows.Forms.Form f, string method)
        {
            var mi = f.GetType().GetMethod(method,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public);
            if (mi == null) return;
            try
            {
                mi.Invoke(f, mi.GetParameters().Length == 0 ? null : new object[mi.GetParameters().Length]);
            }
            catch (Exception ex) { Console.WriteLine("调用 " + method + " 失败: " + ex.InnerException?.Message ?? ex.Message); }
        }

        static void ForceCreate(System.Windows.Forms.Control c)
        {
            if (!c.IsHandleCreated) { var _ = c.Handle; }
            c.CreateControl();
            foreach (System.Windows.Forms.Control ch in c.Controls) ForceCreate(ch);
        }

        /// <summary>找出第一个开了 AutoScroll 的容器（设置界面的滚动区）。</summary>
        static System.Windows.Forms.ScrollableControl FindScroller(System.Windows.Forms.Control c)
        {
            if (c is System.Windows.Forms.ScrollableControl s && s.AutoScroll) return s;
            foreach (System.Windows.Forms.Control ch in c.Controls)
            {
                var r = FindScroller(ch);
                if (r != null) return r;
            }
            return null;
        }

        static void DumpTree(System.Windows.Forms.Control c, int depth)
        {
            if (depth > 3) return;
            Console.WriteLine(new string(' ', depth * 2) +
                c.GetType().Name + " \"" + (c.Text ?? "") + "\" " +
                c.Bounds + " visible=" + c.Visible + " handle=" + c.IsHandleCreated);
            foreach (System.Windows.Forms.Control ch in c.Controls) DumpTree(ch, depth + 1);
        }
    }
}
