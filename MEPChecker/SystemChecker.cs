using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using MEPTools.Shared;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MEPTools.MEPChecker
{
    /// <summary>
    /// 外部命令入口：检测用户选中的 Duct/CableTray 是否与其他系统的管错误连接。
    /// 结果以非模态窗口展示，用户可继续操作 Revit，直到手动关闭窗口。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class Command : IExternalCommand
    {
        // ★ 静态字段持有窗口引用，防止被 GC 回收导致窗口突然消失
        private static SystemCheckerForm _currentWindow;

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
            MEPLogger.Instance.StartSession("SystemChecker");

            try
            {
                // 1. 取目标：有预选用预选；无预选提示后进入点选（SpaceKeyFinisher 空格结束）
                //    只允许 Duct / CableTray，混选的其他图元由共享类自动忽略并提示数量
                List<Element> targets = Preselected.GetSelectedElements(
                    uidoc,
                    new DuctOrCableTrayFilter(),
                    "请选择要检测的 Duct 或 CableTray，按空格键结束",
                    "请先在视图中选中要检测的 Duct 或 CableTray；也可直接点选，按空格键结束");

                if (targets == null || targets.Count == 0)
                {
                    // 共享类内部已给出气泡提示并 EndSession
                    return Result.Cancelled;
                }

                MEPLogger.Instance.AppendLine("文档：" + doc.Title);
                MEPLogger.Instance.AppendLine("目标图元：" + targets.Count + " 个");
                foreach (Element target in targets)
                {
                    MEPLogger.Instance.AppendLine("  [" + target.Id.IntegerValue + "] " + target.Name);
                }

                // 2. 检测逻辑完全不变
                var checker = new MismatchConnectionChecker();
                List<MismatchResult> results = checker.FindMismatchedConnections(targets);

                if (results.Count == 0)
                {
                    InfoCenterTip.Show(string.Format(
                        "已检查 {0} 个图元，未发现系统不匹配的连接。", targets.Count));
                    MEPLogger.Instance.EndSession(string.Format(
                        "OK: {0} targets, no mismatch.", targets.Count));
                    return Result.Succeeded;
                }

                // ★ 关闭上一次遗留的窗口，避免重复点击时堆叠
                if (_currentWindow != null && !_currentWindow.IsDisposed)
                {
                    _currentWindow.Close();
                    _currentWindow.Dispose();
                    _currentWindow = null;
                }

                // ★ 创建新窗口
                var window = new SystemCheckerForm(uidoc, results);
                window.FormClosed += (s, e) =>
                {
                    // 窗口关闭后清空静态引用
                    if (ReferenceEquals(_currentWindow, window))
                        _currentWindow = null;
                };
                _currentWindow = window;

                // ★ 关键：非模态显示（Show 而不是 ShowDialog）
                //   并把 Revit 主窗口设为 owner，让表格浮动在 Revit 之上
                IntPtr revitHandle = uiApp.MainWindowHandle;
                if (revitHandle != IntPtr.Zero)
                {
                    window.Show(new RevitWindowWrapper(revitHandle));
                }
                else
                {
                    window.Show();
                }

                // 重要结果用气泡提示（原来这里是任务对话框）
                InfoCenterTip.Show(string.Format(
                    "发现 {0} 处系统不匹配，请查看窗口。", results.Count));
                MEPLogger.Instance.EndSession(string.Format(
                    "OK: {0} targets, {1} mismatches.", targets.Count, results.Count));

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("SystemChecker.Execute", ex);
                message = ex.Message;
                InfoCenterTip.Show("检测失败，请查看日志。");
                return Result.Failed;
            }
        }
    }

    /// <summary>
    /// 选择过滤器：只允许选择 Duct / CableTray（预选过滤 + 点选过滤共用）。
    /// </summary>
    internal class DuctOrCableTrayFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            return elem is Duct || elem is CableTray;
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            // 只允许选择整个图元，不允许选面 / 边
            return false;
        }
    }

    /// <summary>
    /// 把 Revit 主窗口的句柄包装成 WinForms 的 IWin32Window，
    /// 这样 window.Show(owner) 就能把 Revit 主窗口设为 owner。
    /// </summary>
    internal class RevitWindowWrapper : System.Windows.Forms.IWin32Window
    {
        private readonly IntPtr _handle;
        public RevitWindowWrapper(IntPtr handle) { _handle = handle; }
        public IntPtr Handle { get { return _handle; } }
    }

    /// <summary>
    /// 表示一处系统不匹配的连接。
    /// </summary>
    public class MismatchResult
    {
        public ElementId ElementId1 { get; set; }
        public ElementId ElementId2 { get; set; }
        public string ElementName1 { get; set; }
        public string ElementName2 { get; set; }
        public string System1 { get; set; }
        public string System2 { get; set; }

        public int ElementId1Value
        {
            get { return ElementId1 != null ? ElementId1.IntegerValue : 0; }
        }

        public int ElementId2Value
        {
            get { return ElementId2 != null ? ElementId2.IntegerValue : 0; }
        }
    }

    /// <summary>
    /// 核心检测逻辑：
    /// 从选中的 Duct/CableTray 出发，穿透一层或多层 Fitting，
    /// 找到与它相连的其他同类图元，比较系统是否一致。
    /// </summary>
    public class MismatchConnectionChecker
    {
        // 穿透 Fitting 的最大深度，避免复杂拓扑导致无限扩展
        private const int MaxTraverseDepth = 3;

        public List<MismatchResult> FindMismatchedConnections(List<Element> targets)
        {
            var results = new List<MismatchResult>();
            var visitedPairs = new HashSet<string>();

            foreach (Element element in targets)
            {
                string sysSelf = GetSystemIdentifier(element);
                if (sysSelf == null) continue;

                var neighbors = FindConnectedSameKindElements(element);

                foreach (Element other in neighbors)
                {
                    string sysOther = GetSystemIdentifier(other);
                    if (sysOther == null) continue;
                    if (sysSelf == sysOther) continue;

                    int id1 = element.Id.IntegerValue;
                    int id2 = other.Id.IntegerValue;
                    string pairKey = id1 < id2
                        ? id1.ToString() + "_" + id2.ToString()
                        : id2.ToString() + "_" + id1.ToString();

                    if (visitedPairs.Contains(pairKey)) continue;
                    visitedPairs.Add(pairKey);

                    results.Add(new MismatchResult
                    {
                        ElementId1 = element.Id,
                        ElementId2 = other.Id,
                        ElementName1 = element.Name,
                        ElementName2 = other.Name,
                        System1 = sysSelf,
                        System2 = sysOther
                    });
                }
            }

            return results;
        }

        private List<Element> FindConnectedSameKindElements(Element origin)
        {
            var result = new List<Element>();
            var visited = new HashSet<int>();
            visited.Add(origin.Id.IntegerValue);

            ConnectorManager cm = GetConnectorManager(origin);
            if (cm == null) return result;

            foreach (Connector connector in cm.Connectors)
            {
                if (!connector.IsConnected) continue;
                TraverseFromConnector(connector, origin, result, visited, 0);
            }

            return result;
        }

        private void TraverseFromConnector(
            Connector connector,
            Element origin,
            List<Element> result,
            HashSet<int> visited,
            int depth)
        {
            if (depth > MaxTraverseDepth) return;

            foreach (Connector refConnector in connector.AllRefs)
            {
                Element owner = refConnector.Owner;
                if (owner.Id == origin.Id) continue;
                if (visited.Contains(owner.Id.IntegerValue)) continue;

                if (IsSameKind(origin, owner))
                {
                    visited.Add(owner.Id.IntegerValue);
                    result.Add(owner);
                    continue;
                }

                if (IsFitting(owner))
                {
                    visited.Add(owner.Id.IntegerValue);
                    ConnectorManager fittingCm = GetConnectorManager(owner);
                    if (fittingCm == null) continue;

                    int skipConnectorId = refConnector.Id;
                    foreach (Connector c in fittingCm.Connectors)
                    {
                        if (c.Id == skipConnectorId) continue;
                        if (!c.IsConnected) continue;
                        TraverseFromConnector(c, origin, result, visited, depth + 1);
                    }
                }
            }
        }

        private bool IsSameKind(Element a, Element b)
        {
            if (a is Duct && b is Duct) return true;
            if (a is CableTray && b is CableTray) return true;
            return false;
        }

        private bool IsFitting(Element element)
        {
            if (element.Category == null) return false;
            int catId = element.Category.Id.IntegerValue;
            return catId == (int)BuiltInCategory.OST_DuctFitting
                || catId == (int)BuiltInCategory.OST_CableTrayFitting;
        }

        private ConnectorManager GetConnectorManager(Element element)
        {
            if (element is Duct duct) return duct.ConnectorManager;
            if (element is CableTray tray) return tray.ConnectorManager;

            FamilyInstance fi = element as FamilyInstance;
            if (fi != null && fi.MEPModel != null)
                return fi.MEPModel.ConnectorManager;

            return null;
        }

        private string GetSystemIdentifier(Element element)
        {
            if (element is Duct)
            {
                string val = GetParameterValue(element, BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM);
                if (val != null) return val;

                val = GetParameterValue(element, BuiltInParameter.RBS_SYSTEM_NAME_PARAM);
                if (val != null) return val;

                return element.Name;
            }

            if (element is CableTray)
            {
                string val = GetParameterValue(element, BuiltInParameter.RBS_CTC_SERVICE_TYPE);
                if (val != null) return val;

                val = GetParameterValue(element, BuiltInParameter.RBS_CABLETRAYCONDUIT_SYSTEM_TYPE);
                if (val != null) return val;

                return element.Name;
            }

            return null;
        }

        private string GetParameterValue(Element element, BuiltInParameter bip)
        {
            Parameter p = element.get_Parameter(bip);
            if (p == null || !p.HasValue) return null;

            if (p.StorageType == StorageType.String)
            {
                string val = p.AsString();
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }
            else if (p.StorageType == StorageType.ElementId)
            {
                ElementId id = p.AsElementId();
                if (id != null && id != ElementId.InvalidElementId)
                {
                    Element e = element.Document.GetElement(id);
                    if (e != null) return e.Name;
                }
            }
            return null;
        }
    }
}