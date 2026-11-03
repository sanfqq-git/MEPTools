using System;
using Autodesk.Revit.DB;
using MEPTools.Shared;
using Connector = Autodesk.Revit.DB.Connector;

namespace MEPTools.MEPPlanarConnector.Core
{
    public class PlanarConnectorService
    {
        private readonly MEPLogger _log;

        public PlanarConnectorService() : this(MEPLogger.Instance) { }
        public PlanarConnectorService(MEPLogger log) { _log = log ?? MEPLogger.Instance; }

        #region ==================== 共线连接 ====================

        public bool ProcessCollinear(Document doc, Element a, Element b)
        {
            // 【关键修复】：前置判断，若传入元素已经被删除（null），直接拦截
            if (a == null || b == null || !a.IsValidObject || !b.IsValidObject) 
            {
                _log.AppendLine("✗ [共线] 传入元素无效或已被删除");
                return false;
            }

            var g = PlanarConnectorCalculator.ComputeCollinear(a, b);
            if (!g.IsValid) { _log.AppendLine("✗ [共线] 计算失败: " + g.ErrorMessage); return false; }

            // 【核心修复】：在删除 b 之前，先保存好两者的 ID，后续日志只用 ID 变量，绝不访问 b.Id
            ElementId idA = a.Id;
            ElementId idB = b.Id;

            // 1. 核心逻辑：无条件尝试把 A 拉长覆盖 B，并删除 B（保留 A 组 ID）
            if (TryHardMerge(doc, a, b, g))
            {
                _log.AppendLine($"✓ [共线] 成功合并为一条管线: {idA} 吞噬 {idB}");
                return true;
            }

            // 2. 如果吞噬失败（例如 B 的远端连着其他配件，Revit 拒绝删除报 Error 1）
            // 退回“端点对齐 + 逻辑连接”，绝不生成任何配件。
            _log.AppendLine($"⚠ [共线] 强行吞噬失败 {idA} -> {idB}，退回端点对齐+逻辑连接模式。");
            return ProcessCollinearFallback(doc, a, b, g);
        }

        private bool TryHardMerge(Document doc, Element a, Element b, PlanarConnectGeometry g)
        {
            try
            {
                var lcA = a.Location as LocationCurve;
                var lineA = lcA?.Curve as Line;
                if (lineA == null) return false;

                XYZ a0 = lineA.GetEndPoint(0);
                XYZ a1 = lineA.GetEndPoint(1);

                // 找到 A 不需要移动的那一端（也就是原 A 的外侧端）
                XYZ preservedA = (g.PA.DistanceTo(a0) < 1e-3) ? a1 : a0;

                Line newLine;
                // 严格判断方向，生成与原拓扑一致的曲线，避免 Error 2 (反向报错)
                if (a0.DistanceTo(preservedA) < 1e-3)
                {
                    // A 的起点是固定端，那么把终点拉伸到 B 的远端
                    newLine = Line.CreateBound(a0, g.OuterPB);
                }
                else
                {
                    // A 的终点是固定端，那么把起点拉伸到 B 的远端
                    newLine = Line.CreateBound(g.OuterPB, a1);
                }

                // 暴力拉长 A
                lcA.Curve = newLine;
                doc.Regenerate();

                // 暴力删除 B
                doc.Delete(b.Id);
                doc.Regenerate();
                
                return true;
            }
            catch (Exception ex)
            {
                _log.AppendLine($"⚠ [共线] 吞噬发生异常: {ex.Message}");
                return false;
            }
        }

        private bool ProcessCollinearFallback(Document doc, Element a, Element b, PlanarConnectGeometry g)
        {
            // 1. 移动 A 端点使其与 B 完全重合
            if (g.PA.DistanceTo(g.PB) > 1e-4)
            {
                if (!MoveEndpointTo(doc, a, g.PA, g.PB))
                {
                    _log.AppendLine($"✗ [共线-退避] 端点对齐失败");
                    return false;
                }
            }
            doc.Regenerate();

            // 2. 获取端点连接件
            Connector ca = GetExactConnector(a, g.PB);
            Connector cb = GetExactConnector(b, g.PB);

            if (ca == null || cb == null) 
            {
                _log.AppendLine($"✗ [共线-退避] 无法获取端点连接件");
                return false;
            }
            if (ca.IsConnectedTo(cb)) return true;

            // 3. 直接调用 ConnectTo 进行底层逻辑连接 (绝不生成可视配件)
            try
            {
                ca.ConnectTo(cb);
                _log.AppendLine($"✓ [共线-退避] 底层逻辑连通成功");
                return true;
            }
            catch (Exception ex)
            {
                _log.AppendLine($"✗ [共线-退避] ConnectTo 失败: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region ==================== 转角连接 ====================

        public bool ProcessCorner(Document doc, Element a, Element b)
        {
            if (a == null || b == null || !a.IsValidObject || !b.IsValidObject) return false;

            var g = PlanarConnectorCalculator.ComputeCorner(a, b);
            if (!g.IsValid) { _log.AppendLine("✗ [转角] 计算失败: " + g.ErrorMessage); return false; }

            if (g.PA.DistanceTo(g.P) > 1e-4) MoveEndpointTo(doc, a, g.PA, g.P);
            if (g.PB.DistanceTo(g.P) > 1e-4) MoveEndpointTo(doc, b, g.PB, g.P);

            doc.Regenerate();

            Connector ca = GetExactConnector(a, g.P);
            Connector cb = GetExactConnector(b, g.P);

            if (ca == null || cb == null) 
            {
                _log.AppendLine("✗ [转角] 无法获取交点处连接件");
                return false;
            }
            if (ca.IsConnectedTo(cb)) return true;

            try
            {
                doc.Create.NewElbowFitting(ca, cb);
                _log.AppendLine($"✓ [转角] 弯头创建成功: {a.Id} -> {b.Id}");
                return true;
            }
            catch (Exception ex)
            {
                _log.AppendLine($"✗ [转角] 创建弯头失败: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region ==================== 公共辅助 ====================

        private bool MoveEndpointTo(Document doc, Element e, XYZ oldEndPt, XYZ newEndPt)
        {
            var mc = e as MEPCurve;
            var lc = mc?.Location as LocationCurve;
            var line = lc?.Curve as Line;
            if (line == null) return false;

            XYZ p0 = line.GetEndPoint(0);
            XYZ p1 = line.GetEndPoint(1);

            if (p0.DistanceTo(oldEndPt) < 1e-3) p0 = newEndPt;
            else if (p1.DistanceTo(oldEndPt) < 1e-3) p1 = newEndPt;
            else return false;

            if (p0.DistanceTo(p1) < 1e-4) return false;

            try
            {
                lc.Curve = Line.CreateBound(p0, p1);
                return true;
            }
            catch { return false; }
        }

        private Connector GetExactConnector(Element elem, XYZ pt)
        {
            var cm = GetConnectorManager(elem);
            if (cm == null) return null;

            foreach (Connector c in cm.Connectors)
            {
                if (c.Origin.DistanceTo(pt) < 1e-5) return c;
            }
            return null;
        }

        private static ConnectorManager GetConnectorManager(Element elem)
        {
            if (elem is MEPCurve mep) return mep.ConnectorManager;
            if (elem is FamilyInstance fi && fi.MEPModel != null) return fi.MEPModel.ConnectorManager;
            return null;
        }

        #endregion
    }
}