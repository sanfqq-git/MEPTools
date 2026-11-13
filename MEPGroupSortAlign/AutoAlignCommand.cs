using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.Shared;

namespace MEPTools.MEPGroupSortAlign
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AutoAlignCommand : IExternalCommand
    {
        private const double DefaultSpacingMM = 50.0;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            const string commandName = "MEP分组排序对齐-自动对齐";

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

            return new MepSortAlignEngine().Run(
                uiDoc, filtered, spacingInternal,
                AnchorMode.AutoFirstExtreme, null, commandName);
        }
    }
}