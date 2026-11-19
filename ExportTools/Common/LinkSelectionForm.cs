using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.DB;

namespace MEPTools.ExportTools.Common
{
    public class LinkSelectionForm : System.Windows.Forms.Form
    {
        private TextBox _txtSearch;
        private CheckedListBox _checkedListBox;
        private Label _lblCurrentDoc;
        private CheckBox _chkMainModelBackup;
        private TextBox _txtOutputFolder;
        private Button _btnBrowseFolder;
        private Button _btnSelectAll;
        private Button _btnSelectNone;
        private Button _btnOk;
        private Button _btnCancel;

        private List<LinkItem> _allItems = new List<LinkItem>();
        private HashSet<ElementId> _checkedIds = new HashSet<ElementId>();
        private bool _isRefreshing = false;

        private bool _advancedMode = false;      // 本命令流程：当前档案红字 + 本档案备份勾选 + 输出位置
        private string _currentFileName = "";    // 例如 project 1.rvt
        private string _defaultOutputFolder = "";

        public List<ElementId> SelectedTypeIds { get; private set; } = new List<ElementId>();

        /// <summary>是否勾选"同时备份本档案（BIM360 中心文件缓存）"；旧界面下恒为 true。</summary>
        public bool IncludeMainModel
        {
            get { return _advancedMode ? _chkMainModelBackup.Checked : true; }
        }

        /// <summary>用户确定的输出根目录（仅新界面会赋值）。</summary>
        public string OutputFolder { get; private set; } = "";

        /// <summary>旧调用方（一键打包）：链接默认全勾选，界面与原来完全一致。</summary>
        public LinkSelectionForm(List<RevitLinkType> linkTypes)
        {
            _advancedMode = false;
            InitializeComponent();
            FillItems(linkTypes, true);
        }

        /// <summary>
        /// 本命令流程：链接默认不勾选；显示"当前档案"红字、默认勾选的"备份本档案"勾选框、
        /// 以及默认指向电脑"文档"目录的输出位置（可改可浏览）。
        /// </summary>
        public LinkSelectionForm(List<RevitLinkType> linkTypes, string currentDocTitle, string defaultOutputFolder)
        {
            _advancedMode = true;
            _currentFileName = ToRvtFileName(currentDocTitle);
            _defaultOutputFolder = string.IsNullOrWhiteSpace(defaultOutputFolder)
                ? GetDefaultOutputFolder()
                : defaultOutputFolder;
            InitializeComponent();
            FillItems(linkTypes, false);
        }

        /// <summary>默认输出位置：电脑的"文档"目录。</summary>
        public static string GetDefaultOutputFolder()
        {
            string path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(path))
                path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return path;
        }

        private static string ToRvtFileName(string title)
        {
            string name = string.IsNullOrWhiteSpace(title) ? "当前档案" : title.Trim();
            if (!name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) name += ".rvt";
            return name;
        }

        private void FillItems(List<RevitLinkType> linkTypes, bool defaultCheckAll)
        {
            foreach (var linkType in linkTypes)
            {
                _allItems.Add(new LinkItem { TypeId = linkType.Id, Name = linkType.Name });
                if (defaultCheckAll) _checkedIds.Add(linkType.Id);
            }
            RefreshList();
        }

