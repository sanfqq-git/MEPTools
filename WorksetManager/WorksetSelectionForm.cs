using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using MEPTools.Shared;

// ⭐ 解决 Point 命名冲突：明确将 Point 指代 UI 坐标结构
using Point = System.Drawing.Point;

namespace MEPTools.WorksetManager
{
    public class WorksetSelectionForm : SpaceSubmitForm
    {
        public List<string> CheckedWorksetNames { get; private set; }
        
        private readonly CheckedListBox _clbWorksets;

        public WorksetSelectionForm(IList<Workset> allWorksets, IEnumerable<string> checkedNames)
        {
            Text = "选择要显示的工作集";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(360, 480);

            var checkedSet = new HashSet<string>(checkedNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            _clbWorksets = new CheckedListBox
            {
                Location = new Point(12, 12),
                Size = new Size(336, 380),
                CheckOnClick = true,
                IntegralHeight = false,
                HorizontalScrollbar = true
            };

            // ⭐ 性能优化：BeginUpdate/EndUpdate 抑制逐项重绘，
            //    排序逻辑与比较器保持与原版本一致，不改变显示顺序。
            _clbWorksets.BeginUpdate();
            try
            {
                foreach (var ws in allWorksets.OrderBy(w => w.Name))
                {
                    bool isChecked = checkedSet.Contains(ws.Name);
                    _clbWorksets.Items.Add(ws.Name, isChecked);
                }
            }
            finally
            {
                _clbWorksets.EndUpdate();
            }

            // 快捷按钮
            var btnAll = new Button { Text = "全选", Location = new Point(12, 405), Width = 75 };
            btnAll.Click += (s, e) => SetAllChecked(true);

            var btnNone = new Button { Text = "全不选", Location = new Point(95, 405), Width = 75 };
            btnNone.Click += (s, e) => SetAllChecked(false);

            var btnOk = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(192, 405), Width = 75 };
            btnOk.Click += (s, e) =>
            {
                CheckedWorksetNames = _clbWorksets.CheckedItems.Cast<string>().ToList();
            };

            var btnCancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(273, 405), Width = 75 };

            Controls.Add(_clbWorksets);
            Controls.Add(btnAll);
            Controls.Add(btnNone);
            Controls.Add(btnOk);
            Controls.Add(btnCancel);

            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }

        private void SetAllChecked(bool isChecked)
        {
            // ⭐ 性能优化：批量勾选时抑制逐项刷新
            _clbWorksets.BeginUpdate();
            try
            {
                for (int i = 0; i < _clbWorksets.Items.Count; i++)
                {
                    _clbWorksets.SetItemChecked(i, isChecked);
                }
            }
            finally
            {
                _clbWorksets.EndUpdate();
            }
        }
    }
}