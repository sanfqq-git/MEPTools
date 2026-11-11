using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.Shared;

using Point = System.Drawing.Point;
using Button = System.Windows.Forms.Button;
using Label = System.Windows.Forms.Label;
using Size = System.Drawing.Size;

namespace MEPTools.MEPGroupTag
{
    public class ExtraRowRule
    {
        public string GroupIndex { get; set; } // "1", "2", "*", "After_1", "After_2", "*After"
        public string TagText { get; set; }    // 如 "T1"
        public string TagSymbolName { get; set; } // 如 "HA-ANN-DUG-TK TAG-EL: Standard"
    }

    public class GroupTagConfig
    {
        public double GroupToleranceMM { get; set; } = 25.0;
        public double LineSpacingMM { get; set; } = 200.0;
        public int BlankLines { get; set; } = 2;
        
        public string DuctTagType { get; set; } = "";
        public string CableTrayTagType { get; set; } = "";
        public string PipeTagType { get; set; } = "";

        public List<ExtraRowRule> ExtraRules { get; set; } = new List<ExtraRowRule>();

        public static GroupTagConfig Load()
        {
            var cfg = new GroupTagConfig();
            
            cfg.GroupToleranceMM = MEPConfig.GetDouble("TagGroup.Tolerance", 25.0);
            cfg.LineSpacingMM = MEPConfig.GetDouble("TagGroup.LineSpacing", 200.0);
            cfg.BlankLines = MEPConfig.GetInt("TagGroup.BlankLines", 2);
            cfg.DuctTagType = MEPConfig.GetString("TagGroup.DuctTagType", "");
            cfg.CableTrayTagType = MEPConfig.GetString("TagGroup.CableTrayTagType", "");
            cfg.PipeTagType = MEPConfig.GetString("TagGroup.PipeTagType", "");

            // 反序列化规则：格式 "1,T1,族A;After_1,T2,族B;"
            string rulesStr = MEPConfig.GetString("TagGroup.ExtraRules", "");
            if (!string.IsNullOrWhiteSpace(rulesStr))
            {
                var items = rulesStr.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var item in items)
                {
                    var parts = item.Split(',');
                    if (parts.Length == 3)
                    {
                        cfg.ExtraRules.Add(new ExtraRowRule 
                        { 
                            GroupIndex = parts[0].Trim(), 
                            TagText = parts[1].Trim(), 
                            TagSymbolName = parts[2].Trim() 
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
            MEPConfig.SetValue("TagGroup.DuctTagType", DuctTagType);
            MEPConfig.SetValue("TagGroup.CableTrayTagType", CableTrayTagType);
            MEPConfig.SetValue("TagGroup.PipeTagType", PipeTagType);

            var ruleItems = ExtraRules.Select(r => $"{r.GroupIndex},{r.TagText},{r.TagSymbolName}");
            MEPConfig.SetValue("TagGroup.ExtraRules", string.Join(";", ruleItems));
            
            MEPConfig.Save();
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
        private Button btnOk;
        
        private Dictionary<string, FamilySymbol> _allTagSymbols;

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
            this.ClientSize = new Size(800, 580);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular, GraphicsUnit.Point, 134);

            int y = 20, gap = 35;
            int labelX = 20, inputX = 140;

            // 参数设置区
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

            // 附加行设置区
            var lblExtra = new Label { Text = "组前/组后附加标签设定 (指定在哪一组前/后插入什么文字，并使用哪个 Tag 族):", Location = new Point(labelX, y), AutoSize = true, Font = new Font(this.Font, FontStyle.Bold) };
            this.Controls.Add(lblExtra); y += 25;

            dgvExtra = new DataGridView
            {
                Location = new Point(labelX, y),
                Size = new Size(760, 350),
                AllowUserToAddRows = true,
                AllowUserToDeleteRows = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersVisible = false
            };
            dgvExtra.DataError += (s, e) => { e.ThrowException = false; e.Cancel = true; };
            
            // 👇 修改：增加“后”的选项
            var colPosition = new DataGridViewComboBoxColumn { Name = "GroupIndex", HeaderText = "插入位置", FillWeight = 30 };
            var positions = new List<string> { 
                "每一组前", "每一组后",
                "第1组前", "第1组后",
                "第2组前", "第2组后",
                "第3组前", "第3组后",
                "第4组前", "第4组后",
                "第5组前", "第5组后",
                "第6组前", "第6组后",
                "第7组前", "第7组后",
                "第8组前", "第8组后",
                "第9组前", "第9组后",
                "第10组前", "第10组后"
            };
            colPosition.Items.AddRange(positions.ToArray());
            dgvExtra.Columns.Add(colPosition);

            dgvExtra.Columns.Add(new DataGridViewTextBoxColumn { Name = "TagText", HeaderText = "生成的文字(如T1)", FillWeight = 25 });
            
            var colSymbol = new DataGridViewComboBoxColumn { Name = "TagSymbolName", HeaderText = "使用的 Tag 族 (FAMILY AND TYPE)", FillWeight = 45 };
            dgvExtra.Columns.Add(colSymbol);
            
            this.Controls.Add(dgvExtra);
            y += 360;

            btnOk = new Button { Text = "保存设置 (空格)", Location = new Point(340, y), Width = 120, Height = 32, DialogResult = DialogResult.OK };
            btnOk.Click += BtnOk_Click;
            this.AcceptButton = btnOk;
            this.Controls.Add(btnOk);
        }

        private void LoadData()
        {
            nudTolerance.Value = (decimal)_config.GroupToleranceMM;
            nudLineSpacing.Value = (decimal)_config.LineSpacingMM;
            nudBlankLines.Value = _config.BlankLines;

            _allTagSymbols = new Dictionary<string, FamilySymbol>();
            var collector = new FilteredElementCollector(_doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => s.Category != null && (s.Category.Id.IntegerValue == (int)BuiltInCategory.OST_DuctTags || 
                                                   s.Category.Id.IntegerValue == (int)BuiltInCategory.OST_PipeTags || 
                                                   s.Category.Id.IntegerValue == (int)BuiltInCategory.OST_CableTrayTags ||
                                                   s.Category.Id.IntegerValue == (int)BuiltInCategory.OST_MultiCategoryTags));
            foreach (var sym in collector)
            {
                string key = $"{sym.Family.Name}: {sym.Name}";
                if (!_allTagSymbols.ContainsKey(key)) _allTagSymbols.Add(key, sym);
            }

            var allTagNames = _allTagSymbols.Keys.OrderBy(s => s).ToArray();
            var extraCol = dgvExtra.Columns["TagSymbolName"] as DataGridViewComboBoxColumn;
            if (extraCol != null) extraCol.Items.AddRange(allTagNames);

            // 默认加一行示例
            if (_config.ExtraRules.Count == 0)
            {
                _config.ExtraRules.Add(new ExtraRowRule { GroupIndex = "1", TagText = "T1", TagSymbolName = allTagNames.FirstOrDefault() ?? "" });
                _config.ExtraRules.Add(new ExtraRowRule { GroupIndex = "After_1", TagText = "T2", TagSymbolName = allTagNames.FirstOrDefault() ?? "" });
            }

            foreach (var rule in _config.ExtraRules)
            {
                string displayPos = "";
                if (rule.GroupIndex == "*") displayPos = "每一组前";
                else if (rule.GroupIndex == "*After") displayPos = "每一组后";
                else if (rule.GroupIndex.StartsWith("After_")) displayPos = $"第{rule.GroupIndex.Replace("After_", "")}组后";
                else displayPos = $"第{rule.GroupIndex}组前";

                int rowIdx = dgvExtra.Rows.Add(displayPos, rule.TagText);
                dgvExtra.Rows[rowIdx].Cells["TagSymbolName"].Value = rule.TagSymbolName;
            }
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            // 强制提交 UI 数据
            dgvExtra.EndEdit();
            dgvExtra.CommitEdit(DataGridViewDataErrorContexts.Commit);
            dgvExtra.CurrentCell = null; 

            _config.GroupToleranceMM = (double)nudTolerance.Value;
            _config.LineSpacingMM = (double)nudLineSpacing.Value;
            _config.BlankLines = (int)nudBlankLines.Value;

            _config.ExtraRules.Clear();
            foreach (DataGridViewRow row in dgvExtra.Rows)
            {
                if (row.IsNewRow) continue;
                
                string posStr = row.Cells["GroupIndex"].Value?.ToString()?.Trim() ?? "";
                string gIdx = "";
                
                // 👇 解析：区分组前和组后
                if (posStr.StartsWith("第") && posStr.Contains("组"))
                {
                    string num = new string(posStr.Where(char.IsDigit).ToArray());
                    gIdx = posStr.EndsWith("后") ? "After_" + num : num;
                }
                else if (posStr == "每一组前")
                {
                    gIdx = "*";
                }
                else if (posStr == "每一组后")
                {
                    gIdx = "*After";
                }

                string tText = row.Cells["TagText"].Value?.ToString()?.Trim() ?? "";
                string tSymbol = row.Cells["TagSymbolName"].Value?.ToString()?.Trim() ?? "";
                
                if (!string.IsNullOrEmpty(gIdx) && !string.IsNullOrEmpty(tText) && !string.IsNullOrEmpty(tSymbol))
                {
                    _config.ExtraRules.Add(new ExtraRowRule { GroupIndex = gIdx, TagText = tText, TagSymbolName = tSymbol });
                }
            }
            _config.Save();
        }
    }
}