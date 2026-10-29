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

namespace MEPTools.LinkElementFloorHole
{
    [Transaction(TransactionMode.Manual)]
    public class Command : IExternalCommand
    {
        private double OFFSET_MM;
        private string FAMILY_NAME;              // 建议 config.txt 第 2 行填 "GEN-NJV-CBWD-VOID"
        private string SYMBOL_NAME;              // 建议 config.txt 第 3 行填 "Rect CBWD for Floor"
        private string LEVEL_NAME;
        private double CABLE_TRAY_EXTRA_HEIGHT_MM; // 楼板洞通常不需要这个，但保留配置位

        // 匹配容差（毫米）：在 XY 平面上找相关的管子
        private const double MATCH_TOLERANCE_MM = 300.0;

        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            MEPLogger.Instance.StartSession("链接楼板洞创建/更新");

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
                    MEPLogger.Instance.AppendLine($"[Command] 发现预选了 {preSelectedHoles.Count} 个楼板洞，进入更新流程。");

                    IList<Reference> pipeRefs = null;
                    try
                    {
                        using (var finisher = new SpaceKeyFinisher())
                        {
                            finisher.Enable();
                            pipeRefs = uidoc.Selection.PickObjects(
                                ObjectType.LinkedElement,
                                new LinkMEPSelectionFilter(doc),
                                $"已识别预选的 {preSelectedHoles.Count} 个楼板洞。\n" +
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

                    var holeRefs = preSelectedHoles.Select(h => new Reference(h)).ToList();
                    return UpdateExistingFloorHoles(doc, holeRefs, pipeElements, pipeTf);
                }
                else
                {
                    // ------------------------------------------------------
                    // 分支 A：无预选孔 → 进入新建流程（分两步）
                    // ------------------------------------------------------
                    MEPLogger.Instance.AppendLine("[Command] 未预选楼板洞，进入新建流程。");

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
                                "【新建楼板洞 - 步骤 1 / 2】\n" +
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

                    // 步骤 2：选择 Linked 楼板
                    Reference floorRef = null;
                    try
                    {
                        using (var finisher = new SpaceKeyFinisher())
                        {
                            finisher.Enable();
                            floorRef = uidoc.Selection.PickObject(
                                ObjectType.LinkedElement,
                                new LinkFloorSelectionFilter(doc),
                                "【新建楼板洞 - 步骤 2 / 2】\n" +
                                "请选择 1 面需要开洞的链接楼板");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw new Exception("未选择楼板，操作取消。");
                    }

                    return CreateNewFloorHole(doc, floorRef, pipeElements, pipeTf);
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
        // 辅助逻辑：获取预选的楼板洞
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
        // 分支 A：创建新楼板洞
        // ==========================================================
        private Result CreateNewFloorHole(
            Document doc, Reference floorRef,
            List<Element> pipeElements, Transform pipeTf)
        {
            MEPLogger.Instance.AppendLine("[CreateNewFloorHole] 开始创建新楼板洞。");
            
            RevitLinkInstance floorLinkInst = doc.GetElement(floorRef.ElementId) as RevitLinkInstance;
            Document floorLinkDoc = floorLinkInst.GetLinkDocument();
            Floor targetFloor = floorLinkDoc.GetElement(floorRef.LinkedElementId) as Floor;

            // 1. 计算 MEP 在 XY 平面的极值
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;

            foreach (Element elem in pipeElements)
            {
                BoundingBoxXYZ bbox = elem.get_BoundingBox(null);
                if (bbox == null) continue;
                
                Outline worldOutline = GetWorldOutline(bbox, pipeTf);
                if (worldOutline == null) continue;

                minX = Math.Min(minX, worldOutline.MinimumPoint.X);
                maxX = Math.Max(maxX, worldOutline.MaximumPoint.X);
                minY = Math.Min(minY, worldOutline.MinimumPoint.Y);
                maxY = Math.Max(maxY, worldOutline.MaximumPoint.Y);
            }

            // 2. 加上预留偏移
            double offsetFeet = OFFSET_MM / 304.8;
            double width = (maxX - minX) + 2 * offsetFeet;  // X 方向尺寸
            double height = (maxY - minY) + 2 * offsetFeet; // Y 方向尺寸

            // 3. 获取楼板厚度作为深度
            double floorThickness = targetFloor.get_Parameter(BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM).AsDouble();
            // 地洞的深度通常比楼板稍微厚一点，或者直接等于楼板厚度
            double depth = floorThickness + 2 * offsetFeet; 

            // 4. 计算插入点
            double centerX = (minX + maxX) / 2;
            double centerY = (minY + maxY) / 2;
            
            // 获取当前项目中的标高（基于配置）
            Level level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault(l => l.Name == LEVEL_NAME);

            if (level == null)
                throw new Exception($"未找到标高：{LEVEL_NAME}");

            // Z 轴坐标：使用标高高度，或者楼板顶面高度
            XYZ insertPt = new XYZ(centerX, centerY, level.Elevation);

            FamilySymbol holeSymbol = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(s => s.Family.Name == FAMILY_NAME && s.Name == SYMBOL_NAME);

            if (holeSymbol == null)
                throw new Exception($"未找到族：{FAMILY_NAME} - {SYMBOL_NAME}");

            using (Transaction tran = new Transaction(doc, "创建链接楼板洞"))
            {
                tran.Start();
                if (!holeSymbol.IsActive) holeSymbol.Activate();

                FamilyInstance hole = doc.Create.NewFamilyInstance(
                    insertPt, holeSymbol, level,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                // 设置参数
                SetParam(hole, "NDH_Width", width);
                SetParam(hole, "NDH_Height", height);
                SetParam(hole, "NDH_Depth", depth);

                tran.Commit();
            }

            MEPLogger.Instance.AppendLine("[CreateNewFloorHole] 创建成功。");
            return Result.Succeeded;
        }

        // ==========================================================
        // 分支 B：根据已选 MEP + 已选楼板洞 更新洞
        // ==========================================================
        private Result UpdateExistingFloorHoles(
            Document doc,
            IList<Reference> holeRefs,
            List<Element> pipeElements,
            Transform pipeTf)
        {
            MEPLogger.Instance.AppendLine("[UpdateExistingFloorHoles] 开始更新楼板洞。");

            // 预处理 MEP 包围盒
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

            using (Transaction tran = new Transaction(doc, "根据MEP更新楼板洞"))
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

                    // 匹配逻辑
                    List<Element> relatedPipes;
                    if (holeRefs.Count == 1)
                    {
                        relatedPipes = pipeElements;
                        MEPLogger.Instance.AppendLine($"[Update] 仅选中 1 个洞，强制绑定所有 MEP。");
                    }
                    else
                    {
                        relatedPipes = MatchPipesToHole(hole, pipeOutlines);
                    }

                    if (relatedPipes.Count == 0)
                    {
                        skippedNoMatch++;
                        continue;
                    }

                    // 计算 XY 极值
                    double minX = double.MaxValue, maxX = double.MinValue;
                    double minY = double.MaxValue, maxY = double.MinValue;

                    foreach (var pipe in relatedPipes)
                    {
                        if (!pipeOutlines.TryGetValue(pipe, out Outline outline)) continue;
                        minX = Math.Min(minX, outline.MinimumPoint.X);
                        maxX = Math.Max(maxX, outline.MaximumPoint.X);
                        minY = Math.Min(minY, outline.MinimumPoint.Y);
                        maxY = Math.Max(maxY, outline.MaximumPoint.Y);
                    }

                    double offsetFeet = OFFSET_MM / 304.8;
                    double width = (maxX - minX) + 2 * offsetFeet;
                    double height = (maxY - minY) + 2 * offsetFeet;

                    // 读取已有深度，不要重算
                    double depth = hole.LookupParameter("NDH_Depth")?.AsDouble() ?? 0.0;
                    if (depth <= 0.0) depth = 200.0 / 304.8;

                    // 更新参数
                    SetParam(hole, "NDH_Width", width);
                    SetParam(hole, "NDH_Height", height);
                    SetParam(hole, "NDH_Depth", depth);

                    // 计算位置移动
                    XYZ posNow = lp.Point;
                    XYZ targetPos = new XYZ((minX + maxX) / 2, (minY + maxY) / 2, posNow.Z);
                    XYZ delta = targetPos - posNow;
                    if (delta.GetLength() > 1e-6)
                        ElementTransformUtils.MoveElement(doc, hole.Id, delta);

                    updated++;
                }

                tran.Commit();

                if (updated == 0)
                {
                    throw new Exception($"未更新任何楼板洞。\n（跳过 {skippedNoMatch} 个未匹配的洞）");
                }
            }

            return Result.Succeeded;
        }

        // ========== 辅助方法：XY 平面相交判断 ==========
        private List<Element> MatchPipesToHole(
            FamilyInstance hole, Dictionary<Element, Outline> pipeOutlines)
        {
            BoundingBoxXYZ holeBbox = hole.get_BoundingBox(null);
            if (holeBbox == null) return new List<Element>();

            double tol = MATCH_TOLERANCE_MM / 304.8;
            // 对于楼板洞，我们主要在 XY 平面上判断是否重叠
            Outline holeOutline = new Outline(
                new XYZ(holeBbox.Min.X - tol, holeBbox.Min.Y - tol, double.MinValue),
                new XYZ(holeBbox.Max.X + tol, holeBbox.Max.Y + tol, double.MaxValue));

            List<Element> result = new List<Element>();
            foreach (var kvp in pipeOutlines)
            {
                // 只要 X 和 Y 的投影有交集，就算匹配
                Outline pipeXYOutline = new Outline(
                    new XYZ(kvp.Value.MinimumPoint.X, kvp.Value.MinimumPoint.Y, double.MinValue),
                    new XYZ(kvp.Value.MaximumPoint.X, kvp.Value.MaximumPoint.Y, double.MaxValue));

                if (holeOutline.Intersects(pipeXYOutline, tol))
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

        // ========== 辅助方法：提取链接中的 MEP ==========
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

        void SetParam(FamilyInstance inst, string name, double val)
        {
            Parameter p = inst.LookupParameter(name);
            if (p != null && !p.IsReadOnly) p.Set(val);
        }

        // ========== 选择过滤器区 ==========

        // 过滤器：只能选链接中的 MEP
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

        // 过滤器：只能选链接中的楼板
        private class LinkFloorSelectionFilter : ISelectionFilter
        {
            private readonly Document _doc;
            public LinkFloorSelectionFilter(Document doc) { _doc = doc; }

            public bool AllowElement(Element elem) => elem is RevitLinkInstance;

            public bool AllowReference(Reference reference, XYZ position)
            {
                RevitLinkInstance linkInst = _doc.GetElement(reference.ElementId) as RevitLinkInstance;
                if (linkInst == null) return false;
                Document linkDoc = linkInst.GetLinkDocument();
                if (linkDoc == null) return false;
                Element linkedElem = linkDoc.GetElement(reference.LinkedElementId);
                
                return linkedElem is Floor || linkedElem.Category?.Id.IntegerValue == (int)BuiltInCategory.OST_Floors;
            }
        }
    }
}