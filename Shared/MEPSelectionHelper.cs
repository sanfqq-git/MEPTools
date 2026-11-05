using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Autodesk.Revit.Exceptions;

namespace MEPTools.Shared
{
    public static class MEPSelectionHelper
    {
        public static List<Element> GetElements(
            UIDocument uidoc,
            ISelectionFilter filter,
            string pickPrompt,
            string infoTipNoPreselection = "请在视图中选择元素，按空格键结束",
            int minCount = 1,
            bool useSpaceKey = true,
            bool singlePick = false)
        {
            Document doc = uidoc.Document;
            List<Element> result = new List<Element>();

            // 1. 尝试获取预选
            ICollection<ElementId> selectedIds = uidoc.Selection.GetElementIds();
            if (selectedIds != null && selectedIds.Count > 0)
            {
                int originalCount = selectedIds.Count;
                
                result = selectedIds
                    .Select(id => doc.GetElement(id))
                    .Where(e => e != null && (filter == null || SafeAllowElement(filter, e)))
                    .ToList();

                if (result.Count < originalCount)
                {
                    InfoCenterTip.Show($"已忽略 {originalCount - result.Count} 个不符合要求的元素");
                }

                if (singlePick && result.Count > 1)
                {
                    InfoCenterTip.Show("当前操作仅支持单选，请只选择一个元素后重试");
                    return new List<Element>();
                }
            }

            // 2. 预选数量不足，启动交互选择
            if (result.Count < minCount)
            {
                InfoCenterTip.Show(infoTipNoPreselection);
                MEPLogger.Instance.AppendLine("预选元素不足，启动交互式选择。");

                // ⭐ 核心修复：强行重置一次选择状态，防止 Cannot re-enter 异常
                try { uidoc.Selection.SetElementIds(new List<ElementId>()); } catch { }

                try
                {
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
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    InfoCenterTip.Show("操作已取消");
                    MEPLogger.Instance.EndSession("Cancelled by user.");
                    return new List<Element>();
                }
                catch (Exception ex)
                {
                    MEPLogger.Instance.LogException("MEPSelectionHelper.DoPickObjects", ex);
                    InfoCenterTip.Show("选择过程中发生错误，请查看日志");
                    MEPLogger.Instance.EndSession("Failed: " + ex.Message);
                    return new List<Element>();
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
                Reference reference = uidoc.Selection.PickObject(ObjectType.Element, filter, prompt);
                return new List<Element> { doc.GetElement(reference.ElementId) };
            }
            else
            {
                IList<Reference> refs = uidoc.Selection.PickObjects(ObjectType.Element, filter, prompt);
                return refs.Select(r => doc.GetElement(r.ElementId)).ToList();
            }
        }

        private static bool SafeAllowElement(ISelectionFilter filter, Element elem)
        {
            try { return filter.AllowElement(elem); }
            catch { return false; }
        }
    }
}