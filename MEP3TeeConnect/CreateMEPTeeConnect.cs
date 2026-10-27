using System;
using System.Collections.Generic;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace MEPTools.MEP3TeeConnect
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Command : IExternalCommand
    {
        private readonly StringBuilder debugInfo = new StringBuilder();

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // ---------- 1. 预选 A 组 ----------
                ICollection<ElementId> preSelected = uidoc.Selection.GetElementIds();
                if (preSelected == null || preSelected.Count == 0)
                {
                    message = "请先选中第一组管（A 组：Pipe / Duct）";
                    return Result.Failed;
                }

                TeeElementType commonType = TeeElementType.Unknown;
                List<ElementId> groupA = new List<ElementId>();
                foreach (ElementId id in preSelected)
                {
                    Element e = doc.GetElement(id);
                    TeeElementType t = MEPTeeCalculator.GetTeeType(e);
                    if (t == TeeElementType.Unknown) continue;
                    if (commonType == TeeElementType.Unknown) commonType = t;
                    if (t != commonType) continue;
                    groupA.Add(id);
                }
                if (groupA.Count == 0)
                {
                    message = "A 组中未包含有效的 Pipe 或 Duct（且必须同类型）";
                    return Result.Failed;
                }
                debugInfo.AppendLine($"A 组：{groupA.Count} 个元素，类型 = {commonType}");

                // ---------- 2. B 组 PickObjects ----------
                IList<Reference> bRefs;
                try
                {
                    bRefs = uidoc.Selection.PickObjects(
                        ObjectType.Element,
                        new TeeSelectionFilter(commonType),
                        $"请选择第二组（{commonType}）管（数量需为 {groupA.Count}），选完按完成/ESC");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    debugInfo.AppendLine("用户取消了 B 组选择");
                    return Result.Cancelled;
                }

                List<ElementId> groupB = new List<ElementId>();
                foreach (Reference r in bRefs) groupB.Add(r.ElementId);

                if (groupB.Count != groupA.Count)
                {
                    message = $"两组数量不一致：A={groupA.Count}，B={groupB.Count}";
                    return Result.Failed;
                }
                debugInfo.AppendLine($"B 组：{groupB.Count} 个元素");

                // ---------- 3. 按系统配对 ----------
                string pairErr;
                List<TeePair> pairs = PairBySystem(doc, groupA, groupB, commonType, out pairErr);
                if (pairs == null)
                {
                    message = pairErr;
                    return Result.Failed;
                }
                debugInfo.AppendLine($"系统配对：{pairs.Count} 对");

                // ---------- 4. 几何校验（无副作用） ----------
                foreach (TeePair p in pairs)
                {
                    string geomErr;
                    if (!BuildPairGeometry(doc, p, out geomErr))
                    {
                        message = $"配对失败（系统 {p.SystemName}）：{geomErr}";
                        debugInfo.AppendLine($"✗ {message}");
                        ShowDebugInfo("几何校验失败", message);
                        return Result.Failed;
                    }
                    debugInfo.AppendLine(
                        $"✓ 系统 {p.SystemName}：A={p.IdA.IntegerValue}，B={p.IdB.IntegerValue}，" +
                        $"交点=({p.CrossPoint.X:F3},{p.CrossPoint.Y:F3})，" +
                        $"高差={Math.Abs(p.ZA - p.ZB) * MEPTeeCalculator.FtToMm:F1}mm，" +
                        $"{(p.NeedExtend ? "需延长" : "需缩短")} B 的 {(p.ModifyB0 ? "b0" : "b1")} 端");
                }

                // ---------- 5. 单事务执行 ----------
                int success = 0;
                using (Transaction trans = new Transaction(doc, "MEP T 形自动连接"))
                {
                    trans.Start();
                    try
                    {
                        MEPTeeService service = new MEPTeeService(debugInfo);
                        foreach (TeePair p in pairs)
                        {
                            debugInfo.AppendLine($"\n--- 处理系统 {p.SystemName} ---");
                            service.ProcessPair(doc, p);
                            success++;
                        }
                        trans.Commit();
                    }
                    catch (Exception ex)
                    {
                        debugInfo.AppendLine($"✗ 执行失败：{ex.Message}");
                        debugInfo.AppendLine("⚠ 单事务模式，已回滚所有更改");
                        trans.RollBack();
                        ShowDebugInfo("执行失败（已回滚）", ex.Message);
                        return Result.Failed;
                    }
                }

                ShowDebugInfo("MEP T 形自动连接", $"成功连接 {success} 对");
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                debugInfo.AppendLine("用户取消了操作");
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                debugInfo.AppendLine($"异常：{ex.Message}");
                debugInfo.AppendLine($"堆栈：{ex.StackTrace}");
                ShowDebugInfo("操作失败", null);
                return Result.Failed;
            }
        }

        // =====================================================================
        // 按系统名分组 + 顺序配对
        // =====================================================================
        private List<TeePair> PairBySystem(Document doc, List<ElementId> groupA, List<ElementId> groupB,
            TeeElementType type, out string err)
        {
            err = null;
            Dictionary<string, List<ElementId>> aDict = GroupBySystem(doc, groupA, type);
            Dictionary<string, List<ElementId>> bDict = GroupBySystem(doc, groupB, type);

            if (aDict.Count == 0)
            { err = "A 组中没有任何元素能读取到系统名称"; return null; }
            if (aDict.Count != bDict.Count)
            { err = $"两组系统数量不一致：A={aDict.Count}，B={bDict.Count}"; return null; }

            foreach (var k in aDict.Keys)
            {
                if (!bDict.ContainsKey(k))
                { err = $"B 组缺少系统 {k}"; return null; }
                if (aDict[k].Count != bDict[k].Count)
                { err = $"系统 {k} 数量不一致：A={aDict[k].Count}，B={bDict[k].Count}"; return null; }
            }

            List<TeePair> pairs = new List<TeePair>();
            foreach (var k in aDict.Keys)
            {
                List<ElementId> aList = aDict[k];
                List<ElementId> bList = bDict[k];
                for (int i = 0; i < aList.Count; i++)
                {
                    pairs.Add(new TeePair
                    {
                        SystemName = k,
                        IdA = aList[i],
                        IdB = bList[i],
                        ElemType = type
                    });
                }
            }
            return pairs;
        }

        private Dictionary<string, List<ElementId>> GroupBySystem(Document doc,
            List<ElementId> ids, TeeElementType type)
        {
            var dict = new Dictionary<string, List<ElementId>>();
            foreach (ElementId id in ids)
            {
                Element e = doc.GetElement(id);
                string name = MEPTeeCalculator.GetSystemName(doc, e, type);
                if (string.IsNullOrEmpty(name))
                {
                    debugInfo.AppendLine($"⚠ 元素 {id.IntegerValue} 无系统名称，跳过");
                    continue;
                }
                if (!dict.ContainsKey(name)) dict[name] = new List<ElementId>();
                dict[name].Add(id);
            }
            return dict;
        }

        // =====================================================================
        // 几何校验
        // =====================================================================
        private bool BuildPairGeometry(Document doc, TeePair p, out string err)
        {
            err = null;
            Element elemA = doc.GetElement(p.IdA);
            Element elemB = doc.GetElement(p.IdB);
            if (elemA == null || elemB == null) { err = "元素已删除"; return false; }

            p.ZA = MEPTeeCalculator.GetCenterZ(elemA);
            p.ZB = MEPTeeCalculator.GetCenterZ(elemB);

            // 立管参数取 A 组
            p.SystemTypeId = MEPTeeCalculator.GetSystemTypeId(elemA, p.ElemType);
            p.TypeId = elemA.GetTypeId();

            p.LevelId = elemA.LevelId;
            if (p.LevelId == ElementId.InvalidElementId)
            {
                Parameter lp = elemA.get_Parameter(BuiltInParameter.RBS_START_LEVEL_PARAM);
                if (lp != null) p.LevelId = lp.AsElementId();
            }
            if (p.LevelId == ElementId.InvalidElementId)
            { err = "无法确定 A 组标高"; return false; }

            // 等径校验
            string sizeErr;
            if (!MEPTeeCalculator.AreEqualSize(elemA, elemB, p.ElemType, out sizeErr))
            { err = sizeErr; return false; }

            p.Diameter = MEPTeeCalculator.GetDiameter(elemA, p.ElemType);
            p.Width = MEPTeeCalculator.GetWidth(elemA, p.ElemType);
            p.Height = MEPTeeCalculator.GetHeight(elemA, p.ElemType);

            // T 形几何
            string geomErr;
            XYZ crossPt;
            double sOnA, tCross;
            bool needExtend, modifyB0;
            if (!MEPTeeCalculator.ComputeTeeGeometry(elemA, elemB,
                out crossPt, out sOnA, out tCross,
                out needExtend, out modifyB0, out geomErr))
            { err = geomErr; return false; }

            p.CrossPoint = crossPt;
            p.SOnA = sOnA;
            p.TCross = tCross;
            p.NeedExtend = needExtend;
            p.ModifyB0 = modifyB0;

            // 交点距 A 两端 ≥ 100mm
            string endErr;
            if (!MEPTeeCalculator.CheckEndDistance(elemA, crossPt, out endErr))
            { err = $"A 管：{endErr}"; return false; }

            // 高差
            string hErr;
            if (!MEPTeeCalculator.CheckHeightDifference(p.ZA, p.ZB, p.Diameter, out hErr))
            { err = hErr; return false; }

            // A 交点附近 ±100mm 管件检查
            string fitErr;
            if (!MEPTeeCalculator.CheckNearbyFitting(doc, crossPt, p.ZA, p.IdA, p.IdB, out fitErr))
            { err = $"A 交叉点：{fitErr}"; return false; }

            // B 近端管件检查（近端 XY 处）
            XYZ bEndPt = new XYZ(crossPt.X, crossPt.Y, p.ZB);
            if (!MEPTeeCalculator.CheckNearbyFitting(doc, bEndPt, p.ZB, p.IdA, p.IdB, out fitErr))
            { err = $"B 近端：{fitErr}"; return false; }

            return true;
        }

        // =====================================================================
        // 辅助
        // =====================================================================
        private void ShowDebugInfo(string title, string mainInstruction)
        {
            TaskDialog td = new TaskDialog(title);
            td.MainInstruction = mainInstruction ?? string.Empty;
            td.MainContent = debugInfo.ToString();
            td.CommonButtons = TaskDialogCommonButtons.Ok;
            td.Show();
        }

        private class TeeSelectionFilter : ISelectionFilter
        {
            private readonly TeeElementType _type;
            public TeeSelectionFilter(TeeElementType t) { _type = t; }
            public bool AllowElement(Element e)
            {
                switch (_type)
                {
                    case TeeElementType.Pipe: return e is Pipe;
                    case TeeElementType.Duct: return e is Duct;
                }
                return false;
            }
            public bool AllowReference(Reference r, XYZ p) => true;
        }
    }
}