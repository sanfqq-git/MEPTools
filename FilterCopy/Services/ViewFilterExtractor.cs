using System.Collections.Generic;
using Autodesk.Revit.DB;
using MEPTools.FilterCopy.Models;

namespace MEPTools.FilterCopy.Services
{
    /// <summary>从源视图提取过滤器快照（启用状态 + 图形替换副本）。</summary>
    public class ViewFilterExtractor
    {
        private readonly Document _doc;

        public ViewFilterExtractor(Document doc) => _doc = doc;

        public List<ViewFilterSnapshot> Extract(View sourceView)
        {
            var result = new List<ViewFilterSnapshot>();
            foreach (ElementId filterId in sourceView.GetFilters())
            {
                var filterEl = _doc.GetElement(filterId) as FilterElement;
                bool visible = sourceView.GetFilterVisibility(filterId);
                var ogs = sourceView.GetFilterOverrides(filterId);

                // 坑点：OverrideGraphicSettings 为引用类型，必须 new 副本再存快照
                result.Add(new ViewFilterSnapshot(
                    filterId,
                    filterEl?.Name ?? filterId.ToString(),
                    visible,
                    new OverrideGraphicSettings(ogs)));
            }
            return result;
        }
    }
}
