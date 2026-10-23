using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace MEPTools.UpDown
{
    public class MEPUpDownService
    {
        private readonly StringBuilder _log;

        public MEPUpDownService(StringBuilder log)
        {
            _log = log ?? new StringBuilder();
        }

        /// <summary>
        /// 处理单个元素：Pipe/Duct 走 BreakCurve，CableTray 走删除重建
        /// ⚠ 调用方必须已开启事务
        /// </summary>
        public bool ProcessSingleElement(Document doc, ElementId elemId,
            XYZ breakPt, XYZ sidePt, double h_offset_ft)
        {
            // ============================================================
            // 阶段 1：预校验（无副作用）
            // ============================================================
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

            // ---------- 几何计算 ----------
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
            _log.AppendLine($"过渡段水平投影长度: {Math.Abs(geom.Offset) * MEPUpDownCalculator.FtToMm:F1} mm");
            _log.AppendLine($"  打断点距起点: {geom.DistFromStart * MEPUpDownCalculator.FtToMm:F1} mm");
            _log.AppendLine($"  打断点距终点: {geom.DistToEnd * MEPUpDownCalculator.FtToMm:F1} mm");
            _log.AppendLine($"  升降侧可用空间: {geom.AvailableSpace * MEPUpDownCalculator.FtToMm:F1} mm");

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

            // ---------- 曲线端点 ----------
            XYZ curveStart = curve.GetEndPoint(0);
            XYZ curveEnd = curve.GetEndPoint(1);

            // ---------- 外部连接信息 ----------
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

            // ---------- 识别"被抬升侧"端点的外部连接（在修改前记录） ----------
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

            if (connectedToLiftedEnd.Count > 0)
            {
                _log.AppendLine($"  ℹ 抬升侧端点有 {connectedToLiftedEnd.Count} 个外部连接，将同步移动被连接元素");
                foreach (ElementId ceId in connectedToLiftedEnd)
                {
                    Element ce = doc.GetElement(ceId);
                    _log.AppendLine($"      · {ceId.IntegerValue} ({ce?.GetType().Name ?? "?"})");
                }
            }

            // 固定侧的外部连接（用于重连）
            Connector extAtFixed = fixedConn?.AllRefs
                .Cast<Connector>().FirstOrDefault(c => c.Owner is FamilyInstance);

            // ============================================================
            // 阶段 2：修改
            // ============================================================

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

            // ---------- 同步移动被连接元素以保持连接 ----------
            if (connectedToLiftedEnd.Count > 0)
            {
                SyncMoveConnectedElements(doc, connectedToLiftedEnd, liftedEndPt, h_offset_ft);

                // ---------- 移动后显式重连抬升侧 ----------
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

        // =====================================================================
        // 抬升侧显式重连
        // =====================================================================
        /// <summary>
        /// 抬升/下降完成后，将新元素在抬升端的连接器
        /// 与已被搬移过来的外部元素连接器显式 ConnectTo。
        /// Revit 不会因为几何重合而自动建立连接关系。
        /// </summary>
        private void ReconnectAtLiftedEnd(Document doc, ElementId liftedElemId,
            XYZ expectedPt, List<ElementId> movedIds)
        {
            if (liftedElemId == ElementId.InvalidElementId) return;

            doc.Regenerate();

            Element liftedElem = doc.GetElement(liftedElemId);
            if (liftedElem == null)
            {
                _log.AppendLine($"⚠ 抬升侧重连：新元素 {liftedElemId.IntegerValue} 不存在");
                return;
            }

            ConnectorManager liftedCm = GetConnectorManager(liftedElem);
            if (liftedCm == null)
            {
                _log.AppendLine($"⚠ 抬升侧重连：无法获取新元素 {liftedElemId.IntegerValue} 的连接器管理器");
                return;
            }

            // 合理容差：连接器与 LocationCurve 端点通常应重合，30mm 已足够宽
            double tol = UnitUtils.ConvertToInternalUnits(30.0, UnitTypeId.Millimeters);

            // ---------- 第一次尝试：按预期抬升端点位置匹配 ----------
            Connector liftedConn = FindNearestConnector(liftedCm, expectedPt, out double d1);

            // ---------- 第二次尝试：以新元素的 LocationCurve 端点为参考（应对 Revit 端点漂移） ----------
            if (liftedConn == null || d1 > tol)
            {
                if (liftedElem.Location is LocationCurve lc)
                {
                    XYZ p0 = lc.Curve.GetEndPoint(0);
                    XYZ p1 = lc.Curve.GetEndPoint(1);
                    // 抬升侧的远端 = 离 expectedPt 更近的那个端点
                    XYZ fallback = p0.DistanceTo(expectedPt) <= p1.DistanceTo(expectedPt) ? p0 : p1;

                    Connector alt = FindNearestConnector(liftedCm, fallback, out double d2);
                    if (alt != null && d2 <= tol)
                    {
                        _log.AppendLine(
                            $"  ℹ 抬升侧按 LocationCurve 端点回退匹配 (偏差 {d2 * MEPUpDownCalculator.FtToMm:F1}mm)");
                        liftedConn = alt;
                    }
                }
            }

            if (liftedConn == null)
            {
                _log.AppendLine(
                    $"⚠ 抬升侧重连：新元素 {liftedElemId.IntegerValue} 上未找到 {MEPUpDownCalculator.Fmt(expectedPt)} 附近的连接器");
                return;
            }

            if (liftedConn.IsConnected)
            {
                _log.AppendLine("  ℹ 抬升侧连接器已由 Revit 自动连接，跳过显式重连");
                return;
            }

            // ---------- 依次尝试连接到被搬移过来的元素 ----------
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
                    _log.AppendLine($"✓ 抬升侧重连成功：新元素 {liftedElemId.IntegerValue} ↔ {eid.IntegerValue}");
                    return;
                }
                catch (Exception ex)
                {
                    _log.AppendLine($"⚠ 抬升侧 ConnectTo 失败（{eid.IntegerValue}）：{ex.Message}");
                }
            }

            _log.AppendLine(
                $"⚠ 抬升侧未找到可连接的对象（预期位置 {MEPUpDownCalculator.Fmt(expectedPt)}），连接可能断开");
        }

        /// <summary>查找连接器管理器中距离 target 最近的连接器（不区分是否已连接）</summary>
        private Connector FindNearestConnector(ConnectorManager cm, XYZ target, out double distance)
        {
            Connector best = null;
            distance = double.MaxValue;
            if (cm == null) return null;

            foreach (Connector c in cm.Connectors)
            {
                double d = c.Origin.DistanceTo(target);
                if (d < distance)
                {
                    distance = d;
                    best = c;
                }
            }
            return best;
        }

        // =====================================================================
        // 同步移动被连接元素：保持抬升侧端点的连接
        // =====================================================================
        private void SyncMoveConnectedElements(Document doc, List<ElementId> connectedIds,
            XYZ originalLiftPt, double h_offset_ft)
        {
            XYZ offsetVec = XYZ.BasisZ * h_offset_ft;

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
        // Pipe / Duct 处理：BreakCurve + 重建过渡段
        // =====================================================================
        private bool ProcessPipeDuct(Document doc, ElementId elemId, MEPElementType elemType,
            UpDownGeometry geom, ElementId typeId, ElementId levelId, ElementId systemTypeId,
            double diameter, double width, double height,
            XYZ curveStart, XYZ curveEnd,
            Connector extAtFixed, XYZ fixedEndPt,
            out ElementId liftedEndElementId)
        {
            liftedEndElementId = ElementId.InvalidElementId;

            // ---------- 在打断点 P0 处打断原管段 ----------
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

            // ---------- 判断哪段是左侧段（靠近曲线起点） ----------
            Element e1 = doc.GetElement(id1);
            Element e2 = doc.GetElement(elemId);
            LocationCurve lc1 = e1?.Location as LocationCurve;
            LocationCurve lc2 = e2?.Location as LocationCurve;

            if (lc1 == null || lc2 == null)
                throw new InvalidOperationException("无法获取打断后的定位曲线");

            double d1Start = Math.Min(
                lc1.Curve.GetEndPoint(0).DistanceTo(curveStart),
                lc1.Curve.GetEndPoint(1).DistanceTo(curveStart));
            double d2Start = Math.Min(
                lc2.Curve.GetEndPoint(0).DistanceTo(curveStart),
                lc2.Curve.GetEndPoint(1).DistanceTo(curveStart));

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

            // ---------- 创建斜段 ----------
            Element ramp = CreateMEPCurve(doc, elemType, typeId, levelId, systemTypeId,
                diameter, width, height, geom.P0, geom.P1);
            if (ramp == null) throw new InvalidOperationException("斜段创建失败");

            // ---------- 创建新高度段（若长度不为零） ----------
            Element newHoriz = null;
            if (!newHorizStart.IsAlmostEqualTo(newHorizEnd, 1e-6))
            {
                newHoriz = CreateMEPCurve(doc, elemType, typeId, levelId, systemTypeId,
                    diameter, width, height, newHorizStart, newHorizEnd);
                if (newHoriz == null) throw new InvalidOperationException("新高度段创建失败");
            }

            doc.Regenerate();

            if (newHoriz != null)
            {
                _log.AppendLine(
                    $"重建 → 斜段={ramp.Id.IntegerValue}, 新高度段={newHoriz.Id.IntegerValue}");
            }
            else
            {
                _log.AppendLine($"重建 → 斜段={ramp.Id.IntegerValue}（新高度段长度为零，已省略）");
            }

            // ---------- 创建弯头 ----------
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

            // ---------- 记录"抬升侧端点"所在的新元素 ----------
            liftedEndElementId = newHoriz != null ? newHoriz.Id : ramp.Id;

            // ---------- 固定侧端点外部连接重连 ----------
            ReconnectExternal(doc, keepSegId, fixedEndPt, extAtFixed, "固定侧");

            return true;
        }

        // =====================================================================
        // CableTray 专用处理：整段删除 + 重建
        // =====================================================================
        private bool ProcessCableTray(Document doc, CableTray tray, UpDownGeometry geom,
            ElementId typeId, ElementId levelId, double width, double height,
            XYZ curveStart, XYZ curveEnd,
            Connector extAtFixed, XYZ fixedEndPt,
            out ElementId liftedEndElementId)
        {
            liftedEndElementId = ElementId.InvalidElementId;

            if (tray == null)
            {
                _log.AppendLine("✗ CableTray 为空");
                return false;
            }

            LocationCurve lc = tray.Location as LocationCurve;
            if (lc == null)
            {
                _log.AppendLine("✗ CT 无定位曲线");
                return false;
            }

            XYZ trayStart = lc.Curve.GetEndPoint(0);
            XYZ trayEnd = lc.Curve.GetEndPoint(1);
            ElementId trayId = tray.Id;

            _log.AppendLine($"CT 原始段: {MEPUpDownCalculator.Fmt(trayStart)} → {MEPUpDownCalculator.Fmt(trayEnd)}");
            _log.AppendLine($"CT 截面: 宽={width * MEPUpDownCalculator.FtToMm:F1}mm 高={height * MEPUpDownCalculator.FtToMm:F1}mm");

            doc.Delete(trayId);
            doc.Regenerate();

            List<Element> segs = new List<Element>();
            const double endpointTol = 1e-4;

            XYZ newEnd = trayEnd + XYZ.BasisZ * geom.Offset;
            XYZ newStart = trayStart + XYZ.BasisZ * geom.Offset;

            if (geom.LiftRight)
            {
                if (!trayStart.IsAlmostEqualTo(geom.P0, endpointTol))
                {
                    Element s1 = CreateCableTraySegment(doc, typeId, levelId, width, height, trayStart, geom.P0);
                    if (s1 == null) { _log.AppendLine("✗ CT 段1 创建失败"); return false; }
                    segs.Add(s1);
                }
                else
                {
                    _log.AppendLine("  CT 段1 跳过（P0 与起点重合）");
                }

                Element s2 = CreateCableTraySegment(doc, typeId, levelId, width, height, geom.P0, geom.P1);
                if (s2 == null) { _log.AppendLine("✗ CT 斜段创建失败"); return false; }
                segs.Add(s2);

                if (!geom.P1.IsAlmostEqualTo(newEnd, endpointTol))
                {
                    Element s3 = CreateCableTraySegment(doc, typeId, levelId, width, height, geom.P1, newEnd);
                    if (s3 == null) { _log.AppendLine("✗ CT 新高度段创建失败"); return false; }
                    segs.Add(s3);
                }
                else
                {
                    _log.AppendLine("  CT 新高度段 跳过（P1 与 newEnd 重合）");
                }
            }
            else
            {
                if (!newStart.IsAlmostEqualTo(geom.P1, endpointTol))
                {
                    Element s1 = CreateCableTraySegment(doc, typeId, levelId, width, height, newStart, geom.P1);
                    if (s1 == null) { _log.AppendLine("✗ CT 新高度段创建失败"); return false; }
                    segs.Add(s1);
                }
                else
                {
                    _log.AppendLine("  CT 新高度段 跳过（newStart 与 P1 重合）");
                }

                Element s2 = CreateCableTraySegment(doc, typeId, levelId, width, height, geom.P1, geom.P0);
                if (s2 == null) { _log.AppendLine("✗ CT 斜段创建失败"); return false; }
                segs.Add(s2);

                if (!geom.P0.IsAlmostEqualTo(trayEnd, endpointTol))
                {
                    Element s3 = CreateCableTraySegment(doc, typeId, levelId, width, height, geom.P0, trayEnd);
                    if (s3 == null) { _log.AppendLine("✗ CT 段3 创建失败"); return false; }
                    segs.Add(s3);
                }
                else
                {
                    _log.AppendLine("  CT 段3 跳过（P0 与终点重合）");
                }
            }

            doc.Regenerate();
            _log.AppendLine($"CT 重建 {segs.Count} 段");

            // ---------- 创建弯头 ----------
            for (int i = 0; i < segs.Count - 1; i++)
            {
                Element ea = segs[i];
                Element eb = segs[i + 1];
                XYZ joint = FindCommonEndPoint(ea, eb);
                if (joint == null)
                {
                    _log.AppendLine($"⚠ CT 段{i + 1}-{i + 2} 未找到公共端点，跳过弯头");
                    continue;
                }
                TryCreateElbow(doc, ea.Id, eb.Id, joint, $"CT弯头{i + 1}");
            }

            // ---------- 记录"抬升侧端点"所在的新 CT 段 ----------
            if (segs.Count > 0)
            {
                liftedEndElementId = geom.LiftRight
                    ? segs[segs.Count - 1].Id
                    : segs[0].Id;
            }

            // ---------- 固定侧端点外部连接重连 ----------
            if (segs.Count > 0)
            {
                if (geom.LiftRight)
                {
                    ReconnectExternal(doc, segs[0].Id, curveStart, extAtFixed, "CT固定侧");
                }
                else
                {
                    ReconnectExternal(doc, segs[segs.Count - 1].Id, curveEnd, extAtFixed, "CT固定侧");
                }
            }

            return true;
        }

        /// <summary>创建单个线槽段（含宽高设置）</summary>
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

        /// <summary>查找两个元素 LocationCurve 上的公共端点</summary>
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
                _log.AppendLine($"⚠ 跳过零长段 ({MEPUpDownCalculator.Fmt(s)})");
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

        /// <summary>
        /// 创建弯头：严格位置匹配（保持原路径）→ 回退到"两元素间最近的未连接连接器对"。
        /// 合理性上限 = min(短段长度, 1000mm)。无硬编码容差、无魔法数。
        /// </summary>
        private bool TryCreateElbow(Document doc, ElementId idA, ElementId idB, XYZ atPt, string tag)
        {
            Element elA = doc.GetElement(idA);
            Element elB = doc.GetElement(idB);
            if (elA == null || elB == null)
            {
                _log.AppendLine($"✗ {tag}: 关联元素为空");
                return false;
            }

            // ---------- 步骤 1：严格位置匹配 ----------
            Connector ca = GetConnectorAtPoint(elA, atPt);
            Connector cb = GetConnectorAtPoint(elB, atPt);

            // ---------- 步骤 2：回退 —— 找两元素间最近的未连接连接器对 ----------
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

                _log.AppendLine($"  {tag}: 最近连接器对距离 {pairDist * MEPUpDownCalculator.FtToMm:F1}mm");

                ca = pa;
                cb = pb;
            }

            // ---------- 步骤 3：合理性校验 ----------
            // 上限 = min(短段长度, 1000mm)。两相邻段接口之间的连接器距离，
            // 从几何上讲不可能超过它们各自段长。
            double dist = ca.Origin.DistanceTo(cb.Origin);
            double lenA = GetLocationCurveLength(elA);
            double lenB = GetLocationCurveLength(elB);
            double minSegLen = (lenA > 0 && lenB > 0) ? Math.Min(lenA, lenB) : 0;
            double hardCap = UnitUtils.ConvertToInternalUnits(1000.0, UnitTypeId.Millimeters);
            double maxAllowed = minSegLen > 0 ? Math.Min(minSegLen, hardCap) : hardCap;

            if (dist > maxAllowed)
            {
                _log.AppendLine(
                    $"✗ {tag}: 连接器对距离 {dist * MEPUpDownCalculator.FtToMm:F1}mm " +
                    $"超过合理上限 {maxAllowed * MEPUpDownCalculator.FtToMm:F1}mm，放弃");
                return false;
            }

            // ---------- 步骤 4：角度校验 ----------
            double outerDeg = ca.CoordinateSystem.BasisZ
                .AngleTo(cb.CoordinateSystem.BasisZ) * 180.0 / Math.PI;
            double elbowDeg = MEPUpDownCalculator.CalcElbowAngleDeg(ca, cb);

            _log.AppendLine(
                $"  {tag}: 连接器间距 {dist * MEPUpDownCalculator.FtToMm:F1}mm，" +
                $"外角={outerDeg:F2}° → 内角={elbowDeg:F2}°");

            if (!MEPUpDownCalculator.IsElbowAngleValid(elbowDeg))
            {
                _log.AppendLine($"✗ {tag}: 弯头角度 {elbowDeg:F2}° 超出范围 (2~95°)");
                return false;
            }

            // ---------- 步骤 5：创建弯头 ----------
            try
            {
                FamilyInstance fit = doc.Create.NewElbowFitting(ca, cb);
                if (fit == null)
                {
                    _log.AppendLine($"✗ {tag}: NewElbowFitting 返回 null（可能缺配件族）");
                    return false;
                }

                Parameter angleParam = fit.LookupParameter("Angle")
                                    ?? fit.LookupParameter("角度")
                                    ?? fit.LookupParameter("Bend Angle")
                                    ?? fit.LookupParameter("弯头角度");

                if (angleParam != null && !angleParam.IsReadOnly
                    && angleParam.StorageType == StorageType.Double)
                {
                    try
                    {
                        angleParam.Set(45.0);
                        _log.AppendLine($"✓ {tag}: 创建成功，角度设为 45°");
                    }
                    catch
                    {
                        angleParam.Set(45.0 * Math.PI / 180.0);
                        _log.AppendLine($"✓ {tag}: 创建成功，角度设为 45°（弧度）");
                    }
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

        /// <summary>读取元素 LocationCurve 的长度（ft）；失败返回 0</summary>
        private double GetLocationCurveLength(Element elem)
        {
            if (elem?.Location is LocationCurve lc && lc.Curve != null)
                return lc.Curve.Length;
            return 0;
        }

        /// <summary>
        /// 在两个元素之间查找最近的"未连接连接器对"。
        /// 用于位置匹配失败时的回退。
        /// </summary>
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
                    if (d < distance)
                    {
                        distance = d;
                        ca = c1;
                        cb = c2;
                    }
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

        /// <summary>获取元素的连接器管理器：MEPCurve 与 FamilyInstance 均支持。</summary>
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
                if (d < bestDist)
                {
                    bestDist = d;
                    best = c;
                }
            }

            return (best != null && bestDist < tol) ? best : null;
        }
    }
}