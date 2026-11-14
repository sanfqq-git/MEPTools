using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.Shared;

// ⭐ 显式别名，消除歧义
using WinColor   = System.Drawing.Color;
using WinControl = System.Windows.Forms.Control;

namespace MEPTools.Rotate90ByAxis
{
    public class Rotate90AxisForm : SpaceSubmitForm
    {
        private readonly UIDocument _uidoc;
        private readonly Document _doc;
        private readonly List<Element> _targets;
        private readonly XYZ _center;

        private readonly XYZ _axisPlanar;
        private readonly XYZ _axisFlipV;
        private readonly XYZ _axisFlipH;

        private RadioButton _rbPlanar, _rbFlipV, _rbFlipH;
        private Label _effectLabel;
        private Button _btnRotate, _btnClose;

        public Rotate90AxisForm(UIDocument uidoc, List<Element> targets, XYZ center)
        {
            _uidoc = uidoc;
            _doc = uidoc.Document;
            _targets = targets;
            _center = center;

            // ⭐ 固定使用世界坐标轴（俯视图 Z轴，正视图 X轴，侧视图 Y轴）
            _axisPlanar = XYZ.BasisZ;
            _axisFlipV  = XYZ.BasisX;
            _axisFlipH  = XYZ.BasisY;

            // ---------- 窗体基础设置 ----------
            Text = "90° 旋转物体";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            
            // 自动布局，不写死宽高
            AutoScaleMode = AutoScaleMode.Font;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);

            var mainLayout = new TableLayoutPanel
            {
                ColumnCount = 1,
                RowCount = 5,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            // 1. 信息标签
            var infoLabel = new Label
            {
                Text = $"已选 {_targets.Count} 个元素，旋转中心：\n" +
                       $"({_center.X:F2}, {_center.Y:F2}, {_center.Z:F2}) ft",
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 9F),
                Margin = new Padding(0, 0, 0, 10)
            };
            mainLayout.Controls.Add(infoLabel, 0, 0);

