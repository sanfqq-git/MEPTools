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
        private const string PanelName = "MEP 工具";
        private const string TabName = "MEP工具";
        private UIControlledApplication _app;

        // 启动诊断信息收集，最后统一落盘
        private readonly List<string> _diagnostics = new List<string>();

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

            // 启动前跑一遍诊断（不影响主流程）
            RunDiagnostics(allButtons);

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

            // 诊断结果落盘
            FlushDiagnostics();

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

        // ================= 启动诊断 =================

        private void RunDiagnostics(IEnumerable<ButtonConfig> configs)
        {
            var list = configs.ToList();

            // ① CommandType 为 null：按钮无法创建
            foreach (var c in list.Where(x => x.CommandType == null))
            {
                _diagnostics.Add($"[ERROR] 配置 \"{c.Text}\" 的 CommandType 为 null，该按钮将被跳过。");
            }

            // ② CommandType 重复：同一个命令类挂了多个按钮（由于 name=FullName，后创建会因重名失败）
            var dupTypes = list
                .Where(x => x.CommandType != null)
                .GroupBy(x => x.CommandType)
                .Where(g => g.Count() > 1);
            foreach (var g in dupTypes)
            {
                _diagnostics.Add(
                    $"[WARN] 命令类 \"{g.Key.FullName}\" 被 {g.Count()} 个按钮共用：" +
                    $"{string.Join("、", g.Select(x => x.Text))}。name 使用 FullName 后，后创建的按钮会因重名而创建失败。");
            }

            // ③ Id 与 FullName 不一致：这正是导致旧快捷键失效的根因，作为提示项
            foreach (var c in list.Where(x => x.CommandType != null && !string.IsNullOrEmpty(x.Id)))
            {
                if (c.Id != c.CommandType.FullName)
                {
                    _diagnostics.Add(
                        $"[INFO] 按钮 \"{c.Text}\"：Id=\"{c.Id}\" 与 FullName=\"{c.CommandType.FullName}\" 不一致。" +
                        $"已切换为 FullName 作为 name，快捷键不再受此影响（Id 仅作业务标识）。");
                }
            }

            // ④ Id 重复：仅业务标识层面的警告，不影响命令
            var dupIds = list
                .Where(x => !string.IsNullOrEmpty(x.Id))
                .GroupBy(x => x.Id)
                .Where(g => g.Count() > 1);
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
                string logPath = Path.Combine(
                    Path.GetTempPath(),
                    "MEPTools_Diagnostics.log");

                var sb = new StringBuilder();
                sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] MEPTools 启动诊断");
                sb.AppendLine($"Revit 版本 : {_app.ControlledApplication.VersionNumber}");
                sb.AppendLine($"程序集路径 : {Assembly.GetExecutingAssembly().Location}");
                sb.AppendLine(new string('-', 64));
                foreach (var line in _diagnostics) sb.AppendLine(line);

                File.WriteAllText(logPath, sb.ToString(), Encoding.UTF8);

                // 如需在开发时看到弹窗，取消下面这行注释即可
                // TaskDialog.Show("MEPTools 启动诊断", string.Join("\n", _diagnostics));
            }
            catch
            {
                // 日志写入失败不影响主流程
            }
        }

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
            if (config.CommandType == null) return;

            // 【核心修复】name 使用 CommandType.FullName，
            // 保证 Revit 生成的 CommandId 末尾始终是 "MEPTools.XXX.Command"。
            // 旧 KeyboardShortcuts.xml 无需修改即可继续生效。
            string fullClassName = config.CommandType.FullName;
            string buttonName = fullClassName; // ← 与旧 XML 保持一致

            try
            {
                PushButtonData data = new PushButtonData(buttonName, config.Text, asmPath, fullClassName);
                PushButton btn = pulldown.AddPushButton(data);
                if (btn != null)
                {
                    btn.ToolTip = config.ToolTip;
                    ApplyIcon(btn, config.Text);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MEPTools] AddSubButton 失败：\"{config.Text}\" ({fullClassName}) - {ex.Message}");
            }
        }

        private static void AddPushButton(RibbonPanel panel, string asmPath, ButtonConfig config)
        {
            if (config.CommandType == null) return;

            // 【核心修复】同 AddSubButton，name 用 FullName
            string fullClassName = config.CommandType.FullName;
            string buttonName = fullClassName;

            try
            {
                PushButtonData data = new PushButtonData(buttonName, config.Text, asmPath, fullClassName);
                PushButton btn = panel.AddItem(data) as PushButton;
                if (btn != null)
                {
                    btn.ToolTip = config.ToolTip;
                    ApplyIcon(btn, config.Text);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MEPTools] AddPushButton 失败：\"{config.Text}\" ({fullClassName}) - {ex.Message}");
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