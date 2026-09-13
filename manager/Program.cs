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
    }
}
