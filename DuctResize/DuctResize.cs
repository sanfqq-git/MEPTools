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
                // 1. 弹出配置窗口 (封装在 Form 内部)
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

                    // 4. 风管
                    foreach (var duct in ducts)
                    {
                        bool isFlipped = IsDuctFlipped(duct);
                        double setW = isFlipped ? h : w;
                        double setH = isFlipped ? w : h;

                        duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.Set(setW);
                        duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)?.Set(setH);
                    }

                    doc.Regenerate();

                    // 5. 管件
                    foreach (var fi in fittings)
                    {
                        Parameter pW = fi.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                        Parameter pH = fi.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);

                        if (pW != null && !pW.IsReadOnly && pH != null && !pH.IsReadOnly)
                        {
                            pW.Set(w);
                            pH.Set(h);
                        }
                        else
                        {
                            bool swapParams = IsNonOrthogonalFitting(fi);
                            double setW = swapParams ? h : w;
                            double setH = swapParams ? w : h;

                            foreach (var paramName in newConfig.WidthParams)
                            {
                                Parameter p = fi.LookupParameter(paramName);
                                if (p != null && !p.IsReadOnly) p.Set(setW);
                            }

                            foreach (var paramName in newConfig.HeightParams)
                            {
                                Parameter p = fi.LookupParameter(paramName);
                                if (p != null && !p.IsReadOnly) p.Set(setH);
                            }
                        }
                    }

                    doc.Regenerate();
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