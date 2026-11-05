using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using MEPTools.Shared;
using WinForms = System.Windows.Forms;

using Connector = Autodesk.Revit.DB.Connector;

namespace MEPTools.MEPHighLowConnector
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Command : IExternalCommand
    {
        private const double DefaultAngleDeg = 45.0;

        public Result Execute(ExternalCommandData commandData,
            ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            MEPLogger.Instance.StartSession("CreateMEPConnector 连接两组 MEP");
            var log = MEPLogger.Instance;

            try
            {
                // ==========================================
                // 【修改点 1】：提示文案支持 90 度
                // ==========================================
                double angleDeg = DefaultAngleDeg;

                if ((WinForms.Control.ModifierKeys & WinForms.Keys.Shift) == WinForms.Keys.Shift)
                {
                    string input = ShowInputBox("按住 Shift 唤起了自定义角度。\n请输入连接角度（1 ~ 90 度）：\n\n注：输入 90 度将直接垂直连接（无斜段）。", "MEP 斜段连接角度", DefaultAngleDeg.ToString());

                    if (input == null) return Result.Cancelled; // 用户点了取消

                    // ==========================================
                    // 【修改点 2】：允许等于 90 度
                    // ==========================================
                    if (!double.TryParse(input, out angleDeg) || angleDeg < 1.0 || angleDeg > 90.0)
                    {
                        message = "输入的角度无效，请输入 1 到 90 之间的数字。";
                        InfoCenterTip.Show("角度输入无效，已取消");
                        return Result.Failed;
                    }
                    log.AppendLine($"用户自定义连接角度：{angleDeg}°");
                }
                else
                {
                    log.AppendLine($"使用默认连接角度：{angleDeg}°");
                }

                var aList = GetPreselectedCurves(uiDoc, doc);
                if (aList.Count > 0)
                {
                    InfoCenterTip.Show($"已使用预选的 {aList.Count} 个 A 组元素");
                    log.AppendLine($"A 组来自预选，数量：{aList.Count}");
                }
                else
                {
                    InfoCenterTip.Show("未检测到预选，请选择 A 组元素（按空格确认）");
                    aList = PickCurves(uiDoc, "请选择 A 组元素（可框选多个，按空格确认）");
                    log.AppendLine($"A 组为手动拾取，数量：{aList.Count}");
                }

                if (aList.Count < 1)
                {
                    message = "请先选中至少 1 个 A 组元素（管道/风管/线槽）";
                    InfoCenterTip.Show("未选择 A 组元素，已取消");
                    return Result.Failed;
                }

                InfoCenterTip.Show("请选择 B 组元素（按空格确认）");
                var bList = PickCurves(uiDoc, "请选择 B 组元素（可框选多个，按空格确认）");
                if (bList.Count < 1)
                {
                    message = "用户取消 B 组拾取";
                    InfoCenterTip.Show("未选择 B 组元素，已取消");
                    return Result.Failed;
                }
                log.AppendLine($"B 组数量：{bList.Count}");

                if (aList.Count != bList.Count)
                {
                    message = string.Format("A 组 ({0}) 与 B 组 ({1}) 数量不一致", aList.Count, bList.Count);
                    InfoCenterTip.Show($"数量不一致：A组 {aList.Count}，B组 {bList.Count}");
                    return Result.Failed;
                }

                var aElems = new List<Element>();
                foreach (var id in aList) aElems.Add(doc.GetElement(id));
                var bElems = new List<Element>();
                foreach (var id in bList) bElems.Add(doc.GetElement(id));

                var pairs = MEPConnectorCalculator.MatchPairs(aElems, bElems);

                if (pairs.Count == 0)
                {
                    message = "找不到系统类型一致的配对。请检查 A/B 组是否存在相同的 System Type。";
                    InfoCenterTip.Show("配对失败：没有系统类型一致的管道");
                    return Result.Failed;
                }

                int idx = 0;
                foreach (var p in pairs)
                {
                    idx++;
                    var a = doc.GetElement(p.AId);
                    var b = doc.GetElement(p.BId);
                    log.AppendLine(string.Format(
                        "\n--- 配对 {0}: A={1} ↔ B={2}（匹配距离 {3:F1}mm）---",
                        idx, p.AId.IntegerValue, p.BId.IntegerValue, p.Distance * 304.8));

                    string reason;
                    if (!MEPConnectorCalculator.IsHorizontallyCollinear(a, b, out reason))
                    {
                        message = string.Format("配对 {0} 共线校验失败：{1}", idx, reason);
                        InfoCenterTip.Show("共线校验失败：" + reason);
                        return Result.Failed;
                    }
                    if (!MEPConnectorCalculator.HasCompatibleSystemType(a, b, out reason))
                    {
                        message = string.Format("配对 {0} 系统类型校验失败：{1}", idx, reason);
                        InfoCenterTip.Show("系统类型校验失败：" + reason);
                        return Result.Failed;
                    }
                }
                log.AppendLine("\n所有配对通过共线/系统类型校验");

                InfoCenterTip.Show($"开始处理 {pairs.Count} 对连接（角度 {angleDeg:F0}°）");
                
                int success = 0, fail = 0;
                using (var tx = new Transaction(doc, "连接两组 MEP"))
                {
                    tx.Start();
                    var svc = new MEPConnectorService(log);
                    foreach (var p in pairs)
                    {
                        try
                        {
                            if (svc.ProcessPair(doc, p.AId, p.BId, angleDeg)) success++;
                            else fail++;
                        }
                        catch (Exception ex)
                        {
                            log.AppendLine("✗ 处理失败：" + ex.Message);
                            log.AppendLine("  堆栈：" + ex.StackTrace);
                            fail++;
                        }
                    }
                    tx.Commit();
                }

                log.AppendLine(string.Format("\n完成：成功 {0}, 失败 {1}", success, fail));
                InfoCenterTip.Show($"MEP 连接完成：成功 {success}，失败 {fail}");

                if (success == 0 && fail > 0)
                {
                    var td = new TaskDialog("MEP 连接失败");
                    td.MainInstruction = string.Format("处理完成：成功 {0}, 失败 {1}", success, fail);
                    td.MainContent = log.GetFullText();
                    td.CommonButtons = TaskDialogCommonButtons.Ok;
                    td.Show();
                }

                return success > 0 ? Result.Succeeded : Result.Failed;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                InfoCenterTip.Show("操作已取消");
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                log.LogException("Execute", ex);
                InfoCenterTip.Show("执行异常：" + ex.Message);
                return Result.Failed;
            }
            finally
            {
                MEPLogger.Instance.EndSession("完成");
            }
        }

        private static string ShowInputBox(string prompt, string title, string defaultValue)
        {
            using (var form = new WinForms.Form())
            {
                form.Text = title;
                form.Width = 400;
                form.Height = 220; // 稍微加高一点，容纳说明文字
                form.FormBorderStyle = WinForms.FormBorderStyle.FixedDialog;
                form.StartPosition = WinForms.FormStartPosition.CenterScreen;
                form.MaximizeBox = false;
                form.MinimizeBox = false;

                var label = new WinForms.Label() { Left = 20, Top = 20, Text = prompt, Width = 350, Height = 60 };
                var textBox = new WinForms.TextBox() { Left = 20, Top = 90, Width = 340, Text = defaultValue };
                var btnOk = new WinForms.Button() { Text = "确定", Left = 200, Width = 80, Top = 130, DialogResult = WinForms.DialogResult.OK };
                var btnCancel = new WinForms.Button() { Text = "取消", Left = 290, Width = 80, Top = 130, DialogResult = WinForms.DialogResult.Cancel };

                form.Controls.Add(label);
                form.Controls.Add(textBox);
                form.Controls.Add(btnOk);
                form.Controls.Add(btnCancel);
                form.AcceptButton = btnOk;
                form.CancelButton = btnCancel;

                return form.ShowDialog() == WinForms.DialogResult.OK ? textBox.Text : null;
            }
        }

        private static List<ElementId> GetPreselectedCurves(UIDocument uiDoc, Document doc)
        {
            var list = new List<ElementId>();
            var selIds = uiDoc.Selection.GetElementIds();
            if (selIds == null) return list;
            foreach (var id in selIds)
            {
                var e = doc.GetElement(id);
                if (e is Pipe || e is Duct || e is CableTray) list.Add(id);
            }
            return list;
        }

        private static List<ElementId> PickCurves(UIDocument uiDoc, string prompt)
        {
            var list = new List<ElementId>();
            try
            {
                using (var finisher = new SpaceKeyFinisher(WinForms.Keys.Space, "完成", "Finish"))
                {
                    finisher.Enable();
                    var refs = uiDoc.Selection.PickObjects(ObjectType.Element, new MEPCurveSelectionFilter(), prompt);
                    if (refs != null)
                        foreach (var r in refs) list.Add(r.ElementId);
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { }
            catch (Exception ex) { MEPLogger.Instance.LogException("PickCurves", ex); }
            return list;
        }
    }

    public class MEPCurveSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element e) { return e is Pipe || e is Duct || e is CableTray; }
        public bool AllowReference(Reference r, XYZ position) { return true; }
    }
}