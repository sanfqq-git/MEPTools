// ============================================================
// 文件：LevelSelectorForm.cs
// 位置：Shared 文件夹
// 用途：弹出窗体让用户选择目标参照标高
// ============================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.DB;

namespace MEPTools.Shared
{
    /// <summary>
    /// 目标参照标高选择窗体。
    /// 继承 SpaceSubmitForm，支持空格键确认。
    /// </summary>
    public class LevelSelectorForm : SpaceSubmitForm
    {
        private ComboBox _cbLevels;
        private Button _btnOk;
        private Button _btnCancel;
        private List<Level> _levels;

        public Level SelectedLevel
        {
            get { return _cbLevels?.SelectedItem as Level; }
        }

        public LevelSelectorForm(Document doc, ElementId defaultLevelId = null)
        {
            InitializeComponent();
            LoadLevels(doc, defaultLevelId);
        }

        private void InitializeComponent()
        {
            this.Text = MEPConfig.GetString("ChangeRefLevel.FormTitle", "选择目标参照标高");
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.ClientSize = new System.Drawing.Size(380, 120);

            var lbl = new Label
            {
                Text = MEPConfig.GetString("ChangeRefLevel.FormLabel", "请选择目标参照标高："),
                Location = new System.Drawing.Point(12, 15),
                Size = new System.Drawing.Size(356, 20)
            };

            _cbLevels = new ComboBox
            {
                Location = new System.Drawing.Point(12, 40),
                Size = new System.Drawing.Size(356, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                FormattingEnabled = true
            };
            _cbLevels.Format += CbLevels_Format;

            _btnOk = new Button
            {
                Text = MEPConfig.GetString("ChangeRefLevel.ButtonOk", "确定"),
                DialogResult = DialogResult.OK,
                Location = new System.Drawing.Point(212, 80),
                Size = new System.Drawing.Size(75, 28)
            };

            _btnCancel = new Button
            {
                Text = MEPConfig.GetString("ChangeRefLevel.ButtonCancel", "取消"),
                DialogResult = DialogResult.Cancel,
                Location = new System.Drawing.Point(293, 80),
                Size = new System.Drawing.Size(75, 28)
            };

            this.Controls.Add(lbl);
            this.Controls.Add(_cbLevels);
            this.Controls.Add(_btnOk);
            this.Controls.Add(_btnCancel);
            this.AcceptButton = _btnOk;
            this.CancelButton = _btnCancel;
        }

        private void CbLevels_Format(object sender, ListControlConvertEventArgs e)
        {
            if (e.ListItem is Level lv)
            {
                e.Value = string.Format("{0}  ({1:F0} mm)", lv.Name, lv.Elevation * 304.8);
            }
        }

        private void LoadLevels(Document doc, ElementId defaultLevelId)
        {
            _levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            foreach (var lv in _levels)
                _cbLevels.Items.Add(lv);

            if (defaultLevelId != null && defaultLevelId != ElementId.InvalidElementId)
            {
                for (int i = 0; i < _levels.Count; i++)
                {
                    if (_levels[i].Id == defaultLevelId)
                    {
                        _cbLevels.SelectedIndex = i;
                        break;
                    }
                }
            }

            if (_cbLevels.SelectedIndex < 0 && _levels.Count > 0)
                _cbLevels.SelectedIndex = 0;
        }
    }
}