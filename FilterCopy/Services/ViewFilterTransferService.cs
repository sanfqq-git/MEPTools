using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using MEPTools.FilterCopy.Models;

namespace MEPTools.FilterCopy.Services
{
    /// <summary>
    /// 批量写入目标视图。承担全部安全校验与异常隔离：
    /// 1) 绑定视图模板的目标视图 → 直接跳过（绝不修改模板、绝不剥离模板）；
    /// 2) 图纸/图例/明细表等不支持过滤器的视图类型 → 跳过；
    /// 3) 单个视图、单个过滤器异常均 try-catch，不影响事务与其他视图。
    /// </summary>
    public class ViewFilterTransferService
    {
        private readonly Document _doc;
        public TransferResult Result { get; } = new TransferResult();

        public ViewFilterTransferService(Document doc) => _doc = doc;

        public void ApplyToTarget(View target, IReadOnlyList<ViewFilterSnapshot> snapshots, bool overrideMode)
        {
            // ── 安全策略 1：绑定模板的目标视图 → 直接跳过，不碰模板、不剥离模板 ──
            if (target.ViewTemplateId != ElementId.InvalidElementId)
            {
                var tpl = _doc.GetElement(target.ViewTemplateId) as View;
                Result.Log.Add($"[模板跳过] {target.Name} — 绑定了模板「{tpl?.Name}」，已安全跳过，未做任何修改。");
                return;
            }

            // ── 安全策略 2：视图类型校验 ──
            if (!ViewFilterSupport.IsSupported(target))
            {
                Result.Log.Add($"[类型跳过] {target.Name} — 视图类型（{target.ViewType}）不支持过滤器。");
                return;
            }

            try
            {
                // ── 覆盖模式：先清空目标视图原有过滤器 ──
                // 绑定了模板的视图已在上面拦截，因此到这里 target 一定没有模板，
                // 其所有过滤器都不来自模板，直接全部移除即可。
                if (overrideMode)
                {
                    foreach (ElementId oldId in target.GetFilters().ToList())
                    {
                        target.RemoveFilter(oldId);
                    }
                }

                // ── 逐过滤器写入（异常隔离到单个过滤器粒度）──
                var existing = new HashSet<ElementId>(target.GetFilters());
                foreach (var snap in snapshots)
                {
                    try
                    {
                        // 正确顺序：先加入 → 再设启用状态 → 最后设图形替换
                        if (!existing.Contains(snap.FilterId))
                            target.AddFilter(snap.FilterId);

                        target.SetFilterVisibility(snap.FilterId, snap.IsVisible);
                        target.SetFilterOverrides(snap.FilterId, snap.Overrides);
                    }
                    catch (Exception ex)
                    {
                        // 类别不兼容等：仅该过滤器失败，不影响同视图其他过滤器
                        Result.Log.Add($"[过滤器失败] {target.Name} / {snap.FilterName} — {ex.Message}");
                    }
                }

                Result.SuccessCount++;
            }
            catch (Exception ex)
            {
                // 视图级异常隔离：事务不崩溃，其他视图继续执行
                Result.FailCount++;
                Result.Log.Add($"[视图失败] {target.Name} — {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 支持过滤器的视图类型白名单。图纸/图例/明细表/草图视图/模板本身一律排除。
    /// </summary>
    public static class ViewFilterSupport
    {
        private static readonly HashSet<ViewType> SupportedTypes = new HashSet<ViewType>
        {
            ViewType.FloorPlan,       // 平面（含结构平面）
            ViewType.CeilingPlan,     // 天花板平面
            ViewType.Elevation,       // 立面
            ViewType.Section,         // 剖面
            ViewType.ThreeD,          // 三维
            ViewType.AreaPlan,        // 面积平面
            ViewType.Rendering        // 渲染
            // ViewType.EngineeringPlan 新版已移除，不再使用
            // ViewType.StructuralPlan 不存在，结构平面归为 FloorPlan
            // ViewType.Legend 不在白名单中，天然被排除（图例不支持过滤器）
        };

        public static bool IsSupported(View view)
        {
            if (view == null || view.IsTemplate) return false;
            if (view is ViewSheet) return false;              // 图纸
            if (!SupportedTypes.Contains(view.ViewType)) return false;
            return true;
        }
    }
}