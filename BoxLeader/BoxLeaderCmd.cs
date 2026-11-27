using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using MEPTools.Shared;

namespace MEPTools.BoxLeader
{
    /// <summary>
    /// 方形引线：选择一根或多根 MEP（本图预选 / LINK 框选），按整组外包盒生成闭合矩形框 + 两段式引线。
    /// u = 管线走向，v = 平面内垂直于走向；长边 L=D+2EQ 沿 v 包住整排，短边 W 沿 u 为总窄带宽。
    /// 引线接垂直于管线的侧边：先点 P1 生成 P3→P1 桩线预览，再点 P2 生成 P1→P2 尾线。
    /// 配置（MEPConfig）：
    ///   Leader.W          沿走向的总窄带宽（mm），默认 50
    ///   Leader.EQ         垂直方向两端留白（mm），默认 50
    ///   Leader.LineStyle  详图线样式名称（Projection 线样式），留空 = 默认线样式
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class BoxLeaderCmd : IExternalCommand
    {
        // 平面投影方向退化容差（内部单位，英尺）：投影长度小于此值视为立管
        private const double VerticalDegenerateTol = 1e-6;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;
            View activeView = doc.ActiveView;

            MEPLogger.Instance.StartSession("方形引线");

            try
            {
                // 1. 读取配置
                double wMm = MEPConfig.GetDouble("Leader.W", 50.0);
                double eqMm = MEPConfig.GetDouble("Leader.EQ", 50.0);
                string lineStyleName = MEPConfig.GetString("Leader.LineStyle", "");
                double wFt = UnitUtils.ConvertToInternalUnits(Math.Max(0, wMm), UnitTypeId.Millimeters);
                double eqFt = UnitUtils.ConvertToInternalUnits(Math.Max(0, eqMm), UnitTypeId.Millimeters);
                MEPLogger.Instance.AppendLine($"[配置] W={wMm:F1} mm, EQ={eqMm:F1} mm, LineStyle=\"{lineStyleName}\"");

                // 2. 取参考 MEP 整组：预选本图（可多选）；无预选则框选 LINK 内多个 MEP
                List<RefItem> items = PickReferences(uiDoc, doc);
                if (items.Count == 0)
                {
                    MEPLogger.Instance.AppendLine("[退出] 未选择有效 MEP 元素。");
                    MEPLogger.Instance.EndSession("已取消：未选择有效 MEP 元素。");
                    return Result.Cancelled;
                }

                MEPLogger.Instance.AppendLine(
                    $"[参考] 共 {items.Count} 个 MEP 元素：" +
                    string.Join("，", items.Select(i => $"{i.Element.GetType().Name} Id={i.Element.Id.IntegerValue}{(i.IsLink ? "(LINK)" : "")}")));

                // 3. 解析管线方向 u（以第一根有效水平/斜向管线为准）、法向 v；全部为立管则退出
                if (!TryGetAxes(items, activeView, out XYZ u, out XYZ v))
                {
                    InfoCenterTip.Show("所选均为立管（竖向管线），不支持方形引线，请选择水平/斜向管线。");
                    MEPLogger.Instance.EndSession("已取消：立管方向退化。");
                    return Result.Cancelled;
                }
                MEPLogger.Instance.AppendLine(
                    $"[方向] u=({FmtAxis(u)}), v=({FmtAxis(v)})");

                // 4. 整组外包盒：
                //    D 取垂直管线(v)方向的整组总宽；长边 L = D + 2·EQ（沿 v 包住整排）；
                //    W 为沿管线(u)方向的总窄带宽，接引线侧边过 P1，另一边向 MEP 中心偏 W。
                GetUnionExtent(items, u, v, out double uMin, out double uMax,
                    out double vMin, out double vMax, out XYZ groupCenter);

                double dFt = vMax - vMin;
                if (dFt <= 1e-9) dFt = GetMaxSectionSize(items); // 包围盒退化时按截面尺寸兜底
                double lFt = dFt + 2.0 * eqFt;
                double halfV = lFt * 0.5;

                if (lFt <= 1e-9)
                {
                    InfoCenterTip.Show("参考元素尺寸计算失败，请换一组 MEP 重试。");
                    MEPLogger.Instance.AppendLine($"[退出] 垂直方向尺寸为 0，D={ToMM(dFt):F1} mm。");
                    MEPLogger.Instance.EndSession("已取消：管线尺寸为 0。");
                    return Result.Cancelled;
                }

                double groupCu = groupCenter.DotProduct(u);
                double groupCv = groupCenter.DotProduct(v);
                MEPLogger.Instance.AppendLine(
                    $"[尺寸] D(v向整组总宽)={ToMM(dFt):F1} mm, L={ToMM(lFt):F1} mm, " +
                    $"框半长v={ToMM(halfV):F1} mm, 窄带宽W={wMm:F1} mm");
                MEPLogger.Instance.AppendLine($"[尺寸] 整组中心={FmtXYZ(groupCenter)}");

                // 5. 先点 P1（P1 保持不动）：接引线的侧边过 P1 的 u，再向 MEP 中心方向偏移 W 得到另一边
                XYZ p1;
                try
                {
                    p1 = uiDoc.Selection.PickPoint("请点击引线落点 P1");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    MEPLogger.Instance.EndSession("已取消：用户未点 P1。");
                    return Result.Cancelled;
                }

                double p1U = p1.DotProduct(u);
                double p1V = p1.DotProduct(v);
                bool sideNegU = (p1U - groupCu) <= 0;

                double vLo = groupCv - halfV;
                double vHi = groupCv + halfV;
                double uAttach = p1U;                                   // 接引线侧边的 u（过 P1）
                double uOther = sideNegU ? p1U + wFt : p1U - wFt;      // 另一侧边，向 MEP 中心偏 W

                // 由 (u 标量, v 标量) 还原到世界平面坐标
                XYZ Pt(double su, double sv) =>
                    groupCenter + u.Multiply(su - groupCu) + v.Multiply(sv - groupCv);

                XYZ attachLo = Pt(uAttach, vLo);
                XYZ attachHi = Pt(uAttach, vHi);
                XYZ otherLo = Pt(uOther, vLo);
                XYZ otherHi = Pt(uOther, vHi);

                // P3：接引线侧边上靠近 P1 v 向的框角；P3 与 P1 同 u，桩线沿 v 垂直 MEP（单段）
                XYZ p3 = p1V >= groupCv ? attachHi : attachLo;

                MEPLogger.Instance.AppendLine(
                    $"[点位] P1={FmtXYZ(p1)} 侧边={(sideNegU ? "-u" : "+u")} P3={FmtXYZ(p3)}");
                MEPLogger.Instance.AppendLine(
                    $"[框角] 侧边(attach) {FmtXYZ(attachLo)} ~ {FmtXYZ(attachHi)}；" +
                    $"另一边(other) {FmtXYZ(otherLo)} ~ {FmtXYZ(otherHi)}");

                // 6. 解析线样式（找不到回退默认）
                GraphicsStyle lineStyle = ResolveLineStyle(doc, lineStyleName);
                double planeZ = GetViewPlaneZ(activeView);

                // 事务一：方框 4 边 + 引线桩线 P3→P1（单段，垂直 MEP），先提交给用户看效果
                int created = 0;
                using (Transaction t = new Transaction(doc, "方形引线-框与引线桩"))
                {
                    t.Start();

                    created += CreateDetailCurve(doc, activeView, planeZ, "框边下横", attachLo, otherLo, lineStyle);
                    created += CreateDetailCurve(doc, activeView, planeZ, "框边侧边other", otherLo, otherHi, lineStyle);
                    created += CreateDetailCurve(doc, activeView, planeZ, "框边上横", attachHi, otherHi, lineStyle);
                    created += CreateDetailCurve(doc, activeView, planeZ, "框边侧边attach", attachLo, attachHi, lineStyle);
                    created += CreateDetailCurve(doc, activeView, planeZ, "引线P3-P1", p3, p1, lineStyle);

                    t.Commit();
                }

                // 7. 再点 P2：生成尾线段 P1→P2；用户取消则保留已生成的框与 stub
                XYZ p2;
                bool tailCreated = false;
                try
                {
                    p2 = uiDoc.Selection.PickPoint("请点击引线外端点 P2");
                    using (Transaction t2 = new Transaction(doc, "方形引线-尾线"))
                    {
                        t2.Start();
                        created += CreateDetailCurve(doc, activeView, planeZ, "引线P1-P2", p1, p2, lineStyle);
                        t2.Commit();
                        tailCreated = true;
                    }
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    MEPLogger.Instance.AppendLine("[提示] 未点 P2，尾线段 P1-P2 未生成。");
                }

                MEPLogger.Instance.AppendLine(
                    $"[完成] 共生成 {created} 段详图线（尾线{(tailCreated ? "已生成" : "未生成")}）。");
                MEPLogger.Instance.Flush();
                string logName = System.IO.Path.GetFileName(MEPLogger.Instance.LogFilePath);
                InfoCenterTip.Show($"方形引线已生成（{created} 段）。日志：{logName}");
                MEPLogger.Instance.EndSession("完成。");
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                MEPLogger.Instance.AppendLine("[取消] 用户终止操作。");
                MEPLogger.Instance.Flush();
                MEPLogger.Instance.EndSession("已取消。");
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("BoxLeaderCmd.Execute", ex);
                InfoCenterTip.Show("方形引线生成失败，请查看日志。");
                MEPLogger.Instance.EndSession("失败：" + ex.Message);
                message = ex.Message;
                return Result.Failed;
            }
        }

