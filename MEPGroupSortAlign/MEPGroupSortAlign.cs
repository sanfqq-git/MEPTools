using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System.Drawing;
using Point = System.Drawing.Point;
using WinForms = System.Windows.Forms;

namespace MEPTools.GroupSortAlign
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class MEPGroupSortAlignCommand : IExternalCommand
    {
        private const double DefaultSpacingMM = 50.0;
        private const double DefaultGroupToleranceMM = 25.0;

        private class MepGroup
        {
            public List<Element> Elements { get; } = new List<Element>();
            public double BaseElevation { get; set; }
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;

            // ---------- 1. 获取预选元素 ----------
            ICollection<ElementId> selectedIds = uiDoc.Selection.GetElementIds();
            if (selectedIds == null || selectedIds.Count < 2)
            {
                TaskDialog.Show("MEP分组排序对齐", "请至少选中两个管道/风管/线槽元素后再执行命令！");
                return Result.Cancelled;
            }

            List<Element> filteredElements = selectedIds
                .Select(id => doc.GetElement(id))
                .Where(e => e is MEPCurve && e.Location is LocationCurve)
                .ToList();

            if (filteredElements.Count < 2)
            {
                TaskDialog.Show("MEP分组排序对齐", "选中的元素中未包含有效的管道、风管或线槽！");
                return Result.Cancelled;
            }

            // ---------- 2. 询问净间距 ----------
            double spacingMM = PromptForValue("MEP分组排序对齐 - 输入净间距",
                "请输入净间距（毫米）：按ESC取消视为默认50mm", DefaultSpacingMM);
            double targetSpacingInternal = UnitUtils.ConvertToInternalUnits(
                spacingMM, UnitTypeId.Millimeters);

            // ---------- 3. 确定排列方向（用第一个预选元素） ----------
            Curve dirCurve = ((LocationCurve)filteredElements[0].Location).Curve;
            XYZ dirVec = dirCurve.GetEndPoint(1) - dirCurve.GetEndPoint(0);
            if (dirVec.GetLength() < 1e-9)
            {
                TaskDialog.Show("MEP分组排序对齐", "首个元素长度为零，无法确定方向。");
                return Result.Cancelled;
            }
            dirVec = dirVec.Normalize();

            XYZ perpVecBase = new XYZ(-dirVec.Y, dirVec.X, 0);
            if (perpVecBase.GetLength() < 1e-9)
                perpVecBase = new XYZ(0, 1, 0);
            perpVecBase = perpVecBase.Normalize();

            // ---------- 4. 按底部标高分组 ----------
            List<MepGroup> groups = GroupByBottomElevation(filteredElements);

            // ---------- 5. 记录原始位置 ----------
            Dictionary<ElementId, XYZ> originalPositions = new Dictionary<ElementId, XYZ>();
            foreach (Element elem in filteredElements)
                originalPositions[elem.Id] = ((LocationCurve)elem.Location).Curve.GetEndPoint(0);

            // ==================== 局部辅助函数 ====================

            double GetRadius(Element elem)
            {
                if (elem is Pipe pipe) return pipe.Diameter / 2.0;
                if (elem is Duct duct) return Math.Max(duct.Width, duct.Height) / 2.0;
                if (elem is CableTray tray) return Math.Max(tray.Width, tray.Height) / 2.0;
                return 0.0;
            }

            void RestorePositions()
            {
                foreach (Element elem in filteredElements)
                {
                    XYZ cur = ((LocationCurve)elem.Location).Curve.GetEndPoint(0);
                    XYZ delta = originalPositions[elem.Id] - cur;
                    if (delta.GetLength() > 1e-9)
                        ElementTransformUtils.MoveElement(doc, elem.Id, delta);
                }
            }

            void ApplyArrangement(XYZ perpVec)
            {
                var groupInfos =
                    new List<(List<Element> sorted, List<double> curProj, List<double> targets)>();

                foreach (var group in groups)
                {
                    if (group.Elements.Count == 0) continue;

                    var sorted = group.Elements
                        .OrderBy(e => ((LocationCurve)e.Location).Curve
                            .GetEndPoint(0).DotProduct(perpVec))
                        .ToList();

                    var curProj = sorted
                        .Select(e => ((LocationCurve)e.Location).Curve
                            .GetEndPoint(0).DotProduct(perpVec))
                        .ToList();

                    // 组内第一条暂放原位，后续按 前半径 + 净间距 + 后半径 依次展开
                    var targets = new List<double> { curProj[0] };
                    for (int i = 1; i < sorted.Count; i++)
                    {
                        double prevR = GetRadius(sorted[i - 1]);
                        double curR = GetRadius(sorted[i]);
                        targets.Add(targets[i - 1] + prevR + targetSpacingInternal + curR);
                    }

                    groupInfos.Add((sorted, curProj, targets));
                }

                if (groupInfos.Count == 0) return;

                // 全局锚点 = 各组首条元素投影的最小值 → 所有组左边缘齐平
                double anchorProj = groupInfos.Select(g => g.curProj[0]).Min();

                // 应用：每组首条对齐到 anchorProj
                foreach (var (sorted, curProj, targets) in groupInfos)
                {
                    double delta = anchorProj - targets[0];
                    for (int i = 0; i < sorted.Count; i++)
                    {
                        double targetProj = targets[i] + delta;
                        double move = targetProj - curProj[i];
                        if (Math.Abs(move) > 1e-9)
                            ElementTransformUtils.MoveElement(
                                doc, sorted[i].Id, perpVec.Multiply(move));
                    }
                }
            }

            // ==================== 事务组：预览 + 反向 ====================
            using (TransactionGroup tg = new TransactionGroup(doc,
                $"MEP分组排序对齐 {spacingMM:0.##}mm"))
            {
                tg.Start();

                bool reversed = false;

                while (true)
                {
                    using (Transaction trans = new Transaction(doc, "MEP分组排序对齐"))
                    {
                        trans.Start();
                        RestorePositions();
                        ApplyArrangement(reversed ? perpVecBase.Negate() : perpVecBase);
                        trans.Commit();
                    }

                    TaskDialog td = new TaskDialog("MEP分组排序对齐 - 结果确认");
                    td.MainInstruction = $"已按净间距 {spacingMM:0.##} mm 排列（共 {groups.Count} 组）";
                    td.MainContent =
                        "当前结果已应用到模型：\n\n" +
                        "· 按 ESC 或关闭对话框 → 接受当前结果\n" +
                        "· 按空格 / 回车，或点击下方按钮 → 反向排列";
                    td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                        "反向排列", "以相反方向重新排列");
                    td.CommonButtons = TaskDialogCommonButtons.None;
                    td.AllowCancellation = true;

                    TaskDialogResult res = td.Show();

                    if (res == TaskDialogResult.CommandLink1)
                    {
                        reversed = !reversed;
                        continue;
                    }
                    break;
                }

                tg.Assimilate();
            }

            return Result.Succeeded;
        }

        // ==================== 分组辅助 ====================

        private static double GetElementSize(Element elem)
        {
            if (elem is Pipe pipe) return pipe.Diameter;
            if (elem is Duct duct) return Math.Min(duct.Width, duct.Height);
            if (elem is CableTray tray) return Math.Min(tray.Width, tray.Height);
            return 0;
        }

        private static double? GetBottomElevationInternal(Element elem)
        {
            if (elem is Duct)
                return elem.get_Parameter(BuiltInParameter.RBS_DUCT_BOTTOM_ELEVATION)?.AsDouble();
            if (elem is Pipe)
                return elem.get_Parameter(BuiltInParameter.RBS_PIPE_BOTTOM_ELEVATION)?.AsDouble();
            if (elem is CableTray)
                return elem.get_Parameter(BuiltInParameter.RBS_CTC_BOTTOM_ELEVATION)?.AsDouble();
            return null;
        }

        /// <summary>
        /// 按底部标高升序分组。容差 = min(默认 25mm, 最小元素尺寸)。
        /// </summary>
        private static List<MepGroup> GroupByBottomElevation(List<Element> elements)
        {
            double minSizeMM = elements
                .Select(GetElementSize)
                .Where(s => s > 1e-9)
                .Select(s => UnitUtils.ConvertFromInternalUnits(s, UnitTypeId.Millimeters))
                .DefaultIfEmpty(DefaultGroupToleranceMM)
                .Min();

            double toleranceMM = Math.Min(DefaultGroupToleranceMM, minSizeMM);
            double toleranceInternal = UnitUtils.ConvertToInternalUnits(
                toleranceMM, UnitTypeId.Millimeters);

            var sorted = elements
                .OrderBy(e => GetBottomElevationInternal(e) ?? 0)
                .ToList();

            List<MepGroup> groups = new List<MepGroup>();
            MepGroup current = null;
            double currentBaseElev = 0;

            foreach (var elem in sorted)
            {
                double elev = GetBottomElevationInternal(elem) ?? 0;

                if (current == null || (elev - currentBaseElev) > toleranceInternal)
                {
                    current = new MepGroup { BaseElevation = elev };
                    groups.Add(current);
                    currentBaseElev = elev;
                }
                current.Elements.Add(elem);
            }

            return groups;
        }

        // ==================== 输入框 ====================

        private static double PromptForValue(string title, string prompt, double defaultValue)
        {
            double result = defaultValue;

            using (WinForms.Form form = new WinForms.Form())
            {
                form.Text = title;
                form.ClientSize = new Size(300, 130);
                form.StartPosition = WinForms.FormStartPosition.CenterScreen;
                form.FormBorderStyle = WinForms.FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.ShowInTaskbar = false;

                WinForms.Label lbl = new WinForms.Label
                {
                    Text = prompt,
                    Location = new Point(15, 15),
                    AutoSize = true
                };

                WinForms.TextBox txt = new WinForms.TextBox
                {
                    Text = defaultValue.ToString("0.##"),
                    Location = new Point(18, 45),
                    Width = 260
                };
                txt.SelectAll();

                WinForms.Button okBtn = new WinForms.Button
                {
                    Text = "确定",
                    Location = new Point(115, 85),
                    Width = 75,
                    DialogResult = WinForms.DialogResult.OK
                };

                WinForms.Button cancelBtn = new WinForms.Button
                {
                    Text = "取消",
                    Location = new Point(200, 85),
                    Width = 75,
                    DialogResult = WinForms.DialogResult.Cancel
                };

                form.Controls.Add(lbl);
                form.Controls.Add(txt);
                form.Controls.Add(okBtn);
                form.Controls.Add(cancelBtn);
                form.AcceptButton = okBtn;
                form.CancelButton = cancelBtn;
                form.ActiveControl = txt;

                WinForms.DialogResult dr = form.ShowDialog();

                if (dr == WinForms.DialogResult.OK)
                {
                    string s = txt.Text.Trim();
                    if (double.TryParse(s, out double val) && val > 0)
                        result = val;
                }
            }

            return result;
        }
    }
}