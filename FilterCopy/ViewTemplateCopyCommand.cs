using System;
using Autodesk.Revit.Attributes;
using RevitDoc = Autodesk.Revit.DB.Document;
using RevitElementSet = Autodesk.Revit.DB.ElementSet;
using Autodesk.Revit.UI;
using MEPTools.FilterCopy.Views;
using MEPTools.Shared;

namespace MEPTools.FilterCopy.Commands
{
    /// <summary>
    /// A 功能：从其它文件复制视图样板（View Template）—— 独立命令。
    /// 流程：StartSession → ViewTemplateCopyForm（选源文档 + 勾样板 + 复制）
    ///      → EndSession + InfoCenterTip 气泡。
    /// 只搬样板本体与其过滤器设置，不动当前文档已有视图的任何绑定关系。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ViewTemplateCopyCommand : IExternalCommand
    {
        private const string Title = "从其它文件复制视图样板";

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
                int copiedCount;

                using (var form = new ViewTemplateCopyForm(doc))
                {
                    form.ShowDialog();
                    copiedCount = form.CopiedCount;
                }

                if (copiedCount > 0)
                {
                    summary = "已复制 " + copiedCount + " 个视图样板到当前文档。";
                    InfoCenterTip.Show(summary);
                    return Result.Succeeded;
                }

                summary = "未复制任何视图样板。";
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("ViewTemplateCopyCommand", ex);
                summary = "执行失败：" + ex.Message;
                InfoCenterTip.Show("复制视图样板执行失败，请查看日志");
                return Result.Failed;
            }
            finally
            {
                MEPLogger.Instance.EndSession(summary);
            }
        }
    }
}
