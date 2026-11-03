using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using MEPTools.Shared; // 【修复】添加共享命名空间的引用

namespace MEPTools.MEPPlanarLateralBend
{
    public class MEPLateralBendService
    {
        private readonly MEPLogger _log;

        public MEPLateralBendService(MEPLogger log)
        {
            _log = log ?? MEPLogger.Instance;
        }

        /// <summary>
        /// 处理单个元素（多根管共用全局 frame，保证斜段平行等距）。
        /// ⚠ 调用方必须已开启事务
        /// </summary>
        public bool ProcessSingleElement(Document doc, ElementId elemId, GlobalFrame frame)
        {
            Element elem = doc.GetElement(elemId);
            if (elem == null || !elem.IsValidObject)
            {
                _log.AppendLine("✗ 元素无效或已删除");
                return false;
            }

            MEPElementType elemType = MEPLateralBendCalculator.GetElementType(elem);
            if (elemType == MEPElementType.Unknown)
            {
                _log.AppendLine("✗ 元素类型不支持平面拐弯");
                return false;
            }

            LocationCurve locCurve = elem.Location as LocationCurve;
            if (locCurve == null)
            {
                _log.AppendLine("✗ 无定位曲线");
                return false;
            }
            Curve curve = locCurve.Curve;

            // ---------- 每根管的几何 ----------
            string geomErr;
            LateralBendGeometry geom = MEPLateralBendCalculator.ComputePipeGeometry(
                curve, frame, out geomErr);
            if (!geom.IsValid)
            {
                _log.AppendLine($"✗ {geomErr}");
                return false;
            }

            double angleDeg = frame.AngleRad * 180.0 / Math.PI;

            _log.AppendLine($"打断点 P0 = {MEPLateralBendCalculator.Fmt(geom.P0)}");
            _log.AppendLine($"斜段终点 Q = {MEPLateralBendCalculator.Fmt(geom.Q)}");
            _log.AppendLine($"平移侧端点 = {MEPLateralBendCalculator.Fmt(geom.LiftEndPt)}");
            _log.AppendLine($"固定侧端点 = {MEPLateralBendCalculator.Fmt(geom.FixedEndPt)}");
            _log.AppendLine($"横向偏移: {frame.AbsOffset * MEPLateralBendCalculator.FtToMm:F1} mm, " +
                            $"θ = {angleDeg:F1}°, ProjLen = {frame.ProjLen * MEPLateralBendCalculator.FtToMm:F1} mm");
            _log.AppendLine($"  平移侧可用空间: {geom.AvailableSpace * MEPLateralBendCalculator.FtToMm:F1} mm");

            // ---------- 读取原始参数 ----------
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

            if (elemType != MEPElementType.CableTray && systemTypeId == ElementId.InvalidElementId)
            {
                _log.AppendLine("✗ 系统类型 ID 无效");
                return false;
            }

            XYZ curveStart = curve.GetEndPoint(0);
            XYZ curveEnd = curve.GetEndPoint(1);

            // ---------- 连接器定位 ----------
            ConnectorManager cm = GetConnectorManager(elem);
            if (cm == null)
            {
                _log.AppendLine("✗ 无法获取连接器管理器");
                return false;
            }

            Connector connAtLift = null, connAtFix = null;
            {
                double minD = double.MaxValue;
                foreach (Connector c in cm.Connectors)
                {
                    double d = c.Origin.DistanceTo(geom.LiftEndPt);
                    if (d < minD) { minD = d; connAtLift = c; }
                }
                minD = double.MaxValue;
                foreach (Connector c in cm.Connectors)
                {
                    if (c == connAtLift) continue;
                    double d = c.Origin.DistanceTo(geom.FixedEndPt);
                    if (d < minD) { minD = d; connAtFix = c; }
                }

                _log.AppendLine($"  [方向] AlongSign={(frame.AlongSign > 0 ? "下游(+)" : "上游(-)")}, " +
                                $"LatSign={(frame.LatSign > 0 ? "左拐(+)" : "右拐(-)")}");
                _log.AppendLine($"  [端点] 平移侧 LiftEndPt = {MEPLateralBendCalculator.Fmt(geom.LiftEndPt)}");
                _log.AppendLine($"  [端点] 固定侧 FixedEndPt = {MEPLateralBendCalculator.Fmt(geom.FixedEndPt)}");
                _log.AppendLine($"  [连接器] connAtLift  origin = " +
                                (connAtLift != null ? MEPLateralBendCalculator.Fmt(connAtLift.Origin) : "null"));
                _log.AppendLine($"  [连接器] connAtFix  origin = " +
                                (connAtFix != null ? MEPLateralBendCalculator.Fmt(connAtFix.Origin) : "null"));
            }

            List<ElementId> connectedToLiftedEnd = new List<ElementId>();
            if (connAtLift != null && connAtLift.IsConnected)
            {
                foreach (Connector c in connAtLift.AllRefs)
                {
                    if (c.Owner != null && c.Owner.Id != elemId)
                        connectedToLiftedEnd.Add(c.Owner.Id);
                }
            }

            if (connectedToLiftedEnd.Count > 0)
            {
                _log.AppendLine($"  ℹ 平移侧端点有 {connectedToLiftedEnd.Count} 个外部连接，将同步移动");
            }

            Connector extAtFixed = connAtFix?.AllRefs
                .Cast<Connector>().FirstOrDefault(c => c.Owner is FamilyInstance);

            // ============================================================
            // 修改
            // ============================================================
            bool ok;
            ElementId liftedEndElemId = ElementId.InvalidElementId;

            if (elemType == MEPElementType.CableTray)
            {
                ok = ProcessCableTray(doc, elem as CableTray, geom, angleDeg,
                    typeId, levelId, width, height,
                    curveStart, curveEnd, extAtFixed,
                    out liftedEndElemId);
            }
            else
            {
                ok = ProcessPipeDuct(doc, elemId, elemType, geom, angleDeg,
                    typeId, levelId, systemTypeId,
                    diameter, width, height,
                    curveStart, curveEnd, extAtFixed,
                    out liftedEndElemId);
            }

            if (!ok) return false;

            // ---------- 同步移动被连接元素 ----------
            if (connectedToLiftedEnd.Count > 0)
            {
                SyncMoveConnectedElements(doc, connectedToLiftedEnd,
                    geom.LiftEndPt, geom.LateralVector);

                if (liftedEndElemId != ElementId.InvalidElementId)
                {
                    XYZ newLiftPt = geom.LiftEndPt + geom.LateralVector;
                    ReconnectAtLiftedEnd(doc, liftedEndElemId, newLiftPt, connectedToLiftedEnd);
                }
            }

            _log.AppendLine($"✓ 元素 {elemId.IntegerValue} 平面拐弯处理成功");
            return true;
        }

