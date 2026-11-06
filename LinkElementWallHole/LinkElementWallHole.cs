using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Runtime.InteropServices;
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
        private double OFFSET_MM;
        private string FAMILY_NAME;
        private string SYMBOL_NAME;
        private string LEVEL_NAME;
        private double CABLE_TRAY_EXTRA_HEIGHT_MM;

        private const double MATCH_TOLERANCE_MM = 300.0;
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
                    fi.Category?.Id.IntegerValue == (int)BuiltInCategory.OST_GenericModel)
                {
                    holes.Add(fi);
                }
            }
            return holes;
        }

        // ==========================================================
        // 分支 A：创建新洞
        // 【改动】宽度改用截面参数，高度仍用 8 角点投影；位置和其余不变
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

            // ============ 【改动区开始】宽度用截面参数，高度用 8 角点投影 ============
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

                // --- 宽度：优先用截面参数 ---
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
                    // Fallback: 8 角点投影
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

                // --- 高度：始终用 8 角点投影（沿墙法向消距后取世界 Z） ---
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
            // ============ 【改动区结束】 ============

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
                                     "是否从当前项目中手动选择一个已有的墙洞实例作为模板？\n" +
                                     "（选择后将自动更新 config.txt 配置）";
                dialog.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;

                if (dialog.Show() == TaskDialogResult.Yes)
                {
                    try
                    {
                        Reference pickedRef = uidoc.Selection.PickObject(
                            ObjectType.Element,
                            new HoleInstanceSelectionFilter(),
                            "请选择一个当前项目中的墙洞实例（通用模型类别）");

                        FamilyInstance pickedInstance = doc.GetElement(pickedRef) as FamilyInstance;
                        if (pickedInstance != null && pickedInstance.Symbol != null)
                        {
                            holeSymbol = pickedInstance.Symbol;
                            FAMILY_NAME = holeSymbol.Family.Name;
                            SYMBOL_NAME = holeSymbol.Name;

                            UpdateConfigFile(FAMILY_NAME, SYMBOL_NAME);
                            Log($"[CreateNewHole] 用户手动指定族：{FAMILY_NAME} - {SYMBOL_NAME}，并已尝试更新 config.txt");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw new Exception("用户取消了墙洞族选择，操作结束。");
                    }
                }
                else
                {
                    throw new Exception($"未找到族：{FAMILY_NAME} - {SYMBOL_NAME}，且用户未手动选择。");
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

                Log($"[CreateNewHole] 实例创建 Id={hole.Id} 初始 HandOrient={FmtDir(hole.HandOrientation)} " +
                    $"FaceOrient={FmtDir(hole.FacingOrientation)}");

                ElementId holeId = hole.Id;
                HoleBiz.RotateToWall(doc, holeId, wallDir, insertPt);

                FamilyInstance holeAfter = doc.GetElement(holeId) as FamilyInstance;
                if (holeAfter == null) throw new Exception("创建后重新获取墙洞实例失败。");

                HoleBiz.SetParam(holeAfter, "NDH_Depth", depth);
                HoleBiz.SetParam(holeAfter, "NDH_Width", width);
                HoleBiz.SetParam(holeAfter, "NDH_Height", height);

                Log($"[CreateNewHole] 参数写入 NDH_Depth={FmtMm(depth)} NDH_Width={FmtMm(width)} NDH_Height={FmtMm(height)}");

                tran.Commit();
            }

            Log("[CreateNewHole] 创建成功。");
            return Result.Succeeded;
        }

        private void UpdateConfigFile(string familyName, string symbolName)
        {
            try
            {
                string dllPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                string dir = System.IO.Path.GetDirectoryName(dllPath);
                string path = System.IO.Path.Combine(dir, "config.txt");

                if (System.IO.File.Exists(path))
                {
                    var lines = System.IO.File.ReadAllLines(path).ToList();
                    var newLines = new List<string>();
                    int validLineIndex = 0;

                    foreach (var line in lines)
                    {
                        string trimmed = line.Trim();
                        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                        {
                            newLines.Add(line);
                            continue;
                        }
                        if (validLineIndex == 1) newLines.Add(familyName);
                        else if (validLineIndex == 2) newLines.Add(symbolName);
                        else newLines.Add(line);
                        validLineIndex++;
                    }
                    System.IO.File.WriteAllLines(path, newLines);
                }
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("UpdateConfigFile", ex);
            }
        }

        // ==========================================================
        // 分支 B：更新现有洞
        // 【改动】宽度改用截面参数，高度仍用 8 角点投影；位置和其余不变
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
                        foreach (var p in pipeElements)
                            if (pipeOutlines.TryGetValue(p, out var o))
                                Log($"[Update] 强绑成员 MEP {p.Id} {FmtOutline(o)}");
                    }
                    else
                    {
                        relatedPipes = HoleBiz.MatchPipesToHole(hole, pipeOutlines);
                        Log($"[Update] 洞 {hole.Id} 空间匹配到 {relatedPipes.Count} 个 MEP。");
                    }

                    if (relatedPipes.Count == 0)
                    {
                        skippedNoMatch++;
                        Log($"[Update] 洞 {hole.Id} 未匹配到任何 MEP，跳过。");
                        continue;
                    }

                    // ============ 【改动区开始】宽度用截面参数，高度用 8 角点投影 ============
                    XYZ wallOrigin = new XYZ(lp.Point.X, lp.Point.Y, 0);
                    double minU = double.MaxValue, maxU = double.MinValue;
                    double minZ = double.MaxValue, maxZ = double.MinValue;
                    bool hasWidth = false;
                    bool hasHeight = false;

                    foreach (var pipe in relatedPipes)
                    {
                        if (!pipeOutlines.TryGetValue(pipe, out Outline outline)) continue;

                        // --- 宽度：优先用截面参数 ---
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
                            // Fallback: 8 角点投影
                            Log($"[Update] MEP {pipe.Id} 截面参数不可用，宽度退回 8 角点投影");
                            foreach (var lp3 in HoleCalc.EnumerateCorners(outline))
                            {
                                double u = (lp3 - wallOrigin).DotProduct(wallDir);
                                if (u < minU) minU = u;
                                if (u > maxU) maxU = u;
                            }
                            hasWidth = true;
                        }

                        // --- 高度：始终用 8 角点投影（世界 Z） ---
                        foreach (var lp3 in HoleCalc.EnumerateCorners(outline))
                        {
                            if (lp3.Z < minZ) minZ = lp3.Z;
                            if (lp3.Z > maxZ) maxZ = lp3.Z;
                            hasHeight = true;
                        }
                    }

                    if (!hasHeight) { skippedNoMatch++; continue; }
                    if (!hasWidth)   { skippedNoMatch++; continue; }
                    // ============ 【改动区结束】 ============

                    double offsetFeet = OFFSET_MM / MM_PER_FOOT;
                    double width = (maxU - minU) + 2 * offsetFeet;
                    double height = (maxZ - minZ) + 2 * offsetFeet;
                    double trayExtra = HoleBiz.GetCableTrayExtraHeightFeet(relatedPipes, pipeTf, CABLE_TRAY_EXTRA_HEIGHT_MM);
                    height += trayExtra;

                    double depth = hole.LookupParameter("NDH_Depth")?.AsDouble() ?? 0.0;
                    if (depth <= 0.0) depth = 200.0 / MM_PER_FOOT;

                    Log($"[Update] 洞 {hole.Id} 投影 minU={FmtMm(minU)} maxU={FmtMm(maxU)} " +
                        $"minZ={FmtMm(minZ)} maxZ={FmtMm(maxZ)} → " +
                        $"width={FmtMm(width)} height={FmtMm(height)} depth={FmtMm(depth)} trayExtra={FmtMm(trayExtra)}");

                    if (holeRefs.Count == 1 && pipeElements.Count > 1)
                    {
                        double uSpan = (maxU - minU) * MM_PER_FOOT;
                        double zSpan = (maxZ - minZ) * MM_PER_FOOT;
                        if (uSpan > 3000 || zSpan > 3000)
                            Log($"[Update] 警告：单洞强绑了跨度较大的 MEP（U跨度={uSpan:F0}mm Z跨度={zSpan:F0}mm），请核对洞口位置与尺寸是否符合预期。");
                    }

                    HoleBiz.SetParam(hole, "NDH_Width", width);
                    HoleBiz.SetParam(hole, "NDH_Height", height);
                    HoleBiz.SetParam(hole, "NDH_Depth", depth);

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
                    throw new Exception(
                        $"未更新任何墙洞。\n" +
                        $"可能原因：MEP 与墙洞空间不重叠（相距超过 {MATCH_TOLERANCE_MM}mm），" +
                        $"或所选元素非族 \"{FAMILY_NAME}\"。\n（跳过 {skippedNoMatch} 个）");
                }
                Log($"[Update] 完成。共更新 {updated} 个，跳过 {skippedNoMatch} 个。");
            }

            return Result.Succeeded;
        }

        private void LoadConfig()
        {
            try
            {
                string dllPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                string dir = System.IO.Path.GetDirectoryName(dllPath);
                string path = System.IO.Path.Combine(dir, "config.txt");

                if (System.IO.File.Exists(path))
                {
                    var validLines = System.IO.File.ReadAllLines(path)
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("#"))
                        .ToList();

                    if (validLines.Count >= 5)
                    {
                        OFFSET_MM = double.Parse(validLines[0]);
                        FAMILY_NAME = validLines[1];
                        SYMBOL_NAME = validLines[2];
                        LEVEL_NAME = validLines[3];
                        CABLE_TRAY_EXTRA_HEIGHT_MM = double.Parse(validLines[4]);
                        Log($"[LoadConfig] OFFSET={OFFSET_MM}mm FAMILY='{FAMILY_NAME}' SYMBOL='{SYMBOL_NAME}' " +
                            $"LEVEL='{LEVEL_NAME}' TRAY_EXTRA={CABLE_TRAY_EXTRA_HEIGHT_MM}mm");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("LoadConfig", ex);
            }

            if (string.IsNullOrEmpty(FAMILY_NAME)) FAMILY_NAME = "REWallHole";
            if (string.IsNullOrEmpty(SYMBOL_NAME)) SYMBOL_NAME = "Rect CBWD for Wall";
            if (string.IsNullOrEmpty(LEVEL_NAME)) LEVEL_NAME = "WCS 0.0";
            if (OFFSET_MM <= 0) OFFSET_MM = 100.0;
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
                return linkedElem is Duct || linkedElem is Pipe || linkedElem is CableTray;
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

        private class HoleInstanceSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                return elem is FamilyInstance fi &&
                       fi.Category?.Id.IntegerValue == (int)BuiltInCategory.OST_GenericModel;
            }

            public bool AllowReference(Reference reference, XYZ position) => true;
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
                if (e != null && (e is Duct || e is Pipe || e is CableTray))
                    pipeElements.Add(e);
            }

            if (pipeElements.Count == 0) return false;
            pipeTf = pipeLinkInstance.GetTotalTransform();
            Log($"[ParsePipeRefs] 链接={pipeLinkInstance.Id} linkTf.IsIdentity={pipeTf.IsIdentity} 收集 MEP {pipeElements.Count} 个。");
            return true;
        }

        /// <summary>
        /// 【新增】用 MEP 截面参数计算沿墙方向的洞口宽度贡献。
        /// 返回该 MEP 沿 wallDir 的中心位置 uCenter 和投影宽度 widthAlong。
        /// 立管等无法用截面参数计算时返回 false（调用方应 fallback 到 8 角点投影）。
        /// </summary>
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

            // 中心位置投影
            XYZ midLocal = curve.Evaluate(0.5, true);
            XYZ midWorld = tf.OfPoint(midLocal);
            uCenter = (midWorld - wallOrigin).DotProduct(wallDir);

            // 轴线方向（世界坐标）
            XYZ axisLocal = curve.GetEndPoint(1) - curve.GetEndPoint(0);
            XYZ axisWorld = tf.OfVector(axisLocal);
            if (axisWorld.GetLength() < 1e-9) return false;
            axisWorld = axisWorld.Normalize();

            // 管道：圆形截面，任意水平方向投影 = Diameter
            if (e is Pipe pipe)
            {
                widthAlong = pipe.Diameter;
                return widthAlong > 0;
            }

            // 风管 / 桥架：矩形截面
            double baseWidth = 0;
            if (e is Duct duct) baseWidth = duct.Width;
            else if (e is CableTray tray) baseWidth = tray.Width;
            if (baseWidth <= 0) return false;

            // 截面水平方向 = 轴线方向 × Z
            XYZ widthDir = axisWorld.CrossProduct(XYZ.BasisZ);
            if (widthDir.GetLength() < 1e-6)
            {
                // 立管：轴线竖直，退回 8 角点投影
                return false;
            }
            widthDir = widthDir.Normalize();

            // 沿 wallDir 投影
            widthAlong = baseWidth * Math.Abs(widthDir.DotProduct(wallDir));
            return widthAlong > 0;
        }

        public static List<Element> MatchPipesToHole(FamilyInstance hole, Dictionary<Element, Outline> pipeOutlines)
        {
            BoundingBoxXYZ holeBbox = hole.get_BoundingBox(null);
            if (holeBbox == null) return new List<Element>();

            double tol = MATCH_TOLERANCE_MM / MM_PER_FOOT;
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

        public static void SetParam(FamilyInstance inst, string name, double val)
        {
            Parameter p = inst.LookupParameter(name);
            if (p != null && !p.IsReadOnly)
            {
                p.Set(val);
            }
            else
            {
                Log($"[SetParam] 警告：洞 {inst.Id} 参数 '{name}' 不存在或只读，跳过。");
            }
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