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
        // 拦截普通警告，防止弹窗干扰，并记录到内存日志
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

            // 初始化日志
            MEPLogger.Instance.StartSession("Duct Resize Tool");

            try
            {
                // 1. 读取配置
                ResizeConfig config = LoadOrCreateConfig();

                // 2. 获取选中元素
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

                // =========================================================
                // 核心执行逻辑（保持原有的高速运行体验）
                // =========================================================
                using (Transaction tr = new Transaction(doc, "修改风管及管件参数尺寸"))
                {
                    FailureHandlingOptions options = tr.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(new DuctFailureHandler());
                    tr.SetFailureHandlingOptions(options);

                    tr.Start();

                    // 阶段一：修改宽度
                    foreach (var duct in ducts)
                        duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.Set(w);

                    foreach (var fi in fittings)
                    {
                        foreach (var paramName in config.WidthParams)
                        {
                            Parameter p = fi.LookupParameter(paramName);
                            if (p != null && !p.IsReadOnly) p.Set(w);
                        }
                    }
                    
                    // 强制刷新宽度
                    doc.Regenerate();

                    // 阶段二：修改高度
                    foreach (var duct in ducts)
                        duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)?.Set(h);

                    foreach (var fi in fittings)
                    {
                        foreach (var paramName in config.HeightParams)
                        {
                            Parameter p = fi.LookupParameter(paramName);
                            if (p != null && !p.IsReadOnly) p.Set(h);
                        }
                    }

                    // 强制刷新高度
                    doc.Regenerate();

                    tr.Commit();
                }

                MEPLogger.Instance.EndSession($"Success - Modified {ducts.Count} ducts, {fittings.Count} fittings");
                
                // 保持你的原样，静默执行，不弹窗
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("Command.Execute", ex);
                MEPLogger.Instance.EndSession("Error - Exception Caught");
                
                // 【兜底机制】：只有在发生严重异常时，才启动热键监听
                using (var spaceFinisher = new SpaceKeyFinisher(System.Windows.Forms.Keys.Space, "完成", "Finish", "OK"))
                {
                    spaceFinisher.Enable();
                    TaskDialog.Show("错误", $"执行过程中发生异常：\n{ex.Message}\n\n如果界面出现被卡住的对话框，可按【空格键】尝试跳过。");
                }
                
                return Result.Failed;
            }
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
                        "TargetWidth=100",
                        "TargetHeight=100",
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

                    // 【微调优化】使用 TryParse 防止用户写错字符导致整个插件崩溃
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
        public double TargetWidthMm { get; set; } = 100.0;
        public double TargetHeightMm { get; set; } = 100.0;
        public List<string> WidthParams { get; set; } = new List<string>();
        public List<string> HeightParams { get; set; } = new List<string>();
    }
}