        // =====================================================================
        // 平移侧显式重连
        // =====================================================================
        private void ReconnectAtLiftedEnd(Document doc, ElementId liftedElemId,
            XYZ expectedPt, List<ElementId> movedIds)
        {
            if (liftedElemId == ElementId.InvalidElementId) return;

            doc.Regenerate();

            Element liftedElem = doc.GetElement(liftedElemId);
            if (liftedElem == null)
            {
                _log.AppendLine($"⚠ 平移侧重连：新元素 {liftedElemId.IntegerValue} 不存在");
                return;
            }

            ConnectorManager liftedCm = GetConnectorManager(liftedElem);
            if (liftedCm == null)
            {
                _log.AppendLine($"⚠ 平移侧重连：无法获取新元素 {liftedElemId.IntegerValue} 的连接器管理器");
                return;
            }

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
                    if (alt != null && d2 <= tol)
                    {
                        _log.AppendLine(
                            $"  ℹ 平移侧按 LocationCurve 端点回退匹配 (偏差 {d2 * MEPLateralBendCalculator.FtToMm:F1}mm)");
                        liftedConn = alt;
                    }
                }
            }

            if (liftedConn == null)
            {
                _log.AppendLine(
                    $"⚠ 平移侧重连：新元素 {liftedElemId.IntegerValue} 上未找到 {MEPLateralBendCalculator.Fmt(expectedPt)} 附近的连接器");
                return;
            }

