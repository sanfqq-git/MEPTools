// ============================================================
// 文件  ：Cmd_HoleCheck.cs
// 位置  ：LinkElementWallHole 目录
// 功能  ：墙洞体检（只读检查，不修改任何孔）
//         ① 原则1 出孔外：穿墙 MEP 是否仍在孔洞范围内（水平/竖直越界量）
//         ② 原则2 尺寸  ：孔径与穿洞 MEP 组是否匹配（偏小=严重，偏大=警告）
//         ③ 附加        ：族参数与实体几何是否一致、空孔、一孔多组、疑似孔位偏移
// 复用  ：LinkElementWallHole 的 config.txt（族名/类型/宽高深参数名/类别/偏移/桥架附加高）
//         HoleBiz.GetMepCrossSectionBoundary（与开孔命令同一套公式，保证判定与生成同源）
//         Shared：InfoCenterTip / MEPLogger / MEPConfig / Preselected
// 约束  ：不改动 LinkElementWallHole.cs 任何逻辑；结果窗无模式，关闭时自动清除视图着色
// ============================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using MEPTools.Shared;

using RevitColor = Autodesk.Revit.DB.Color;
using WinColor = System.Drawing.Color;
using DbView = Autodesk.Revit.DB.View;
using WinForm = System.Windows.Forms.Form;
using WinPanel = System.Windows.Forms.Panel;

namespace MEPTools.LinkElementWallHole
{
    // ================================================================
    // 【A】命令入口
    // ================================================================
    [Transaction(TransactionMode.Manual)]
    public class Cmd_HoleCheck : IExternalCommand
    {
        private static HoleCheckResultForm _form;
        private static ExternalEvent _event;
        private static HoleCheckHandler _handler;

        private const double MM = HoleCalc.MM_PER_FOOT;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData == null ? null : commandData.Application;
            UIDocument uidoc = uiapp == null ? null : uiapp.ActiveUIDocument;
            if (uidoc == null || uidoc.Document == null)
            {
                InfoCenterTip.Show("未找到活动文档，请先打开模型。");
                return Result.Cancelled;
            }

            Document doc = uidoc.Document;
            MEPLogger.Instance.StartSession("墙洞体检");

            Cursor prevCursor = Cursor.Current;
            try
            {
                // 复用开孔配置：族名 / 族类型 / 宽高深参数名 / 目标类别 / 四周偏移 / 桥架附加高
                Command cfg = new Command();
                cfg.LoadConfig();

                // 1. 取孔：预选或框选优先，无预选进入点选（空格结束）
                List<FamilyInstance> holes = PickHoles(uidoc, cfg);
                if (holes.Count == 0)
                {
                    MEPLogger.Instance.EndSession("未选择到可检测的墙洞");
                    InfoCenterTip.Show("未选择到可检测的墙洞，已退出。");
                    return Result.Cancelled;
                }

                MEPLogger.Instance.AppendLine("文档：" + doc.Title);
                MEPLogger.Instance.AppendLine("待检测墙洞：" + holes.Count + " 个");
                foreach (FamilyInstance h in holes)
                {
                    string label = (h.Symbol != null && h.Symbol.Family != null)
                        ? h.Symbol.Family.Name + " : " + h.Symbol.Name : h.Name;
                    MEPLogger.Instance.AppendLine("  孔 [" + h.Id.IntegerValue + "] " + label);
                }

                // 2. 关闭上一次残留的结果窗（不清色，避免异步清色盖掉本次着色）
                if (_form != null && !_form.IsDisposed)
                {
                    try { _form.SuppressClearOnClose = true; _form.Close(); } catch { }
                }
                if (_handler != null) { _handler.Form = null; _handler.PendingOp = HoleCheckHandler.Op.None; }

                // 3. 清上一次残留着色（命令上下文内，事务合法）
                HoleCheckRunner.ClearPreviousHighlight(doc);

                // 4. 首轮计算 + 着色
                Cursor.Current = Cursors.WaitCursor;
                HoleCheckRunner runner = new HoleCheckRunner(doc, uidoc, holes, cfg);
                List<HoleCheckIssue> issues = runner.RunAndHighlight();
                Cursor.Current = prevCursor;

                // 5. 无模式结果窗：TopMost 在 Show 之前设一次，之后不再改动属性。
                //    "闪一下"的根源就是先 Show、再由定时器把 TopMost 切 true 再切 false（窗口被抬起又落下）；
                //    这里只设一次，窗口出现即在前、不会被 Revit 主窗压到后面，也不会再跳。
                _handler = new HoleCheckHandler();
                _event = ExternalEvent.Create(_handler);
                _form = new HoleCheckResultForm(uidoc, runner, issues, _handler, _event);
                _handler.Runner = runner;
                _handler.Form = _form;

                _form.TopMost = true;
                _form.Show();
                try { _form.Activate(); } catch { }

                InfoCenterTip.Show(HoleCheckRunner.BuildSummary(issues));
                MEPLogger.Instance.EndSession("结果窗已打开：" + issues.Count + " 条");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Cursor.Current = prevCursor;
                MEPLogger.Instance.LogException("Cmd_HoleCheck.Execute", ex);
                InfoCenterTip.Show("墙洞体检失败：" + ex.Message);
                message = ex.Message;
                return Result.Failed;
            }
        }

        // ---------- 取孔 ----------
        private static List<FamilyInstance> PickHoles(UIDocument uidoc, Command cfg)
        {
            List<FamilyInstance> holes = new List<FamilyInstance>();
            int skipped = 0;

            List<Element> picked = Preselected.GetSelectedElements(
                uidoc,
                new HoleSelectionFilter(cfg),
                "请框选或点选要检测的墙洞，按空格键结束",
                "请先框选或点选要检测的墙洞（预选后直接运行也可以）");

            if (picked == null) return holes;

            foreach (Element e in picked)
            {
                FamilyInstance fi = e as FamilyInstance;
                if (fi == null || !HasSizeParams(fi, cfg)) { skipped++; continue; }
                holes.Add(fi);
            }

            if (skipped > 0 && holes.Count == 0)
            {
                // 全部被判为非孔：多半是参数名与族不匹配，直接告诉用户去核对，避免"选了却没反应"
                TaskDialog dlg = new TaskDialog("墙洞体检");
                dlg.MainInstruction = "选中的实例都不具备墙洞的宽/高/深参数";
                dlg.MainContent =
                    "当前配置的宽/高/深参数名：\n" +
                    cfg.PARAM_WIDTH_NAME + " / " + cfg.PARAM_HEIGHT_NAME + " / " + cfg.PARAM_DEPTH_NAME + "\n\n" +
                    "被忽略：" + skipped + " 个实例。\n" +
                    "如果这确实是墙洞，请先打开【墙洞设置】核对族与参数名。";
                dlg.CommonButtons = TaskDialogCommonButtons.Ok;
                dlg.Show();
            }
            else if (skipped > 0)
            {
                InfoCenterTip.Show("已忽略 " + skipped + " 个缺少墙洞参数的实例");
            }

            return holes;
        }

        private static bool HasSizeParams(FamilyInstance fi, Command cfg)
        {
            if (fi == null) return false;
            if (string.IsNullOrEmpty(cfg.PARAM_WIDTH_NAME)) return false;
            return fi.LookupParameter(cfg.PARAM_WIDTH_NAME) != null
                && fi.LookupParameter(cfg.PARAM_HEIGHT_NAME) != null
                && fi.LookupParameter(cfg.PARAM_DEPTH_NAME) != null;
        }

        private class HoleSelectionFilter : ISelectionFilter
        {
            private readonly Command _cfg;
            public HoleSelectionFilter(Command cfg) { _cfg = cfg; }

            public bool AllowElement(Element elem)
            {
                FamilyInstance fi = elem as FamilyInstance;
                if (fi == null || fi.Symbol == null || fi.Symbol.Family == null) return false;
                if (fi.Category == null) return false;
                bool catOk = fi.Category.Id.IntegerValue == _cfg.TARGET_CATEGORY_ID;
                bool famOk = !string.IsNullOrEmpty(_cfg.FAMILY_NAME)
                          && fi.Symbol.Family.Name == _cfg.FAMILY_NAME;
                return catOk || famOk;
            }

