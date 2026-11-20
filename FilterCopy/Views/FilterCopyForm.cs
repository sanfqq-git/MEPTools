using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MEPTools.FilterCopy.Models;
using MEPTools.FilterCopy.Services;
using RevitDoc = Autodesk.Revit.DB.Document;
using RevitElementId = Autodesk.Revit.DB.ElementId;
using RevitCollector = Autodesk.Revit.DB.FilteredElementCollector;
using RevitView = Autodesk.Revit.DB.View;

namespace MEPTools.FilterCopy.Views
{
    /// <summary>来源列表的一行（视图样板 或 源视图）。</summary>
    internal class SourceRow
    {
        public RevitElementId ViewId { get; set; }
        public string ViewName { get; set; }
        public string ViewTypeName { get; set; }
        public int FilterCount { get; set; }
    }

    /// <summary>
    /// B 功能窗体：复制 View Filter 到目标视图（独立于样板复制窗体）。
    /// ① 顶部单选来源方式：从【视图样板】复制 / 从【源视图】复制；
    /// ② 左栏单选来源，右栏勾选目标视图（已绑样板的行红色加粗，执行时安全跳过）；
    /// ③ 确定后由 FilterCopyCommand 走原执行管道。
    /// </summary>
    internal class FilterCopyForm : Form
    {
        private readonly RevitDoc _doc;
        private readonly RevitElementId _activeViewId;
        private readonly Font _boldFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        private readonly BindingList<SourceRow> _sourceBinding = new BindingList<SourceRow>();
        private readonly BindingList<ViewTargetItem> _targetBinding = new BindingList<ViewTargetItem>();
        private readonly Dictionary<RevitElementId, bool> _pickState = new Dictionary<RevitElementId, bool>();

        private List<SourceRow> _templateRows = new List<SourceRow>();
        private List<SourceRow> _viewRows = new List<SourceRow>();
        private List<SourceRow> _currentRows = new List<SourceRow>();
        private List<ViewTargetItem> _targets;

        private RadioButton _rbTemplate;
        private RadioButton _rbView;
        private DataGridView _gridSource;
        private DataGridView _gridTarget;
        private TextBox _txtFilter;
        private CheckBox _chkOverride;
        private Button _btnAll;
        private Button _btnNone;
        private Button _btnNoTemplate;
        private Button _btnCurrentView;
        private Label _countLabel;
        private Label _sourceCountLabel;
        private bool _bindingSources;
        private bool _resolveToActiveView;

        /// <summary>选中来源（样板或视图）的 Id；未选为 InvalidElementId。</summary>
        public RevitElementId SelectedSourceId { get; private set; } = RevitElementId.InvalidElementId;

        /// <summary>当前是否为“从视图样板复制”方式。</summary>
        public bool SourceIsTemplate => _rbTemplate != null && _rbTemplate.Checked;

        /// <summary>是否覆盖目标视图原有过滤器。</summary>
        public bool OverrideMode => _chkOverride != null && _chkOverride.Checked;

        public FilterCopyForm(RevitDoc doc, RevitElementId activeViewId)
        {
            _doc = doc;
            _activeViewId = activeViewId;

            Text = "复制视图过滤器 - 选择来源与目标";
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1000, 660);
            MinimumSize = new Size(840, 540);

            BuildUi();
            LoadCandidateRows();
            _rbTemplate.Checked = true;   // 默认“从视图样板复制”，触发加载

            // 窗体首次显示后，默认焦点落在“仅复制过滤器到当前视图”按钮上
            Shown += (s, e) => _btnCurrentView.Focus();
        }

        // ==================== 界面 ====================

        private void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // 来源方式
            _rbTemplate = new RadioButton { Text = "① 从视图样板复制", AutoSize = true, Margin = new Padding(2, 6, 14, 0) };
            _rbView = new RadioButton { Text = "② 从源视图复制", AutoSize = true, Margin = new Padding(2, 6, 14, 0) };
            _rbTemplate.CheckedChanged += (s, e) => { if (_rbTemplate.Checked) LoadSourceMode(true); };
            _rbView.CheckedChanged += (s, e) => { if (_rbView.Checked) LoadSourceMode(false); };

