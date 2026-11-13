using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.Exceptions;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using MEPTools.Shared;

namespace MEPTools.MEPGroupSortAlign
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AnchorAlignCommand : IExternalCommand
    {
        private const double DefaultSpacingMM = 50.0;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            const string commandName = "MEP分组排序对齐-指定基准对齐";

            MEPLogger.Instance.StartSession(commandName);

            var filtered = MEPSelectionHelper.GetElements(
                uidoc: uiDoc,
                filter: new MepSortAlignEngine.MEPCurveSelectionFilter(),
                pickPrompt: "请选择至少两个管道/风管/线槽，按空格键完成，按ESC取消",
                infoTipNoPreselection: "请选择至少两个MEP元素，按空格键完成",
                minCount: 2,
                useSpaceKey: true);

            if (filtered.Count < 2)
                return Result.Cancelled;

            double spacingMM = MepSortAlignEngine.PromptForValue(
                $"{commandName} - 输入净间距",
                "请输入净间距（毫米）：按ESC取消视为默认50mm，按空格确认",
                DefaultSpacingMM);
            double spacingInternal = UnitUtils.ConvertToInternalUnits(spacingMM, UnitTypeId.Millimeters);

            // [NEW] 额外拾取锚点（必须落在已选集合中）
            var selectedIds = new HashSet<ElementId>(filtered.Select(e => e.Id));
            Reference anchorRef;
            try
            {
                anchorRef = uiDoc.Selection.PickObject(
                    ObjectType.Element,
                    new AnchorPickFilter(selectedIds),
                    "请点选一个已选中的元素作为对齐基准（该元素所在组保持不动），按ESC取消");
            }
            catch (OperationCanceledException)
            {
                MEPLogger.Instance.EndSession("Cancelled: anchor pick cancelled.");
                return Result.Cancelled;
            }

            return new MepSortAlignEngine().Run(
                uiDoc, filtered, spacingInternal,
                AnchorMode.UserPicked, anchorRef.ElementId, commandName);
        }

        // [NEW] 只允许拾取已选中的 MEPCurve
        private class AnchorPickFilter : ISelectionFilter
        {
            private readonly HashSet<ElementId> _allowed;
            public AnchorPickFilter(HashSet<ElementId> allowed) => _allowed = allowed;

            public bool AllowElement(Element elem) =>
                elem is MEPCurve && elem.Location is LocationCurve && _allowed.Contains(elem.Id);

            public bool AllowReference(Reference reference, XYZ position) => true;
        }
    }
}