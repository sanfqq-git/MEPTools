using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace MEPTools.MEPConnector.Planar.Core
{
    public enum MEPElementType { Pipe, Duct, CableTray, Unknown }

    public class PlanarConnectGeometry
    {
        public XYZ PA { get; set; }
        public XYZ PB { get; set; }
        public XYZ OuterPA { get; set; }
        public XYZ OuterPB { get; set; }
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

            // 【修复】放宽平行度判断
            double cross = da.X * db.Y - da.Y * db.X;
            if (Math.Abs(cross) > 1e-2) return double.MaxValue;

            XYZ sa = la.GetEndPoint(0);
            XYZ sb = lb.GetEndPoint(0);
            XYZ ab = new XYZ(sb.X - sa.X, sb.Y - sa.Y, 0);
            return Math.Abs(ab.X * da.Y - ab.Y * da.X);
        }

        public static PlanarConnectGeometry ComputeCollinear(Element a, Element b)
        {
            var g = new PlanarConnectGeometry { IsValid = false, IsCollinear = true };

            Line la = GetAxisLine(a);
            Line lb = GetAxisLine(b);
            if (la == null || lb == null) { g.ErrorMessage = "存在非直线段元素"; return g; }

            XYZ a0 = la.GetEndPoint(0), a1 = la.GetEndPoint(1);
            XYZ b0 = lb.GetEndPoint(0), b1 = lb.GetEndPoint(1);

            if (Math.Abs(a0.Z - b0.Z) > 1e-3)
            {
                g.ErrorMessage = "两管标高不一致，无法平面共线合并";
                return g;
            }

            XYZ da = new XYZ(a1.X - a0.X, a1.Y - a0.Y, 0);
            XYZ db = new XYZ(b1.X - b0.X, b1.Y - b0.Y, 0);
            if (da.GetLength() < 1e-9 || db.GetLength() < 1e-9) { g.ErrorMessage = "存在竖直段，无法平面连接"; return g; }
            da = da.Normalize(); db = db.Normalize();

            // 【修复】放宽平行度判断
            double cross = da.X * db.Y - da.Y * db.X;
            if (Math.Abs(cross) > 1e-3) { g.ErrorMessage = "两管不平行，无法共线延长"; return g; }

            XYZ aMid = (a0 + a1) * 0.5;
            XYZ bMid = (b0 + b1) * 0.5;
            XYZ perp = new XYZ(-da.Y, da.X, 0);
            double perpDist = Math.Abs((bMid - a0).DotProduct(perp));
            double tolFt = UnitUtils.ConvertToInternalUnits(50.0, UnitTypeId.Millimeters);

            if (perpDist > tolFt) { g.ErrorMessage = $"两管平行但错开 {perpDist * FtToMm:F1}mm，超出容差"; return g; }

            g.PA = (a0.DistanceTo(bMid) < a1.DistanceTo(bMid)) ? a0 : a1;
            g.PB = (b0.DistanceTo(aMid) < b1.DistanceTo(aMid)) ? b0 : b1;
            g.P = g.PA;

            g.OuterPA = (g.PA.DistanceTo(a0) < 1e-4) ? a1 : a0;
            g.OuterPB = (g.PB.DistanceTo(b0) < 1e-4) ? b1 : b0;

            g.IsValid = true;
            return g;
        }

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

            XYZ v = new XYZ(b0.X - a0.X, b0.Y - a0.Y, 0);
            double det = da.X * (-db.Y) - (-db.X) * da.Y;
            if (Math.Abs(det) < 1e-9) { g.ErrorMessage = "交点计算奇异"; return g; }

            double tP = (v.X * (-db.Y) - (-db.X) * v.Y) / det;
            g.P = a0 + da * tP;

            g.PA = (a0.DistanceTo(g.P) < a1.DistanceTo(g.P)) ? a0 : a1;
            g.PB = (b0.DistanceTo(g.P) < b1.DistanceTo(g.P)) ? b0 : b1;

            g.IsValid = true;
            return g;
        }

        public static List<ConnectorPair> MatchPairs(List<Element> aList, List<Element> bList, bool isCorner)
        {
            var pairs = new List<ConnectorPair>();
            if (aList == null || bList == null || !aList.Any() || !bList.Any()) return pairs;

            if (!isCorner)
            {
                Line refLine = GetAxisLine(aList[0]);
                if (refLine == null) return pairs;

                // 【核心修复】绝对统一排序基准！取第一根管的法向量投影
                XYZ dir = new XYZ(refLine.Direction.X, refLine.Direction.Y, 0).Normalize();
                XYZ normal = new XYZ(-dir.Y, dir.X, 0);

                Func<Element, double> getSortValue = (el) =>
                {
                    var line = GetAxisLine(el);
                    if (line == null) return 0;
                    var mid = (line.GetEndPoint(0) + line.GetEndPoint(1)) * 0.5;
                    return mid.DotProduct(normal);
                };

                var sortedA = aList.OrderBy(getSortValue).ToList();
                var sortedB = bList.OrderBy(getSortValue).ToList();

                int count = Math.Min(sortedA.Count, sortedB.Count);
                for (int i = 0; i < count; i++)
                {
                    pairs.Add(new ConnectorPair { AId = sortedA[i].Id, BId = sortedB[i].Id });
                }
            }
            else
            {
                var availableB = new List<Element>(bList);

                foreach (var a in aList)
                {
                    Element bestB = null;
                    double minCost = double.MaxValue;

                    foreach (var b in availableB)
                    {
                        if (!IsSameType(a, b)) continue;

                        MEPElementType typeA = GetElementType(a);
                        MEPElementType typeB = GetElementType(b);
                        if (typeA == MEPElementType.Pipe || typeA == MEPElementType.Duct)
                        {
                            if (GetSystemTypeId(a, typeA) != GetSystemTypeId(b, typeB)) continue;
                        }

                        var g = ComputeCorner(a, b);
                        if (g.IsValid)
                        {
                            double cost = g.PA.DistanceTo(g.P) + g.PB.DistanceTo(g.P);
                            if (cost < minCost)
                            {
                                minCost = cost;
                                bestB = b;
                            }
                        }
                    }

                    if (bestB != null)
                    {
                        pairs.Add(new ConnectorPair { AId = a.Id, BId = bestB.Id });
                        availableB.Remove(bestB);
                    }
                }
            }
            return pairs;
        }
    }
}