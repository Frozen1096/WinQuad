// 配置模型 + 读写工具。
// 刻意与覆盖层（WinQuad.exe）保持各自独立 —— 覆盖层已经验证稳定，不去动它。
// 两边通过 json 文件这个契约通信，只要字段名一致就能互通。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace WinQuad.Manager
{
    #region 模型

    internal sealed class AppConfig
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("size")] public SizeSection Size { get; set; } = new SizeSection();
        [JsonPropertyName("style")] public StyleSection Style { get; set; } = new StyleSection();
        [JsonPropertyName("behaviour")] public BehaviourSection Behaviour { get; set; } = new BehaviourSection();
        [JsonPropertyName("groups")] public List<GroupRef> Groups { get; set; } = new List<GroupRef>();
    }

    internal sealed class SizeSection
    {
        [JsonPropertyName("cols")] public int Cols { get; set; } = 2;
        [JsonPropertyName("rows")] public int Rows { get; set; } = 2;
        [JsonPropertyName("cellWidth")] public int CellWidth { get; set; } = 37;
        [JsonPropertyName("cellHeight")] public int CellHeight { get; set; } = 48;
        [JsonPropertyName("padX")] public int PadX { get; set; } = 2;
        [JsonPropertyName("padY")] public int PadY { get; set; } = 2;
        [JsonPropertyName("gap")] public int Gap { get; set; } = 1;
        [JsonPropertyName("iconSize")] public int IconSize { get; set; } = 24;
        [JsonPropertyName("labelHeight")] public int LabelHeight { get; set; } = 16;
        [JsonPropertyName("ringSize")] public int RingSize { get; set; } = 3;
        [JsonPropertyName("bleed")] public int Bleed { get; set; } = 2;

        [JsonIgnore] public int Width => PadX * 2 + CellWidth * Cols + Gap * (Cols - 1);
        [JsonIgnore] public int Height => PadY * 2 + CellHeight * Rows + Gap * (Rows - 1);
        [JsonIgnore] public int FootprintWidth => Width + Bleed * 2;
        [JsonIgnore] public int FootprintHeight => Height + Bleed * 2;
    }

    internal sealed class StyleSection
    {
        [JsonPropertyName("plateColor")] public int[] PlateColor { get; set; } = { 255, 255, 255 };
        [JsonPropertyName("plateAlpha")] public int PlateAlpha { get; set; } = 40;
        [JsonPropertyName("plateAlphaHover")] public int PlateAlphaHover { get; set; } = 96;
        [JsonPropertyName("borderAlpha")] public int BorderAlpha { get; set; } = 40;
        [JsonPropertyName("borderColor")] public int[] BorderColor { get; set; } = { 255, 255, 255 };
        [JsonPropertyName("backdropColor")] public int[] BackdropColor { get; set; } = { 24, 26, 32 };
        [JsonPropertyName("fontFamily")] public string FontFamily { get; set; } = "Microsoft YaHei UI";
        [JsonPropertyName("fontSizePt")] public float FontSizePt { get; set; } = 8f;
        [JsonPropertyName("fontBold")] public bool FontBold { get; set; }
        [JsonPropertyName("textColor")] public int[] TextColor { get; set; } = { 255, 255, 255 };
        [JsonPropertyName("textColorHover")] public int[] TextColorHover { get; set; } = { 255, 255, 255 };
        [JsonPropertyName("outlineColor")] public int[] OutlineColor { get; set; } = { 0, 0, 0 };
        [JsonPropertyName("outlineAlpha")] public int OutlineAlpha { get; set; } = 245;
        [JsonPropertyName("outlineWidth")] public int OutlineWidth { get; set; } = 1;
        [JsonPropertyName("iconShadow")] public bool IconShadow { get; set; }
    }

    internal sealed class BehaviourSection
    {
        [JsonPropertyName("doubleClickToLaunch")] public bool DoubleClickToLaunch { get; set; } = true;
        [JsonPropertyName("showTooltip")] public bool ShowTooltip { get; set; } = true;
        [JsonPropertyName("avoidDesktopIcons")] public bool AvoidDesktopIcons { get; set; } = true;
        [JsonPropertyName("snapToIconGrid")] public bool SnapToIconGrid { get; set; } = true;
        [JsonPropertyName("maxNudgeDistance")] public int MaxNudgeDistance { get; set; } = 700;
        [JsonPropertyName("startWithWindows")] public bool StartWithWindows { get; set; }

        /// <summary>桌面图标网格横向间距（像素）。各机器可能不同，可用「刷新行列距」实测。</summary>
        [JsonPropertyName("gridStepX")] public int GridStepX { get; set; } = 83;

        /// <summary>桌面图标网格纵向间距（像素）。注意注册表值通常与实际不符，必须实测。</summary>
        [JsonPropertyName("gridStepY")] public int GridStepY { get; set; } = 114;

        /// <summary>遮挡判定用的图标碰撞盒宽度。理论格位 78px，实际图形约 48px。</summary>
        [JsonPropertyName("collisionW")] public int CollisionW { get; set; } = 44;

        /// <summary>遮挡判定用的图标碰撞盒高度。理论格位 113px，实际图形+文字约 64px。</summary>
        [JsonPropertyName("collisionH")] public int CollisionH { get; set; } = 56;
    }

    internal sealed class GroupRef
    {
        [JsonPropertyName("groupFile")] public string GroupFile { get; set; }
    }

    internal sealed class GroupFile
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("position")] public PositionSection Position { get; set; } = new PositionSection();
        [JsonPropertyName("defaults")] public DefaultsSection Defaults { get; set; } = new DefaultsSection();
        [JsonPropertyName("items")] public List<GroupItem> Items { get; set; } = new List<GroupItem>();

        [JsonIgnore] public string FilePath { get; set; }
    }

    internal sealed class PositionSection
    {
        public const int Unset = int.MinValue;

        [JsonPropertyName("col")] public int Col { get; set; } = Unset;
        [JsonPropertyName("row")] public int Row { get; set; } = Unset;
        [JsonPropertyName("x")] public int X { get; set; } = Unset;
        [JsonPropertyName("y")] public int Y { get; set; } = Unset;

        /// <summary>旧字段，已废弃。位置现在只由手动拖动决定。保留只为兼容老配置文件。</summary>
        [JsonPropertyName("anchor")] public string Anchor { get; set; }
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

    internal static class Cfg
    {
        public static readonly string AppDir = AppContext.BaseDirectory;
        public static readonly string ConfigPath = Path.Combine(AppDir, "config.json");
        public static readonly string LogPath = Path.Combine(AppDir, "winquad-manager.log");

        /// <summary>
        /// 本次进程是否以管理员身份运行。
        ///
        /// 这件事直接决定"从资源管理器拖文件进来"能不能用：
        /// Windows 的 UIPI 禁止低权限进程（普通权限的 explorer.exe）
        /// 向高权限进程（提权的本程序）投放数据，症状就是光标一直是禁止号、
        /// 而且连 DragEnter 都不会触发。所以检测到提权时必须明确告诉用户。
        /// </summary>
        public static bool IsElevated()
        {
            try
            {
                var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                var pr = new System.Security.Principal.WindowsPrincipal(id);
                return pr.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        public static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        static readonly object LogLock = new object();

        public static void Log(string m)
        {
            try
            {
                lock (LogLock)
                    File.AppendAllText(LogPath,
                        DateTime.Now.ToString("HH:mm:ss.fff") + "  " + m + Environment.NewLine);
            }
            catch { }
        }

        public static AppConfig LoadConfig()
        {
            try { return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), Json); }
            catch (Exception ex) { Log("读总配置失败: " + ex.Message); return null; }
        }

        public static GroupFile LoadGroup(string file)
        {
            try
            {
                string p = Path.Combine(AppDir, file);
                if (!File.Exists(p)) { Log("找不到 " + p); return null; }
                var g = JsonSerializer.Deserialize<GroupFile>(File.ReadAllText(p), Json);
                if (g != null) g.FilePath = p;
                return g;
            }
            catch (Exception ex) { Log("读 " + file + " 失败: " + ex.Message); return null; }
        }

        /// <summary>
        /// 写总配置，并且**保住 "_说明" 注释**。
        ///
        /// 教训：原实现直接整份序列化，把 config.json 里 46 个注释字段全抹了。
        /// group-*.json 早就修过同样的毛病，总配置漏掉了 —— 而注释恰恰是这套配置的
        /// 设计核心（用户要把它发给别人或 AI 帮忙改）。现在两者用同一套策略：
        /// 能就地更新就就地更新，只替换已知字段的值，其余字节一个不动。
        /// </summary>
        public static bool SaveConfig(AppConfig cfg)
        {
            try
            {
                string serialized = JsonSerializer.Serialize(cfg, Json);

                if (File.Exists(ConfigPath))
                {
                    string existing = File.ReadAllText(ConfigPath);
                    if (existing.Contains("_说明"))
                    {
                        string patched = PatchConfig(existing, cfg);
                        if (patched != null)
                        {
                            File.WriteAllText(ConfigPath, patched, new UTF8Encoding(false));
                            return true;
                        }
                        Log("[保存] 总配置就地更新失败，退回整份重写（注释会丢）");
                    }
                    else
                    {
                        Log("[保存] 总配置里没有 _说明 注释，整份重写");
                    }
                }

                File.WriteAllText(ConfigPath, serialized, new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex) { Log("写总配置失败: " + ex.Message); return false; }
        }

        /// <summary>
        /// 就地更新总配置：逐个替换已知字段的值，注释与排版原样保留。
        /// 返回 null 表示结构不符合预期（调用方会退回整份重写）。
        /// </summary>
        static string PatchConfig(string json, AppConfig c)
        {
            try
            {
                var s = c.Size; var st = c.Style; var b = c.Behaviour;
                string o = json;

                // ── size ──
                o = SetSec(o, "size", "cols", Num(s.Cols));
                o = SetSec(o, "size", "rows", Num(s.Rows));
                o = SetSec(o, "size", "cellWidth", Num(s.CellWidth));
                o = SetSec(o, "size", "cellHeight", Num(s.CellHeight));
                o = SetSec(o, "size", "padX", Num(s.PadX));
                o = SetSec(o, "size", "padY", Num(s.PadY));
                o = SetSec(o, "size", "gap", Num(s.Gap));
                o = SetSec(o, "size", "iconSize", Num(s.IconSize));
                o = SetSec(o, "size", "labelHeight", Num(s.LabelHeight));
                o = SetSec(o, "size", "ringSize", Num(s.RingSize));
                o = SetSec(o, "size", "bleed", Num(s.Bleed));

                // ── style ──
                o = SetSec(o, "style", "plateColor", JColor(st.PlateColor));
                o = SetSec(o, "style", "plateAlpha", Num(st.PlateAlpha));
                o = SetSec(o, "style", "plateAlphaHover", Num(st.PlateAlphaHover));
                o = SetSec(o, "style", "borderAlpha", Num(st.BorderAlpha));
                o = SetSec(o, "style", "borderColor", JColor(st.BorderColor));
                o = SetSec(o, "style", "backdropColor", JColor(st.BackdropColor));
                o = SetSec(o, "style", "fontFamily", JStr(st.FontFamily));
                o = SetSec(o, "style", "fontSizePt", NumF(st.FontSizePt));
                o = SetSec(o, "style", "fontBold", Bool(st.FontBold));
                o = SetSec(o, "style", "textColor", JColor(st.TextColor));
                o = SetSec(o, "style", "textColorHover", JColor(st.TextColorHover));
                o = SetSec(o, "style", "outlineColor", JColor(st.OutlineColor));
                o = SetSec(o, "style", "outlineAlpha", Num(st.OutlineAlpha));
                o = SetSec(o, "style", "outlineWidth", Num(st.OutlineWidth));
                o = SetSec(o, "style", "iconShadow", Bool(st.IconShadow));

                // ── behaviour ──
                o = SetSec(o, "behaviour", "doubleClickToLaunch", Bool(b.DoubleClickToLaunch));
                o = SetSec(o, "behaviour", "showTooltip", Bool(b.ShowTooltip));
                o = SetSec(o, "behaviour", "avoidDesktopIcons", Bool(b.AvoidDesktopIcons));
                o = SetSec(o, "behaviour", "snapToIconGrid", Bool(b.SnapToIconGrid));
                o = SetSec(o, "behaviour", "maxNudgeDistance", Num(b.MaxNudgeDistance));
                o = SetSec(o, "behaviour", "startWithWindows", Bool(b.StartWithWindows));
                o = SetSec(o, "behaviour", "gridStepX", Num(b.GridStepX));
                o = SetSec(o, "behaviour", "gridStepY", Num(b.GridStepY));
                o = SetSec(o, "behaviour", "collisionW", Num(b.CollisionW));
                o = SetSec(o, "behaviour", "collisionH", Num(b.CollisionH));

                // ── groups（数组整体换掉，数组外的注释不受影响）──
                string g = ReplaceArray(o, "\"groups\"", BuildGroupsArray(c.Groups));
                if (g != null) o = g;

                // 打完补丁先解析回来核对关键值：对不上说明替换位置找错了，
                // 宁可退回整份重写（丢注释），也不能写出一份坏配置。
                var chk = JsonSerializer.Deserialize<AppConfig>(o, Json);
                if (chk == null
                    || chk.Size.Cols != s.Cols || chk.Size.CellWidth != s.CellWidth
                    || chk.Size.IconSize != s.IconSize || chk.Size.Bleed != s.Bleed
                    || chk.Style.PlateAlpha != st.PlateAlpha
                    || Math.Abs(chk.Style.FontSizePt - st.FontSizePt) > 0.001f
                    || chk.Behaviour.GridStepX != b.GridStepX
                    || chk.Behaviour.CollisionW != b.CollisionW
                    || chk.Behaviour.MaxNudgeDistance != b.MaxNudgeDistance
                    || (chk.Groups?.Count ?? 0) != (c.Groups?.Count ?? 0))
                {
                    Log("[保存] 总配置补丁自检不通过，退回整份重写");
                    return null;
                }
                return o;
            }
            catch (Exception ex) { Log("总配置就地更新异常: " + ex.Message); return null; }
        }

        static string BuildGroupsArray(List<GroupRef> groups)
        {
            var sb = new StringBuilder();
            sb.Append("[\r\n");
            groups ??= new List<GroupRef>();
            for (int i = 0; i < groups.Count; i++)
            {
                sb.Append("    { \"groupFile\": ").Append(JStr(groups[i].GroupFile)).Append(" }");
                if (i < groups.Count - 1) sb.Append(',');
                sb.Append("\r\n");
            }
            sb.Append("  ]");
            return sb.ToString();
        }

        static string Num(int v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        static string NumF(float v) => v.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture);
        static string Bool(bool v) => v ? "true" : "false";

        /// <summary>在 section 对象内部替换 key 的值。section 必须写成带引号的形式。</summary>
        static string SetSec(string json, string section, string key, string literal)
            => SetValue(json, section, key, literal);

        /// <summary>
        /// 把 `"section": { ... }` 里 `"key": 值` 的值部分换掉，其余字节原样保留。
        /// 找不到就原样返回（字段可能被用户删了，不算错误）。
        /// </summary>
        static string SetValue(string json, string section, string key, string literal)
        {
            string sk = "\"" + section + "\"";
            int k = json.IndexOf(sk, StringComparison.Ordinal);
            if (k < 0) return json;
            int open = json.IndexOf('{', k + sk.Length);
            if (open < 0) return json;

            int depth = 0, end = -1;
            for (int i = open; i < json.Length; i++)
            {
                if (json[i] == '{') depth++;
                else if (json[i] == '}') { depth--; if (depth == 0) { end = i; break; } }
            }
            if (end < 0) return json;

            string qk = "\"" + key + "\"";
            int p = json.IndexOf(qk, open, StringComparison.Ordinal);
            if (p < 0 || p >= end) return json;

            // 必须确实是「键」：引号后面紧跟冒号。
            // 这样 "_cols说明" 之类的注释键、以及值里出现的同名字样都不会被误伤。
            int c2 = p + qk.Length;
            while (c2 < end && char.IsWhiteSpace(json[c2])) c2++;
            if (c2 >= end || json[c2] != ':') return json;

            int v = c2 + 1;
            while (v < end && char.IsWhiteSpace(json[v])) v++;
            if (v >= end) return json;

            int ve;
            char ch = json[v];
            if (ch == '[' || ch == '{')
            {
                char close = ch == '[' ? ']' : '}';
                int d = 0; ve = -1;
                for (int i = v; i <= end; i++)
                {
                    if (json[i] == ch) d++;
                    else if (json[i] == close) { d--; if (d == 0) { ve = i + 1; break; } }
                }
                if (ve < 0) return json;
            }
            else if (ch == '"')
            {
                int q = json.IndexOf('"', v + 1);
                if (q < 0 || q > end) return json;
                ve = q + 1;
            }
            else
            {
                ve = v;
                while (ve <= end && json[ve] != ',' && json[ve] != '}' && json[ve] != ']'
                       && json[ve] != '\r' && json[ve] != '\n') ve++;
                while (ve > v && char.IsWhiteSpace(json[ve - 1])) ve--;
            }

            return json.Substring(0, v) + literal + json.Substring(ve);
        }

        /// <summary>
        /// 写宫格文件，并且**保住 "_说明" 注释**。
        ///
        /// 教训：早先的实现是"有注释就普通序列化"，逻辑正好写反了 ——
        /// 序列化本身就会把注释全部丢掉，所以有注释时反而更需要保留。
        /// 现在的做法统一为：能就地更新就就地更新（只替换 position 数字与 items 数组，
        /// 文件其余字节一个不动）；只有文件缺失整段结构时才用模板重建。
        /// </summary>
        public static bool SaveGroup(GroupFile g)
        {
            try
            {
                string serialized = JsonSerializer.Serialize(g, Json);
                string existing = File.Exists(g.FilePath) ? File.ReadAllText(g.FilePath) : null;

                // 1) 有现成文件：就地更新
                //    注意：就地更新只能"保留"注释，不能"补回"已经丢掉的注释。
                //    所以更新完还要查一下 —— 若文件里没有注释，就改用模板重建把它补回来。
                if (existing != null && existing.Contains("_说明"))
                {
                    string updated = UpdateInPlace(existing, g);
                    if (updated != null)
                    {
                        File.WriteAllText(g.FilePath, updated, new UTF8Encoding(false));
                        return true;
                    }
                    Log("[保存] 就地更新失败，改用模板重建: " + Path.GetFileName(g.FilePath));
                }

                // 2) 模板重建（新文件，或原文件已缺注释）
                string rebuilt = RebuildWithComments(g);
                Log("[保存] " + Path.GetFileName(g.FilePath)
                    + "  原文件=" + (existing == null ? "无" : existing.Length + "字节")
                    + "  模板含注释=" + Template.Contains("_说明")
                    + "  重建=" + (rebuilt == null ? "失败" : "成功 " + rebuilt.Length + "字节"
                                                  + " 含注释=" + rebuilt.Contains("_说明")));
                if (rebuilt != null)
                {
                    File.WriteAllText(g.FilePath, rebuilt, new UTF8Encoding(false));
                    return true;
                }

                // 3) 兜底：普通序列化（会丢注释，但至少不丢数据）
                File.WriteAllText(g.FilePath, serialized, new UTF8Encoding(false));
                Log("[保存] 退回普通序列化（注释会丢失）: " + Path.GetFileName(g.FilePath));
                return true;
            }
            catch (Exception ex)
            {
                Log("写 " + g.FilePath + " 失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 就地更新：只替换 position 的四个数字与 items 数组，其余（含注释）原样保留。
        /// 返回 null 表示这个文件的结构不适合就地更新。
        /// </summary>
        static string UpdateInPlace(string json, GroupFile g)
        {
            try
            {
                if (!json.Contains("\"position\"") || !json.Contains("\"items\"")) return null;

                if (g.Position != null)
                {
                    json = ReplaceNum(json, "col", g.Position.Col);
                    json = ReplaceNum(json, "row", g.Position.Row);
                    json = ReplaceNum(json, "x", g.Position.X);
                    json = ReplaceNum(json, "y", g.Position.Y);
                }

                string itemsJson = BuildItemsArray(g.Items);
                string replaced = ReplaceArray(json, "\"items\"", itemsJson);
                return replaced ?? json;   // 找不到 items 数组就只更新位置
            }
            catch (Exception ex) { Log("就地更新异常: " + ex.Message); return null; }
        }

        /// <summary>把 JSON 里 "key": [ ... ] 的数组整体换掉，方括号配对由深度计数保证。</summary>
        static string ReplaceArray(string json, string key, string newArray)
        {
            int start = json.IndexOf(key, StringComparison.Ordinal);
            if (start < 0) return null;
            int open = json.IndexOf('[', start);
            if (open < 0) return null;

            int depth = 0, end = -1;
            for (int i = open; i < json.Length; i++)
            {
                if (json[i] == '[') depth++;
                else if (json[i] == ']') { depth--; if (depth == 0) { end = i; break; } }
            }
            if (end < 0) return null;

            return json.Substring(0, open) + newArray + json.Substring(end + 1);
        }

        static string BuildItemsArray(List<GroupItem> items)
        {
            var sb = new StringBuilder();
            sb.Append("[\r\n");
            items ??= new List<GroupItem>();
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                sb.Append("    { \"caption\": ").Append(JStr(it.Caption))
                  .Append(", \"path\": ").Append(JStr(it.Path))
                  .Append(", \"icon\": ").Append(JStr(it.Icon))
                  .Append(", \"bgColor\": ").Append(JColor(it.BgColor))
                  .Append(", \"bgAlpha\": ").Append(it.BgAlpha?.ToString() ?? "null")
                  .Append(" }");
                if (i < items.Count - 1) sb.Append(',');
                sb.Append("\r\n");
            }
            sb.Append("  ]");
            return sb.ToString();
        }

        /// <summary>用模板生成全新宫格文件（带完整注释）。</summary>
        static string RebuildWithComments(GroupFile g)
        {
            try
            {
                string t = Template.Replace("{{NAME}}", g.Name ?? "未命名");

                if (g.Position != null)
                {
                    t = ReplaceNum(t, "col", g.Position.Col);
                    t = ReplaceNum(t, "row", g.Position.Row);
                    t = ReplaceNum(t, "x", g.Position.X);
                    t = ReplaceNum(t, "y", g.Position.Y);
                }

                return ReplaceArray(t, "\"items\"", BuildItemsArray(g.Items));
            }
            catch (Exception ex) { Log("模板重建失败: " + ex.Message); return null; }
        }

        static string ReplaceNum(string json, string key, int v) =>
            System.Text.RegularExpressions.Regex.Replace(
                json, "(\"" + key + "\"\\s*:\\s*)-?\\d+", "${1}" + v);

        static string JStr(string s) => s == null ? "null" : JsonSerializer.Serialize(s, Json);

        static string JColor(int[] c) =>
            c == null || c.Length < 3 ? "null" : "[" + c[0] + ", " + c[1] + ", " + c[2] + "]";

        /// <summary>
        /// 给新建的宫格找一个不与现有宫格重叠的格位。
        /// 不做这件事的话新宫格会带着"未设置"的占位值跑到屏幕左上角 (0,0)，
        /// 和已有宫格、桌面图标叠在一起。
        /// </summary>
        public static void PickInitialPosition(GroupFile g, AppConfig cfg, IEnumerable<GroupFile> existing)
        {
            var s = cfg.Size;
            int stepX = cfg.Behaviour.GridStepX > 0 ? cfg.Behaviour.GridStepX : 83;
            int stepY = cfg.Behaviour.GridStepY > 0 ? cfg.Behaviour.GridStepY : 114;

            var wa = Screen.PrimaryScreen.WorkingArea;
            int maxCol = Math.Max(0, (wa.Width - s.Width) / stepX);
            int maxRow = Math.Max(0, (wa.Height - s.Height) / stepY);

            // 先用现有宫格占位；实际桌面图标占用由覆盖层启动时再避让
            var taken = new List<Rectangle>();
            foreach (var o in existing)
            {
                if (o == g || o?.Position == null) continue;
                if (o.Position.Col == PositionSection.Unset || o.Position.Row == PositionSection.Unset) continue;
                taken.Add(new Rectangle(o.Position.Col, o.Position.Row, 1, 1));
            }

            // 从右下往左上找最近的空位（和大多数人想放的位置一致）
            for (int r = maxRow; r >= 0; r--)
            {
                for (int c = maxCol; c >= 0; c--)
                {
                    bool clash = false;
                    foreach (var t in taken)
                        if (t.X == c && t.Y == r) { clash = true; break; }
                    if (clash) continue;

                    g.Position.Col = c;
                    g.Position.Row = r;
                    g.Position.X = c * stepX + wa.Left;
                    g.Position.Y = r * stepY + wa.Top;
                    Log("[新建宫格] 自动选格位 col=" + c + " row=" + r);
                    return;
                }
            }

            // 全占满就只能放右上角
            g.Position.Col = maxCol;
            g.Position.Row = 0;
            g.Position.X = maxCol * stepX + wa.Left;
            g.Position.Y = wa.Top;
            Log("[新建宫格] 已无空位，放在 col=" + maxCol + " row=0");
        }

        /// <summary>
        /// 新建宫格文件。刻意手写模板而不用序列化 ——
        /// 这样新文件自带中文注释说明，用户和 AI 都能直接看懂，与手工写的文件一致。
        /// </summary>
        public static bool CreateGroupFile(string file, string name, bool overwrite = false)
        {
            string p = Path.Combine(AppDir, file);
            if (File.Exists(p) && !overwrite) { Log("已存在，不覆盖: " + p); return false; }
            try
            {
                string t = Template.Replace("{{NAME}}", name);
                File.WriteAllText(p, t, new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex) { Log("新建失败: " + ex.Message); return false; }
        }

        const string Template = @"{
  ""_说明"": ""═══════════════════════════════════════════════════════════════"",
  ""_说明2"": ""这是「{{NAME}}」这个四宫格的内容文件。4 个格子的顺序是固定的："",
  ""_顺序"": ""  items[0] = 左上    items[1] = 右上"",
  ""_顺序2"": ""  items[2] = 左下    items[3] = 右下"",
  ""_说明3"": ""改完保存，在四宫格上点右键 →「重新载入全部配置」即可生效；或用管理器保存。"",
  ""_说明4"": ""以 _ 开头的字段都是注释，程序会忽略。可以把本文件发给他人或 AI 让它帮你改。"",
  ""_说明5"": ""═══════════════════════════════════════════════════════════════"",

  ""name"": ""{{NAME}}"",
  ""_name说明"": ""宫格名称。"",

  ""position"": {
    ""_说明"": ""宫格位置。只有手动拖动会改它 —— 在桌面上把宫格拖到位，菜单里点「记住当前位置」即可。"",
    ""col"": -2147483648,
    ""_col说明"": ""★ 图标网格列号（从 0 开始）。屏幕横坐标 = 83 × col。分辨率无关，换屏幕后仍有效。"",
    ""row"": -2147483648,
    ""_row说明"": ""★ 图标网格行号（从 0 开始）。屏幕纵坐标 = 114 × row。"",
    ""x"": -2147483648,
    ""_x说明"": ""屏幕像素横坐标。由 col 换算而来，仅供人肉核对。"",
    ""y"": -2147483648,
    ""_y说明"": ""屏幕像素纵坐标。由 row 换算而来，仅供人肉核对。""
  },

  ""defaults"": {
    ""_说明"": ""本宫格所有格子的默认外观，可在下面单个 item 里覆盖。"",
    ""icon"": null,
    ""_icon说明"": ""图标来源。null = 自动：把 path 解析到真正的 .exe 后提取（所以没有快捷方式小箭头）。也可填 .ico / .png 路径。""
  },

  ""items"": [
    { ""caption"": ""程序1"", ""path"": """", ""icon"": null, ""bgColor"": null, ""bgAlpha"": null,
      ""_说明"": ""caption = 格子里显示的字（格子窄，建议短别名）；path = 要启动的文件，.lnk / .exe / .url 都行；bgColor = 本格底板 [R,G,B]，null = 用总配置；bgAlpha = 本格底板透明度 0~255，null = 用总配置。"" },
    { ""caption"": ""程序2"", ""path"": """", ""icon"": null, ""bgColor"": null, ""bgAlpha"": null },
    { ""caption"": ""程序3"", ""path"": """", ""icon"": null, ""bgColor"": null, ""bgAlpha"": null },
    { ""caption"": ""程序4"", ""path"": """", ""icon"": null, ""bgColor"": null, ""bgAlpha"": null }
  ]
}
";
    }

    #region 图标提取（管理器只用于显示，不参与覆盖层渲染）

    internal static class Icons
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO info, uint cb, uint flags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr h);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEINFO
        {
            public IntPtr hIcon; public int iIcon; public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0;

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLinkCoClass { }

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder f, int c, IntPtr pfd, uint fl);
            void GetIDList(out IntPtr p); void SetIDList(IntPtr p);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder n, int c);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string n);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder d, int c);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string d);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder a, int c);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string a);
            void GetHotkey(out short k); void SetHotkey(short k);
            void GetShowCmd(out int s); void SetShowCmd(int s);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder p, int c, out int i);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string p, int i);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string p, uint r);
            void Resolve(IntPtr h, uint f);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string p);
        }

        [ComImport, Guid("0000010b-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPersistFile
        {
            void GetClassID(out Guid g);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string f, uint m);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string f, bool r);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string f);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string f);
        }

        /// <summary>把 .lnk 解析成目标路径（去快捷方式小箭头的关键）。</summary>
        public static string ResolveShortcut(string lnk)
        {
            object o = null;
            try
            {
                o = new ShellLinkCoClass();
                ((IPersistFile)o).Load(lnk, 0);
                var sb = new StringBuilder(1024);
                ((IShellLinkW)o).GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                string t = sb.ToString();
                return string.IsNullOrWhiteSpace(t) ? null : t;
            }
            catch { return null; }
            finally { if (o != null) Marshal.ReleaseComObject(o); }
        }

        public static Icon Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            path = Environment.ExpandEnvironmentVariables(path.Trim());
            if (!File.Exists(path)) return null;

            string ext = Path.GetExtension(path).ToLowerInvariant();

            // .lnk -> 解析到目标 exe，去掉小箭头
            if (ext == ".lnk")
            {
                string t = ResolveShortcut(path);
                if (!string.IsNullOrEmpty(t) && File.Exists(t))
                {
                    var ic = FromExe(t) ?? FromShell(t);
                    if (ic != null) return ic;
                }
                return FromShell(path);   // 解析不了只能带箭头
            }

            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp")
            {
                try
                {
                    using (var src = new Bitmap(path))
                    {
                        var bmp = new Bitmap(src, new Size(32, 32));
                        IntPtr h = bmp.GetHicon();
                        try { return (Icon)Icon.FromHandle(h).Clone(); }
                        finally { DestroyIcon(h); }
                    }
                }
                catch { return null; }
            }

            return FromExe(path) ?? FromShell(path);
        }

        static Icon FromExe(string file)
        {
            try
            {
                var large = new IntPtr[1];
                if (ExtractIconEx(file, 0, large, null, 1) == 0 || large[0] == IntPtr.Zero) return null;
                try { return (Icon)Icon.FromHandle(large[0]).Clone(); }
                finally { DestroyIcon(large[0]); }
            }
            catch { return null; }
        }

        static Icon FromShell(string path)
        {
            try
            {
                var info = new SHFILEINFO();
                IntPtr r = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info),
                                         SHGFI_ICON | SHGFI_LARGEICON);
                if (r == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
                try { return (Icon)Icon.FromHandle(info.hIcon).Clone(); }
                finally { DestroyIcon(info.hIcon); }
            }
            catch { return null; }
        }
    }

    #endregion
}
