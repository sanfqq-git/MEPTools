using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace MEPTools.BatchLink
{
    public class BatchLinkForm : Form
    {
        public string Placement { get; private set; }
        public List<string> LocalFiles { get; private set; } = new List<string>();
        public List<string> CloudGuids { get; private set; } = new List<string>();
        public string LastDirectory { get; private set; }

        private TextBox txtLocalFiles;
        private TextBox txtCloud;
        private RadioButton rbOrigin, rbShared;

        public BatchLinkForm(string lastDir, string lastPlacement)
        {
            LastDirectory = lastDir;
            Placement = lastPlacement;

            // 窗体基础设置
            Text = "MEPTools - 批量链接";
            Size = new Size(550, 480);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Microsoft YaHei", 9F);

            // 1. 本地文件选择区
            var lblLocal = new Label { Text = "本地模型（支持多选）：", Location = new Point(15, 15), AutoSize = true };
            var btnBrowse = new Button { Text = "浏览文件...", Location = new Point(15, 40), Size = new Size(100, 30) };
            
            txtLocalFiles = new TextBox
            {
                Location = new Point(125, 42),
                Size = new Size(390, 25),
                ReadOnly = true,
                BackColor = SystemColors.Window
            };
            
            btnBrowse.Click += BtnBrowse_Click;

            // 2. 定位方式选择区
            var lblMode = new Label { Text = "定位方式：", Location = new Point(15, 90), AutoSize = true };
            rbOrigin = new RadioButton { Text = "自动 - 原点到原点", Location = new Point(90, 90), AutoSize = true, Checked = lastPlacement != "Shared" };
            rbShared = new RadioButton { Text = "自动 - 通过共享坐标", Location = new Point(240, 90), AutoSize = true, Checked = lastPlacement == "Shared" };

            // 3. 云端模型输入区
            var lblCloud = new Label 
            { 
                Text = "云端模型 GUID（可粘贴多行）：\n格式：region;projectGuid;modelGuid\n例如：EMEA;xxxx-xxxx;yyyy-yyyy", 
                Location = new Point(15, 130), 
                AutoSize = true,
                ForeColor = Color.DimGray
            };
            
            txtCloud = new TextBox
            {
                Location = new Point(15, 190),
                Size = new Size(500, 150),
                Multiline = true,
                ScrollBars = ScrollBars.Vertical
            };

            // 4. 底部按钮
            var btnOk = new Button
            {
                Text = "开始批量链接",
                Location = new Point(380, 370),
                Size = new Size(130, 35),
                DialogResult = DialogResult.OK,
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnOk.FlatAppearance.BorderSize = 0;
            
            var btnCancel = new Button
            {
                Text = "取消",
                Location = new Point(290, 370),
                Size = new Size(80, 35),
                DialogResult = DialogResult.Cancel
            };

            btnOk.Click += BtnOk_Click;

            Controls.AddRange(new Control[]
            {
                lblLocal, btnBrowse, txtLocalFiles,
                lblMode, rbOrigin, rbShared,
                lblCloud, txtCloud,
                btnOk, btnCancel
            });
            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }

        private void BtnBrowse_Click(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "选择要链接的 Revit 模型（可多选）";
                dlg.Filter = "Revit 模型 (*.rvt)|*.rvt";
                dlg.Multiselect = true;
                dlg.InitialDirectory = Directory.Exists(LastDirectory) 
                    ? LastDirectory 
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    LocalFiles = new List<string>(dlg.FileNames);
                    LastDirectory = Path.GetDirectoryName(dlg.FileNames[0]);
                    
                    // 界面友好提示
                    txtLocalFiles.Text = $"已选择 {LocalFiles.Count} 个本地文件";
                }
            }
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            Placement = rbShared.Checked ? "Shared" : "Origin";
            
            // 提取云端 GUID
            CloudGuids.Clear();
            foreach (var line in txtCloud.Lines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    CloudGuids.Add(line.Trim());
                }
            }

            // 校验：如果既没选本地，也没填云端，提示用户
            if (LocalFiles.Count == 0 && CloudGuids.Count == 0)
            {
                MessageBox.Show("请至少选择本地文件或输入云端GUID。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None; // 阻止关闭窗口
            }
        }
    }
}