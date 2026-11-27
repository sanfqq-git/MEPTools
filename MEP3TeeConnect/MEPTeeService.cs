using System;
using System.Collections.Generic;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Electrical;

namespace MEPTools.MEP3TeeConnect
{
    /// <summary>
    /// 单对 T 形处理：
    ///   1) 修改 B 近端到交点
    ///   2) 打断 A 于交点
    ///   3) 竖直段（立管恒取 B 规格）
    ///   4) 三通（等径 = A 规格）
    ///   5) 大小头（唯一变径处，紧贴三通支管口）
    ///   6) 弯头（等径 = B 规格）
    ///
    /// ★ 异径路径采用"逐个尝试、失败即回滚重试"的阶梯，互不污染：
    ///      尝试 1：等径三通(A) + 极短 A 领口 + 大小头(贴住三通) + 立管(B)
    ///      尝试 2：降级 —— 异径三通（支管口 = B）+ 立管(B) 直连
    ///   每次尝试跑在一个独立子事务里，失败整体回滚，不影响其他系统。
    ///
    /// ⚠ 调用方必须已开启事务
    /// </summary>
    public class MEPTeeService
    {
        private StringBuilder _log;          // 尝试期间可切换为缓冲区
        private const double ConnectorTolMm = 50.0;
        private const double ParallelDotThreshold = 0.99;
        private const double SizeEpsilon = 1e-6;

        /// <summary>大小头必须吃掉大径侧的长度，这里额外留的"领口"（视觉上即为贴住）</summary>
        private const double StubMarginMm = 1.5;
        private const double ProbeSegMm = 200.0;

        private enum BuildMode
        {
            Equal,          // 等径：单立管 + 等径三通
            Stub,           // 异径首选：等径三通 + 极短 A 领口 + 大小头
            ReducingTee     // 异径降级：异径三通 + 立管直连
        }

        public MEPTeeService(StringBuilder log)
        {
            _log = log ?? new StringBuilder();
        }

        // =====================================================================
        // 入口
        // =====================================================================
        public void ProcessPair(Document doc, TeePair p)
        {
            if (doc.GetElement(p.IdA) == null || doc.GetElement(p.IdB) == null)
                throw new InvalidOperationException("元素已删除");

            bool reducing = IsReducing(p);

            _log.AppendLine($"  交点 (平面)：{Fmt2D(p.CrossPoint)}");
            _log.AppendLine($"  A 中心线 Z = {p.ZA * MEPTeeCalculator.FtToMm:F1}mm，" +
                            $"B 中心线 Z = {p.ZB * MEPTeeCalculator.FtToMm:F1}mm");
            _log.AppendLine($"  B 修改端点：{(p.ModifyB0 ? "b0" : "b1")}，" +
                            $"{(p.NeedExtend ? "延长" : "缩短")}");
            _log.AppendLine($"  A 规格（三通）：{FmtSize(p.DiameterA, p.WidthA, p.HeightA)}，" +
                            $"立管规格（取 B）：{FmtSize(p.DiameterB, p.WidthB, p.HeightB)}");

            string err1, err2;

            if (!reducing)
            {
                if (TryAttempt(doc, p, BuildMode.Equal, out err1)) return;
                throw new InvalidOperationException("等径连接失败：" + err1);
            }

            // ---- 异径：首选"大小头贴住三通" ----
            if (TryAttempt(doc, p, BuildMode.Stub, out err1)) return;

            // ---- 降级：异径三通 ----
            if (TryAttempt(doc, p, BuildMode.ReducingTee, out err2))
            {
                _log.AppendLine("  ⚠ 已降级为异径三通（变径并入三通支管口）");
                return;
            }

            throw new InvalidOperationException(
                "该规格组合无法建立三通/大小头。首选方案：" + err1 +
                "；降级方案：" + err2);
        }

        // =====================================================================
        // 尝试封装：独立子事务，失败整体回滚，细节只在成功时写入主日志
        // =====================================================================
        private bool TryAttempt(Document doc, TeePair p, BuildMode mode, out string err)
        {
            err = null;
            StringBuilder outer = _log;
            StringBuilder buf = new StringBuilder();
            _log = buf;

            SubTransaction st = new SubTransaction(doc);
            st.Start();
            try
            {
                BuildPair(doc, p, mode);
                st.Commit();
                outer.Append(buf.ToString());
                return true;
            }
            catch (Exception ex)
            {
                SafeRollback(st);
                err = ex.Message;
                outer.AppendLine($"  ✗ {ModeLabel(mode)} 失败：{ex.Message}");
                outer.Append(buf.ToString());     // 失败细节附在后面，便于排查
                return false;
            }
            finally
            {
                _log = outer;
            }
        }

