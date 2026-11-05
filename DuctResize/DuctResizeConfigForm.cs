using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MEPTools.Shared;

using Form = System.Windows.Forms.Form;
using TextBox = System.Windows.Forms.TextBox;
using Panel = System.Windows.Forms.Panel;
using Point = System.Drawing.Point;

namespace MEPTools.DuctResize
{
    public class DuctResizeConfigForm : SpaceSubmitForm
    {
        private readonly ResizeConfig _source;
        private NumericUpDown _nudWidth;
        private NumericUpDown _nudHeight;
        private TextBox _txtWidthParams;
        private TextBox _txtHeightParams;
        private Panel _advPanel;
        private Button _btnAdvanced;
        private Button _btnOk;
        private Button _btnCancel;

        private const int CollapsedHeight = 220;
        private const int ExpandedHeight = 330;

        private DuctResizeConfigForm(ResizeConfig config)
        {
            _source = config ?? new ResizeConfig();
            BuildUi();
            LoadValues();
        }

        /// <summary>
        /// 静态弹窗方法：内部处理 Revit 窗口挂载和返回值。
        /// </summary>
        public static ResizeConfig Show(ResizeConfig config)
        {
            using (var form = new DuctResizeConfigForm(config))
            {
                // 获取 Revit 主窗口句柄
                var owner = new WindowWrapper(Autodesk.Windows.ComponentManager.ApplicationWindow);
                DialogResult dr = form.ShowDialog(owner);
                
                return dr == DialogResult.OK ? form.GetConfig() : null;
            }
        }

        private class WindowWrapper : IWin32Window
        {
            public IntPtr Handle { get; }
            public WindowWrapper(IntPtr handle) { Handle = handle; }
        }

        private void BuildUi()
        {
            Text = "风管尺寸修改";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 9F);
            ClientSize = new Size(360, CollapsedHeight);

            Controls.Add(new Label { Text = "目标宽度 (mm)：", Location = new Point(20, 25), AutoSize = true });
            _nudWidth = new NumericUpDown
            {
                Location = new Point(150, 22),
                Width = 175,
                Minimum = 1,
                Maximum = 10000,
                DecimalPlaces = 1,
                Increment = 10,
            };
            Controls.Add(_nudWidth);

            Controls.Add(new Label { Text = "目标高度 (mm)：", Location = new Point(20, 65), AutoSize = true });
            _nudHeight = new NumericUpDown
            {
                Location = new Point(150, 62),
                Width = 175,
                Minimum = 1,
                Maximum = 10000,
                DecimalPlaces = 1,
                Increment = 10,
            };
            Controls.Add(_nudHeight);

            _btnAdvanced = new Button
            {
                Text = "高级参数 >>",
                Location = new Point(20, 100),
                Width = 120,
                Height = 26,
            };
            _btnAdvanced.Click += (s, e) => ToggleAdvanced();
            Controls.Add(_btnAdvanced);

            _advPanel = new Panel
            {
                Location = new Point(0, 130),
                Size = new Size(ClientSize.Width, 90),
                Visible = false,
            };
            _advPanel.Controls.Add(new Label { Text = "宽度参数名：", Location = new Point(20, 8), AutoSize = true });
            _txtWidthParams = new TextBox
            {
                Location = new Point(150, 5),
                Width = 175,
            };
            _advPanel.Controls.Add(_txtWidthParams);
            _advPanel.Controls.Add(new Label { Text = "高度参数名：", Location = new Point(20, 40), AutoSize = true });
            _txtHeightParams = new TextBox
            {
                Location = new Point(150, 37),
                Width = 175,
            };
            _advPanel.Controls.Add(_txtHeightParams);
            Controls.Add(_advPanel);

            _btnOk = new Button
            {
                Text = "确定",
                DialogResult = DialogResult.OK,
                Location = new Point(150, 180),
                Width = 80,
                Height = 30,
            };
            _btnCancel = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Location = new Point(240, 180),
                Width = 80,
                Height = 30,
            };
            Controls.Add(_btnOk);
            Controls.Add(_btnCancel);

            AcceptButton = _btnOk;
            CancelButton = _btnCancel;
        }

        private void ToggleAdvanced()
        {
            if (_advPanel.Visible)
            {
                _advPanel.Visible = false;
                _btnAdvanced.Text = "高级参数 >>";
                ClientSize = new Size(ClientSize.Width, CollapsedHeight);
                _btnOk.Top = _btnCancel.Top = 180;
            }
            else
            {
                _advPanel.Visible = true;
                _btnAdvanced.Text = "高级参数 <<";
                ClientSize = new Size(ClientSize.Width, ExpandedHeight);
                _btnOk.Top = _btnCancel.Top = 280;
            }
        }

        private void LoadValues()
        {
            _nudWidth.Value = ClampDecimal((decimal)_source.TargetWidthMm, _nudWidth);
            _nudHeight.Value = ClampDecimal((decimal)_source.TargetHeightMm, _nudHeight);
            _txtWidthParams.Text = string.Join(",", _source.WidthParams ?? new List<string>());
            _txtHeightParams.Text = string.Join(",", _source.HeightParams ?? new List<string>());
        }

        private static decimal ClampDecimal(decimal v, NumericUpDown nud)
        {
            if (v < nud.Minimum) return nud.Minimum;
            if (v > nud.Maximum) return nud.Maximum;
            return v;
        }

        private ResizeConfig GetConfig()
        {
            return new ResizeConfig
            {
                TargetWidthMm = (double)_nudWidth.Value,
                TargetHeightMm = (double)_nudHeight.Value,
                WidthParams = SplitCsv(_txtWidthParams.Text),
                HeightParams = SplitCsv(_txtHeightParams.Text),
            };
        }

        private static List<string> SplitCsv(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return new List<string>();
            return s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => x.Length > 0)
                    .ToList();
        }
    }
}