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
                string asmPath = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(asmPath)) dir = Path.GetDirectoryName(asmPath);
            }
            catch { }

            if (string.IsNullOrEmpty(dir))
            {
                _values = dict;
                return;
            }

            _configPath = Path.Combine(dir, "MEPTools.config");
            string legacyPath = Path.Combine(dir, "TagAlignLeft.config");

            if (File.Exists(_configPath))
            {
                LoadKeyValueFile(_configPath, dict);
            }
            else if (File.Exists(legacyPath))
            {
                TryLoadLegacyRowSpacing(legacyPath, dict);
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

        private static void TryLoadLegacyRowSpacing(string path, Dictionary<string, string> dict)
        {
            try
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    string t = line.Trim();
                    if (t.Length == 0 || t.StartsWith("//")) continue;
                    if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0)
                    {
                        dict["TagAlign.RowSpacing"] = t;
                        break;
                    }
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

        // ============================================================
        // 新增：设置与保存 API
        // ============================================================
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
                    
                    foreach (var kv in Defaults)
                    {
                        if (_values.TryGetValue(kv.Key, out string val))
                            sb.AppendLine(kv.Key + "=" + val);
                        else
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