using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using MEPTools.Shared;

namespace MEPTools.RemakeInPlaceCommands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Command : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            MEPLogger.Instance.StartSession("原地重制");

            try
            {
                // ---------- 1. 预选检测 ----------
                ICollection<ElementId> preselectedIds = uidoc.Selection.GetElementIds();
                if (preselectedIds != null && preselectedIds.Count > 0)
                {
                    InfoCenterTip.Show($"正在原地重制 {preselectedIds.Count} 个预选元素…");
                    return RemakeFromHost(doc, preselectedIds.ToList());
                }

                // ---------- 2. 无预选 → 链接文件交互选择 ----------
                InfoCenterTip.Show("未检测到预选元素，请在视图中选择 LINK 文件中的元素，按空格键确认");

                List<Reference> linkRefs;
                try
                {
                    // 使用 Shared 中的 SpaceKeyFinisher 监听空格键
                    using (var finisher = new SpaceKeyFinisher())
                    {
                        finisher.Enable();
                        linkRefs = uidoc.Selection
                            .PickObjects(ObjectType.LinkedElement, "选择 LINK 文件中的元素，按空格完成")
                            .ToList();
                    }
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    InfoCenterTip.Show("操作已取消");
                    MEPLogger.Instance.EndSession("Cancelled by user.");
                    return Result.Cancelled;
                }

                if (linkRefs == null || linkRefs.Count == 0)
                {
                    InfoCenterTip.Show("未选择任何 LINK 元素");
                    MEPLogger.Instance.EndSession("No linked elements selected.");
                    return Result.Cancelled;
                }

                return RemakeFromLink(doc, linkRefs);
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("RemakeInPlaceCommand", ex);
                InfoCenterTip.Show("原地重制失败，请查看日志");
                MEPLogger.Instance.EndSession("Failed: " + ex.Message);
                return Result.Failed;
            }
        }

        // ============================================================
        //  分支 A：预选元素原地重制（同一文档内复制 → 删除原元素）
        // ============================================================
        private Result RemakeFromHost(Document doc, List<ElementId> originalIds)
        {
            // 过滤掉不能删除/复制的主体元素，避免 Transaction 中途失败
            var validIds = originalIds
                .Where(id => doc.GetElement(id) != null)
                .ToList();

            if (validIds.Count == 0)
            {
                InfoCenterTip.Show("预选元素无效");
                MEPLogger.Instance.EndSession("No valid elements.");
                return Result.Cancelled;
            }

            // 协同环境：只 checkout 将被删除的原元素，不批量 checkout 无关元素
            if (doc.IsWorkshared)
            {
                WorksharingUtils.CheckoutElements(doc, validIds);
            }

            using (Transaction tx = new Transaction(doc, "原地重制（Host）"))
            {
                tx.Start();

                // CopyElements 在同一文档内复制，Transform 传 null 即保持原位
                ICollection<ElementId> copiedIds = ElementTransformUtils.CopyElements(
                    doc,
                    validIds,
                    doc,
                    null,
                    new CopyPasteOptions());

                // 删除原元素
                doc.Delete(validIds);

                tx.Commit();

                MEPLogger.Instance.AppendLine($"Host 重制完成：原 {validIds.Count} 个，新 {copiedIds.Count} 个。");
                InfoCenterTip.Show($"已完成 {copiedIds.Count} 个元素的原地重制");
                MEPLogger.Instance.EndSession($"Success. {copiedIds.Count} elements remade in place.");
            }

            return Result.Succeeded;
        }

        // ============================================================
        //  分支 B：从 LINK 文件复制到当前文档
        // ============================================================
        private Result RemakeFromLink(Document hostDoc, List<Reference> linkRefs)
        {
            // 按 LinkInstance 分组，因为不同链接实例可能有不同的 Transform
            var groups = linkRefs
                .GroupBy(r => r.ElementId) // Reference.ElementId 是链接实例（RevitLinkInstance）的 Id
                .ToList();

            int totalCopied = 0;

            using (Transaction tx = new Transaction(hostDoc, "原地重制（Link）"))
            {
                tx.Start();

                foreach (var group in groups)
                {
                    var linkInstance = hostDoc.GetElement(group.Key) as RevitLinkInstance;
                    if (linkInstance == null) continue;

                    Document linkDoc = linkInstance.GetLinkDocument();
                    if (linkDoc == null)
                    {
                        MEPLogger.Instance.AppendLine($"链接文档未加载，跳过 LinkInstance {group.Key}");
                        continue;
                    }

                    // 该链接实例的总变换：把链接文档坐标 → 宿主文档坐标
                    Transform linkTransform = linkInstance.GetTotalTransform();

                    // 取该组下所有链接元素在链接文档中的 ElementId
                    var linkedElementIds = group
                        .Select(r => r.LinkedElementId)
                        .Where(id => linkDoc.GetElement(id) != null)
                        .ToList();

                    if (linkedElementIds.Count == 0) continue;

                    // 跨文档 CopyElements：sourceDocument = linkDoc，destinationDocument = hostDoc
                    ICollection<ElementId> newIds = ElementTransformUtils.CopyElements(
                        linkDoc,
                        linkedElementIds,
                        hostDoc,
                        linkTransform,
                        new CopyPasteOptions());

                    totalCopied += newIds.Count;
                    MEPLogger.Instance.AppendLine(
                        $"LinkInstance {group.Key}：复制 {newIds.Count} 个元素，Transform 已应用。");
                }

                tx.Commit();
            }

            InfoCenterTip.Show($"已从 LINK 文件复制 {totalCopied} 个元素");
            MEPLogger.Instance.EndSession($"Success. {totalCopied} elements copied from linked file.");
            return Result.Succeeded;
        }
    }
}