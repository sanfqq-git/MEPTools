using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.Shared;

// ⭐ 显式别名，消除 Autodesk.Revit.DB 与 WinForms 之间的类型歧义
using WinPoint = System.Drawing.Point;
using WinColor = System.Drawing.Color;
using WinControl = System.Windows.Forms.Control;

namespace MEPTools.Rotate90ByAxis
{
    /// <summary>
    /// XYZ 轴旋转窗体：
    /// - 单选 X / Y / Z 世界轴方向；
    /// - 旋转中心固定为整体包围盒中心；
    /// - 空格键连续 90° 翻转，ESC 结束。
    /// </summary>
    public class Rotate90AxisForm : SpaceSubmitForm
    {
        private readonly UIDocument _uidoc;
        private readonly Document _doc;
        private readonly List<Element> _targets;
        private readonly XYZ _center;

        private RadioButton _rbX, _rbY, _rbZ;
        private Button _btnRotate, _btnClose;

        public Rotate90AxisForm(UIDocument uidoc, List<Element> targets, XYZ center)
        {
            _uidoc = uidoc;
            _doc = uidoc.Document;
            _targets = targets;
            _center = center;

            Text = "90° 旋转物体 (XYZ 轴)";
            Width = 340;
            Height = 230;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;

            // 信息标签
            var infoLabel = new Label
            {
                Text = $"已选 {_targets.Count} 个元素，旋转中心：\n" +
                       $"({_center.X:F2}, {_center.Y:F2}, {_center.Z:F2}) ft",
                Location = new WinPoint(12, 10),
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 9F)
            };
            Controls.Add(infoLabel);

            // 旋转轴方向
            var axisGroup = new GroupBox
            {
                Text = "旋转轴方向",
                Location = new WinPoint(12, 55),
                Size = new Size(300, 60)
            };
            _rbX = new RadioButton { Text = "X 轴", Location = new WinPoint(15, 25), AutoSize = true };
            _rbY = new RadioButton { Text = "Y 轴", Location = new WinPoint(105, 25), AutoSize = true };
            _rbZ = new RadioButton { Text = "Z 轴", Location = new WinPoint(195, 25), AutoSize = true, Checked = true };
            axisGroup.Controls.AddRange(new WinControl[] { _rbX, _rbY, _rbZ });
            Controls.Add(axisGroup);

            // 提示
            var hintLabel = new Label
            {
                Text = "按空格键旋转 90°，可连续翻转；按 ESC 结束",
                Location = new WinPoint(12, 122),
                AutoSize = true,
                ForeColor = WinColor.DimGray,
                Font = new Font("Microsoft YaHei UI", 8.5F)
            };
            Controls.Add(hintLabel);

            // 旋转按钮（AcceptButton，空格键触发）
            _btnRotate = new Button
            {
                Text = "旋转 90° (空格)",
                Location = new WinPoint(50, 152),
                Size = new Size(130, 30)
            };
            _btnRotate.Click += (s, e) => RotateOnce();

            // 关闭按钮（CancelButton，ESC 触发）
            _btnClose = new Button
            {
                Text = "关闭 (ESC)",
                Location = new WinPoint(190, 152),
                Size = new Size(100, 30),
                DialogResult = DialogResult.Cancel
            };
            _btnClose.Click += (s, e) => this.Close();

            Controls.Add(_btnRotate);
            Controls.Add(_btnClose);

            AcceptButton = _btnRotate;
            CancelButton = _btnClose;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                this.DialogResult = DialogResult.Cancel;
                this.Close();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private XYZ GetAxisDirection()
        {
            if (_rbX.Checked) return XYZ.BasisX;
            if (_rbY.Checked) return XYZ.BasisY;
            return XYZ.BasisZ;
        }

        private string GetAxisName()
        {
            if (_rbX.Checked) return "X";
            if (_rbY.Checked) return "Y";
            return "Z";
        }

        /// <summary>
        /// 执行一次 90° 旋转：
        /// - 跳过被他人拥有的元素，避免协同污染；
        /// - 若无元素实际旋转，回滚事务；
        /// - 提交后刷新视图并用 InfoCenter 提示。
        /// </summary>
        private void RotateOnce()
        {
            XYZ axisDir = GetAxisDirection();
            Line axis = Line.CreateUnbound(_center, axisDir);

            bool anyRotated = false;
            string currentUser = _doc.Application.Username;

            using (Transaction trans = new Transaction(_doc, "Rotate 90° (XYZ Axis)"))
            {
                trans.Start();

                foreach (Element elem in _targets)
                {
                    WorksharingTooltipInfo info =
                        WorksharingUtils.GetWorksharingTooltipInfo(_doc, elem.Id);

                    if (!string.IsNullOrEmpty(info.Owner) &&
                        !string.Equals(info.Owner, currentUser, StringComparison.OrdinalIgnoreCase))
                    {
                        MEPLogger.Instance.AppendLine(
                            $"跳过 {elem.Id}（被 {info.Owner} 拥有）");
                        continue;
                    }

                    try
                    {
                        ElementTransformUtils.RotateElement(
                            _doc, elem.Id, axis, Math.PI / 2.0);
                        anyRotated = true;
                    }
                    catch (Exception ex)
                    {
                        MEPLogger.Instance.LogException($"Rotate {elem.Id}", ex);
                    }
                }

                if (anyRotated)
                    trans.Commit();
                else
                    trans.RollBack();
            }

            if (anyRotated)
            {
                _uidoc.RefreshActiveView();
                string axisName = GetAxisName();
                InfoCenterTip.Show($"已绕 {axisName} 轴旋转 90°");
                MEPLogger.Instance.AppendLine($"绕 {axisName} 轴旋转 90° 完成");
            }
            else
            {
                InfoCenterTip.Show("没有可旋转的元素（可能被他人拥有或旋转失败）");
            }
        }
    }
}