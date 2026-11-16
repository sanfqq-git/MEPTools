using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace MEPTools.MEP3TeeConnect
{
    public enum TeeElementType { Pipe, Duct, Unknown }

    /// <summary>一对 T 形配对</summary>
    public class TeePair
    {
        public string SystemName { get; set; }
        public ElementId IdA { get; set; }          // 预选组（A）
        public ElementId IdB { get; set; }          // 后选组（B）

        public TeeElementType ElemType { get; set; }

        /// <summary>平面交点（XY）</summary>
        public XYZ CrossPoint { get; set; }

        /// <summary>B 上参数：0 = b0 侧，1 = b1 侧。判断被修改的端点。</summary>
        public double TCross { get; set; }

        /// <summary>true = 需要延长 B；false = 需要缩短 B（tCross 在 [0,1] 内）</summary>
        public bool NeedExtend { get; set; }

        /// <summary>B 被修改的端点：true = b0，false = b1</summary>
        public bool ModifyB0 { get; set; }

        /// <summary>交点落在 A 上的参数 s ∈ [0,1]</summary>
        public double SOnA { get; set; }

        /// <summary>A、B 的中心线 Z</summary>
        public double ZA { get; set; }
        public double ZB { get; set; }

        /// <summary>原始 A / B 规格（用于日志与提示）</summary>
        public double DiameterA { get; set; }
        public double DiameterB { get; set; }
        public double WidthA { get; set; }
        public double WidthB { get; set; }
        public double HeightA { get; set; }
        public double HeightB { get; set; }

        /// <summary>立管/三通规格（取 A、B 中较大者，异径时即为大管径）</summary>
        public double Diameter { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }

        /// <summary>立管参数（取 A 组）</summary>
        public ElementId SystemTypeId { get; set; }
        public ElementId TypeId { get; set; }
        public ElementId LevelId { get; set; }
    }

    public static class MEPTeeCalculator
    {
        public const double FtToMm = 304.8;
        public const double MinEndOffsetMm = 100.0;
        public const double FittingCheckMm = 100.0;
        public const double DiameterGapMm = 100.0;
        public const double MinBLengthMm = 100.0;

        public static TeeElementType GetTeeType(Element e)
        {
            if (e is Pipe) return TeeElementType.Pipe;
            if (e is Duct) return TeeElementType.Duct;
            return TeeElementType.Unknown;
        }

        public static ElementId GetSystemTypeId(Element e, TeeElementType t)
        {
            switch (t)
            {
                case TeeElementType.Pipe:
                    return e.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId()
                        ?? ElementId.InvalidElementId;
                case TeeElementType.Duct:
                    return e.get_Parameter(BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM)?.AsElementId()
                        ?? ElementId.InvalidElementId;
            }
            return ElementId.InvalidElementId;
        }

        public static string GetSystemName(Document doc, Element e, TeeElementType t)
        {
            var sid = GetSystemTypeId(e, t);
            if (sid == ElementId.InvalidElementId) return null;
            return doc.GetElement(sid)?.Name;
        }

        /// <summary>等效"管径"：Pipe→公称直径；Duct→矩形取高度/圆形取直径</summary>
        public static double GetDiameter(Element e, TeeElementType t)
        {
            if (t == TeeElementType.Pipe)
                return e.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble() ?? 0;

            if (t == TeeElementType.Duct)
            {
                double d = e.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM)?.AsDouble() ?? 0;
                double w = e.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.AsDouble() ?? 0;
                double h = e.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)?.AsDouble() ?? 0;
                if (w > 1e-9 && h > 1e-9) return h;
                return d;
            }
            return 0;
        }

        public static double GetWidth(Element e, TeeElementType t)
        {
            if (t == TeeElementType.Duct)
            {
                double d = e.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM)?.AsDouble() ?? 0;
                double w = e.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.AsDouble() ?? 0;
                if (d > 1e-9 && w <= 1e-9) return 0;
                return w;
            }
            return 0;
        }

        public static double GetHeight(Element e, TeeElementType t)
        {
            if (t == TeeElementType.Duct)
            {
                double d = e.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM)?.AsDouble() ?? 0;
                double w = e.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.AsDouble() ?? 0;
                if (d > 1e-9 && w <= 1e-9) return 0; // 圆形无高度
                // ★ 优化：直接读取 h，避免再走一次 GetDiameter 重复读取三参数。
                // 行为等价：
                //   矩形（w>0,h>0）→ h    ；w>0,h=0（异常）→ d    ；圆形已在上方拦截
                double h = e.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)?.AsDouble() ?? 0;
                return (w > 1e-9 && h > 1e-9) ? h : d;
            }
            return GetDiameter(e, t);
        }

        // ============================================================
        // 异径支持：取 A、B 中较大规格
        // ============================================================
        public static double GetMaxDiameter(Element a, Element b, TeeElementType t)
            => Math.Max(GetDiameter(a, t), GetDiameter(b, t));

        public static double GetMaxWidth(Element a, Element b, TeeElementType t)
            => Math.Max(GetWidth(a, t), GetWidth(b, t));

        public static double GetMaxHeight(Element a, Element b, TeeElementType t)
            => Math.Max(GetHeight(a, t), GetHeight(b, t));

        public static double GetCenterZ(Element e)
        {
            if (e.Location is LocationCurve lc)
                return (lc.Curve.GetEndPoint(0).Z + lc.Curve.GetEndPoint(1).Z) * 0.5;
            return 0;
        }

        /// <summary>
        /// 计算 T 形几何：
        /// - A（预选）、B（后选）都必须是直线
        /// - 求 A、B（无限延长线）的平面交点
        /// - 交点必须在 A 管身上（s ∈ [0,1]，且距两端 ≥ 100mm）
        /// - 通过 tCross 判断 B 是"延长"还是"缩短"，以及要修改的端点
        /// </summary>
        public static bool ComputeTeeGeometry(Element a, Element b,
            out XYZ crossPoint, out double sOnA, out double tCross,
            out bool needExtend, out bool modifyB0,
            out string err)
        {
            crossPoint = null; sOnA = 0; tCross = 0;
            needExtend = false; modifyB0 = false;
            err = null;

            if (!(a.Location is LocationCurve lca) || !(lca.Curve is Line))
            { err = "A 管不是直线（含弯头或非直线段）"; return false; }
            if (!(b.Location is LocationCurve lcb) || !(lcb.Curve is Line))
            { err = "B 管不是直线（含弯头或非直线段）"; return false; }

            XYZ a0 = lca.Curve.GetEndPoint(0);
            XYZ a1 = lca.Curve.GetEndPoint(1);
            XYZ b0 = lcb.Curve.GetEndPoint(0);
            XYZ b1 = lcb.Curve.GetEndPoint(1);

            double ax = a1.X - a0.X, ay = a1.Y - a0.Y;
            double bx = b1.X - b0.X, by = b1.Y - b0.Y;
            double denom = ax * by - ay * bx;

            if (Math.Abs(denom) < 1e-12)
            { err = "A 与 B 在平面上平行（无唯一交点）"; return false; }

            double dx = b0.X - a0.X, dy = b0.Y - a0.Y;
            sOnA = (dx * by - dy * bx) / denom;
            tCross = (dx * ay - dy * ax) / denom;

            const double tol = 1e-6;
            if (sOnA < -tol || sOnA > 1 + tol)
            { err = "平面交点不在 A 管范围内（A 管需为承接方）"; return false; }

            crossPoint = new XYZ(a0.X + sOnA * ax, a0.Y + sOnA * ay, 0);

            if (tCross < 0)
            { needExtend = true; modifyB0 = true; }
            else if (tCross > 1)
            { needExtend = true; modifyB0 = false; }
            else
            {
                // 缩短：修改更靠近交点的端点
                needExtend = false;
                modifyB0 = tCross < 0.5;
            }
            return true;
        }

        /// <summary>交点距 A 端点 ≥ 100mm</summary>
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

        /// <summary>
        /// 交点 ±100mm 内是否已有管件。
        /// ★ 优化：在 FilteredElementCollector 阶段按专业类别预过滤（ElementCategoryFilter + LogicalOrFilter），
        /// 避免把整个包围盒内的无关元素全部拉进内存再逐条判别；同时保留原有"仅同专业"的语义。
        /// </summary>
        public static bool CheckNearbyFitting(Document doc, XYZ planPt, double z,
            ElementId ignoreA, ElementId ignoreB, out string err)
        {
            err = null;
            double r = UnitUtils.ConvertToInternalUnits(FittingCheckMm, UnitTypeId.Millimeters);
            var outline = new Outline(
                new XYZ(planPt.X - r, planPt.Y - r, z - r),
                new XYZ(planPt.X + r, planPt.Y + r, z + r));

            Element elemA = doc.GetElement(ignoreA);
            TeeElementType typeA = GetTeeType(elemA);

            var collector = new FilteredElementCollector(doc)
                .WherePasses(new BoundingBoxIntersectsFilter(outline));

            // 按 typeA 预构造类别过滤器（与旧逻辑完全等价）
            var catFilters = new List<ElementFilter>(4);
            if (typeA == TeeElementType.Pipe || typeA == TeeElementType.Unknown)
            {
                catFilters.Add(new ElementCategoryFilter(BuiltInCategory.OST_PipeFitting));
                catFilters.Add(new ElementCategoryFilter(BuiltInCategory.OST_PipeAccessory));
            }
            if (typeA == TeeElementType.Duct || typeA == TeeElementType.Unknown)
            {
                catFilters.Add(new ElementCategoryFilter(BuiltInCategory.OST_DuctFitting));
                catFilters.Add(new ElementCategoryFilter(BuiltInCategory.OST_DuctAccessory));
            }
            if (catFilters.Count > 0)
            {
                ElementFilter catFilter = catFilters.Count == 1
                    ? catFilters[0]
                    : new LogicalOrFilter(catFilters);
                collector = collector.WherePasses(catFilter);
            }

            var col = collector.WhereElementIsNotElementType().ToElements();

            foreach (var el in col)
            {
                if (el.Id == ignoreA || el.Id == ignoreB) continue;
                if (!(el is FamilyInstance fi) || fi.Category == null) continue;

                err = $"交点附近发现管件：{el.Id.IntegerValue} ({fi.Name})";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 高差校验：使用两管较大等效直径，需 ≥ maxDia + 100mm。
        /// 异径时按大管径计算，保证大管穿越净空。
        /// </summary>
        public static bool CheckHeightDifference(double z1, double z2, double maxDiameter, out string err)
        {
            err = null;
            double diff = Math.Abs(z1 - z2);
            double need = maxDiameter + UnitUtils.ConvertToInternalUnits(DiameterGapMm, UnitTypeId.Millimeters);
            if (diff < need)
            {
                err = $"高差 {diff * FtToMm:F1}mm 不足（需 ≥ 大管径 + {DiameterGapMm}mm = {need * FtToMm:F1}mm）";
                return false;
            }
            return true;
        }

        public static string Fmt(XYZ p) =>
            p == null ? "null" : $"({p.X:F3},{p.Y:F3},{p.Z:F3})";
    }
}