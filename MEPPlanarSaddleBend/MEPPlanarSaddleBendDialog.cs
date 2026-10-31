using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MEPTools.MEPPlanarSaddleBend
{
    /// <summary>参数输入窗（纯代码构建，无需 XAML）。</summary>
    public class SaddleBendDialog : Window
    {
        private TextBox _txtAngle;

        public double AngleDeg { get; private set; }
        public bool Confirmed { get; private set; }

        public SaddleBendDialog(double defaultAngleDeg = 45.0)
        {
            Title = "MEP 平面翻弯参数";
            Width = 400;
            Height = 230;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            Topmost = true;

            var grid = new Grid { Margin = new Thickness(16) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 3; i++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            int row = 0;
            _txtAngle = new TextBox { Text = defaultAngleDeg.ToString("F0") };
            AddRow(grid, row++, "斜段角度 θ(度)：", _txtAngle);

            var hint = new TextBlock
            {
                Text = "θ 为斜段与管轴线的夹角。\n" +
                       "常用：45°（斜段翻弯） | 90°（直角翻弯）。\n" +
                       "翻弯高度 H 及宽度由拾取的 P2 点自动计算。\n" +
                       "P1、P2 沿管距离必须 ≥ 2×H/tan(θ)。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 8)
            };
            Grid.SetRow(hint, 1);
            Grid.SetColumnSpan(hint, 2);
            grid.Children.Add(hint);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0)
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
            Grid.SetRow(btnPanel, 2);
            Grid.SetColumn(btnPanel, 1);
            grid.Children.Add(btnPanel);

            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) OnOk();
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
            if (!double.TryParse(_txtAngle.Text.Trim(), out double ang) || ang <= 0 || ang > 90)
            {
                MessageBox.Show(this,
                    "角度 θ 必须介于 0~90 度之间（含 90°）：\n" +
                    "  45° = 斜段翻弯（推荐）\n" +
                    "  90° = 直角翻弯",
                    "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
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