using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using MEPTools.Shared;

using Connector = Autodesk.Revit.DB.Connector;
using WinForms = System.Windows.Forms;

namespace MEPTools.MEPConnector.Selection
{
    /// <summary>
    /// 选择"连接着的管"：从起点 MEP 管出发，沿 Connector 拓扑遍历整组相连的管，
    /// 并将穿透的弯头 / 三通 / 阀门等 FamilyInstance 配件一并选中。
    /// </summary>
    [Transaction(TransactionMode.Manual)] // 修复 Add-In Manager 报错
    public class SelectConnectedCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc?.Document;
            if (doc == null) return Result.Cancelled;

            MEPLogger.Instance.StartSession("选择连接组");
            var log = MEPLogger.Instance;

            try
            {
                var seeds = GetSeeds(uiDoc, doc, "选择任意一根 MEP 管（将选中整组连接的管及配件）");
                if (seeds.Count == 0)
                {
                    message = "未选择任何起点管道";
                    InfoCenterTip.Show("未选择起点，已取消");
                    return Result.Cancelled;
                }

                var collected = new Dictionary<ElementId, Element>();
                foreach (var seed in seeds)
                {
                    var group = CollectConnected(doc, seed, null);
                    foreach (var e in group)
                        if (!collected.ContainsKey(e.Id)) collected[e.Id] = e;
                }

                var ids = collected.Keys.ToList();
                uiDoc.Selection.SetElementIds(ids);

                InfoCenterTip.Show($"已选中连接组：共 {ids.Count} 个元素（含管与配件）");
                log.AppendLine($"起点 {seeds.Count} 个，最终收集 {ids.Count} 个");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                log.LogException("SelectConnectedCommand", ex);
                InfoCenterTip.Show("执行异常：" + ex.Message);
                return Result.Failed;
            }
            finally
            {
                MEPLogger.Instance.EndSession("完成");
            }
        }

        // ---------------- 核心 BFS ----------------

        /// <summary>
        /// 从 seed 出发沿 Connector 拓扑收集所有相连的 MEP 管及配件。
        /// </summary>
        internal static List<Element> CollectConnected(Document doc, Element seed, Func<Element, bool> allow)
        {
            var result = new List<Element>();
            if (seed == null) return result;

            var visited = new HashSet<ElementId>();
            var queue = new Queue<Element>();

            // 处理起点
            if (seed is MEPCurve)
            {
                if (allow == null || allow(seed))
                {
                    result.Add(seed);
                    visited.Add(seed.Id);
                    queue.Enqueue(seed);
                }
            }
            else if (seed is FamilyInstance fi && fi.MEPModel != null)
            {
                result.Add(seed);
                visited.Add(seed.Id);
                queue.Enqueue(seed);
            }

            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                var cm = GetConnectorManager(cur);
                if (cm == null) continue;

                foreach (Connector c in cm.Connectors)
                {
                    if (!c.IsConnected) continue;

                    foreach (Connector r in c.AllRefs)
                    {
                        var neighbor = r.Owner;
                        if (neighbor == null) continue;
                        if (neighbor.Id == cur.Id) continue;
                        if (visited.Contains(neighbor.Id)) continue;

                        if (neighbor is MEPCurve)
                        {
                            if (allow != null && !allow(neighbor)) continue;
                            visited.Add(neighbor.Id);
                            result.Add(neighbor); // 收集管
                            queue.Enqueue(neighbor);
                        }
                        else if (neighbor is FamilyInstance neighborFi && neighborFi.MEPModel != null)
                        {
                            // 收集配件（弯头、三通、阀门等），并继续穿透
                            visited.Add(neighbor.Id);
                            result.Add(neighbor); // 【新增】把配件也加入结果集
                            queue.Enqueue(neighbor);
                        }
                    }
                }
            }
            return result;
        }

        internal static ConnectorManager GetConnectorManager(Element e)
        {
            if (e is MEPCurve mep) return mep.ConnectorManager;
            if (e is FamilyInstance fi && fi.MEPModel != null) return fi.MEPModel.ConnectorManager;
            return null;
        }

        // ---------------- 起点获取 ----------------

        private static List<Element> GetSeeds(UIDocument uiDoc, Document doc, string prompt)
        {
            var seeds = new List<Element>();

            var selIds = uiDoc.Selection.GetElementIds();
            if (selIds != null && selIds.Count > 0)
            {
                foreach (var id in selIds)
                {
                    var e = doc.GetElement(id);
                    if (e is MEPCurve) seeds.Add(e);
                }
                if (seeds.Count > 0)
                {
                    MEPLogger.Instance.AppendLine($"使用预选起点 {seeds.Count} 个");
                    return seeds;
                }
            }

            try
            {
                InfoCenterTip.Show(prompt + "（空格键确认）");
                using (var finisher = new SpaceKeyFinisher(WinForms.Keys.Space, "完成", "Finish"))
                {
                    finisher.Enable();
                    var refs = uiDoc.Selection.PickObjects(
                        ObjectType.Element, new SelectConnectedFilter(), prompt);
                    if (refs != null)
                    {
                        foreach (var r in refs)
                        {
                            var el = doc.GetElement(r.ElementId);
                            if (el is MEPCurve) seeds.Add(el);
                        }
                    }
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { }
            catch (Exception ex) { MEPLogger.Instance.LogException("GetSeeds", ex); }

            return seeds;
        }
    }

    internal class SelectConnectedFilter : ISelectionFilter
    {
        public bool AllowElement(Element e) { return e is MEPCurve; }
        public bool AllowReference(Reference r, XYZ position) { return false; }
    }
}