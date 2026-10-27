using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace MEPTools.MEP3TeeCrossConnect
{
    public enum CrossElementType { Pipe, Duct, Unknown }

    public class CrossPair
    {
        public string SystemName { get; set; }
        public ElementId IdA { get; set; }
        public ElementId IdB { get; set; }

        public ElementId HighId { get; set; }
        public ElementId LowId { get; set; }
        public XYZ CrossPoint { get; set; }
        public double HighZ { get; set; }
        public double LowZ { get; set; }

        public ElementId SystemTypeId { get; set; }
        public ElementId HighTypeId { get; set; }
        public ElementId HighLevelId { get; set; }

        // 立管尺寸（取高/低组较大值，形状与高组一致）
        public double Diameter { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }

        // 记录两侧原始尺寸，便于日志与诊断
        public double DiameterHigh { get; set; }
        public double DiameterLow { get; set; }

        public CrossElementType ElemType { get; set; }
    }

    public static class MEPCrossCalculator
    {
        public const double FtToMm = 304.8;
        public const double MinEndOffsetMm = 100.0;
        public const double FittingCheckMm = 100.0;
        public const double DiameterGapMm = 100.0;
        private const double SizeTolMm = 1.0;

        public static CrossElementType GetCrossType(Element e)
        {
            if (e is Pipe) return CrossElementType.Pipe;
            if (e is Duct) return CrossElementType.Duct;
            return CrossElementType.Unknown;
        }

        public static ElementId GetSystemTypeId(Element e, CrossElementType t)
        {
            if (t == CrossElementType.Pipe)
            {
                var p = e.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM);
                return p?.AsElementId() ?? ElementId.InvalidElementId;
            }
            if (t == CrossElementType.Duct)
            {
                var p = e.get_Parameter(BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM);
                return p?.AsElementId() ?? ElementId.InvalidElementId;
            }
            return ElementId.InvalidElementId;
        }

        public static string GetSystemName(Document doc, Element e, CrossElementType t)
        {
            var sid = GetSystemTypeId(e, t);
            if (sid == ElementId.InvalidElementId) return null;
            return doc.GetElement(sid)?.Name;
        }

        /// <summary>
        /// 获取「等效管径」：
        ///   Pipe → 公称直径
        ///   Duct → 矩形取高度；圆形取直径
        /// </summary>
        public static double GetDiameter(Element e, CrossElementType t)
        {
            if (t == CrossElementType.Pipe)
            {
                var p = e.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                return p?.AsDouble() ?? 0;
            }
            if (t == CrossElementType.Duct)
            {
                var pd = e.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                var pw = e.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                var ph = e.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);

                double d = pd?.AsDouble() ?? 0;
                double w = pw?.AsDouble() ?? 0;
                double h = ph?.AsDouble() ?? 0;

                // 矩形：宽、高都非零 → 用高度
                if (w > 1e-9 && h > 1e-9) return h;
                // 圆形：用直径
                return d;
            }
            return 0;
        }

        public static double GetWidth(Element e, CrossElementType t)
        {
            if (t != CrossElementType.Duct) return 0;
            var pd = e.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
            var pw = e.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
            double d = pd?.AsDouble() ?? 0;
            double w = pw?.AsDouble() ?? 0;
            // 圆形：直径非零、宽度为零或无效 → 返回 0
            if (d > 1e-9 && w <= 1e-9) return 0;
            return w;
        }

        public static double GetHeight(Element e, CrossElementType t)
        {
            if (t == CrossElementType.Duct)
            {
                var pd = e.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                double d = pd?.AsDouble() ?? 0;
                if (d > 1e-9)
                {
                    var pw = e.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                    double w = pw?.AsDouble() ?? 0;
                    // 圆形风管没有「高度」，返回 0
                    if (w <= 1e-9) return 0;
                }
            }
            return GetDiameter(e, t);
        }

        /// <summary>是否矩形风管（宽、高都非零）</summary>
        public static bool IsRectangular(Element e, CrossElementType t)
        {
            if (t != CrossElementType.Duct) return false;
            double w = GetWidth(e, t);
            double h = GetHeight(e, t);
            return w > 1e-9 && h > 1e-9;
        }

        public static double GetCenterZ(Element e)
        {
            if (e.Location is LocationCurve lc)
                return (lc.Curve.GetEndPoint(0).Z + lc.Curve.GetEndPoint(1).Z) * 0.5;
            return 0;
        }

        /// <summary>计算两管在平面 (XY) 上的交点；两管必须都是直线</summary>
        public static XYZ ComputePlanCrossPoint(Element a, Element b, out string err)
        {
            err = null;
            if (!(a.Location is LocationCurve lca) || !(lca.Curve is Line))
            { err = "A 管不是直线（含弯头或非直线段）"; return null; }
            if (!(b.Location is LocationCurve lcb) || !(lcb.Curve is Line))
            { err = "B 管不是直线（含弯头或非直线段）"; return null; }

            XYZ a0 = lca.Curve.GetEndPoint(0);
            XYZ a1 = lca.Curve.GetEndPoint(1);
            XYZ b0 = lcb.Curve.GetEndPoint(0);
            XYZ b1 = lcb.Curve.GetEndPoint(1);

            double ax = a1.X - a0.X, ay = a1.Y - a0.Y;
            double bx = b1.X - b0.X, by = b1.Y - b0.Y;
            double denom = ax * by - ay * bx;

            if (Math.Abs(denom) < 1e-12)
            { err = "两管在平面上平行（无唯一交点）"; return null; }

            double dx = b0.X - a0.X, dy = b0.Y - a0.Y;
            double t = (dx * by - dy * bx) / denom;
            double s = (dx * ay - dy * ax) / denom;

            const double tol = 1e-6;
            if (t < -tol || t > 1 + tol) { err = "平面交点不在 A 管范围内"; return null; }
            if (s < -tol || s > 1 + tol) { err = "平面交点不在 B 管范围内"; return null; }

            double px = a0.X + t * ax;
            double py = a0.Y + t * ay;
            return new XYZ(px, py, 0);
        }

        /// <summary>交点距端点（XY 平面）≥ 100mm</summary>
        public static bool CheckEndDistance(Element e, XYZ planPt, out string err)
        {
            err = null;
            if (!(e.Location is LocationCurve lc)) { err = "定位曲线缺失"; return false; }
            double tolFt = UnitUtils.ConvertToInternalUnits(MinEndOffsetMm, UnitTypeId.Millimeters);
            XYZ p0 = lc.Curve.GetEndPoint(0);
            XYZ p1 = lc.Curve.GetEndPoint(1);
            double d0 = Math.Sqrt((planPt.X - p0.X) * (planPt.X - p0.X) + (planPt.Y - p0.Y) * (planPt.Y - p0.Y));
            double d1 = Math.Sqrt((planPt.X - p1.X) * (planPt.X - p1.X) + (planPt.Y - p1.Y) * (planPt.Y - p1.Y));
            double dMin = Math.Min(d0, d1);
            if (dMin < tolFt)
            {
                err = $"交点距端点 {dMin * FtToMm:F1}mm < {MinEndOffsetMm}mm";
                return false;
            }
            return true;
        }

        /// <summary>交点 ±100mm 内是否已有管件</summary>
        public static bool CheckNearbyFitting(Document doc, XYZ planPt, double z,
            ElementId ignoreA, ElementId ignoreB, out string err)
        {
            err = null;
            double r = UnitUtils.ConvertToInternalUnits(FittingCheckMm, UnitTypeId.Millimeters);
            var outline = new Outline(
                new XYZ(planPt.X - r, planPt.Y - r, z - r),
                new XYZ(planPt.X + r, planPt.Y + r, z + r));

            var col = new FilteredElementCollector(doc)
                .WherePasses(new BoundingBoxIntersectsFilter(outline))
                .WhereElementIsNotElementType()
                .ToElements();

            foreach (var el in col)
            {
                if (el.Id == ignoreA || el.Id == ignoreB) continue;
                if (!(el is FamilyInstance fi) || fi.Category == null) continue;

                int cid = fi.Category.Id.IntegerValue;
                if (cid == (int)BuiltInCategory.OST_PipeFitting ||
                    cid == (int)BuiltInCategory.OST_DuctFitting ||
                    cid == (int)BuiltInCategory.OST_PipeAccessory ||
                    cid == (int)BuiltInCategory.OST_DuctAccessory)
                {
                    err = $"交点附近发现管件：{el.Id.IntegerValue} ({fi.Name})";
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 尺寸比较（仅信息性；调用方已放宽，允许变径连接）
        /// 返回 true = 尺寸一致；false = 尺寸有差异，info 中给出差异说明
        /// </summary>
        public static bool AreEqualSize(Element a, Element b, CrossElementType t, out string info)
        {
            info = null;
            double tol = UnitUtils.ConvertToInternalUnits(SizeTolMm, UnitTypeId.Millimeters);
            double da = GetDiameter(a, t), db = GetDiameter(b, t);
            bool ok = true;

            if (Math.Abs(da - db) > tol)
            {
                info = $"管径/高度不一致：{da * FtToMm:F1}mm vs {db * FtToMm:F1}mm";
                ok = false;
            }

            if (t == CrossElementType.Duct)
            {
                double wa = GetWidth(a, t), wb = GetWidth(b, t);
                if (Math.Abs(wa - wb) > tol)
                {
                    string s = $"风管宽度不一致：{wa * FtToMm:F1}mm vs {wb * FtToMm:F1}mm";
                    info = string.IsNullOrEmpty(info) ? s : info + "；" + s;
                    ok = false;
                }
            }
            return ok;
        }

        /// <summary>
        /// 高差校验：需 ≥ max(高组、低组尺寸) + 100mm
        /// maxDiameter 由调用方传入（圆形=直径；矩形=高度）
        /// </summary>
        public static bool CheckHeightDifference(double z1, double z2, double maxDiameter, out string err)
        {
            err = null;
            double diff = Math.Abs(z1 - z2);
            double need = maxDiameter + UnitUtils.ConvertToInternalUnits(DiameterGapMm, UnitTypeId.Millimeters);
            if (diff < need)
            {
                err = $"高差 {diff * FtToMm:F1}mm 不足（需 ≥ 较大管径 {maxDiameter * FtToMm:F1}mm + {DiameterGapMm}mm = {need * FtToMm:F1}mm）";
                return false;
            }
            return true;
        }

        public static string Fmt(XYZ p) =>
            p == null ? "null" : $"({p.X:F3},{p.Y:F3},{p.Z:F3})";
    }
}