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

        public bool ProcessCollinear(Document doc, Element a, Element b)
        {
            var g = PlanarConnectorCalculator.ComputeCollinear(a, b);
            if (!g.IsValid) { _log.AppendLine("✗ " + g.ErrorMessage); return false; }

            if (g.PA.DistanceTo(g.PB) < 1e-4) return true; // 已重合

            if (!MoveEndpointTo(doc, a, g.PA, g.PB))
            {
                _log.AppendLine("✗ 延伸失败");
                return false;
            }
            _log.AppendLine($"✓ 共线连接成功: {a.Id} -> {b.Id}");
            return true;
        }

        public bool ProcessCorner(Document doc, Element a, Element b)
        {
            var g = PlanarConnectorCalculator.ComputeCorner(a, b);
            if (!g.IsValid) { _log.AppendLine("✗ " + g.ErrorMessage); return false; }

            // 移动 A 的端点到交点 (自动判定是延长还是修剪)
            if (g.PA.DistanceTo(g.P) > 1e-4)
            {
                if (!MoveEndpointTo(doc, a, g.PA, g.P)) 
                {
                    _log.AppendLine($"✗ 移动 A({a.Id}) 失败");
                    return false; 
                }
            }
            
            // 移动 B 的端点到交点
            if (g.PB.DistanceTo(g.P) > 1e-4)
            {
                if (!MoveEndpointTo(doc, b, g.PB, g.P)) 
                {
                    _log.AppendLine($"✗ 移动 B({b.Id}) 失败");
                    return false;
                }
            }

            doc.Regenerate();
            // 放弯头
            return TryCreateElbow(doc, a.Id, b.Id, g.P);
        }

        /// <summary>
        /// 把元素的端点移动到新位置（支持延长和修剪）
        /// </summary>
        private bool MoveEndpointTo(Document doc, Element e, XYZ oldEndPt, XYZ newEndPt)
        {
            var mc = e as MEPCurve;
            var lc = mc?.Location as LocationCurve;
            var line = lc?.Curve as Line;
            if (line == null) return false;

            XYZ p0 = line.GetEndPoint(0);
            XYZ p1 = line.GetEndPoint(1);

            const double tol = 1e-3;
            XYZ newP0 = p0, newP1 = p1;
            
            if (p0.DistanceTo(oldEndPt) < tol) newP0 = newEndPt;
            else if (p1.DistanceTo(oldEndPt) < tol) newP1 = newEndPt;
            else return false;

            // 防止长度变为 0
            if (newP0.DistanceTo(newP1) < 1e-4) return false;

            try
            {
                lc.Curve = Line.CreateBound(newP0, newP1);
                doc.Regenerate();
                return true;
            }
            catch { return false; }
        }

        private bool TryCreateElbow(Document doc, ElementId idA, ElementId idB, XYZ atPt)
        {
            var elA = doc.GetElement(idA);
            var elB = doc.GetElement(idB);
            if (elA == null || elB == null) return false;

            Connector ca = GetConnectorAtPoint(elA, atPt);
            Connector cb = GetConnectorAtPoint(elB, atPt);

            if (ca == null || cb == null)
            {
                FindClosestConnectorPair(elA, elB, out ca, out cb, out _);
                if (ca == null || cb == null) return false;
            }

            try
            {
                FamilyInstance fit = doc.Create.NewElbowFitting(ca, cb);
                if (fit != null)
                {
                    _log.AppendLine($"✓ 转角连接成功，弯头 ID={fit.Id}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _log.AppendLine($"✗ 创建弯头失败: {ex.Message}");
            }
            return false;
        }

        private Connector GetConnectorAtPoint(Element elem, XYZ pt)
        {
            var cm = GetConnectorManager(elem);
            if (cm == null) return null;
            double tol = UnitUtils.ConvertToInternalUnits(20.0, UnitTypeId.Millimeters);
            Connector best = null;
            double bestD = double.MaxValue;
            foreach (Connector c in cm.Connectors)
            {
                double d = c.Origin.DistanceTo(pt);
                if (d < bestD) { bestD = d; best = c; }
            }
            return (best != null && bestD < tol) ? best : null;
        }

        private void FindClosestConnectorPair(Element a, Element b, out Connector ca, out Connector cb, out double distance)
        {
            ca = null; cb = null; distance = double.MaxValue;
            var cma = GetConnectorManager(a);
            var cmb = GetConnectorManager(b);
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

        private static ConnectorManager GetConnectorManager(Element elem)
        {
            if (elem is MEPCurve mep) return mep.ConnectorManager;
            if (elem is FamilyInstance fi && fi.MEPModel != null) return fi.MEPModel.ConnectorManager;
            return null;
        }
    }
}