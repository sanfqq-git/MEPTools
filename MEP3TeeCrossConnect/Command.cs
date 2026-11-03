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

namespace MEPTools.MEP3TeeCrossConnect
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
                log.StartSession("MEP 自动交叉连接");

                // ============================================================
                // 1. A 组：优先使用预选，否则让用户 PickObjects（空格确认）
                // ============================================================
                CrossElementType commonType = CrossElementType.Unknown;
                List<ElementId> groupA = new List<ElementId>();
                ICollection<ElementId> preSelected = uidoc.Selection.GetElementIds();
                bool hasPreSelection = false;

                if (preSelected != null && preSelected.Count > 0)
                {
                    CrossElementType tmpType = CrossElementType.Unknown;
                    List<ElementId> tmp = new List<ElementId>();
                    foreach (ElementId id in preSelected)
                    {
                        Element e = doc.GetElement(id);
                        CrossElementType t = MEPCrossCalculator.GetCrossType(e);
                        if (t == CrossElementType.Unknown) continue;
                        if (tmpType == CrossElementType.Unknown) tmpType = t;
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
                        CrossElementType t = MEPCrossCalculator.GetCrossType(e);
                        if (t == CrossElementType.Unknown) continue;
                        if (commonType == CrossElementType.Unknown) commonType = t;
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
                            new CrossSelectionFilter(commonType),
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
                List<CrossPair> pairs = PairBySystem(doc, groupA, groupB, commonType, out pairErr);
                if (pairs == null)
                {
                    log.AppendLine(pairErr);
                    log.EndSession(pairErr);
                    InfoCenterTip.Show(pairErr);
                    return Result.Failed;
                }
                log.AppendLine($"系统配对：{pairs.Count} 对");

                // ============================================================
                // 4. 几何计算（无副作用；放宽校验，仅提示不阻断）
                // ============================================================
                foreach (CrossPair p in pairs)
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
                    log.AppendLine(
                        $"✓ 系统 {p.SystemName}：高 ID={p.HighId.IntegerValue}，低 ID={p.LowId.IntegerValue}，" +
                        $"交点=({p.CrossPoint.X:F3},{p.CrossPoint.Y:F3})，" +
                        $"高差={(p.HighZ - p.LowZ) * MEPCrossCalculator.FtToMm:F1}mm，" +
                        $"管径(高/低)=({p.DiameterHigh * MEPCrossCalculator.FtToMm:F1}/" +
                        $"{p.DiameterLow * MEPCrossCalculator.FtToMm:F1})mm，" +
                        $"立管尺寸 D/W/H=({p.Diameter * MEPCrossCalculator.FtToMm:F1}/" +
                        $"{p.Width * MEPCrossCalculator.FtToMm:F1}/" +
                        $"{p.Height * MEPCrossCalculator.FtToMm:F1})mm");
                }

                // ============================================================
                // 5. 单事务执行
                // ============================================================
                int success = 0;
                using (Transaction trans = new Transaction(doc, "MEP 自动交叉连接"))
                {
                    trans.Start();
                    try
                    {
                        StringBuilder svcLog = new StringBuilder();
                        MEPCrossService service = new MEPCrossService(svcLog);
                        foreach (CrossPair p in pairs)
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
                InfoCenterTip.Show($"✓ MEP 自动交叉连接完成，成功 {success} 对");
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
        // 按系统名分组 + 同系统内顺序配对
        // =====================================================================
        private List<CrossPair> PairBySystem(Document doc, List<ElementId> groupA, List<ElementId> groupB,
            CrossElementType type, out string err)
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

            List<CrossPair> pairs = new List<CrossPair>();
            foreach (var k in aDict.Keys)
            {
                List<ElementId> aList = aDict[k];
                List<ElementId> bList = bDict[k];
                for (int i = 0; i < aList.Count; i++)
                {
                    pairs.Add(new CrossPair
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
            List<ElementId> ids, CrossElementType type)
        {
            var dict = new Dictionary<string, List<ElementId>>();
            foreach (ElementId id in ids)
            {
                Element e = doc.GetElement(id);
                string name = MEPCrossCalculator.GetSystemName(doc, e, type);
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
        // 判高/低 + 交点 + 尺寸读取
        // =====================================================================
        private bool BuildPairGeometry(Document doc, CrossPair p, out string err)
        {
            err = null;
            Element elemA = doc.GetElement(p.IdA);
            Element elemB = doc.GetElement(p.IdB);
            if (elemA == null || elemB == null) { err = "元素已删除"; return false; }

            // 判高/低
            double zA = MEPCrossCalculator.GetCenterZ(elemA);
            double zB = MEPCrossCalculator.GetCenterZ(elemB);
            Element highElem, lowElem;
            if (zA >= zB) { highElem = elemA; lowElem = elemB; p.HighId = p.IdA; p.LowId = p.IdB; }
            else { highElem = elemB; lowElem = elemA; p.HighId = p.IdB; p.LowId = p.IdA; }

            p.HighZ = MEPCrossCalculator.GetCenterZ(highElem);
            p.LowZ = MEPCrossCalculator.GetCenterZ(lowElem);

            p.SystemTypeId = MEPCrossCalculator.GetSystemTypeId(highElem, p.ElemType);
            if (p.SystemTypeId == ElementId.InvalidElementId) { err = "系统类型无效"; return false; }

            // -------- 硬性：形状一致性 --------
            bool rectHigh = MEPCrossCalculator.IsRectangular(highElem, p.ElemType);
            bool rectLow = MEPCrossCalculator.IsRectangular(lowElem, p.ElemType);
            if (rectHigh != rectLow)
            {
                err = "高组与低组管的截面形状不一致（圆形 ↔ 矩形），无法创建三通";
                return false;
            }

            // -------- 尺寸读取（允许变径，取较大值） --------
            p.DiameterHigh = MEPCrossCalculator.GetDiameter(highElem, p.ElemType);
            p.DiameterLow = MEPCrossCalculator.GetDiameter(lowElem, p.ElemType);

            double effectiveDia; 
            if (p.ElemType == CrossElementType.Pipe || !rectHigh)
            {
                p.Diameter = Math.Max(p.DiameterHigh, p.DiameterLow);
                p.Width = 0;
                p.Height = 0;
                effectiveDia = p.Diameter;
            }
            else
            {
                double wHigh = MEPCrossCalculator.GetWidth(highElem, p.ElemType);
                double wLow = MEPCrossCalculator.GetWidth(lowElem, p.ElemType);
                double hHigh = MEPCrossCalculator.GetHeight(highElem, p.ElemType);
                double hLow = MEPCrossCalculator.GetHeight(lowElem, p.ElemType);
                p.Width = Math.Max(wHigh, wLow);
                p.Height = Math.Max(hHigh, hLow);
                p.Diameter = 0;
                effectiveDia = p.Height;
            }

            // -------- 尺寸比较（仅提示） --------
            string sizeInfo;
            if (!MEPCrossCalculator.AreEqualSize(highElem, lowElem, p.ElemType, out sizeInfo))
            {
                MEPLogger.Instance.AppendLine($"⚠ 尺寸不一致（允许变径连接）：{sizeInfo}");
            }

            // -------- 硬性：平面交点必须存在 --------
            XYZ planPt = MEPCrossCalculator.ComputePlanCrossPoint(highElem, lowElem, out err);
            if (planPt == null) return false;
            p.CrossPoint = planPt;

            // -------- 端点距离（仅提示，不阻断） --------
            string endErr;
            if (!MEPCrossCalculator.CheckEndDistance(highElem, planPt, out endErr))
                MEPLogger.Instance.AppendLine($"⚠ 高组管：{endErr}（已放宽，继续执行）");
            if (!MEPCrossCalculator.CheckEndDistance(lowElem, planPt, out endErr))
                MEPLogger.Instance.AppendLine($"⚠ 低组管：{endErr}（已放宽，继续执行）");

            // -------- 高差（仅提示，不阻断，交由 Revit 自动判断） --------
            string hErr;
            if (!MEPCrossCalculator.CheckHeightDifference(p.HighZ, p.LowZ, effectiveDia, out hErr))
            {
                MEPLogger.Instance.AppendLine($"⚠ {hErr}（已放宽交由 Revit 判断，继续执行）");
            }

            // -------- 已有管件（仅提示，不阻断） --------
            string fitErr;
            if (!MEPCrossCalculator.CheckNearbyFitting(doc, planPt, p.HighZ, p.HighId, p.LowId, out fitErr))
                MEPLogger.Instance.AppendLine($"⚠ 高组交叉点：{fitErr}（已放宽，继续执行）");
            if (!MEPCrossCalculator.CheckNearbyFitting(doc, planPt, p.LowZ, p.HighId, p.LowId, out fitErr))
                MEPLogger.Instance.AppendLine($"⚠ 低组交叉点：{fitErr}（已放宽，继续执行）");

            // -------- 高组 type / level（硬性：创建立管/三通必需） --------
            p.HighTypeId = highElem.GetTypeId();
            p.HighLevelId = highElem.LevelId;
            if (p.HighLevelId == ElementId.InvalidElementId)
            {
                Parameter lp = highElem.get_Parameter(BuiltInParameter.RBS_START_LEVEL_PARAM);
                if (lp != null) p.HighLevelId = lp.AsElementId();
            }
            if (p.HighLevelId == ElementId.InvalidElementId)
            { err = "无法确定高组管的标高"; return false; }

            return true;
        }

        // =====================================================================
        // 选择过滤器
        // =====================================================================
        private class AnyMEPCurveFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) => e is Pipe || e is Duct;
            public bool AllowReference(Reference r, XYZ p) => true;
        }

        private class CrossSelectionFilter : ISelectionFilter
        {
            private readonly CrossElementType _type;
            public CrossSelectionFilter(CrossElementType t) { _type = t; }
            public bool AllowElement(Element e)
            {
                if (_type == CrossElementType.Pipe) return e is Pipe;
                if (_type == CrossElementType.Duct) return e is Duct;
                return false;
            }
            public bool AllowReference(Reference r, XYZ p) => true;
        }
    }
}