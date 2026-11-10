using System;
using System.Drawing;
using System.Windows.Forms;
using MEPTools.Shared;

namespace MEPTools.MEPTagAlignLeft
{
    /// <summary>
    /// 设置窗体（继承自共享文件夹的 SpaceSubmitForm，支持空格键确认）
    /// </summary>
    public class TagAlignSettingForm : SpaceSubmitForm
    {
        private NumericUpDown numSpacing;
        public double SpacingInMm { get; private set; }

        public TagAlignSettingForm(double currentSpacingInFeet)
        {
            this.Text = "标记对齐 - 行距设置";
            this.Size = new Size(320, 160);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            // 英尺转毫米显示给用户
            double currentMm = currentSpacingInFeet * 304.8;

            Label lbl = new Label
            {
                Text = "请设置标记堆叠的行距（毫米）：",
                Location = new Point(20, 20),
                AutoSize = true
            };

            numSpacing = new NumericUpDown
            {
                Location = new Point(20, 45),
                Width = 260,
                Minimum = 1,
                Maximum = 5000,
                DecimalPlaces = 1,
                Increment = 10,
                Value = Math.Max(1, Math.Min(5000, (decimal)currentMm))
            };

            Button btnOk = new Button
            {
                Text = "保存 (空格)",
                Location = new Point(110, 80),
                Size = new Size(90, 28),
                DialogResult = DialogResult.OK
            };
            
            Button btnCancel = new Button
            {
                Text = "取消",
                Location = new Point(205, 80),
                Size = new Size(75, 28),
                DialogResult = DialogResult.Cancel
            };

            this.Controls.Add(lbl);
            this.Controls.Add(numSpacing);
            this.Controls.Add(btnOk);
            this.Controls.Add(btnCancel);

            this.AcceptButton = btnOk; 
            this.CancelButton = btnCancel;
            this.ActiveControl = numSpacing;

            btnOk.Click += (s, e) =>
            {
                // 记录用户输入的毫米值
                SpacingInMm = (double)numSpacing.Value;
            };
        }
    }
}