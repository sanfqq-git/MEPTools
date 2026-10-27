using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace MEPTools.MEPPlanarSaddleBend
{
    public enum MEPElementType { Pipe, Duct, CableTray, Unknown }

    public class SaddleBendResult
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }

        public Element Element { get; set; }
        public Curve OriginalCurve { get; set; }
        public XYZ OriginalEnd0 { get; set; }
        public XYZ OriginalEnd1 { get; set; }
        public XYZ AxisDir { get; set; }

        public XYZ P0 { get; set; }   // 该管 ∩ P1LineDir 分布线
        public XYZ P2p { get; set; }  // 该管 ∩ P2LineDir 分布线
        public XYZ A { get; set; }    // 斜段1顶点
        public XYZ B { get; set; }    // 斜段2起点
        public double MidLen { get; set; }

        /// <summary>4 点：[0]P0  [1]A  [2]B  [3]P2p</summary>
        public XYZ[] Points { get; set; }

        /// <summary>P0 超出原管 E0 侧，需要向 E0 外侧延长</summary>
        public bool ExtendE0 { get; set; }
        /// <summary>P2p 超出原管 E1 侧，需要向 E1 外侧延长</summary>
        public bool ExtendE1 { get; set; }
    }

    public static class MEPSaddleBendCalculator
    {
        public const double FtToMm = 304.8;

        public static MEPElementType GetElementType(Element elem)
        {
            if (elem is Pipe) return MEPElementType.Pipe;
            if (elem is Duct) return MEPElementType.Duct;
            if (elem is CableTray) return MEPElementType.CableTray;
            return MEPElementType.Unknown;
        }

        public static SaddleBendResult ComputePipeGeometry(Element elem, SaddleFrame f)
        {
            var r = new SaddleBendResult { IsValid = false, Element = elem };

            if (elem == null) { r.ErrorMessage = "元素为空"; return r; }
            if (f == null || !f.IsValid) { r.ErrorMessage = "全局 frame 无效"; return r; }
            if (GetElementType(elem) == MEPElementType.Unknown)
            {
                r.ErrorMessage = "元素类型不支持（仅 Pipe / Duct / CableTray）";
                return r;
            }

            var mc = elem as MEPCurve;
            var lc = (mc == null) ? null : (mc.Location as LocationCurve);
            var line = (lc == null) ? null : (lc.Curve as Line);
            if (line == null) { r.ErrorMessage = "仅支持直线段"; return r; }

            XYZ e0 = line.GetEndPoint(0);
            XYZ e1 = line.GetEndPoint(1);
            XYZ rawAxis = e1 - e0;
            double len = rawAxis.GetLength();
            if (len < 1e-9) { r.ErrorMessage = "管段长度为零"; return r; }
            XYZ axis = rawAxis / len;

            r.OriginalCurve = line;
            r.OriginalEnd0 = e0;
            r.OriginalEnd1 = e1;
            r.AxisDir = axis;

            // P0 = 该管 ∩ (过 frame.P1 沿 P1LineDir) —— 允许越界，越界时 ExtendE0=true
            XYZ p0;
            if (!TryIntersectCurveWithLine(line, f.P1, f.P1LineDir, true, out p0))
            {
                r.ErrorMessage = "管段与 P1 侧分布线无法求交";
                return r;
            }

            // P2p = 该管 ∩ (过 frame.P2 沿 P2LineDir) —— 允许越界，越界时 ExtendE1=true
            XYZ p2p;
            if (!TryIntersectCurveWithLine(line, f.P2, f.P2LineDir, true, out p2p))
            {
                r.ErrorMessage = "管段与 P2 侧分布线无法求交";
                return r;
            }

            // 沿轴距离 D（恒正，因为 AlongDir 已指向 P2 侧）
            double D = (p2p - p0).DotProduct(f.AlongDir);
            double tol = UnitUtils.ConvertToInternalUnits(0.5, UnitTypeId.Millimeters);

            if (D < 2.0 * f.ProjLen - tol)
            {
                r.ErrorMessage = string.Format(
                    "该管打断距离不足：{0:F1}mm < 需要 {1:F1}mm（H={2:F0}mm, θ={3:F0}°）",
                    D * FtToMm, 2.0 * f.ProjLen * FtToMm,
                    f.AbsOffset * FtToMm, f.AngleRad * 180.0 / Math.PI);
                return r;
            }

            double midLen = Math.Max(0.0, D - 2.0 * f.ProjLen);

            XYZ a = p0 + f.AlongDir * f.ProjLen + f.LatDir * f.AbsOffset;
            XYZ b = a + f.AlongDir * midLen;

            r.P0 = p0;
            r.A = a;
            r.B = b;
            r.P2p = p2p;
            r.MidLen = midLen;
            r.Points = new XYZ[] { p0, a, b, p2p };

            // 判断越界：t 参数沿 AxisDir（从 e0 起算）
            double t0 = (p0 - e0).DotProduct(axis);
            double t2 = (p2p - e0).DotProduct(axis);

            r.ExtendE0 = false;
            r.ExtendE1 = false;

            if (t0 < -tol) r.ExtendE0 = true;
            if (t0 > len + tol) r.ExtendE1 = true;   // 理论上不该发生（AlongDir 已定向）
            if (t2 < -tol) r.ExtendE0 = true;
            if (t2 > len + tol) r.ExtendE1 = true;

            r.IsValid = true;
            return r;
        }

        /// <summary>
        /// 曲线与"过 lineOrigin 沿 lineDir 的无界直线"在 XY 平面求交。
        /// allowOutOfRange=false：交点必须落在管段参数范围内；
        /// allowOutOfRange=true ：允许在管段延长线上，最多越界 ±1e6 ft。
        /// </summary>
        private static bool TryIntersectCurveWithLine(Curve curve, XYZ lineOrigin,
            XYZ lineDir, bool allowOutOfRange, out XYZ result)
        {
            result = null;
            if (!(curve is Line)) return false;

            XYZ cs = curve.GetEndPoint(0);
            XYZ ce = curve.GetEndPoint(1);
            XYZ cv = ce - cs;
            double cLen = cv.GetLength();
            if (cLen < 1e-9) return false;
            XYZ cd = cv / cLen;

            XYZ ldir = new XYZ(lineDir.X, lineDir.Y, 0);
            if (ldir.GetLength() < 1e-9) return false;
            ldir = ldir.Normalize();

            XYZ cdir = new XYZ(cd.X, cd.Y, 0);
            if (cdir.GetLength() < 1e-9) return false;
            cdir = cdir.Normalize();

            double det = cdir.X * ldir.Y - cdir.Y * ldir.X;
            if (Math.Abs(det) < 1e-9) return false;   // 平行

            double bx = lineOrigin.X - cs.X;
            double by = lineOrigin.Y - cs.Y;
            double t0 = (bx * ldir.Y - by * ldir.X) / det;

            if (!allowOutOfRange)
            {
                if (t0 < -1e-6 || t0 > cLen + 1e-6) return false;
            }
            else
            {
                const double huge = 1e6;
                if (t0 < -huge || t0 > cLen + huge) return false;
                t0 = Math.Max(-huge, Math.Min(cLen + huge, t0));
            }

            result = cs + cd * t0;
            return true;
        }

        public static string Fmt(XYZ p)
        {
            return p == null ? "null" : string.Format("({0:F3},{1:F3},{2:F3})", p.X, p.Y, p.Z);
        }
    }
}