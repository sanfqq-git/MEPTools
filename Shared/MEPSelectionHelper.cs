using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace MEPTools.Shared
{
    public static class MEPSelectionHelper
    {
        /// <summary>
        /// 智能获取元素（有预选直接返回，无预选则 InfoCenter 提示并使用空格键交互选择）
        /// </summary>
        /// <param name="uidoc">UIDocument</param>
        /// <param name="filter">选择过滤器</param>
        /// <param name="pickPrompt">底部状态栏提示文字</param>
        /// <param name="infoTipNoPreselection">InfoCenter 提示文字</param>
        /// <param name="minCount">最少需要的元素数量</param>
        /// <param name="useSpaceKey">是否使用空格键确认</param>
        /// <returns>返回选中的元素列表，如果取消或数量不足则返回空列表</returns>
        public static List<Element> GetElements(
            UIDocument uidoc,
            ISelectionFilter filter,
            string pickPrompt,
            string infoTipNoPreselection = "请在视图中选择元素，按空格键结束",
            int minCount = 1,
            bool useSpaceKey = true)
        {
            Document doc = uidoc.Document;
            List<Element> result = new List<Element>();

            // 1. 尝试获取预选
            ICollection<ElementId> selectedIds = uidoc.Selection.GetElementIds();
            if (selectedIds != null && selectedIds.Count > 0)
            {
                result = selectedIds
                    .Select(id => doc.GetElement(id))
                    .Where(e => e != null && (filter == null || filter.AllowElement(e)))
                    .ToList();
            }

            // 2. 预选数量不足，启动交互选择
            if (result.Count < minCount)
            {
                InfoCenterTip.Show(infoTipNoPreselection);
                MEPLogger.Instance.AppendLine("预选元素不足，启动交互式选择。");

                try
                {
                    if (useSpaceKey)
                    {
                        // 使用 SpaceKeyFinisher 包裹，实现按空格点击状态栏 Finish
                        using (var finisher = new SpaceKeyFinisher())
                        {
                            finisher.Enable();
                            result = DoPickObjects(uidoc, filter, pickPrompt, doc);
                        }
                    }
                    else
                    {
                        result = DoPickObjects(uidoc, filter, pickPrompt, doc);
                    }
                }
                catch (OperationCanceledException)
                {
                    InfoCenterTip.Show("操作已取消");
                    MEPLogger.Instance.EndSession("Cancelled by user.");
                    return new List<Element>(); // 返回空代表取消
                }
            }

            // 3. 最终数量校验
            if (result.Count < minCount)
            {
                InfoCenterTip.Show($"有效元素不足 {minCount} 个，操作已退出");
                MEPLogger.Instance.EndSession("Failed: Not enough valid elements.");
                return new List<Element>();
            }

            return result;
        }

        private static List<Element> DoPickObjects(UIDocument uidoc, ISelectionFilter filter, string prompt, Document doc)
        {
            IList<Reference> refs = uidoc.Selection.PickObjects(
                ObjectType.Element,
                filter,
                prompt);

            return refs.Select(r => doc.GetElement(r.ElementId)).ToList();
        }
    }
}