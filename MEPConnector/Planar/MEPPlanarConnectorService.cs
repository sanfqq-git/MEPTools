using System;
using System.Collections.Generic;
using System.Text;
using Autodesk.Revit.DB;
using MEPTools.Shared;
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
            return PlanarMEPAligner.AlignPair(doc, a, b);
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

            // 校验 A 内侧端（PA）是否连接了 A/B 以外的元素
            if (!IsInnerEndFree(a, b, g.PA, out string innerDetail))
            {
                _log.AppendLine($"✗ [共线] A({a.Id}) 内侧端已连接外部元素，合并后会断开；已跳过");
                _log.AppendLine("     " + innerDetail);
                return false;
            }

            ElementId idA = a.Id;
            ElementId idB = b.Id;

            if (TryHardMerge(doc, a, b, g))
            {
                _log.AppendLine($"✓ [共线] 成功合并为一条管线: {idA} 吞噬 {idB}");
                return true;
            }

            _log.AppendLine($"⚠ [共线] 强行吞噬失败 {idA} -> {idB}，退回端点对齐+逻辑连接模式。");
            return ProcessCollinearFallback(doc, a, b, g);
        }

        private bool IsInnerEndFree(Element host, Element guest, XYZ innerPt, out string detail)
        {
            detail = null;
            Connector c = GetExactConnector(host, innerPt);
            if (c == null)
            {
                detail = "未找到内侧端连接器（坐标偏移，保守放行）";
                return true;
            }
            if (!c.IsConnected) return true;

            bool hasExternal = false;
            var sb = new StringBuilder();
            foreach (Connector r in c.AllRefs)
            {
                if (r.Owner.Id != host.Id && r.Owner.Id != guest.Id)
                {
                    hasExternal = true;
                    sb.Append($" {r.Owner.Id}({r.Owner.GetType().Name})");
                }
            }

            if (hasExternal) detail = "已连接外部元素：" + sb.ToString();
            return !hasExternal;
        }

        private bool TryHardMerge(Document doc, Element a, Element b, PlanarConnectGeometry g)
        {
            try
            {
                var lcA = a.Location as LocationCurve;
                var lineA = lcA?.Curve as Line;
                if (lineA == null) return false;

                // 1. 备份 A 外侧端连接件
                Connector aOuterConn = GetExactConnector(a, g.OuterPA);
                List<Connector> aOuterRefs = new List<Connector>();
                if (aOuterConn != null && aOuterConn.IsConnected)
                {
                    foreach (Connector c in aOuterConn.AllRefs)
                        if (c.Owner.Id != a.Id) aOuterRefs.Add(c);
                }

                // 2. 备份 B 外侧端连接件
                Connector bOuterConn = GetExactConnector(b, g.OuterPB);
                List<Connector> bOuterRefs = new List<Connector>();
                if (bOuterConn != null && bOuterConn.IsConnected)
                {
                    foreach (Connector c in bOuterConn.AllRefs)
                        if (c.Owner.Id != b.Id) bOuterRefs.Add(c);
                }

                // 3. 断开 A 和 B 的所有连接（核心修复：避免修改曲线时触发 Revit 拓扑警告）
                DisconnectAll(a);
                DisconnectAll(b);

                // 4. 删除 B
                doc.Delete(b.Id);
                doc.Regenerate();

                // 5. 获取新的 A 管坐标，并防止起点/终点反转导致的错误
                XYZ a0 = lineA.GetEndPoint(0);
                XYZ a1 = lineA.GetEndPoint(1);
                
                // 判断原来的起点(0)是不是内侧点(g.PA)
                bool paIs0 = g.PA.DistanceTo(a0) < g.PA.DistanceTo(a1);
                
                XYZ newStart, newEnd;
                if (paIs0)
                {
                    // 如果原点是内侧，为了让新线不反转，我们用原来的外侧点(a1)作为起点
                    newStart = a1; // OuterPA
                    newEnd = g.OuterPB;
                }
                else
                {
                    // 如果原点已经是外侧，起点保持不变
                    newStart = a0; // OuterPA
                    newEnd = g.OuterPB;
                }

                double minLength = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);
                if (newStart.DistanceTo(newEnd) < minLength)
                {
                    _log.AppendLine("⚠ [共线] 合并后曲线长度过短，放弃硬合并");
                    return false;
                }

                Line newLine = Line.CreateBound(newStart, newEnd);
                lcA.Curve = newLine;
                doc.Regenerate();

                // 6. 恢复 A 的外侧连接
                if (aOuterRefs.Count > 0)
                {
                    Connector aNewOuter = GetExactConnector(a, g.OuterPA);
                    if (aNewOuter != null)
                    {
                        int rec = 0;
                        foreach (var r in aOuterRefs)
                        {
                            if (r.IsValidObject && !r.IsConnected)
                            {
                                try { aNewOuter.ConnectTo(r); rec++; } catch { }
                            }
                        }
                        if (rec > 0)
                            _log.AppendLine($"  [INFO] A 外侧端连接恢复 {rec}/{aOuterRefs.Count}");
                    }
                }

                // 7. 恢复 B 的外侧连接（接到 A 的新远端）
                if (bOuterRefs.Count > 0)
                {
                    Connector aFarConn = GetExactConnector(a, g.OuterPB);
                    if (aFarConn != null)
                    {
                        int rec = 0;
                        foreach (var target in bOuterRefs)
                        {
                            if (target.IsValidObject && !target.IsConnected)
                            {
                                try { aFarConn.ConnectTo(target); rec++; } catch { }
                            }
                        }
                        if (rec > 0)
                            _log.AppendLine($"  [INFO] B 外侧端连接恢复 {rec}/{bOuterRefs.Count}");
                    }
                    else
                    {
                        _log.AppendLine($"⚠ [共线] A 新远端未找到连接器，{bOuterRefs.Count} 个原始连接无法恢复");
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

        private void DisconnectAll(Element e)
        {
            var cm = GetConnectorManager(e);
            if (cm == null) return;

            foreach (Connector c in cm.Connectors)
            {
                if (c.IsConnected)
                {
                    // 拷贝一份 AllRefs，防止遍历时修改集合
                    var refs = new List<Connector>();
                    foreach (Connector r in c.AllRefs) refs.Add(r);

                    foreach (Connector r in refs)
                    {
                        if (r.Owner.Id != e.Id)
                        {
                            try { c.DisconnectFrom(r); } catch { }
                        }
                    }
                }
            }
        }

        private bool ProcessCollinearFallback(Document doc, Element a, Element b, PlanarConnectGeometry g)
        {
            Connector ca = GetExactConnector(a, g.PA);
            Connector cb = GetExactConnector(b, g.PB);

            if (ca == null || cb == null)
            {
                _log.AppendLine($"✗ [共线-退避] 无法获取端点连接件 (A@{g.PA} B@{g.PB})");
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

            // 1) 精确匹配
            foreach (Connector c in cm.Connectors)
            {
                if (c.Origin.DistanceTo(pt) < 1e-4) return c;
            }

            // 2) 最近兜底
            Connector best = null;
            double bestD = double.MaxValue;
            foreach (Connector c in cm.Connectors)
            {
                double d = c.Origin.DistanceTo(pt);
                if (d < bestD) { bestD = d; best = c; }
            }
            double tol = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);
            return (best != null && bestD < tol) ? best : null;
        }

        private static ConnectorManager GetConnectorManager(Element elem)
        {
            if (elem is MEPCurve mep) return mep.ConnectorManager;
            if (elem is FamilyInstance fi && fi.MEPModel != null) return fi.MEPModel.ConnectorManager;
            return null;
        }
    }
}