        private void SafeRollback(SubTransaction st)
        {
            try
            {
                if (st != null && st.GetStatus() == TransactionStatus.Started)
                    st.RollBack();
            }
            catch { }
        }

        private string ModeLabel(BuildMode m)
        {
            switch (m)
            {
                case BuildMode.Stub: return "等径三通 + 大小头贴住三通";
                case BuildMode.ReducingTee: return "异径三通 + 立管直连";
                default: return "等径单立管 + 等径三通";
            }
        }

        // =====================================================================
        // 组装一整对
        // =====================================================================
        private void BuildPair(Document doc, TeePair p, BuildMode mode)
        {
            Element elemA = doc.GetElement(p.IdA);
            Element elemB = doc.GetElement(p.IdB);

            XYZ b0 = (elemB.Location as LocationCurve).Curve.GetEndPoint(0);
            XYZ b1 = (elemB.Location as LocationCurve).Curve.GetEndPoint(1);
            double bZ = (b0.Z + b1.Z) * 0.5;

            XYZ crossPtB = new XYZ(p.CrossPoint.X, p.CrossPoint.Y, bZ);
            XYZ crossPtA = new XYZ(p.CrossPoint.X, p.CrossPoint.Y, p.ZA);

            // ---- 1. 修改 B 的端点 ----
            ModifyBEndpoint(doc, p, crossPtB);
            doc.Regenerate();

            double newBLen = GetCurveLength(doc.GetElement(p.IdB));
            _log.AppendLine($"  B 修改后长度：" +
                            $"{newBLen * MEPTeeCalculator.FtToMm:F1}mm");
            if (newBLen < UnitUtils.ConvertToInternalUnits(
                MEPTeeCalculator.MinBLengthMm, UnitTypeId.Millimeters))
            {
                throw new InvalidOperationException(
                    $"B 修改后长度 {newBLen * MEPTeeCalculator.FtToMm:F1}mm 小于 " +
                    $"{MEPTeeCalculator.MinBLengthMm}mm，请调整选择");
            }

            // ---- 2. 线槽照搬手工拓扑：主管 A 通长不打断、不插三通/大小头 ----
            //   立管立在平面交点，顶端搭接主管（不建逻辑连接）；仅 B 侧立管 ↔ B 插等径弯头。
            if (p.ElemType == TeeElementType.CableTray)
            {
                BuildCableTrayDirect(doc, p, doc.GetElement(p.IdB), crossPtB);
                return;
            }

            // ---- 2. 打断 A 于交点 ----
            ElementId other = BreakCurve(doc, p.IdA, crossPtA, p.ElemType);
            if (other == null || other == ElementId.InvalidElementId)
                throw new InvalidOperationException("A 管打断失败");
            ElementId aLeft = p.IdA;
            ElementId aRight = other;
            doc.Regenerate();
            _log.AppendLine($"  A 打断：左段={aLeft.IntegerValue}，右段={aRight.IntegerValue}");

            // A 侧参考连接器（三通侧水平管宽边方向，用于立管截面朝向对齐）
            Connector aRef = FindConnectorAtPoint(doc.GetElement(aLeft), crossPtA)
                          ?? FindConnectorAtPoint(doc.GetElement(aRight), crossPtA);

            // ---- 3~5. 竖直段 + 三通 + 大小头 ----
            XYZ riserTop = new XYZ(p.CrossPoint.X, p.CrossPoint.Y, p.ZA);
            XYZ riserBot = new XYZ(p.CrossPoint.X, p.CrossPoint.Y, p.ZB);

            Element riser = null;
            FamilyInstance tee = null;

            switch (mode)
            {
                case BuildMode.Equal:
                    riser = CreateMEPCurve(doc, p.ElemType,
                        p.TypeId, p.LevelId, p.SystemTypeId,
                        p.DiameterB, p.WidthB, p.HeightB,
                        riserTop, riserBot);
                    if (riser == null) throw new InvalidOperationException("立管创建失败");
                    doc.Regenerate();
                    _log.AppendLine($"  立管：ID={riser.Id.IntegerValue}");

                    // 先对齐三通侧(A)截面朝向
                    AlignRiserToReference(doc, p, riser, riserTop, aRef, "三通侧(A)");

                    tee = CreateTee(doc, aLeft, aRight, crossPtA,
                        FindConnectorAtPoint(riser, riserTop), p);
                    doc.Regenerate();
                    _log.AppendLine($"  三通：ID={tee.Id.IntegerValue}，" +
                                    $"族={tee.Symbol?.Family?.Name}");
                    break;

                case BuildMode.Stub:
                    BuildVerticalWithTransition(doc, p, aLeft, aRight, crossPtA,
                        riserTop, riserBot, elemA, out riser, out tee);
                    break;

                case BuildMode.ReducingTee:
                    BuildVerticalWithReducingTee(doc, p, aLeft, aRight, crossPtA,
                        riserTop, riserBot, out riser, out tee);
                    break;
            }

            // ---- 6. 弯头（等径，B 规格）----
            // 诊断（只读，不改几何）：线槽垂直段可能被 Revit 旋转，记录实际截面便于排查
            const double f2 = MEPTeeCalculator.FtToMm;
            Connector rTopChk = FindConnectorAtPoint(riser, riserTop);
            if (rTopChk != null && rTopChk.Shape == ConnectorProfileType.Rectangular)
                _log.AppendLine($"  立管实际截面：{rTopChk.Width * f2:F0}x{rTopChk.Height * f2:F0}");

            Connector rBot = FindConnectorAtPoint(riser, riserBot);
            Connector bNear = FindConnectorAtPoint(doc.GetElement(p.IdB), crossPtB);
            if (rBot == null || bNear == null)
                throw new InvalidOperationException(
                    $"弯头连接器未找齐（立管底={rBot != null}, B 近端={bNear != null}）");

            // 两端校验：A 侧已先对齐，这里复核 B 侧弯头；不一致时以 B（刚性弯头）为准转正立管
            AlignRiserToReference(doc, p, riser, riserBot, bNear, "弯头侧(B)");
            doc.Regenerate();

            FamilyInstance elbow = doc.Create.NewElbowFitting(rBot, bNear);
            if (elbow == null)
                throw new InvalidOperationException(
                    "弯头创建失败。规格：立管 " + FmtConnSize(rBot) +
                    " / B " + FmtConnSize(bNear) +
                    "。两者均为 B 规格，请确认项目中有匹配的等径弯头族。");
            doc.Regenerate();
            _log.AppendLine($"  弯头：ID={elbow.Id.IntegerValue}，" +
                            $"族={elbow.Symbol?.Family?.Name}");
        }