            if (liftedConn.IsConnected)
            {
                _log.AppendLine("  ℹ 平移侧连接器已由 Revit 自动连接，跳过显式重连");
                return;
            }

            foreach (ElementId eid in movedIds)
            {
                Element e = doc.GetElement(eid);
                if (e == null) continue;

                ConnectorManager cm = GetConnectorManager(e);
                if (cm == null) continue;

                Connector best = FindNearestConnector(cm, liftedConn.Origin, out double bd);
                if (best == null || bd > tol) continue;
                if (best.IsConnected) continue;

                try
                {
                    liftedConn.ConnectTo(best);
                    _log.AppendLine($"✓ 平移侧重连成功：新元素 {liftedElemId.IntegerValue} ↔ {eid.IntegerValue}");
                    return;
                }
                catch (Exception ex)
                {
                    _log.AppendLine($"⚠ 平移侧 ConnectTo 失败（{eid.IntegerValue}）：{ex.Message}");
                }
            }

            _log.AppendLine(
                $"⚠ 平移侧未找到可连接的对象（预期位置 {MEPLateralBendCalculator.Fmt(expectedPt)}），连接可能断开");
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

        // =====================================================================
        // 同步移动被连接元素
        // =====================================================================
        private void SyncMoveConnectedElements(Document doc, List<ElementId> connectedIds,
            XYZ originalLiftPt, XYZ offsetVec)
        {
            foreach (ElementId ceId in connectedIds)
            {
                Element ce = doc.GetElement(ceId);
                if (ce == null)
                {
                    _log.AppendLine($"  ⚠ 被连接元素 {ceId.IntegerValue} 已被删除，跳过");
                    continue;
                }

                try
                {
                    if (ce is MEPCurve mepCurve)
                    {
                        LocationCurve lcc = mepCurve.Location as LocationCurve;
                        if (lcc == null || !(lcc.Curve is Line))
                        {
                            _log.AppendLine($"  ⚠ 被连接管线 {ceId.IntegerValue} 非直线，整体移动");
                            ElementTransformUtils.MoveElement(doc, ceId, offsetVec);
                            continue;
                        }

                        XYZ p0 = lcc.Curve.GetEndPoint(0);
                        XYZ p1 = lcc.Curve.GetEndPoint(1);
                        double d0 = p0.DistanceTo(originalLiftPt);
                        double d1 = p1.DistanceTo(originalLiftPt);

                        XYZ newP0 = p0, newP1 = p1;
                        if (d0 <= d1) newP0 = p0 + offsetVec;
                        else newP1 = p1 + offsetVec;

                        if (newP0.DistanceTo(newP1) < 1e-6)
                        {
                            _log.AppendLine($"  ⚠ 被连接管线 {ceId.IntegerValue} 移动后长度为零，跳过");
                            continue;
                        }

                        try
                        {
                            lcc.Curve = Line.CreateBound(newP0, newP1);
                            _log.AppendLine($"  ✓ 已移动被连接管线 {ceId.IntegerValue} 的端点（保持连接）");
                        }
                        catch (Exception lex)
                        {
                            _log.AppendLine($"  ⚠ 修改管线 {ceId.IntegerValue} 定位曲线失败：{lex.Message}，改为整体移动");
                            ElementTransformUtils.MoveElement(doc, ceId, offsetVec);
                        }
                    }
                    else if (ce is FamilyInstance fi)
                    {
                        ElementTransformUtils.MoveElement(doc, ceId, offsetVec);
                        _log.AppendLine($"  ✓ 已整体移动被连接设备 {ceId.IntegerValue}");
                    }
                    else
                    {
                        _log.AppendLine($"  ⚠ 被连接元素 {ceId.IntegerValue} ({ce.GetType().Name}) 不支持同步移动，连接可能断开");
                    }
                }
                catch (Exception ex)
                {
                    _log.AppendLine($"  ⚠ 移动被连接元素 {ceId.IntegerValue} 失败：{ex.Message}");
                }
            }

            doc.Regenerate();
        }

