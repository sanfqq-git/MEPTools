using System;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace MEPTools
{
    public class App : IExternalApplication
    {
        private const string PanelName = "MEP 工具";
        private const string TabName = "MEP工具";
        private UIControlledApplication _app;

        public Result OnStartup(UIControlledApplication application)
        {
            _app = application;
            try
            {
                application.CreateRibbonTab(TabName);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { }

            RibbonPanel panel = null;
            foreach (RibbonPanel p in application.GetRibbonPanels(TabName))
            {
                if (p.Name == PanelName) { panel = p; break; }
            }
            if (panel == null)
                panel = application.CreateRibbonPanel(TabName, PanelName);

            string asmPath = Assembly.GetExecutingAssembly().Location;

            // 1. 连接工具
            string connGroupName = GetUniqueName("ConnectionTools");
            PulldownButtonData connData = new PulldownButtonData(connGroupName, "连接工具");
            PulldownButton connPulldown = panel.AddItem(connData) as PulldownButton;
            if (connPulldown != null)
            {
                connPulldown.ToolTip = "MEP 管道/风管连接工具集合";
                AddSubButton(connPulldown, asmPath, "三通连接", "MEPTools.TeeConnect.CreateMEPTeeConnect", "自动识别并连接管道/风管，生成三通");
                AddSubButton(connPulldown, asmPath, "四通连接", "MEPTools.CrossConnect.CreateMEPCrossConnect", "自动识别并连接管道/风管，生成四通");
                AddSubButton(connPulldown, asmPath, "高低连接", "MEPTools.MEPHighLowConnector.CreateMEPConnector", "在不同标高的 MEP 元素之间自动生成连接");
            }

            panel.AddSeparator();

            // 2. 翻弯工具
            string bendGroupName = GetUniqueName("BendTools");
            PulldownButtonData bendData = new PulldownButtonData(bendGroupName, "翻弯工具");
            PulldownButton bendPulldown = panel.AddItem(bendData) as PulldownButton;
            if (bendPulldown != null)
            {
                bendPulldown.ToolTip = "MEP 翻弯与升降工具集合";
                AddSubButton(bendPulldown, asmPath, "高低翻弯", "MEPTools.MEPHighLowSaddleBend.CreateMEPOffsetBend", "高低差生成马鞍翻弯");
                AddSubButton(bendPulldown, asmPath, "平面侧翻弯", "MEPTools.LateralBend.CreateMEPLateralBend", "在水平面内两组MEP 元素之间生成拐弯侧向翻弯");
                AddSubButton(bendPulldown, asmPath, "平面翻弯", "MEPTools.SaddleBend.CreateMEPSaddleBend", "在平面内两组MEP 元素之间生成马鞍翻弯");
                AddSubButton(bendPulldown, asmPath, "高低抬升/下降", "MEPTools.UpDown.CreateMEPUpDown", "对单侧 MEP 元素进行抬升或下降操作");
            }

            panel.AddSeparator();

            // 3. 对齐与排序
            AddPushButton(panel, asmPath, "底部平齐", "MEPTools.AlignBottomElevation.AlignMEPBottomElevationCommand", "将选中的 MEP 元素底部标高对齐到指定的基准");
            AddPushButton(panel, asmPath, "分组排序对齐", "MEPTools.GroupSortAlign.MEPGroupSortAlignCommand", "按底部标高分组，组内按净间距自动排布");
            AddPushButton(panel, asmPath, "标记左对齐", "TagAlignLeft.Command", "将选中的标记（Tag）统一左对齐到 MEP 元素");

            panel.AddSeparator();

            // 4. 其他辅助工具
            AddPushButton(panel, asmPath, "链接墙洞", "LinkElementWallHole.CreateHoleFromLinkElements", "在链接模型（如建筑墙）上批量创建墙洞");
            AddPushButton(panel, asmPath, "删除重复图元", "RemoveDuplicateElements.RemoveDuplicateCommand", "查找并删除当前视图中重复的 MEP 图元");
            AddPushButton(panel, asmPath, "标高重命名", "RevitElevationRenamer.ElevationRenamerCommand", "批量重命名或规范化 Revit 标高名称");
            AddPushButton(panel, asmPath, "系统检查", "RevitAddin.SystemChecker.CheckMismatchedConnectionsCommand", "检查 MEP 系统的连通性及参数完整性");

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

        /// <summary>
        /// 检查当前 Revit 面板中是否已存在同名项，存在则自动添加序号后缀
        /// </summary>
        private string GetUniqueName(string baseName)
        {
            string name = baseName;
            int i = 1;
            while (IsNameUsedInRibbon(name))
            {
                name = $"{baseName}{i++}";
            }
            return name;
        }

        private bool IsNameUsedInRibbon(string name)
        {
            foreach (RibbonPanel p in _app.GetRibbonPanels(TabName))
            {
                foreach (RibbonItem item in p.GetItems())
                {
                    if (item.Name == name) return true;
                }
            }
            return false;
        }

        private static void AddSubButton(PulldownButton pulldown, string asmPath,
            string text, string className, string tooltip)
        {
            // 子按钮也使用唯一 Name 避免冲突
            string subName = $"{className}_{Guid.NewGuid().ToString("N").Substring(0, 6)}";
            PushButtonData data = new PushButtonData(subName, text, asmPath, className);
            PushButton btn = pulldown.AddPushButton(data);
            if (btn != null)
            {
                btn.ToolTip = tooltip;
                ApplyIcon(btn, text);
            }
        }

        private static void AddPushButton(RibbonPanel panel, string asmPath,
            string text, string className, string tooltip)
        {
            string btnName = $"{className}_{Guid.NewGuid().ToString("N").Substring(0, 6)}";
            PushButtonData data = new PushButtonData(btnName, text, asmPath, className);
            PushButton btn = panel.AddItem(data) as PushButton;
            if (btn != null)
            {
                btn.ToolTip = tooltip;
                ApplyIcon(btn, text);
            }
        }

        private static void ApplyIcon(PushButton btn, string text)
        {
            try
            {
                btn.LargeImage = LoadImage(text + "_32.png");
                btn.Image = LoadImage(text + "_16.png");
            }
            catch { }
        }

        private static BitmapImage LoadImage(string fileName)
        {
            Uri uri = new Uri($"pack://application:,,,/MEPTools;component/Resources/{fileName}", UriKind.Absolute);
            return new BitmapImage(uri);
        }
    }
}