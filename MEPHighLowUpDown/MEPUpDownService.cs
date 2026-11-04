using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using MEPTools.Shared;

namespace MEPTools.MEPHighLowUpDown
{
    public class MEPUpDownService
    {
        private readonly StringBuilder _log;

        public MEPUpDownService(StringBuilder log)
        {
            _log = log ?? new StringBuilder();
        }

        public bool ProcessSingleElement(Document doc, ElementId elemId,
            XYZ breakPt, XYZ sidePt, double h_offset_ft)
        {
            Element elem = doc.GetElement(elemId);
            if (elem == null || !elem.IsValidObject)
            {
                _log.AppendLine("✗ 元素无效或已删除");
                return false;
            }

            MEPElementType elemType = MEPUpDownCalculator.GetElementType(elem);
            if (elemType == MEPElementType.Unknown)
            {
                _log.AppendLine("✗ 元素类型不支持 45° 抬升/下降");
                return false;
            }

            LocationCurve locCurve = elem.Location as LocationCurve;
            if (locCurve == null)
            {
                _log.AppendLine("✗ 无定位曲线");
                return false;
            }
            Curve curve = locCurve.Curve;

            string geomErr;
            UpDownGeometry geom = MEPUpDownCalculator.ComputeLift(
                curve, breakPt, sidePt, h_offset_ft, out geomErr);
            if (!geom.IsValid)
            {
                _log.AppendLine($"✗ {geomErr}");
                return false;
            }

            _log.AppendLine($"打断点 P0 = {MEPUpDownCalculator.Fmt(geom.P0)}");
            _log.AppendLine($"过渡终点 P1 = {MEPUpDownCalculator.Fmt(geom.P1)}");
            _log.AppendLine($"抬升方向: {(geom.LiftRight ? "沿管线方向" : "逆管线方向")}");
            _log.AppendLine($"高度偏移: {h_offset_ft * MEPUpDownCalculator.FtToMm:F1} mm");

            ElementId typeId = elem.GetTypeId();
            ElementId levelId = GetLevelId(elem, elemType);
            if (levelId == ElementId.InvalidElementId)
            {
                _log.AppendLine("✗ 读取标高 ID 失败");
                return false;
            }

            ElementId systemTypeId = ElementId.InvalidElementId;
            double diameter = 0, width = 0, height = 0;

            if (elem is Pipe pipe)
            {
                Parameter psys = pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM);
                if (psys != null) systemTypeId = psys.AsElementId();
                Parameter pd = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                if (pd != null) diameter = pd.AsDouble();
            }
            else if (elem is Duct duct)
            {
                Parameter psys = duct.get_Parameter(BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM);
                if (psys != null) systemTypeId = psys.AsElementId();
                Parameter pd = duct.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                if (pd != null) diameter = pd.AsDouble();
                Parameter pw = duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                if (pw != null) width = pw.AsDouble();
                Parameter ph = duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
                if (ph != null) height = ph.AsDouble();
            }
            else if (elem is CableTray tray)
            {
                Parameter pw = tray.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
                if (pw != null) width = pw.AsDouble();
                Parameter ph = tray.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
                if (ph != null) height = ph.AsDouble();
            }

            // ==================== 核心修复：系统类型获取策略 ====================
            if (elemType != MEPElementType.CableTray && systemTypeId == ElementId.InvalidElementId)
            {
                _log.AppendLine("  ℹ 管道无系统参数，尝试通过 MEPSystem 提取...");
                if (elem is MEPCurve mepCurve && mepCurve.MEPSystem != null)
                {
                    systemTypeId = mepCurve.MEPSystem.GetTypeId();
                }

                // 如果依然没有，尝试借用项目里的默认类型，保证 Create 不崩溃
                if (systemTypeId == ElementId.InvalidElementId)
                {
                    _log.AppendLine("  ℹ 尝试借用文档中同类默认系统...");
                    Type sysTypeClass = elemType == MEPElementType.Pipe ? typeof(PipingSystemType) : typeof(MechanicalSystemType);
                    var defaultSysType = new FilteredElementCollector(doc).OfClass(sysTypeClass).FirstOrDefault();
                    
                    if (defaultSysType != null)
                    {
                        systemTypeId = defaultSysType.Id;
                        _log.AppendLine($"  ℹ 成功借用系统 ID={systemTypeId.IntegerValue}");
                    }
                    else
                    {
                        // 连库里都没有，说明项目模板极度缺失，强行接会导致 Revit 崩溃
                        _log.AppendLine("  ✗ 文档中找不到任何可用的系统类型，无法重建。");
                        InfoCenterTip.Show("✗ 项目中没有可用的管线系统，请先在“系统浏览器”中创建一个系统。");
                        return false;
                    }
                }
            }
            // =================================================================

