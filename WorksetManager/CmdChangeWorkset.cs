using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.Shared;

namespace MEPTools.WorksetManager
{
    [Transaction(TransactionMode.Manual)]
    public class CmdChangeWorkset : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            MEPLogger.Instance.StartSession("修改元素工作集");

            if (!doc.IsWorkshared)
            {
                InfoCenterTip.Show("当前文档未开启工作共享，无法修改工作集");
                MEPLogger.Instance.EndSession("Failed: Document is not workshared.");
                return Result.Cancelled;
            }

            try
            {
                // 1. 获取元素
                var filter = new MEPElementSelectionFilter();
                List<Element> selectedElements = MEPSelectionHelper.GetElements(
                    uidoc,
                    filter,
                    "请选择要修改工作集的 MEP 元素，按空格键结束",
                    "请在视图中选择要修改工作集的元素，按空格键结束",
                    minCount: 1,
                    useSpaceKey: true
                );

                if (selectedElements == null || selectedElements.Count == 0)
                {
                    return Result.Cancelled;
                }

                // 2. 获取当前活动工作集
                WorksetId activeWorksetId = doc.GetWorksetTable().GetActiveWorksetId();
                Workset activeWorkset = doc.GetWorksetTable().GetWorkset(activeWorksetId);

                int successCount = 0;
                int failCount = 0;
                int skipCount = 0;

                // 3. 开启事务修改工作集
                using (Transaction trans = new Transaction(doc, "修改元素工作集"))
                {
                    trans.Start();

                    // ⭐ 性能优化：事务内只收集失败日志，事务结束后再统一写盘，
                    //    避免在事务持锁期间进行 N 次日志 I/O 拖慢事务提交。
                    var failLogs = new List<string>();
                    int activeWorksetIntId = activeWorksetId.IntegerValue;

                    foreach (Element elem in selectedElements)
                    {
                        Parameter param = elem.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);

                        if (param == null || param.IsReadOnly)
                        {
                            failCount++;
                            string reason = param == null ? "参数不存在" : "参数只读";
                            failLogs.Add($"元素 {elem.Id} 无法修改工作集（{reason}）。");
                            continue;
                        }

                        // ⭐ 获取元素当前的工作集 ID，若与目标一致，则跳过，避免污染协同状态
                        int currentWorksetIntId = param.AsInteger();
                        if (currentWorksetIntId == activeWorksetIntId)
                        {
                            skipCount++;
                            continue;
                        }

                        // 只有真正执行了 Set 且返回 true，才算成功
                        if (param.Set(activeWorksetIntId))
                        {
                            successCount++;
                        }
                        else
                        {
                            failCount++;
                            failLogs.Add($"元素 {elem.Id} 无法修改工作集（Set返回False）。");
                        }
                    }

                    // 检查事务提交状态
                    if (trans.Commit() != TransactionStatus.Committed)
                    {
                        successCount = 0;
                        failCount = selectedElements.Count;
                        skipCount = 0;
                        InfoCenterTip.Show("事务提交失败，所有修改已回滚");
                        MEPLogger.Instance.AppendLine("Transaction Commit Failed.");
                    }

                    // ⭐ 事务结束后、持锁已释放时，统一写出收集的失败日志
                    foreach (string logLine in failLogs)
                    {
                        MEPLogger.Instance.AppendLine(logLine);
                    }
                }

                // 4. 结果反馈
                string resultMsg = $"成功修改 {successCount} 个元素为【{activeWorkset.Name}】";
                if (skipCount > 0) resultMsg += $"，跳过 {skipCount} 个(已在该工作集)";
                if (failCount > 0) resultMsg += $"，{failCount} 个失败";

                InfoCenterTip.Show(resultMsg);
                MEPLogger.Instance.EndSession(resultMsg);

                return Result.Succeeded;
            }
            catch (OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("CmdChangeWorkset", ex);
                InfoCenterTip.Show("执行过程中发生错误，请查看日志");
                MEPLogger.Instance.EndSession("Failed: " + ex.Message);
                return Result.Failed;
            }
        }
    }
}