            public bool AllowReference(Reference reference, XYZ position) { return true; }
        }
    }

    // ================================================================
    // 【B】检测执行体（只读计算 + 视图临时着色）
    // ================================================================
    public class HoleCheckRunner
    {
        private const double MM = HoleCalc.MM_PER_FOOT;
        private const double EPS = 1e-6;

        private readonly Document _doc;
        private readonly UIDocument _uidoc;
        private readonly List<FamilyInstance> _holes;
        private readonly Command _cfg;

        private readonly List<MepItem> _meps = new List<MepItem>();
        private double _indexPadMm = -1.0;          // 已索引的搜索半径（mm），用于判断是否需要重建
        private double _holeBBoxPadMm = 2000.0;     // 孔总包围盒外扩，用于筛选 MEP

        // 跨命令残留着色（静态，便于下次运行前清理 / 窗口关闭时清理）
        private static string _lastDocTitle;
        private static string _lastViewUniqueId;
        private static Dictionary<int, OverrideGraphicSettings> _lastOrig;

        public HoleCheckRunner(Document doc, UIDocument uidoc, List<FamilyInstance> holes, Command cfg)
        {
            _doc = doc;
            _uidoc = uidoc;
            _holes = holes ?? new List<FamilyInstance>();
            _cfg = cfg;
        }

        public Document Document { get { return _doc; } }

        // ---------- 对外：算 + 着色 ----------
        public List<HoleCheckIssue> RunAndHighlight()
        {
            EnsureIndex();

            Dictionary<ElementId, int> severityByHole;
            List<HoleCheckIssue> issues;
            Compute(out issues, out severityByHole);

            List<HoleCheckIssue> sorted = issues
                .OrderBy(i => i.SortKey)
                .ThenBy(i => i.HoleIdValue)
                .ToList();

            ClearPreviousHighlight(_doc);
            ApplyHighlight(severityByHole, _uidoc.ActiveView);
            return sorted;
        }

        public static string BuildSummary(List<HoleCheckIssue> issues)
        {
            if (issues == null) return "墙洞体检：0 条";
            int s = issues.Count(i => i.SortKey == 0);
            int w = issues.Count(i => i.SortKey == 1);
            int t = issues.Count(i => i.SortKey == 2);
            int h = issues.Select(i => i.HoleIdValue).Distinct().Count();
            return string.Format("墙洞体检：{0} 个孔 / 严重 {1} · 警告 {2} · 提示 {3}", h, s, w, t);
        }

        // ============================================================
        // 主计算
        // ============================================================
        private void Compute(out List<HoleCheckIssue> issues, out Dictionary<ElementId, int> severityByHole)
        {
            issues = new List<HoleCheckIssue>();
            severityByHole = new Dictionary<ElementId, int>();

            double nearR = Math.Max(0.0, MEPConfig.GetDouble("HoleCheck.NearRadiusMm", 100.0)) / MM;
            double searchR = Math.Max(0.0, MEPConfig.GetDouble("HoleCheck.SearchRadiusMm", 300.0)) / MM;
            double sizeTol = Math.Max(0.0, MEPConfig.GetDouble("HoleCheck.SizeTolMm", 20.0)) / MM;
            double exceedTol = Math.Max(0.0, MEPConfig.GetDouble("HoleCheck.ExceedTolMm", 5.0)) / MM;
            double groupU = Math.Max(0.0, MEPConfig.GetDouble("HoleCheck.GroupDuMm", 60.0));
            double groupV = Math.Max(0.0, MEPConfig.GetDouble("HoleCheck.GroupDvMm", 100.0));

            double offFt = _cfg.OFFSET_MM / MM;
            double trayExtraFt = _cfg.CABLE_TRAY_EXTRA_HEIGHT_MM / MM;

            string pW = _cfg.PARAM_WIDTH_NAME;
            string pH = _cfg.PARAM_HEIGHT_NAME;
            string pD = _cfg.PARAM_DEPTH_NAME;

            foreach (FamilyInstance hole in _holes)
            {
                if (hole == null || !hole.IsValidObject) continue;
                ElementId hid = hole.Id;
                string label = (hole.Symbol != null && hole.Symbol.Family != null)
                    ? hole.Symbol.Family.Name + " : " + hole.Symbol.Name : hole.Name;
                string locText = "—";
                XYZ org = null;
                try { LocationPoint lp = hole.Location as LocationPoint; if (lp != null) org = lp.Point; } catch { }

                // ---- 1. 孔实体：只取一次几何，方向与尺寸都用它（几何优先，族参数只做互校） ----
                List<Solid> solids = new List<Solid>();
                CollectSolids(SafeGeometry(hole), solids);
                Solid body = PickBiggestSolid(solids);

                XYZ uDir, nDir;
                XYZ origin = org;
                if (origin == null || !TryBuildFrame(hole, body, origin, out uDir, out nDir))
                {
                    AddIssue(issues, severityByHole, hid, label, "—", 2, "孔方向无法解析",
                        "—", "—", "实体面法向与 HandOrientation / FacingOrientation 都取不到水平方向，跳过该孔的越界与尺寸判定。");
                    MEPLogger.Instance.AppendLine("[孔 " + hid.IntegerValue + "] 方向解析失败，跳过。");
                    continue;
                }
                locText = HoleCalc.Fmt(origin);
                MEPLogger.Instance.AppendLine(string.Format(
                    "[孔 {0}] 原点(世界) {1}｜nDir {2}｜uDir {3}",
                    hid.IntegerValue, HoleCalc.Fmt(origin),
                    HoleCalc.Fmt(nDir), HoleCalc.Fmt(uDir)));

                // ---- 2. 孔实际矩形（实测） ----
                double u0 = 0.0, u1 = 0.0, z0 = 0.0, z1 = 0.0, depth = 0.0;
                bool measured = false;
                if (body != null)
                    measured = TryMeasureHoleBox(body, origin, uDir, nDir, out u0, out u1, out z0, out z1, out depth);

                double paramW, paramH, paramD;
                bool hasW = TryParamFt(hole, pW, out paramW);
                bool hasH = TryParamFt(hole, pH, out paramH);
                bool hasD = TryParamFt(hole, pD, out paramD);
                bool hasParams = hasW && hasH && hasD;

                if (!measured && !hasParams)
                {
                    AddIssue(issues, severityByHole, hid, label, locText, 2, "几何与参数都读不到",
                        "—", "—", "族实体取不到 Solid，且缺少 " + pW + "/" + pH + "/" + pD + " 参数，跳过。");
                    continue;
                }

                if (!measured)
                {
                    // 退化：按参数造一个以原点为中心的虚拟矩形，仅用于尺寸比较，越界检查不执行
                    double halfW = (hasW ? paramW : 200.0 / MM) / 2;
                    double hh = hasH ? paramH : 200.0 / MM;
                    u0 = -halfW; u1 = halfW;
                    z0 = origin.Z - hh / 2.0; z1 = origin.Z + hh / 2.0;
                    depth = hasD ? paramD : 200.0 / MM;
                }

                // 先留住实测值（互校基准），再决定判定矩形用哪套尺寸。
                double measW = u1 - u0, measH = z1 - z0, measD = depth;
                double measCU = (u0 + u1) / 2.0, measCZ = (z0 + z1) / 2.0;

                // 判定矩形：族参数优先（宽/高/深取 NDH_Width/Height/Depth），中心仍用实测矩形中心
                // （族实例原点不一定落在孔洞中心，位置只能信几何）。
                // 参数缺失时自动退回实测尺寸；两者不一致由下一步「参数与几何不符」报出来。
                bool rectFromParams = measured && hasParams;
                if (rectFromParams)
                {
                    u0 = measCU - paramW / 2.0;
                    u1 = measCU + paramW / 2.0;
                    z0 = measCZ - paramH / 2.0;
                    z1 = measCZ + paramH / 2.0;
                    depth = paramD;
                }

                double wActFt = u1 - u0;
                double hActFt = z1 - z0;

                // 诊断：判定矩形 vs 实测 vs 族参数，三者一次摊开
                MEPLogger.Instance.AppendLine(string.Format(
                    "[孔 {0}] 判定矩形[{1}] u=[{2},{3}]mm z=[{4},{5}]mm 深 {6}mm｜实测 {7}×{8}×{9}｜参数 {10}={11} {12}={13} {14}={15}（W{16} H{17} D{18}）",
                    hid.IntegerValue, rectFromParams ? "参数尺寸+实测中心" : "实测",
                    MmText(u0), MmText(u1), MmText(z0), MmText(z1), MmText(depth),
                    MmText(measW), MmText(measH), MmText(measD),
                    pW, hasW ? MmText(paramW) : "读取失败",
                    pH, hasH ? MmText(paramH) : "读取失败",
                    pD, hasD ? MmText(paramD) : "读取失败",
                    hasW ? "✓" : "✗", hasH ? "✓" : "✗", hasD ? "✓" : "✗"));

                // ---- 3. 参数与几何互校 ----
                if (measured && hasParams)
                {
                    double dWmm = Math.Abs(measW - paramW) * MM;
                    double dHmm = Math.Abs(measH - paramH) * MM;
                    double dDmm = Math.Abs(measD - paramD) * MM;
                    double worst = Math.Max(dWmm, Math.Max(dHmm, dDmm));
                    if (worst > 5.0)
                    {
                        int sevKey = worst > 100.0 ? 1 : 2;
                        AddIssue(issues, severityByHole, hid, label, locText, sevKey, "参数与几何不符",
                            "实体 " + MmText(measW) + "×" + MmText(measH) + "×" + MmText(measD),
                            "参数 " + MmText(paramW) + "×" + MmText(paramH) + "×" + MmText(paramD),
                            "宽/高/深差异最大 " + worst.ToString("F1") + "mm。" +
                            (sevKey == 1 ? "差值过大，基本可判定为参数没驱动几何（族被拉伸或参数写错）。"
                                         : "差值较小，可能是族几何含边框/倒角，核对族即可。"));
                    }
                }

                // ---- 4. 找穿墙 MEP，并按二维距离分档 ----
                List<MepProj> inside = new List<MepProj>();
                List<MepProj> nearBy = new List<MepProj>();
                int crossCount = 0;
                int candCount = 0, axisFail = 0, parallelSkip = 0, tSkip = 0, secFail = 0;
                double minApproach = double.MaxValue;
                string firstFail = "";
                string candIds = "";

                // 查询盒用孔实测矩形（u/z + 深度）构造，不再依赖族实例 get_BoundingBox。
                // 链接里的 MEP 只要世界坐标落在盒内就会被取到，与它来自哪个文档无关。
                Outline qBox = BuildQueryOutline(origin, uDir, nDir, u0, u1, z0, z1, depth, searchR);

                // 兜底：实测盒一个候选都取不到时，用族参数矩形（宽/高/深）再查一次。
                // 只在 0 候选时生效，正常情况完全不影响实测口径。
                Outline qBoxParam = null;
                if (hasParams)
                {
                    double halfW = paramW / 2.0;
                    double halfH = paramH / 2.0;
                    double pDepth = hasD ? paramD : depth;
                    qBoxParam = BuildQueryOutline(origin, uDir, nDir,
                        -halfW, halfW, origin.Z - halfH, origin.Z + halfH, pDepth, searchR);
                }

                List<MepItem> cands = QueryMeps(qBox);
                if (cands.Count == 0 && qBoxParam != null)
                {
                    cands = QueryMeps(qBoxParam);
                    if (cands.Count > 0)
                        MEPLogger.Instance.AppendLine("[孔 " + hid.IntegerValue + "] 实测查询盒 0 候选，已回退族参数盒（" +
                            MmText(paramW) + "×" + MmText(paramH) + "×" + MmText(paramD) + "）取到 " + cands.Count + " 个");
                }

                foreach (MepItem m in cands)
                {
                    candCount++;
                    if (candIds.Length < 600)
                        candIds += (candIds.Length == 0 ? "" : "，") + "#" + m.Elem.Id.IntegerValue;
                    XYZ p0, p1;
                    if (!TryGetAxis(m, out p0, out p1))
                    {
                        axisFail++;
                        if (firstFail.Length == 0)
                            firstFail = "轴失败样本：" + ElemTag(m.Elem);
                        continue;
                    }
                    XYZ axis = (p1 - p0).Normalize();
                    double denom = axis.DotProduct(nDir);
                    if (Math.Abs(denom) < 1e-7)
                    {
                        parallelSkip++;
                        if (firstFail.Length == 0)
                            firstFail = "平行样本：" + ElemTag(m.Elem);
                        continue;                                   // 轴线平行墙面 → 不穿这个孔
                    }

                    // 沿轴线从 p0 走到孔中面的距离（ft）。
                    // 注意：axis 已单位化、denom = axis·nDir ∈ (-1,1]，所以这个商是「距离」，
                    // 不是归一化参数。之前直接拿它和 1.0 比较，等于要求「管长 ≤ 1 英尺」才判定穿墙，
                    // 结果是链接里几米长的直管全部被误判成 "t 越界"——这是"穿墙 0 根"的真正原因。
                    double distAlong = (origin - p0).DotProduct(nDir) / denom;
                    double segLen = p0.DistanceTo(p1);
                    double tNorm = (segLen <= 1e-6) ? 0.0 : distAlong / segLen;

                    // 轴线（无限延长）与孔平面的交点，以及该点到孔矩形的二维距离。
                    // 这个值不受管段范围影响：即使管段没跨墙，也能看出「管延长线离孔有多远」，
                    // 用来区分「管真的在孔里但没接长」和「孔附近压根没管」。
                    XYZ planePt = p0 + axis * distAlong;
                    double pu = (planePt - origin).DotProduct(uDir);
                    double pz2 = planePt.Z;
                    double duApp = Math.Max(0.0, Math.Max(u0 - pu, pu - u1));
                    double dzApp = Math.Max(0.0, Math.Max(z0 - pz2, pz2 - z1));
                    double app = Math.Sqrt(duApp * duApp + dzApp * dzApp);
                    if (app < minApproach) minApproach = app;

                    if (candCount <= 5)
                        MEPLogger.Instance.AppendLine(string.Format(
                            "    候选{0} {1}｜轴 {2} → {3}｜管长 {4}mm｜轴到孔面 {5}mm(t={6})｜延长线距孔 {7}mm｜世界盒 {8}",
                            candCount, ElemTag(m.Elem), HoleCalc.Fmt(p0), HoleCalc.Fmt(p1),
                            (segLen * MM).ToString("F0"), (distAlong * MM).ToString("F0"),
                            tNorm.ToString("F3"), (app * MM).ToString("F0"), HoleCalc.FmtOutline(m.Box)));

                    if (distAlong < -EPS || distAlong > segLen + EPS)
                    {
                        tSkip++;
                        if (firstFail.Length == 0)
                            firstFail = "轴未跨孔面样本：" + ElemTag(m.Elem) +
                                        " 轴到孔面 " + (distAlong * MM).ToString("F0") + "mm / 管长 " + (segLen * MM).ToString("F0") + "mm";
                        continue;                                   // 交点不在管段范围内 → 只是延长线穿过
                    }

                    XYZ crossPt = planePt;
                    crossCount++;

                    double mu0, mu1, mz0, mz1;
                    if (!HoleBiz.GetMepCrossSectionBoundary(m.Elem, m.Tf,
                            new XYZ(origin.X, origin.Y, 0.0), uDir, nDir, depth, 0.0,
                            out mu0, out mu1, out mz0, out mz1))
                    {
                        secFail++;
                        if (firstFail.Length == 0)
                            firstFail = "截面失败样本：" + ElemTag(m.Elem);
                        continue;
                    }

                    // 一维区间间距：相交/接触为 0，完全分离才返回分离量。
                    // 用「间距」而不是「包含」，是为了让只差几毫米出孔边的 MEP 仍留在 inside，
                    // 否则越界判定永远看不到它（这是本功能最关键的一处判据）。
                    double sepU = Math.Max(0.0, Math.Max(u0 - mu1, mu0 - u1));
                    double sepZ = Math.Max(0.0, Math.Max(z0 - mz1, mz0 - z1));
                    double gap = Math.Sqrt(sepU * sepU + sepZ * sepZ);

                    MepProj mp = new MepProj();
                    mp.Item = m;
                    mp.MinU = mu0; mp.MaxU = mu1; mp.MinZ = mz0; mp.MaxZ = mz1;
                    mp.CenterU = (crossPt - origin).DotProduct(uDir);
                    mp.CenterZ = crossPt.Z;
                    mp.Gap = gap;

                    if (gap <= nearR)
                    {
                        // 只有「轴线交点（截面中心）落在孔矩形内」的才是这个洞的穿管。
                        // 中心落在孔外、只是截面边缘搭在洞边的，是从洞边掠过的邻管——
                        // 洞并不是为它开的，把它算进来会凭空拉大"应有尺寸"和"最大超出"。
                        // 这类归入 nearBy：只写日志，不参与「出孔外 / 孔径偏小」判定。
                        double cOutU = Math.Max(0.0, Math.Max(u0 - mp.CenterU, mp.CenterU - u1));
                        double cOutZ = Math.Max(0.0, Math.Max(z0 - mp.CenterZ, mp.CenterZ - z1));
                        if (cOutU <= EPS && cOutZ <= EPS)
                        {
                            inside.Add(mp);
                            MEPLogger.Instance.AppendLine(string.Format(
                                "    入孔 {0}｜截面投影 u=[{1},{2}]mm z=[{3},{4}]mm｜间距 {5}mm",
                                ElemTag(m.Elem), MmText(mu0), MmText(mu1), MmText(mz0), MmText(mz1),
                                (gap * MM).ToString("F1")));
                        }
                        else
                        {
                            nearBy.Add(mp);
                            MEPLogger.Instance.AppendLine(string.Format(
                                "    擦边 {0}｜截面投影 u=[{1},{2}]mm z=[{3},{4}]mm｜轴心在孔外 水平 {5}mm 竖直 {6}mm｜不计入本洞",
                                ElemTag(m.Elem), MmText(mu0), MmText(mu1), MmText(mz0), MmText(mz1),
                                (cOutU * MM).ToString("F1"), (cOutZ * MM).ToString("F1")));
                        }
                    }
                    else if (searchR > nearR && gap <= searchR) nearBy.Add(mp);
                }

                MEPLogger.Instance.AppendLine(string.Format(
                    "[孔 {0}] 实测 {1}×{2}×{3}｜候选 {4}｜穿墙 {5}｜入孔 {6}｜附近 {7}｜轴延长线距孔 {8}｜轴失败 {9} 平行 {10} 轴未跨孔面 {11} 截面失败 {12}{13}{14}",
                    hid.IntegerValue, MmText(wActFt), MmText(hActFt), MmText(depth),
                    candCount, crossCount, inside.Count, nearBy.Count,
                    minApproach == double.MaxValue ? "—" : (minApproach * MM).ToString("F0") + "mm",
                    axisFail, parallelSkip, tSkip, secFail,
                    candCount == 0 ? "｜查询盒内 0 个 MEP：" + HoleCalc.FmtOutline(qBox) : "",
                    firstFail.Length == 0 ? "" : "｜" + firstFail));
                if (candCount > 0)
                    MEPLogger.Instance.AppendLine("    候选ID：" + candIds + (candIds.Length >= 600 ? " …" : ""));

                // ---- 5. 原则1：出孔外 ----
                if (measured && inside.Count > 0)
                {
                    int outCount = 0;
                    double maxOver = 0.0;
                    string worstDir = "";

                    foreach (MepProj mp in inside)
                    {
                        double exL = u0 - mp.MinU;
                        double exR = mp.MaxU - u1;
                        double exDn = z0 - mp.MinZ;
                        double exUp = mp.MaxZ - z1;

                        double ex = 0.0; string dir = "";
                        if (exL > ex) { ex = exL; dir = "左"; }
                        if (exR > ex) { ex = exR; dir = "右"; }
                        if (exDn > ex) { ex = exDn; dir = "下"; }
                        if (exUp > ex) { ex = exUp; dir = "上"; }

                        if (ex > exceedTol)
                        {
                            outCount++;
                            if (ex > maxOver) { maxOver = ex; worstDir = dir; }
                        }
                    }

                    if (outCount > 0)
                    {
                        AddIssue(issues, severityByHole, hid, label, locText, 0, "MEP 出孔外",
                            MmText(wActFt) + "×" + MmText(hActFt),
                            "—",
                            string.Format("{0} 根 MEP 已被孔边切除，最大超出 {1}mm（{2}侧）。孔或 MEP 位置有偏差，需重新开洞/移位。",
                                outCount, (maxOver * MM).ToString("F1"), worstDir));
                    }
                }

                // ---- 6. 原则2：尺寸（与开孔命令同一公式） ----
                if (inside.Count > 0)
                {
                    double cMinU = double.MaxValue, cMaxU = -double.MaxValue;
                    double cMinZ = double.MaxValue, cMaxZ = -double.MaxValue;
                    Element highest = null;

                    foreach (MepProj mp in inside)
                    {
                        double a0, a1, b0, b1;
                        if (!HoleBiz.GetMepCrossSectionBoundary(mp.Item.Elem, mp.Item.Tf,
                                new XYZ(origin.X, origin.Y, 0.0), uDir, nDir, depth, _cfg.OFFSET_MM,
                                out a0, out a1, out b0, out b1))
                            continue;

                        if (a0 < cMinU) cMinU = a0;
                        if (a1 > cMaxU) cMaxU = a1;
                        if (b0 < cMinZ) cMinZ = b0;
                        if (b1 > cMaxZ) { cMaxZ = b1; highest = mp.Item.Elem; }
                    }

                    if (cMinU <= cMaxU && cMinZ <= cMaxZ)
                    {
                        double wNeed = (cMaxU - cMinU) + 2.0 * offFt;
                        double hNeed = (cMaxZ - cMinZ) + 2.0 * offFt;
                        if (highest is CableTray) hNeed += trayExtraFt;

                        double dW = wActFt - wNeed;
                        double dH = hActFt - hNeed;

                        string diffText = string.Format("宽{0}{1}mm，高{2}{3}mm（容差 ±{4}mm）",
                            dW < 0 ? "缺 " : "多 ", Math.Abs(dW * MM).ToString("F1"),
                            dH < 0 ? "缺 " : "多 ", Math.Abs(dH * MM).ToString("F1"),
                            (sizeTol * MM).ToString("F0"));

                        if (dW < -sizeTol || dH < -sizeTol)
                        {
                            AddIssue(issues, severityByHole, hid, label, locText, 0, "孔径偏小",
                                MmText(wActFt) + "×" + MmText(hActFt),
                                MmText(wNeed) + "×" + MmText(hNeed),
                                "孔比应有尺寸小：" + diffText + "。MEP 可能被切，建议重开。");
                        }
                        else if (dW > sizeTol || dH > sizeTol)
                        {
                            AddIssue(issues, severityByHole, hid, label, locText, 1, "孔径偏大",
                                MmText(wActFt) + "×" + MmText(hActFt),
                                MmText(wNeed) + "×" + MmText(hNeed),
                                "孔比应有尺寸大：" + diffText + "。多开的部分会削弱墙体，按项目要求决定是否修正。");
                        }
                    }
                }

                // ---- 7. 成组：只统计进日志，不再报"一孔多组" ----
                //      一个洞穿多根并排管（风管排/桥架排/管束）是常规做法，报成问题只会产生噪音，
                //      保留组数记录用于排查，不再进入结果列表。
                if (inside.Count > 1)
                {
                    int groups = CountGroups(inside, groupU, groupV);
                    MEPLogger.Instance.AppendLine(string.Format(
                        "[孔 {0}] 组统计：{1} 根 / {2} 组（水平 {3}mm / 竖直 {4}mm）",
                        hid.IntegerValue, inside.Count, groups, groupU.ToString("F0"), groupV.ToString("F0")));
                }

                // ---- 8. 空孔 / 疑似孔位偏移 ----
                if (inside.Count == 0)
                {
                    if (nearBy.Count > 0)
                    {
                        double minGap = nearBy.Min(x => x.Gap) * MM;
                        AddIssue(issues, severityByHole, hid, label, locText, 1, "疑似孔位偏移",
                            "0 根入孔", "—",
                            string.Format("孔范围内没有 MEP，但 {0}mm 内有 {1} 根穿墙 MEP，最近一根离孔边 {2}mm。可能是孔开错位置/漏开在正确位置，也可能是相邻孔的管。",
                                searchR * MM, nearBy.Count, minGap.ToString("F1")));
                    }
                    else
                    {
                        AddIssue(issues, severityByHole, hid, label, locText, 2, "空孔",
                            "0 根入孔", "—",
                            "该孔没有任何 MEP 穿过（搜索半径 " + (searchR * MM).ToString("F0") + "mm）。确认是否要删除这个洞。");
                    }
                }
            }
        }

        // ---------- 成组：并查集 ----------
        private static int CountGroups(List<MepProj> list, double groupU, double groupV)
        {
            int n = list.Count;
            int[] parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    double du = Math.Abs(list[i].CenterU - list[j].CenterU) * MM;
                    double dv = Math.Abs(list[i].CenterZ - list[j].CenterZ) * MM;
                    if (du <= groupU && dv <= groupV) Union(parent, i, j);
                }
            }

            HashSet<int> roots = new HashSet<int>();
            for (int i = 0; i < n; i++) roots.Add(Find(parent, i));
            return roots.Count;
        }

        private static int Find(int[] parent, int i)
        {
            while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
            return i;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra != rb) parent[rb] = ra;
        }

        // ---------- 加一条问题 ----------
        private static void AddIssue(List<HoleCheckIssue> issues, Dictionary<ElementId, int> sev,
            ElementId hid, string label, string loc, int sevKey, string kind,
            string actual, string expected, string detail)
        {
            HoleCheckIssue item = new HoleCheckIssue();
            item.SortKey = sevKey;
            item.SeverityText = SevText(sevKey);
            item.HoleId = hid;
            item.HoleIdValue = hid == null ? -1 : hid.IntegerValue;
            item.HoleText = label;
            item.LocationText = loc;
            item.Kind = kind;
            item.ActualText = actual;
            item.ExpectedText = expected;
            item.Detail = detail;
            issues.Add(item);

            int cur;
            if (!sev.TryGetValue(hid, out cur) || sevKey < cur) sev[hid] = sevKey;
        }

        private static string SevText(int k)
        {
            if (k == 0) return "严重";
            if (k == 1) return "警告";
            return "提示";
        }

        private static string MmText(double ft)
        {
            return (ft * MM).ToString("F1");
        }

        // ============================================================
        // 孔坐标系 / 孔实际矩形
        // ============================================================
        private static bool TryBuildFrame(FamilyInstance hole, Solid body, XYZ origin, out XYZ uDir, out XYZ nDir)
        {
            uDir = null; nDir = null;

            // 1) 优先用实体几何定方向：取面积最大的竖直面法向（墙法向）。
            //    这样不受族「总是垂直」设置影响，也不会因 HandOrientation 与墙向不平行而把矩形算成外接包络。
            XYZ dominant = GetDominantHorizontalNormal(body);
            if (dominant != null)
            {
                nDir = dominant;
                uDir = nDir.CrossProduct(XYZ.BasisZ);
                if (uDir.GetLength() < EPS) return false;
                uDir = uDir.Normalize();
                return true;
            }

            // 2) 几何不可用时退 HandOrientation / FacingOrientation
            XYZ hand = null, facing = null;
            try { hand = hole.HandOrientation; } catch { }
            try { facing = hole.FacingOrientation; } catch { }

            XYZ handH = Horizontalize(hand);
            XYZ facingH = Horizontalize(facing);

            if (handH != null)
            {
                uDir = handH;
            }
            else if (facingH != null)
            {
                // 族的前方向通常是墙法向，转 90° 得墙方向
                uDir = new XYZ(-facingH.Y, facingH.X, 0.0);
                if (uDir.GetLength() < EPS) return false;
                uDir = uDir.Normalize();
            }
            else return false;

            nDir = uDir.CrossProduct(XYZ.BasisZ);
            if (nDir.GetLength() < EPS) return false;
            nDir = nDir.Normalize();
            return true;
        }

        private static XYZ Horizontalize(XYZ v)
        {
            if (v == null) return null;
            if (v.GetLength() < EPS) return null;
            XYZ h = new XYZ(v.X, v.Y, 0.0);
            if (h.GetLength() < 1e-3) return null;   // 接近竖直，水平分量不可用
            return h.Normalize();
        }

        /// <summary>
        /// 从孔实体里找面积最大的竖直面的法向（即墙法向）。
        /// 只取竖直面，避免方洞三向面积接近时误取上下水平面。
        /// </summary>
        private static XYZ GetDominantHorizontalNormal(Solid body)
        {
            if (body == null) return null;

            XYZ bestNormal = null;
            double bestArea = 0.0;
            FaceArray faces = body.Faces;
            for (int i = 0; i < faces.Size; i++)
            {
                Face f = faces.get_Item(i);
                XYZ n = null;
                try { n = f.ComputeNormal(new UV(0.5, 0.5)); } catch { }
                if (n == null) continue;
                if (Math.Abs(n.Z) > 0.01) continue;          // 只要竖直面（法向水平）

                double area = 0.0;
                try { area = f.Area; } catch { }
                if (area > bestArea) { bestArea = area; bestNormal = n; }
            }

            if (bestNormal == null) return null;
            XYZ flat = new XYZ(bestNormal.X, bestNormal.Y, 0.0);
            return flat.GetLength() < EPS ? null : flat.Normalize();
        }

        private static bool TryMeasureHoleBox(Solid body, XYZ origin, XYZ uDir, XYZ nDir,
            out double u0, out double u1, out double z0, out double z1, out double depth)
        {
            u0 = u1 = z0 = z1 = depth = 0.0;
            if (body == null) return false;

            double minU = double.MaxValue, maxU = -double.MaxValue;
            double minZ = double.MaxValue, maxZ = -double.MaxValue;
            double minN = double.MaxValue, maxN = -double.MaxValue;

            EdgeArray edges = body.Edges;
            for (int i = 0; i < edges.Size; i++)
            {
                Edge ed = edges.get_Item(i);
                IList<XYZ> pts = null;
                try { pts = ed.Tessellate(); } catch { continue; }
                if (pts == null) continue;

                for (int k = 0; k < pts.Count; k++)
                {
                    XYZ p = pts[k];
                    double uu = (p - origin).DotProduct(uDir);
                    double nn = (p - origin).DotProduct(nDir);

                    if (uu < minU) minU = uu;
                    if (uu > maxU) maxU = uu;
                    if (p.Z < minZ) minZ = p.Z;
                    if (p.Z > maxZ) maxZ = p.Z;
                    if (nn < minN) minN = nn;
                    if (nn > maxN) maxN = nn;
                }
            }

            if (minU > maxU || minZ > maxZ) return false;

            u0 = minU; u1 = maxU;
            z0 = minZ; z1 = maxZ;
            depth = maxN - minN;
            if (depth < EPS) depth = 200.0 / MM;
            return true;
        }

        private static GeometryElement SafeGeometry(FamilyInstance hole)
        {
            try
            {
                Options opt = new Options();
                opt.DetailLevel = ViewDetailLevel.Fine;
                opt.ComputeReferences = false;
                opt.IncludeNonVisibleObjects = false;
                return hole.get_Geometry(opt);
            }
            catch { return null; }
        }

        private static void CollectSolids(GeometryElement ge, List<Solid> list)
        {
            if (ge == null) return;
            foreach (GeometryObject go in ge)
            {
                Solid s = go as Solid;
                if (s != null)
                {
                    if (s.Edges != null && s.Edges.Size > 0) list.Add(s);
                    continue;
                }
                GeometryInstance gi = go as GeometryInstance;
                if (gi != null)
                {
                    GeometryElement instGe = null;
                    try { instGe = gi.GetInstanceGeometry(); } catch { }
                    CollectSolids(instGe, list);
                }
            }
        }

        private static Solid PickBiggestSolid(List<Solid> solids)
        {
            Solid best = null;
            double bestVol = 0.0;
            foreach (Solid s in solids)
            {
                double v = 0.0;
                try { v = s.Volume; } catch { continue; }
                if (v > bestVol) { bestVol = v; best = s; }
            }
            return best;
        }

        // ============================================================
        // MEP 索引与查询
        // ============================================================
        private void EnsureIndex()
        {
            double needMm = Math.Max(2000.0, MEPConfig.GetDouble("HoleCheck.SearchRadiusMm", 300.0) * 2.0);
            if (_indexPadMm > 0 && needMm <= _indexPadMm + 0.1) return;
            BuildIndex(needMm);
        }

        private void BuildIndex(double padMm)
        {
            _meps.Clear();
            _indexPadMm = padMm;

            // 1. 先求所有孔的总包围盒（外扩 pad），只要世界坐标在这个盒子内（含 1mm 容差）的 MEP 才进索引
            Outline total = null;
            foreach (FamilyInstance h in _holes)
            {
                if (h == null || !h.IsValidObject) continue;
                BoundingBoxXYZ b = null;
                try { b = h.get_BoundingBox(null); } catch { }
                if (b == null) continue;
                Outline o = new Outline(b.Min, b.Max);
                if (total == null) total = o;
                else total = UnionOutline(total, o);
            }

            double padFt = padMm / MM;
            if (total != null)
                total = new Outline(total.MinimumPoint - new XYZ(padFt, padFt, padFt),
                                    total.MaximumPoint + new XYZ(padFt, padFt, padFt));
            _holeBBoxPadMm = padMm;

            MEPLogger.Instance.AppendLine("孔总包围盒(世界)：" + HoleCalc.FmtOutline(total) +
                                         "（外扩 " + padMm.ToString("F0") + "mm）");

            // 2. 当前文档 + 所有链接文档。链接里的 MEP 用 GetTotalTransform 转到世界坐标，
            //    这一步是本命令支持 LINK MEP 的关键：bbox 一律先转世界，再用同一个世界盒子做判断。
            CollectMeps(_doc, Transform.Identity, total, "当前文档[" + _doc.Title + "]");

            int linkTotal = 0, linkLoaded = 0;
            foreach (RevitLinkInstance li in new FilteredElementCollector(_doc).OfClass(typeof(RevitLinkInstance)))
            {
                linkTotal++;
                Document ld = null;
                Transform tf = null;
                try { ld = li.GetLinkDocument(); tf = li.GetTotalTransform(); } catch { }
                if (ld == null || tf == null)
                {
                    MEPLogger.Instance.AppendLine("链接[" + li.Id.IntegerValue + "]：未加载（GetLinkDocument 为空），已跳过");
                    continue;
                }
                linkLoaded++;
                CollectMeps(ld, tf, total, "链接[" + li.Id.IntegerValue + "][" + ld.Title + "]");
            }

            MEPLogger.Instance.AppendLine("MEP 索引合计：" + _meps.Count + " 个；链接 " + linkLoaded + "/" + linkTotal + " 个已加载");
        }

        /// <summary>
        /// 收集某文档里的 Duct / Pipe / CableTray，包围盒统一转到世界坐标后再用世界盒子过滤。
        /// 不用 BoundingBoxIntersectsFilter 做服务端预筛：它对链接文档的坐标基准不可控，
        /// 曾出现"链接里有管却全部被筛掉"的情况（索引只剩当前文档的少数元素）。
        /// 逐个元素取 bbox 的代价是几秒，换准确性，符合本命令"稳定优先"的要求。
        /// </summary>
        private void CollectMeps(Document d, Transform tf, Outline worldFilter, string tag)
        {
            if (d == null)
            {
                MEPLogger.Instance.AppendLine(tag + "：文档不可用，跳过");
                return;
            }

            int raw = 0, kept = 0;
            Outline docRange = null;
            Type[] classes = new Type[] { typeof(Duct), typeof(Pipe), typeof(CableTray) };

            foreach (Type t in classes)
            {
                FilteredElementCollector col;
                try { col = new FilteredElementCollector(d).WhereElementIsNotElementType().OfClass(t); }
                catch { continue; }

                foreach (Element e in col)
                {
                    raw++;

                    BoundingBoxXYZ bb = null;
                    try { bb = e.get_BoundingBox(null); } catch { }
                    if (bb == null) continue;

                    Outline world = null;
                    try { world = HoleCalc.GetWorldOutline(bb, tf); } catch { }
                    if (world == null) continue;

                    // 全文档 MEP 的世界范围（不论是否在孔附近），用于判断链接坐标是否与孔对齐
                    docRange = (docRange == null) ? world : UnionOutline(docRange, world);

                    if (worldFilter != null)
                    {
                        bool hit = true;
                        try { hit = worldFilter.Intersects(world, 1.0 / MM); } catch { hit = true; }
                        if (!hit) continue;
                    }

                    MepItem item = new MepItem();
                    item.Elem = e;
                    item.Tf = tf;
                    item.Box = world;
                    _meps.Add(item);
                    kept++;
                }
            }

            MEPLogger.Instance.AppendLine(tag + "：Duct/Pipe/CableTray 共 " + raw + " 个，范围内 " + kept + " 个" +
                                         (docRange == null ? "｜全文档世界范围 <无>" : "｜全文档世界范围 " + HoleCalc.FmtOutline(docRange)));
        }

        private static Outline UnionOutline(Outline a, Outline b)
        {
            XYZ min = new XYZ(Math.Min(a.MinimumPoint.X, b.MinimumPoint.X),
                              Math.Min(a.MinimumPoint.Y, b.MinimumPoint.Y),
                              Math.Min(a.MinimumPoint.Z, b.MinimumPoint.Z));
            XYZ max = new XYZ(Math.Max(a.MaximumPoint.X, b.MaximumPoint.X),
                              Math.Max(a.MaximumPoint.Y, b.MaximumPoint.Y),
                              Math.Max(a.MaximumPoint.Z, b.MaximumPoint.Z));
            return new Outline(min, max);
        }

        private List<MepItem> QueryMeps(Outline q)
        {
            List<MepItem> result = new List<MepItem>();
            if (q == null) return result;

            foreach (MepItem m in _meps)
            {
                try
                {
                    if (q.Intersects(m.Box, 0.001)) result.Add(m);
                }
                catch { }
            }
            return result;
        }

        /// <summary>
        /// 用「孔实测矩形 + 深度 + 半径」在世界坐标构造查询盒，不再依赖族实例的 get_BoundingBox。
        /// 有些孔族的 bbox 为空或严重偏小，用它查询会一个候选都取不到（会表现为"完全不支持 LINK"）。
        /// u0/u1 是相对 origin 的 u 偏移，z0/z1 是绝对世界 Z。
        /// </summary>
        private static Outline BuildQueryOutline(XYZ origin, XYZ uDir, XYZ nDir,
            double u0, double u1, double z0, double z1, double depth, double radius)
        {
            double uMin = Math.Min(u0, u1) - radius;
            double uMax = Math.Max(u0, u1) + radius;
            double zMin = Math.Min(z0, z1) - radius;
            double zMax = Math.Max(z0, z1) + radius;
            double halfN = Math.Max(0.0, depth) / 2.0 + radius;

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = -double.MaxValue, maxY = -double.MaxValue, maxZ = -double.MaxValue;

            for (int iu = 0; iu <= 1; iu++)
            {
                double uu = (iu == 0) ? uMin : uMax;
                for (int iz = 0; iz <= 1; iz++)
                {
                    double zz = (iz == 0) ? zMin : zMax;
                    for (int ir = 0; ir <= 1; ir++)
                    {
                        double nn = (ir == 0) ? -halfN : halfN;
                        double px = origin.X + uDir.X * uu + nDir.X * nn;
                        double py = origin.Y + uDir.Y * uu + nDir.Y * nn;
                        double pz = zz + nDir.Z * nn;

                        if (px < minX) minX = px;
                        if (px > maxX) maxX = px;
                        if (py < minY) minY = py;
                        if (py > maxY) maxY = py;
                        if (pz < minZ) minZ = pz;
                        if (pz > maxZ) maxZ = pz;
                    }
                }
            }

            if (minX > maxX || minZ > maxZ) return null;
            return new Outline(new XYZ(minX, minY, minZ), new XYZ(maxX, maxY, maxZ));
        }

        private static string ElemTag(Element e)
        {
            if (e == null) return "<null>";
            string cat = "?";
            try { if (e.Category != null) cat = e.Category.Name; } catch { }
            string doc = "";
            try { if (e.Document != null) doc = e.Document.Title; } catch { }
            return "#" + e.Id.IntegerValue + " " + cat + (string.IsNullOrEmpty(doc) ? "" : "@" + doc);
        }

        private static bool TryGetAxis(MepItem m, out XYZ p0, out XYZ p1)
        {
            p0 = null; p1 = null;
            if (m == null || m.Elem == null || !m.Elem.IsValidObject) return false;

            LocationCurve lc = m.Elem.Location as LocationCurve;
            if (lc == null || lc.Curve == null) return false;

            Curve c = lc.Curve;
            p0 = m.Tf.OfPoint(c.GetEndPoint(0));
            p1 = m.Tf.OfPoint(c.GetEndPoint(1));
            return p0.DistanceTo(p1) > 1e-5;
        }

        private static bool TryParamFt(FamilyInstance fi, string name, out double ft)
        {
            ft = 0.0;
            if (fi == null || string.IsNullOrEmpty(name)) return false;
            Parameter p = fi.LookupParameter(name);
            if (p == null) return false;
            if (p.StorageType != StorageType.Double) return false;
            ft = p.AsDouble();
            return ft > 0.0;
        }

        // ============================================================
        // 视图临时着色（会在窗口关闭 / 下次运行前恢复）
        // ============================================================
        private void ApplyHighlight(Dictionary<ElementId, int> severityByHole, DbView view)
        {
            if (view == null || !view.IsValidObject)
            {
                InfoCenterTip.Show("当前视图不支持临时着色，仅输出列表。");
                return;
            }
            if (severityByHole == null || severityByHole.Count == 0) return;

            Dictionary<int, OverrideGraphicSettings> orig = new Dictionary<int, OverrideGraphicSettings>();
            using (Transaction t = new Transaction(_doc, "墙洞体检-临时着色"))
            {
                t.Start();

                foreach (KeyValuePair<ElementId, int> kv in severityByHole)
                {
                    try
                    {
                        if (!orig.ContainsKey(kv.Key.IntegerValue))
                            orig[kv.Key.IntegerValue] = view.GetElementOverrides(kv.Key);

                        RevitColor c;
                        int weight;
                        if (kv.Value == 0) { c = new RevitColor(200, 0, 0); weight = 4; }
                        else if (kv.Value == 1) { c = new RevitColor(255, 140, 0); weight = 3; }
                        else { c = new RevitColor(210, 170, 0); weight = 2; }

                        OverrideGraphicSettings ogs = new OverrideGraphicSettings();
                        ogs.SetProjectionLineColor(c);
                        ogs.SetProjectionLineWeight(weight);
                        view.SetElementOverrides(kv.Key, ogs);
                    }
                    catch (Exception ex)
                    {
                        MEPLogger.Instance.AppendLine("[着色失败] " + kv.Key.IntegerValue + " : " + ex.Message);
                    }
                }

                t.Commit();
            }

            _lastDocTitle = _doc.Title;
            _lastViewUniqueId = view.UniqueId;
            _lastOrig = orig;
        }

        public static void ClearPreviousHighlight(Document doc)
        {
            Dictionary<int, OverrideGraphicSettings> orig = _lastOrig;
            string viewUid = _lastViewUniqueId;
            string docTitle = _lastDocTitle;

            _lastOrig = null;
            _lastViewUniqueId = null;
            _lastDocTitle = null;

            if (doc == null || !doc.IsValidObject) return;
            if (orig == null || orig.Count == 0) return;
            if (string.IsNullOrEmpty(viewUid) || doc.Title != docTitle) return;

            DbView view = null;
            try { view = doc.GetElement(viewUid) as DbView; } catch { }
            if (view == null || !view.IsValidObject) return;

            using (Transaction t = new Transaction(doc, "墙洞体检-清除着色"))
            {
                t.Start();
                foreach (KeyValuePair<int, OverrideGraphicSettings> kv in orig)
                {
                    try { view.SetElementOverrides(new ElementId(kv.Key), kv.Value); }
                    catch { }
                }
                t.Commit();
            }
        }

        // ---------- 内部数据结构 ----------
        private class MepItem
        {
            public Element Elem;
            public Transform Tf;
            public Outline Box;
        }

        private class MepProj
        {
            public MepItem Item;
            public double MinU, MaxU, MinZ, MaxZ;
            public double CenterU, CenterZ;
            public double Gap;
        }
    }

    // ================================================================
    // 【C】结果条目
    // ================================================================
    public class HoleCheckIssue
    {
        public int SortKey { get; set; }
        public string SeverityText { get; set; }
        public ElementId HoleId { get; set; }
        public int HoleIdValue { get; set; }
        public string HoleText { get; set; }
        public string LocationText { get; set; }
        public string Kind { get; set; }
        public string ActualText { get; set; }
        public string ExpectedText { get; set; }
        public string Detail { get; set; }
    }

    // ================================================================
    // 【D】无模式结果窗
    // ================================================================
    public class HoleCheckResultForm : WinForm
    {
        private readonly UIDocument _uidoc;
        private readonly HoleCheckRunner _runner;
        private readonly HoleCheckHandler _handler;
        private readonly ExternalEvent _event;

        private DataGridView _grid;
        private Label _lblSummary;
        private Button _btnRefresh;
        private Button _btnClear;
        private Button _btnClose;

        private NumericUpDown _nudNear, _nudSearch, _nudGroupU, _nudGroupV, _nudSizeTol, _nudExceed;

        private List<HoleCheckIssue> _issues = new List<HoleCheckIssue>();

        public bool SuppressClearOnClose;

        public HoleCheckResultForm(UIDocument uidoc, HoleCheckRunner runner, List<HoleCheckIssue> issues,
            HoleCheckHandler handler, ExternalEvent ev)
        {
            _uidoc = uidoc;
            _runner = runner;
            _handler = handler;
            _event = ev;

            // 双缓冲：避免窗体首次显示时先刷白底再画控件造成的"一下白闪"
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            DoubleBuffered = true;

            BuildUi();
            LoadParamsFromConfig();
            UpdateIssues(issues);
        }

        // ---------- UI ----------
        private void BuildUi()
        {
            Text = "墙洞体检";
            Width = 1220;
            Height = 680;
            MinimumSize = new System.Drawing.Size(1080, 560);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = true;
            MaximizeBox = true;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 3;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56F));

            WinPanel top = BuildParamPanel();
            _grid = BuildGrid();
            WinPanel bottom = BuildBottomPanel();

            layout.Controls.Add(top, 0, 0);
            layout.Controls.Add(_grid, 0, 1);
            layout.Controls.Add(bottom, 0, 2);
            Controls.Add(layout);
        }

        private WinPanel BuildParamPanel()
        {
            WinPanel panel = new WinPanel();
            panel.Dock = DockStyle.Fill;
            panel.BackColor = WinColor.FromArgb(246, 246, 246);

            ToolTip tip = new ToolTip();
            tip.AutoPopDelay = 10000;

            int row1 = 8, row2 = 50;
            int step = 196;

            _nudNear = AddNum(panel, "关联半径(mm)", 12, row1, 100.0, 0.0, 100000.0, tip,
                "MEP 截面距孔边的二维距离小于该值即视为「穿这个孔」的 MEP。默认 100。");
            _nudSearch = AddNum(panel, "偏移搜索(mm)", 12 + step, row1, 300.0, 0.0, 100000.0, tip,
                "超过关联半径、但在该范围内穿墙的 MEP，会报「疑似孔位偏移」。填 0 关闭该提示。");
            _nudGroupU = AddNum(panel, "成组水平间距(mm)", 12 + step * 2, row1, 60.0, 0.0, 100000.0, tip,
                "两根 MEP 截面中心的水平距离不超过该值即归为同一组。默认 60。");
            _nudGroupV = AddNum(panel, "成组竖直间距(mm)", 12, row2, 100.0, 0.0, 100000.0, tip,
                "两根 MEP 截面中心的竖直距离不超过该值即归为同一组。默认 100。");
            _nudSizeTol = AddNum(panel, "尺寸容差(mm)", 12 + step, row2, 20.0, 0.0, 100000.0, tip,
                "孔径与应有尺寸的差超过该值才报。默认 20。");
            _nudExceed = AddNum(panel, "越界容差(mm)", 12 + step * 2, row2, 5.0, 0.0, 100000.0, tip,
                "MEP 超出孔边的量小于该值时不报，用于吸收建模精度。默认 5。");

            Label hint = new Label();
            hint.Text = "改完参数点【重新检测】生效；着色：红=严重 / 橙=警告 / 黄=提示";
            hint.AutoSize = true;
            hint.ForeColor = WinColor.FromArgb(110, 110, 110);
            hint.Location = new System.Drawing.Point(12 + step * 3 - 40, row2 + 4);
            panel.Controls.Add(hint);

            return panel;
        }

        private NumericUpDown AddNum(WinPanel parent, string caption, int x, int y,
            double val, double min, double max, ToolTip tip, string tooltip)
        {
            Label lbl = new Label();
            lbl.Text = caption;
            lbl.AutoSize = true;
            lbl.Location = new System.Drawing.Point(x, y + 3);
            parent.Controls.Add(lbl);

            NumericUpDown nud = new NumericUpDown();
            nud.Location = new System.Drawing.Point(x + 128, y);
            nud.Width = 60;
            nud.Minimum = (decimal)min;
            nud.Maximum = (decimal)max;
            nud.DecimalPlaces = 1;
            nud.Increment = 10;
            nud.Value = (decimal)val;
            tip.SetToolTip(nud, tooltip);
            parent.Controls.Add(nud);
            return nud;
        }

        private DataGridView BuildGrid()
        {
            DataGridView g = new DataGridView();
            g.Dock = DockStyle.Fill;
            g.AutoGenerateColumns = false;
            g.ReadOnly = true;
            g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            g.MultiSelect = false;
            g.AllowUserToAddRows = false;
            g.AllowUserToResizeRows = false;
            g.RowHeadersVisible = false;
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            g.GridColor = WinColor.FromArgb(220, 220, 220);
            g.BackgroundColor = WinColor.White;
            g.BorderStyle = BorderStyle.None;
            g.RowTemplate.Height = 26;

            g.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle();
            g.ColumnHeadersDefaultCellStyle.BackColor = WinColor.FromArgb(235, 235, 235);
            g.ColumnHeadersDefaultCellStyle.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Bold);

            g.Columns.Add(NewCol("级别", "SeverityText", 6));
            g.Columns.Add(NewCol("孔ID", "HoleIdValue", 7));
            g.Columns.Add(NewCol("族 : 类型", "HoleText", 20));
            g.Columns.Add(NewCol("位置(mm)", "LocationText", 20));
            g.Columns.Add(NewCol("问题", "Kind", 12));
            g.Columns.Add(NewCol("实际(mm)", "ActualText", 12));
            g.Columns.Add(NewCol("应有(mm)", "ExpectedText", 12));
            g.Columns.Add(NewCol("说明", "Detail", 40));

            g.CellDoubleClick += OnCellDoubleClick;
            g.CellFormatting += OnCellFormatting;
            return g;
        }

        private static DataGridViewTextBoxColumn NewCol(string header, string prop, int weight)
        {
            DataGridViewTextBoxColumn col = new DataGridViewTextBoxColumn();
            col.HeaderText = header;
            col.DataPropertyName = prop;
            col.FillWeight = weight;
            col.SortMode = DataGridViewColumnSortMode.NotSortable;
            return col;
        }

        private WinPanel BuildBottomPanel()
        {
            WinPanel panel = new WinPanel();
            panel.Dock = DockStyle.Fill;

            _lblSummary = new Label();
            _lblSummary.Dock = DockStyle.Left;
            _lblSummary.Width = 520;
            _lblSummary.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            _lblSummary.Padding = new Padding(12, 0, 0, 0);
            _lblSummary.ForeColor = WinColor.FromArgb(85, 85, 85);
            panel.Controls.Add(_lblSummary);

            FlowLayoutPanel flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Right;
            flow.FlowDirection = FlowDirection.RightToLeft;
            flow.Width = 400;
            flow.Padding = new Padding(0, 10, 10, 0);

            _btnClose = new Button(); _btnClose.Text = "关闭"; _btnClose.Width = 96; _btnClose.Height = 32;
            _btnClose.Click += delegate { Close(); };

            _btnClear = new Button(); _btnClear.Text = "清除着色"; _btnClear.Width = 96; _btnClear.Height = 32;
            _btnClear.Click += BtnClear_Click;

            _btnRefresh = new Button(); _btnRefresh.Text = "重新检测"; _btnRefresh.Width = 96; _btnRefresh.Height = 32;
            _btnRefresh.Click += BtnRefresh_Click;

            flow.Controls.Add(_btnClose);
            flow.Controls.Add(_btnClear);
            flow.Controls.Add(_btnRefresh);
            panel.Controls.Add(flow);
            return panel;
        }

        // ---------- 数据 ----------
        public void UpdateIssues(List<HoleCheckIssue> issues)
        {
            _issues = issues ?? new List<HoleCheckIssue>();
            _grid.DataSource = null;
            _grid.DataSource = _issues;

            int s = _issues.Count(i => i.SortKey == 0);
            int w = _issues.Count(i => i.SortKey == 1);
            int t = _issues.Count(i => i.SortKey == 2);
            int h = _issues.Select(i => i.HoleIdValue).Distinct().Count();

            Text = "墙洞体检 — " + _issues.Count + " 条";
            _lblSummary.Text = string.Format("孔 {0} 个｜严重 {1} · 警告 {2} · 提示 {3}｜双击行定位孔", h, s, w, t);
            _btnRefresh.Enabled = true;
        }

        public void OnRepaintDone()
        {
            _btnRefresh.Enabled = true;
        }

        // ---------- 事件 ----------
        private void OnCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0) return;
            HoleCheckIssue item = _grid.Rows[e.RowIndex].DataBoundItem as HoleCheckIssue;
            if (item == null) return;

            if (item.SortKey == 0) e.CellStyle.BackColor = WinColor.FromArgb(255, 236, 236);
            else if (item.SortKey == 1) e.CellStyle.BackColor = WinColor.FromArgb(255, 246, 230);
            else e.CellStyle.BackColor = WinColor.FromArgb(252, 252, 238);
        }

        private void OnCellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
            HoleCheckIssue item = _grid.Rows[e.RowIndex].DataBoundItem as HoleCheckIssue;
            if (item == null || item.HoleId == null) return;

            try
            {
                Document doc = _uidoc == null ? null : _uidoc.Document;
                if (doc == null || !doc.IsValidObject) return;
                _uidoc.ShowElements(item.HoleId);
                _uidoc.Selection.SetElementIds(new List<ElementId> { item.HoleId });
            }
            catch { }
        }

        private void BtnRefresh_Click(object sender, EventArgs e)
        {
            try
            {
                SaveParamsToConfig();
                _btnRefresh.Enabled = false;
                _handler.Form = this;
                _handler.PendingOp = HoleCheckHandler.Op.Repaint;
                _event.Raise();
            }
            catch (Exception ex)
            {
                _btnRefresh.Enabled = true;
                InfoCenterTip.Show("重新检测失败：" + ex.Message);
            }
        }

        private void BtnClear_Click(object sender, EventArgs e)
        {
            try
            {
                _handler.PendingOp = HoleCheckHandler.Op.Clear;
                _event.Raise();
            }
            catch (Exception ex)
            {
                InfoCenterTip.Show("清除着色失败：" + ex.Message);
            }
        }

        // ---------- 参数读写 ----------
        private void LoadParamsFromConfig()
        {
            _nudNear.Value = Clamp((decimal)MEPConfig.GetDouble("HoleCheck.NearRadiusMm", 100.0), _nudNear);
            _nudSearch.Value = Clamp((decimal)MEPConfig.GetDouble("HoleCheck.SearchRadiusMm", 300.0), _nudSearch);
            _nudGroupU.Value = Clamp((decimal)MEPConfig.GetDouble("HoleCheck.GroupDuMm", 60.0), _nudGroupU);
            _nudGroupV.Value = Clamp((decimal)MEPConfig.GetDouble("HoleCheck.GroupDvMm", 100.0), _nudGroupV);
            _nudSizeTol.Value = Clamp((decimal)MEPConfig.GetDouble("HoleCheck.SizeTolMm", 20.0), _nudSizeTol);
            _nudExceed.Value = Clamp((decimal)MEPConfig.GetDouble("HoleCheck.ExceedTolMm", 5.0), _nudExceed);
        }

        private static decimal Clamp(decimal v, NumericUpDown nud)
        {
            if (v < nud.Minimum) return nud.Minimum;
            if (v > nud.Maximum) return nud.Maximum;
            return v;
        }

        private void SaveParamsToConfig()
        {
            MEPConfig.SetValue("HoleCheck.NearRadiusMm", Num(_nudNear));
            MEPConfig.SetValue("HoleCheck.SearchRadiusMm", Num(_nudSearch));
            MEPConfig.SetValue("HoleCheck.GroupDuMm", Num(_nudGroupU));
            MEPConfig.SetValue("HoleCheck.GroupDvMm", Num(_nudGroupV));
            MEPConfig.SetValue("HoleCheck.SizeTolMm", Num(_nudSizeTol));
            MEPConfig.SetValue("HoleCheck.ExceedTolMm", Num(_nudExceed));
            MEPConfig.Save();
        }

        private static string Num(NumericUpDown nud)
        {
            return ((double)nud.Value).ToString("F1", CultureInfo.InvariantCulture);
        }

        // ---------- 关闭：清色 ----------
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);

            if (SuppressClearOnClose) return;
            if (_handler == null) return;

            _handler.Form = null;
            if (_event != null)
            {
                _handler.PendingOp = HoleCheckHandler.Op.Clear;
                _event.Raise();
            }
        }
    }

    // ================================================================
    // 【E】ExternalEvent：无模式窗口里不能直接开事务，重检/清色都走这里
    // ================================================================
    public class HoleCheckHandler : IExternalEventHandler
    {
        public enum Op { None, Repaint, Clear }

        public Op PendingOp = Op.None;
        public HoleCheckRunner Runner;
        public HoleCheckResultForm Form;

        public string GetName() { return "MEPTools.HoleCheck.Highlight"; }

        public void Execute(UIApplication app)
        {
            Op op = PendingOp;
            PendingOp = Op.None;

            try
            {
                if (Runner == null) return;
                Document doc = Runner.Document;
                if (doc == null || !doc.IsValidObject) return;

                if (op == Op.Clear)
                {
                    HoleCheckRunner.ClearPreviousHighlight(doc);
                    return;
                }

                if (op != Op.Repaint) return;

                List<HoleCheckIssue> issues = Runner.RunAndHighlight();
                if (Form != null && !Form.IsDisposed)
                {
                    Form.UpdateIssues(issues);
                    Form.OnRepaintDone();
                }
                InfoCenterTip.Show(HoleCheckRunner.BuildSummary(issues));
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("HoleCheckHandler.Execute", ex);
                InfoCenterTip.Show("墙洞体检刷新失败：" + ex.Message);
                if (Form != null && !Form.IsDisposed) Form.OnRepaintDone();
            }
        }
    }
}
