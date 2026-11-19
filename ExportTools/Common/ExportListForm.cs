
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace MEPTools.ExportTools.Common
{
    /// <summary>通用列表项：Key 用于回传（一般放 ElementId），Name 主文本，Info 副说明。</summary>
    public class ExportListItem
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public string Info { get; set; }
    }

    /// <summary>
    /// 通用「勾选对象 + 指定输出位置」窗体：明细表、类别、图纸/视图等模块共用，
    /// 避免每个命令各弹一次「选对象 + 选目录」。
    /// </summary>
    public class ExportListForm : System.Windows.Forms.Form
    {
        private TextBox _txtSearch;
        private CheckedListBox _checkedListBox;
        private TextBox _txtOutputFolder;
        private Button _btnBrowse;
        private Button _btnSelectAll;
        private Button _btnSelectNone;
        private Button _btnOk;
        private Button _btnCancel;

        private readonly List<ExportListItem> _allItems = new List<ExportListItem>();
        private readonly HashSet<string> _checkedKeys = new HashSet<string>();
        private bool _isRefreshing = false;

        public List<string> SelectedKeys { get; private set; }
        public string OutputFolder { get; private set; }

        public ExportListForm(IList<ExportListItem> items, string title, string hint,
            bool defaultCheckAll, string defaultOutputFolder)
        {
            SelectedKeys = new List<string>();
            OutputFolder = string.Empty;

            if (items != null)
            {
                foreach (ExportListItem item in items)
                {
                    _allItems.Add(item);
                    if (defaultCheckAll) _checkedKeys.Add(item.Key);
                }
            }

            InitializeComponent(title, hint, defaultOutputFolder);
            RefreshList();
        }

        private void InitializeComponent(string title, string hint, string defaultOutputFolder)
        {
            Text = title;
            Size = new Size(480, 520);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            var panelTop = new System.Windows.Forms.Panel { Dock = DockStyle.Top, Height = 40 };
            var lblSearch = new Label { Text = "搜索:", Location = new Point(10, 13), AutoSize = true };
            _txtSearch = new TextBox { Location = new Point(55, 10), Width = 390 };
            _txtSearch.TextChanged += (s, e) => RefreshList();
            panelTop.Controls.Add(lblSearch);
            panelTop.Controls.Add(_txtSearch);

            var panelHint = new System.Windows.Forms.Panel { Dock = DockStyle.Bottom, Height = 24 };
            var lblHint = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.DimGray,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Text = hint ?? string.Empty
            };
            panelHint.Controls.Add(lblHint);

            var panelOutput = new System.Windows.Forms.Panel { Dock = DockStyle.Bottom, Height = 34 };
            var lblOutput = new Label { Text = "输出位置:", Location = new Point(10, 9), AutoSize = true };
            _txtOutputFolder = new TextBox
            {
                Location = new Point(80, 5),
                Width = 300,
                Text = string.IsNullOrWhiteSpace(defaultOutputFolder)
                    ? LinkSelectionForm.GetDefaultOutputFolder()
                    : defaultOutputFolder
            };
            _btnBrowse = new Button { Text = "浏览...", Location = new Point(384, 3), Size = new Size(72, 26) };
            _btnBrowse.Click += (s, e) => BrowseFolder();
            panelOutput.Controls.Add(lblOutput);
            panelOutput.Controls.Add(_txtOutputFolder);
            panelOutput.Controls.Add(_btnBrowse);

            var panelBottom = new System.Windows.Forms.Panel { Dock = DockStyle.Bottom, Height = 50 };
            _btnSelectAll = new Button { Text = "全选", Location = new Point(10, 10), Size = new Size(70, 30) };
            _btnSelectAll.Click += (s, e) => SetAllChecked(true);
            _btnSelectNone = new Button { Text = "全不选", Location = new Point(90, 10), Size = new Size(70, 30) };
            _btnSelectNone.Click += (s, e) => SetAllChecked(false);
            _btnOk = new Button { Text = "确定", Location = new Point(290, 10), Size = new Size(80, 30) };
            _btnOk.Click += (s, e) => OnOkClicked();
            _btnCancel = new Button { Text = "取消", Location = new Point(380, 10), Size = new Size(80, 30), DialogResult = DialogResult.Cancel };
            panelBottom.Controls.AddRange(new System.Windows.Forms.Control[] { _btnSelectAll, _btnSelectNone, _btnOk, _btnCancel });

            _checkedListBox = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true };
            _checkedListBox.ItemCheck += CheckedListBox_ItemCheck;

            Controls.Add(_checkedListBox);
            Controls.Add(panelTop);
            Controls.Add(panelHint);    // 先加者后停放：提示 → 输出位置 → 按钮
            Controls.Add(panelOutput);
            Controls.Add(panelBottom);

            AcceptButton = _btnOk;
            CancelButton = _btnCancel;
        }

        private void BrowseFolder()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "请选择输出目录";
                dialog.ShowNewFolderButton = true;
                string current = _txtOutputFolder.Text.Trim();
                if (Directory.Exists(current)) dialog.SelectedPath = current;
                if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                    _txtOutputFolder.Text = dialog.SelectedPath;
            }
        }

        private void OnOkClicked()
        {
            if (_checkedKeys.Count == 0)
            {
                MessageBox.Show(this, "请至少勾选一项。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string folder = _txtOutputFolder.Text.Trim();
            if (string.IsNullOrWhiteSpace(folder))
            {
                MessageBox.Show(this, "请填写输出位置。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                folder = Path.GetFullPath(folder);
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "输出位置不可用：" + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SelectedKeys.Clear();
            SelectedKeys.AddRange(_checkedKeys);
            OutputFolder = folder;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void CheckedListBox_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (_isRefreshing) return;
            object entry = _checkedListBox.Items[e.Index];
            ExportListItem item = entry is ListRow ? ((ListRow)entry).Item : entry as ExportListItem;
            if (item == null || item.Key == null) return;

            if (e.NewValue == CheckState.Checked) _checkedKeys.Add(item.Key);
            else _checkedKeys.Remove(item.Key);
        }

        private void RefreshList()
        {
            _isRefreshing = true;
            string keyword = _txtSearch.Text.Trim();
            _checkedListBox.Items.Clear();

            foreach (ExportListItem item in _allItems)
            {
                if (!string.IsNullOrEmpty(keyword) &&
                    item.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0) continue;
                _checkedListBox.Items.Add(new ListRow(item), _checkedKeys.Contains(item.Key));
            }
            _isRefreshing = false;
        }

        private void SetAllChecked(bool isChecked)
        {
            foreach (object entry in _checkedListBox.Items)
            {
                ExportListItem item = entry is ListRow ? ((ListRow)entry).Item : entry as ExportListItem;
                if (item == null || item.Key == null) continue;
                if (isChecked) _checkedKeys.Add(item.Key);
                else _checkedKeys.Remove(item.Key);
            }
            RefreshList();
        }

        private class ListRow
        {
            public ExportListItem Item { get; private set; }
            public ListRow(ExportListItem item) { Item = item; }
            public override string ToString()
            {
                return string.IsNullOrWhiteSpace(Item.Info) ? Item.Name : Item.Name + "    " + Item.Info;
            }
        }
    }
}
