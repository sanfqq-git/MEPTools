using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Connector = Autodesk.Revit.DB.Connector;

namespace MEPTools.MEPHighLowUpDown
{
    public enum MEPElementType { Pipe, Duct, CableTray, Unknown }

    public class UpDownGeometry
    {
        public XYZ P0 { get; set; }
        public XYZ P1 { get; set; }
        public XYZ Dir { get; set; }
        public double Offset { get; set; }
        public bool LiftRight { get; set; }
        public double DistFromStart { get; set; }
        public double DistToEnd { get; set; }
        public double AvailableSpace { get; set; }
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
    }

    public static class MEPUpDownCalculator
    {
        public const double ElbowAngleMin = 2.0;
        public const double ElbowAngleMax = 95.0;
        public const double FtToMm = 304.8;
        public const double MinTransitionLength = 0.0001;

        public static MEPElementType GetElementType(Element elem)
        {
            if (elem is Pipe) return MEPElementType.Pipe;
            if (elem is Duct) return MEPElementType.Duct;
            if (elem is CableTray) return MEPElementType.CableTray;
            return MEPElementType.Unknown;
        }

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

            // 仅投影打断点（因为打断点必须在管线上）
            IntersectionResult proj1 = curve.Project(breakPt);
            if (proj1 == null)
            {
                errorMsg = "打断点投影失败（拾取点不在管线上）";
                return result;
            }

            XYZ p0 = proj1.XYZPoint;

            // 计算曲线方向
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

            // ★ 升降侧判定：直接用点击的空间点与打断点的相对位置，不再需要投影到曲线上
            // 如果方向向量的点积 >= 0，说明点在沿管线的正方向上（右侧），反之左侧
            bool liftRight = (sidePt - p0).DotProduct(dir) >= 0;

            double absH = Math.Abs(h_offset_ft);
            double sign = liftRight ? 1.0 : -1.0;

            XYZ curveStart = curve.GetEndPoint(0);
            XYZ curveEnd = curve.GetEndPoint(1);

            double distFromStart = (p0 - curveStart).DotProduct(dir);
            double distToEnd = (curveEnd - p0).DotProduct(dir);

            double tolFt = UnitUtils.ConvertToInternalUnits(0.5, UnitTypeId.Millimeters);
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
                    $"  (打断点距起点 {distFromStart * FtToMm:F1} mm, 距终点 {distToEnd * FtToMm:F1} mm)\n" +
                    $"  建议：减小偏移量至 ≤ {availableSpace * FtToMm:F1} mm，或将打断点向升降侧端点方向移动";
                return result;
            }

            // P1 = P0 + dir*sign*|h| + Z*h
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

        public static double CalcElbowAngleDeg(Connector c1, Connector c2)
        {
            double outerDeg = c1.CoordinateSystem.BasisZ.AngleTo(c2.CoordinateSystem.BasisZ) * 180.0 / Math.PI;
            return 180.0 - outerDeg;
        }

        public static bool IsElbowAngleValid(double elbowDeg)
        {
            return elbowDeg >= ElbowAngleMin && elbowDeg <= ElbowAngleMax;
        }

        public static string Fmt(XYZ p)
        {
            return p == null ? "null" : $"({p.X:F3},{p.Y:F3},{p.Z:F3})";
        }
    }
}