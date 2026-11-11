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
    public class Cmd_Debug : IExternalCommand
    {
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

            // 1. 获取元素并计算世界坐标中心
            List<ElemInfo> selectedData = GetSelectedMEPElements(uiDoc);
            if (selectedData.Count < 1) return Result.Cancelled;

            // 2. 按高度分组
            List<DebugMepGroup> groups = GroupByBottomElevation(selectedData, config.GroupToleranceMM);
            MEPLogger.Instance.AppendLine($"[日志] 共分为 {groups.Count} 组。");

            // 3. 抓取默认实体 Tag 族（优先继承视图）
            FamilySymbol ductTagSym = GetDefaultTagSymbol(doc, activeView, BuiltInCategory.OST_DuctTags);
            FamilySymbol trayTagSym = GetDefaultTagSymbol(doc, activeView, BuiltInCategory.OST_CableTrayTags);
            FamilySymbol pipeTagSym = GetDefaultTagSymbol(doc, activeView, BuiltInCategory.OST_PipeTags);

            // 抓取所有可用 Tag 族，用于附加行规则匹配
            var allTagSymbols = new Dictionary<string, FamilySymbol>();
            var collector = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => s.Category != null && (s.Category.Id.IntegerValue == (int)BuiltInCategory.OST_DuctTags || 
                                                   s.Category.Id.IntegerValue == (int)BuiltInCategory.OST_PipeTags || 
                                                   s.Category.Id.IntegerValue == (int)BuiltInCategory.OST_CableTrayTags ||
                                                   s.Category.Id.IntegerValue == (int)BuiltInCategory.OST_MultiCategoryTags));
            foreach (var sym in collector)
            {
                string key = $"{sym.Family.Name}: {sym.Name}";
                if (!allTagSymbols.ContainsKey(key)) allTagSymbols.Add(key, sym);
            }

            // 4. 点击起始位置
            XYZ basePickPt;
            try
            {
                basePickPt = uiDoc.Selection.PickPoint("请点击 Tag 清单的左上角起始位置");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }

            if (activeView is ViewPlan viewPlan && viewPlan.GenLevel != null)
                basePickPt = new XYZ(basePickPt.X, basePickPt.Y, viewPlan.GenLevel.Elevation);

            // 5. 生成与排版
            using (Transaction t = new Transaction(doc, "带T1分组排版"))
            {
                t.Start();

                double currentY = basePickPt.Y;
                double lineSpacingFt = UnitUtils.ConvertToInternalUnits(config.LineSpacingMM, UnitTypeId.Millimeters);

                // 分组按标高从上到下
                groups = groups.OrderByDescending(g => g.BaseElevation).ToList();

                for (int i = 0; i < groups.Count; i++)
                {
                    var group = groups[i];
                    int groupIndex = i + 1;
                    
                    // 组内排序：Y降序（从上到下），X升序（从左到右）
                    var sortedElems = group.Elements
                        .OrderByDescending(e => e.WorldCenter.Y)
                        .ThenBy(e => e.WorldCenter.X)
                        .ToList();

                    // ============ 核心1：在每一组【前】插入 自定义附加行 ============
                    var rulesBefore = config.ExtraRules.Where(r => r.GroupIndex == groupIndex.ToString() || r.GroupIndex == "*").ToList();
                    
                    foreach (var rule in rulesBefore)
                    {
                        if (!allTagSymbols.TryGetValue(rule.TagSymbolName, out FamilySymbol targetSym))
                        {
                            MEPLogger.Instance.AppendLine($"[警告] 找不到指定的 Tag 族: {rule.TagSymbolName}");
                            continue; 
                        }

                        Element hostElem = null;
                        var catId = targetSym.Category.Id.IntegerValue;
                        if (catId == (int)BuiltInCategory.OST_DuctTags) 
                            hostElem = group.Elements.FirstOrDefault(e => e.Elem is Duct)?.Elem;
                        else if (catId == (int)BuiltInCategory.OST_CableTrayTags) 
                            hostElem = group.Elements.FirstOrDefault(e => e.Elem is CableTray)?.Elem;
                        else if (catId == (int)BuiltInCategory.OST_PipeTags) 
                            hostElem = group.Elements.FirstOrDefault(e => e.Elem is Pipe)?.Elem;
                        else 
                            hostElem = group.Elements.FirstOrDefault()?.Elem;

                        if (hostElem == null)
                        {
                            TaskDialog.Show("宿主缺失", $"设定中针对第 {groupIndex} 组配置了标签 [{rule.TagText}] ({rule.TagSymbolName})，\n但是当前组内没有找到对应类别的实体作为宿主！");
                            t.RollBack();
                            return Result.Cancelled;
                        }

                        var refPair = selectedData.FirstOrDefault(d => d.Elem.Id == hostElem.Id);
                        if (refPair.Ref == null) continue;

                        if (!targetSym.IsActive) targetSym.Activate();

                        // 【修复】：生成时直接传入计算好的位置，绝不用 basePickPt 重叠
                        XYZ createPos = new XYZ(basePickPt.X, currentY, basePickPt.Z);
                        IndependentTag prefixTag = IndependentTag.Create(
                            doc, targetSym.Id, activeView.Id, refPair.Ref, false, TagOrientation.Horizontal, createPos);
                        
                        TrySetTagText(prefixTag, rule.TagText);
                        currentY -= lineSpacingFt;
                    }

                    // ============ 核心2：排版本组所有实体 ============
                    foreach (var item in sortedElems)
                    {
                        FamilySymbol targetSym = null;
                        if (item.Elem is Duct) targetSym = ductTagSym;
                        else if (item.Elem is CableTray) targetSym = trayTagSym;
                        else if (item.Elem is Pipe) targetSym = pipeTagSym;

                        if (targetSym == null) continue;
                        if (!targetSym.IsActive) targetSym.Activate();

                        XYZ createPos = new XYZ(basePickPt.X, currentY, basePickPt.Z);
                        IndependentTag newTag = IndependentTag.Create(
                            doc, targetSym.Id, activeView.Id, item.Ref, false, TagOrientation.Horizontal, createPos);

                        currentY -= lineSpacingFt;
                    }

                    // ============ 核心3：在每一组【后】插入 自定义附加行 ============
                    var rulesAfter = config.ExtraRules.Where(r => r.GroupIndex == "After_" + groupIndex.ToString() || r.GroupIndex == "*After").ToList();
                    
                    MEPLogger.Instance.AppendLine($"[日志] 第 {groupIndex} 组后置规则数: {rulesAfter.Count}");
                    
                    foreach (var rule in rulesAfter)
                    {
                        if (!allTagSymbols.TryGetValue(rule.TagSymbolName, out FamilySymbol targetSym))
                        {
                            MEPLogger.Instance.AppendLine($"[警告] 找不到指定的 Tag 族: {rule.TagSymbolName}");
                            continue; 
                        }

                        Element hostElem = null;
                        var catId = targetSym.Category.Id.IntegerValue;
                        if (catId == (int)BuiltInCategory.OST_DuctTags) 
                            hostElem = group.Elements.FirstOrDefault(e => e.Elem is Duct)?.Elem;
                        else if (catId == (int)BuiltInCategory.OST_CableTrayTags) 
                            hostElem = group.Elements.FirstOrDefault(e => e.Elem is CableTray)?.Elem;
                        else if (catId == (int)BuiltInCategory.OST_PipeTags) 
                            hostElem = group.Elements.FirstOrDefault(e => e.Elem is Pipe)?.Elem;
                        else 
                            hostElem = group.Elements.FirstOrDefault()?.Elem;

                        if (hostElem == null)
                        {
                            TaskDialog.Show("宿主缺失", $"设定中针对第 {groupIndex} 组【后】配置了标签 [{rule.TagText}] ({rule.TagSymbolName})，\n但是当前组内没有找到对应类别的实体作为宿主！");
                            t.RollBack();
                            return Result.Cancelled;
                        }

                        var refPair = selectedData.FirstOrDefault(d => d.Elem.Id == hostElem.Id);
                        if (refPair.Ref == null) continue;

                        if (!targetSym.IsActive) targetSym.Activate();

                        // 【修复】：生成时直接传入计算好的位置
                        XYZ createPos = new XYZ(basePickPt.X, currentY, basePickPt.Z);
                        IndependentTag suffixTag = IndependentTag.Create(
                            doc, targetSym.Id, activeView.Id, refPair.Ref, false, TagOrientation.Horizontal, createPos);
                        
                        TrySetTagText(suffixTag, rule.TagText);
                        currentY -= lineSpacingFt;
                    }

                    // ============ 核心4：组间空行 ============
                    currentY -= (config.BlankLines * lineSpacingFt);
                }

                t.Commit();
            }

            InfoCenterTip.Show("排版完成，请查看效果。");
            MEPLogger.Instance.EndSession("Debug完毕");
            return Result.Succeeded;
        }

        private void TrySetTagText(IndependentTag tag, string text)
        {
            Parameter p = tag.LookupParameter("T TO B") ?? 
                          tag.LookupParameter("Tag") ?? 
                          tag.LookupParameter("标记") ??
                          tag.LookupParameter("标签") ??
                          tag.LookupParameter("文字");

            if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String)
            {
                p.Set(text);
            }
            else
            {
                MEPLogger.Instance.AppendLine($"[警告] 未在族 {tag.Name} 中找到可写文字参数，{text} 无法显示。");
            }
        }

        // ================= 辅助函数 =================
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
                            WorldCenter = GetElementCenter(elem, null) 
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
                                    WorldCenter = GetElementCenter(linkedElem, tf) 
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
                center = (lc.Curve.GetEndPoint(0) + lc.Curve.GetEndPoint(1)) * 0.5;
            else if (elem.Location is LocationPoint lp)
                center = lp.Point;

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
    }
}