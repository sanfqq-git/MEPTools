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

using MEPTools.Shared;   // SpaceKeyFinisher, MEPLogger

namespace MEPTools.LinkElementWallHole
{
    [Transaction(TransactionMode.Manual)]
    public class Command : IExternalCommand
    {
        private double OFFSET_MM;
        private string FAMILY_NAME;              // 建议 config.txt 第 2 行填 "REWallHole"
        private string SYMBOL_NAME;
        private string LEVEL_NAME;
        private double CABLE_TRAY_EXTRA_HEIGHT_MM;

        // 匹配容差（毫米）：只要包围盒在 300mm 内有交集即视为关联
        private const double MATCH_TOLERANCE_MM = 300.0;

        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            MEPLogger.Instance.StartSession("链接墙洞创建/更新");

            try
            {
                LoadConfig();

                // ==========================================================
                // 阶段 0：检查预选择，决定分支
                // ==========================================================
                IList<Element> preSelectedHoles = GetPreSelectedHoles(uidoc);

                if (preSelectedHoles.Count > 0)
                {
                    // ------------------------------------------------------
                    // 分支 B：有预选孔 → 进入更新流程
                    // ------------------------------------------------------
                    MEPLogger.Instance.AppendLine($"[Command] 发现预选了 {preSelectedHoles.Count} 个墙洞，进入更新流程。");

                    IList<Reference> pipeRefs = null;
                    try
                    {
                        using (var finisher = new SpaceKeyFinisher())
                        {
                            finisher.Enable();
                            // 严格限制：只能选择链接里的 MEP
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

                    if (!ParsePipeReferences(doc, pipeRefs, out _, out var pipeElements, out var pipeTf))
                        throw new Exception("未选中任何有效的管道/桥架/风管，或它们不在同一个链接中。");

                    // 提取 ElementIds 供后续使用
                    var holeRefs = preSelectedHoles.Select(h => new Reference(h)).ToList();
                    return UpdateExistingHoles(doc, holeRefs, pipeElements, pipeTf);
                }
                else
                {
                    // ------------------------------------------------------
                    // 分支 A：无预选孔 → 进入新建流程（分两步）
                    // ------------------------------------------------------
                    MEPLogger.Instance.AppendLine("[Command] 未预选墙洞，进入新建流程。");

                    // 步骤 1：选择 Linked MEP
                    IList<Reference> pipeRefs = null;
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

                    if (!ParsePipeReferences(doc, pipeRefs, out _, out var pipeElements, out var pipeTf))
                        throw new Exception("未选中任何有效的管道/桥架/风管，或它们不在同一个链接中。");

                    // 步骤 2：选择 Linked 墙
                    Reference wallRef = null;
                    try
                    {
                        using (var finisher = new SpaceKeyFinisher())
                        {
                            finisher.Enable();
                            wallRef = uidoc.Selection.PickObject(
                                ObjectType.LinkedElement,
                                new LinkWallSelectionFilter(doc),
                                "【新建洞 - 步骤 2 / 2】\n" +
                                "请选择 1 面需要开洞的链接墙");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw new Exception("未选择墙，操作取消。");
                    }

                    return CreateNewHole(doc, wallRef, pipeElements, pipeTf);
                }
            }
            catch (OperationCanceledException)
            {
                MEPLogger.Instance.AppendLine("[Command] 用户取消了操作。");
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

        // ==========================================================
        // 辅助逻辑：获取预选的墙洞
        // ==========================================================
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
        // ==========================================================
        private Result CreateNewHole(
            Document doc, Reference wallRef,
            List<Element> pipeElements, Transform pipeTf)
        {
            MEPLogger.Instance.AppendLine("[CreateNewHole] 开始创建新洞。");
            RevitLinkInstance wallLinkInstance = doc.GetElement(wallRef.ElementId) as RevitLinkInstance;
            Document wallLinkDoc = wallLinkInstance.GetLinkDocument();
            Wall targetWall = wallLinkDoc.GetElement(wallRef.LinkedElementId) as Wall;

            LocationCurve wallLoc = targetWall.Location as LocationCurve;
            Line wallLine = wallLoc.Curve as Line;
            if (wallLine == null) throw new Exception("仅支持直线墙");

            Transform wallTf = wallLinkInstance.GetTotalTransform();
            Line wallWorldLine = wallLine.CreateTransformed(wallTf) as Line;
            XYZ wallDir = wallWorldLine.Direction.Normalize();
            XYZ wallOrigin = wallWorldLine.GetEndPoint(0);
            double wallThickness = targetWall.Width;
            XYZ wallNormal = new XYZ(wallDir.Y, -wallDir.X, 0).Normalize();

            List<XYZ> allPoints = new List<XYZ>();
            
            foreach (Element elem in pipeElements)
            {
                BoundingBoxXYZ bbox = elem.get_BoundingBox(null);
                if (bbox == null) continue;
                
                Outline worldOutline = GetWorldOutline(bbox, pipeTf);
                if (worldOutline == null) continue;

                allPoints.Add(worldOutline.MinimumPoint);
                allPoints.Add(worldOutline.MaximumPoint);
            }

            double minU = double.MaxValue, maxU = double.MinValue;
            double minZproj = double.MaxValue, maxZproj = double.MinValue;

            foreach (XYZ pt in allPoints)
            {
                double dist = (pt - wallOrigin).DotProduct(wallNormal);
                XYZ ptOnPlane = pt - dist * wallNormal;
                double u = (ptOnPlane - wallOrigin).DotProduct(wallDir);
                minU = Math.Min(minU, u);
                maxU = Math.Max(maxU, u);
                minZproj = Math.Min(minZproj, ptOnPlane.Z);
                maxZproj = Math.Max(maxZproj, ptOnPlane.Z);
            }

            double offsetFeet = OFFSET_MM / 304.8;
            double width = (maxU - minU) + 2 * offsetFeet;
            double height = (maxZproj - minZproj) + 2 * offsetFeet;
            height += GetCableTrayExtraHeightFeet(pipeElements, pipeTf);

            double depth = wallThickness;

            XYZ insertPt = wallOrigin + (minU + maxU) / 2 * wallDir;
            insertPt = new XYZ(insertPt.X, insertPt.Y, minZproj - offsetFeet);

            FamilySymbol holeSymbol = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(s => s.Family.Name == FAMILY_NAME && s.Name == SYMBOL_NAME);

            if (holeSymbol == null)
                throw new Exception($"未找到族：{FAMILY_NAME} - {SYMBOL_NAME}");

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

                RotateToWall(doc, hole, wallDir, insertPt);

                SetParam(hole, "NDH_Depth", depth);
                SetParam(hole, "NDH_Width", width);
                SetParam(hole, "NDH_Height", height);

                tran.Commit();
            }

            MEPLogger.Instance.AppendLine("[CreateNewHole] 创建成功。");
            return Result.Succeeded;
        }

        // ==========================================================
        // 分支 B：根据已选 MEP + 已选 WallHole 更新洞
        // ==========================================================
        private Result UpdateExistingHoles(
            Document doc,
            IList<Reference> holeRefs,
            List<Element> pipeElements,
            Transform pipeTf)
        {
            MEPLogger.Instance.AppendLine("[UpdateExistingHoles] 开始更新墙洞。");

            Dictionary<Element, Outline> pipeOutlines = new Dictionary<Element, Outline>();
            foreach (var pipe in pipeElements)
            {
                BoundingBoxXYZ pb = pipe.get_BoundingBox(null);
                if (pb != null)
                {
                    Outline outline = GetWorldOutline(pb, pipeTf);
                    if (outline != null) pipeOutlines.Add(pipe, outline);
                }
            }

            using (Transaction tran = new Transaction(doc, "根据MEP更新墙洞"))
            {
                tran.Start();

                int updated = 0;
                int skippedNoMatch = 0;

                foreach (Reference hr in holeRefs)
                {
                    FamilyInstance hole = doc.GetElement(hr.ElementId) as FamilyInstance;
                    if (hole == null) continue;
                    if (hole.Symbol?.Family?.Name != FAMILY_NAME)
                    {
                        skippedNoMatch++;
                        continue;
                    }

                    LocationPoint lp = hole.Location as LocationPoint;
                    if (lp == null) continue;

                    List<Element> relatedPipes;
                    if (holeRefs.Count == 1)
                    {
                        // 【兜底逻辑】只选了 1 个洞，无视空间相交判断，强绑所有选中的 MEP
                        relatedPipes = pipeElements;
                        MEPLogger.Instance.AppendLine($"[Update] 仅选中 1 个洞，强制将所有 {pipeElements.Count} 个 MEP 绑定到此洞。");
                    }
                    else
                    {
                        // 如果选了多个洞，通过空间相交分配归属
                        relatedPipes = MatchPipesToHole(hole, pipeOutlines);
                    }

                    if (relatedPipes.Count == 0)
                    {
                        skippedNoMatch++;
                        MEPLogger.Instance.AppendLine($"[Update] 洞 {hole.Id} 未匹配到任何 MEP，跳过。");
                        continue;
                    }

                    XYZ holePos = lp.Point;
                    XYZ wallDir = hole.HandOrientation.Normalize();
                    XYZ wallOrigin = new XYZ(holePos.X, holePos.Y, 0);

                    double minU = double.MaxValue, maxU = double.MinValue;
                    double minZ = double.MaxValue, maxZ = double.MinValue;
                    bool hasPt = false;

                    foreach (var pipe in relatedPipes)
                    {
                        if (!pipeOutlines.TryGetValue(pipe, out Outline outline)) continue;

                        for (int i = 0; i <= 1; i++)
                        for (int j = 0; j <= 1; j++)
                        for (int k = 0; k <= 1; k++)
                        {
                            XYZ lp3 = new XYZ(
                                i == 0 ? outline.MinimumPoint.X : outline.MaximumPoint.X,
                                j == 0 ? outline.MinimumPoint.Y : outline.MaximumPoint.Y,
                                k == 0 ? outline.MinimumPoint.Z : outline.MaximumPoint.Z);
                            
                            double u = (lp3 - wallOrigin).DotProduct(wallDir);
                            minU = Math.Min(minU, u);
                            maxU = Math.Max(maxU, u);
                            minZ = Math.Min(minZ, lp3.Z);
                            maxZ = Math.Max(maxZ, lp3.Z);
                            hasPt = true;
                        }
                    }
                    if (!hasPt) { skippedNoMatch++; continue; }

                    double offsetFeet = OFFSET_MM / 304.8;
                    double width = (maxU - minU) + 2 * offsetFeet;
                    double height = (maxZ - minZ) + 2 * offsetFeet;
                    height += GetCableTrayExtraHeightFeet(relatedPipes, pipeTf);

                    double depth = hole.LookupParameter("NDH_Depth")?.AsDouble() ?? 0.0;
                    if (depth <= 0.0) depth = 200.0 / 304.8;

                    SetParam(hole, "NDH_Width", width);
                    SetParam(hole, "NDH_Height", height);
                    SetParam(hole, "NDH_Depth", depth);

                    XYZ posNow = (hole.Location as LocationPoint)?.Point ?? holePos;
                    XYZ targetXY = wallOrigin + ((minU + maxU) / 2) * wallDir;
                    XYZ targetPos = new XYZ(targetXY.X, targetXY.Y, minZ - offsetFeet);
                    XYZ delta = targetPos - posNow;
                    if (delta.GetLength() > 1e-6)
                        ElementTransformUtils.MoveElement(doc, hole.Id, delta);

                    updated++;
                    MEPLogger.Instance.AppendLine($"[Update] 成功更新洞 {hole.Id}，关联 MEP 数量：{relatedPipes.Count}。");
                }

                tran.Commit();

                if (updated == 0)
                {
                    throw new Exception(
                        $"未更新任何墙洞。\n" +
                        $"可能原因：您选中的 MEP 与所选墙洞在空间上并没有重叠（相隔超过 {MATCH_TOLERANCE_MM}mm），\n" +
                        $"或所选元素不是族名 \"{FAMILY_NAME}\" 的墙洞。\n" +
                        $"（跳过 {skippedNoMatch} 个未匹配的洞）");
                }
                
                MEPLogger.Instance.AppendLine($"[Update] 更新完成。共更新 {updated} 个洞，跳过 {skippedNoMatch} 个。");
            }

            return Result.Succeeded;
        }

        // ========== 辅助方法：提取链接中的 MEP 引用 ==========
        private bool ParsePipeReferences(
            Document doc,
            IList<Reference> refs,
            out RevitLinkInstance pipeLinkInstance,
            out List<Element> pipeElements,
            out Transform pipeTf)
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
                if (e != null) 
                {
                    if (e is Duct || e is Pipe || e is CableTray)
                        pipeElements.Add(e);
                }
            }

            if (pipeElements.Count == 0) return false;
            
            pipeTf = pipeLinkInstance.GetTotalTransform();
            return true;
        }

        // ========== 核心逻辑：包围盒相交判断 ==========
        private List<Element> MatchPipesToHole(
            FamilyInstance hole, Dictionary<Element, Outline> pipeOutlines)
        {
            BoundingBoxXYZ holeBbox = hole.get_BoundingBox(null);
            if (holeBbox == null) return new List<Element>();

            double tol = MATCH_TOLERANCE_MM / 304.8;
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

        // ========== 辅助方法：精确计算包围盒 ==========
        private Outline GetWorldOutline(BoundingBoxXYZ bbox, Transform transform)
        {
            if (bbox == null) return null;

            XYZ[] corners = new XYZ[8];
            corners[0] = new XYZ(bbox.Min.X, bbox.Min.Y, bbox.Min.Z);
            corners[1] = new XYZ(bbox.Max.X, bbox.Min.Y, bbox.Min.Z);
            corners[2] = new XYZ(bbox.Min.X, bbox.Max.Y, bbox.Min.Z);
            corners[3] = new XYZ(bbox.Max.X, bbox.Max.Y, bbox.Min.Z);
            corners[4] = new XYZ(bbox.Min.X, bbox.Min.Y, bbox.Max.Z);
            corners[5] = new XYZ(bbox.Max.X, bbox.Min.Y, bbox.Max.Z);
            corners[6] = new XYZ(bbox.Min.X, bbox.Max.Y, bbox.Max.Z);
            corners[7] = new XYZ(bbox.Max.X, bbox.Max.Y, bbox.Max.Z);

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

            foreach (XYZ corner in corners)
            {
                XYZ worldPt = transform.OfPoint(corner);
                minX = Math.Min(minX, worldPt.X); maxX = Math.Max(maxX, worldPt.X);
                minY = Math.Min(minY, worldPt.Y); maxY = Math.Max(maxY, worldPt.Y);
                minZ = Math.Min(minZ, worldPt.Z); maxZ = Math.Max(maxZ, worldPt.Z);
            }

            return new Outline(new XYZ(minX, minY, minZ), new XYZ(maxX, maxY, maxZ));
        }

        // ========== 读取配置 ==========
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
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("LoadConfig", ex);
            }
        }

