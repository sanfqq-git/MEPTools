using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace MEPTools
{
    public class App : IExternalApplication
    {
        // 移除之前的硬编码，保留 TabName
        private const string TabName = "MEP工具";
        
        // 定义面板的创建顺序（重要：决定在 Revit 中从左到右的顺序）
        private readonly string[] _panelOrder = new string[] 
        { 
            "连接与翻弯", 
            "对齐与标记", 
            "管理与协同", 
            "实用工具" 
        };

        private UIControlledApplication _app;
        private readonly List<string> _diagnostics = new List<string>();

        public Result OnStartup(UIControlledApplication application)
        {
            _app = application;
            try { application.CreateRibbonTab(TabName); } catch { }

            string asmPath = Assembly.GetExecutingAssembly().Location;

            // 获取所有按钮配置
            var allButtons = RibbonConfig.GetButtonConfigs();
            RunDiagnostics(allButtons);

            // 按照预定义的顺序循环创建面板
            foreach (string panelName in _panelOrder)
            {
                // 1. 获取当前面板的按钮配置
                var panelButtons = allButtons.Where(b => b.PanelName == panelName).ToList();
                if (!panelButtons.Any()) continue; // 如果这个面板没有配置任何按钮，就跳过

                // 2. 查找或创建 RibbonPanel
                RibbonPanel panel = null;
                foreach (RibbonPanel p in application.GetRibbonPanels(TabName))
                {
                    if (p.Name == panelName) { panel = p; break; }
                }
                if (panel == null) panel = application.CreateRibbonPanel(TabName, panelName);

                // 3. 在当前面板内，处理下拉菜单 (按 PulldownGroup 分组)
                var groups = panelButtons.Where(b => !string.IsNullOrEmpty(b.PulldownGroup))
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

                // 4. 在当前面板内，处理直接展示的按钮 (没有 PulldownGroup 的)
                var directButtons = panelButtons.Where(b => string.IsNullOrEmpty(b.PulldownGroup));
                if (directButtons.Any() && groups.Any()) panel.AddSeparator();

                foreach (var btnConfig in directButtons)
                {
                    AddPushButton(panel, asmPath, btnConfig);
                }
            }

            FlushDiagnostics();
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

        // ================= 以下保持原有代码不变 =================

        private void RunDiagnostics(IEnumerable<ButtonConfig> configs)
        {
            var list = configs.ToList();
            // ... (保持原样)
            foreach (var c in list.Where(x => x.CommandType == null))
            {
                _diagnostics.Add($"[ERROR] 配置 \"{c.Text}\" 的 CommandType 为 null，该按钮将被跳过。");
            }
            var dupTypes = list.Where(x => x.CommandType != null).GroupBy(x => x.CommandType).Where(g => g.Count() > 1);
            foreach (var g in dupTypes)
            {
                _diagnostics.Add($"[WARN] 命令类 \"{g.Key.FullName}\" 被 {g.Count()} 个按钮共用：{string.Join("、", g.Select(x => x.Text))}。");
            }
            foreach (var c in list.Where(x => x.CommandType != null && !string.IsNullOrEmpty(x.Id)))
            {
                if (c.Id != c.CommandType.FullName)
                {
                    _diagnostics.Add($"[INFO] 按钮 \"{c.Text}\"：Id 与 FullName 不一致。已使用 FullName 作为 name。");
                }
            }
            var dupIds = list.Where(x => !string.IsNullOrEmpty(x.Id)).GroupBy(x => x.Id).Where(g => g.Count() > 1);
            foreach (var g in dupIds)
            {
                _diagnostics.Add($"[WARN] 业务 Id \"{g.Key}\" 被 {g.Count()} 个按钮共用，建议改成唯一值。");
            }
        }

        private void FlushDiagnostics()
        {
            if (_diagnostics.Count == 0) return;
            try
            {
                string logPath = Path.Combine(Path.GetTempPath(), "MEPTools_Diagnostics.log");
                var sb = new StringBuilder();
                sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] MEPTools 启动诊断");
                sb.AppendLine($"Revit 版本 : {_app.ControlledApplication.VersionNumber}");
                sb.AppendLine($"程序集路径 : {Assembly.GetExecutingAssembly().Location}");
                sb.AppendLine(new string('-', 64));
                foreach (var line in _diagnostics) sb.AppendLine(line);
                File.WriteAllText(logPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

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
            if (config.CommandType == null) return;
            string fullClassName = config.CommandType.FullName;
            try
            {
                PushButtonData data = new PushButtonData(fullClassName, config.Text, asmPath, fullClassName);
                PushButton btn = pulldown.AddPushButton(data);
                if (btn != null)
                {
                    btn.ToolTip = config.ToolTip;
                    ApplyIcon(btn, config.Text);
                }
            }
            catch { }
        }

        private static void AddPushButton(RibbonPanel panel, string asmPath, ButtonConfig config)
        {
            if (config.CommandType == null) return;
            string fullClassName = config.CommandType.FullName;
            try
            {
                PushButtonData data = new PushButtonData(fullClassName, config.Text, asmPath, fullClassName);
                PushButton btn = panel.AddItem(data) as PushButton;
                if (btn != null)
                {
                    btn.ToolTip = config.ToolTip;
                    ApplyIcon(btn, config.Text);
                }
            }
            catch { }
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