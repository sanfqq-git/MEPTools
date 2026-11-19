using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using RevitDoc = Autodesk.Revit.DB.Document;
using RevitView = Autodesk.Revit.DB.View;
using RevitElementId = Autodesk.Revit.DB.ElementId;
using RevitElementSet = Autodesk.Revit.DB.ElementSet;
using Autodesk.Revit.UI;
using MEPTools.FilterCopy.Models;
using MEPTools.FilterCopy.Services;
using MEPTools.FilterCopy.ViewModels;
using MEPTools.FilterCopy.Views;
using MEPTools.Shared;

namespace MEPTools.FilterCopy.Commands
{
    /// <summary>
    /// B 功能：复制 View Filter 到目标视图 —— 独立命令。
    /// 来源两种：① 从视图样板复制　② 从用户选择的源视图复制（窗体里单选切换）。
    /// 流程：StartSession → FilterCopyForm 选来源 + 勾目标 → 校验 → MainViewModel
    ///      （单一事务 + ApplyToTarget 安全策略 + FeedbackWindow）→ EndSession + 气泡。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class FilterCopyCommand : IExternalCommand
    {
        private const string Title = "复制视图过滤器";

        public Result Execute(ExternalCommandData commandData, ref string message, RevitElementSet elements)
        {
            var app = commandData == null ? null : commandData.Application;
            var uiDoc = app == null ? null : app.ActiveUIDocument;
            if (uiDoc == null)
            {
                message = "当前没有活动文档。";
                return Result.Failed;
            }

            RevitDoc doc = uiDoc.Document;
            MEPLogger.Instance.StartSession(Title);

            string summary = "已取消，未做任何修改。";

            try
            {
                // ── 1. 选来源（视图样板 / 源视图）+ 勾目标视图，全在一个窗体里完成 ──
                RevitElementId sourceId;
                List<ViewTargetItem> targets;
                bool overrideMode;
                bool sourceIsTemplate;

                using (var form = new FilterCopyForm(doc, uiDoc.ActiveView == null ? RevitElementId.InvalidElementId : uiDoc.ActiveView.Id))
                {
                    if (form.ShowDialog() != DialogResult.OK)
                    {
                        summary = "已取消：用户在视图选择窗口点了取消。";
                        return Result.Cancelled;
                    }

                    // 窗体销毁前把结果取出来（都是纯数据）
                    sourceId = form.SelectedSourceId;
                    targets = form.GetSelectedTargets();
                    overrideMode = form.OverrideMode;
                    sourceIsTemplate = form.SourceIsTemplate;
                }

                // ── 2. 校验来源 ──
                RevitView sourceView = sourceId == RevitElementId.InvalidElementId
                    ? null
                    : doc.GetElement(sourceId) as RevitView;

                if (sourceView == null)
                {
                    TaskDialog.Show(Title, "所选来源不存在，可能已被删除。");
                    MEPLogger.Instance.AppendLine("[校验失败] 来源视图不存在。");
                    summary = "失败：来源视图不存在。";
                    return Result.Cancelled;
                }

                // 视图样板本身不需要过“视图类型白名单”，普通视图才需要
                if (!sourceView.IsTemplate && !ViewFilterSupport.IsSupported(sourceView))
                {
                    TaskDialog.Show(Title, "所选源视图不支持过滤器。");
                    MEPLogger.Instance.AppendLine("[校验失败] 源视图不支持过滤器。");
                    summary = "失败：源视图不支持过滤器。";
                    return Result.Cancelled;
                }

                if (targets.Count == 0)
                {
                    TaskDialog.Show(Title, "项目中没有其他视图可作为目标。");
                    summary = "已取消：没有可用的目标视图。";
                    return Result.Cancelled;
                }

                // ── 3. 源过滤器数量先记日志 ──
                int sourceFilterCount = 0;
                try
                {
                    sourceFilterCount = sourceView.GetFilters().Count;
                }
                catch (Exception ex)
                {
                    MEPLogger.Instance.AppendLine("[警告] 读取来源过滤器失败：" + ex.Message);
                }

                MEPLogger.Instance.AppendLine("来源：" + (sourceIsTemplate || sourceView.IsTemplate ? "视图样板" : "源视图") +
                                              "「" + sourceView.Name + "」（Id=" + sourceView.Id + "，过滤器 " + sourceFilterCount + " 个）");

                if (sourceFilterCount == 0)
                {
                    MEPLogger.Instance.AppendLine("[中止] 来源没有任何过滤器。");
                    TaskDialog.Show(Title, "所选来源「" + sourceView.Name + "」里没有任何过滤器，换一个视图样板或源视图再试。");
                    summary = "失败：来源没有过滤器。";
                    return Result.Cancelled;
                }

                MEPLogger.Instance.AppendLine("目标视图：" + targets.Count + " 个；覆盖模式：" + (overrideMode ? "开" : "关"));
                foreach (ViewTargetItem t in targets)
                {
                    MEPLogger.Instance.AppendLine("  - " + t.ViewName + "（" + t.ViewTypeName +
                        (t.HasTemplate ? "，模板：" + t.TemplateName : "") + "）");
                }

                // ── 4. 原执行管道（MainViewModel / 事务 / 安全策略 / FeedbackWindow）一行未改 ──
                var vm = new MainViewModel(doc, sourceView, targets) { OverrideMode = overrideMode };

                TransferResult result = null;
                EventHandler<TransferResult> onExecuted = (s, e) => result = e;
                vm.Executed += onExecuted;
                try
                {
                    vm.ExecuteCommand.Execute(null);
                }
                finally
                {
                    vm.Executed -= onExecuted;
                }

                int okCount = result == null ? 0 : result.SuccessCount;
                int skipCount = result == null ? 0 : result.Log.Count;

                if (result != null)
                {
                    foreach (string line in result.Log)
                        MEPLogger.Instance.AppendLine(line);
                }

                summary = "已复制到 " + okCount + " 个视图，跳过/失败 " + skipCount + " 项。";
                InfoCenterTip.Show(summary);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("FilterCopyCommand", ex);
                summary = "执行失败：" + ex.Message;
                InfoCenterTip.Show("复制视图过滤器执行失败，请查看日志");
                return Result.Failed;
            }
            finally
            {
                MEPLogger.Instance.EndSession(summary);
            }
        }
    }
}