            var modeBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false, Margin = new Padding(0, 0, 0, 8)
            };
            modeBar.Controls.Add(new Label { Text = "过滤器来源：", AutoSize = true, Margin = new Padding(2, 8, 4, 0) });
            modeBar.Controls.Add(_rbTemplate);
            modeBar.Controls.Add(_rbView);
            modeBar.Controls.Add(new Label
            {
                Text = "② 右栏勾选目标视图；已绑定样板的行红色加粗 = 执行时安全跳过",
                AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(6, 8, 0, 0)
            });
            root.Controls.Add(modeBar, 0, 0);

            // 左右两栏
            var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44F));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56F));
            split.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            split.Controls.Add(BuildSourceGroup(), 0, 0);
            split.Controls.Add(BuildTargetGroup(), 1, 0);
            root.Controls.Add(split, 0, 1);

            root.Controls.Add(BuildFooter(), 0, 2);
            Controls.Add(root);
        }

        private Control BuildSourceGroup()
        {
            var group = new GroupBox { Text = "① 来源（单选）", Dock = DockStyle.Fill, Padding = new Padding(8), Margin = new Padding(0, 0, 8, 0) };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            _sourceCountLabel = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(2, 0, 0, 4), Text = "共 0 个" };
            layout.Controls.Add(_sourceCountLabel, 0, 0);

            _gridSource = CreateGrid();
            _gridSource.Columns.Add(MakeColumn("名称", "ViewName", 60));
            _gridSource.Columns.Add(MakeColumn("类型", "ViewTypeName", 22));
            _gridSource.Columns.Add(MakeColumn("过滤器数", "FilterCount", 18));
            _gridSource.DataSource = _sourceBinding;
            _gridSource.SelectionChanged += GridSource_SelectionChanged;
            layout.Controls.Add(_gridSource, 0, 1);

            group.Controls.Add(layout);
            return group;
        }

        private Control BuildTargetGroup()
        {
            var group = new GroupBox { Text = "② 目标视图（可多选）", Dock = DockStyle.Fill, Padding = new Padding(8), Margin = new Padding(8, 0, 0, 0) };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            _btnAll = new Button { Text = "全选", Width = 70, Height = 26, Margin = new Padding(0, 0, 6, 0) };
            _btnNone = new Button { Text = "全不选", Width = 70, Height = 26, Margin = new Padding(0, 0, 6, 0) };
            _btnNoTemplate = new Button { Text = "仅选未绑定模板", Width = 126, Height = 26, Margin = new Padding(0, 0, 6, 0) };
            _btnAll.Click += (s, e) => SetSelection(t => true);
            _btnNone.Click += (s, e) => SetSelection(t => false);
            _btnNoTemplate.Click += (s, e) => SetSelection(t => !t.HasTemplate);

            _countLabel = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(8, 8, 0, 0), Text = "已选 0 / 共 0" };

            // 筛选框：按视图名称实时过滤右栏（仅隐藏不匹配行，勾选状态保留）
            var lblFilter = new Label { Text = "筛选：", AutoSize = true, Margin = new Padding(0, 6, 4, 0) };
            _txtFilter = new TextBox { Width = 140, Margin = new Padding(0, 3, 6, 0) };
            _txtFilter.TextChanged += (s, e) => ApplyFilter();

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false, Margin = new Padding(0, 0, 0, 4)
            };
            bar.Controls.Add(lblFilter);
            bar.Controls.Add(_txtFilter);
            bar.Controls.Add(_btnAll);
            bar.Controls.Add(_btnNone);
            bar.Controls.Add(_btnNoTemplate);
            bar.Controls.Add(_countLabel);
            layout.Controls.Add(bar, 0, 0);

            _gridTarget = CreateGrid();
            _gridTarget.Columns.Add(new DataGridViewCheckBoxColumn
            {
                HeaderText = "复制到", DataPropertyName = "IsSelected", Width = 56,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None, FalseValue = false, TrueValue = true,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
            _gridTarget.Columns.Add(MakeColumn("视图名称", "ViewName", 52));
            _gridTarget.Columns.Add(MakeColumn("类型", "ViewTypeName", 22));
            _gridTarget.Columns.Add(MakeColumn("模板状态", "TemplateName", 26));
            _gridTarget.DataSource = _targetBinding;
            _gridTarget.CellFormatting += Grid_CellFormatting;
            _gridTarget.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_gridTarget.IsCurrentCellDirty && _gridTarget.CurrentCell is DataGridViewCheckBoxCell)
                    _gridTarget.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            _gridTarget.CellValueChanged += GridTarget_CellValueChanged;
            layout.Controls.Add(_gridTarget, 0, 1);

            group.Controls.Add(layout);
            return group;
        }

        private Control BuildFooter()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 10, 0, 0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _chkOverride = new CheckBox
            {
                Text = "覆盖模式：先清空目标视图原有过滤器", Checked = false, AutoSize = true,
                Anchor = AnchorStyles.Left, Margin = new Padding(2, 10, 0, 0)
            };
            layout.Controls.Add(_chkOverride, 0, 0);

            _btnCurrentView = new Button { Text = "仅复制过滤器到当前视图", Width = 170, Height = 30, Margin = new Padding(0, 0, 8, 0) };
            _btnCurrentView.Click += BtnCurrentView_Click;

            var btnCancel = new Button { Text = "取消", Width = 88, Height = 30, DialogResult = DialogResult.Cancel, Margin = new Padding(0, 0, 8, 0) };
            var btnOk = new Button { Text = "确定", Width = 110, Height = 30, Margin = new Padding(0) };
            btnOk.Click += BtnOk_Click;

            var buttons = new FlowLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
                Anchor = AnchorStyles.Right, Margin = new Padding(0)
            };
            buttons.Controls.Add(_btnCurrentView);
            buttons.Controls.Add(btnCancel);
            buttons.Controls.Add(btnOk);
            layout.Controls.Add(buttons, 1, 0);

            AcceptButton = _btnCurrentView;
            CancelButton = btnCancel;
            return layout;
        }

        private static DataGridView CreateGrid()
        {
            return new DataGridView
            {
                Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false,
                AllowUserToDeleteRows = false, AllowUserToResizeRows = false, AllowUserToOrderColumns = false,
                RowHeadersVisible = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
                GridColor = Color.FromArgb(228, 228, 228),
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                EditMode = DataGridViewEditMode.EditOnEnter, Margin = new Padding(0)
            };
        }

        private static DataGridViewTextBoxColumn MakeColumn(string header, string property, int fillWeight)
        {
            return new DataGridViewTextBoxColumn
            {
                HeaderText = header, DataPropertyName = property, FillWeight = fillWeight,
                ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable
            };
        }

        // ==================== 来源候选 ====================

        /// <summary>一次性收集两套来源：当前文档全部视图样板 + 全部支持过滤器的视图。</summary>
        private void LoadCandidateRows()
        {
            _templateRows = new List<SourceRow>();
            _viewRows = new List<SourceRow>();

            var allViews = new RevitCollector(_doc).OfClass(typeof(RevitView)).Cast<RevitView>()
                .OrderBy(v => v.ViewType).ThenBy(v => v.Name);

            foreach (RevitView v in allViews)
            {
                int filterCount = 0;
                try { filterCount = v.GetFilters().Count; }
                catch (Exception) { filterCount = 0; }

                var row = new SourceRow
                {
                    ViewId = v.Id, ViewName = v.Name,
                    ViewTypeName = v.ViewType.ToString(), FilterCount = filterCount
                };

                if (v.IsTemplate) _templateRows.Add(row);
                else if (ViewFilterSupport.IsSupported(v)) _viewRows.Add(row);
            }

            MEPTools.Shared.MEPLogger.Instance.AppendLine(
                "来源候选：视图样板 " + _templateRows.Count + " 个，源视图 " + _viewRows.Count + " 个。");
        }

        /// <summary>切换来源方式：重建左栏数据源并刷新右栏（右栏自动排除当前来源）。</summary>
        private void LoadSourceMode(bool templateMode)
        {
            _currentRows = templateMode ? _templateRows : _viewRows;

            _bindingSources = true;
            _sourceBinding.RaiseListChangedEvents = false;
            _sourceBinding.Clear();
            foreach (SourceRow r in _currentRows) _sourceBinding.Add(r);
            _sourceBinding.RaiseListChangedEvents = true;
            _sourceBinding.ResetBindings();
            _bindingSources = false;

            _sourceCountLabel.Text = (templateMode ? "视图样板共 " : "源视图共 ") + _currentRows.Count + " 个";

            // 尽量保留原选中项；没有就选第一行（视图模式优先定位当前活动视图）
            SourceRow keep = _currentRows.FirstOrDefault(r => r.ViewId == SelectedSourceId);
            if (keep == null && _currentRows.Count > 0)
            {
                keep = _currentRows[0];
                if (!templateMode)
                {
                    SourceRow active = _currentRows.FirstOrDefault(r => r.ViewId == _activeViewId);
                    if (active != null) keep = active;
                }
            }

            SelectedSourceId = keep == null ? RevitElementId.InvalidElementId : keep.ViewId;
            SelectRow(keep);
            ReloadTargets();
        }

        private void SelectRow(SourceRow row)
        {
            if (row == null) return;

            _bindingSources = true;
            foreach (DataGridViewRow gridRow in _gridSource.Rows)
            {
                var item = gridRow.DataBoundItem as SourceRow;
                if (item != null && item.ViewId == row.ViewId)
                {
                    gridRow.Selected = true;
                    _gridSource.CurrentCell = gridRow.Cells[0];
                    break;
                }
            }
            _bindingSources = false;
        }

        private void GridSource_SelectionChanged(object sender, EventArgs e)
        {
            if (_bindingSources) return;

            var item = _gridSource.CurrentRow == null ? null : (_gridSource.CurrentRow.DataBoundItem as SourceRow);
            RevitElementId newId = item == null ? RevitElementId.InvalidElementId : item.ViewId;
            if (newId == SelectedSourceId) return;

            SelectedSourceId = newId;
            ReloadTargets();
        }

        // ==================== 目标视图 ====================

        private void ReloadTargets()
        {
            if (_targets != null)
            {
                foreach (ViewTargetItem t in _targets) _pickState[t.ViewId] = t.IsSelected;
            }

            List<ViewTargetItem> list = new ViewSelectionService(_doc).CollectTargets(SelectedSourceId);

            foreach (ViewTargetItem t in list)
            {
                bool sel;
                if (!_pickState.TryGetValue(t.ViewId, out sel)) sel = false;   // 默认全不选
                t.IsSelected = sel;
                _pickState[t.ViewId] = sel;
            }
            _targets = list;

            ApplyFilter();
            UpdateCount();
        }

        private void SetSelection(Func<ViewTargetItem, bool> predicate)
        {
            if (_targets == null) return;
            foreach (ViewTargetItem t in _targets)
            {
                t.IsSelected = predicate(t);
                _pickState[t.ViewId] = t.IsSelected;
            }
            _gridTarget.Refresh();
            UpdateCount();
        }

        /// <summary>
        /// 按筛选关键字把匹配视图装入右栏绑定列表；不匹配项不显示。
        /// 不操作行 Visible（绑定列表的当前行不允许隐藏，会抛 CurrencyManager 异常），
        /// 改为子集重建绑定；勾选状态保留在原对象上，不受筛选影响。
        /// </summary>
        private void ApplyFilter()
        {
            if (_gridTarget == null || _targets == null) return;

            string keyword = _txtFilter == null ? string.Empty : _txtFilter.Text.Trim();
            bool hasKeyword = !string.IsNullOrEmpty(keyword);

            _targetBinding.RaiseListChangedEvents = false;
            _targetBinding.Clear();
            foreach (ViewTargetItem t in _targets)
            {
                string name = t.ViewName ?? string.Empty;
                if (!hasKeyword || name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    _targetBinding.Add(t);
            }
            _targetBinding.RaiseListChangedEvents = true;
            _targetBinding.ResetBindings();
        }

        private void UpdateCount()
        {
            if (_countLabel == null) return;
            int total = _targets == null ? 0 : _targets.Count;
            int selected = _targets == null ? 0 : _targets.Count(t => t.IsSelected);
            _countLabel.Text = "已选 " + selected + " / 共 " + total;
        }

        private void GridTarget_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.RowIndex >= _gridTarget.Rows.Count) return;
            if (!(_gridTarget.Columns[e.ColumnIndex] is DataGridViewCheckBoxColumn)) return;

            var item = _gridTarget.Rows[e.RowIndex].DataBoundItem as ViewTargetItem;
            if (item == null) return;

            _pickState[item.ViewId] = item.IsSelected;
            UpdateCount();
        }

        private void Grid_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            var grid = sender as DataGridView;
            if (grid == null || e.RowIndex < 0 || e.RowIndex >= grid.Rows.Count) return;

            var item = grid.Rows[e.RowIndex].DataBoundItem as ViewTargetItem;
            if (item == null || !item.HasTemplate) return;

            e.CellStyle.ForeColor = Color.Red;
            e.CellStyle.SelectionForeColor = Color.Red;
            e.CellStyle.Font = _boldFont;
        }

        // ==================== 确定 ====================

        public List<ViewTargetItem> GetSelectedTargets()
        {
            if (_gridTarget != null) _gridTarget.EndEdit();

            // “仅复制到当前视图”按钮：目标固定为活动视图，忽略右栏勾选
            if (_resolveToActiveView)
            {
                ViewTargetItem active = BuildActiveViewTarget();
                return active == null ? new List<ViewTargetItem>() : new List<ViewTargetItem> { active };
            }

            return _targets == null ? new List<ViewTargetItem>() : _targets.Where(t => t.IsSelected).ToList();
        }

        /// <summary>构造当前活动视图目标项；优先复用右栏已有行（保留模板状态），取不到返回 null。</summary>
        private ViewTargetItem BuildActiveViewTarget()
        {
            if (_activeViewId == RevitElementId.InvalidElementId) return null;

            ViewTargetItem active = _targets == null
                ? null
                : _targets.FirstOrDefault(t => t.ViewId == _activeViewId);

            if (active != null)
            {
                active.IsSelected = true;
                return active;
            }

            RevitView activeView = _doc.GetElement(_activeViewId) as RevitView;
            if (activeView == null) return null;

            active = new ViewTargetItem
            {
                ViewId = activeView.Id,
                ViewName = activeView.Name,
                ViewTypeName = activeView.ViewType.ToString(),
                TemplateName = activeView.ViewTemplateId != RevitElementId.InvalidElementId
                    ? (_doc.GetElement(activeView.ViewTemplateId) as RevitView)?.Name ?? "?"
                    : string.Empty
            };
            active.IsSelected = true;
            return active;
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            if (!ValidateSource()) return;

            _resolveToActiveView = false;

            if (GetSelectedTargets().Count == 0)
            {
                MessageBox.Show(this, "请至少勾选一个目标视图。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DialogResult = DialogResult.OK;
        }

        private void BtnCurrentView_Click(object sender, EventArgs e)
        {
            if (!ValidateSource()) return;

            // 边界情况在窗体阶段拦截，避免无效复制
            if (_activeViewId == RevitElementId.InvalidElementId)
            {
                MessageBox.Show(this, "当前没有活动视图，无法仅复制到当前视图。", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (_activeViewId == SelectedSourceId)
            {
                MessageBox.Show(this, "来源就是当前视图，无需复制到自身，请换一个来源。",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (BuildActiveViewTarget() == null)
            {
                MessageBox.Show(this, "当前活动视图已不存在，无法复制到当前视图。", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _resolveToActiveView = true;
            DialogResult = DialogResult.OK;
        }

        /// <summary>校验来源已选中；未选中弹提示并返回 false。</summary>
        private bool ValidateSource()
        {
            if (SelectedSourceId == RevitElementId.InvalidElementId)
            {
                MessageBox.Show(this, SourceIsTemplate ? "请先在左栏选中一个视图样板。" : "请先在左栏选中一个源视图。",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _boldFont.Dispose();
        }
    }
}
