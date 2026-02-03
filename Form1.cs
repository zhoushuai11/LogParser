using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LogParserTool {
    public partial class Form1 : Form {
        // === 核心控件 ===
        private TreeView tvMain;
        private Panel rightPanel;

        // === 顶部筛选控件 ===
        private ComboBox cmbFilter;
        private TextBox txtSearch;
        private TextBox txtPid;
        private Label lblStatus;

        // === 右侧工具控件 ===
        private TextBox txtTsInput;
        private TextBox txtDateResult;
        private DateTimePicker dtpDateInput;
        private TextBox txtTsResult;
        private RadioButton rbSec;
        private RadioButton rbMs;

        // === 数据源 ===
        private List<GameLogItem> _allLogs = new List<GameLogItem>();

        public Form1() {
            this.Size = new Size(1300, 850);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Text = "Unity 日志分析器 (稳定布局版)";

            BuildUI();
        }

        private void BuildUI() {
            // ---------------------------------------------------------
            // 1. 顶部操作栏 (Dock = Top)
            // ---------------------------------------------------------
            Panel topPanel = new Panel {
                Dock = DockStyle.Top, Height = 60, BackColor = Color.WhiteSmoke, Padding = new Padding(10)
            };

            Button btnLoad = new Button {
                Text = "📂 导入",
                Left = 15,
                Top = 15,
                Width = 80,
                Height = 30,
                BackColor = Color.White
            };
            btnLoad.Click += (s, e) => OpenLogFile();

            Button btnExpand = new Button {
                Text = "展开",
                Left = 100,
                Top = 15,
                Width = 60,
                Height = 30,
                BackColor = Color.White
            };
            btnExpand.Click += (s, e) => {
                tvMain.BeginUpdate();
                tvMain.ExpandAll();
                tvMain.EndUpdate();
            };

            Button btnCollapse = new Button {
                Text = "折叠",
                Left = 165,
                Top = 15,
                Width = 60,
                Height = 30,
                BackColor = Color.White
            };
            btnCollapse.Click += (s, e) => {
                tvMain.BeginUpdate();
                tvMain.CollapseAll();
                tvMain.EndUpdate();
            };

            Label lblType = new Label {
                Text = "类型:", Left = 240, Top = 22, AutoSize = true
            };
            cmbFilter = new ComboBox {
                Left = 280, Top = 18, Width = 120, DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbFilter.Items.AddRange(new string[] {
                "All",
                "LoginBattlefield",
                "DoType1",
                "SendGrpc",        // <--- 新增
                "DoType2",
                "DoType3",
                "GRPC",
                "JsonLog",
                "Error",
                "System"
            });
            cmbFilter.SelectedIndex = 0;
            cmbFilter.SelectedIndexChanged += (s, e) => RenderLogs();

            Label lblPid = new Label {
                Text = "PID:",
                Left = 410,
                Top = 22,
                AutoSize = true,
                ForeColor = Color.DarkBlue
            };
            txtPid = new TextBox {
                Left = 445, Top = 18, Width = 100
            };
            txtPid.TextChanged += (s, e) => RenderLogs();

            Label lblSearch = new Label {
                Text = "搜索:", Left = 560, Top = 22, AutoSize = true
            };
            txtSearch = new TextBox {
                Left = 600, Top = 18, Width = 150
            };
            txtSearch.KeyDown += (s, e) => {
                if (e.KeyCode == Keys.Enter)
                    RenderLogs();
            };

            Button btnSearch = new Button {
                Text = "Go",
                Left = 760,
                Top = 17,
                Width = 40,
                Height = 23
            };
            btnSearch.Click += (s, e) => RenderLogs();

            lblStatus = new Label {
                Text = "准备就绪",
                Left = 820,
                Top = 22,
                AutoSize = true,
                ForeColor = Color.Gray
            };

            topPanel.Controls.AddRange(new Control[] {
                btnLoad, btnExpand, btnCollapse, lblType, cmbFilter, lblPid, txtPid, lblSearch, txtSearch, btnSearch, lblStatus
            });

            // ---------------------------------------------------------
            // 2. 右侧工具栏 (Dock = Right)
            //    直接固定宽度，不使用 SplitContainer 避免崩溃
            // ---------------------------------------------------------
            rightPanel = new Panel {
                Dock = DockStyle.Right, Width = 340, BackColor = Color.WhiteSmoke, Padding = new Padding(10)
            };
            BuildRightTools(rightPanel);

            // ---------------------------------------------------------
            // 3. 左侧 TreeView (Dock = Fill)
            //    自动填充剩余空间
            // ---------------------------------------------------------
            tvMain = new TreeView {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                ForeColor = Color.Black,
                Font = new Font("Consolas", 10),
                ShowLines = true,
                ShowPlusMinus = true,
                ShowRootLines = true,
                AllowDrop = true,
                BorderStyle = BorderStyle.FixedSingle // 加个边框好看点
            };

            // 拖拽事件
            tvMain.DragEnter += (s, e) => {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                    e.Effect = DragDropEffects.Copy;
            };
            tvMain.DragDrop += (s, e) => {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files.Length > 0)
                    ProcessFile(files[0]);
            };
            tvMain.BeforeExpand += TvMain_BeforeExpand;

            // 复制事件
            tvMain.KeyDown += (s, e) => {
                if (e.Control && e.KeyCode == Keys.C)
                    CopySelectedNode();
            };
            var ctxMenu = new ContextMenuStrip();
            ctxMenu.Items.Add("📄 复制当前行", null, (s, e) => CopySelectedNode());
            ctxMenu.Items.Add("🌳 复制完整日志", null, (s, e) => CopyFullLog());
            tvMain.ContextMenuStrip = ctxMenu;

            // ---------------------------------------------------------
            // 4. 按顺序添加控件 (重要：先加边缘 Dock，最后加 Fill)
            // ---------------------------------------------------------
            this.Controls.Add(tvMain); // Fill (最后占据中间)
            this.Controls.Add(rightPanel); // Right
            this.Controls.Add(topPanel); // Top

            // 强制调整 Z-Order，确保 TopPanel 在最上面，RightPanel 在右边，TV 在底层铺满
            topPanel.BringToFront();
            rightPanel.BringToFront();
        }

        // ==========================================
        // 复制逻辑
        // ==========================================
        private void CopySelectedNode() {
            if (tvMain.SelectedNode != null && tvMain.SelectedNode.Text != "Loading...") {
                Clipboard.SetText(tvMain.SelectedNode.Text);
                lblStatus.Text = "已复制";
            }
        }

        private void CopyFullLog() {
            if (tvMain.SelectedNode != null) {
                TreeNode root = tvMain.SelectedNode;
                while (root.Parent != null)
                    root = root.Parent;
                if (root.Tag is GameLogItem item) {
                    Clipboard.SetText(string.IsNullOrEmpty(item.PrettyContent)? item.RawContent : item.PrettyContent);
                    lblStatus.Text = "完整日志已复制";
                }
            }
        }

        // ==========================================
        // 右侧工具栏构建
        // ==========================================
        private void BuildRightTools(Panel panel) {
            GroupBox gbTools = new GroupBox {
                Text = "⏱️ 时间戳工具", Dock = DockStyle.Top, Height = 350, Font = new Font("Microsoft YaHei", 9)
            };

            Label lblUnit = new Label {
                Text = "单位:", Left = 15, Top = 30, AutoSize = true
            };
            rbSec = new RadioButton {
                Text = "秒(10位)",
                Left = 60,
                Top = 28,
                Checked = true,
                AutoSize = true
            };
            rbMs = new RadioButton {
                Text = "毫秒(13位)", Left = 140, Top = 28, AutoSize = true
            };

            Label lblPart1 = new Label {
                Text = "--- 时间戳 ➔ 日期 ---",
                Left = 15,
                Top = 60,
                ForeColor = Color.Blue,
                AutoSize = true
            };
            txtTsInput = new TextBox {
                Left = 15, Top = 85, Width = 200, Text = DateTimeOffset.Now.ToUnixTimeSeconds().ToString()
            };
            Button btnTsToDate = new Button {
                Text = "转换 ⬇", Left = 15, Top = 115, Width = 200
            };
            txtDateResult = new TextBox {
                Left = 15,
                Top = 145,
                Width = 200,
                ReadOnly = true,
                BackColor = Color.White
            };

            Label lblPart2 = new Label {
                Text = "--- 日期 ➔ 时间戳 ---",
                Left = 15,
                Top = 185,
                ForeColor = Color.Blue,
                AutoSize = true
            };
            dtpDateInput = new DateTimePicker {
                Left = 15,
                Top = 210,
                Width = 200,
                Format = DateTimePickerFormat.Custom,
                CustomFormat = "yyyy-MM-dd HH:mm:ss"
            };
            Button btnDateToTs = new Button {
                Text = "转换 ⬇", Left = 15, Top = 240, Width = 200
            };
            txtTsResult = new TextBox {
                Left = 15,
                Top = 270,
                Width = 200,
                ReadOnly = true,
                BackColor = Color.White
            };
            Button btnCopyTs = new Button {
                Text = "复制", Left = 15, Top = 300, Width = 200
            };

            btnTsToDate.Click += (s, e) => ConvertTsToDate();
            btnDateToTs.Click += (s, e) => ConvertDateToTs();
            btnCopyTs.Click += (s, e) => {
                if (!string.IsNullOrEmpty(txtTsResult.Text))
                    Clipboard.SetText(txtTsResult.Text);
            };

            gbTools.Controls.AddRange(new Control[] {
                lblUnit, rbSec, rbMs, lblPart1, txtTsInput, btnTsToDate, txtDateResult, lblPart2, dtpDateInput, btnDateToTs, txtTsResult, btnCopyTs
            });

            panel.Controls.Add(gbTools);
        }

        private void ConvertTsToDate() {
            if (long.TryParse(txtTsInput.Text.Trim(), out long ts)) {
                try {
                    DateTimeOffset dateTime;
                    if (rbSec.Checked)
                        dateTime = DateTimeOffset.FromUnixTimeSeconds(ts);
                    else
                        dateTime = DateTimeOffset.FromUnixTimeMilliseconds(ts);
                    txtDateResult.Text = dateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                } catch {
                    txtDateResult.Text = "无效";
                }
            }
        }

        private void ConvertDateToTs() {
            long ts = rbSec.Checked? new DateTimeOffset(dtpDateInput.Value).ToUnixTimeSeconds() : new DateTimeOffset(dtpDateInput.Value).ToUnixTimeMilliseconds();
            txtTsResult.Text = ts.ToString();
        }

        // ==========================================
        // 日志加载与渲染
        // ==========================================
        private void OpenLogFile() {
            OpenFileDialog ofd = new OpenFileDialog();
            if (ofd.ShowDialog() == DialogResult.OK)
                ProcessFile(ofd.FileName);
        }

        private void ProcessFile(string path) {
            lblStatus.Text = "读取中...";
            Application.DoEvents();
            try {
                string fullText = File.ReadAllText(path);
                // SmartSplit 必须确保 LogModels.cs 里的代码已经更新
                var rawChunks = LogParser.SmartSplit(fullText);
                _allLogs.Clear();
                int idx = 0;
                foreach (var chunk in rawChunks)
                    _allLogs.Add(LogParser.Parse(chunk, ++idx));
                lblStatus.Text = $"解析完成: {_allLogs.Count} 条";
                RenderLogs();
            } catch (Exception ex) {
                MessageBox.Show("出错: " + ex.Message);
            }
        }

        private void RenderLogs() {
            tvMain.BeginUpdate();
            tvMain.Nodes.Clear();
            string filterCat = cmbFilter.SelectedItem?.ToString() ?? "All";
            string pidKey = txtPid.Text.Trim();
            string searchKey = txtSearch.Text.Trim().ToLower();

            var filtered = _allLogs.Where(x => {
                bool catMatch = filterCat == "All" || x.Category == filterCat;
                bool pidMatch = string.IsNullOrEmpty(pidKey) || (x.PID != null && x.PID.Contains(pidKey));
                bool textMatch = string.IsNullOrEmpty(searchKey) || x.RawContent.ToLower().Contains(searchKey) || (x.PrettyContent != null && x.PrettyContent.ToLower().Contains(searchKey));
                return catMatch && pidMatch && textMatch;
            }).ToList();

            if (filtered.Count > 2000)
                lblStatus.Text = $"结果: {filtered.Count} (仅显示前2000条)";
            else
                lblStatus.Text = $"结果: {filtered.Count} 条";

            foreach (var item in filtered.Take(2000)) {
                TreeNode rootNode = new TreeNode(item.HeaderInfo);
                rootNode.Tag = item;
                if (item.Category == "Error")
                    rootNode.ForeColor = Color.Red;
                else
                    rootNode.ForeColor = Color.Blue;
                rootNode.Nodes.Add(new TreeNode("Loading..."));
                tvMain.Nodes.Add(rootNode);
                if (!string.IsNullOrEmpty(searchKey))
                    rootNode.Expand();
            }

            tvMain.EndUpdate();
        }

        private void TvMain_BeforeExpand(object sender, TreeViewCancelEventArgs e) {
            TreeNode parent = e.Node;
            if (parent.Nodes.Count == 1 && parent.Nodes[0].Text == "Loading...") {
                parent.Nodes.Clear();
                if (parent.Tag is GameLogItem item) {
                    try {
                        if (item.PrettyContent != null && (item.PrettyContent.Trim().StartsWith("{") || item.PrettyContent.Trim().StartsWith("["))) {
                            var token = JToken.Parse(item.PrettyContent);
                            AddJsonToTree(token, parent);
                        } else {
                            string content = item.PrettyContent ?? item.RawContent;
                            foreach (var line in content.Split('\n'))
                                parent.Nodes.Add(new TreeNode(line));
                        }
                    } catch {
                        parent.Nodes.Add(new TreeNode(item.PrettyContent ?? item.RawContent));
                    }
                }
            }
        }

        private void AddJsonToTree(JToken token, TreeNode parentNode) {
            if (token == null)
                return;
            if (token is JValue) {
                parentNode.Nodes.Add(new TreeNode(token.ToString()) {
                    ForeColor = Color.Green
                });
            } else if (token is JObject obj) {
                foreach (var property in obj.Properties()) {
                    TreeNode childNode = new TreeNode(property.Name);
                    if (property.Value is JValue) {
                        childNode.Text += $": {property.Value}";
                        childNode.ForeColor = Color.Black;
                    } else
                        AddJsonToTree(property.Value, childNode);

                    parentNode.Nodes.Add(childNode);
                }
            } else if (token is JArray array) {
                for (int i = 0; i < array.Count; i++) {
                    TreeNode childNode = new TreeNode($"[{i}]");
                    AddJsonToTree(array[i], childNode);
                    parentNode.Nodes.Add(childNode);
                }
            }
        }
    }
}