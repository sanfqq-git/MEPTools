// ============================================================
// 文件：MEPConfig.cs
// 位置：Shared 文件夹
// ============================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace MEPTools.Shared
{
    public static class MEPConfig
    {
        private static readonly object _lock = new object();
        private static volatile Dictionary<string, string> _values;
        private static string _configPath;

        public static string ConfigPath { get { EnsureLoaded(); return _configPath; } }

        private static readonly List<KeyValuePair<string, string>> Defaults =
            new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string,string>("TaskDialog.Title",                "提示"),
            new KeyValuePair<string,string>("TagAlign.RowSpacing",             "0.50"),
            new KeyValuePair<string,string>("TagAlign.Epsilon",                "1e-9"),
            new KeyValuePair<string,string>("TagAlign.TxName.StackedLeft",     "左对齐并等距排序标记"),
            new KeyValuePair<string,string>("TagAlign.TxName.LeftOnly",        "左对齐标记"),
            new KeyValuePair<string,string>("TagAlign.TxName.StackOnly",       "等距排序标记"),
            new KeyValuePair<string,string>("TagAlign.Msg.View.Unsupported",   "请在二维视图（平面、结构平面、天花板平面、立面、剖面、详图等）中运行此命令。"),
            new KeyValuePair<string,string>("TagAlign.Msg.Selection.NeedTwo",  "请先选中至少两个标记（Tag），再运行命令。"),
            new KeyValuePair<string,string>("TagAlign.Msg.Move.NoNeed",        "所有标记均已处于目标状态，无需移动。"),
            new KeyValuePair<string,string>("TagAlign.Msg.Move.Fail.Title",    "执行失败"),
            new KeyValuePair<string,string>("TagAlign.Msg.Move.Fail.Body",     "移动标记时发生错误，可能元素被锁定或视图受限："),
            new KeyValuePair<string,string>("Selection.DefaultPrompt",         "请在视图中选择元素，按空格键结束"),
            new KeyValuePair<string,string>("Selection.Msg.IgnoredCount",      "已忽略 {0} 个不符合要求的元素"),
            new KeyValuePair<string,string>("Selection.Msg.SingleOnly",        "当前操作仅支持单选，请只选择一个元素后重试"),
            new KeyValuePair<string,string>("Selection.Msg.Cancelled",         "操作已取消"),
            new KeyValuePair<string,string>("Selection.Msg.PickError",         "选择过程中发生错误，请查看日志"),
            new KeyValuePair<string,string>("Selection.Msg.NotEnough",         "有效元素不足 {0} 个，操作已退出"),
            new KeyValuePair<string,string>("SpaceKey.ButtonTexts",            "完成;Finish;Complete"),
            new KeyValuePair<string,string>("InfoCenter.DisplaySeconds",       "5"),
            new KeyValuePair<string,string>("Logger.BaseFolder",               "MEPTools"),
            new KeyValuePair<string,string>("Logger.LogsSubFolder",            "Logs"),
            new KeyValuePair<string,string>("Logger.FilePrefix",               "mep_"),
            new KeyValuePair<string,string>("MEPAlignment.HorizontalEps",      "1e-6"),
            new KeyValuePair<string,string>("MEPAlignment.AlignEps",           "1e-4"),

            new KeyValuePair<string,string>("TagGroup.Tolerance",              "25.0"),
            new KeyValuePair<string,string>("TagGroup.LineSpacing",            "200.0"),
            new KeyValuePair<string,string>("TagGroup.BlankLines",             "2"),
            new KeyValuePair<string,string>("TagGroup.ExtraRules",             ""),
            new KeyValuePair<string,string>("TagGroup.MainDuctTag",            ""),
            new KeyValuePair<string,string>("TagGroup.MainPipeTag",            ""),
            new KeyValuePair<string,string>("TagGroup.MainTrayTag",            ""),
            // 排版完成后是否把本次生成的 Tag 全部选中（关掉可省一次高亮刷新，约 20+ ms）
            new KeyValuePair<string,string>("TagGroup.SelectCreatedTags",      "true"),

            // ================= BoxLeader：方形引线 =================
            new KeyValuePair<string,string>("Leader.W",                        "50"),
            new KeyValuePair<string,string>("Leader.EQ",                       "50"),
            new KeyValuePair<string,string>("Leader.LineStyle",                ""),

            // ================= ExportLocal：LINK/BIM360 输出到本地 =================
            new KeyValuePair<string,string>("ExportLocal.LinkedSubFolder",      "LinkedModels"),
            new KeyValuePair<string,string>("ExportLocal.CloudSubFolder",       "CloudModels"),
            new KeyValuePair<string,string>("ExportLocal.CopyLocalSource",      "true"),
            new KeyValuePair<string,string>("ExportLocal.SaveCurrentCloudModel","true"),
            new KeyValuePair<string,string>("ExportLocal.OverwriteExisting",    "false"),

            new KeyValuePair<string,string>("ExportLocal.Msg.SummaryTitle",     "输出 LINK / BIM360 文件到本地"),
            new KeyValuePair<string,string>("ExportLocal.Msg.SummaryBody",
                "输出完成。\n成功: {0}\n失败: {1}\n跳过: {2}\n\n输出目录:\n{3}"),
            new KeyValuePair<string,string>("ExportLocal.Msg.CurrentCloudAsLocal",
                "当前云模型已另存为本地副本：\n{0}\n\n注意：当前文档现已切换为本地文档。\n如需继续编辑原云文档，请从 BIM360/ACC 重新打开。"),
            new KeyValuePair<string,string>("ExportLocal.Msg.NoLinks",
                "当前文档中没有链接模型，仅处理当前文档本身。"),
            new KeyValuePair<string,string>("ExportLocal.Msg.Failed",
                "输出 LINK / BIM360 文件失败，请查看日志。"),
            new KeyValuePair<string,string>("ExportLocal.Msg.CancelledFolder",
                "未选择输出目录，操作已取消。"),

            // ================= ChangeRefLevel：修改参照标高（位置保持不变） =================
            new KeyValuePair<string,string>("ChangeRefLevel.FormTitle",         "选择目标参照标高"),
            new KeyValuePair<string,string>("ChangeRefLevel.FormLabel",         "请选择目标参照标高："),
            new KeyValuePair<string,string>("ChangeRefLevel.ButtonOk",          "确定"),
            new KeyValuePair<string,string>("ChangeRefLevel.ButtonCancel",      "取消"),
            new KeyValuePair<string,string>("ChangeRefLevel.PickPrompt",
                "请选择要修改参照标高的元素（按空格键结束）"),
            new KeyValuePair<string,string>("ChangeRefLevel.Msg.SummaryTitle",  "修改参照标高"),
            new KeyValuePair<string,string>("ChangeRefLevel.Msg.SummaryBody",
                "修改完成：\n成功 {0} 个\n跳过 {1} 个\n失败 {2} 个\n\n目标标高：{3}"),
            new KeyValuePair<string,string>("ChangeRefLevel.PositionToleranceMm", "0.01"),

            // ================= DuctResize：修改风管/管件尺寸（原 DuctResizeConfig.txt 迁移到此处） =================
            new KeyValuePair<string,string>("DuctResize.TargetWidth",           "150"),
            new KeyValuePair<string,string>("DuctResize.TargetHeight",          "400"),
            new KeyValuePair<string,string>("DuctResize.WidthParams",
                "Width,宽度,Duct Width 1,Duct Width 2,Duct Width 3"),
            new KeyValuePair<string,string>("DuctResize.HeightParams",
                "Height,高度,Duct Height"),
            // 尺寸值比较容差（mm）：差值在此范围内视为“已是目标值”，直接跳过，不产生写操作
            new KeyValuePair<string,string>("DuctResize.SizeToleranceMm",       "0.001"),
            new KeyValuePair<string,string>("DuctResize.PickPrompt",
                "请选择风管或管件，按【空格键】结束"),
            new KeyValuePair<string,string>("DuctResize.Msg.NoPreselection",
                "未检测到预选：请在视图中选择风管或管件，按空格键结束"),
            new KeyValuePair<string,string>("DuctResize.Msg.NoValid",
                "所选元素中没有可处理的风管或管件"),
            new KeyValuePair<string,string>("DuctResize.Msg.Cancelled",         "已取消"),
            new KeyValuePair<string,string>("DuctResize.Msg.NoChange",
                "尺寸已是目标值，未做任何修改"),
            new KeyValuePair<string,string>("DuctResize.Msg.Summary",
                "已完成：风管 {0} 根，管件 {1} 个（无需修改 {2} 个）"),
            new KeyValuePair<string,string>("DuctResize.Msg.SummaryFailed",
                "；失败 {0} 个，详见日志"),
            new KeyValuePair<string,string>("DuctResize.Msg.Error",             "执行出错：{0}"),
        };

        private static void EnsureLoaded()
        {
            if (_values != null) return;
            lock (_lock)
            {
                if (_values != null) return;
                LoadCore();
            }
        }

        private static void LoadCore()
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in Defaults) dict[kv.Key] = kv.Value;

            string dir = null;
            try
            {
                // 核心修复：强制写入到 LocalAppData，避开 C 盘 Program Files 权限问题
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MEPTools");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }
            catch { }

            if (string.IsNullOrEmpty(dir))
            {
                _values = dict;
                return;
            }

            _configPath = Path.Combine(dir, "MEPTools.config");

            if (File.Exists(_configPath))
            {
                LoadKeyValueFile(_configPath, dict);
            }
            else
            {
                TryWriteDefaultConfig(_configPath);
            }

            _values = dict;
        }

        private static void LoadKeyValueFile(string path, Dictionary<string, string> dict)
        {
            try
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    string l = line.Trim();
                    if (l.Length == 0 || l.StartsWith("#") || l.StartsWith("//")) continue;
                    int idx = l.IndexOf('=');
                    if (idx <= 0) continue;
                    string key = l.Substring(0, idx).Trim();
                    string val = l.Substring(idx + 1).Trim();
                    if (key.Length > 0) dict[key] = val;
                }
            }
            catch { }
        }

        private static void TryWriteDefaultConfig(string path)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# =================================================");
                sb.AppendLine("# MEPTools 全局配置文件");
                sb.AppendLine("# 修改后保存，重启 Revit 或下次运行命令时自动生效");
                sb.AppendLine("# 缺失项将自动使用程序内置默认值");
                sb.AppendLine("# =================================================");
                sb.AppendLine();
                foreach (var kv in Defaults)
                    sb.AppendLine(kv.Key + "=" + kv.Value);
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        public static string GetString(string key, string fallback = null)
        {
            EnsureLoaded();
            if (_values.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)) return v;
            return fallback;
        }

        public static double GetDouble(string key, double fallback)
        {
            string s = GetString(key);
            if (s == null) return fallback;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }

        public static int GetInt(string key, int fallback)
        {
            string s = GetString(key);
            if (s == null) return fallback;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }

        /// <summary>
        /// 读取布尔值，支持 "true"/"false" 以及 "1"/"0"。
        /// </summary>
        public static bool GetBool(string key, bool fallback)
        {
            string s = GetString(key);
            if (string.IsNullOrEmpty(s)) return fallback;
            if (bool.TryParse(s, out var b)) return b;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i != 0;
            return fallback;
        }

        public static string[] GetStringArray(string key, char separator, string[] fallback)
        {
            string s = GetString(key);
            if (string.IsNullOrEmpty(s)) return fallback;
            var parts = s.Split(new[] { separator }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(p => p.Trim())
                         .Where(p => p.Length > 0)
                         .ToArray();
            return parts.Length > 0 ? parts : fallback;
        }

        public static void SetValue(string key, string value)
        {
            EnsureLoaded();
            lock (_lock)
            {
                if (_values.ContainsKey(key))
                    _values[key] = value;
                else
                    _values.Add(key, value);
            }
        }

        public static bool Save()
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(_configPath)) return false;

            lock (_lock)
            {
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("# =================================================");
                    sb.AppendLine("# MEPTools 全局配置文件");
                    sb.AppendLine("# 修改后保存，重启 Revit 或下次运行命令时自动生效");
                    sb.AppendLine("# 缺失项将自动使用程序内置默认值");
                    sb.AppendLine("# =================================================");
                    sb.AppendLine();

                    foreach (var kv in _values)
                    {
                        sb.AppendLine(kv.Key + "=" + kv.Value);
                    }

                    File.WriteAllText(_configPath, sb.ToString(), new UTF8Encoding(false));
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}