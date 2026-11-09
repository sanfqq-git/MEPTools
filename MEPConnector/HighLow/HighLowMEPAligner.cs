using System;
using Autodesk.Revit.DB;

namespace MEPTools.MEPConnector.HighLow
{
    /// <summary>
    /// 高差连接场景的 B→A 水平对齐工具。
    /// 保持原 MEPConnectorUtils.AlignPair 的平行度容差（1e-3），行为与原版一致。
    /// </summary>
    public static class HighLowMEPAligner
    {
        // 保持原共享类行为，不改变高差插件语义
        private const double ParallelTolCross = 1e-3;

        public static bool AlignPair(Document doc, Element a, Element b)
        {
            var la = (a.Location as LocationCurve)?.Curve as Line;
            var lb = (b.Location as LocationCurve)?.Curve as Line;
            if (la == null || lb == null) return false;

            XYZ dirA = new XYZ(la.Direction.X, la.Direction.Y, 0);
            XYZ dirB = new XYZ(lb.Direction.X, lb.Direction.Y, 0);
            if (dirA.GetLength() < 1e-6 || dirB.GetLength() < 1e-6) return false;
            dirA = dirA.Normalize();
            dirB = dirB.Normalize();

            double cross = dirA.X * dirB.Y - dirA.Y * dirB.X;
            if (Math.Abs(cross) > ParallelTolCross) return false;

            XYZ a0 = la.GetEndPoint(0);
            XYZ b0 = lb.GetEndPoint(0);
            XYZ b1 = lb.GetEndPoint(1);
            XYZ mb = (b0 + b1) * 0.5;

            XYZ normalA = new XYZ(-dirA.Y, dirA.X, 0);
            XYZ vecAtoB = new XYZ(mb.X - a0.X, mb.Y - a0.Y, 0);
            double dist = vecAtoB.DotProduct(normalA);

            if (Math.Abs(dist) < 1e-4) return true;

            XYZ moveVec = -normalA * dist;

            try
            {
                LocationCurve lcB = b.Location as LocationCurve;
                XYZ newB0 = new XYZ(b0.X + moveVec.X, b0.Y + moveVec.Y, b0.Z);
                XYZ newB1 = new XYZ(b1.X + moveVec.X, b1.Y + moveVec.Y, b1.Z);
                lcB.Curve = Line.CreateBound(newB0, newB1);
                return true;
            }
            catch { return false; }
        }
    }
}