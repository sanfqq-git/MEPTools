using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            MEPLogger.Instance.StartSession("Duct Resize Tool");

            try
            {
                ResizeConfig config = LoadOrCreateConfig();
                ICollection<ElementId> ids = uidoc.Selection.GetElementIds();

                if (ids.Count == 0)
                {
                    try
                    {
                        IList<Reference> refs = uidoc.Selection.PickObjects(ObjectType.Element, "请选择风管或管件");
                        ids = refs.Select(r => r.ElementId).ToList();
                    }
                    catch (OperationCanceledException)
                    {
                        MEPLogger.Instance.EndSession("Cancelled");
                        return Result.Cancelled;
                    }
                }
                if (ids.Count == 0) return Result.Cancelled;

                double w = UnitUtils.ConvertToInternalUnits(config.TargetWidthMm, UnitTypeId.Millimeters);
                double h = UnitUtils.ConvertToInternalUnits(config.TargetHeightMm, UnitTypeId.Millimeters);

                var ducts = new List<Duct>();
                var fittings = new List<FamilyInstance>();

                foreach (var id in ids)
                {
                    Element e = doc.GetElement(id);
                    if (e is Duct duct && duct.DuctType.Shape == ConnectorProfileType.Rectangular)
                        ducts.Add(duct);
                    else if (e is FamilyInstance fi && fi.MEPModel is MechanicalFitting)
                        fittings.Add(fi);
                }

                if (ducts.Count == 0 && fittings.Count == 0)
                {
                    MEPLogger.Instance.EndSession("Failed - No valid elements");
                    return Result.Cancelled;
                }

                using (Transaction tr = new Transaction(doc, "修改风管及管件参数尺寸"))
                {
                    FailureHandlingOptions options = tr.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(new DuctFailureHandler());
                    tr.SetFailureHandlingOptions(options);
                    tr.Start();

                    // =========================================================
                    // 1. 处理风管（判断是否翻转）
                    // =========================================================
                    foreach (var duct in ducts)
                    {
                        bool isFlipped = IsDuctFlipped(duct);
                        double setW = isFlipped ? h : w;
                        double setH = isFlipped ? w : h;

                        duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.Set(setW);
                        duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)?.Set(setH);
                    }

                    doc.Regenerate();

                    // =========================================================
                    // 2. 处理管件（解决 45度转角错乱问题）
                    // =========================================================
                    foreach (var fi in fittings)
                    {
                        // 策略一：优先尝试使用内置参数，内置参数能像 Options Bar 一样自动处理旋转映射
                        Parameter pW = fi.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                        Parameter pH = fi.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);

                        if (pW != null && !pW.IsReadOnly && pH != null && !pH.IsReadOnly)
                        {
                            pW.Set(w);
                            pH.Set(h);
                        }
                        else
                        {
                            // 策略二：如果管件不支持内置参数，则判断是否为非正交转角（如45度），若是则交换宽高
                            bool swapParams = IsNonOrthogonalFitting(fi);
                            double setW = swapParams ? h : w;
                            double setH = swapParams ? w : h;

                            foreach (var paramName in config.WidthParams)
                            {
                                Parameter p = fi.LookupParameter(paramName);
                                if (p != null && !p.IsReadOnly) p.Set(setW);
                            }

                            foreach (var paramName in config.HeightParams)
                            {
                                Parameter p = fi.LookupParameter(paramName);
                                if (p != null && !p.IsReadOnly) p.Set(setH);
                            }
                        }
                    }

                    doc.Regenerate();
                    tr.Commit();
                }

                MEPLogger.Instance.EndSession($"Success - Modified {ducts.Count} ducts, {fittings.Count} fittings");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("Command.Execute", ex);
                MEPLogger.Instance.EndSession("Error - Exception Caught");

                using (var spaceFinisher = new SpaceKeyFinisher(System.Windows.Forms.Keys.Space, "完成", "Finish", "OK"))
                {
                    spaceFinisher.Enable();
                    TaskDialog.Show("错误", $"执行过程中发生异常：\n{ex.Message}\n\n如果界面出现被卡住的对话框，可按【空格键】尝试跳过。");
                }
                return Result.Failed;
            }
        }

        /// <summary>
        /// 判断风管是否因为方向翻转导致宽高反置
        /// </summary>
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
                    // 如果连接器的 X 轴与风管物理方向相反，说明被翻转了
                    if (basisX.DotProduct(dir) < -0.001)
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 判断管件是否为非正交状态（如45度弯头），这类管件通常需要交换宽高参数
        /// </summary>
        private bool IsNonOrthogonalFitting(FamilyInstance fi)
        {
            if (fi.MEPModel == null || fi.MEPModel.ConnectorManager == null) return false;

            var connectors = new List<Connector>();
            foreach (Connector c in fi.MEPModel.ConnectorManager.Connectors)
            {
                if (c.ConnectorType == ConnectorType.End) connectors.Add(c);
            }

            // 1. 检查管件两个接口的夹角。如果是 45 度弯头，夹角不是 90 度（也不是 180 度直线）
            if (connectors.Count == 2)
            {
                XYZ dir1 = connectors[0].CoordinateSystem.BasisZ;
                XYZ dir2 = connectors[1].CoordinateSystem.BasisZ;
                double angle = dir1.AngleTo(dir2) * 180 / Math.PI;
                
                // 如果夹角偏离 90 度，说明是异形管件（如 45度、60度弯头）
                if (Math.Abs(angle - 90) > 5 && Math.Abs(angle - 180) > 5)
                {
                    return true;
                }
            }

            // 2. 检查管件自身的局部坐标系是否发生非 90 度的旋转（针对某些在水平面被旋转放置的弯头）
            Transform t = fi.GetTransform();
            double rotAngle = Math.Atan2(t.BasisX.Y, t.BasisX.X) * 180 / Math.PI;
            rotAngle = (rotAngle + 360) % 360;
            
            bool isRotated = !(
                Math.Abs(rotAngle) < 5 || 
                Math.Abs(rotAngle - 90) < 5 || 
                Math.Abs(rotAngle - 180) < 5 || 
                Math.Abs(rotAngle - 270) < 5
            );
            
            if (isRotated)
            {
                return true;
            }

            return false;
        }

        private ResizeConfig LoadOrCreateConfig()
        {
            var config = new ResizeConfig();
            string dllPath = Assembly.GetExecutingAssembly().Location;
            string configPath = Path.Combine(Path.GetDirectoryName(dllPath) ?? "", "DuctResizeConfig.txt");

            if (!File.Exists(configPath))
            {
                config.WidthParams = new List<string> { "Width", "宽度", "Duct Width 1", "Duct Width 2", "Duct Width 3" };
                config.HeightParams = new List<string> { "Height", "高度", "Duct Height" };
                try
                {
                    File.WriteAllLines(configPath, new[]
                    {
                        "# 目标尺寸设置 (单位: mm)",
                        "TargetWidth=150",
                        "TargetHeight=400",
                        "",
                        "# 宽度参数名 (多个参数用英文逗号分隔，用于管件)",
                        "WidthParams=Width,宽度,Duct Width 1,Duct Width 2,Duct Width 3",
                        "",
                        "# 高度参数名 (多个参数用英文逗号分隔，用于管件)",
                        "HeightParams=Height,高度,Duct Height"
                    });
                }
                catch { }
                return config;
            }

            try
            {
                foreach (var line in File.ReadAllLines(configPath))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;

                    if (line.StartsWith("TargetWidth="))
                    {
                        if (double.TryParse(line.Replace("TargetWidth=", "").Trim(), out double w)) config.TargetWidthMm = w;
                    }
                    else if (line.StartsWith("TargetHeight="))
                    {
                        if (double.TryParse(line.Replace("TargetHeight=", "").Trim(), out double h)) config.TargetHeightMm = h;
                    }
                    else if (line.StartsWith("WidthParams="))
                    {
                        config.WidthParams = line.Replace("WidthParams=", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
                    }
                    else if (line.StartsWith("HeightParams="))
                    {
                        config.HeightParams = line.Replace("HeightParams=", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
                    }
                }
            }
            catch
            {
                config.WidthParams = new List<string> { "Width", "宽度", "Duct Width 1", "Duct Width 2", "Duct Width 3" };
                config.HeightParams = new List<string> { "Height", "高度", "Duct Height" };
            }

            return config;
        }
    }

    public class ResizeConfig
    {
        public double TargetWidthMm { get; set; } = 150.0;
        public double TargetHeightMm { get; set; } = 400.0;
        public List<string> WidthParams { get; set; } = new List<string>();
        public List<string> HeightParams { get; set; } = new List<string>();
    }
}