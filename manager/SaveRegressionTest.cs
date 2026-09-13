// 临时回归测试：验证 SaveGroup 不会再丢掉注释。
// 用法：WinQuad.Manager.exe --test-save <文件名>
using System;
using System.IO;
using System.Text;

namespace WinQuad.Manager
{
    internal static class SaveRegressionTest
    {
        public static void Run(string fileName)
        {
            string path = Path.Combine(Cfg.AppDir, fileName);
            var sb = new StringBuilder();
            sb.AppendLine("=== SaveGroup 注释回归测试 ===");
            sb.AppendLine("文件: " + fileName);

            // 1) 先备份
            string backup = path + ".bak";
            File.Copy(path, backup, true);
            sb.AppendLine("已备份到 " + Path.GetFileName(backup));

            // 2) 载入、改一个值、保存
            var g = Cfg.LoadGroup(fileName);
            if (g == null) { sb.AppendLine("载入失败"); Flush(sb); return; }

            sb.AppendLine("载入时条目数 = " + g.Items.Count);
            sb.AppendLine("载入时 col=" + g.Position.Col + " row=" + g.Position.Row);

            // 模拟用户在界面上改了点东西
            g.Items[0].Caption = (g.Items[0].Caption ?? "") + "!";
            Cfg.SaveGroup(g);

            // 3) 检查结果
            string after = File.ReadAllText(path);
            bool hasComments = after.Contains("_说明");
            sb.AppendLine("保存后还有注释: " + hasComments);

            var g2 = Cfg.LoadGroup(fileName);
            sb.AppendLine("保存后条目数 = " + g2.Items.Count);
            sb.AppendLine("保存后格0 caption = " + g2.Items[0].Caption);
            sb.AppendLine("保存后 col=" + g2.Position.Col + " row=" + g2.Position.Row);
            sb.AppendLine("保存后格0 path = " + g2.Items[0].Path);

            // 4) 还原
            File.Copy(backup, path, true);
            File.Delete(backup);
            sb.AppendLine("已还原原文件");

            Flush(sb);
        }

        static void Flush(StringBuilder sb)
        {
            string outp = Path.Combine(Cfg.AppDir, "save-test-result.txt");
            File.WriteAllText(outp, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>
        /// 总配置的同类回归测试：SaveConfig 也不能丢注释。
        /// 用法：WinQuad.Manager.exe --test-config
        /// </summary>
        public static void RunConfig()
        {
            string path = Cfg.ConfigPath;
            var sb = new StringBuilder();
            sb.AppendLine("=== SaveConfig 注释回归测试 ===");
            sb.AppendLine("文件: " + path);

            string backup = path + ".bak";
            File.Copy(path, backup, true);

            string before = File.ReadAllText(path);
            sb.AppendLine("保存前注释字段数 = " + CountComments(before));

            var c = Cfg.LoadConfig();
            if (c == null) { sb.AppendLine("载入失败"); Flush(sb); return; }

            // 模拟用户在设置界面里改了一堆东西
            c.Size.IconSize = 26;
            c.Size.LabelHeight = 18;
            c.Style.PlateAlpha = 88;
            c.Style.FontSizePt = 6.5f;
            c.Behaviour.GridStepX = 84;
            c.Behaviour.CollisionH = 58;
            Cfg.SaveConfig(c);

            string after = File.ReadAllText(path);
            sb.AppendLine("保存后注释字段数 = " + CountComments(after));
            sb.AppendLine("注释还在: " + after.Contains("_说明"));

            var c2 = Cfg.LoadConfig();
            sb.AppendLine("回读 iconSize      = " + c2.Size.IconSize + "  (期望 26)");
            sb.AppendLine("回读 labelHeight   = " + c2.Size.LabelHeight + "  (期望 18)");
            sb.AppendLine("回读 plateAlpha    = " + c2.Style.PlateAlpha + "  (期望 88)");
            sb.AppendLine("回读 fontSizePt    = " + c2.Style.FontSizePt + "  (期望 6.5)");
            sb.AppendLine("回读 gridStepX     = " + c2.Behaviour.GridStepX + "  (期望 84)");
            sb.AppendLine("回读 collisionH    = " + c2.Behaviour.CollisionH + "  (期望 58)");
            sb.AppendLine("回读 groups 数     = " + c2.Groups.Count);

            bool ok = after.Contains("_说明")
                   && c2.Size.IconSize == 26 && c2.Size.LabelHeight == 18
                   && c2.Style.PlateAlpha == 88
                   && Math.Abs(c2.Style.FontSizePt - 6.5f) < 0.001f
                   && c2.Behaviour.GridStepX == 84 && c2.Behaviour.CollisionH == 58
                   && c2.Groups.Count == c.Groups.Count;
            sb.AppendLine(ok ? "★ 通过" : "✗ 不通过");

            File.Copy(backup, path, true);
            File.Delete(backup);
            sb.AppendLine("已还原原文件");
            Flush(sb);
        }

        static int CountComments(string json)
        {
            int n = 0, i = 0;
            while ((i = json.IndexOf("\"_", i, StringComparison.Ordinal)) >= 0) { n++; i += 2; }
            return n;
        }
    }
}