        private void InitializeComponent()
        {
            this.Text = "选择要导出的链接模型";
            this.Size = new Size(480, _advancedMode ? 560 : 480);
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

            // 按钮行
            var panelBottom = new System.Windows.Forms.Panel { Dock = DockStyle.Bottom, Height = 50 };

            _btnSelectAll = new Button { Text = "全选", Location = new System.Drawing.Point(10, 10), Size = new Size(70, 30) };
            _btnSelectAll.Click += (s, e) => SetAllChecked(true);

            _btnSelectNone = new Button { Text = "全不选", Location = new System.Drawing.Point(90, 10), Size = new Size(70, 30) };
            _btnSelectNone.Click += (s, e) => SetAllChecked(false);

            _btnOk = new Button { Text = "确定", Location = new System.Drawing.Point(290, 10), Size = new Size(80, 30) };
            _btnOk.Click += (s, e) => OnOkClicked();

            _btnCancel = new Button { Text = "取消", Location = new System.Drawing.Point(380, 10), Size = new Size(80, 30), DialogResult = DialogResult.Cancel };

            panelBottom.Controls.AddRange(new System.Windows.Forms.Control[] { _btnSelectAll, _btnSelectNone, _btnOk, _btnCancel });

            // 输出位置行：默认电脑"文档"目录，可直接改路径或点"浏览"
            var panelOutput = new System.Windows.Forms.Panel { Dock = DockStyle.Bottom, Height = 34 };
            var lblOutput = new Label { Text = "输出位置:", Location = new System.Drawing.Point(10, 9), AutoSize = true };
            _txtOutputFolder = new TextBox { Location = new System.Drawing.Point(80, 5), Width = 300, Text = _defaultOutputFolder };
            _btnBrowseFolder = new Button { Text = "浏览...", Location = new System.Drawing.Point(384, 3), Size = new Size(72, 26) };
            _btnBrowseFolder.Click += (s, e) => BrowseOutputFolder();
            panelOutput.Controls.Add(lblOutput);
            panelOutput.Controls.Add(_txtOutputFolder);
            panelOutput.Controls.Add(_btnBrowseFolder);

            // 本档案备份勾选框：独立一行，默认勾选
            var panelMainModel = new System.Windows.Forms.Panel { Dock = DockStyle.Bottom, Height = 28 };
            _chkMainModelBackup = new CheckBox
            {
                Text = "同时备份本档案（BIM360 中心文件缓存）",
                Location = new System.Drawing.Point(12, 4),
                AutoSize = true,
                Checked = true
            };
            panelMainModel.Controls.Add(_chkMainModelBackup);

            // 当前档案名（红字），显示识别到的当前文档名与备份文件名
            var panelCurrentDoc = new System.Windows.Forms.Panel { Dock = DockStyle.Bottom, Height = 24 };
            _lblCurrentDoc = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = System.Drawing.Color.Red,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Text = "当前档案：" + _currentFileName + "　→　备份名：" + ExportFileUtils.CurrentDocNamePrefix + _currentFileName
            };
            panelCurrentDoc.Controls.Add(_lblCurrentDoc);

            this.Controls.Add(_checkedListBox);
            this.Controls.Add(panelTop);
            if (_advancedMode)
            {
                this.Controls.Add(panelCurrentDoc);   // 先加者后停放，所以顺序为：红字 → 勾选框 → 输出位置 → 按钮
                this.Controls.Add(panelMainModel);
                this.Controls.Add(panelOutput);
            }
            this.Controls.Add(panelBottom);

            this.AcceptButton = _btnOk;
            this.CancelButton = _btnCancel;
        }

        private void BrowseOutputFolder()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "请选择输出根目录";
                dialog.ShowNewFolderButton = true;
                string current = _txtOutputFolder.Text.Trim();
                if (Directory.Exists(current)) dialog.SelectedPath = current;
                if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                    _txtOutputFolder.Text = dialog.SelectedPath;
            }
        }

        private void OnOkClicked()
        {
            if (_advancedMode)
            {
                if (!_chkMainModelBackup.Checked && _checkedIds.Count == 0)
                {
                    MessageBox.Show(this, "请至少勾选一个要复制的模型（链接模型或本档案）。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                string folder = _txtOutputFolder.Text.Trim();
                if (string.IsNullOrWhiteSpace(folder))
                {
                    MessageBox.Show(this, "请填写输出位置。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                try
                {
                    folder = Path.GetFullPath(folder);
                    Directory.CreateDirectory(folder);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "输出位置不可用：" + ex.Message, "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                OutputFolder = folder;
            }

            CollectSelected();
            this.DialogResult = DialogResult.OK;
            this.Close();
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
