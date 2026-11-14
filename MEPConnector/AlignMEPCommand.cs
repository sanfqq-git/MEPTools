using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using MEPTools.Shared;
using MEPTools.MEPConnector.HighLow; 

using WinForms = System.Windows.Forms;

namespace MEPTools.MEPConnector
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AlignMEPCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            MEPLogger.Instance.StartSession("Align MEP 对齐两组元素");
            var log = MEPLogger.Instance;

            try
            {
                var aList = GetPreselectedCurves(uiDoc, doc);
                if (aList.Count > 0)
                {
                    InfoCenterTip.Show($"已使用预选的 {aList.Count} 个 A 组元素");
                    log.AppendLine($"A 组来自预选，数量：{aList.Count}");
                }
                else
                {
                    InfoCenterTip.Show("未检测到预选，请选择 A 组元素（按空格确认）");
                    aList = PickCurves(uiDoc, "请选择 A 组元素（可框选多个，按空格确认）");
                }

                if (aList.Count < 1)
                {
                    message = "请先选中至少 1 个 A 组元素";
                    return Result.Failed;
                }

                InfoCenterTip.Show("请选择 B 组元素（按空格确认）");
                var bList = PickCurves(uiDoc, "请选择 B 组元素（可框选多个，按空格确认）");
                if (bList.Count < 1)
                {
                    message = "用户取消 B 组拾取";
                    return Result.Failed;
                }

                if (aList.Count != bList.Count)
                {
                    message = $"A 组 ({aList.Count}) 与 B 组 ({bList.Count}) 数量不一致";
                    InfoCenterTip.Show(message);
                    return Result.Failed;
                }

                var aElems = new List<Element>();
                foreach (var id in aList) aElems.Add(doc.GetElement(id));
                var bElems = new List<Element>();
                foreach (var id in bList) bElems.Add(doc.GetElement(id));

                var pairs = MEPConnectorCalculator.MatchPairs(aElems, bElems);
                if (pairs.Count == 0)
                {
                    message = "找不到有效的配对。";
                    InfoCenterTip.Show(message);
                    return Result.Failed;
                }

                int success = 0, fail = 0;
                using (var tx = new Transaction(doc, "B组对齐到A组"))
                {
                    tx.Start();
                    foreach (var p in pairs)
                    {
                        var a = doc.GetElement(p.AId);
                        var b = doc.GetElement(p.BId);

                        if (AlignPairKeepConnections(doc, a, b, log))
                        {
                            success++;
                            log.AppendLine($"✓ 配对 {a.Id} → {b.Id}: 对齐成功");
                        }
                        else
                        {
                            fail++;
                            log.AppendLine($"✗ 配对 {a.Id} → {b.Id}: 对齐失败(详见上方日志)");
                        }
                    }
                    tx.Commit();
                }

                InfoCenterTip.Show($"对齐完成：成功 {success}，失败 {fail}");
                return success > 0 ? Result.Succeeded : Result.Failed;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                log.LogException("AlignMEPCommand", ex);
                return Result.Failed;
            }
            finally
            {
                MEPLogger.Instance.EndSession("完成");
            }
        }

        /// <summary>
        /// 最简对齐逻辑：不主动断连，优先改曲线，异常则整体平移。
        /// </summary>
        private static bool AlignPairKeepConnections(Document doc, Element a, Element b, MEPLogger log)
        {
            if (a == null || b == null) return false;

            try
            {
                var la = (a.Location as LocationCurve)?.Curve as Line;
                var lb = (b.Location as LocationCurve)?.Curve as Line;
                if (la == null || lb == null) { log.AppendLine("    [失败] 元素不是直线"); return false; }

                // 1. 方向计算及降维
                XYZ dirA = new XYZ(la.Direction.X, la.Direction.Y, 0);
                XYZ dirB = new XYZ(lb.Direction.X, lb.Direction.Y, 0);
                if (dirA.GetLength() < 1e-6 || dirB.GetLength() < 1e-6) return false;
                dirA = dirA.Normalize();
                dirB = dirB.Normalize();

                // 2. 平行容差判断 (1e-3)
                double cross = dirA.X * dirB.Y - dirA.Y * dirB.X;
                if (Math.Abs(cross) > 1e-3) { log.AppendLine("    [失败] 两根管线不平行"); return false; }

                // 3. 竖直判断
                if (Math.Abs(dirA.Z) > 1 - 1e-3) { log.AppendLine("    [失败] 管线竖直"); return false; }

                // 4. 计算法向平移量
                XYZ a0 = la.GetEndPoint(0);
                XYZ b0 = lb.GetEndPoint(0);
                XYZ b1 = lb.GetEndPoint(1);
                XYZ mb = (b0 + b1) * 0.5;

                XYZ normalA = new XYZ(-dirA.Y, dirA.X, 0);
                XYZ vecAtoB = new XYZ(mb.X - a0.X, mb.Y - a0.Y, 0);
                double dist = vecAtoB.DotProduct(normalA);

                if (Math.Abs(dist) < 1e-4) { log.AppendLine("    [跳过] 已经共线，无需移动"); return true; }

                XYZ moveVec = -normalA * dist;
                log.AppendLine($"    [计算] 需平移距离: {Math.Abs(dist):F4}，向量: ({moveVec.X:F4}, {moveVec.Y:F4}, {moveVec.Z:F4})");

                // 5. 最简尝试：直接修改 LocationCurve
                try
                {
                    LocationCurve lcB = b.Location as LocationCurve;
                    XYZ newB0 = new XYZ(b0.X + moveVec.X, b0.Y + moveVec.Y, b0.Z);
                    XYZ newB1 = new XYZ(b1.X + moveVec.X, b1.Y + moveVec.Y, b1.Z);
                    lcB.Curve = Line.CreateBound(newB0, newB1);
                    
                    log.AppendLine("    [方式] 直接修改 LocationCurve 成功。");
                    return true;
                }
                catch (Exception exCurve)
                {
                    // 6. 兜底尝试：MoveElement 整体平移
                    log.AppendLine($"    [异常] 直接改曲线失败: {exCurve.Message}，尝试整体平移...");
                    try
                    {
                        ElementTransformUtils.MoveElement(doc, b.Id, moveVec);
                        log.AppendLine("    [方式] 整体平移 MoveElement 成功。");
                        return true;
                    }
                    catch (Exception exMove)
                    {
                        log.AppendLine($"    [失败] 整体平移也失败: {exMove.Message}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                log.AppendLine($"    [致命异常] {ex.Message}");
                return false;
            }
        }

        private static List<ElementId> GetPreselectedCurves(UIDocument uiDoc, Document doc)
        {
            var list = new List<ElementId>();
            var selIds = uiDoc.Selection.GetElementIds();
            if (selIds == null) return list;
            foreach (var id in selIds)
            {
                var e = doc.GetElement(id);
                if (e is Pipe || e is Duct || e is CableTray) list.Add(id);
            }
            return list;
        }

        private static List<ElementId> PickCurves(UIDocument uiDoc, string prompt)
        {
            var list = new List<ElementId>();
            try
            {
                using (var finisher = new SpaceKeyFinisher(WinForms.Keys.Space, "完成", "Finish"))
                {
                    finisher.Enable();
                    var refs = uiDoc.Selection.PickObjects(ObjectType.Element, new MEPCurveSelectionFilter(), prompt);
                    if (refs != null)
                        foreach (var r in refs) list.Add(r.ElementId);
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { }
            catch (Exception ex) { MEPLogger.Instance.LogException("PickCurves", ex); }
            return list;
        }
    }
}