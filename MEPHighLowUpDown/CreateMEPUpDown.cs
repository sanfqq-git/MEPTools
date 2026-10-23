using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace MEPTools.UpDown
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CreateMEPUpDown : IExternalCommand
    {
        private readonly StringBuilder debugInfo = new StringBuilder();

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // ---------- 1. 获取预选元素 ----------
                ICollection<ElementId> elemIds = uidoc.Selection.GetElementIds();
                if (elemIds == null || elemIds.Count < 1)
                {
                    message = "请至少选中 1 个管道/风管/线槽元素！";
                    return Result.Failed;
                }

                List<ElementId> elemIdList = new List<ElementId>();
                foreach (ElementId id in elemIds)
                {
                    Element elem = doc.GetElement(id);
                    MEPElementType t = MEPUpDownCalculator.GetElementType(elem);

                    if (t == MEPElementType.Pipe
                        || t == MEPElementType.Duct
                        || t == MEPElementType.CableTray)
                        elemIdList.Add(id);
                }

                if (elemIdList.Count < 1)
                {
                    message = "选中的元素中未包含有效的管道、风管或线槽！";
                    return Result.Failed;
                }

                // ---------- 2. 输入偏移值 ----------
                double h_offset_mm = ShowInputDialog("45° 抬升/下降（Up/Down）",
                    "正数=抬升(Up)，负数=下降(Down)\n单位：mm\n示例：200 / -150", "200");

                if (double.IsNaN(h_offset_mm))
                {
                    message = "用户取消输入偏移量，操作终止";
                    return Result.Cancelled;
                }
                if (Math.Abs(h_offset_mm) < 1)
                {
                    message = "偏移量绝对值不能小于 1mm";
                    return Result.Failed;
                }

                double h_offset_ft = UnitUtils.ConvertToInternalUnits(
                    h_offset_mm, UnitTypeId.Millimeters);
                debugInfo.AppendLine($"偏移量: {h_offset_mm:F2} mm = {h_offset_ft:F6} ft");
                debugInfo.AppendLine($"过渡段水平投影长度 = |{h_offset_mm:F2}| mm （45° 自动计算）");

                // ---------- 3. 拾取打断点 ----------
                Reference ref1 = uidoc.Selection.PickObject(ObjectType.PointOnElement,
                    new MEPCurveSelectionFilter(),
                    "【第 1/2 步】选择打断点（原高度侧的过渡起点，避开弯头/三通）");

                // ---------- 4. 拾取升降侧 ----------
                Reference ref2 = uidoc.Selection.PickObject(ObjectType.PointOnElement,
                    new MEPCurveSelectionFilter(),
                    "【第 2/2 步】点击需要抬升/下降的一侧（沿管线方向点击）");

                if (ref1.ElementId != ref2.ElementId)
                {
                    message = "两次拾取必须在同一元素上！\n请重新运行命令。";
                    return Result.Failed;
                }

                XYZ breakPt = ref1.GlobalPoint;
                XYZ sidePt = ref2.GlobalPoint;

                debugInfo.AppendLine($"打断点: ({breakPt.X:F3},{breakPt.Y:F3},{breakPt.Z:F3})");
                debugInfo.AppendLine($"升降侧参考点: ({sidePt.X:F3},{sidePt.Y:F3},{sidePt.Z:F3})");

                // ---------- 5. 单事务处理所有元素 ----------
                MEPUpDownService service = new MEPUpDownService(debugInfo);
                int successCount = 0, failCount = 0;

                using (Transaction trans = new Transaction(doc, "45° Up/Down"))
                {
                    trans.Start();

                    foreach (ElementId eid in elemIdList)
                    {
                        debugInfo.AppendLine($"\n--- 处理元素 (ID: {eid.IntegerValue}) ---");
                        try
                        {
                            bool ok = service.ProcessSingleElement(
                                doc, eid, breakPt, sidePt, h_offset_ft);
                            if (ok) successCount++; else failCount++;
                        }
                        catch (Exception ex)
                        {
                            debugInfo.AppendLine($"✗ 元素 {eid.IntegerValue} 处理失败：{ex.Message}");
                            debugInfo.AppendLine("⚠ 单事务模式，已回滚所有更改");
                            trans.RollBack();
                            ShowDebugInfo("处理中止",
                                $"元素 {eid.IntegerValue} 处理失败，所有更改已回滚");
                            return Result.Failed;
                        }
                    }

                    trans.Commit();
                }

                debugInfo.AppendLine($"\n处理完成：成功 {successCount}，失败 {failCount}");
                ShowDebugInfo("45° 抬升/下降 - 批量处理完成",
                    $"成功: {successCount}, 失败: {failCount}");
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                debugInfo.AppendLine("用户取消了操作");
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                debugInfo.AppendLine($"异常：{ex.Message}");
                debugInfo.AppendLine($"堆栈：{ex.StackTrace}");
                ShowDebugInfo("操作失败", null);
                return Result.Failed;
            }
        }

        // =====================================================================
        // WPF 输入弹窗
        // =====================================================================
        private double ShowInputDialog(string title, string tipText, string defaultValue)
        {
            Window window = new Window
            {
                Title = title,
                Width = 420,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true
            };

            System.Windows.Controls.StackPanel panel =
                new System.Windows.Controls.StackPanel { Margin = new Thickness(15) };

            System.Windows.Controls.TextBlock txtTip = new System.Windows.Controls.TextBlock
            {
                Text = tipText + "\n回车确认 | ESC取消",
                Margin = new Thickness(0, 0, 0, 12),
                FontSize = 12
            };

            System.Windows.Controls.TextBox txtInput = new System.Windows.Controls.TextBox
            {
                Text = defaultValue,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 15)
            };

            System.Windows.Controls.StackPanel btnPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };

            System.Windows.Controls.Button btnOk = new System.Windows.Controls.Button
            { Content = "确定", Width = 100, Margin = new Thickness(0, 0, 10, 0) };

            System.Windows.Controls.Button btnCancel = new System.Windows.Controls.Button
            { Content = "取消", Width = 100 };

            btnOk.Click += (s, e) => window.DialogResult = true;
            btnCancel.Click += (s, e) => window.DialogResult = false;

            txtInput.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter)
                {
                    window.DialogResult = true;
                    e.Handled = true;
                }
            };

            window.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape)
                {
                    window.DialogResult = false;
                    e.Handled = true;
                }
            };

            window.Loaded += (s, e) =>
            {
                txtInput.Focus();
                txtInput.SelectAll();
            };

            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnCancel);
            panel.Children.Add(txtTip);
            panel.Children.Add(txtInput);
            panel.Children.Add(btnPanel);
            window.Content = panel;

            bool? dialogResult = window.ShowDialog();
            if (dialogResult != true) return double.NaN;

            string input = txtInput.Text.Trim();
            if (double.TryParse(input, out double val)) return val;

            MessageBox.Show($"输入「{input}」不是有效数字，请重新运行命令！",
                "输入错误", MessageBoxButton.OK, MessageBoxImage.Error);
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

        // =====================================================================
        // 选择过滤器：Pipe / Duct / CableTray
        // =====================================================================
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