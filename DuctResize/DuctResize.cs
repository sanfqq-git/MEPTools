// ============================================================
// 文件：DuctResize.cs
// 位置：DuctResize 文件夹
// 修改重点：
//   1) 配置读写全部走 Shared/MEPConfig（原 DuctResizeConfig.txt 废弃）
//   2) 选择走 Shared/Preselected → MEPSelectionHelper（预选优先 + InfoCenter 提示 + 空格结束）
//   3) 所有写参数前做“值比较守卫”，并在无实际写入时 RollBack，避免污染 Last changed by
// ============================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using MEPTools.Shared;

namespace MEPTools.DuctResize
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Command : IExternalCommand
    {
        public class DuctFailureHandler : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                foreach (var f in failuresAccessor.GetFailureMessages())
                {
                    if (f.GetSeverity() == FailureSeverity.Warning)
                    {
                        MEPLogger.Instance.AppendLine($"[Warning Filtered] {f.GetDescriptionText()}");
                        failuresAccessor.DeleteWarning(f);
                    }
                }
                return FailureProcessingResult.Continue;
            }
        }

        private class DuctFittingSelectionFilter : ISelectionFilter
        {
            private readonly Document _doc;
            public DuctFittingSelectionFilter(Document doc) { _doc = doc; }

            public bool AllowElement(Element elem)
            {
                if (elem is Duct duct
                    && duct.DuctType != null
                    && duct.DuctType.Shape == ConnectorProfileType.Rectangular)
                    return true;

                if (elem is FamilyInstance fi && fi.MEPModel is MechanicalFitting)
                    return true;

                return false;
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                if (reference == null) return false;
                return AllowElement(_doc.GetElement(reference));
            }
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData?.Application?.ActiveUIDocument;
            if (uidoc == null || uidoc.Document == null)
            {
                message = "没有可用的活动文档。";
                return Result.Cancelled;
            }
            Document doc = uidoc.Document;

            MEPLogger.Instance.StartSession("Duct Resize Tool");

            try
            {
                // ---------- 1. 共享配置 + 配置窗口 ----------
                ResizeConfig oldConfig = ResizeConfig.Load();
                ResizeConfig newConfig = DuctResizeConfigForm.Show(oldConfig);

                if (newConfig == null)
                {
                    InfoCenterTip.Show(MEPConfig.GetString("DuctResize.Msg.Cancelled", "已取消"));
                    MEPLogger.Instance.EndSession("Cancelled - Config dialog closed");
                    return Result.Cancelled;
                }

                // 确定后写回 MEPTools.config（只写文件，不触碰文档）
                newConfig.Save();
                MEPLogger.Instance.AppendLine($"目标尺寸: {newConfig.TargetWidthMm} x {newConfig.TargetHeightMm} mm");

                // ---------- 2. 选择元素（有预选用预选，无预选气泡提示后交互拾取） ----------
                var selectionFilter = new DuctFittingSelectionFilter(doc);
                List<Element> selectedElements = Preselected.GetSelectedElements(
                    uidoc,
                    selectionFilter,
                    MEPConfig.GetString("DuctResize.PickPrompt", "请选择风管或管件，按【空格键】结束"),
                    MEPConfig.GetString("DuctResize.Msg.NoPreselection",
                        "未检测到预选：请在视图中选择风管或管件，按空格键结束"));

                if (selectedElements == null || selectedElements.Count == 0)
                {
                    // 取消/数量不足的气泡与日志已由 MEPSelectionHelper 统一处理，这里不再重复提示
                    MEPLogger.Instance.EndSession("Cancelled - No elements");
                    return Result.Cancelled;
                }

                MEPLogger.Instance.AppendLine($"已获取 {selectedElements.Count} 个候选元素。");

                // ---------- 3. 分类 ----------
                var ducts = new List<Duct>();
                var fittings = new List<FamilyInstance>();

                foreach (var e in selectedElements)
                {
                    if (e is Duct duct
                        && duct.DuctType != null
                        && duct.DuctType.Shape == ConnectorProfileType.Rectangular)
                        ducts.Add(duct);
                    else if (e is FamilyInstance fi && fi.MEPModel is MechanicalFitting)
                        fittings.Add(fi);
                }

                if (ducts.Count == 0 && fittings.Count == 0)
                {
                    InfoCenterTip.Show(MEPConfig.GetString("DuctResize.Msg.NoValid",
                        "所选元素中没有可处理的风管或管件"));
                    MEPLogger.Instance.EndSession("Failed - No valid elements");
                    return Result.Cancelled;
                }

                double w = UnitUtils.ConvertToInternalUnits(newConfig.TargetWidthMm, UnitTypeId.Millimeters);
                double h = UnitUtils.ConvertToInternalUnits(newConfig.TargetHeightMm, UnitTypeId.Millimeters);
                double tolFt = MEPConfig.GetDouble("DuctResize.SizeToleranceMm", 0.001) / 304.8;

                int changedDucts = 0, changedFittings = 0, skipped = 0, failed = 0;

                // ---------- 4. 写入（每个写操作前先比较，值相同则不 Set） ----------
                using (Transaction tr = new Transaction(doc, "修改风管及管件参数尺寸"))
                {
                    FailureHandlingOptions options = tr.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(new DuctFailureHandler());
                    tr.SetFailureHandlingOptions(options);
                    tr.Start();

                    foreach (var duct in ducts)
                    {
                        try
                        {
                            if (ResizeOneDuct(duct, w, h, tolFt, doc)) changedDucts++;
                            else skipped++;
                        }
                        catch (Exception ex)
                        {
                            MEPLogger.Instance.LogException($"ResizeDuct[{duct?.Id}]", ex);
                            failed++;
                        }
                    }

                    foreach (var fi in fittings)
                    {
                        try
                        {
                            if (UpdateFittingSize(fi, w, h, newConfig, tolFt, doc)) changedFittings++;
                            else skipped++;
                        }
                        catch (Exception ex)
                        {
                            MEPLogger.Instance.LogException($"ResizeFitting[{fi?.Id}]", ex);
                            failed++;
                        }
                    }

                    // 没有任何实际写入 → 回滚，连一次“空提交”的痕迹都不留
                    if (changedDucts + changedFittings == 0)
                        tr.RollBack();
                    else
                        tr.Commit();
                }

                // ---------- 5. 结果反馈 ----------
                if (changedDucts + changedFittings == 0)
                {
                    InfoCenterTip.Show(MEPConfig.GetString("DuctResize.Msg.NoChange",
                        "尺寸已是目标值，未做任何修改"));
                    MEPLogger.Instance.EndSession(
                        $"NoChange - ducts={ducts.Count}, fittings={fittings.Count}, skipped={skipped}, failed={failed}");
                    return Result.Succeeded;
                }

                string msg = string.Format(
                    MEPConfig.GetString("DuctResize.Msg.Summary",
                        "已完成：风管 {0} 根，管件 {1} 个（无需修改 {2} 个）"),
                    changedDucts, changedFittings, skipped);

                if (failed > 0)
                {
                    msg += string.Format(
                        MEPConfig.GetString("DuctResize.Msg.SummaryFailed", "；失败 {0} 个，详见日志"),
                        failed);
                }

                InfoCenterTip.Show(msg);
                MEPLogger.Instance.EndSession(
                    $"Success - ducts={changedDucts}, fittings={changedFittings}, skipped={skipped}, failed={failed}");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("Command.Execute", ex);
                MEPLogger.Instance.EndSession("Error - Exception Caught");
                InfoCenterTip.Show(string.Format(
                    MEPConfig.GetString("DuctResize.Msg.Error", "执行出错：{0}"), ex.Message));
                return Result.Failed;
            }
        }

        /// <summary>
        /// 处理单根风管：宽高任一实际变化才算“已修改”。
        /// 保持原顺序（先宽后高，每次真正写入后 Regenerate），防止高度设置覆盖宽度。
        /// </summary>
        private bool ResizeOneDuct(Duct duct, double targetW, double targetH, double tolFt, Document doc)
        {
            if (duct == null) return false;

            try
            {
                if (duct.Pinned)
                {
                    MEPLogger.Instance.AppendLine($"[Skip] Duct {duct.Id} 已锁定");
                    return false;
                }
            }
            catch { }

            bool isFlipped = IsDuctFlipped(duct);
            double setW = isFlipped ? targetH : targetW;
            double setH = isFlipped ? targetW : targetH;

            Parameter pW = duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
            Parameter pH = duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);

            bool changed = false;

            // 关键：设置宽度后重生成，防止高度设置被覆盖
            if (SetIfDifferent(pW, setW, tolFt))
            {
                changed = true;
                doc.Regenerate();
            }

            if (SetIfDifferent(pH, setH, tolFt))
            {
                changed = true;
                doc.Regenerate();
            }

            if (!changed)
                MEPLogger.Instance.AppendLine($"[NoChange] Duct {duct.Id} 尺寸已是目标值");

            return changed;
        }

        /// <summary>
        /// 更新单个管件的宽度和高度（Connector反查优先 + 内置参数兜底 + 自定义参数兜底）。
        /// 每个候选参数都先比较值，已是目标值则视为“命中”，不再写入。
        /// </summary>
        private bool UpdateFittingSize(FamilyInstance fi, double targetW, double targetH,
            ResizeConfig config, double tolFt, Document doc)
        {
            if (fi == null) return false;

            try
            {
                if (fi.Pinned)
                {
                    MEPLogger.Instance.AppendLine($"[Skip] Fitting {fi.Id} 已锁定");
                    return false;
                }
            }
            catch { }

            // 1. 前置判断：如果是非正交管件，必须先互换宽高
            bool swapParams = IsNonOrthogonalFitting(fi);
            double setW = swapParams ? targetH : targetW;
            double setH = swapParams ? targetW : targetH;

            bool widthDone = false;
            bool heightDone = false;
            bool changed = false;

            // 策略 1：通过 Connector 反查关联的族参数（最智能、准确）
            if (fi.MEPModel?.ConnectorManager != null)
            {
                foreach (Connector connector in fi.MEPModel.ConnectorManager.Connectors)
                {
                    if (connector.ConnectorType != ConnectorType.End) continue;

                    MEPFamilyConnectorInfo connInfo = connector.GetMEPConnectorInfo() as MEPFamilyConnectorInfo;
                    if (connInfo == null) continue;

                    // 反查并设置宽度
                    if (!widthDone)
                    {
                        Parameter pW = GetAssociatedParam(fi, connInfo, BuiltInParameter.CONNECTOR_WIDTH);
                        if (pW != null && !pW.IsReadOnly && pW.StorageType == StorageType.Double)
                        {
                            if (IsCloseEnough(pW, setW, tolFt))
                            {
                                widthDone = true;   // 已是目标值：命中，但不写
                            }
                            else if (pW.Set(setW))
                            {
                                widthDone = true;
                                changed = true;
                                doc.Regenerate();   // 关键：防止高度覆盖宽度
                            }
                        }
                    }

                    // 反查并设置高度
                    if (!heightDone)
                    {
                        Parameter pH = GetAssociatedParam(fi, connInfo, BuiltInParameter.CONNECTOR_HEIGHT);
                        if (pH != null && !pH.IsReadOnly && pH.StorageType == StorageType.Double)
                        {
                            if (IsCloseEnough(pH, setH, tolFt))
                            {
                                heightDone = true;
                            }
                            else if (pH.Set(setH))
                            {
                                heightDone = true;
                                changed = true;
                                doc.Regenerate();
                            }
                        }
                    }

                    if (widthDone && heightDone) break;
                }
            }

            // 策略 2：Connector 没找到，尝试内置参数（常见于系统自带的标准族）
            if (!widthDone)
            {
                Parameter pW = fi.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                if (pW != null && !pW.IsReadOnly && pW.StorageType == StorageType.Double)
                {
                    if (IsCloseEnough(pW, setW, tolFt))
                    {
                        widthDone = true;
                    }
                    else if (pW.Set(setW))
                    {
                        widthDone = true;
                        changed = true;
                        doc.Regenerate();
                    }
                }
            }
            if (!heightDone)
            {
                Parameter pH = fi.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
                if (pH != null && !pH.IsReadOnly && pH.StorageType == StorageType.Double)
                {
                    if (IsCloseEnough(pH, setH, tolFt))
                    {
                        heightDone = true;
                    }
                    else if (pH.Set(setH))
                    {
                        heightDone = true;
                        changed = true;
                        doc.Regenerate();
                    }
                }
            }

            // 策略 3：兜底策略，使用用户配置的参数名匹配
            if (!widthDone)
            {
                foreach (var paramName in config.WidthParams ?? new List<string>())
                {
                    Parameter p = fi.LookupParameter(paramName);
                    if (p == null || p.IsReadOnly || p.StorageType != StorageType.Double) continue;

                    if (IsCloseEnough(p, setW, tolFt))
                    {
                        widthDone = true;
                        break;   // 参数命中且值已达标，无需再找其他名字
                    }
                    if (p.Set(setW))
                    {
                        widthDone = true;
                        changed = true;
                        doc.Regenerate();
                        break;
                    }
                }
            }
            if (!heightDone)
            {
                foreach (var paramName in config.HeightParams ?? new List<string>())
                {
                    Parameter p = fi.LookupParameter(paramName);
                    if (p == null || p.IsReadOnly || p.StorageType != StorageType.Double) continue;

                    if (IsCloseEnough(p, setH, tolFt))
                    {
                        heightDone = true;
                        break;
                    }
                    if (p.Set(setH))
                    {
                        heightDone = true;
                        changed = true;
                        doc.Regenerate();
                        break;
                    }
                }
            }

            if (!changed)
                MEPLogger.Instance.AppendLine($"[NoChange] Fitting {fi.Id} 尺寸已是目标值");

            return changed;
        }

        /// <summary>
        /// 写入守卫：值已在容差内则不 Set（避免无意义写操作改变 Last changed by）。
        /// </summary>
        private static bool SetIfDifferent(Parameter p, double target, double tolFt)
        {
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.Double) return false;
            if (IsCloseEnough(p, target, tolFt)) return false;
            return p.Set(target);
        }

        private static bool IsCloseEnough(Parameter p, double target, double tolFt)
        {
            try { return Math.Abs(p.AsDouble() - target) <= tolFt; }
            catch { return false; }
        }

        /// <summary>
        /// 由 Connector 的关联族参数 Id 取到管件实例上的参数
        /// </summary>
        private Parameter GetAssociatedParam(FamilyInstance fi, MEPFamilyConnectorInfo connInfo, BuiltInParameter bip)
        {
            ElementId paramId = connInfo.GetAssociateFamilyParameterId(new ElementId(bip));
            if (paramId == null || paramId == ElementId.InvalidElementId) return null;
            return GetParameterByElementId(fi, paramId);
        }

        /// <summary>
        /// 根据 ElementId 获取管件实例上的参数
        /// </summary>
        private Parameter GetParameterByElementId(FamilyInstance fi, ElementId paramId)
        {
            // 参数不属于 Element 类，不能使用 doc.GetElement()。
            // 在 FamilyInstance 的参数集合中按 Id 匹配
            foreach (Parameter p in fi.Parameters)
            {
                if (p.Id == paramId)
                    return p;
            }
            return null;
        }

        private bool IsDuctFlipped(Duct duct)
        {
            LocationCurve locCurve = duct.Location as LocationCurve;
            if (locCurve == null) return false;

            XYZ dir = (locCurve.Curve.GetEndPoint(1) - locCurve.Curve.GetEndPoint(0)).Normalize();
            ConnectorManager cm = duct.ConnectorManager;
            if (cm == null) return false;

            foreach (Connector c in cm.Connectors)
            {
                if (c.ConnectorType == ConnectorType.End)
                {
                    XYZ basisX = c.CoordinateSystem.BasisX;
                    if (basisX.DotProduct(dir) < -0.001)
                        return true;
                }
            }
            return false;
        }

        private bool IsNonOrthogonalFitting(FamilyInstance fi)
        {
            if (fi.MEPModel == null || fi.MEPModel.ConnectorManager == null) return false;

            var connectors = new List<Connector>();
            foreach (Connector c in fi.MEPModel.ConnectorManager.Connectors)
            {
                if (c.ConnectorType == ConnectorType.End) connectors.Add(c);
            }

            if (connectors.Count == 2)
            {
                XYZ dir1 = connectors[0].CoordinateSystem.BasisZ;
                XYZ dir2 = connectors[1].CoordinateSystem.BasisZ;
                double angle = dir1.AngleTo(dir2) * 180 / Math.PI;

                if (Math.Abs(angle - 90) > 5 && Math.Abs(angle - 180) > 5)
                    return true;
            }

            Transform t = fi.GetTransform();
            double rotAngle = Math.Atan2(t.BasisX.Y, t.BasisX.X) * 180 / Math.PI;
            rotAngle = (rotAngle + 360) % 360;

            bool isRotated = !(
                Math.Abs(rotAngle) < 5 ||
                Math.Abs(rotAngle - 90) < 5 ||
                Math.Abs(rotAngle - 180) < 5 ||
                Math.Abs(rotAngle - 270) < 5
            );

            return isRotated;
        }
    }
}
