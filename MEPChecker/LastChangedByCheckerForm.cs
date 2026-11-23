using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.Shared;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

using Form = System.Windows.Forms.Form;
using Color = System.Drawing.Color;
using Panel = System.Windows.Forms.Panel;
using Point = System.Drawing.Point;

namespace MEPTools.MEPChecker
{
    /// <summary>
    /// 结果展示窗口（WinForms 版本，非模态）。
    /// 只读展示：点击列头排序，双击行在 Revit 中定位并选中该图元，可一键复制整张表。
    /// </summary>
    public class LastChangedByCheckerForm : Form
    {
        private readonly UIDocument _uidoc;
        private readonly List<LastChangedInfo> _rows;
        private DataGridView _grid;

        private int _sortColumnIndex = -1;
        private bool _sortAscending = true;

        public LastChangedByCheckerForm(UIDocument uidoc, List<LastChangedInfo> rows)
        {
            _uidoc = uidoc;
            _rows = rows ?? new List<LastChangedInfo>();
            InitializeUI();
        }

        private void InitializeUI()
        {
            Text = "图元修改信息查询 — 共 " + _rows.Count + " 个";
            Width = 1100;
            Height = 560;
            MinimumSize = new Size(760, 320);
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = true;
            MaximizeBox = true;
            FormBorderStyle = FormBorderStyle.Sizable;

            string currentUser = "—";
            try
            {
                if (_uidoc != null && _uidoc.Document != null &&
                    _uidoc.Document.Application != null)
                {
                    currentUser = _uidoc.Document.Application.Username;
                }
            }
            catch { }

            var lblTip = new Label
            {
                Text = "所有者 = 当前借用该图元的用户；最后修改者 = Revit 记录的最后修改该图元的用户。\r\n"
                     + "当前登录用户：" + currentUser + "　|　双击任意行可在视图中定位并选中该图元；点击列头可排序。",
                Dock = DockStyle.Top,
                Height = 46,
                Padding = new Padding(12, 6, 12, 0),
                ForeColor = Color.FromArgb(85, 85, 85),
                AutoEllipsis = true
            };
            Controls.Add(lblTip);

            var bottomPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 50
            };

            var btnCopy = new Button
            {
                Text = "复制表格",
                Width = 100,
                Height = 30,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnCopy.Click += OnCopyClick;

            var btnClose = new Button
            {
                Text = "关闭",
                Width = 100,
                Height = 30,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnClose.Click += (s, e) => Close();

            bottomPanel.Controls.Add(btnCopy);
            bottomPanel.Controls.Add(btnClose);
            bottomPanel.Resize += (s, e) =>
            {
                btnClose.Location = new Point(
                    bottomPanel.ClientSize.Width - btnClose.Width - 12,
                    (bottomPanel.ClientSize.Height - btnClose.Height) / 2);
                btnCopy.Location = new Point(
                    btnClose.Left - btnCopy.Width - 8,
                    (bottomPanel.ClientSize.Height - btnCopy.Height) / 2);
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
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AllowUserToOrderColumns = false,
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

            AddColumn("ID", "ElementIdValue", 7);
            AddColumn("类别", "CategoryName", 10);
            AddColumn("族 / 类型", "ElementName", 26);
            AddColumn("工作集", "WorksetName", 13);
            AddColumn("当前所有者", "Owner", 11);
            AddColumn("借用状态", "Ownership", 11);
            AddColumn("最后修改者", "LastChangedBy", 11);
            AddColumn("创建者", "Creator", 10);
            AddColumn("借用请求者", "Requesters", 12);
            AddColumn("与中心同步", "CentralStatus", 10);

            _grid.DataSource = _rows;
            _grid.CellDoubleClick += OnCellDoubleClick;
            _grid.ColumnHeaderMouseClick += OnColumnHeaderMouseClick;

            Controls.Add(_grid);
            _grid.BringToFront();
        }

        private void AddColumn(string header, string propertyName, int fillWeight)
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                DataPropertyName = propertyName,
                FillWeight = fillWeight,
                SortMode = DataGridViewColumnSortMode.Programmatic,
                Resizable = DataGridViewTriState.True
            });
        }

