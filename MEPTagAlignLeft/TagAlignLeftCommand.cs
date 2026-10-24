using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TagAlignLeft
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Command : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;

            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;

            // 1. 检查视图类型
            if (activeView == null || !IsSupportedView(activeView))
            {
                TaskDialog.Show("提示",
                    "请在二维视图（平面、结构平面、天花板平面、立面、剖面、详图等）中运行此命令。");
                return Result.Cancelled;
            }

            // 2. 获取选中的标记
            List<IndependentTag> tags = Preselected.GetSelectedIndependentTags(uidoc, activeView);
            if (tags.Count < 2)
            {
                TaskDialog.Show("提示", "请先选中至少两个标记（Tag），再运行命令。");
                return Result.Cancelled;
            }

            // 3. 从配置文件读取行距（Revit 内部单位：英尺）
            double rowSpacing = GetRowSpacingFromConfig();

            // 4. 计算移动向量
            Dictionary<ElementId, XYZ> moveVectors =
                CalculateIndependentTag.CalculateStackedLeftMoves(
                    doc, activeView, tags, rowSpacing);

            if (moveVectors == null || moveVectors.Count == 0)
            {
                TaskDialog.Show("提示", "所有标记均已对齐，或无法计算边界框。");
                return Result.Cancelled;
            }

            // 5. 执行移动
            using (Transaction trans = new Transaction(doc, "左对齐并堆叠标记"))
            {
                trans.Start();
                foreach (KeyValuePair<ElementId, XYZ> kvp in moveVectors)
                {
                    ElementTransformUtils.MoveElement(doc, kvp.Key, kvp.Value);
                }
                trans.Commit();
            }

            return Result.Succeeded;
        }

        /// <summary>
        /// 从 DLL 同级目录下的 TagAlignLeft.config 读取行距。
        /// 逐行解析，支持 // 注释。
        /// </summary>
        private static double GetRowSpacingFromConfig()
        {
            double defaultSpacing = 0.50;
            try
            {
                string assemblyPath = Assembly.GetExecutingAssembly().Location;
                string directory = Path.GetDirectoryName(assemblyPath);
                string configPath = Path.Combine(directory, "TagAlignLeft.config");

                if (File.Exists(configPath))
                {
                    // 逐行读取文件
                    string[] lines = File.ReadAllLines(configPath);
                    foreach (string line in lines)
                    {
                        string trimmedLine = line.Trim();

                        // 跳过空行和注释行
                        if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("//"))
                            continue;

                        // 尝试解析第一个找到的有效数字
                        if (double.TryParse(trimmedLine, out double spacing) && spacing > 0)
                        {
                            return spacing;
                        }
                    }
                }
                else
                {
                    // 如果文件不存在，自动生成默认配置
                    File.WriteAllText(configPath, 
                        "0.50\r\n\r\n" +
                        "// 此处修改 Tag 堆叠行距 (Revit内部单位：英尺)\r\n" +
                        "// 1.0 ft = 304.8 mm\r\n" +
                        "// 修改后保存，无需重新编译插件。");
                }
            }
            catch
            {
                // 如果读写文件发生异常，回退到默认值
            }

            return defaultSpacing;
        }

        /// <summary>
        /// 判断是否为可用于"左对齐 + 堆叠"的二维视图。
        /// </summary>
        private static bool IsSupportedView(View view)
        {
            switch (view.ViewType)
            {
                case ViewType.FloorPlan:        // 建筑平面
                case ViewType.CeilingPlan:      // 天花板平面
                case ViewType.EngineeringPlan:  // 结构平面 / 工程平面
                case ViewType.AreaPlan:         // 面积平面
                case ViewType.Elevation:        // 立面
                case ViewType.Section:          // 剖面
                case ViewType.Detail:           // 详图
                    return true;
                default:
                    return false;
            }
        }
    }
}