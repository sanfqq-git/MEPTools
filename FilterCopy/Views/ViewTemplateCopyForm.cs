using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using MEPTools.FilterCopy.Services;
using RevitDoc = Autodesk.Revit.DB.Document;
using WinForms = System.Windows.Forms;

namespace MEPTools.FilterCopy.Views
{
    /// <summary>
    /// 「从其它文档复制视图样板」窗体（纯 WinForms、代码构建、零 XAML）：
    /// 下拉框选源文档 → 列出该文档所有视图样板 → 勾选 → 复制到当前文档。
    /// </summary>
    internal class ViewTemplateCopyForm : WinForms.Form
    {
        private readonly ViewTemplateCopyService _service;
        private readonly BindingList<ViewTemplateInfo> _binding = new BindingList<ViewTemplateInfo>();

        private WinForms.ComboBox _cmbDoc;
        private WinForms.DataGridView _grid;
        private WinForms.Label _lblCount;
        private WinForms.Button _btnCopy;
        private List<ViewTemplateInfo> _items;

        /// <summary>本次已成功复制的样板数量（供调用方提示）。</summary>
        public int CopiedCount { get; private set; }

        public ViewTemplateCopyForm(RevitDoc targetDoc)
        {
            _service = new ViewTemplateCopyService(targetDoc);

            Text = "从其它文档复制视图样板";
            StartPosition = WinForms.FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = WinForms.AutoScaleMode.Font;
            ClientSize = new Size(780, 540);
            MinimumSize = new Size(680, 420);

            BuildUi();
            LoadDocuments();
        }

        // ==================== 界面 ====================

