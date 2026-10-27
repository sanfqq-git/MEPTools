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
    ///   3) 创建立管（X/Y = 交点，Z 从 A 中心线到 B 中心线）
    ///   4) A 三通 = A 左段 + A 右段 + 立管接 A 端
    ///   5) B 弯头 = 立管接 B 端 + B 近端连接器
    /// ⚠ 调用方必须已开启事务
    /// </summary>
    public class MEPTeeService
    {
        private readonly StringBuilder _log;
        private const double ConnectorTolMm = 50.0;

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
            // 3. 创建立管（交点处，Z 从 A 中心到 B 中心）
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

            // ============================================================
            // 4. 三通
            // ============================================================
            Connector caL = FindConnectorAtPoint(doc.GetElement(aLeft), crossPtA);
            Connector caR = FindConnectorAtPoint(doc.GetElement(aRight), crossPtA);
            Connector rTop = FindConnectorAtPoint(riser, riserTop);

            if (caL == null || caR == null || rTop == null)
                throw new InvalidOperationException(
                    $"A 三通连接器未找齐（左={caL != null}, 右={caR != null}, 立管={rTop != null}）");

            FamilyInstance tee = doc.Create.NewTeeFitting(caL, caR, rTop);
            if (tee == null)
                throw new InvalidOperationException("A 等径三通创建失败（缺族或规格不匹配）");
            doc.Regenerate();
            _log.AppendLine($"  三通：ID={tee.Id.IntegerValue}");

            // ============================================================
            // 5. 弯头
            // ============================================================
            Connector rBot = FindConnectorAtPoint(riser, riserBot);
            Connector bNear = FindConnectorAtPoint(doc.GetElement(p.IdB), crossPtB);

            if (rBot == null || bNear == null)
                throw new InvalidOperationException(
                    $"弯头连接器未找齐（立管底={rBot != null}, B 近端={bNear != null}）");

            FamilyInstance elbow = doc.Create.NewElbowFitting(rBot, bNear);
            if (elbow == null)
                throw new InvalidOperationException("弯头创建失败（缺族或规格不匹配）");
            doc.Regenerate();
            _log.AppendLine($"  弯头：ID={elbow.Id.IntegerValue}");

            _log.AppendLine($"  ✓ 系统 {p.SystemName} 处理完成");
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
    }
}