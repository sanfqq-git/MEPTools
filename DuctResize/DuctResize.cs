using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using MEPTools.Shared;

// === 冲突类型别名：Autodesk 命名空间与 .NET UI 命名空间同名 ===
using Form    = System.Windows.Forms.Form;
using TextBox = System.Windows.Forms.TextBox;
using Panel   = System.Windows.Forms.Panel;
using Point   = System.Drawing.Point;
using Color   = System.Drawing.Color;

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

        private class RevitWindowWrapper : IWin32Window
        {
            public IntPtr Handle { get; }
            public RevitWindowWrapper(IntPtr handle) { Handle = handle; }
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            MEPLogger.Instance.StartSession("Duct Resize Tool");

            try
            {
                // 1. 弹出配置窗口
                ResizeConfig config = PromptConfig();
                if (config == null)
                {
                    InfoCenterTip.Show("已取消");
                    MEPLogger.Instance.EndSession("Cancelled - Config dialog closed");
                    return Result.Cancelled;
                }

                MEPLogger.Instance.AppendLine(
                    $"目标尺寸: {config.TargetWidthMm} x {config.TargetHeightMm} mm");

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

                double w = UnitUtils.ConvertToInternalUnits(config.TargetWidthMm, UnitTypeId.Millimeters);
                double h = UnitUtils.ConvertToInternalUnits(config.TargetHeightMm, UnitTypeId.Millimeters);

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

        private ResizeConfig PromptConfig()
        {
            // 优化：将配置文件写入 LocalAppData，避免 Program Files 目录无写入权限导致崩溃
            string configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MEPTools");
            Directory.CreateDirectory(configDir);
            string configPath = Path.Combine(configDir, "DuctResizeConfig.txt");

            ResizeConfig config = ReadConfig(configPath);

            if (!File.Exists(configPath))
                SaveConfig(configPath, config);

            using (var form = new DuctResizeConfigForm(config))
            {
                var owner = new RevitWindowWrapper(Autodesk.Windows.ComponentManager.ApplicationWindow);
                DialogResult dr = form.ShowDialog(owner);
                if (dr != DialogResult.OK)
                    return null;

                ResizeConfig newConfig = form.GetConfig();
                SaveConfig(configPath, newConfig);
                return newConfig;
            }
        }

        private ResizeConfig ReadConfig(string path)
        {
            var config = new ResizeConfig
            {
                WidthParams = new List<string> { "Width", "宽度", "Duct Width 1", "Duct Width 2", "Duct Width 3" },
                HeightParams = new List<string> { "Height", "高度", "Duct Height" },
            };

            if (!File.Exists(path)) return config;

            try
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "TargetWidth":
                            if (double.TryParse(val, out double w)) config.TargetWidthMm = w;
                            break;
                        case "TargetHeight":
                            if (double.TryParse(val, out double h)) config.TargetHeightMm = h;
                            break;
                        case "WidthParams":
                            config.WidthParams = SplitCsv(val);
                            break;
                        case "HeightParams":
                            config.HeightParams = SplitCsv(val);
                            break;
                    }
                }
            }
            catch { }

            return config;
        }

        private void SaveConfig(string path, ResizeConfig config)
        {
            try
            {
                File.WriteAllLines(path, new[]
                {
                    "# 目标尺寸设置 (单位: mm)",
                    "TargetWidth=" + config.TargetWidthMm,
                    "TargetHeight=" + config.TargetHeightMm,
                    "",
                    "# 宽度参数名 (多个参数用英文逗号分隔，用于管件)",
                    "WidthParams=" + string.Join(",", config.WidthParams ?? new List<string>()),
                    "",
                    "# 高度参数名 (多个参数用英文逗号分隔，用于管件)",
                    "HeightParams=" + string.Join(",", config.HeightParams ?? new List<string>()),
                });
            }
            catch { }
        }

        private static List<string> SplitCsv(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return new List<string>();
            return s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => x.Length > 0)
                    .ToList();
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

    public class ResizeConfig
    {
        public double TargetWidthMm { get; set; } = 150.0;
        public double TargetHeightMm { get; set; } = 400.0;
        public List<string> WidthParams { get; set; } = new List<string>();
        public List<string> HeightParams { get; set; } = new List<string>();
    }

    // 继承自我们刚刚提取的 SpaceSubmitForm 类
    public class DuctResizeConfigForm : SpaceSubmitForm
    {
        private readonly ResizeConfig _source;
        private NumericUpDown _nudWidth;
        private NumericUpDown _nudHeight;
        private TextBox _txtWidthParams;
        private TextBox _txtHeightParams;
        private Panel _advPanel;
        private Button _btnAdvanced;
        private Button _btnOk;
        private Button _btnCancel;

        private const int CollapsedHeight = 220;
        private const int ExpandedHeight = 330;

        public DuctResizeConfigForm(ResizeConfig config)
        {
            _source = config ?? new ResizeConfig();
            BuildUi();
            LoadValues();
        }

        private void BuildUi()
        {
            Text = "风管尺寸修改";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 9F);
            ClientSize = new Size(360, CollapsedHeight);

            Controls.Add(new Label { Text = "目标宽度 (mm)：", Location = new Point(20, 25), AutoSize = true });
            _nudWidth = new NumericUpDown
            {
                Location = new Point(150, 22),
                Width = 175,
                Minimum = 1,
                Maximum = 10000,
                DecimalPlaces = 1,
                Increment = 10,
            };
            Controls.Add(_nudWidth);

            Controls.Add(new Label { Text = "目标高度 (mm)：", Location = new Point(20, 65), AutoSize = true });
            _nudHeight = new NumericUpDown
            {
                Location = new Point(150, 62),
                Width = 175,
                Minimum = 1,
                Maximum = 10000,
                DecimalPlaces = 1,
                Increment = 10,
            };
            Controls.Add(_nudHeight);

            _btnAdvanced = new Button
            {
                Text = "高级参数 >>",
                Location = new Point(20, 100),
                Width = 120,
                Height = 26,
            };
            _btnAdvanced.Click += (s, e) => ToggleAdvanced();
            Controls.Add(_btnAdvanced);

            _advPanel = new Panel
            {
                Location = new Point(0, 130),
                Size = new Size(ClientSize.Width, 90),
                Visible = false,
            };
            _advPanel.Controls.Add(new Label { Text = "宽度参数名：", Location = new Point(20, 8), AutoSize = true });
            _txtWidthParams = new TextBox
            {
                Location = new Point(150, 5),
                Width = 175,
            };
            _advPanel.Controls.Add(_txtWidthParams);
            _advPanel.Controls.Add(new Label { Text = "高度参数名：", Location = new Point(20, 40), AutoSize = true });
            _txtHeightParams = new TextBox
            {
                Location = new Point(150, 37),
                Width = 175,
            };
            _advPanel.Controls.Add(_txtHeightParams);
            Controls.Add(_advPanel);

            _btnOk = new Button
            {
                Text = "确定",
                DialogResult = DialogResult.OK,
                Location = new Point(150, 180),
                Width = 80,
                Height = 30,
            };
            _btnCancel = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Location = new Point(240, 180),
                Width = 80,
                Height = 30,
            };
            Controls.Add(_btnOk);
            Controls.Add(_btnCancel);

            AcceptButton = _btnOk;
            CancelButton = _btnCancel;
        }

        private void ToggleAdvanced()
        {
            if (_advPanel.Visible)
            {
                _advPanel.Visible = false;
                _btnAdvanced.Text = "高级参数 >>";
                ClientSize = new Size(ClientSize.Width, CollapsedHeight);
                _btnOk.Top = _btnCancel.Top = 180;
            }
            else
            {
                _advPanel.Visible = true;
                _btnAdvanced.Text = "高级参数 <<";
                ClientSize = new Size(ClientSize.Width, ExpandedHeight);
                _btnOk.Top = _btnCancel.Top = 280;
            }
        }

        private void LoadValues()
        {
            _nudWidth.Value = ClampDecimal((decimal)_source.TargetWidthMm, _nudWidth);
            _nudHeight.Value = ClampDecimal((decimal)_source.TargetHeightMm, _nudHeight);
            _txtWidthParams.Text = string.Join(",", _source.WidthParams ?? new List<string>());
            _txtHeightParams.Text = string.Join(",", _source.HeightParams ?? new List<string>());
        }

        private static decimal ClampDecimal(decimal v, NumericUpDown nud)
        {
            if (v < nud.Minimum) return nud.Minimum;
            if (v > nud.Maximum) return nud.Maximum;
            return v;
        }

        public ResizeConfig GetConfig()
        {
            return new ResizeConfig
            {
                TargetWidthMm = (double)_nudWidth.Value,
                TargetHeightMm = (double)_nudHeight.Value,
                WidthParams = SplitCsv(_txtWidthParams.Text),
                HeightParams = SplitCsv(_txtHeightParams.Text),
            };
        }

        private static List<string> SplitCsv(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return new List<string>();
            return s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => x.Length > 0)
                    .ToList();
        }
    }
}