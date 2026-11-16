using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace MEPTools.MEPConnector.HighLow
{
    public static class HighLowMEPAligner
    {
        private const double ParallelTolCross = 1e-3;

        /// <summary>"近端"判定默认容差：20mm。与 GetConnectorAtPoint 默认容差保持一致。</summary>
        private const double DefaultDisconnectTolMm = 20.0;

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
            XYZ a1 = la.GetEndPoint(1);
            XYZ aMid = (a0 + a1) * 0.5;

            XYZ b0 = lb.GetEndPoint(0);
            XYZ b1 = lb.GetEndPoint(1);
            
            // 判断 B 管哪一端离 A 更近（近端）
            bool b0IsNear = b0.DistanceTo(aMid) < b1.DistanceTo(aMid);
            XYZ bNear = b0IsNear ? b0 : b1;

            XYZ mb = (b0 + b1) * 0.5;
            XYZ normalA = new XYZ(-dirA.Y, dirA.X, 0);
            XYZ vecAtoB = new XYZ(mb.X - a0.X, mb.Y - a0.Y, 0);
            double dist = vecAtoB.DotProduct(normalA);

            if (Math.Abs(dist) < 1e-4) return true;

            XYZ moveVec = -normalA * dist;

            try
            {
                // 1. 仅断开 B 组近端连接，保留远端连接
                DisconnectConnectorsAt(b, bNear);

                // 2. 整体平移 B 元素。
                // 必须整管平移，若只平移近端会导致 B 管倾斜，后续 45度斜管将错位且无法生成弯头。
                // 使用 Revit 原生 API 平移，能最大程度保留拓扑联动。
                ElementTransformUtils.MoveElement(doc, b.Id, moveVec);
                
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 断开元素上的【全部】连接器。
        /// 语义与旧版一致，仅供 AlignPair 等需要"整体平移/重置"的场景使用。
        /// </summary>
        public static void DisconnectConnectors(Element e)
        {
            var mep = e as MEPCurve;
            if (mep == null || mep.ConnectorManager == null) return;

            var toDisconnect = new List<Connector>();
            foreach (Connector c in mep.ConnectorManager.Connectors)
            {
                if (c.IsConnected) toDisconnect.Add(c);
            }
            DisconnectConnectorList(toDisconnect);
        }

        /// <summary>
        /// 只断开距 <paramref name="atPoint"/> 指定容差范围内的连接器（即"近端"），
        /// 保留远端原有连接。
        /// </summary>
        public static void DisconnectConnectorsAt(
            Element e, XYZ atPoint, double tolMm = DefaultDisconnectTolMm)
        {
            var mep = e as MEPCurve;
            if (mep == null || mep.ConnectorManager == null || atPoint == null) return;

            double tol = UnitUtils.ConvertToInternalUnits(tolMm, UnitTypeId.Millimeters);
            double tolSq = tol * tol;

            var toDisconnect = new List<Connector>();
            foreach (Connector c in mep.ConnectorManager.Connectors)
            {
                if (!c.IsConnected) continue;

                XYZ d = c.Origin - atPoint;
                double dSq = d.X * d.X + d.Y * d.Y + d.Z * d.Z;
                if (dSq > tolSq) continue;

                toDisconnect.Add(c);
            }
            DisconnectConnectorList(toDisconnect);
        }

        private static void DisconnectConnectorList(List<Connector> connectors)
        {
            if (connectors == null || connectors.Count == 0) return;

            foreach (var c in connectors)
            {
                if (c == null || !c.IsConnected) continue;

                var refs = new List<Connector>();
                foreach (Connector r in c.AllRefs) refs.Add(r);

                foreach (var r in refs)
                {
                    if (r == null) continue;
                    try { c.DisconnectFrom(r); }
                    catch { }
                }
            }
        }
    }
}