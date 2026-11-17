// ============================================================
// 文件：ChangeReferenceLevelCommand.cs
// 位置：ChangeReferenceLevel 文件夹
// 修改重点：所有写操作前增加"值比较"守卫，避免无意义触碰
// ============================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using MEPTools.Shared;
using View = Autodesk.Revit.DB.View;

namespace MEPTools.ChangeReferenceLevel
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ChangeReferenceLevelCommand : IExternalCommand
    {
        private enum ChangeResult { Success, Skipped, Failed, NoChange }

        private static double PositionTolFt
        {
            get
            {
                double mm = MEPConfig.GetDouble("ChangeRefLevel.PositionToleranceMm", 0.01);
                return mm / 304.8;
            }
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData?.Application?.ActiveUIDocument;
            if (uidoc == null || uidoc.Document == null)
            {
                message = "没有可用的活动文档。";
                return Result.Cancelled;
            }
            Document doc = uidoc.Document;

            MEPLogger.Instance.StartSession("修改参照标高");
            try
            {
                // ---------- 1. 选择元素 ----------
                string pickPrompt = MEPConfig.GetString(
                    "ChangeRefLevel.PickPrompt",
                    "请选择要修改参照标高的元素（按空格键结束）");

                var filter = new EditableElementFilter();
                var elems = MEPSelectionHelper.GetElements(
                    uidoc, filter, pickPrompt,
                    minCount: 1, useSpaceKey: true, singlePick: false);

                if (elems == null || elems.Count == 0)
                {
                    MEPLogger.Instance.EndSession("No elements selected.");
                    return Result.Cancelled;
                }
                MEPLogger.Instance.AppendLine($"已选择 {elems.Count} 个候选元素。");

                // ---------- 2. 选择目标标高 ----------
                Level targetLevel = null;
                using (var form = new LevelSelectorForm(doc))
                {
                    if (form.ShowDialog() != System.Windows.Forms.DialogResult.OK || form.SelectedLevel == null)
                    {
                        MEPLogger.Instance.EndSession("Level selection cancelled.");
                        return Result.Cancelled;
                    }
                    targetLevel = form.SelectedLevel;
                }
                MEPLogger.Instance.AppendLine($"目标标高：{targetLevel.Name}（Elevation = {targetLevel.Elevation:F4} ft）");

                // ---------- 3. 批量修改 ----------
                int success = 0, skipped = 0, failed = 0, noChange = 0;
                using (var tx = new Transaction(doc, "修改参照标高"))
                {
                    tx.Start();
                    foreach (var elem in elems)
                    {
                        try
                        {
                            var r = ChangeOneElement(doc, elem, targetLevel);
                            switch (r)
                            {
                                case ChangeResult.Success: success++; break;
                                case ChangeResult.Skipped: skipped++; break;
                                case ChangeResult.NoChange: noChange++; break;
                                default: failed++; break;
                            }
                        }
                        catch (Exception ex)
                        {
                            MEPLogger.Instance.LogException($"ChangeRefLevel[{elem?.Id}]", ex);
                            failed++;
                        }
                    }
                    tx.Commit();
                }

                MEPLogger.Instance.EndSession(
                    $"Success={success}, NoChange={noChange}, Skipped={skipped}, Failed={failed}, TargetLevel={targetLevel.Name}");

                string msg = string.Format(
                    MEPConfig.GetString("ChangeRefLevel.Msg.SummaryBody",
                        "修改完成：\n成功 {0} 个\n无需修改 {1} 个\n跳过 {2} 个\n失败 {3} 个\n\n目标标高：{4}"),
                    success, noChange, skipped, failed, targetLevel.Name);
                InfoCenterTip.Show(msg);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("ChangeReferenceLevelCommand.Execute", ex);
                MEPLogger.Instance.EndSession("Failed: " + ex.Message);
                message = ex.Message;
                return Result.Failed;
            }
        }

        // ---------------- 核心逻辑（写入前严格比较） ----------------

        private static ChangeResult ChangeOneElement(Document doc, Element elem, Level targetLevel)
        {
            if (elem == null) return ChangeResult.Skipped;
            if (elem is Level || elem is View || elem is ProjectInfo || elem is ViewSheet)
                return ChangeResult.Skipped;

            try { if (elem.Pinned) { MEPLogger.Instance.AppendLine($"[Skip] {elem.Id} 已锁定"); return ChangeResult.Skipped; } }
            catch { }

            Parameter levelParam = FindReferenceLevelParam(elem, doc);
            if (levelParam == null)
            {
                MEPLogger.Instance.AppendLine($"[Skip] {elem.Id} 无参照标高参数");
                return ChangeResult.Skipped;
            }

            // ========== 守卫 1：标高已是目标值 → 直接跳过，不触碰 ==========
            ElementId oldLevelId = levelParam.AsElementId();
            if (oldLevelId == targetLevel.Id)
            {
                MEPLogger.Instance.AppendLine($"[NoChange] {elem.Id} 标高已是目标值，跳过");
                return ChangeResult.NoChange;
            }

            XYZ oldPos = GetElementPosition(elem);
            if (oldPos == null)
            {
                MEPLogger.Instance.AppendLine($"[Skip] {elem.Id} 无有效位置");
                return ChangeResult.Skipped;
            }

            // ---- 步骤 1：修改参照标高 ----
            try
            {
                levelParam.Set(targetLevel.Id);
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException($"SetLevel[{elem.Id}]", ex);
                return ChangeResult.Failed;
            }

            SafeRegenerate(doc);

            // ---- 步骤 2：Offset 补偿 Z 位移（补偿前同样比较） ----
            try { ApplyOffsetCompensation(elem, oldPos); }
            catch (Exception ex) { MEPLogger.Instance.LogException($"Offset[{elem.Id}]", ex); }

            SafeRegenerate(doc);

            // ---- 步骤 3：残余位移用 MoveElement 兜底 ----
            XYZ curPos = GetElementPosition(elem);
            if (curPos == null)
            {
                MEPLogger.Instance.AppendLine($"[Warn] {elem.Id} 修改后无法读取位置");
                return ChangeResult.Failed;
            }

            XYZ delta = oldPos - curPos;
            // ========== 守卫 2：位移小于容差 → 不调用 MoveElement ==========
            if (delta.GetLength() > PositionTolFt)
            {
                try
                {
                    ElementTransformUtils.MoveElement(doc, elem.Id, delta);
                }
                catch (Exception ex)
                {
                    MEPLogger.Instance.LogException($"MoveElement[{elem.Id}]", ex);
                    return ChangeResult.Failed;
                }
                SafeRegenerate(doc);
            }
            else if (delta.GetLength() > 1e-9)
            {
                MEPLogger.Instance.AppendLine(
                    $"[NoMove] {elem.Id} 残余位移 {delta.GetLength() * 304.8:F4} mm 在容差内，跳过 MoveElement");
            }

            // ---- 最终校验 ----
            XYZ finalPos = GetElementPosition(elem);
            if (finalPos != null)
            {
                double dev = (finalPos - oldPos).GetLength();
                if (dev > PositionTolFt)
                {
                    MEPLogger.Instance.AppendLine(
                        $"[Warn] {elem.Id} 最终位置偏差 {dev * 304.8:F3} mm（超过容差 {PositionTolFt * 304.8:F3} mm）");
                }
            }

            return ChangeResult.Success;
        }

        /// <summary>
        /// Offset 补偿前先比较：目标值已相同则不 Set。
        /// </summary>
        private static void ApplyOffsetCompensation(Element elem, XYZ oldPos)
        {
            XYZ curPos = GetElementPosition(elem);
            if (curPos == null) return;

            double deltaZ = oldPos.Z - curPos.Z;
            if (Math.Abs(deltaZ) < 1e-9) return; // 无需补偿

            var bips = new[]
            {
                BuiltInParameter.RBS_OFFSET_PARAM,
                BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM
            };

            foreach (var bip in bips)
            {
                try
                {
                    var p = elem.get_Parameter(bip);
                    if (p == null || p.IsReadOnly || p.StorageType != StorageType.Double)
                        continue;

                    double cur = p.AsDouble();
                    double newVal = cur + deltaZ;

                    // ========== 守卫 3：Offset 值未变 → 不 Set ==========
                    if (Math.Abs(newVal - cur) < 1e-12)
                    {
                        MEPLogger.Instance.AppendLine($"[NoChange] {elem.Id} Offset 无需调整");
                        return;
                    }

                    p.Set(newVal);
                    return;
                }
                catch (Exception ex)
                {
                    MEPLogger.Instance.LogException($"OffsetParam[{elem.Id}][{bip}]", ex);
                }
            }
        }

        private static void SafeRegenerate(Document doc)
        {
            try { doc.Regenerate(); } catch { }
        }

        private static XYZ GetElementPosition(Element elem)
        {
            try
            {
                if (elem.Location is LocationPoint lp && lp.Point != null) return lp.Point;
                if (elem.Location is LocationCurve lc && lc.Curve != null) return lc.Curve.Evaluate(0.5, true);
            }
            catch { }
            return null;
        }

        private static Parameter FindReferenceLevelParam(Element elem, Document doc)
        {
            var candidates = new[]
            {
                BuiltInParameter.RBS_START_LEVEL_PARAM,
                BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM,
                BuiltInParameter.FAMILY_LEVEL_PARAM,
                BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM
            };

            foreach (var bip in candidates)
            {
                try { var p = elem.get_Parameter(bip); if (IsValidLevelParam(p, doc)) return p; }
                catch { }
            }

            try
            {
                foreach (Parameter p in elem.Parameters)
                {
                    if (p == null || p.IsReadOnly || p.StorageType != StorageType.ElementId) continue;
                    string name = p.Definition?.Name ?? "";
                    if (name.Contains("标高") || name.Equals("Reference Level", StringComparison.OrdinalIgnoreCase))
                        if (IsValidLevelParam(p, doc)) return p;
                }
            }
            catch { }

            return null;
        }

        private static bool IsValidLevelParam(Parameter p, Document doc)
        {
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.ElementId) return false;
            ElementId id = p.AsElementId();
            if (id == null || id == ElementId.InvalidElementId) return false;
            return doc.GetElement(id) is Level;
        }

        private class EditableElementFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                if (elem == null) return false;
                if (elem is Level || elem is View || elem is ProjectInfo || elem is ViewSheet) return false;
                try { if (elem.Pinned) return false; } catch { }
                return true;
            }
            public bool AllowReference(Reference reference, XYZ position) => true;
        }
    }
}