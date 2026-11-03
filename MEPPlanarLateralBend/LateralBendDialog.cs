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
        private TextBox _txtOffset;
        private TextBox _txtAngle;
        private TextBlock _errorText; // 内联错误提示

        public double OffsetMm { get; private set; }
        public double AngleDeg { get; private set; }
        public bool Confirmed { get; private set; }

        public LateralBendDialog(double defaultOffsetMm = 200.0, double defaultAngleDeg = 45.0)
        {
            Title = "MEP 平面拐弯参数";
            Width = 400;
            Height = 300;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            Topmost = true;

            var grid = new Grid { Margin = new Thickness(16) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 5; i++) 
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            int row = 0;
            _txtOffset = new TextBox { Text = defaultOffsetMm.ToString("F0") };
            AddRow(grid, row++, "偏移距离 H(mm)：", _txtOffset);
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
            Grid.SetRow(_errorText, 2);
            Grid.SetColumnSpan(_errorText, 2);
            grid.Children.Add(_errorText);

            var hint = new TextBlock
            {
                Text = "θ 为斜段与管轴线的夹角。\n" +
                       "常用：45°（斜段拐弯） | 90°（L 形直角拐弯）。\n" +
                       "H 从中心线算起；拐弯方向由 P2 相对 P1 的方位决定。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 8)
            };
            Grid.SetRow(hint, 3);
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
            Grid.SetRow(btnPanel, 4);
            Grid.SetColumn(btnPanel, 1);
            grid.Children.Add(btnPanel);

            // 仅使用 Enter 确定，ESC 取消
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) OnOk();
                if (e.Key == Key.Escape) { DialogResult = false; Close(); }
            };
            Content = grid;
            Loaded += (s, e) => { _txtOffset.SelectAll(); _txtOffset.Focus(); };
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

            if (!double.TryParse(_txtOffset.Text.Trim(), out double off) || off <= 0)
            {
                _errorText.Text = "偏移距离 H 必须为大于 0 的数值。";
                _errorText.Visibility = Visibility.Visible;
                _txtOffset.Focus();
                return;
            }

            if (!double.TryParse(_txtAngle.Text.Trim(), out double ang) || ang <= 0 || ang > 90)
            {
                _errorText.Text = "角度 θ 必须介于 0~90 度之间（含 90°）。";
                _errorText.Visibility = Visibility.Visible;
                _txtAngle.Focus();
                return;
            }

            OffsetMm = off;
            AngleDeg = ang;
            Confirmed = true;
            DialogResult = true;
            Close();
        }
    }
}