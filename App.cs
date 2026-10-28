using System;
using System.Collections.Generic;
using System.Linq;
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
            try { application.CreateRibbonTab(TabName); } catch { }

            RibbonPanel panel = null;
            foreach (RibbonPanel p in application.GetRibbonPanels(TabName))
            {
                if (p.Name == PanelName) { panel = p; break; }
            }
            if (panel == null) panel = application.CreateRibbonPanel(TabName, PanelName);

            string asmPath = Assembly.GetExecutingAssembly().Location;

            // =========================================================
            // 核心：直接调用 RibbonConfig 里的配置，自动生成 UI
            // =========================================================
            var allButtons = RibbonConfig.GetButtonConfigs();
            
            // 1. 先处理所有下拉菜单（按名称分组）
            var groups = allButtons.Where(b => !string.IsNullOrEmpty(b.PulldownGroup))
                                   .GroupBy(b => b.PulldownGroup);

            bool isFirst = true;
            foreach (var group in groups)
            {
                if (!isFirst) panel.AddSeparator();
                isFirst = false;

                string groupName = GetUniqueName(group.Key);
                PulldownButtonData groupData = new PulldownButtonData(groupName, group.Key);
                PulldownButton pulldown = panel.AddItem(groupData) as PulldownButton;
                
                if (pulldown != null)
                {
                    pulldown.ToolTip = $"{group.Key}集合";
                    foreach (var btnConfig in group)
                    {
                        AddSubButton(pulldown, asmPath, btnConfig);
                    }
                }
            }

            // 2. 处理直接放在面板上的按钮（没有 PulldownGroup 的）
            var directButtons = allButtons.Where(b => string.IsNullOrEmpty(b.PulldownGroup));
            if (directButtons.Any() && groups.Any()) panel.AddSeparator();

            foreach (var btnConfig in directButtons)
            {
                AddPushButton(panel, asmPath, btnConfig);
            }

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

        // ================= 底层辅助方法 =================

        private string GetUniqueName(string baseName)
        {
            string name = baseName;
            int i = 1;
            while (IsNameUsedInRibbon(name)) { name = $"{baseName}{i++}"; }
            return name;
        }

        private bool IsNameUsedInRibbon(string name)
        {
            foreach (RibbonPanel p in _app.GetRibbonPanels(TabName))
                foreach (RibbonItem item in p.GetItems())
                    if (item.Name == name) return true;
            return false;
        }

        private static void AddSubButton(PulldownButton pulldown, string asmPath, ButtonConfig config)
        {
            string fullClassName = config.CommandType.FullName;
            
            // 【修改点】使用固定的 Id，如果没有配置 Id，则使用类名
            string subName = string.IsNullOrEmpty(config.Id) ? fullClassName : config.Id; 
            
            PushButtonData data = new PushButtonData(subName, config.Text, asmPath, fullClassName);
            PushButton btn = pulldown.AddPushButton(data);
            if (btn != null)
            {
                btn.ToolTip = config.ToolTip;
                ApplyIcon(btn, config.Text);
            }
        }

        private static void AddPushButton(RibbonPanel panel, string asmPath, ButtonConfig config)
        {
            string fullClassName = config.CommandType.FullName;
            
            // 【修改点】使用固定的 Id，如果没有配置 Id，则使用类名
            string btnName = string.IsNullOrEmpty(config.Id) ? fullClassName : config.Id; 
            
            PushButtonData data = new PushButtonData(btnName, config.Text, asmPath, fullClassName);
            PushButton btn = panel.AddItem(data) as PushButton;
            if (btn != null)
            {
                btn.ToolTip = config.ToolTip;
                ApplyIcon(btn, config.Text);
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