using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

// 公共日志（独立到 MEPTools.Shared）
using MEPTools.Shared;
// 解决 Connector 命名冲突（只保留一个别名）
using Connector = Autodesk.Revit.DB.Connector;

namespace MEPTools.MEPPlanarConnector
{
    public class MEPPlanarConnectorService
    {
        private readonly MEPLogger _log;
        private const double FtToMm = 304.8;

        public MEPPlanarConnectorService() : this(MEPLogger.Instance) { }
        public MEPPlanarConnectorService(MEPLogger log) { _log = log ?? MEPLogger.Instance; }

        private enum MepKind { Pipe, Duct, CableTray }

        private static MepKind? GetMepKind(Element el)
        {
            if (el is Pipe) return MepKind.Pipe;
            if (el is Duct) return MepKind.Duct;
            if (el is CableTray) return MepKind.CableTray;
            return null;
        }

        private class Snap
        {
            public MepKind Kind;
            public ElementId TypeId;
            public ElementId LevelId;
            public ElementId SystemTypeId;
            public double Diameter, Width, Height;
        }

        private static Snap CaptureSnapshot(Element el)
        {
            var s = new Snap { Kind = GetMepKind(el).Value };
            s.TypeId = el.GetTypeId();
            s.LevelId = el.LevelId;
            if (s.LevelId == ElementId.InvalidElementId)
            {
                var lp = el.get_Parameter(BuiltInParameter.RBS_START_LEVEL_PARAM);
                if (lp != null) s.LevelId = lp.AsElementId();
            }
            if (s.LevelId == ElementId.InvalidElementId)
            {
                var lv = new FilteredElementCollector(el.Document)
                    .OfClass(typeof(Level)).FirstOrDefault() as Level;
                if (lv != null) s.LevelId = lv.Id;
            }

            if (el is Pipe pipe)
            {
                var psys = pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM);
                if (psys != null) s.SystemTypeId = psys.AsElementId();
                var pd = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                if (pd != null) s.Diameter = pd.AsDouble();
            }
            else if (el is Duct duct)
            {
                var dsys = duct.get_Parameter(BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM);
                if (dsys != null) s.SystemTypeId = dsys.AsElementId();
                var pd = duct.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                if (pd != null) s.Diameter = pd.AsDouble();
                var pw = duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                if (pw != null) s.Width = pw.AsDouble();
                var ph = duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
                if (ph != null) s.Height = ph.AsDouble();
            }
            else if (el is CableTray tray)
            {
                var pw = tray.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
                if (pw != null) s.Width = pw.AsDouble();
                var ph = tray.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
                if (ph != null) s.Height = ph.AsDouble();
            }
            return s;
        }

        // =====================================================================
        // 主处理：TR 语义（延长 + 弯头）
        // =====================================================================
        public bool ProcessPair(Document doc, ElementId aId, ElementId bId)
        {
            Element a = doc.GetElement(aId);
            Element b = doc.GetElement(bId);
            if (a == null || b == null) { _log.AppendLine("✗ 元素不存在"); return false; }

            var g = MEPPlanarConnectorCalculator.Compute(a, b);
            if (!g.IsValid) { _log.AppendLine("✗ " + g.ErrorMessage); return false; }

            _log.AppendLine("  P_A = " + MEPPlanarConnectorCalculator.Fmt(g.PA));
            _log.AppendLine("  P_B = " + MEPPlanarConnectorCalculator.Fmt(g.PB));
            _log.AppendLine("  P   = " + MEPPlanarConnectorCalculator.Fmt(g.P));
            _log.AppendLine("  共线 = " + g.IsCollinear);

            if (g.IsCollinear)
            {
                // ---- 共线：延长 A 到 P_B（TR 效果），无弯头 ----
                if (g.PA.DistanceTo(g.PB) < 1e-4)
                {
                    _log.AppendLine("  ℹ P_A 与 P_B 已重合，无需延长");
                    return true;
                }

                if (!ExtendElementEndpoint(doc, a, g.PA, g.PB))
                {
                    _log.AppendLine("✗ 延长 A 组管失败");
                    return false;
                }
                _log.AppendLine("  ✓ A 组管已延长至 P_B，共线直连完成");
                return true;
            }

            // ---- 非共线：A、B 各自延伸到交点 P，然后在 P 处放弯头 ----
            if (g.PA.DistanceTo(g.P) > 1e-4)
            {
                if (!ExtendElementEndpoint(doc, a, g.PA, g.P))
                { _log.AppendLine("✗ 延长 A 组管到 P 失败"); return false; }
                _log.AppendLine("  ✓ A 组管已延长至 P");
            }
            else
            {
                _log.AppendLine("  ℹ P_A 已在 P，无需延长 A");
            }

            if (g.PB.DistanceTo(g.P) > 1e-4)
            {
                if (!ExtendElementEndpoint(doc, b, g.PB, g.P))
                { _log.AppendLine("✗ 延长 B 组管到 P 失败"); return false; }
                _log.AppendLine("  ✓ B 组管已延长至 P");
            }
            else
            {
                _log.AppendLine("  ℹ P_B 已在 P，无需延长 B");
            }

            doc.Regenerate();

            // 在 P 处放弯头（此时 A、B 端点均重合于 P）
            TryCreateElbow(doc, aId, bId, g.P, "弯头");
            return true;
        }

