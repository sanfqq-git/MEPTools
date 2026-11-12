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

            using (Transaction t = new Transaction(doc, "带T1分组排版"))
            {
                t.Start();

                double currentY = basePickPt.Y;
                double lineSpacingFt = UnitUtils.ConvertToInternalUnits(config.LineSpacingMM, UnitTypeId.Millimeters);

                groups = groups.OrderByDescending(g => g.BaseElevation).ToList();

                for (int i = 0; i < groups.Count; i++)
                {
                    var group = groups[i];
                    int groupIndex = i + 1;
                    
                    var sortedElems = group.Elements
                        .OrderByDescending(e => e.WorldCenter.Y)
                        .ThenBy(e => e.WorldCenter.X)
                        .ToList();

                    var rulesBefore = config.ExtraRules.Where(r => r.GroupIndex == groupIndex.ToString() || r.GroupIndex == "*").ToList();
                    foreach (var rule in rulesBefore)
                    {
                        TryCreateTagForRule(rule, group.Elements, selectedData, allTagSymbols, doc, activeView, basePickPt.X, ref currentY, lineSpacingFt);
                    }

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

        /// <summary>
        /// 核心修复：无论配了什么，只要本组有管子，就绝不会跳过。
        /// </summary>
        private bool TryCreateTagForRule(ExtraRowRule rule, List<ElemInfo> groupElems, List<ElemInfo> selectedData, Dictionary<string, FamilySymbol> allTagSymbols, Document doc, View activeView, double baseX, ref double currentY, double lineSpacingFt)
        {
            Element hostElem = null;
            FamilySymbol targetSym = null;

            // 1. 优先尝试找 DUCT
            var ductHost = groupElems.FirstOrDefault(e => e.Elem is Duct);
            if (ductHost != null)
            {
                hostElem = ductHost.Elem;
                if (!string.IsNullOrEmpty(rule.DuctTagSymName) && allTagSymbols.TryGetValue(rule.DuctTagSymName, out var dSym))
                    targetSym = dSym;
                else
                    targetSym = allTagSymbols.Values.FirstOrDefault(s => s.Category.Id.IntegerValue == (int)BuiltInCategory.OST_DuctTags); // 兜底
            }

            // 2. 如果没有 DUCT，或者没风管Tag，尝试找 PIPE
            if (hostElem == null || targetSym == null)
            {
                var pipeHost = groupElems.FirstOrDefault(e => e.Elem is Pipe);
                if (pipeHost != null)
                {
                    hostElem = pipeHost.Elem;
                    if (!string.IsNullOrEmpty(rule.PipeTagSymName) && allTagSymbols.TryGetValue(rule.PipeTagSymName, out var pSym))
                        targetSym = pSym;
                    else
                        targetSym = allTagSymbols.Values.FirstOrDefault(s => s.Category.Id.IntegerValue == (int)BuiltInCategory.OST_PipeTags); // 兜底
                }
            }

            // 3. 如果还没有，尝试找 CABLE TRAY
            if (hostElem == null || targetSym == null)
            {
                var trayHost = groupElems.FirstOrDefault(e => e.Elem is CableTray);
                if (trayHost != null)
                {
                    hostElem = trayHost.Elem;
                    if (!string.IsNullOrEmpty(rule.CableTrayTagSymName) && allTagSymbols.TryGetValue(rule.CableTrayTagSymName, out var tSym))
                        targetSym = tSym;
                    else
                        targetSym = allTagSymbols.Values.FirstOrDefault(s => s.Category.Id.IntegerValue == (int)BuiltInCategory.OST_CableTrayTags); // 兜底
                }
            }

            // 如果无论怎么找，都没有宿主或没可用的 Tag，直接返回
            if (hostElem == null || targetSym == null) return false;

            var refPair = selectedData.FirstOrDefault(d => d.Elem.Id == hostElem.Id);
            if (refPair.Ref == null) return false;

            CreateTag(targetSym, refPair.Ref, rule.TagText, doc, activeView, baseX, currentY);
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