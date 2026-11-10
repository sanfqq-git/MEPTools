using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.Shared;

namespace MEPTools.Rotate90
{
    /// <summary>
    /// 模态窗体，每次按空格键或点击按钮对目标元素执行一次 90° 旋转。
    /// 继承 SpaceSubmitForm，空格键直接触发 AcceptButton。
    /// </summary>
    public class Rotate90Form : SpaceSubmitForm
    {
        private readonly UIDocument _uidoc;
        private readonly Document _doc;
        private readonly List<Element> _targets;
        private readonly Line _axis;
        private readonly Button _rotateButton;

        public Rotate90Form(UIDocument uidoc, List<Element> targets, Line axis)
        {
            _uidoc = uidoc;
            _doc = uidoc.Document;
            _targets = targets;
            _axis = axis;

            Text = "90° 旋转";
            Width = 320;
            Height = 160;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;

            // 说明标签
            var label = new Label
            {
                Text = "按空格键旋转 90°\n可连续按空格多次翻转\n关闭窗口结束",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Microsoft YaHei UI", 9F)
            };
            Controls.Add(label);

            // 旋转按钮（同时作为 AcceptButton，空格键会触发它）
            _rotateButton = new Button
            {
                Text = "旋转 90°",
                Width = 120,
                Height = 30
            };
            _rotateButton.Click += (s, e) => RotateTargetsOnce();

            AcceptButton = _rotateButton;

            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 50,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(10, 8, 10, 8)
            };
            panel.Controls.Add(_rotateButton);
            Controls.Add(panel);
        }

        /// <summary>
        /// 执行一次 90° 旋转。只对当前用户拥有（或未被拥有）的元素生效，
        /// 且没有任何元素实际旋转时回滚事务，避免无意义修改。
        /// </summary>
        private void RotateTargetsOnce()
        {
            bool anyRotated = false;
            string currentUser = _doc.Application.Username;

            using (Transaction trans = new Transaction(_doc, "Rotate 90°"))
            {
                trans.Start();

                foreach (Element elem in _targets)
                {
                    // 工作共享环境下，跳过他人拥有的元素
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
                            _doc, elem.Id, _axis, Math.PI / 2.0);
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
                InfoCenterTip.Show("已旋转 90°");
            }
            else
            {
                InfoCenterTip.Show("没有可旋转的元素（可能被他人拥有或旋转失败）");
            }
        }
    }
}