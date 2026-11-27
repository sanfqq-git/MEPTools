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

namespace MEPTools.MEPConnector.Probe
{
    /// <summary>
    /// 只读诊断命令：导出选中 MEP 图元的连接器清单（位置/截面/是否连通/对端归属）
    /// 以及交点周边 2m 内的管件，写入日志，供对比原生接头与插件产物的连接拓扑。
    /// 本命令不创建、不修改、不删除任何图元。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ConnectorProbeCommand : IExternalCommand
    {
        private const double ProbeRadiusMm = 2000.0;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData?.Application?.ActiveUIDocument;
            Document doc = uiDoc?.Document;
            if (doc == null)
            {
                InfoCenterTip.Show("未找到活动文档");
                return Result.Cancelled;
            }

            MEPLogger.Instance.StartSession("连接拓扑诊断");
            var log = MEPLogger.Instance;

            try
            {
                var targets = Preselected.GetSelectedElements(
                    uiDoc,
                    new ProbeFilter(),
                    "请选择要诊断的 MEP 管 / 管件（可多选），按空格键结束",
                    "请先选中要诊断的管或管件");

                if (targets.Count == 0)
                {
                    log.EndSession("未选择图元");
                    InfoCenterTip.Show("未选择图元，已取消");
                    return Result.Cancelled;
                }

                log.AppendLine($"诊断图元：{targets.Count} 个");

                foreach (var elem in targets)
                {
                    if (elem == null) continue;
                    DumpElement(doc, log, elem);
                }

                InfoCenterTip.Show($"已导出 {targets.Count} 个图元的连接器信息到日志");
                log.EndSession("完成");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                log.LogException("ConnectorProbeCommand", ex);
                log.EndSession("异常：" + ex.Message);
                InfoCenterTip.Show("诊断异常：" + ex.Message);
                return Result.Failed;
            }
        }

        // ------------------------------------------------------------------

        private static void DumpElement(Document doc, MEPLogger log, Element elem)
        {
            log.AppendLine();
            log.AppendLine("----------------------------------------------------------------");
            string cat = elem.Category?.Name ?? "无类别";
            string fam = GetFamilyName(elem);
            string sym = GetSymbolName(elem);
            log.AppendLine($"[{elem.Id.IntegerValue}] {cat} | 族={fam} | 类型={sym}");

            DumpLocation(doc, log, elem);
            DumpSizeParams(log, elem);

            var cm = GetConnectorManager(elem);
            if (cm == null)
            {
                log.AppendLine("  连接器：无 ConnectorManager");
            }
            else
            {
                var conns = cm.Connectors.Cast<Connector>().ToList();
                log.AppendLine($"  连接器数量：{conns.Count}");
                int idx = 0;
                foreach (var c in conns)
                {
                    idx++;
                    DumpConnector(log, elem, c, idx);
                }
            }

            DumpNearbyFittings(doc, log, elem);
        }