        // ========== 桥架额外高度 ==========
        private double GetCableTrayExtraHeightFeet(List<Element> elements, Transform transform)
        {
            double maxZ = double.MinValue;
            Element highest = null;

            foreach (Element e in elements)
            {
                BoundingBoxXYZ bbox = e.get_BoundingBox(null);
                if (bbox == null) continue;

                Outline worldOutline = GetWorldOutline(bbox, transform);
                if (worldOutline == null) continue;

                if (worldOutline.MaximumPoint.Z > maxZ)
                {
                    maxZ = worldOutline.MaximumPoint.Z;
                    highest = e;
                }
            }

            return (highest is CableTray) ? CABLE_TRAY_EXTRA_HEIGHT_MM / 304.8 : 0.0;
        }

        void SetParam(FamilyInstance inst, string name, double val)
        {
            Parameter p = inst.LookupParameter(name);
            if (p != null && !p.IsReadOnly) p.Set(val);
        }

        void RotateToWall(Document doc, FamilyInstance fi, XYZ dir, XYZ org)
        {
            double ang = fi.HandOrientation.AngleTo(dir);
            if (ang > 0.001)
            {
                Line axis = Line.CreateUnbound(org, XYZ.BasisZ);
                ElementTransformUtils.RotateElement(doc, fi.Id, axis, ang);
            }
        }

        // ========== 选择过滤器区 ==========

        // 1. 过滤器：只能选链接中的 MEP（管/风管/桥架）
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
                
                return linkedElem is Duct 
                    || linkedElem is Pipe 
                    || linkedElem is CableTray;
            }
        }

        // 2. 过滤器：只能选链接中的墙
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
                
                return linkedElem is Wall 
                    || linkedElem.Category?.Id.IntegerValue == (int)BuiltInCategory.OST_Walls;
            }
        }
    }
}