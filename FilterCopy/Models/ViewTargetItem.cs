using Autodesk.Revit.DB;
using MEPTools.FilterCopy.ViewModels;

namespace MEPTools.FilterCopy.Models
{
    /// <summary>
    /// DataGrid 目标视图行。实现 INPC，保证复选框与 DataTrigger 实时生效。
    /// </summary>
    public class ViewTargetItem : ObservableObject
    {
        private bool _isSelected = false;
        private string _templateName = string.Empty;

        public ElementId ViewId { get; set; }
        public string ViewName { get; set; }
        public string ViewTypeName { get; set; }

        /// <summary>绑定的模板名称；空串表示未绑定模板。</summary>
        public string TemplateName
        {
            get => _templateName;
            set
            {
                if (SetProperty(ref _templateName, value))
                    OnPropertyChanged(nameof(HasTemplate)); // HasTemplate 依赖此属性
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        /// <summary>是否绑定了视图模板 → 驱动 DataTrigger 红色加粗。</summary>
        public bool HasTemplate => !string.IsNullOrEmpty(TemplateName);
    }
}
