using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using WinFormsTextBox = System.Windows.Forms.TextBox;
using WinFormsControl = System.Windows.Forms.Control;

namespace MEPTools.SyncRelinquish
{
    /// <summary>
    /// 主动执行：同步到中心模型，并可选重载链接文件，完成后自动放弃所有权限
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class Cmd_SyncAndRelinquish : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc?.Document;

            if (doc == null || !doc.IsWorkshared)
            {
                InfoCenterTip.Show("当前文档不是工作共享模型，无法同步。");
                return Result.Cancelled;
            }
            if (doc.IsDetached)
            {
                InfoCenterTip.Show("当前是分离模型，无法同步到中心。");
                return Result.Cancelled;
            }

            // 1. 获取所有链接文件
            var linkTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkType))
                .Cast<RevitLinkType>()
                .OrderBy(l => l.Name)
                .ToList();

            // 2. 弹出选择窗口 (如果有链接文件)
            List<RevitLinkType> linksToReload = new List<RevitLinkType>();
            if (linkTypes.Count > 0)
            {
                using (var dialog = new LinkReloadDialog(linkTypes))
                {
                    if (dialog.ShowDialog() != DialogResult.OK)
                    {
                        return Result.Cancelled;
                    }
                    linksToReload = dialog.SelectedLinks;
                }
            }

            // 3. 重载选中的链接文件 (重载不需要使用 Transaction)
            if (linksToReload.Count > 0)
            {
                int reloadSuccess = 0;
                foreach (var link in linksToReload)
                {
                    try
                    {
                        link.Reload();
                        reloadSuccess++;
                    }
                    catch (Exception ex)
                    {
                        MEPLogger.Instance.LogException($"Reload Link: {link.Name}", ex);
                    }
                }
                if (reloadSuccess > 0)
                {
                    InfoCenterTip.Show($"已重载 {reloadSuccess} 个链接文件");
                }
            }

            // 4. 执行同步并放权
            try
            {
                TransactWithCentralOptions transOpts = new TransactWithCentralOptions();
                SynchronizeWithCentralOptions syncOpts = new SynchronizeWithCentralOptions();
                
                syncOpts.SaveLocalBefore = true;
                syncOpts.SaveLocalAfter = true;
                syncOpts.SetRelinquishOptions(new RelinquishOptions(true));

                doc.SynchronizeWithCentral(transOpts, syncOpts);

                InfoCenterTip.Show("同步完成，已自动放弃全部权限");
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.CentralFileCommunicationException)
            {
                InfoCenterTip.Show("同步失败：无法与中心模型通信，请检查网络");
                return Result.Failed;
            }
            catch (Autodesk.Revit.Exceptions.CentralModelAccessDeniedException)
            {
                InfoCenterTip.Show("同步失败：没有访问中心模型的权限");
                return Result.Failed;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("Cmd_SyncAndRelinquish", ex);
                message = ex.Message;
                InfoCenterTip.Show("同步过程中发生错误，请查看日志");
                return Result.Failed;
            }
        }
    }

    /// <summary>
    /// 链接文件重载选择窗口（支持SHIFT连选，空格默认确认仅同步）
    /// </summary>
    public class LinkReloadDialog : SpaceSubmitForm
    {
        private WinFormsTextBox txtFilter; 
        private CheckedListBox chkList;
        private Button btnSyncOnly;
        private Button btnReloadAndSync;
        private Button btnSelectAll;
        private List<RevitLinkType> _allLinks;

        private int _lastClickedIndex = -1;
        private bool _isUpdatingChecks = false;

        public List<RevitLinkType> SelectedLinks { get; private set; } = new List<RevitLinkType>();

        public LinkReloadDialog(List<RevitLinkType> links)
        {
            _allLinks = links;
            InitializeComponent();
            LoadLinks("");
        }

        private void InitializeComponent()
        {
            this.Text = "同步并放权 - 选择要重载的链接";
            this.Size = new System.Drawing.Size(520, 480);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            Label lblFilter = new Label { Text = "搜索过滤:", Location = new System.Drawing.Point(15, 18), AutoSize = true };
            
            txtFilter = new WinFormsTextBox { Location = new System.Drawing.Point(80, 15), Width = 405 };
            txtFilter.TextChanged += (s, e) => LoadLinks(txtFilter.Text);

            chkList = new CheckedListBox
            {
                Location = new System.Drawing.Point(15, 50),
                Size = new System.Drawing.Size(470, 320),
                CheckOnClick = true,
                DisplayMember = "Name",
                SelectionMode = SelectionMode.One
            };
            chkList.MouseDown += ChkList_MouseDown;
            chkList.ItemCheck += ChkList_ItemCheck;

            btnSelectAll = new Button { Text = "全选 / 全不选", Location = new System.Drawing.Point(15, 385), Size = new System.Drawing.Size(100, 32) };
            btnSelectAll.Click += (s, e) =>
            {
                bool allChecked = chkList.CheckedItems.Count == chkList.Items.Count;
                _isUpdatingChecks = true;
                for (int i = 0; i < chkList.Items.Count; i++)
                    chkList.SetItemChecked(i, !allChecked);
                _isUpdatingChecks = false;
            };

            btnSyncOnly = new Button { Text = "仅同步并放权", Location = new System.Drawing.Point(260, 385), Size = new System.Drawing.Size(105, 32) };
            btnSyncOnly.Click += (s, e) =>
            {
                this.SelectedLinks.Clear();
                this.DialogResult = DialogResult.OK;
                this.Close();
            };

            btnReloadAndSync = new Button { Text = "重载选中并同步", Location = new System.Drawing.Point(375, 385), Size = new System.Drawing.Size(110, 32) };
            btnReloadAndSync.Click += (s, e) =>
            {
                this.SelectedLinks = chkList.CheckedItems.Cast<RevitLinkType>().ToList();
                this.DialogResult = DialogResult.OK;
                this.Close();
            };

            this.AcceptButton = btnSyncOnly;
            // 修复：将焦点设置移入 Load 事件，避免窗体未显示时调用导致异常
            this.Load += (s, e) => this.ActiveControl = btnSyncOnly;
            
            this.Controls.Add(lblFilter);
            this.Controls.Add(txtFilter);
            this.Controls.Add(chkList);
            this.Controls.Add(btnSelectAll);
            this.Controls.Add(btnSyncOnly);
            this.Controls.Add(btnReloadAndSync);
        }

        private void ChkList_MouseDown(object sender, MouseEventArgs e)
        {
            int index = chkList.IndexFromPoint(e.Location);
            if (index == -1) return;

            if ((WinFormsControl.ModifierKeys & Keys.Shift) != 0 && _lastClickedIndex != -1 && _lastClickedIndex != index)
            {
                int start = Math.Min(_lastClickedIndex, index);
                int end = Math.Max(_lastClickedIndex, index);
                
                bool targetState = !chkList.GetItemChecked(index);

                _isUpdatingChecks = true;
                for (int i = start; i <= end; i++)
                {
                    chkList.SetItemChecked(i, targetState);
                }
                _isUpdatingChecks = false;
            }
            else
            {
                _lastClickedIndex = index;
            }
        }

        private void ChkList_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (_isUpdatingChecks) return;
            
            if ((WinFormsControl.ModifierKeys & Keys.Shift) != 0) return;
            
            _lastClickedIndex = e.Index;
        }

        private void LoadLinks(string filter)
        {
            chkList.Items.Clear();
            _lastClickedIndex = -1;
            
            var filtered = string.IsNullOrWhiteSpace(filter) 
                ? _allLinks 
                : _allLinks.Where(l => l.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            _isUpdatingChecks = true;
            foreach (var link in filtered)
            {
                chkList.Items.Add(link);
            }
            _isUpdatingChecks = false;
        }
    }
}