        // =====================================================================
        // 线槽直连拓扑（照搬手工模型）：
        //   · 主管 A 通长保留，不打断、不插三通/大小头/底座等任何管件；
        //   · 立管立在平面交点，顶端搁在主管标高上（视觉搭接，连接器不逻辑连通）；
        //   · B 侧：立管底端 ↔ B 近端，按现有做法插等径弯头。
        // =====================================================================
        private void BuildCableTrayDirect(Document doc, TeePair p,
            Element elemB, XYZ crossPtB)
        {
            XYZ riserTop = new XYZ(p.CrossPoint.X, p.CrossPoint.Y, p.ZA);
            XYZ riserBot = new XYZ(p.CrossPoint.X, p.CrossPoint.Y, p.ZB);

            Element riser = CreateMEPCurve(doc, p.ElemType,
                p.TypeId, p.LevelId, p.SystemTypeId,
                p.DiameterB, p.WidthB, p.HeightB, riserTop, riserBot);
            if (riser == null)
                throw new InvalidOperationException("线槽立管创建失败");
            doc.Regenerate();
            _log.AppendLine($"  线槽立管：ID={riser.Id.IntegerValue}（顶端搭接主管，未插三通）");

            Connector rBot = FindConnectorAtPoint(riser, riserBot);
            Connector bNear = FindConnectorAtPoint(elemB, crossPtB);
            if (rBot == null || bNear == null)
                throw new InvalidOperationException(
                    $"弯头连接器未找齐（立管底={rBot != null}, B 近端={bNear != null}）");

            // 复核 B 侧截面朝向，不一致时转正立管（与风管同一套对齐逻辑）
            AlignRiserToReference(doc, p, riser, riserBot, bNear, "弯头侧(B)");
            doc.Regenerate();

            FamilyInstance elbow = doc.Create.NewElbowFitting(rBot, bNear);
            if (elbow == null)
                throw new InvalidOperationException(
                    "线槽弯头创建失败。规格：立管 " + FmtConnSize(rBot) +
                    " / B " + FmtConnSize(bNear) +
                    "。两者均为 B 规格，请确认项目中有匹配的线槽弯头族。");
            doc.Regenerate();
            _log.AppendLine($"  弯头：ID={elbow.Id.IntegerValue}，" +
                            $"族={elbow.Symbol?.Family?.Name}");
        }

