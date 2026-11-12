using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using MEPTools.Shared;
using MEPTools.MEPConnector.Spatial.Core;

namespace MEPTools.MEPConnector.Spatial.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class CornerCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc?.Document;
            if (doc == null) return Result.Cancelled;

            // ---------- 1. 读取预选 ----------
            var preselectedIds = uidoc.Selection.GetElementIds();
            var preselectedCurves = preselectedIds
                .Select(id => doc.GetElement(id))
                .Where(e => e is MEPCurve)
                .ToList();

            List<Element> aList = new List<Element>();
            List<Element> bList = new List<Element>();

            var filter = new MEPCurveSelectionFilter();

            using (var spaceFinisher = new SpaceKeyFinisher(Keys.Space, "完成", "Finish", "Complete"))
            {
                spaceFinisher.Enable();

                try
                {
                    // ---------- 2. A 组 ----------
                    if (preselectedCurves.Count > 0)
                    {
                        aList = preselectedCurves;
                        uidoc.Selection.SetElementIds(new List<ElementId>());
                        MEPLogger.Instance.AppendLine(
                            $"[Corner] 使用预选 A 组 {aList.Count} 个元素。");
                        InfoCenterTip.Show(
                            $"已识别预选 A 组 {aList.Count} 个，请选择 B 组（空格键确认）");
                    }
                    else
                    {
                        InfoCenterTip.Show("请选择 A 组 MEP 元素（按空格键结束）");

                        IList<Reference> refsA = uidoc.Selection.PickObjects(
                            ObjectType.Element, filter, "第一步：请选择 A 组元素（按空格键结束）");
                        aList = refsA.Select(r => doc.GetElement(r)).ToList();

                        uidoc.Selection.SetElementIds(new List<ElementId>());

                        InfoCenterTip.Show(
                            $"已选择 A 组 {aList.Count} 个，请选择 B 组（空格键确认）");
                    }

                    // ---------- 3. B 组 ----------
                    IList<Reference> refsB = uidoc.Selection.PickObjects(
                        ObjectType.Element, filter, "第二步：请选择 B 组元素（按空格键结束）");
                    bList = refsB.Select(r => doc.GetElement(r)).ToList();
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    return Result.Cancelled;
                }
                finally
                {
                    spaceFinisher.Disable();
                }
            }

            // ---------- 4. 校验 ----------
            if (!aList.Any() || !bList.Any())
            {
                InfoCenterTip.Show("A 组或 B 组未选择任何有效管道，操作取消。");
                return Result.Cancelled;
            }

            var pairs = ConnectorCalculator.MatchPairs(aList, bList, true);

            if (pairs.Count == 0)
            {
                InfoCenterTip.Show("未能匹配到任何一对合法元素，请检查是否选错了模式。");
                return Result.Cancelled;
            }

            // ---------- 4.5 空间错开预检（不改判定逻辑，仅采集信息）----------
            // 说明：
            //   · 仅处理 IsPlanarPath = true 的对（平面求交路径）。立管 × 水平管走 3D 路径，
            //     ComputeCorner3D 已有自身 50mm 容差，不参与本流程。
            //   · 1mm 仅作为浮点噪声过滤，不做工程分级。
            //   · 全部风险对汇总后一次弹窗，用户全权决定是否生成斜管。
            const double noiseMm = 1.0;
            var riskyPairs = new List<ConnectorPair>();
            double minGapMm = double.MaxValue, maxGapMm = 0;

            foreach (var pair in pairs)
            {
                Element elA = doc.GetElement(pair.AId);
                Element elB = doc.GetElement(pair.BId);
                if (elA == null || elB == null) continue;

                var g = ConnectorCalculator.ComputeCornerAuto(elA, elB);
                if (!g.IsValid || !g.IsPlanarPath) continue;

                double gapMm = g.SpatialGap * ConnectorCalculator.FtToMm;
                if (gapMm <= noiseMm) continue;

                pair.SpatialGapMm = gapMm;
                riskyPairs.Add(pair);
                if (gapMm < minGapMm) minGapMm = gapMm;
                if (gapMm > maxGapMm) maxGapMm = gapMm;
            }

            // ---------- 4.6 汇总弹窗（一次）----------
            bool allowSlant = false;
            if (riskyPairs.Count > 0)
            {
                var sb = new StringBuilder();
                foreach (var p in riskyPairs.OrderByDescending(x => x.SpatialGapMm))
                    sb.AppendLine($"  {p.AId} ↔ {p.BId}    错开 {p.SpatialGapMm:F1} mm");

                var td = new TaskDialog("检测到空间错开");
                td.MainInstruction =
                    $"检测到 {riskyPairs.Count} 对元素 XY 投影相交但空间错开";
                td.MainContent =
                    $"错开量范围：{minGapMm:F1} ~ {maxGapMm:F1} mm\n\n" +
                    "直接连接会使 B 组管道产生倾斜（变成带坡度的斜管），" +
                    "并可能改写其坡度参数，影响水力计算及已连接构件。\n\n" +
                    "· 是：对本批全部风险对生成斜管连接\n" +
                    "· 否：跳过这些风险对，其余正常对继续执行\n" +
                    "· 关闭：取消整个操作";
                td.ExpandedContent = sb.ToString();
                td.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
                td.DefaultButton = TaskDialogResult.No;   // 默认否，防误触

                var r = td.Show();
                if (r == TaskDialogResult.Cancel)          // 点 X 关闭
                {
                    InfoCenterTip.Show("已取消整个转角连接操作。");
                    return Result.Cancelled;
                }
                allowSlant = (r == TaskDialogResult.Yes);
            }

            // ---------- 5. 执行 ----------
            MEPLogger.Instance.StartSession("MEP 转角连接");
            var service = new ConnectorService();
            int success = 0, fail = 0, skipped = 0;

            var riskyKey = new HashSet<string>(
                //riskyPairs.Select(p => $"{p.AId.Value}_{p.BId.Value}"));
		riskyPairs.Select(p => $"{p.AId}_{p.BId}"));

            using (Transaction trans = new Transaction(doc, "MEP 转角连接"))
            {
                trans.Start();
                foreach (var pair in pairs)
                {
                    Element elA = doc.GetElement(pair.AId);
                    Element elB = doc.GetElement(pair.BId);
                    if (elA == null || elB == null) { fail++; continue; }

                    //bool isRisky = riskyKey.Contains($"{pair.AId.Value}_{pair.BId.Value}");
		    bool isRisky = riskyKey.Contains($"{pair.AId}_{pair.BId}");

                    if (isRisky && !allowSlant)
                    {
                        MEPLogger.Instance.AppendLine(
                            $"→ 跳过 {pair.AId} & {pair.BId}：用户选择不生成斜管（错开 {pair.SpatialGapMm:F1}mm）");
                        skipped++;
                        continue;
                    }

                    if (isRisky)
                    {
                        MEPLogger.Instance.AppendLine(
                            $"⚠ 用户授权生成斜管：{pair.AId} & {pair.BId}，错开 {pair.SpatialGapMm:F1}mm");
                    }

                    if (!ConnectorCalculator.IsSameType(elA, elB))
                    {
                        MEPLogger.Instance.AppendLine($"✗ 跳过 {elA.Id} & {elB.Id}: 元素类型不同");
                        fail++;
                        continue;
                    }

                    if (!IsSystemTypeCompatible(elA, elB))
                    {
                        MEPLogger.Instance.AppendLine(
                            $"⚠ 警告: {elA.Id} & {elB.Id} 系统类型不一致，仍尝试连接");
                    }

                    if (service.ProcessCorner(doc, elA, elB)) success++;
                    else fail++;
                }
                trans.Commit();
            }

            MEPLogger.Instance.EndSession(
                $"成功 {success} 对，失败 {fail} 对，跳过 {skipped} 对");

            // ---------- 6. 结果用 InfoCenter 显示 ----------
            string hint;
            if (skipped > 0)
                hint = $"（跳过 {skipped} 对空间错开）";
            else if (success == 0 && fail > 0)
                hint = "（若两管平行错开，请用【共线连接】命令）";
            else
                hint = "";

            InfoCenterTip.Show($"转角连接完成：成功 {success} 对，失败 {fail} 对{hint}");

            return Result.Succeeded;
        }

        private bool IsSystemTypeCompatible(Element a, Element b)
        {
            var ta = ConnectorCalculator.GetElementType(a);
            var tb = ConnectorCalculator.GetElementType(b);

            if (ta == MEPElementType.CableTray) return true;

            var sysA = ConnectorCalculator.GetSystemTypeId(a, ta);
            var sysB = ConnectorCalculator.GetSystemTypeId(b, tb);
            return sysA == sysB;
        }
    }

    // 全局管道选择过滤器
    public class MEPCurveSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) { return elem is MEPCurve; }
        public bool AllowReference(Reference reference, XYZ position) { return false; }
    }
}