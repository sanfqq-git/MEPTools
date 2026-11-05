using System.Windows.Forms;

namespace MEPTools.Shared
{
    /// <summary>
    /// 带有空格键确认功能的 WinForms 窗体基类。
    /// 特性：如果焦点在文本框内，允许输入空格；如果在按钮或数值框等控件上，按空格触发 AcceptButton。
    /// </summary>
    public class SpaceSubmitForm : Form
    {
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Space)
            {
                Control active = this.ActiveControl;
                
                // 如果焦点在独立的普通文本框内，正常输入空格
                // (注意：NumericUpDown 内部也包含 TextBox，需要排除掉)
                if (active is TextBox && !(active.Parent is UpDownBase))
                {
                    return base.ProcessCmdKey(ref msg, keyData);
                }

                // 其他情况（焦点在数值框、按钮或窗体空白处），按空格触发“确定”按钮
                if (AcceptButton is Button btn && btn.Enabled)
                {
                    btn.PerformClick();
                    return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}