using System;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace MEPTools.MEP3TeeCrossConnect
{
    /// <summary>
    /// 单对处理流程（NewTeeFitting 需 3 个连接器）：
    ///   1) 先在两断点之间创建立管（此时立管两端各有 1 个未连接连接器）
    ///   2) 打断高组管、低组管
    ///   3) NewTeeFitting(hA, hB, riserTop) → 高组三通
    ///   4) NewTeeFitting(lA, lB, riserBottom) → 低组三通
    /// ⚠ 调用方必须已开启事务
    /// </summary>
    public class MEPCrossService
    {
        private readonly StringBuilder _log;
        private const double ConnectorTolMm = 50.0;

        public MEPCrossService(StringBuilder log)
        {
            _log = log ?? new StringBuilder();
        }

        public void ProcessPair(Document doc, CrossPair pair)
        {
            Element highElem = doc.GetElement(pair.HighId);
            Element lowElem = doc.GetElement(pair.LowId);
            if (highElem == null || lowElem == null)
                throw new InvalidOperationException("元素已删除");

            XYZ highBreakPt = new XYZ(pair.CrossPoint.X, pair.CrossPoint.Y, pair.HighZ);
            XYZ lowBreakPt = new XYZ(pair.CrossPoint.X, pair.CrossPoint.Y, pair.LowZ);

            _log.AppendLine($"  高组打断点：{MEPCrossCalculator.Fmt(highBreakPt)}");
            _log.AppendLine($"  低组打断点：{MEPCrossCalculator.Fmt(lowBreakPt)}");

            // ============================================================
            // 1. 先创建立管（两端连接器分别位于两个打断点上）
            //    ⚠ 必须在打断之前，后续 NewTeeFitting 才能拿到支管连接器
            // ============================================================
            Element riser = CreateMEPCurve(doc, pair.ElemType,
                pair.HighTypeId, pair.HighLevelId, pair.SystemTypeId,
                pair.Diameter, pair.Width, pair.Height,
                highBreakPt, lowBreakPt);
            if (riser == null)
                throw new InvalidOperationException(
                    $"立管创建失败（Z: {pair.LowZ * MEPCrossCalculator.FtToMm:F0} → " +
                    $"{pair.HighZ * MEPCrossCalculator.FtToMm:F0} mm，" +
                    $"D/W/H=({pair.Diameter * MEPCrossCalculator.FtToMm:F1}/" +
                    $"{pair.Width * MEPCrossCalculator.FtToMm:F1}/" +
                    $"{pair.Height * MEPCrossCalculator.FtToMm:F1})mm）");
            doc.Regenerate();
            _log.AppendLine($"  立管：ID={riser.Id.IntegerValue}，长度=" +
                $"{Math.Abs(pair.HighZ - pair.LowZ) * MEPCrossCalculator.FtToMm:F1}mm");

            // ============================================================
            // 2. 打断高组管
            // ============================================================
            ElementId highOther = BreakCurve(doc, pair.HighId, highBreakPt, pair.ElemType);
            if (highOther == null || highOther == ElementId.InvalidElementId)
                throw new InvalidOperationException("高组管打断失败");
            doc.Regenerate();

            // ============================================================
            // 3. 打断低组管
            // ============================================================
            ElementId lowOther = BreakCurve(doc, pair.LowId, lowBreakPt, pair.ElemType);
            if (lowOther == null || lowOther == ElementId.InvalidElementId)
                throw new InvalidOperationException("低组管打断失败");
            doc.Regenerate();

            // ============================================================
            // 4. 高组三通 = hA + hB + 立管顶端
            // ============================================================
            Connector hA = FindConnectorAtPoint(doc.GetElement(pair.HighId), highBreakPt);
            Connector hB = FindConnectorAtPoint(doc.GetElement(highOther), highBreakPt);
            Connector rT = FindConnectorAtPoint(riser, highBreakPt);

            if (hA == null || hB == null || rT == null)
                throw new InvalidOperationException(
                    $"高组连接器未找齐（子管A={hA != null}, 子管B={hB != null}, 立管顶={rT != null}）");

            FamilyInstance teeH = doc.Create.NewTeeFitting(hA, hB, rT);
            if (teeH == null)
                throw new InvalidOperationException(
                    $"高组三通创建失败。高组管径(等效)={pair.DiameterHigh * MEPCrossCalculator.FtToMm:F1}mm，" +
                    $"立管管径={pair.Diameter * MEPCrossCalculator.FtToMm:F1}mm，" +
                    $"立管尺寸 W×H={pair.Width * MEPCrossCalculator.FtToMm:F1}×" +
                    $"{pair.Height * MEPCrossCalculator.FtToMm:F1}mm。" +
                    (pair.ElemType == CrossElementType.Pipe
                        ? "请确认项目已载入对应规格的三通/变径三通族。"
                        : "请确认项目已载入对应规格的风管三通/变径三通族，且立管形状与主管一致。"));
            doc.Regenerate();
            _log.AppendLine($"  高组三通：ID={teeH.Id.IntegerValue}");

            // ============================================================
            // 5. 低组三通 = lA + lB + 立管底端
            // ============================================================
            Connector lA = FindConnectorAtPoint(doc.GetElement(pair.LowId), lowBreakPt);
            Connector lB = FindConnectorAtPoint(doc.GetElement(lowOther), lowBreakPt);
            Connector rB = FindConnectorAtPoint(riser, lowBreakPt);

            if (lA == null || lB == null || rB == null)
                throw new InvalidOperationException(
                    $"低组连接器未找齐（子管A={lA != null}, 子管B={lB != null}, 立管底={rB != null}）");

            FamilyInstance teeL = doc.Create.NewTeeFitting(lA, lB, rB);
            if (teeL == null)
                throw new InvalidOperationException(
                    $"低组三通创建失败。低组管径(等效)={pair.DiameterLow * MEPCrossCalculator.FtToMm:F1}mm，" +
                    $"立管管径={pair.Diameter * MEPCrossCalculator.FtToMm:F1}mm，" +
                    $"立管尺寸 W×H={pair.Width * MEPCrossCalculator.FtToMm:F1}×" +
                    $"{pair.Height * MEPCrossCalculator.FtToMm:F1}mm。" +
                    (pair.ElemType == CrossElementType.Pipe
                        ? "请确认项目已载入对应规格的三通/变径三通族。"
                        : "请确认项目已载入对应规格的风管三通/变径三通族，且立管形状与主管一致。"));
            doc.Regenerate();
            _log.AppendLine($"  低组三通：ID={teeL.Id.IntegerValue}");

            _log.AppendLine($"  ✓ 系统 {pair.SystemName} 连接完成");
        }

        private ElementId BreakCurve(Document doc, ElementId elemId, XYZ breakPt, CrossElementType t)
        {
            if (t == CrossElementType.Pipe)
                return PlumbingUtils.BreakCurve(doc, elemId, breakPt);
            return MechanicalUtils.BreakCurve(doc, elemId, breakPt);
        }

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

        private Element CreateMEPCurve(Document doc, CrossElementType t,
            ElementId typeId, ElementId levelId, ElementId systemTypeId,
            double dia, double w, double h, XYZ s, XYZ e)
        {
            if (s.IsAlmostEqualTo(e, 1e-6)) return null;

            Element el = null;
            switch (t)
            {
                case CrossElementType.Pipe:
                    el = Pipe.Create(doc, systemTypeId, typeId, levelId, s, e);
                    if (el != null && dia > 0)
                        TrySetParam(el, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM, dia);
                    break;

                case CrossElementType.Duct:
                    el = Duct.Create(doc, systemTypeId, typeId, levelId, s, e);
                    if (el is Duct nd)
                    {
                        // 立管形状：根据尺寸判断是矩形还是圆形
                        // 矩形：w、h 都 > 0 → 只设宽高，不设直径
                        // 圆形：dia > 0，w=h=0 → 只设直径
                        bool isRect = w > 1e-9 && h > 1e-9;
                        if (isRect)
                        {
                            TrySetParam(nd, BuiltInParameter.RBS_CURVE_WIDTH_PARAM, w);
                            TrySetParam(nd, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM, h);
                        }
                        else if (dia > 0)
                        {
                            TrySetParam(nd, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM, dia);
                        }
                    }
                    break;
            }
            return el;
        }

        /// <summary>安全设置参数：参数不存在或只读时静默跳过</summary>
        private void TrySetParam(Element e, BuiltInParameter bip, double val)
        {
            try
            {
                var p = e.get_Parameter(bip);
                if (p != null && !p.IsReadOnly && p.StorageType == StorageType.Double)
                    p.Set(val);
            }
            catch { /* 忽略 */ }
        }
    }
}