using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Electrical;
using MEPTools.Shared; // 必须引入以使用 MEPConfig

namespace MEPTools.MEPConnector.Shared
{
    public static class MEPAlignmentHelper
    {
        public const double FtToMm = 304.8; // 物理常量，保留

        private static readonly double HorizontalEps = MEPConfig.GetDouble("MEPAlignment.HorizontalEps", 1e-6);
        private static readonly double AlignEps = MEPConfig.GetDouble("MEPAlignment.AlignEps", 1e-4);

        /// <summary>
        /// 获取管线的水平方向（归一化）
        /// </summary>
        public static XYZ GetHorizontalDirection(Element elem)
        {
            var loc = elem.Location as LocationCurve;
            if (loc == null || !(loc.Curve is Line line)) return null;

            XYZ dir = new XYZ(line.Direction.X, line.Direction.Y, 0);
            if (dir.GetLength() < HorizontalEps) return null;
            return dir.Normalize();
        }

        /// <summary>
        /// 计算两根管在水平投影上的平行度偏差（叉乘）。0 表示完全平行。
        /// </summary>
        public static double GetParallelDeviation(Element a, Element b)
        {
            XYZ dirA = GetHorizontalDirection(a);
            XYZ dirB = GetHorizontalDirection(b);
            if (dirA == null || dirB == null) return double.MaxValue;

            return Math.Abs(dirA.X * dirB.Y - dirA.Y * dirB.X);
        }

        /// <summary>
        /// 计算 B 管中点到 A 管轴线的水平垂直距离
        /// </summary>
        public static double GetPerpendicularDeviation(Element a, Element b)
        {
            var locA = a.Location as LocationCurve;
            var locB = b.Location as LocationCurve;
            if (locA?.Curve is Line la && locB?.Curve is Line lb)
            {
                XYZ dirA = GetHorizontalDirection(a);
                if (dirA == null) return double.MaxValue;

                XYZ a0 = la.GetEndPoint(0);
                XYZ mb = (lb.GetEndPoint(0) + lb.GetEndPoint(1)) * 0.5;

                XYZ normalA = new XYZ(-dirA.Y, dirA.X, 0).Normalize();
                XYZ vecAtoB = new XYZ(mb.X - a0.X, mb.Y - a0.Y, 0);
                
                return Math.Abs(vecAtoB.DotProduct(normalA));
            }
            return double.MaxValue;
        }

        /// <summary>
        /// 强制将 B 管在水平面上平移到 A 管的轴线上（保持 Z 坐标不变）
        /// </summary>
        public static bool AlignPair(Document doc, Element a, Element b)
        {
            var locA = a.Location as LocationCurve;
            var locB = b.Location as LocationCurve;
            if (locA?.Curve is Line la && locB?.Curve is Line lb)
            {
                XYZ dirA = GetHorizontalDirection(a);
                if (dirA == null) return false;

                XYZ normalA = new XYZ(-dirA.Y, dirA.X, 0).Normalize();
                XYZ a0 = la.GetEndPoint(0);
                XYZ b0 = lb.GetEndPoint(0);
                XYZ b1 = lb.GetEndPoint(1);
                XYZ mb = (b0 + b1) * 0.5;

                XYZ vecAtoB = new XYZ(mb.X - a0.X, mb.Y - a0.Y, 0);
                double dist = vecAtoB.DotProduct(normalA);

                if (Math.Abs(dist) < AlignEps) return true; // 已共线

                XYZ moveVec = -normalA * dist;
                XYZ newB0 = new XYZ(b0.X + moveVec.X, b0.Y + moveVec.Y, b0.Z);
                XYZ newB1 = new XYZ(b1.X + moveVec.X, b1.Y + moveVec.Y, b1.Z);

                try
                {
                    locB.Curve = Line.CreateBound(newB0, newB1);
                    return true;
                }
                catch { return false; }
            }
            return false;
        }
    }
}