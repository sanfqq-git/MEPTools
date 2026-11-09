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
    /// 选择"连接着同系统的管"：在"连接组"基础上，只保留与起点同系统类型的管道，
    /// 并把路径上的配件一并选中。
    /// </summary>
    [Transaction(TransactionMode.Manual)] // 修复 Add-In Manager 报错
    public class SelectConnectedSameSystemCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc?.Document;
            if (doc == null) return Result.Cancelled;

            MEPLogger.Instance.StartSession("选择同系统连接组");
            var log = MEPLogger.Instance;

            try
            {
                var seeds = GetSeeds(uiDoc, doc,
                    "选择任意一根 MEP 管（将选中整组相连且同系统的管及配件）");
                if (seeds.Count == 0)
                {
                    message = "未选择任何起点管道";
                    InfoCenterTip.Show("未选择起点，已取消");
                    return Result.Cancelled;
                }

                var collected = new Dictionary<ElementId, Element>();
                foreach (var seed in seeds)
                {
                    Func<Element, bool> allow = BuildSameSystemFilter(seed);
                    // 调用同一个 CollectConnected
                    var group = SelectConnectedCommand.CollectConnected(doc, seed, allow);
                    foreach (var e in group)
                        if (!collected.ContainsKey(e.Id)) collected[e.Id] = e;
                }

                var ids = collected.Keys.ToList();
                uiDoc.Selection.SetElementIds(ids);

                InfoCenterTip.Show($"已选中同系统连接组：共 {ids.Count} 个元素（含管与配件）");
                log.AppendLine($"起点 {seeds.Count} 个，最终收集 {ids.Count} 个（同系统）");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                log.LogException("SelectConnectedSameSystemCommand", ex);
                InfoCenterTip.Show("执行异常：" + ex.Message);
                return Result.Failed;
            }
            finally
            {
                MEPLogger.Instance.EndSession("完成");
            }
        }

        // ---------------- 同系统过滤 ----------------

        private static Func<Element, bool> BuildSameSystemFilter(Element seed)
        {
            if (seed == null) return _ => false;

            if (seed is CableTray)
                return e => e is CableTray;

            if (seed is Pipe)
            {
                var sysId = GetSystemTypeId(seed);
                return e => e is Pipe && IsSameSystemId(sysId, GetSystemTypeId(e));
            }

            if (seed is Duct)
            {
                var sysId = GetSystemTypeId(seed);
                return e => e is Duct && IsSameSystemId(sysId, GetSystemTypeId(e));
            }

            return _ => false;
        }

        private static bool IsSameSystemId(ElementId a, ElementId b)
        {
            if (a == ElementId.InvalidElementId || b == ElementId.InvalidElementId) return true;
            return a == b;
        }

        internal static ElementId GetSystemTypeId(Element e)
        {
            try
            {
                if (e is Pipe p)
                {
                    var pp = p.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM);
                    return pp?.AsElementId() ?? ElementId.InvalidElementId;
                }
                if (e is Duct d)
                {
                    var pp = d.get_Parameter(BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM);
                    return pp?.AsElementId() ?? ElementId.InvalidElementId;
                }
                if (e is CableTray) return ElementId.InvalidElementId;
            }
            catch { }
            return ElementId.InvalidElementId;
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
                if (seeds.Count > 0) return seeds;
            }

            try
            {
                InfoCenterTip.Show(prompt + "（空格键确认）");
                using (var finisher = new SpaceKeyFinisher(WinForms.Keys.Space, "完成", "Finish"))
                {
                    finisher.Enable();
                    var refs = uiDoc.Selection.PickObjects(
                        ObjectType.Element, new SelectSameSystemFilter(), prompt);
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

    internal class SelectSameSystemFilter : ISelectionFilter
    {
        public bool AllowElement(Element e) { return e is MEPCurve; }
        public bool AllowReference(Reference r, XYZ position) { return false; }
    }
}