using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TagLeaderStyle
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class TagLeaderStyleCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;

            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;

            List<IndependentTag> tags = GetSelectedTags(uidoc);
            if (tags.Count == 0)
            {
                TaskDialog.Show("提示", "请先选中至少一个标记（Tag）。");
                return Result.Cancelled;
            }

            // 获取视图坐标轴
            XYZ rightDir = activeView.RightDirection.Normalize();
            XYZ upDir = activeView.UpDirection.Normalize();
            XYZ viewDir = activeView.ViewDirection.Normalize();

            int modifiedCount = 0;

            using (Transaction trans = new Transaction(doc, "修改标记引线为90度折线"))
            {
                trans.Start();

                // =========================================================
                // 步骤 1：临时关闭所有 Tag 的引线，获取“纯文字”的包围盒中心
                // =========================================================
                foreach (var t in tags) t.HasLeader = false;
                doc.Regenerate(); // 强制刷新，让包围盒变成纯文字的大小

                Dictionary<ElementId, double> pureTextCenterU = new Dictionary<ElementId, double>();
                foreach (var t in tags)
                {
                    double u = t.TagHeadPosition.DotProduct(upDir); // 默认用插入点
                    BoundingBoxXYZ bb = t.get_BoundingBox(activeView);
                    if (bb != null && bb.Min != null && bb.Max != null)
                    {
                        XYZ center = (bb.Min + bb.Max) * 0.5;
                        u = center.DotProduct(upDir); // 获得不含引线的纯文字垂直中心
                    }
                    pureTextCenterU[t.Id] = u;
                }

                // =========================================================
                // 步骤 2：恢复引线
                // =========================================================
                foreach (var t in tags) t.HasLeader = true;
                doc.Regenerate(); 

                // =========================================================
                // 步骤 3：计算并设置折点 (使用刚才获取的纯文字中心)
                // =========================================================
                foreach (IndependentTag tag in tags)
                {
                    ICollection<Reference> refs = tag.GetTaggedReferences();
                    if (refs == null || refs.Count == 0) continue;

                    bool tagModified = false;
                    foreach (Reference reference in refs)
                    {
                        // 获取宿主连接点
                        XYZ leaderEnd = null;
                        if (tag.LeaderEndCondition == LeaderEndCondition.Free)
                        {
                            leaderEnd = tag.GetLeaderEnd(reference);
                        }
                        else
                        {
                            Element host = doc.GetElement(reference);
                            if (host != null)
                            {
                                if (host.Location is LocationPoint lp && lp.Point != null)
                                    leaderEnd = lp.Point;
                                else if (host.Location is LocationCurve lc && lc.Curve != null)
                                    leaderEnd = lc.Curve.Evaluate(0.5, true);
                                else
                                {
                                    BoundingBoxXYZ hostBb = host.get_BoundingBox(activeView);
                                    if (hostBb != null)
                                        leaderEnd = hostBb.Transform.OfPoint((hostBb.Min + hostBb.Max) * 0.5);
                                }
                            }
                        }

                        if (leaderEnd == null) leaderEnd = tag.TagHeadPosition - upDir * 2.0;

                        XYZ tagHead = tag.TagHeadPosition;

                        // 计算投影坐标
                        double R_head = tagHead.DotProduct(rightDir);
                        double R_end = leaderEnd.DotProduct(rightDir);
                        double D_head = tagHead.DotProduct(viewDir);

                        // 【核心】：折点计算
                        // 水平方向 R_elbow 对齐到宿主构件的水平位置（确保垂直段垂直）
                        double R_elbow = R_end;
                        
                        // 垂直方向 U_elbow 对齐到“纯文字”的垂直中心（确保水平段居中）
                        double U_elbow = pureTextCenterU[tag.Id];

                        // 构建三维坐标
                        XYZ elbowPos = rightDir * R_elbow + upDir * U_elbow + viewDir * D_head;

                        tag.SetLeaderElbow(reference, elbowPos);
                        tagModified = true;
                    }

                    if (tagModified) modifiedCount++;
                }

                trans.Commit();
            }

            if (modifiedCount > 0)
                TaskDialog.Show("完成", $"成功将 {modifiedCount} 个标记的引线调整为 90° Z字形折线。");
            else
                TaskDialog.Show("提示", "选中的标记无需修改。");

            return Result.Succeeded;
        }

        private List<IndependentTag> GetSelectedTags(UIDocument uidoc)
        {
            List<IndependentTag> tags = new List<IndependentTag>();
            Document doc = uidoc.Document;

            foreach (ElementId id in uidoc.Selection.GetElementIds())
            {
                Element elem = doc.GetElement(id);
                if (elem == null) continue;

                if (elem is IndependentTag tag)
                    tags.Add(tag);
                else if (elem is Group group)
                {
                    foreach (ElementId mId in group.GetMemberIds())
                        if (doc.GetElement(mId) is IndependentTag memberTag) tags.Add(memberTag);
                }
                else if (elem is MultiReferenceAnnotation mra)
                {
                    ElementId mraTagId = mra.TagId;
                    if (mraTagId != null && mraTagId != ElementId.InvalidElementId)
                    {
                        if (doc.GetElement(mraTagId) is IndependentTag mraTag)
                            tags.Add(mraTag);
                    }
                }
            }
            return tags;
        }
    }
}