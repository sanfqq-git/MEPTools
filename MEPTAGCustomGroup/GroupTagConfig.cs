using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO; 
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.Shared;

using Point = System.Drawing.Point;
using Color = System.Drawing.Color;
using Button = System.Windows.Forms.Button;
using Label = System.Windows.Forms.Label;
using Size = System.Drawing.Size;
using ComboBox = System.Windows.Forms.ComboBox;
using TextBox = System.Windows.Forms.TextBox;
using Control = System.Windows.Forms.Control; 
using Form = System.Windows.Forms.Form; 
using ListBox = System.Windows.Forms.ListBox; 
using OpenFileDialog = System.Windows.Forms.OpenFileDialog;
using SaveFileDialog = System.Windows.Forms.SaveFileDialog;

namespace MEPTools.MEPGroupTag
{
    public class ExtraRowRule
    {
        public string GroupIndex { get; set; } 
        public string TagText { get; set; }    
        public string DuctTagSymName { get; set; } 
        public string PipeTagSymName { get; set; } 
        public string CableTrayTagSymName { get; set; } 
    }

    /// <summary>
    /// Tag 族类型名匹配器：把配置里的 "族名: 类型名" 与当前文档实际的族类型键做比对。
    /// 容忍：大小写、各类空白（含 Tab / 全角空格 / NBSP）、零宽字符、全角冒号；
    /// 允许只写族名或只写类型名；族名命中但类型改名时，回退到该族第一个类型。
    /// </summary>
    public static class TagTypeNameMatcher
    {
        // 去掉所有空白与零宽字符，全角冒号统一为半角，转小写
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var kept = new char[s.Length];
            int n = 0;
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c)) continue;
                if (c == '\u200B' || c == '\u200C' || c == '\u200D' || c == '\uFEFF') continue;
                kept[n++] = (c == '：') ? ':' : c;
            }
            return new string(kept, 0, n).ToLowerInvariant();
        }

        // 按第一个冒号拆成 族名 / 类型名
        public static bool TrySplit(string s, out string family, out string type)
        {
            family = "";
            type = "";
            if (string.IsNullOrEmpty(s)) return false;
            int idx = s.IndexOf(':');
            if (idx < 0) idx = s.IndexOf('：');
            if (idx < 0) return false;
            family = s.Substring(0, idx).Trim();
            type = s.Substring(idx + 1).Trim();
            return family.Length > 0;
        }

        // 在候选键集合里找最合适的那个；找不到返回 null，matchNote 说明匹配方式
        public static string Resolve(IEnumerable<string> keys, string input, out string matchNote)
        {
            matchNote = "";
            if (keys == null || string.IsNullOrWhiteSpace(input)) return null;

            string trimmed = input.Trim();

            // ① 原始字符串完全一致
            foreach (string key in keys)
            {
                if (string.Equals(key, trimmed, StringComparison.Ordinal)) return key;
            }

            // ② 规范化后完全一致（空格 / 大小写 / 全角冒号差异）
            string normInput = Normalize(trimmed);
            foreach (string key in keys)
            {
                if (Normalize(key) == normInput)
                {
                    matchNote = "（已忽略空格/大小写差异）";
                    return key;
                }
            }

            string inFamily, inType;
            bool hasSplit = TrySplit(trimmed, out inFamily, out inType);
            string nFamily = hasSplit ? Normalize(inFamily) : normInput;
            string nType = hasSplit ? Normalize(inType) : "";

            // ③ 族名与类型都对上（规范化后）
            if (hasSplit && nType.Length > 0)
            {
                foreach (string key in keys)
                {
                    string kFamily, kType;
                    if (!TrySplit(key, out kFamily, out kType)) continue;
                    if (Normalize(kFamily) == nFamily && Normalize(kType) == nType)
                    {
                        matchNote = "（已忽略空格/大小写差异）";
                        return key;
                    }
                }
            }

            // ④ 族名对上、类型对不上 → 用该族第一个类型（调用方需提示核对）
            if (hasSplit)
            {
                foreach (string key in keys)
                {
                    string kFamily, kType;
                    if (!TrySplit(key, out kFamily, out kType)) continue;
                    if (Normalize(kFamily) == nFamily)
                    {
                        matchNote = $"（族名命中，类型“{inType}”在当前模型中不存在，已改用该族第一个类型“{kType}”，请核对）";
                        return key;
                    }
                }
            }

            // ⑤ 只写了族名或只写了类型名
            foreach (string key in keys)
            {
                string kFamily, kType;
                if (!TrySplit(key, out kFamily, out kType)) continue;
                if (Normalize(kFamily) == normInput || Normalize(kType) == normInput)
                {
                    matchNote = "（按族名/类型名单独匹配）";
                    return key;
                }
            }

            return null;
        }
    }

    public class GroupTagConfig
    {
        public double GroupToleranceMM { get; set; } = 25.0;
        public double LineSpacingMM { get; set; } = 200.0;
        public int BlankLines { get; set; } = 2;

        // 主行 Tag 族（所有高度组共用）。格式 "族名: 类型名"，留空表示沿用视图内已有 Tag 的类型
        public string MainDuctTagSymName { get; set; } = "";
        public string MainPipeTagSymName { get; set; } = "";
        public string MainTrayTagSymName { get; set; } = "";

        public List<ExtraRowRule> ExtraRules { get; set; } = new List<ExtraRowRule>();

        public static GroupTagConfig Load()
        {
            var cfg = new GroupTagConfig();
            
            cfg.GroupToleranceMM = MEPConfig.GetDouble("TagGroup.Tolerance", 25.0);
            cfg.LineSpacingMM = MEPConfig.GetDouble("TagGroup.LineSpacing", 200.0);
            cfg.BlankLines = MEPConfig.GetInt("TagGroup.BlankLines", 2);
            // 组间空行允许为 0：0 表示组与组之间紧挨排版，是否留空由用户在设置界面控制，这里不再强制下限。
            if (cfg.BlankLines < 0) cfg.BlankLines = 0;

            cfg.MainDuctTagSymName = MEPConfig.GetString("TagGroup.MainDuctTag", "");
            cfg.MainPipeTagSymName = MEPConfig.GetString("TagGroup.MainPipeTag", "");
            cfg.MainTrayTagSymName = MEPConfig.GetString("TagGroup.MainTrayTag", "");

            string rulesStr = MEPConfig.GetString("TagGroup.ExtraRules", "");
            if (!string.IsNullOrWhiteSpace(rulesStr))
            {
                var items = rulesStr.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var item in items)
                {
                    var parts = item.Split(';');
                    if (parts.Length >= 5)
                    {
                        cfg.ExtraRules.Add(new ExtraRowRule 
                        { 
                            GroupIndex = parts[0], 
                            TagText = parts[1], 
                            DuctTagSymName = parts[2],
                            PipeTagSymName = parts[3],
                            CableTrayTagSymName = parts[4]
                        });
                    }
                }
            }
            return cfg;
        }

        public void Save()
        {
            MEPConfig.SetValue("TagGroup.Tolerance", GroupToleranceMM.ToString());
            MEPConfig.SetValue("TagGroup.LineSpacing", LineSpacingMM.ToString());
            MEPConfig.SetValue("TagGroup.BlankLines", BlankLines.ToString());

            MEPConfig.SetValue("TagGroup.MainDuctTag", MainDuctTagSymName ?? "");
            MEPConfig.SetValue("TagGroup.MainPipeTag", MainPipeTagSymName ?? "");
            MEPConfig.SetValue("TagGroup.MainTrayTag", MainTrayTagSymName ?? "");

            var ruleItems = ExtraRules.Select(r => $"{r.GroupIndex};{r.TagText};{r.DuctTagSymName};{r.PipeTagSymName};{r.CableTrayTagSymName}");
            MEPConfig.SetValue("TagGroup.ExtraRules", string.Join("|", ruleItems));
            
            MEPConfig.Save();
        }

        public void SaveToFile(string path)
        {
            var lines = new List<string>
            {
                $"Tolerance={GroupToleranceMM}",
                $"LineSpacing={LineSpacingMM}",
                $"BlankLines={BlankLines}",
                $"MainDuctTag={MainDuctTagSymName}",
                $"MainPipeTag={MainPipeTagSymName}",
                $"MainTrayTag={MainTrayTagSymName}"
            };
            var ruleItems = ExtraRules.Select(r => $"{r.GroupIndex};{r.TagText};{r.DuctTagSymName};{r.PipeTagSymName};{r.CableTrayTagSymName}");
            lines.Add($"ExtraRules={string.Join("|", ruleItems)}");
            File.WriteAllLines(path, lines);
        }

        public static GroupTagConfig LoadFromFile(string path)
        {
            var cfg = new GroupTagConfig();
            if (!File.Exists(path)) return cfg;

            var lines = File.ReadAllLines(path);
            foreach (var line in lines)
            {
                var parts = line.Split(new[] { '=' }, 2);
                if (parts.Length != 2) continue;
                string key = parts[0].Trim();
                string val = parts[1].Trim();

                if (key == "Tolerance" && double.TryParse(val, out double t)) cfg.GroupToleranceMM = t;
                else if (key == "LineSpacing" && double.TryParse(val, out double ls)) cfg.LineSpacingMM = ls;
                else if (key == "BlankLines" && int.TryParse(val, out int bl)) cfg.BlankLines = bl;
                else if (key == "MainDuctTag") cfg.MainDuctTagSymName = val;
                else if (key == "MainPipeTag") cfg.MainPipeTagSymName = val;
                else if (key == "MainTrayTag") cfg.MainTrayTagSymName = val;
                else if (key == "ExtraRules")
                {
                    var items = val.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var item in items)
                    {
                        var rParts = item.Split(';');
                        if (rParts.Length >= 5)
                        {
                            cfg.ExtraRules.Add(new ExtraRowRule 
                            { 
                                GroupIndex = rParts[0], 
                                TagText = rParts[1], 
                                DuctTagSymName = rParts[2],
                                PipeTagSymName = rParts[3],
                                CableTrayTagSymName = rParts[4]
                            });
                        }
                    }
                }
            }
            // 与 Load() 保持一致：组间空行允许为 0，仅拦截负值
            if (cfg.BlankLines < 0) cfg.BlankLines = 0;
            return cfg;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class Cmd_TagGroupSettings : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument.Document;
            var config = GroupTagConfig.Load();
            using (var form = new GroupTagConfigForm(doc, config))
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    InfoCenterTip.Show("TAG 排版与附加行设置已保存。");
                }
            }
            return Result.Succeeded;
        }
    }

    public class GroupTagConfigForm : SpaceSubmitForm
    {
        private Document _doc;
        private GroupTagConfig _config;
        
        private NumericUpDown nudTolerance, nudLineSpacing, nudBlankLines;
        private DataGridView dgvExtra;
        private TextBox txtMainDuct, txtMainPipe, txtMainTray;
        private Button btnOk, btnImport, btnExport, btnClear;
        private Button btnInsert, btnUp, btnDown, btnDeleteRow; // 👈 新增删除按钮

        private List<string> _ductTagsAll = new List<string>();
        private List<string> _pipeTagsAll = new List<string>();
        private List<string> _trayTagsAll = new List<string>();

        public GroupTagConfigForm(Document doc, GroupTagConfig config)
        {
            _doc = doc;
            _config = config;
            InitializeUI();
            LoadData();
        }

        private void InitializeUI()
        {
            this.Text = "高度分组 TAG - 规则设置";
            this.ClientSize = new Size(950, 620); 
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MaximizeBox = true;
            this.MinimumSize = new Size(800, 450);
            this.Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular, GraphicsUnit.Point, 134);

            int y = 20, gap = 35;
            int labelX = 20, inputX = 140;

            var lblTol = new Label { Text = "分组容差(mm):", Location = new Point(labelX, y + 3), AutoSize = true };
            nudTolerance = new NumericUpDown { Location = new Point(inputX, y), Width = 80, Minimum = 1, Maximum = 1000, DecimalPlaces = 1 };
            this.Controls.Add(lblTol); this.Controls.Add(nudTolerance); 

            var lblSpacing = new Label { Text = "单行行距(mm):", Location = new Point(250, y + 3), AutoSize = true };
            nudLineSpacing = new NumericUpDown { Location = new Point(360, y), Width = 80, Minimum = 1, Maximum = 2000, DecimalPlaces = 1 };
            this.Controls.Add(lblSpacing); this.Controls.Add(nudLineSpacing);

            var lblBlank = new Label { Text = "组间空行数:", Location = new Point(480, y + 3), AutoSize = true };
            // 下限 0：0 表示组间紧挨排版，是否留空由用户控制
            nudBlankLines = new NumericUpDown { Location = new Point(570, y), Width = 80, Minimum = 0, Maximum = 10 };
            this.Controls.Add(lblBlank); this.Controls.Add(nudBlankLines);
            y += gap;

            // 主行 Tag 族：所有高度组共用；留空 = 沿用视图内已有 Tag 的类型
            var lblMainDuct = new Label { Text = "主行 DUCT Tag 族:", Location = new Point(labelX, y + 3), AutoSize = true };
            txtMainDuct = new TextBox { Location = new Point(labelX + 120, y), Width = 190 };
            txtMainDuct.DoubleClick += (s, e) => PickTagSymbol(txtMainDuct, _ductTagsAll);
            this.Controls.Add(lblMainDuct); this.Controls.Add(txtMainDuct);

            var lblMainPipe = new Label { Text = "PIPE 族:", Location = new Point(labelX + 335, y + 3), AutoSize = true };
            txtMainPipe = new TextBox { Location = new Point(labelX + 400, y), Width = 170 };
            txtMainPipe.DoubleClick += (s, e) => PickTagSymbol(txtMainPipe, _pipeTagsAll);
            this.Controls.Add(lblMainPipe); this.Controls.Add(txtMainPipe);

            var lblMainTray = new Label { Text = "TRAY 族:", Location = new Point(labelX + 590, y + 3), AutoSize = true };
            txtMainTray = new TextBox { Location = new Point(labelX + 655, y), Width = 235 };
            txtMainTray.DoubleClick += (s, e) => PickTagSymbol(txtMainTray, _trayTagsAll);
            this.Controls.Add(lblMainTray); this.Controls.Add(txtMainTray);
            y += 28;

            var lblMainHint = new Label
            {
                Text = "留空 = 沿用视图内已有 Tag 的类型；双击输入框可搜索族和类型",
                Location = new Point(labelX, y),
                AutoSize = true,
                ForeColor = Color.Gray
            };
            this.Controls.Add(lblMainHint);
            y += 26;

            var lblExtra = new Label { Text = "组前/组后附加标签设定 (提示：双击右侧三个族列可弹出大窗口搜索):", Location = new Point(labelX, y), AutoSize = true, Font = new Font(this.Font, FontStyle.Bold), ForeColor = Color.DarkBlue };
            this.Controls.Add(lblExtra); y += 25;

            dgvExtra = new DataGridView
            {
                Location = new Point(labelX, y),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Size = new Size(910, 320), 
                AllowUserToAddRows = true,
                AllowUserToDeleteRows = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect 
            };

            typeof(DataGridView).InvokeMember("DoubleBuffered", 
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.SetProperty, 
                null, dgvExtra, new object[] { true });

            dgvExtra.DataError += (s, e) => { e.ThrowException = false; e.Cancel = true; };
            
            var colPosition = new DataGridViewComboBoxColumn { Name = "GroupIndex", HeaderText = "插入位置", FillWeight = 15, ValueType = typeof(string) };
            var positions = new List<string> { 
                "每一组前", "每一组后",
                "从左到右", "从上到下",
                "第1组前", "第1组后", "第2组前", "第2组后", "第3组前", "第3组后", "第4组前", "第4组后", "第5组前", "第5组后",
                "第6组前", "第6组后", "第7组前", "第7组后", "第8组前", "第8组后", "第9组前", "第9组后", "第10组前", "第10组后"
            };
            colPosition.Items.AddRange(positions.ToArray());
            dgvExtra.Columns.Add(colPosition);

            dgvExtra.Columns.Add(new DataGridViewTextBoxColumn { Name = "TagText", HeaderText = "生成的文字(如T1)", FillWeight = 10 });
            
            dgvExtra.Columns.Add(new DataGridViewTextBoxColumn { Name = "DuctTagSym", HeaderText = "DUCT Tag 族", FillWeight = 25 });
            dgvExtra.Columns.Add(new DataGridViewTextBoxColumn { Name = "PipeTagSym", HeaderText = "PIPE Tag 族", FillWeight = 25 });
            dgvExtra.Columns.Add(new DataGridViewTextBoxColumn { Name = "TrayTagSym", HeaderText = "CABLE TRAY Tag 族", FillWeight = 25 });
            
            dgvExtra.CellDoubleClick += DgvExtra_CellDoubleClick;

            // 👇 增加键盘 Delete 键快捷删除 👇
            dgvExtra.KeyDown += (s, e) => {
                if (e.KeyCode == Keys.Delete)
                {
                    BtnDeleteRow_Click(s, e);
                }
            };

            this.Controls.Add(dgvExtra);
            y += 330;

            btnInsert = new Button { Text = "插入新行", Location = new Point(labelX, y), Width = 80, Height = 28, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            btnInsert.Click += BtnInsert_Click;
            this.Controls.Add(btnInsert);

            btnUp = new Button { Text = "上移 ↑", Location = new Point(labelX + 85, y), Width = 70, Height = 28, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            btnUp.Click += BtnUp_Click;
            this.Controls.Add(btnUp);

            btnDown = new Button { Text = "下移 ↓", Location = new Point(labelX + 160, y), Width = 70, Height = 28, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            btnDown.Click += BtnDown_Click;
            this.Controls.Add(btnDown);

            // 👇 新增删除按钮，并调整后续按钮的 X 坐标 👇
            btnDeleteRow = new Button { Text = "删除选中行", Location = new Point(labelX + 235, y), Width = 85, Height = 28, ForeColor = Color.Red, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            btnDeleteRow.Click += BtnDeleteRow_Click;
            this.Controls.Add(btnDeleteRow);

            btnImport = new Button { Text = "输入配置", Location = new Point(labelX + 335, y), Width = 80, Height = 28, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            btnImport.Click += BtnImport_Click;
            this.Controls.Add(btnImport);

            btnExport = new Button { Text = "输出配置", Location = new Point(labelX + 425, y), Width = 80, Height = 28, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            btnExport.Click += BtnExport_Click;
            this.Controls.Add(btnExport);

            btnClear = new Button { Text = "清空配置", Location = new Point(labelX + 515, y), Width = 80, Height = 28, ForeColor = Color.Red, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            btnClear.Click += BtnClear_Click;
            this.Controls.Add(btnClear);

            btnOk = new Button { Text = "保存设置 (空格)", Location = new Point(810, y), Width = 120, Height = 28, DialogResult = DialogResult.OK, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            btnOk.Click += BtnOk_Click;
            this.AcceptButton = btnOk;
            this.Controls.Add(btnOk);

            this.Resize += (s, e) => {
                int buttonAreaHeight = 52; 
                int newY = this.ClientSize.Height - buttonAreaHeight;
                
                btnInsert.Top = newY;
                btnUp.Top = newY;
                btnDown.Top = newY;
                btnDeleteRow.Top = newY;
                btnImport.Top = newY;
                btnExport.Top = newY;
                btnClear.Top = newY;
                btnOk.Top = newY;
                
                dgvExtra.Height = newY - dgvExtra.Top - 15; 
            };
        }

        // ================= 插入、删除与排序逻辑 =================

        private void BtnInsert_Click(object sender, EventArgs e)
        {
            dgvExtra.EndEdit();
            int index = dgvExtra.CurrentRow?.Index ?? dgvExtra.Rows.Count;
            if (index >= dgvExtra.Rows.Count) index = Math.Max(0, dgvExtra.Rows.Count - 1);
            
            dgvExtra.Rows.Insert(index, new object[] { "", "", "", "", "" });
            dgvExtra.CurrentCell = dgvExtra.Rows[index].Cells[0];
        }

        private void BtnDeleteRow_Click(object sender, EventArgs e)
        {
            dgvExtra.EndEdit(); // 停止当前编辑
            
            if (dgvExtra.CurrentRow != null && !dgvExtra.CurrentRow.IsNewRow)
            {
                int index = dgvExtra.CurrentRow.Index;
                dgvExtra.Rows.RemoveAt(index);
                
                // 删完后保持选中状态
                if (dgvExtra.Rows.Count > 0)
                {
                    int nextIndex = Math.Min(index, dgvExtra.Rows.Count - 1);
                    dgvExtra.CurrentCell = dgvExtra.Rows[nextIndex].Cells[0];
                }
            }
        }

        private void BtnUp_Click(object sender, EventArgs e)
        {
            dgvExtra.EndEdit();
            if (dgvExtra.CurrentRow == null || dgvExtra.CurrentRow.Index <= 0) return;
            
            int index = dgvExtra.CurrentRow.Index;
            SwapRows(index, index - 1);
            dgvExtra.CurrentCell = dgvExtra.Rows[index - 1].Cells[0];
        }

        private void BtnDown_Click(object sender, EventArgs e)
        {
            dgvExtra.EndEdit();
            if (dgvExtra.CurrentRow == null || dgvExtra.CurrentRow.Index >= dgvExtra.Rows.Count - 1) return;
            if (dgvExtra.CurrentRow.IsNewRow) return; 
            
            int index = dgvExtra.CurrentRow.Index;
            SwapRows(index, index + 1);
            dgvExtra.CurrentCell = dgvExtra.Rows[index + 1].Cells[0];
        }

        private void SwapRows(int row1, int row2)
        {
            if (row1 < 0 || row2 < 0 || row1 >= dgvExtra.Rows.Count || row2 >= dgvExtra.Rows.Count) return;

            for (int i = 0; i < dgvExtra.Columns.Count; i++)
            {
                object temp = dgvExtra.Rows[row1].Cells[i].Value;
                dgvExtra.Rows[row1].Cells[i].Value = dgvExtra.Rows[row2].Cells[i].Value;
                dgvExtra.Rows[row2].Cells[i].Value = temp;
            }
        }

        // ================= 其他原有逻辑 =================

        // 把配置里的族名规范化为当前文档精确的"族名: 类型名"；对不上则原样保留并记入 unmatched
        private static string CanonicalTagName(List<string> pool, string value, string label, List<string> unmatched)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string note;
            string key = TagTypeNameMatcher.Resolve(pool, value, out note);
            if (key == null)
            {
                unmatched.Add($"{label}：{value}");
                return value;
            }
            return key;
        }

        private void PickTagSymbol(TextBox box, List<string> dataSource)
        {
            if (dataSource == null) return;
            using (var searchForm = new TagSearchForm(dataSource))
            {
                if (searchForm.ShowDialog() == DialogResult.OK && !string.IsNullOrEmpty(searchForm.SelectedResult))
                {
                    box.Text = searchForm.SelectedResult;
                }
            }
        }

        private void DgvExtra_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            
            string colName = dgvExtra.Columns[e.ColumnIndex].Name;
            List<string> dataSource = null;

            if (colName == "DuctTagSym") dataSource = _ductTagsAll;
            else if (colName == "PipeTagSym") dataSource = _pipeTagsAll;
            else if (colName == "TrayTagSym") dataSource = _trayTagsAll;

            if (dataSource != null)
            {
                using (var searchForm = new TagSearchForm(dataSource))
                {
                    if (searchForm.ShowDialog() == DialogResult.OK)
                    {
                        dgvExtra.Rows[e.RowIndex].Cells[e.ColumnIndex].Value = searchForm.SelectedResult;
                        dgvExtra.CommitEdit(DataGridViewDataErrorContexts.Commit);
                    }
                }
            }
        }

        private void LoadData()
        {
            nudTolerance.Value = (decimal)_config.GroupToleranceMM;
            nudLineSpacing.Value = (decimal)_config.LineSpacingMM;
            nudBlankLines.Value = _config.BlankLines;

            txtMainDuct.Text = _config.MainDuctTagSymName ?? "";
            txtMainPipe.Text = _config.MainPipeTagSymName ?? "";
            txtMainTray.Text = _config.MainTrayTagSymName ?? "";

            _ductTagsAll.Clear();
            _pipeTagsAll.Clear();
            _trayTagsAll.Clear();

            var collector = new FilteredElementCollector(_doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => s.Category != null);
            
            foreach (var sym in collector)
            {
                string key = $"{sym.Family.Name}: {sym.Name}";
                int catId = sym.Category.Id.IntegerValue;
                if (catId == (int)BuiltInCategory.OST_DuctTags) _ductTagsAll.Add(key);
                else if (catId == (int)BuiltInCategory.OST_PipeTags) _pipeTagsAll.Add(key);
                else if (catId == (int)BuiltInCategory.OST_CableTrayTags) _trayTagsAll.Add(key);
            }

            _ductTagsAll = _ductTagsAll.OrderBy(s => s).ToList();
            _pipeTagsAll = _pipeTagsAll.OrderBy(s => s).ToList();
            _trayTagsAll = _trayTagsAll.OrderBy(s => s).ToList();

            RefreshGrid();
        }

        private void RefreshGrid()
        {
            dgvExtra.Rows.Clear();
            foreach (var rule in _config.ExtraRules)
            {
                string displayPos = "";
                if (rule.GroupIndex == "*") displayPos = "每一组前";
                else if (rule.GroupIndex == "*After") displayPos = "每一组后";
                else if (rule.GroupIndex == "LR") displayPos = "从左到右";
                else if (rule.GroupIndex == "TB") displayPos = "从上到下";
                else if (rule.GroupIndex.StartsWith("After_")) displayPos = $"第{rule.GroupIndex.Replace("After_", "")}组后";
                else displayPos = $"第{rule.GroupIndex}组前";

                int rowIdx = dgvExtra.Rows.Add(displayPos, rule.TagText, rule.DuctTagSymName, rule.PipeTagSymName, rule.CableTrayTagSymName);
            }
        }

        private void BtnClear_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("确定要清空所有配置吗？", "提示", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                dgvExtra.Rows.Clear();
                nudTolerance.Value = 25.0m;
                nudLineSpacing.Value = 200.0m;
                nudBlankLines.Value = 2;
                txtMainDuct.Text = "";
                txtMainPipe.Text = "";
                txtMainTray.Text = "";
            }
        }

        private void BtnImport_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog { Filter = "TAG配置文件 (*.txt)|*.txt", Title = "选择要导入的配置文件" })
            {
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    var newConfig = GroupTagConfig.LoadFromFile(ofd.FileName);
                    if (newConfig != null)
                    {
                        // 导入即校验：按当前文档实际存在的族类型把旧配置里的族名规范化一次，
                        // 能在当前模型里对上的直接改成精确的"族名: 类型名"，对不上的收集起来提示用户
                        var unmatched = new List<string>();
                        newConfig.MainDuctTagSymName = CanonicalTagName(_ductTagsAll, newConfig.MainDuctTagSymName, "主行 DUCT 族", unmatched);
                        newConfig.MainPipeTagSymName = CanonicalTagName(_pipeTagsAll, newConfig.MainPipeTagSymName, "主行 PIPE 族", unmatched);
                        newConfig.MainTrayTagSymName = CanonicalTagName(_trayTagsAll, newConfig.MainTrayTagSymName, "主行 TRAY 族", unmatched);

                        foreach (var rule in newConfig.ExtraRules)
                        {
                            string row = string.IsNullOrEmpty(rule.TagText) ? rule.GroupIndex : $"{rule.GroupIndex} {rule.TagText}";
                            rule.DuctTagSymName = CanonicalTagName(_ductTagsAll, rule.DuctTagSymName, row + " · DUCT", unmatched);
                            rule.PipeTagSymName = CanonicalTagName(_pipeTagsAll, rule.PipeTagSymName, row + " · PIPE", unmatched);
                            rule.CableTrayTagSymName = CanonicalTagName(_trayTagsAll, rule.CableTrayTagSymName, row + " · TRAY", unmatched);
                        }

                        _config.GroupToleranceMM = newConfig.GroupToleranceMM;
                        _config.LineSpacingMM = newConfig.LineSpacingMM;
                        _config.BlankLines = newConfig.BlankLines;
                        // 旧版本导出的配置文件里没有 MainDuctTag/MainPipeTag/MainTrayTag 三项，
                        // 导入时若为空则保留当前设置，避免把已经配好的主行族清空
                        if (!string.IsNullOrWhiteSpace(newConfig.MainDuctTagSymName)) _config.MainDuctTagSymName = newConfig.MainDuctTagSymName;
                        if (!string.IsNullOrWhiteSpace(newConfig.MainPipeTagSymName)) _config.MainPipeTagSymName = newConfig.MainPipeTagSymName;
                        if (!string.IsNullOrWhiteSpace(newConfig.MainTrayTagSymName)) _config.MainTrayTagSymName = newConfig.MainTrayTagSymName;
                        _config.ExtraRules = newConfig.ExtraRules;

                        nudTolerance.Value = (decimal)_config.GroupToleranceMM;
                        nudLineSpacing.Value = (decimal)_config.LineSpacingMM;
                        nudBlankLines.Value = _config.BlankLines;

                        txtMainDuct.Text = _config.MainDuctTagSymName ?? "";
                        txtMainPipe.Text = _config.MainPipeTagSymName ?? "";
                        txtMainTray.Text = _config.MainTrayTagSymName ?? "";

                        RefreshGrid();
                        InfoCenterTip.Show("配置导入成功，请点击【保存设置】生效。");

                        if (unmatched.Count > 0)
                        {
                            string body = "以下 Tag 族在当前模型里找不到（多半是旧配置来自其它项目，或族名/类型名已经改过）：\n\n"
                                + string.Join("\n", unmatched.Take(12))
                                + (unmatched.Count > 12 ? $"\n… 共 {unmatched.Count} 项" : "")
                                + "\n\n原值已保留，运行时这些行会自动回退到主行族。"
                                + "请双击对应单元格，从当前模型的族里重新选择后点【保存设置】。";
                            MessageBox.Show(body, "导入提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    }
                }
            }
        }

        private void BtnExport_Click(object sender, EventArgs e)
        {
            CollectDataFromUI();
            using (SaveFileDialog sfd = new SaveFileDialog { Filter = "TAG配置文件 (*.txt)|*.txt", Title = "保存配置文件", FileName = "MEP_GroupTag_Config.txt" })
            {
                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    _config.SaveToFile(sfd.FileName);
                    InfoCenterTip.Show("配置导出成功。");
                }
            }
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            CollectDataFromUI();
            _config.Save();
            InfoCenterTip.Show($"配置已保存！(共 {_config.ExtraRules.Count} 条附加行规则)");
        }

        private void CollectDataFromUI()
        {
            dgvExtra.EndEdit();
            dgvExtra.CommitEdit(DataGridViewDataErrorContexts.Commit);

            _config.GroupToleranceMM = (double)nudTolerance.Value;
            _config.LineSpacingMM = (double)nudLineSpacing.Value;
            _config.BlankLines = (int)nudBlankLines.Value;

            _config.MainDuctTagSymName = txtMainDuct.Text.Trim();
            _config.MainPipeTagSymName = txtMainPipe.Text.Trim();
            _config.MainTrayTagSymName = txtMainTray.Text.Trim();

            _config.ExtraRules.Clear();
            foreach (DataGridViewRow row in dgvExtra.Rows)
            {
                if (row.IsNewRow) continue;
                
                string posStr = row.Cells["GroupIndex"].Value?.ToString()?.Trim() ?? "";
                string gIdx = "";
                
                if (posStr.StartsWith("第") && posStr.Contains("组"))
                {
                    string num = new string(posStr.Where(char.IsDigit).ToArray());
                    gIdx = posStr.EndsWith("后") ? "After_" + num : num;
                }
                else if (posStr == "每一组前") gIdx = "*";
                else if (posStr == "每一组后") gIdx = "*After";
                else if (posStr == "从左到右") gIdx = "LR";
                else if (posStr == "从上到下") gIdx = "TB";

                string tText = row.Cells["TagText"].Value?.ToString()?.Trim() ?? "";
                string dSym = row.Cells["DuctTagSym"].Value?.ToString()?.Trim() ?? "";
                string pSym = row.Cells["PipeTagSym"].Value?.ToString()?.Trim() ?? "";
                string tSym = row.Cells["TrayTagSym"].Value?.ToString()?.Trim() ?? "";
                
                if (!string.IsNullOrEmpty(gIdx) && !string.IsNullOrEmpty(tText))
                {
                    _config.ExtraRules.Add(new ExtraRowRule { 
                        GroupIndex = gIdx, 
                        TagText = tText, 
                        DuctTagSymName = dSym,
                        PipeTagSymName = pSym,
                        CableTrayTagSymName = tSym
                    });
                }
            }
        }
    }

    public class TagSearchForm : Form
    {
        public string SelectedResult { get; private set; }
        private ListBox listBox;
        private TextBox txtSearch;
        private List<string> _allData;

        public TagSearchForm(List<string> data)
        {
            _allData = data;
            InitializeUI();
        }

        private void InitializeUI()
        {
            this.Text = "搜索 Tag 族";
            this.ClientSize = new Size(600, 400);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            var lbl = new Label { Text = "输入关键字过滤 (双击下方选中并关闭):", Location = new Point(10, 10), AutoSize = true };
            txtSearch = new TextBox { Location = new Point(10, 30), Width = 580 };
            txtSearch.TextChanged += TxtSearch_TextChanged;

            listBox = new ListBox { Location = new Point(10, 65), Size = new Size(580, 320), Font = new Font("Consolas", 10f) };
            listBox.DoubleClick += ListBox_DoubleClick;

            this.Controls.Add(lbl);
            this.Controls.Add(txtSearch);
            this.Controls.Add(listBox);

            listBox.Items.AddRange(_allData.ToArray());
        }

        private void TxtSearch_TextChanged(object sender, EventArgs e)
        {
            string filter = txtSearch.Text.Trim().ToLower();
            if (string.IsNullOrEmpty(filter))
            {
                listBox.Items.Clear();
                listBox.Items.AddRange(_allData.ToArray());
            }
            else
            {
                var filtered = _allData.Where(s => s.ToLower().Contains(filter)).ToArray();
                listBox.Items.Clear();
                listBox.Items.AddRange(filtered);
            }
        }

        private void ListBox_DoubleClick(object sender, EventArgs e)
        {
            if (listBox.SelectedItem != null)
            {
                SelectedResult = listBox.SelectedItem.ToString();
                this.DialogResult = DialogResult.OK;
                this.Close();
            }
        }
    }
}