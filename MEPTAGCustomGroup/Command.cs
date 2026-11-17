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

namespace MEPTools.MEPGroupTag
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Command : IExternalCommand
    {
        // 行聚类容差：SortUp 差 <= 此值视为"同一视觉行"（仅消抖，不误合并不同行）
        private const double SortRowToleranceMM = 5.0;

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

            MEPLogger.Instance.StartSession("MEP高度分组排版带T1");

            GroupTagConfig config = GroupTagConfig.Load();

            List<ElemInfo> selectedData = GetSelectedMEPElements(uiDoc);
            if (selectedData.Count < 1) return Result.Cancelled;

            List<DebugMepGroup> groups = GroupByBottomElevation(selectedData, config.GroupToleranceMM);
            MEPLogger.Instance.AppendLine($"[日志] 共分为 {groups.Count} 组。");

            FamilySymbol ductTagSym = GetDefaultTagSymbol(doc, activeView, BuiltInCategory.OST_DuctTags);
            FamilySymbol trayTagSym = GetDefaultTagSymbol(doc, activeView, BuiltInCategory.OST_CableTrayTags);
            FamilySymbol pipeTagSym = GetDefaultTagSymbol(doc, activeView, BuiltInCategory.OST_PipeTags);

            var allTagSymbols = new Dictionary<string, FamilySymbol>();
            var collector = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => s.Category != null && (s.Category.Id.IntegerValue == (int)BuiltInCategory.OST_DuctTags || 
                                                   s.Category.Id.IntegerValue == (int)BuiltInCategory.OST_PipeTags || 
                                                   s.Category.Id.IntegerValue == (int)BuiltInCategory.OST_CableTrayTags));
            foreach (var sym in collector)
            {
                string key = $"{sym.Family.Name}: {sym.Name}";
                if (!allTagSymbols.ContainsKey(key)) allTagSymbols.Add(key, sym);
            }

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

                    var sortResult = SortElementsForTagging(group.Elements, viewUp, viewRight, sortRowTolInternal);
                    var sortedItems = sortResult.Items;

                    WriteGroupHeaderLog(groupIndex, groups.Count, group, sortResult);
                    WriteSortLog(group, sortedItems, sortResult, viewUp, viewRight);

                    var rulesBefore = config.ExtraRules.Where(r => r.GroupIndex == groupIndex.ToString() || r.GroupIndex == "*").ToList();
                    foreach (var rule in rulesBefore)
                    {
                        TryCreateTagForRule(rule, group.Elements, selectedData, allTagSymbols, doc, activeView, basePickPt.X, ref currentY, lineSpacingFt);
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
                        IndependentTag newTag = IndependentTag.Create(
                            doc, targetSym.Id, activeView.Id, elemRef, false, TagOrientation.Horizontal, createPos);

                        MEPLogger.Instance.AppendLine(
                            $"  #{k + 1} [Tag] {elem.GetType().Name} Id={elem.Id.IntegerValue} " +
                            $"SortUp={ToMM(item.SortUp):F1} SortRight={ToMM(item.SortRight):F1} " +
                            $"→ TagPos={FmtXYZ(createPos)}");

                        currentY -= lineSpacingFt;
                    }

                    var rulesAfter = config.ExtraRules.Where(r => r.GroupIndex == "After_" + groupIndex.ToString() || r.GroupIndex == "*After").ToList();
                    foreach (var rule in rulesAfter)
                    {
                        TryCreateTagForRule(rule, group.Elements, selectedData, allTagSymbols, doc, activeView, basePickPt.X, ref currentY, lineSpacingFt);
                    }

                    currentY -= (config.BlankLines * lineSpacingFt);
                }

                t.Commit();
            }

            InfoCenterTip.Show("排版完成，请查看效果。");
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
            DebugMepGroup group, List<SortedElem> sortedItems, SortResult result,
            XYZ viewUp, XYZ viewRight)
        {
            MEPLogger.Instance.AppendLine("  --- 排序前（分组原始顺序，含端点投影） ---");
            var raw = ComputeSortData(group.Elements, viewUp, viewRight);
            foreach (var it in raw)
            {
                MEPLogger.Instance.AppendLine(
                    $"    [{it.Info.Elem.GetType().Name}] Id={it.Info.Elem.Id.IntegerValue}");
                MEPLogger.Instance.AppendLine(
                    $"       P1={FmtXYZ(it.P1)}  P2={FmtXYZ(it.P2)}");
                MEPLogger.Instance.AppendLine(
                    $"       Up1={ToMM(it.Up1):F1} Up2={ToMM(it.Up2):F1} | " +
                    $"Right1={ToMM(it.Right1):F1} Right2={ToMM(it.Right2):F1} | " +
                    $"SortUp={ToMM(it.SortUp):F1} SortRight={ToMM(it.SortRight):F1}");
            }

            MEPLogger.Instance.AppendLine($"  --- 排序后（{result.StrategyName}） ---");
            for (int k = 0; k < sortedItems.Count; k++)
            {
                var it = sortedItems[k];
                if (it.IsNewRow && k > 0)
                {
                    MEPLogger.Instance.AppendLine("      ------ 新行 ------");
                }
                MEPLogger.Instance.AppendLine(
                    $"    #{k + 1} [{it.Info.Elem.GetType().Name}] Id={it.Info.Elem.Id.IntegerValue} " +
                    $"SortUp={ToMM(it.SortUp):F1} SortRight={ToMM(it.SortRight):F1}");
            }
        }

        // ============================================================

        private bool TryCreateTagForRule(ExtraRowRule rule, List<ElemInfo> groupElems, List<ElemInfo> selectedData, Dictionary<string, FamilySymbol> allTagSymbols, Document doc, View activeView, double baseX, ref double currentY, double lineSpacingFt)
        {
            Element hostElem = null;
            FamilySymbol targetSym = null;

            var ductHost = groupElems.FirstOrDefault(e => e.Elem is Duct);
            if (ductHost != null)
            {
                hostElem = ductHost.Elem;
                if (!string.IsNullOrEmpty(rule.DuctTagSymName) && allTagSymbols.TryGetValue(rule.DuctTagSymName, out var dSym))
                    targetSym = dSym;
                else
                    targetSym = allTagSymbols.Values.FirstOrDefault(s => s.Category.Id.IntegerValue == (int)BuiltInCategory.OST_DuctTags);
            }

            if (hostElem == null || targetSym == null)
            {
                var pipeHost = groupElems.FirstOrDefault(e => e.Elem is Pipe);
                if (pipeHost != null)
                {
                    hostElem = pipeHost.Elem;
                    if (!string.IsNullOrEmpty(rule.PipeTagSymName) && allTagSymbols.TryGetValue(rule.PipeTagSymName, out var pSym))
                        targetSym = pSym;
                    else
                        targetSym = allTagSymbols.Values.FirstOrDefault(s => s.Category.Id.IntegerValue == (int)BuiltInCategory.OST_PipeTags);
                }
            }

            if (hostElem == null || targetSym == null)
            {
                var trayHost = groupElems.FirstOrDefault(e => e.Elem is CableTray);
                if (trayHost != null)
                {
                    hostElem = trayHost.Elem;
                    if (!string.IsNullOrEmpty(rule.CableTrayTagSymName) && allTagSymbols.TryGetValue(rule.CableTrayTagSymName, out var tSym))
                        targetSym = tSym;
                    else
                        targetSym = allTagSymbols.Values.FirstOrDefault(s => s.Category.Id.IntegerValue == (int)BuiltInCategory.OST_CableTrayTags);
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

            Parameter p = newTag.LookupParameter("T TO B") ?? 
                          newTag.LookupParameter("Tag") ?? 
                          newTag.LookupParameter("标记") ??
                          newTag.LookupParameter("标签") ??
                          newTag.LookupParameter("文字");

            if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String)
                p.Set(text);
        }

        private FamilySymbol GetDefaultTagSymbol(Document doc, View activeView, BuiltInCategory cat)
        {
            var existingTag = new FilteredElementCollector(doc, activeView.Id)
                .OfClass(typeof(IndependentTag))
                .Cast<IndependentTag>()
                .FirstOrDefault(t => t.Category != null && t.Category.Id.IntegerValue == (int)cat);
            
            if (existingTag != null && existingTag.GetTypeId() != ElementId.InvalidElementId)
                return doc.GetElement(existingTag.GetTypeId()) as FamilySymbol;

            return new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(s => s.Category != null && s.Category.Id.IntegerValue == (int)cat);
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