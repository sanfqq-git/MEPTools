using System;
using Autodesk.Revit.DB;

namespace MEPTools.MEPPlanarSaddleBend
{
    public class SaddleFrame
    {
        public XYZ P1 { get; set; }
        public XYZ P2 { get; set; }
        public XYZ RefDir { get; set; }
        public XYZ PerpLeft { get; set; }
        public XYZ AlongDir { get; set; }
        public XYZ LatDir { get; set; }
        public XYZ P1LineDir { get; set; }   // 分布线方向（现在是垂直方向）
        public XYZ P2LineDir { get; set; }   // 分布线方向（现在是垂直方向）
        public int AlongSign { get; set; }
        public int LatSign { get; set; }
        public double AbsOffset { get; set; } // ft
        public double AngleRad { get; set; }
        public double ProjLen { get; set; }
        public double AlongDist { get; set; }
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
    }

    public static class SaddleFrameCalculator
    {
        public const double FtToMm = 304.8;

        public static SaddleFrame Compute(Curve refCurve, XYZ p1Raw, XYZ p2Raw, double angleDeg)
        {
            var f = new SaddleFrame { IsValid = false };

            if (refCurve == null) { f.ErrorMessage = "参考曲线为空"; return f; }
            Line line = refCurve as Line;
            if (line == null) { f.ErrorMessage = "参考管不是直线段"; return f; }

            XYZ p1, p2OnCurve;
            if (!TryProject(refCurve, p1Raw, out p1) || !TryProject(refCurve, p2Raw, out p2OnCurve))
            {
                f.ErrorMessage = "P1 或 P2 无法投影到参考管轴线";
                return f;
            }

            XYZ d = line.Direction;
            XYZ refDirRaw = new XYZ(d.X, d.Y, 0);
            if (refDirRaw.GetLength() < 1e-9)
            {
                f.ErrorMessage = "参考管在水平方向投影为零";
                return f;
            }
            XYZ refDir = refDirRaw.Normalize();
            XYZ perpLeft = XYZ.BasisZ.CrossProduct(refDir).Normalize();

            XYZ dCurve = new XYZ(p2OnCurve.X - p1.X, p2OnCurve.Y - p1.Y, 0);
            double dAlong = dCurve.DotProduct(refDir);
            XYZ dDir = new XYZ(p2Raw.X - p1.X, p2Raw.Y - p1.Y, 0);
            double dPerp = dDir.DotProduct(perpLeft);

            double tolFt = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);

            if (Math.Abs(dAlong) < tolFt) { f.ErrorMessage = "P2 投影与 P1 重合，无法确定打断距离"; return f; }

            double offsetFt = Math.Abs(dPerp);
            if (offsetFt < tolFt) { f.ErrorMessage = "P2 横向距离过小，无法确定翻弯高度"; return f; }

            if (angleDeg <= 0 || angleDeg > 90) { f.ErrorMessage = "角度必须介于 0~90 度"; return f; }

            double angleRad = angleDeg * Math.PI / 180.0;
            double sinA = Math.Sin(angleRad);
            double cosA = Math.Cos(angleRad);

            if (sinA < 1e-9) { f.ErrorMessage = "角度过小"; return f; }

            double projLen = offsetFt * cosA / sinA;

            int sAlong = dAlong >= 0 ? 1 : -1;
            int sLat = dPerp >= 0 ? 1 : -1;

            XYZ alongDir = refDir * sAlong;
            XYZ latDir = perpLeft * sLat;

            f.P1 = p1;
            f.P2 = p2OnCurve;
            f.RefDir = refDir;
            f.PerpLeft = perpLeft;
            f.AlongDir = alongDir;
            f.LatDir = latDir;
            
            // ★ 关键修改：分布线垂直于管轴线
            f.P1LineDir = perpLeft; 
            f.P2LineDir = perpLeft; 

            f.AlongSign = sAlong;
            f.LatSign = sLat;
            f.AbsOffset = offsetFt;
            f.AngleRad = angleRad;
            f.ProjLen = projLen;
            f.AlongDist = Math.Abs(dAlong);
            f.IsValid = true;
            return f;
        }

        private static bool TryProject(Curve c, XYZ p, out XYZ result)
        {
            result = null;
            if (c == null || p == null) return false;
            try
            {
                var pr = c.Project(p);
                if (pr == null || pr.XYZPoint == null) return false;
                result = pr.XYZPoint;
                return true;
            }
            catch { return false; }
        }
        
        public static string Fmt(XYZ p)
        {
            return p == null ? "null" : string.Format("({0:F3},{1:F3},{2:F3})", p.X, p.Y, p.Z);
        }
    }
}