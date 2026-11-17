using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.Shared;

namespace MEPTools.WorksetManager
{
    [Transaction(TransactionMode.Manual)]
    public class CmdIsolateWorkset : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;
            View view = uidoc.ActiveView;

            MEPLogger.Instance.StartSession("仅显示所选元素工作集");

            if (!doc.IsWorkshared)
            {
                InfoCenterTip.Show("当前文档未开启工作共享，无法操作工作集");
                MEPLogger.Instance.EndSession("Failed: Document is not workshared.");
                return Result.Cancelled;
            }

            if (view == null)
            {
                InfoCenterTip.Show("无法获取当前活动视图");
                MEPLogger.Instance.EndSession("Failed: ActiveView is null.");
                return Result.Cancelled;
            }

            if (view is ViewSheet)
            {
                InfoCenterTip.Show("图纸视图不支持工作集可见性控制，请在模型视图中运行");
                MEPLogger.Instance.EndSession("Failed: ViewSheet not supported.");
                return Result.Cancelled;
            }

            if (view.IsTemplate)
            {
                InfoCenterTip.Show("当前视图是视图模板，无法设置工作集可见性");
                MEPLogger.Instance.EndSession("Failed: View is a template.");
                return Result.Cancelled;
            }

            bool hasTemplate = view.ViewTemplateId != ElementId.InvalidElementId;
            if (hasTemplate)
            {
                MEPLogger.Instance.AppendLine(
                    $"[警告] 当前视图应用了模板 {view.ViewTemplateId.IntegerValue}，若模板控制工作集可见性，修改可能不生效。");
            }

            try
            {
                IList<Workset> allWorksets = new FilteredWorksetCollector(doc)
                    .OfKind(WorksetKind.UserWorkset)
                    .ToWorksets();

                if (allWorksets.Count == 0)
                {
                    InfoCenterTip.Show("当前项目没有用户工作集");
                    MEPLogger.Instance.EndSession("Failed: No user worksets.");
                    return Result.Cancelled;
                }

                // 预检：视图是否支持工作集可见性
                try
                {
                    view.GetWorksetVisibility(allWorksets[0].Id);
                }
                catch (Exception ex)
                {
                    InfoCenterTip.Show("当前视图类型不支持工作集可见性控制");
                    MEPLogger.Instance.LogException("CmdIsolateWorkset.PreCheck", ex);
                    MEPLogger.Instance.EndSession("Failed: View does not support workset visibility.");
                    return Result.Cancelled;
                }

                // ⭐ 修改点：支持多选，空格键结束
                var filter = new MEPElementSelectionFilter();
                List<Element> selectedElements = MEPSelectionHelper.GetElements(
                    uidoc,
                    filter,
                    "请选择参考元素（可多选，用于确定要隔离的工作集），按空格键结束",
                    "请在视图中选择元素以提取其工作集，按空格键结束",
                    minCount: 1,
                    useSpaceKey: true
                );

                if (selectedElements == null || selectedElements.Count == 0)
                {
                    MEPLogger.Instance.EndSession("Cancelled: No element selected.");
                    return Result.Cancelled;
                }

                // ⭐ 修改点：收集所有选中元素的工作集（去重）
                HashSet<WorksetId> targetWorksetIds = new HashSet<WorksetId>();
                List<string> invalidElementLogs = new List<string>();

                foreach (Element elem in selectedElements)
                {
                    Parameter param = elem.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);

                    if (param == null)
                    {
                        invalidElementLogs.Add($"元素 {elem.Id} 无工作集参数，已跳过。");
                        continue;
                    }

                    int worksetIntId = param.AsInteger();
                    if (worksetIntId < 0)
                    {
                        invalidElementLogs.Add($"元素 {elem.Id} 的工作集ID无效({worksetIntId})，已跳过。");
                        continue;
                    }

                    targetWorksetIds.Add(new WorksetId(worksetIntId));
                }

                if (targetWorksetIds.Count == 0)
                {
                    InfoCenterTip.Show("所选元素中未找到有效的工作集");
                    foreach (string logLine in invalidElementLogs)
                    {
                        MEPLogger.Instance.AppendLine(logLine);
                    }
                    MEPLogger.Instance.EndSession("Failed: No valid workset from selected elements.");
                    return Result.Cancelled;
                }

                int hiddenCount = 0;
                List<string> shownWorksetNames = new List<string>();

                using (Transaction trans = new Transaction(doc, "仅显示所选元素工作集"))
                {
                    trans.Start();

                    foreach (Workset ws in allWorksets)
                    {
                        if (targetWorksetIds.Contains(ws.Id))
                        {
                            // ⭐ 性能优化：先 Get 判断，已是目标状态则跳过 Set
                            if (view.GetWorksetVisibility(ws.Id) != WorksetVisibility.Visible)
                            {
                                view.SetWorksetVisibility(ws.Id, WorksetVisibility.Visible);
                            }
                            shownWorksetNames.Add(ws.Name);
                        }
                        else
                        {
                            // ⭐ 性能优化：先 Get 判断，已是目标状态则跳过 Set
                            if (view.GetWorksetVisibility(ws.Id) != WorksetVisibility.Hidden)
                            {
                                view.SetWorksetVisibility(ws.Id, WorksetVisibility.Hidden);
                            }
                            hiddenCount++; // 计数语义保持不变
                        }
                    }

                    if (trans.Commit() != TransactionStatus.Committed)
                    {
                        InfoCenterTip.Show("事务提交失败，修改已回滚");
                        MEPLogger.Instance.EndSession("Failed: Transaction not committed.");
                        return Result.Failed;
                    }
                }

                // 将跳过元素的日志在事务结束后统一写出（避免事务持锁期间 I/O）
                foreach (string logLine in invalidElementLogs)
                {
                    MEPLogger.Instance.AppendLine(logLine);
                }

                string shownList = string.Join("、", shownWorksetNames);
                string msg = $"已仅显示 {shownWorksetNames.Count} 个工作集【{shownList}】，隐藏其他 {hiddenCount} 个工作集";
                if (hasTemplate) msg += "（提示：视图有模板，可能影响效果）";
                InfoCenterTip.Show(msg);
                MEPLogger.Instance.EndSession(msg);

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                MEPLogger.Instance.EndSession("Cancelled by user.");
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("CmdIsolateWorkset", ex);
                InfoCenterTip.Show("执行失败，请查看日志");
                MEPLogger.Instance.EndSession("Failed: " + ex.Message);
                return Result.Failed;
            }
        }
    }
}