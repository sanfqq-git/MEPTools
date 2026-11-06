using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace MEPTools.MEPConnector.HighLow
{
    public enum MEPElementType { Pipe, Duct, CableTray, Unknown }

    public class ConnectorPair
    {
        public ElementId AId { get; set; }
        public ElementId BId { get; set; }
        public double Distance { get; set; }
        public bool IsCollinear { get; set; }
    }

    public class PairGeometry
    {
        public XYZ PA { get; set; }
        public XYZ PB { get; set; }
        public XYZ BFarPt { get; set; }
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
    }

    public static class MEPConnectorCalculator
    {
        public const double FtToMm = 304.8;

        public static MEPElementType GetElementType(Element elem)
        {
            if (elem is Pipe) return MEPElementType.Pipe;
            if (elem is Duct) return MEPElementType.Duct;
            if (elem is CableTray) return MEPElementType.CableTray;
            return MEPElementType.Unknown;
        }

        public static Line GetAxisLine(Element e)
        {
            var mc = e as MEPCurve;
            if (mc == null) return null;
            var lc = mc.Location as LocationCurve;
            return lc?.Curve as Line;
        }

        public static bool IsHorizontallyCollinear(Element a, Element b, out string reason)
        {
            reason = null;
            Line la = GetAxisLine(a);
            Line lb = GetAxisLine(b);
            if (la == null || lb == null) { reason = "非直线段"; return false; }

            XYZ da = new XYZ(la.Direction.X, la.Direction.Y, 0);
            XYZ db = new XYZ(lb.Direction.X, lb.Direction.Y, 0);
            if (da.GetLength() < 1e-9 || db.GetLength() < 1e-9)
            { reason = "水平投影为零（竖直段不支持）"; return false; }
            da = da.Normalize();
            db = db.Normalize();

            double cross = da.X * db.Y - da.Y * db.X;
            if (Math.Abs(cross) > 1e-2) { reason = "两管水平投影不平行"; return false; }

            XYZ sa = la.GetEndPoint(0);
            XYZ sb = lb.GetEndPoint(0);
            XYZ ab = new XYZ(sb.X - sa.X, sb.Y - sa.Y, 0);
            double perpDist = Math.Abs(ab.X * da.Y - ab.Y * da.X);
            
            double tol = UnitUtils.ConvertToInternalUnits(50.0, UnitTypeId.Millimeters);
            if (perpDist > tol)
            {
                reason = string.Format("两管水平投影偏移 {0:F2}mm（不共线，容差50mm）", perpDist * FtToMm);
                return false;
            }
            return true;
        }

        /// <summary>
        /// 计算两管水平方向的实际偏移距离。若不平行返回 double.MaxValue。
        /// </summary>
        public static double GetHorizontalDeviation(Element a, Element b)
        {
            Line la = GetAxisLine(a);
            Line lb = GetAxisLine(b);
            if (la == null || lb == null) return double.MaxValue;

            XYZ da = new XYZ(la.Direction.X, la.Direction.Y, 0);
            XYZ db = new XYZ(lb.Direction.X, lb.Direction.Y, 0);
            if (da.GetLength() < 1e-9 || db.GetLength() < 1e-9) return double.MaxValue;
            da = da.Normalize();
            db = db.Normalize();

            double cross = da.X * db.Y - da.Y * db.X;
            if (Math.Abs(cross) > 1e-2) return double.MaxValue; // 不平行

            XYZ sa = la.GetEndPoint(0);
            XYZ sb = lb.GetEndPoint(0);
            XYZ ab = new XYZ(sb.X - sa.X, sb.Y - sa.Y, 0);
            return Math.Abs(ab.X * da.Y - ab.Y * da.X);
        }

        public static bool HasCompatibleSystemType(Element a, Element b, out string reason)
        {
            reason = null;
            var ta = GetElementType(a);
            var tb = GetElementType(b);
            if (ta != tb) { reason = string.Format("类型大类不一致：{0} vs {1}", ta, tb); return false; }
            if (ta == MEPElementType.CableTray) return true;

            ElementId sysA = GetSystemTypeId(a, ta);
            ElementId sysB = GetSystemTypeId(b, tb);
            
            if (sysA == ElementId.InvalidElementId || sysB == ElementId.InvalidElementId) return true;
            
            if (sysA != sysB) 
            {
                string nameA = a.Document.GetElement(sysA)?.Name ?? "未知";
                string nameB = b.Document.GetElement(sysB)?.Name ?? "未知";
                reason = $"System Type 不一致！A: '{nameA}' vs B: '{nameB}'"; 
                return false;
            }
            return true;
        }

        public static ElementId GetSystemTypeId(Element elem, MEPElementType t)
        {
            try
            {
                if (t == MEPElementType.Pipe && elem is Pipe p)
                {
                    var pp = p.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM);
                    return pp != null ? pp.AsElementId() : ElementId.InvalidElementId;
                }
                if (t == MEPElementType.Duct && elem is Duct d)
                {
                    var pp = d.get_Parameter(BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM);
                    return pp != null ? pp.AsElementId() : ElementId.InvalidElementId;
                }
            }
            catch { }
            return ElementId.InvalidElementId;
        }

        public static List<ConnectorPair> MatchPairs(List<Element> aList, List<Element> bList)
        {
            var candidates = new List<ConnectorPair>();
            foreach (var a in aList)
            {
                var typeA = GetElementType(a);
                var sysA = GetSystemTypeId(a, typeA);
                Line la = GetAxisLine(a);
                if (la == null) continue;
                XYZ ma = (la.GetEndPoint(0) + la.GetEndPoint(1)) * 0.5;

                foreach (var b in bList)
                {
                    var typeB = GetElementType(b);
                    var sysB = GetSystemTypeId(b, typeB);

                    if (sysA != ElementId.InvalidElementId && sysB != ElementId.InvalidElementId)
                    {
                        if (sysA != sysB) continue;
                    }

                    Line lb = GetAxisLine(b);
                    if (lb == null) continue;
                    XYZ mb = (lb.GetEndPoint(0) + lb.GetEndPoint(1)) * 0.5;

                    double d = new XYZ(ma.X - mb.X, ma.Y - mb.Y, 0).GetLength();
                    
                    string reason;
                    bool isCollinear = IsHorizontallyCollinear(a, b, out reason);

                    if (!isCollinear)
                    {
                        d += 100000.0; 
                    }

                    candidates.Add(new ConnectorPair 
                    { 
                        AId = a.Id, BId = b.Id, Distance = d, IsCollinear = isCollinear 
                    });
                }
            }

            candidates.Sort((x, y) => x.Distance.CompareTo(y.Distance));

            var pairs = new List<ConnectorPair>();
            var usedA = new HashSet<ElementId>();
            var usedB = new HashSet<ElementId>();
            foreach (var c in candidates)
            {
                if (usedA.Contains(c.AId) || usedB.Contains(c.BId)) continue;
                pairs.Add(c);
                usedA.Add(c.AId);
                usedB.Add(c.BId);
            }
            return pairs;
        }

        public static PairGeometry ComputeGeometry(Element a, Element b, double thetaDeg)
        {
            var g = new PairGeometry { IsValid = false };

            Line la = GetAxisLine(a);
            Line lb = GetAxisLine(b);
            if (la == null || lb == null) { g.ErrorMessage = "非直线段"; return g; }

            XYZ a0 = la.GetEndPoint(0);
            XYZ a1 = la.GetEndPoint(1);
            XYZ bMid = (lb.GetEndPoint(0) + lb.GetEndPoint(1)) * 0.5;
            XYZ pA = (a0.DistanceTo(bMid) < a1.DistanceTo(bMid)) ? a0 : a1;

            XYZ b0 = lb.GetEndPoint(0);
            XYZ b1 = lb.GetEndPoint(1);
            XYZ aMid = (a0 + a1) * 0.5;
            XYZ bNear = (b0.DistanceTo(aMid) < b1.DistanceTo(aMid)) ? b0 : b1;
            XYZ bFar  = (bNear.IsAlmostEqualTo(b0)) ? b1 : b0;

            XYZ axisDir = new XYZ(bFar.X - bNear.X, bFar.Y - bNear.Y, 0);
            if (axisDir.GetLength() < 1e-9) { g.ErrorMessage = "方向无法确定"; return g; }
            axisDir = axisDir.Normalize();

            XYZ toB = new XYZ(bMid.X - pA.X, bMid.Y - pA.Y, 0);
            if (toB.DotProduct(axisDir) < 0) axisDir = -axisDir;

            double zB = bNear.Z;
            double dZ = pA.Z - zB;
            double absDz = Math.Abs(dZ);
            if (absDz < 1e-6) { g.ErrorMessage = "A/B 组管高度相同，无法生成斜段或垂直段"; return g; }

            double dX;
            if (thetaDeg >= 89.9) 
            {
                dX = 0; 
            }
            else
            {
                double thetaRad = thetaDeg * Math.PI / 180.0;
                double tanTheta = Math.Tan(thetaRad);
                if (tanTheta < 1e-6) { g.ErrorMessage = "角度过小"; return g; }
                dX = absDz / tanTheta;
            }

            XYZ pB = new XYZ(pA.X + axisDir.X * dX, pA.Y + axisDir.Y * dX, zB);

            g.PA = pA;
            g.PB = pB;
            g.BFarPt = bFar;
            g.IsValid = true;
            return g;
        }

        public static string Fmt(XYZ p)
        {
            return p == null ? "null" : string.Format("({0:F3},{1:F3},{2:F3})", p.X, p.Y, p.Z);
        }
    }
}