        // =====================================================================
        // Pipe / Duct 处理
        // =====================================================================
        private bool ProcessPipeDuct(Document doc, ElementId elemId, MEPElementType elemType,
            LateralBendGeometry geom, double angleDeg,
            ElementId typeId, ElementId levelId, ElementId systemTypeId,
            double diameter, double width, double height,
            XYZ curveStart, XYZ curveEnd,
            Connector extAtFixed,
            out ElementId liftedEndElementId)
        {
            liftedEndElementId = ElementId.InvalidElementId;

            ElementId id1;
            if (elemType == MEPElementType.Pipe)
                id1 = PlumbingUtils.BreakCurve(doc, elemId, geom.P0);
            else
                id1 = MechanicalUtils.BreakCurve(doc, elemId, geom.P0);

            if (id1 == ElementId.InvalidElementId)
                throw new InvalidOperationException($"BreakCurve 返回无效 ID (elemId={elemId.IntegerValue})");

            doc.Regenerate();

            Element e1 = doc.GetElement(id1);
            Element e2 = doc.GetElement(elemId);
            LocationCurve lc1 = e1?.Location as LocationCurve;
            LocationCurve lc2 = e2?.Location as LocationCurve;

            if (lc1 == null || lc2 == null)
                throw new InvalidOperationException("无法获取打断后的定位曲线");

            double dist1 = Math.Min(
                lc1.Curve.GetEndPoint(0).DistanceTo(geom.LiftEndPt),
                lc1.Curve.GetEndPoint(1).DistanceTo(geom.LiftEndPt));
            double dist2 = Math.Min(
                lc2.Curve.GetEndPoint(0).DistanceTo(geom.LiftEndPt),
                lc2.Curve.GetEndPoint(1).DistanceTo(geom.LiftEndPt));

            // 含平移侧端点的段 = 待重建段
            ElementId delSegId = (dist1 < dist2) ? id1 : elemId;
            ElementId keepSegId = (delSegId == id1) ? elemId : id1;

            _log.AppendLine($"打断完成 → 保留段(含固定端) ID={keepSegId.IntegerValue}, " +
                            $"待重建段(含平移端) ID={delSegId.IntegerValue}");

            doc.Delete(delSegId);
            doc.Regenerate();

            // 新沿管段：Q → (LiftEndPt + LateralVec)
            XYZ newHorizStart = geom.Q;
            XYZ newHorizEnd = geom.LiftEndPt + geom.LateralVector;

            // 斜段：P0 → Q
            Element ramp = CreateMEPCurve(doc, elemType, typeId, levelId, systemTypeId,
                diameter, width, height, geom.P0, geom.Q);
            if (ramp == null) throw new InvalidOperationException("斜段创建失败");

            Element newHoriz = null;
            if (!newHorizStart.IsAlmostEqualTo(newHorizEnd, 1e-6))
            {
                newHoriz = CreateMEPCurve(doc, elemType, typeId, levelId, systemTypeId,
                    diameter, width, height, newHorizStart, newHorizEnd);
                if (newHoriz == null) throw new InvalidOperationException("新沿管段创建失败");
            }

            doc.Regenerate();

            _log.AppendLine(newHoriz != null
                ? $"重建 → 斜段={ramp.Id.IntegerValue}, 新沿管段={newHoriz.Id.IntegerValue}"
                : $"重建 → 斜段={ramp.Id.IntegerValue}（新沿管段长度为零，已省略）");

            TryCreateElbow(doc, keepSegId, ramp.Id, geom.P0, "弯头1（原段-斜段）", angleDeg);
            if (newHoriz != null)
                TryCreateElbow(doc, ramp.Id, newHoriz.Id, geom.Q, "弯头2（斜段-新沿管段）", angleDeg);

            liftedEndElementId = newHoriz != null ? newHoriz.Id : ramp.Id;

            ReconnectExternal(doc, keepSegId, geom.FixedEndPt, extAtFixed, "固定侧");

            return true;
        }

