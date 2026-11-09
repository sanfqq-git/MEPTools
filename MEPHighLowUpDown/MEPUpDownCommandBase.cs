using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using MEPTools.Shared;
using Forms = System.Windows.Forms;

using TextBox = System.Windows.Controls.TextBox;

namespace MEPTools.MEPHighLowUpDown
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public abstract class MEPUpDownCommandBase : IExternalCommand
    {
        private readonly StringBuilder debugInfo = new StringBuilder();

        /// <summary>命令标题（用于日志、事务名、对话框标题）</summary>
        protected abstract string Title { get; }

        /// <summary>获取本次执行的角度（度）。返回 false 表示用户取消。</summary>
        protected abstract bool TryGetAngleDeg(out double angleDeg);

        /// <summary>偏移量输入框的默认值</summary>
        protected virtual string DefaultOffsetInput => "200";

        protected void Log(string text)
        {
            debugInfo.AppendLine(text);
            MEPLogger.Instance.AppendLine(text);
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            MEPLogger.Instance.StartSession(Title);
            int successCount = 0, failCount = 0;

            try
            {
                // ── 第 0 步：确定角度 ──
                if (!TryGetAngleDeg(out double angleDeg))
                {
                    MEPLogger.Instance.EndSession("用户取消角度输入");
                    return Result.Cancelled;
                }
                Log($"连接角度 = {angleDeg:F2}°");

                // ── 第 1 步：获取有效元素 ──
                List<ElementId> elemIdList = GetValidMEPElementIds(doc, uidoc);

                if (elemIdList.Count < 1)
                {
                    InfoCenterTip.Show("请先选择至少 1 个管道/风管/线槽元素");
                    Log("无预选元素，启动交互式选择");

                    try
                    {
                        using (SpaceKeyFinisher finisher = new SpaceKeyFinisher(
                            Forms.Keys.Space, "完成", "Finish", "Complete"))
                        {
                            try { finisher.Enable(); }
                            catch (Exception ex) { Log($"[SpaceKeyFinisher] 启用失败（不影响选择）：{ex.Message}"); }

                            IList<Reference> refs = uidoc.Selection.PickObjects(
                                ObjectType.Element,
                                new MEPCurveSelectionFilter(),
                                "选择 MEP 管线元素（ESC 结束 / 空格键完成选择）");

                            foreach (Reference r in refs)
                            {
                                Element e = doc.GetElement(r.ElementId);
                                if (IsValidMEPElement(e)) elemIdList.Add(r.ElementId);
                            }
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        InfoCenterTip.Show("已取消元素选择");
                        MEPLogger.Instance.EndSession("用户取消选择");
                        return Result.Cancelled;
                    }
                }
                else
                {
                    InfoCenterTip.Show($"使用已选中 {elemIdList.Count} 个 MEP 元素");
                    Log($"使用预选 {elemIdList.Count} 个元素");
                }

                if (elemIdList.Count < 1)
                {
                    InfoCenterTip.Show("✗ 选中的元素中未包含有效的管道/风管/线槽");
                    MEPLogger.Instance.EndSession("无有效元素");
                    return Result.Failed;
                }

                // ── 第 2 步：高度偏移 ──
                double h_offset_mm = ShowInputDialog(
                    $"{Title}（偏移量）",
                    "正数=抬升(Up)，负数=下降(Down)\n单位：mm\n示例：200 / -150",
                    DefaultOffsetInput);

                if (double.IsNaN(h_offset_mm)) return Result.Cancelled;
                if (Math.Abs(h_offset_mm) < 1)
                {
                    InfoCenterTip.Show("✗ 偏移量绝对值不能小于 1mm");
                    return Result.Failed;
                }

                double h_offset_ft = UnitUtils.ConvertToInternalUnits(h_offset_mm, UnitTypeId.Millimeters);

                // ── 第 3 步：拾取打断点与升降侧（两点均在管上）──
                InfoCenterTip.Show("第 1/2 步：请选择打断点（原高度侧）");
                Reference ref1 = uidoc.Selection.PickObject(ObjectType.PointOnElement,
                    new MEPCurveSelectionFilter(),
                    "【第 1/2 步】选择打断点（原高度侧的过渡起点，避开弯头/三通）");

                XYZ breakPt = ref1.GlobalPoint;

                InfoCenterTip.Show("第 2/2 步：请在需要抬升/下降一侧的管上点击任意位置");
                Reference ref2 = uidoc.Selection.PickObject(ObjectType.PointOnElement,
                    new MEPCurveSelectionFilter(),
                    "【第 2/2 步】在需要抬升/下降一侧的管上点击任意位置（可点任意一根已选管）");

                XYZ sidePt = ref2.GlobalPoint;

                Log($"打断点: {MEPUpDownCalculator.Fmt(breakPt)}");
                Log($"升降侧参考点: {MEPUpDownCalculator.Fmt(sidePt)}");

                // ── 第 4 步：批量处理 ──
                MEPUpDownService service = new MEPUpDownService(debugInfo);

                using (Transaction trans = new Transaction(doc, Title))
                {
                    trans.Start();

                    foreach (ElementId eid in elemIdList)
                    {
                        Log($"\n--- 处理元素 (ID: {eid.IntegerValue}) ---");
                        try
                        {
                            bool ok = service.ProcessSingleElement(
                                doc, eid, breakPt, sidePt, h_offset_ft, angleDeg);
                            if (ok) successCount++; else failCount++;
                        }
                        catch (Exception ex)
                        {
                            Log($"✗ 元素 {eid.IntegerValue} 处理失败：{ex.Message}");
                            trans.RollBack();
                            InfoCenterTip.Show($"✗ 元素 {eid.IntegerValue} 处理失败，已回滚");
                            return Result.Failed;
                        }
                    }

                    trans.Commit();
                }

                if (failCount == 0)
                    InfoCenterTip.Show($"✓ 处理完成：成功 {successCount} 个元素");
                else
                {
                    InfoCenterTip.Show($"⚠ 处理完成：成功 {successCount}，失败 {failCount}");
                    ShowDebugInfo($"{Title} - 批量处理完成", $"成功 {successCount}，失败 {failCount}");
                }

                MEPLogger.Instance.EndSession($"成功 {successCount}，失败 {failCount}");
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                InfoCenterTip.Show("已取消操作");
                MEPLogger.Instance.EndSession("用户取消");
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                Log($"异常：{ex.Message}");
                InfoCenterTip.Show($"✗ 操作失败：{ex.Message}");
                ShowDebugInfo("操作失败", null);
                MEPLogger.Instance.EndSession("异常终止");
                return Result.Failed;
            }
        }

        private List<ElementId> GetValidMEPElementIds(Document doc, UIDocument uidoc)
        {
            List<ElementId> result = new List<ElementId>();
            ICollection<ElementId> preSelected = uidoc.Selection.GetElementIds();
            if (preSelected == null) return result;
            foreach (ElementId id in preSelected)
            {
                Element elem = doc.GetElement(id);
                if (IsValidMEPElement(elem)) result.Add(id);
            }
            return result;
        }

        private bool IsValidMEPElement(Element elem)
        {
            if (elem == null) return false;
            MEPElementType t = MEPUpDownCalculator.GetElementType(elem);
            return t == MEPElementType.Pipe || t == MEPElementType.Duct || t == MEPElementType.CableTray;
        }

        /// <summary>供子类复用的输入对话框（回车/空格确认，ESC 取消）</summary>
        protected double ShowInputDialog(string title, string tipText, string defaultValue)
        {
            InfoCenterTip.Show($"{title}：{tipText.Replace("\n", " | ")}");

            Window window = new Window
            {
                Title = title,
                Width = 420,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true
            };

            StackPanel panel = new StackPanel { Margin = new Thickness(15) };
            TextBlock txtTip = new TextBlock
            {
                Text = tipText + "\n回车 / 空格 确认   |   ESC 取消",
                Margin = new Thickness(0, 0, 0, 12),
                FontSize = 12
            };

            TextBox txtInput = new TextBox
            {
                Text = defaultValue,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 15)
            };

            StackPanel btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            Button btnOk = new Button { Content = "确定", Width = 100, Margin = new Thickness(0, 0, 10, 0) };
            Button btnCancel = new Button { Content = "取消", Width = 100 };

            btnOk.Click += (s, e) => window.DialogResult = true;
            btnCancel.Click += (s, e) => window.DialogResult = false;

            txtInput.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter || e.Key == Key.Space)
                {
                    window.DialogResult = true;
                    e.Handled = true;
                }
            };

            window.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { window.DialogResult = false; e.Handled = true; }
            };

            window.Loaded += (s, e) => { txtInput.Focus(); txtInput.SelectAll(); };

            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnCancel);
            panel.Children.Add(txtTip);
            panel.Children.Add(txtInput);
            panel.Children.Add(btnPanel);
            window.Content = panel;

            if (window.ShowDialog() != true) return double.NaN;

            string input = txtInput.Text.Trim();
            if (double.TryParse(input, out double val)) return val;

            InfoCenterTip.Show($"✗ 输入「{input}」不是有效数字");
            return double.NaN;
        }

        private void ShowDebugInfo(string title, string mainInstruction)
        {
            TaskDialog td = new TaskDialog(title);
            td.MainInstruction = mainInstruction ?? string.Empty;
            td.MainContent = debugInfo.ToString();
            td.CommonButtons = TaskDialogCommonButtons.Ok;
            td.Show();
        }

        private class MEPCurveSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                return elem is Pipe || elem is Duct || elem is CableTray;
            }
            public bool AllowReference(Reference reference, XYZ position) => true;
        }
    }
}