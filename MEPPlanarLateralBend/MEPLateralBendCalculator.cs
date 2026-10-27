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
        /// <summary>用户拾取的打断点（投影到参考管中轴线）</summary>
        public XYZ P1 { get; set; }
        /// <summary>参考方向 = 参考管在 P1 处的水平切线</summary>
        public XYZ RefDir { get; set; }
        /// <summary>左侧方向 = Z × RefDir</summary>
        public XYZ PerpLeft { get; set; }
        /// <summary>沿管方向（指向平移侧）= sAlong × RefDir</summary>
        public XYZ AlongDir { get; set; }
        /// <summary>横向方向（指向拐弯侧）= sLat × PerpLeft</summary>
        public XYZ LatDir { get; set; }
        /// <summary>斜段方向（取决于 θ：θ=45° 时为 45° 斜；θ=90° 时为纯横向）</summary>
        public XYZ DiagDir { get; set; }
        /// <summary>P0 分布方向 = normalize(LatDir − tan(θ/2) × AlongDir)</summary>
        public XYZ P0LineDir { get; set; }
        /// <summary>+1 = 下游（RefDir 正向），-1 = 上游</summary>
        public int AlongSign { get; set; }
        /// <summary>+1 = 左拐，-1 = 右拐</summary>
        public int LatSign { get; set; }
        /// <summary>全局横向平移向量 = LatDir × |offset|</summary>
        public XYZ LateralVector { get; set; }
        /// <summary>|offset|（ft）</summary>
        public double AbsOffset { get; set; }
        /// <summary>斜段角度（弧度）</summary>
        public double AngleRad { get; set; }
        /// <summary>斜段沿管方向的投影 = H × cos(θ) / sin(θ)（ft）</summary>
        public double ProjLen { get; set; }
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>单根管的平面拐弯几何</summary>
    public class LateralBendGeometry
    {
        /// <summary>打断点 = 该管 ∩ 过 P1 沿 P0LineDir 的直线</summary>
        public XYZ P0 { get; set; }
        /// <summary>斜段终点 = P0 + ProjLen × AlongDir + |offset| × LatDir</summary>
        public XYZ Q { get; set; }
        /// <summary>平移侧端点（原路径）</summary>
        public XYZ LiftEndPt { get; set; }
        /// <summary>固定侧端点（原路径）</summary>
        public XYZ FixedEndPt { get; set; }
        /// <summary>P0 到平移侧端点的沿管距离</summary>
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
        /// - refDir     = 参考管在 P1 处的水平切线
        /// - perpLeft   = Z × refDir
        /// - alongDir   = sAlong × refDir（指向平移侧）
        /// - latDir     = sLat × perpLeft（指向拐弯侧）
        /// - projLen    = |offset| / tan(θ) = |offset| × cos(θ) / sin(θ)
        /// - diagDir    = normalize(alongDir × projLen + latDir × |offset|)（斜段方向）
        /// - p0LineDir  = normalize(latDir − tan(θ/2) × alongDir)（P0 分布方向，角平分线）
        /// - lateralVec = latDir × |offset|
        /// </summary>
        public static GlobalFrame ComputeGlobalFrame(
            Curve refCurve, XYZ p1, XYZ p2, double offset_ft, double angleDeg)
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

            // 参考管在水平面内的方向（用 Line.Direction 更稳健）
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
                frame.ErrorMessage = "P2 与 P1 沿管方向共线（无法判断左拐/右拐）";
                return frame;
            }

            int sAlong = dAlong >= 0 ? 1 : -1;
            int sLat = dPerp >= 0 ? 1 : -1;

            XYZ alongDir = refDir * sAlong;
            XYZ latDir = perpLeft * sLat;

            double absOffset = Math.Abs(offset_ft);

            // projLen = |offset| / tan(θ) = |offset| × cos(θ) / sin(θ)
            // θ = 90° → cos(90°) = 0 → projLen = 0（L 形直角拐弯，无 tan 奇异）
            double projLen = absOffset * cosA / sinA;

            // 斜段方向（用 projLen、absOffset 的组合决定；θ=45° 时与 (along+lat)/√2 等价）
            XYZ diagRaw = alongDir * projLen + latDir * absOffset;
            XYZ diagDir = (diagRaw.GetLength() < 1e-9) ? alongDir : diagRaw.Normalize();

            // P0 分布方向 = normalize(latDir − tan(θ/2) × alongDir)
            // θ=45° → tan(22.5°) = √2−1（与旧版本一致）
            // θ=90° → tan(45°) = 1
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

        /// <summary>
        /// 计算单根管的拐弯几何：
        /// - P0 = 该管中轴线 ∩ 过 P1 沿 P0LineDir 的直线
        /// - Q  = P0 + ProjLen × AlongDir + |offset| × LatDir
        /// </summary>
        public static LateralBendGeometry ComputePipeGeometry(
            Curve curve, GlobalFrame frame, out string errorMsg)
        {
            errorMsg = null;
            var geom = new LateralBendGeometry { IsValid = false };

            if (curve == null) { errorMsg = "曲线为空"; return geom; }
            if (frame == null || !frame.IsValid) { errorMsg = "全局坐标系无效"; return geom; }

            // ---------- P0 = 管 i ∩ (过 P1 沿 P0LineDir 的直线) ----------
            XYZ p0;
            if (!TryIntersectCurveWithLine(curve, frame.P1, frame.P0LineDir, out p0))
            {
                errorMsg = "管线与 P0 分布线无交点（可能：曲线非直线 / 交点不在管段内）";
                return geom;
            }

            // ---------- 沿 AlongDir 判断平移侧 / 固定侧端点 ----------
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

            // 可用空间必须 ≥ ProjLen（否则 Q 会超过 LiftEndPt）
            if (availableSpace < frame.ProjLen - tolFt)
            {
                errorMsg =
                    $"打断点距离平移侧端点空间不足：\n" +
                    $"  需要 = {frame.ProjLen * FtToMm:F1} mm（θ={(frame.AngleRad * 180 / Math.PI):F1}°）\n" +
                    $"  可用 = {availableSpace * FtToMm:F1} mm";
                return geom;
            }

            // ---------- Q = P0 + ProjLen × AlongDir + |offset| × LatDir ----------
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

        /// <summary>
        /// 求直线曲线 C 与"过 lineOrigin 沿 lineDir 的无界直线 L"在 X-Y 平面的交点。
        /// 仅支持 Line 类型曲线；交点必须落在管段参数范围内。
        /// </summary>
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