        // =====================================================================
        // 异径首选：等径三通(A) + 极短 A 领口 + 大小头 + 立管(B)
        //   变径族自身有长度 L，Revit 插入时必须吃掉大径侧 L 的长度，
        //   所以 A 侧管段至少要留 L + 领口；领口取 1.5mm，视觉上即"贴住三通"。
        // =====================================================================
        private void BuildVerticalWithTransition(Document doc, TeePair p,
            ElementId aLeft, ElementId aRight, XYZ crossPtA,
            XYZ riserTop, XYZ riserBot, Element elemA,
            out Element riser, out FamilyInstance tee)
        {
            riser = null;
            tee = null;
            const double f = MEPTeeCalculator.FtToMm;

            double dirToA = p.ZA >= p.ZB ? 1.0 : -1.0;

            // 实测：大小头插入后吃掉大径侧多少长度
            double eaten = MeasureTransitionEaten(doc, p, dirToA);
            double eatenMm = Math.Max(0, eaten) * f;
            double stubMm = eatenMm + StubMarginMm;

            double hMm = Math.Abs(p.ZB - p.ZA) * f;
            if (stubMm > hMm - 50.0)
                throw new InvalidOperationException(
                    $"高差 {hMm:F1}mm 无法容纳 {stubMm:F1}mm 领口与立管");

            double stubLen = UnitUtils.ConvertToInternalUnits(stubMm, UnitTypeId.Millimeters);
            _log.AppendLine($"  大小头长度实测：{eatenMm:F1}mm，A 领口取 {stubMm:F1}mm");

            XYZ midPt = new XYZ(p.CrossPoint.X, p.CrossPoint.Y, p.ZA - dirToA * stubLen);

            // 3.1 A 领口（等径三通的支管必须是 A 规格）
            Element stub = CreateMEPCurve(doc, p.ElemType,
                p.TypeId, p.LevelId, p.SystemTypeId,
                p.DiameterA, p.WidthA, p.HeightA,
                riserTop, midPt);
            if (stub == null) throw new InvalidOperationException("A 领口创建失败");
            doc.Regenerate();
            _log.AppendLine($"  A 领口：ID={stub.Id.IntegerValue}，长度={stubMm:F1}mm");
            if (p.ElemType == TeeElementType.Duct)
                LogRectStubNote(stub, elemA, riserTop);

            // 3.2 立管（B 规格）
            riser = CreateMEPCurve(doc, p.ElemType,
                p.TypeId, p.LevelId, p.SystemTypeId,
                p.DiameterB, p.WidthB, p.HeightB,
                midPt, riserBot);
            if (riser == null) throw new InvalidOperationException("立管（B 规格）创建失败");
            doc.Regenerate();
            _log.AppendLine($"  立管（B 规格）：ID={riser.Id.IntegerValue}，" +
                            $"长度={midPt.DistanceTo(riserBot) * f:F1}mm");

            // 先对齐三通侧(A)截面朝向
            Connector aRefStub = FindConnectorAtPoint(doc.GetElement(aLeft), crossPtA)
                               ?? FindConnectorAtPoint(doc.GetElement(aRight), crossPtA);
            AlignRiserToReference(doc, p, riser, midPt, aRefStub, "三通侧(A)");

            // 4. 等径三通（A 左 + A 右 + A 领口）
            tee = CreateTee(doc, aLeft, aRight, crossPtA,
                FindConnectorAtPoint(stub, riserTop), p);
            doc.Regenerate();
            _log.AppendLine($"  三通：ID={tee.Id.IntegerValue}，" +
                            $"族={tee.Symbol?.Family?.Name}");

            // 5. 大小头
            Connector cStubBot = FindConnectorAtPoint(stub, midPt);
            Connector cRiserTop = FindConnectorAtPoint(riser, midPt);
            if (cStubBot == null || cRiserTop == null)
                throw new InvalidOperationException(
                    $"大小头连接器未找齐（A 领口={cStubBot != null}, 立管={cRiserTop != null}）");

            FamilyInstance trans = doc.Create.NewTransitionFitting(cStubBot, cRiserTop);
            if (trans == null)
                throw new InvalidOperationException(
                    "大小头创建失败。规格：A " + FmtConnSize(cStubBot) +
                    " → B " + FmtConnSize(cRiserTop) + "。");
            doc.Regenerate();
            _log.AppendLine($"  大小头：ID={trans.Id.IntegerValue}，" +
                            $"族={trans.Symbol?.Family?.Name}，类型={trans.Symbol?.Name}");

            // 实测：大小头大端与三通支管口的剩余间隙
            XYZ teePt = new XYZ(p.CrossPoint.X, p.CrossPoint.Y, p.ZA);
            Connector cBranch = FindFittingConnectorNear(tee, teePt);
            Connector cBig = FindFittingConnectorNear(trans, teePt);
            if (cBranch != null && cBig != null)
            {
                double gap = cBranch.Origin.DistanceTo(cBig.Origin) * f;
                _log.AppendLine($"  大小头大端距三通支管口：{gap:F1}mm" +
                                (gap <= 3.0 ? "（贴住）" : "（未贴住，请检查变径族长度）"));
            }
        }

