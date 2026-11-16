using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.DB;

namespace MEPTools.ExportTools // 修正：统一命名空间
{
    public class LinkSelectionForm : System.Windows.Forms.Form
    {
        private TextBox _txtSearch;
        private CheckedListBox _checkedListBox;
        private Button _btnSelectAll;
        private Button _btnSelectNone;
        private Button _btnOk;
        private Button _btnCancel;

        private List<LinkItem> _allItems = new List<LinkItem>();
        private HashSet<ElementId> _checkedIds = new HashSet<ElementId>();
        private bool _isRefreshing = false;

        public List<ElementId> SelectedTypeIds { get; private set; } = new List<ElementId>();

        public LinkSelectionForm(List<RevitLinkType> linkTypes)
        {
            InitializeComponent();
            
            foreach (var linkType in linkTypes)
            {
                _allItems.Add(new LinkItem { TypeId = linkType.Id, Name = linkType.Name });
                _checkedIds.Add(linkType.Id);
            }

            RefreshList();
        }

        private void InitializeComponent()
        {
            this.Text = "选择要导出的链接模型";
            this.Size = new Size(480, 480);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            var panelTop = new System.Windows.Forms.Panel { Dock = DockStyle.Top, Height = 40 };
            var lblSearch = new Label { Text = "搜索:", Location = new System.Drawing.Point(10, 13), AutoSize = true };
            _txtSearch = new TextBox { Location = new System.Drawing.Point(55, 10), Width = 390 };
            _txtSearch.TextChanged += (s, e) => RefreshList();
            panelTop.Controls.Add(lblSearch);
            panelTop.Controls.Add(_txtSearch);

            _checkedListBox = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                CheckOnClick = true,
                DisplayMember = "Name"
            };
            _checkedListBox.ItemCheck += CheckedListBox_ItemCheck;

            var panelBottom = new System.Windows.Forms.Panel { Dock = DockStyle.Bottom, Height = 50 };
            
            _btnSelectAll = new Button { Text = "全选", Location = new System.Drawing.Point(10, 10), Size = new Size(70, 30) };
            _btnSelectAll.Click += (s, e) => SetAllChecked(true);

            _btnSelectNone = new Button { Text = "全不选", Location = new System.Drawing.Point(90, 10), Size = new Size(70, 30) };
            _btnSelectNone.Click += (s, e) => SetAllChecked(false);

            _btnOk = new Button { Text = "确定", Location = new System.Drawing.Point(290, 10), Size = new Size(80, 30), DialogResult = DialogResult.OK };
            _btnOk.Click += (s, e) => { CollectSelected(); this.Close(); };

            _btnCancel = new Button { Text = "取消", Location = new System.Drawing.Point(380, 10), Size = new Size(80, 30), DialogResult = DialogResult.Cancel };
            
            panelBottom.Controls.AddRange(new System.Windows.Forms.Control[] { _btnSelectAll, _btnSelectNone, _btnOk, _btnCancel });
            
            this.Controls.Add(_checkedListBox);
            this.Controls.Add(panelTop);
            this.Controls.Add(panelBottom);
            
            this.AcceptButton = _btnOk;
            this.CancelButton = _btnCancel;
        }

        private void CheckedListBox_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (_isRefreshing) return;

            if (_checkedListBox.Items[e.Index] is LinkItem item)
            {
                if (e.NewValue == CheckState.Checked)
                    _checkedIds.Add(item.TypeId);
                else
                    _checkedIds.Remove(item.TypeId);
            }
        }

        private void RefreshList()
        {
            _isRefreshing = true;
            string keyword = _txtSearch.Text.Trim();

            _checkedListBox.Items.Clear();
            var filteredItems = _allItems.Where(i => string.IsNullOrEmpty(keyword) || 
                                                     i.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            foreach (var item in filteredItems)
            {
                bool isChecked = _checkedIds.Contains(item.TypeId);
                _checkedListBox.Items.Add(item, isChecked);
            }
            _isRefreshing = false;
        }

        private void SetAllChecked(bool isChecked)
        {
            foreach (var item in _checkedListBox.Items)
            {
                if (item is LinkItem linkItem)
                {
                    if (isChecked) _checkedIds.Add(linkItem.TypeId);
                    else _checkedIds.Remove(linkItem.TypeId);
                }
            }
            RefreshList();
        }

        private void CollectSelected()
        {
            SelectedTypeIds.Clear();
            SelectedTypeIds.AddRange(_checkedIds);
        }

        private class LinkItem
        {
            public ElementId TypeId { get; set; }
            public string Name { get; set; }
        }
    }
}