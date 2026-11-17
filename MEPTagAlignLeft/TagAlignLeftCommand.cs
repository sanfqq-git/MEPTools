using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using MEPTools.Shared;

namespace MEPTools.MEPTagAlignLeft
{
    /// <summary>
    /// 用于 MEPSelectionHelper 的过滤器，支持独立标记、组和多类别标记
    /// </summary>
    public class TagSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            return elem is IndependentTag || elem is Group || elem is MultiReferenceAnnotation;
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            return false;
        }
    }

    internal static class CommandHelper
    {
        public static bool CheckView(View view)
        {
            if (view == null || !IsSupportedView(view))
            {
                // 改用 InfoCenterTip 非阻塞提示
                InfoCenterTip.Show(MEPConfig.GetString("TagAlign.Msg.View.Unsupported",
                    "请在二维视图（平面、结构平面、天花板平面、立面、剖面、详图等）中运行此命令。"));
                return false;
            }
            return true;
        }

        public static List<IndependentTag> GetTags(UIDocument uidoc, View view)
        {
            // 1. 尝试获取预选
            List<IndependentTag> tags = Preselected.GetSelectedIndependentTags(uidoc, view);
            
            // 2. 预选不足，启动交互式选择
            if (tags.Count < 2)
            {
                InfoCenterTip.Show(MEPConfig.GetString("TagAlign.Msg.Selection.NeedTwo",
                    "请先选中至少两个标记（Tag），正在启动选择模式..."));

                // 调用共享类 MEPSelectionHelper（内部已包含空格键确认与 InfoCenter 提示）
                var filter = new TagSelectionFilter();
                var elements = MEPSelectionHelper.GetElements(
                    uidoc,
                    filter,
                    "请选择至少两个标记（Tag）",
                    "请在视图中选择标记，按空格键确认结束",
                    2,
                    useSpaceKey: true,
                    singlePick: false);

                if (elements == null || elements.Count < 2)
                {
                    // 用户取消或选不够，MEPSelectionHelper 内部已经提示过了，直接退出
                    return null;
                }

                // 3. 提取选中的 Tag（兼容 Group 和 MRA）
                tags = new List<IndependentTag>();
                HashSet<ElementId> addedTagIds = new HashSet<ElementId>();
                Document doc = uidoc.Document;

                foreach (var elem in elements)
                {
                    if (elem is IndependentTag tag)
                    {
                        if (addedTagIds.Add(tag.Id)) tags.Add(tag);
                    }
                    else if (elem is Group group)
                    {
                        foreach (var memberId in group.GetMemberIds())
                        {
                            var member = doc.GetElement(memberId);
                            if (member is IndependentTag memberTag && addedTagIds.Add(memberTag.Id))
                                tags.Add(memberTag);
                        }
                    }
                    else if (elem is MultiReferenceAnnotation mra)
                    {
                        var mraTag = doc.GetElement(mra.TagId);
                        if (mraTag is IndependentTag innerTag && addedTagIds.Add(innerTag.Id))
                            tags.Add(innerTag);
                    }
                }
            }

            return tags.Count >= 2 ? tags : null;
        }

        public static double GetRowSpacing()
        {
            // 【方案A】与 MEPGroupTag 共用同一配置项 TagGroup.LineSpacing（单位：毫米）
            double spacingMM = MEPConfig.GetDouble("TagGroup.LineSpacing", 200.0);
            return UnitUtils.ConvertToInternalUnits(spacingMM, UnitTypeId.Millimeters);
        }

        public static void ExecuteMoves(Document doc, string transactionName,
            Dictionary<ElementId, XYZ> moveVectors)
        {
            if (moveVectors == null || moveVectors.Count == 0)
            {
                InfoCenterTip.Show(MEPConfig.GetString("TagAlign.Msg.Move.NoNeed",
                    "所有标记均已处于目标状态，无需移动。"));
                return;
            }

            using (Transaction trans = new Transaction(doc, transactionName))
            {
                trans.Start();
                try
                {
                    foreach (var kvp in moveVectors)
                        ElementTransformUtils.MoveElement(doc, kvp.Key, kvp.Value);
                    trans.Commit();
                    InfoCenterTip.Show("标记对齐完成");
                }
                catch (Exception ex)
                {
                    trans.RollBack();
                    MEPLogger.Instance.LogException(transactionName, ex);
                    
                    string title = MEPConfig.GetString("TagAlign.Msg.Move.Fail.Title", "执行失败");
                    string body  = MEPConfig.GetString("TagAlign.Msg.Move.Fail.Body",
                        "移动标记时发生错误，可能元素被锁定：");
                    // 将错误信息合并为单行显示（InfoCenter 仅支持单行）
                    InfoCenterTip.Show($"{title}: {body} ({ex.Message})");
                }
            }
        }

        private static bool IsSupportedView(View view)
        {
            switch (view.ViewType)
            {
                case ViewType.FloorPlan:
                case ViewType.CeilingPlan:
                case ViewType.EngineeringPlan:
                case ViewType.AreaPlan:
                case ViewType.Elevation:
                case ViewType.Section:
                case ViewType.Detail:
                    return true;
                default:
                    return false;
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Command : IExternalCommand
    {
        public Result Execute(ExternalCommandData cd, ref string message, ElementSet elements)
        {
            var uidoc = cd.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;

            // 1. 检查视图
            if (!CommandHelper.CheckView(activeView)) return Result.Cancelled;
            
            // 2. 获取 Tag（预选不足则启动空格键交互选择）
            var tags = CommandHelper.GetTags(uidoc, activeView);
            if (tags == null) return Result.Cancelled; // 取消或不足两个

            // 3. 计算并执行
            double rowSpacing = CommandHelper.GetRowSpacing();
            var moves = CalculateIndependentTag.CalculateStackedLeftMoves(activeView, tags, rowSpacing);

            CommandHelper.ExecuteMoves(doc,
                MEPConfig.GetString("TagAlign.TxName.StackedLeft", "左对齐并等距排序标记"),
                moves);
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CommandAlignLeftOnly : IExternalCommand
    {
        public Result Execute(ExternalCommandData cd, ref string message, ElementSet elements)
        {
            var uidoc = cd.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;

            if (!CommandHelper.CheckView(activeView)) return Result.Cancelled;
            var tags = CommandHelper.GetTags(uidoc, activeView);
            if (tags == null) return Result.Cancelled;

            var moves = CalculateIndependentTag.CalculateLeftAlignMoves(activeView, tags);
            CommandHelper.ExecuteMoves(doc,
                MEPConfig.GetString("TagAlign.TxName.LeftOnly", "左对齐标记"),
                moves);
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CommandStackOnly : IExternalCommand
    {
        public Result Execute(ExternalCommandData cd, ref string message, ElementSet elements)
        {
            var uidoc = cd.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;

            if (!CommandHelper.CheckView(activeView)) return Result.Cancelled;
            var tags = CommandHelper.GetTags(uidoc, activeView);
            if (tags == null) return Result.Cancelled;

            double rowSpacing = CommandHelper.GetRowSpacing();
            var moves = CalculateIndependentTag.CalculateStackMoves(activeView, tags, rowSpacing);
            CommandHelper.ExecuteMoves(doc,
                MEPConfig.GetString("TagAlign.TxName.StackOnly", "等距排序标记"),
                moves);
            return Result.Succeeded;
        }
    }
}