        private static void DumpLocation(Document doc, MEPLogger log, Element elem)
        {
            try
            {
                if (elem.Location is LocationCurve lc && lc.Curve is Line line)
                {
                    var p0 = line.GetEndPoint(0);
                    var p1 = line.GetEndPoint(1);
                    log.AppendLine($"  定位线：({p0.X:F3},{p0.Y:F3},{p0.Z * 304.8:F1}mm) → " +
                                   $"({p1.X:F3},{p1.Y:F3},{p1.Z * 304.8:F1}mm)");
                }
                else if (elem.Location is LocationPoint lp)
                {
                    var p = lp.Point;
                    log.AppendLine($"  定位点：({p.X:F3},{p.Y:F3},{p.Z * 304.8:F1}mm)");
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("  定位读取失败：" + ex.Message);
            }
        }

        private static void DumpSizeParams(MEPLogger log, Element elem)
        {
            string w = ReadParamMm(elem, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM)
                       ?? ReadParamMm(elem, BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
            string h = ReadParamMm(elem, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM)
                       ?? ReadParamMm(elem, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
            string d = ReadParamMm(elem, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)
                       ?? ReadParamMm(elem, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);

            if (w != null || h != null)
                log.AppendLine($"  截面：{w ?? "?"}×{h ?? "?"} mm");
            else if (d != null)
                log.AppendLine($"  直径：Ø{d} mm");
        }

        private static void DumpConnector(MEPLogger log, Element owner, Connector c, int idx)
        {
            var o = c.Origin;
            string size;
            if (c.Shape == ConnectorProfileType.Rectangular)
                size = $"{c.Width * 304.8:F1}×{c.Height * 304.8:F1}mm";
            else
                size = $"Ø{c.Radius * 2 * 304.8:F1}mm";

            log.AppendLine($"  连接器#{idx}: 位置=({o.X:F3},{o.Y:F3},{o.Z * 304.8:F1}mm) " +
                           $"形状={c.Shape} 截面={size} IsConnected={c.IsConnected}");

            if (!c.IsConnected) return;

            var refs = c.AllRefs?.Cast<Connector>().ToList();
            if (refs == null || refs.Count == 0)
            {
                log.AppendLine("    AllRefs：空");
                return;
            }

            foreach (var r in refs)
            {
                var n = r.Owner;
                if (n == null)
                {
                    log.AppendLine("    对端：Owner=null");
                    continue;
                }
                if (n.Id == owner.Id)
                {
                    log.AppendLine($"    对端：自身连接器 #{r.Id}（{r.Origin.X:F3},{r.Origin.Y:F3},{r.Origin.Z * 304.8:F1}mm）");
                    continue;
                }

                string nCat = n.Category?.Name ?? "无类别";
                string nFam = GetFamilyName(n);
                string nSym = GetSymbolName(n);
                var ro = r.Origin;
                log.AppendLine($"    对端：[{n.Id.IntegerValue}] {nCat} | 族={nFam} | 类型={nSym} | " +
                               $"位置=({ro.X:F3},{ro.Y:F3},{ro.Z * 304.8:F1}mm)");
            }
        }

        private static void DumpNearbyFittings(Document doc, MEPLogger log, Element elem)
        {
            try
            {
                var bb = elem.get_BoundingBox(null);
                if (bb == null) return;

                double r = UnitUtils.ConvertToInternalUnits(ProbeRadiusMm, UnitTypeId.Millimeters);
                var outline = new Outline(
                    new XYZ(bb.Min.X - r, bb.Min.Y - r, bb.Min.Z - r),
                    new XYZ(bb.Max.X + r, bb.Max.Y + r, bb.Max.Z + r));

                var cats = new List<ElementFilter>
                {
                    new ElementCategoryFilter(BuiltInCategory.OST_CableTrayFitting),
                    new ElementCategoryFilter(BuiltInCategory.OST_DuctFitting),
                    new ElementCategoryFilter(BuiltInCategory.OST_PipeFitting),
                };
                ElementFilter catFilter = new LogicalOrFilter(cats);

                var nearby = new FilteredElementCollector(doc)
                    .WherePasses(new BoundingBoxIntersectsFilter(outline))
                    .WherePasses(catFilter)
                    .WhereElementIsNotElementType()
                    .ToElements()
                    .Where(e => e.Id != elem.Id)
                    .ToList();

                if (nearby.Count == 0)
                {
                    log.AppendLine($"  周边 {ProbeRadiusMm:F0}mm 内：无管件");
                    return;
                }

                log.AppendLine($"  周边 {ProbeRadiusMm:F0}mm 内管件：{nearby.Count} 个");
                foreach (var f in nearby)
                {
                    string loc = "—";
                    if (f.Location is LocationCurve flc && flc.Curve is Line fl)
                    {
                        var fp = fl.GetEndPoint(0);
                        loc = $"({fp.X:F3},{fp.Y:F3},{fp.Z * 304.8:F1}mm)";
                    }
                    else if (f.Location is LocationPoint fp2)
                    {
                        loc = $"({fp2.Point.X:F3},{fp2.Point.Y:F3},{fp2.Point.Z * 304.8:F1}mm)";
                    }
                    log.AppendLine($"    [{f.Id.IntegerValue}] {f.Category?.Name} | 族={GetFamilyName(f)} | " +
                                   $"类型={GetSymbolName(f)} | 位置={loc}");
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("  周边管件扫描失败：" + ex.Message);
            }
        }

        private static ConnectorManager GetConnectorManager(Element e)
        {
            if (e is MEPCurve mep) return mep.ConnectorManager;
            if (e is FamilyInstance fi && fi.MEPModel != null) return fi.MEPModel.ConnectorManager;
            return null;
        }

        private static string GetFamilyName(Element e)
        {
            if (e is FamilyInstance fi) return fi.Symbol?.Family?.Name ?? "—";
            return "—";
        }

        private static string GetSymbolName(Element e)
        {
            if (e is FamilyInstance fi) return fi.Symbol?.Name ?? "—";
            return e?.Name ?? "—";
        }

        private static string ReadParamMm(Element e, BuiltInParameter bip)
        {
            try
            {
                var p = e.get_Parameter(bip);
                if (p == null || p.StorageType != StorageType.Double) return null;
                double v = p.AsDouble();
                if (v <= 1e-9) return null;
                return (v * 304.8).ToString("F1");
            }
            catch { return null; }
        }

        private class ProbeFilter : ISelectionFilter
        {
            public bool AllowElement(Element e)
            {
                return e is MEPCurve || (e is FamilyInstance fi && fi.MEPModel != null);
            }

            public bool AllowReference(Reference r, XYZ p) => false;
        }
    }
}
