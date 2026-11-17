using System;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace MEPTools.MEP3TeeConnect
{
    /// <summary>
    /// 单对 T 形处理流程：
    ///   1) 修改 B 的近端（延长/缩短）到交点
    ///   2) 打断 A 于交点（BreakCurve）
    ///   3) 创建立管（X/Y = 交点，Z 从 A 中心线到 B 中心线），立管规格取 B
    ///      ★ 矩形风管：检测立管顶朝向，避免三通处矩形截面扭曲
    ///         - 情况一：立管正方形 → 保持
    ///         - 情况二：立管长方形，长边 ∥ A 轴 → 保持
    ///         - 情况三：立管长方形，长边 ⟂ A 轴 → 正方形化（边长 = 长边）
    ///   4) A 三通 = A 左段 + A 右段 + 立管接 A 端
    ///   5) B 弯头 = 立管接 B 端 + B 近端连接器
    /// ⚠ 调用方必须已开启事务
    /// </summary>
    public class MEPTeeService
    {
        private readonly StringBuilder _log;
        private const double ConnectorTolMm = 50.0;
        private const double ParallelDotThreshold = 0.99;   // |dot| > 0.99 视为平行
        private const double SizeEpsilon = 1e-6;

        public MEPTeeService(StringBuilder log)
        {
            _log = log ?? new StringBuilder();
        }

        public void ProcessPair(Document doc, TeePair p)
        {
            Element elemA = doc.GetElement(p.IdA);
            Element elemB = doc.GetElement(p.IdB);
            if (elemA == null || elemB == null)
                throw new InvalidOperationException("元素已删除");

            // 保持 B 的原始 Z，只改 XY
            XYZ bEnds0 = (elemB.Location as LocationCurve).Curve.GetEndPoint(0);
            XYZ bEnds1 = (elemB.Location as LocationCurve).Curve.GetEndPoint(1);
            double bZ = (bEnds0.Z + bEnds1.Z) * 0.5;

            XYZ crossPtB = new XYZ(p.CrossPoint.X, p.CrossPoint.Y, bZ);
            XYZ crossPtA = new XYZ(p.CrossPoint.X, p.CrossPoint.Y, p.ZA);

            _log.AppendLine($"  交点 (平面)：{Fmt2D(p.CrossPoint)}");
            _log.AppendLine($"  A 中心线 Z = {p.ZA * MEPTeeCalculator.FtToMm:F1}mm");
            _log.AppendLine($"  B 中心线 Z = {p.ZB * MEPTeeCalculator.FtToMm:F1}mm");
            _log.AppendLine($"  B 修改端点：{(p.ModifyB0 ? "b0" : "b1")}，" +
                            $"{(p.NeedExtend ? "延长" : "缩短")}");
            _log.AppendLine($"  立管规格（取 B）：{FmtSize(p.Diameter, p.Width, p.Height)}");

            // ============================================================
            // 1. 修改 B 的端点
            // ============================================================
            ModifyBEndpoint(doc, p, crossPtB);
            doc.Regenerate();

            double newBLen = GetCurveLength(doc.GetElement(p.IdB));
            _log.AppendLine($"  B 修改后长度：{newBLen * MEPTeeCalculator.FtToMm:F1}mm");
            if (newBLen < UnitUtils.ConvertToInternalUnits(
                MEPTeeCalculator.MinBLengthMm, UnitTypeId.Millimeters))
            {
                throw new InvalidOperationException(
                    $"B 修改后长度 {newBLen * MEPTeeCalculator.FtToMm:F1}mm 小于 " +
                    $"{MEPTeeCalculator.MinBLengthMm}mm，请调整选择");
            }

            // ============================================================
            // 2. 打断 A 于交点
            // ============================================================
            ElementId aLeft, aRight;
            ElementId other = BreakCurve(doc, p.IdA, crossPtA, p.ElemType);
            if (other == null || other == ElementId.InvalidElementId)
                throw new InvalidOperationException("A 管打断失败");
            aLeft = p.IdA;
            aRight = other;
            doc.Regenerate();
            _log.AppendLine($"  A 打断：左段={aLeft.IntegerValue}，右段={aRight.IntegerValue}");

            // ============================================================
            // 3. 创建立管（交点处，Z 从 A 中心到 B 中心，规格取 B）
            // ============================================================
            XYZ riserTop = new XYZ(p.CrossPoint.X, p.CrossPoint.Y, p.ZA);
            XYZ riserBot = new XYZ(p.CrossPoint.X, p.CrossPoint.Y, p.ZB);

            Element riser = CreateMEPCurve(doc, p.ElemType,
                p.TypeId, p.LevelId, p.SystemTypeId,
                p.Diameter, p.Width, p.Height,
                riserTop, riserBot);
            if (riser == null)
                throw new InvalidOperationException("立管创建失败");
            doc.Regenerate();
            _log.AppendLine($"  立管：ID={riser.Id.IntegerValue}");

            // ★ 新增：矩形风管立管顶朝向检测与正方形化（防扭曲）
            AdjustRiserTopForRectDuct(doc, p, riser, elemA, riserTop);
            doc.Regenerate();

            // ============================================================
            // 4. 三通（异径时 Revit 自动选异径三通族）
            // ============================================================
            Connector caL = FindConnectorAtPoint(doc.GetElement(aLeft), crossPtA);
            Connector caR = FindConnectorAtPoint(doc.GetElement(aRight), crossPtA);
            Connector rTop = FindConnectorAtPoint(riser, riserTop);

            if (caL == null || caR == null || rTop == null)
                throw new InvalidOperationException(
                    $"A 三通连接器未找齐（左={caL != null}, 右={caR != null}, 立管={rTop != null}）");

            FamilyInstance tee = doc.Create.NewTeeFitting(caL, caR, rTop);
            if (tee == null)
            {
                throw new InvalidOperationException(
                    "A 三通创建失败。规格：A " + FmtSize(p.DiameterA, p.WidthA, p.HeightA) +
                    " / 立管 " + FmtConnSize(rTop) +
                    "。若为异径连接，请确认项目中有匹配的异径三通族" +
                    "（如 Tee - Generic - Reducing）且规格覆盖该组合。");
            }
            doc.Regenerate();
            _log.AppendLine($"  三通：ID={tee.Id.IntegerValue}，族={tee.Symbol?.Family?.Name}");

            // ============================================================
            // 5. 弯头（异径时 Revit 自动选异径弯头族）
            // ============================================================
            Connector rBot = FindConnectorAtPoint(riser, riserBot);
            Connector bNear = FindConnectorAtPoint(doc.GetElement(p.IdB), crossPtB);

            if (rBot == null || bNear == null)
                throw new InvalidOperationException(
                    $"弯头连接器未找齐（立管底={rBot != null}, B 近端={bNear != null}）");

            FamilyInstance elbow = doc.Create.NewElbowFitting(rBot, bNear);
            if (elbow == null)
            {
                throw new InvalidOperationException(
                    "弯头创建失败。规格：立管 " + FmtConnSize(rBot) +
                    " / B " + FmtSize(p.DiameterB, p.WidthB, p.HeightB) +
                    "。若为异径连接，请确认项目中有匹配的异径弯头族。");
            }
            doc.Regenerate();
            _log.AppendLine($"  弯头：ID={elbow.Id.IntegerValue}，族={elbow.Symbol?.Family?.Name}");

            _log.AppendLine($"  ✓ 系统 {p.SystemName} 处理完成");
        }

        // =====================================================================
        // ★ 立管顶朝向检测与正方形化（防止三通处矩形截面扭曲）
        // =====================================================================
        private void AdjustRiserTopForRectDuct(Document doc, TeePair p, Element riser,
            Element elemA, XYZ riserTop)
        {
            // 圆形管（Pipe）或未知类型：不处理
            if (p.ElemType != TeeElementType.Duct) return;

            Connector rTop = FindConnectorAtPoint(riser, riserTop);
            if (rTop == null) return;
            if (rTop.Shape != ConnectorProfileType.Rectangular) return;   // 圆形风管不处理

            double w = rTop.Width;
            double h = rTop.Height;
            if (w <= SizeEpsilon || h <= SizeEpsilon) return;

            const double f = MEPTeeCalculator.FtToMm;

            // ---------- 情况一：正方形 → 保持 ----------
            if (Math.Abs(w - h) < SizeEpsilon)
            {
                _log.AppendLine($"  立管顶：正方形 {w * f:F0}x{h * f:F0}，保持");
                return;
            }

            // ---------- 长方形：判断长边朝向 ----------
            Transform cs = rTop.CoordinateSystem;
            if (cs == null)
            {
                _log.AppendLine("  立管顶：无法读取坐标系，跳过朝向调整");
                return;
            }

            // 长边方向（水平面内两条正交边之一）
            XYZ longSideDir = (w > h) ? cs.BasisX : cs.BasisY;

            XYZ axisA = GetAxisDirection(elemA);
            if (axisA == null)
            {
                _log.AppendLine("  立管顶：无法获取 A 轴方向，跳过朝向调整");
                return;
            }

            double dot = Math.Abs(longSideDir.DotProduct(axisA));
            bool isParallel = dot > ParallelDotThreshold;

            // ---------- 情况二：长边 ∥ A 轴，不扭曲 → 保持 ----------
            if (isParallel)
            {
                _log.AppendLine(
                    $"  立管顶：长方形 {w * f:F0}x{h * f:F0}，长边 ∥ A 轴（平行，不扭曲），保持");
                return;
            }

            // ---------- 情况三：长边 ⟂ A 轴（或斜交）→ 正方形化 ----------
            double maxSide = Math.Max(w, h);
            _log.AppendLine(
                $"  立管顶：长方形 {w * f:F0}x{h * f:F0}，长边 ⟂ A 轴（垂直，会扭曲）→ " +
                $"正方形化 {maxSide * f:F0}x{maxSide * f:F0}");

            TrySetParam(riser, BuiltInParameter.RBS_CURVE_WIDTH_PARAM, maxSide);
            TrySetParam(riser, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM, maxSide);
            doc.Regenerate();

            // 验证修改是否生效（部分立管受约束时可能改不动）
            Connector rTopAfter = FindConnectorAtPoint(riser, riserTop);
            if (rTopAfter != null && rTopAfter.Shape == ConnectorProfileType.Rectangular)
            {
                double w2 = rTopAfter.Width;
                double h2 = rTopAfter.Height;
                if (Math.Abs(w2 - h2) < SizeEpsilon && Math.Abs(w2 - maxSide) < SizeEpsilon)
                {
                    _log.AppendLine($"  ✓ 立管顶正方形化成功：{w2 * f:F0}x{h2 * f:F0}");
                }
                else
                {
                    _log.AppendLine(
                        $"  ⚠ 立管顶正方形化未完全生效（当前 {w2 * f:F0}x{h2 * f:F0}），" +
                        $"三通可能因截面不匹配而失败，请检查立管约束");
                }
            }
        }

        /// <summary>获取水平管轴线方向（单位向量，XY 平面）</summary>
        private XYZ GetAxisDirection(Element e)
        {
            if (e?.Location is LocationCurve lc && lc.Curve is Line line)
            {
                XYZ d = line.Direction;
                XYZ flat = new XYZ(d.X, d.Y, 0);
                if (flat.GetLength() > 1e-6) return flat.Normalize();
            }
            return null;
        }

        // =====================================================================
        // B 端点修改
        // =====================================================================
        private void ModifyBEndpoint(Document doc, TeePair p, XYZ newEndPoint)
        {
            Element b = doc.GetElement(p.IdB);
            if (!(b.Location is LocationCurve lc))
                throw new InvalidOperationException("B 无定位曲线");

            XYZ p0 = lc.Curve.GetEndPoint(0);
            XYZ p1 = lc.Curve.GetEndPoint(1);

            XYZ newP0 = p.ModifyB0
                ? new XYZ(newEndPoint.X, newEndPoint.Y, p0.Z)
                : p0;
            XYZ newP1 = p.ModifyB0
                ? p1
                : new XYZ(newEndPoint.X, newEndPoint.Y, p1.Z);

            if (newP0.DistanceTo(newP1) < 1e-6)
                throw new InvalidOperationException("B 修改后长度为零");

            lc.Curve = Line.CreateBound(newP0, newP1);
        }

        private double GetCurveLength(Element e)
        {
            if (e?.Location is LocationCurve lc && lc.Curve != null)
                return lc.Curve.Length;
            return 0;
        }

        // =====================================================================
        // 打断
        // =====================================================================
        private ElementId BreakCurve(Document doc, ElementId elemId, XYZ breakPt, TeeElementType t)
        {
            if (t == TeeElementType.Pipe)
                return PlumbingUtils.BreakCurve(doc, elemId, breakPt);
            if (t == TeeElementType.Duct)
                return MechanicalUtils.BreakCurve(doc, elemId, breakPt);
            return ElementId.InvalidElementId;
        }

        // =====================================================================
        // 连接器定位
        // =====================================================================
        private Connector FindConnectorAtPoint(Element e, XYZ pt)
        {
            if (e == null) return null;
            var cm = (e as MEPCurve)?.ConnectorManager;
            if (cm == null) return null;

            Connector best = null;
            double bd = double.MaxValue;
            foreach (Connector c in cm.Connectors)
            {
                double d = c.Origin.DistanceTo(pt);
                if (d < bd) { bd = d; best = c; }
            }
            double tol = UnitUtils.ConvertToInternalUnits(ConnectorTolMm, UnitTypeId.Millimeters);
            return (best != null && bd < tol) ? best : null;
        }

        // =====================================================================
        // 元素创建
        // =====================================================================
        private Element CreateMEPCurve(Document doc, TeeElementType t,
            ElementId typeId, ElementId levelId, ElementId systemTypeId,
            double dia, double w, double h, XYZ s, XYZ e)
        {
            if (s.IsAlmostEqualTo(e, 1e-6)) return null;

            Element el = null;
            switch (t)
            {
                case TeeElementType.Pipe:
                    el = Pipe.Create(doc, systemTypeId, typeId, levelId, s, e);
                    if (el != null && dia > 0)
                        TrySetParam(el, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM, dia);
                    break;

                case TeeElementType.Duct:
                    el = Duct.Create(doc, systemTypeId, typeId, levelId, s, e);
                    if (el is Duct nd)
                    {
                        if (dia > 0) TrySetParam(nd, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM, dia);
                        if (w > 0) TrySetParam(nd, BuiltInParameter.RBS_CURVE_WIDTH_PARAM, w);
                        if (h > 0) TrySetParam(nd, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM, h);
                    }
                    break;
            }
            return el;
        }

        private void TrySetParam(Element e, BuiltInParameter bip, double val)
        {
            try
            {
                var p = e.get_Parameter(bip);
                if (p != null && !p.IsReadOnly && p.StorageType == StorageType.Double)
                    p.Set(val);
            }
            catch { }
        }

        private string Fmt2D(XYZ p) =>
            p == null ? "null" : $"({p.X:F3},{p.Y:F3})";

        private string FmtSize(double dia, double w, double h)
        {
            const double f = MEPTeeCalculator.FtToMm;
            if (w > 1e-9 && h > 1e-9)
                return $"{w * f:F0}x{h * f:F0}";
            return $"Ø{dia * f:F0}";
        }

        /// <summary>按连接器实际尺寸格式化（用于异常信息，反映调整后尺寸）</summary>
        private string FmtConnSize(Connector c)
        {
            const double f = MEPTeeCalculator.FtToMm;
            if (c == null) return "null";
            if (c.Shape == ConnectorProfileType.Rectangular)
                return $"{c.Width * f:F0}x{c.Height * f:F0}";
            return $"Ø{c.Radius * 2 * f:F0}";
        }
    }
}