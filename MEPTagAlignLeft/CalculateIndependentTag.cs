using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace TagAlignLeft
{
    /// <summary>
    /// 计算类：基于 TagHeadPosition 计算"左对齐 + 按管序从上往下堆叠"的移动向量。
    /// 不依赖 BoundingBox，避免引线导致的结果漂移，保证多次运行幂等。
    /// </summary>
    public static class CalculateIndependentTag
    {
        private const double Eps = 1e-9;

        private class TagInfo
        {
            public IndependentTag Tag;
            public XYZ HostPos;   // 宿主元素中心（模型坐标），仅用于排序
            public XYZ HeadPos;   // Tag 头插入点（TagHeadPosition），用于定位
        }

        /// <summary>
        /// 左对齐 + 按管道顺序从上往下垂直堆叠。
        /// rowSpacing 表示相邻两行 Tag 头在 upDir 方向上的距离（模型单位）。
        /// 返回：Tag 的 ElementId -> 移动向量（模型坐标）。
        /// </summary>
        public static Dictionary<ElementId, XYZ> CalculateStackedLeftMoves(
            Document doc, View view, IList<IndependentTag> tags, double rowSpacing)
        {
            Dictionary<ElementId, XYZ> moves = new Dictionary<ElementId, XYZ>();
            if (doc == null || view == null || tags == null || tags.Count < 2)
                return moves;

            // 视图的右 / 上方向（正交单位向量）
            XYZ rightDir = view.RightDirection;
            XYZ upDir = view.UpDirection;
            if (rightDir == null || upDir == null) return moves;
            if (rightDir.GetLength() < Eps || upDir.GetLength() < Eps) return moves;
            rightDir = rightDir.Normalize();
            upDir = upDir.Normalize();

            // 1) 收集每个 Tag 的宿主位置与头位置
            List<TagInfo> infos = new List<TagInfo>();
            foreach (IndependentTag tag in tags)
            {
                infos.Add(new TagInfo
                {
                    Tag = tag,
                    HostPos = GetHostPosition(doc, tag, view),
                    HeadPos = tag.TagHeadPosition
                });
            }

            if (infos.Count < 2) return moves;

            // 2) 判断管道布局：从左到右 还是 从上到下（用 Host 位置判断，与移动无关）
            double xRange = infos.Max(i => i.HostPos.DotProduct(rightDir))
                          - infos.Min(i => i.HostPos.DotProduct(rightDir));
            double yRange = infos.Max(i => i.HostPos.DotProduct(upDir))
                          - infos.Min(i => i.HostPos.DotProduct(upDir));

            if (xRange >= yRange)
            {
                // 横向排列 → 从左到右
                infos.Sort((a, b) =>
                    a.HostPos.DotProduct(rightDir)
                        .CompareTo(b.HostPos.DotProduct(rightDir)));
            }
            else
            {
                // 纵向排列 → 从上到下
                infos.Sort((a, b) =>
                    b.HostPos.DotProduct(upDir)
                        .CompareTo(a.HostPos.DotProduct(upDir)));
            }

            // 3) 计算目标位置（完全基于 TagHeadPosition）
            //    水平：对齐到所有 HeadPos 中最靠左（rightDir 投影最小）的那个
            //    垂直：排序后第 0 个 HeadPos 的 upDir 投影作为起点，依次向下排列
            double leftMostR = infos.Min(i => i.HeadPos.DotProduct(rightDir));
            double anchorU = infos[0].HeadPos.DotProduct(upDir);

            for (int i = 0; i < infos.Count; i++)
            {
                TagInfo info = infos[i];

                double curR = info.HeadPos.DotProduct(rightDir);
                double curU = info.HeadPos.DotProduct(upDir);

                double dR = leftMostR - curR;                       // 水平对齐
                double dU = (anchorU - i * rowSpacing) - curU;      // 垂直堆叠

                XYZ moveVec = rightDir * dR + upDir * dU;
                if (moveVec.GetLength() > Eps)
                {
                    moves[info.Tag.Id] = moveVec;
                }
            }

            return moves;
        }

        /// <summary>
        /// 取 Tag 所标注宿主的中心点（模型坐标）。
        /// 仅用于排序，Host 本身不移动，因此该值在多次运行之间稳定。
        /// </summary>
        private static XYZ GetHostPosition(Document doc, IndependentTag tag, View view)
        {
            try
            {
                ICollection<Element> taggedElems = tag.GetTaggedLocalElements();
                if (taggedElems != null && taggedElems.Count > 0)
                {
                    Element host = taggedElems.First();
                    if (host != null)
                    {
                        Location loc = host.Location;
                        if (loc is LocationPoint lp)
                            return lp.Point;
                        if (loc is LocationCurve lc && lc.Curve != null)
                            return lc.Curve.Evaluate(0.5, true);

                        BoundingBoxXYZ bb = host.get_BoundingBox(view);
                        if (bb != null)
                            return bb.Transform.OfPoint((bb.Min + bb.Max) * 0.5);
                    }
                }
            }
            catch
            {
                // 忽略异常，回退到 TagHeadPosition
            }

            return tag.TagHeadPosition;
        }
    }
}