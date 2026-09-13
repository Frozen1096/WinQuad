// 迁移工具：把所有 group-*.json 用当前模板补齐注释（保留数据）。
// 用法：WinQuad.Manager.exe --restore-comments
using System;
using System.IO;
using System.Text;

namespace WinQuad.Manager
{
    internal static class CommentRestorer
    {
        public static void Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== 补齐 group-*.json 的注释 ===");

            var cfg = Cfg.LoadConfig();
            if (cfg == null) { sb.AppendLine("读不到 config.json"); Flush(sb); return; }

            foreach (var r in cfg.Groups)
            {
                if (string.IsNullOrWhiteSpace(r.GroupFile)) continue;
                string path = Path.Combine(Cfg.AppDir, r.GroupFile);
                if (!File.Exists(path)) { sb.AppendLine("  跳过（文件不存在）: " + r.GroupFile); continue; }

                string before = File.ReadAllText(path);
                bool had = before.Contains("_说明");

                var g = Cfg.LoadGroup(r.GroupFile);
                if (g == null) { sb.AppendLine("  跳过（解析失败）: " + r.GroupFile); continue; }

                Cfg.SaveGroup(g);

                string after = File.ReadAllText(path);
                sb.AppendLine("  " + r.GroupFile
                    + "  原有注释=" + had
                    + "  处理后注释=" + after.Contains("_说明")
                    + "  条目=" + g.Items.Count
                    + "  大小 " + before.Length + " -> " + after.Length);
            }

            Flush(sb);
        }

        static void Flush(StringBuilder sb)
        {
            File.WriteAllText(Path.Combine(Cfg.AppDir, "restore-comments-result.txt"),
                              sb.ToString(), new UTF8Encoding(false));
        }
    }
}
