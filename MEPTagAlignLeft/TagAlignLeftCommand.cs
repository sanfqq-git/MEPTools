using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.Shared;

namespace MEPTools.MEPTagAlignLeft
{
    /// <summary>
    /// 命令公共辅助类：处理视图检查、Tag 获取、配置读取、事务执行等。
    /// </summary>
    internal static class CommandHelper
    {
        public static bool CheckView(View view)
        {
            if (view == null || !IsSupportedView(view))
            {
                TaskDialog.Show("提示",
                    "请在二维视图（平面、结构平面、天花板平面、立面、剖面、详图等）中运行此命令。");
                return false;
            }
            return true;
        }

        public static List<IndependentTag> GetTags(UIDocument uidoc, View view)
        {
            List<IndependentTag> tags = Preselected.GetSelectedIndependentTags(uidoc, view);
            if (tags.Count < 2)
            {
                TaskDialog.Show("提示", "请先选中至少两个标记（Tag），再运行命令。");
                return null;
            }
            return tags;
        }

        /// <summary>
        /// 从 DLL 同级目录下的 TagAlignLeft.config 读取行距。
        /// 逐行解析，支持 // 注释。
        /// </summary>
        public static double GetRowSpacingFromConfig()
        {
            double defaultSpacing = 0.50;
            try
            {
                string assemblyPath = Assembly.GetExecutingAssembly().Location;
                string directory = Path.GetDirectoryName(assemblyPath);
                string configPath = Path.Combine(directory, "TagAlignLeft.config");

                if (File.Exists(configPath))
                {
                    string[] lines = File.ReadAllLines(configPath);
                    foreach (string line in lines)
                    {
                        string trimmedLine = line.Trim();
                        if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("//"))
                            continue;

                        if (double.TryParse(trimmedLine, out double spacing) && spacing > 0)
                        {
                            return spacing;
                        }
                    }
                }
                else
                {
                    File.WriteAllText(configPath, 
                        "0.50\r\n\r\n" +
                        "// 此处修改 Tag 堆叠行距 (Revit内部单位：英尺)\r\n" +
                        "// 1.0 ft = 304.8 mm\r\n" +
                        "// 修改后保存，无需重新编译插件。");
                }
            }
            catch
            {
                // 异常时回退到默认值
            }

            return defaultSpacing;
        }

        /// <summary>
        /// 执行移动向量（包含事务包装及异常回滚）。
        /// </summary>
        public static void ExecuteMoves(Document doc, string transactionName, Dictionary<ElementId, XYZ> moveVectors)
        {
            if (moveVectors == null || moveVectors.Count == 0)
            {
                TaskDialog.Show("提示", "所有标记均已处于目标状态，无需移动。");
                return;
            }

            using (Transaction trans = new Transaction(doc, transactionName))
            {
                trans.Start();
                try
                {
                    foreach (KeyValuePair<ElementId, XYZ> kvp in moveVectors)
                    {
                        ElementTransformUtils.MoveElement(doc, kvp.Key, kvp.Value);
                    }
                    trans.Commit();
                }
                catch (Exception ex)
                {
                    trans.RollBack();
                    MEPLogger.Instance.LogException(transactionName, ex);
                    TaskDialog.Show("执行失败", $"移动标记时发生错误，可能元素被锁定或视图受限：\n{ex.Message}");
                }
            }
        }

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

    /// <summary>
    /// 【图标 C】主功能：左对齐 + 等距排序
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Command : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;

            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;

            if (!CommandHelper.CheckView(activeView)) return Result.Cancelled;

            List<IndependentTag> tags = CommandHelper.GetTags(uidoc, activeView);
            if (tags == null) return Result.Cancelled;

            double rowSpacing = CommandHelper.GetRowSpacingFromConfig();
            var moveVectors = CalculateIndependentTag.CalculateStackedLeftMoves(activeView, tags, rowSpacing);
            
            CommandHelper.ExecuteMoves(doc, "左对齐并等距排序标记", moveVectors);

            return Result.Succeeded;
        }
    }

    /// <summary>
    /// 【图标 A】仅左对齐
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CommandAlignLeftOnly : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;

            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;

            if (!CommandHelper.CheckView(activeView)) return Result.Cancelled;

            List<IndependentTag> tags = CommandHelper.GetTags(uidoc, activeView);
            if (tags == null) return Result.Cancelled;

            var moveVectors = CalculateIndependentTag.CalculateLeftAlignMoves(activeView, tags);
            
            CommandHelper.ExecuteMoves(doc, "左对齐标记", moveVectors);

            return Result.Succeeded;
        }
    }

    /// <summary>
    /// 【图标 B】仅等距排序
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CommandStackOnly : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;

            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;

            if (!CommandHelper.CheckView(activeView)) return Result.Cancelled;

            List<IndependentTag> tags = CommandHelper.GetTags(uidoc, activeView);
            if (tags == null) return Result.Cancelled;

            double rowSpacing = CommandHelper.GetRowSpacingFromConfig();
            var moveVectors = CalculateIndependentTag.CalculateStackMoves(activeView, tags, rowSpacing);
            
            CommandHelper.ExecuteMoves(doc, "等距排序标记", moveVectors);

            return Result.Succeeded;
        }
    }
}