using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace MEPTools.MEPPlanarLateralBend
{
    public enum MEPElementType { Pipe, Duct, CableTray, Unknown }

    /// <summary>全局参考坐标系（由参考管 + P1 + P2 + θ 唯一确定）</summary>
    public class GlobalFrame
    {
        public XYZ P1 { get; set; }
        public XYZ RefDir { get; set; }
        public XYZ PerpLeft { get; set; }
        public XYZ AlongDir { get; set; }
        public XYZ LatDir { get; set; }
        public XYZ DiagDir { get; set; }
        public XYZ P0LineDir { get; set; }
        public int AlongSign { get; set; }
        public int LatSign { get; set; }
        public XYZ LateralVector { get; set; }
        public double AbsOffset { get; set; }
        public double AngleRad { get; set; }
        public double ProjLen { get; set; }
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class LateralBendGeometry
    {
        public XYZ P0 { get; set; }
        public XYZ Q { get; set; }
        public XYZ LiftEndPt { get; set; }
        public XYZ FixedEndPt { get; set; }
        public double AvailableSpace { get; set; }
        public XYZ LateralVector { get; set; }
        public bool LiftRight { get; set; }
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
    }

    public static class MEPLateralBendCalculator
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

        /// <summary>
        /// 计算全局参考坐标系。
        /// 【修改】AbsOffset 由 P2 到参考管水平轴线的垂直距离自动计算。
        /// </summary>
        public static GlobalFrame ComputeGlobalFrame(
            Curve refCurve, XYZ p1, XYZ p2, double angleDeg)
        {
            var frame = new GlobalFrame { IsValid = false };

            if (refCurve == null) { frame.ErrorMessage = "参考曲线为空"; return frame; }

            // 校验角度
            if (angleDeg <= 0 || angleDeg > 90)
            {
                frame.ErrorMessage = "拐弯角度必须介于 (0, 90] 之间（含 90°）";
                return frame;
            }

            double angleRad = angleDeg * Math.PI / 180.0;
            double sinA = Math.Sin(angleRad);
            double cosA = Math.Cos(angleRad);
            if (sinA < 1e-9)
            {
                frame.ErrorMessage = "拐弯角度过小（sin(θ) ≈ 0）";
                return frame;
            }

            IntersectionResult proj = refCurve.Project(p1);
            if (proj == null) { frame.ErrorMessage = "P1 投影失败"; return frame; }
            XYZ p1OnCurve = proj.XYZPoint;

            // 参考管在水平面内的方向
            XYZ refDirRaw;
            Line asLine = refCurve as Line;
            if (asLine != null)
            {
                XYZ dd = asLine.Direction;
                refDirRaw = new XYZ(dd.X, dd.Y, 0);
            }
            else
            {
                double t1 = Math.Max(0.0, Math.Min(1.0, proj.Parameter));
                XYZ tangent;
                try { tangent = refCurve.ComputeDerivatives(t1, false).BasisX; }
                catch { frame.ErrorMessage = "无法计算参考管切线"; return frame; }
                refDirRaw = new XYZ(tangent.X, tangent.Y, 0);
            }

            if (refDirRaw.GetLength() < 1e-9)
            {
                frame.ErrorMessage = "参考管水平投影为零（竖直段无法平面拐弯）";
                return frame;
            }
            XYZ refDir = refDirRaw.Normalize();
            XYZ perpLeft = XYZ.BasisZ.CrossProduct(refDir).Normalize();

            // 计算 P2 相对于 P1 在水平面内的偏移向量
            XYZ d = new XYZ(p2.X - p1OnCurve.X, p2.Y - p1OnCurve.Y, 0);
            double dAlong = d.DotProduct(refDir);
            double dPerp = d.DotProduct(perpLeft);

            double tolMm = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);
            if (Math.Abs(dAlong) < tolMm)
            {
                frame.ErrorMessage = "P2 与 P1 沿管方向重合（无法判断平移哪一段）";
                return frame;
            }
            if (Math.Abs(dPerp) < tolMm)
            {
                frame.ErrorMessage = "P2 与 P1 沿管方向共线（无法判断左拐/右拐，且偏移距离为0）";
                return frame;
            }

            int sAlong = dAlong >= 0 ? 1 : -1;
            int sLat = dPerp >= 0 ? 1 : -1;

            XYZ alongDir = refDir * sAlong;
            XYZ latDir = perpLeft * sLat;

            // 【核心修改】AbsOffset 由 P2 的垂直投影距离自动确定
            double absOffset = Math.Abs(dPerp);

            double projLen = absOffset * cosA / sinA;

            // 斜段方向
            XYZ diagRaw = alongDir * projLen + latDir * absOffset;
            XYZ diagDir = (diagRaw.GetLength() < 1e-9) ? alongDir : diagRaw.Normalize();

            // P0 分布方向
            double tanHalf = Math.Tan(angleRad * 0.5);
            XYZ p0LineRaw = latDir - alongDir * tanHalf;
            XYZ p0LineDir = p0LineRaw.Normalize();

            XYZ lateralVec = latDir * absOffset;

            frame.P1 = p1OnCurve;
            frame.RefDir = refDir;
            frame.PerpLeft = perpLeft;
            frame.AlongDir = alongDir;
            frame.LatDir = latDir;
            frame.DiagDir = diagDir;
            frame.P0LineDir = p0LineDir;
            frame.AlongSign = sAlong;
            frame.LatSign = sLat;
            frame.LateralVector = lateralVec;
            frame.AbsOffset = absOffset;
            frame.AngleRad = angleRad;
            frame.ProjLen = projLen;
            frame.IsValid = true;
            return frame;
        }

        // ... 下面保留原来的 ComputePipeGeometry, TryIntersectCurveWithLine 等方法不变 ...
        
        public static LateralBendGeometry ComputePipeGeometry(
            Curve curve, GlobalFrame frame, out string errorMsg)
        {
            errorMsg = null;
            var geom = new LateralBendGeometry { IsValid = false };

            if (curve == null) { errorMsg = "曲线为空"; return geom; }
            if (frame == null || !frame.IsValid) { errorMsg = "全局坐标系无效"; return geom; }

            XYZ p0;
            if (!TryIntersectCurveWithLine(curve, frame.P1, frame.P0LineDir, out p0))
            {
                errorMsg = "管线与 P0 分布线无交点（可能：曲线非直线 / 交点不在管段内）";
                return geom;
            }

            XYZ cs = curve.GetEndPoint(0);
            XYZ ce = curve.GetEndPoint(1);
            XYZ alongDir = frame.AlongDir;

            double d0 = (cs - p0).DotProduct(alongDir);
            double d1 = (ce - p0).DotProduct(alongDir);

            XYZ liftEndPt, fixedEndPt;
            double availableSpace;
            if (d0 >= 0 && d1 < 0) { liftEndPt = cs; fixedEndPt = ce; availableSpace = d0; }
            else if (d1 >= 0 && d0 < 0) { liftEndPt = ce; fixedEndPt = cs; availableSpace = d1; }
            else
            {
                if (Math.Abs(d0) > Math.Abs(d1)) { liftEndPt = cs; fixedEndPt = ce; availableSpace = Math.Abs(d0); }
                else { liftEndPt = ce; fixedEndPt = cs; availableSpace = Math.Abs(d1); }
            }

            double tolFt = UnitUtils.ConvertToInternalUnits(0.5, UnitTypeId.Millimeters);

            if (availableSpace < frame.ProjLen - tolFt)
            {
                errorMsg =
                    $"打断点距离平移侧端点空间不足：\n" +
                    $"  需要 = {frame.ProjLen * FtToMm:F1} mm（θ={(frame.AngleRad * 180 / Math.PI):F1}°）\n" +
                    $"  可用 = {availableSpace * FtToMm:F1} mm";
                return geom;
            }

            XYZ q = p0 + frame.AlongDir * frame.ProjLen + frame.LatDir * frame.AbsOffset;

            if ((q - p0).GetLength() < MinTransitionLength)
            {
                errorMsg = "过渡段长度过短（偏移量太小）";
                return geom;
            }

            geom.P0 = p0;
            geom.Q = q;
            geom.LiftEndPt = liftEndPt;
            geom.FixedEndPt = fixedEndPt;
            geom.AvailableSpace = availableSpace;
            geom.LateralVector = frame.LateralVector;
            geom.LiftRight = frame.AlongSign > 0;
            geom.IsValid = true;
            return geom;
        }

        private static bool TryIntersectCurveWithLine(
            Curve curve, XYZ lineOrigin, XYZ lineDir, out XYZ p0)
        {
            p0 = null;

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
            if (Math.Abs(det) < 1e-9) return false;

            double bx = lineOrigin.X - cs.X;
            double by = lineOrigin.Y - cs.Y;
            double t0 = (bx * ldir.Y - by * ldir.X) / det;

            if (t0 < -1e-6 || t0 > cLen + 1e-6) return false;
            t0 = Math.Max(0.0, Math.Min(cLen, t0));

            p0 = cs + cd * t0;
            return true;
        }

        public static double CalcElbowAngleDeg(Connector c1, Connector c2)
        {
            double outerDeg = c1.CoordinateSystem.BasisZ
                .AngleTo(c2.CoordinateSystem.BasisZ) * 180.0 / Math.PI;
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