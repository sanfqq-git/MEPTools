using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.Shared;
using System;
using System.Collections.Generic;

namespace MEPTools.MEPChecker
{
    /// <summary>
    /// 外部命令入口：查询选中图元的协同信息（当前所有者 / 借用状态 / 最后修改者 / 创建者 / 借用请求者 / 与中心同步状态）。
    /// 流程：有预选直接用预选 → 无预选则提示并进入 Revit 交互拾取（空格结束）
    ///       → 逐个读取 WorksharingUtils 的协同信息 → 非模态窗口展示（双击行可定位图元）。
    /// 说明：所有信息均为 Revit 自身记录的工作共享数据，本命令只读，不修改任何图元。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class LastChangedByChecker : IExternalCommand
    {
        // ★ 静态字段持有窗口引用，防止被 GC 回收导致窗口突然消失
        private static LastChangedByCheckerForm _currentWindow;

        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIApplication uiApp = commandData == null ? null : commandData.Application;
            UIDocument uidoc = uiApp == null ? null : uiApp.ActiveUIDocument;

            if (uidoc == null || uidoc.Document == null)
            {
                InfoCenterTip.Show("未找到活动文档，请先打开模型。");
                return Result.Cancelled;
            }

            Document doc = uidoc.Document;

            if (doc.IsFamilyDocument)
            {
                InfoCenterTip.Show("当前为族文件，没有协同修改记录。");
                return Result.Cancelled;
            }

            // 工作共享信息只在“中心文件（工作共享）模型”里才有记录：
            // 非工作共享、或从中心分离出来的副本，Revit 都不会记录修改者。
            if (!doc.IsWorkshared || doc.IsDetached)
            {
                InfoCenterTip.Show("当前模型未启用工作共享（或为分离副本），Revit 没有记录“最后修改者”。");
                return Result.Cancelled;
            }

            MEPLogger.Instance.StartSession("LastChangedByChecker");

            try
            {
                // 1. 取目标图元：有预选用预选；无预选由共享类提示并进入交互拾取（空格结束）
                List<Element> targets = Preselected.GetSelectedElements(
                    uidoc,
                    null,                                                            // 不限类别，选什么查什么
                    "请选择要查询修改信息的图元，按空格键结束",
                    "请先选择要查询修改信息的图元；也可直接在视图中点选，按空格键结束");

                if (targets == null || targets.Count == 0)
                {
                    // 共享类内部已给出提示并 EndSession
                    return Result.Cancelled;
                }

                MEPLogger.Instance.AppendLine("目标图元：" + targets.Count + " 个");

                // 2. 逐个读取协同信息（单个图元失败不影响其他图元）
                List<LastChangedInfo> rows = new List<LastChangedInfo>();
                int failedCount = 0;

                foreach (Element elem in targets)
                {
                    if (elem == null || elem.Id == ElementId.InvalidElementId) continue;

                    try
                    {
                        LastChangedInfo row = BuildRow(doc, elem);
                        rows.Add(row);

                        // 常规记录：每个图元的关键协同信息都进日志，便于事后追溯
                        MEPLogger.Instance.AppendLine(string.Format(
                            "  [{0}] {1} | 工作集={2} | 所有者={3} | 最后修改者={4}",
                            row.ElementIdValue, row.CategoryName, row.WorksetName,
                            row.Owner, row.LastChangedBy));
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        MEPLogger.Instance.LogException("BuildRow " + elem.Id.IntegerValue, ex);
                    }
                }

                if (rows.Count == 0)
                {
                    InfoCenterTip.Show("没有可读取的协同信息，请查看日志。");
                    MEPLogger.Instance.EndSession("Failed: no valid rows.");
                    return Result.Cancelled;
                }

                // 3. 关闭上一次遗留的窗口，避免重复点击时堆叠
                if (_currentWindow != null && !_currentWindow.IsDisposed)
                {
                    _currentWindow.Close();
                    _currentWindow.Dispose();
                    _currentWindow = null;
                }

                var window = new LastChangedByCheckerForm(uidoc, rows);
                window.FormClosed += (s, e) =>
                {
                    if (ReferenceEquals(_currentWindow, window))
                        _currentWindow = null;
                };
                _currentWindow = window;

                // 4. 非模态显示，并把 Revit 主窗口设为 owner
                IntPtr revitHandle = uiApp.MainWindowHandle;
                if (revitHandle != IntPtr.Zero)
                {
                    window.Show(new RevitWindowWrapper(revitHandle));
                }
                else
                {
                    window.Show();
                }

                InfoCenterTip.Show(string.Format("已查询 {0} 个图元的修改信息", rows.Count));
                MEPLogger.Instance.EndSession(string.Format(
                    "OK: {0} rows, {1} failed.", rows.Count, failedCount));

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("LastChangedByChecker.Execute", ex);
                message = ex.Message;
                InfoCenterTip.Show("查询失败，请查看日志。");
                return Result.Failed;
            }
        }

