using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using MEPTools.Shared;
using WinForms = System.Windows.Forms;

namespace MEPTools.MEPConnector.HighLow
{
    /// <summary>
    /// 高差连接命令基类。封装 A/B 组拾取、配对、纠偏、连接等公共流程。
    /// 子类只需实现 <see cref="ResolveAngle"/> 决定本次使用的连接角度。
    /// </summary>
    public abstract class HighLowConnectorCmdBase : IExternalCommand
    {
        protected virtual string SessionTitle => "CreateMEPConnector 连接两组 MEP";

        /// <summary>
        /// 决定本次连接使用的角度。返回 <see cref="Result.Succeeded"/> 时表示 angleDeg 有效。
        /// 返回 <see cref="Result.Cancelled"/> 视为用户取消；返回 <see cref="Result.Failed"/> 视为输入无效。
        /// </summary>
        protected abstract Result ResolveAngle(out double angleDeg, ref string message);

        public Result Execute(ExternalCommandData commandData,
            ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            MEPLogger.Instance.StartSession(SessionTitle);
            var log = MEPLogger.Instance;

            try
            {
                // ───────── 1. 角度获取（由子类决定） ─────────
                double angleDeg;
                Result angleResult = ResolveAngle(out angleDeg, ref message);
                if (angleResult == Result.Cancelled)
                {
                    InfoCenterTip.Show("操作已取消");
                    return Result.Cancelled;
                }
                if (angleResult == Result.Failed)
                {
                    InfoCenterTip.Show("角度无效：" + (message ?? "未知原因"));
                    return Result.Failed;
                }
                log.AppendLine($"本次连接角度：{angleDeg}°");

                // ───────── 2. A 组拾取 ─────────
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

                // ───────── 3. B 组拾取 ─────────
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
                    message = string.Format("A 组 ({0}) 与 B 组 ({1}) 数量不一致",
                        aList.Count, bList.Count);
                    InfoCenterTip.Show($"数量不一致：A组 {aList.Count}，B组 {bList.Count}");
                    return Result.Failed;
                }

                var aElems = new List<Element>();
                foreach (var id in aList) aElems.Add(doc.GetElement(id));
                var bElems = new List<Element>();
                foreach (var id in bList) bElems.Add(doc.GetElement(id));

                // ───────── 4. 配对 ─────────
                var pairs = MEPConnectorCalculator.MatchPairs(aElems, bElems);
                if (pairs.Count == 0)
                {
                    message = "找不到系统类型一致的配对。请检查 A/B 组是否存在相同的 System Type。";
                    InfoCenterTip.Show("配对失败：没有系统类型一致的管道");
                    return Result.Failed;
                }

                // ───────── 5. 共线偏差检查 ─────────
                int idx = 0;
                int nonCollinearCount = 0;
                double minDeviationMm = 1.0; // 1mm 偏差容忍度

                foreach (var p in pairs)
                {
                    idx++;
                    var a = doc.GetElement(p.AId);
                    var b = doc.GetElement(p.BId);

                    string reason;
                    if (!MEPConnectorCalculator.HasCompatibleSystemType(a, b, out reason))
                    {
                        message = string.Format("配对 {0} 系统类型校验失败：{1}", idx, reason);
                        InfoCenterTip.Show("系统类型校验失败：" + reason);
                        return Result.Failed;
                    }

                    double dev = MEPConnectorCalculator.GetHorizontalDeviation(a, b);

                    log.AppendLine(string.Format(
                        "\n--- 配对 {0}: A={1} ↔ B={2}（匹配距离 {3:F1}mm, 水平偏差 {4:F2}mm）---",
                        idx, p.AId.IntegerValue, p.BId.IntegerValue,
                        p.Distance * 304.8, dev * 304.8));

                    if (dev * 304.8 > minDeviationMm)
                    {
                        nonCollinearCount++;
                        log.AppendLine($"配对 {idx} 存在水平偏差，需纠偏。");
                    }
                }

                // ───────── 6. 是否执行前置纠偏 ─────────
                bool doAlign = false;
                if (nonCollinearCount > 0)
                {
                    var dialog = new TaskDialog("检测到共线偏差");
                    dialog.MainInstruction = $"检测到 {nonCollinearCount} 对元素不完全共线（存在水平偏差）";
                    dialog.MainContent = "是否自动在水平面上平移 B 组元素，使其与 A 组完全共线？\n\n" +
                                         "选择“是”将为您执行前置准备，消除偏差，确保连接平滑。\n" +
                                         "选择“否”将取消本次操作。";
                    dialog.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
                    dialog.DefaultButton = TaskDialogResult.Yes;

                    if (dialog.Show() == TaskDialogResult.Yes)
                    {
                        doAlign = true;
                        log.AppendLine($"\n用户确认：执行前置纠偏。");
                    }
                    else
                    {
                        message = "存在未对齐的元素，用户取消了纠偏。";
                        InfoCenterTip.Show("已取消：元素存在偏差且未纠偏");
                        return Result.Failed;
                    }
                }

                InfoCenterTip.Show($"开始处理 {pairs.Count} 对连接（角度 {angleDeg:F0}°）");

                // ───────── 7. 事务内：纠偏 + 连接 ─────────
                int success = 0, fail = 0;
                using (var tx = new Transaction(doc, "连接两组 MEP"))
                {
                    tx.Start();
                    var svc = new MEPConnectorService(log);

                    if (doAlign)
                    {
                        int alignCount = 0;
                        foreach (var p in pairs)
                        {
                            var a = doc.GetElement(p.AId);
                            var b = doc.GetElement(p.BId);
                            if (svc.AlignPair(doc, a, b)) alignCount++;
                        }
                        doc.Regenerate();
                        log.AppendLine($"已自动对齐 {alignCount} 对元素。\n");
                    }

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

        // ───────── 供子类复用的辅助方法 ─────────

        protected static string ShowInputBox(string prompt, string title, string defaultValue)
        {
            using (var form = new WinForms.Form())
            {
                form.Text = title;
                form.Width = 400;
                form.Height = 220;
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
                    var refs = uiDoc.Selection.PickObjects(ObjectType.Element,
                        new MEPCurveSelectionFilter(), prompt);
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