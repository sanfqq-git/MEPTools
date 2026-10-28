using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace MEPTools.MEPTagAlignLeft
{
    /// <summary>
    /// 计算类：基于 TagHeadPosition 计算左对齐、堆叠的移动向量。
    /// 完全不依赖 Host 元素位置，保证多次运行严格幂等。
    /// </summary>
    public static class CalculateIndependentTag
    {
        private const double Eps = 1e-9;

        private class TagInfo
        {
            public IndependentTag Tag;
            public XYZ HeadPos;   // Tag 头插入点（TagHeadPosition），用于定位和排序
        }

        /// <summary>
        /// 统一的初始化逻辑：提取 Tag 信息并获取视图的右/上方向。
        /// </summary>
        private static List<TagInfo> PrepareTagInfos(
            View view, IList<IndependentTag> tags, out XYZ rightDir, out XYZ upDir)
        {
            rightDir = null;
            upDir = null;

            if (view == null || tags == null || tags.Count < 2)
                return null;

            rightDir = view.RightDirection?.Normalize();
            upDir = view.UpDirection?.Normalize();

            if (rightDir == null || upDir == null || 
                rightDir.GetLength() < Eps || upDir.GetLength() < Eps)
            {
                return null;
            }

            return tags.Select(t => new TagInfo
            {
                Tag = t,
                HeadPos = t.TagHeadPosition
            }).ToList();
        }

        /// <summary>
        /// 仅左对齐：将所有 Tag 的 HeadPos 对齐到最左侧，不改变垂直位置。
        /// </summary>
        public static Dictionary<ElementId, XYZ> CalculateLeftAlignMoves(
            View view, IList<IndependentTag> tags)
        {
            var moves = new Dictionary<ElementId, XYZ>();
            var infos = PrepareTagInfos(view, tags, out XYZ rightDir, out _);
            if (infos == null) return moves;

            double leftMostR = infos.Min(i => i.HeadPos.DotProduct(rightDir));

            foreach (var info in infos)
            {
                double curR = info.HeadPos.DotProduct(rightDir);
                double dR = leftMostR - curR;

                if (Math.Abs(dR) > Eps)
                {
                    moves[info.Tag.Id] = rightDir * dR;
                }
            }

            return moves;
        }

        /// <summary>
        /// 仅堆叠：保持 Tag 现有的垂直相对顺序，将其按 rowSpacing 统一排列。
        /// </summary>
        public static Dictionary<ElementId, XYZ> CalculateStackMoves(
            View view, IList<IndependentTag> tags, double rowSpacing)
        {
            var moves = new Dictionary<ElementId, XYZ>();
            var infos = PrepareTagInfos(view, tags, out _, out XYZ upDir);
            if (infos == null) return moves;

            // 按当前 HeadPos 的 upDir 投影从上到下排序
            infos.Sort((a, b) =>
                b.HeadPos.DotProduct(upDir).CompareTo(a.HeadPos.DotProduct(upDir)));

            double anchorU = infos[0].HeadPos.DotProduct(upDir);

            for (int i = 0; i < infos.Count; i++)
            {
                double curU = infos[i].HeadPos.DotProduct(upDir);
                double dU = (anchorU - i * rowSpacing) - curU;

                if (Math.Abs(dU) > Eps)
                {
                    moves[infos[i].Tag.Id] = upDir * dU;
                }
            }

            return moves;
        }

        /// <summary>
        /// 左对齐 + 按现有垂直顺序从上往下垂直堆叠。
        /// rowSpacing 表示相邻两行 Tag 头在 upDir 方向上的距离（模型单位）。
        /// </summary>
        public static Dictionary<ElementId, XYZ> CalculateStackedLeftMoves(
            View view, IList<IndependentTag> tags, double rowSpacing)
        {
            var moves = new Dictionary<ElementId, XYZ>();
            var infos = PrepareTagInfos(view, tags, out XYZ rightDir, out XYZ upDir);
            if (infos == null) return moves;

            // 按当前 HeadPos 的 upDir 投影从上到下排序
            infos.Sort((a, b) =>
                b.HeadPos.DotProduct(upDir).CompareTo(a.HeadPos.DotProduct(upDir)));

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
    }
}