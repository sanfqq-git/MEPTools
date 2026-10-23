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

// 你的服务类
using RevitPlugins.Services;

namespace LinkElementWallHole
{
    [Transaction(TransactionMode.Manual)]
    public class CreateHoleFromLinkElements : IExternalCommand
    {
        private double OFFSET_MM;
        private string FAMILY_NAME;
        private string SYMBOL_NAME;
        private string LEVEL_NAME;
        private double CABLE_TRAY_EXTRA_HEIGHT_MM;

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
                // 加载配置文件
                LoadConfig();

                // ========== 空格键结束选择（调用你的外部服务类） ==========
                IList<Reference> selectedRefs = null;
                using (var finisher = new SpaceKeyFinisher())
                {
                    finisher.Enable();
                    try
                    {
                        selectedRefs = uidoc.Selection.PickObjects(
                            ObjectType.LinkedElement,
                            new AnyLinkElementFilter(),
                            "请选择墙（一个链接）和管道/桥架（另一个链接）\n" +
                            "提示：墙1个，桥架/风管/管道可多选\n按空格键结束选择。");
                    }
                    finally
                    {
                        finisher.Disable();
                    }
                }

                if (selectedRefs == null || selectedRefs.Count < 2)
                    throw new Exception("至少选择一面墙 + 一个管道/桥架。");

                List<Reference> wallRefs = new List<Reference>();
                List<Reference> pipeRefs = new List<Reference>();

                foreach (Reference r in selectedRefs)
                {
                    RevitLinkInstance linkInst = doc.GetElement(r.ElementId) as RevitLinkInstance;
                    if (linkInst == null) continue;
                    Document linkDoc = linkInst.GetLinkDocument();
                    Element linkedElem = linkDoc.GetElement(r.LinkedElementId);

                    if (linkedElem is Wall)
                        wallRefs.Add(r);
                    else if (linkedElem is Duct || linkedElem is Pipe || linkedElem is CableTray)
                        pipeRefs.Add(r);
                }

                if (wallRefs.Count == 0) throw new Exception("未选中墙");
                if (wallRefs.Count > 1) throw new Exception("只能选一面墙");
                if (pipeRefs.Count == 0) throw new Exception("未选中管道/桥架");

                RevitLinkInstance wallLinkInstance = doc.GetElement(wallRefs[0].ElementId) as RevitLinkInstance;
                Document wallLinkDoc = wallLinkInstance.GetLinkDocument();
                Wall targetWall = wallLinkDoc.GetElement(wallRefs[0].LinkedElementId) as Wall;

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
                    Element e = pipeLinkDoc.GetElement(pref.LinkedElementId);
                    pipeElements.Add(e);
                }

                if (wallLinkInstance.Id == pipeLinkInstance.Id)
                    throw new Exception("墙和管线不能在同一个链接");

                // ========== 墙几何 ==========
                LocationCurve wallLoc = targetWall.Location as LocationCurve;
                Line wallLine = wallLoc.Curve as Line;
                if (wallLine == null) throw new Exception("仅支持直线墙");

                Transform wallTf = wallLinkInstance.GetTotalTransform();
                Line wallWorldLine = wallLine.CreateTransformed(wallTf) as Line;
                XYZ wallDir = wallWorldLine.Direction.Normalize();
                XYZ wallOrigin = wallWorldLine.GetEndPoint(0);
                double wallThickness = targetWall.Width;
                XYZ wallNormal = new XYZ(wallDir.Y, -wallDir.X, 0).Normalize();

                // ========== 管道/桥架几何 ==========
                Transform pipeTf = pipeLinkInstance.GetTotalTransform();
                List<XYZ> allPoints = new List<XYZ>();

                foreach (Element elem in pipeElements)
                {
                    BoundingBoxXYZ bbox = elem.get_BoundingBox(null);
                    if (bbox == null) continue;
                    allPoints.Add(pipeTf.OfPoint(bbox.Min));
                    allPoints.Add(pipeTf.OfPoint(bbox.Max));
                }

                // ========== 计算开孔尺寸 ==========
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

                // ========== 核心：桥架在最高处 → 高度+50mm ==========
                if (CheckIfCableTrayIsHighest(pipeElements, pipeTf, height))
                {
                    height += 50.0 / 304.8;
                }

                double depth = wallThickness;

                XYZ insertPt = wallOrigin + (minU + maxU) / 2 * wallDir;
                insertPt = new XYZ(insertPt.X, insertPt.Y, minZproj - offsetFeet);

                // ========== 查找族和标高 ==========
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

                // ========== 创建开孔 ==========
                using (Transaction tran = new Transaction(doc, "创建链接墙洞"))
                {
                    tran.Start();
                    if (!holeSymbol.IsActive) holeSymbol.Activate();

                    FamilyInstance hole = doc.Create.NewFamilyInstance(
                        insertPt,
                        holeSymbol,
                        level,
                        Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                    RotateToWall(doc, hole, wallDir, insertPt);

                    SetParam(hole, "NDH_Depth", depth);
                    SetParam(hole, "NDH_Width", width);
                    SetParam(hole, "NDH_Height", height);

                    tran.Commit();
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        // ========== 读取配置（支持#注释） ==========
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
                        CABLE_TRAY_EXTRA_HEIGHT_MM = double.Parse(validLines[4]); // 读取额外高度配置
                        return;
                    }
                }
            }
            catch { }
        }
        // ========== 判断：桥架是否在最高处 ==========
        private bool CheckIfCableTrayIsHighest(List<Element> elements, Transform transform, double height)
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

            if (highest != null && highest is CableTray)
            {
                height += CABLE_TRAY_EXTRA_HEIGHT_MM / 304.8; // 使用配置的额外高度
            }
            return highest != null && highest is CableTray;
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

        private class AnyLinkElementFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) => e is RevitLinkInstance;
            public bool AllowReference(Reference r, XYZ p) => true;
        }
    }
}