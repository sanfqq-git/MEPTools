using Autodesk.Revit.DB;

namespace MEPTools.FilterCopy.Models
{
    /// <summary>源视图中单个过滤器的快照（不可变 DTO）。</summary>
    public class ViewFilterSnapshot
    {
        public ElementId FilterId { get; }
        public string FilterName { get; }
        /// <summary>启用/禁用状态。</summary>
        public bool IsVisible { get; }
        /// <summary>图形替换设置的副本。</summary>
        public OverrideGraphicSettings Overrides { get; }

        public ViewFilterSnapshot(ElementId filterId, string filterName,
                                  bool isVisible, OverrideGraphicSettings overrides)
        {
            FilterId = filterId;
            FilterName = filterName;
            IsVisible = isVisible;
            Overrides = overrides;
        }
    }
}
