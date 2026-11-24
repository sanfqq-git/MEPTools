using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.ExportTools.Common;
using MEPTools.Shared;

namespace MEPTools.ExportTools.ExportSheet
{
    /// <summary>
    /// 撤销图纸信息导入：按导入时落盘的「回滚快照」或「变更日志」，把名称/编号按主键改回导入前。
    ///
    /// 为什么能保证回退：
    ///  - 导入只改两个字符串属性（Name / SheetNumber），从不删除或新建元素，所以回退就是「改回去」，天然可逆；
    ///  - 凭据是导入事务之前落盘的（write-ahead），不依赖 Revit 的 Undo，关掉 Revit 之后也能撤；
    ///  - 逐字段判断「当前值 == 导入后的值」才回退，否则跳过并说明（不覆盖别人后来的修改）。
    ///
    /// 兼容两种凭据：
    ///  - 新版：图纸信息回滚快照_*.csv（主键/旧编号/旧名称/新编号/新名称）
    ///  - 老版（11-19）：图纸信息导入_变更日志_*.txt（[名称]/[编号] 行）
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class SheetInfoRollbackCommand : IExternalCommand
    {
        private const int MaxPreviewRows = 15;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            Document doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null) return Result.Cancelled;

            MEPLogger.Instance.StartSession("撤销图纸信息导入");

