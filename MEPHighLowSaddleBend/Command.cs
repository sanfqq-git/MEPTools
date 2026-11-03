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
using MEPTools.Shared;   // InfoCenterTip / MEPLogger / SpaceKeyFinisher

// 解决 Connector 命名冲突
using Connector = Autodesk.Revit.DB.Connector;

namespace MEPTools.MEPHighLowSaddleBend
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Command : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            // ---- 启动日志会话 ----
            MEPLogger.Instance.StartSession("45度翻弯");
            StringBuilder debugInfo = new StringBuilder();

            try
            {
                // ============================================================
                // 1. 优先使用预选元素
                // ============================================================
                List<ElementId> elemIdList = GetPreselectedMEPElements(doc, uidoc);

                // ============================================================
                // 2. 无预选 → InfoCenter 提示 + PickObjects 交互选择（空格确认）
                // ============================================================
                if (elemIdList.Count < 1)
                {
                    InfoCenterTip.Show("请选择要翻弯的管道/风管/线槽，按【空格】确认完成选择");
                    MEPLogger.Instance.AppendLine("未预选有效元素，进入交互选择模式。");

                    elemIdList = PickMEPElements(uidoc);

                    if (elemIdList.Count < 1)
                    {
                        InfoCenterTip.Show("未选择有效的管道、风管或线槽，操作终止");
                        MEPLogger.Instance.EndSession("未选择有效元素");
                        return Result.Failed;
                    }
                }

                MEPLogger.Instance.AppendLine($"目标元素数量: {elemIdList.Count}");

                // ============================================================
                // 3. 输入偏移值（WPF 输入框，需要数值输入）
                // ============================================================
                double h_offset_mm = ShowInputDialog("输入翻弯偏移高度",
                    "正数=向上翻弯，负数=向下翻弯\n单位：mm\n示例：200 / -150", "200");

                if (double.IsNaN(h_offset_mm))
                {
                    InfoCenterTip.Show("已取消输入偏移量");
                    MEPLogger.Instance.EndSession("用户取消输入");
                    return Result.Cancelled;
                }
                if (Math.Abs(h_offset_mm) < 1)
                {
                    InfoCenterTip.Show("偏移量绝对值不能小于 1mm");
                    MEPLogger.Instance.EndSession("偏移量过小");
                    return Result.Failed;
                }

                double h_offset_ft = UnitUtils.ConvertToInternalUnits(
                    h_offset_mm, UnitTypeId.Millimeters);
                MEPLogger.Instance.AppendLine($"偏移量: {h_offset_mm:F2} mm = {h_offset_ft:F6} ft");

                // ============================================================
                // 4. 拾取两个打断点
                // ============================================================
                Reference ref1, ref2;
                try
                {
                    ref1 = uidoc.Selection.PickObject(ObjectType.PointOnElement,
                        new MEPCurveSelectionFilter(),
                        "选择第一个打断点（仅限单个直管段，请避开弯头/三通）");
                    ref2 = uidoc.Selection.PickObject(ObjectType.PointOnElement,
                        new MEPCurveSelectionFilter(),
                        "选择第二个打断点（同一段直管，请避开弯头/三通）");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    InfoCenterTip.Show("已取消打断点拾取");
                    MEPLogger.Instance.EndSession("用户取消拾取");
                    return Result.Cancelled;
                }

                XYZ globalP1 = ref1.GlobalPoint;
                XYZ globalP2 = ref2.GlobalPoint;

                // ============================================================
                // 5. 单事务处理
                // ============================================================
                MEPOffsetBendService service = new MEPOffsetBendService(debugInfo);
                int successCount = 0, failCount = 0;

                using (Transaction trans = new Transaction(doc, "45度翻弯"))
                {
                    trans.Start();

                    foreach (ElementId eid in elemIdList)
                    {
                        debugInfo.AppendLine($"\n--- 处理元素 (ID: {eid.IntegerValue}) ---");
                        try
                        {
                            bool ok = service.ProcessSingleElement(
                                doc, eid, globalP1, globalP2, h_offset_ft);
                            if (ok) successCount++; else failCount++;
                        }
                        catch (Exception ex)
                        {
                            debugInfo.AppendLine(
                                $"✗ 元素 {eid.IntegerValue} 处理失败：{ex.Message}");
                            MEPLogger.Instance.AppendLine(debugInfo.ToString());
                            MEPLogger.Instance.LogException($"元素 {eid.IntegerValue}", ex);

                            trans.RollBack();
                            InfoCenterTip.Show("处理失败，所有更改已回滚");
                            MEPLogger.Instance.EndSession("处理失败回滚");
                            return Result.Failed;
                        }
                    }

                    trans.Commit();
                }

                // service 详细日志合并到 MEPLogger
                MEPLogger.Instance.AppendLine(debugInfo.ToString());

                string summary = $"成功 {successCount}，失败 {failCount}";
                MEPLogger.Instance.EndSession(summary);
                InfoCenterTip.Show($"翻弯完成：{summary}");
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                MEPLogger.Instance.EndSession("用户取消");
                InfoCenterTip.Show("操作已取消");
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                MEPLogger.Instance.LogException("Command.Execute", ex);
                MEPLogger.Instance.EndSession("异常终止");
                InfoCenterTip.Show("操作失败：" + ex.Message);
                return Result.Failed;
            }
        }

        // =====================================================================
        // 获取预选元素（Pipe / Duct / CableTray）
        // =====================================================================
        private List<ElementId> GetPreselectedMEPElements(Document doc, UIDocument uidoc)
        {
            List<ElementId> result = new List<ElementId>();
            ICollection<ElementId> elemIds = uidoc.Selection.GetElementIds();
            if (elemIds == null || elemIds.Count == 0) return result;

            foreach (ElementId id in elemIds)
            {
                Element elem = doc.GetElement(id);
                MEPElementType t = MEPOffsetBendCalculator.GetElementType(elem);
                if (t == MEPElementType.Pipe
                    || t == MEPElementType.Duct
                    || t == MEPElementType.CableTray)
                    result.Add(id);
            }
            return result;
        }

        // =====================================================================
        // 交互式选择（PickObjects + SpaceKeyFinisher 空格确认）
        // =====================================================================
        private List<ElementId> PickMEPElements(UIDocument uidoc)
        {
            List<ElementId> result = new List<ElementId>();

            // SpaceKeyFinisher 默认热键=Space，默认按钮文本="完成"/"Finish"/"Complete"
            using (SpaceKeyFinisher finisher = new SpaceKeyFinisher())
            {
                finisher.Enable();
                try
                {
                    IList<Reference> refs = uidoc.Selection.PickObjects(
                        ObjectType.Element,
                        new MEPCurveSelectionFilter(),
                        "请选择要翻弯的管道/风管/线槽，按【空格】确认完成选择");

                    if (refs != null)
                    {
                        foreach (Reference r in refs)
                        {
                            if (r == null || r.ElementId == ElementId.InvalidElementId) continue;
                            if (!result.Contains(r.ElementId)) result.Add(r.ElementId);
                        }
                    }
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    // 用户 ESC 或空选 → result 保持空，交由上层给出提示
                }
                finally
                {
                    finisher.Disable();
                }
            }
            return result;
        }

        // =====================================================================
        // WPF 数值输入弹窗（InfoCenter 不支持输入，仅保留此 UI）
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

            System.Windows.Controls.StackPanel btnPanel =
                new System.Windows.Controls.StackPanel
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

            // 输入错误也改走 InfoCenter，不打断流程
            InfoCenterTip.Show($"输入「{input}」不是有效数字，请重新运行命令");
            return double.NaN;
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