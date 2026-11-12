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
        public double AngleDeg { get; set; }
        public bool LiftRight { get; set; }
        public double DistFromStart { get; set; }
        public double DistToEnd { get; set; }
        public double AvailableSpace { get; set; }
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
        
        // 用于记录高度差与打断点偏移量
        public double DeltaZ { get; set; }
        public double OffsetAmount { get; set; }
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
            double h_offset_ft, double angleDeg, double refZ, out string errorMsg)
        {
            errorMsg = null;
            var result = new UpDownGeometry { IsValid = false };

            if (angleDeg <= 0.0 || angleDeg > 90.0)
            {
                errorMsg = $"连接角度必须在 (0°, 90°] 之间（当前 {angleDeg:F2}°）";
                return result;
            }

            if (curve == null)
            {
                errorMsg = "曲线为空";
                return result;
            }

            IntersectionResult proj1 = curve.Project(breakPt);
            if (proj1 == null)
            {
                errorMsg = "打断点投影失败（拾取点不在管线上）";
                return result;
            }

            XYZ p0 = proj1.XYZPoint;

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

            bool liftRight = (sidePt - p0).DotProduct(dir) >= 0;
            double sign = liftRight ? 1.0 : -1.0;
            
            // ★ 提前计算 rad，确保后续代码都能访问
            double rad = angleDeg * Math.PI / 180.0;

            // ★ 多层错开逻辑：根据参考标高 refZ 计算高度差
            double deltaZ = p0.Z - refZ;
            double offsetAmount = 0;
            
            // 仅对水平管段应用错开逻辑（避免倾斜管段偏离）
            if (Math.Abs(dir.Z) < 1e-4 && Math.Abs(deltaZ) > 1e-6)
            {
                // 统一公式：tan(A/2)
                // 对于 45°，tan(22.5°) ≈ 0.414，下层打断点向翻弯方向移动 0.414*层高，使 D2=D1
                // 对于 90°，tan(45°) = 1，下层打断点向翻弯方向移动 1*层高，垂直段完全错开
                double angleFactor = Math.Tan(rad / 2.0);
                
                // 将偏移量作用于打断点（沿翻弯方向）
                offsetAmount = -deltaZ * sign * angleFactor * Math.Sign(h_offset_ft);

                p0 = p0 + dir * offsetAmount;
            }

            XYZ curveStart = curve.GetEndPoint(0);
            XYZ curveEnd = curve.GetEndPoint(1);
            
            // ★ 重新计算偏移后的距离
            double distFromStart = (p0 - curveStart).DotProduct(dir);
            double distToEnd = (curveEnd - p0).DotProduct(dir);

            double tolFt = UnitUtils.ConvertToInternalUnits(0.5, UnitTypeId.Millimeters);
            double availableSpace = liftRight ? distToEnd : distFromStart;

            // 沿管线方向的水平偏移：|h| / tan(θ)
            double horizOffset = angleDeg >= 89.999
                ? 0.0
                : Math.Abs(h_offset_ft) / Math.Tan(rad);

            result.DistFromStart = distFromStart;
            result.DistToEnd = distToEnd;
            result.AvailableSpace = availableSpace;
            result.DeltaZ = deltaZ;
            result.OffsetAmount = offsetAmount;

            if (availableSpace < horizOffset - tolFt)
            {
                errorMsg =
                    $"打断点距离升降侧端点空间不足：\n" +
                    $"  需要水平空间 = {horizOffset * FtToMm:F1} mm（角度 {angleDeg:F1}°）\n" +
                    $"  可用空间 = {availableSpace * FtToMm:F1} mm\n" +
                    $"  (打断点距起点 {distFromStart * FtToMm:F1} mm, 距终点 {distToEnd * FtToMm:F1} mm)\n" +
                    $"  建议：减小偏移量或增大角度（更陡），或将打断点向升降侧端点方向移动";
                return result;
            }

            XYZ p1 = p0 + dir * (sign * horizOffset) + XYZ.BasisZ * h_offset_ft;

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
            result.AngleDeg = angleDeg;
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