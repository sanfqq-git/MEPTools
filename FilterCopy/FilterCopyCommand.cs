using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using RevitDoc = Autodesk.Revit.DB.Document;
using RevitView = Autodesk.Revit.DB.View;
using RevitElementId = Autodesk.Revit.DB.ElementId;
using RevitElementSet = Autodesk.Revit.DB.ElementSet;
using RevitCollector = Autodesk.Revit.DB.FilteredElementCollector;
using Autodesk.Revit.UI;
using MEPTools.FilterCopy.Models;
using MEPTools.FilterCopy.Services;
using MEPTools.FilterCopy.ViewModels;
using MEPTools.Shared;

namespace MEPTools.FilterCopy.Commands
{
    /// <summary>
    /// 批量复制视图过滤器 —— 入口命令。
    /// 流程：
    /// StartSession → 一个 Form 内“左栏选源视图 + 右栏勾选目标视图”（取代原 PickObject 点选）
    /// → 校验源视图 → MainViewModel（单一事务 + ApplyToTarget 安全策略 + FeedbackWindow）
    /// → EndSession + InfoCenterTip 气泡。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class FilterCopyCommand : IExternalCommand
    {
        private const string Title = "批量复制视图过滤器";

        public Result Execute(ExternalCommandData commandData, ref string message, RevitElementSet elements)
        {
            var app = commandData == null ? null : commandData.Application;
            var uiDoc = app == null ? null : app.ActiveUIDocument;
            if (uiDoc == null)
            {
                message = "当前没有活动文档。";
                return Result.Failed;
            }

            RevitDoc doc = uiDoc.Document;
            MEPLogger.Instance.StartSession(Title);

            string summary = "已取消，未做任何修改。";

            try
            {
                // ── 1. 源视图候选：只收集“支持过滤器”的视图（沿用 ViewFilterSupport.IsSupported）──
                List<ViewTargetItem> sources = CollectSourceCandidates(doc);
                MEPLogger.Instance.AppendLine("源视图候选：" + sources.Count + " 个。");
                if (sources.Count == 0)
                {
                    TaskDialog.Show(Title, "当前文档中没有支持过滤器的视图。");
                    summary = "中止：文档中没有支持过滤器的视图。";
                    return Result.Cancelled;
                }

                // ── 2. 一个 Form 内两栏同时选择：左栏源视图（单选）+ 右栏目标视图（勾选）──
                ViewTargetItem pickedSource;
                List<ViewTargetItem> targets;
                bool overrideMode;

                using (var form = new ViewPickForm(doc, sources, uiDoc.ActiveView == null ? null : uiDoc.ActiveView.Id))
                {
                    if (form.ShowDialog() != DialogResult.OK)
                    {
                        summary = "已取消：用户在视图选择窗口点了取消。";
                        return Result.Cancelled;
                    }

                    // 窗体销毁前把结果取出来（都是纯数据对象）
                    pickedSource = form.SelectedSource;
                    targets = form.GetSelectedTargets();
                    overrideMode = form.OverrideMode;
                }

                // ── 3. 校验源视图（沿用原 IsSupported 判断）──
                RevitView sourceView = pickedSource == null ? null : doc.GetElement(pickedSource.ViewId) as RevitView;
                if (sourceView == null || !ViewFilterSupport.IsSupported(sourceView))
                {
                    TaskDialog.Show(Title, "所选源视图不支持过滤器。");
                    MEPLogger.Instance.AppendLine("[校验失败] 源视图不存在或不支持过滤器。");
                    summary = "失败：源视图不支持过滤器。";
                    return Result.Cancelled;
                }

                if (targets.Count == 0)
                {
                    TaskDialog.Show(Title, "项目中没有其他视图可作为目标。");
                    summary = "已取消：没有可用的目标视图。";
                    return Result.Cancelled;
                }

                MEPLogger.Instance.AppendLine("源视图：" + sourceView.Name + "（Id=" + sourceView.Id + "）");
                MEPLogger.Instance.AppendLine("目标视图：" + targets.Count + " 个；覆盖模式：" + (overrideMode ? "开" : "关"));
                foreach (ViewTargetItem t in targets)
                {
                    MEPLogger.Instance.AppendLine("  - " + t.ViewName + "（" + t.ViewTypeName +
                        (t.HasTemplate ? "，模板：" + t.TemplateName : "") + "）");
                }

                // ── 4. 后续完全沿用原执行管道（MainViewModel / 事务 / 安全策略 / FeedbackWindow 一行未改）──
                //    MainViewModel.ExecuteCommand 内部：
                //    ViewFilterExtractor.Extract → 单一事务 → ViewFilterTransferService.ApplyToTarget
                //    （模板跳过 / 类型跳过 / 异常隔离）→ 弹出 FeedbackWindow → RequestClose
                var vm = new MainViewModel(doc, sourceView, targets) { OverrideMode = overrideMode };

                TransferResult result = null;
                EventHandler<TransferResult> onExecuted = (s, e) => result = e;
                vm.Executed += onExecuted;
                try
                {
                    vm.ExecuteCommand.Execute(null);
                }
                finally
                {
                    vm.Executed -= onExecuted;
                }

                int okCount = result == null ? 0 : result.SuccessCount;
                int skipCount = result == null ? 0 : result.Log.Count;

                if (result != null)
                {
                    foreach (string line in result.Log)
                        MEPLogger.Instance.AppendLine(line);
                }

                summary = "已复制到 " + okCount + " 个视图，跳过/失败 " + skipCount + " 项。";
                InfoCenterTip.Show("已复制到 " + okCount + " 个视图，跳过/失败 " + skipCount + " 项");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("FilterCopyCommand", ex);
                summary = "执行失败：" + ex.Message;
                InfoCenterTip.Show("批量复制视图过滤器执行失败，请查看日志");
                return Result.Failed;
            }
            finally
            {
                MEPLogger.Instance.EndSession(summary);
            }
        }

        /// <summary>收集源视图候选：只取支持过滤器的视图，排序方式与原 CollectTargets 一致。</summary>
        private static List<ViewTargetItem> CollectSourceCandidates(RevitDoc doc)
        {
            return new RevitCollector(doc)
                .OfClass(typeof(RevitView))
                .Cast<RevitView>()
                .Where(ViewFilterSupport.IsSupported)
                .OrderBy(v => v.ViewType)
                .ThenBy(v => v.Name)
                .Select(v =>
                {
                    RevitView tpl = v.ViewTemplateId == RevitElementId.InvalidElementId
                        ? null
                        : doc.GetElement(v.ViewTemplateId) as RevitView;

                    return new ViewTargetItem
                    {
                        ViewId = v.Id,
                        ViewName = v.Name,
                        ViewTypeName = v.ViewType.ToString(),
                        TemplateName = v.ViewTemplateId == RevitElementId.InvalidElementId
                            ? string.Empty
                            : (tpl == null ? "?" : tpl.Name)
                    };
                })
                .ToList();
        }
    }

