using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace MEPTools.MEPPlanarConnector.Core
{
    public enum MEPElementType { Pipe, Duct, CableTray, Unknown }

    public class PlanarConnectGeometry
    {
        public XYZ PA { get; set; }
        public XYZ PB { get; set; }
        public XYZ P { get; set; }
        public bool IsCollinear { get; set; }
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class ConnectorPair
    {
        public ElementId AId { get; set; }
        public ElementId BId { get; set; }
        public double Distance { get; set; }
    }

    public static class PlanarConnectorCalculator
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
            var lc = mc?.Location as LocationCurve;
            return lc?.Curve as Line;
        }

        public static ElementId GetSystemTypeId(Element elem, MEPElementType t)
        {
            if (t == MEPElementType.Pipe && elem is Pipe p)
                return p.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId() ?? ElementId.InvalidElementId;
            if (t == MEPElementType.Duct && elem is Duct d)
                return d.get_Parameter(BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM)?.AsElementId() ?? ElementId.InvalidElementId;
            return ElementId.InvalidElementId;
        }

        public static bool IsSameType(Element a, Element b)
        {
            return GetElementType(a) == GetElementType(b);
        }

        // ==========================================
        // 1. 计算共线
        // ==========================================
        public static PlanarConnectGeometry ComputeCollinear(Element a, Element b)
        {
            var g = new PlanarConnectGeometry { IsValid = false, IsCollinear = true };

            Line la = GetAxisLine(a);
            Line lb = GetAxisLine(b);
            if (la == null || lb == null) { g.ErrorMessage = "存在非直线段元素"; return g; }

            XYZ a0 = la.GetEndPoint(0), a1 = la.GetEndPoint(1);
            XYZ b0 = lb.GetEndPoint(0), b1 = lb.GetEndPoint(1);

            XYZ da = new XYZ(a1.X - a0.X, a1.Y - a0.Y, 0);
            XYZ db = new XYZ(b1.X - b0.X, b1.Y - b0.Y, 0);
            if (da.GetLength() < 1e-9 || db.GetLength() < 1e-9) { g.ErrorMessage = "存在竖直段，无法平面连接"; return g; }
            da = da.Normalize(); db = db.Normalize();

            double cross = da.X * db.Y - da.Y * db.X;
            if (Math.Abs(cross) > 1e-6) { g.ErrorMessage = "两管不平行，无法共线延长"; return g; }

            // 检查距离是否在同一条直线上
            XYZ bMid = (b0 + b1) * 0.5;
            XYZ perp = new XYZ(-da.Y, da.X, 0);
            double perpDist = Math.Abs((bMid - a0).DotProduct(perp));
            double tolFt = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);

            if (perpDist > tolFt) { g.ErrorMessage = $"两管平行但错开 {perpDist * FtToMm:F1}mm，无法连接"; return g; }

            // 找靠近彼此的端点
            XYZ aMid = (a0 + a1) * 0.5;
            g.PA = (a0.DistanceTo(bMid) < a1.DistanceTo(bMid)) ? a0 : a1;
            g.PB = (b0.DistanceTo(aMid) < b1.DistanceTo(aMid)) ? b0 : b1;
            g.P = g.PA;
            g.IsValid = true;
            return g;
        }

        // ==========================================
        // 2. 计算转角
        // ==========================================
        public static PlanarConnectGeometry ComputeCorner(Element a, Element b)
        {
            var g = new PlanarConnectGeometry { IsValid = false, IsCollinear = false };

            Line la = GetAxisLine(a);
            Line lb = GetAxisLine(b);
            if (la == null || lb == null) { g.ErrorMessage = "存在非直线段元素"; return g; }

            XYZ a0 = la.GetEndPoint(0), a1 = la.GetEndPoint(1);
            XYZ b0 = lb.GetEndPoint(0), b1 = lb.GetEndPoint(1);

            XYZ da = new XYZ(a1.X - a0.X, a1.Y - a0.Y, 0);
            XYZ db = new XYZ(b1.X - b0.X, b1.Y - b0.Y, 0);
            if (da.GetLength() < 1e-9 || db.GetLength() < 1e-9) { g.ErrorMessage = "存在竖直段，无法平面转角连接"; return g; }
            da = da.Normalize(); db = db.Normalize();

            double cross = da.X * db.Y - da.Y * db.X;
            if (Math.Abs(cross) < 1e-6) { g.ErrorMessage = "两管平行，无法转角连接"; return g; }

            // 交点计算
            XYZ v = new XYZ(b0.X - a0.X, b0.Y - a0.Y, 0);
            double det = da.X * (-db.Y) - (-db.X) * da.Y;
            if (Math.Abs(det) < 1e-9) { g.ErrorMessage = "交点计算奇异"; return g; }

            double tP = (v.X * (-db.Y) - (-db.X) * v.Y) / det;
            g.P = a0 + da * tP;

            // 找靠近交点的端点
            g.PA = (a0.DistanceTo(g.P) < a1.DistanceTo(g.P)) ? a0 : a1;
            g.PB = (b0.DistanceTo(g.P) < b1.DistanceTo(g.P)) ? b0 : b1;

            // 内部检查
            double tOnA = (g.P - a0).DotProduct(da) / (a1 - a0).GetLength();
            double tOnB = (g.P - b0).DotProduct(db) / (b1 - b0).GetLength();

            if (tOnA > 1e-4 && tOnA < 1 - 1e-4) { g.ErrorMessage = "交点落在 A 内部（不支持修剪）"; return g; }
            if (tOnB > 1e-4 && tOnB < 1 - 1e-4) { g.ErrorMessage = "交点落在 B 内部（不支持修剪）"; return g; }

            g.IsValid = true;
            return g;
        }

        // 修复：配对逻辑，改为按端点最近距离匹配
        public static List<ConnectorPair> MatchPairs(List<Element> aList, List<Element> bList)
        {
            var pairs = new List<ConnectorPair>();
            var usedB = new HashSet<ElementId>();

            foreach (var a in aList)
            {
                Line la = GetAxisLine(a);
                if (la == null) continue;

                Element bestB = null;
                double minDistance = double.MaxValue;

                foreach (var b in bList)
                {
                    if (usedB.Contains(b.Id)) continue;
                    Line lb = GetAxisLine(b);
                    if (lb == null) continue;

                    // 取两个轴线的四个端点间的最小距离，而不是中点距离
                    double d = Math.Min(
                        Math.Min(la.GetEndPoint(0).DistanceTo(lb.GetEndPoint(0)), la.GetEndPoint(0).DistanceTo(lb.GetEndPoint(1))),
                        Math.Min(la.GetEndPoint(1).DistanceTo(lb.GetEndPoint(0)), la.GetEndPoint(1).DistanceTo(lb.GetEndPoint(1)))
                    );

                    if (d < minDistance) { minDistance = d; bestB = b; }
                }

                if (bestB != null)
                {
                    usedB.Add(bestB.Id);
                    pairs.Add(new ConnectorPair { AId = a.Id, BId = bestB.Id, Distance = minDistance });
                }
            }
            return pairs;
        }
    }
}