using System.Collections.Generic;
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

            // 1. 仅允许立面视图
            if (activeView == null || activeView.ViewType != ViewType.Elevation)
            {
                TaskDialog.Show("提示", "请在立面视图（Elevation）中运行此命令。");
                return Result.Cancelled;
            }

            // 2. 从预选集中取得当前视图的 IndependentTag
            List<IndependentTag> tags = Preselected.GetSelectedIndependentTags(uidoc, activeView);
            if (tags.Count < 2)
            {
                TaskDialog.Show("提示", "请先选中至少两个标记（Tag），再运行命令。");
                return Result.Cancelled;
            }

            // 3. 行距（Revit 内部单位：英尺）。0.02 ft ≈ 6mm
            //    数值越小堆叠越紧。可根据字号自行调整。
            const double rowSpacing = 0.50;

            // 4. 计算"左对齐 + 按管序从上往下堆叠"的移动向量
            Dictionary<ElementId, XYZ> moveVectors =
                CalculateIndependentTag.CalculateStackedLeftMoves(
                    doc, activeView, tags, rowSpacing);

            if (moveVectors == null || moveVectors.Count == 0)
            {
                TaskDialog.Show("提示", "所有标记均已对齐，或无法计算边界框。");
                return Result.Cancelled;
            }

            // 5. 事务中执行移动
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
    }
}