using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Connector = Autodesk.Revit.DB.Connector;

namespace MEPTools.UpDown
{
    /// <summary>MEP 管线类型</summary>
    public enum MEPElementType
    {
        Pipe,
        Duct,
        CableTray,
        Unknown
    }

    /// <summary>45° 单侧抬升/下降几何数据</summary>
    public class UpDownGeometry
    {
        /// <summary>打断点（原始高度侧）</summary>
        public XYZ P0 { get; set; }
        /// <summary>斜段另一端（新高度侧）</summary>
        public XYZ P1 { get; set; }
        /// <summary>管线方向（沿曲线从起点到终点）</summary>
        public XYZ Dir { get; set; }
        /// <summary>带符号高度偏移（ft）</summary>
        public double Offset { get; set; }
        /// <summary>true = 沿 dir 方向的一侧被抬升/下降</summary>
        public bool LiftRight { get; set; }

        /// <summary>打断点到曲线起点沿管距离（ft）</summary>
        public double DistFromStart { get; set; }
        /// <summary>打断点到曲线终点沿管距离（ft）</summary>
        public double DistToEnd { get; set; }
        /// <summary>升降侧可用空间（ft）</summary>
        public double AvailableSpace { get; set; }

        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>45° 抬升/下降几何计算器（无状态）</summary>
    public static class MEPUpDownCalculator
    {
        /// <summary>弯头内角允许范围（度）</summary>
        public const double ElbowAngleMin = 2.0;
        public const double ElbowAngleMax = 95.0;

        /// <summary>英尺 → 毫米</summary>
        public const double FtToMm = 304.8;

        /// <summary>最小过渡长度（ft）</summary>
        public const double MinTransitionLength = 0.0001;

        /// <summary>判断元素类型</summary>
        public static MEPElementType GetElementType(Element elem)
        {
            if (elem is Pipe) return MEPElementType.Pipe;
            if (elem is Duct) return MEPElementType.Duct;
            if (elem is CableTray) return MEPElementType.CableTray;
            return MEPElementType.Unknown;
        }

        /// <summary>
        /// 计算 45° 单侧抬升/下降几何：
        /// - 打断点 P0 位于原始高度
        /// - 过渡段沿管线方向水平投影长度 = |h_offset|
        /// - 过渡段另一端 P1 位于新高度
        /// </summary>
        public static UpDownGeometry ComputeLift(Curve curve, XYZ breakPt, XYZ sidePt,
            double h_offset_ft, out string errorMsg)
        {
            errorMsg = null;
            var result = new UpDownGeometry { IsValid = false };

            if (curve == null)
            {
                errorMsg = "曲线为空";
                return result;
            }

            IntersectionResult proj1 = curve.Project(breakPt);
            IntersectionResult proj2 = curve.Project(sidePt);
            if (proj1 == null || proj2 == null)
            {
                errorMsg = "点位投影失败（拾取点不在管线上）";
                return result;
            }

            XYZ p0 = proj1.XYZPoint;

            // ---------- 计算曲线方向 ----------
            XYZ dir;
            if (curve is Line line)
            {
                dir = (line.GetEndPoint(1) - line.GetEndPoint(0)).Normalize();
            }
            else
            {
                double t0 = Math.Max(0.0, Math.Min(1.0, proj1.Parameter));
                XYZ tangent = curve.ComputeDerivatives(t0, false).BasisX;
                if (tangent.GetLength() < 1e-9)
                {
                    errorMsg = "无法获取管线切线方向";
                    return result;
                }
                dir = tangent.Normalize();
            }

            XYZ sideProj = proj2.XYZPoint;
            // 升降侧判定：点击侧相对打断点的方向
            bool liftRight = (sideProj - p0).DotProduct(dir) >= 0;

            double absH = Math.Abs(h_offset_ft);
            double sign = liftRight ? 1.0 : -1.0;

            // ---------- 用向量点积直接计算沿管距离 ----------
            XYZ curveStart = curve.GetEndPoint(0);
            XYZ curveEnd = curve.GetEndPoint(1);

            double distFromStart = (p0 - curveStart).DotProduct(dir);
            double distToEnd = (curveEnd - p0).DotProduct(dir);

            // 容差 0.5mm
            double tolFt = UnitUtils.ConvertToInternalUnits(0.5, UnitTypeId.Millimeters);

            // 升降侧需要预留的空间
            double availableSpace = liftRight ? distToEnd : distFromStart;

            result.DistFromStart = distFromStart;
            result.DistToEnd = distToEnd;
            result.AvailableSpace = availableSpace;

            if (availableSpace < absH - tolFt)
            {
                errorMsg =
                    $"打断点距离升降侧端点空间不足：\n" +
                    $"  需要 = {absH * FtToMm:F1} mm\n" +
                    $"  可用 = {availableSpace * FtToMm:F1} mm\n" +
                    $"  (打断点距起点 {distFromStart * FtToMm:F1} mm, " +
                    $"距终点 {distToEnd * FtToMm:F1} mm)\n" +
                    $"  建议：减小偏移量至 ≤ {availableSpace * FtToMm:F1} mm，" +
                    $"或将打断点向升降侧端点方向移动";
                return result;
            }

            // P1 = P0 + dir*sign*|h| + Z*h  （45° 斜段端点）
            XYZ p1 = p0 + dir * (sign * absH) + XYZ.BasisZ * h_offset_ft;

            double transLen = (p1 - p0).GetLength();
            if (transLen < MinTransitionLength)
            {
                errorMsg = "过渡长度过短（偏移量太小）";
                return result;
            }

            result.P0 = p0;
            result.P1 = p1;
            result.Dir = dir;
            result.Offset = h_offset_ft;
            result.LiftRight = liftRight;
            result.IsValid = true;
            return result;
        }

        /// <summary>根据两个连接器朝向计算弯头内角（度）</summary>
        public static double CalcElbowAngleDeg(Connector c1, Connector c2)
        {
            double outerDeg = c1.CoordinateSystem.BasisZ
                .AngleTo(c2.CoordinateSystem.BasisZ) * 180.0 / Math.PI;
            return 180.0 - outerDeg;
        }

        /// <summary>弯头角度是否在允许范围内</summary>
        public static bool IsElbowAngleValid(double elbowDeg)
        {
            return elbowDeg >= ElbowAngleMin && elbowDeg <= ElbowAngleMax;
        }

        /// <summary>格式化 XYZ 点用于日志</summary>
        public static string Fmt(XYZ p)
        {
            return p == null ? "null" : $"({p.X:F3},{p.Y:F3},{p.Z:F3})";
        }
    }
}