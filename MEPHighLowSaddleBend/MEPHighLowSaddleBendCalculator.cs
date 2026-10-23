using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

// 解决 Connector 命名冲突
using Connector = Autodesk.Revit.DB.Connector;

namespace MEPTools.MEPHighLowSaddleBend
{
    /// <summary>MEP 管线类型</summary>
    public enum MEPElementType
    {
        Pipe,
        Duct,
        CableTray,
        Unknown
    }

    /// <summary>45° 翻弯几何计算结果</summary>
    public class OffsetGeometry
    {
        public XYZ PA { get; set; }                 // 打断点 A（原管线）
        public XYZ PB { get; set; }                 // 打断点 B（原管线）
        public XYZ P3 { get; set; }                 // 斜段 a 顶端（抬升后）
        public XYZ P4 { get; set; }                 // 斜段 c 顶端（抬升后）
        public XYZ Dir { get; set; }                // 管线方向（PA→PB）
        public double Length { get; set; }          // PA~PB 有效长度
        public double RequiredLength { get; set; }  // 2 × |offset|
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>45° 翻弯几何计算器（无状态）</summary>
    public static class MEPOffsetBendCalculator
    {
        /// <summary>中间水平段最小长度（ft）</summary>
        public const double MinMidLength = 0.001;

        /// <summary>弯头内角允许范围（度）</summary>
        public const double ElbowAngleMin = 2.0;
        public const double ElbowAngleMax = 95.0;

        /// <summary>连接器匹配点容差（ft ≈ 1.5mm）</summary>
        public const double ConnectorMatchTolerance = 0.005;

        /// <summary>英尺 → 毫米</summary>
        public const double FtToMm = 304.8;

        /// <summary>判断元素类型</summary>
        public static MEPElementType GetElementType(Element elem)
        {
            if (elem is Pipe) return MEPElementType.Pipe;
            if (elem is Duct) return MEPElementType.Duct;
            if (elem is CableTray) return MEPElementType.CableTray;
            return MEPElementType.Unknown;
        }

        /// <summary>计算 45° 翻弯几何关键点并校验间距</summary>
        public static OffsetGeometry Compute(Curve curve, XYZ pickPt1, XYZ pickPt2,
            double h_offset_ft, out string errorMsg)
        {
            errorMsg = null;
            var result = new OffsetGeometry { IsValid = false };

            if (curve == null)
            {
                errorMsg = "曲线为空";
                return result;
            }

            IntersectionResult proj1 = curve.Project(pickPt1);
            IntersectionResult proj2 = curve.Project(pickPt2);
            if (proj1 == null || proj2 == null)
            {
                errorMsg = "点位投影失败（拾取点不在管线上）";
                return result;
            }

            XYZ pA = proj1.XYZPoint;
            XYZ pB = proj2.XYZPoint;
            double tA = proj1.Parameter;
            double tB = proj2.Parameter;

            if (tA > tB)
            {
                XYZ tmpP = pA; pA = pB; pB = tmpP;
                double tmpT = tA; tA = tB; tB = tmpT;
            }

            XYZ dir = curve.ComputeDerivatives(tA, false).BasisX.Normalize();
            double L = (pB - pA).DotProduct(dir);
            double need = 2.0 * Math.Abs(h_offset_ft);

            result.PA = pA;
            result.PB = pB;
            result.Dir = dir;
            result.Length = L;
            result.RequiredLength = need;

            if (L < need + MinMidLength)
            {
                errorMsg = $"间距不足：{L * FtToMm:F1} mm < {need * FtToMm:F1} mm (2×|offset|)";
                return result;
            }

            double absOff = Math.Abs(h_offset_ft);
            result.P3 = pA + dir * absOff + XYZ.BasisZ * h_offset_ft;
            result.P4 = pB - dir * absOff + XYZ.BasisZ * h_offset_ft;
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