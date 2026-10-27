using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace MEPTools.MEPPlanarConnector
{
    public enum MEPElementType { Pipe, Duct, CableTray, Unknown }

    /// <summary>单对连接几何</summary>
    public class PlanarConnectGeometry
    {
        public XYZ PA { get; set; }          // A 组管端点（靠交点侧）
        public XYZ PB { get; set; }          // B 组管端点（靠交点侧）
        public XYZ P { get; set; }           // A、B 延长交点（共线时 = PA）
        public bool IsCollinear { get; set; } // true=共线，false=有交点
        public bool NeedSegA { get; set; }   // 保留字段（兼容旧代码），本版本不再使用
        public bool NeedSegB { get; set; }   // 保留字段（兼容旧代码），本版本不再使用
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class ConnectorPair
    {
        public ElementId AId { get; set; }
        public ElementId BId { get; set; }
        public double Distance { get; set; }
    }

    public static class MEPPlanarConnectorCalculator
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

        public static ElementId GetSystemTypeId(Element elem, MEPElementType t)
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
            return ElementId.InvalidElementId;
        }

        /// <summary>System Type 一致性校验</summary>
        public static bool HasCompatibleSystemType(Element a, Element b, out string reason)
        {
            reason = null;
            var ta = GetElementType(a);
            var tb = GetElementType(b);
            if (ta != tb) { reason = string.Format("类型不一致：{0} vs {1}", ta, tb); return false; }
            if (ta == MEPElementType.CableTray) return true;

            ElementId sysA = GetSystemTypeId(a, ta);
            ElementId sysB = GetSystemTypeId(b, tb);
            if (sysA != sysB) { reason = "System Type 不一致"; return false; }
            return true;
        }

        /// <summary>按选择顺序配对：A[i] ↔ B[i]</summary>
        public static List<ConnectorPair> MatchPairs(List<Element> aList, List<Element> bList)
        {
            var pairs = new List<ConnectorPair>();
            int n = Math.Min(aList.Count, bList.Count);
            for (int i = 0; i < n; i++)
            {
                Line la = GetAxisLine(aList[i]);
                Line lb = GetAxisLine(bList[i]);
                double d = 0;
                if (la != null && lb != null)
                {
                    XYZ ma = (la.GetEndPoint(0) + la.GetEndPoint(1)) * 0.5;
                    XYZ mb = (lb.GetEndPoint(0) + lb.GetEndPoint(1)) * 0.5;
                    d = new XYZ(ma.X - mb.X, ma.Y - mb.Y, 0).GetLength();
                }
                pairs.Add(new ConnectorPair
                {
                    AId = aList[i].Id,
                    BId = bList[i].Id,
                    Distance = d
                });
            }
            return pairs;
        }

        /// <summary>计算单对连接几何</summary>
        public static PlanarConnectGeometry Compute(Element a, Element b)
        {
            var g = new PlanarConnectGeometry { IsValid = false };

            Line la = GetAxisLine(a);
            Line lb = GetAxisLine(b);
            if (la == null || lb == null) { g.ErrorMessage = "A 或 B 非直线段"; return g; }

            XYZ a0 = la.GetEndPoint(0);
            XYZ a1 = la.GetEndPoint(1);
            XYZ b0 = lb.GetEndPoint(0);
            XYZ b1 = lb.GetEndPoint(1);

            XYZ da = new XYZ(a1.X - a0.X, a1.Y - a0.Y, 0);
            XYZ db = new XYZ(b1.X - b0.X, b1.Y - b0.Y, 0);
            if (da.GetLength() < 1e-9 || db.GetLength() < 1e-9)
            { g.ErrorMessage = "A/B 轴线水平投影为零（竖直段不支持）"; return g; }
            da = da.Normalize();
            db = db.Normalize();

            // B 中点 → 选 A 端点；A 中点 → 选 B 端点
            XYZ bMid = (b0 + b1) * 0.5;
            XYZ pA = (a0.DistanceTo(bMid) < a1.DistanceTo(bMid)) ? a0 : a1;
            XYZ aMid = (a0 + a1) * 0.5;
            XYZ pB = (b0.DistanceTo(aMid) < b1.DistanceTo(aMid)) ? b0 : b1;

            double tolFt = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);

            // 平行判断（水平投影叉积）
            double cross = da.X * db.Y - da.Y * db.X;

            if (Math.Abs(cross) < 1e-6)
            {
                // ---- 平行：检查是否共线（位置） ----
                XYZ perp = new XYZ(-da.Y, da.X, 0);
                double perpDist = Math.Abs((pB - pA).DotProduct(perp));
                if (perpDist > tolFt)
                {
                    g.ErrorMessage = string.Format(
                        "A/B 平行但错开 {0:F1}mm，无交点，无法连接",
                        perpDist * FtToMm);
                    return g;
                }

                // 共线 → 直接从 PA 拉长到 PB
                g.PA = pA;
                g.PB = pB;
                g.P = pA;
                g.IsCollinear = true;
                g.NeedSegA = (pA.DistanceTo(pB) > 1e-4);
                g.NeedSegB = false;
                g.IsValid = true;
                return g;
            }
            else
            {
                // ---- 不平行：求 L_A ∩ L_B ----
                XYZ v = new XYZ(b0.X - a0.X, b0.Y - a0.Y, 0);
                double det = da.X * (-db.Y) - (-db.X) * da.Y;
                if (Math.Abs(det) < 1e-9) { g.ErrorMessage = "交点计算奇异"; return g; }

                double tP = (v.X * (-db.Y) - (-db.X) * v.Y) / det;

                XYZ p = a0 + da * tP;

                // P 在 A 段上的位置参数
                double lenA = (a1 - a0).GetLength();
                double tOnA = (p - a0).DotProduct(da) / lenA;
                // P 在 B 段上的位置参数
                XYZ dbVec = (b1 - b0);
                double lenB = dbVec.GetLength();
                double tOnB = (p - b0).DotProduct(db) / lenB;

                // 检查 P 是否在 A/B 内部（若在内部，TR 语义是"修剪"，本插件不支持）
                if (tOnA > 1e-4 && tOnA < 1 - 1e-4)
                { g.ErrorMessage = "交点 P 落在 A 组管内部（仅支持延长，不支持修剪 A）"; return g; }
                if (tOnB > 1e-4 && tOnB < 1 - 1e-4)
                { g.ErrorMessage = "交点 P 落在 B 组管内部（仅支持延长，不支持修剪 B）"; return g; }

                g.PA = pA;
                g.PB = pB;
                g.P = p;
                g.IsCollinear = false;
                g.NeedSegA = (pA.DistanceTo(p) > 1e-4);
                g.NeedSegB = (p.DistanceTo(pB) > 1e-4);
                g.IsValid = true;
                return g;
            }
        }

        public static string Fmt(XYZ p)
        {
            return p == null ? "null" : string.Format("({0:F3},{1:F3},{2:F3})", p.X, p.Y, p.Z);
        }
    }
}