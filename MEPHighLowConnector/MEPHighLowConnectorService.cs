using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

// 解决 Connector 命名冲突
using Connector = Autodesk.Revit.DB.Connector;

namespace MEPTools.MEPHighLowConnector
{
    public class MEPConnectorService
    {
        private readonly MEPLogger _log;
        private const double FtToMm = 304.8;

        public MEPConnectorService() : this(MEPLogger.Instance) { }
        public MEPConnectorService(MEPLogger log) { _log = log ?? MEPLogger.Instance; }

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

        public bool ProcessPair(Document doc, ElementId aId, ElementId bId, double thetaDeg)
        {
            Element a = doc.GetElement(aId);
            Element b = doc.GetElement(bId);
            if (a == null || b == null) { _log.AppendLine("✗ 元素不存在"); return false; }

            var g = MEPConnectorCalculator.ComputeGeometry(a, b, thetaDeg);
            if (!g.IsValid) { _log.AppendLine("✗ " + g.ErrorMessage); return false; }

            _log.AppendLine("  P_A = " + MEPConnectorCalculator.Fmt(g.PA));
            _log.AppendLine("  P_B = " + MEPConnectorCalculator.Fmt(g.PB));
            _log.AppendLine("  B 远端 = " + MEPConnectorCalculator.Fmt(g.BFarPt));

            if (g.PB.DistanceTo(g.BFarPt) < 1e-4)
            { _log.AppendLine("✗ P_B 与 B 组管远端重合"); return false; }

            // ---- 修改 B 组管端点：靠 A 组的端点 → P_B ----
            var bCurve = (b as MEPCurve)?.Location as LocationCurve;
            if (bCurve == null) { _log.AppendLine("✗ B 组管无定位曲线"); return false; }
            Line bLine = bCurve.Curve as Line;
            if (bLine == null) { _log.AppendLine("✗ B 组管非直线"); return false; }

            XYZ b0 = bLine.GetEndPoint(0);
            XYZ b1 = bLine.GetEndPoint(1);
            double d0 = b0.DistanceTo(g.PA);
            double d1 = b1.DistanceTo(g.PA);
            XYZ bNear = (d0 < d1) ? b0 : b1;
            XYZ bFar  = (d0 < d1) ? b1 : b0;

            XYZ newStart, newEnd;
            if (bNear.IsAlmostEqualTo(b0)) { newStart = g.PB; newEnd = bFar; }
            else { newStart = bFar; newEnd = g.PB; }

            if (newStart.DistanceTo(newEnd) < 1e-4)
            { _log.AppendLine("✗ 修改后 B 组管长度为零"); return false; }

            try
            {
                bCurve.Curve = Line.CreateBound(newStart, newEnd);
                doc.Regenerate();
                _log.AppendLine("  ✓ B 组管端点已调整：" + MEPConnectorCalculator.Fmt(bNear)
                    + " → " + MEPConnectorCalculator.Fmt(g.PB));
            }
            catch (Exception ex)
            { _log.AppendLine("✗ 修改 B 组管失败：" + ex.Message); return false; }

            // ---- 创建斜段（用 A 组管类型）----
            var snap = CaptureSnapshot(a);
            Element ramp = CreateMEPCurve(doc, snap, g.PA, g.PB);
            if (ramp == null) { _log.AppendLine("✗ 斜段创建失败"); return false; }
            doc.Regenerate();
            _log.AppendLine("  ✓ 斜段 ID=" + ramp.Id.IntegerValue);

            // ---- 2 个弯头 ----
            TryCreateElbow(doc, aId, ramp.Id, g.PA, "弯头1(A-斜段)");
            TryCreateElbow(doc, ramp.Id, bId, g.PB, "弯头2(斜段-B)");

            return true;
        }

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
            { _log.AppendLine("✗ " + tag + ": 连接器间距过大 " + (dist * FtToMm).ToString("F1") + "mm"); return false; }

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