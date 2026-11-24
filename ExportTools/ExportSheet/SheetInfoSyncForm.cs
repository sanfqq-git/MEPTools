using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using MEPTools.ExportTools.Common;
using MEPTools.Shared;

namespace MEPTools.ExportTools.ExportSheet
{
    /// <summary>导出结果：只用于本窗口内部传递，不涉及模型。</summary>
    public class SheetInfoSyncExportResult
    {
        public bool Success { get; set; }
        public string Path { get; set; }
        public int Count { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// 导出动作：由命令提供。命令在打开窗口之前已经把图纸信息快照进内存，
    /// 所以这里只做文件写出，不碰任何 Revit API。
    /// </summary>
    public delegate SheetInfoSyncExportResult SheetInfoSyncExportHandler(List<string> keys, string folder);

    public enum SheetInfoSyncAction
    {
        None,
        Import
    }

    /// <summary>
    /// 图纸信息「导出 + 回读」汇总窗口（一个窗口两步走）：
    ///   第 1 步：勾选图纸 → 导出 Excel（导出成功后就地切到第 2 步，窗口不关）
    ///   第 2 步：去 Excel 修改并保存 → 回到本窗口点「② 回读并写入模型」
    ///
    /// 本窗口只负责：选择、显示状态、读文件时间戳、挑文件。
    /// 任何模型写入都在窗口关闭之后由命令执行 —— 避免“模态窗口里开事务”这种隐患。
    /// </summary>
    public class SheetInfoSyncForm : Form
    {
        private readonly List<ExportListItem> _allItems = new List<ExportListItem>();
        private readonly HashSet<string> _checkedKeys = new HashSet<string>();
        private readonly SheetInfoSyncExportHandler _exportHandler;

        private bool _isRefreshing;
        private bool _onlyChecked;

        // 本次导出（用于第 2 步的时间戳比对）
        private string _currentPath = string.Empty;
        private int _currentCount;
        private bool _isOwnExport;
        private DateTime _exportTime = DateTime.MinValue;

        private Label _lblStage;
        private Panel _panelSearch;
        private TextBox _txtSearch;
        private CheckBox _chkOnlyChecked;
        private CheckedListBox _list;

        private Panel _panelStatus;
        private Label _lblStatus;
        private Label _lblTips;

        private Panel _panelFolder;
        private TextBox _txtFolder;
        private Button _btnBrowse;
        private CheckBox _chkAutoOpen;

        private Panel _panelFile;
        private TextBox _txtPath;
        private Label _lblFileInfo;

        private Panel _panelBottom;
        private Button _btnSelectAll;
        private Button _btnSelectNone;
        private Button _btnExport;
        private Button _btnImport;
        private Button _btnCancel;

        private Timer _timer;

        /// <summary>窗口关闭时的结果：要不要回读、回读哪份文件。</summary>
        public SheetInfoSyncAction Action { get; private set; }
        public string ImportFile { get; private set; }
        public string ExportPath { get { return _currentPath; } }
        public int ExportCount { get { return _currentCount; } }

        public SheetInfoSyncForm(IList<ExportListItem> items, string defaultFolder,
            SheetInfoSyncExportHandler exportHandler, string presetImportPath)
        {
            _exportHandler = exportHandler;
            ImportFile = string.Empty;

            if (items != null)
            {
                foreach (ExportListItem item in items)
                {
                    if (item == null || item.Key == null) continue;
                    _allItems.Add(item);
                    _checkedKeys.Add(item.Key);   // 默认全选：图纸导出通常是整批
                }
            }

            InitializeComponent(defaultFolder);
            RefreshList();

            // 从“回读上次导出的那份”进入（比如上次只导出没回读）
            if (!string.IsNullOrWhiteSpace(presetImportPath))
            {
                _currentPath = presetImportPath;
                _currentCount = 0;
                _isOwnExport = false;
                _txtPath.Text = _currentPath;
                SetStage(true);
                UpdateFileInfo();
                _lblStatus.ForeColor = Color.DimGray;
                _lblStatus.Text = "已带入上次的表格，可直接回读；若要重新导出，关闭窗口后重新运行本命令。";
            }
            else
            {
                SetStage(false);
            }
        }

        private void InitializeComponent(string defaultFolder)
        {
            Text = "图纸信息：导出 / 回读";

            // 固定字号基准 + 按系统 DPI 等比缩放：
            // 之前显示不全，就是因为窗体尺寸按 96DPI 写死、而系统是 125%/150%，文字被撑大后装不进固定高度的面板。
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            Size = new Size(760, 680);
            MinimumSize = new Size(700, 600);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;   // 可拉伸：万一还有裁切，用户可以自己拉大
            MaximizeBox = true;
            MinimizeBox = false;

            _lblStage = new Label
            {
                Dock = DockStyle.Top,
                Height = 26,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0),
                Font = new Font(Font, FontStyle.Bold),
                Text = "第 1 步：选择图纸，导出 Excel"
            };

            _panelSearch = new Panel { Dock = DockStyle.Top, Height = 60 };
            var lblSearch = new Label { Text = "搜索:", Location = new Point(10, 10), AutoSize = true };
            _txtSearch = new TextBox { Location = new Point(55, 6), Width = 600 };
            _txtSearch.TextChanged += (s, e) => RefreshList();
            _chkOnlyChecked = new CheckBox
            {
                Text = "只显示已勾选（隐藏未勾选）",
                Location = new Point(55, 34),
                AutoSize = true
            };
            _chkOnlyChecked.CheckedChanged += (s, e) =>
            {
                _onlyChecked = _chkOnlyChecked.Checked;
                RefreshList();
            };
            _panelSearch.Controls.Add(lblSearch);
            _panelSearch.Controls.Add(_txtSearch);
            _panelSearch.Controls.Add(_chkOnlyChecked);

            _list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true };
            _list.ItemCheck += List_ItemCheck;

            // ---------- 状态行 ----------
            _panelStatus = new Panel { Dock = DockStyle.Bottom, Height = 40 };
            _lblStatus = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 10, 0),
                ForeColor = Color.DimGray,
                AutoEllipsis = true,
                Text = "勾选要处理的图纸 → 导出 Excel → 在 Excel 里改完保存 → 点「② 回读并写入模型」写回。"
            };
            _panelStatus.Controls.Add(_lblStatus);