        // =====================================================================
        // CableTray 处理
        // =====================================================================
        private bool ProcessCableTray(Document doc, CableTray tray, LateralBendGeometry geom,
            double angleDeg,
            ElementId typeId, ElementId levelId, double width, double height,
            XYZ curveStart, XYZ curveEnd,
            Connector extAtFixed,
            out ElementId liftedEndElementId)
        {
            liftedEndElementId = ElementId.InvalidElementId;

            if (tray == null) { _log.AppendLine("✗ CableTray 为空"); return false; }

            LocationCurve lc = tray.Location as LocationCurve;
            if (lc == null) { _log.AppendLine("✗ CT 无定位曲线"); return false; }

            XYZ trayStart = lc.Curve.GetEndPoint(0);
            XYZ trayEnd = lc.Curve.GetEndPoint(1);
            ElementId trayId = tray.Id;

            _log.AppendLine($"CT 原始段: {MEPLateralBendCalculator.Fmt(trayStart)} → {MEPLateralBendCalculator.Fmt(trayEnd)}");

            doc.Delete(trayId);
            doc.Regenerate();

            List<Element> segs = new List<Element>();
            const double tol = 1e-4;

            XYZ liftEndNew = geom.LiftEndPt + geom.LateralVector;

            // 段1：FixedEndPt → P0（原路径段，保留位置）
            if (!geom.FixedEndPt.IsAlmostEqualTo(geom.P0, tol))
            {
                Element s1 = CreateCableTraySegment(doc, typeId, levelId, width, height,
                    geom.FixedEndPt, geom.P0);
                if (s1 == null) { _log.AppendLine("✗ CT 段1 创建失败"); return false; }
                segs.Add(s1);
            }
            else _log.AppendLine("  CT 段1 跳过（P0 与固定侧端点重合）");

            // 斜段：P0 → Q
            Element s2 = CreateCableTraySegment(doc, typeId, levelId, width, height, geom.P0, geom.Q);
            if (s2 == null) { _log.AppendLine("✗ CT 斜段创建失败"); return false; }
            segs.Add(s2);

            // 段3：Q → LiftEndPt + lateralVec（新沿管段）
            if (!geom.Q.IsAlmostEqualTo(liftEndNew, tol))
            {
                Element s3 = CreateCableTraySegment(doc, typeId, levelId, width, height,
                    geom.Q, liftEndNew);
                if (s3 == null) { _log.AppendLine("✗ CT 新沿管段创建失败"); return false; }
                segs.Add(s3);
            }
            else _log.AppendLine("  CT 新沿管段 跳过（Q 与平移侧端点重合）");

            doc.Regenerate();
            _log.AppendLine($"CT 重建 {segs.Count} 段");

            for (int i = 0; i < segs.Count - 1; i++)
            {
                XYZ joint = FindCommonEndPoint(segs[i], segs[i + 1]);
                if (joint == null)
                {
                    _log.AppendLine($"⚠ CT 段{i + 1}-{i + 2} 未找到公共端点，跳过弯头");
                    continue;
                }
                TryCreateElbow(doc, segs[i].Id, segs[i + 1].Id, joint, $"CT弯头{i + 1}", angleDeg);
            }

            if (segs.Count > 0)
                liftedEndElementId = segs[segs.Count - 1].Id;

            if (segs.Count > 0)
                ReconnectExternal(doc, segs[0].Id, geom.FixedEndPt, extAtFixed, "CT固定侧");

            return true;
        }

        private Element CreateCableTraySegment(Document doc, ElementId typeId, ElementId levelId,
            double width, double height, XYZ s, XYZ e)
        {
            if (s.IsAlmostEqualTo(e, 1e-6)) return null;

            CableTray ct = CableTray.Create(doc, typeId, s, e, levelId);
            if (ct == null) return null;

            if (width > 0)
            {
                Parameter pw = ct.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
                if (pw != null) pw.Set(width);
            }
            if (height > 0)
            {
                Parameter ph = ct.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
                if (ph != null) ph.Set(height);
            }
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
            foreach (XYZ pa in aEnds)
                foreach (XYZ pb in bEnds)
                    if (pa.IsAlmostEqualTo(pb, tol))
                        return pa;

            return null;
        }

        // =====================================================================
        // 公共辅助
        // =====================================================================