            // 2. 旋转方向单选组
            var axisGroup = new GroupBox
            {
                Text = "旋转方向（固定世界轴）",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10, 10, 10, 10),
                Margin = new Padding(0, 0, 0, 10)
            };
            var axisLayout = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill
            };

            _rbPlanar = new RadioButton { Text = "(1) ⟲  平面旋转（绕世界 Z 轴）", AutoSize = true, Checked = true, Margin = new Padding(3, 5, 3, 5) };
            _rbFlipV  = new RadioButton { Text = "(2) ⇅  左右翻转（绕世界 X 轴）",   AutoSize = true, Margin = new Padding(3, 5, 3, 5) };
            _rbFlipH  = new RadioButton { Text = "(3) ⇄  上下翻转（绕世界 Y 轴）",   AutoSize = true, Margin = new Padding(3, 5, 3, 5) };

            axisLayout.Controls.AddRange(new WinControl[] { _rbPlanar, _rbFlipV, _rbFlipH });
            axisGroup.Controls.Add(axisLayout);
            mainLayout.Controls.Add(axisGroup, 0, 1);

            // 3. 效果说明标签
            _effectLabel = new Label
            {
                MaximumSize = new Size(320, 0), 
                AutoSize = true,
                ForeColor = WinColor.SteelBlue,
                Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Italic),
                Margin = new Padding(0, 0, 0, 10)
            };
            mainLayout.Controls.Add(_effectLabel, 0, 2);

            // 4. 快捷键提示
            var hintLabel = new Label
            {
                Text = "按空格键旋转 90°，可连续翻转；按 ESC 结束",
                AutoSize = true,
                ForeColor = WinColor.DimGray,
                Font = new Font("Microsoft YaHei UI", 8.5F),
                Margin = new Padding(0, 0, 0, 10)
            };
            mainLayout.Controls.Add(hintLabel, 0, 3);

            // 5. 底部按钮区
            var bottomLayout = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0)
            };
            _btnRotate = new Button { Text = "旋转 90° (空格)", Size = new Size(130, 30), Margin = new Padding(0, 0, 10, 0) };
            _btnRotate.Click += (s, e) => RotateOnce();

            _btnClose = new Button { Text = "关闭 (ESC)", Size = new Size(100, 30), DialogResult = DialogResult.Cancel };
            _btnClose.Click += (s, e) => this.Close();

            bottomLayout.Controls.AddRange(new WinControl[] { _btnRotate, _btnClose });
            mainLayout.Controls.Add(bottomLayout, 0, 4);

            Controls.Add(mainLayout);

            AcceptButton = _btnRotate;
            CancelButton = _btnClose;

            _rbPlanar.CheckedChanged += (s, e) => UpdateEffectLabel();
            _rbFlipV.CheckedChanged  += (s, e) => UpdateEffectLabel();
            _rbFlipH.CheckedChanged  += (s, e) => UpdateEffectLabel();
            UpdateEffectLabel();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // 拦截 1、2、3 及小键盘 1、2、3，切换对应选项
            if (keyData == Keys.D1 || keyData == Keys.NumPad1)
            {
                _rbPlanar.Checked = true;
                return true;
            }
            if (keyData == Keys.D2 || keyData == Keys.NumPad2)
            {
                _rbFlipV.Checked = true;
                return true;
            }
            if (keyData == Keys.D3 || keyData == Keys.NumPad3)
            {
                _rbFlipH.Checked = true;
                return true;
            }

            // 拦截 ESC 关闭
            if (keyData == Keys.Escape)
            {
                this.DialogResult = DialogResult.Cancel;
                this.Close();
                return true;
            }

            // 其他按键（如空格）交由 SpaceSubmitForm 处理
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void UpdateEffectLabel()
        {
            if (_rbPlanar.Checked)
                _effectLabel.Text = "效果：元素绕世界 Z 轴原地水平旋转";
            else if (_rbFlipV.Checked)
                _effectLabel.Text = "效果：元素绕世界 X 轴上下翻转";
            else
                _effectLabel.Text = "效果：元素绕世界 Y 轴左右翻转";
        }

        private XYZ GetAxisDirection()
        {
            if (_rbFlipV.Checked) return _axisFlipV;
            if (_rbFlipH.Checked) return _axisFlipH;
            return _axisPlanar;
        }

        private string GetAxisName()
        {
            if (_rbFlipV.Checked) return "左右翻转";
            if (_rbFlipH.Checked) return "上下翻转";
            return "平面旋转";
        }

        private void RotateOnce()
        {
            XYZ axisDir = GetAxisDirection();
            if (axisDir == null || axisDir.GetLength() < 1e-9)
            {
                InfoCenterTip.Show("当前旋转轴无效，无法执行旋转");
                return;
            }
            Line axis = Line.CreateUnbound(_center, axisDir);

            bool anyRotated = false;
            int skippedOwner = 0;
            // ⭐ [修改点1] 移除 skippedParallel 变量
            string currentUser = _doc.Application.Username;

            using (Transaction trans = new Transaction(_doc, "Rotate 90°"))
            {
                trans.Start();

                foreach (Element elem in _targets)
                {
                    WorksharingTooltipInfo info = WorksharingUtils.GetWorksharingTooltipInfo(_doc, elem.Id);
                    if (!string.IsNullOrEmpty(info.Owner) && !string.Equals(info.Owner, currentUser, StringComparison.OrdinalIgnoreCase))
                    {
                        skippedOwner++;
                        MEPLogger.Instance.AppendLine($"跳过 {elem.Id}（被 {info.Owner} 拥有）");
                        continue;
                    }

                    // ⭐ [修改点2] 注释/移除了对轴向平行的跳过逻辑
                    // 原因：矩形风管等元素自身有不同面，绕自身平行轴旋转依然有实际意义
                    // if (elem.Location is LocationCurve lc && lc.Curve is Line line)
                    // {
                    //     if (Math.Abs(line.Direction.DotProduct(axisDir)) > 0.99)
                    //     {
                    //         skippedParallel++;
                    //         MEPLogger.Instance.AppendLine($"跳过 {elem.Id}（绕自身轴向旋转无可见变化）");
                    //         continue;
                    //     }
                    // }

                    try
                    {
                        ElementTransformUtils.RotateElement(_doc, elem.Id, axis, Math.PI / 2.0);
                        anyRotated = true;
                    }
                    catch (Exception ex)
                    {
                        MEPLogger.Instance.LogException($"Rotate {elem.Id}", ex);
                    }
                }

                if (anyRotated) trans.Commit();
                else trans.RollBack();
            }

            if (anyRotated)
            {
                _uidoc.RefreshActiveView();
                InfoCenterTip.Show($"已{GetAxisName()} 90°");
                MEPLogger.Instance.AppendLine($"{GetAxisName()} 90° 完成");
            }
            // ⭐ [修改点3] 移除了 skippedParallel 的分支判断，直接返回无旋转提示
            else
            {
                InfoCenterTip.Show("没有可旋转的元素（可能被他人拥有或旋转失败）");
            }
        }
    }
}