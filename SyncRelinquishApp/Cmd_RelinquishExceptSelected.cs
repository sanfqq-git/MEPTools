using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.Shared;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MEPTools.SyncRelinquish
{
    [Transaction(TransactionMode.Manual)]
    public class Cmd_RelinquishExceptSelected : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc?.Document;

            if (doc == null || !doc.IsWorkshared || doc.IsDetached)
            {
                InfoCenterTip.Show("当前文档不是有效的工作共享模型。");
                return Result.Cancelled;
            }

            ICollection<ElementId> selectedIds = uidoc.Selection.GetElementIds();
            if (selectedIds == null || selectedIds.Count == 0)
            {
                InfoCenterTip.Show("请先在视图中选中需要【保留】权限的图元。");
                return Result.Cancelled;
            }

            try
            {
                // 1. 放弃当前用户的所有借用权限
                WorksharingUtils.RelinquishOwnership(doc, new RelinquishOptions(true), null);

                // 2. 立即重新获取选中元素的权限，实现“保留选中，放弃其余”的效果
                List<ElementId> validIds = new List<ElementId>();
                foreach (ElementId id in selectedIds)
                {
                    Element elem = doc.GetElement(id);
                    if (elem != null && elem.IsValidObject)
                    {
                        validIds.Add(id);
                    }
                }

                if (validIds.Count > 0)
                {
                    WorksharingUtils.CheckoutElements(doc, validIds);
                }

                InfoCenterTip.Show($"已保留选中 {validIds.Count} 个图元的权限，其余权限已全部放弃。");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("Cmd_RelinquishExceptSelected", ex);
                message = ex.Message;
                InfoCenterTip.Show("操作失败，请查看日志。");
                return Result.Failed;
            }
        }
    }
}