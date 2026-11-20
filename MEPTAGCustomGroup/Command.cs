using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using MEPTools.Shared;

namespace MEPTools.MEPGroupTag
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Command : IExternalCommand
    {
        // 行聚类容差：SortUp 差 <= 此值视为"同一视觉行"（仅消抖，不误合并不同行）
        private const double SortRowToleranceMM = 5.0;

        // 记录本次命令生成的所有 Tag，用于事务提交后统一置为选中
        private readonly List<ElementId> _createdTagIds = new List<ElementId>();

        private class LinkMEPSelectionFilter : ISelectionFilter
        {
            private readonly Document _doc;
            public LinkMEPSelectionFilter(Document doc) { _doc = doc; }
            public bool AllowElement(Element elem) => elem is RevitLinkInstance;
            public bool AllowReference(Reference reference, XYZ position)
            {
                var inst = _doc.GetElement(reference.ElementId) as RevitLinkInstance;
                var linkDoc = inst?.GetLinkDocument();
                var elem = linkDoc?.GetElement(reference.LinkedElementId);
                return elem is Pipe || elem is Duct || elem is CableTray;
            }
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;
            View activeView = doc.ActiveView;

            // 清空上一次的记录，防止重复执行时残留
            _createdTagIds.Clear();

            MEPLogger.Instance.StartSession("MEP高度分组排版带T1");

            GroupTagConfig config = GroupTagConfig.Load();

            List<ElemInfo> selectedData = GetSelectedMEPElements(uiDoc);
            if (selectedData.Count < 1) return Result.Cancelled;

            List<DebugMepGroup> groups = GroupByBottomElevation(selectedData, config.GroupToleranceMM);
            MEPLogger.Instance.AppendLine($"[日志] 共分为 {groups.Count} 组。");

            // 先收集文档内全部可用 Tag 族（族类型键：族名: 类型名），再按配置解析各组共用的主行族
            var swPrep = Stopwatch.StartNew();
            var allTagSymbols = new Dictionary<string, FamilySymbol>();
            // 只对三个 Tag 类别各做一次"类别+类"的快速过滤收集，
            // 不再先枚举全文档所有 FamilySymbol 再用 LINQ 过滤（大模型里这一步原本要 20 ms 以上）
            BuiltInCategory[] tagCats =
            {
                BuiltInCategory.OST_DuctTags,
                BuiltInCategory.OST_PipeTags,
                BuiltInCategory.OST_CableTrayTags
            };
            foreach (BuiltInCategory bic in tagCats)
            {
                var syms = new FilteredElementCollector(doc)
                    .OfCategory(bic)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>();

                foreach (FamilySymbol sym in syms)
                {
                    string key = $"{sym.Family.Name}: {sym.Name}";
                    if (!allTagSymbols.ContainsKey(key)) allTagSymbols.Add(key, sym);
                }
            }

            // 主行 Tag 族：优先取配置（TagGroup.MainDuctTag / MainPipeTag / MainTrayTag），配置留空才回退到视图内 Tag 类型
            FamilySymbol ductTagSym = ResolveMainTagSymbol(doc, activeView, allTagSymbols, config.MainDuctTagSymName, BuiltInCategory.OST_DuctTags, "DUCT");
            FamilySymbol trayTagSym = ResolveMainTagSymbol(doc, activeView, allTagSymbols, config.MainTrayTagSymName, BuiltInCategory.OST_CableTrayTags, "CABLE TRAY");
            FamilySymbol pipeTagSym = ResolveMainTagSymbol(doc, activeView, allTagSymbols, config.MainPipeTagSymName, BuiltInCategory.OST_PipeTags, "PIPE");
            swPrep.Stop();
            MEPLogger.Instance.AppendLine($"  [计时] 族类型收集 + 主行族解析 = {swPrep.ElapsedMilliseconds} ms（共 {allTagSymbols.Count} 个 Tag 族类型）");

            XYZ basePickPt;
            try
            {
                basePickPt = uiDoc.Selection.PickPoint("请点击 Tag 清单的左上角起始位置");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }

            if (activeView is ViewPlan viewPlan && viewPlan.GenLevel != null)
                basePickPt = new XYZ(basePickPt.X, basePickPt.Y, viewPlan.GenLevel.Elevation);

            MEPLogger.Instance.AppendLine("");
            MEPLogger.Instance.AppendLine("========== 全局参数 ==========");
            MEPLogger.Instance.AppendLine($"  起始点 basePickPt = {FmtXYZ(basePickPt)}");
            MEPLogger.Instance.AppendLine($"  分组容差 = {config.GroupToleranceMM:F1} mm");
            MEPLogger.Instance.AppendLine($"  行距 = {config.LineSpacingMM:F1} mm, 组间空行 = {config.BlankLines}");
            MEPLogger.Instance.AppendLine($"  排序方案 = 自动识别方向：");
            MEPLogger.Instance.AppendLine($"     类A = 纯垂直 + 右斜 \\");
            MEPLogger.Instance.AppendLine($"     类B = 左斜 / + 纯水平");
            MEPLogger.Instance.AppendLine($"     类A > 类B → SortRight 升序（从左到右）");
            MEPLogger.Instance.AppendLine($"     类A <= 类B → SortUp 降序（从上到下）+ 行内 SortRight 升序");
            MEPLogger.Instance.AppendLine($"  行聚类容差 = {SortRowToleranceMM:F1} mm");
            MEPLogger.Instance.AppendLine($"  视图 Up    = {FmtXYZ(activeView.UpDirection)}");
            MEPLogger.Instance.AppendLine($"  视图 Right = {FmtXYZ(activeView.RightDirection)}");
            MEPLogger.Instance.AppendLine("=============================");

            using (Transaction t = new Transaction(doc, "带T1分组排版"))
            {
                t.Start();

                // 计时插桩（仅写日志，不影响任何流程）：用来定位"点了起始点后卡一下"到底花在哪一步
                var swTotal = Stopwatch.StartNew();
                var swSort = new Stopwatch();      // 分组内排序（含端点投影）
                var swLog = new Stopwatch();       // 写调试日志
                var swCreate = new Stopwatch();    // 建 Tag 段（含每行日志）
                double pureCreateMs = 0;           // 纯 IndependentTag.Create 累计耗时
                int taggedCount = 0;

                double currentY = basePickPt.Y;
                double lineSpacingFt = UnitUtils.ConvertToInternalUnits(config.LineSpacingMM, UnitTypeId.Millimeters);

                groups = groups.OrderByDescending(g => g.BaseElevation).ToList();

                XYZ viewUp = activeView.UpDirection;
                XYZ viewRight = activeView.RightDirection;
                double sortRowTolInternal = UnitUtils.ConvertToInternalUnits(SortRowToleranceMM, UnitTypeId.Millimeters);

                for (int i = 0; i < groups.Count; i++)
                {
                    var group = groups[i];
                    int groupIndex = i + 1;

                    swSort.Start();
                    var sortResult = SortElementsForTagging(group.Elements, viewUp, viewRight, sortRowTolInternal);
                    swSort.Stop();
                    var sortedItems = sortResult.Items;

                    swLog.Start();
                    WriteGroupHeaderLog(groupIndex, groups.Count, group, sortResult);
                    WriteSortLog(sortedItems, sortResult);
                    swLog.Stop();

                    swCreate.Start();
                    var rulesBefore = config.ExtraRules.Where(r => r.GroupIndex == groupIndex.ToString() || r.GroupIndex == "*").ToList();
                    foreach (var rule in rulesBefore)
                    {
                        TryCreateTagForRule(rule, group.Elements, selectedData, allTagSymbols, ductTagSym, pipeTagSym, trayTagSym, doc, activeView, basePickPt.X, ref currentY, lineSpacingFt);
                    }

                    for (int k = 0; k < sortedItems.Count; k++)
                    {
                        var item = sortedItems[k];
                        Element elem = item.Info.Elem;
                        Reference elemRef = item.Info.Ref;

                        FamilySymbol targetSym = null;
                        if (elem is Duct) targetSym = ductTagSym;
                        else if (elem is CableTray) targetSym = trayTagSym;
                        else if (elem is Pipe) targetSym = pipeTagSym;

                        if (targetSym == null)
                        {
                            MEPLogger.Instance.AppendLine($"  #{k + 1} [跳过] {elem.GetType().Name} Id={elem.Id.IntegerValue} 无可用 Tag 族");
                            continue;
                        }
                        if (!targetSym.IsActive) targetSym.Activate();

                        XYZ createPos = new XYZ(basePickPt.X, currentY, basePickPt.Z);
                        var swOne = Stopwatch.StartNew();
                        IndependentTag newTag = IndependentTag.Create(
                            doc, targetSym.Id, activeView.Id, elemRef, false, TagOrientation.Horizontal, createPos);
                        swOne.Stop();
                        pureCreateMs += swOne.Elapsed.TotalMilliseconds;
                        taggedCount++;

                        // 记录生成的 Tag
                        _createdTagIds.Add(newTag.Id);

                        MEPLogger.Instance.AppendLine(
                            $"  #{k + 1} [Tag] {elem.GetType().Name} Id={elem.Id.IntegerValue} " +
                            $"SortUp={ToMM(item.SortUp):F1} SortRight={ToMM(item.SortRight):F1} " +
                            $"→ TagPos={FmtXYZ(createPos)}");

                        currentY -= lineSpacingFt;
                    }

                    var rulesAfter = config.ExtraRules.Where(r => r.GroupIndex == "After_" + groupIndex.ToString() || r.GroupIndex == "*After").ToList();
                    foreach (var rule in rulesAfter)
                    {
                        TryCreateTagForRule(rule, group.Elements, selectedData, allTagSymbols, ductTagSym, pipeTagSym, trayTagSym, doc, activeView, basePickPt.X, ref currentY, lineSpacingFt);
                    }
                    swCreate.Stop();

                    currentY -= (config.BlankLines * lineSpacingFt);
                }

                var swCommit = Stopwatch.StartNew();
                t.Commit();
                swCommit.Stop();
                swTotal.Stop();

                double avgCreateMs = taggedCount > 0 ? pureCreateMs / taggedCount : 0;
                MEPLogger.Instance.AppendLine("");
                MEPLogger.Instance.AppendLine("========== 计时（排查卡顿用） ==========");
                MEPLogger.Instance.AppendLine($"  总耗时        = {swTotal.ElapsedMilliseconds} ms");
                MEPLogger.Instance.AppendLine($"    排序        = {swSort.ElapsedMilliseconds} ms");
                MEPLogger.Instance.AppendLine($"    写调试日志  = {swLog.ElapsedMilliseconds} ms（{groups.Count} 组）");
                MEPLogger.Instance.AppendLine($"    建 Tag 段   = {swCreate.ElapsedMilliseconds} ms（含每行日志，主行共 {taggedCount} 个）");
                MEPLogger.Instance.AppendLine($"      其中纯 IndependentTag.Create（主行）= {pureCreateMs:F1} ms，平均 {avgCreateMs:F1} ms/个");
                MEPLogger.Instance.AppendLine($"    提交事务    = {swCommit.ElapsedMilliseconds} ms");
                if (swCreate.ElapsedMilliseconds > 0 && pureCreateMs < swCreate.ElapsedMilliseconds * 0.5)
                    MEPLogger.Instance.AppendLine($"  [提示] 建 Tag 段里有 {swCreate.ElapsedMilliseconds - pureCreateMs:F0} ms 不属于 Revit 建 Tag（主要是日志/循环开销），可优先从日志减负");
                MEPLogger.Instance.AppendLine("======================================");
            }

            // 事务提交后，将本次生成的所有 Tag 置为选中状态（可配置关闭，省一次高亮刷新）
            var swAfter = Stopwatch.StartNew();
            bool selectTags = MEPConfig.GetBool("TagGroup.SelectCreatedTags", true);
            if (selectTags && _createdTagIds.Count > 0)
            {
                uiDoc.Selection.SetElementIds(_createdTagIds);
            }
            double selMs = swAfter.Elapsed.TotalMilliseconds;

            var swTip = Stopwatch.StartNew();
            InfoCenterTip.Show("排版完成，请查看效果。");
            swTip.Stop();

            MEPLogger.Instance.AppendLine(
                $"  [计时] 提交后：选中 {_createdTagIds.Count} 个 Tag = {selMs:F0} ms（SelectCreatedTags={selectTags}），气泡提示 = {swTip.ElapsedMilliseconds} ms");
            MEPLogger.Instance.EndSession("Debug完毕");
            return Result.Succeeded;
        }

        // ======================= 排序核心 =======================

        private class SortedElem
        {
            public ElemInfo Info { get; set; }
            public XYZ P1 { get; set; }
            public XYZ P2 { get; set; }
            public double Up1 { get; set; }
            public double Up2 { get; set; }
            public double Right1 { get; set; }
            public double Right2 { get; set; }
            public double SortUp => Math.Max(Up1, Up2);
            public double SortRight => Math.Min(Right1, Right2);
            public bool IsNewRow { get; set; }
        }

        public enum GroupSlope { LeftSlant, RightSlant, None }

        private class SortResult
        {
            public GroupSlope Slope { get; set; }
            public string StrategyName { get; set; }
            public List<SortedElem> Items { get; set; }
            public List<SortedElem> RawItems { get; set; }   // 排序前的原始投影数据，仅给日志复用，避免重复计算
            public int PositiveSlopeCount { get; set; }   // 左斜 /
            public int NegativeSlopeCount { get; set; }   // 右斜 \
            public int HorizontalCount { get; set; }      // 水平
            public int VerticalCount { get; set; }        // 垂直
            public int ClassA { get; set; }               // 纯垂直 + 右斜
            public int ClassB { get; set; }               // 左斜 + 纯水平
        }

        private static List<SortedElem> ComputeSortData(List<ElemInfo> elems, XYZ viewUp, XYZ viewRight)
        {
            var list = new List<SortedElem>();
            foreach (var e in elems)
            {
                XYZ p1, p2;
                if (e.Elem.Location is LocationCurve lc)
                {
                    p1 = lc.Curve.GetEndPoint(0);
                    p2 = lc.Curve.GetEndPoint(1);
                    if (e.LinkTransform != null)
                    {
                        p1 = e.LinkTransform.OfPoint(p1);
                        p2 = e.LinkTransform.OfPoint(p2);
                    }
                }
                else
                {
                    p1 = p2 = e.WorldCenter ?? XYZ.Zero;
                }

                list.Add(new SortedElem
                {
                    Info = e,
                    P1 = p1,
                    P2 = p2,
                    Up1 = p1.DotProduct(viewUp),
                    Up2 = p2.DotProduct(viewUp),
                    Right1 = p1.DotProduct(viewRight),
                    Right2 = p2.DotProduct(viewRight)
                });
            }
            return list;
        }

        private static SortResult SortElementsForTagging(
            List<ElemInfo> elems, XYZ viewUp, XYZ viewRight, double rowToleranceInternal)
        {
            var data = ComputeSortData(elems, viewUp, viewRight);
            var result = new SortResult();
            result.RawItems = data;

            int pos = 0, neg = 0, hori = 0, vert = 0;
            foreach (var it in data)
            {
                double dx = Math.Abs(it.Right2 - it.Right1);
                double dy = Math.Abs(it.Up2 - it.Up1);
                const double eps = 1e-6;

                if (dx < eps && dy < eps) continue;      // 退化点，忽略
                if (dy < eps) { hori++; continue; }      // 水平
                if (dx < eps) { vert++; continue; }      // 垂直

                if ((it.Right2 - it.Right1) * (it.Up2 - it.Up1) > 0) pos++;  // 左斜 /
                else neg++;                                                   // 右斜 \
            }

            result.PositiveSlopeCount = pos;
            result.NegativeSlopeCount = neg;
            result.HorizontalCount = hori;
            result.VerticalCount = vert;

            // 新规则：
            //   类A = 纯垂直 + 右斜 \   → 从左到右（SortRight 升序）
            //   类B = 左斜 / + 纯水平    → 从上到下（SortUp 降序 + 行内 SortRight 升序）
            int classA = vert + neg;
            int classB = pos + hori;
            result.ClassA = classA;
            result.ClassB = classB;

            if (classA > classB)
            {
                result.Slope = GroupSlope.RightSlant;
                result.StrategyName = $"类A(垂直+右斜\\={classA}) > 类B(左斜/+水平={classB}) → 从左到右（SortRight 升序）";
                foreach (var it in data) it.IsNewRow = false;
                result.Items = data
                    .OrderBy(x => x.SortRight)
                    .ThenBy(x => x.Info.Elem.Id.IntegerValue)
                    .ToList();
            }
            else
            {
                result.Slope = (pos > 0) ? GroupSlope.LeftSlant : GroupSlope.None;
                result.StrategyName = $"类A(垂直+右斜\\={classA}) <= 类B(左斜/+水平={classB}) → 从上到下（SortUp 降序，行内 SortRight 升序）";
                result.Items = SortByUpDescThenRightAsc(data, rowToleranceInternal);
            }

            return result;
        }

        private static List<SortedElem> SortByUpDescThenRightAsc(List<SortedElem> data, double rowTol)
        {
            var ordered = data.OrderByDescending(x => x.SortUp).ToList();

            var rows = new List<List<SortedElem>>();
            List<SortedElem> cur = null;
            double anchor = 0;
            foreach (var it in ordered)
            {
                if (cur == null || (anchor - it.SortUp) > rowTol)
                {
                    cur = new List<SortedElem>();
                    rows.Add(cur);
                    anchor = it.SortUp;
                }
                cur.Add(it);
            }

            var result = new List<SortedElem>();
            foreach (var row in rows)
            {
                var sortedRow = row.OrderBy(x => x.SortRight).ToList();
                for (int i = 0; i < sortedRow.Count; i++) sortedRow[i].IsNewRow = (i == 0);
                result.AddRange(sortedRow);
            }
            return result;
        }

        // ======================= 调试日志 =======================

        private static string FmtXYZ(XYZ p)
        {
            if (p == null) return "(null)";
            double x = UnitUtils.ConvertFromInternalUnits(p.X, UnitTypeId.Millimeters);
            double y = UnitUtils.ConvertFromInternalUnits(p.Y, UnitTypeId.Millimeters);
            double z = UnitUtils.ConvertFromInternalUnits(p.Z, UnitTypeId.Millimeters);
            return $"({x:F1}, {y:F1}, {z:F1}) mm";
        }

        private static double ToMM(double internalValue)
        {
            return UnitUtils.ConvertFromInternalUnits(internalValue, UnitTypeId.Millimeters);
        }

        private static void WriteGroupHeaderLog(int groupIndex, int totalGroups, DebugMepGroup group, SortResult result)
        {
            MEPLogger.Instance.AppendLine("");
            MEPLogger.Instance.AppendLine($"========== 组 {groupIndex}/{totalGroups} ==========");
            MEPLogger.Instance.AppendLine($"  基准标高 = {ToMM(group.BaseElevation):F1} mm");
            MEPLogger.Instance.AppendLine($"  元素数   = {group.Elements.Count}");
            MEPLogger.Instance.AppendLine($"  方向统计 = 左斜:{result.PositiveSlopeCount} 右斜:{result.NegativeSlopeCount} 水平:{result.HorizontalCount} 垂直:{result.VerticalCount}");
            MEPLogger.Instance.AppendLine($"  分类计数 = 类A(垂直+右斜)={result.ClassA}  类B(左斜+水平)={result.ClassB}");
            MEPLogger.Instance.AppendLine($"  采用策略 = {result.StrategyName}");
        }

        private static void WriteSortLog(
            List<SortedElem> sortedItems, SortResult result)
        {
            MEPLogger.Instance.AppendLine("  --- 排序前（分组原始顺序，含端点投影） ---");
            // 复用排序阶段已算好的投影数据（原来这里又对每个元素重算一次 LocationCurve 端点）
            var raw = result.RawItems ?? new List<SortedElem>();
            foreach (var it in raw)
            {
                MEPLogger.Instance.AppendLine(
                    $"    [{it.Info.Elem.GetType().Name}] Id={it.Info.Elem.Id.IntegerValue} " +
                    $"P1={FmtXYZ(it.P1)} P2={FmtXYZ(it.P2)} " +
                    $"Up1={ToMM(it.Up1):F1} Up2={ToMM(it.Up2):F1} | " +
                    $"Right1={ToMM(it.Right1):F1} Right2={ToMM(it.Right2):F1} | " +
                    $"SortUp={ToMM(it.SortUp):F1} SortRight={ToMM(it.SortRight):F1}");
            }

            MEPLogger.Instance.AppendLine($"  --- 排序后（{result.StrategyName}） ---");
            for (int k = 0; k < sortedItems.Count; k++)
            {
                var it = sortedItems[k];
                string rowMark = (it.IsNewRow && k > 0) ? "    <<< 新行" : "";
                MEPLogger.Instance.AppendLine(
                    $"    #{k + 1} [{it.Info.Elem.GetType().Name}] Id={it.Info.Elem.Id.IntegerValue} " +
                    $"SortUp={ToMM(it.SortUp):F1} SortRight={ToMM(it.SortRight):F1}{rowMark}");
            }
        }

        // ============================================================

        private bool TryCreateTagForRule(ExtraRowRule rule, List<ElemInfo> groupElems, List<ElemInfo> selectedData, Dictionary<string, FamilySymbol> allTagSymbols,
            FamilySymbol mainDuctSym, FamilySymbol mainPipeSym, FamilySymbol mainTraySym,
            Document doc, View activeView, double baseX, ref double currentY, double lineSpacingFt)
        {
            Element hostElem = null;
            FamilySymbol targetSym = null;

            var ductHost = groupElems.FirstOrDefault(e => e.Elem is Duct);
            if (ductHost != null)
            {
                hostElem = ductHost.Elem;
                targetSym = ResolveRuleTagSymbol(allTagSymbols, rule.DuctTagSymName, mainDuctSym, BuiltInCategory.OST_DuctTags, rule.TagText);
            }

            if (hostElem == null || targetSym == null)
            {
                var pipeHost = groupElems.FirstOrDefault(e => e.Elem is Pipe);
                if (pipeHost != null)
                {
                    hostElem = pipeHost.Elem;
                    targetSym = ResolveRuleTagSymbol(allTagSymbols, rule.PipeTagSymName, mainPipeSym, BuiltInCategory.OST_PipeTags, rule.TagText);
                }
            }

            if (hostElem == null || targetSym == null)
            {
                var trayHost = groupElems.FirstOrDefault(e => e.Elem is CableTray);
                if (trayHost != null)
                {
                    hostElem = trayHost.Elem;
                    targetSym = ResolveRuleTagSymbol(allTagSymbols, rule.CableTrayTagSymName, mainTraySym, BuiltInCategory.OST_CableTrayTags, rule.TagText);
                }
            }

            if (hostElem == null || targetSym == null)
            {
                MEPLogger.Instance.AppendLine($"  [规则] \"{rule.TagText}\" 跳过：未找到宿主或无可用 Tag 族");
                return false;
            }

            var refPair = selectedData.FirstOrDefault(d => d.Elem.Id == hostElem.Id);
            if (refPair.Ref == null)
            {
                MEPLogger.Instance.AppendLine($"  [规则] \"{rule.TagText}\" 跳过：宿主 {hostElem.GetType().Name} Id={hostElem.Id.IntegerValue} 无 Reference");
                return false;
            }

            XYZ createPos = new XYZ(baseX, currentY, doc.ActiveView.GenLevel != null ? doc.ActiveView.GenLevel.Elevation : 0);
            CreateTag(targetSym, refPair.Ref, rule.TagText, doc, activeView, baseX, currentY);
            MEPLogger.Instance.AppendLine(
                $"  [规则] \"{rule.TagText}\" 宿主 {hostElem.GetType().Name} Id={hostElem.Id.IntegerValue} " +
                $"→ TagPos={FmtXYZ(createPos)}");

            currentY -= lineSpacingFt;
            return true;
        }

        private void CreateTag(FamilySymbol sym, Reference hostRef, string text, Document doc, View activeView, double x, double y)
        {
            if (!sym.IsActive) sym.Activate();
            XYZ createPos = new XYZ(x, y, doc.ActiveView.GenLevel != null ? doc.ActiveView.GenLevel.Elevation : 0);

            IndependentTag newTag = IndependentTag.Create(
                doc, sym.Id, activeView.Id, hostRef, false, TagOrientation.Horizontal, createPos);

            // 记录生成的 Tag
            _createdTagIds.Add(newTag.Id);

            Parameter p = newTag.LookupParameter("T TO B") ?? 
                          newTag.LookupParameter("Tag") ?? 
                          newTag.LookupParameter("标记") ??
                          newTag.LookupParameter("标签") ??
                          newTag.LookupParameter("文字");

            if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String)
                p.Set(text);
        }

        // ======================= Tag 族解析 =======================

        // 主行（所有高度组共用）Tag 族解析顺序：
        //   ① 配置项（族名: 类型名）命中且类别匹配 → 用配置
        //   ② 配置留空或匹配失败 → 沿用当前视图内已有同类别 Tag 的类型（取最新放置的那个）
        //   ③ 视图内也没有 → 文档内首个同类别族类型（仅保底，不中断功能）
        private FamilySymbol ResolveMainTagSymbol(Document doc, View activeView, Dictionary<string, FamilySymbol> allTagSymbols,
            string configuredName, BuiltInCategory cat, string label)
        {
            if (!string.IsNullOrWhiteSpace(configuredName))
            {
                string note;
                FamilySymbol fromConfig = FindTagSymbolByName(allTagSymbols, configuredName, cat, out note);
                if (fromConfig != null)
                {
                    MEPLogger.Instance.AppendLine($"  [族] {label} 主行族 = {fromConfig.Family.Name}: {fromConfig.Name}（来源：配置文件{note}）");
                    return fromConfig;
                }
                MEPLogger.Instance.AppendLine($"  [族][警告] {label} 配置值 \"{configuredName}\" 未匹配到同类别 Tag 族，回退到视图内 Tag 类型。");
            }

            FamilySymbol fromView = GetViewTagSymbol(doc, activeView, cat);
            if (fromView != null)
            {
                MEPLogger.Instance.AppendLine(
                    $"  [族] {label} 主行族 = {fromView.Family.Name}: {fromView.Name}（来源：视图内已有 Tag 类型，配置未指定）");
                return fromView;
            }

            FamilySymbol fallback = allTagSymbols.Values.FirstOrDefault(s => IsTagCategory(s, cat));
            if (fallback != null)
            {
                MEPLogger.Instance.AppendLine(
                    $"  [族] {label} 主行族 = {fallback.Family.Name}: {fallback.Name}（来源：兜底-文档内首个同类别族，建议在 TAG排版设置 中指定）");
            }
            else
            {
                MEPLogger.Instance.AppendLine($"  [族][警告] {label} 未找到任何可用的同类别 Tag 族。");
            }
            return fallback;
        }

        // 附加行族：规则指定 → 跟随主行同款族 → 文档内首个同类别族（保底）
        private FamilySymbol ResolveRuleTagSymbol(Dictionary<string, FamilySymbol> allTagSymbols, string ruleName,
            FamilySymbol mainSym, BuiltInCategory cat, string ruleText)
        {
            FamilySymbol sym = null;
            if (!string.IsNullOrWhiteSpace(ruleName))
            {
                string note;
                sym = FindTagSymbolByName(allTagSymbols, ruleName, cat, out note);
                if (sym != null && !string.IsNullOrEmpty(note))
                    MEPLogger.Instance.AppendLine($"  [族] 附加行 \"{ruleText}\" 配置族 \"{ruleName}\" → {sym.Family.Name}: {sym.Name}{note}");
                if (sym == null)
                    MEPLogger.Instance.AppendLine($"  [族][警告] 附加行 \"{ruleText}\" 指定的 \"{ruleName}\" 未匹配到同类别 Tag 族，改跟随主行族。");
            }
            if (sym == null) sym = mainSym;
            if (sym == null) sym = allTagSymbols.Values.FirstOrDefault(s => IsTagCategory(s, cat));
            return sym;
        }

        // 按 "族名: 类型名" 找族类型；交由 TagTypeNameMatcher 做宽容匹配（空白/大小写/全角冒号/类型改名）
        private static FamilySymbol FindTagSymbolByName(Dictionary<string, FamilySymbol> allTagSymbols, string name,
            BuiltInCategory cat, out string matchNote)
        {
            matchNote = "";
            if (allTagSymbols == null || string.IsNullOrWhiteSpace(name)) return null;

            var keys = allTagSymbols.Where(kv => IsTagCategory(kv.Value, cat)).Select(kv => kv.Key).ToList();
            string resolvedKey = TagTypeNameMatcher.Resolve(keys, name, out matchNote);
            if (resolvedKey == null) return null;
            return allTagSymbols[resolvedKey];
        }

        private static bool IsTagCategory(FamilySymbol sym, BuiltInCategory cat)
        {
            return sym != null && sym.Category != null && sym.Category.Id.IntegerValue == (int)cat;
        }

        // 视图内该类别最近放置的一个 Tag 的类型
        private static FamilySymbol GetViewTagSymbol(Document doc, View activeView, BuiltInCategory cat)
        {
            var tags = new FilteredElementCollector(doc, activeView.Id)
                .OfClass(typeof(IndependentTag))
                .Cast<IndependentTag>()
                .Where(t => t.Category != null && t.Category.Id.IntegerValue == (int)cat)
                .OrderByDescending(t => t.Id.IntegerValue);

            foreach (var tag in tags)
            {
                if (tag.GetTypeId() == ElementId.InvalidElementId) continue;
                var sym = doc.GetElement(tag.GetTypeId()) as FamilySymbol;
                if (sym != null && IsTagCategory(sym, cat)) return sym;
            }
            return null;
        }

        private List<ElemInfo> GetSelectedMEPElements(UIDocument uiDoc)
        {
            Document doc = uiDoc.Document;
            List<ElemInfo> result = new List<ElemInfo>();

            ICollection<ElementId> selectedIds = uiDoc.Selection.GetElementIds();
            if (selectedIds != null && selectedIds.Count > 0)
            {
                foreach (var id in selectedIds)
                {
                    Element elem = doc.GetElement(id);
                    if (elem is MEPCurve) 
                    {
                        result.Add(new ElemInfo { 
                            Elem = elem, 
                            Ref = new Reference(elem), 
                            WorldCenter = GetElementCenter(elem, null),
                            LinkTransform = null
                        });
                    }
                }
                if (result.Count > 0) return result;
            }

            InfoCenterTip.Show("请选择 MEP 元素（支持 Link 内部元素）。");
            try
            {
                using (var finisher = new SpaceKeyFinisher())
                {
                    finisher.Enable();
                    IList<Reference> refs = uiDoc.Selection.PickObjects(ObjectType.LinkedElement, new LinkMEPSelectionFilter(doc), "请选择 LINK 中的 MEP 元素，按空格完成");
                    foreach (Reference reference in refs)
                    {
                        RevitLinkInstance inst = doc.GetElement(reference.ElementId) as RevitLinkInstance;
                        if (inst != null && reference.LinkedElementId != ElementId.InvalidElementId)
                        {
                            Document linkDoc = inst.GetLinkDocument();
                            Element linkedElem = linkDoc?.GetElement(reference.LinkedElementId);
                            if (linkedElem is Pipe || linkedElem is Duct || linkedElem is CableTray)
                            {
                                Transform tf = inst.GetTotalTransform();
                                result.Add(new ElemInfo { 
                                    Elem = linkedElem, 
                                    Ref = reference, 
                                    WorldCenter = GetElementCenter(linkedElem, tf),
                                    LinkTransform = tf
                                });
                            }
                        }
                    }
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { }
            return result;
        }

        private XYZ GetElementCenter(Element elem, Transform linkTransform)
        {
            XYZ center = XYZ.Zero;
            if (elem.Location is LocationCurve lc)
            {
                center = lc.Curve.Evaluate(0.5, true);
            }
            else if (elem.Location is LocationPoint lp)
            {
                center = lp.Point;
            }

            if (linkTransform != null) center = linkTransform.OfPoint(center);
            return center;
        }

        private static double GetElementSize(Element elem)
        {
            if (elem is Pipe pipe) return pipe.Diameter;
            if (elem is Duct duct) return Math.Min(duct.Width, duct.Height);
            if (elem is CableTray tray) return Math.Min(tray.Width, tray.Height);
            return 0;
        }

        private static double? GetBottomElevationInternal(Element elem)
        {
            if (elem is Duct) return elem.get_Parameter(BuiltInParameter.RBS_DUCT_BOTTOM_ELEVATION)?.AsDouble();
            if (elem is Pipe) return elem.get_Parameter(BuiltInParameter.RBS_PIPE_BOTTOM_ELEVATION)?.AsDouble();
            if (elem is CableTray) return elem.get_Parameter(BuiltInParameter.RBS_CTC_BOTTOM_ELEVATION)?.AsDouble();
            return null;
        }

        private static List<DebugMepGroup> GroupByBottomElevation(List<ElemInfo> infos, double configToleranceMM)
        {
            double minSizeMM = infos.Select(d => GetElementSize(d.Elem)).Where(s => s > 1e-9)
                .Select(s => UnitUtils.ConvertFromInternalUnits(s, UnitTypeId.Millimeters))
                .DefaultIfEmpty(configToleranceMM).Min();

            double toleranceMM = Math.Min(configToleranceMM, minSizeMM);
            double toleranceInternal = UnitUtils.ConvertToInternalUnits(toleranceMM, UnitTypeId.Millimeters);

            var sorted = infos.OrderBy(d => GetBottomElevationInternal(d.Elem) ?? 0).ToList();
            List<DebugMepGroup> groups = new List<DebugMepGroup>();
            DebugMepGroup current = null;
            double currentBaseElev = 0;

            foreach (var item in sorted)
            {
                double elev = GetBottomElevationInternal(item.Elem) ?? 0;
                if (current == null || (elev - currentBaseElev) > toleranceInternal)
                {
                    current = new DebugMepGroup { BaseElevation = elev };
                    groups.Add(current);
                    currentBaseElev = elev;
                }
                current.Elements.Add(item);
            }
            return groups;
        }
    }

    public class DebugMepGroup
    {
        public List<ElemInfo> Elements { get; } = new List<ElemInfo>();
        public double BaseElevation { get; set; }
    }

    public class ElemInfo
    {
        public Element Elem { get; set; }
        public Reference Ref { get; set; }
        public XYZ WorldCenter { get; set; }
        public Transform LinkTransform { get; set; }
    }
}