        private Element CreateMEPCurve(Document doc, MEPElementType t,
            ElementId typeId, ElementId levelId, ElementId systemTypeId,
            double dia, double w, double h, XYZ s, XYZ e)
        {
            if (s.IsAlmostEqualTo(e, 1e-6))
            {
                _log.AppendLine($"⚠ 跳过零长段 ({MEPLateralBendCalculator.Fmt(s)})");
                return null;
            }

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
                        if (dia > 0)
                        {
                            Parameter pd = nd.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                            if (pd != null) pd.Set(dia);
                        }
                        if (w > 0)
                        {
                            Parameter pw = nd.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                            if (pw != null) pw.Set(w);
                        }
                        if (h > 0)
                        {
                            Parameter ph = nd.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
                            if (ph != null) ph.Set(h);
                        }
                    }
                    break;
            }
            return el;
        }

        private bool TryCreateElbow(Document doc, ElementId idA, ElementId idB,
            XYZ atPt, string tag, double angleDeg)
        {
            Element elA = doc.GetElement(idA);
            Element elB = doc.GetElement(idB);
            if (elA == null || elB == null)
            {
                _log.AppendLine($"✗ {tag}: 关联元素为空");
                return false;
            }

            Connector ca = GetConnectorAtPoint(elA, atPt);
            Connector cb = GetConnectorAtPoint(elB, atPt);

            if (ca == null || cb == null)
            {
                _log.AppendLine(
                    $"  {tag}: 位置匹配失败 (A={(ca != null)}, B={(cb != null)})，启用最近连接器对匹配");

                Connector pa, pb;
                double pairDist;
                FindClosestConnectorPair(elA, elB, out pa, out pb, out pairDist);

                if (pa == null || pb == null)
                {
                    _log.AppendLine($"✗ {tag}: 两元素间找不到未连接的连接器对");
                    return false;
                }

                _log.AppendLine($"  {tag}: 最近连接器对距离 {pairDist * MEPLateralBendCalculator.FtToMm:F1}mm");
                ca = pa; cb = pb;
            }

            double dist = ca.Origin.DistanceTo(cb.Origin);
            double lenA = GetLocationCurveLength(elA);
            double lenB = GetLocationCurveLength(elB);
            double minSegLen = (lenA > 0 && lenB > 0) ? Math.Min(lenA, lenB) : 0;
            double hardCap = UnitUtils.ConvertToInternalUnits(1000.0, UnitTypeId.Millimeters);
            double maxAllowed = minSegLen > 0 ? Math.Min(minSegLen, hardCap) : hardCap;

            if (dist > maxAllowed)
            {
                _log.AppendLine(
                    $"✗ {tag}: 连接器对距离 {dist * MEPLateralBendCalculator.FtToMm:F1}mm " +
                    $"超过合理上限 {maxAllowed * MEPLateralBendCalculator.FtToMm:F1}mm，放弃");
                return false;
            }

            double outerDeg = ca.CoordinateSystem.BasisZ
                .AngleTo(cb.CoordinateSystem.BasisZ) * 180.0 / Math.PI;
            double elbowDeg = MEPLateralBendCalculator.CalcElbowAngleDeg(ca, cb);

            _log.AppendLine(
                $"  {tag}: 连接器间距 {dist * MEPLateralBendCalculator.FtToMm:F1}mm，" +
                $"外角={outerDeg:F2}° → 内角={elbowDeg:F2}°");

            if (!MEPLateralBendCalculator.IsElbowAngleValid(elbowDeg))
            {
                _log.AppendLine($"✗ {tag}: 弯头角度 {elbowDeg:F2}° 超出范围 (2~95°)");
                return false;
            }

            try
            {
                FamilyInstance fit = doc.Create.NewElbowFitting(ca, cb);
                if (fit == null)
                {
                    _log.AppendLine($"✗ {tag}: NewElbowFitting 返回 null（可能缺配件族）");
                    return false;
                }

                // 尝试写入目标角度参数（θ）
                Parameter angleParam = fit.LookupParameter("Angle")
                                    ?? fit.LookupParameter("角度")
                                    ?? fit.LookupParameter("Bend Angle")
                                    ?? fit.LookupParameter("弯头角度");

                if (angleParam != null && !angleParam.IsReadOnly
                    && angleParam.StorageType == StorageType.Double)
                {
                    bool setOk = false;
                    try
                    {
                        angleParam.Set(angleDeg);
                        setOk = true;
                        _log.AppendLine($"✓ {tag}: 创建成功，角度设为 {angleDeg:F1}°");
                    }
                    catch
                    {
                        try
                        {
                            angleParam.Set(angleDeg * Math.PI / 180.0);
                            setOk = true;
                            _log.AppendLine($"✓ {tag}: 创建成功，角度设为 {angleDeg:F1}°（弧度）");
                        }
                        catch { }
                    }
                    if (!setOk)
                        _log.AppendLine($"✓ {tag}: 创建成功（角度写入失败，使用默认）");
                }
                else
                {
                    _log.AppendLine($"✓ {tag}: 创建成功（无写入角度参数，使用默认）");
                }
                return true;
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException aex)
            {
                _log.AppendLine($"✗ {tag}: 参数异常 → {aex.Message}");
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException iex)
            {
                _log.AppendLine($"✗ {tag}: 操作无效 → {iex.Message}");
            }
            catch (Exception ex)
            {
                _log.AppendLine($"✗ {tag}: 异常 → {ex.Message}");
            }
            return false;
        }

        private double GetLocationCurveLength(Element elem)
        {
            if (elem?.Location is LocationCurve lc && lc.Curve != null)
                return lc.Curve.Length;
            return 0;
        }

        private void FindClosestConnectorPair(Element a, Element b,
            out Connector ca, out Connector cb, out double distance)
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

        private void ReconnectExternal(Document doc, ElementId segmentId, XYZ targetPt,
            Connector externalConn, string tag)
        {
            if (externalConn == null) return;

            Element elem = doc.GetElement(segmentId);
            Connector c = GetConnectorAtPoint(elem, targetPt);
            if (c == null)
            {
                _log.AppendLine($"⚠ {tag}: 未找到目标连接器，外部重连跳过");
                return;
            }
            if (c.IsConnected)
            {
                _log.AppendLine($"  {tag}: 已连接，跳过");
                return;
            }

            try
            {
                c.ConnectTo(externalConn);
                _log.AppendLine($"✓ 重连{tag}外部连接");
            }
            catch (Exception ex)
            {
                _log.AppendLine($"⚠ {tag}外部重连失败: {ex.Message}");
            }
        }

        private ElementId GetLevelId(Element elem, MEPElementType elemType)
        {
            ElementId levelId = elem.LevelId;
            if (levelId != ElementId.InvalidElementId)
                return levelId;

            if (elemType == MEPElementType.Pipe || elemType == MEPElementType.Duct)
            {
                Parameter p = elem.get_Parameter(BuiltInParameter.RBS_START_LEVEL_PARAM);
                if (p != null) levelId = p.AsElementId();
            }
            else if (elemType == MEPElementType.CableTray && elem is CableTray tray)
            {
                if (tray.ReferenceLevel != null)
                    levelId = tray.ReferenceLevel.Id;
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
            if (elem is MEPCurve mep)
                return mep.ConnectorManager;

            if (elem is FamilyInstance fi && fi.MEPModel != null)
                return fi.MEPModel.ConnectorManager;

            return null;
        }

        private Connector GetConnectorAtPoint(Element elem, XYZ targetPt)
        {
            ConnectorManager cm = GetConnectorManager(elem);
            if (cm == null) return null;

            double size = 0;
            MEPCurve mep = elem as MEPCurve;
            if (mep != null)
            {
                Parameter pd = mep.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM)
                            ?? mep.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)
                            ?? mep.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
                if (pd != null && pd.StorageType == StorageType.Double)
                    size = pd.AsDouble();
            }

            double tolMin = UnitUtils.ConvertToInternalUnits(1.5, UnitTypeId.Millimeters);
            double tolMax = UnitUtils.ConvertToInternalUnits(20.0, UnitTypeId.Millimeters);
            double tol = Math.Max(tolMin, Math.Min(tolMax, size * 0.1));

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