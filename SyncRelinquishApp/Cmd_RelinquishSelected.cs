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
    public class Cmd_RelinquishSelected : IExternalCommand
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
                InfoCenterTip.Show("请先在视图中选中需要【放弃】权限的图元。");
                return Result.Cancelled;
            }

            try
            {
                // 1. 获取当前文档中所有被当前用户借用的图元
                ICollection<ElementId> borrowedIds = GetBorrowedElements(doc);
                if (borrowedIds.Count == 0)
                {
                    InfoCenterTip.Show("当前文档没有已借用的图元，无需操作。");
                    return Result.Succeeded;
                }

                // 2. 从借用列表中筛选出被选中的图元
                List<ElementId> toRelinquish = borrowedIds.Intersect(selectedIds).ToList();
                if (toRelinquish.Count == 0)
                {
                    InfoCenterTip.Show("选中的图元均未被当前用户借用，无需放弃。");
                    return Result.Succeeded;
                }

                // 3. 核心逻辑：先放弃当前用户的所有借用权限
                WorksharingUtils.RelinquishOwnership(doc, new RelinquishOptions(true), null);

                // 4. 立即重新获取所有未被选中元素的权限，实现“仅放弃选中”的效果
                List<ElementId> toRecheckout = borrowedIds.Where(id => !selectedIds.Contains(id)).ToList();
                
                if (toRecheckout.Count > 0)
                {
                    WorksharingUtils.CheckoutElements(doc, toRecheckout);
                }

                InfoCenterTip.Show($"已放弃 {toRelinquish.Count} 个选中图元的权限，并重新获取了其余 {toRecheckout.Count} 个图元的权限。");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("Cmd_RelinquishSelected", ex);
                message = ex.Message;
                InfoCenterTip.Show("操作失败，请查看日志。");
                return Result.Failed;
            }
        }

        /// <summary>
        /// 遍历文档图元，利用 GetCheckoutStatus 找出所有被当前用户借用的元素
        /// </summary>
        private ICollection<ElementId> GetBorrowedElements(Document doc)
        {
            var borrowed = new List<ElementId>();
            var collector = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WhereElementIsViewIndependent();

            foreach (Element elem in collector)
            {
                if (elem == null || !elem.IsValidObject) continue;
                
                // 查询借用状态，判断是否归当前用户所有
                var status = WorksharingUtils.GetCheckoutStatus(doc, elem.Id);
                if (status == CheckoutStatus.OwnedByCurrentUser)
                {
                    borrowed.Add(elem.Id);
                }
            }
            return borrowed;
        }
    }
}