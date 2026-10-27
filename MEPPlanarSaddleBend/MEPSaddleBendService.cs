using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace MEPTools.MEPPlanarSaddleBend
{
    public class MEPSaddleBendService
    {
        private readonly MEPLogger _log;
        private const double FtToMm = 304.8;

        public MEPSaddleBendService() : this(MEPLogger.Instance) { }

        public MEPSaddleBendService(MEPLogger log)
        {
            _log = log ?? MEPLogger.Instance;
        }

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
            public double Diameter;
            public double Width, Height;
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
        // 单元素处理
        // =====================================================================
        public bool ProcessSingleElement(Document doc, ElementId elemId, SaddleBendResult r)
        {
            if (r == null || !r.IsValid) { _log.AppendLine("✗ 计算结果无效"); return false; }

            Element elem = doc.GetElement(elemId);
            if (elem == null || !elem.IsValidObject) { _log.AppendLine("✗ 元素无效"); return false; }

            var kind = GetMepKind(elem);
            if (kind == null) { _log.AppendLine("✗ 不支持的类型"); return false; }

            _log.AppendLine("元素 ID=" + elemId.IntegerValue);
            _log.AppendLine("  P0  = " + MEPSaddleBendCalculator.Fmt(r.P0));
            _log.AppendLine("  A   = " + MEPSaddleBendCalculator.Fmt(r.A));
            _log.AppendLine("  B   = " + MEPSaddleBendCalculator.Fmt(r.B));
            _log.AppendLine("  P2p = " + MEPSaddleBendCalculator.Fmt(r.P2p));
            _log.AppendLine(string.Format("  MidLen={0:F1}mm, ExtendE0={1}, ExtendE1={2}",
                r.MidLen * FtToMm, r.ExtendE0, r.ExtendE1));

            var snap = CaptureSnapshot(elem);

            if (r.ExtendE0 || r.ExtendE1)
            {
                if (!ExtendCurveEnds(doc, elem, r))
                {
                    _log.AppendLine("✗ 延长管段失败");
                    return false;
                }
            }

            if (kind == MepKind.CableTray)
                return ProcessCableTray(doc, elem as CableTray, r, snap);

            return ProcessPipeDuct(doc, elem, r, snap, kind.Value);
        }

        // =====================================================================
        // 延长原管端点
        // =====================================================================
        private bool ExtendCurveEnds(Document doc, Element elem, SaddleBendResult r)
        {
            var lc = (elem as MEPCurve)?.Location as LocationCurve;
            var line = (lc == null) ? null : (lc.Curve as Line);
            if (line == null) { _log.AppendLine("✗ 延长：非直线段"); return false; }

            XYZ oldE0 = line.GetEndPoint(0);
            XYZ oldE1 = line.GetEndPoint(1);
            XYZ newE0 = r.ExtendE0 ? r.P0 : oldE0;
            XYZ newE1 = r.ExtendE1 ? r.P2p : oldE1;

            if (newE0.DistanceTo(newE1) < 1e-6)
            {
                _log.AppendLine("✗ 延长后管段长度为零");
                return false;
            }

            try
            {
                lc.Curve = Line.CreateBound(newE0, newE1);
                doc.Regenerate();
                r.OriginalEnd0 = newE0;
                r.OriginalEnd1 = newE1;
                r.OriginalCurve = lc.Curve;
                _log.AppendLine("✓ 延长: E0=" + MEPSaddleBendCalculator.Fmt(newE0) +
                                " E1=" + MEPSaddleBendCalculator.Fmt(newE1));
                return true;
            }
            catch (Exception ex)
            {
                _log.AppendLine("✗ 延长异常: " + ex.Message);
                return false;
            }
        }

        // =====================================================================
        // Pipe / Duct：两次打断 + 保留 head/tail + 只重建 mid
        // =====================================================================
        private bool ProcessPipeDuct(Document doc, Element elem, SaddleBendResult r,
            Snap snap, MepKind kind)
        {
            // 判定哪个切点离 E0 更远（沿轴投影）
            double dP0 = (r.P0 - r.OriginalEnd0).DotProduct(r.AxisDir);
            double dP2p = (r.P2p - r.OriginalEnd0).DotProduct(r.AxisDir);
            bool p0IsFar = dP0 > dP2p;

            XYZ firstPt = p0IsFar ? r.P0 : r.P2p;
            XYZ secondPt = p0IsFar ? r.P2p : r.P0;

            // ---------- 第一次打断 ----------
            ElementId firstNewId = ElementId.InvalidElementId;
            if (!IsAtSegmentEnd(doc, elem.Id, firstPt))
            {
                firstNewId = BreakCurveAt(doc, kind, elem.Id, firstPt);
                if (firstNewId == ElementId.InvalidElementId)
                {
                    _log.AppendLine("✗ 打断 " + MEPSaddleBendCalculator.Fmt(firstPt) + " 失败");
                    return false;
                }
                doc.Regenerate();
                _log.AppendLine("打断 @ " + MEPSaddleBendCalculator.Fmt(firstPt) +
                                " → 新段 " + firstNewId.IntegerValue);
            }
            else
            {
                _log.AppendLine("跳过打断 " + MEPSaddleBendCalculator.Fmt(firstPt) + "（与端点重合）");
            }

            // ---------- 第二次打断 ----------
            var segPool = new List<ElementId> { elem.Id };
            if (firstNewId != ElementId.InvalidElementId) segPool.Add(firstNewId);

            ElementId secondSegId = FindSegmentContainingPoint(doc, segPool, secondPt);
            if (secondSegId == ElementId.InvalidElementId)
            {
                _log.AppendLine("✗ 找不到含 " + MEPSaddleBendCalculator.Fmt(secondPt) + " 的段");
                return false;
            }

            ElementId secondNewId = ElementId.InvalidElementId;
            if (!IsAtSegmentEnd(doc, secondSegId, secondPt))
            {
                secondNewId = BreakCurveAt(doc, kind, secondSegId, secondPt);
                if (secondNewId == ElementId.InvalidElementId)
                {
                    _log.AppendLine("✗ 打断 " + MEPSaddleBendCalculator.Fmt(secondPt) + " 失败");
                    return false;
                }
                doc.Regenerate();
                _log.AppendLine("打断 @ " + MEPSaddleBendCalculator.Fmt(secondPt) +
                                " → 新段 " + secondNewId.IntegerValue);
            }
            else
            {
                _log.AppendLine("跳过打断 " + MEPSaddleBendCalculator.Fmt(secondPt) + "（与端点重合）");
            }

            // ---------- 汇总所有段 ----------
            var allIds = new List<ElementId> { elem.Id };
            if (firstNewId != ElementId.InvalidElementId) allIds.Add(firstNewId);
            if (secondNewId != ElementId.InvalidElementId) allIds.Add(secondNewId);

            ElementId headSeg = ElementId.InvalidElementId;
            ElementId tailSeg = ElementId.InvalidElementId;
            ElementId midSeg = ElementId.InvalidElementId;

            var seen = new HashSet<int>();
            double tol = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);

            foreach (var id in allIds)
            {
                if (!seen.Add(id.IntegerValue)) continue;
                var mc = doc.GetElement(id) as MEPCurve;
                var line = (mc == null) ? null : (mc.Location as LocationCurve)?.Curve as Line;
                if (line == null) continue;

                XYZ a = line.GetEndPoint(0);
                XYZ b = line.GetEndPoint(1);

                if (a.DistanceTo(r.OriginalEnd0) < tol || b.DistanceTo(r.OriginalEnd0) < tol)
                    headSeg = id;
                else if (a.DistanceTo(r.OriginalEnd1) < tol || b.DistanceTo(r.OriginalEnd1) < tol)
                    tailSeg = id;
                else
                    midSeg = id;
            }

            _log.AppendLine("识别 head=" + FmtId(headSeg) + " mid=" + FmtId(midSeg) +
                            " tail=" + FmtId(tailSeg) + " p0IsFar=" + p0IsFar);

            if (midSeg == ElementId.InvalidElementId)
            {
                _log.AppendLine("✗ 未识别到中间段");
                return false;
            }

            try { doc.Delete(midSeg); }
            catch (Exception ex)
            {
                _log.AppendLine("✗ 删除 mid 失败: " + ex.Message);
                return false;
            }
            doc.Regenerate();

            // ---------- 创建 3 段 ----------
            XYZ[] pts = r.Points;
            Element seg1 = CreateMEPCurve(doc, snap, pts[0], pts[1]);
            if (seg1 == null) { _log.AppendLine("✗ 斜段1 创建失败"); return false; }

            Element seg2 = null;
            if (!pts[1].IsAlmostEqualTo(pts[2], 1e-6))
            {
                seg2 = CreateMEPCurve(doc, snap, pts[1], pts[2]);
                if (seg2 == null) { _log.AppendLine("✗ 平移段 创建失败"); return false; }
            }
            else
            {
                _log.AppendLine("  ℹ 平移段长度为零，退化为双斜段对接");
            }

            Element seg3 = CreateMEPCurve(doc, snap, pts[2], pts[3]);
            if (seg3 == null) { _log.AppendLine("✗ 斜段2 创建失败"); return false; }

            doc.Regenerate();

            // ============================================================
            // 4 个弯头 —— 关键修复：按 p0IsFar 决定 head/tail 连哪一端
            // ============================================================
            // 内部弯头（A、B 处）两种情况都一样
            if (seg2 != null)
            {
                TryCreateElbow(doc, seg1.Id, seg2.Id, pts[1], "弯头(斜段1-平移)");
                TryCreateElbow(doc, seg2.Id, seg3.Id, pts[2], "弯头(平移-斜段2)");
            }
            else
            {
                TryCreateElbow(doc, seg1.Id, seg3.Id, pts[1], "弯头(斜段1-斜段2)");
            }

            // 外部弯头：按 p0IsFar 分支
            if (p0IsFar)
            {
                // 段划分：[E0,P2p] [P2p,P0] [P0,E1]
                // head=[E0,P2p] 终止于 P2p  → head ↔ seg3（seg3 的 P2p 端）
                // tail=[P0,E1] 起始于 P0   → seg1 ↔ tail
                if (headSeg != ElementId.InvalidElementId)
                    TryCreateElbow(doc, headSeg, seg3.Id, r.P2p, "弯头(head-斜段2 @ P2p)");
                if (tailSeg != ElementId.InvalidElementId)
                    TryCreateElbow(doc, seg1.Id, tailSeg, r.P0, "弯头(斜段1-tail @ P0)");
            }
            else
            {
                // 段划分：[E0,P0] [P0,P2p] [P2p,E1]
                // head=[E0,P0] 终止于 P0    → head ↔ seg1
                // tail=[P2p,E1] 起始于 P2p → seg3 ↔ tail
                if (headSeg != ElementId.InvalidElementId)
                    TryCreateElbow(doc, headSeg, seg1.Id, r.P0, "弯头(head-斜段1 @ P0)");
                if (tailSeg != ElementId.InvalidElementId)
                    TryCreateElbow(doc, seg3.Id, tailSeg, r.P2p, "弯头(斜段2-tail @ P2p)");
            }

            return true;
        }

        // =====================================================================
        // CableTray：无 BreakCurve，全删重建 + 两端精确重连
        // =====================================================================
        private bool ProcessCableTray(Document doc, CableTray tray, SaddleBendResult r, Snap snap)
        {
            if (tray == null) { _log.AppendLine("✗ CableTray 为空"); return false; }

            Connector extE0 = CaptureExternalConnectorAt(tray, r.OriginalEnd0);
            Connector extE1 = CaptureExternalConnectorAt(tray, r.OriginalEnd1);

            try { doc.Delete(tray.Id); }
            catch (Exception ex)
            {
                _log.AppendLine("✗ 删除 CT 失败: " + ex.Message);
                return false;
            }
            doc.Regenerate();

            XYZ e0 = r.OriginalEnd0, e1 = r.OriginalEnd1;
            XYZ[] pts = r.Points;
            var segs = new List<Element>();

            if (!e0.IsAlmostEqualTo(pts[0], 1e-6))
            {
                var s = CreateMEPCurve(doc, snap, e0, pts[0]);
                if (s == null) { _log.AppendLine("✗ CT head 创建失败"); return false; }
                segs.Add(s);
            }
            var s1 = CreateMEPCurve(doc, snap, pts[0], pts[1]);
            if (s1 == null) { _log.AppendLine("✗ CT 斜段1 失败"); return false; }
            segs.Add(s1);

            if (!pts[1].IsAlmostEqualTo(pts[2], 1e-6))
            {
                var s2 = CreateMEPCurve(doc, snap, pts[1], pts[2]);
                if (s2 == null) { _log.AppendLine("✗ CT 平移段 失败"); return false; }
                segs.Add(s2);
            }

            var s3 = CreateMEPCurve(doc, snap, pts[2], pts[3]);
            if (s3 == null) { _log.AppendLine("✗ CT 斜段2 失败"); return false; }
            segs.Add(s3);

            if (!pts[3].IsAlmostEqualTo(e1, 1e-6))
            {
                var s = CreateMEPCurve(doc, snap, pts[3], e1);
                if (s == null) { _log.AppendLine("✗ CT tail 失败"); return false; }
                segs.Add(s);
            }

            doc.Regenerate();
            _log.AppendLine("CT 重建 " + segs.Count + " 段");

            for (int i = 0; i < segs.Count - 1; i++)
            {
                XYZ joint = FindCommonEndPoint(segs[i], segs[i + 1]);
                if (joint == null)
                {
                    _log.AppendLine("⚠ CT 段" + (i + 1) + "-" + (i + 2) + " 未找到公共端点");
                    continue;
                }
                TryCreateElbow(doc, segs[i].Id, segs[i + 1].Id, joint, "CT弯头" + (i + 1));
            }

            if (extE0 != null && segs.Count > 0)
                ReconnectExternal(doc, segs[0], e0, extE0, "CT-E0");
            if (extE1 != null && segs.Count > 0)
                ReconnectExternal(doc, segs[segs.Count - 1], e1, extE1, "CT-E1");

            return true;
        }

        // =====================================================================
        // 打断辅助
        // =====================================================================
        private ElementId BreakCurveAt(Document doc, MepKind kind, ElementId elemId, XYZ pt)
        {
            try
            {
                switch (kind)
                {
                    case MepKind.Pipe:
                        return PlumbingUtils.BreakCurve(doc, elemId, pt);
                    case MepKind.Duct:
                        return MechanicalUtils.BreakCurve(doc, elemId, pt);
                }
            }
            catch (Exception ex)
            {
                _log.AppendLine("BreakCurve 异常: " + ex.Message);
            }
            return ElementId.InvalidElementId;
        }

        private bool IsAtSegmentEnd(Document doc, ElementId id, XYZ pt)
        {
            var mc = doc.GetElement(id) as MEPCurve;
            var line = (mc == null) ? null : (mc.Location as LocationCurve)?.Curve as Line;
            if (line == null) return false;

            double tol = UnitUtils.ConvertToInternalUnits(0.5, UnitTypeId.Millimeters);
            return pt.DistanceTo(line.GetEndPoint(0)) < tol ||
                   pt.DistanceTo(line.GetEndPoint(1)) < tol;
        }

        private ElementId FindSegmentContainingPoint(Document doc,
            IEnumerable<ElementId> candidates, XYZ pt)
        {
            ElementId best = ElementId.InvalidElementId;
            double bestDist = double.MaxValue;

            foreach (var id in candidates)
            {
                var mc = doc.GetElement(id) as MEPCurve;
                var line = (mc == null) ? null : (mc.Location as LocationCurve)?.Curve as Line;
                if (line == null) continue;

                XYZ e0 = line.GetEndPoint(0);
                XYZ e1 = line.GetEndPoint(1);
                XYZ dir = e1 - e0;
                double len = dir.GetLength();
                if (len < 1e-9) continue;
                dir = dir / len;

                XYZ v = pt - e0;
                double t = v.DotProduct(dir);
                if (t < -1e-3 || t > len + 1e-3) continue;

                XYZ onLine = e0 + dir * t;
                double d = onLine.DistanceTo(pt);
                if (d < bestDist) { bestDist = d; best = id; }
            }
            return best;
        }

        // =====================================================================
        // 创建管段
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

        // =====================================================================
        // 弯头
        // =====================================================================
        private bool TryCreateElbow(Document doc, ElementId idA, ElementId idB,
            XYZ atPt, string tag)
        {
            var elA = doc.GetElement(idA);
            var elB = doc.GetElement(idB);
            if (elA == null || elB == null)
            {
                _log.AppendLine("✗ " + tag + ": 关联元素为空");
                return false;
            }

            var ca = GetConnectorAtPoint(elA, atPt);
            var cb = GetConnectorAtPoint(elB, atPt);

            if (ca == null || cb == null)
            {
                FindClosestConnectorPair(elA, elB, out ca, out cb, out double pd);
                if (ca == null || cb == null)
                {
                    _log.AppendLine("✗ " + tag + ": 找不到可连接的连接器对");
                    return false;
                }
                _log.AppendLine("  " + tag + ": 使用最近连接器对 距离=" +
                    (pd * FtToMm).ToString("F1") + "mm");
            }

            double dist = ca.Origin.DistanceTo(cb.Origin);
            double cap = UnitUtils.ConvertToInternalUnits(1000.0, UnitTypeId.Millimeters);
            if (dist > cap)
            {
                _log.AppendLine("✗ " + tag + ": 连接器间距过大 " +
                    (dist * FtToMm).ToString("F1") + "mm");
                return false;
            }

            double deg = 180.0 - ca.CoordinateSystem.BasisZ
                .AngleTo(cb.CoordinateSystem.BasisZ) * 180.0 / Math.PI;
            _log.AppendLine("  " + tag + ": 间距=" + (dist * FtToMm).ToString("F1") +
                            "mm 内角=" + deg.ToString("F2") + "°");

            if (deg < 2.0 || deg > 95.0)
            {
                _log.AppendLine("✗ " + tag + ": 弯头角度超范围 (2~95°)");
                return false;
            }

            try
            {
                FamilyInstance fit = doc.Create.NewElbowFitting(ca, cb);
                if (fit == null)
                {
                    _log.AppendLine("✗ " + tag + ": NewElbowFitting 返回 null");
                    return false;
                }
                _log.AppendLine("✓ " + tag + ": 弯头 ID=" + fit.Id.IntegerValue);
                return true;
            }
            catch (Exception ex)
            {
                _log.AppendLine("✗ " + tag + ": " + ex.Message);
                return false;
            }
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

        // =====================================================================
        // 外部连接（只重连 FamilyInstance，沿用 LateralBend）
        // =====================================================================
        private Connector CaptureExternalConnectorAt(MEPCurve curve, XYZ pt)
        {
            if (curve == null) return null;
            var cm = curve.ConnectorManager;
            if (cm == null) return null;

            Connector near = null;
            double best = double.MaxValue;
            foreach (Connector c in cm.Connectors)
            {
                double d = c.Origin.DistanceTo(pt);
                if (d < best) { best = d; near = c; }
            }
            if (near == null || !near.IsConnected) return null;

            foreach (Connector c in near.AllRefs)
            {
                if (c.Owner != null && c.Owner.Id != curve.Id && c.Owner is FamilyInstance)
                    return c;
            }
            return null;
        }

        private void ReconnectExternal(Document doc, Element newSeg, XYZ targetPt,
            Connector externalConn, string tag)
        {
            if (externalConn == null) return;
            if (externalConn.IsConnected)
            {
                _log.AppendLine("  " + tag + ": 外部已自动重连");
                return;
            }

            var c = GetConnectorAtPoint(newSeg, targetPt);
            if (c == null)
            {
                _log.AppendLine("⚠ " + tag + ": 新段上找不到目标连接器");
                return;
            }
            if (c.IsConnected)
            {
                _log.AppendLine("  " + tag + ": 已连接");
                return;
            }

            try
            {
                c.ConnectTo(externalConn);
                _log.AppendLine("✓ " + tag + ": 重连成功");
            }
            catch (Exception ex)
            {
                _log.AppendLine("⚠ " + tag + ": 重连失败 " + ex.Message);
            }
        }

        // =====================================================================
        // 通用辅助
        // =====================================================================
        private static ConnectorManager GetConnectorManager(Element elem)
        {
            if (elem is MEPCurve mep) return mep.ConnectorManager;
            if (elem is FamilyInstance fi && fi.MEPModel != null)
                return fi.MEPModel.ConnectorManager;
            return null;
        }

        private static XYZ FindCommonEndPoint(Element a, Element b)
        {
            var lca = a.Location as LocationCurve;
            var lcb = b.Location as LocationCurve;
            if (lca == null || lcb == null) return null;

            const double tol = 1e-3;
            foreach (var pa in new[] { lca.Curve.GetEndPoint(0), lca.Curve.GetEndPoint(1) })
                foreach (var pb in new[] { lcb.Curve.GetEndPoint(0), lcb.Curve.GetEndPoint(1) })
                    if (pa.IsAlmostEqualTo(pb, tol)) return pa;
            return null;
        }

        private static string FmtId(ElementId id)
        {
            return (id == null || id == ElementId.InvalidElementId) ? "—" : id.IntegerValue.ToString();
        }
    }
}