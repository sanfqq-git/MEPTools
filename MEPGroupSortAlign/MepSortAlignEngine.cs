using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System.Drawing;
using Point = System.Drawing.Point;
using WinForms = System.Windows.Forms;
using MEPTools.Shared;

namespace MEPTools.MEPGroupSortAlign
{
    public enum AnchorMode
    {
        AutoFirstExtreme,   // 自动对齐：globalAnchor = 所有组极值
        UserPicked          // 指定基准：globalAnchor = 被点选元素的边缘
    }

    public sealed class MepSortAlignEngine
    {
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

        public Result Run(
            UIDocument uiDoc,
            List<Element> filteredElements,
            double spacingInternal,
            AnchorMode anchorMode,
            ElementId pickedAnchorId,
            string commandName)
        {
            if (filteredElements == null || filteredElements.Count < 2)
                return Result.Cancelled;

            Document doc = uiDoc.Document;
            double spacingMM = UnitUtils.ConvertFromInternalUnits(spacingInternal, UnitTypeId.Millimeters);

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

            // 定位锚点所在组的索引
            int anchorGroupIdx = -1;
            if (anchorMode == AnchorMode.UserPicked)
            {
                if (pickedAnchorId == null)
                {
                    MEPLogger.Instance.EndSession("Failed: Anchor id is null.");
                    return Result.Cancelled;
                }
                for (int gi = 0; gi < groups.Count; gi++)
                {
                    if (groups[gi].Elements.Any(e => e.Id == pickedAnchorId))
                    {
                        anchorGroupIdx = gi;
                        break;
                    }
                }
                if (anchorGroupIdx < 0)
                {
                    InfoCenterTip.Show("锚点元素不在已选择的元素集合内。");
                    MEPLogger.Instance.EndSession("Failed: Anchor not found in selection.");
                    return Result.Cancelled;
                }
            }

            // ---------- 5. 记录原始位置 ----------
            Dictionary<ElementId, XYZ> originalPositions = new Dictionary<ElementId, XYZ>();
            foreach (Element elem in filteredElements)
            {
                Curve c = ((LocationCurve)elem.Location).Curve;
                originalPositions[elem.Id] = (c.GetEndPoint(0) + c.GetEndPoint(1)) / 2.0;
            }

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
                    Curve c = ((LocationCurve)elem.Location).Curve;
                    XYZ curMid = (c.GetEndPoint(0) + c.GetEndPoint(1)) / 2.0;
                    XYZ delta = originalPositions[elem.Id] - curMid;
                    if (delta.GetLength() > 1e-9)
                        ElementTransformUtils.MoveElement(doc, elem.Id, delta);
                }
            }

            void ApplyArrangement(XYZ perpVec, bool isLeftAlign)
            {
                var groupInfos = new List<(List<Element> sorted, List<double> curProj, List<double> targets, bool isAnchor)>();

                for (int gi = 0; gi < groups.Count; gi++)
                {
                    var group = groups[gi];
                    if (group.Elements.Count == 0) continue;

                    var sorted = group.Elements
                        .OrderBy(e => originalPositions[e.Id].DotProduct(perpVec))
                        .ToList();

                    var curProj = sorted
                        .Select(e =>
                        {
                            Curve c = ((LocationCurve)e.Location).Curve;
                            XYZ mid = (c.GetEndPoint(0) + c.GetEndPoint(1)) / 2.0;
                            return mid.DotProduct(perpVec);
                        })
                        .ToList();

                    bool isAnchor = (anchorMode == AnchorMode.UserPicked) && (gi == anchorGroupIdx);

                    List<double> targets;
                    if (isAnchor)
                    {
                        // ★ 修复点1：锚点组保持原样绝对不动，不重排，完全避免由于拉扯产生的“线太短”错误
                        targets = new List<double>(curProj);
                    }
                    else
                    {
                        // 其他组：按设定的间距正常重排（从左向右推）
                        targets = new List<double> { curProj[0] };
                        for (int i = 1; i < sorted.Count; i++)
                        {
                            double prevR = GetRadius(sorted[i - 1]);
                            double curR = GetRadius(sorted[i]);
                            targets.Add(targets[i - 1] + prevR + spacingInternal + curR);
                        }
                    }

                    groupInfos.Add((sorted, curProj, targets, isAnchor));
                }

                if (groupInfos.Count == 0) return;

                double globalAnchor = 0;
                if (anchorMode == AnchorMode.UserPicked)
                {
                    var anchorInfo = groupInfos.First(x => x.isAnchor);
                    // ★ 修复点2：找到你点选的那一根具体的管子，而不是整组
                    int anchorIdx = anchorInfo.sorted.FindIndex(e => e.Id == pickedAnchorId);
                    if (anchorIdx < 0) anchorIdx = 0;

                    // ★ 修复点3：以被点选元素的边缘作为全局基准线
                    if (isLeftAlign)
                        globalAnchor = anchorInfo.targets[anchorIdx] - GetRadius(anchorInfo.sorted[anchorIdx]);
                    else
                        globalAnchor = anchorInfo.targets[anchorIdx] + GetRadius(anchorInfo.sorted[anchorIdx]);
                }
                else
                {
                    // 原版极值逻辑保留
                    bool firstGroup = true;
                    foreach (var info in groupInfos)
                    {
                        if (isLeftAlign)
                        {
                            double leftEdge = info.targets[0] - GetRadius(info.sorted[0]);
                            if (firstGroup || leftEdge < globalAnchor)
                            {
                                globalAnchor = leftEdge;
                                firstGroup = false;
                            }
                        }
                        else
                        {
                            double rightEdge = info.targets.Last() + GetRadius(info.sorted.Last());
                            if (firstGroup || rightEdge > globalAnchor)
                            {
                                globalAnchor = rightEdge;
                                firstGroup = false;
                            }
                        }
                    }
                }

                // 逐组应用平移
                foreach (var info in groupInfos)
                {
                    double offset;
                    if (info.isAnchor)
                    {
                        offset = 0;  // 锚点组自身不动（保留它的原始位置）
                    }
                    else if (isLeftAlign)
                    {
                        // 其他组：左边缘对齐到基准线
                        offset = globalAnchor - info.targets[0] + GetRadius(info.sorted[0]);
                    }
                    else
                    {
                        // 其他组：右边缘对齐到基准线
                        offset = globalAnchor - info.targets.Last() - GetRadius(info.sorted.Last());
                    }

                    for (int i = 0; i < info.sorted.Count; i++)
                    {
                        double targetProj = info.targets[i] + offset;
                        double move = targetProj - info.curProj[i];
                        if (Math.Abs(move) > 1e-9)
                        {
                            ElementTransformUtils.MoveElement(doc, info.sorted[i].Id, perpVec.Multiply(move));
                        }
                    }
                }
            }

