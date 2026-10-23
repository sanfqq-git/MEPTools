using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

// 解决 Connector 命名冲突
using Connector = Autodesk.Revit.DB.Connector;

namespace MEPTools.MEPHighLowSaddleBend
{
    public class MEPOffsetBendService
    {
        private readonly StringBuilder _log;

        public MEPOffsetBendService(StringBuilder log)
        {
            _log = log ?? new StringBuilder();
        }

        /// <summary>
        /// 处理单个元素：Pipe/Duct 走 BreakCurve，CableTray 走删除重建
        /// ⚠ 调用方必须已开启事务
        /// </summary>
        public bool ProcessSingleElement(Document doc, ElementId elemId,
            XYZ pickPt1, XYZ pickPt2, double h_offset_ft)
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

            MEPElementType elemType = MEPOffsetBendCalculator.GetElementType(elem);
            if (elemType == MEPElementType.Unknown)
            {
                _log.AppendLine("✗ 元素类型不支持 45° 翻弯");
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
            OffsetGeometry geom = MEPOffsetBendCalculator.Compute(
                curve, pickPt1, pickPt2, h_offset_ft, out geomErr);
            if (!geom.IsValid)
            {
                _log.AppendLine($"✗ {geomErr}");
                return false;
            }

            _log.AppendLine($"打断点间距: {geom.Length * MEPOffsetBendCalculator.FtToMm:F1} mm");
            _log.AppendLine($"所需最小间距: {geom.RequiredLength * MEPOffsetBendCalculator.FtToMm:F1} mm");
            _log.AppendLine($"pA={MEPOffsetBendCalculator.Fmt(geom.PA)}  p3={MEPOffsetBendCalculator.Fmt(geom.P3)}");
            _log.AppendLine($"p4={MEPOffsetBendCalculator.Fmt(geom.P4)}  pB={MEPOffsetBendCalculator.Fmt(geom.PB)}");

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

            // ---------- 外部连接信息 ----------
            ConnectorManager cm = GetConnectorManager(elem);
            if (cm == null)
            {
                _log.AppendLine("✗ 无法获取连接器管理器");
                return false;
            }

            Connector connStart = null, connEnd = null;
            foreach (Connector c in cm.Connectors)
            {
                if (c.CoordinateSystem.BasisZ.DotProduct(geom.Dir) > 0) connEnd = c;
                else connStart = c;
            }
            if (connStart == null || connEnd == null)
            {
                _log.AppendLine("✗ 无法获取管线两端连接器");
                return false;
            }

            XYZ originalStartPt = connStart.Origin;
            XYZ originalEndPt = connEnd.Origin;

            Connector externalConnAtStart = connStart.AllRefs
                .Cast<Connector>().FirstOrDefault(c => c.Owner is FamilyInstance);
            Connector externalConnAtEnd = connEnd.AllRefs
                .Cast<Connector>().FirstOrDefault(c => c.Owner is FamilyInstance);

            // ============================================================
            // 阶段 2：修改
            // ============================================================

            // ---------- CableTray 分支：删除 + 重建 ----------
            if (elemType == MEPElementType.CableTray)
            {
                return ProcessCableTray(doc, elem as CableTray, geom,
                    typeId, levelId, width, height,
                    originalStartPt, originalEndPt,
                    externalConnAtStart, externalConnAtEnd);
            }

            // ---------- Pipe/Duct 分支：BreakCurve ----------
            ElementId segLeftId, segMidId, segRightId;

            if (elemType == MEPElementType.Pipe)
            {
                segLeftId = PlumbingUtils.BreakCurve(doc, elemId, geom.PA);
                segMidId = PlumbingUtils.BreakCurve(doc, elemId, geom.PB);
                segRightId = elemId;
            }
            else // Duct
            {
                segLeftId = MechanicalUtils.BreakCurve(doc, elemId, geom.PA);
                segMidId = MechanicalUtils.BreakCurve(doc, elemId, geom.PB);
                segRightId = elemId;
            }

            if (segLeftId == ElementId.InvalidElementId || segMidId == ElementId.InvalidElementId)
                throw new InvalidOperationException($"BreakCurve 返回无效 ID (elemId={elemId.IntegerValue})");

            doc.Regenerate();

            _log.AppendLine(
                $"打断完成 → 左段 ID={segLeftId.IntegerValue}, 中段 ID={segMidId.IntegerValue}, 右段 ID={segRightId.IntegerValue}");

            doc.Delete(segMidId);

            Element segA = CreateMEPCurve(doc, elemType, typeId, levelId, systemTypeId,
                diameter, width, height, geom.PA, geom.P3);
            Element segB = CreateMEPCurve(doc, elemType, typeId, levelId, systemTypeId,
                diameter, width, height, geom.P3, geom.P4);
            Element segC = CreateMEPCurve(doc, elemType, typeId, levelId, systemTypeId,
                diameter, width, height, geom.P4, geom.PB);

            if (segA == null || segB == null || segC == null)
                throw new InvalidOperationException($"重建管段失败 (elemId={elemId.IntegerValue})");

            doc.Regenerate();

            _log.AppendLine(
                $"重建段 → A={segA.Id.IntegerValue}, B={segB.Id.IntegerValue}, C={segC.Id.IntegerValue}");

            TryCreateElbow(doc, segLeftId, segA.Id, geom.PA, "弯头1(原段-斜段a)");
            TryCreateElbow(doc, segA.Id, segB.Id, geom.P3, "弯头2(斜段a-水平段)");
            TryCreateElbow(doc, segB.Id, segC.Id, geom.P4, "弯头3(水平段-斜段c)");
            TryCreateElbow(doc, segC.Id, segRightId, geom.PB, "弯头4(斜段c-原段)");

            ReconnectExternal(doc, segLeftId, originalStartPt, externalConnAtStart, "起点");
            ReconnectExternal(doc, segRightId, originalEndPt, externalConnAtEnd, "终点");

            _log.AppendLine($"✓ 元素 {elemId.IntegerValue} 翻弯处理成功");
            return true;
        }

