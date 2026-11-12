using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace MEPTools.MEPConnector.Spatial.Core
{
    public enum MEPElementType { Pipe, Duct, CableTray, Unknown }

    public class ConnectGeometry
    {
        public XYZ PA { get; set; }
        public XYZ PB { get; set; }
        public XYZ OuterPA { get; set; }
        public XYZ OuterPB { get; set; }
        public XYZ P { get; set; }
        public bool IsCollinear { get; set; }
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }

        /// <summary>【新增】空间错开量（ft）。仅信息采集，不参与 IsValid 判定。</summary>
        public double SpatialGap { get; set; }

        /// <summary>【新增】true = ComputeCorner 平面路径；false = ComputeCorner3D 空间路径。</summary>
        public bool IsPlanarPath { get; set; }
    }

    public class ConnectorPair
    {
        public ElementId AId { get; set; }
        public ElementId BId { get; set; }
        public double Distance { get; set; }

        /// <summary>【新增】空间错开量（mm）。仅转角命令汇总用；0 表示未检测或无错开。</summary>
        public double SpatialGapMm { get; set; }
    }

    public static class ConnectorCalculator
    {
        public const double FtToMm = 304.8;

        // 平行度容差（共线判定与水平偏差判定共用）
        private const double ParallelTolCross = 1e-2;

        // 平面投影退化阈值：XY 投影 / 总线长 < 该值视为"平面退化"
        // 0.2 约对应 78.5° 以上倾斜；立管必然满足，普通坡度管不会误判
        private const double PlanarDegenerateRatio = 0.2;

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

            double cross = da.X * db.Y - da.Y * db.X;
            if (Math.Abs(cross) > ParallelTolCross) return double.MaxValue;

            XYZ sa = la.GetEndPoint(0);
            XYZ sb = lb.GetEndPoint(0);
            XYZ ab = new XYZ(sb.X - sa.X, sb.Y - sa.Y, 0);
            return Math.Abs(ab.X * da.Y - ab.Y * da.X);
        }

        public static ConnectGeometry ComputeCollinear(Element a, Element b)
        {
            var g = new ConnectGeometry { IsValid = false, IsCollinear = true, IsPlanarPath = true };

            Line la = GetAxisLine(a);
            Line lb = GetAxisLine(b);
            if (la == null || lb == null) { g.ErrorMessage = "存在非直线段元素"; return g; }

            XYZ a0 = la.GetEndPoint(0), a1 = la.GetEndPoint(1);
            XYZ b0 = lb.GetEndPoint(0), b1 = lb.GetEndPoint(1);

            if (Math.Abs(a0.Z - b0.Z) > 1e-3 || Math.Abs(a1.Z - b1.Z) > 1e-3)
            {
                g.ErrorMessage = "两管标高不一致，无法平面共线合并";
                return g;
            }

            XYZ da = new XYZ(a1.X - a0.X, a1.Y - a0.Y, 0);
            XYZ db = new XYZ(b1.X - b0.X, b1.Y - b0.Y, 0);
            if (da.GetLength() < 1e-9 || db.GetLength() < 1e-9) { g.ErrorMessage = "存在竖直段，无法平面连接"; return g; }
            da = da.Normalize(); db = db.Normalize();

            double cross = da.X * db.Y - da.Y * db.X;
            if (Math.Abs(cross) > ParallelTolCross) { g.ErrorMessage = "两管不平行，无法共线延长"; return g; }

            XYZ aMid = (a0 + a1) * 0.5;
            XYZ bMid = (b0 + b1) * 0.5;
            XYZ perp = new XYZ(-da.Y, da.X, 0);
            double perpDist = Math.Abs((bMid - a0).DotProduct(perp));
            double tolFt = UnitUtils.ConvertToInternalUnits(50.0, UnitTypeId.Millimeters);

            if (perpDist > tolFt) { g.ErrorMessage = $"两管平行但错开 {perpDist * FtToMm:F1}mm，超出容差"; return g; }

            g.PA = (a0.DistanceTo(bMid) < a1.DistanceTo(bMid)) ? a0 : a1;
            g.PB = (b0.DistanceTo(aMid) < b1.DistanceTo(aMid)) ? b0 : b1;
            g.P = g.PA;

            double dPA_a0 = g.PA.DistanceTo(a0);
            double dPA_a1 = g.PA.DistanceTo(a1);
            g.OuterPA = (dPA_a0 > dPA_a1) ? a0 : a1;

            double dPB_b0 = g.PB.DistanceTo(b0);
            double dPB_b1 = g.PB.DistanceTo(b1);
            g.OuterPB = (dPB_b0 > dPB_b1) ? b0 : b1;

            double mergedLen = g.OuterPA.DistanceTo(g.OuterPB);
            double lenA = a0.DistanceTo(a1);
            double lenB = b0.DistanceTo(b1);
            if (mergedLen < Math.Max(lenA, lenB) - 1e-3)
            {
                g.ErrorMessage = "共线延长方向异常（合并后长度反而缩短），已放弃";
                return g;
            }

            g.SpatialGap = 0;   // 共线场景必然共面
            g.IsValid = true;
            return g;
        }

        /// <summary>
        /// 平面转角（原有逻辑不变）。
        /// 仅在两端都非立管时被 ComputeCornerAuto 调用。
        /// 【新增】末尾采集 SpatialGap / IsPlanarPath，供命令层汇总决策，不参与 IsValid 判定。
        /// </summary>
        public static ConnectGeometry ComputeCorner(Element a, Element b)
        {
            var g = new ConnectGeometry { IsValid = false, IsCollinear = false };

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

            // 【仅信息采集，不改变 IsValid 判定与返回路径】
            XYZ qa, qb;
            g.SpatialGap = GetSpatialLineGap(a, b, out qa, out qb);
            g.IsPlanarPath = true;

            g.IsValid = true;
            return g;
        }

        // ============================================================
        // 空间转角路径，与平面路径完全隔离
        // ============================================================

        /// <summary>
        /// 判断单条线在平面投影中是否退化（近似立管）。
        /// 采用 XY 投影长度占比，直接对应"平面视图里是否可见"。
        /// null 线交给原有平面逻辑报错，不在此处判退化。
        /// </summary>
        private static bool IsPlanarDegenerate(Line l)
        {
            if (l == null) return false;
            XYZ d = l.Direction;
            double total = d.GetLength();
            if (total < 1e-9) return false;
            double xy = Math.Sqrt(d.X * d.X + d.Y * d.Y);
            return (xy / total) < PlanarDegenerateRatio;
        }

        /// <summary>
        /// 任一端平面投影退化（近似立管），则需要空间转角。
        /// 水平管 × 水平管 → false（走原平面路径，行为不变）。
        /// </summary>
        private static bool NeedSpatialCorner(Element a, Element b)
        {
            return IsPlanarDegenerate(GetAxisLine(a)) || IsPlanarDegenerate(GetAxisLine(b));
        }

        /// <summary>
        /// 【新增·公共 API】两条 MEP 轴线（视为无限空间直线）的最近点对距离。
        ///
        /// 用途：判断两条管在空间中是否真正相交。XY 投影上十字交叉但标高/坡度不同时，
        /// 投影有交点但空间中错开 —— 这不是真正交点，强行接驳会生成斜管。
        ///
        /// 独立于任何连接逻辑，可用于未来带坡度管辅助接驳、坡道对齐等场景。
        /// </summary>
        /// <returns>最近点对距离（ft）；无法计算返回 double.MaxValue。</returns>
        public static double GetSpatialLineGap(Element a, Element b, out XYZ closestA, out XYZ closestB)
        {
            closestA = closestB = null;
            Line la = GetAxisLine(a);
            Line lb = GetAxisLine(b);
            if (la == null || lb == null) return double.MaxValue;

            XYZ a0 = la.GetEndPoint(0), a1 = la.GetEndPoint(1);
            XYZ b0 = lb.GetEndPoint(0), b1 = lb.GetEndPoint(1);

            XYZ da = a1 - a0;
            XYZ db = b1 - b0;
            if (da.GetLength() < 1e-9 || db.GetLength() < 1e-9) return double.MaxValue;
            da = da.Normalize();
            db = db.Normalize();

            XYZ w0 = a0 - b0;
            double aa = 1.0;
            double bb = da.DotProduct(db);
            double cc = 1.0;
            double dd = da.DotProduct(w0);
            double ee = db.DotProduct(w0);
            double denom = aa * cc - bb * bb;

            double s, t;
            if (Math.Abs(denom) < 1e-9)
            {
                // 平行：距离为恒定值，任取一点投影即可
                s = 0;
                t = ee;
            }
            else
            {
                s = (bb * ee - cc * dd) / denom;
                t = (aa * ee - bb * dd) / denom;
            }

            closestA = a0 + da * s;
            closestB = b0 + db * t;
            return closestA.DistanceTo(closestB);
        }

        /// <summary>
        /// 空间转角：取两条空间直线最近点对的中点作为转角位置。
        /// 适用于立管 + 水平管；两根立管会因平行被拒绝。
        /// 与 ComputeCorner 的 XY 投影逻辑完全独立，互不影响。
        /// </summary>
        public static ConnectGeometry ComputeCorner3D(Element a, Element b)
        {
            var g = new ConnectGeometry { IsValid = false, IsCollinear = false };

            Line la = GetAxisLine(a);
            Line lb = GetAxisLine(b);
            if (la == null || lb == null) { g.ErrorMessage = "存在非直线段元素"; return g; }

            XYZ a0 = la.GetEndPoint(0), a1 = la.GetEndPoint(1);
            XYZ b0 = lb.GetEndPoint(0), b1 = lb.GetEndPoint(1);

            XYZ da = a1 - a0;
            XYZ db = b1 - b0;
            if (da.GetLength() < 1e-9 || db.GetLength() < 1e-9) { g.ErrorMessage = "存在零长度段"; return g; }
            da = da.Normalize();
            db = db.Normalize();

            // 三维平行判定：1 - |cosθ| < 1e-4 约对应 0.81° 以内，视为平行
            double dot = da.DotProduct(db);
            if (1.0 - Math.Abs(dot) < 1e-4) { g.ErrorMessage = "两管平行，无法转角连接"; return g; }

            XYZ w0 = a0 - b0;
            double aa = 1.0;
            double bb = da.DotProduct(db);
            double cc = 1.0;
            double dd = da.DotProduct(w0);
            double ee = db.DotProduct(w0);
            double denom = aa * cc - bb * bb;
            if (Math.Abs(denom) < 1e-9) { g.ErrorMessage = "交点计算奇异"; return g; }

            double t = (bb * ee - cc * dd) / denom;
            double s = (aa * ee - bb * dd) / denom;

            XYZ q1 = a0 + da * t;
            XYZ q2 = b0 + db * s;
            double gap = q1.DistanceTo(q2);

            // 空间转角异面容差：50mm
            double tol = UnitUtils.ConvertToInternalUnits(50.0, UnitTypeId.Millimeters);
            if (gap > tol)
            {
                g.ErrorMessage = $"两管空间错开 {gap * FtToMm:F1}mm，超出容差";
                return g;
            }

            g.P = (q1 + q2) * 0.5;
            g.PA = (a0.DistanceTo(g.P) < a1.DistanceTo(g.P)) ? a0 : a1;
            g.PB = (b0.DistanceTo(g.P) < b1.DistanceTo(g.P)) ? b0 : b1;

            // 【信息采集】供命令层区分路径类型；立管路径不参与斜管弹窗
            g.SpatialGap = gap;
            g.IsPlanarPath = false;

            g.IsValid = true;
            return g;
        }

        /// <summary>
        /// 转角计算统一入口：
        /// - 任一端为近似立管 → ComputeCorner3D（空间路径）
        /// - 否则             → ComputeCorner（平面路径）
        /// </summary>
        public static ConnectGeometry ComputeCornerAuto(Element a, Element b)
        {
            return NeedSpatialCorner(a, b) ? ComputeCorner3D(a, b) : ComputeCorner(a, b);
        }

        public static List<ConnectorPair> MatchPairs(List<Element> aList, List<Element> bList, bool isCorner)
        {
            var pairs = new List<ConnectorPair>();
            if (aList == null || bList == null || !aList.Any() || !bList.Any()) return pairs;

            if (!isCorner)
            {
                Line refLine = GetAxisLine(aList[0]);
                if (refLine == null) return pairs;

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

                        var g = ComputeCornerAuto(a, b);
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