        // =====================================================================
        // 异径降级：异径三通（支管口 = B）+ 立管(B) 直连
        // =====================================================================
        private void BuildVerticalWithReducingTee(Document doc, TeePair p,
            ElementId aLeft, ElementId aRight, XYZ crossPtA,
            XYZ riserTop, XYZ riserBot,
            out Element riser, out FamilyInstance tee)
        {
            riser = CreateMEPCurve(doc, p.ElemType,
                p.TypeId, p.LevelId, p.SystemTypeId,
                p.DiameterB, p.WidthB, p.HeightB,
                riserTop, riserBot);
            if (riser == null) throw new InvalidOperationException("立管（B 规格）创建失败");
            doc.Regenerate();
            _log.AppendLine($"  立管（B 规格）：ID={riser.Id.IntegerValue}，无 A 领口");

            // 先对齐三通侧(A)截面朝向
            Connector aRefRed = FindConnectorAtPoint(doc.GetElement(aLeft), crossPtA)
                              ?? FindConnectorAtPoint(doc.GetElement(aRight), crossPtA);
            AlignRiserToReference(doc, p, riser, riserTop, aRefRed, "三通侧(A)");

            tee = CreateTee(doc, aLeft, aRight, crossPtA,
                FindConnectorAtPoint(riser, riserTop), p);
            doc.Regenerate();
            _log.AppendLine($"  三通：ID={tee.Id.IntegerValue}，" +
                            $"族={tee.Symbol?.Family?.Name}" +
                            $"（支管口应为 {FmtSize(p.DiameterB, p.WidthB, p.HeightB)}）");
        }

