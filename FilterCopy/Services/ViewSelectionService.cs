using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using MEPTools.FilterCopy.Models;

namespace MEPTools.FilterCopy.Services
{
    /// <summary>收集文档中可作为目标视图的候选（含模板名称，供 UI 红色加粗提示）。</summary>
    public class ViewSelectionService
    {
        private readonly Document _doc;

        public ViewSelectionService(Document doc) => _doc = doc;

        public List<ViewTargetItem> CollectTargets(ElementId excludeViewId)
        {
            return new FilteredElementCollector(_doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => v.Id != excludeViewId)
                .OrderBy(v => v.ViewType)
                .ThenBy(v => v.Name)
                .Select(v => new ViewTargetItem
                {
                    ViewId = v.Id,
                    ViewName = v.Name,
                    ViewTypeName = v.ViewType.ToString(),
                    TemplateName = v.ViewTemplateId != ElementId.InvalidElementId
                        ? (_doc.GetElement(v.ViewTemplateId) as View)?.Name ?? "?"
                        : string.Empty
                })
                .ToList();
        }
    }
}
