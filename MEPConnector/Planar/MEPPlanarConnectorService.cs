using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using MEPTools.Shared;
using MEPTools.MEPConnector.Shared;
using Connector = Autodesk.Revit.DB.Connector;

namespace MEPTools.MEPConnector.Planar.Core
{
    public class PlanarConnectorService
    {
        private readonly MEPLogger _log;

        public PlanarConnectorService() : this(MEPLogger.Instance) { }
        public PlanarConnectorService(MEPLogger log) { _log = log ?? MEPLogger.Instance; }

        public bool AlignPair(Document doc, Element a, Element b)
        {
            return MEPConnectorUtils.AlignPair(doc, a, b);
        }

        public bool ProcessCollinear(Document doc, Element a, Element b)
        {
            if (a == null || b == null || !a.IsValidObject || !b.IsValidObject) 
            {
                _log.AppendLine("✗ [共线] 传入元素无效或已被删除");
                return false;
            }

            var g = PlanarConnectorCalculator.ComputeCollinear(a, b);
            if (!g.IsValid) { _log.AppendLine("✗ [共线] 计算失败: " + g.ErrorMessage); return false; }

            ElementId idA = a.Id;
            ElementId idB = b.Id;

            // 前置：强行把 A 管的端点拉到与 B 管完全贴合（消除微小的计算误差）
            if (g.PA.DistanceTo(g.PB) > 1e-4)
            {
                MoveEndpointTo(doc, a, g.PA, g.PB);
                doc.Regenerate();
            }

            if (TryHardMerge(doc, a, b, g))
            {
                _log.AppendLine($"✓ [共线] 成功合并为一条管线: {idA} 吞噬 {idB}");
                return true;
            }

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

                // 【核心修复 1】：在删除 B 管前，记录下挂在 B 远端的所有连接头（比如截图里那个 45 度弯头）
                Connector bOuterConn = GetExactConnector(b, g.OuterPB);
                List<Connector> outerRefs = new List<Connector>();
                if (bOuterConn != null && bOuterConn.IsConnected)
                {
                    foreach (Connector c in bOuterConn.AllRefs)
                    {
                        if (c.Owner.Id != b.Id) outerRefs.Add(c);
                    }
                }

                XYZ a0 = lineA.GetEndPoint(0);
                XYZ a1 = lineA.GetEndPoint(1);
                XYZ preservedA = (g.PA.DistanceTo(a0) < 1e-3) ? a1 : a0;

                Line newLine;
                if (a0.DistanceTo(preservedA) < 1e-3)
                {
                    newLine = Line.CreateBound(a0, g.OuterPB);
                }
                else
                {
                    newLine = Line.CreateBound(g.OuterPB, a1);
                }

                // 1. A 管伸长取代 B 管
                lcA.Curve = newLine;
                doc.Regenerate();

                // 2. 删除 B 管（此时斜角弯头会掉落断开）
                doc.Delete(b.Id);
                doc.Regenerate();

                // 【核心修复 2】：让 A 管的远端去“接盘” B 管留下的接头
                if (outerRefs.Count > 0)
                {
                    Connector aOuterConn = GetExactConnector(a, g.OuterPB);
                    if (aOuterConn != null)
                    {
                        foreach (var target in outerRefs)
                        {
                            if (target.IsValidObject && !target.IsConnected)
                            {
                                try { aOuterConn.ConnectTo(target); } catch { }
                            }
                        }
                    }
                }
                
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
            Connector ca = GetExactConnector(a, g.PB);
            Connector cb = GetExactConnector(b, g.PB);

            if (ca == null || cb == null) 
            {
                _log.AppendLine($"✗ [共线-退避] 无法获取端点连接件");
                return false;
            }
            if (ca.IsConnectedTo(cb)) return true;

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
                // 将容差从 1e-5 放宽到 1e-4，防止坐标计算舍入误差导致找不到接头
                if (c.Origin.DistanceTo(pt) < 1e-4) return c; 
            }
            return null;
        }

        private static ConnectorManager GetConnectorManager(Element elem)
        {
            if (elem is MEPCurve mep) return mep.ConnectorManager;
            if (elem is FamilyInstance fi && fi.MEPModel != null) return fi.MEPModel.ConnectorManager;
            return null;
        }
    }
}