            // ==================== 事务组：预览 + 反向确认 ====================
            using (TransactionGroup tg = new TransactionGroup(doc, $"{commandName} {spacingMM:0.##}mm"))
            {
                tg.Start();
                bool isLeftAlign = true;
                bool accepted = false;

                while (!accepted)
                {
                    using (Transaction trans = new Transaction(doc, commandName))
                    {
                        trans.Start();
                        FailureHandlingOptions failOpt = trans.GetFailureHandlingOptions();
                        failOpt.SetFailuresPreprocessor(new MepWarningSwallower());
                        trans.SetFailureHandlingOptions(failOpt);

                        RestorePositions();
                        ApplyArrangement(perpVecBase, isLeftAlign);
                        trans.Commit();
                    }

                    using (WinForms.Form form = new WinForms.Form())
                    {
                        form.Text = $"{commandName} - 结果确认";
                        form.ClientSize = new Size(340, 160);
                        form.StartPosition = WinForms.FormStartPosition.CenterScreen;
                        form.FormBorderStyle = WinForms.FormBorderStyle.FixedDialog;
                        form.MaximizeBox = false;
                        form.MinimizeBox = false;
                        form.ShowInTaskbar = false;
                        form.KeyPreview = true;

                        string tipText = anchorMode == AnchorMode.UserPicked 
                            ? "· 基准组保持不动，空格切换其他组贴左/贴右\n" 
                            : "· 按空格键 / 回车键 → 切换左/右对齐\n";

                        WinForms.Label lbl = new WinForms.Label
                        {
                            Text = $"已按净间距 {spacingMM:0.##} mm 排列（共 {groups.Count} 组）。\n\n" +
                                   tipText +
                                   "· 按ESC键 / 点击“完成” → 接受当前结果",
                            Location = new Point(15, 15),
                            AutoSize = true
                        };

                        WinForms.Button reverseBtn = new WinForms.Button
                        {
                            Text = "反向对齐",
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

                        form.AcceptButton = reverseBtn;
                        form.CancelButton = okBtn;
                        form.ActiveControl = reverseBtn;

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
                            isLeftAlign = !isLeftAlign;
                            MEPLogger.Instance.AppendLine($"用户切换为：{(isLeftAlign ? "左对齐" : "右对齐")}。");
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

            InfoCenterTip.Show($"{commandName}完成");
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
            double toleranceInternal = UnitUtils.ConvertToInternalUnits(toleranceMM, UnitTypeId.Millimeters);

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
        public static double PromptForValue(string title, string prompt, double defaultValue)
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

    public class MepWarningSwallower : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            IList<FailureMessageAccessor> failures = failuresAccessor.GetFailureMessages();
            foreach (FailureMessageAccessor f in failures)
            {
                if (f.GetSeverity() == FailureSeverity.Warning)
                {
                    failuresAccessor.DeleteWarning(f);
                }
            }
            return FailureProcessingResult.Continue;
        }
    }
}