        private void BuildUi()
        {
            var root = new WinForms.TableLayoutPanel
            {
                Dock = WinForms.DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new WinForms.Padding(12)
            };
            root.ColumnStyles.Add(new WinForms.ColumnStyle(WinForms.SizeType.Percent, 100F));
            root.RowStyles.Add(new WinForms.RowStyle(WinForms.SizeType.AutoSize));
            root.RowStyles.Add(new WinForms.RowStyle(WinForms.SizeType.Percent, 100F));
            root.RowStyles.Add(new WinForms.RowStyle(WinForms.SizeType.AutoSize));

            // ① 源文档
            var docBar = new WinForms.FlowLayoutPanel
            {
                Dock = WinForms.DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = WinForms.AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = new WinForms.Padding(0, 0, 0, 8)
            };

            docBar.Controls.Add(new WinForms.Label
            {
                Text = "源文档：",
                AutoSize = true,
                Margin = new WinForms.Padding(2, 8, 4, 0)
            });

            _cmbDoc = new WinForms.ComboBox
            {
                DropDownStyle = WinForms.ComboBoxStyle.DropDownList,
                Width = 380,
                Margin = new WinForms.Padding(0, 4, 8, 0)
            };
            _cmbDoc.SelectedIndexChanged += (s, e) => LoadTemplates(SelectedDoc());
            docBar.Controls.Add(_cmbDoc);

            docBar.Controls.Add(new WinForms.Label
            {
                Text = "只列当前已打开文档；换文档自动刷新列表（只搬样板本体+过滤器，不改现有视图绑定）",
                AutoSize = true,
                ForeColor = Color.DimGray,
                Margin = new WinForms.Padding(2, 8, 0, 0)
            });
            root.Controls.Add(docBar, 0, 0);

            // ② 样板列表
            _grid = new WinForms.DataGridView
            {
                Dock = WinForms.DockStyle.Fill,
                AutoGenerateColumns = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AllowUserToOrderColumns = false,
                RowHeadersVisible = false,
                BackgroundColor = Color.White,
                BorderStyle = WinForms.BorderStyle.FixedSingle,
                SelectionMode = WinForms.DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                GridColor = Color.FromArgb(228, 228, 228),
                ColumnHeadersHeightSizeMode = WinForms.DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                AutoSizeColumnsMode = WinForms.DataGridViewAutoSizeColumnsMode.Fill,
                EditMode = WinForms.DataGridViewEditMode.EditOnEnter,
                Margin = new WinForms.Padding(0)
            };

            _grid.Columns.Add(new WinForms.DataGridViewCheckBoxColumn
            {
                HeaderText = "复制",
                DataPropertyName = "IsSelected",
                Width = 52,
                AutoSizeMode = WinForms.DataGridViewAutoSizeColumnMode.None,
                FalseValue = false,
                TrueValue = true,
                SortMode = WinForms.DataGridViewColumnSortMode.NotSortable
            });
            _grid.Columns.Add(MakeColumn("样板名称", "TemplateName", 60));
            _grid.Columns.Add(MakeColumn("类型", "ViewTypeName", 20));
            _grid.Columns.Add(MakeColumn("过滤器数", "FilterCount", 18));

            _grid.DataSource = _binding;
            _grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                // 勾选框点一下立即提交，避免“点了勾但值没写回对象”
                if (_grid.IsCurrentCellDirty && _grid.CurrentCell is WinForms.DataGridViewCheckBoxCell)
                    _grid.CommitEdit(WinForms.DataGridViewDataErrorContexts.Commit);
            };
            _grid.CellValueChanged += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.ColumnIndex >= 0 &&
                    _grid.Columns[e.ColumnIndex] is WinForms.DataGridViewCheckBoxColumn)
                    UpdateCount();
            };
            root.Controls.Add(_grid, 0, 1);

            // ③ 底部
            var footer = new WinForms.TableLayoutPanel
            {
                Dock = WinForms.DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new WinForms.Padding(0, 10, 0, 0)
            };
            footer.ColumnStyles.Add(new WinForms.ColumnStyle(WinForms.SizeType.Percent, 100F));
            footer.ColumnStyles.Add(new WinForms.ColumnStyle(WinForms.SizeType.AutoSize));

            _lblCount = new WinForms.Label
            {
                Text = "共 0 个样板，已勾选 0 个",
                AutoSize = true,
                ForeColor = Color.DimGray,
                Margin = new WinForms.Padding(2, 10, 0, 0)
            };
            footer.Controls.Add(_lblCount, 0, 0);

            var buttons = new WinForms.FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = WinForms.AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Anchor = WinForms.AnchorStyles.Right,
                Margin = new WinForms.Padding(0)
            };

            var btnAll = new WinForms.Button { Text = "全选", Width = 70, Height = 30, Margin = new WinForms.Padding(0, 0, 6, 0) };
            var btnNone = new WinForms.Button { Text = "全不选", Width = 70, Height = 30, Margin = new WinForms.Padding(0, 0, 6, 0) };
            _btnCopy = new WinForms.Button { Text = "复制到当前文档", Width = 130, Height = 30, Margin = new WinForms.Padding(0, 0, 6, 0) };
            var btnClose = new WinForms.Button { Text = "关闭", Width = 80, Height = 30, Margin = new WinForms.Padding(0) };

            btnAll.Click += (s, e) => SetSelection(true);
            btnNone.Click += (s, e) => SetSelection(false);
            _btnCopy.Click += BtnCopy_Click;
            btnClose.Click += (s, e) => { DialogResult = WinForms.DialogResult.Cancel; Close(); };

            buttons.Controls.Add(btnAll);
            buttons.Controls.Add(btnNone);
            buttons.Controls.Add(_btnCopy);
            buttons.Controls.Add(btnClose);
            footer.Controls.Add(buttons, 1, 0);
            root.Controls.Add(footer, 0, 2);

            Controls.Add(root);
            AcceptButton = _btnCopy;
        }

        private static WinForms.DataGridViewTextBoxColumn MakeColumn(string header, string property, int fillWeight)
        {
            return new WinForms.DataGridViewTextBoxColumn
            {
                HeaderText = header,
                DataPropertyName = property,
                FillWeight = fillWeight,
                ReadOnly = true,
                SortMode = WinForms.DataGridViewColumnSortMode.NotSortable
            };
        }

        // ==================== 数据 ====================

        private sealed class DocItem
        {
            public RevitDoc Doc;
            public string Display;

            public override string ToString()
            {
                return Display;
            }
        }

        private RevitDoc SelectedDoc()
        {
            var item = _cmbDoc == null ? null : _cmbDoc.SelectedItem as DocItem;
            return item == null ? null : item.Doc;
        }

        private void LoadDocuments()
        {
            _cmbDoc.Items.Clear();

            foreach (RevitDoc d in _service.CollectSourceDocuments())
            {
                string title = string.IsNullOrEmpty(d.Title) ? "（未命名文档）" : d.Title;
                _cmbDoc.Items.Add(new DocItem { Doc = d, Display = title });
            }

            if (_cmbDoc.Items.Count == 0)
            {
                _cmbDoc.Enabled = false;
                _btnCopy.Enabled = false;
                LoadTemplates(null);
                WinForms.MessageBox.Show(this,
                    "当前 Revit 会话里没有其它已打开的文档。\n请先打开样板来源文件（母版项目 / 标准图库），再回来执行。",
                    Text, WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Information);
                return;
            }

            _cmbDoc.SelectedIndex = 0;
        }

        private void LoadTemplates(RevitDoc sourceDoc)
        {
            _items = _service.CollectTemplates(sourceDoc);
            _btnCopy.Enabled = sourceDoc != null;

            _binding.RaiseListChangedEvents = false;
            _binding.Clear();
            foreach (ViewTemplateInfo t in _items) _binding.Add(t);
            _binding.RaiseListChangedEvents = true;
            _binding.ResetBindings();

            UpdateCount();
        }

        private void SetSelection(bool value)
        {
            if (_items == null) return;
            foreach (ViewTemplateInfo t in _items) t.IsSelected = value;
            _grid.Refresh();
            UpdateCount();
        }

        private void UpdateCount()
        {
            if (_lblCount == null) return;
            int total = _items == null ? 0 : _items.Count;
            int selected = _items == null ? 0 : _items.Count(t => t.IsSelected);
            _lblCount.Text = "共 " + total + " 个样板，已勾选 " + selected + " 个";
        }

        // ==================== 复制 ====================

        private void BtnCopy_Click(object sender, EventArgs e)
        {
            _grid.EndEdit();

            List<ViewTemplateInfo> selected = _items == null
                ? new List<ViewTemplateInfo>()
                : _items.Where(t => t.IsSelected).ToList();

            if (selected.Count == 0)
            {
                WinForms.MessageBox.Show(this, "请至少勾选一个视图样板。", Text,
                    WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Warning);
                return;
            }

            WinForms.Cursor previous = Cursor;
            Cursor = WinForms.Cursors.WaitCursor;
            try
            {
                var result = _service.CopyTemplates(selected);
                CopiedCount += result.SuccessCount;

                // 明细写进 MEPLogger，事后能复盘每个样板/过滤器发生了什么
                MEPTools.Shared.MEPLogger.Instance.AppendLine(
                    "复制视图样板：成功 " + result.SuccessCount + " 个，失败 " + result.FailCount + " 个。");
                foreach (string line in result.Log)
                    MEPTools.Shared.MEPLogger.Instance.AppendLine("  " + line);

                string detail = result.Log.Count == 0
                    ? string.Empty
                    : Environment.NewLine + string.Join(Environment.NewLine, result.Log.Take(12).ToArray());

                WinForms.MessageBox.Show(this,
                    "已复制 " + result.SuccessCount + " 个视图样板到当前文档，失败 " + result.FailCount + " 个。" + detail,
                    Text, WinForms.MessageBoxButtons.OK,
                    result.SuccessCount > 0 ? WinForms.MessageBoxIcon.Information : WinForms.MessageBoxIcon.Warning);

                if (result.SuccessCount > 0)
                {
                    DialogResult = WinForms.DialogResult.OK;
                    Close();
                }
                // 全部失败：窗口不关，换样板或换源文档再试
            }
            catch (Exception ex)
            {
                WinForms.MessageBox.Show(this, "复制视图样板失败：" + ex.Message, Text,
                    WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = previous;
            }
        }
    }
}