            // ---------- 第 2 步：操作说明（填满中间的空白区） ----------
            _lblTips = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                Padding = new Padding(24, 24, 24, 24),
                ForeColor = Color.DimGray,
                TextAlign = ContentAlignment.TopLeft,
                Visible = false,
                Text =
                    "在 Excel 里：\r\n\r\n" +
                    "1) 只改「图纸名称」列；要改「图纸编号」也可以，回读时会单独让你勾选解锁。\r\n" +
                    "2) 右侧 4 个隐藏列（原始编号 / 原始名称 / 来源批次 / 来源文档）请勿改动——它们是校验基线，改了会整份拒绝。\r\n" +
                    "3) 保存即可，Excel 不用关（回读只读磁盘上已保存的内容）。\r\n\r\n" +
                    "回到这里：\r\n\r\n" +
                    "点「② 回读并写入模型」→ 先弹差异清单（逐行 旧→新），确认后才写；\r\n" +
                    "写之前自动落一份回滚快照，随时可以用「撤销图纸导入」退回导入前。\r\n\r\n" +
                    "注意：同一份导出表只能成功导入一次——导入后模型里的值变了，隐藏基线就对不上了。要再改就重新导出。\r\n\r\n" +
                    "本窗口只回读“第 1 步刚导出的这一份”；要回读别的表格（别人给的、或更早导出的），请用「导入图纸信息」命令。"
            };

            // ---------- 第 1 步：输出位置 + 自动打开开关 ----------
            _panelFolder = new Panel { Dock = DockStyle.Bottom, Height = 62 };
            var lblFolder = new Label { Text = "输出位置:", Location = new Point(10, 9), AutoSize = true };
            _txtFolder = new TextBox
            {
                Location = new Point(80, 5),
                Width = 500,
                Text = string.IsNullOrWhiteSpace(defaultFolder)
                    ? LinkSelectionForm.GetDefaultOutputFolder()
                    : defaultFolder
            };
            _btnBrowse = new Button { Text = "浏览...", Location = new Point(588, 3), Size = new Size(80, 26) };
            _btnBrowse.Click += (s, e) => BrowseFolder();

            _chkAutoOpen = new CheckBox
            {
                Text = "导出后自动打开 Excel（取消勾选则只导出，不弹表格）",
                Location = new Point(80, 36),
                AutoSize = true,
                Checked = true
            };

            _panelFolder.Controls.Add(lblFolder);
            _panelFolder.Controls.Add(_txtFolder);
            _panelFolder.Controls.Add(_btnBrowse);
            _panelFolder.Controls.Add(_chkAutoOpen);

            // ---------- 第 2 步：待回读文件 ----------
            _panelFile = new Panel { Dock = DockStyle.Bottom, Height = 84 };
            var lblFile = new Label { Text = "待回读文件:", Location = new Point(10, 8), AutoSize = true };
            _txtPath = new TextBox
            {
                Location = new Point(10, 30),
                Width = 650,
                ReadOnly = true,
                BorderStyle = BorderStyle.FixedSingle
            };
            _lblFileInfo = new Label
            {
                Location = new Point(10, 58),
                AutoSize = false,
                Width = 650,
                Height = 22,
                ForeColor = Color.DimGray,
                AutoEllipsis = true,
                Text = "—"
            };
            _panelFile.Controls.Add(lblFile);
            _panelFile.Controls.Add(_txtPath);
            _panelFile.Controls.Add(_lblFileInfo);

            // ---------- 底部按钮 ----------
            _panelBottom = new Panel { Dock = DockStyle.Bottom, Height = 52 };
            _btnSelectAll = new Button { Text = "全选", Location = new Point(10, 11), Size = new Size(70, 30) };
            _btnSelectAll.Click += (s, e) => SetAllChecked(true);
            _btnSelectNone = new Button { Text = "全不选", Location = new Point(86, 11), Size = new Size(70, 30) };
            _btnSelectNone.Click += (s, e) => SetAllChecked(false);

            _btnExport = new Button { Text = "① 导出到 Excel →", Location = new Point(430, 11), Size = new Size(160, 30) };
            _btnExport.Click += (s, e) => OnExportClicked();

            _btnImport = new Button { Text = "② 回读并写入模型 ✓", Location = new Point(300, 11), Size = new Size(170, 30), Visible = false };
            _btnImport.Click += (s, e) => OnImportClicked();

            _btnCancel = new Button { Text = "取消", Location = new Point(598, 11), Size = new Size(72, 30), DialogResult = DialogResult.Cancel };

            _panelBottom.Controls.AddRange(new Control[]
            {
                _btnSelectAll, _btnSelectNone, _btnExport, _btnImport, _btnCancel
            });

            Controls.Add(_list);            // Fill
            Controls.Add(_lblTips);         // Fill（第 2 步用，默认隐藏）
            Controls.Add(_panelSearch);     // Top（后加者更靠上：标题要压在搜索框之上）
            Controls.Add(_lblStage);        // Top（最上）
            Controls.Add(_panelStatus);     // Bottom（先加者在上面）
            Controls.Add(_panelFolder);
            Controls.Add(_panelFile);
            Controls.Add(_panelBottom);     // Bottom（最后=最下）

            AcceptButton = _btnExport;
            CancelButton = _btnCancel;

            _timer = new Timer { Interval = 2000 };
            _timer.Tick += (s, e) => UpdateFileInfo();
        }

        /// <summary>
        /// 拉伸时把“该跟着变宽”的控件重新排一下：
        /// 文本框拉满剩余宽度、底部按钮贴右边。这样窗口拉大或被系统缩放到 125%/150% 都不会被裁掉。
        /// 只在宽度有效（>0）时才改，避免初始化阶段把控件摆到负坐标。
        /// </summary>
        private void ApplyLayout()
        {
            if (_panelBottom == null) return;

            int width;

            width = _panelSearch.ClientSize.Width;
            if (width > 0) _txtSearch.Width = Math.Max(160, width - 75);

            width = _panelFolder.ClientSize.Width;
            if (width > 0)
            {
                _btnBrowse.Left = width - _btnBrowse.Width - 10;
                _txtFolder.Width = Math.Max(120, _btnBrowse.Left - 80 - 10);
            }

            width = _panelFile.ClientSize.Width;
            if (width > 0)
            {
                _txtPath.Width = Math.Max(200, width - 20);
                _lblFileInfo.Width = _txtPath.Width;
            }

            width = _panelBottom.ClientSize.Width;
            if (width > 0)
            {
                _btnCancel.Left = width - _btnCancel.Width - 10;
                _btnImport.Left = _btnCancel.Left - _btnImport.Width - 8;
                _btnExport.Left = _btnCancel.Left - _btnExport.Width;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ApplyLayout();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ApplyLayout();
        }

        private void SetStage(bool importStage)
        {
            _panelSearch.Visible = !importStage;
            _list.Visible = !importStage;
            _lblTips.Visible = importStage;
            _panelFolder.Visible = !importStage;
            _panelFile.Visible = importStage;

            _btnSelectAll.Visible = !importStage;
            _btnSelectNone.Visible = !importStage;
            _btnExport.Visible = !importStage;
            _btnImport.Visible = importStage;

            _btnCancel.Text = importStage ? "关闭" : "取消";
            _lblStage.Text = importStage
                ? "第 2 步：回读 Excel，写回模型"
                : "第 1 步：选择图纸，导出 Excel";

            AcceptButton = importStage ? (IButtonControl)_btnImport : (IButtonControl)_btnExport;

            if (importStage) _timer.Start();
            else _timer.Stop();

            ApplyLayout();
        }

        private void OnExportClicked()
        {
            if (_checkedKeys.Count == 0)
            {
                MessageBox.Show(this, "请至少勾选一张图纸。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string folder = _txtFolder.Text.Trim();
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

            // 按列表顺序收集勾选项，跟导出顺序一致
            List<string> keys = new List<string>();
            foreach (ExportListItem item in _allItems)
            {
                if (_checkedKeys.Contains(item.Key)) keys.Add(item.Key);
            }
            if (keys.Count == 0)
            {
                MessageBox.Show(this, "没有可导出的行。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Cursor = Cursors.WaitCursor;
            try
            {
                SheetInfoSyncExportResult result = _exportHandler == null ? null : _exportHandler(keys, folder);
                if (result == null || !result.Success)
                {
                    _lblStatus.ForeColor = Color.FromArgb(192, 0, 0);
                    _lblStatus.Text = "✗ 导出失败：" + (result == null ? "未知错误" : result.Error);
                    return;
                }

                _currentPath = result.Path;
                _currentCount = result.Count;
                _isOwnExport = true;
                try { _exportTime = File.GetLastWriteTime(_currentPath); }
                catch { _exportTime = DateTime.Now; }

                _txtPath.Text = _currentPath;
                _lblStatus.ForeColor = Color.FromArgb(0, 128, 0);

                // 是否自动打开 Excel：由第 1 步的勾选框决定（默认勾选）
                string tail = "；改完保存后点「② 回读并写入模型」。";
                if (_chkAutoOpen.Checked)
                {
                    bool opened = TryOpenExportedFile(_currentPath);
                    _lblStatus.Text = opened
                        ? "✓ 已导出 " + result.Count + " 张并在 Excel 中打开" + tail
                        : "✓ 已导出 " + result.Count + " 张（自动打开失败，请手动双击下面这个文件）" + tail;
                }
                else
                {
                    _lblStatus.Text = "✓ 已导出 " + result.Count + " 张（按设置未自动打开）" + tail;
                }

                SetStage(true);
                UpdateFileInfo();
            }
            catch (Exception ex)
            {
                _lblStatus.ForeColor = Color.FromArgb(192, 0, 0);
                _lblStatus.Text = "✗ 导出失败：" + ex.Message;
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void OnImportClicked()
        {
            if (string.IsNullOrWhiteSpace(_currentPath) || !File.Exists(_currentPath))
            {
                MessageBox.Show(this,
                    "没有可回读的文件" +
                    (string.IsNullOrWhiteSpace(_currentPath) ? "。" : "（路径已失效）：\n" + _currentPath) +
                    "\n\n先在第 1 步导出 Excel 再回读；要回读别的表格（别人给的、或更早导出的），请用「导入图纸信息」命令。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 自导出后没保存过 → 回读一定没有改动，提醒一下但不拦（用户可能想校验一遍）
            if (_isOwnExport && _exportTime != DateTime.MinValue)
            {
                try
                {
                    if (File.GetLastWriteTime(_currentPath) <= _exportTime)
                    {
                        DialogResult answer = MessageBox.Show(this,
                            "这份 Excel 自导出后没有保存过，回读不会产生任何改动。仍要继续？",
                            "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                        if (answer != DialogResult.Yes) return;
                    }
                }
                catch { }
            }

            MEPLogger.Instance.AppendLine("[同步] 回读：" + _currentPath);
            Action = SheetInfoSyncAction.Import;
            ImportFile = _currentPath;
            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>
        /// 用系统默认程序打开刚导出的 xlsx（一般就是 Excel）。
        /// 失败绝不影响导出结果：返回 false，由调用方在状态栏提示“请手动双击”。
        /// </summary>
        private bool TryOpenExportedFile(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                MEPLogger.Instance.AppendLine("[同步] 已自动打开导出文件：" + path);
                return true;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.AppendLine("[同步] 自动打开导出文件失败：" + ex.Message);
                return false;
            }
        }

        private void UpdateFileInfo()
        {
            if (string.IsNullOrWhiteSpace(_currentPath)) return;

            try
            {
                if (!File.Exists(_currentPath))
                {
                    _lblFileInfo.ForeColor = Color.FromArgb(192, 0, 0);
                    _lblFileInfo.Text = "文件不存在（可能被移动、改名或删除）";
                    return;
                }

                DateTime lastWrite = File.GetLastWriteTime(_currentPath);
                long size = new FileInfo(_currentPath).Length;

                if (_isOwnExport && _exportTime != DateTime.MinValue)
                {
                    bool modified = lastWrite > _exportTime;
                    _lblFileInfo.ForeColor = modified ? Color.FromArgb(0, 128, 0) : Color.Gray;
                    _lblFileInfo.Text = "导出 " + _currentCount + " 行  ｜  最后保存 " +
                                        lastWrite.ToString("HH:mm:ss") + "  ｜  " +
                                        (size / 1024.0).ToString("0.#") + " KB  ｜  " +
                                        (modified ? "已改动，可以回读" : "尚未改动（回读不会产生变化）");
                }
                else
                {
                    _lblFileInfo.ForeColor = Color.DimGray;
                    _lblFileInfo.Text = "外部文件  ｜  最后保存 " + lastWrite.ToString("HH:mm:ss") +
                                        "  ｜  " + (size / 1024.0).ToString("0.#") + " KB";
                }
            }
            catch (Exception ex)
            {
                _lblFileInfo.ForeColor = Color.FromArgb(192, 0, 0);
                _lblFileInfo.Text = "读取文件信息失败：" + ex.Message;
            }
        }

        private void BrowseFolder()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "请选择输出目录";
                dialog.ShowNewFolderButton = true;
                string current = _txtFolder.Text.Trim();
                if (Directory.Exists(current)) dialog.SelectedPath = current;
                if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                    _txtFolder.Text = dialog.SelectedPath;
            }
        }

        private void List_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (_isRefreshing) return;
            object entry = _list.Items[e.Index];
            ExportListItem item = entry is ListRow ? ((ListRow)entry).Item : entry as ExportListItem;
            if (item == null || item.Key == null) return;

            if (e.NewValue == CheckState.Checked) _checkedKeys.Add(item.Key);
            else _checkedKeys.Remove(item.Key);

            if (_onlyChecked && e.NewValue == CheckState.Unchecked && IsHandleCreated && !IsDisposed)
            {
                try { BeginInvoke(new Action(RefreshList)); }
                catch { }
            }
        }

        private void RefreshList()
        {
            if (IsDisposed || _list == null) return;

            _isRefreshing = true;
            string keyword = _txtSearch.Text.Trim();
            _list.Items.Clear();

            foreach (ExportListItem item in _allItems)
            {
                if (!string.IsNullOrEmpty(keyword) &&
                    item.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (_onlyChecked && !_checkedKeys.Contains(item.Key)) continue;
                _list.Items.Add(new ListRow(item), _checkedKeys.Contains(item.Key));
            }
            _isRefreshing = false;
        }

        private void SetAllChecked(bool isChecked)
        {
            foreach (object entry in _list.Items)
            {
                ExportListItem item = entry is ListRow ? ((ListRow)entry).Item : entry as ExportListItem;
                if (item == null || item.Key == null) continue;
                if (isChecked) _checkedKeys.Add(item.Key);
                else _checkedKeys.Remove(item.Key);
            }
            RefreshList();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_timer != null) _timer.Stop();
            base.OnFormClosed(e);
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
