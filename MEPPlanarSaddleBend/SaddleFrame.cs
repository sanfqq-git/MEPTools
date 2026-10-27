using System;
using Autodesk.Revit.DB;

namespace MEPTools.MEPPlanarSaddleBend
{
    /// <summary>多管共用参考系，由 参考管 + P1 + P2 + H + θ 唯一确定。</summary>
    public class SaddleFrame
    {
        public XYZ P1 { get; set; }          // P1 在参考管轴线上的投影
        public XYZ P2 { get; set; }          // P2 在参考管轴线上的投影（用于计算 P2p 打断点）
        public XYZ RefDir { get; set; }      // 参考管在 P1 处的水平切线（单位）
        public XYZ PerpLeft { get; set; }    // Z × RefDir
        public XYZ AlongDir { get; set; }    // sAlong × RefDir（指向 P2 侧）
        public XYZ LatDir { get; set; }      // sLat × PerpLeft（指向 P2 所在侧）
        public XYZ P1LineDir { get; set; }   // P1 侧打断点分布方向
        public XYZ P2LineDir { get; set; }   // P2 侧打断点分布方向
        public int AlongSign { get; set; }
        public int LatSign { get; set; }
        public double AbsOffset { get; set; } // ft
        public double AngleRad { get; set; }
        public double ProjLen { get; set; }   // H × cos(θ) / sin(θ)，每个斜段的沿轴投影
        public double AlongDist { get; set; } // P1→P2 沿轴距离（正）
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
        /// <summary>非致命警告（例如横向共线时采用了默认方向）</summary>
        public string Warning { get; set; }
    }

    public static class SaddleFrameCalculator
    {
        public const double FtToMm = 304.8;

        /// <summary>
        /// 计算全局参考系。
        /// ★ 沿管方向 sAlong 由 p2OnCurve（P2 投影到管上）决定 → 用于打断位置
        /// ★ 横向方向 sLat 由 p2Raw（P2 原始拾取点）决定 → 用于翻弯方向
        /// ★ projLen 用 H×cos(θ)/sin(θ) 计算，θ=90° 无奇异（projLen = 0，直角翻弯）
        /// ★ 分布线角度 φ = θ/2：
        ///     - θ=45° → 22.5°（斜段翻弯，各管斜段沿斜线错开）
        ///     - θ=60° → 30°  （错开量增大）
        ///     - θ=90° → 45°  （直角翻弯，各管翻弯段沿 45° 斜线错开）
        /// </summary>
        public static SaddleFrame Compute(Curve refCurve, XYZ p1Raw, XYZ p2Raw,
            double offsetFt, double angleDeg)
        {
            var f = new SaddleFrame { IsValid = false };

            if (refCurve == null) { f.ErrorMessage = "参考曲线为空"; return f; }
            Line line = refCurve as Line;
            if (line == null) { f.ErrorMessage = "参考管不是直线段"; return f; }

            // P1、P2 分别投影到参考管
            XYZ p1, p2OnCurve;
            if (!TryProject(refCurve, p1Raw, out p1) ||
                !TryProject(refCurve, p2Raw, out p2OnCurve))
            {
                f.ErrorMessage = "P1 或 P2 无法投影到参考管轴线";
                return f;
            }

            // 参考管在水平面内的方向（直接用 Line.Direction，最稳健）
            XYZ d = line.Direction;
            XYZ refDirRaw = new XYZ(d.X, d.Y, 0);
            if (refDirRaw.GetLength() < 1e-9)
            {
                f.ErrorMessage = "参考管在水平方向投影为零（竖直段不支持平面翻弯）";
                return f;
            }
            XYZ refDir = refDirRaw.Normalize();
            XYZ perpLeft = XYZ.BasisZ.CrossProduct(refDir).Normalize();

            // ★ 沿管方向：用 p2OnCurve（P2 在管上的投影点）
            XYZ dCurve = new XYZ(p2OnCurve.X - p1.X, p2OnCurve.Y - p1.Y, 0);
            double dAlong = dCurve.DotProduct(refDir);

            // ★ 横向方向：用 p2Raw（P2 原始拾取点）—— 能正确区分左右的关键
            XYZ dDir = new XYZ(p2Raw.X - p1.X, p2Raw.Y - p1.Y, 0);
            double dPerp = dDir.DotProduct(perpLeft);

            double tolFt = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);

