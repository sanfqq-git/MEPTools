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
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            MEPLogger.Instance.StartSession("Duct Resize Tool");

            try
            {
                // 1. 弹出配置窗口
                ResizeConfig oldConfig = ResizeConfigStorage.Load();
                ResizeConfig newConfig = DuctResizeConfigForm.Show(oldConfig);
                
                if (newConfig == null)
                {
                    InfoCenterTip.Show("已取消");
                    MEPLogger.Instance.EndSession("Cancelled - Config dialog closed");
                    return Result.Cancelled;
                }
                
                // 记录并保存新配置
                ResizeConfigStorage.Save(newConfig);
                MEPLogger.Instance.AppendLine($"目标尺寸: {newConfig.TargetWidthMm} x {newConfig.TargetHeightMm} mm");

                // 2. 智能选择元素
                var selectionFilter = new DuctFittingSelectionFilter(doc);
                List<Element> selectedElements = MEPSelectionHelper.GetElements(
                    uidoc,
                    selectionFilter,
                    "请选择风管或管件，按【空格键】结束",
                    "请在视图中选择风管或管件，按【空格键】结束",
                    minCount: 1,
                    useSpaceKey: true);

                if (selectedElements == null || selectedElements.Count == 0)
                    return Result.Cancelled;

                // 3. 分类
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
                    InfoCenterTip.Show("未找到可处理的风管或管件");
                    MEPLogger.Instance.EndSession("Failed - No valid elements");
                    return Result.Cancelled;
                }

                double w = UnitUtils.ConvertToInternalUnits(newConfig.TargetWidthMm, UnitTypeId.Millimeters);
                double h = UnitUtils.ConvertToInternalUnits(newConfig.TargetHeightMm, UnitTypeId.Millimeters);

                using (Transaction tr = new Transaction(doc, "修改风管及管件参数尺寸"))
                {
                    FailureHandlingOptions options = tr.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(new DuctFailureHandler());
                    tr.SetFailureHandlingOptions(options);
                    tr.Start();

                    // 4. 处理风管 (加入强制 Regenerate 防止宽高覆盖)
                    foreach (var duct in ducts)
                    {
                        bool isFlipped = IsDuctFlipped(duct);
                        double setW = isFlipped ? h : w;
                        double setH = isFlipped ? w : h;

                        Parameter pW = duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                        Parameter pH = duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);

                        if (pW != null && !pW.IsReadOnly)
                        {
                            pW.Set(setW);
                            doc.Regenerate(); // 关键：设置宽度后重生成，防止高度设置被覆盖
                        }

                        if (pH != null && !pH.IsReadOnly)
                        {
                            pH.Set(setH);
                            doc.Regenerate();
                        }
                    }

                    // 5. 处理管件 (使用智能反查)
                    foreach (var fi in fittings)
                    {
                        UpdateFittingSize(fi, w, h, newConfig, doc);
                        doc.Regenerate(); // 每个管件完成后刷新
                    }

                    tr.Commit();
                }

                InfoCenterTip.Show($"已完成：风管 {ducts.Count} 根，管件 {fittings.Count} 个");
                MEPLogger.Instance.EndSession($"Success - Modified {ducts.Count} ducts, {fittings.Count} fittings");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("Command.Execute", ex);
                MEPLogger.Instance.EndSession("Error - Exception Caught");
                InfoCenterTip.Show($"执行出错：{ex.Message}");
                return Result.Failed;
            }
        }

        /// <summary>
        /// 更新单个管件的宽度和高度（Connector反查优先 + 内置参数兜底 + 自定义参数兜底）
        /// </summary>
        private void UpdateFittingSize(FamilyInstance fi, double targetW, double targetH, ResizeConfig config, Document doc)
        {
            // 1. 前置判断：如果是非正交管件，必须先互换宽高
            bool swapParams = IsNonOrthogonalFitting(fi);
            double setW = swapParams ? targetH : targetW;
            double setH = swapParams ? targetW : targetH;

            bool widthSet = false;
            bool heightSet = false;

            // 策略 1：通过 Connector 反查关联的族参数（最智能、准确）
            if (fi.MEPModel?.ConnectorManager != null)
            {
                foreach (Connector connector in fi.MEPModel.ConnectorManager.Connectors)
                {
                    if (connector.ConnectorType != ConnectorType.End) continue;

                    MEPFamilyConnectorInfo connInfo = connector.GetMEPConnectorInfo() as MEPFamilyConnectorInfo;
                    if (connInfo == null) continue;

                    // 反查并设置宽度
                    if (!widthSet)
                    {
                        ElementId widthParamId = connInfo.GetAssociateFamilyParameterId(new ElementId(BuiltInParameter.CONNECTOR_WIDTH));
                        if (widthParamId != ElementId.InvalidElementId)
                        {
                            Parameter pW = GetParameterByElementId(fi, widthParamId);
                            if (pW != null && !pW.IsReadOnly)
                            {
                                pW.Set(setW);
                                widthSet = true;
                                doc.Regenerate(); // 关键：防止高度覆盖宽度
                            }
                        }
                    }

                    // 反查并设置高度
                    if (!heightSet)
                    {
                        ElementId heightParamId = connInfo.GetAssociateFamilyParameterId(new ElementId(BuiltInParameter.CONNECTOR_HEIGHT));
                        if (heightParamId != ElementId.InvalidElementId)
                        {
                            Parameter pH = GetParameterByElementId(fi, heightParamId);
                            if (pH != null && !pH.IsReadOnly)
                            {
                                pH.Set(setH);
                                heightSet = true;
                                doc.Regenerate();
                            }
                        }
                    }

                    if (widthSet && heightSet) break;
                }
            }

            // 策略 2：如果 Connector 没找到，尝试内置参数（常见于系统自带的标准族）
            if (!widthSet)
            {
                Parameter pW = fi.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                if (pW != null && !pW.IsReadOnly)
                {
                    pW.Set(setW);
                    widthSet = true;
                    doc.Regenerate();
                }
            }
            if (!heightSet)
            {
                Parameter pH = fi.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
                if (pH != null && !pH.IsReadOnly)
                {
                    pH.Set(setH);
                    heightSet = true;
                    doc.Regenerate();
                }
            }

            // 策略 3：兜底策略，使用用户配置的硬编码参数名匹配
            if (!widthSet)
            {
                foreach (var paramName in config.WidthParams)
                {
                    Parameter p = fi.LookupParameter(paramName);
                    if (p != null && !p.IsReadOnly)
                    {
                        p.Set(setW);
                        widthSet = true;
                        doc.Regenerate();
                        break; // 找到了就跳过剩余的参数名
                    }
                }
            }
            if (!heightSet)
            {
                foreach (var paramName in config.HeightParams)
                {
                    Parameter p = fi.LookupParameter(paramName);
                    if (p != null && !p.IsReadOnly)
                    {
                        p.Set(setH);
                        heightSet = true;
                        doc.Regenerate();
                        break; // 找到了就跳过剩余的参数名
                    }
                }
            }
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