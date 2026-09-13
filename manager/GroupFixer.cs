// 一次性修复工具：把被正则改坏、注释丢失的 group-*.json 用当前模板重新生成，
// 同时保留用户已填的 items（程序路径 / 显示文字 / 颜色 / 透明度）和 position。
//
// 用法：WinQuad.Manager.exe --fix-group <文件名>
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace WinQuad.Manager
{
    internal static class GroupFixer
    {
        public static void Run(string fileName)
        {
            string path = Path.Combine(Cfg.AppDir, fileName);
            if (!File.Exists(path)) { Console.WriteLine("找不到 " + path); return; }

            var g = Cfg.LoadGroup(fileName);
            if (g == null) { Console.WriteLine("解析失败 " + path); return; }

            // 1) 用模板重新生成（overwrite=true），拿到带完整注释的空文件
            if (!Cfg.CreateGroupFile(fileName, g.Name ?? "未命名", true))
            {
                Console.WriteLine("模板生成失败");
                return;
            }

            // 2) 把用户数据写回新文件（用最小改动的方式：重新序列化会把注释弄丢，
            //    所以这里手工把 items / position 的数字替换进去）
            string raw = File.ReadAllText(path);

            // 位置
            if (g.Position != null)
            {
                raw = ReplaceNum(raw, "col", g.Position.Col);
                raw = ReplaceNum(raw, "row", g.Position.Row);
                raw = ReplaceNum(raw, "x", g.Position.X);
                raw = ReplaceNum(raw, "y", g.Position.Y);
            }

            // items：模板里是 4 个占位项，把它们的 caption/path 等替换掉
            var items = g.Items ?? new List<GroupItem>();
            raw = ReplaceItems(raw, items);

            File.WriteAllText(path, raw, new UTF8Encoding(false));

            // 3) 校验语法
            try
            {
                JsonSerializer.Deserialize<GroupFile>(raw, Cfg.Json);
                Console.WriteLine("已重建 " + fileName + "，保留 " + items.Count + " 项，语法校验通过");
            }
            catch (Exception ex)
            {
                Console.WriteLine("重建后语法错误: " + ex.Message);
            }
        }

        static string ReplaceNum(string json, string key, int value) =>
            System.Text.RegularExpressions.Regex.Replace(
                json, "(\"" + key + "\"\\s*:\\s*)-?\\d+", "${1}" + value);

        /// <summary>
        /// 把模板里的 items 数组整体替换成真实数据。
        /// 做法：找到 "items": [ ... ] 这一段，用序列化后的真实数组替换。
        /// </summary>
        static string ReplaceItems(string json, List<GroupItem> items)
        {
            int start = json.IndexOf("\"items\"", StringComparison.Ordinal);
            if (start < 0) return json;
            int open = json.IndexOf('[', start);
            if (open < 0) return json;

            // 找到匹配的右括号
            int depth = 0, end = -1;
            for (int i = open; i < json.Length; i++)
            {
                if (json[i] == '[') depth++;
                else if (json[i] == ']') { depth--; if (depth == 0) { end = i; break; } }
            }
            if (end < 0) return json;

            var sb = new StringBuilder();
            sb.Append("[\r\n");
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                sb.Append("    { \"caption\": ").Append(JsonStr(it.Caption))
                  .Append(", \"path\": ").Append(JsonStr(it.Path))
                  .Append(", \"icon\": ").Append(JsonStr(it.Icon))
                  .Append(", \"bgColor\": ").Append(ColorStr(it.BgColor))
                  .Append(", \"bgAlpha\": ").Append(it.BgAlpha?.ToString() ?? "null")
                  .Append(" }");
                if (i < items.Count - 1) sb.Append(',');
                sb.Append("\r\n");
            }
            sb.Append("  ]");

            return json.Substring(0, open) + sb + json.Substring(end + 1);
        }

        static string JsonStr(string s) =>
            s == null ? "null" : JsonSerializer.Serialize(s, Cfg.Json);

        static string ColorStr(int[] c)
        {
            if (c == null || c.Length < 3) return "null";
            return "[" + c[0] + ", " + c[1] + ", " + c[2] + "]";
        }
    }
}