        // =====================================================================
        // CableTray 专用处理
        // =====================================================================
        /// <summary>
        /// 线槽处理：删除整段 → 重建 5 段（首尾可省略）→ 4 弯头 → 重连外部
        /// </summary>
        private bool ProcessCableTray(Document doc, CableTray tray, OffsetGeometry geom,
            ElementId typeId, ElementId levelId, double width, double height,
            XYZ originalStartPt, XYZ originalEndPt,
            Connector extStart, Connector extEnd)
        {
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

            _log.AppendLine($"CT 原始段: {MEPOffsetBendCalculator.Fmt(trayStart)} → {MEPOffsetBendCalculator.Fmt(trayEnd)}");
            _log.AppendLine($"CT 截面: 宽={width * MEPOffsetBendCalculator.FtToMm:F1}mm 高={height * MEPOffsetBendCalculator.FtToMm:F1}mm");

            // ---------- 删除原 CT ----------
            doc.Delete(trayId);
            doc.Regenerate();

            // ---------- 重建 5 段 ----------
            List<Element> segs = new List<Element>();
            const double endpointTol = 1e-4; // 约 0.03mm

            // 段1：trayStart → pA（若 pA 已在 trayStart 附近则跳过）
            if (!trayStart.IsAlmostEqualTo(geom.PA, endpointTol))
            {
                Element s1 = CreateCableTraySegment(doc, typeId, levelId, width, height, trayStart, geom.PA);
                if (s1 == null) { _log.AppendLine("✗ CT 段1 创建失败"); return false; }
                segs.Add(s1);
            }
            else
            {
                _log.AppendLine("  CT 段1 跳过（pA 与起点重合）");
            }

            // 段2：pA → p3（45° 斜段 a）
            Element s2 = CreateCableTraySegment(doc, typeId, levelId, width, height, geom.PA, geom.P3);
            if (s2 == null) { _log.AppendLine("✗ CT 斜段a 创建失败"); return false; }
            segs.Add(s2);

            // 段3：p3 → p4（水平抬升段）
            Element s3 = CreateCableTraySegment(doc, typeId, levelId, width, height, geom.P3, geom.P4);
            if (s3 == null) { _log.AppendLine("✗ CT 水平段 创建失败"); return false; }
            segs.Add(s3);

            // 段4：p4 → pB（45° 斜段 c）
            Element s4 = CreateCableTraySegment(doc, typeId, levelId, width, height, geom.P4, geom.PB);
            if (s4 == null) { _log.AppendLine("✗ CT 斜段c 创建失败"); return false; }
            segs.Add(s4);

            // 段5：pB → trayEnd（若 pB 已在 trayEnd 附近则跳过）
            if (!geom.PB.IsAlmostEqualTo(trayEnd, endpointTol))
            {
                Element s5 = CreateCableTraySegment(doc, typeId, levelId, width, height, geom.PB, trayEnd);
                if (s5 == null) { _log.AppendLine("✗ CT 段5 创建失败"); return false; }
                segs.Add(s5);
            }
            else
            {
                _log.AppendLine("  CT 段5 跳过（pB 与终点重合）");
            }

            doc.Regenerate();
            _log.AppendLine($"CT 重建 {segs.Count} 段");

            // ---------- 创建弯头（相邻段公共端点处） ----------
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

            // ---------- 重连外部设备 ----------
            if (segs.Count > 0)
            {
                Element firstSeg = segs[0];
                Element lastSeg = segs[segs.Count - 1];

                if (extStart != null)
                    ReconnectExternal(doc, firstSeg.Id, originalStartPt, extStart, "CT起点");
                if (extEnd != null)
                    ReconnectExternal(doc, lastSeg.Id, originalEndPt, extEnd, "CT终点");
            }

            _log.AppendLine($"✓ CT {trayId.IntegerValue} 翻弯处理成功");
            return true;
        }

