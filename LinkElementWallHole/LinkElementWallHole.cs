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
        // ==== 改为 public，方便设置窗体访问和修改 ====
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

        // ============== 主入口 ==============
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
            foreach (var h in preSelectedHoles)
            {
                var fi = h as FamilyInstance;
                if (fi == null) continue;
                var lp = fi.Location as LocationPoint;
                Log($"[PreSelectedHole] Id={fi.Id} Pos={Fmt(lp?.Point)} " +
                    $"HandOrient={FmtDir(fi.HandOrientation)} FaceOrient={FmtDir(fi.FacingOrientation)} " +
                    $"Symbol='{fi.Symbol?.Name}'");
            }

            IList<Reference> pipeRefs;
            try
            {
                using (var finisher = new SpaceKeyFinisher())
                {
                    finisher.Enable();
                    pipeRefs = uidoc.Selection.PickObjects(
                        ObjectType.LinkedElement,
                        new LinkMEPSelectionFilter(doc),
                        $"已识别预选的 {preSelectedHoles.Count} 个墙洞。\n" +
                        "请框选/点选要关联的 MEP（仅限链接中的管道/风管/桥架）\n" +
                        "按空格结束");
                }
            }
            catch (OperationCanceledException)
            {
                throw new Exception("未选择任何 MEP，无法更新。");
            }

            if (!HoleBiz.ParsePipeReferences(doc, pipeRefs, out _, out var pipeElements, out var pipeTf))
                throw new Exception("未选中任何有效的管道/桥架/风管，或它们不在同一个链接中。");

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
                        "【新建洞 - 步骤 1 / 2】\n" +
                        "请选择 1~N 个需要开洞的 MEP（仅限链接中的管道/风管/桥架）\n" +
                        "按空格结束");
                }
            }
            catch (OperationCanceledException)
            {
                throw new Exception("未选择任何 MEP，操作取消。");
            }

            if (!HoleBiz.ParsePipeReferences(doc, pipeRefs, out _, out var pipeElements, out var pipeTf))
                throw new Exception("未选中任何有效的管道/桥架/风管，或它们不在同一个链接中。");

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
            catch (OperationCanceledException)
            {
                throw new Exception("未选择墙，操作取消。");
            }

            return CreateNewHole(doc, uidoc, wallRef, pipeElements, pipeTf);
        }

        private IList<Element> GetPreSelectedHoles(UIDocument uidoc)
        {
            List<Element> holes = new List<Element>();
            Document doc = uidoc.Document;
            ICollection<ElementId> selectedIds = uidoc.Selection.GetElementIds();

            foreach (ElementId id in selectedIds)
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

            Log($"[CreateNewHole] WallId={targetWall.Id} WallType='{targetWall.WallType?.Name}' 厚度={FmtMm(wallThickness)}");
            Log($"[CreateNewHole] wallLinkTf.IsIdentity={wallTf.IsIdentity}");
            Log($"[CreateNewHole] wallOrigin={Fmt(wallOrigin)}");
            Log($"[CreateNewHole] wallDir={FmtDir(wallDir)}  wallNormal={FmtDir(wallNormal)}");

            if (Math.Abs(wallDir.Z) > 0.01)
                Log("[CreateNewHole] 警告：墙不是竖直的（wallDir.Z != 0），投影与插入点计算可能有偏差。");

            double minU = double.MaxValue, maxU = double.MinValue;
            double minZproj = double.MaxValue, maxZproj = double.MinValue;
            bool hasWidth = false;
            bool hasHeight = false;

            foreach (Element elem in pipeElements)
            {
                BoundingBoxXYZ bbox = elem.get_BoundingBox(null);
                if (bbox == null) continue;
                Outline worldOutline = HoleCalc.GetWorldOutline(bbox, pipeTf);
                if (worldOutline == null) continue;

                Log($"[CreateNewHole] MEP {elem.Id} ({elem.GetType().Name}) {FmtOutline(worldOutline)}");

                double uC, wA;
                if (HoleBiz.TryGetMepSectionWidth(elem, pipeTf, wallOrigin, wallDir, out uC, out wA))
                {
                    if (uC - wA / 2 < minU) minU = uC - wA / 2;
                    if (uC + wA / 2 > maxU) maxU = uC + wA / 2;
                    hasWidth = true;
                    Log($"[CreateNewHole] MEP {elem.Id} 截面参数: uCenter={FmtMm(uC)} 截面宽={FmtMm(wA)}");
                }
                else
                {
                    Log($"[CreateNewHole] MEP {elem.Id} 截面参数不可用，宽度退回 8 角点投影");
                    foreach (var pt in HoleCalc.EnumerateCorners(worldOutline))
                    {
                        double dist = (pt - wallOrigin).DotProduct(wallNormal);
                        XYZ ptOnPlane = pt - dist * wallNormal;
                        double u = (ptOnPlane - wallOrigin).DotProduct(wallDir);
                        if (u < minU) minU = u;
                        if (u > maxU) maxU = u;
                    }
                    hasWidth = true;
                }

                foreach (var pt in HoleCalc.EnumerateCorners(worldOutline))
                {
                    double dist = (pt - wallOrigin).DotProduct(wallNormal);
                    XYZ ptOnPlane = pt - dist * wallNormal;
                    if (ptOnPlane.Z < minZproj) minZproj = ptOnPlane.Z;
                    if (ptOnPlane.Z > maxZproj) maxZproj = ptOnPlane.Z;
                    hasHeight = true;
                }
            }

            if (!hasHeight)
                throw new Exception("选中的 MEP 都没有有效的包围盒，无法计算洞口尺寸。");
            if (!hasWidth)
                throw new Exception("无法计算洞口宽度。");

            Log($"[CreateNewHole] 8-角投影(高度部分): minZ={FmtMm(minZproj)} maxZ={FmtMm(maxZproj)}");
            Log($"[CreateNewHole] 宽度汇总: minU={FmtMm(minU)} maxU={FmtMm(maxU)}");

            double offsetFeet = OFFSET_MM / MM_PER_FOOT;
            double width = (maxU - minU) + 2 * offsetFeet;
            double height = (maxZproj - minZproj) + 2 * offsetFeet;
            double trayExtra = HoleBiz.GetCableTrayExtraHeightFeet(pipeElements, pipeTf, CABLE_TRAY_EXTRA_HEIGHT_MM);
            height += trayExtra;
            double depth = wallThickness;

            XYZ insertPt = wallOrigin + (minU + maxU) / 2 * wallDir;
            insertPt = new XYZ(insertPt.X, insertPt.Y, minZproj - offsetFeet);

            Log($"[CreateNewHole] 结果尺寸 width={FmtMm(width)} height={FmtMm(height)} " +
                $"depth={FmtMm(depth)} trayExtra={FmtMm(trayExtra)} insertPt={Fmt(insertPt)}");

            FamilySymbol holeSymbol = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(s => s.Family.Name == FAMILY_NAME && s.Name == SYMBOL_NAME);

            if (holeSymbol == null)
            {
                Log($"[CreateNewHole] 未找到配置族：{FAMILY_NAME} - {SYMBOL_NAME}");

                TaskDialog dialog = new TaskDialog("未找到墙洞族");
                dialog.MainInstruction = "当前项目未找到配置文件中指定的墙洞族";
                dialog.MainContent = $"配置指定的族：\n{FAMILY_NAME} - {SYMBOL_NAME}\n\n" +
                                     "是否立即打开设置界面，从当前项目的模型中选择正确的族？";
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
                            
                            if (holeSymbol == null)
                                throw new Exception("设置完成，但当前项目中仍然找不到选定的族，操作终止。");
                        }
                        else
                        {
                            throw new Exception("用户取消了设置，操作结束。");
                        }
                    }
                }
                else
                {
                    throw new Exception($"未找到族：{FAMILY_NAME} - {SYMBOL_NAME}，且用户未进行设置。");
                }
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
                {
                    throw new Exception(
                        $"族 '{FAMILY_NAME}-{SYMBOL_NAME}' 不是基于标高的可自由放置族（LocationPoint 为空）。" +
                        "当前工具仅支持基于标高/工作平面的常规模型族，请更换族或改用基于面的创建逻辑。");
                }

                Log($"[CreateNewHole] 实例创建 Id={hole.Id} 初始 HandOrient={FmtDir(hole.HandOrientation)} " +
                    $"FaceOrient={FmtDir(hole.FacingOrientation)}");

                ElementId holeId = hole.Id;
                HoleBiz.RotateToWall(doc, holeId, wallDir, insertPt);

                FamilyInstance holeAfter = doc.GetElement(holeId) as FamilyInstance;
                if (holeAfter == null) throw new Exception("创建后重新获取墙洞实例失败。");

                bool okDepth = HoleBiz.SetParam(holeAfter, PARAM_DEPTH_NAME, depth);
                bool okWidth = HoleBiz.SetParam(holeAfter, PARAM_WIDTH_NAME, width);
                bool okHeight = HoleBiz.SetParam(holeAfter, PARAM_HEIGHT_NAME, height);

                if (!okWidth || !okHeight)
                {
                    throw new Exception(
                        $"墙洞族缺少可写参数 '{PARAM_WIDTH_NAME}' 或 '{PARAM_HEIGHT_NAME}'" +
                        $"（当前族：{FAMILY_NAME}-{SYMBOL_NAME}）。请检查设置界面的参数名配置。");
                }
                if (!okDepth)
                {
                    Log($"[CreateNewHole] 警告：深度参数 '{PARAM_DEPTH_NAME}' 写入失败（洞 {holeAfter.Id}），仅记录不中断。");
                }

                Log($"[CreateNewHole] 参数写入 {PARAM_DEPTH_NAME}={FmtMm(depth)} " +
                    $"{PARAM_WIDTH_NAME}={FmtMm(width)} {PARAM_HEIGHT_NAME}={FmtMm(height)}");

                tran.Commit();
            }

            Log("[CreateNewHole] 创建成功。");
            return Result.Succeeded;
        }

        // ==========================================================
        // 分支 B：更新现有洞
        // ==========================================================
        private Result UpdateExistingHoles(Document doc, IList<Reference> holeRefs,
            List<Element> pipeElements, Transform pipeTf)
        {
            Log($"[Update] 开始。洞数={holeRefs.Count} MEP数={pipeElements.Count}");

            Dictionary<Element, Outline> pipeOutlines = new Dictionary<Element, Outline>();
            foreach (var pipe in pipeElements)
            {
                BoundingBoxXYZ pb = pipe.get_BoundingBox(null);
                if (pb == null) continue;
                Outline outline = HoleCalc.GetWorldOutline(pb, pipeTf);
                if (outline == null) continue;
                pipeOutlines[pipe] = outline;
                Log($"[Update] MEP {pipe.Id} ({pipe.GetType().Name}) {FmtOutline(outline)}");
            }

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
                    if (hole.Symbol?.Family?.Name != FAMILY_NAME)
                    {
                        Log($"[Update] 洞 {hole.Id} 非目标族（Family='{hole.Symbol?.Family?.Name}'），跳过。");
                        skippedNoMatch++; continue;
                    }

                    LocationPoint lp = hole.Location as LocationPoint;
                    if (lp == null) continue;

                    XYZ hand = hole.HandOrientation;
                    XYZ facing = hole.FacingOrientation;
                    Log($"[Update] 洞 {hole.Id} Pos={Fmt(lp.Point)} HandOrient={FmtDir(hand)} FaceOrient={FmtDir(facing)}");

                    XYZ wallDir;
                    if (Math.Abs(hand.Z) < 0.01)
                    {
                        wallDir = new XYZ(hand.X, hand.Y, 0).Normalize();
                    }
                    else if (Math.Abs(facing.Z) < 0.01)
                    {
                        wallDir = new XYZ(-facing.Y, facing.X, 0).Normalize();
                        Log($"[Update] 洞 {hole.Id} 警告：HandOrientation 非水平(Z={hand.Z:F3})，改用 FacingOrient 推墙方向。");
                    }
                    else
                    {
                        var proj = new XYZ(hand.X, hand.Y, 0);
                        wallDir = proj.GetLength() > EPS ? proj.Normalize() : XYZ.BasisX;
                        Log($"[Update] 洞 {hole.Id} 警告：Hand/Face 均非水平，退回 Hand 水平投影。");
                    }
                    Log($"[Update] 洞 {hole.Id} 推断 wallDir={FmtDir(wallDir)}");

                    List<Element> relatedPipes;
                    if (holeRefs.Count == 1)
                    {
                        relatedPipes = pipeElements;
                        Log($"[Update] 单洞强绑 {pipeElements.Count} 个 MEP → 洞 {hole.Id}。");
                    }
                    else
                    {
                        relatedPipes = HoleBiz.MatchPipesToHole(hole, pipeOutlines, MATCH_TOLERANCE_MM);
                        Log($"[Update] 洞 {hole.Id} 空间匹配到 {relatedPipes.Count} 个 MEP。");
                    }

                    if (relatedPipes.Count == 0)
                    {
                        skippedNoMatch++;
                        Log($"[Update] 洞 {hole.Id} 未匹配到任何 MEP，跳过。");
                        continue;
                    }

                    XYZ wallOrigin = new XYZ(lp.Point.X, lp.Point.Y, 0);
                    double minU = double.MaxValue, maxU = double.MinValue;
                    double minZ = double.MaxValue, maxZ = double.MinValue;
                    bool hasWidth = false;
                    bool hasHeight = false;

                    foreach (var pipe in relatedPipes)
                    {
                        if (!pipeOutlines.TryGetValue(pipe, out Outline outline)) continue;

                        double uC, wA;
                        if (HoleBiz.TryGetMepSectionWidth(pipe, pipeTf, wallOrigin, wallDir, out uC, out wA))
                        {
                            if (uC - wA / 2 < minU) minU = uC - wA / 2;
                            if (uC + wA / 2 > maxU) maxU = uC + wA / 2;
                            hasWidth = true;
                            Log($"[Update] MEP {pipe.Id} 截面参数: uCenter={FmtMm(uC)} 截面宽={FmtMm(wA)}");
                        }
                        else
                        {
                            Log($"[Update] MEP {pipe.Id} 截面参数不可用，宽度退回 8 角点投影");
                            foreach (var lp3 in HoleCalc.EnumerateCorners(outline))
                            {
                                double u = (lp3 - wallOrigin).DotProduct(wallDir);
                                if (u < minU) minU = u;
                                if (u > maxU) maxU = u;
                            }
                            hasWidth = true;
                        }

                        foreach (var lp3 in HoleCalc.EnumerateCorners(outline))
                        {
                            if (lp3.Z < minZ) minZ = lp3.Z;
                            if (lp3.Z > maxZ) maxZ = lp3.Z;
                            hasHeight = true;
                        }
                    }

                    if (!hasHeight) { skippedNoMatch++; continue; }
                    if (!hasWidth) { skippedNoMatch++; continue; }

                    double offsetFeet = OFFSET_MM / MM_PER_FOOT;
                    double width = (maxU - minU) + 2 * offsetFeet;
                    double height = (maxZ - minZ) + 2 * offsetFeet;
                    double trayExtra = HoleBiz.GetCableTrayExtraHeightFeet(relatedPipes, pipeTf, CABLE_TRAY_EXTRA_HEIGHT_MM);
                    height += trayExtra;

                    double depth = hole.LookupParameter(PARAM_DEPTH_NAME)?.AsDouble() ?? 0.0;
                    if (depth <= 0.0) depth = 200.0 / MM_PER_FOOT;

                    Log($"[Update] 洞 {hole.Id} 投影 minU={FmtMm(minU)} maxU={FmtMm(maxU)} " +
                        $"minZ={FmtMm(minZ)} maxZ={FmtMm(maxZ)} → " +
                        $"width={FmtMm(width)} height={FmtMm(height)} depth={FmtMm(depth)} trayExtra={FmtMm(trayExtra)}");

                    bool okW = HoleBiz.SetParam(hole, PARAM_WIDTH_NAME, width);
                    bool okH = HoleBiz.SetParam(hole, PARAM_HEIGHT_NAME, height);
                    bool okD = HoleBiz.SetParam(hole, PARAM_DEPTH_NAME, depth);

                    if (!okW || !okH)
                    {
                        Log($"[Update] 洞 {hole.Id} 参数写入失败（宽度={okW}, 高度={okH}），跳过本次更新。");
                        paramWriteFailCount++;
                        skippedNoMatch++;
                        continue;
                    }
                    if (!okD)
                    {
                        Log($"[Update] 洞 {hole.Id} 警告：深度参数 '{PARAM_DEPTH_NAME}' 写入失败（不中断）。");
                    }

                    XYZ posNow = (hole.Location as LocationPoint)?.Point ?? lp.Point;
                    XYZ targetXY = wallOrigin + ((minU + maxU) / 2) * wallDir;
                    XYZ targetPos = new XYZ(targetXY.X, targetXY.Y, minZ - offsetFeet);
                    XYZ delta = targetPos - posNow;
                    Log($"[Update] 洞 {hole.Id} posNow={Fmt(posNow)} targetPos={Fmt(targetPos)} deltaLen={FmtMm(delta.GetLength())}");
                    if (delta.GetLength() > EPS)
                        ElementTransformUtils.MoveElement(doc, hole.Id, delta);

                    updated++;
                    Log($"[Update] 洞 {hole.Id} 更新成功，关联 {relatedPipes.Count} 个 MEP。");
                }

                tran.Commit();

                if (updated == 0)
                {
                    string paramDetail = paramWriteFailCount > 0
                        ? $"\n其中 {paramWriteFailCount} 个洞因族缺少可写参数 " +
                          $"'{PARAM_WIDTH_NAME}' / '{PARAM_HEIGHT_NAME}' 而失败（请检查族参数名）。"
                        : "";
                    throw new Exception(
                        $"未更新任何墙洞。\n" +
                        $"可能原因：MEP 与墙洞空间不重叠（相距超过 {MATCH_TOLERANCE_MM:F0}mm），" +
                        $"或所选元素非族 \"{FAMILY_NAME}\"。\n（跳过 {skippedNoMatch} 个）{paramDetail}");
                }
                Log($"[Update] 完成。共更新 {updated} 个，跳过 {skippedNoMatch} 个。");
            }

            return Result.Succeeded;
        }

        // ==========================================================
        // 配置读写（UI 界面调用）
        // ==========================================================
        public void LoadConfig()
        {
            // ==== 默认值 ====
            OFFSET_MM = 100.0;
            FAMILY_NAME = "REWallHole";
            SYMBOL_NAME = "Rect CBWD for Wall";
            LEVEL_NAME = "WCS 0.0";
            CABLE_TRAY_EXTRA_HEIGHT_MM = 0.0;
            PARAM_WIDTH_NAME = "NDH_Width";
            PARAM_HEIGHT_NAME = "NDH_Height";
            PARAM_DEPTH_NAME = "NDH_Depth";
            TARGET_CATEGORY_ID = (int)BuiltInCategory.OST_GenericModel;
            TARGET_CATEGORY_NAME = "OST_GenericModel";
            MATCH_TOLERANCE_MM = 300.0;

            try
            {
                string dllPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                string dir = System.IO.Path.GetDirectoryName(dllPath);
                string path = System.IO.Path.Combine(dir, "config.txt");

                if (System.IO.File.Exists(path))
                {
                    // ⭐ 修复：完美剥离 # 及其后面的行尾注释
                    var validLines = System.IO.File.ReadAllLines(path)
                        .Select(l => 
                        {
                            int hashIdx = l.IndexOf('#');
                            return hashIdx >= 0 ? l.Substring(0, hashIdx).Trim() : l.Trim();
                        })
                        .Where(l => !string.IsNullOrEmpty(l))
                        .ToList();

                    if (validLines.Count >= 5)
                    {
                        if (double.TryParse(validLines[0], out double off)) OFFSET_MM = off;
                        FAMILY_NAME = validLines[1];
                        SYMBOL_NAME = validLines[2];
                        LEVEL_NAME = validLines[3];
                        if (double.TryParse(validLines[4], out double tray)) CABLE_TRAY_EXTRA_HEIGHT_MM = tray;

                        if (validLines.Count > 5 && !string.IsNullOrEmpty(validLines[5])) PARAM_WIDTH_NAME = validLines[5];
                        if (validLines.Count > 6 && !string.IsNullOrEmpty(validLines[6])) PARAM_HEIGHT_NAME = validLines[6];
                        if (validLines.Count > 7 && !string.IsNullOrEmpty(validLines[7])) PARAM_DEPTH_NAME = validLines[7];

                        if (validLines.Count > 8 && !string.IsNullOrEmpty(validLines[8]))
                        {
                            string catStr = validLines[8];
                            if (Enum.TryParse<BuiltInCategory>(catStr, true, out var bic))
                            {
                                TARGET_CATEGORY_ID = (int)bic;
                                TARGET_CATEGORY_NAME = bic.ToString();
                            }
                        }

                        if (validLines.Count > 9)
                        {
                            if (double.TryParse(validLines[9], out double tol) && tol > 0) MATCH_TOLERANCE_MM = tol;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("LoadConfig", ex);
            }

            // ==== 兜底校验 ====
            if (OFFSET_MM <= 0) OFFSET_MM = 100.0;
            if (string.IsNullOrEmpty(FAMILY_NAME)) FAMILY_NAME = "REWallHole";
            if (string.IsNullOrEmpty(SYMBOL_NAME)) SYMBOL_NAME = "Rect CBWD for Wall";
            if (string.IsNullOrEmpty(LEVEL_NAME)) LEVEL_NAME = "WCS 0.0";
            if (string.IsNullOrEmpty(PARAM_WIDTH_NAME)) PARAM_WIDTH_NAME = "NDH_Width";
            if (string.IsNullOrEmpty(PARAM_HEIGHT_NAME)) PARAM_HEIGHT_NAME = "NDH_Height";
            if (string.IsNullOrEmpty(PARAM_DEPTH_NAME)) PARAM_DEPTH_NAME = "NDH_Depth";

            Log($"[LoadConfig] 加载完毕: FAMILY='{FAMILY_NAME}' SYMBOL='{SYMBOL_NAME}' 类别='{TARGET_CATEGORY_NAME}'");
        }

        public void SaveConfigToFile()
        {
            try
            {
                string dllPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                string dir = System.IO.Path.GetDirectoryName(dllPath);
                string path = System.IO.Path.Combine(dir, "config.txt");

                var lines = new List<string>
                {
                    "# ============================================================",
                    "# MEPTools - 链接墙洞创建/更新 配置文件",
                    "# 本文件已通过 UI 界面保存，推荐后续通过“设置”按钮修改",
                    "# ============================================================",
                    OFFSET_MM.ToString(),
                    FAMILY_NAME,
                    SYMBOL_NAME,
                    LEVEL_NAME,
                    CABLE_TRAY_EXTRA_HEIGHT_MM.ToString(),
                    PARAM_WIDTH_NAME,
                    PARAM_HEIGHT_NAME,
                    PARAM_DEPTH_NAME,
                    TARGET_CATEGORY_NAME,
                    MATCH_TOLERANCE_MM.ToString()
                };

                System.IO.File.WriteAllLines(path, lines);
                Log("[SaveConfig] 配置文件已成功更新。");
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("SaveConfigToFile", ex);
            }
        }

        // ============== 选择过滤器 ==============
        private class LinkMEPSelectionFilter : ISelectionFilter
        {
            private readonly Document _doc;
            public LinkMEPSelectionFilter(Document doc) { _doc = doc; }

            public bool AllowElement(Element elem) => elem is RevitLinkInstance;

            public bool AllowReference(Reference reference, XYZ position)
            {
                RevitLinkInstance linkInst = _doc.GetElement(reference.ElementId) as RevitLinkInstance;
                if (linkInst == null) return false;
                Document linkDoc = linkInst.GetLinkDocument();
                if (linkDoc == null) return false;
                Element linkedElem = linkDoc.GetElement(reference.LinkedElementId);
                return HoleBiz.IsSupportedMep(linkedElem);
            }
        }

        private class LinkWallSelectionFilter : ISelectionFilter
        {
            private readonly Document _doc;
            public LinkWallSelectionFilter(Document doc) { _doc = doc; }

            public bool AllowElement(Element elem) => elem is RevitLinkInstance;

            public bool AllowReference(Reference reference, XYZ position)
            {
                RevitLinkInstance linkInst = _doc.GetElement(reference.ElementId) as RevitLinkInstance;
                if (linkInst == null) return false;
                Document linkDoc = linkInst.GetLinkDocument();
                if (linkDoc == null) return false;
                Element linkedElem = linkDoc.GetElement(reference.LinkedElementId);
                return linkedElem is Wall ||
                       linkedElem.Category?.Id.IntegerValue == (int)BuiltInCategory.OST_Walls;
            }
        }
    }

    // ================================================================
    // 【1.5】独立命令入口：打开设置界面 (用于挂载 Ribbon 按钮)
    // ================================================================
    [Transaction(TransactionMode.Manual)]
    public class Cmd_WallHoleSettings : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument.Document;
            
            // 实例化主命令以读取当前配置
            var mainCmd = new Command();
            mainCmd.LoadConfig();

            using (var form = new WallHoleConfigForm(doc, mainCmd))
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    InfoCenterTip.Show("墙洞配置已保存！");
                }
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

        private NumericUpDown nudOffset;
        private NumericUpDown nudTrayExtra;
        private NumericUpDown nudTolerance;
        private System.Windows.Forms.ComboBox cmbFamily;
        private System.Windows.Forms.ComboBox cmbSymbol;
        private System.Windows.Forms.ComboBox cmbLevel;
        private System.Windows.Forms.ComboBox cmbCategory;
        private System.Windows.Forms.TextBox txtParamWidth;
        private System.Windows.Forms.TextBox txtParamHeight;
        private System.Windows.Forms.TextBox txtParamDepth;

        public WallHoleConfigForm(Document doc, Command cmd)
        {
            _doc = doc;
            _cmd = cmd;
            
            InitializeComponent();
            LoadRevitData();
            LoadConfigData();
        }

        private void InitializeComponent()
        {
            this.Text = "墙洞工具 - 参数设置";
            // ⭐ 修复 1：将窗体高度从 460 调整到 540，防止底部按钮被切掉
            this.Size = new System.Drawing.Size(430, 540);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            int labelX = 25, inputX = 150, y = 20, gap = 36;

            // 用于显示气泡提示
            ToolTip tip = new ToolTip();
            tip.AutoPopDelay = 8000;
            tip.InitialDelay = 200;
            tip.ReshowDelay = 200;

            // 1. 基础参数
            AddLabel("四周预留偏移(mm):", labelX, y);
            nudOffset = new NumericUpDown { Location = new System.Drawing.Point(inputX, y), Width = 230, Maximum = 1000, DecimalPlaces = 1 };
            tip.SetToolTip(nudOffset, "开出的洞口尺寸比管线大出来的边距。\n例如填 100，洞口四周就会比管线大 100mm。");
            this.Controls.Add(nudOffset);
            y += gap;

            AddLabel("桥架附加高(mm):", labelX, y);
            nudTrayExtra = new NumericUpDown { Location = new System.Drawing.Point(inputX, y), Width = 230, Maximum = 1000, DecimalPlaces = 1 };
            tip.SetToolTip(nudTrayExtra, "如果在最高处遇到的是桥架（而不是管道或风管），\n顶部额外增加的高度。");
            this.Controls.Add(nudTrayExtra);
            y += gap;

            // ⭐ 修复 2：改名“容差”为“空间关联范围”，并增加气泡提示
            AddLabel("空间关联范围(mm):", labelX, y);
            nudTolerance = new NumericUpDown { Location = new System.Drawing.Point(inputX, y), Width = 230, Maximum = 5000, DecimalPlaces = 1 };
            tip.SetToolTip(nudTolerance, "更新多个墙洞时，程序会在洞口周围多大范围内寻找管线。\n如果管线离洞口太远（超过这个范围），则不会被关联更新。\n一般保持 300 即可。");
            this.Controls.Add(nudTolerance);
            y += gap + 10;

            // 2. 族与类型联动
            AddLabel("墙洞族 (Family):", labelX, y);
            cmbFamily = new System.Windows.Forms.ComboBox { Location = new System.Drawing.Point(inputX, y), Width = 230, DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList };
            cmbFamily.SelectedIndexChanged += CmbFamily_SelectedIndexChanged;
            this.Controls.Add(cmbFamily);
            y += gap;

            AddLabel("族类型 (Type):", labelX, y);
            cmbSymbol = new System.Windows.Forms.ComboBox { Location = new System.Drawing.Point(inputX, y), Width = 230, DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList };
            this.Controls.Add(cmbSymbol);
            y += gap;

            AddLabel("放置标高 (Level):", labelX, y);
            cmbLevel = new System.Windows.Forms.ComboBox { Location = new System.Drawing.Point(inputX, y), Width = 230, DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList };
            this.Controls.Add(cmbLevel);
            y += gap;

            AddLabel("目标类别 (Category):", labelX, y);
            cmbCategory = new System.Windows.Forms.ComboBox { Location = new System.Drawing.Point(inputX, y), Width = 230, DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList };
            cmbCategory.Items.AddRange(new object[] { "OST_GenericModel", "OST_Windows", "OST_Doors", "OST_MechanicalEquipment" });
            this.Controls.Add(cmbCategory);
            y += gap + 10;

            // 3. 参数名映射
            AddLabel("宽度参数名:", labelX, y);
            txtParamWidth = new System.Windows.Forms.TextBox { Location = new System.Drawing.Point(inputX, y), Width = 230 };
            tip.SetToolTip(txtParamWidth, "填写的参数名必须在选定的族中存在，\n且必须是长度(Length)类型的参数。");
            this.Controls.Add(txtParamWidth);
            y += gap;

            AddLabel("高度参数名:", labelX, y);
            txtParamHeight = new System.Windows.Forms.TextBox { Location = new System.Drawing.Point(inputX, y), Width = 230 };
            this.Controls.Add(txtParamHeight);
            y += gap;

            AddLabel("深度参数名:", labelX, y);
            txtParamDepth = new System.Windows.Forms.TextBox { Location = new System.Drawing.Point(inputX, y), Width = 230 };
            this.Controls.Add(txtParamDepth);
            y += gap + 15;

            // 4. 按钮
            var btnOK = new Button { Text = "保存并应用", Location = new System.Drawing.Point(80, y), Width = 100, Height = 30 };
            btnOK.Click += BtnOK_Click;
            this.Controls.Add(btnOK);
            this.AcceptButton = btnOK;

            var btnCancel = new Button { Text = "取消", Location = new System.Drawing.Point(220, y), Width = 100, Height = 30, DialogResult = DialogResult.Cancel };
            this.Controls.Add(btnCancel);
        }

        private void AddLabel(string text, int x, int y)
        {
            this.Controls.Add(new Label { Text = text, Location = new System.Drawing.Point(x, y + 3), AutoSize = true });
        }

        private void LoadRevitData()
        {
            // 1. 标高
            var levels = new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>().Select(l => l.Name).OrderBy(n => n).ToList();
            cmbLevel.DataSource = levels;

            // 2. 族 (过滤系统族)
            var families = new FilteredElementCollector(_doc).OfClass(typeof(Family))
                .Cast<Family>()
                .Where(f => f.IsEditable && f.FamilyCategory != null) 
                .Select(f => f.Name)
                .Distinct().OrderBy(n => n).ToList();
            cmbFamily.DataSource = families;
        }

        private void CmbFamily_SelectedIndexChanged(object sender, EventArgs e)
        {
            string famName = cmbFamily.SelectedItem as string;
            if (string.IsNullOrEmpty(famName)) return;

            var symbols = new FilteredElementCollector(_doc).OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s.Family.Name == famName)
                .Select(s => s.Name)
                .OrderBy(n => n)
                .ToList();
            
            cmbSymbol.DataSource = symbols;
        }

        private void LoadConfigData()
        {
            nudOffset.Value = (decimal)_cmd.OFFSET_MM;
            nudTrayExtra.Value = (decimal)_cmd.CABLE_TRAY_EXTRA_HEIGHT_MM;
            nudTolerance.Value = (decimal)_cmd.MATCH_TOLERANCE_MM;
            
            if (cmbFamily.Items.Contains(_cmd.FAMILY_NAME))
                cmbFamily.SelectedItem = _cmd.FAMILY_NAME;
            else
                cmbFamily.Text = _cmd.FAMILY_NAME;

            if (cmbSymbol.Items.Contains(_cmd.SYMBOL_NAME))
                cmbSymbol.SelectedItem = _cmd.SYMBOL_NAME;
            else
                cmbSymbol.Text = _cmd.SYMBOL_NAME;

            if (cmbLevel.Items.Contains(_cmd.LEVEL_NAME))
                cmbLevel.SelectedItem = _cmd.LEVEL_NAME;
            else
                cmbLevel.Text = _cmd.LEVEL_NAME;

            if (cmbCategory.Items.Contains(_cmd.TARGET_CATEGORY_NAME))
                cmbCategory.SelectedItem = _cmd.TARGET_CATEGORY_NAME;
            else
            {
                cmbCategory.Items.Add(_cmd.TARGET_CATEGORY_NAME);
                cmbCategory.SelectedItem = _cmd.TARGET_CATEGORY_NAME;
            }

            txtParamWidth.Text = _cmd.PARAM_WIDTH_NAME;
            txtParamHeight.Text = _cmd.PARAM_HEIGHT_NAME;
            txtParamDepth.Text = _cmd.PARAM_DEPTH_NAME;
        }

        private void BtnOK_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(cmbFamily.Text) || string.IsNullOrWhiteSpace(cmbSymbol.Text))
            {
                InfoCenterTip.Show("族和类型不能为空！");
                return;
            }
            if (string.IsNullOrWhiteSpace(txtParamWidth.Text) || string.IsNullOrWhiteSpace(txtParamHeight.Text))
            {
                InfoCenterTip.Show("宽度/高度参数名不能为空！");
                return;
            }

            // 写回 Command 内存
            _cmd.OFFSET_MM = (double)nudOffset.Value;
            _cmd.CABLE_TRAY_EXTRA_HEIGHT_MM = (double)nudTrayExtra.Value;
            _cmd.MATCH_TOLERANCE_MM = (double)nudTolerance.Value;
            _cmd.FAMILY_NAME = cmbFamily.Text;
            _cmd.SYMBOL_NAME = cmbSymbol.Text;
            _cmd.LEVEL_NAME = cmbLevel.Text;
            _cmd.TARGET_CATEGORY_NAME = cmbCategory.Text;
            
            if (Enum.TryParse<BuiltInCategory>(_cmd.TARGET_CATEGORY_NAME, true, out var bic))
                _cmd.TARGET_CATEGORY_ID = (int)bic;
            
            _cmd.PARAM_WIDTH_NAME = txtParamWidth.Text;
            _cmd.PARAM_HEIGHT_NAME = txtParamHeight.Text;
            _cmd.PARAM_DEPTH_NAME = txtParamDepth.Text;

            // 写入本地文件
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

        public static string Fmt(XYZ p) => p == null ? "<null>"
            : $"({p.X * MM_PER_FOOT:F1}, {p.Y * MM_PER_FOOT:F1}, {p.Z * MM_PER_FOOT:F1})mm";

        public static string FmtDir(XYZ d) => d == null ? "<null>"
            : $"({d.X:F3}, {d.Y:F3}, {d.Z:F3})";

        public static string FmtOutline(Outline o) => o == null ? "<null>"
            : $"Min={Fmt(o.MinimumPoint)} Max={Fmt(o.MaximumPoint)}";

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
                        XYZ local = new XYZ(
                            i == 0 ? bbox.Min.X : bbox.Max.X,
                            j == 0 ? bbox.Min.Y : bbox.Max.Y,
                            k == 0 ? bbox.Min.Z : bbox.Max.Z);

                        XYZ inLinkDoc = bboxTf.OfPoint(local);
                        XYZ world = (linkTf == null) ? inLinkDoc : linkTf.OfPoint(inLinkDoc);

                        if (world.X < minX) minX = world.X;
                        if (world.X > maxX) maxX = world.X;
                        if (world.Y < minY) minY = world.Y;
                        if (world.Y > maxY) maxY = world.Y;
                        if (world.Z < minZ) minZ = world.Z;
                        if (world.Z > maxZ) maxZ = world.Z;
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

        public static bool IsSupportedMep(Element e)
            => e is Duct || e is Pipe || e is CableTray;

        public static bool ParsePipeReferences(Document doc, IList<Reference> refs,
            out RevitLinkInstance pipeLinkInstance, out List<Element> pipeElements, out Transform pipeTf)
        {
            pipeLinkInstance = null;
            pipeElements = new List<Element>();
            pipeTf = null;

            Document pipeLinkDoc = null;
            foreach (Reference pref in refs)
            {
                RevitLinkInstance inst = doc.GetElement(pref.ElementId) as RevitLinkInstance;
                if (inst == null) continue;
                if (pipeLinkInstance == null)
                {
                    pipeLinkInstance = inst;
                    pipeLinkDoc = inst.GetLinkDocument();
                }
                else if (pipeLinkInstance.Id != inst.Id)
                {
                    throw new Exception("所有管线必须来自同一个链接");
                }
                Element e = pipeLinkDoc?.GetElement(pref.LinkedElementId);
                if (e != null && IsSupportedMep(e))
                    pipeElements.Add(e);
            }

            if (pipeElements.Count == 0) return false;
            pipeTf = pipeLinkInstance.GetTotalTransform();
            Log($"[ParsePipeRefs] 链接={pipeLinkInstance.Id} linkTf.IsIdentity={pipeTf.IsIdentity} 收集 MEP {pipeElements.Count} 个。");
            return true;
        }

        public static bool TryGetMepSectionWidth(
            Element e, Transform tf, XYZ wallOrigin, XYZ wallDir,
            out double uCenter, out double widthAlong)
        {
            uCenter = 0;
            widthAlong = 0;
            if (e == null) return false;

            LocationCurve lc = e.Location as LocationCurve;
            if (lc == null || lc.Curve == null) return false;

            Curve curve = lc.Curve;

            XYZ midLocal = curve.Evaluate(0.5, true);
            XYZ midWorld = tf.OfPoint(midLocal);
            uCenter = (midWorld - wallOrigin).DotProduct(wallDir);

            XYZ axisLocal = curve.GetEndPoint(1) - curve.GetEndPoint(0);
            XYZ axisWorld = tf.OfVector(axisLocal);
            if (axisWorld.GetLength() < 1e-9) return false;
            axisWorld = axisWorld.Normalize();

            if (e is Pipe pipe)
            {
                widthAlong = pipe.Diameter;
                return widthAlong > 0;
            }

            double baseWidth = 0;
            if (e is Duct duct) baseWidth = duct.Width;
            else if (e is CableTray tray) baseWidth = tray.Width;
            if (baseWidth <= 0) return false;

            XYZ widthDir = axisWorld.CrossProduct(XYZ.BasisZ);
            if (widthDir.GetLength() < 1e-6) return false;
            widthDir = widthDir.Normalize();

            widthAlong = baseWidth * Math.Abs(widthDir.DotProduct(wallDir));
            return widthAlong > 0;
        }

        public static List<Element> MatchPipesToHole(
            FamilyInstance hole,
            Dictionary<Element, Outline> pipeOutlines,
            double matchToleranceMm = MATCH_TOLERANCE_MM)
        {
            BoundingBoxXYZ holeBbox = hole.get_BoundingBox(null);
            if (holeBbox == null) return new List<Element>();

            double tol = matchToleranceMm / MM_PER_FOOT;
            Outline holeOutline = new Outline(
                holeBbox.Min - new XYZ(tol, tol, tol),
                holeBbox.Max + new XYZ(tol, tol, tol));

            List<Element> result = new List<Element>();
            foreach (var kvp in pipeOutlines)
            {
                if (holeOutline.Intersects(kvp.Value, tol) ||
                    holeOutline.Contains((kvp.Value.MinimumPoint + kvp.Value.MaximumPoint) / 2, tol))
                {
                    result.Add(kvp.Key);
                }
            }
            return result;
        }

        public static double GetCableTrayExtraHeightFeet(
            List<Element> elements, Transform transform, double extraHeightMm)
        {
            double maxZ = double.MinValue;
            Element highest = null;

            foreach (Element e in elements)
            {
                BoundingBoxXYZ bbox = e.get_BoundingBox(null);
                if (bbox == null) continue;
                Outline worldOutline = HoleCalc.GetWorldOutline(bbox, transform);
                if (worldOutline == null) continue;
                if (worldOutline.MaximumPoint.Z > maxZ)
                {
                    maxZ = worldOutline.MaximumPoint.Z;
                    highest = e;
                }
            }

            double result = (highest is CableTray) ? extraHeightMm / MM_PER_FOOT : 0.0;
            if (highest is CableTray)
                Log($"[GetCableTrayExtraHeight] 触发（最高元素为桥架 Id={highest.Id}），附加 {HoleCalc.FmtMm(result)}");
            return result;
        }

        public static bool SetParam(FamilyInstance inst, string name, double val)
        {
            Parameter p = inst.LookupParameter(name);
            if (p != null && !p.IsReadOnly)
            {
                p.Set(val);
                return true;
            }
            Log($"[SetParam] 警告：洞 {inst.Id} 参数 '{name}' 不存在或只读，写入失败。");
            return false;
        }

        public static void RotateToWall(Document doc, ElementId holeId, XYZ targetDir, XYZ org)
        {
            FamilyInstance fi = doc.GetElement(holeId) as FamilyInstance;
            if (fi == null) return;

            XYZ current = fi.HandOrientation;
            double ang = current.AngleTo(targetDir);
            double crossZ = current.CrossProduct(targetDir).Z;
            if (crossZ < 0) ang = -ang;

            Log($"[RotateToWall] 洞 {holeId} current={HoleCalc.FmtDir(current)} target={HoleCalc.FmtDir(targetDir)} " +
                $"angle={ang * 180.0 / Math.PI:F2}° (crossZ={crossZ:F4})");

            if (Math.Abs(ang) > 1e-3)
            {
                Line axis = Line.CreateUnbound(org, XYZ.BasisZ);
                ElementTransformUtils.RotateElement(doc, holeId, axis, ang);

                FamilyInstance fi2 = doc.GetElement(holeId) as FamilyInstance;
                Log($"[RotateToWall] 旋转后 HandOrient={HoleCalc.FmtDir(fi2?.HandOrientation)}");
            }
        }
    }
}