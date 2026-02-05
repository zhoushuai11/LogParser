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
        // === 布局核心 ===
        private TableLayoutPanel mainLayout;
        private SplitContainer splitOuter;   
        private SplitContainer splitInner;   
        
        // === 控件 ===
        private ListBox lstCategories; 
        private TreeView tvMain;
        private StatusStrip statusStrip;
        private ToolStripStatusLabel lblStatus;
        
        // 顶部筛选
        private TextBox txtSearch;
        private TextBox txtPid;
        private Button btnShowErrors;

        // 右侧工具
        private TextBox txtTsInput;
        private TextBox txtDateResult;
        private DateTimePicker dtpDateInput;
        private TextBox txtTsResult;
        private RadioButton rbSec;
        private RadioButton rbMs;

        // 数据源
        private List<GameLogItem> _allLogs = new List<GameLogItem>();
        private bool _isErrorFilterMode = false; 

        public Form1() {
            this.Size = new Size(1400, 900);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Text = "Unity 日志分析器 (最终完美版)";
            
            InitializeComponent();
        }

        private void InitializeComponent() {
            // 1. 全局布局
            mainLayout = new TableLayoutPanel();
            mainLayout.Dock = DockStyle.Fill;
            mainLayout.ColumnCount = 1;
            mainLayout.RowCount = 3;
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60F)); 
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); 
            mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      
            this.Controls.Add(mainLayout);

            // 2. 顶部
            Panel topPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.WhiteSmoke, Padding = new Padding(5) };
            BuildTopPanel(topPanel);
            mainLayout.Controls.Add(topPanel, 0, 0);

            // 3. 中间核心区
            splitOuter = new SplitContainer { 
                Dock = DockStyle.Fill, 
                Orientation = Orientation.Vertical, 
                SplitterDistance = 220, 
                FixedPanel = FixedPanel.Panel1 
            };
            mainLayout.Controls.Add(splitOuter, 0, 1);

            // === 左侧：类型列表 ===
            GroupBox gbCat = new GroupBox { Text = "日志类型 (Category)", Dock = DockStyle.Fill, Padding = new Padding(5) };
            lstCategories = new ListBox { 
                Dock = DockStyle.Fill, 
                Font = new Font("Microsoft YaHei", 10),
                BorderStyle = BorderStyle.None,
                BackColor = Color.WhiteSmoke,
                ItemHeight = 24,
                DrawMode = DrawMode.OwnerDrawFixed 
            };
            lstCategories.DrawItem += LstCategories_DrawItem;
            // 点击列表时，仅刷新日志，不重置报错模式
            lstCategories.SelectedIndexChanged += (s, e) => RenderLogs();
            gbCat.Controls.Add(lstCategories);
            splitOuter.Panel1.Controls.Add(gbCat);
            splitOuter.Panel1.Padding = new Padding(5);

            // === 右侧：日志+工具 ===
            splitInner = new SplitContainer { 
                Dock = DockStyle.Fill, 
                Orientation = Orientation.Vertical, 
                SplitterDistance = 900, 
                FixedPanel = FixedPanel.Panel2 
            };
            splitOuter.Panel2.Controls.Add(splitInner);

            // 日志树
            tvMain = new TreeView {
                Dock = DockStyle.Fill, 
                BackColor = Color.White,
                Font = new Font("Consolas", 10),
                ShowLines = true,
                ShowPlusMinus = true,
                BorderStyle = BorderStyle.FixedSingle,
                HideSelection = false 
            };
            tvMain.BeforeExpand += TvMain_BeforeExpand;
            
            var ctx = new ContextMenuStrip();
            ctx.Items.Add("📄 复制当前行", null, (s, e) => CopyNodeText(tvMain.SelectedNode));
            ctx.Items.Add("🌳 复制完整日志", null, (s, e) => CopyFullLog());
            tvMain.ContextMenuStrip = ctx;
            tvMain.KeyDown += (s, e) => { if (e.Control && e.KeyCode == Keys.C) CopyNodeText(tvMain.SelectedNode); };

            splitInner.Panel1.Controls.Add(tvMain);

            // 工具箱
            Panel rightToolPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.AliceBlue, Padding = new Padding(10) };
            BuildRightTools(rightToolPanel);
            splitInner.Panel2.Controls.Add(rightToolPanel);

            // 4. 底部
            statusStrip = new StatusStrip { Dock = DockStyle.Fill };
            lblStatus = new ToolStripStatusLabel { Text = "请导入日志文件..." };
            statusStrip.Items.Add(lblStatus);
            mainLayout.Controls.Add(statusStrip, 0, 2);
        }

        private void LstCategories_DrawItem(object sender, DrawItemEventArgs e) {
            if (e.Index < 0) return;
            e.DrawBackground();
            bool isSelected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            
            string text = lstCategories.Items[e.Index].ToString();
            
            Brush textBrush = isSelected ? Brushes.White : Brushes.Black;
            if (isSelected) e.Graphics.FillRectangle(Brushes.DodgerBlue, e.Bounds);
            
            e.Graphics.DrawString(text, e.Font, textBrush, e.Bounds.X + 5, e.Bounds.Y + 2);
            e.DrawFocusRectangle();
        }

        private void BuildTopPanel(Panel panel) {
            int x = 10, y = 15;

            Button btnLoad = new Button { Text = "📂 导入", Location = new Point(x, y), Size = new Size(80, 30), BackColor = Color.White };
            btnLoad.Click += (s, e) => OpenLogFile();
            panel.Controls.Add(btnLoad); x += 90;

            Button btnExp = new Button { Text = "展开", Location = new Point(x, y), Size = new Size(50, 30) };
            btnExp.Click += (s, e) => { tvMain.BeginUpdate(); tvMain.ExpandAll(); tvMain.EndUpdate(); };
            panel.Controls.Add(btnExp); x += 60;

            Button btnCol = new Button { Text = "折叠", Location = new Point(x, y), Size = new Size(50, 30) };
            btnCol.Click += (s, e) => { tvMain.BeginUpdate(); tvMain.CollapseAll(); tvMain.EndUpdate(); };
            panel.Controls.Add(btnCol); x += 70;

            Label div1 = new Label { Text = "|", Location = new Point(x, y + 5), AutoSize = true, ForeColor = Color.Gray };
            panel.Controls.Add(div1); x += 20;

            // 报错按钮
            btnShowErrors = new Button { 
                Text = "🔴 仅看报错", 
                Location = new Point(x, y), 
                Size = new Size(90, 30), 
                BackColor = Color.WhiteSmoke,
                ForeColor = Color.Red,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei", 9, FontStyle.Bold)
            };
            btnShowErrors.Click += (s, e) => ToggleErrorMode();
            panel.Controls.Add(btnShowErrors); x += 100;

            panel.Controls.Add(new Label { Text = "PID:", Location = new Point(x, y + 5), AutoSize = true, ForeColor = Color.Blue });
            x += 35;
            txtPid = new TextBox { Location = new Point(x, y + 2), Width = 80 };
            txtPid.TextChanged += (s, e) => RenderLogs();
            panel.Controls.Add(txtPid); x += 90;

            panel.Controls.Add(new Label { Text = "搜索:", Location = new Point(x, y + 5), AutoSize = true });
            x += 40;
            txtSearch = new TextBox { Location = new Point(x, y + 2), Width = 150 };
            txtSearch.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) RenderLogs(); };
            panel.Controls.Add(txtSearch); x += 160;

            Button btnGo = new Button { Text = "Go", Location = new Point(x, y), Size = new Size(40, 25) };
            btnGo.Click += (s, e) => RenderLogs();
            panel.Controls.Add(btnGo);
        }

        private void ToggleErrorMode() {
            _isErrorFilterMode = !_isErrorFilterMode;
            UpdateErrorBtnStyle();
            
            // 【修正】不再强制重置左侧列表，保留用户的选择
            // 只是单纯刷新日志，应用新的过滤条件
            RenderLogs();
        }

        private void UpdateErrorBtnStyle() {
            if (_isErrorFilterMode) {
                btnShowErrors.BackColor = Color.MistyRose; 
                btnShowErrors.Text = "❌ 取消报错";
            } else {
                btnShowErrors.BackColor = Color.WhiteSmoke;
                btnShowErrors.Text = "🔴 仅看报错";
            }
        }

        private void BuildRightTools(Panel panel) {
            GroupBox gb = new GroupBox { Text = "时间戳转换", Dock = DockStyle.Top, Height = 400, Font = new Font("Microsoft YaHei", 9) };
            
            int y = 30;
            rbSec = new RadioButton { Text = "秒(10位)", Location = new Point(20, y), Checked = true, AutoSize = true };
            rbMs = new RadioButton { Text = "毫秒(13位)", Location = new Point(120, y), AutoSize = true };
            gb.Controls.Add(rbSec); gb.Controls.Add(rbMs); y += 40;

            gb.Controls.Add(new Label { Text = "时间戳 ➔ 日期", Location = new Point(20, y), ForeColor = Color.Blue, AutoSize = true }); y += 25;
            txtTsInput = new TextBox { Location = new Point(20, y), Width = 200, Text = DateTimeOffset.Now.ToUnixTimeSeconds().ToString() }; y += 30;
            Button btn1 = new Button { Text = "转换", Location = new Point(20, y), Width = 80 };
            btn1.Click += (s, e) => {
                if (long.TryParse(txtTsInput.Text, out long ts)) {
                    var dt = rbSec.Checked ? DateTimeOffset.FromUnixTimeSeconds(ts) : DateTimeOffset.FromUnixTimeMilliseconds(ts);
                    txtDateResult.Text = dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                }
            };
            gb.Controls.Add(btn1); y += 35;
            txtDateResult = new TextBox { Location = new Point(20, y), Width = 200, ReadOnly = true }; y += 40;

            gb.Controls.Add(new Label { Text = "日期 ➔ 时间戳", Location = new Point(20, y), ForeColor = Color.Blue, AutoSize = true }); y += 25;
            dtpDateInput = new DateTimePicker { Location = new Point(20, y), Width = 200, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss" }; y += 30;
            Button btn2 = new Button { Text = "转换", Location = new Point(20, y), Width = 80 };
            btn2.Click += (s, e) => {
                long ts = rbSec.Checked ? new DateTimeOffset(dtpDateInput.Value).ToUnixTimeSeconds() : new DateTimeOffset(dtpDateInput.Value).ToUnixTimeMilliseconds();
                txtTsResult.Text = ts.ToString();
            };
            gb.Controls.Add(btn2); y += 35;
            txtTsResult = new TextBox { Location = new Point(20, y), Width = 200, ReadOnly = true }; y += 40;

            panel.Controls.Add(gb);
        }

        private void OpenLogFile() {
            OpenFileDialog ofd = new OpenFileDialog();
            if (ofd.ShowDialog() == DialogResult.OK) {
                lblStatus.Text = "正在解析...";
                Application.DoEvents(); 
                
                try {
                    string text = File.ReadAllText(ofd.FileName);
                    var chunks = LogParser.SmartSplit(text);
                    _allLogs.Clear();
                    
                    HashSet<string> cats = new HashSet<string> { "All" };
                    int idx = 0;
                    foreach (var c in chunks) {
                        var item = LogParser.Parse(c, ++idx);
                        _allLogs.Add(item);
                        if (!string.IsNullOrEmpty(item.Category)) cats.Add(item.Category);
                    }

                    // 更新左侧列表
                    lstCategories.Items.Clear();
                    var sorted = cats.OrderBy(c => c == "All" ? 0 : 1).ThenBy(c => c).ToArray();
                    lstCategories.Items.AddRange(sorted);
                    lstCategories.SelectedIndex = 0; // 默认选中 All

                    RenderLogs();
                } catch (Exception ex) { MessageBox.Show("解析失败: " + ex.Message); }
            }
        }

        // === 【核心修复】逻辑合并 ===
        private void RenderLogs() {
            // 1. 获取基础条件
            string cat = "All";
            if (lstCategories.SelectedItem != null) {
                cat = lstCategories.SelectedItem.ToString();
            }

            string pid = txtPid.Text.Trim();
            string search = txtSearch.Text.Trim().ToLower();

            // 2. 筛选
            var list = _allLogs.Where(x => {
                // 条件 A: 类别筛选 (AND 逻辑)
                if (cat != "All" && !string.Equals(x.Category, cat, StringComparison.OrdinalIgnoreCase)) {
                    return false; // 如果没选中 All 且类别不匹配，直接排除
                }

                // 条件 B: 报错筛选 (AND 逻辑，如果开启的话)
                if (_isErrorFilterMode) {
                    bool isError = x.Category.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   x.Category.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   x.RawContent.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   x.RawContent.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   x.RawContent.IndexOf("NullReference", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   x.RawContent.IndexOf("Fail", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!isError) return false; // 如果开启了报错模式但不是报错，排除
                }

                // 条件 C: PID 和 搜索
                bool pidMatch = string.IsNullOrEmpty(pid) || (x.PID != null && x.PID.Contains(pid));
                bool textMatch = string.IsNullOrEmpty(search) || x.RawContent.ToLower().Contains(search) || (x.PrettyContent != null && x.PrettyContent.ToLower().Contains(search));
                
                return pidMatch && textMatch;
            }).ToList();

            // 3. 状态更新
            string modeText = _isErrorFilterMode ? "[报错模式]" : "";
            lblStatus.Text = $"{modeText} 分类[{cat}]: {list.Count} 条";

            // 4. 重绘
            tvMain.Visible = false; 
            tvMain.Nodes.Clear();
            
            if (list.Count > 0) {
                tvMain.BeginUpdate();
                try {
                    foreach (var item in list.Take(2000)) {
                        TreeNode node = new TreeNode(item.HeaderInfo);
                        node.Tag = item;
                        
                        if (item.Category.Contains("Error")) node.ForeColor = Color.Red;
                        else if (item.Category == "System") node.ForeColor = Color.Gray;
                        else node.ForeColor = Color.Blue;

                        node.Nodes.Add(new TreeNode("Loading..."));
                        tvMain.Nodes.Add(node);

                        // 搜索或报错模式自动展开
                        if (!string.IsNullOrEmpty(search) || _isErrorFilterMode) node.Expand();
                    }
                } finally {
                    tvMain.EndUpdate();
                }
            }

            tvMain.Visible = true;
            if (tvMain.Nodes.Count > 0) {
                tvMain.SelectedNode = tvMain.Nodes[0];
                tvMain.Nodes[0].EnsureVisible();
                tvMain.Focus();
            }
        }

        private void TvMain_BeforeExpand(object sender, TreeViewCancelEventArgs e) {
            TreeNode node = e.Node;
            if (node.Nodes.Count == 1 && node.Nodes[0].Text == "Loading...") {
                tvMain.BeginUpdate(); 
                try {
                    node.Nodes.Clear();
                    if (node.Tag is GameLogItem item) {
                        string content = item.PrettyContent ?? item.RawContent;
                        try {
                            if (content.Trim().StartsWith("{") || content.Trim().StartsWith("[")) {
                                var token = JToken.Parse(content);
                                AddJsonNode(token, node);
                            } else {
                                foreach (var line in content.Split('\n')) node.Nodes.Add(new TreeNode(line));
                            }
                        } catch { 
                            foreach (var line in content.Split('\n')) node.Nodes.Add(new TreeNode(line));
                        }
                    }
                } finally {
                    tvMain.EndUpdate();
                }
            }
        }

        private void AddJsonNode(JToken token, TreeNode parent) {
            if (token is JValue) {
                parent.Nodes.Add(new TreeNode(token.ToString()) { ForeColor = Color.Green });
            } else if (token is JObject obj) {
                foreach (var p in obj.Properties()) {
                    TreeNode child = new TreeNode(p.Name);
                    if (p.Value is JValue) {
                        child.Text += $": {p.Value}";
                        child.ForeColor = Color.Black;
                    } else AddJsonNode(p.Value, child);
                    parent.Nodes.Add(child);
                }
            } else if (token is JArray arr) {
                for (int i = 0; i < arr.Count; i++) {
                    TreeNode child = new TreeNode($"[{i}]");
                    AddJsonNode(arr[i], child);
                    parent.Nodes.Add(child);
                }
            }
        }

        private void CopyNodeText(TreeNode node) {
            if (node != null && node.Text != "Loading...") Clipboard.SetText(node.Text);
        }

        private void CopyFullLog() {
            if (tvMain.SelectedNode?.Tag is GameLogItem item)
                Clipboard.SetText(item.PrettyContent ?? item.RawContent);
            else if (tvMain.SelectedNode?.Parent?.Tag is GameLogItem parentItem)
                Clipboard.SetText(parentItem.PrettyContent ?? parentItem.RawContent);
        }
    }
}