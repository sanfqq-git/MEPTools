using System;
using Autodesk.Revit.DB;

namespace MEPTools.MEPConnector.Spatial.Core
{
    /// <summary>
    /// 平面（无高差）共线场景的 B→A 水平对齐工具。
    /// 平行度容差与 ConnectorCalculator 保持一致（1e-2）。
    /// </summary>
    public static class MEPAligner
    {
        // 与 ConnectorCalculator.GetHorizontalDeviation / ComputeCollinear 一致
        private const double ParallelTolCross = 1e-2;

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
                // 【修复】使用 Revit 原生的元素移动方法，保证相连的管件（弯头、三通等）同步联动
                // 如果因拓扑约束（如两端都硬性连接）导致 Revit 抛出异常，
                // 应在外部捕获，或者在实际业务层先断开近端/远端连接再平移。
                ElementTransformUtils.MoveElement(doc, b.Id, moveVec);
                return true;
            }
            catch 
            { 
                return false; 
            }
        }
    }
}