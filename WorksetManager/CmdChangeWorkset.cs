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

            // 1. 检查工作共享状态
            if (!doc.IsWorkshared)
            {
                InfoCenterTip.Show("当前文档未开启工作共享，无法修改工作集");
                MEPLogger.Instance.EndSession("Failed: Document is not workshared.");
                return Result.Cancelled;
            }

            try
            {
                // 2. 获取元素（自动处理预选和空格键交互）
                var filter = new MEPElementSelectionFilter();
                List<Element> selectedElements = MEPSelectionHelper.GetElements(
                    uidoc,
                    filter,
                    "请选择要修改工作集的 MEP 元素，按空格键结束",
                    "请在视图中选择要修改工作集的元素，按空格键结束",
                    minCount: 1,
                    useSpaceKey: true
                );

                if (selectedElements.Count == 0)
                {
                    return Result.Cancelled;
                }

                // 3. 获取当前活动工作集
                WorksetId activeWorksetId = doc.GetWorksetTable().GetActiveWorksetId();
                Workset activeWorkset = doc.GetWorksetTable().GetWorkset(activeWorksetId);

                int successCount = 0;
                int failCount = 0;

                // 4. 开启事务修改工作集
                using (Transaction trans = new Transaction(doc, "修改元素工作集"))
                {
                    trans.Start();
                    foreach (Element elem in selectedElements)
                    {
                        Parameter param = elem.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                        if (param != null && !param.IsReadOnly)
                        {
                            param.Set(activeWorksetId.IntegerValue);
                            successCount++;
                        }
                        else
                        {
                            failCount++;
                            MEPLogger.Instance.AppendLine($"元素 {elem.Id} 无法修改工作集（参数只读或无权限）。");
                        }
                    }
                    trans.Commit();
                }

                // 5. 结果反馈
                string resultMsg = $"已成功将 {successCount} 个元素修改为【{activeWorkset.Name}】";
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