    /// <summary>
    /// 两栏选择窗体（纯 WinForms、代码构建、零 XAML 依赖）：
    /// 1) 左栏单选源视图（数据源 = ViewFilterSupport.IsSupported 过滤后的视图）；
    /// 2) 右栏勾选目标视图（数据源 = ViewSelectionService.CollectTargets，切换源视图时自动排除源视图）；
    /// 3) 模板视图按 ViewTargetItem.HasTemplate 显示为红色加粗（执行阶段仍由 ApplyToTarget 安全跳过）；
    /// 4) 全选 / 全不选 / 仅选未绑定模板 三个按钮沿用原有选择逻辑。
    /// </summary>
    // 注意：Autodesk.Revit.DB 里也有 Form / Control（模型文字窗体、族控件），
    // 所以这里显式写全 System.Windows.Forms.Form / Control，避免 CS0104 二义性。
    internal class ViewPickForm : System.Windows.Forms.Form
    {
        private readonly RevitDoc _doc;
        private readonly Font _boldFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        private readonly BindingList<ViewTargetItem> _targetBinding = new BindingList<ViewTargetItem>();

        /// <summary>勾选记忆：切换源视图时保留用户已勾选的目标。</summary>
        private readonly Dictionary<RevitElementId, bool> _pickState = new Dictionary<RevitElementId, bool>();

        private List<ViewTargetItem> _targets;
        private DataGridView _gridSource;
        private DataGridView _gridTarget;
        private CheckBox _chkOverride;
        private Label _countLabel;
        private bool _isBindingSources;

        /// <summary>用户选定的源视图（取消时为 null）。</summary>
        public ViewTargetItem SelectedSource { get; private set; }

        /// <summary>是否覆盖目标视图原有过滤器（对应原 MainViewModel.OverrideMode）。</summary>
        public bool OverrideMode
        {
            get { return _chkOverride != null && _chkOverride.Checked; }
        }

        public ViewPickForm(RevitDoc doc, IEnumerable<ViewTargetItem> sources, RevitElementId activeViewId)
        {
            _doc = doc;

            Text = "批量复制视图过滤器 - 选择视图";
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(980, 640);
            MinimumSize = new Size(820, 520);

            BuildUi();

            IList<ViewTargetItem> list = sources as IList<ViewTargetItem> ?? sources.ToList();
            BindSources(list, FindItem(list, activeViewId));
        }

        // ==================== 界面构建 ====================

