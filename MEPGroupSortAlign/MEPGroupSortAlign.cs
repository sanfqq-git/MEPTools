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
using MEPTools.Shared; // 引入共享类

namespace MEPTools.MEPGroupSortAlign
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Command : IExternalCommand
    {
        private const double DefaultSpacingMM = 50.0;
        private const double DefaultGroupToleranceMM = 25.0;

        private class MepGroup
        {
            public List<Element> Elements { get; } = new List<Element>();
            public double BaseElevation { get; set; }
        }

        public class MEPCurveSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is MEPCurve && elem.Location is LocationCurve;
            public bool AllowReference(Reference reference, XYZ position) => true;
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;

            MEPLogger.Instance.StartSession("MEP分组排序对齐");

            // ---------- 1. 使用通用方法获取预选或交互式选择的元素 ----------
            List<Element> filteredElements = MEPSelectionHelper.GetElements(
                uidoc: uiDoc,
                filter: new MEPCurveSelectionFilter(),
                pickPrompt: "请选择至少两个管道/风管/线槽，按空格键完成，按ESC取消",
                infoTipNoPreselection: "请选择至少两个MEP元素，按空格键完成",
                minCount: 2,
                useSpaceKey: true);

            if (filteredElements.Count < 2)
            {
                return Result.Cancelled;
            }

            // ---------- 2. 询问净间距（WinForms 输入框） ----------
            double spacingMM = PromptForValue("MEP分组排序对齐 - 输入净间距",
                "请输入净间距（毫米）：按ESC取消视为默认50mm，按空格确认", DefaultSpacingMM);
            double targetSpacingInternal = UnitUtils.ConvertToInternalUnits(
                spacingMM, UnitTypeId.Millimeters);

            // ---------- 3. 确定排列方向 ----------
            Curve dirCurve = ((LocationCurve)filteredElements[0].Location).Curve;
            XYZ dirVec = dirCurve.GetEndPoint(1) - dirCurve.GetEndPoint(0);
            if (dirVec.GetLength() < 1e-9)
            {
                InfoCenterTip.Show("首个元素长度为零，无法确定方向。");
                MEPLogger.Instance.EndSession("Failed: Zero length direction vector.");
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

                double anchorProj = groupInfos.Select(g => g.curProj[0]).Min();

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

            // ==================== 事务组：预览 + 反向确认 ====================
            using (TransactionGroup tg = new TransactionGroup(doc,
                $"MEP分组排序对齐 {spacingMM:0.##}mm"))
            {
                tg.Start();
                bool reversed = false;
                bool accepted = false;

                while (!accepted)
                {
                    using (Transaction trans = new Transaction(doc, "MEP分组排序对齐"))
                    {
                        trans.Start();
                        RestorePositions();
                        ApplyArrangement(reversed ? perpVecBase.Negate() : perpVecBase);
                        trans.Commit();
                    }

                    // 使用 WinForms 自定义确认界面
                    using (WinForms.Form form = new WinForms.Form())
                    {
                        form.Text = "MEP分组排序对齐 - 结果确认";
                        form.ClientSize = new Size(340, 160);
                        form.StartPosition = WinForms.FormStartPosition.CenterScreen;
                        form.FormBorderStyle = WinForms.FormBorderStyle.FixedDialog;
                        form.MaximizeBox = false;
                        form.MinimizeBox = false;
                        form.ShowInTaskbar = false;
                        form.KeyPreview = true; 

                        WinForms.Label lbl = new WinForms.Label
                        {
                            Text = $"已按净间距 {spacingMM:0.##} mm 排列（共 {groups.Count} 组）。\n\n" +
                                   "· 按空格键 / 回车键 → 反向排列\n" +
                                   "· 按ESC键 / 点击“完成” → 接受当前结果",
                            Location = new Point(15, 15),
                            AutoSize = true
                        };

                        WinForms.Button reverseBtn = new WinForms.Button
                        {
                            Text = "反向排列",
                            Location = new Point(135, 105),
                            Width = 80,
                            DialogResult = WinForms.DialogResult.Retry
                        };

                        WinForms.Button okBtn = new WinForms.Button
                        {
                            Text = "完成",
                            Location = new Point(225, 105),
                            Width = 80,
                            DialogResult = WinForms.DialogResult.OK
                        };

                        form.Controls.Add(lbl);
                        form.Controls.Add(reverseBtn);
                        form.Controls.Add(okBtn);

                        // ===== 核心逻辑修改 =====
                        // 1. 默认回车键 -> 触发“反向排列”
                        form.AcceptButton = reverseBtn; 
                        // 2. 默认 ESC 键 -> 触发“完成” (因为 OkBtn.DialogResult 为 OK)
                        form.CancelButton = okBtn; 
                        // 3. 默认焦点在“反向排列”上（如果用户直接按空格触发）
                        form.ActiveControl = reverseBtn; 

                        // 4. 捕获空格键，强制绑定给“反向排列”
                        form.KeyDown += (s, e) =>
                        {
                            if (e.KeyCode == WinForms.Keys.Space)
                            {
                                form.DialogResult = WinForms.DialogResult.Retry;
                                form.Close();
                            }
                        };

                        WinForms.DialogResult res = form.ShowDialog();

                        if (res == WinForms.DialogResult.Retry)
                        {
                            reversed = !reversed;
                            MEPLogger.Instance.AppendLine("用户选择了反向排列。");
                        }
                        else
                        {
                            accepted = true;
                            MEPLogger.Instance.AppendLine("用户接受了当前排列结果。");
                        }
                    }
                }

                tg.Assimilate();
            }

            InfoCenterTip.Show("MEP分组排序对齐完成");
            MEPLogger.Instance.EndSession("Success: Alignment completed.");
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
                form.ClientSize = new Size(320, 140);
                form.StartPosition = WinForms.FormStartPosition.CenterScreen;
                form.FormBorderStyle = WinForms.FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.ShowInTaskbar = false;
                form.KeyPreview = true;

                WinForms.Label lbl = new WinForms.Label
                {
                    Text = prompt,
                    Location = new Point(15, 15),
                    AutoSize = true
                };

                WinForms.TextBox txt = new WinForms.TextBox
                {
                    Text = defaultValue.ToString("0.##"),
                    Location = new Point(18, 50),
                    Width = 280
                };
                txt.SelectAll();

                WinForms.Button okBtn = new WinForms.Button
                {
                    Text = "完成",
                    Location = new Point(130, 90),
                    Width = 75,
                    DialogResult = WinForms.DialogResult.OK
                };

                WinForms.Button cancelBtn = new WinForms.Button
                {
                    Text = "取消",
                    Location = new Point(215, 90),
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

                form.KeyDown += (s, e) =>
                {
                    if (e.KeyCode == WinForms.Keys.Space)
                    {
                        form.DialogResult = WinForms.DialogResult.OK;
                        form.Close();
                    }
                };

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