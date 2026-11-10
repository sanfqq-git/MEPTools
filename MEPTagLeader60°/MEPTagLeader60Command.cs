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
            logger.StartSession("MEPTagLeader60 - 引线折角 60°（修复版）");

            try
            {
                UIDocument uidoc = commandData?.Application?.ActiveUIDocument;
                if (uidoc == null)
                {
                    InfoCenterTip.Show("无活动文档");
                    return Result.Cancelled;
                }

                Document doc = uidoc.Document;
                View activeView = doc.ActiveView;
                if (activeView == null)
                {
                    InfoCenterTip.Show("无活动视图");
                    return Result.Cancelled;
                }

                // =========================================================
                // 1. 预选获取 & 2. 无预选交互选择 (使用共享类 + 空格键结束)
                // =========================================================
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
                    logger.EndSession("已取消：未选中有效 MEP 标记");
                    InfoCenterTip.Show("未选择任何标记，操作取消");
                    return Result.Cancelled;
                }

                // 视图坐标系
                XYZ rightDir = activeView.RightDirection.Normalize();
                XYZ upDir    = activeView.UpDirection.Normalize();
                XYZ viewDir  = activeView.ViewDirection.Normalize();

                bool isRightAngle = Math.Abs(TargetAngleDeg - 90.0) < 1e-6;
                double tanA = isRightAngle ? 0.0 : Math.Tan(TargetAngleDeg * Math.PI / 180.0);

                int modifiedCount   = 0;
                int skippedCount    = 0;
                int noChangeCount   = 0;
                int degenerateCount = 0; // 记录退化为直线的数量

                using (Transaction trans = new Transaction(doc, $"调整标记引线为 {TargetAngleDeg}°"))
                {
                    trans.Start();

                    foreach (IndependentTag tag in tags)
                    {
                        if (!tag.HasLeader)
                        {
                            skippedCount++;
                            continue; 
                        }

                        ICollection<Reference> refs = tag.GetTaggedReferences();
                        if (refs == null || refs.Count == 0)
                        {
                            skippedCount++;
                            continue;
                        }

                        Reference reference = refs.FirstOrDefault();
                        if (reference == null) continue;

                        // ★ 修复：优先获取真实的吸附终点，防止引线吸在管壁边缘导致角度算错
                        XYZ leaderEnd = null;
                        bool usedFallback = false;
                        try
                        {
                            if (tag.LeaderEndCondition == LeaderEndCondition.Attached)
                            {
                                leaderEnd = tag.GetLeaderEnd(reference);
                            }
                        }
                        catch { /* 容错处理 */ }

                        if (leaderEnd == null)
                        {
                            // 如果是 Free 模式或获取失败，才退回宿主几何中心
                            leaderEnd = ResolveHostCenter(doc, activeView, reference);
                            usedFallback = true;
                        }

                        if (leaderEnd == null)
                        {
                            skippedCount++;
                            continue;
                        }

                        XYZ tagHead = tag.TagHeadPosition;
                        double textCenterU = tagHead.DotProduct(upDir);

                        // 视图坐标投影
                        double R_head = tagHead.DotProduct(rightDir);
                        double U_head = tagHead.DotProduct(upDir);
                        double R_end  = leaderEnd.DotProduct(rightDir);
                        double U_end  = leaderEnd.DotProduct(upDir);
                        double D_head = tagHead.DotProduct(viewDir);

                        double U_elbow = textCenterU;
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
                                // ★ 修复：明确提示用户角度无法成形，因为元素与文字处于同一水平线
                                R_elbow = R_end;
                                note = "退化-元素与文字水平对齐，无法形成折角";
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

                        // 避免无意义触碰：检查当前肘点是否已经符合要求
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

                        // 统一设置肘点
                        bool anyModified = false;
                        foreach (Reference r in refs)
                        {
                            try
                            {
                                tag.SetLeaderElbow(r, elbowPos);
                                anyModified = true;
                            }
                            catch (Exception ex)
                            {
                                logger.LogException($"SetLeaderElbow tag={tag.Id}", ex);
                            }
                        }

                        if (anyModified) modifiedCount++;
                        
                        logger.AppendLine($"[Tag {tag.Id}] end=({R_end:F3},{U_end:F3}) " +
                                          $"elbow=({R_elbow:F3},{U_elbow:F3}) [{note}]{(usedFallback ? " (Fallback)" : "")}");
                    }

                    // 无修改则回滚，避免污染 Last changed by
                    if (modifiedCount == 0)
                    {
                        trans.RollBack();
                        logger.AppendLine("[Info] 无实际修改，事务已回滚");
                    }
                    else
                    {
                        trans.Commit();
                    }
                }

                string summary = $"完成：修改 {modifiedCount} 个，跳过 {skippedCount} 个，无需修改 {noChangeCount} 个。";
                if (degenerateCount > 0)
                {
                    summary += $" 注意：有 {degenerateCount} 个标记因元素与文字完全对齐（水平），无法形成 60° 折角，已自动退化为水平引线。";
                }

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

        private static XYZ ResolveHostCenter(Document doc, View activeView, Reference reference)
        {
            Element host = doc.GetElement(reference);
            if (host == null) return null;

            if (host.Location is LocationPoint lp && lp.Point != null)
                return lp.Point;

            if (host.Location is LocationCurve lc && lc.Curve != null)
                return lc.Curve.Evaluate(0.5, true);

            BoundingBoxXYZ bb = host.get_BoundingBox(activeView);
            if (bb != null && bb.Min != null && bb.Max != null)
            {
                try { return bb.Transform.OfPoint((bb.Min + bb.Max) * 0.5); }
                catch { return (bb.Min + bb.Max) * 0.5; }
            }
            return null;
        }

        private static double StableSign(double v)
        {
            if (Math.Abs(v) < SIGN_EPSILON) return 1.0;
            return Math.Sign(v);
        }

        /// <summary>
        /// 选择过滤器：仅允许选中 MEP 元素（管线/管件等）上的标签
        /// </summary>
        private class MEPTagSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                if (!(elem is IndependentTag tag)) return false;

                var refs = tag.GetTaggedReferences();
                if (refs == null || refs.Count == 0) return false;

                var host = tag.Document.GetElement(refs.First());
                if (host == null) return false;

                // 判断是不是 MEP 元素（管线、风管、桥架、管件等）
                return host is MEPCurve || 
                       (host.Category != null && host.Category.CategoryType == CategoryType.Model && host.Category.Id.IntegerValue != (int)BuiltInCategory.OST_Walls);
            }

            public bool AllowReference(Reference reference, XYZ position) => true;
        }
    }
}