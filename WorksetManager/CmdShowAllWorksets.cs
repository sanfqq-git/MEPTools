using System;
using System.Collections.Generic;   // ⭐ 新增：IList<Workset> 需要此命名空间
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.Shared;

namespace MEPTools.WorksetManager
{
    [Transaction(TransactionMode.Manual)]
    public class CmdShowAllWorksets : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;
            View view = uidoc.ActiveView;

            MEPLogger.Instance.StartSession("显示所有工作集");

            if (!doc.IsWorkshared)
            {
                InfoCenterTip.Show("当前文档未开启工作共享，无需操作");
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
                    MEPLogger.Instance.LogException("CmdShowAllWorksets.PreCheck", ex);
                    MEPLogger.Instance.EndSession("Failed: View does not support workset visibility.");
                    return Result.Cancelled;
                }

                int resetCount = 0;
                using (Transaction trans = new Transaction(doc, "显示所有工作集"))
                {
                    trans.Start();
                    foreach (Workset ws in allWorksets)
                    {
                        view.SetWorksetVisibility(ws.Id, WorksetVisibility.UseGlobalSetting);
                        resetCount++;
                    }

                    if (trans.Commit() != TransactionStatus.Committed)
                    {
                        InfoCenterTip.Show("事务提交失败，修改已回滚");
                        MEPLogger.Instance.EndSession("Failed: Transaction not committed.");
                        return Result.Failed;
                    }
                }

                string msg = $"已恢复 {resetCount} 个工作集的全局可见性设置（请确保工作集在项目浏览器中处于打开状态）";
                if (hasTemplate) msg += "（提示：视图有模板，可能影响效果）";
                InfoCenterTip.Show(msg);
                MEPLogger.Instance.EndSession(msg);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("CmdShowAllWorksets", ex);
                InfoCenterTip.Show("执行失败，请查看日志");
                MEPLogger.Instance.EndSession("Failed: " + ex.Message);
                return Result.Failed;
            }
        }
    }
}