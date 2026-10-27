using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

using Form = System.Windows.Forms.Form;
using Color = System.Drawing.Color;
using Panel = System.Windows.Forms.Panel;
using Point = System.Drawing.Point;

namespace MEPTools.SystemChecker
{
    /// <summary>
    /// 结果展示窗口（WinForms 版本）。
    /// </summary>
    public class SystemCheckerForm : Form
    {
        private readonly UIDocument _uidoc;
        private readonly List<MismatchResult> _results;
        private DataGridView _grid;

        public SystemCheckerForm(UIDocument uidoc, List<MismatchResult> results)
        {
            _uidoc = uidoc;
            _results = results;
            InitializeUI();
        }

        private void InitializeUI()
        {
            Text = "系统不匹配连接检测 — 共 " + _results.Count + " 处";
            Width = 940;
            Height = 540;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.Sizable;

            var lblTip = new Label
            {
                Text = "双击任意行可在 Revit 视图中定位并选中对应的两个图元。",
                Dock = DockStyle.Top,
                Height = 30,
                Padding = new Padding(12, 8, 0, 0),
                ForeColor = Color.FromArgb(85, 85, 85)
            };
            Controls.Add(lblTip);

            var bottomPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 50
            };
            var btnClose = new Button
            {
                Text = "关闭",
                Width = 100,
                Height = 30,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnClose.Location = new Point(bottomPanel.ClientSize.Width - 112, 10);
            btnClose.Click += (s, e) => Close();
            bottomPanel.Controls.Add(btnClose);
            bottomPanel.Resize += (s, e) =>
            {
                btnClose.Location = new Point(
                    bottomPanel.ClientSize.Width - btnClose.Width - 12,
                    (bottomPanel.ClientSize.Height - btnClose.Height) / 2);
            };
            Controls.Add(bottomPanel);

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AutoGenerateColumns = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AllowUserToAddRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                GridColor = Color.FromArgb(220, 220, 220),
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.None,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
            };
            _grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(247, 247, 247)
            };
            _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(235, 235, 235),
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold)
            };

            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "图元1 ID",
                DataPropertyName = "ElementId1Value",
                FillWeight = 8
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "图元1 名称",
                DataPropertyName = "ElementName1",
                FillWeight = 22
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "系统1",
                DataPropertyName = "System1",
                FillWeight = 14
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "图元2 ID",
                DataPropertyName = "ElementId2Value",
                FillWeight = 8
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "图元2 名称",
                DataPropertyName = "ElementName2",
                FillWeight = 22
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "系统2",
                DataPropertyName = "System2",
                FillWeight = 14
            });

            _grid.DataSource = _results;
            _grid.CellDoubleClick += OnCellDoubleClick;

            Controls.Add(_grid);
            _grid.BringToFront();
        }

        private void OnCellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _results.Count) return;

            MismatchResult row = _results[e.RowIndex];
            if (row == null) return;

            var ids = new List<ElementId> { row.ElementId1, row.ElementId2 };

            try
            {
                _uidoc.ShowElements(ids);
                _uidoc.Selection.SetElementIds(ids);
            }
            catch
            {
                // 图元可能已被删除或不可见，静默忽略
            }
        }
    }
}