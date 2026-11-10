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
using System.Windows.Forms;

namespace MEPTools.MEPPlanarLateralBend
{
    /// <summary>
    /// 平面拐弯公共基类：负责元素收集、P1/P2 拾取、frame 计算、事务处理。
    /// 子类只需提供 <see cref="CommandTitle"/> 和 <see cref="TryGetAngleDeg"/>。
    /// </summary>
    public abstract class MEPLateralBendCommandBase : IExternalCommand
    {
        private readonly MEPLogger debugInfo = MEPLogger.Instance;

        /// <summary>命令显示名称（用于日志、进度提示、事务名）。</summary>
        protected abstract string CommandTitle { get; }

        /// <summary>
        /// 获取拐弯角度（度）。返回 false 表示用户取消了本命令。
        /// 45°/90° 命令直接返回 true + 常量；自由角度命令在此弹出输入框。
        /// </summary>
        protected abstract bool TryGetAngleDeg(UIDocument uidoc, out double angleDeg);

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            MEPLogger.Instance.StartSession($"MEPLateralBend {CommandTitle}");
            debugInfo.AppendLine($"[{CommandTitle}] 开始，当前预选元素数：{uidoc.Selection.GetElementIds()?.Count ?? 0}");

            // 拦截空格键 → 点击当前窗体的"完成/确定"按钮
            using (var spaceFinisher = new SpaceKeyFinisher(Keys.Space, "完成", "Finish", "Complete", "确定"))
            {
                spaceFinisher.Enable();

                try
                {
                    // ---------- 1. 收集待处理 MEP 元素 ----------
                    List<ElementId> elemIdList = new List<ElementId>();
                    HashSet<ElementId> addedIds = new HashSet<ElementId>();

                    // 1.1 通过预选 Tag 反查管线
                    var selectedTags = Preselected.GetSelectedIndependentTags(uidoc, doc.ActiveView);
                    foreach (var tag in selectedTags)
                    {
                        ICollection<ElementId> taggedIds = GetTaggedElementIds(tag);
                        foreach (var taggedId in taggedIds)
                        {
                            Element elem = doc.GetElement(taggedId);
                            if (IsValidMEPElement(elem) && addedIds.Add(elem.Id))
                                elemIdList.Add(elem.Id);
                        }
                    }

                    // 1.2 用户直接选中的 MEP
                    foreach (ElementId id in uidoc.Selection.GetElementIds())
                    {
                        Element elem = doc.GetElement(id);
                        if (IsValidMEPElement(elem) && addedIds.Add(id))
                            elemIdList.Add(id);
                    }

                    // 1.3 无预选 → 交互框选
                    if (elemIdList.Count < 1)
                    {
                        InfoCenterTip.Show("未检测到预选元素，请去选择要处理的 MEP 元素（管道/风管/线槽）。");

                        try
                        {
                            IList<Reference> refs = uidoc.Selection.PickObjects(
                                ObjectType.Element,
                                new MEPCurveSelectionFilter(),
                                "请选择要处理的 MEP 元素（可框选）");

                            foreach (var r in refs)
                            {
                                Element elem = doc.GetElement(r.ElementId);
                                if (IsValidMEPElement(elem) && addedIds.Add(elem.Id))
                                    elemIdList.Add(elem.Id);
                            }
                        }
                        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                        {
                            debugInfo.AppendLine("用户取消了元素选择");
                            return Result.Cancelled;
                        }
                    }

                    if (elemIdList.Count < 1)
                    {
                        InfoCenterTip.Show("未选择到任何有效的 MEP 元素，操作终止。");
                        return Result.Failed;
                    }

                    // ---------- 2. 获取角度（子类决定是否弹窗） ----------
                    if (!TryGetAngleDeg(uidoc, out double angleDeg))
                    {
                        debugInfo.AppendLine("用户取消了角度输入");
                        return Result.Cancelled;
                    }
                    if (angleDeg <= 0 || angleDeg > 90)
                    {
                        InfoCenterTip.Show($"角度 θ={angleDeg:F1}° 超出允许范围 (0, 90]。");
                        return Result.Failed;
                    }
                    debugInfo.AppendLine($"[{CommandTitle}] 参数：θ = {angleDeg:F1}°");

                    // ---------- 3. 拾取 P1 ----------
                    Reference ref1 = uidoc.Selection.PickObject(
                        ObjectType.PointOnElement,
                        new MEPCurveSelectionFilter(),
                        $"【{CommandTitle}】第 1/3 步：在参考管上拾取 P1（拐弯起点，避开弯头/三通）");

                    Element refElem = doc.GetElement(ref1.ElementId);
                    LocationCurve refLocCurve = refElem?.Location as LocationCurve;
                    if (refLocCurve == null)
                    {
                        InfoCenterTip.Show("参考元素无定位曲线！");
                        return Result.Failed;
                    }
                    Curve refCurve = refLocCurve.Curve;
                    XYZ breakPt = ref1.GlobalPoint;

                    // ---------- 4. 拾取 P2 ----------
                    XYZ sidePt;
                    try
                    {
                        sidePt = uidoc.Selection.PickPoint(
                            $"【{CommandTitle}】第 2/3 步：拾取 P2（决定偏移距离与方向）\n" +
                            "⚠ P2 到 P1 的横向垂直距离将作为偏移量 H，\n" +
                            "   请点在参考管的侧方（左或右），不要点在轴线上。");
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        debugInfo.AppendLine("用户取消了 P2 拾取");
                        return Result.Cancelled;
                    }

                    // ---------- 5. 计算全局 frame ----------
                    GlobalFrame frame = MEPLateralBendCalculator.ComputeGlobalFrame(
                        refCurve, breakPt, sidePt, angleDeg);

                    if (frame == null || !frame.IsValid)
                    {
                        string frameErr = frame?.ErrorMessage ?? "全局坐标系计算失败";
                        InfoCenterTip.Show($"计算失败: {frameErr}");
                        return Result.Failed;
                    }

                    debugInfo.AppendLine($"自动计算偏移量 H = {frame.AbsOffset * 304.8:F1} mm");
                    debugInfo.AppendLine($"斜段沿管投影   = {frame.ProjLen * 304.8:F1} mm（= H/tan(θ)）");

                    // ---------- 6. 事务处理 ----------
                    MEPLateralBendService service = new MEPLateralBendService(debugInfo);
                    int successCount = 0, failCount = 0;

                    using (Transaction trans = new Transaction(doc, CommandTitle))
                    {
                        trans.Start();
                        foreach (ElementId eid in elemIdList)
                        {
                            try
                            {
                                bool ok = service.ProcessSingleElement(doc, eid, frame);
                                if (ok) successCount++; else failCount++;
                            }
                            catch (Exception ex)
                            {
                                debugInfo.LogException($"元素 {eid.IntegerValue} 处理失败", ex);
                                trans.RollBack();
                                InfoCenterTip.Show($"处理中止: 元素 {eid.IntegerValue} 失败，已回滚");
                                return Result.Failed;
                            }
                        }
                        trans.Commit();
                    }

                    debugInfo.AppendLine($"\n[{CommandTitle}] 处理完成：成功 {successCount}，失败 {failCount}");
                    InfoCenterTip.Show($"{CommandTitle} 完成: 成功 {successCount}, 失败 {failCount}");
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
                    debugInfo.LogException("全局异常", ex);
                    InfoCenterTip.Show($"操作失败: {ex.Message}");
                    return Result.Failed;
                }
                finally
                {
                    MEPLogger.Instance.EndSession("See log file above");
                }
            }
        }

        // ---------- 公共辅助 ----------
        private ICollection<ElementId> GetTaggedElementIds(IndependentTag tag)
        {
            try
            {
                var refs = tag.GetTaggedReferences();
                return refs.Select(r => r.ElementId).ToList();
            }
            catch
            {
                var linkIds = tag.GetTaggedElementIds();
                if (linkIds == null) return new List<ElementId>();

                var res = new List<ElementId>();
                foreach (var linkId in linkIds)
                {
                    if (linkId.HostElementId != ElementId.InvalidElementId)
                        res.Add(linkId.HostElementId);
                    else if (linkId.LinkedElementId != ElementId.InvalidElementId)
                        res.Add(linkId.LinkedElementId);
                }
                return res;
            }
        }

        private bool IsValidMEPElement(Element elem)
        {
            if (elem == null) return false;
            return elem is Pipe || elem is Duct || elem is CableTray;
        }

        private class MEPCurveSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is Pipe || elem is Duct || elem is CableTray;
            public bool AllowReference(Reference reference, XYZ position) => true;
        }
    }
}