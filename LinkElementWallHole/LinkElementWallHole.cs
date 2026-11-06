using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Drawing;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

using MEPTools.Shared;

namespace MEPTools.LinkElementWallHole
{
    // ================================================================
    // 【1】Command：命令入口 / 用户交互 / 流程编排
    // ================================================================
    [Transaction(TransactionMode.Manual)]
    public class Command : IExternalCommand
    {
        public double OFFSET_MM;
        public string FAMILY_NAME;
        public string SYMBOL_NAME;
        public string LEVEL_NAME;
        public double CABLE_TRAY_EXTRA_HEIGHT_MM;
        public string PARAM_WIDTH_NAME;
        public string PARAM_HEIGHT_NAME;
        public string PARAM_DEPTH_NAME;
        public int TARGET_CATEGORY_ID;
        public string TARGET_CATEGORY_NAME;
        public double MATCH_TOLERANCE_MM;

        private const double MM_PER_FOOT = HoleCalc.MM_PER_FOOT;
        private const double EPS = HoleCalc.EPS;

        private static void Log(string s) => MEPLogger.Instance.AppendLine(s);
        private static string FmtMm(double ft) => HoleCalc.FmtMm(ft);
        private static string Fmt(XYZ p) => HoleCalc.Fmt(p);
        private static string FmtDir(XYZ d) => HoleCalc.FmtDir(d);
        private static string FmtOutline(Outline o) => HoleCalc.FmtOutline(o);

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            MEPLogger.Instance.StartSession("链接墙洞创建/更新");

            try
            {
                LoadConfig();

                IList<Element> preSelectedHoles = GetPreSelectedHoles(uidoc);
                if (preSelectedHoles.Count > 0)
                {
                    Log($"[Command] 预选墙洞 {preSelectedHoles.Count} 个 → 更新流程。");
                    return RunUpdateBranch(doc, uidoc, preSelectedHoles);
                }
                else
                {
                    Log("[Command] 未预选墙洞 → 新建流程。");
                    return RunCreateBranch(doc, uidoc);
                }
            }
            catch (OperationCanceledException)
            {
                Log("[Command] 用户取消操作。");
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("Command.Execute", ex);
                message = ex.Message;
                return Result.Failed;
            }
            finally
            {
                MEPLogger.Instance.EndSession("命令结束");
            }
        }

        private Result RunUpdateBranch(Document doc, UIDocument uidoc, IList<Element> preSelectedHoles)
        {
            IList<Reference> pipeRefs;
            try
            {
                using (var finisher = new SpaceKeyFinisher())
                {
                    finisher.Enable();
                    pipeRefs = uidoc.Selection.PickObjects(
                        ObjectType.LinkedElement,
                        new LinkMEPSelectionFilter(doc),
                        $"已识别预选的 {preSelectedHoles.Count} 个墙洞。\n请框选/点选要关联的 MEP\n按空格结束");
                }
            }
            catch (OperationCanceledException) { throw new Exception("未选择任何 MEP，无法更新。"); }

            if (!HoleBiz.ParsePipeReferences(doc, pipeRefs, out _, out var pipeElements, out var pipeTf))
                throw new Exception("未选中任何有效的 MEP，或它们不在同一个链接中。");

            var holeRefs = preSelectedHoles.Select(h => new Reference(h)).ToList();
            return UpdateExistingHoles(doc, holeRefs, pipeElements, pipeTf);
        }

        private Result RunCreateBranch(Document doc, UIDocument uidoc)
        {
            IList<Reference> pipeRefs;
            try
            {
                using (var finisher = new SpaceKeyFinisher())
                {
                    finisher.Enable();
                    pipeRefs = uidoc.Selection.PickObjects(
                        ObjectType.LinkedElement,
                        new LinkMEPSelectionFilter(doc),
                        "【新建洞 - 步骤 1 / 2】\n请选择 1~N 个需要开洞的 MEP\n按空格结束");
                }
            }
            catch (OperationCanceledException) { throw new Exception("未选择任何 MEP，操作取消。"); }

            if (!HoleBiz.ParsePipeReferences(doc, pipeRefs, out _, out var pipeElements, out var pipeTf))
                throw new Exception("未选中任何有效的 MEP，或它们不在同一个链接中。");

            Reference wallRef;
            try
            {
                using (var finisher = new SpaceKeyFinisher())
                {
                    finisher.Enable();
                    wallRef = uidoc.Selection.PickObject(
                        ObjectType.LinkedElement,
                        new LinkWallSelectionFilter(doc),
                        "【新建洞 - 步骤 2 / 2】\n请选择 1 面需要开洞的链接墙");
                }
            }
            catch (OperationCanceledException) { throw new Exception("未选择墙，操作取消。"); }

            return CreateNewHole(doc, uidoc, wallRef, pipeElements, pipeTf);
        }