        /// <summary>
        /// 点击列头排序。List 绑定 DataGridView 不会自动排序，这里手动排序后重新绑定，
        /// 并在表头显示升/降序箭头。
        /// </summary>
        private void OnColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex < 0 || e.ColumnIndex >= _grid.Columns.Count) return;

            Comparison<LastChangedInfo> comparison =
                GetComparison(_grid.Columns[e.ColumnIndex].DataPropertyName);
            if (comparison == null) return;

            if (_sortColumnIndex == e.ColumnIndex)
            {
                _sortAscending = !_sortAscending;
            }
            else
            {
                _sortColumnIndex = e.ColumnIndex;
                _sortAscending = true;
            }

            Comparison<LastChangedInfo> effective = comparison;
            if (!_sortAscending)
            {
                effective = (a, b) => comparison(b, a);
            }
            _rows.Sort(effective);

            _grid.DataSource = null;
            _grid.DataSource = _rows;

            foreach (DataGridViewColumn col in _grid.Columns)
            {
                col.HeaderCell.SortGlyphDirection = SortOrder.None;
            }
            _grid.Columns[e.ColumnIndex].HeaderCell.SortGlyphDirection =
                _sortAscending ? SortOrder.Ascending : SortOrder.Descending;
        }

        private static Comparison<LastChangedInfo> GetComparison(string propertyName)
        {
            switch (propertyName)
            {
                case "ElementIdValue":
                    return (a, b) => a.ElementIdValue.CompareTo(b.ElementIdValue);
                case "CategoryName":
                    return (a, b) => CompareText(a.CategoryName, b.CategoryName);
                case "ElementName":
                    return (a, b) => CompareText(a.ElementName, b.ElementName);
                case "WorksetName":
                    return (a, b) => CompareText(a.WorksetName, b.WorksetName);
                case "Owner":
                    return (a, b) => CompareText(a.Owner, b.Owner);
                case "Ownership":
                    return (a, b) => CompareText(a.Ownership, b.Ownership);
                case "LastChangedBy":
                    return (a, b) => CompareText(a.LastChangedBy, b.LastChangedBy);
                case "Creator":
                    return (a, b) => CompareText(a.Creator, b.Creator);
                case "Requesters":
                    return (a, b) => CompareText(a.Requesters, b.Requesters);
                case "CentralStatus":
                    return (a, b) => CompareText(a.CentralStatus, b.CentralStatus);
                default:
                    return null;
            }
        }

        private static int CompareText(string a, string b)
        {
            return string.Compare(a ?? string.Empty, b ?? string.Empty,
                StringComparison.CurrentCulture);
        }

        private void OnCellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;

            LastChangedInfo row = _grid.Rows[e.RowIndex].DataBoundItem as LastChangedInfo;
            if (row == null || row.ElementId == null) return;

            try
            {
                var ids = new List<ElementId> { row.ElementId };
                _uidoc.ShowElements(ids);
                _uidoc.Selection.SetElementIds(ids);
            }
            catch
            {
                // 图元可能已被删除或当前视图不可见，静默忽略
            }
        }

        /// <summary>
        /// 按当前表格顺序复制为制表符分隔文本，方便贴到 Excel / 聊天窗口。
        /// </summary>
        private void OnCopyClick(object sender, EventArgs e)
        {
            try
            {
                var sb = new StringBuilder();

                var headers = new List<string>();
                foreach (DataGridViewColumn col in _grid.Columns)
                    headers.Add(col.HeaderText);
                sb.AppendLine(string.Join("\t", headers));

                foreach (DataGridViewRow gridRow in _grid.Rows)
                {
                    var cells = new List<string>();
                    foreach (DataGridViewCell cell in gridRow.Cells)
                    {
                        object value = cell.Value;
                        cells.Add(value == null ? string.Empty : value.ToString());
                    }
                    sb.AppendLine(string.Join("\t", cells));
                }

                Clipboard.SetText(sb.ToString());
                InfoCenterTip.Show("已复制 " + _rows.Count + " 行到剪贴板");
                MEPLogger.Instance.AppendLine("复制表格： " + _rows.Count + " rows");
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("LastChangedByCheckerForm.OnCopyClick", ex);
                InfoCenterTip.Show("复制失败，请查看日志。");
            }
        }
    }
}
