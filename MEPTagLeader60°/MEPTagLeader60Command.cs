using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.Shared;

namespace MEPTools.TagLeaderStyle60
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Command : IExternalCommand
    {
        // 60°：第二段与水平方向成 60°（第一段保持水平，落在文字垂直中心）
        private const double TargetAngleDeg = 60.0;

        // sign 判定的死区阈值（视图坐标，单位与 Revit 内部一致）
        // 当 |R_end - R_head| 小于此值时，固定取 +1，避免左右抖动
        private const double SIGN_EPSILON = 1e-3;

        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              ElementSet elements)
        {
            var logger = MEPLogger.Instance;
            logger.StartSession("MEPTagLeader60 - 引线折角 60°（方案B：宿主几何恒定）");

            try
            {
                UIDocument uidoc = commandData?.Application?.ActiveUIDocument;
                if (uidoc == null)
                {
                    logger.AppendLine("[Info] 无活动 UIDocument");
                    logger.EndSession("已取消：无活动文档");
                    return Result.Cancelled;
                }

                Document doc = uidoc.Document;
                View activeView = doc.ActiveView;
                if (activeView == null)
                {
                    logger.AppendLine("[Info] ActiveView 为空");
                    logger.EndSession("已取消：无活动视图");
                    return Result.Cancelled;
                }

                List<IndependentTag> tags =
                    Preselected.GetSelectedIndependentTags(uidoc, activeView);

                if (tags.Count == 0)
                {
                    logger.AppendLine("[Info] 未选中任何标记");
                    TaskDialog.Show("提示", "请先选中至少一个标记（Tag）。");
                    logger.EndSession("已取消：未选中标记");
                    return Result.Cancelled;
                }

                XYZ rightDir = activeView.RightDirection.Normalize();
                XYZ upDir    = activeView.UpDirection.Normalize();
                XYZ viewDir  = activeView.ViewDirection.Normalize();

                logger.AppendLine($"[Info] 视图 = {activeView.Name}（{activeView.ViewType}）");
                logger.AppendLine($"[Info] 选中标记数量 = {tags.Count}");
                logger.AppendLine($"[Info] 目标折角 = {TargetAngleDeg}°");

                bool isRightAngle = Math.Abs(TargetAngleDeg - 90.0) < 1e-6;
                double tanA = isRightAngle ? 0.0 : Math.Tan(TargetAngleDeg * Math.PI / 180.0);

                int modifiedCount = 0;

                using (Transaction trans = new Transaction(
                           doc, $"将标记引线调整为 {TargetAngleDeg}° 折线"))
                {
                    trans.Start();

                    // =========================================================
                    // 步骤 1：临时关闭引线，获取"纯文字"包围盒垂直中心
                    // 注意：这一步只关/开引线，不改 tagHead，依赖此前提
                    // =========================================================
                    foreach (var t in tags) t.HasLeader = false;
                    doc.Regenerate();

                    var pureTextCenterU = new Dictionary<ElementId, double>(tags.Count);
                    foreach (var t in tags)
                    {
                        double u = t.TagHeadPosition.DotProduct(upDir);
                        BoundingBoxXYZ bb = t.get_BoundingBox(activeView);
                        if (bb != null && bb.Min != null && bb.Max != null)
                        {
                            XYZ center = (bb.Min + bb.Max) * 0.5;
                            u = center.DotProduct(upDir);
                        }
                        pureTextCenterU[t.Id] = u;
                    }

                    // =========================================================
                    // 步骤 2：恢复引线
                    // =========================================================
                    foreach (var t in tags) t.HasLeader = true;
                    doc.Regenerate();

                    // =========================================================
                    // 步骤 3：计算折点
                    // 【方案 B 核心】：leaderEnd 恒取宿主几何中心，
                    // 不读 GetLeaderEnd，彻底消除 Free 模式端点污染
                    // =========================================================
                    foreach (IndependentTag tag in tags)
                    {
                        ICollection<Reference> refs = tag.GetTaggedReferences();
                        if (refs == null || refs.Count == 0)
                        {
                            logger.AppendLine($"[Skip] Tag {tag.Id} 无参照");
                            continue;
                        }

                        // 取第一个 reference（elbow 是共享的，只计算一次）
                        Reference reference = null;
                        foreach (var r in refs) { reference = r; break; }
                        if (reference == null) continue;

                        if (refs.Count > 1)
                        {
                            logger.AppendLine(
                                $"[Warn] Tag {tag.Id} 有 {refs.Count} 个参照，" +
                                "肘点共享，仅用第一个 reference 计算，并对全部 reference 设置同一肘点。");
                        }

                        if (!pureTextCenterU.TryGetValue(tag.Id, out double textCenterU))
                            textCenterU = tag.TagHeadPosition.DotProduct(upDir);

                        XYZ tagHead = tag.TagHeadPosition;

                        // ★ 方案 B：仅宿主几何
                        XYZ leaderEnd = ResolveHostCenter(doc, activeView, reference);
                        bool usedFallback = false;
                        if (leaderEnd == null)
                        {
                            leaderEnd = tagHead - upDir * 2.0;
                            usedFallback = true;
                        }

                        // 视图坐标系投影
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
                            if (Math.Abs(dU) < 1e-6)
                            {
                                R_elbow = R_end;
                                note = "退化-dU≈0";
                            }
                            else
                            {
                                double dR = Math.Abs(dU) / tanA;
                                double sign = StableSign(R_end - R_head);
                                R_elbow = R_end - sign * dR;
                                note = $"dR={dR:F3},sign={sign:+0;-0}";
                            }
                        }

                        XYZ elbowPos =
                            rightDir * R_elbow + upDir * U_elbow + viewDir * D_head;

                        // 对所有 reference 设置同一肘点（共享肘点）
                        foreach (Reference r in refs)
                        {
                            try
                            {
                                tag.SetLeaderElbow(r, elbowPos);
                            }
                            catch (Exception ex)
                            {
                                logger.LogException(
                                    $"Command.SetLeaderElbow tag={tag.Id} ref={r.ElementId}", ex);
                            }
                        }

                        modifiedCount++;

                        // 详细日志：跑两次对比就能判断 tagHead 是否漂移
                        logger.AppendLine(
                            $"[Tag {tag.Id}] mode={tag.LeaderEndCondition} " +
                            $"ref={reference.ElementId}{(usedFallback ? "(fallback)" : "")} " +
                            $"head=({R_head:F3},{U_head:F3}) " +
                            $"end=({R_end:F3},{U_end:F3}) " +
                            $"textCenterU={textCenterU:F3} " +
                            $"elbow=({R_elbow:F3},{U_elbow:F3}) " +
                            $"[{note}]");
                    }

                    trans.Commit();
                }
                logger.EndSession($"完成，共修改 {modifiedCount} 个标记");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                logger.LogException("Command.Execute", ex);
                message = ex.Message;
                return Result.Failed;
            }
        }

        /// <summary>
        /// 取宿主几何中心（与引线状态无关，稳定可重复）。
        /// 优先级：LocationPoint → LocationCurve 中点 → 包围盒中心。
        /// 都取不到时返回 null，由调用方兜底。
        /// </summary>
        private static XYZ ResolveHostCenter(Document doc, View activeView,
                                             Reference reference)
        {
            Element host = doc.GetElement(reference);
            if (host == null) return null;

            if (host.Location is LocationPoint lp && lp.Point != null)
                return lp.Point;

            if (host.Location is LocationCurve lc && lc.Curve != null)
                return lc.Curve.Evaluate(0.5, true);

            BoundingBoxXYZ bb = host.get_BoundingBox(activeView);
            if (bb != null && bb.Min != null && bb.Max != null)
                return bb.Transform.OfPoint((bb.Min + bb.Max) * 0.5);

            return null;
        }

        /// <summary>
        /// 带死区的符号函数：接近 0 时固定 +1，避免折点方向抖动。
        /// </summary>
        private static double StableSign(double v)
        {
            if (Math.Abs(v) < SIGN_EPSILON) return 1.0;
            return Math.Sign(v);
        }
    }
}