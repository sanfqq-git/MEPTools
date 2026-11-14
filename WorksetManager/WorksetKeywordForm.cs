using System;
using System.Drawing;
using System.Windows.Forms;
using MEPTools.Shared;

namespace MEPTools.WorksetManager
{
    /// <summary>
    /// 关键词输入窗体。继承 SpaceSubmitForm 以获得：
    /// - 焦点在 TextBox 时按空格正常输入空格
    /// - 焦点在按钮/其它控件时按空格触发「确定」
    /// </summary>
    public class WorksetKeywordForm : SpaceSubmitForm
    {
        public string Keyword { get; private set; }

        private readonly TextBox _txtKeyword;

        public WorksetKeywordForm(string initialKeyword = "")
        {
            Text = "显示关键词工作集";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(400, 130);

            var lbl = new Label
            {
                AutoSize = true,
                Location = new Point(12, 15),
                Text = "请输入工作集关键词（包含匹配，不区分大小写）："
            };

            _txtKeyword = new TextBox
            {
                Location = new Point(12, 42),
                Width = 376,
                Text = initialKeyword ?? string.Empty
            };

            var btnOk = new Button
            {
                Text = "确定",
                DialogResult = DialogResult.OK,
                Location = new Point(232, 82),
                Width = 75
            };
            // 在 Click 中读取，保证无论回车/空格/鼠标触发都能取到值
            btnOk.Click += (s, e) => { Keyword = _txtKeyword.Text.Trim(); };

            var btnCancel = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Location = new Point(313, 82),
                Width = 75
            };

            Controls.Add(lbl);
            Controls.Add(_txtKeyword);
            Controls.Add(btnOk);
            Controls.Add(btnCancel);

            AcceptButton = btnOk;
            CancelButton = btnCancel;

            Shown += (s, e) =>
            {
                _txtKeyword.Focus();
                _txtKeyword.SelectAll();
            };
        }
    }
}