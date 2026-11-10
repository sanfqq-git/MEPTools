using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Electrical;
using MEPTools.Shared;

namespace MEPTools.TagLeaderStyle60
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Command : IExternalCommand
    {
        private const double TargetAngleDeg = 60.0;
        private const double SIGN_EPSILON = 1e-3;
        private const double ELBOW_TOLERANCE = 1e-3; // 约0.3mm

        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              ElementSet elements)
        {
            var logger = MEPLogger.Instance;
            logger.StartSession("MEPTagLeader60 - 引线折角 60°（绝对安全防乱飞版）");

            try
            {
                UIDocument uidoc = commandData?.Application?.ActiveUIDocument;
                if (uidoc == null) { InfoCenterTip.Show("无活动文档"); return Result.Cancelled; }

                Document doc = uidoc.Document;
                View activeView = doc.ActiveView;
                if (activeView == null) { InfoCenterTip.Show("无活动视图"); return Result.Cancelled; }

                // 1. 预选获取 & 2. 无预选交互选择
                List<IndependentTag> tags = Preselected.GetSelectedIndependentTags(uidoc, activeView);

                if (tags.Count == 0)
                {
                    InfoCenterTip.Show("请选择要调整的标记（Tag），按空格键完成选择");
                    var filter = new MEPTagSelectionFilter();
                    List<Element> picked = MEPSelectionHelper.GetElements(
                        uidoc, filter, "请选择要调整的 MEP 标记",
                        "请在视图中选择标记，按空格键结束",
                        minCount: 1, useSpaceKey: true, singlePick: false);
                    tags = picked.OfType<IndependentTag>().ToList();
                }

                if (tags.Count == 0)
                {
                    InfoCenterTip.Show("未选择任何标记，操作取消");
                    return Result.Cancelled;
                }

                XYZ rightDir = activeView.RightDirection.Normalize();
                XYZ upDir    = activeView.UpDirection.Normalize();
                XYZ viewDir  = activeView.ViewDirection.Normalize();

                bool isRightAngle = Math.Abs(TargetAngleDeg - 90.0) < 1e-6;
                double tanA = isRightAngle ? 0.0 : Math.Tan(TargetAngleDeg * Math.PI / 180.0);

                int modifiedCount   = 0;
                int skippedCount    = 0;
                int noChangeCount   = 0;
                int degenerateCount = 0;

                using (Transaction trans = new Transaction(doc, $"调整标记引线为 {TargetAngleDeg}°"))
                {
                    trans.Start();

                    foreach (IndependentTag tag in tags)
                    {
                        if (!tag.HasLeader) { skippedCount++; continue; }

                        ICollection<Reference> refs = tag.GetTaggedReferences();
                        if (refs == null || refs.Count == 0) { skippedCount++; continue; }

                        Reference reference = refs.FirstOrDefault();
                        if (reference == null) continue;

                        // ★ 绝对安全的终点获取（绝不开关引线，防止状态被破坏）
                        XYZ leaderEnd = null;
                        string endSource = "";
                        
                        try { leaderEnd = tag.GetLeaderEnd(reference); } catch { }
                        if (leaderEnd != null) endSource = "(TrueEnd)";

                        if (leaderEnd == null && reference.GlobalPoint != null)
                        {
                            leaderEnd = reference.GlobalPoint;
                            endSource = "(GlobalPoint)";
                        }

                        if (leaderEnd == null)
                        {
                            Element host = doc.GetElement(reference);
                            if (host != null && host.Location is LocationCurve lc && lc.Curve != null)
                            {
                                IntersectionResult res = lc.Curve.Project(tag.TagHeadPosition);
                                if (res != null) leaderEnd = res.XYZPoint;
                            }
                            endSource = "(Curve.Project)";
                        }

                        if (leaderEnd == null)
                        {
                            logger.AppendLine($"[Skip] Tag {tag.Id} 无法获取引线终点，跳过。");
                            skippedCount++;
                            continue;
                        }

                        XYZ tagHead = tag.TagHeadPosition;
                        
                        // ★ 关键点：直接取 TagHeadPosition 的高度，保证第一段严格水平，绝不触碰 HasLeader
                        double U_elbow = tagHead.DotProduct(upDir);

                        double R_head = tagHead.DotProduct(rightDir);
                        double U_head = tagHead.DotProduct(upDir);
                        double R_end  = leaderEnd.DotProduct(rightDir);
                        double U_end  = leaderEnd.DotProduct(upDir);
                        double D_head = tagHead.DotProduct(viewDir);

                        double R_elbow;
                        string note = "";

                        if (isRightAngle)
                        {
                            R_elbow = R_end;
                            note = "90°-竖直";
                        }
                        else
                        {
                            double dU = U_end - U_elbow;
                            if (Math.Abs(dU) < 1e-4)
                            {
                                R_elbow = R_end;
                                note = "退化-水平对齐";
                                degenerateCount++;
                            }
                            else
                            {
                                double dR = Math.Abs(dU) / tanA;
                                double sign = StableSign(R_end - R_head);
                                R_elbow = R_end - sign * dR;
                                note = $"dR={dR:F3},sign={sign:+0;-0}";
                            }
                        }

                        XYZ elbowPos = rightDir * R_elbow + upDir * U_elbow + viewDir * D_head;

                        // 防无意义触碰检查
                        bool needsUpdate = true;
                        try
                        {
                            XYZ currentElbow = tag.GetLeaderElbow(reference);
                            if (currentElbow != null && currentElbow.DistanceTo(elbowPos) < ELBOW_TOLERANCE)
                            {
                                needsUpdate = false;
                            }
                        }
                        catch { }

                        if (!needsUpdate)
                        {
                            noChangeCount++;
                            continue;
                        }

                        bool anyModified = false;
                        foreach (Reference r in refs)
                        {
                            try { tag.SetLeaderElbow(r, elbowPos); anyModified = true; } catch { }
                        }

                        if (anyModified) modifiedCount++;
                        
                        logger.AppendLine($"[Tag {tag.Id}] mode={tag.LeaderEndCondition} | " +
                                          $"R_end={R_end:F3} U_end={U_end:F3} {endSource} | " +
                                          $"elbow=({R_elbow:F3},{U_elbow:F3}) [{note}]");
                    }

                    // ★ 防污染：若无修改，直接回滚事务
                    if (modifiedCount == 0)
                    {
                        trans.RollBack();
                        logger.AppendLine("[Info] 无实际修改，事务已回滚（模型未受任何污染）");
                    }
                    else
                    {
                        trans.Commit();
                    }
                }

                string summary = $"完成：修改 {modifiedCount} 个，跳过 {skippedCount} 个，无需修改 {noChangeCount} 个。";
                if (degenerateCount > 0) summary += $" 有 {degenerateCount} 个因水平对齐退化为直线。";

                logger.EndSession(summary);
                InfoCenterTip.Show(summary);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                logger.LogException("Command.Execute", ex);
                InfoCenterTip.Show("执行失败：" + ex.Message);
                return Result.Failed;
            }
        }

        private static double StableSign(double v)
        {
            if (Math.Abs(v) < SIGN_EPSILON) return 1.0;
            return Math.Sign(v);
        }

        private class MEPTagSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                if (!(elem is IndependentTag tag)) return false;
                var refs = tag.GetTaggedReferences();
                if (refs == null || refs.Count == 0) return false;
                var host = tag.Document.GetElement(refs.First());
                if (host == null) return false;
                return host is MEPCurve || (host.Category != null && host.Category.CategoryType == CategoryType.Model);
            }
            public bool AllowReference(Reference reference, XYZ position) => true;
        }
    }
}