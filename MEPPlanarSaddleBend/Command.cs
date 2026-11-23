using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using MEPTools.Shared;   // 共享类：MEPLogger（统一日志） / InfoCenterTip（右上角气泡）

namespace MEPTools.MEPPlanarSaddleBend
{
    /// <summary>
    /// 平面翻弯命令。
    /// 共享类适配：
    ///   1. 删除项目内的 MEPLogger.cs，日志统一走 Shared\MEPLogger.cs（API 完全兼容）；
    ///   2. 轻量提示统一走 Shared\InfoCenterTip.cs，配置项走 Shared\MEPConfig.cs；
    ///   3. 未预选元素时，走 Shared\SpaceKeyFinisher.cs 的“框选 + 空格结束”交互；
    ///   4. 只有存在失败元素时才弹 TaskDialog 看完整日志，全部成功只给气泡，不打断操作。
    /// 几何计算、全局自适应、打断/重建/弯头逻辑一律未动。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Command : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            MEPLogger log = MEPLogger.Instance;
            log.StartSession("MEPPlanarSaddleBend 平面翻弯");

            string sessionResult = "未执行";
            try
            {
                // ---------- 1. 预选元素优先；一个都没选到则进入交互框选（空格结束） ----------
                var elemIds = CollectPreselected(doc, uiDoc, log);

                if (elemIds.Count < 1)
                {
                    log.AppendLine("未预选到有效元素，进入交互框选模式。");
                    InfoCenterTip.Show("请在视图中框选要处理的管道 / 风管 / 线槽，按【空格】结束选择");

                    bool cancelled = false;
                    elemIds = PickMEPElements(uiDoc, log, out cancelled);

                    if (cancelled)
                    {
                        sessionResult = "用户取消";
                        InfoCenterTip.Show("已取消元素选择");
                        return Result.Cancelled;
                    }

                    if (elemIds.Count < 1)
                    {
                        sessionResult = "未选择到有效元素";
                        InfoCenterTip.Show("未选择到有效的管道 / 风管 / 线槽，操作已终止");
                        return Result.Failed;
                    }
                }

                log.AppendLine("待处理元素数量：" + elemIds.Count);

                // ---------- 2. 角度输入 ----------
                var dlg = new SaddleBendDialog(45.0);
                if (dlg.ShowDialog() != true || !dlg.Confirmed)
                {
                    log.AppendLine("用户取消了角度输入。");
                    sessionResult = "用户取消";
                    return Result.Cancelled;
                }
                double angleDeg = dlg.AngleDeg;
                log.AppendLine("角度 θ = " + angleDeg.ToString("F2") + " °");

                // ---------- 3. 拾取 P1 ----------
                Reference ref1;
                try
                {
                    ref1 = uiDoc.Selection.PickObject(ObjectType.PointOnElement, new MEPCurveSelectionFilter(),
                        "【第 1/3 步】在参考管上拾取 P1（第一个拐点）");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    log.AppendLine("用户取消了 P1 拾取。");
                    sessionResult = "用户取消";
                    InfoCenterTip.Show("已取消 P1 拾取");
                    return Result.Cancelled;
                }

                Element refElem = doc.GetElement(ref1.ElementId);
                Curve refCurve = (refElem?.Location as LocationCurve)?.Curve;
                if (refCurve == null)
                {
                    log.AppendLine("参考管无定位曲线。");
                    sessionResult = "参考管无定位曲线";
                    InfoCenterTip.Show("参考管无定位曲线，无法确定翻弯基准");
                    return Result.Failed;
                }
                XYZ p1 = ref1.GlobalPoint;

                // ---------- 4. 拾取 P2 ----------
                XYZ p2;
                try
                {
                    p2 = uiDoc.Selection.PickPoint("【第 2/3 步】拾取 P2（翻弯终点）\n其横向距离将作为翻弯高度 H");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    log.AppendLine("用户取消了 P2 拾取。");
                    sessionResult = "用户取消";
                    InfoCenterTip.Show("已取消 P2 拾取");
                    return Result.Cancelled;
                }

                // ---------- 5. 基础 Frame 计算（初始高度由 P2 点决定） ----------
                var frame = SaddleFrameCalculator.Compute(refCurve, p1, p2, angleDeg);
                if (!frame.IsValid)
                {
                    log.AppendLine("坐标系计算失败：" + frame.ErrorMessage);
                    sessionResult = "坐标系计算失败";
                    InfoCenterTip.Show("计算失败：" + frame.ErrorMessage);
                    return Result.Failed;
                }

                log.AppendLine("翻弯高度 H = " + (frame.AbsOffset * SaddleFrameCalculator.FtToMm).ToString("F1") + " mm");
                log.AppendLine("斜段沿管投影 = " + (frame.ProjLen * SaddleFrameCalculator.FtToMm).ToString("F1") + " mm");

                // =====================================================================
                // ★ 全局自适应预计算：如果空间不够，全局统一缩小 H，保证所有管整齐对齐
                // =====================================================================
                double tanA = Math.Tan(frame.AngleRad);
                double minMidLen = UnitUtils.ConvertToInternalUnits(100.0, UnitTypeId.Millimeters); // 最小保留 100mm
                double globalMaxH = frame.AbsOffset; // 初始允许最大 H 即为点击的距离

                foreach (var eid in elemIds)
                {
                    Element el = doc.GetElement(eid);
                    var mc = el as MEPCurve;
                    var line = (mc?.Location as LocationCurve)?.Curve as Line;
                    if (line == null) continue;

                    // 计算该管在当前分布线下的可用长度 D
                    XYZ p0, p2p;
                    if (MEPSaddleBendCalculator.TryIntersectCurveWithLine(line, frame.P1, frame.P1LineDir, true, out p0) &&
                        MEPSaddleBendCalculator.TryIntersectCurveWithLine(line, frame.P2, frame.P2LineDir, true, out p2p))
                    {
                        double D = (p2p - p0).DotProduct(frame.AlongDir);
                        // 根据 D 反推该管能容忍的最大高度：D = 2*(H/tanA) + 100 → H = (D-100)*tanA/2
                        double maxHForThisPipe = (D - minMidLen) * tanA / 2.0;
                        if (maxHForThisPipe < globalMaxH)
                        {
                            globalMaxH = maxHForThisPipe;
                        }
                    }
                }

                if (globalMaxH < 0) globalMaxH = 0;

                if (globalMaxH < frame.AbsOffset)
                {
                    log.AppendLine(string.Format("⚠ 空间不足，整体缩小翻弯高度：{0:F1}mm → {1:F1}mm",
                        frame.AbsOffset * SaddleFrameCalculator.FtToMm, globalMaxH * SaddleFrameCalculator.FtToMm));
                    frame.AbsOffset = globalMaxH;
                    frame.ProjLen = globalMaxH / tanA; // 同步更新斜段投影长度
                }
                // =====================================================================

                // ---------- 6. 单事务逐根处理 ----------
                int successCount = 0, failCount = 0;
                using (var tx = new Transaction(doc, "创建 MEP 平面翻弯"))
                {
                    tx.Start();

                    foreach (var eid in elemIds)
                    {
                        Element el = doc.GetElement(eid);
                        log.AppendLine("\n--- 处理元素 ID=" + eid.IntegerValue + " ---");

                        // 此时 frame 里的 AbsOffset 已经是全局统一缩减后的值
                        var r = MEPSaddleBendCalculator.ComputePipeGeometry(el, frame);
                        if (!r.IsValid)
                        {
                            log.AppendLine("✗ 计算失败: " + r.ErrorMessage);
                            failCount++;
                            continue;
                        }

                        try
                        {
                            var svc = new MEPSaddleBendService(log);
                            if (svc.ProcessSingleElement(doc, eid, r)) successCount++;
                            else failCount++;
                        }
                        catch (Exception ex)
                        {
                            log.AppendLine("✗ 处理失败: " + ex.Message);
                            failCount++;
                        }
                    }
                    tx.Commit();
                }

                // ---------- 7. 结果反馈 ----------
                string summary = string.Format("成功 {0}，失败 {1}", successCount, failCount);
                log.AppendLine("\n处理完成：" + summary);
                sessionResult = summary;

                if (failCount > 0)
                {
                    // 有失败才弹窗：完整逐条日志看得清，便于排查
                    var td = new TaskDialog("MEP 平面翻弯");
                    td.MainInstruction = "处理完成：" + summary;
                    td.MainContent = log.GetFullText();
                    td.Show();
                }
                else
                {
                    // 全部成功：只给气泡，不打断连续操作，详细日志仍写文件
                    InfoCenterTip.Show("平面翻弯完成：" + summary);
                }

                return successCount > 0 ? Result.Succeeded : Result.Failed;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                log.AppendLine("用户取消了操作。");
                sessionResult = "用户取消";
                InfoCenterTip.Show("操作已取消");
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                log.LogException("Execute", ex);
                sessionResult = "异常：" + ex.Message;
                return Result.Failed;
            }
            finally
            {
                log.EndSession(sessionResult);
            }
        }

        // =====================================================================
        // 预选收集：只认 Pipe / Duct / CableTray，非 MEP 元素计数并提示
        // =====================================================================
        private List<ElementId> CollectPreselected(Document doc, UIDocument uiDoc, MEPLogger log)
        {
            var ids = new List<ElementId>();

            ICollection<ElementId> selIds = uiDoc.Selection.GetElementIds();
            if (selIds == null || selIds.Count == 0) return ids;

            int ignored = 0;
            foreach (var id in selIds)
            {
                Element e = doc.GetElement(id);
                if (e is Pipe || e is Duct || e is CableTray) ids.Add(id);
                else ignored++;
            }

            if (ignored > 0)
            {
                log.AppendLine("预选中已忽略 " + ignored + " 个非 管道/风管/线槽 元素。");
                InfoCenterTip.Show(string.Format(
                    MEPConfig.GetString("Selection.Msg.IgnoredCount", "已忽略 {0} 个不符合要求的元素"), ignored));
            }

            return ids;
        }

        // =====================================================================
        // 交互框选：PickObjects + SpaceKeyFinisher（按空格 → 自动点击选项栏“完成”）
        // 热键注册失败不影响主流程，仍可手动点击“完成”结束选择。
        // =====================================================================
        private List<ElementId> PickMEPElements(UIDocument uiDoc, MEPLogger log, out bool cancelled)
        {
            cancelled = false;
            var ids = new List<ElementId>();
            var seen = new HashSet<int>();

            // SpaceKeyFinisher 默认热键 = Space；按钮文本取自 MEPConfig（完成;Finish;Complete）
            using (var finisher = new SpaceKeyFinisher())
            {
                finisher.Enable();
                try
                {
                    IList<Reference> refs = uiDoc.Selection.PickObjects(
                        ObjectType.Element,
                        new MEPCurveSelectionFilter(),
                        "请框选要处理的管道 / 风管 / 线槽，按【空格】结束选择（ESC 取消）");

                    if (refs != null)
                    {
                        foreach (var r in refs)
                        {
                            if (r == null || r.ElementId == ElementId.InvalidElementId) continue;
                            if (seen.Add(r.ElementId.IntegerValue)) ids.Add(r.ElementId);
                        }
                    }
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    cancelled = true;
                    log.AppendLine("用户取消了交互框选。");
                }
                catch (Exception ex)
                {
                    log.AppendLine("✗ 交互框选异常: " + ex.Message);
                }
            }

            return ids;
        }
    }

    public class MEPCurveSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element e) { return e is Pipe || e is Duct || e is CableTray; }
        public bool AllowReference(Reference r, XYZ position) { return true; }
    }
}
