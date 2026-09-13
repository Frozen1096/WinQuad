// 处理"以管理员身份运行"这个兼容性标志。
//
// 为什么需要：本程序要从资源管理器接收拖放，而 Windows 的 UIPI 禁止
// 普通权限进程（explorer.exe）向管理员进程投放数据 —— 一旦被提权，
// 拖放就完全不可用（光标一直是禁止号，连 DragEnter 都不触发）。
//
// manifest 里已经写明 asInvoker，但如果 exe 或快捷方式在
// 「属性 → 兼容性 → 以管理员身份运行」里被勾选，那个勾选会**覆盖** manifest。
// 这个标志存在注册表里，所以这里检测并（在用户同意后）清除它。
using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WinQuad.Manager
{
    internal static class ElevationFix
    {
        const string CompatRoot = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        const string RunAsAdmin = "RUNASADMIN";

        /// <summary>读某个路径的兼容性标志值（没有则返回 null）。</summary>
        static string ReadFlag(string exePath)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(CompatRoot))
                    return k?.GetValue(exePath) as string;
            }
            catch { return null; }
        }

        /// <summary>该路径是否被勾了"以管理员身份运行"。</summary>
        public static bool HasRunAsAdmin(string exePath)
        {
            var v = ReadFlag(exePath);
            return v != null && v.IndexOf(RunAsAdmin, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>清除该路径的 RUNASADMIN 标志（保留其他标志，如兼容模式）。</summary>
        static bool ClearRunAsAdmin(string exePath)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(CompatRoot, true))
                {
                    if (k == null) return false;
                    var v = k.GetValue(exePath) as string;
                    if (v == null) return false;

                    var parts = v.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    var kept = new System.Collections.Generic.List<string>();
                    foreach (var p in parts)
                        if (!string.Equals(p, RunAsAdmin, StringComparison.OrdinalIgnoreCase))
                            kept.Add(p);

                    if (kept.Count == 0) k.DeleteValue(exePath, false);
                    else k.SetValue(exePath, string.Join(" ", kept));
                    return true;
                }
            }
            catch (Exception ex) { Cfg.Log("清除兼容性标志失败: " + ex.Message); return false; }
        }

        /// <summary>
        /// 启动时检查。若当前是提权运行、且原因确实是兼容性标志，就提示并询问是否清除。
        /// 返回 true 表示清除了标志（需要重启才生效）。
        /// </summary>
        public static bool CheckAndOfferFix()
        {
            string exe = Path.Combine(Cfg.AppDir, "WinQuad.Manager.exe");
            bool flagged = HasRunAsAdmin(exe);

            Cfg.Log("[提权检查] 当前是否提权=" + Cfg.IsElevated() + "  exe有RUNASADMIN标志=" + flagged);

            if (!Cfg.IsElevated()) return false;

            if (flagged)
            {
                var r = MessageBox.Show(
                    "管理器正在以管理员身份运行，这会让「从资源管理器拖放程序」彻底不可用。\n\n" +
                    "原因：可执行文件在「属性 → 兼容性」里被勾选了「以管理员身份运行」，\n" +
                    "该勾选由注册表记录，会覆盖程序自身的设置。\n\n" +
                    "是否现在清除这个勾选？清除后重新打开管理器即可使用拖放。",
                    "WinQuad 管理器", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                if (r == DialogResult.Yes)
                {
                    if (ClearRunAsAdmin(exe))
                    {
                        Cfg.Log("[提权检查] 已清除 RUNASADMIN 标志");
                        MessageBox.Show("已清除。请关闭管理器再重新打开。", "WinQuad 管理器");
                        return true;
                    }
                    MessageBox.Show("清除失败，可能需要手动到注册表或文件属性里改。", "WinQuad 管理器");
                }
                return false;
            }

            // 提权了但没有兼容性标志 —— 那多半是父进程本身是管理员
            MessageBox.Show(
                "管理器正以管理员身份运行，「从资源管理器拖放程序」会不可用。\n\n" +
                "程序本身已经声明为普通权限，所以这次提权来自你的启动方式：\n" +
                "例如在管理员权限的终端里启动、或用管理员身份的快捷方式。\n\n" +
                "想要拖放的话，请用普通权限重新打开；直接用「＋ 添加程序」按钮则不受影响。",
                "WinQuad 管理器", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
    }
}