            if (Math.Abs(dAlong) < tolFt)
            {
                f.ErrorMessage = string.Format(
                    "P2 在管上的投影与 P1 重合（沿管方向仅 {0:F2}mm < 1mm），无法确定打断距离。\n" +
                    "请把 P2 点在沿管方向更远的位置。",
                    Math.Abs(dAlong) * FtToMm);
                return f;
            }

            int sAlong = dAlong >= 0 ? 1 : -1;
            int sLat;

            if (Math.Abs(dPerp) >= tolFt)
            {
                sLat = dPerp >= 0 ? 1 : -1;
            }
            else
            {
                sLat = 1;
                f.Warning = string.Format(
                    "P2 与 P1 的水平投影共线（横向偏移仅 {0:F2}mm），" +
                    "已使用默认翻弯方向（+perpLeft）。\n" +
                    "若方向不符，请在平面视图中重新拾取 P2，" +
                    "或把 P2 点在参考管明显的水平侧方。",
                    Math.Abs(dPerp) * FtToMm);
            }

            XYZ alongDir = refDir * sAlong;
            XYZ latDir = perpLeft * sLat;

            // ---- 角度与 projLen（θ 支持 (0, 90]）----
            if (angleDeg <= 0 || angleDeg > 90)
            {
                f.ErrorMessage = "翻弯角度必须介于 (0, 90] 之间（含 90°）";
                return f;
            }

            double angleRad = angleDeg * Math.PI / 180.0;
            double sinA = Math.Sin(angleRad);
            double cosA = Math.Cos(angleRad);

            if (sinA < 1e-9)
            {
                f.ErrorMessage = "翻弯角度过小（sin(θ) ≈ 0）";
                return f;
            }

            // projLen = offsetFt / tan(θ) = offsetFt × cos(θ) / sin(θ)
            // θ = 90° → cos(90°) = 0 → projLen = 0（直角翻弯，无 tan 奇异）
            double projLen = offsetFt * cosA / sinA;
            double alongDist = Math.Abs(dAlong);

            if (alongDist < 2.0 * projLen - tolFt)
            {
                f.ErrorMessage = string.Format(
                    "P1-P2 沿管距离不足：{0:F1}mm < 需要 {1:F1}mm（H={2:F0}mm, θ={3:F0}°）",
                    alongDist * FtToMm, 2.0 * projLen * FtToMm,
                    offsetFt * FtToMm, angleDeg);
                return f;
            }

            // ★ 分布线角度 φ = θ/2（管轴方向与斜段方向之间的角平分线方向）
            //    θ=45° → 22.5°（与之前的 (90-θ)/2 数值相同，不破坏已验证行为）
            //    θ=60° → 30°（比 15° 错开更多）
            //    θ=90° → 45°（不再贴一起）
            double halfAngleRad = angleDeg * 0.5 * Math.PI / 180.0;
            double tanHalf = Math.Tan(halfAngleRad);

            f.P1 = p1;
            f.P2 = p2OnCurve;
            f.RefDir = refDir;
            f.PerpLeft = perpLeft;
            f.AlongDir = alongDir;
            f.LatDir = latDir;
            f.P1LineDir = (latDir - alongDir * tanHalf).Normalize();
            f.P2LineDir = (latDir + alongDir * tanHalf).Normalize();
            f.AlongSign = sAlong;
            f.LatSign = sLat;
            f.AbsOffset = offsetFt;
            f.AngleRad = angleRad;
            f.ProjLen = projLen;
            f.AlongDist = alongDist;
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