            XYZ curveStart = curve.GetEndPoint(0);
            XYZ curveEnd = curve.GetEndPoint(1);

            ConnectorManager cm = GetConnectorManager(elem);
            if (cm == null)
            {
                _log.AppendLine("✗ 无法获取连接器管理器");
                return false;
            }

            Connector connAtStart = null, connAtEnd = null;
            foreach (Connector c in cm.Connectors)
            {
                if (c.Origin.DistanceTo(curveStart) < c.Origin.DistanceTo(curveEnd))
                    connAtStart = c;
                else
                    connAtEnd = c;
            }

            Connector liftedConn = geom.LiftRight ? connAtEnd : connAtStart;
            Connector fixedConn = geom.LiftRight ? connAtStart : connAtEnd;

            XYZ liftedEndPt = liftedConn?.Origin ?? (geom.LiftRight ? curveEnd : curveStart);
            XYZ fixedEndPt = fixedConn?.Origin ?? (geom.LiftRight ? curveStart : curveEnd);

            List<ElementId> connectedToLiftedEnd = new List<ElementId>();
            if (liftedConn != null && liftedConn.IsConnected)
            {
                foreach (Connector c in liftedConn.AllRefs)
                {
                    if (c.Owner != null && c.Owner.Id != elemId)
                        connectedToLiftedEnd.Add(c.Owner.Id);
                }
            }

            Connector extAtFixed = fixedConn?.AllRefs
                .Cast<Connector>().FirstOrDefault(c => c.Owner is FamilyInstance);

            bool ok;
            ElementId liftedEndElemId = ElementId.InvalidElementId;

            if (elemType == MEPElementType.CableTray)
            {
                ok = ProcessCableTray(doc, elem as CableTray, geom,
                    typeId, levelId, width, height,
                    curveStart, curveEnd, extAtFixed, fixedEndPt,
                    out liftedEndElemId);
            }
            else
            {
                ok = ProcessPipeDuct(doc, elemId, elemType, geom,
                    typeId, levelId, systemTypeId,
                    diameter, width, height,
                    curveStart, curveEnd, extAtFixed, fixedEndPt,
                    out liftedEndElemId);
            }

            if (!ok) return false;

            if (connectedToLiftedEnd.Count > 0)
            {
                SyncMoveConnectedElements(doc, connectedToLiftedEnd, liftedEndPt, h_offset_ft);
                if (liftedEndElemId != ElementId.InvalidElementId)
                {
                    XYZ newLiftPt = geom.LiftRight
                        ? curveEnd + XYZ.BasisZ * h_offset_ft
                        : curveStart + XYZ.BasisZ * h_offset_ft;
                    ReconnectAtLiftedEnd(doc, liftedEndElemId, newLiftPt, connectedToLiftedEnd);
                }
            }

            _log.AppendLine($"✓ 元素 {elemId.IntegerValue} 抬升/下降处理成功");
            return true;
        }

