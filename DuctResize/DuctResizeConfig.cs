// ============================================================
// 文件：DuctResizeConfig.cs
// 位置：DuctResize 文件夹
// 修改重点：配置从独立 DuctResizeConfig.txt 迁移到 Shared/MEPConfig
//          （%LocalAppData%\MEPTools\MEPTools.config 的 DuctResize.* 键）
// ============================================================
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MEPTools.Shared;

namespace MEPTools.DuctResize
{
    /// <summary>
    /// 风管尺寸修改的配置对象。
    /// 持久化统一走 Shared/MEPConfig，不再自己维护配置文件。
    /// </summary>
    public class ResizeConfig
    {
        public double TargetWidthMm { get; set; } = 150.0;
        public double TargetHeightMm { get; set; } = 400.0;
        public List<string> WidthParams { get; set; } = new List<string>();
        public List<string> HeightParams { get; set; } = new List<string>();

        /// <summary>
        /// 从共享配置读取。默认值由 MEPConfig 内置默认表提供。
        /// </summary>
        public static ResizeConfig Load()
        {
            var config = new ResizeConfig
            {
                TargetWidthMm = MEPConfig.GetDouble("DuctResize.TargetWidth", 150.0),
                TargetHeightMm = MEPConfig.GetDouble("DuctResize.TargetHeight", 400.0),
                // fallback 传 null：允许把参数名清空（等价于“不使用兜底参数名策略”）
                WidthParams = ToList(MEPConfig.GetStringArray("DuctResize.WidthParams", ',', null)),
                HeightParams = ToList(MEPConfig.GetStringArray("DuctResize.HeightParams", ',', null)),
            };

            // 尺寸兜底：0/负数写进参数会直接失败，统一修正为默认值
            if (config.TargetWidthMm <= 0) config.TargetWidthMm = 150.0;
            if (config.TargetHeightMm <= 0) config.TargetHeightMm = 400.0;

            return config;
        }

        /// <summary>
        /// 写回共享配置（MEPConfig.Save 只重写 MEPTools.config，不产生 Revit 文档写操作）。
        /// </summary>
        public void Save()
        {
            MEPConfig.SetValue("DuctResize.TargetWidth",
                TargetWidthMm.ToString("0.###", CultureInfo.InvariantCulture));
            MEPConfig.SetValue("DuctResize.TargetHeight",
                TargetHeightMm.ToString("0.###", CultureInfo.InvariantCulture));
            MEPConfig.SetValue("DuctResize.WidthParams", Join(WidthParams));
            MEPConfig.SetValue("DuctResize.HeightParams", Join(HeightParams));
            MEPConfig.Save();
        }

        private static string Join(List<string> list)
        {
            if (list == null) return string.Empty;
            return string.Join(",", list.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
        }

        private static List<string> ToList(string[] arr)
        {
            if (arr == null) return new List<string>();
            return arr.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
        }
    }
}