        private void BuildUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(12)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var hint = new Label
            {
                AutoSize = true,
                Text = "① 左栏选中源视图（单选）　② 右栏勾选目标视图（可多选，模板红色加粗 = 执行时安全跳过）　③ 点【确定】开始复制",
                ForeColor = Color.FromArgb(70, 70, 70),
                Margin = new Padding(2, 0, 2, 8)
            };
            root.Controls.Add(hint, 0, 0);

            var split = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0)
            };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42F));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58F));
            split.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            split.Controls.Add(BuildSourceGroup(), 0, 0);
            split.Controls.Add(BuildTargetGroup(), 1, 0);
            root.Controls.Add(split, 0, 1);

            root.Controls.Add(BuildFooter(), 0, 2);

            Controls.Add(root);
        }

        private System.Windows.Forms.Control BuildSourceGroup()
        {
            var group = new GroupBox
            {
                Text = "① 源视图（单选）",
                Dock = DockStyle.Fill,
                Padding = new Padding(8),
                Margin = new Padding(0, 0, 8, 0)
            };

            _gridSource = CreateGrid();
            _gridSource.Columns.Add(MakeTextColumn("视图名称", "ViewName", 62));
            _gridSource.Columns.Add(MakeTextColumn("类型", "ViewTypeName", 24));
            _gridSource.Columns.Add(MakeTextColumn("模板", "TemplateName", 14));
            _gridSource.CellFormatting += Grid_CellFormatting;
            _gridSource.SelectionChanged += GridSource_SelectionChanged;

            group.Controls.Add(_gridSource);
            return group;
        }

        private System.Windows.Forms.Control BuildTargetGroup()
        {
            var group = new GroupBox
            {
                Text = "② 目标视图（可多选）",
                Dock = DockStyle.Fill,
                Padding = new Padding(8),
                Margin = new Padding(8, 0, 0, 0)
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 4)
            };

            var btnAll = new Button { Text = "全选", Width = 70, Height = 26, Margin = new Padding(0, 0, 6, 0) };
            var btnNone = new Button { Text = "全不选", Width = 70, Height = 26, Margin = new Padding(0, 0, 6, 0) };
            var btnNoTemplate = new Button { Text = "仅选未绑定模板", Width = 126, Height = 26, Margin = new Padding(0, 0, 6, 0) };

            btnAll.Click += (s, e) => SetSelection(t => true);
            btnNone.Click += (s, e) => SetSelection(t => false);
            btnNoTemplate.Click += (s, e) => SetSelection(t => !t.HasTemplate);

            _countLabel = new Label
            {
                AutoSize = true,
                ForeColor = Color.DimGray,
                Margin = new Padding(8, 8, 0, 0),
                Text = "已选 0 / 共 0"
            };

            bar.Controls.Add(btnAll);
            bar.Controls.Add(btnNone);
            bar.Controls.Add(btnNoTemplate);
            bar.Controls.Add(_countLabel);

            _gridTarget = CreateGrid();
            _gridTarget.Columns.Add(new DataGridViewCheckBoxColumn
            {
                HeaderText = "复制到",
                DataPropertyName = "IsSelected",
                Width = 56,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                FalseValue = false,
                TrueValue = true,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
            _gridTarget.Columns.Add(MakeTextColumn("视图名称", "ViewName", 52));
            _gridTarget.Columns.Add(MakeTextColumn("类型", "ViewTypeName", 22));
            _gridTarget.Columns.Add(MakeTextColumn("模板状态", "TemplateName", 26));
            _gridTarget.DataSource = _targetBinding;
            _gridTarget.CellFormatting += Grid_CellFormatting;
            _gridTarget.CurrentCellDirtyStateChanged += GridTarget_CurrentCellDirtyStateChanged;
            _gridTarget.CellValueChanged += GridTarget_CellValueChanged;

            layout.Controls.Add(bar, 0, 0);
            layout.Controls.Add(_gridTarget, 0, 1);
            group.Controls.Add(layout);
            return group;
        }

        private System.Windows.Forms.Control BuildFooter()
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0, 10, 0, 0)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _chkOverride = new CheckBox
            {
                Text = "覆盖模式：先清空目标视图原有过滤器",
                Checked = true,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(2, 10, 0, 0)
            };
            layout.Controls.Add(_chkOverride, 0, 0);

            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Anchor = AnchorStyles.Right,
                Margin = new Padding(0)
            };

            var btnCancel = new Button
            {
                Text = "取消",
                Width = 88,
                Height = 30,
                DialogResult = DialogResult.Cancel,
                Margin = new Padding(0, 0, 8, 0)
            };
            var btnOk = new Button
            {
                Text = "确定",
                Width = 110,
                Height = 30,
                Margin = new Padding(0)
            };
            btnOk.Click += BtnOk_Click;

            buttons.Controls.Add(btnCancel);
            buttons.Controls.Add(btnOk);
            layout.Controls.Add(buttons, 1, 0);

            AcceptButton = btnOk;
            CancelButton = btnCancel;

            return layout;
        }

        private static DataGridView CreateGrid()
        {
            return new DataGridView
            {
                Dock = DockStyle.Fill,
                AutoGenerateColumns = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AllowUserToOrderColumns = false,
                ReadOnly = false,
                RowHeadersVisible = false,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                GridColor = Color.FromArgb(228, 228, 228),
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                EditMode = DataGridViewEditMode.EditOnEnter,
                Margin = new Padding(0)
            };
        }

        private static DataGridViewTextBoxColumn MakeTextColumn(string header, string property, int fillWeight)
        {
            return new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                DataPropertyName = property,
                FillWeight = fillWeight,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
        }

        // ==================== 源视图 ====================

        private static ViewTargetItem FindItem(IEnumerable<ViewTargetItem> list, RevitElementId id)
        {
            if (id == null) return null;
            return list.FirstOrDefault(t => t.ViewId == id);
        }

        private void BindSources(IList<ViewTargetItem> sources, ViewTargetItem preset)
        {
            ViewTargetItem target = preset ?? sources.FirstOrDefault();
            if (target == null) return;

            SelectedSource = target;
            _isBindingSources = true;
            _gridSource.DataSource = new BindingList<ViewTargetItem>(sources.ToList());
            _isBindingSources = false;

            // 定位到预选行；SelectionChanged 会因 ViewId 相同直接返回，不会重复刷新
            foreach (DataGridViewRow row in _gridSource.Rows)
            {
                var item = row.DataBoundItem as ViewTargetItem;
                if (item != null && item.ViewId == target.ViewId)
                {
                    row.Selected = true;
                    _gridSource.CurrentCell = row.Cells[0];
                    break;
                }
            }

            ReloadTargets();
        }

        private void GridSource_SelectionChanged(object sender, EventArgs e)
        {
            if (_isBindingSources) return;

            var item = _gridSource.CurrentRow == null
                ? null
                : (_gridSource.CurrentRow.DataBoundItem as ViewTargetItem);
            if (item == null) return;
            if (SelectedSource != null && SelectedSource.ViewId == item.ViewId) return;

            SelectedSource = item;
            ReloadTargets();
        }

        // ==================== 目标视图 ====================

        /// <summary>按当前源视图重建目标列表（复用 ViewSelectionService.CollectTargets），并保留用户勾选。</summary>
        private void ReloadTargets()
        {
            if (_targets != null)
            {
                foreach (ViewTargetItem t in _targets)
                    _pickState[t.ViewId] = t.IsSelected;
            }

            RevitElementId sourceId = SelectedSource == null ? RevitElementId.InvalidElementId : SelectedSource.ViewId;

            List<ViewTargetItem> list = new ViewSelectionService(_doc).CollectTargets(sourceId);

            foreach (ViewTargetItem t in list)
            {
                bool sel;
                if (!_pickState.TryGetValue(t.ViewId, out sel)) sel = true;   // 默认全选（与原 MainWindow 一致）
                t.IsSelected = sel;
                _pickState[t.ViewId] = sel;
            }
            _targets = list;

            _targetBinding.RaiseListChangedEvents = false;
            _targetBinding.Clear();
            foreach (ViewTargetItem t in list)
                _targetBinding.Add(t);
            _targetBinding.RaiseListChangedEvents = true;
            _targetBinding.ResetBindings();

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

        private void UpdateCount()
        {
            if (_countLabel == null) return;

            int total = _targets == null ? 0 : _targets.Count;
            int selected = _targets == null ? 0 : _targets.Count(t => t.IsSelected);
            _countLabel.Text = "已选 " + selected + " / 共 " + total;
        }

        private void GridTarget_CurrentCellDirtyStateChanged(object sender, EventArgs e)
        {
            // 勾选框：点一下立即提交，避免“点了勾但值没写回对象”
            if (_gridTarget.IsCurrentCellDirty && _gridTarget.CurrentCell is DataGridViewCheckBoxCell)
                _gridTarget.CommitEdit(DataGridViewDataErrorContexts.Commit);
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

        // ==================== 模板红色加粗 ====================

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

        /// <summary>返回用户勾选的目标视图集合（已排除源视图）。</summary>
        public List<ViewTargetItem> GetSelectedTargets()
        {
            if (_gridTarget != null) _gridTarget.EndEdit();
            return _targets == null
                ? new List<ViewTargetItem>()
                : _targets.Where(t => t.IsSelected).ToList();
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            if (SelectedSource == null)
            {
                MessageBox.Show(this, "请先在左栏选中一个源视图。", Text,
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            List<ViewTargetItem> selected = GetSelectedTargets();
            if (selected.Count == 0)
            {
                MessageBox.Show(this, "请至少勾选一个目标视图。", Text,
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DialogResult = DialogResult.OK;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _boldFont.Dispose();
        }
    }
}
