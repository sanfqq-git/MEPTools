using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MEPTools.MEPPlanarLateralBend
{
    /// <summary>LateralBend 参数输入窗（纯代码构建，无需 XAML）</summary>
    public class LateralBendDialog : Window
    {
        private TextBox _txtAngle;
        private TextBlock _errorText; // 内联错误提示

        public double AngleDeg { get; private set; }
        public bool Confirmed { get; private set; }

        public LateralBendDialog(double defaultAngleDeg = 45.0)
        {
            Title = "MEP 平面拐弯参数";
            Width = 400;
            Height = 240; // 稍微调低高度
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            Topmost = true;

            var grid = new Grid { Margin = new Thickness(16) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 4; i++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            int row = 0;
            _txtAngle = new TextBox { Text = defaultAngleDeg.ToString("F0") };
            AddRow(grid, row++, "拐弯角度 θ(度)：", _txtAngle);

            _errorText = new TextBlock
            {
                Foreground = Brushes.Red,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 4),
                Visibility = Visibility.Collapsed
            };
            Grid.SetRow(_errorText, 1);
            Grid.SetColumnSpan(_errorText, 2);
            grid.Children.Add(_errorText);

            var hint = new TextBlock
            {
                Text = "θ 为斜段与管轴线的夹角。\n" +
                       "常用：45°（斜段拐弯） | 90°（L 形直角拐弯）。\n" +
                       "偏移距离 H 将由下一步拾取的 P2 点自动决定\n" +
                       "（P2 到参考管轴线的垂直距离即为 H）。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 8)
            };
            Grid.SetRow(hint, 2);
            Grid.SetColumnSpan(hint, 2);
            grid.Children.Add(hint);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var btnOk = new Button
            {
                Content = "确定", Width = 80,
                Margin = new Thickness(0, 0, 8, 0), IsDefault = true
            };
            btnOk.Click += (s, e) => OnOk();
            var btnCancel = new Button { Content = "取消", Width = 80, IsCancel = true };
            btnCancel.Click += (s, e) => { DialogResult = false; Close(); };
            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnCancel);
            Grid.SetRow(btnPanel, 3);
            Grid.SetColumn(btnPanel, 1);
            grid.Children.Add(btnPanel);

            // 【修改】键盘监听：支持 Enter 键 和 空格键 确认，Esc 取消
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter || e.Key == Key.Space) 
                { 
                    OnOk(); 
                    e.Handled = true; 
                }
                if (e.Key == Key.Escape) { DialogResult = false; Close(); }
            };
            Content = grid;
            Loaded += (s, e) => { _txtAngle.SelectAll(); _txtAngle.Focus(); };
        }

        private static void AddRow(Grid grid, int row, string label, TextBox tb)
        {
            var lb = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 8, 4)
            };
            Grid.SetRow(lb, row);
            Grid.SetColumn(lb, 0);
            grid.Children.Add(lb);

            tb.VerticalContentAlignment = VerticalAlignment.Center;
            tb.Margin = new Thickness(0, 4, 0, 4);
            tb.GotKeyboardFocus += (s, e) => tb.SelectAll();
            Grid.SetRow(tb, row);
            Grid.SetColumn(tb, 1);
            grid.Children.Add(tb);
        }

        private void OnOk()
        {
            _errorText.Visibility = Visibility.Collapsed;

            if (!double.TryParse(_txtAngle.Text.Trim(), out double ang) || ang <= 0 || ang > 90)
            {
                _errorText.Text = "角度 θ 必须介于 0~90 度之间（含 90°）。";
                _errorText.Visibility = Visibility.Visible;
                _txtAngle.Focus();
                return;
            }

            AngleDeg = ang;
            Confirmed = true;
            DialogResult = true;
            Close();
        }
    }
}