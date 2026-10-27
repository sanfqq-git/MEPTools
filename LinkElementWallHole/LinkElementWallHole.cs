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

using MEPTools.Shared;   // SpaceKeyFinisher

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

        // 无墙分支：MEP 包围盒中心距洞 bbox（扩大后）多远仍算相关
        private const double SEARCH_TOLERANCE_MM = 1500.0;

        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                LoadConfig();

                // ==========================================================
                // 第 1 次选择：链接里的墙（可选，最多 1 面）+ MEP（必选）
                // ==========================================================
                IList<Reference> selectedRefs;
                using (var finisher = new SpaceKeyFinisher())
                {
                    finisher.Enable();
                    selectedRefs = uidoc.Selection.PickObjects(
                        ObjectType.LinkedElement,
                        new AnyLinkElementFilter(),
                        "步骤 1 / 2：\n" +
                        "  【创建新洞】选 1 面墙 + 1~N 个 MEP，按空格结束\n" +
                        "  【更新已有洞】只选 MEP（不选墙），按空格结束\n" +
                        "  （不选墙时会进入步骤 2 让您选择要更新的墙洞）");
                }

                if (selectedRefs == null || selectedRefs.Count == 0)
                    throw new Exception("未选择任何元素。");

                List<Reference> wallRefs = new List<Reference>();
                List<Reference> pipeRefs = new List<Reference>();

                foreach (Reference r in selectedRefs)
                {
                    RevitLinkInstance linkInst = doc.GetElement(r.ElementId) as RevitLinkInstance;
                    if (linkInst == null) continue;
                    Document linkDoc = linkInst.GetLinkDocument();
                    if (linkDoc == null) continue;
                    Element linkedElem = linkDoc.GetElement(r.LinkedElementId);
                    if (linkedElem == null) continue;

                    if (linkedElem is Wall)
                        wallRefs.Add(r);
                    else if (linkedElem is Duct || linkedElem is Pipe || linkedElem is CableTray)
                        pipeRefs.Add(r);
                }

                if (pipeRefs.Count == 0)
                    throw new Exception("未选中任何管道/桥架/风管。");
                if (wallRefs.Count > 1)
                    throw new Exception("墙最多只能选一面。");

                // 构建 pipeElements（两条分支共用）
                RevitLinkInstance pipeLinkInstance = null;
                Document pipeLinkDoc = null;
                List<Element> pipeElements = new List<Element>();

                foreach (Reference pref in pipeRefs)
                {
                    RevitLinkInstance inst = doc.GetElement(pref.ElementId) as RevitLinkInstance;
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
                    if (e != null) pipeElements.Add(e);
                }

                if (pipeElements.Count == 0)
                    throw new Exception("无法从链接中读取 MEP 元素。");

                Transform pipeTf = pipeLinkInstance.GetTotalTransform();

                // ==========================================================
                // 分支 A：选了墙 → 创建新洞
                // ==========================================================
                if (wallRefs.Count == 1)
                {
                    return CreateNewHole(doc, wallRefs[0], pipeElements, pipeTf);
                }

                // ==========================================================
                // 分支 B：没选墙 → 第 2 次选择 WallHole，然后更新
                // ==========================================================
                IList<Reference> holeRefs;
                using (var finisher = new SpaceKeyFinisher())
                {
                    finisher.Enable();
                    holeRefs = uidoc.Selection.PickObjects(
                        ObjectType.Element,
                        new WallHoleFilter(FAMILY_NAME),
                        $"步骤 2 / 2（未选墙）：\n" +
                        $"请选择要更新的墙洞（族名 = \"{FAMILY_NAME}\"，Generic Models 类别）\n" +
                        $"可多选，按空格键结束。\n" +
                        $"（若看不到墙洞，请切换到三维视图或临时打开 Generic Models 可见性）");
                }

                if (holeRefs == null || holeRefs.Count == 0)
                    throw new Exception(
                        $"未选择任何墙洞，无法更新。\n" +
                        $"如需创建新墙洞，请重新运行并选择 1 面墙 + MEP。");

                return UpdateExistingHoles(doc, holeRefs, pipeElements, pipeTf);
            }
            catch (OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        // ==========================================================
        // 分支 A：创建新洞（原有逻辑抽出来）
        // ==========================================================
        private Result CreateNewHole(
            Document doc, Reference wallRef,
            List<Element> pipeElements, Transform pipeTf)
        {
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
                allPoints.Add(pipeTf.OfPoint(bbox.Min));
                allPoints.Add(pipeTf.OfPoint(bbox.Max));
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

                    // 1) 匹配与这个洞相邻的 MEP
                    List<Element> relatedPipes = MatchPipesToHole(hole, pipeElements, pipeTf);
                    if (relatedPipes.Count == 0)
                    {
                        skippedNoMatch++;
                        continue;
                    }

                    // 2) 计算尺寸
                    XYZ holePos = lp.Point;
                    XYZ wallDir = hole.HandOrientation.Normalize();
                    XYZ wallOrigin = new XYZ(holePos.X, holePos.Y, 0);

                    double minU = double.MaxValue, maxU = double.MinValue;
                    double minZ = double.MaxValue, maxZ = double.MinValue;
                    bool hasPt = false;

                    foreach (var pipe in relatedPipes)
                    {
                        BoundingBoxXYZ bbox = pipe.get_BoundingBox(null);
                        if (bbox == null) continue;

                        for (int i = 0; i <= 1; i++)
                        for (int j = 0; j <= 1; j++)
                        for (int k = 0; k <= 1; k++)
                        {
                            XYZ lp3 = new XYZ(
                                i == 0 ? bbox.Min.X : bbox.Max.X,
                                j == 0 ? bbox.Min.Y : bbox.Max.Y,
                                k == 0 ? bbox.Min.Z : bbox.Max.Z);
                            XYZ wp = pipeTf.OfPoint(lp3);

                            double u = (wp - wallOrigin).DotProduct(wallDir);
                            minU = Math.Min(minU, u);
                            maxU = Math.Max(maxU, u);
                            minZ = Math.Min(minZ, wp.Z);
                            maxZ = Math.Max(maxZ, wp.Z);
                            hasPt = true;
                        }
                    }
                    if (!hasPt) { skippedNoMatch++; continue; }

                    double offsetFeet = OFFSET_MM / 304.8;
                    double width = (maxU - minU) + 2 * offsetFeet;
                    double height = (maxZ - minZ) + 2 * offsetFeet;
                    height += GetCableTrayExtraHeightFeet(relatedPipes, pipeTf);

                    // 3) depth：读现有值（无墙无法重算）
                    double depth = hole.LookupParameter("NDH_Depth")?.AsDouble() ?? 0.0;
                    if (depth <= 0.0) depth = 200.0 / 304.8;

                    // 4) 先改参数，再移动位置（顺序敏感）
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
                }

                tran.Commit();

                if (updated == 0)
                {
                    throw new Exception(
                        $"未更新任何墙洞。\n" +
                        $"可能原因：所选 MEP 与所选墙洞距离超过 {SEARCH_TOLERANCE_MM:0}mm，" +
                        $"或所选元素不是族名 \"{FAMILY_NAME}\" 的墙洞。\n" +
                        $"（跳过 {skippedNoMatch} 个未匹配的洞）");
                }
            }

            return Result.Succeeded;
        }

        // 用 MEP 包围盒中心是否落在洞 bbox（±容差）内来判断"相邻"
        private List<Element> MatchPipesToHole(
            FamilyInstance hole, List<Element> pipeElements, Transform pipeTf)
        {
            BoundingBoxXYZ holeBbox = hole.get_BoundingBox(null);
            if (holeBbox == null) return new List<Element>();

            double tol = SEARCH_TOLERANCE_MM / 304.8;
            Outline outline = new Outline(
                holeBbox.Min - new XYZ(tol, tol, tol),
                holeBbox.Max + new XYZ(tol, tol, tol));

            List<Element> result = new List<Element>();
            foreach (var pipe in pipeElements)
            {
                BoundingBoxXYZ pb = pipe.get_BoundingBox(null);
                if (pb == null) continue;

                XYZ pmin = pipeTf.OfPoint(pb.Min);
                XYZ pmax = pipeTf.OfPoint(pb.Max);
                XYZ pcenter = (pmin + pmax) / 2;

                if (outline.Contains(pcenter, 0.001))
                    result.Add(pipe);
            }
            return result;
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
            catch { }
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

                XYZ worldPt = transform.OfPoint(bbox.Max);
                if (worldPt.Z > maxZ)
                {
                    maxZ = worldPt.Z;
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

        // 第 1 次选择：链接元素过滤器
        private class AnyLinkElementFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) => e is RevitLinkInstance;
            public bool AllowReference(Reference r, XYZ p) => true;
        }

        // 第 2 次选择：当前项目的 WallHole 过滤器
        private class WallHoleFilter : ISelectionFilter
        {
            private readonly string _familyName;
            public WallHoleFilter(string familyName) { _familyName = familyName; }

            public bool AllowElement(Element e)
            {
                if (e is FamilyInstance fi
                    && fi.Symbol?.Family?.Name == _familyName
                    && fi.Category?.Id.IntegerValue == (int)BuiltInCategory.OST_GenericModel)
                    return true;
                return false;
            }

            public bool AllowReference(Reference r, XYZ p) => true;
        }
    }
}