            try
            {
                string path = PickSnapshot();
                if (string.IsNullOrWhiteSpace(path)) return Result.Cancelled;

                List<SheetInfoSafety.SnapshotRow> rows;
                try
                {
                    rows = LoadBackupFile(path);
                }
                catch (Exception ex)
                {
                    MEPLogger.Instance.LogException("SheetInfoRollback/Load", ex);
                    TaskDialog.Show("撤销图纸导入", "回退凭据读取失败：\n" + path + "\n\n" + ex.Message);
                    return Result.Cancelled;
                }

                if (rows == null || rows.Count == 0)
                {
                    TaskDialog.Show("撤销图纸导入",
                        "这份文件里没有可用的回退记录。\n\n" + path +
                        "\n\n快照需要「主键 / 旧名称 / 新名称」列；变更日志需要 11-19 版导入写出的格式。");
                    return Result.Cancelled;
                }

                // ---------- 逐行判定：当前值 == 导入后的值 才回退 ----------
                List<UndoItem> todo = new List<UndoItem>();
                List<string> notes = new List<string>();

                for (int i = 0; i < rows.Count; i++)
                {
                    SheetInfoSafety.SnapshotRow row = rows[i];
                    int rowNo = i + 2;

                    int idValue;
                    if (!int.TryParse((row.Key ?? string.Empty).Trim(), out idValue))
                    {
                        notes.Add("第 " + rowNo + " 行：主键无效「" + row.Key + "」");
                        continue;
                    }

                    ViewSheet sheet = doc.GetElement(new ElementId(idValue)) as ViewSheet;
                    if (sheet == null)
                    {
                        notes.Add("第 " + rowNo + " 行：主键 " + idValue + " 已不是图纸（可能已被删除，Revit 无法无痕恢复删除）");
                        continue;
                    }

                    UndoItem item = new UndoItem { Sheet = sheet, RowIndex = rowNo };

                    // 名称
                    string curName = sheet.Name;
                    if (string.IsNullOrWhiteSpace(row.OldName))
                    {
                        // 凭据里没有旧名称，无法回退名称
                    }
                    else if (string.Equals(curName, row.OldName, StringComparison.Ordinal))
                    {
                        // 已经是导入前的值，无需处理
                    }
                    else if (string.Equals(curName, row.NewName, StringComparison.Ordinal))
                    {
                        item.NameAction = true;
                        item.NameFrom = curName;
                        item.NameTo = row.OldName;
                    }
                    else
                    {
                        notes.Add("第 " + rowNo + " 行：名称现在是「" + curName + "」，既不是导入后「" + row.NewName +
                                  "」也不是导入前「" + row.OldName + "」，可能之后被改过 → 跳过名称");
                    }

                    // 编号
                    string curNumber = sheet.SheetNumber;
                    if (string.IsNullOrWhiteSpace(row.OldNumber))
                    {
                        // 凭据里没有旧编号
                    }
                    else if (string.Equals(curNumber, row.OldNumber, StringComparison.Ordinal))
                    {
                        // 已经是导入前的值
                    }
                    else if (string.Equals(curNumber, row.NewNumber, StringComparison.Ordinal))
                    {
                        item.NumberAction = true;
                        item.NumberFrom = curNumber;
                        item.NumberTo = row.OldNumber;
                    }
                    else
                    {
                        notes.Add("第 " + rowNo + " 行：编号现在是「" + curNumber + "」，既不是导入后「" + row.NewNumber +
                                  "」也不是导入前「" + row.OldNumber + "」，可能之后被改过 → 跳过编号");
                    }

                    // 文本参数（只有新版快照/新版日志才有；老凭据这一列为空 → 不动参数）
                    List<SheetInfoParams.ParamChange> stored = SheetInfoParams.DecodeParamChanges(row.Params);
                    foreach (SheetInfoParams.ParamChange param in stored)
                    {
                        string curParam = SheetInfoParams.GetValue(sheet, param.Name);
                        if (curParam == null)
                        {
                            notes.Add("第 " + rowNo + " 行：参数「" + param.Name + "」在模型里已不存在（可能被删列/改名）→ 跳过");
                            continue;
                        }
                        if (string.Equals(curParam, param.OldValue, StringComparison.Ordinal))
                        {
                            // 已经是导入前的值，无需处理
                        }
                        else if (string.Equals(curParam, param.NewValue, StringComparison.Ordinal))
                        {
                            item.Params.Add(new SheetInfoParams.ParamChange
                            {
                                Name = param.Name,
                                OldValue = curParam,        // 现值（导入后）
                                NewValue = param.OldValue   // 目标值（导入前）
                            });
                        }
                        else
                        {
                            notes.Add("第 " + rowNo + " 行：参数「" + param.Name + "」现在是「" + SheetInfoSafety.Shorten(curParam, 20) +
                                      "」，既不是导入后「" + SheetInfoSafety.Shorten(param.NewValue, 20) +
                                      "」也不是导入前「" + SheetInfoSafety.Shorten(param.OldValue, 20) + "」→ 跳过");
                        }
                    }

                    if (item.NameAction || item.NumberAction || item.Params.Count > 0) todo.Add(item);
                }

                if (todo.Count == 0)
                {
                    TaskDialog dialog = new TaskDialog("撤销图纸导入");
                    dialog.MainInstruction = "没有可回退的行";
                    dialog.MainContent = "可能已经回退过，或这些图纸在导入之后又被改过。\n\n" +
                                         (notes.Count > 0 ? SheetInfoSafety.FormatLines(notes, MaxPreviewRows) : string.Empty);
                    dialog.Show();
                    return Result.Cancelled;
                }

                // ---------- 编号回退的唯一性预检（名称不做预检，按行尝试） ----------
                HashSet<ElementId> touched = new HashSet<ElementId>();
                foreach (UndoItem item in todo) touched.Add(item.Sheet.Id);

                HashSet<string> occupiedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> occupiedNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ViewSheet sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsTemplate))
                {
                    if (touched.Contains(sheet.Id)) continue;
                    occupiedNames.Add(sheet.Name);
                    occupiedNumbers.Add(sheet.SheetNumber);
                }

                List<UndoItem> finalTodo = new List<UndoItem>();
                foreach (UndoItem item in todo)
                {
                    if (item.NumberAction && occupiedNumbers.Contains(item.NumberTo))
                    {
                        notes.Add("第 " + item.RowIndex + " 行：编号「" + item.NumberTo + "」现在被其它图纸占用，跳过编号回退");
                        item.NumberAction = false;
                    }
                    if (item.NameAction && occupiedNames.Contains(item.NameTo))
                    {
                        // 名称不阻断：本模型本来就可能存在同名图纸，先按行尝试，失败会逐条报出来
                        item.NameOccupiedHint = true;
                    }
                    if (item.NameAction || item.NumberAction || item.Params.Count > 0) finalTodo.Add(item);
                }

                if (finalTodo.Count == 0)
                {
                    TaskDialog dialog = new TaskDialog("撤销图纸导入");
                    dialog.MainInstruction = "没有可安全回退的行";
                    dialog.MainContent = SheetInfoSafety.FormatLines(notes, MaxPreviewRows);
                    dialog.Show();
                    return Result.Cancelled;
                }

                // ---------- 回退前预览 ----------
                if (!ConfirmRollback(finalTodo, notes, path)) return Result.Cancelled;

                // ---------- 撤销前再落一份快照（撤销也能再撤销，即“重做”） ----------
                string folder = Path.GetDirectoryName(path) ?? LinkSelectionForm.GetDefaultOutputFolder();
                List<SheetInfoSafety.SnapshotRow> reverseRows = new List<SheetInfoSafety.SnapshotRow>();
                foreach (UndoItem item in finalTodo)
                {
                    reverseRows.Add(new SheetInfoSafety.SnapshotRow
                    {
                        Key = item.Sheet.Id.IntegerValue.ToString(),
                        OldNumber = item.Sheet.SheetNumber,
                        OldName = item.Sheet.Name,
                        NewNumber = item.NumberAction ? item.NumberTo : item.Sheet.SheetNumber,
                        NewName = item.NameAction ? item.NameTo : item.Sheet.Name,
                        Params = SheetInfoParams.EncodeParamChanges(item.Params)
                    });
                }

                string reversePath = SheetInfoSafety.WriteSnapshot(folder, reverseRows, "撤销前快照_" + Path.GetFileName(path));
                if (reversePath == null)
                {
                    TaskDialog.Show("撤销图纸导入",
                        "撤销前的快照写不进磁盘：" + folder + "\n\n为保证可再次回退，本次撤销已中止，模型未做任何修改。");
                    return Result.Cancelled;
                }

                // ---------- 执行回退 ----------
                ImportChangeLog log = new ImportChangeLog(folder, "图纸信息撤销");

                int renamed = 0, renumbered = 0, paramsRestored = 0;
                using (Transaction transaction = new Transaction(doc, "撤销图纸信息导入"))
                {
                    transaction.Start();
                    foreach (UndoItem item in finalTodo)
                    {
                        if (item.NameAction)
                        {
                            try
                            {
                                item.Sheet.Name = item.NameTo;
                                renamed++;
                                log.Log("[名称回退] 主键=" + item.Sheet.Id.IntegerValue + " 旧(导入后)=" + item.NameFrom +
                                        " 新(导入前)=" + item.NameTo + " OK");
                            }
                            catch (Exception ex)
                            {
                                notes.Add("第 " + item.RowIndex + " 行：名称回退失败（当前「" + item.NameFrom + "」→「" +
                                          item.NameTo + "」）：" + ex.Message);
                                log.Log("[名称回退] 主键=" + item.Sheet.Id.IntegerValue + " 失败：" + ex.Message);
                            }
                        }

                        if (item.NumberAction)
                        {
                            try
                            {
                                item.Sheet.SheetNumber = item.NumberTo;
                                renumbered++;
                                log.Log("[编号回退] 主键=" + item.Sheet.Id.IntegerValue + " 旧(导入后)=" + item.NumberFrom +
                                        " 新(导入前)=" + item.NumberTo + " OK");
                            }
                            catch (Exception ex)
                            {
                                notes.Add("第 " + item.RowIndex + " 行：编号回退失败（当前「" + item.NumberFrom + "」→「" +
                                          item.NumberTo + "」）：" + ex.Message);
                                log.Log("[编号回退] 主键=" + item.Sheet.Id.IntegerValue + " 失败：" + ex.Message);
                            }
                        }

                        foreach (SheetInfoParams.ParamChange param in item.Params)
                        {
                            string error = SheetInfoParams.SetValue(item.Sheet, param.Name, param.NewValue);
                            if (error == null)
                            {
                                paramsRestored++;
                                log.Log("[参数回退] 主键=" + item.Sheet.Id.IntegerValue + " 名=" + param.Name +
                                        " 旧(导入后)=" + param.OldValue + " 新(导入前)=" + param.NewValue + " OK");
                            }
                            else
                            {
                                notes.Add("第 " + item.RowIndex + " 行：" + error);
                                log.Log("[参数回退] 主键=" + item.Sheet.Id.IntegerValue + " 名=" + param.Name + " 失败：" + error);
                            }
                        }
                    }
                    transaction.Commit();
                }

                bool logSaved = log.SaveIfAny();
                MEPLogger.Instance.EndSession("Done: RollbackName=" + renamed + " RollbackNumber=" + renumbered +
                                              " RollbackParams=" + paramsRestored + " ReverseSnapshot=" + reversePath);

                TaskDialog result = new TaskDialog("撤销图纸导入");
                result.MainInstruction = "撤销完成";
                result.MainContent =
                    "名称回退 " + renamed + " 张，编号回退 " + renumbered + " 张" +
                    (paramsRestored > 0 ? "，参数恢复 " + paramsRestored + " 处" : string.Empty) +
                    (notes.Count > 0 ? "\n\n跳过/失败 " + notes.Count + " 条：\n" + SheetInfoSafety.FormatLines(notes, MaxPreviewRows) : string.Empty) +
                    (logSaved ? "\n\n变更日志：" + log.Path : string.Empty) +
                    "\n\n撤销前快照（再运行本命令选它即可撤回到“导入后”的状态）：" + reversePath;
                result.Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("SheetInfoRollback", ex);
                InfoCenterTip.Show("撤销图纸导入失败，请查看日志。");
                return Result.Failed;
            }
        }

        /// <summary>按扩展名分派：.csv = 新版快照，.txt = 11-19 版变更日志。</summary>
        private static List<SheetInfoSafety.SnapshotRow> LoadBackupFile(string path)
        {
            string ext = Path.GetExtension(path) ?? string.Empty;
            if (string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase))
                return SheetInfoSafety.ReadChangeLog(path);
            return SheetInfoSafety.ReadSnapshot(path);
        }

        private static bool ConfirmRollback(List<UndoItem> todo, List<string> notes, string snapshotPath)
        {
            int nameCount = 0, numberCount = 0, paramCount = 0, shown = 0;
            StringBuilder sb = new StringBuilder();

            foreach (UndoItem item in todo)
            {
                if (item.NameAction) nameCount++;
                if (item.NumberAction) numberCount++;
                paramCount += item.Params.Count;

                StringBuilder line = new StringBuilder();
                line.Append("第 ").Append(item.RowIndex).Append(" 行  主键 ").Append(item.Sheet.Id.IntegerValue);
                if (item.NumberAction) line.Append("  |  编号 ").Append(item.NumberFrom).Append(" → ").Append(item.NumberTo);
                if (item.NameAction)
                {
                    line.Append("  |  名称 ").Append(item.NameFrom).Append(" → ").Append(item.NameTo);
                    if (item.NameOccupiedHint) line.Append("（目标名已存在，可能被 Revit 拒绝）");
                }
                if (item.Params.Count > 0) line.Append("  |  参数 ").Append(item.Params.Count).Append(" 处");

                MEPLogger.Instance.AppendLine("[回退] " + line);
                foreach (SheetInfoParams.ParamChange param in item.Params)
                {
                    MEPLogger.Instance.AppendLine("[回退]     参数 " + param.Name + "：" +
                                                  SheetInfoSafety.Shorten(param.OldValue, 24) + " → " +
                                                  SheetInfoSafety.Shorten(param.NewValue, 24));
                }

                if (shown < MaxPreviewRows)
                {
                    sb.AppendLine(line.ToString());
                    shown++;
                }
            }
            if (todo.Count > shown) sb.AppendLine("… 其余 " + (todo.Count - shown) + " 行已写入日志");

            if (notes.Count > 0)
            {
                foreach (string note in notes) MEPLogger.Instance.AppendLine("[跳过] " + note);
                sb.AppendLine();
                sb.AppendLine("另有 " + notes.Count + " 行会被跳过：");
                sb.AppendLine(SheetInfoSafety.FormatLines(notes, 10));
            }

            sb.AppendLine();
            sb.AppendLine("凭据：" + snapshotPath);

            TaskDialog dialog = new TaskDialog("撤销图纸导入");
            dialog.MainInstruction = "确认回退 " + todo.Count + " 张图纸？（名称 " + nameCount + " 张 / 编号 " +
                                     numberCount + " 张 / 参数 " + paramCount + " 处）";
            dialog.MainContent = sb.ToString();
            dialog.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
            dialog.DefaultButton = TaskDialogResult.No;

            return dialog.Show() == TaskDialogResult.Yes;
        }

        /// <summary>默认定位到最近一份快照/变更日志，但仍可自己挑别的。</summary>
        private static string PickSnapshot()
        {
            string lastFolder = SheetInfoSafety.GetLastImportFolder();
            string baseFolder = !string.IsNullOrWhiteSpace(lastFolder)
                ? lastFolder
                : LinkSelectionForm.GetDefaultOutputFolder();

            string preset = SheetInfoSafety.FindNewestArtifact(baseFolder, 2);
            if (string.IsNullOrEmpty(preset) && !string.IsNullOrWhiteSpace(lastFolder))
                preset = SheetInfoSafety.FindNewestArtifact(LinkSelectionForm.GetDefaultOutputFolder(), 2);

            string initialDir = preset != null ? Path.GetDirectoryName(preset) : baseFolder;

            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "选择要撤销的导入凭据（回滚快照 .csv 或 变更日志 .txt）";
                dialog.Filter = "回退凭据 (*.csv;*.txt)|*.csv;*.txt|回滚快照 (*.csv)|*.csv|变更日志 (*.txt)|*.txt|所有文件 (*.*)|*.*";
                if (!string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir))
                    dialog.InitialDirectory = initialDir;
                if (preset != null) dialog.FileName = Path.GetFileName(preset);
                return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
            }
        }

        private class UndoItem
        {
            public ViewSheet Sheet { get; set; }
            public int RowIndex { get; set; }

            public bool NameAction { get; set; }
            public string NameFrom { get; set; }
            public string NameTo { get; set; }
            public bool NameOccupiedHint { get; set; }

            public bool NumberAction { get; set; }
            public string NumberFrom { get; set; }
            public string NumberTo { get; set; }

            public List<SheetInfoParams.ParamChange> Params { get; private set; }

            public UndoItem()
            {
                Params = new List<SheetInfoParams.ParamChange>();
            }
        }
    }
}