        /// <summary>
        /// 把元素 e 中"位于 oldEndPt 的端点"移动到 newEndPt。
        /// 返回 false 表示修改失败。
        /// </summary>
        private bool ExtendElementEndpoint(Document doc, Element e, XYZ oldEndPt, XYZ newEndPt)
        {
            if (e == null) { _log.AppendLine("✗ 元素为空"); return false; }
            if (oldEndPt == null || newEndPt == null) { _log.AppendLine("✗ 端点为空"); return false; }

            var mc = e as MEPCurve;
            var lc = mc?.Location as LocationCurve;
            var line = lc?.Curve as Line;
            if (line == null) { _log.AppendLine("✗ 元素非直线段，无法延长"); return false; }

            XYZ p0 = line.GetEndPoint(0);
            XYZ p1 = line.GetEndPoint(1);

            const double tol = 1e-3;
            XYZ newP0 = p0, newP1 = p1;
            if (p0.DistanceTo(oldEndPt) < tol) newP0 = newEndPt;
            else if (p1.DistanceTo(oldEndPt) < tol) newP1 = newEndPt;
            else
            {
                _log.AppendLine("✗ 未找到匹配的端点（" +
                    MEPPlanarConnectorCalculator.Fmt(oldEndPt) + "）");
                return false;
            }

            if (newP0.DistanceTo(newP1) < 1e-4)
            {
                _log.AppendLine("✗ 延长后长度为零");
                return false;
            }

            try
            {
                lc.Curve = Line.CreateBound(newP0, newP1);
                doc.Regenerate();
                return true;
            }
            catch (Exception ex)
            {
                _log.AppendLine("✗ 修改定位曲线失败：" + ex.Message);
                return false;
            }
        }

        // =====================================================================
        // 通用辅助（保留 CreateMEPCurve，供未来扩展）
        // =====================================================================
        private Element CreateMEPCurve(Document doc, Snap snap, XYZ s, XYZ e)
        {
            if (s == null || e == null) return null;
            if (s.IsAlmostEqualTo(e, 1e-6)) return null;

            Element el = null;
            switch (snap.Kind)
            {
                case MepKind.Pipe:
                    el = Pipe.Create(doc, snap.SystemTypeId, snap.TypeId, snap.LevelId, s, e);
                    if (el != null && snap.Diameter > 0)
                    {
                        var pd = el.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                        if (pd != null && !pd.IsReadOnly) pd.Set(snap.Diameter);
                    }
                    break;
                case MepKind.Duct:
                    el = Duct.Create(doc, snap.SystemTypeId, snap.TypeId, snap.LevelId, s, e);
                    if (el != null)
                    {
                        if (snap.Diameter > 0)
                        {
                            var pd = el.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                            if (pd != null && !pd.IsReadOnly) pd.Set(snap.Diameter);
                        }
                        if (snap.Width > 0)
                        {
                            var pw = el.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                            if (pw != null && !pw.IsReadOnly) pw.Set(snap.Width);
                        }
                        if (snap.Height > 0)
                        {
                            var ph = el.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
                            if (ph != null && !ph.IsReadOnly) ph.Set(snap.Height);
                        }
                    }
                    break;
                case MepKind.CableTray:
                    el = CableTray.Create(doc, snap.TypeId, s, e, snap.LevelId);
                    if (el != null)
                    {
                        if (snap.Width > 0)
                        {
                            var pw = el.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
                            if (pw != null && !pw.IsReadOnly) pw.Set(snap.Width);
                        }
                        if (snap.Height > 0)
                        {
                            var ph = el.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
                            if (ph != null && !ph.IsReadOnly) ph.Set(snap.Height);
                        }
                    }
                    break;
            }
            return el;
        }

