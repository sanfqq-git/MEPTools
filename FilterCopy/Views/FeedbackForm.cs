using System;
using System.Drawing;
using System.Windows.Forms;
using MEPTools.FilterCopy.Models;

namespace MEPTools.FilterCopy.Views
{
    /// <summary>
    /// 执行结果反馈窗：成功数量 + 明细日志（含模板安全跳过）。
    ///
    /// ★ 为什么不用原来的 FeedbackWindow.xaml（WPF）
    ///   编译型 XAML（BAML）在运行时靠 pack URI "/MEPTools;component/..." 反查程序集资源，
    ///       new FeedbackWindow() → InitializeComponent() → Application.LoadComponent()
    ///   在 Revit 里这一步并不可靠：插件被动态加载时（RevitAddinManager 还会把 DLL 复制到
    ///   %TEMP%\RevitAddins\MEPTools-Executing-*\ 再加载），同一程序集标识在进程里存在多份，
    ///   pack URI 解析会落到“没有该 BAML 的那一份”，于是抛
    ///       The component 'MEPTools.FilterCopy.Views.FeedbackWindow'
    ///       does not have a resource identified by the URI
    ///       '/MEPTools;component/filtercopy/views/feedbackwindow.xaml'.
    ///   而且这个异常发生在事务提交之后，直接把“已经复制成功”报成“执行失败”。
    ///
    ///   本窗体全部用代码构建 WinForms 界面，不依赖 XAML/BAML、不依赖 pack URI，
    ///   与本模块的 FilterCopyForm 完全同构，稳定性优先。
    /// </summary>
    internal sealed class FeedbackForm : Form
    {
        private const string FormTitle = "执行结果";

        private FeedbackForm(TransferResult result)
        {
            Text = FormTitle;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            Font = new Font("Microsoft YaHei UI", 9F);
            ClientSize = new Size(640, 440);
            MinimumSize = new Size(480, 320);

            BuildUi(result);
        }

        /// <summary>
        /// 显示结果窗。任何异常都不允许外抛：复制已经提交，反馈窗只是展示。
        /// </summary>
        public static void ShowResult(TransferResult result, IntPtr ownerHandle)
        {
            using (var form = new FeedbackForm(result))
            {
                if (ownerHandle != IntPtr.Zero)
                    form.ShowDialog(new WindowHandleOwner(ownerHandle));
                else
                    form.ShowDialog();
            }
        }

        private void BuildUi(TransferResult result)
        {
            int successCount = result == null ? 0 : result.SuccessCount;
            int skipCount = result == null ? 0 : result.Log.Count;

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

            // 顶部摘要（与旧 FeedbackViewModel.Summary 文案完全一致）
            var summary = new Label
            {
                Text = "成功复制到 " + successCount + " 个视图；跳过 / 失败 " + skipCount + " 项（详见日志）。",
                AutoSize = true,
                Font = new Font(Font.FontFamily, 10F, FontStyle.Bold),
                Margin = new Padding(2, 2, 0, 10)
            };
            root.Controls.Add(summary, 0, 0);

            // 明细日志
            var group = new GroupBox
            {
                Text = "执行日志（含模板安全跳过明细）",
                Dock = DockStyle.Fill,
                Padding = new Padding(8),
                Margin = new Padding(0)
            };

            var list = new ListBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9F),
                IntegralHeight = false,
                HorizontalScrollbar = true,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0)
            };

            if (result != null && result.Log.Count > 0)
                list.Items.AddRange(result.Log.ToArray());

            group.Controls.Add(list);
            root.Controls.Add(group, 0, 1);

            // 底部按钮
            var footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0, 10, 0, 0)
            };

            var btnOk = new Button
            {
                Text = "确定",
                Width = 90,
                Height = 30,
                DialogResult = DialogResult.OK,
                Margin = new Padding(0)
            };
            footer.Controls.Add(btnOk);
            root.Controls.Add(footer, 0, 2);

            AcceptButton = btnOk;
            CancelButton = btnOk;   // Esc 也能关（纯展示窗）

            Controls.Add(root);
        }

        /// <summary>把 Revit 主窗口句柄包装成 WinForms 的 owner，保证弹窗居中且不抢焦点。</summary>
        private sealed class WindowHandleOwner : IWin32Window
        {
            public WindowHandleOwner(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; }
        }
    }
}