        // =====================================================================
        // 探测：该规格能否插入大小头，并实测其"吃掉大径侧"的长度
        //   模型外 3 英尺处建一对临时试件，测完立即删除；失败即抛，交由上层换方案
        // =====================================================================
        private double MeasureTransitionEaten(Document doc, TeePair p, double dirToA)
        {
            double seg = UnitUtils.ConvertToInternalUnits(ProbeSegMm, UnitTypeId.Millimeters);

            XYZ midPtC = new XYZ(p.CrossPoint.X + 3.0, p.CrossPoint.Y, p.ZA);
            XYZ aEnd = new XYZ(midPtC.X, midPtC.Y, midPtC.Z + dirToA * seg);
            XYZ bEnd = new XYZ(midPtC.X, midPtC.Y, midPtC.Z - dirToA * seg);

            Element ta = null, tb = null;
            FamilyInstance tr = null;

            try
            {
                ta = CreateMEPCurve(doc, p.ElemType,
                    p.TypeId, p.LevelId, p.SystemTypeId,
                    p.DiameterA, p.WidthA, p.HeightA, aEnd, midPtC);
                tb = CreateMEPCurve(doc, p.ElemType,
                    p.TypeId, p.LevelId, p.SystemTypeId,
                    p.DiameterB, p.WidthB, p.HeightB, midPtC, bEnd);
                if (ta == null || tb == null)
                    throw new InvalidOperationException("试件创建失败");
                doc.Regenerate();

                Connector c1 = FindConnectorAtPoint(ta, midPtC);
                Connector c2 = FindConnectorAtPoint(tb, midPtC);
                if (c1 == null || c2 == null)
                    throw new InvalidOperationException("试件连接器未找到");

                tr = doc.Create.NewTransitionFitting(c1, c2);
                if (tr == null)
                    throw new InvalidOperationException("该规格无法插入变径");
                doc.Regenerate();

                // 找出"大径侧"连接器（按 A 规格匹配），它距交点的偏移即被吃掉的长度
                Connector big = FindBigEndConnector(tr, p);
                if (big == null)
                {
                    Connector[] cs = GetFittingConnectors(tr);
                    if (cs == null || cs.Length < 2)
                        throw new InvalidOperationException("变径连接器读取失败");
                    big = cs[0].Origin.DistanceTo(midPtC) > cs[1].Origin.DistanceTo(midPtC)
                        ? cs[0] : cs[1];
                }

                double d = (big.Origin - midPtC).DotProduct(new XYZ(0, 0, dirToA));
                if (d < 0) d = 0;      // 大端就在交点处，不需要吃掉大径侧
                return d;
            }
            finally
            {
                try
                {
                    List<ElementId> ids = new List<ElementId>(3);
                    if (tr != null && doc.GetElement(tr.Id) != null) ids.Add(tr.Id);
                    if (ta != null && doc.GetElement(ta.Id) != null) ids.Add(ta.Id);
                    if (tb != null && doc.GetElement(tb.Id) != null) ids.Add(tb.Id);
                    if (ids.Count > 0)
                    {
                        doc.Delete(ids);
                        doc.Regenerate();
                    }
                }
                catch { }
            }
        }

        /// <summary>变径的大端连接器：尺寸与 A 规格一致的那个</summary>
        private Connector FindBigEndConnector(Element trans, TeePair p)
        {
            Connector[] cs = GetFittingConnectors(trans);
            if (cs == null) return null;

            foreach (Connector c in cs)
            {
                if (c.Shape == ConnectorProfileType.Rectangular)
                {
                    if (Math.Abs(c.Width - p.WidthA) < 1e-3 &&
                        Math.Abs(c.Height - p.HeightA) < 1e-3) return c;
                }
                else
                {
                    if (p.DiameterA > 0 &&
                        Math.Abs(c.Radius * 2 - p.DiameterA) < 1e-3) return c;
                }
            }
            return null;
        }

        // =====================================================================
        // 三通创建
        // =====================================================================
        private FamilyInstance CreateTee(Document doc,
            ElementId aLeft, ElementId aRight, XYZ crossPtA, Connector branchConn, TeePair p)
        {
            Connector caL = FindConnectorAtPoint(doc.GetElement(aLeft), crossPtA);
            Connector caR = FindConnectorAtPoint(doc.GetElement(aRight), crossPtA);
            if (caL == null || caR == null || branchConn == null)
                throw new InvalidOperationException(
                    $"A 三通连接器未找齐（左={caL != null}, 右={caR != null}, " +
                    $"支管={branchConn != null}）");

            FamilyInstance tee = doc.Create.NewTeeFitting(caL, caR, branchConn);
            if (tee == null)
                throw new InvalidOperationException(
                    "A 三通创建失败（三通族不覆盖当前规格组合）。A=" +
                    FmtSize(p.DiameterA, p.WidthA, p.HeightA) +
                    "，支管=" + FmtConnSize(branchConn));
            return tee;
        }

        // =====================================================================
        // 连接器读取
        // =====================================================================
        private Connector[] GetFittingConnectors(Element fitting)
        {
            if (!(fitting is FamilyInstance fi)) return null;
            ConnectorManager cm = fi.MEPModel?.ConnectorManager;
            if (cm == null) return null;

            List<Connector> list = new List<Connector>(3);
            foreach (Connector c in cm.Connectors) list.Add(c);
            return list.Count > 0 ? list.ToArray() : null;
        }

