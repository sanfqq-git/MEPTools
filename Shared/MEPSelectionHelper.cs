using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Autodesk.Revit.Exceptions; // ⭐ 必须引入这个命名空间以捕获 Revit 专属的取消异常

namespace MEPTools.Shared
{
    public static class MEPSelectionHelper
    {
        /// <summary>
        /// 智能获取元素（有预选直接返回，无预选则 InfoCenter 提示并使用空格键交互选择）
        /// </summary>
        /// <param name="singlePick">是否限制只能单选（默认 false 为多选）</param>
        public static List<Element> GetElements(
            UIDocument uidoc,
            ISelectionFilter filter,
            string pickPrompt,
            string infoTipNoPreselection = "请在视图中选择元素，按空格键结束",
            int minCount = 1,
            bool useSpaceKey = true,
            bool singlePick = false) // ⭐ 新增参数
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
                    
                // 如果是单选模式且有预选，只取第一个
                if (singlePick && result.Count > 1)
                {
                    result = new List<Element> { result.First() };
                }
            }

            // 2. 预选数量不足，启动交互选择
            if (result.Count < minCount)
            {
                InfoCenterTip.Show(infoTipNoPreselection);
                MEPLogger.Instance.AppendLine("预选元素不足，启动交互式选择。");

                try
                {
                    // 单选通常不需要空格确认，点完即结束
                    if (useSpaceKey && !singlePick) 
                    {
                        using (var finisher = new SpaceKeyFinisher())
                        {
                            finisher.Enable();
                            result = DoPickObjects(uidoc, filter, pickPrompt, doc, singlePick);
                        }
                    }
                    else
                    {
                        result = DoPickObjects(uidoc, filter, pickPrompt, doc, singlePick);
                    }
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) // ⭐ 准确捕获 Revit 的取消异常
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

        private static List<Element> DoPickObjects(UIDocument uidoc, ISelectionFilter filter, string prompt, Document doc, bool singlePick)
        {
            if (singlePick)
            {
                // 单选逻辑
                Reference reference = uidoc.Selection.PickObject(ObjectType.Element, filter, prompt);
                return new List<Element> { doc.GetElement(reference.ElementId) };
            }
            else
            {
                // 多选逻辑
                IList<Reference> refs = uidoc.Selection.PickObjects(ObjectType.Element, filter, prompt);
                return refs.Select(r => doc.GetElement(r.ElementId)).ToList();
            }
        }
    }
}