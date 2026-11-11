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
        // 容差 3mm，防止误杀正确的
        private const double ELBOW_TOLERANCE = 0.01; 

        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              ElementSet elements)
        {
            var logger = MEPLogger.Instance;
            logger.StartSession("MEPTagLeader60 - 60°折角（文字中线兼容防飞天版）");

            try
            {
                UIDocument uidoc = commandData?.Application?.ActiveUIDocument;
                if (uidoc == null) { InfoCenterTip.Show("无活动文档"); return Result.Cancelled; }

                Document doc = uidoc.Document;
                View activeView = doc.ActiveView;
                if (activeView == null) { InfoCenterTip.Show("无活动视图"); return Result.Cancelled; }

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

                if (tags.Count == 0) { InfoCenterTip.Show("未选择任何标记，操作取消"); return Result.Cancelled; }

                XYZ rightDir = activeView.RightDirection.Normalize();
                XYZ upDir    = activeView.UpDirection.Normalize();
                XYZ viewDir  = activeView.ViewDirection.Normalize();

                bool isRightAngle = Math.Abs(TargetAngleDeg - 90.0) < 1e-6;
                double tanA = isRightAngle ? 0.0 : Math.Tan(TargetAngleDeg * Math.PI / 180.0);

                int modifiedCount   = 0;
                int skippedCount    = 0;
                int noChangeCount   = 0;

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

                        // =======================================================
                        // 1. 确定绝对水平的参考高度
                        // 完美解法：如果存在旧折点，直接使用旧折点的高度！这完美避开了P1/P2的偏移问题。
                        // =======================================================
                        double U_elbow = tag.TagHeadPosition.DotProduct(upDir);
                        double R_elbow_old = tag.TagHeadPosition.DotProduct(rightDir);
                        
                        try
                        {
                            XYZ currentElbow = tag.GetLeaderElbow(reference);
                            if (currentElbow != null && !currentElbow.IsZeroLength())
                            {
                                U_elbow = currentElbow.DotProduct(upDir);
                                R_elbow_old = currentElbow.DotProduct(rightDir);
                            }
                        }
                        catch { }

                        // =======================================================
                        // 2. 确定终点（拒绝包围盒，防飞天）
                        // =======================================================
                        XYZ leaderEnd = null;
                        string endSource = "";

                        // 优先取真实终点（Free模式）
                        try { leaderEnd = tag.GetLeaderEnd(reference); } catch { }
                        if (leaderEnd != null && !leaderEnd.IsZeroLength()) endSource = "(TrueEnd)";

                        // 次选：宿主管线投影（风管、水管）
                        if (leaderEnd == null)
                        {
                            Element host = doc.GetElement(reference);
                            if (host != null && host.Location is LocationCurve lc && lc.Curve != null)
                            {
                                IntersectionResult res = lc.Curve.Project(tag.TagHeadPosition);
                                if (res != null && res.XYZPoint != null)
                                {
                                    leaderEnd = res.XYZPoint;
                                    endSource = "(Curve.Project)";
                                }
                            }
                        }

                        // 最后：设备原点（法兰、风阀等没有LocationCurve）
                        if (leaderEnd == null)
                        {
                            Element host = doc.GetElement(reference);
                            if (host != null && host.Location is LocationPoint lp && lp.Point != null)
                            {
                                leaderEnd = lp.Point;
                                endSource = "(LocationPoint)";
                            }
                        }

                        // 拿不到绝不瞎猜，宁可不动也不乱飞
                        if (leaderEnd == null)
                        {
                            logger.AppendLine($"[Skip] Tag {tag.Id} 无法获取真实终点，跳过。");
                            skippedCount++;
                            continue;
                        }

                        // =======================================================
                        // 3. 计算折点
                        // =======================================================
                        double R_head = tag.TagHeadPosition.DotProduct(rightDir);
                        double U_end  = leaderEnd.DotProduct(upDir);
                        double R_end  = leaderEnd.DotProduct(rightDir);
                        double D_head = tag.TagHeadPosition.DotProduct(viewDir);

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
                            }
                            else
                            {
                                double dR = Math.Abs(dU) / tanA;
                                // 用旧折点判方向，防震荡
                                double sign = StableSign(R_end - R_elbow_old);
                                R_elbow = R_end - sign * dR;
                                note = $"dR={dR:F3},sign={sign:+0;-0}";
                            }
                        }

                        XYZ elbowPos = rightDir * R_elbow + upDir * U_elbow + viewDir * D_head;

                        // =======================================================
                        // 4. 防误杀检测（3mm 容差）
                        // =======================================================
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

                    // 5. 无修改则回滚，防污染
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