        // ======================= 选择 =======================

        private class RefItem
        {
            public Element Element { get; set; }
            public Transform LinkTransform { get; set; }
            public bool IsLink { get; set; }
        }

        private class LinkMEPFilter : ISelectionFilter
        {
            private readonly Document _doc;
            public LinkMEPFilter(Document doc) { _doc = doc; }

            public bool AllowElement(Element elem) => elem is RevitLinkInstance;
            public bool AllowReference(Reference reference, XYZ position)
            {
                var inst = _doc.GetElement(reference.ElementId) as RevitLinkInstance;
                var linkDoc = inst?.GetLinkDocument();
                var elem = linkDoc?.GetElement(reference.LinkedElementId);
                return IsLocalMep(elem);
            }
        }

        private static bool IsLocalMep(Element elem)
        {
            return elem is Pipe || elem is Duct || elem is CableTray;
        }

        private List<RefItem> PickReferences(UIDocument uiDoc, Document doc)
        {
            var result = new List<RefItem>();

            // ① 预选：仅取本图 MEP（可多选）
            ICollection<ElementId> selectedIds = uiDoc.Selection.GetElementIds();
            if (selectedIds != null && selectedIds.Count > 0)
            {
                foreach (var id in selectedIds)
                {
                    Element elem = doc.GetElement(id);
                    if (IsLocalMep(elem))
                    {
                        result.Add(new RefItem { Element = elem, LinkTransform = null, IsLink = false });
                    }
                }
                if (result.Count > 0) return result;
            }

            // ② 无预选：框选 LINK 内多个 MEP，空格键结束（沿用共享 SpaceKeyFinisher）
            InfoCenterTip.Show("请框选 LINK 文件中的多个 MEP 元素，按空格结束。");
            try
            {
                using (var finisher = new SpaceKeyFinisher())
                {
                    finisher.Enable();
                    IList<Reference> refs = uiDoc.Selection.PickObjects(
                        ObjectType.LinkedElement, new LinkMEPFilter(doc), "请框选 LINK 中的 MEP 元素，按空格结束");

                    foreach (Reference reference in refs)
                    {
                        RevitLinkInstance inst = doc.GetElement(reference.ElementId) as RevitLinkInstance;
                        if (inst == null || reference.LinkedElementId == ElementId.InvalidElementId) continue;

                        Document linkDoc = inst.GetLinkDocument();
                        Element linkedElem = linkDoc?.GetElement(reference.LinkedElementId);
                        if (!IsLocalMep(linkedElem)) continue;

                        Transform tf = inst.GetTotalTransform();
                        result.Add(new RefItem { Element = linkedElem, LinkTransform = tf, IsLink = true });
                    }
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return new List<RefItem>();
            }

            return result;
        }

        // ======================= 几何解析 =======================

        /// <summary>
        /// 以第一根有效水平/斜向管线为准，取平面投影方向 u、视图平面内法向 v。
        /// v 必须落在视图平面内：用 u × ViewDirection（叉乘结果同时垂直于 u 和视线方向，即在平面内），
        /// 不能用 viewUp × u（viewUp 本身在平面内，叉乘会跑到平面外的标高方向）。
        /// 全部为立管/无方向曲线时返回 false。
        /// </summary>
        private static bool TryGetAxes(List<RefItem> items, View view, out XYZ u, out XYZ v)
        {
            u = XYZ.Zero;
            v = XYZ.Zero;

            foreach (var item in items)
            {
                if (!(item.Element.Location is LocationCurve lc)) continue;

                XYZ start = lc.Curve.GetEndPoint(0);
                XYZ end = lc.Curve.GetEndPoint(1);
                if (item.LinkTransform != null)
                {
                    start = item.LinkTransform.OfPoint(start);
                    end = item.LinkTransform.OfPoint(end);
                }

                XYZ planar = new XYZ(end.X - start.X, end.Y - start.Y, 0.0);
                if (planar.GetLength() < VerticalDegenerateTol) continue; // 立管，跳过取下一根

                u = planar.Normalize();

                // 视图平面内的法向：u × 视线方向（结果在平面内且垂直于 u）
                XYZ viewDir = view.ViewDirection;
                v = u.CrossProduct(viewDir);
                if (v.GetLength() < VerticalDegenerateTol) v = view.UpDirection.CrossProduct(u);
                if (v.GetLength() < VerticalDegenerateTol) return false;
                v = v.Normalize();
                return true;
            }

            return false;
        }

        /// <summary>
        /// 整组外包盒：把每个元素包围盒角点（先套 bbox.Transform，再套 linkTf 变换到世界坐标）
        /// 投影到 u / v 取极值；同时累加角点 XY 质心作为整组中心。
        /// 包围盒取不到的元素，用截面尺寸按中心外扩半个截面兜底。
        /// </summary>
        private static void GetUnionExtent(List<RefItem> items, XYZ u, XYZ v,
            out double uMin, out double uMax, out double vMin, out double vMax, out XYZ center)
        {
            uMin = double.MaxValue; uMax = double.MinValue;
            vMin = double.MaxValue; vMax = double.MinValue;
            double cx = 0.0, cy = 0.0;
            int cornerCount = 0;
            bool any = false;

            foreach (var item in items)
            {
                List<XYZ> corners = GetWorldCorners(item);
                if (corners.Count == 0) continue;

                foreach (XYZ c in corners)
                {
                    double pu = c.DotProduct(u);
                    double pv = c.DotProduct(v);
                    if (pu < uMin) uMin = pu;
                    if (pu > uMax) uMax = pu;
                    if (pv < vMin) vMin = pv;
                    if (pv > vMax) vMax = pv;

                    cx += c.X;
                    cy += c.Y;
                    cornerCount++;
                    any = true;
                }
            }

            if (!any)
            {
                uMin = uMax = vMin = vMax = 0.0;
                center = XYZ.Zero;
                return;
            }

            center = new XYZ(cx / cornerCount, cy / cornerCount, 0.0);
        }

        private static List<XYZ> GetWorldCorners(RefItem item)
        {
            var corners = new List<XYZ>();
            Transform tf = item.LinkTransform;

            BoundingBoxXYZ bb = item.Element.get_BoundingBox(null);
            if (bb != null)
            {
                Transform bbTf = bb.Transform ?? Transform.Identity;
                XYZ min = bbTf.OfPoint(bb.Min);
                XYZ max = bbTf.OfPoint(bb.Max);
                if (tf != null)
                {
                    min = tf.OfPoint(min);
                    max = tf.OfPoint(max);
                }

                corners.Add(new XYZ(min.X, min.Y, min.Z));
                corners.Add(new XYZ(max.X, min.Y, min.Z));
                corners.Add(new XYZ(min.X, max.Y, min.Z));
                corners.Add(new XYZ(max.X, max.Y, min.Z));
                corners.Add(new XYZ(min.X, min.Y, max.Z));
                corners.Add(new XYZ(max.X, min.Y, max.Z));
                corners.Add(new XYZ(min.X, max.Y, max.Z));
                corners.Add(new XYZ(max.X, max.Y, max.Z));
                return corners;
            }

            // 兜底：取位置中心，按截面尺寸外扩半个截面作为角点
            XYZ center = GetWorldCenter(item);
            double section = GetElementSectionSize(item.Element);
            if (center == null || section <= 1e-9) return corners;

            double half = section * 0.5;
            corners.Add(center + new XYZ(half, half, 0));
            corners.Add(center + new XYZ(-half, half, 0));
            corners.Add(center + new XYZ(half, -half, 0));
            corners.Add(center + new XYZ(-half, -half, 0));
            return corners;
        }

        private static XYZ GetWorldCenter(RefItem item)
        {
            XYZ center = XYZ.Zero;
            if (item.Element.Location is LocationCurve lc)
            {
                center = lc.Curve.Evaluate(0.5, true);
            }
            else if (item.Element.Location is LocationPoint lp)
            {
                center = lp.Point;
            }
            if (item.LinkTransform != null) center = item.LinkTransform.OfPoint(center);
            return center;
        }

        private static double GetElementSectionSize(Element elem)
        {
            if (elem is Pipe pipe) return pipe.Diameter;
            if (elem is Duct duct) return Math.Min(duct.Width, duct.Height);
            if (elem is CableTray tray) return Math.Min(tray.Width, tray.Height);
            return 0.0;
        }

        /// <summary>包围盒退化时：取整组最大截面尺寸作为垂直方向兜底宽度。</summary>
        private static double GetMaxSectionSize(List<RefItem> items)
        {
            double max = 0.0;
            foreach (var it in items)
            {
                double s = GetElementSectionSize(it.Element);
                if (s > max) max = s;
            }
            return max;
        }

        // ======================= 线样式与建线 =======================

        private static GraphicsStyle ResolveLineStyle(Document doc, string styleName)
        {
            if (string.IsNullOrWhiteSpace(styleName)) return null;

            // 按名称匹配 Projection 类型的线样式（GraphicsStyle）；Cut 为剖切线，不参与匹配
            foreach (GraphicsStyle gs in new FilteredElementCollector(doc)
                .OfClass(typeof(GraphicsStyle))
                .Cast<GraphicsStyle>())
            {
                if (gs.GraphicsStyleType != GraphicsStyleType.Projection) continue;
                if (string.Equals(gs.Name, styleName.Trim(), StringComparison.OrdinalIgnoreCase))
                    return gs;
            }

            MEPLogger.Instance.AppendLine($"[警告] 线样式 \"{styleName}\" 未找到，回退默认线样式。");
            return null;
        }

        /// <summary>取当前视图平面标高：平面视图取标高标高，其余视图退回视图原点 Z。</summary>
        private static double GetViewPlaneZ(View view)
        {
            if (view is ViewPlan vp && vp.GenLevel != null) return vp.GenLevel.Elevation;
            return view.Origin.Z;
        }

        private static int CreateDetailCurve(Document doc, View view, double planeZ, string label,
            XYZ p1, XYZ p2, GraphicsStyle lineStyle)
        {
            // 详图线必须落在视图平面内：统一贴到平面 Z，避免 "Curve must be in the plane"
            XYZ a = new XYZ(p1.X, p1.Y, planeZ);
            XYZ b = new XYZ(p2.X, p2.Y, planeZ);

            double len = a.DistanceTo(b);
            if (len < 1e-9)
            {
                MEPLogger.Instance.AppendLine($"[跳过] {label}：两端点重合，长度 {ToMM(len):F3} mm。");
                return 0;
            }

            try
            {
                Line line = Line.CreateBound(a, b);
                DetailCurve dc = doc.Create.NewDetailCurve(view, line);

                if (lineStyle != null)
                {
                    try { dc.LineStyle = lineStyle; }
                    catch (Exception ex)
                    {
                        MEPLogger.Instance.AppendLine($"[警告] {label} 应用线样式失败：{ex.Message}");
                    }
                }

                MEPLogger.Instance.AppendLine($"[建线] {label}，长度 {ToMM(len):F1} mm。");
                return 1;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.AppendLine($"[错误] {label} 创建失败：{ex.Message}");
                MEPLogger.Instance.LogException("CreateDetailCurve." + label, ex);
                return 0;
            }
        }

        // ======================= 日志格式化 =======================

        private static string FmtXYZ(XYZ p)
        {
            if (p == null) return "(null)";
            double x = UnitUtils.ConvertFromInternalUnits(p.X, UnitTypeId.Millimeters);
            double y = UnitUtils.ConvertFromInternalUnits(p.Y, UnitTypeId.Millimeters);
            double z = UnitUtils.ConvertFromInternalUnits(p.Z, UnitTypeId.Millimeters);
            return $"({x:F1}, {y:F1}, {z:F1}) mm";
        }

        private static string FmtAxis(XYZ v)
        {
            if (v == null) return "(null)";
            return $"{v.X:F3}, {v.Y:F3}, {v.Z:F3}";
        }

        private static double ToMM(double internalValue)
        {
            return UnitUtils.ConvertFromInternalUnits(internalValue, UnitTypeId.Millimeters);
        }
    }
}