        private bool ProcessPipeDuct(Document doc, ElementId elemId, MEPElementType elemType,
            UpDownGeometry geom, ElementId typeId, ElementId levelId, ElementId systemTypeId,
            double diameter, double width, double height,
            XYZ curveStart, XYZ curveEnd,
            Connector extAtFixed, XYZ fixedEndPt,
            out ElementId liftedEndElementId)
        {
            liftedEndElementId = ElementId.InvalidElementId;

            ElementId id1;
            if (elemType == MEPElementType.Pipe)
            {
                id1 = PlumbingUtils.BreakCurve(doc, elemId, geom.P0);
            }
            else
            {
                id1 = MechanicalUtils.BreakCurve(doc, elemId, geom.P0);
            }

            if (id1 == ElementId.InvalidElementId)
                throw new InvalidOperationException($"BreakCurve 返回无效 ID (elemId={elemId.IntegerValue})");

            doc.Regenerate();

            Element e1 = doc.GetElement(id1);
            Element e2 = doc.GetElement(elemId);
            LocationCurve lc1 = e1?.Location as LocationCurve;
            LocationCurve lc2 = e2?.Location as LocationCurve;

            if (lc1 == null || lc2 == null)
                throw new InvalidOperationException("无法获取打断后的定位曲线");

            double d1Start = Math.Min(lc1.Curve.GetEndPoint(0).DistanceTo(curveStart), lc1.Curve.GetEndPoint(1).DistanceTo(curveStart));
            double d2Start = Math.Min(lc2.Curve.GetEndPoint(0).DistanceTo(curveStart), lc2.Curve.GetEndPoint(1).DistanceTo(curveStart));

            ElementId keepSegId, delSegId;
            if (d1Start < d2Start) { keepSegId = id1; delSegId = elemId; }
            else { keepSegId = elemId; delSegId = id1; }

            if (!geom.LiftRight)
            {
                ElementId tmp = keepSegId; keepSegId = delSegId; delSegId = tmp;
            }

            _log.AppendLine($"打断完成 → 保留段 ID={keepSegId.IntegerValue}, 待重建段 ID={delSegId.IntegerValue}");

            doc.Delete(delSegId);
            doc.Regenerate();

            XYZ newHorizStart, newHorizEnd;
            if (geom.LiftRight)
            {
                newHorizStart = geom.P1;
                newHorizEnd = curveEnd + XYZ.BasisZ * geom.Offset;
            }
            else
            {
                newHorizStart = curveStart + XYZ.BasisZ * geom.Offset;
                newHorizEnd = geom.P1;
            }

            Element ramp = CreateMEPCurve(doc, elemType, typeId, levelId, systemTypeId,
                diameter, width, height, geom.P0, geom.P1);
            if (ramp == null) throw new InvalidOperationException("斜段创建失败");

            Element newHoriz = null;
            if (!newHorizStart.IsAlmostEqualTo(newHorizEnd, 1e-6))
            {
                newHoriz = CreateMEPCurve(doc, elemType, typeId, levelId, systemTypeId,
                    diameter, width, height, newHorizStart, newHorizEnd);
                if (newHoriz == null) throw new InvalidOperationException("新高度段创建失败");
            }

            doc.Regenerate();

            if (geom.LiftRight)
            {
                TryCreateElbow(doc, keepSegId, ramp.Id, geom.P0, "弯头1（原段-斜段）");
                if (newHoriz != null)
                    TryCreateElbow(doc, ramp.Id, newHoriz.Id, geom.P1, "弯头2（斜段-新高度段）");
            }
            else
            {
                if (newHoriz != null)
                    TryCreateElbow(doc, newHoriz.Id, ramp.Id, geom.P1, "弯头1（新高度段-斜段）");
                TryCreateElbow(doc, ramp.Id, keepSegId, geom.P0, "弯头2（斜段-原段）");
            }

            liftedEndElementId = newHoriz != null ? newHoriz.Id : ramp.Id;
            ReconnectExternal(doc, keepSegId, fixedEndPt, extAtFixed, "固定侧");

            return true;
        }

