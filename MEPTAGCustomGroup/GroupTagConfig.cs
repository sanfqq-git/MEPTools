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

    public class GroupTagConfig
    {
        public double GroupToleranceMM { get; set; } = 25.0;
        public double LineSpacingMM { get; set; } = 200.0;
        public int BlankLines { get; set; } = 2;

        public List<ExtraRowRule> ExtraRules { get; set; } = new List<ExtraRowRule>();

        public static GroupTagConfig Load()
        {
            var cfg = new GroupTagConfig();
            
            cfg.GroupToleranceMM = MEPConfig.GetDouble("TagGroup.Tolerance", 25.0);
            cfg.LineSpacingMM = MEPConfig.GetDouble("TagGroup.LineSpacing", 200.0);
            cfg.BlankLines = MEPConfig.GetInt("TagGroup.BlankLines", 2);

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
                $"BlankLines={BlankLines}"
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
        private Button btnOk, btnImport, btnExport, btnClear;
        private Button btnInsert, btnUp, btnDown;

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
            this.ClientSize = new Size(950, 580); 
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
            nudBlankLines = new NumericUpDown { Location = new Point(570, y), Width = 80, Minimum = 0, Maximum = 10 };
            this.Controls.Add(lblBlank); this.Controls.Add(nudBlankLines);
            y += gap + 10;

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
            
            // 👇 核心：加上 ValueType = typeof(string)，防止下拉框取值失败 👇
            var colPosition = new DataGridViewComboBoxColumn { Name = "GroupIndex", HeaderText = "插入位置", FillWeight = 15, ValueType = typeof(string) };
            var positions = new List<string> { 
                "每一组前", "每一组后",
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

            btnImport = new Button { Text = "输入配置", Location = new Point(labelX + 250, y), Width = 80, Height = 28, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            btnImport.Click += BtnImport_Click;
            this.Controls.Add(btnImport);

            btnExport = new Button { Text = "输出配置", Location = new Point(labelX + 340, y), Width = 80, Height = 28, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            btnExport.Click += BtnExport_Click;
            this.Controls.Add(btnExport);

            btnClear = new Button { Text = "清空配置", Location = new Point(labelX + 430, y), Width = 80, Height = 28, ForeColor = Color.Red, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
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
                btnImport.Top = newY;
                btnExport.Top = newY;
                btnClear.Top = newY;
                btnOk.Top = newY;
                
                dgvExtra.Height = newY - dgvExtra.Top - 15; 
            };
        }

        private void BtnInsert_Click(object sender, EventArgs e)
        {
            // 👇 插入前强制提交当前正在编辑的单元格数据 👇
            dgvExtra.EndEdit();

            int index = dgvExtra.CurrentRow?.Index ?? dgvExtra.Rows.Count;
            if (index >= dgvExtra.Rows.Count) index = Math.Max(0, dgvExtra.Rows.Count - 1);
            
            dgvExtra.Rows.Insert(index, new object[] { "", "", "", "", "" });
            dgvExtra.CurrentCell = dgvExtra.Rows[index].Cells[0];
        }

        private void BtnUp_Click(object sender, EventArgs e)
        {
            // 👇 移动前强制提交 👇
            dgvExtra.EndEdit();

            if (dgvExtra.CurrentRow == null || dgvExtra.CurrentRow.Index <= 0) return;
            
            int index = dgvExtra.CurrentRow.Index;
            SwapRows(index, index - 1);
            dgvExtra.CurrentCell = dgvExtra.Rows[index - 1].Cells[0];
        }

        private void BtnDown_Click(object sender, EventArgs e)
        {
            // 👇 移动前强制提交 👇
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
                        _config.GroupToleranceMM = newConfig.GroupToleranceMM;
                        _config.LineSpacingMM = newConfig.LineSpacingMM;
                        _config.BlankLines = newConfig.BlankLines;
                        _config.ExtraRules = newConfig.ExtraRules;

                        nudTolerance.Value = (decimal)_config.GroupToleranceMM;
                        nudLineSpacing.Value = (decimal)_config.LineSpacingMM;
                        nudBlankLines.Value = _config.BlankLines;

                        RefreshGrid();
                        InfoCenterTip.Show("配置导入成功，请点击【保存设置】生效。");
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
            // 👇 核心修复：去掉导致焦点丢失的 CurrentCell = null 👇
            dgvExtra.EndEdit();
            dgvExtra.CommitEdit(DataGridViewDataErrorContexts.Commit);

            _config.GroupToleranceMM = (double)nudTolerance.Value;
            _config.LineSpacingMM = (double)nudLineSpacing.Value;
            _config.BlankLines = (int)nudBlankLines.Value;

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