        /// <summary>
        /// 读取单个图元的协同信息。所有字段都取“界面显示值”，空值统一显示为 “—”。
        /// </summary>
        private static LastChangedInfo BuildRow(Document doc, Element elem)
        {
            var row = new LastChangedInfo
            {
                ElementId = elem.Id,
                ElementIdValue = elem.Id.IntegerValue,
                CategoryName = elem.Category != null ? SafeText(elem.Category.Name) : "—",
                ElementName = GetDisplayName(elem),
                WorksetName = GetWorksetName(doc, elem)
            };

            // Revit 右上角“图元信息”气泡里的数据来源就是它：
            // Owner = 当前拥有（借用）该图元的用户，LastChangedBy = 最后修改者，Creator = 创建者
            using (WorksharingTooltipInfo tip =
                WorksharingUtils.GetWorksharingTooltipInfo(doc, elem.Id))
            {
                row.Owner = SafeText(tip.Owner);
                row.LastChangedBy = SafeText(tip.LastChangedBy);
                row.Creator = SafeText(tip.Creator);
                row.Requesters = JoinRequesters(tip);
            }

            row.Ownership = DescribeCheckoutStatus(doc, elem.Id);
            row.CentralStatus = DescribeModelUpdatesStatus(doc, elem.Id);
            return row;
        }

        /// <summary>族实例显示“族 : 类型”，其他图元直接显示名称</summary>
        private static string GetDisplayName(Element elem)
        {
            if (elem == null) return "—";

            FamilyInstance fi = elem as FamilyInstance;
            if (fi != null)
            {
                string familyName = (fi.Symbol != null && fi.Symbol.Family != null)
                    ? fi.Symbol.Family.Name
                    : null;
                string typeName = fi.Name;

                if (string.IsNullOrWhiteSpace(familyName))
                    return SafeText(typeName);

                return familyName + " : " + typeName;
            }

            return SafeText(elem.Name);
        }

        private static string GetWorksetName(Document doc, Element elem)
        {
            try
            {
                WorksetId wsId = elem.WorksetId;
                if (wsId == null || wsId == WorksetId.InvalidWorksetId) return "—";

                Workset ws = doc.GetWorksetTable().GetWorkset(wsId);
                return ws == null ? "—" : SafeText(ws.Name);
            }
            catch
            {
                return "—";
            }
        }

        private static string DescribeCheckoutStatus(Document doc, ElementId id)
        {
            try
            {
                switch (WorksharingUtils.GetCheckoutStatus(doc, id))
                {
                    case CheckoutStatus.OwnedByCurrentUser: return "当前用户已借用";
                    case CheckoutStatus.OwnedByOtherUser: return "他人已借用";
                    case CheckoutStatus.NotOwned: return "未被借用";
                    default: return "—";
                }
            }
            catch
            {
                return "—";
            }
        }

        private static string DescribeModelUpdatesStatus(Document doc, ElementId id)
        {
            try
            {
                switch (WorksharingUtils.GetModelUpdatesStatus(doc, id))
                {
                    case ModelUpdatesStatus.CurrentWithCentral: return "与中心一致";
                    case ModelUpdatesStatus.NotYetInCentral: return "尚未同步到中心";
                    case ModelUpdatesStatus.UpdatedInCentral: return "中心已更新";
                    case ModelUpdatesStatus.DeletedInCentral: return "中心已删除";
                    default: return "—";
                }
            }
            catch
            {
                return "—";
            }
        }

        private static string JoinRequesters(WorksharingTooltipInfo tip)
        {
            if (tip == null) return "—";

            IList<string> requesters = tip.GetRequesters();
            if (requesters == null || requesters.Count == 0) return "—";

            return SafeText(string.Join("、", requesters));
        }

        private static string SafeText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();
        }
    }

    /// <summary>
    /// 一行查询结果（只用于界面展示，不参与任何模型修改）。
    /// 属性名与表格列的 DataPropertyName 一一对应。
    /// </summary>
    public class LastChangedInfo
    {
        /// <summary>真实图元 ID（表格不展示，用于双击定位）</summary>
        public ElementId ElementId { get; set; }

        public int ElementIdValue { get; set; }
        public string CategoryName { get; set; }
        public string ElementName { get; set; }
        public string WorksetName { get; set; }
        /// <summary>当前拥有（借用）该图元的用户</summary>
        public string Owner { get; set; }
        /// <summary>借用状态（当前用户 / 他人 / 未被借用）</summary>
        public string Ownership { get; set; }
        /// <summary>Revit 记录的最后修改者</summary>
        public string LastChangedBy { get; set; }
        public string Creator { get; set; }
        /// <summary>申请借用该图元的用户</summary>
        public string Requesters { get; set; }
        /// <summary>与中心模型的同步状态</summary>
        public string CentralStatus { get; set; }
    }
}
