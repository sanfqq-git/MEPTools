using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MEPTools.DuctResize
{
    public class ResizeConfig
    {
        public double TargetWidthMm { get; set; } = 150.0;
        public double TargetHeightMm { get; set; } = 400.0;
        public List<string> WidthParams { get; set; } = new List<string>();
        public List<string> HeightParams { get; set; } = new List<string>();
    }

    public static class ResizeConfigStorage
    {
        public static string ConfigPath
        {
            get
            {
                // 统一保存到 %LocalAppData%\MEPTools，规避系统盘无权限问题
                string configDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MEPTools");
                Directory.CreateDirectory(configDir);
                return Path.Combine(configDir, "DuctResizeConfig.txt");
            }
        }

        public static ResizeConfig Load()
        {
            var config = new ResizeConfig
            {
                WidthParams = new List<string> { "Width", "宽度", "Duct Width 1", "Duct Width 2", "Duct Width 3" },
                HeightParams = new List<string> { "Height", "高度", "Duct Height" },
            };

            string path = ConfigPath;
            if (!File.Exists(path)) return config;

            try
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "TargetWidth":
                            if (double.TryParse(val, out double w)) config.TargetWidthMm = w;
                            break;
                        case "TargetHeight":
                            if (double.TryParse(val, out double h)) config.TargetHeightMm = h;
                            break;
                        case "WidthParams":
                            config.WidthParams = SplitCsv(val);
                            break;
                        case "HeightParams":
                            config.HeightParams = SplitCsv(val);
                            break;
                    }
                }
            }
            catch { }

            return config;
        }

        public static void Save(ResizeConfig config)
        {
            try
            {
                File.WriteAllLines(ConfigPath, new[]
                {
                    "# 目标尺寸设置 (单位: mm)",
                    "TargetWidth=" + config.TargetWidthMm,
                    "TargetHeight=" + config.TargetHeightMm,
                    "",
                    "# 宽度参数名 (多个参数用英文逗号分隔，用于管件)",
                    "WidthParams=" + string.Join(",", config.WidthParams ?? new List<string>()),
                    "",
                    "# 高度参数名 (多个参数用英文逗号分隔，用于管件)",
                    "HeightParams=" + string.Join(",", config.HeightParams ?? new List<string>()),
                });
            }
            catch { }
        }

        private static List<string> SplitCsv(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return new List<string>();
            return s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => x.Length > 0)
                    .ToList();
        }
    }
}