        // =====================================================================
        // 以下辅助方法直接使用你最初提供的版本（确保不改变任何底层处理逻辑）
        // =====================================================================
        private Element CreateMEPCurve(Document doc, MEPElementType t,
            ElementId typeId, ElementId levelId, ElementId systemTypeId,
            double dia, double w, double h, XYZ s, XYZ e)
        {
            if (s.IsAlmostEqualTo(e, 1e-6)) return null;
            Element el = null;
            switch (t)
            {
                case MEPElementType.Pipe:
                    el = Pipe.Create(doc, systemTypeId, typeId, levelId, s, e);
                    if (el != null && dia > 0)
                    {
                        Parameter pd = el.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                        if (pd != null) pd.Set(dia);
                    }
                    break;
                case MEPElementType.Duct:
                    el = Duct.Create(doc, systemTypeId, typeId, levelId, s, e);
                    if (el is Duct nd)
                    {
                        if (dia > 0) { Parameter pd = nd.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM); if (pd != null) pd.Set(dia); }
                        if (w > 0) { Parameter pw = nd.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM); if (pw != null) pw.Set(w); }
                        if (h > 0) { Parameter ph = nd.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM); if (ph != null) ph.Set(h); }
                    }
                    break;
            }
            return el;
        }

        private void ReconnectAtLiftedEnd(Document doc, ElementId liftedElemId, XYZ expectedPt, List<ElementId> movedIds)
        {
            if (liftedElemId == ElementId.InvalidElementId) return;
            doc.Regenerate();
            Element liftedElem = doc.GetElement(liftedElemId);
            if (liftedElem == null) return;
            ConnectorManager liftedCm = GetConnectorManager(liftedElem);
            if (liftedCm == null) return;

            double tol = UnitUtils.ConvertToInternalUnits(30.0, UnitTypeId.Millimeters);
            Connector liftedConn = FindNearestConnector(liftedCm, expectedPt, out double d1);

            if (liftedConn == null || d1 > tol)
            {
                if (liftedElem.Location is LocationCurve lc)
                {
                    XYZ p0 = lc.Curve.GetEndPoint(0);
                    XYZ p1 = lc.Curve.GetEndPoint(1);
                    XYZ fallback = p0.DistanceTo(expectedPt) <= p1.DistanceTo(expectedPt) ? p0 : p1;
                    Connector alt = FindNearestConnector(liftedCm, fallback, out double d2);
                    if (alt != null && d2 <= tol) liftedConn = alt;
                }
            }
            if (liftedConn == null || liftedConn.IsConnected) return;

            foreach (ElementId eid in movedIds)
            {
                Element e = doc.GetElement(eid);
                if (e == null) continue;
                ConnectorManager cm = GetConnectorManager(e);
                if (cm == null) continue;
                Connector best = FindNearestConnector(cm, liftedConn.Origin, out double bd);
                if (best == null || bd > tol || best.IsConnected) continue;
                try { liftedConn.ConnectTo(best); return; } catch { }
            }
        }

        private Connector FindNearestConnector(ConnectorManager cm, XYZ target, out double distance)
        {
            Connector best = null;
            distance = double.MaxValue;
            if (cm == null) return null;
            foreach (Connector c in cm.Connectors)
            {
                double d = c.Origin.DistanceTo(target);
                if (d < distance) { distance = d; best = c; }
            }
            return best;
        }

        private void SyncMoveConnectedElements(Document doc, List<ElementId> connectedIds, XYZ originalLiftPt, double h_offset_ft)
        {
            XYZ offsetVec = XYZ.BasisZ * h_offset_ft;
            foreach (ElementId ceId in connectedIds)
            {
                Element ce = doc.GetElement(ceId);
                if (ce == null) continue;
                try
                {
                    if (ce is MEPCurve mepCurve)
                    {
                        LocationCurve lcc = mepCurve.Location as LocationCurve;
                        if (lcc == null || !(lcc.Curve is Line)) { ElementTransformUtils.MoveElement(doc, ceId, offsetVec); continue; }
                        XYZ p0 = lcc.Curve.GetEndPoint(0), p1 = lcc.Curve.GetEndPoint(1);
                        XYZ newP0 = p0, newP1 = p1;
                        if (p0.DistanceTo(originalLiftPt) <= p1.DistanceTo(originalLiftPt)) newP0 = p0 + offsetVec;
                        else newP1 = p1 + offsetVec;
                        if (newP0.DistanceTo(newP1) < 1e-6) continue;
                        try { lcc.Curve = Line.CreateBound(newP0, newP1); }
                        catch { ElementTransformUtils.MoveElement(doc, ceId, offsetVec); }
                    }
                    else if (ce is FamilyInstance) ElementTransformUtils.MoveElement(doc, ceId, offsetVec);
                }
                catch { }
            }
            doc.Regenerate();
        }

        private bool ProcessCableTray(Document doc, CableTray tray, UpDownGeometry geom,
            ElementId typeId, ElementId levelId, double width, double height,
            XYZ curveStart, XYZ curveEnd, Connector extAtFixed, XYZ fixedEndPt,
            out ElementId liftedEndElementId)
        {
            liftedEndElementId = ElementId.InvalidElementId;
            if (tray == null) return false;
            LocationCurve lc = tray.Location as LocationCurve;
            if (lc == null) return false;

            XYZ trayStart = lc.Curve.GetEndPoint(0);
            XYZ trayEnd = lc.Curve.GetEndPoint(1);

            doc.Delete(tray.Id);
            doc.Regenerate();

            List<Element> segs = new List<Element>();
            const double endpointTol = 1e-4;
            XYZ newEnd = trayEnd + XYZ.BasisZ * geom.Offset;
            XYZ newStart = trayStart + XYZ.BasisZ * geom.Offset;

            if (geom.LiftRight)
            {
                if (!trayStart.IsAlmostEqualTo(geom.P0, endpointTol)) { Element s1 = CreateCableTraySegment(doc, typeId, levelId, width, height, trayStart, geom.P0); if (s1 != null) segs.Add(s1); }
                Element s2 = CreateCableTraySegment(doc, typeId, levelId, width, height, geom.P0, geom.P1); if (s2 != null) segs.Add(s2);
                if (!geom.P1.IsAlmostEqualTo(newEnd, endpointTol)) { Element s3 = CreateCableTraySegment(doc, typeId, levelId, width, height, geom.P1, newEnd); if (s3 != null) segs.Add(s3); }
            }
            else
            {
                if (!newStart.IsAlmostEqualTo(geom.P1, endpointTol)) { Element s1 = CreateCableTraySegment(doc, typeId, levelId, width, height, newStart, geom.P1); if (s1 != null) segs.Add(s1); }
                Element s2 = CreateCableTraySegment(doc, typeId, levelId, width, height, geom.P1, geom.P0); if (s2 != null) segs.Add(s2);
                if (!geom.P0.IsAlmostEqualTo(trayEnd, endpointTol)) { Element s3 = CreateCableTraySegment(doc, typeId, levelId, width, height, geom.P0, trayEnd); if (s3 != null) segs.Add(s3); }
            }

            doc.Regenerate();

            for (int i = 0; i < segs.Count - 1; i++)
            {
                XYZ joint = FindCommonEndPoint(segs[i], segs[i + 1]);
                if (joint != null) TryCreateElbow(doc, segs[i].Id, segs[i + 1].Id, joint, $"CT弯头{i + 1}");
            }

            if (segs.Count > 0)
            {
                liftedEndElementId = geom.LiftRight ? segs[segs.Count - 1].Id : segs[0].Id;
                if (geom.LiftRight) ReconnectExternal(doc, segs[0].Id, curveStart, extAtFixed, "CT固定侧");
                else ReconnectExternal(doc, segs[segs.Count - 1].Id, curveEnd, extAtFixed, "CT固定侧");
            }

            return true;
        }

        private Element CreateCableTraySegment(Document doc, ElementId typeId, ElementId levelId, double width, double height, XYZ s, XYZ e)
        {
            if (s.IsAlmostEqualTo(e, 1e-6)) return null;
            CableTray ct = CableTray.Create(doc, typeId, s, e, levelId);
            if (ct == null) return null;
            if (width > 0) { Parameter pw = ct.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM); if (pw != null) pw.Set(width); }
            if (height > 0) { Parameter ph = ct.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM); if (ph != null) ph.Set(height); }
            return ct;
        }

        private XYZ FindCommonEndPoint(Element a, Element b)
        {
            LocationCurve lca = a.Location as LocationCurve;
            LocationCurve lcb = b.Location as LocationCurve;
            if (lca == null || lcb == null) return null;
            XYZ[] aEnds = { lca.Curve.GetEndPoint(0), lca.Curve.GetEndPoint(1) };
            XYZ[] bEnds = { lcb.Curve.GetEndPoint(0), lcb.Curve.GetEndPoint(1) };
            const double tol = 1e-3;
            foreach (XYZ pa in aEnds) foreach (XYZ pb in bEnds) if (pa.IsAlmostEqualTo(pb, tol)) return pa;
            return null;
        }

        private bool TryCreateElbow(Document doc, ElementId idA, ElementId idB, XYZ atPt, string tag)
        {
            Element elA = doc.GetElement(idA);
            Element elB = doc.GetElement(idB);
            if (elA == null || elB == null) return false;

            Connector ca = GetConnectorAtPoint(elA, atPt);
            Connector cb = GetConnectorAtPoint(elB, atPt);

            if (ca == null || cb == null)
            {
                Connector pa, pb; double pairDist;
                FindClosestConnectorPair(elA, elB, out pa, out pb, out pairDist);
                if (pa == null || pb == null) return false;
                ca = pa; cb = pb;
            }

            double dist = ca.Origin.DistanceTo(cb.Origin);
            double hardCap = UnitUtils.ConvertToInternalUnits(1000.0, UnitTypeId.Millimeters);
            double maxAllowed = hardCap;
            double lenA = GetLocationCurveLength(elA);
            double lenB = GetLocationCurveLength(elB);
            if (lenA > 0 && lenB > 0) maxAllowed = Math.Min(Math.Min(lenA, lenB), hardCap);

            if (dist > maxAllowed) return false;

            double elbowDeg = MEPUpDownCalculator.CalcElbowAngleDeg(ca, cb);
            if (!MEPUpDownCalculator.IsElbowAngleValid(elbowDeg)) return false;

            try
            {
                FamilyInstance fit = doc.Create.NewElbowFitting(ca, cb);
                if (fit == null) return false;
                Parameter angleParam = fit.LookupParameter("Angle") ?? fit.LookupParameter("角度") ?? fit.LookupParameter("Bend Angle") ?? fit.LookupParameter("弯头角度");
                if (angleParam != null && !angleParam.IsReadOnly && angleParam.StorageType == StorageType.Double)
                {
                    try { angleParam.Set(45.0); } catch { angleParam.Set(45.0 * Math.PI / 180.0); }
                }
                return true;
            }
            catch { return false; }
        }

        private double GetLocationCurveLength(Element elem)
        {
            if (elem?.Location is LocationCurve lc && lc.Curve != null) return lc.Curve.Length;
            return 0;
        }

        private void FindClosestConnectorPair(Element a, Element b, out Connector ca, out Connector cb, out double distance)
        {
            ca = null; cb = null; distance = double.MaxValue;
            ConnectorManager cma = GetConnectorManager(a);
            ConnectorManager cmb = GetConnectorManager(b);
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

        private void ReconnectExternal(Document doc, ElementId segmentId, XYZ targetPt, Connector externalConn, string tag)
        {
            if (externalConn == null) return;
            Element elem = doc.GetElement(segmentId);
            Connector c = GetConnectorAtPoint(elem, targetPt);
            if (c == null || c.IsConnected) return;
            try { c.ConnectTo(externalConn); } catch { }
        }

        private ElementId GetLevelId(Element elem, MEPElementType elemType)
        {
            ElementId levelId = elem.LevelId;
            if (levelId != ElementId.InvalidElementId) return levelId;
            if (elemType == MEPElementType.Pipe || elemType == MEPElementType.Duct)
            {
                Parameter p = elem.get_Parameter(BuiltInParameter.RBS_START_LEVEL_PARAM);
                if (p != null) levelId = p.AsElementId();
            }
            else if (elemType == MEPElementType.CableTray && elem is CableTray tray)
            {
                if (tray.ReferenceLevel != null) levelId = tray.ReferenceLevel.Id;
                else
                {
                    Parameter p = tray.get_Parameter(BuiltInParameter.RBS_START_LEVEL_PARAM);
                    if (p != null) levelId = p.AsElementId();
                }
            }
            return levelId;
        }

        private ConnectorManager GetConnectorManager(Element elem)
        {
            if (elem is MEPCurve mep) return mep.ConnectorManager;
            if (elem is FamilyInstance fi && fi.MEPModel != null) return fi.MEPModel.ConnectorManager;
            return null;
        }

        private Connector GetConnectorAtPoint(Element elem, XYZ targetPt)
        {
            ConnectorManager cm = GetConnectorManager(elem);
            if (cm == null) return null;

            double size = 0;
            if (elem is MEPCurve mep)
            {
                Parameter pd = mep.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM)
                            ?? mep.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)
                            ?? mep.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
                if (pd != null && pd.StorageType == StorageType.Double) size = pd.AsDouble();
            }

            double tol = Math.Max(
                UnitUtils.ConvertToInternalUnits(1.5, UnitTypeId.Millimeters), 
                Math.Min(UnitUtils.ConvertToInternalUnits(20.0, UnitTypeId.Millimeters), size * 0.1));

            Connector best = null;
            double bestDist = double.MaxValue;
            foreach (Connector c in cm.Connectors)
            {
                double d = c.Origin.DistanceTo(targetPt);
                if (d < bestDist) { bestDist = d; best = c; }
            }
            return (best != null && bestDist < tol) ? best : null;
        }
    }
}