        private IList<Element> GetPreSelectedHoles(UIDocument uidoc)
        {
            List<Element> holes = new List<Element>();
            Document doc = uidoc.Document;
            foreach (ElementId id in uidoc.Selection.GetElementIds())
            {
                Element elem = doc.GetElement(id);
                if (elem is FamilyInstance fi &&
                    fi.Symbol?.Family?.Name == FAMILY_NAME &&
                    fi.Category?.Id.IntegerValue == TARGET_CATEGORY_ID)
                {
                    holes.Add(fi);
                }
            }
            return holes;
        }

        // ==========================================================
        // 分支 A：创建新洞
        // ==========================================================
        private Result CreateNewHole(Document doc, UIDocument uidoc, Reference wallRef,
            List<Element> pipeElements, Transform pipeTf)
        {
            Log("[CreateNewHole] 开始创建新洞。");

            RevitLinkInstance wallLinkInstance = doc.GetElement(wallRef.ElementId) as RevitLinkInstance;
            if (wallLinkInstance == null) throw new Exception("未识别的墙体链接。");
            Document wallLinkDoc = wallLinkInstance.GetLinkDocument();
            if (wallLinkDoc == null) throw new Exception("墙体链接文档无法读取。");
            Wall targetWall = wallLinkDoc.GetElement(wallRef.LinkedElementId) as Wall;
            if (targetWall == null) throw new Exception("选中的不是墙。");

            LocationCurve wallLoc = targetWall.Location as LocationCurve;
            Line wallLine = wallLoc?.Curve as Line;
            if (wallLine == null) throw new Exception("仅支持直线墙");

            Transform wallTf = wallLinkInstance.GetTotalTransform();
            Line wallWorldLine = wallLine.CreateTransformed(wallTf) as Line;
            if (wallWorldLine == null) throw new Exception("墙线变换失败。");

            XYZ wallDir = wallWorldLine.Direction.Normalize();
            XYZ wallOrigin = wallWorldLine.GetEndPoint(0);
            double wallThickness = targetWall.Width;
            XYZ wallNormal = wallDir.CrossProduct(XYZ.BasisZ).Normalize();

            double minU = double.MaxValue, maxU = double.MinValue;
            double minZproj = double.MaxValue, maxZproj = double.MinValue;
            Element highestElement = null;

            // 按照用户图的逻辑，沿管线两端各延长预留偏移量，然后求截面的投影极值
            foreach (Element elem in pipeElements)
            {
                double tMinU, tMaxU, tMinZ, tMaxZ;
                if (HoleBiz.GetMepCrossSectionBoundary(elem, pipeTf, wallOrigin, wallDir, wallNormal, wallThickness, OFFSET_MM, out tMinU, out tMaxU, out tMinZ, out tMaxZ))
                {
                    if (tMinU < minU) minU = tMinU;
                    if (tMaxU > maxU) maxU = tMaxU;
                    if (tMinZ < minZproj) minZproj = tMinZ;
                    if (tMaxZ > maxZproj) { maxZproj = tMaxZ; highestElement = elem; }
                    
                    Log($"[CreateNewHole] MEP {elem.Id} 截面包络: u=[{FmtMm(tMinU)}, {FmtMm(tMaxU)}] z=[{FmtMm(tMinZ)}, {FmtMm(tMaxZ)}]");
                }
            }

            if (minU > maxU || minZproj > maxZproj)
                throw new Exception("无法计算出有效的洞口尺寸。");

            // 因为上面算出的极值已经把轴向的偏移算进去了，这里再加上正交方向的外围偏移量
            double offsetFeet = OFFSET_MM / MM_PER_FOOT;
            double width = (maxU - minU) + 2 * offsetFeet;
            double height = (maxZproj - minZproj) + 2 * offsetFeet;
            
            double trayExtra = (highestElement is CableTray) ? CABLE_TRAY_EXTRA_HEIGHT_MM / MM_PER_FOOT : 0.0;
            height += trayExtra;
            double depth = wallThickness;

            XYZ insertPt = wallOrigin + (minU + maxU) / 2 * wallDir;
            insertPt = new XYZ(insertPt.X, insertPt.Y, minZproj - offsetFeet);

            Log($"[CreateNewHole] 结果尺寸 width={FmtMm(width)} height={FmtMm(height)} depth={FmtMm(depth)} insertPt={Fmt(insertPt)}");

            FamilySymbol holeSymbol = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(s => s.Family.Name == FAMILY_NAME && s.Name == SYMBOL_NAME);

            if (holeSymbol == null)
            {
                TaskDialog dialog = new TaskDialog("未找到墙洞族");
                dialog.MainInstruction = "当前项目未找到指定的墙洞族";
                dialog.MainContent = $"配置族：{FAMILY_NAME} - {SYMBOL_NAME}\n\n是否立即打开设置界面重新选择族？";
                dialog.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;

                if (dialog.Show() == TaskDialogResult.Yes)
                {
                    using (var form = new WallHoleConfigForm(doc, this))
                    {
                        if (form.ShowDialog() == DialogResult.OK)
                        {
                            holeSymbol = new FilteredElementCollector(doc)
                                .OfClass(typeof(FamilySymbol))
                                .Cast<FamilySymbol>()
                                .FirstOrDefault(s => s.Family.Name == FAMILY_NAME && s.Name == SYMBOL_NAME);
                            if (holeSymbol == null) throw new Exception("设置完成，但仍然找不到选定的族。");
                        }
                        else throw new Exception("用户取消了设置。");
                    }
                }
                else throw new Exception($"未找到族：{FAMILY_NAME}。");
            }

            Level level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault(l => l.Name == LEVEL_NAME);
            if (level == null)
                throw new Exception($"未找到标高：{LEVEL_NAME}");

            using (Transaction tran = new Transaction(doc, "创建链接墙洞"))
            {
                tran.Start();
                if (!holeSymbol.IsActive) holeSymbol.Activate();

                FamilyInstance hole = doc.Create.NewFamilyInstance(
                    insertPt, holeSymbol, level,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                if (!(hole.Location is LocationPoint))
                    throw new Exception($"族 '{FAMILY_NAME}' 不是基于标高的可自由放置族。请更换族。");

                ElementId holeId = hole.Id;
                HoleBiz.RotateToWall(doc, holeId, wallDir, insertPt);

                FamilyInstance holeAfter = doc.GetElement(holeId) as FamilyInstance;
                
                bool okDepth = HoleBiz.SetParam(holeAfter, PARAM_DEPTH_NAME, depth);
                bool okWidth = HoleBiz.SetParam(holeAfter, PARAM_WIDTH_NAME, width);
                bool okHeight = HoleBiz.SetParam(holeAfter, PARAM_HEIGHT_NAME, height);

                if (!okWidth || !okHeight || !okDepth)
                {
                    tran.RollBack();
                    throw new Exception(
                        $"墙洞族缺少可写参数！\n" +
                        $"当前族：{FAMILY_NAME}-{SYMBOL_NAME}\n" +
                        $"宽度参数 '{PARAM_WIDTH_NAME}' 写入{(okWidth ? "成功" : "失败")}\n" +
                        $"高度参数 '{PARAM_HEIGHT_NAME}' 写入{(okHeight ? "成功" : "失败")}\n" +
                        $"深度参数 '{PARAM_DEPTH_NAME}' 写入{(okDepth ? "成功" : "失败")}\n" +
                        $"请打开面板设置确认参数名是否正确。");
                }

                tran.Commit();
            }

            return Result.Succeeded;
        }

        // ==========================================================
        // 分支 B：更新现有洞
        // ==========================================================
        private Result UpdateExistingHoles(Document doc, IList<Reference> holeRefs,
            List<Element> pipeElements, Transform pipeTf)
        {
            using (Transaction tran = new Transaction(doc, "根据MEP更新墙洞"))
            {
                tran.Start();
                int updated = 0;
                int skippedNoMatch = 0;
                int paramWriteFailCount = 0;

                foreach (Reference hr in holeRefs)
                {
                    FamilyInstance hole = doc.GetElement(hr.ElementId) as FamilyInstance;
                    if (hole == null) { skippedNoMatch++; continue; }
                    
                    LocationPoint lp = hole.Location as LocationPoint;
                    if (lp == null) continue;

                    XYZ hand = hole.HandOrientation;
                    XYZ facing = hole.FacingOrientation;
                    XYZ wallDir;
                    if (Math.Abs(hand.Z) < 0.01) wallDir = new XYZ(hand.X, hand.Y, 0).Normalize();
                    else if (Math.Abs(facing.Z) < 0.01) wallDir = new XYZ(-facing.Y, facing.X, 0).Normalize();
                    else
                    {
                        var proj = new XYZ(hand.X, hand.Y, 0);
                        wallDir = proj.GetLength() > EPS ? proj.Normalize() : XYZ.BasisX;
                    }
                    XYZ wallNormal = wallDir.CrossProduct(XYZ.BasisZ).Normalize();

                    // 匹配管线
                    Dictionary<Element, Outline> pipeOutlines = new Dictionary<Element, Outline>();
                    foreach (var pipe in pipeElements)
                    {
                        BoundingBoxXYZ pb = pipe.get_BoundingBox(null);
                        if (pb != null) pipeOutlines[pipe] = HoleCalc.GetWorldOutline(pb, pipeTf);
                    }

                    List<Element> relatedPipes = (holeRefs.Count == 1) 
                        ? pipeElements 
                        : HoleBiz.MatchPipesToHole(hole, pipeOutlines, MATCH_TOLERANCE_MM);

                    if (relatedPipes.Count == 0) { skippedNoMatch++; continue; }

                    // 更新时，墙厚通常取自洞族的深度参数
                    double wallThickness = hole.LookupParameter(PARAM_DEPTH_NAME)?.AsDouble() ?? 0.2;
                    
                    XYZ wallOrigin = new XYZ(lp.Point.X, lp.Point.Y, 0);
                    double minU = double.MaxValue, maxU = double.MinValue;
                    double minZ = double.MaxValue, maxZ = double.MinValue;
                    Element highestElement = null;

                    foreach (var pipe in relatedPipes)
                    {
                        double tMinU, tMaxU, tMinZ, tMaxZ;
                        if (HoleBiz.GetMepCrossSectionBoundary(pipe, pipeTf, wallOrigin, wallDir, wallNormal, wallThickness, OFFSET_MM, out tMinU, out tMaxU, out tMinZ, out tMaxZ))
                        {
                            if (tMinU < minU) minU = tMinU;
                            if (tMaxU > maxU) maxU = tMaxU;
                            if (tMinZ < minZ) minZ = tMinZ;
                            if (tMaxZ > maxZ) { maxZ = tMaxZ; highestElement = pipe; }
                        }
                    }

                    if (minU > maxU || minZ > maxZ) { skippedNoMatch++; continue; }

                    double offsetFeet = OFFSET_MM / MM_PER_FOOT;
                    double width = (maxU - minU) + 2 * offsetFeet;
                    double height = (maxZ - minZ) + 2 * offsetFeet;
                    double trayExtra = (highestElement is CableTray) ? CABLE_TRAY_EXTRA_HEIGHT_MM / MM_PER_FOOT : 0.0;
                    height += trayExtra;

                    double depth = wallThickness;
                    if (depth <= 0.0) depth = 200.0 / MM_PER_FOOT;

                    bool okW = HoleBiz.SetParam(hole, PARAM_WIDTH_NAME, width);
                    bool okH = HoleBiz.SetParam(hole, PARAM_HEIGHT_NAME, height);
                    bool okD = HoleBiz.SetParam(hole, PARAM_DEPTH_NAME, depth);

                    if (!okW || !okH || !okD)
                    {
                        paramWriteFailCount++;
                        skippedNoMatch++;
                        continue;
                    }

                    XYZ posNow = (hole.Location as LocationPoint)?.Point ?? lp.Point;
                    XYZ targetXY = wallOrigin + ((minU + maxU) / 2) * wallDir;
                    XYZ targetPos = new XYZ(targetXY.X, targetXY.Y, minZ - offsetFeet);
                    XYZ delta = targetPos - posNow;
                    if (delta.GetLength() > EPS)
                        ElementTransformUtils.MoveElement(doc, hole.Id, delta);

                    updated++;
                }

                tran.Commit();
                if (updated == 0)
                {
                    throw new Exception($"未更新任何墙洞。\n" +
                        $"可能原因：空间不重叠，或族缺少可写参数（失败 {paramWriteFailCount} 个）。");
                }
            }
            return Result.Succeeded;
        }

        // ==========================================================
        // 配置读写
        // ==========================================================
        public void LoadConfig()
        {
            OFFSET_MM = 100.0; FAMILY_NAME = "REWallHole"; SYMBOL_NAME = "Rect CBWD for Wall";
            LEVEL_NAME = "WCS 0.0"; CABLE_TRAY_EXTRA_HEIGHT_MM = 0.0;
            PARAM_WIDTH_NAME = "NDH_Width"; PARAM_HEIGHT_NAME = "NDH_Height"; PARAM_DEPTH_NAME = "NDH_Depth";
            TARGET_CATEGORY_ID = (int)BuiltInCategory.OST_GenericModel; TARGET_CATEGORY_NAME = "OST_GenericModel";
            MATCH_TOLERANCE_MM = 300.0;

            try
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "config.txt");
                if (System.IO.File.Exists(path))
                {
                    var validLines = System.IO.File.ReadAllLines(path)
                        .Select(l => { int idx = l.IndexOf('#'); return idx >= 0 ? l.Substring(0, idx).Trim() : l.Trim(); })
                        .Where(l => !string.IsNullOrEmpty(l)).ToList();

                    if (validLines.Count >= 5)
                    {
                        if (double.TryParse(validLines[0], out double off)) OFFSET_MM = off;
                        FAMILY_NAME = validLines[1]; SYMBOL_NAME = validLines[2]; LEVEL_NAME = validLines[3];
                        if (double.TryParse(validLines[4], out double tray)) CABLE_TRAY_EXTRA_HEIGHT_MM = tray;

                        if (validLines.Count > 5 && !string.IsNullOrEmpty(validLines[5])) PARAM_WIDTH_NAME = validLines[5];
                        if (validLines.Count > 6 && !string.IsNullOrEmpty(validLines[6])) PARAM_HEIGHT_NAME = validLines[6];
                        if (validLines.Count > 7 && !string.IsNullOrEmpty(validLines[7])) PARAM_DEPTH_NAME = validLines[7];

                        if (validLines.Count > 8 && Enum.TryParse<BuiltInCategory>(validLines[8], true, out var bic))
                        {
                            TARGET_CATEGORY_ID = (int)bic; TARGET_CATEGORY_NAME = bic.ToString();
                        }

                        if (validLines.Count > 9 && double.TryParse(validLines[9], out double tol) && tol > 0) MATCH_TOLERANCE_MM = tol;
                    }
                }
            }
            catch { }
        }

        public void SaveConfigToFile()
        {
            try
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "config.txt");
                var lines = new List<string>
                {
                    "# ============================================================",
                    "# MEPTools - 链接墙洞创建/更新 配置文件",
                    "# ============================================================",
                    OFFSET_MM.ToString(), FAMILY_NAME, SYMBOL_NAME, LEVEL_NAME, CABLE_TRAY_EXTRA_HEIGHT_MM.ToString(),
                    PARAM_WIDTH_NAME, PARAM_HEIGHT_NAME, PARAM_DEPTH_NAME, TARGET_CATEGORY_NAME, MATCH_TOLERANCE_MM.ToString()
                };
                System.IO.File.WriteAllLines(path, lines);
            }
            catch { }
        }

        // ============== 选择过滤器 ==============
        private class LinkMEPSelectionFilter : ISelectionFilter
        {
            private readonly Document _doc;
            public LinkMEPSelectionFilter(Document doc) { _doc = doc; }
            public bool AllowElement(Element elem) => elem is RevitLinkInstance;
            public bool AllowReference(Reference reference, XYZ position)
            {
                var inst = _doc.GetElement(reference.ElementId) as RevitLinkInstance;
                var doc = inst?.GetLinkDocument();
                var elem = doc?.GetElement(reference.LinkedElementId);
                return HoleBiz.IsSupportedMep(elem);
            }
        }

        private class LinkWallSelectionFilter : ISelectionFilter
        {
            private readonly Document _doc;
            public LinkWallSelectionFilter(Document doc) { _doc = doc; }
            public bool AllowElement(Element elem) => elem is RevitLinkInstance;
            public bool AllowReference(Reference reference, XYZ position)
            {
                var inst = _doc.GetElement(reference.ElementId) as RevitLinkInstance;
                var doc = inst?.GetLinkDocument();
                var elem = doc?.GetElement(reference.LinkedElementId);
                return elem is Wall || elem?.Category?.Id.IntegerValue == (int)BuiltInCategory.OST_Walls;
            }
        }
    }

    // ================================================================
    // 【1.5】独立命令入口：打开设置界面
    // ================================================================
    [Transaction(TransactionMode.Manual)]
    public class Cmd_WallHoleSettings : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument.Document;
            var mainCmd = new Command();
            mainCmd.LoadConfig();
            using (var form = new WallHoleConfigForm(doc, mainCmd))
            {
                if (form.ShowDialog() == DialogResult.OK) InfoCenterTip.Show("配置已保存！");
            }
            return Result.Succeeded;
        }
    }

    // ================================================================
    // 【1.6】UI 设置窗体
    // ================================================================
    public class WallHoleConfigForm : MEPTools.Shared.SpaceSubmitForm
    {
        private Document _doc;
        private Command _cmd;

        private NumericUpDown nudOffset, nudTrayExtra, nudTolerance;
        private System.Windows.Forms.ComboBox cmbFamily, cmbSymbol, cmbLevel, cmbCategory;
        private System.Windows.Forms.TextBox txtParamWidth, txtParamHeight, txtParamDepth;

        public WallHoleConfigForm(Document doc, Command cmd)
        {
            _doc = doc; _cmd = cmd;
            InitializeComponent();
            LoadRevitData();
            LoadConfigData();
        }

        private void InitializeComponent()
        {
            this.Text = "墙洞工具 - 参数设置";
            this.Size = new System.Drawing.Size(440, 560);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false; this.MinimizeBox = false;

            int labelX = 25, inputX = 160, y = 20, gap = 36;
            ToolTip tip = new ToolTip { AutoPopDelay = 8000, InitialDelay = 200 };

            AddLabel("四周预留偏移(mm):", labelX, y);
            nudOffset = new NumericUpDown { Location = new System.Drawing.Point(inputX, y), Width = 230, Maximum = 1000, DecimalPlaces = 1 };
            tip.SetToolTip(nudOffset, "开出的洞口尺寸比管线大出来的边距。\n例如填 100，斜穿时沿轴线两端各延长100。");
            this.Controls.Add(nudOffset); y += gap;

            AddLabel("桥架附加高(mm):", labelX, y);
            nudTrayExtra = new NumericUpDown { Location = new System.Drawing.Point(inputX, y), Width = 230, Maximum = 1000, DecimalPlaces = 1 };
            tip.SetToolTip(nudTrayExtra, "如果在最高处遇到的是桥架（而不是管道或风管），\n顶部额外增加的高度。");
            this.Controls.Add(nudTrayExtra); y += gap;

            AddLabel("空间关联范围(mm):", labelX, y);
            nudTolerance = new NumericUpDown { Location = new System.Drawing.Point(inputX, y), Width = 230, Maximum = 5000, DecimalPlaces = 1 };
            tip.SetToolTip(nudTolerance, "更新多个墙洞时，程序会在洞口周围多大范围内寻找管线。\n如果管线离洞口太远（超过这个范围），则不会被关联更新。\n一般保持 300 即可。");
            this.Controls.Add(nudTolerance); y += gap + 10;

            AddLabel("墙洞族 (Family):", labelX, y);
            cmbFamily = new System.Windows.Forms.ComboBox { Location = new System.Drawing.Point(inputX, y), Width = 230, DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList };
            cmbFamily.SelectedIndexChanged += CmbFamily_SelectedIndexChanged;
            this.Controls.Add(cmbFamily); y += gap;

            AddLabel("族类型 (Type):", labelX, y);
            cmbSymbol = new System.Windows.Forms.ComboBox { Location = new System.Drawing.Point(inputX, y), Width = 230, DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList };
            this.Controls.Add(cmbSymbol); y += gap;

            AddLabel("放置标高 (Level):", labelX, y);
            cmbLevel = new System.Windows.Forms.ComboBox { Location = new System.Drawing.Point(inputX, y), Width = 230, DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList };
            this.Controls.Add(cmbLevel); y += gap;

            AddLabel("目标类别 (Category):", labelX, y);
            cmbCategory = new System.Windows.Forms.ComboBox { Location = new System.Drawing.Point(inputX, y), Width = 230, DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList };
            cmbCategory.Items.AddRange(new object[] { "OST_GenericModel", "OST_Windows", "OST_Doors", "OST_MechanicalEquipment" });
            this.Controls.Add(cmbCategory); y += gap + 10;

            AddLabel("宽度参数名:", labelX, y);
            txtParamWidth = new System.Windows.Forms.TextBox { Location = new System.Drawing.Point(inputX, y), Width = 230 };
            tip.SetToolTip(txtParamWidth, "填写的参数名必须在选定的族中存在，\n且必须是长度(Length)类型的参数。");
            this.Controls.Add(txtParamWidth); y += gap;

            AddLabel("高度参数名:", labelX, y);
            txtParamHeight = new System.Windows.Forms.TextBox { Location = new System.Drawing.Point(inputX, y), Width = 230 };
            this.Controls.Add(txtParamHeight); y += gap;

            AddLabel("深度参数名:", labelX, y);
            txtParamDepth = new System.Windows.Forms.TextBox { Location = new System.Drawing.Point(inputX, y), Width = 230 };
            tip.SetToolTip(txtParamDepth, "必须填写！如果填错或族里没有此参数，\n生成的洞口将没有厚度（表现为看不见或是一条白线）。");
            this.Controls.Add(txtParamDepth); y += gap + 15;

            var btnOK = new Button { Text = "保存并应用", Location = new System.Drawing.Point(80, y), Width = 100, Height = 32 };
            btnOK.Click += BtnOK_Click;
            this.Controls.Add(btnOK); this.AcceptButton = btnOK;

            var btnCancel = new Button { Text = "取消", Location = new System.Drawing.Point(230, y), Width = 100, Height = 32, DialogResult = DialogResult.Cancel };
            this.Controls.Add(btnCancel);
        }

        private void AddLabel(string text, int x, int y) => this.Controls.Add(new Label { Text = text, Location = new System.Drawing.Point(x, y + 3), AutoSize = true });

        private void LoadRevitData()
        {
            cmbLevel.DataSource = new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>().Select(l => l.Name).OrderBy(n => n).ToList();
            cmbFamily.DataSource = new FilteredElementCollector(_doc).OfClass(typeof(Family)).Cast<Family>()
                .Where(f => f.IsEditable && f.FamilyCategory != null).Select(f => f.Name).Distinct().OrderBy(n => n).ToList();
        }

        private void CmbFamily_SelectedIndexChanged(object sender, EventArgs e)
        {
            string famName = cmbFamily.SelectedItem as string;
            if (string.IsNullOrEmpty(famName)) return;
            cmbSymbol.DataSource = new FilteredElementCollector(_doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => s.Family.Name == famName).Select(s => s.Name).OrderBy(n => n).ToList();
        }

        private void LoadConfigData()
        {
            nudOffset.Value = (decimal)_cmd.OFFSET_MM;
            nudTrayExtra.Value = (decimal)_cmd.CABLE_TRAY_EXTRA_HEIGHT_MM;
            nudTolerance.Value = (decimal)_cmd.MATCH_TOLERANCE_MM;
            
            cmbFamily.Text = _cmd.FAMILY_NAME; cmbSymbol.Text = _cmd.SYMBOL_NAME;
            cmbLevel.Text = _cmd.LEVEL_NAME; cmbCategory.Text = _cmd.TARGET_CATEGORY_NAME;
            txtParamWidth.Text = _cmd.PARAM_WIDTH_NAME; txtParamHeight.Text = _cmd.PARAM_HEIGHT_NAME; txtParamDepth.Text = _cmd.PARAM_DEPTH_NAME;
        }

        private void BtnOK_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(cmbFamily.Text) || string.IsNullOrWhiteSpace(cmbSymbol.Text))
            { InfoCenterTip.Show("族和类型不能为空！"); return; }
            if (string.IsNullOrWhiteSpace(txtParamWidth.Text) || string.IsNullOrWhiteSpace(txtParamHeight.Text) || string.IsNullOrWhiteSpace(txtParamDepth.Text))
            { InfoCenterTip.Show("宽/高/深参数名不能为空！"); return; }

            _cmd.OFFSET_MM = (double)nudOffset.Value;
            _cmd.CABLE_TRAY_EXTRA_HEIGHT_MM = (double)nudTrayExtra.Value;
            _cmd.MATCH_TOLERANCE_MM = (double)nudTolerance.Value;
            _cmd.FAMILY_NAME = cmbFamily.Text; _cmd.SYMBOL_NAME = cmbSymbol.Text;
            _cmd.LEVEL_NAME = cmbLevel.Text; _cmd.TARGET_CATEGORY_NAME = cmbCategory.Text;
            if (Enum.TryParse<BuiltInCategory>(_cmd.TARGET_CATEGORY_NAME, true, out var bic)) _cmd.TARGET_CATEGORY_ID = (int)bic;
            _cmd.PARAM_WIDTH_NAME = txtParamWidth.Text; _cmd.PARAM_HEIGHT_NAME = txtParamHeight.Text; _cmd.PARAM_DEPTH_NAME = txtParamDepth.Text;

            _cmd.SaveConfigToFile();
            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }

    // ================================================================
    // 【2】HoleCalc：纯计算 / 格式化
    // ================================================================
    public static class HoleCalc
    {
        public const double MM_PER_FOOT = 304.8;
        public const double EPS = 1e-6;
        public static string FmtMm(double ft) => $"{ft * MM_PER_FOOT:F1}mm";
        public static string Fmt(XYZ p) => p == null ? "<null>" : $"({p.X * MM_PER_FOOT:F1}, {p.Y * MM_PER_FOOT:F1}, {p.Z * MM_PER_FOOT:F1})mm";
        public static string FmtDir(XYZ d) => d == null ? "<null>" : $"({d.X:F3}, {d.Y:F3}, {d.Z:F3})";
        public static string FmtOutline(Outline o) => o == null ? "<null>" : $"Min={Fmt(o.MinimumPoint)} Max={Fmt(o.MaximumPoint)}";

        public static IEnumerable<XYZ> EnumerateCorners(Outline o)
        {
            for (int i = 0; i <= 1; i++)
                for (int j = 0; j <= 1; j++)
                    for (int k = 0; k <= 1; k++)
                        yield return new XYZ(
                            i == 0 ? o.MinimumPoint.X : o.MaximumPoint.X,
                            j == 0 ? o.MinimumPoint.Y : o.MaximumPoint.Y,
                            k == 0 ? o.MinimumPoint.Z : o.MaximumPoint.Z);
        }

        public static Outline GetWorldOutline(BoundingBoxXYZ bbox, Transform linkTf)
        {
            if (bbox == null) return null;
            Transform bboxTf = bbox.Transform ?? Transform.Identity;
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

            for (int i = 0; i <= 1; i++)
                for (int j = 0; j <= 1; j++)
                    for (int k = 0; k <= 1; k++)
                    {
                        XYZ local = new XYZ(i == 0 ? bbox.Min.X : bbox.Max.X, j == 0 ? bbox.Min.Y : bbox.Max.Y, k == 0 ? bbox.Min.Z : bbox.Max.Z);
                        XYZ inLinkDoc = bboxTf.OfPoint(local);
                        XYZ world = (linkTf == null) ? inLinkDoc : linkTf.OfPoint(inLinkDoc);
                        if (world.X < minX) minX = world.X; if (world.X > maxX) maxX = world.X;
                        if (world.Y < minY) minY = world.Y; if (world.Y > maxY) maxY = world.Y;
                        if (world.Z < minZ) minZ = world.Z; if (world.Z > maxZ) maxZ = world.Z;
                    }
            return new Outline(new XYZ(minX, minY, minZ), new XYZ(maxX, maxY, maxZ));
        }
    }

    // ================================================================
    // 【3】HoleBiz：需要 Document/Element 的业务操作
    // ================================================================
    public static class HoleBiz
    {
        public const double MATCH_TOLERANCE_MM = 300.0;
        public const double MM_PER_FOOT = 304.8;
        private static void Log(string s) => MEPLogger.Instance.AppendLine(s);

        public static bool IsSupportedMep(Element e) => e is Duct || e is Pipe || e is CableTray;

        public static bool ParsePipeReferences(Document doc, IList<Reference> refs, out RevitLinkInstance pipeLinkInstance, out List<Element> pipeElements, out Transform pipeTf)
        {
            pipeLinkInstance = null; pipeElements = new List<Element>(); pipeTf = null; Document pipeLinkDoc = null;
            foreach (Reference pref in refs)
            {
                RevitLinkInstance inst = doc.GetElement(pref.ElementId) as RevitLinkInstance;
                if (inst == null) continue;
                if (pipeLinkInstance == null) { pipeLinkInstance = inst; pipeLinkDoc = inst.GetLinkDocument(); }
                else if (pipeLinkInstance.Id != inst.Id) throw new Exception("所有管线必须来自同一个链接");

                Element e = pipeLinkDoc?.GetElement(pref.LinkedElementId);
                if (e != null && IsSupportedMep(e)) pipeElements.Add(e);
            }
            if (pipeElements.Count == 0) return false;
            pipeTf = pipeLinkInstance.GetTotalTransform();
            return true;
        }

        /// <summary>
        /// 【核心算法修正】根据管线与墙的交点，沿管线延伸设定距离求包络投影。
        /// </summary>
        public static bool GetMepCrossSectionBoundary(
            Element e, Transform tf, XYZ wallOrigin, XYZ wallDir, XYZ wallNormal,
            double wallThickness, double offsetMm,
            out double minU, out double maxU, out double minZ, out double maxZ)
        {
            minU = double.MaxValue; maxU = double.MinValue;
            minZ = double.MaxValue; maxZ = double.MinValue;

            LocationCurve lc = e.Location as LocationCurve;
            if (lc == null || lc.Curve == null) return false;
            Curve curve = lc.Curve;

            XYZ p0 = tf.OfPoint(curve.GetEndPoint(0));
            XYZ p1 = tf.OfPoint(curve.GetEndPoint(1));
            XYZ axis = (p1 - p0);
            if (axis.GetLength() < 1e-6) return false;
            axis = axis.Normalize();

            // 1. 求轴线与墙中心面的交点
            double d = (wallOrigin - p0).DotProduct(wallNormal);
            double denom = axis.DotProduct(wallNormal);
            
            if (Math.Abs(denom) < 1e-6) return false; 
            
            XYZ centerPt = p0 + (d / denom) * axis;

            // 2. 计算延伸距离：穿墙所需长度的一半 + 配置的偏移量
            double halfThickness = (wallThickness / 2) / Math.Abs(denom);
            double halfOffset = (offsetMm / MM_PER_FOOT);
            double extendDist = halfThickness + halfOffset;

            // 前后两个延伸点
            XYZ ptA = centerPt - axis * extendDist;
            XYZ ptB = centerPt + axis * extendDist;

            // 3. 获取截面尺寸
            double W = 0, H = 0;
            if (e is Pipe pipe) { W = pipe.Diameter; H = pipe.Diameter; }
            else if (e is Duct duct) { W = duct.Width; H = duct.Height; }
            else if (e is CableTray tray) { W = tray.Width; H = tray.Height; }
            else return false;

            if (W <= 0 || H <= 0) return false;

            // 4. 构造截面局部坐标轴
            XYZ wDir = axis.CrossProduct(XYZ.BasisZ);
            if (wDir.GetLength() < 1e-6) wDir = XYZ.BasisX; // 立管
            wDir = wDir.Normalize();

            XYZ hDir = wDir.CrossProduct(axis).Normalize();
            if (hDir.Z < 0) hDir = -hDir; // 确保向上

            // 5. 生成 8 个关键点
            XYZ[] pts = new XYZ[8];
            int idx = 0;
            foreach (var c in new[] { ptA, ptB })
            {
                pts[idx++] = c + wDir * (W / 2) + hDir * (H / 2);
                pts[idx++] = c + wDir * (W / 2) - hDir * (H / 2);
                pts[idx++] = c - wDir * (W / 2) + hDir * (H / 2);
                pts[idx++] = c - wDir * (W / 2) - hDir * (H / 2);
            }

            // 6. 投影到墙面上求极值
            foreach (var pt in pts)
            {
                double dist = (pt - wallOrigin).DotProduct(wallNormal);
                XYZ ptOnPlane = pt - dist * wallNormal;
                double u = (ptOnPlane - wallOrigin).DotProduct(wallDir);
                if (u < minU) minU = u;
                if (u > maxU) maxU = u;
                
                if (ptOnPlane.Z < minZ) minZ = ptOnPlane.Z;
                if (ptOnPlane.Z > maxZ) maxZ = ptOnPlane.Z;
            }
            return true;
        }

        public static List<Element> MatchPipesToHole(FamilyInstance hole, Dictionary<Element, Outline> pipeOutlines, double matchToleranceMm = MATCH_TOLERANCE_MM)
        {
            BoundingBoxXYZ holeBbox = hole.get_BoundingBox(null);
            if (holeBbox == null) return new List<Element>();
            double tol = matchToleranceMm / MM_PER_FOOT;
            Outline holeOutline = new Outline(holeBbox.Min - new XYZ(tol, tol, tol), holeBbox.Max + new XYZ(tol, tol, tol));
            List<Element> result = new List<Element>();
            foreach (var kvp in pipeOutlines)
                if (holeOutline.Intersects(kvp.Value, tol) || holeOutline.Contains((kvp.Value.MinimumPoint + kvp.Value.MaximumPoint) / 2, tol))
                    result.Add(kvp.Key);
            return result;
        }

        public static bool SetParam(FamilyInstance inst, string name, double val)
        {
            Parameter p = inst.LookupParameter(name);
            if (p != null && !p.IsReadOnly) { p.Set(val); return true; }
            return false;
        }

        public static void RotateToWall(Document doc, ElementId holeId, XYZ targetDir, XYZ org)
        {
            FamilyInstance fi = doc.GetElement(holeId) as FamilyInstance;
            if (fi == null) return;
            XYZ current = fi.HandOrientation;
            double ang = current.AngleTo(targetDir);
            if (current.CrossProduct(targetDir).Z < 0) ang = -ang;

            if (Math.Abs(ang) > 1e-3)
                ElementTransformUtils.RotateElement(doc, holeId, Line.CreateUnbound(org, XYZ.BasisZ), ang);
        }
    }
}