using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.Shared;

namespace MEPTools.WorksetManager
{
    [Transaction(TransactionMode.Manual)]
    public class CmdSetDefaultWorkset : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            MEPLogger.Instance.StartSession("提取并设置默认工作集");

            if (!doc.IsWorkshared)
            {
                InfoCenterTip.Show("当前文档未开启工作共享");
                MEPLogger.Instance.EndSession("Failed: Document is not workshared.");
                return Result.Cancelled;
            }

            try
            {
                // 1. 获取元素（开启单选模式）
                var filter = new MEPElementSelectionFilter();
                List<Element> selectedElements = MEPSelectionHelper.GetElements(
                    uidoc,
                    filter,
                    "请选择参考元素的 MEP 元素",
                    "请在视图中选择一个元素以提取其工作集",
                    minCount: 1,
                    useSpaceKey: false, 
                    singlePick: true
                );

                if (selectedElements == null || selectedElements.Count == 0) return Result.Cancelled;

                // 2. 读取提取元素的工作集
                Element targetElem = selectedElements.First();
                Parameter param = targetElem.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                
                if (param == null)
                {
                    InfoCenterTip.Show("选中的元素不存在工作集参数");
                    MEPLogger.Instance.AppendLine($"元素 {targetElem.Id} 没有 ELEM_PARTITION_PARAM 参数。");
                    MEPLogger.Instance.EndSession("Failed: Param is null.");
                    return Result.Cancelled;
                }

                // 3. 使用 AsInteger 获取工作集 ID
                int worksetIntId = param.AsInteger();
                WorksetId targetWorksetId = new WorksetId(worksetIntId);
                
                WorksetTable worksetTable = doc.GetWorksetTable();
                Workset targetWorkset = worksetTable.GetWorkset(targetWorksetId);

                if (targetWorkset == null)
                {
                    InfoCenterTip.Show("无法识别目标工作集");
                    MEPLogger.Instance.AppendLine($"无法找到 WorksetId 为 {worksetIntId} 的工作集。");
                    MEPLogger.Instance.EndSession("Failed: Workset not found.");
                    return Result.Cancelled;
                }

                // 4. 设置为默认（活动）工作集
                using (Transaction trans = new Transaction(doc, "设置默认工作集"))
                {
                    trans.Start();
                    worksetTable.SetActiveWorksetId(targetWorksetId);
                    trans.Commit();
                }

                // 5. 反馈
                string resultMsg = $"已将【{targetWorkset.Name}】设为默认工作集";
                InfoCenterTip.Show(resultMsg);
                MEPLogger.Instance.EndSession(resultMsg);

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled; 
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("CmdSetDefaultWorkset", ex);
                InfoCenterTip.Show("发生错误，请查看日志");
                MEPLogger.Instance.EndSession("Failed: " + ex.Message);
                return Result.Failed;
            }
        }
    }
}