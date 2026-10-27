using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using TextBox = System.Windows.Controls.TextBox;

namespace MEPTools.RevitElevationRenamer
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Command : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // 1. 读取用户在 Revit 中预选的元素
                ICollection<ElementId> preselectedIds = uidoc.Selection.GetElementIds();
                if (preselectedIds.Count == 0)
                {
                    TaskDialog.Show("提示",
                        "请先在 Revit 中选中要重命名的视图（可框选多个），再运行本插件。");
                    return Result.Cancelled;
                }

                // 2. 预先收集所有 View
                List<View> allViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .ToList();

                // 3. 从预选元素中提取真正的 ViewSection
                HashSet<ElementId> viewIds = new HashSet<ElementId>();
                List<ViewSection> selectedViews = new List<ViewSection>();

                foreach (ElementId eid in preselectedIds)
                {
                    Element elem = doc.GetElement(eid);
                    if (elem == null) continue;

                    // 尝试把当前元素直接当 ViewSection
                    if (elem is ViewSection vsDirect && !vsDirect.IsTemplate && viewIds.Add(vsDirect.Id))
                    {
                        selectedViews.Add(vsDirect);
                        continue;
                    }

                    // 按 Name 反查真正的 ViewSection（当前验证有效的方法）
                    if (!string.IsNullOrEmpty(elem.Name))
                    {
                        View matched = allViews.FirstOrDefault(v => v.Name == elem.Name);
                        if (matched is ViewSection vs && !vs.IsTemplate && viewIds.Add(vs.Id))
                        {
                            selectedViews.Add(vs);
                            continue;
                        }
                    }

                    // 兜底：按 ElementId 反查（保留以备不时之需）
                    View matchedById = allViews.FirstOrDefault(v => v.Id.IntegerValue == elem.Id.IntegerValue);
                    if (matchedById is ViewSection vsById && !vsById.IsTemplate && viewIds.Add(vsById.Id))
                    {
                        selectedViews.Add(vsById);
                    }
                }

                if (selectedViews.Count == 0)
                {
                    TaskDialog.Show("提示", "未从预选元素中识别到任何有效的立面视图。");
                    return Result.Cancelled;
                }

                // 4. 提示用户输入基础名称（纯 C# WPF 输入框）
                string baseName = ShowInputDialog(
                    "视图重命名",
                    $"已识别 {selectedViews.Count} 个立面视图。\n请输入基础名称（例如：EL Rm_5）：",
                    "EL Rm_5");
                if (string.IsNullOrWhiteSpace(baseName)) return Result.Cancelled;
                baseName = baseName.Trim();

                // 5. 按顺时针排序（ViewDirection 的方位角）
                selectedViews.Sort((v1, v2) =>
                    GetClockwiseAngle(v1.ViewDirection).CompareTo(GetClockwiseAngle(v2.ViewDirection)));

                // 6. 去重并计算最终名称
                List<string> finalNames = GenerateUniqueNames(doc, baseName, selectedViews.Count);

                // 7. 执行重命名（两阶段法，防止冲突）
                using (Transaction tx = new Transaction(doc, "自动重命名立面视图"))
                {
                    tx.Start();

                    // 阶段一：先改为临时名称
                    foreach (var view in selectedViews)
                    {
                        view.Name = "temp_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    }

                    // 阶段二：改为最终名称
                    for (int i = 0; i < selectedViews.Count; i++)
                    {
                        selectedViews[i].Name = finalNames[i];
                    }

                    tx.Commit();
                }

                // 8. 输出结果
                string resultList = string.Join("\n", finalNames.Select((n, idx) => $"{idx + 1}. {n}"));
                TaskDialog.Show("成功",
                    $"已成功重命名 {selectedViews.Count} 个立面视图：\n\n{resultList}");
                return Result.Succeeded;
            }
            catch (OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        /// <summary>
        /// 纯 C# WPF 输入对话框，替代 Microsoft.VisualBasic.Interaction.InputBox。
        /// 用户取消或输入为空时返回 null。
        /// </summary>
        private string ShowInputDialog(string title, string tipText, string defaultValue)
        {
            Window window = new Window
            {
                Title = title,
                Width = 460,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true
            };

            StackPanel panel = new StackPanel { Margin = new Thickness(15) };

            TextBlock txtTip = new TextBlock
            {
                Text = tipText + "\n回车确认 | ESC取消",
                Margin = new Thickness(0, 0, 0, 12),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };

            TextBox txtInput = new TextBox
            {
                Text = defaultValue,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 15)
            };

            StackPanel btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            Button btnOk = new Button
            {
                Content = "确定",
                Width = 100,
                Margin = new Thickness(0, 0, 10, 0),
                IsDefault = true
            };

            Button btnCancel = new Button
            {
                Content = "取消",
                Width = 100,
                IsCancel = true
            };

            btnOk.Click += (s, e) => window.DialogResult = true;
            btnCancel.Click += (s, e) => window.DialogResult = false;

            txtInput.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    window.DialogResult = true;
                    e.Handled = true;
                }
            };

            window.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    window.DialogResult = false;
                    e.Handled = true;
                }
            };

            window.Loaded += (s, e) =>
            {
                txtInput.Focus();
                txtInput.SelectAll();
            };

            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnCancel);
            panel.Children.Add(txtTip);
            panel.Children.Add(txtInput);
            panel.Children.Add(btnPanel);
            window.Content = panel;

            bool? dialogResult = window.ShowDialog();
            if (dialogResult != true) return null;

            string input = txtInput.Text.Trim();
            return string.IsNullOrEmpty(input) ? null : input;
        }

        /// <summary>
        /// 计算顺时针角度（正北为0度，顺时针增加）
        /// </summary>
        private double GetClockwiseAngle(XYZ dir)
        {
            // Revit中 Y 轴为正北 (0, 1, 0)，X 轴为正东 (1, 0, 0)
            double angle = Math.Atan2(dir.X, dir.Y) * (180.0 / Math.PI);
            if (angle < 0) angle += 360.0;
            return angle;
        }

        /// <summary>
        /// 生成不重复的名称列表 (A, B, C...)
        /// </summary>
        private List<string> GenerateUniqueNames(Document doc, string baseName, int count)
        {
            List<string> names = new List<string>();

            // 收集当前文档中所有已有的视图名称
            HashSet<string> existingNames = new HashSet<string>(
                new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Select(v => v.Name)
            );

            int letterIndex = 0;
            for (int i = 0; i < count; i++)
            {
                string newName;
                while (true)
                {
                    char letter = (char)('A' + (letterIndex % 26));
                    string suffix = letterIndex >= 26
                        ? new string('A', letterIndex / 26) + letter
                        : letter.ToString();

                    newName = baseName + suffix;

                    if (!existingNames.Contains(newName))
                    {
                        existingNames.Add(newName);
                        letterIndex++;
                        break;
                    }
                    letterIndex++; // 冲突则自动递增字母
                }
                names.Add(newName);
            }
            return names;
        }
    }
}