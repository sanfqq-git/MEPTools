using System;
using System.Collections.Generic;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using MEPTools.Shared;

namespace MEPTools.MEP3TeeConnect
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Command : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;
            MEPLogger log = MEPLogger.Instance;

            try
            {
                log.StartSession("MEP T 形自动连接");

                // ============================================================
                // 1. A 组：优先使用预选，否则让用户 PickObjects（空格确认）
                // ============================================================
                TeeElementType commonType = TeeElementType.Unknown;
                List<ElementId> groupA = new List<ElementId>();

                ICollection<ElementId> preSelected = uidoc.Selection.GetElementIds();
                bool hasPreSelection = false;

                if (preSelected != null && preSelected.Count > 0)
                {
                    TeeElementType tmpType = TeeElementType.Unknown;
                    List<ElementId> tmp = new List<ElementId>();
                    foreach (ElementId id in preSelected)
                    {
                        Element e = doc.GetElement(id);
                        TeeElementType t = MEPTeeCalculator.GetTeeType(e);
                        if (t == TeeElementType.Unknown) continue;
                        if (tmpType == TeeElementType.Unknown) tmpType = t;
                        if (t != tmpType) continue;
                        tmp.Add(id);
                    }
                    if (tmp.Count > 0)
                    {
                        groupA = tmp;
                        commonType = tmpType;
                        hasPreSelection = true;
                        log.AppendLine($"✓ 预选 A 组：{groupA.Count} 个元素，类型 = {commonType}");
                        InfoCenterTip.Show($"已识别预选 A 组 {groupA.Count} 个（{commonType}）");
                    }
                }

                if (!hasPreSelection)
                {
                    log.AppendLine("未检测到有效预选 → 进入 A 组交互选择");
                    InfoCenterTip.Show("请选择 A 组 MEP 管（Pipe/Duct），按空格完成");

                    IList<Reference> aRefs;
                    try
                    {
                        using (var finisher = new SpaceKeyFinisher())
                        {
                            finisher.Enable();
                            aRefs = uidoc.Selection.PickObjects(
                                ObjectType.Element,
                                new AnyMEPCurveFilter(),
                                "请选择 A 组（Pipe/Duct，需同类型），选完按空格完成");
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        log.AppendLine("用户取消了 A 组选择");
                        log.EndSession("取消");
                        InfoCenterTip.Show("已取消");
                        return Result.Cancelled;
                    }

                    foreach (Reference r in aRefs)
                    {
                        Element e = doc.GetElement(r.ElementId);
                        TeeElementType t = MEPTeeCalculator.GetTeeType(e);
                        if (t == TeeElementType.Unknown) continue;
                        if (commonType == TeeElementType.Unknown) commonType = t;
                        if (t != commonType)
                        {
                            log.AppendLine($"⚠ A 组 {r.ElementId.IntegerValue} 类型 {t} 与 {commonType} 不一致，已跳过");
                            continue;
                        }
                        groupA.Add(r.ElementId);
                    }

                    if (groupA.Count == 0)
                    {
                        log.EndSession("A 组无有效元素");
                        InfoCenterTip.Show("A 组未包含有效的 Pipe 或 Duct");
                        return Result.Failed;
                    }
                    log.AppendLine($"✓ A 组：{groupA.Count} 个元素，类型 = {commonType}");
                }

                // ============================================================
                // 2. B 组 PickObjects（空格确认）
                // ============================================================
                InfoCenterTip.Show($"请选择 B 组（{commonType}，需 {groupA.Count} 个），按空格完成");

                IList<Reference> bRefs;
                try
                {
                    using (var finisher = new SpaceKeyFinisher())
                    {
                        finisher.Enable();
                        bRefs = uidoc.Selection.PickObjects(
                            ObjectType.Element,
                            new TeeSelectionFilter(commonType),
                            $"请选择 B 组（{commonType}，数量需为 {groupA.Count}），选完按空格完成");
                    }
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    log.AppendLine("用户取消了 B 组选择");
                    log.EndSession("取消");
                    InfoCenterTip.Show("已取消");
                    return Result.Cancelled;
                }

                List<ElementId> groupB = new List<ElementId>();
                foreach (Reference r in bRefs) groupB.Add(r.ElementId);

                if (groupB.Count != groupA.Count)
                {
                    string err = $"两组数量不一致：A={groupA.Count}，B={groupB.Count}";
                    log.AppendLine(err);
                    log.EndSession(err);
                    InfoCenterTip.Show(err);
                    return Result.Failed;
                }
                log.AppendLine($"✓ B 组：{groupB.Count} 个元素");

                // ============================================================
                // 3. 按系统配对
                // ============================================================
                string pairErr;
                List<TeePair> pairs = PairBySystem(doc, groupA, groupB, commonType, out pairErr);
                if (pairs == null)
                {
                    log.AppendLine(pairErr);
                    log.EndSession(pairErr);
                    InfoCenterTip.Show(pairErr);
                    return Result.Failed;
                }
                log.AppendLine($"系统配对：{pairs.Count} 对");

                // ============================================================
                // 4. 几何校验（无副作用）
                // ============================================================
                foreach (TeePair p in pairs)
                {
                    string geomErr;
                    if (!BuildPairGeometry(doc, p, out geomErr))
                    {
                        string err = $"配对失败（系统 {p.SystemName}）：{geomErr}";
                        log.AppendLine($"✗ {err}");
                        log.EndSession(err);
                        InfoCenterTip.Show(err);
                        return Result.Failed;
                    }

                    bool isReducing = Math.Abs(p.DiameterA - p.DiameterB) > 1e-6
                                      || Math.Abs(p.WidthA - p.WidthB) > 1e-6
                                      || Math.Abs(p.HeightA - p.HeightB) > 1e-6;
                    string sizeTag = isReducing
                        ? $"[异径 A={FmtSize(p.DiameterA, p.WidthA, p.HeightA)} / B={FmtSize(p.DiameterB, p.WidthB, p.HeightB)}，" +
                          $"立管取大={FmtSize(p.Diameter, p.Width, p.Height)}]"
                        : $"[等径 {FmtSize(p.DiameterA, p.WidthA, p.HeightA)}]";

                    log.AppendLine(
                        $"✓ 系统 {p.SystemName}：A={p.IdA.IntegerValue}，B={p.IdB.IntegerValue}，{sizeTag}，" +
                        $"交点=({p.CrossPoint.X:F3},{p.CrossPoint.Y:F3})，" +
                        $"高差={Math.Abs(p.ZA - p.ZB) * MEPTeeCalculator.FtToMm:F1}mm，" +
                        $"{(p.NeedExtend ? "需延长" : "需缩短")} B 的 {(p.ModifyB0 ? "b0" : "b1")} 端");
                }

                // ============================================================
                // 5. 单事务执行
                // ============================================================
                int success = 0;
                using (Transaction trans = new Transaction(doc, "MEP T 形自动连接"))
                {
                    trans.Start();
                    try
                    {
                        StringBuilder svcLog = new StringBuilder();
                        MEPTeeService service = new MEPTeeService(svcLog);
                        foreach (TeePair p in pairs)
                        {
                            log.AppendLine($"\n--- 处理系统 {p.SystemName} ---");
                            service.ProcessPair(doc, p);
                            success++;
                        }
                        if (svcLog.Length > 0) log.AppendLine(svcLog.ToString());
                        trans.Commit();
                    }
                    catch (Exception ex)
                    {
                        log.AppendLine($"✗ 执行失败：{ex.Message}");
                        log.LogException("ProcessPair", ex);
                        log.AppendLine("⚠ 单事务模式，已回滚所有更改");
                        trans.RollBack();
                        log.EndSession("失败（已回滚）");
                        InfoCenterTip.Show($"✗ 执行失败：{ex.Message}");
                        return Result.Failed;
                    }
                }

                log.EndSession($"成功 {success} 对");
                InfoCenterTip.Show($"✓ MEP T 形自动连接完成，成功 {success} 对");
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                log.AppendLine("用户取消了操作");
                log.EndSession("取消");
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                log.LogException("Command.Execute", ex);
                log.EndSession("异常");
                InfoCenterTip.Show($"✗ 操作失败：{ex.Message}");
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
                    MEPLogger.Instance.AppendLine($"⚠ 元素 {id.IntegerValue} 无系统名称，跳过");
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

            p.DiameterA = MEPTeeCalculator.GetDiameter(elemA, p.ElemType);
            p.DiameterB = MEPTeeCalculator.GetDiameter(elemB, p.ElemType);
            p.WidthA = MEPTeeCalculator.GetWidth(elemA, p.ElemType);
            p.WidthB = MEPTeeCalculator.GetWidth(elemB, p.ElemType);
            p.HeightA = MEPTeeCalculator.GetHeight(elemA, p.ElemType);
            p.HeightB = MEPTeeCalculator.GetHeight(elemB, p.ElemType);

            p.Diameter = MEPTeeCalculator.GetMaxDiameter(elemA, elemB, p.ElemType);
            p.Width = MEPTeeCalculator.GetMaxWidth(elemA, elemB, p.ElemType);
            p.Height = MEPTeeCalculator.GetMaxHeight(elemA, elemB, p.ElemType);

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

            string endErr;
            if (!MEPTeeCalculator.CheckEndDistance(elemA, crossPt, out endErr))
            { err = $"A 管：{endErr}"; return false; }

            // 高差检查：不再强制阻断，仅作为警告记录，交由 Revit 自动判断管件匹配高度
            string hErr;
            if (!MEPTeeCalculator.CheckHeightDifference(p.ZA, p.ZB, p.Diameter, out hErr))
            {
                MEPLogger.Instance.AppendLine($"⚠ 提示（系统 {p.SystemName}）：{hErr}。交由 Revit 尝试自动连接。");
            }

            string fitErr;
            if (!MEPTeeCalculator.CheckNearbyFitting(doc, crossPt, p.ZA, p.IdA, p.IdB, out fitErr))
            { err = $"A 交叉点：{fitErr}"; return false; }

            XYZ bEndPt = new XYZ(crossPt.X, crossPt.Y, p.ZB);
            if (!MEPTeeCalculator.CheckNearbyFitting(doc, bEndPt, p.ZB, p.IdA, p.IdB, out fitErr))
            { err = $"B 近端：{fitErr}"; return false; }

            return true;
        }

        // =====================================================================
        // 辅助
        // =====================================================================
        private string FmtSize(double dia, double w, double h)
        {
            const double f = MEPTeeCalculator.FtToMm;
            if (w > 1e-9 && h > 1e-9)
                return $"{w * f:F0}x{h * f:F0}";
            return $"Ø{dia * f:F0}";
        }

        /// <summary>A 组无预选时使用：允许 Pipe 与 Duct 混选，后续按首元素类型过滤</summary>
        private class AnyMEPCurveFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) => e is Pipe || e is Duct;
            public bool AllowReference(Reference r, XYZ p) => true;
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