        private bool TryCreateElbow(Document doc, ElementId idA, ElementId idB,
            XYZ atPt, string tag)
        {
            var elA = doc.GetElement(idA);
            var elB = doc.GetElement(idB);
            if (elA == null || elB == null) { _log.AppendLine("✗ " + tag + ": 元素为空"); return false; }

            Connector ca = GetConnectorAtPoint(elA, atPt);
            Connector cb = GetConnectorAtPoint(elB, atPt);

            if (ca == null || cb == null)
            {
                FindClosestConnectorPair(elA, elB, out ca, out cb, out double pd);
                if (ca == null || cb == null)
                { _log.AppendLine("✗ " + tag + ": 无可用连接器对"); return false; }
                _log.AppendLine("  " + tag + ": 最近连接器对 " + (pd * FtToMm).ToString("F1") + "mm");
            }

            double dist = ca.Origin.DistanceTo(cb.Origin);
            double cap = UnitUtils.ConvertToInternalUnits(1000.0, UnitTypeId.Millimeters);
            if (dist > cap)
            {
                _log.AppendLine("✗ " + tag + ": 连接器间距过大 " +
                    (dist * FtToMm).ToString("F1") + "mm");
                return false;
            }

            double elbowDeg = 180.0 - ca.CoordinateSystem.BasisZ
                .AngleTo(cb.CoordinateSystem.BasisZ) * 180.0 / Math.PI;
            _log.AppendLine("  " + tag + ": 间距=" + (dist * FtToMm).ToString("F1") +
                            "mm 内角=" + elbowDeg.ToString("F2") + "°");

            try
            {
                FamilyInstance fit = doc.Create.NewElbowFitting(ca, cb);
                if (fit == null) { _log.AppendLine("✗ " + tag + ": NewElbowFitting 返回 null"); return false; }
                _log.AppendLine("✓ " + tag + ": 弯头 ID=" + fit.Id.IntegerValue);
                return true;
            }
            catch (Exception ex)
            { _log.AppendLine("✗ " + tag + ": " + ex.Message); return false; }
        }

        private Connector GetConnectorAtPoint(Element elem, XYZ pt)
        {
            var cm = GetConnectorManager(elem);
            if (cm == null) return null;

            double tol = UnitUtils.ConvertToInternalUnits(20.0, UnitTypeId.Millimeters);
            Connector best = null;
            double bestD = double.MaxValue;
            foreach (Connector c in cm.Connectors)
            {
                double d = c.Origin.DistanceTo(pt);
                if (d < bestD) { bestD = d; best = c; }
            }
            return (best != null && bestD < tol) ? best : null;
        }

        private void FindClosestConnectorPair(Element a, Element b,
            out Connector ca, out Connector cb, out double distance)
        {
            ca = null; cb = null; distance = double.MaxValue;
            var cma = GetConnectorManager(a);
            var cmb = GetConnectorManager(b);
            if (cma == null || cmb == null) return;

            foreach (Connector c1 in cma.Connectors)
            {
                if (c1.IsConnected) continue;
                foreach (Connector c2 in cmb.Connectors)
                {
                    if (c2.IsConnected) continue;
                    double d = c1.Origin.DistanceTo(c2.Origin);
                    if (d < distance) { distance = d; ca = c1; cb = c2; }
                }
            }
        }

        private static ConnectorManager GetConnectorManager(Element elem)
        {
            if (elem is MEPCurve mep) return mep.ConnectorManager;
            if (elem is FamilyInstance fi && fi.MEPModel != null) return fi.MEPModel.ConnectorManager;
            return null;
        }
    }
}