        private Connector FindFittingConnectorNear(Element fitting, XYZ pt)
        {
            Connector[] cs = GetFittingConnectors(fitting);
            if (cs == null) return null;

            Connector best = null;
            double bd = double.MaxValue;
            foreach (Connector c in cs)
            {
                double d = c.Origin.DistanceTo(pt);
                if (d < bd) { bd = d; best = c; }
            }
            return best;
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

        // =====================================================================
        // 矩形风管领口：仅诊断记录，不做几何强改（改任何一边都会让三通变异径/扭曲）
        // =====================================================================
        private void LogRectStubNote(Element stub, Element elemA, XYZ stubTop)
        {
            Connector c = FindConnectorAtPoint(stub, stubTop);
            if (c == null || c.Shape != ConnectorProfileType.Rectangular) return;

            const double f = MEPTeeCalculator.FtToMm;
            double w = c.Width, h = c.Height;

            XYZ axisA = GetAxisDirection(elemA);
            string orient = "未知";
            if (axisA != null && c.CoordinateSystem != null && w > SizeEpsilon && h > SizeEpsilon)
            {
                XYZ longDir = (w >= h) ? c.CoordinateSystem.BasisX : c.CoordinateSystem.BasisY;
                XYZ flat = new XYZ(longDir.X, longDir.Y, 0);
                if (flat.GetLength() > 1e-6)
                {
                    double dot = Math.Abs(flat.Normalize().DotProduct(axisA));
                    orient = dot > ParallelDotThreshold ? "长边 ∥ A 轴" : "长边 ⟂ A 轴";
                }
            }
            _log.AppendLine($"    领口截面：{w * f:F0}x{h * f:F0}（矩形，{orient}）");
        }

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
        // 矩形立管截面朝向对齐（防弯头/三通处出现扭曲大小头）
        //   水平管宽边在水平面内垂直于管轴；立管竖直走向，两个截面边都在平面内，
        //   Duct.Create 默认朝向不跟随参考管，错 90° 时弯头两侧宽高对不上，
        //   Revit 只能退化成扭转变径。这里按参考管宽边(BasisX)把立管绕竖轴转正。
        //   圆形 / 正方形 / 读不到朝向 → 跳过；旋转失败只记日志，不阻断连接。
        // =====================================================================
        private void AlignRiserToReference(Document doc, TeePair p, Element riser,
            XYZ endPt, Connector reference, string sideLabel)
        {
            if (riser == null || reference == null) return;
            if (p.ElemType == TeeElementType.Pipe) return;

            var line = (riser.Location as LocationCurve)?.Curve as Line;
            if (line == null || Math.Abs(line.Direction.Z) <= 0.99) return;

            doc.Regenerate();

            Connector rConn = FindConnectorAtPoint(riser, endPt);
            if (rConn == null || rConn.Shape != ConnectorProfileType.Rectangular) return;
            if (reference.Shape != ConnectorProfileType.Rectangular) return;
            if (rConn.Width <= SizeEpsilon || rConn.Height <= SizeEpsilon) return;
            if (Math.Abs(rConn.Width - rConn.Height) < SizeEpsilon) return;   // 正方形无朝向

            XYZ vecRef = ProjectPlanar(reference.CoordinateSystem?.BasisX);
            XYZ vecRiser = ProjectPlanar(rConn.CoordinateSystem?.BasisX);
            if (vecRef == null || vecRiser == null) return;

            double angle = PlanarSignedAngle(vecRef, vecRiser);
            const double tolRad = Math.PI / 180.0;
            if (Math.Abs(angle) <= tolRad)
            {
                _log.AppendLine($"  立管{sideLabel}截面朝向已顺拐（差 {Math.Abs(angle) * 180.0 / Math.PI:F1}°）");
                return;
            }

            try
            {
                Line axis = Line.CreateUnbound(endPt, XYZ.BasisZ);
                ElementTransformUtils.RotateElement(doc, riser.Id, axis, angle);
                doc.Regenerate();
                _log.AppendLine($"  ✓ 立管{sideLabel}截面已旋转 {angle * 180.0 / Math.PI:F1}° 对齐参考管宽边");
            }
            catch (Exception ex)
            {
                _log.AppendLine($"  ✗ 立管{sideLabel}截面旋转失败（已跳过，不阻断）：{ex.Message}");
            }
        }

        /// <summary>取向量水平面投影并归一化；接近竖直时返回 null</summary>
        private static XYZ ProjectPlanar(XYZ v)
        {
            if (v == null) return null;
            XYZ f = new XYZ(v.X, v.Y, 0);
            return f.GetLength() > 1e-6 ? f.Normalize() : null;
        }

        /// <summary>平面内从 b(当前) 转到 a(目标) 的有符号角（弧度，绕 Z 右手系）</summary>
        private static double PlanarSignedAngle(XYZ a, XYZ b)
        {
            double dot = a.DotProduct(b);
            double cross = a.X * b.Y - a.Y * b.X;
            double ang = Math.Acos(Math.Max(-1.0, Math.Min(1.0, dot)));
            return cross < 0 ? -ang : ang;
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

            XYZ newP0 = p.ModifyB0 ? new XYZ(newEndPoint.X, newEndPoint.Y, p0.Z) : p0;
            XYZ newP1 = p.ModifyB0 ? p1 : new XYZ(newEndPoint.X, newEndPoint.Y, p1.Z);

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
            // 线槽没有 BreakCurve，走"新建远段 + 缩回原段"的手动打断
            if (t == TeeElementType.CableTray)
                return SplitCableTray(doc, elemId, breakPt);
            return ElementId.InvalidElementId;
        }

        // =====================================================================
        // ★ 线槽打断：Revit 没有 CableTray 的 BreakCurve，只能手工拆
        //   原段缩回 [p0, cut]，另建一段 [cut, p1]（同类型/同标高/同宽高）
        // =====================================================================
        private ElementId SplitCableTray(Document doc, ElementId trayId, XYZ breakPt)
        {
            if (!(doc.GetElement(trayId) is CableTray tray))
                return ElementId.InvalidElementId;
            if (!(tray.Location is LocationCurve lc) || !(lc.Curve is Line line))
                return ElementId.InvalidElementId;

            XYZ p0 = line.GetEndPoint(0);
            XYZ p1 = line.GetEndPoint(1);
            XYZ dir = (p1 - p0);
            double len = dir.GetLength();
            if (len < 1e-6) return ElementId.InvalidElementId;
            dir = dir / len;

            // 交点投影到轴线，保证断开点严格落在线上
            double t = (breakPt - p0).DotProduct(dir);
            XYZ cut = p0 + dir * t;
            if (t < 1e-6 || t > len - 1e-6) return ElementId.InvalidElementId;

            double w = ReadDoubleParam(tray, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
            double h = ReadDoubleParam(tray, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
            ElementId typeId = tray.GetTypeId();

            ElementId levelId = tray.LevelId;
            if (levelId == ElementId.InvalidElementId)
                levelId = tray.get_Parameter(BuiltInParameter.RBS_START_LEVEL_PARAM)?.AsElementId()
                          ?? ElementId.InvalidElementId;

            CableTray nt = CableTray.Create(doc, typeId, cut, p1, levelId);
            if (nt == null) return ElementId.InvalidElementId;

            if (w > 0) TrySetParam(nt, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM, w);
            if (h > 0) TrySetParam(nt, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM, h);

            // 原段缩回到断开点（保持 p0 → cut 的方向，不翻转朝向）
            lc.Curve = Line.CreateBound(p0, cut);
            doc.Regenerate();

            return nt.Id;
        }

        private double ReadDoubleParam(Element e, BuiltInParameter bip)
        {
            try
            {
                var p = e.get_Parameter(bip);
                if (p != null && p.StorageType == StorageType.Double) return p.AsDouble();
            }
            catch { }
            return 0;
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

                case TeeElementType.CableTray:
                    // 线槽创建不传系统类型，宽高用线槽自己的参数
                    el = CableTray.Create(doc, typeId, s, e, levelId);
                    if (el != null)
                    {
                        if (w > 0) TrySetParam(el, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM, w);
                        if (h > 0) TrySetParam(el, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM, h);
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

        // =====================================================================
        // 输出格式
        // =====================================================================
        private bool IsReducing(TeePair p)
        {
            if (Math.Abs(p.DiameterA - p.DiameterB) > SizeEpsilon) return true;
            if (Math.Abs(p.WidthA - p.WidthB) > SizeEpsilon) return true;
            if (Math.Abs(p.HeightA - p.HeightB) > SizeEpsilon) return true;
            return false;
        }

        private string Fmt2D(XYZ p) =>
            p == null ? "null" : $"({p.X:F3},{p.Y:F3})";

        private string FmtSize(double dia, double w, double h)
        {
            const double f = MEPTeeCalculator.FtToMm;
            if (w > 1e-9 && h > 1e-9) return $"{w * f:F0}x{h * f:F0}";
            return $"Ø{dia * f:F0}";
        }

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
