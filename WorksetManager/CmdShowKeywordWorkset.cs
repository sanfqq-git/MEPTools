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
    public class CmdShowKeywordWorkset : IExternalCommand
    {
        // 记忆上次勾选的配置键名
        private const string ConfigKeyLastSelection = "WorksetManager.SelectedWorksets";
        private const char SplitChar = '|';

        // 用于将 Revit 主窗口句柄包装给 WinForms 窗体做 Owner
        private sealed class RevitWindowWrapper : System.Windows.Forms.IWin32Window
        {
            private readonly IntPtr _handle;
            public RevitWindowWrapper(IntPtr handle) { _handle = handle; }
            public IntPtr Handle { get { return _handle; } }
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;
            View view = uidoc.ActiveView;

            MEPLogger.Instance.StartSession("显示指定工作集");

            // ================= 基础检测 =================
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
                // ================= 获取工作集与预检 =================
                IList<Workset> allWorksets = new FilteredWorksetCollector(doc)
                    .OfKind(WorksetKind.UserWorkset)
                    .ToWorksets();

                if (allWorksets.Count == 0)
                {
                    InfoCenterTip.Show("当前项目没有用户工作集");
                    MEPLogger.Instance.EndSession("Failed: No user worksets.");
                    return Result.Cancelled;
                }

                try
                {
                    view.GetWorksetVisibility(allWorksets[0].Id);
                }
                catch (Exception ex)
                {
                    InfoCenterTip.Show("当前视图类型不支持工作集可见性控制");
                    MEPLogger.Instance.LogException("CmdShowKeywordWorkset.PreCheck", ex);
                    MEPLogger.Instance.EndSession("Failed: View does not support workset visibility.");
                    return Result.Cancelled;
                }

                // ================= 读取记忆并弹窗 =================
                string lastConfig = MEPConfig.GetString(ConfigKeyLastSelection, string.Empty);
                List<string> lastSelected = string.IsNullOrEmpty(lastConfig)
                    ? new List<string>()
                    : lastConfig.Split(new[] { SplitChar }, StringSplitOptions.RemoveEmptyEntries).ToList();

                List<string> selectedNames = null;
                try
                {
                    using (var form = new WorksetSelectionForm(allWorksets, lastSelected))
                    {
                        IntPtr revitHandle = Autodesk.Windows.ComponentManager.ApplicationWindow;
                        var dlgResult = revitHandle != IntPtr.Zero
                            ? form.ShowDialog(new RevitWindowWrapper(revitHandle))
                            : form.ShowDialog();

                        if (dlgResult != System.Windows.Forms.DialogResult.OK)
                        {
                            MEPLogger.Instance.EndSession("Cancelled by user at workset selection.");
                            return Result.Cancelled;
                        }
                        
                        selectedNames = form.CheckedWorksetNames;
                    }
                }
                catch (Exception ex)
                {
                    MEPLogger.Instance.LogException("CmdShowKeywordWorkset.ShowForm", ex);
                    InfoCenterTip.Show("打开工作集选择窗口失败，请查看日志");
                    MEPLogger.Instance.EndSession("Failed: Show form error.");
                    return Result.Failed;
                }

                // ================= 保存本次记忆 =================
                string newConfig = string.Join(SplitChar.ToString(), selectedNames);
                MEPConfig.SetValue(ConfigKeyLastSelection, newConfig);
                MEPConfig.Save();

                // ================= 执行可见性设置 =================
                HashSet<string> selectedSet = new HashSet<string>(selectedNames, StringComparer.OrdinalIgnoreCase);
                int visibleCount = 0;
                int hiddenCount = 0;

                using (Transaction trans = new Transaction(doc, "显示指定工作集"))
                {
                    trans.Start();
                    foreach (Workset ws in allWorksets)
                    {
                        if (selectedSet.Contains(ws.Name))
                        {
                            // ⭐ 性能优化：先 Get 判断，避免无意义的 Set
                            if (view.GetWorksetVisibility(ws.Id) != WorksetVisibility.Visible)
                            {
                                view.SetWorksetVisibility(ws.Id, WorksetVisibility.Visible);
                            }
                            visibleCount++;
                        }
                        else
                        {
                            // ⭐ 性能优化：先 Get 判断，避免无意义的 Set
                            if (view.GetWorksetVisibility(ws.Id) != WorksetVisibility.Hidden)
                            {
                                view.SetWorksetVisibility(ws.Id, WorksetVisibility.Hidden);
                            }
                            hiddenCount++;
                        }
                    }

                    if (trans.Commit() != TransactionStatus.Committed)
                    {
                        InfoCenterTip.Show("事务提交失败，修改已回滚");
                        MEPLogger.Instance.EndSession("Failed: Transaction not committed.");
                        return Result.Failed;
                    }
                }

                // ================= 结果反馈 =================
                string msg = $"已显示 {visibleCount} 个工作集";
                if (hiddenCount > 0) msg += $"，隐藏其他 {hiddenCount} 个";
                if (hasTemplate) msg += "（提示：视图有模板，可能影响效果）";

                InfoCenterTip.Show(msg);
                MEPLogger.Instance.EndSession(msg);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("CmdShowKeywordWorkset", ex);
                InfoCenterTip.Show("执行失败，请查看日志");
                MEPLogger.Instance.EndSession("Failed: " + ex.Message);
                return Result.Failed;
            }
        }
    }
}