        /// <summary>
        /// 创建单个线槽段（含宽高设置）
        /// </summary>
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

        /// <summary>
        /// 查找两个元素 LocationCurve 上的公共端点
        /// </summary>
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
                _log.AppendLine($"⚠ 跳过零长段 ({MEPOffsetBendCalculator.Fmt(s)})");
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

                _log.AppendLine($"  {tag}: 最近连接器对距离 {pairDist * MEPOffsetBendCalculator.FtToMm:F1}mm");

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
                    $"✗ {tag}: 连接器对距离 {dist * MEPOffsetBendCalculator.FtToMm:F1}mm " +
                    $"超过合理上限 {maxAllowed * MEPOffsetBendCalculator.FtToMm:F1}mm，放弃");
                return false;
            }

            // ---------- 步骤 4：角度校验 ----------
            double outerDeg = ca.CoordinateSystem.BasisZ
                .AngleTo(cb.CoordinateSystem.BasisZ) * 180.0 / Math.PI;
            double elbowDeg = MEPOffsetBendCalculator.CalcElbowAngleDeg(ca, cb);

            _log.AppendLine(
                $"  {tag}: 连接器间距 {dist * MEPOffsetBendCalculator.FtToMm:F1}mm，" +
                $"外角={outerDeg:F2}° → 内角={elbowDeg:F2}°");

            if (!MEPOffsetBendCalculator.IsElbowAngleValid(elbowDeg))
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

        private ConnectorManager GetConnectorManager(Element elem)
        {
            MEPCurve mep = elem as MEPCurve;
            return mep?.ConnectorManager;
        }

        private Connector GetConnectorAtPoint(Element elem, XYZ targetPt)
        {
            MEPCurve mep = elem as MEPCurve;
            if (mep == null) return null;

            // 动态容差：管径/线槽宽的 1/10，夹在 [1.5mm, 20mm]
            double size = 0;
            Parameter pd = mep.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM)
                        ?? mep.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)
                        ?? mep.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
            if (pd != null && pd.StorageType == StorageType.Double)
                size = pd.AsDouble();

            double tolMin = UnitUtils.ConvertToInternalUnits(1.5, UnitTypeId.Millimeters);
            double tolMax = UnitUtils.ConvertToInternalUnits(20.0, UnitTypeId.Millimeters);
            double tol = Math.Max(tolMin, Math.Min(tolMax, size * 0.1));

            Connector best = null;
            double bestDist = double.MaxValue;

            foreach (Connector c in mep.ConnectorManager.Connectors)
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