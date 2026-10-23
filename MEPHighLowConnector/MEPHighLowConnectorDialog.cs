using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

// 解决 Connector 命名冲突（此文件不使用，但保持一致性）
using Connector = Autodesk.Revit.DB.Connector;

namespace MEPTools.MEPHighLowConnector
{
    public class ConnectorDialog : Window
    {
        private TextBox _txtAngle;
        public double AngleDeg { get; private set; }
        public bool Confirmed { get; private set; }

        public ConnectorDialog(double defaultAngleDeg = 45.0)
        {
            Title = "MEP 连接参数";
            Width = 380;
            Height = 230;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            Topmost = true;

            var grid = new Grid { Margin = new Thickness(16) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 4; i++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var lb = new TextBlock
            {
                Text = "斜段角度 θ(度)：",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 8, 4)
            };
            Grid.SetRow(lb, 0); Grid.SetColumn(lb, 0); grid.Children.Add(lb);

            _txtAngle = new TextBox
            {
                Text = defaultAngleDeg.ToString("F0"),
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 4)
            };
            _txtAngle.GotKeyboardFocus += (s, e) => _txtAngle.SelectAll();
            Grid.SetRow(_txtAngle, 0); Grid.SetColumn(_txtAngle, 1);
            grid.Children.Add(_txtAngle);

            var hint = new TextBlock
            {
                Text = "θ 为斜段与水平方向（A 组管轴）的夹角：\n" +
                       "  45° = 45° 斜段\n" +
                       "  90° = 竖直段（L 形连接）\n" +
                       "斜段从 A 组管端点出发，自动落到 B 组管。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 8)
            };
            Grid.SetRow(hint, 1); Grid.SetColumnSpan(hint, 2);
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
            Grid.SetRow(btnPanel, 2); Grid.SetColumn(btnPanel, 1);
            grid.Children.Add(btnPanel);

            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) OnOk();
                if (e.Key == Key.Escape) { DialogResult = false; Close(); }
            };
            Content = grid;
            Loaded += (s, e) => { _txtAngle.SelectAll(); _txtAngle.Focus(); };
        }

        private void OnOk()
        {
            if (!double.TryParse(_txtAngle.Text.Trim(), out double ang) || ang <= 0 || ang > 90)
            {
                MessageBox.Show(this, "角度 θ 必须介于 0~90 度之间（含 90°）。",
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