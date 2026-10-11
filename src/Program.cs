using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BMM
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length > 0 && args[0] == "--ui-check") { MainForm.UiCheck(args[1]); return; }
            if (args.Length > 0 && args[0] == "--selftest")
            {
                string outFile = args.Length > 1 ? args[1] : Path.Combine(Paths.ManagerDir, "selftest.txt");
                MainForm.SelfTest(outFile);
                return;
            }
            Application.Run(new MainForm());
        }
    }

    internal partial class MainForm : Form
    {
        private readonly Settings _s;
        private readonly bool _persistPreferences;
        private List<ModInfo> _mods = new List<ModInfo>();
        private List<TsPackage> _ts = new List<TsPackage>();
        private List<BackupItem> _backups = new List<BackupItem>();
        private bool _tsLoading;
        private bool _suppressDialogs;
        private bool _fillingCategories;
        private int _loadToken;
        private string _loadingKey = "";
        private string _lastLoadKey = "";
        private DateTime _lastLoadAt = DateTime.MinValue;
        private static int _loadCount;

        /// <summary>How many times an online list was actually fetched (regression guard for refresh loops).</summary>
        internal static int LoadCount { get { return _loadCount; } }

        /// <summary>Debug trace of online loads: source|query|category and whether it was skipped/rejected.</summary>
        internal static readonly List<string> LoadTrace = new List<string>();

        private ListView _lvMods;
        private TextBox _txtModDetail;
        private StatusStrip _status;
        private ToolStripStatusLabel _statusLabel;
        private Label _lblGameState;

        private TextBox _txtSearch;
        private ComboBox _cbCategory;
        private ComboBox _cbSort;
        private ComboBox _cbSource;
        private CheckBox _chkAutoTranslate;
        private ListView _lvOnline;
        private TextBox _txtOnlineDetail;
        private Label _lblOnlineStatus;
        private TabPage _onlineTab;
        private TabControl _tabs;
        private Button _bDetailTrans;
        private ComboBox _cbTransSource;
        private Label _lblDetailHead;
        private Label _lblDetailStatus;
        private PictureBox _picDetail;
        private TsPackage _detailPkg;
        private string _detailReadme;
        private string _detailZhReadme;
        private string _detailZhTitle = "";
        private string _detailZhDesc = "";
        private bool _showDetailZh;
        private int _detailToken;
        private volatile string _detailProgress = "";
        private Timer _detailPump;

        private TextBox _txtReport;
        private CheckBox _chkLog;

        private ComboBox _cbLogs;
        private TextBox _txtLog;
        private CheckBox _chkProblems;

        private TextBox _txtGameDir;
        private ListBox _lstBackups;

        public MainForm() : this(false) { }

        private MainForm(bool offlineCheck) : this(offlineCheck, !offlineCheck) { }

        private MainForm(bool offlineCheck, bool persistPreferences)
        {
            _offlineCheck = offlineCheck;
            _persistPreferences = persistPreferences;
            _s = offlineCheck ? new Settings { AutoTranslate = false, TranslateSource = Thunderstore.TranslateSources[0] } : Settings.Load();
            Text = "小丑牌 Mod 管理器  ·  Balatro Mod Manager";
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1180, 740);
            Size = new Size(1500, 940);

            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.Padding = new Point(14, 6);
            tabs.TabPages.Add(BuildModsTab());
            _onlineTab = BuildOnlineTab();
            tabs.TabPages.Add(_onlineTab);
            tabs.TabPages.Add(BuildReportTab());
            tabs.TabPages.Add(BuildLogTab());
            tabs.TabPages.Add(BuildSettingsTab());
            tabs.SelectedIndexChanged += delegate
            {
                // first visit to the online tab pulls the list automatically (uses the 6h cache when present)
                if (tabs.SelectedTab == _onlineTab && _ts.Count == 0 && !_tsLoading) LoadOnline(false);
            };
            _tabs = tabs;
            Controls.Add(tabs);
            Controls.Add(BuildTopBar());
            InitializeRedesign();

            _status = new StatusStrip();
            _statusLabel = new ToolStripStatusLabel("就绪");
            _statusLabel.Spring = true;
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            _status.Items.Add(_statusLabel);
            Controls.Add(_status);

            Theme.Apply(this);
            Thunderstore.CurrentSource = _s.TranslateSource;
            Thunderstore.AutoTranslate = _s.AutoTranslate;
            _detailPump = new Timer();
            _detailPump.Interval = 400;
            _detailPump.Tick += delegate { if (_detailProgress.Length > 0) SetDetailStatus(_detailProgress); };
            _detailPump.Start();
            FormClosed += delegate { _detailPump.Stop(); };

            Load += delegate
            {
                if (_offlineCheck) { _lblGameState.Text = "离线示例 · 不读取本机游戏、Mod 或存档"; SetStatus("示例数据预览"); return; }
                RefreshMods();
                RefreshGameState();
                RefreshLogList();
                RefreshBackups();
                SetStatus("就绪。共 " + _mods.Count + " 个 mod 项。");
            };
        }

        // ================= chrome =================

        private Control BuildTopBar()
        {
            Panel p = new Panel();
            p.Dock = DockStyle.Top;
            p.Height = 52;
            p.Padding = new Padding(12, 8, 12, 6);

            _lblGameState = new Label();
            _lblGameState.AutoSize = false;
            _lblGameState.Dock = DockStyle.Fill;
            _lblGameState.TextAlign = ContentAlignment.MiddleLeft;
            _lblGameState.Text = "";

            FlowLayoutPanel right = new FlowLayoutPanel();
            right.Dock = DockStyle.Right;
            right.FlowDirection = FlowDirection.LeftToRight;
            right.WrapContents = false;
            right.AutoSize = true;
            right.Padding = new Padding(0, 2, 0, 0);

            Button btnLaunch = MakeButton("启动游戏", 92);
            btnLaunch.Click += delegate { LaunchGame(); };
            Button btnSteam = MakeButton("通过 Steam 启动", 120);
            btnSteam.Click += delegate { Process.Start("steam://rungameid/2379780"); };
            Button btnRefresh = MakeButton("全部刷新", 92);
            btnRefresh.Click += delegate { RefreshAll(); };
            right.Controls.Add(btnLaunch);
            right.Controls.Add(btnSteam);
            right.Controls.Add(btnRefresh);

            p.Controls.Add(_lblGameState);
            p.Controls.Add(right);
            return p;
        }

        private TabPage BuildModsTab()
        {
            TabPage tab = new TabPage("已安装 Mod");
            TableLayoutPanel t = Grid(3);
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 168F));

            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Fill;
            bar.WrapContents = false;
            bar.Padding = new Padding(4, 6, 4, 0);
            Button bInstall = MakeButton("本地安装…", 96);
            bInstall.Click += delegate { InstallLocal(); };
            Button bToggle = MakeButton("启用 / 禁用", 100);
            bToggle.Click += delegate { ToggleSelected(); };
            Button bUninstall = MakeButton("卸载", 72);
            bUninstall.Click += delegate { UninstallSelected(); };
            Button bOpen = MakeButton("打开文件夹", 96);
            bOpen.Click += delegate { OpenSelectedFolder(); };
            Button bCheck = MakeButton("检查更新", 92);
            bCheck.Click += delegate { CheckUpdates(); };
            Button bEnableAll = MakeButton("全部启用", 92);
            bEnableAll.Click += delegate { SetAll(true); };
            Button bDisableAll = MakeButton("全部禁用", 92);
            bDisableAll.Click += delegate { SetAll(false); };
            bar.Controls.AddRange(new Control[] { bInstall, bToggle, bUninstall, bOpen, bCheck, bEnableAll, bDisableAll });

            _lvMods = new ListView();
            _lvMods.Dock = DockStyle.Fill;
            _lvMods.View = View.Details;
            _lvMods.FullRowSelect = true;
            _lvMods.MultiSelect = false;
            _lvMods.HideSelection = false;
            _lvMods.Columns.Add("Mod 名称", 210);
            _lvMods.Columns.Add("版本", 88);
            _lvMods.Columns.Add("作者", 132);
            _lvMods.Columns.Add("状态", 56);
            _lvMods.Columns.Add("更新", 104);
            _lvMods.Columns.Add("来源", 82);
            _lvMods.Columns.Add("简介", 300);
            _lvMods.SelectedIndexChanged += delegate { ShowModDetail(); };
            _lvMods.DoubleClick += delegate { ToggleSelected(); };

            _txtModDetail = new TextBox();
            _txtModDetail.Dock = DockStyle.Fill;
            _txtModDetail.Multiline = true;
            _txtModDetail.ReadOnly = true;
            _txtModDetail.ScrollBars = ScrollBars.Vertical;
            _txtModDetail.BackColor = Color.White;
            _txtModDetail.Font = new Font("Consolas", 9F);
            _txtModDetail.Text = "选中上面的 mod 查看详情。\r\n\r\n"
                + "提示：双击一行 = 快速启用/禁用。禁用是通过 Mods\\lovely\\blacklist.txt 实现的，改动需要重启游戏才生效。\r\n"
                + "“检查更新”会联网比对 Thunderstore 上的最新版本，可更新的行会黄色高亮。";

            t.Controls.Add(bar, 0, 0);
            t.Controls.Add(_lvMods, 0, 1);
            t.Controls.Add(_txtModDetail, 0, 2);
            tab.Controls.Add(t);
            return tab;
        }

        private TabPage BuildOnlineTab()
        {
            TabPage tab = new TabPage("发现 Mod");
            TableLayoutPanel t = Grid(3);
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 130F));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 56F));

            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Fill;
            bar.WrapContents = false;
            bar.Padding = new Padding(4, 6, 4, 0);
            Label l0 = new Label();
            l0.Text = "来源：";
            l0.AutoSize = true;
            l0.Padding = new Padding(0, 8, 0, 0);
            _cbSource = new DarkComboBox();
            _cbSource.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbSource.Width = 156;
            _cbSource.Items.Add("Thunderstore");
            _cbSource.Items.Add("GitHub 搜索");
            _cbSource.Items.Add("精选清单");
            _cbSource.Items.Add("自定义仓库");
            _cbSource.Items.Add("全部来源");
            _cbSource.SelectedIndex = SourceIndexFromCode(_s.OnlineSource);
            _cbSource.SelectedIndexChanged += delegate { OnSourceChanged(); };
            Label l1 = new Label();
            l1.Text = "搜索：";
            l1.AutoSize = true;
            l1.Padding = new Padding(6, 8, 0, 0);
            _txtSearch = new TextBox();
            _txtSearch.Width = 180;
            _txtSearch.TextChanged += delegate { if (CurrentSourceCode == "github") return; FillOnline(); };
            _txtSearch.KeyDown += delegate (object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.Handled = true; if (CurrentSourceCode == "github") LoadOnline(false); else FillOnline(); }
            };
            Label l2 = new Label();
            l2.Text = "分类：";
            l2.AutoSize = true;
            l2.Padding = new Padding(6, 8, 0, 0);
            _cbCategory = new DarkComboBox();
            _cbCategory.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbCategory.Width = 118;
            _cbCategory.SelectedIndexChanged += delegate
            {
                if (_fillingCategories) return;   // programmatic rebuild, not a user change
                if (CurrentSourceCode == "github" && !_tsLoading) LoadOnline(false);   // topic needs a fresh search
                else FillOnline();
            };
            Label l3 = new Label();
            l3.Text = "排序：";
            l3.AutoSize = true;
            l3.Padding = new Padding(6, 8, 0, 0);
            _cbSort = new DarkComboBox();
            _cbSort.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbSort.Width = 96;
            foreach (string m in Thunderstore.SortModesFor(_s.OnlineSource)) _cbSort.Items.Add(m);
            _cbSort.SelectedIndex = 0;
            _cbSort.SelectedIndexChanged += delegate { FillOnline(); };
            Button bReload = MakeButton("刷新列表", 88);
            bReload.Click += delegate { LoadOnline(true); };
            _lblOnlineStatus = new Label();
            _lblOnlineStatus.AutoSize = true;
            _lblOnlineStatus.Padding = new Padding(8, 8, 0, 0);
            bar.Controls.AddRange(new Control[] { l0, _cbSource, l1, _txtSearch, l2, _cbCategory, l3, _cbSort, bReload, _lblOnlineStatus });

            _lvOnline = new ListView();
            _lvOnline.Dock = DockStyle.Fill;
            _lvOnline.View = View.Details;
            _lvOnline.FullRowSelect = true;
            _lvOnline.MultiSelect = true;
            _lvOnline.HideSelection = false;
            _lvOnline.Columns.Add("名称", 200);
            _lvOnline.Columns.Add("作者", 120);
            _lvOnline.Columns.Add("版本", 92);
            _lvOnline.Columns.Add("下载量", 74);
            _lvOnline.Columns.Add("点赞", 54);
            _lvOnline.Columns.Add("更新日期", 92);
            _lvOnline.Columns.Add("简介", 330);
            _lvOnline.Columns.Add("来源", 110);
            _lvOnline.Columns.Add("安装状态", 160);
            _lvOnline.SelectedIndexChanged += delegate { ShowOnlineDetail(); };
            _lvOnline.DoubleClick += delegate { ShowOnlineReadme(); };
            _lvOnline.KeyDown += delegate (object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.Handled = true; ShowOnlineReadme(); }
            };

            // left: list   right: quick detail panel (cover + title + intro + translate)
            TableLayoutPanel split = new TableLayoutPanel();
            split.Dock = DockStyle.Fill;
            split.ColumnCount = 2;
            split.RowCount = 1;
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 440F));
            split.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            split.Controls.Add(_lvOnline, 0, 0);
            split.Controls.Add(BuildDetailPanel(), 1, 0);

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Fill;
            Button bInstall = MakeButton("安装选中", 96);
            bInstall.Location = new Point(4, 5);
            bInstall.Click += delegate { InstallSelectedOnline(); };
            Button bInstallDep = MakeButton("安装 + 依赖", 110);
            bInstallDep.Location = new Point(106, 5);
            bInstallDep.Click += delegate { InstallSelectedOnlineWithDeps(); };
            Button bReadme = MakeButton("大窗口看介绍", 120);
            bReadme.Location = new Point(222, 5);
            bReadme.Click += delegate { ShowOnlineReadme(); };
            Button bWeb = MakeButton("打开网页", 92);
            bWeb.Location = new Point(348, 5);
            bWeb.Click += delegate { OpenSelectedOnlinePage(); };
            bottom.Controls.AddRange(new Control[] { bInstall, bInstallDep, bReadme, bWeb });

            t.Controls.Add(BuildBrowseToolbar(bar), 0, 0);
            t.Controls.Add(split, 0, 1);
            t.Controls.Add(bottom, 0, 2);
            tab.Controls.Add(t);
            return tab;
        }

        /// <summary>Right-hand quick view: cover, title line, intro text and the translate-all button.</summary>
        private Panel BuildDetailPanel()
        {
            Panel p = new Panel();
            p.Dock = DockStyle.Fill;
            p.Padding = new Padding(6, 0, 0, 0);

            _txtOnlineDetail = new TextBox();
            _txtOnlineDetail.Dock = DockStyle.Fill;
            _txtOnlineDetail.Multiline = true;
            _txtOnlineDetail.ReadOnly = true;
            _txtOnlineDetail.ScrollBars = ScrollBars.Vertical;
            _txtOnlineDetail.WordWrap = true;
            _txtOnlineDetail.Font = new Font("Microsoft YaHei UI", 10F);
            _txtOnlineDetail.BackColor = Color.White;
            _txtOnlineDetail.Text = "数据来自 thunderstore.io/c/balatro（约 170+ 个 mod）。\r\n"
                + "第一次进入这个页签会自动联网抓取，之后 6 小时内用本地缓存，离线也能翻列表。\r\n\r\n"
                + "点左边任意一行，这里会直接显示：封面图、名称、作者、点赞、下载量、简介，以及这个 mod 的完整说明。\r\n"
                + "点“翻译全部”可以把标题、简介、完整说明一起翻成中文（免费翻译接口，不需要 API key，仅发送介绍文本；服务端可见网络连接信息）。";

            _lblDetailStatus = new Label();
            _lblDetailStatus.Dock = DockStyle.Bottom;
            _lblDetailStatus.Height = 20;
            _lblDetailStatus.ForeColor = Color.DimGray;
            _lblDetailStatus.Text = "";

            Panel btns = new Panel();
            btns.Dock = DockStyle.Top;
            btns.Height = 72;
            _bDetailTrans = new Button();
            _bDetailTrans.Text = "翻译全部";
            _bDetailTrans.Width = 96; _bDetailTrans.Height = 27; _bDetailTrans.Location = new Point(0, 4);
            _bDetailTrans.Enabled = false;
            _bDetailTrans.Click += delegate { TranslateDetail(); };
            Label lsrc = new Label();
            lsrc.Text = "翻译源：";
            lsrc.AutoSize = true;
            lsrc.Location = new Point(0, 42);
            _cbTransSource = new DarkComboBox();
            _cbTransSource.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbTransSource.Width = 240;
            _cbTransSource.Location = new Point(70, 38);
            foreach (string src in Thunderstore.TranslateSources) _cbTransSource.Items.Add(src);
            if (_cbTransSource.Items.Contains(_s.TranslateSource)) _cbTransSource.SelectedItem = _s.TranslateSource;
            else _cbTransSource.SelectedIndex = 0;
            _cbTransSource.SelectedIndexChanged += delegate
            {
                string pick = _cbTransSource.SelectedItem as string;
                if (string.IsNullOrEmpty(pick)) return;
                _s.TranslateSource = pick;
                SaveSettings();
                Thunderstore.CurrentSource = pick;
                _detailZhReadme = null; _detailZhTitle = ""; _detailZhDesc = "";
                if (_showDetailZh) { _showDetailZh = false; RenderDetail(); }
                SetDetailStatus("翻译源已切换为：" + pick);
            };
            _chkAutoTranslate = new CheckBox();
            _chkAutoTranslate.Text = "自动翻译";
            _chkAutoTranslate.AutoSize = true;
            _chkAutoTranslate.Location = new Point(112, 9);
            _chkAutoTranslate.Checked = _s.AutoTranslate;
            _chkAutoTranslate.CheckedChanged += delegate
            {
                _s.AutoTranslate = _chkAutoTranslate.Checked;
                SaveSettings();
                Thunderstore.AutoTranslate = _chkAutoTranslate.Checked;
                if (_chkAutoTranslate.Checked && _detailPkg != null && _detailReadme != null) TranslateDetail();
                else SetDetailStatus(_chkAutoTranslate.Checked ? "已开启自动翻译：以后点任意 mod 会自动翻成中文" : "已关闭自动翻译");
            };
            btns.Controls.AddRange(new Control[] { _bDetailTrans, lsrc, _cbTransSource, _chkAutoTranslate });

            _lblDetailHead = new Label();
            _lblDetailHead.Dock = DockStyle.Fill;
            _lblDetailHead.Padding = new Padding(10, 2, 2, 2);
            _lblDetailHead.Text = "";

            _picDetail = new PictureBox();
            _picDetail.Dock = DockStyle.Left;
            _picDetail.Width = 136;
            _picDetail.SizeMode = PictureBoxSizeMode.Zoom;
            _picDetail.BackColor = Color.White;
            _picDetail.BorderStyle = BorderStyle.FixedSingle;

            Panel head = new Panel();
            head.Dock = DockStyle.Top;
            head.Height = 174;
            head.Controls.Add(_lblDetailHead);
            head.Controls.Add(_picDetail);

            p.Controls.Add(_txtOnlineDetail);
            p.Controls.Add(_lblDetailStatus);
            p.Controls.Add(btns);
            p.Controls.Add(head);
            return p;
        }

        private TabPage BuildReportTab()
        {
            TabPage tab = new TabPage("检测报告");
            TableLayoutPanel t = Grid(2);
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Fill;
            bar.WrapContents = false;
            bar.Padding = new Padding(4, 6, 4, 0);
            Button bRun = MakeButton("重新检测", 96);
            bRun.Click += delegate { RunReport(); };
            _chkLog = new CheckBox();
            _chkLog.Text = "同时分析最近一次启动日志";
            _chkLog.Checked = _s.IncludeLogInReport;
            _chkLog.AutoSize = true;
            _chkLog.Padding = new Padding(10, 8, 0, 0);
            _chkLog.CheckedChanged += delegate { _s.IncludeLogInReport = _chkLog.Checked; SaveSettings(); RunReport(); };
            Button bCopy = MakeButton("复制报告", 92);
            bCopy.Click += delegate
            {
                if (_txtReport.TextLength > 0) Clipboard.SetText(_txtReport.Text);
                SetStatus("报告已复制到剪贴板。");
            };
            bar.Controls.AddRange(new Control[] { bRun, _chkLog, bCopy });

            _txtReport = new TextBox();
            _txtReport.Dock = DockStyle.Fill;
            _txtReport.Multiline = true;
            _txtReport.ReadOnly = true;
            _txtReport.ScrollBars = ScrollBars.Both;
            _txtReport.WordWrap = false;
            _txtReport.Font = new Font("Consolas", 9F);

            t.Controls.Add(bar, 0, 0);
            t.Controls.Add(_txtReport, 0, 1);
            tab.Controls.Add(t);
            return tab;
        }

        private TabPage BuildLogTab()
        {
            TabPage tab = new TabPage("日志");
            TableLayoutPanel t = Grid(2);
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Fill;
            bar.WrapContents = false;
            bar.Padding = new Padding(4, 6, 4, 0);
            Label l1 = new Label();
            l1.Text = "日志文件：";
            l1.AutoSize = true;
            l1.Padding = new Padding(0, 8, 0, 0);
            _cbLogs = new DarkComboBox();
            _cbLogs.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbLogs.Width = 300;
            _cbLogs.SelectedIndexChanged += delegate { LoadLog(); };
            _chkProblems = new CheckBox();
            _chkProblems.Text = "只看问题行（错误 / 黑名单 / 补丁失败）";
            _chkProblems.AutoSize = true;
            _chkProblems.Padding = new Padding(10, 8, 0, 0);
            _chkProblems.CheckedChanged += delegate { LoadLog(); };
            Button bOpen = MakeButton("打开日志文件夹", 120);
            bOpen.Click += delegate { OpenFolder(Paths.LogDir); };
            bar.Controls.AddRange(new Control[] { l1, _cbLogs, _chkProblems, bOpen });

            _txtLog = new TextBox();
            _txtLog.Dock = DockStyle.Fill;
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Both;
            _txtLog.WordWrap = false;
            _txtLog.Font = new Font("Consolas", 9F);

            t.Controls.Add(bar, 0, 0);
            t.Controls.Add(_txtLog, 0, 1);
            tab.Controls.Add(t);
            return tab;
        }

        private TabPage BuildSettingsTab()
        {
            TabPage tab = new TabPage("设置 / 工具");
            Panel host = new Panel();
            host.Dock = DockStyle.Fill;
            host.AutoScroll = true;

            GroupBox g1 = new GroupBox();
            g1.Text = "游戏目录";
            g1.Location = new Point(12, 10);
            g1.Size = new Size(1030, 92);
            g1.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _txtGameDir = new TextBox();
            _txtGameDir.Location = new Point(16, 28);
            _txtGameDir.Width = 700;
            _txtGameDir.Text = _s.GameDir;
            Button bAuto = MakeButton("自动检测", 92);
            bAuto.Location = new Point(726, 26);
            bAuto.Click += delegate
            {
                string d = GameLocator.Detect();
                if (d.Length == 0)
                {
                    MessageBox.Show(this, "没找到 Balatro 安装位置，请手动选择游戏目录（里面有 Balatro.exe）。", "自动检测失败");
                    return;
                }
                _txtGameDir.Text = d;
            };
            Button bBrowse = MakeButton("浏览…", 78);
            bBrowse.Location = new Point(824, 26);
            bBrowse.Click += delegate
            {
                FolderBrowserDialog f = new FolderBrowserDialog();
                f.Description = "选择 Balatro 游戏目录（包含 Balatro.exe 的文件夹）";
                if (f.ShowDialog(this) == DialogResult.OK) _txtGameDir.Text = f.SelectedPath;
            };
            Button bSave = MakeButton("保存", 78);
            bSave.Location = new Point(908, 26);
            bSave.Click += delegate
            {
                _s.GameDir = _txtGameDir.Text.Trim();
                SaveSettings();
                RefreshGameState();
                SetStatus("游戏目录已保存。");
            };
            Label hint = new Label();
            hint.Text = "游戏目录里应该有 Balatro.exe 和 Lovely 注入器（winmm.dll）。mod 本体不放在这里，而是放在 Mods 目录。";
            hint.AutoSize = true;
            hint.ForeColor = Color.DimGray;
            hint.Location = new Point(16, 60);
            g1.Controls.AddRange(new Control[] { _txtGameDir, bAuto, bBrowse, bSave, hint });

            GroupBox g2 = new GroupBox();
            g2.Text = "存档备份 / 恢复（只动存档，不碰 mod）";
            g2.Location = new Point(12, 112);
            g2.Size = new Size(1030, 250);
            g2.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Button bBackup = MakeButton("立即备份", 96);
            bBackup.Location = new Point(16, 26);
            bBackup.Click += delegate
            {
                try
                {
                    string dir = SaveManager.BackupNow("manual");
                    RefreshBackups();
                    SetStatus("已备份到 " + dir);
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "备份失败"); }
            };
            Button bRestore = MakeButton("恢复选中备份", 120);
            bRestore.Location = new Point(118, 26);
            bRestore.Click += delegate
            {
                BackupItem item = _lstBackups.SelectedItem as BackupItem;
                if (item == null) { MessageBox.Show(this, "请先在下面的列表里选择一个备份。", "提示"); return; }
                if (ModLibrary.IsGameRunning()) { MessageBox.Show(this, "游戏正在运行，请先关闭游戏再恢复存档。", "提示"); return; }
                if (MessageBox.Show(this, "确定要用这个备份覆盖当前存档吗？\r\n\r\n" + item.ToString() + "\r\n\r\n（当前存档会先自动备份一次）", "确认恢复", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                try
                {
                    string safety = SaveManager.Restore(item);
                    RefreshBackups();
                    SetStatus("已恢复。恢复前的存档备份在 " + safety);
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "恢复失败"); }
            };
            Button bOpenB = MakeButton("打开备份文件夹", 120);
            bOpenB.Location = new Point(244, 26);
            bOpenB.Click += delegate { OpenFolder(Paths.SaveBackupDir); };

            _lstBackups = new ListBox();
            _lstBackups.Location = new Point(16, 62);
            _lstBackups.Size = new Size(984, 172);
            _lstBackups.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _lstBackups.Font = new Font("Consolas", 9F);
            g2.Controls.AddRange(new Control[] { bBackup, bRestore, bOpenB, _lstBackups });

            GroupBox g3 = new GroupBox();
            g3.Text = "快捷入口";
            g3.Location = new Point(12, 372);
            g3.Size = new Size(1030, 150);
            g3.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Button b1 = MakeButton("打开 Mods 文件夹", 140);
            b1.Location = new Point(16, 28);
            b1.Click += delegate { OpenFolder(Paths.ModsDir); };
            Button b2 = MakeButton("打开游戏目录", 120);
            b2.Location = new Point(164, 28);
            b2.Click += delegate { OpenFolder(_s.GameDir); };
            Button b3 = MakeButton("打开管理器数据目录", 150);
            b3.Location = new Point(292, 28);
            b3.Click += delegate { OpenFolder(Paths.ManagerDir); };
            Button b4 = MakeButton("清空 Thunderstore 缓存", 170);
            b4.Location = new Point(450, 28);
            b4.Click += delegate
            {
                try
                {
                    if (File.Exists(Paths.TsCacheFile)) File.Delete(Paths.TsCacheFile);
                    SetStatus("缓存已清空，下次进“在线安装”会重新联网抓取。");
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message); }
            };
            Label about = new Label();
            about.AutoSize = false;
            about.Size = new Size(990, 74);
            about.Location = new Point(16, 64);
            about.ForeColor = Color.DimGray;
            about.Text = "关于：本工具不修改游戏文件，只做三件事——往 Mods 目录安装/移除 mod、通过 Mods\\lovely\\blacklist.txt 启用/禁用 mod、读写存档备份。\r\n"
                + "禁用原理和游戏内 Mods 菜单一致；改动后需要重启游戏才生效。卸载默认会先备份到管理器数据目录，随时可以手动取回。\r\n"
                + "mod 来源：thunderstore.io/c/balatro（在线安装页）或本地 zip / 单个 .lua（本地安装按钮）。";
            g3.Controls.AddRange(new Control[] { b1, b2, b3, b4, about });

            host.Controls.AddRange(new Control[] { g1, g2, g3 });
            tab.Controls.Add(host);
            return tab;
        }

        // ================= helpers =================

        private static TableLayoutPanel Grid(int rows)
        {
            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Fill;
            t.ColumnCount = 1;
            t.RowCount = rows;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            return t;
        }

        private static Button MakeButton(string text, int width)
        {
            Button b = new Button();
            b.Text = text;
            b.Width = width;
            b.Height = 28;
            b.Margin = new Padding(3, 0, 3, 0);
            return b;
        }

        private void SaveSettings()
        {
            if (_persistPreferences) _s.Save();
        }

        private void SetStatus(string text)
        {
            _statusLabel.Text = text;
        }

        private void OpenFolder(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { MessageBox.Show(this, "目录不存在：" + dir, "提示"); return; }
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "打开失败"); }
        }

        private void RunAsync(string busy, Func<object> work, Action<object> done)
        {
            SetStatus(busy);
            Task<object> task = Task.Factory.StartNew<object>(work);
            task.ContinueWith(delegate (Task<object> t)
            {
                Exception err = t.Exception;
                object result = null;
                if (t.Status == TaskStatus.RanToCompletion) result = t.Result;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (err != null)
                        {
                            SaveManager.AddLog("操作失败(" + busy + ")：" + err.ToString());
                            SetStatus("操作失败：" + err.GetBaseException().Message);
                            if (!_suppressDialogs)
                                MessageBox.Show(this, err.GetBaseException().GetType().Name + "\r\n\r\n" + err.GetBaseException().Message
                                    + "\r\n\r\n详细信息已写入 manager.log", "出错了", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        done(result);
                    });
                }
                catch { }
            });
        }

        private ModInfo SelectedMod()
        {
            if (_lvMods.SelectedItems.Count == 0) return null;
            return _lvMods.SelectedItems[0].Tag as ModInfo;
        }

        // ================= mods tab =================

        private void RefreshAll()
        {
            RefreshMods();
            RefreshGameState();
            RefreshLogList();
            RefreshBackups();
            if (_ts.Count > 0) FillOnline();
            SetStatus("已刷新。");
        }

        private void RefreshGameState()
        {
            StringBuilder sb = new StringBuilder();
            string lovely = GameLocator.LovelyVersion(_s.GameDir);
            bool exe = _s.GameDir.Length > 0 && File.Exists(Path.Combine(_s.GameDir, "Balatro.exe"));
            bool smods = Directory.Exists(Path.Combine(Paths.ModsDir, "smods"));
            sb.Append(exe ? "游戏目录 OK" : "游戏目录未设置或无效");
            sb.Append("   ·   Lovely: ").Append(lovely == null ? "缺失" : (lovely.Length == 0 ? "已装(版本未知)" : lovely));
            sb.Append("   ·   Steamodded: ").Append(smods ? "已装" : "缺失");
            if (ModLibrary.IsGameRunning()) sb.Append("   ·   游戏正在运行（改动需重启生效）");
            _lblGameState.Text = sb.ToString();
            _lblGameState.ForeColor = (exe && lovely != null && smods) ? Color.FromArgb(0, 110, 0) : Color.Firebrick;
        }

        private void RefreshMods()
        {
            string keep = "";
            if (SelectedMod() != null) keep = SelectedMod().FolderName;
            _mods = ModLibrary.Scan();
            FillModList(keep);
        }

        private void FillModList(string selectFolder)
        {
            IntPtr listHandle = _lvMods.Handle;
            ListViewItem chosen = null;
            _lvMods.BeginUpdate();
            _lvMods.Items.Clear();
            foreach (ModInfo m in _mods)
            {
                ListViewItem it = new ListViewItem(m.DisplayName.Length > 0 ? m.DisplayName : m.FolderName);
                it.SubItems.Add(m.Version.Parts.Count > 0 ? m.Version.Raw : "-");
                it.SubItems.Add(m.Author.Length > 0 ? m.Author : "-");
                it.SubItems.Add(m.Enabled ? "启用" : "禁用");
                it.SubItems.Add(m.UpdateTo.Length > 0 ? "可更新 " + m.UpdateTo : "-");
                it.SubItems.Add(m.IsFramework ? "框架" : (m.IsFile ? "单文件" : (m.MetadataKind == "manifest" ? "Thunderstore" : "文件夹")));
                it.SubItems.Add(FirstLine(m.Description));
                it.Tag = m;
                if (!m.Enabled) it.ForeColor = Color.Gray;
                if (m.NestingProblem || !m.HasMetadata) it.ForeColor = Color.Firebrick;
                if (m.UpdateTo.Length > 0 && m.Enabled) it.BackColor = Color.FromArgb(255, 250, 205);
                _lvMods.Items.Add(it);
                if (!string.IsNullOrEmpty(selectFolder) && m.FolderName.Equals(selectFolder, StringComparison.OrdinalIgnoreCase)) chosen = it;
            }
            _lvMods.EndUpdate();
            if (chosen == null && _lvMods.Items.Count > 0) chosen = _lvMods.Items[0];
            if (chosen != null) { chosen.Selected = true; chosen.Focused = true; }
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.IndexOfAny(new char[] { '\r', '\n' });
            string r = i < 0 ? s : s.Substring(0, i);
            return r.Length > 120 ? r.Substring(0, 120) + "..." : r;
        }

        private void ShowModDetail()
        {
            ModInfo m = SelectedMod();
            if (m == null) return;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("名称：" + (m.DisplayName.Length > 0 ? m.DisplayName : m.FolderName) + "    文件夹：" + m.FolderName);
            sb.AppendLine("版本：" + (m.Version.Parts.Count > 0 ? m.Version.Raw : "未知") + "    作者：" + (m.Author.Length > 0 ? m.Author : "未知") + "    优先级：" + m.Priority);
            sb.AppendLine("状态：" + (m.Enabled ? "启用" : "禁用") + "    元数据：" + (m.HasMetadata ? m.MetadataKind : "无") + "    大小：" + Fs.Human(m.SizeBytes));
            if (m.UpdateTo.Length > 0) sb.AppendLine("可更新到：" + m.UpdateTo + "（Thunderstore 最新版）");
            if (m.TsNamespace.Length > 0) sb.AppendLine("来源：Thunderstore " + m.TsNamespace + "/" + m.TsPackage);
            sb.AppendLine("路径：" + m.FullPath);
            if (m.MainFile.Length > 0) sb.AppendLine("入口文件：" + m.MainFile);
            if (m.Website.Length > 0) sb.AppendLine("主页：" + m.Website);
            if (m.Dependencies.Count > 0) sb.AppendLine("依赖：" + string.Join("，", m.Dependencies.ToArray()));
            if (m.Conflicts.Count > 0) sb.AppendLine("冲突声明：" + string.Join("，", m.Conflicts.ToArray()));
            if (m.Provides.Count > 0) sb.AppendLine("提供：" + string.Join("，", m.Provides.ToArray()));
            if (m.NestingProblem) sb.AppendLine("!! 目录套娃：" + m.NestedInner + "（会导致加载失败）");
            if (m.Description.Length > 0) { sb.AppendLine(); sb.AppendLine(m.Description); }
            _txtModDetail.Text = sb.ToString();
        }

        private void ToggleSelected()
        {
            ModInfo m = SelectedMod();
            if (m == null) { MessageBox.Show(this, "先在列表里选一个 mod。", "提示"); return; }
            if (m.IsFramework) { MessageBox.Show(this, "Steamodded 框架本身不能禁用，否则所有 mod 都不会加载。", "提示"); return; }
            try
            {
                bool wasEnabled = m.Enabled;
                ModLibrary.SetEnabled(m, !wasEnabled);
                SaveManager.AddLog((wasEnabled ? "禁用 " : "启用 ") + m.FolderName);
                RefreshMods();
                SetStatus((wasEnabled ? "已禁用 " : "已启用 ") + m.FolderName + "（重启游戏后生效）");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "操作失败"); }
        }

        private void SetAll(bool enabled)
        {
            if (MessageBox.Show(this, (enabled ? "启用" : "禁用") + "全部 mod？\r\n（Steamodded 框架不会被禁用）", "确认", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            try
            {
                ModLibrary.SetAllEnabled(_mods, enabled);
                RefreshMods();
                SetStatus("已" + (enabled ? "启用" : "禁用") + "全部 mod。");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "操作失败"); }
        }

        private void UninstallSelected()
        {
            ModInfo m = SelectedMod();
            if (m == null) { MessageBox.Show(this, "先在列表里选一个 mod。", "提示"); return; }
            if (m.IsFramework && MessageBox.Show(this, "这是 Steamodded 框架，删掉后所有 mod 都不会加载。确定继续？", "警告", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            DialogResult r = MessageBox.Show(this,
                "卸载「" + m.FolderName + "」？\r\n\r\n【是】= 移到管理器备份区（推荐，可在管理器数据目录取回）\r\n【否】= 直接删除到回收站\r\n【取消】= 什么都不做",
                "卸载 mod", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            try
            {
                if (r == DialogResult.Yes)
                {
                    string dir = ModLibrary.UninstallToBackup(m);
                    SaveManager.AddLog("卸载(备份) " + m.FolderName + " -> " + dir);
                    SetStatus("已卸载并备份到 " + dir);
                }
                else if (r == DialogResult.No)
                {
                    ModLibrary.UninstallToRecycleBin(m);
                    SaveManager.AddLog("卸载(回收站) " + m.FolderName);
                    SetStatus("已删除到回收站。");
                }
                else return;
                RefreshMods();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "卸载失败"); }
        }

        private void OpenSelectedFolder()
        {
            ModInfo m = SelectedMod();
            if (m == null) { OpenFolder(Paths.ModsDir); return; }
            OpenFolder(m.IsFile ? Path.GetDirectoryName(m.FullPath) : m.FullPath);
        }

        private void InstallLocal()
        {
            OpenFileDialog f = new OpenFileDialog();
            f.Title = "选择要安装的 mod（zip 或单个 .lua）";
            f.Filter = "Mod 包 (*.zip;*.lua)|*.zip;*.lua|所有文件 (*.*)|*.*";
            if (f.ShowDialog(this) != DialogResult.OK) return;
            string path = f.FileName;
            RunAsync("正在安装 " + Path.GetFileName(path) + " ...", delegate
            {
                ModLibrary.InstallResult r;
                if (path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) r = ModLibrary.InstallLua(path, null, false);
                else r = ModLibrary.InstallZip(path, null, false);
                return r;
            },
            delegate (object o)
            {
                ModLibrary.InstallResult r = (ModLibrary.InstallResult)o;
                if (r.Warnings.Count > 0 && !r.Replaced && File.Exists(path))
                {
                    if (MessageBox.Show(this, "已经存在同名 mod：" + r.Name + "\r\n要覆盖它吗？", "已存在", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        RunAsync("正在覆盖安装…", delegate
                        {
                            if (path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) return ModLibrary.InstallLua(path, r.Name, true);
                            return ModLibrary.InstallZip(path, r.Name, true);
                        },
                        delegate (object o2)
                        {
                            ModLibrary.InstallResult r2 = (ModLibrary.InstallResult)o2;
                            SaveManager.AddLog("覆盖安装 " + r2.Name);
                            RefreshMods();
                            SetStatus("已覆盖安装 " + r2.Name + "（重启游戏后生效）");
                            WarnInstall(r2);
                        });
                        return;
                    }
                    return;
                }
                SaveManager.AddLog("本地安装 " + r.Name + " 来自 " + path);
                RefreshMods();
                SetStatus("已安装 " + r.Name + "（重启游戏后生效）");
                WarnInstall(r);
            });
        }

        private void WarnInstall(ModLibrary.InstallResult r)
        {
            if (r.Warnings.Count == 0 || _suppressDialogs) return;
            MessageBox.Show(this, "安装完成，但有以下提示：\r\n\r\n" + string.Join("\r\n", r.Warnings.ToArray()), "提示");
        }

        private void CheckUpdates()
        {
            RunAsync("正在获取 Thunderstore 最新版本信息…", delegate
            {
                return Thunderstore.Load(false);
            },
            delegate (object o)
            {
                List<TsPackage> all = (List<TsPackage>)o;
                _ts = all;
                if (_ts.Count == 0)
                {
                    if (!_suppressDialogs)
                        MessageBox.Show(this, "获取在线列表失败：" + Thunderstore.LastError, "无法检查更新");
                    return;
                }
                int n = Thunderstore.MarkUpdates(_mods, _ts);
                FillModList(null);
                SetStatus(n == 0 ? "没有发现可更新的 mod。" : "发现 " + n + " 个 mod 可以更新（黄色高亮行）。");
            });
        }

        /// <summary>Headless diagnostic: reproduces the fetch / parse / match / UI-fill path and writes the outcome.</summary>
        internal static string SelfTest(string outFile)
        {
            StringBuilder sb = new StringBuilder();
            bool pass = true;
            MainForm f = null;
            try
            {
                f = new MainForm(false, false);
                f._suppressDialogs = true;
                IntPtr h = f.Handle;   // create the window handle so BeginInvoke works
                sb.AppendLine("== 环境 ==");
                sb.AppendLine("游戏目录: " + f._s.GameDir);
                sb.AppendLine("Lovely  : " + Report.LovelyText(f._s.GameDir));

                sb.AppendLine();
                sb.AppendLine("== 1. 扫描 mod ==");
                f.RefreshMods();
                sb.AppendLine("扫描到 " + f._mods.Count + " 项");
                foreach (ModInfo m in f._mods) sb.AppendLine("  " + m.FolderName + " v" + m.Version.Raw + " 元数据=" + m.MetadataKind);

                sb.AppendLine();
                sb.AppendLine("== 2. 获取在线列表 ==");
                List<TsPackage> all = Thunderstore.Load(false);
                sb.AppendLine("包数量=" + all.Count + " LastError=[" + Thunderstore.LastError + "] CacheFresh=" + Thunderstore.CacheIsFresh());

                sb.AppendLine();
                sb.AppendLine("== 3. 更新匹配 ==");
                int n = Thunderstore.MarkUpdates(f._mods, all);
                foreach (ModInfo m in f._mods)
                {
                    TsPackage hit = Thunderstore.FindByName(all, m);
                    sb.AppendLine("  " + m.FolderName + " 本地=" + m.Version.Raw + " 线上=" + (hit == null ? "(未匹配到)" : hit.Name + " " + hit.Version) + " 可更新=" + (m.UpdateTo.Length > 0 ? m.UpdateTo : "否"));
                }
                sb.AppendLine("可更新数量=" + n);

                sb.AppendLine();
                sb.AppendLine("== 4. 填回列表控件（检查更新按钮走的就是这里） ==");
                f.FillModList(null);
                sb.AppendLine("FillModList(null) OK，行数=" + f._lvMods.Items.Count);
                f.FillModList("");
                sb.AppendLine("FillModList(\"\") OK，行数=" + f._lvMods.Items.Count);
                if (f._mods.Count > 0)
                {
                    f.FillModList(f._mods[0].FolderName);
                    sb.AppendLine("按文件夹名选中 OK，选中数=" + f._lvMods.SelectedItems.Count);
                }

                sb.AppendLine();
                sb.AppendLine("== 5. 异步通道 (RunAsync + BeginInvoke) ==");
                bool done = false;
                string got = null;
                f.RunAsync("selftest", delegate { return (object)"ping"; }, delegate (object o) { got = (string)o; done = true; });
                DateTime t0 = DateTime.Now;
                while (!done && (DateTime.Now - t0).TotalSeconds < 30) { Application.DoEvents(); System.Threading.Thread.Sleep(30); }
                sb.AppendLine(done ? "异步回调 OK，返回=" + got : "!! 异步回调超时（BeginInvoke 没回到 UI 线程）");
                if (!done) pass = false;

                sb.AppendLine();
                sb.AppendLine("== 6. mod 介绍 (README) ==");
                TsPackage target = null;
                foreach (TsPackage p in all) if (p.Name == "JokerDisplay") target = p;
                if (target == null) sb.AppendLine("在线列表里没有 JokerDisplay，跳过");
                else
                {
                    try
                    {
                        string md = Thunderstore.ReadmeFor(target);
                        sb.AppendLine("读取到 " + md.Length + " 字，前 160 字：");
                        sb.AppendLine(md.Length > 160 ? md.Substring(0, 160).Replace("\n", " / ") : md);
                        sb.AppendLine("缓存文件：" + Thunderstore.ReadmeCachePath(target));
                        if (md.Length < 100) pass = false;

                        sb.AppendLine();
                        sb.AppendLine("== 7. 界面冒烟测试（截图输出到 selftest_*.png） ==");
                        string dir = Path.GetDirectoryName(outFile);
                        f._ts = all;
                        f.SelectOnlineSource("thunderstore");     // 固定从 Thunderstore 开始，避免受上次保存的来源影响
                        Application.DoEvents();
                        f._ts = all;
                        f.SelectOnlineTab();
                        f.FillCategories();
                        f.FillOnline();
                        f.Show();
                        Application.DoEvents();
                        bool autoOrig = Thunderstore.AutoTranslate;
                        f._chkAutoTranslate.Checked = true;    // 自动翻译：选中的 mod 应该自己翻好
                        sb.AppendLine("已开启自动翻译: " + Thunderstore.AutoTranslate);
                        sb.AppendLine("选中 JokerDisplay 行: " + f.SelectOnlinePackage("JokerDisplay"));
                        Application.DoEvents();
                        DateTime tp = DateTime.Now;
                        while (f._detailReadme == null && (DateTime.Now - tp).TotalSeconds < 90)
                        {
                            Application.DoEvents();
                            System.Threading.Thread.Sleep(100);
                        }
                        Application.DoEvents();
                        sb.AppendLine("右侧面板介绍: " + (f._detailReadme == null ? "!! 没加载出来" : f._detailReadme.Length + " 字"));
                        if (f._detailReadme == null) pass = false;
                        WinCapture.Save(f.Handle, Path.Combine(dir, "selftest_main.png"));
                        sb.AppendLine("主窗口截图: " + Path.Combine(dir, "selftest_main.png") + "  在线列表行数=" + f._lvOnline.Items.Count);

                        sb.AppendLine();
                        sb.AppendLine("== 7b. 自动翻译（没有再手点按钮，截图 selftest_panel_zh.png） ==");
                        DateTime tz = DateTime.Now;
                        while (!f.ShowingTranslated && (DateTime.Now - tz).TotalSeconds < 150)
                        {
                            Application.DoEvents();
                            System.Threading.Thread.Sleep(100);
                        }
                        sb.AppendLine(f.ShowingTranslated ? "侧栏自动翻译完成，用时 " + Math.Round((DateTime.Now - tz).TotalSeconds) + " 秒（源：" + Thunderstore.CurrentSource + "）" : "!! 自动翻译没发生");
                        if (!f.ShowingTranslated) pass = false;
                        Application.DoEvents();
                        WinCapture.Save(f.Handle, Path.Combine(dir, "selftest_panel_zh.png"));
                        sb.AppendLine("侧栏中文截图: " + Path.Combine(dir, "selftest_panel_zh.png"));
                        f._chkAutoTranslate.Checked = autoOrig;   // 还原用户原来的设置

                        sb.AppendLine();
                        sb.AppendLine("== 9. GitHub 源 ==");
                        MainForm.LoadTrace.Clear();
                        f.SelectOnlineSource("github");
                        Application.DoEvents();
                        sb.AppendLine("  诊断: 来源下拉=" + f._cbSource.SelectedItem + " code=" + f.CurrentSourceCode
                            + " 分类=" + (f._cbCategory.SelectedItem as string) + " 列表数=" + f._ts.Count
                            + " 首项来源=" + (f._ts.Count > 0 ? f._ts[0].Source + ":" + f._ts[0].Owner + "/" + f._ts[0].Name : "-"));
                        DateTime tg = DateTime.Now;
                        while (f._ts.Count == 0 && (DateTime.Now - tg).TotalSeconds < 60)
                        {
                            Application.DoEvents();
                            System.Threading.Thread.Sleep(150);
                        }
                        sb.AppendLine("GitHub 搜索到 " + f._ts.Count + " 个仓库" + (GitHub.LastError.Length > 0 ? "  提示:" + GitHub.LastError : ""));
                        sb.AppendLine("  加载轨迹: " + string.Join(" | ", MainForm.LoadTrace.ToArray()));
                        if (f._ts.Count == 0) pass = false;
                        if (f._ts.Count > 0)
                        {
                            TsPackage first = f._ts[0];
                            string derr;
                            bool ok = GitHub.ResolveDownload(first, out derr);
                            sb.AppendLine("  示例: " + first.Owner + "/" + first.Name + "  tag=" + first.Version + "  下载=" + (first.DownloadUrl.Length > 0 ? "已解析" : "失败 " + derr) + "  星标=" + first.Rating);
                            if (!ok) pass = false;
                            f.SelectOnlinePackage(first.Name);
                            DateTime tg2 = DateTime.Now;
                            while (f._detailReadme == null && (DateTime.Now - tg2).TotalSeconds < 60)
                            {
                                Application.DoEvents();
                                System.Threading.Thread.Sleep(150);
                            }
                            sb.AppendLine("  README: " + (f._detailReadme == null ? "!! 没加载" : f._detailReadme.Length + " 字"));
                        }
                        Application.DoEvents();
                        WinCapture.Save(f.Handle, Path.Combine(dir, "selftest_github.png"));
                        sb.AppendLine("GitHub 截图: " + Path.Combine(dir, "selftest_github.png"));

                        sb.AppendLine();
                        sb.AppendLine("== 9b. 自动刷新回归检查（GitHub 源静置 15 秒不应再发起请求） ==");
                        int before = MainForm.LoadCount;
                        DateTime idle = DateTime.Now;
                        while ((DateTime.Now - idle).TotalSeconds < 15)
                        {
                            Application.DoEvents();
                            System.Threading.Thread.Sleep(100);
                        }
                        int extra = MainForm.LoadCount - before;
                        sb.AppendLine("静置 15 秒内额外加载次数 = " + extra + (extra == 0 ? "（正常）" : "  !! 又自动刷新了"));
                        if (extra != 0) pass = false;

                        sb.AppendLine();
                        sb.AppendLine("== 10. 精选清单源 ==");
                        string ierr;
                        List<TsPackage> idx = GitHub.FromAwesomeList(out ierr);
                        sb.AppendLine("精选清单解析到 " + idx.Count + " 个 GitHub 仓库" + (ierr.Length > 0 ? "  " + ierr : ""));
                        if (idx.Count == 0) pass = false;

                        f.SelectOnlineSource("thunderstore");
                        ReadmeForm rf = new ReadmeForm(target, Thunderstore.DescribeForViewer(target, all), md);
                        rf.StartPosition = FormStartPosition.Manual;
                        rf.Location = new Point(20, 20);
                        rf.Show();
                        Application.DoEvents();
                        System.Threading.Thread.Sleep(1200);
                        Application.DoEvents();
                        WinCapture.Save(rf.Handle, Path.Combine(dir, "selftest_readme.png"));
                        sb.AppendLine("大窗口截图: " + Path.Combine(dir, "selftest_readme.png") + "  封面图: " + (Thunderstore.EnsureIcon(target) ?? "(无)"));
                        rf.Close();
                        f.Hide();
                    }
                    catch (Exception ex) { sb.AppendLine("读取介绍失败：" + ex.Message); pass = false; }
                }

                sb.AppendLine();
                sb.AppendLine("SELFTEST RESULT: " + (pass ? "PASS" : "FAIL"));
            }
            catch (Exception ex)
            {
                sb.AppendLine();
                sb.AppendLine("SELFTEST RESULT: FAIL");
                sb.AppendLine(ex.ToString());
            }
            finally
            {
                try { f.Dispose(); } catch { }
            }
            string text = sb.ToString();
            try
            {
                Fs.EnsureDir(Path.GetDirectoryName(outFile));
                File.WriteAllText(outFile, text, new UTF8Encoding(false));
            }
            catch { }
            return text;
        }

        // ================= online tab =================

        internal void SelectOnlineTab()
        {
            if (_tabs != null && _onlineTab != null) _tabs.SelectedTab = _onlineTab;
        }

        internal bool ShowingTranslated { get { return _showDetailZh; } }

        /// <summary>Selects a package row by name (used by the headless self test).</summary>
        internal bool SelectOnlinePackage(string name)
        {
            foreach (ListViewItem it in _lvOnline.Items)
            {
                TsPackage p = it.Tag as TsPackage;
                if (p == null || !p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                _lvOnline.SelectedItems.Clear();      // single target, not additive
                _lvOnline.FocusedItem = it;
                it.Selected = true;
                it.EnsureVisible();
                ShowOnlineDetail();
                return true;
            }
            return false;
        }

        private void LoadOnline(bool force)
        {
            if (_offlineCheck) return;
            string code = CurrentSourceCode;
            string key = code + "|" + (_txtSearch.Text ?? "").Trim() + "|" + ((_cbCategory.SelectedItem as string) ?? "");
            if (!force && key == _lastLoadKey && (DateTime.Now - _lastLoadAt).TotalSeconds < 2.0)
            {
                LoadTrace.Add("SKIP " + key);
                return;   // 同一请求紧接着重复触发，忽略
            }
            if (_tsLoading && key == _loadingKey)
            {
                LoadTrace.Add("SKIP-INFLIGHT " + key);
                return;   // 同一个请求已经在跑了
            }
            _lastLoadKey = key;
            _lastLoadAt = DateTime.Now;
            _loadCount++;
            int token = ++_loadToken;      // 新请求会作废掉还在路上的旧请求（例如切换来源时）
            _loadingKey = key;
            _tsLoading = true;
            LoadTrace.Add("LOAD " + key);
            string querySnapshot = _txtSearch.Text;
            string categorySnapshot = _cbCategory.SelectedItem as string;
            string label = code == "all" ? "全部来源" : code == "custom" ? "自定义仓库" : code == "github" ? "GitHub" : (code == "index" ? "精选清单" : "Thunderstore");
            _lblOnlineStatus.Text = "正在联网获取 " + label + " 列表…";
            RunAsync("正在从 " + label + " 获取 mod 列表…", delegate
            {
                object[] box = new object[2];
                if (code == "github")
                {
                    string err;
                    box[0] = GitHub.SearchPackages(querySnapshot, categorySnapshot, out err);
                    box[1] = err;
                }
                else if (code == "all")
                {
                    string err;
                    List<TsPackage> combined = new List<TsPackage>(Thunderstore.Load(force));
                    string warnings = Thunderstore.LastError;
                    combined.AddRange(GitHub.SearchPackages("", "balatro-mod", out err)); warnings += err;
                    try { combined.AddRange(GitHub.FromAwesomeList(out err)); warnings += err; } catch (Exception ex) { warnings += "精选清单：" + ex.Message; }
                    combined.AddRange(LoadCustomSources(out err)); warnings += err;
                    HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    List<TsPackage> unique = new List<TsPackage>();
                    foreach (TsPackage package in combined) if (seen.Add(PackageKey(package))) unique.Add(package);
                    box[0] = unique; box[1] = warnings;
                }
                else if (code == "custom")
                {
                    string err;
                    box[0] = LoadCustomSources(out err);
                    box[1] = err;
                }
                else if (code == "index")
                {
                    string err;
                    box[0] = GitHub.FromAwesomeList(out err);
                    box[1] = err;
                }
                else
                {
                    box[0] = Thunderstore.Load(force);
                    box[1] = Thunderstore.LastError;
                }
                return (object)box;
            },
            delegate (object o)
            {
                if (token != _loadToken)
                {
                    LoadTrace.Add("STALE " + key);   // 已经有更新的请求了，这份结果丢弃
                    return;
                }
                _tsLoading = false;
                _loadingKey = "";
                object[] box = (object[])o;
                _ts = (List<TsPackage>)box[0];
                string err = (string)box[1];
                if (_ts.Count == 0)
                {
                    _lblOnlineStatus.Text = "获取失败：" + (err.Length > 0 ? err : "没有结果");
                    _lblOnlineStatus.ForeColor = Color.Firebrick;
                    return;
                }
                _lblOnlineStatus.ForeColor = Color.DimGray;
                FillCategories();
                FillOnline();
                if (code == "github")
                {
                    int n = GitHub.MarkUpdates(_mods);
                    if (n > 0) { FillModList(null); SetStatus("GitHub 上发现 " + n + " 个可更新的 mod。"); }
                }
                if (err.Length > 0) SetStatus("注意：" + err);
            });
        }

        private void FillCategories()
        {
            string keep = _cbCategory.SelectedItem as string;
            _fillingCategories = true;
            try
            {
                _cbCategory.Items.Clear();
                string code = CurrentSourceCode;
                if (code == "github")
                {
                    foreach (string t in GitHub.TopicFilters) _cbCategory.Items.Add(t);
                }
                else if (code == "index" || code == "custom")
                {
                    _cbCategory.Items.Add("全部");
                }
                else
                {
                    foreach (string c in Thunderstore.AllCategories(_ts)) _cbCategory.Items.Add(c);
                }
                if (keep != null && _cbCategory.Items.Contains(keep)) _cbCategory.SelectedItem = keep;
                else if (_cbCategory.Items.Count > 0) _cbCategory.SelectedIndex = 0;
            }
            finally { _fillingCategories = false; }
        }

        private static int SourceIndexFromCode(string code)
        {
            if (code == "all") return 4;
            if (code == "custom") return 3;
            if (code == "github") return 1;
            if (code == "index") return 2;
            return 0;
        }

        private static string SourceCodeFromIndex(int i)
        {
            if (i == 4) return "all";
            if (i == 3) return "custom";
            if (i == 1) return "github";
            if (i == 2) return "index";
            return "thunderstore";
        }

        private string CurrentSourceCode
        {
            get { return SourceCodeFromIndex(_cbSource == null ? 0 : _cbSource.SelectedIndex); }
        }

        private void OnSourceChanged()
        {
            if (_cbSource == null) return;
            _s.OnlineSource = CurrentSourceCode;
            SaveSettings();
            _ts = new List<TsPackage>();
            _lvOnline.Items.Clear();
            _txtOnlineDetail.Text = "已切换到「" + _cbSource.SelectedItem + "」源，正在加载列表…";
            _cbSort.Items.Clear();
            foreach (string m in Thunderstore.SortModesFor(CurrentSourceCode)) _cbSort.Items.Add(m);
            if (_cbSort.Items.Count > 0) _cbSort.SelectedIndex = 0;
            LoadOnline(false);
        }

        /// <summary>Switches the online source programmatically (used by the headless self test).</summary>
        internal void SelectOnlineSource(string code)
        {
            int idx = SourceIndexFromCode(code);
            if (_cbSource.SelectedIndex != idx)
            {
                _cbSource.SelectedIndex = idx;   // goes through OnSourceChanged: resets the list and loads
                return;
            }
            _ts = new List<TsPackage>();
            _lvOnline.Items.Clear();
            LoadOnline(false);
        }

        private void FillOnline()
        {
            IntPtr listHandle = _lvOnline.Handle;
            ListViewItem chosen = null;
            string selectedKey = _detailPkg == null ? "" : PackageKey(_detailPkg);
            string cat = _cbCategory.SelectedItem as string;
            string sortMode = _cbSort.SelectedItem as string;
            if (string.IsNullOrEmpty(sortMode)) sortMode = Thunderstore.SortModes[0];
            List<TsPackage> res = Thunderstore.Sort(Thunderstore.Search(_ts, _txtSearch.Text, cat, true), sortMode);
            _lvOnline.BeginUpdate();
            _lvOnline.Items.Clear();
            foreach (TsPackage p in res)
            {
                string installed = InstalledVersionOf(p);
                if (!MatchesBrowseFilter(p, installed)) continue;
                ListViewItem it = new ListViewItem(p.Name);
                it.SubItems.Add(p.Owner);
                it.SubItems.Add(p.Version);
                it.SubItems.Add(p.Source == "github" ? "未提供" : Thunderstore.Human(p.Downloads));
                it.SubItems.Add(p.Rating.ToString());
                it.SubItems.Add(ShortDate(p.DateUpdated));
                it.SubItems.Add(installed.Length > 0 ? "[已装 " + installed + "] " + FirstLine(p.Description) : FirstLine(p.Description));
                it.SubItems.Add(p.Source == "github" ? "GitHub" : "Thunderstore");
                it.SubItems.Add(BrowseState(p, installed));
                it.Tag = p;
                if (PackageKey(p) == selectedKey) chosen = it;
                if (installed.Length > 0) it.ForeColor = Theme.Green;
                if (p.Deprecated) it.ForeColor = Color.Firebrick;
                _lvOnline.Items.Add(it);
            }
            _lvOnline.EndUpdate();
            if (_lvOnline.Items.Count == 0)
            {
                ++_detailToken; _detailPkg = null; _bDetailTrans.Enabled = false;
                _lblDetailHead.Text = "没有匹配的 Mod";
                _txtOnlineDetail.Text = "试试其他关键词、分类或安装状态筛选。";
                if (_picDetail.Image != null) { _picDetail.Image.Dispose(); _picDetail.Image = null; }
                SetDetailStatus("");
            }
            if (chosen == null && _lvOnline.Items.Count > 0) chosen = _lvOnline.Items[0];
            if (chosen != null) { chosen.Selected = true; chosen.Focused = true; ShowOnlineDetail(); }
            _lblOnlineStatus.Text = "匹配 " + _lvOnline.Items.Count + " / 共 " + _ts.Count
                + "（" + (_cbSource == null ? "" : _cbSource.SelectedItem + " · ") + "按" + sortMode + "排序）";
        }

        private string InstalledVersionOf(TsPackage p)
        {
            foreach (ModInfo m in _mods)
            {
                if (p.Source == "github" && m.TsNamespace == "github" && m.TsPackage.Equals(p.Owner + "/" + p.Name, StringComparison.OrdinalIgnoreCase)) return m.Version.Raw;
                if (m.TsNamespace.Length > 0)
                {
                    if (m.TsNamespace.Equals(p.Owner, StringComparison.OrdinalIgnoreCase) && m.TsPackage.Equals(p.Name, StringComparison.OrdinalIgnoreCase)) return m.Version.Raw;
                    continue;
                }
                if (m.FolderName.Equals(p.Name, StringComparison.OrdinalIgnoreCase) || m.DisplayName.Equals(p.Name, StringComparison.OrdinalIgnoreCase) || m.Id.Equals(p.Name, StringComparison.OrdinalIgnoreCase))
                    return m.Version.Raw;
            }
            return "";
        }

        private static string ShortDate(string iso)
        {
            if (string.IsNullOrEmpty(iso) || iso.Length < 10) return iso;
            return iso.Substring(0, 10);
        }

        private void ShowOnlineDetail()
        {
            // multi-select keeps old rows selected, so follow the row the user actually clicked last
            ListViewItem item = _lvOnline.FocusedItem;
            if (item == null && _lvOnline.SelectedItems.Count > 0) item = _lvOnline.SelectedItems[0];
            if (item == null || !item.Selected) return;
            TsPackage p = item.Tag as TsPackage;
            if (p == null) return;
            if (_detailPkg == p) return;

            if (_picDetail.Image != null) { Image old = _picDetail.Image; _picDetail.Image = null; old.Dispose(); }
            _picDetail.Visible = true;
            _detailPkg = p;
            _detailReadme = null;
            _detailZhReadme = null;
            _detailZhTitle = "";
            _detailZhDesc = "";
            _showDetailZh = false;
            _bDetailTrans.Text = "翻译全部";
            _bDetailTrans.Enabled = false;
            _lblDetailHead.Text = p.Name + "   v" + p.Version
                + "\r\n作者：" + p.Owner + (p.Source == "github" ? "    星标 " + p.Rating + "    下载量未提供" : "    点赞 " + p.Rating + "    下载 " + p.Downloads) + "    大小 " + Fs.Human(p.FileSize)
                + (Thunderstore.IsModManager(p) ? "\r\n!! 这是 mod 管理器工具，不是游戏 mod，一般不需要装进 Mods" : "")
                + "\r\n" + p.Description;
            _txtOnlineDetail.Text = "正在读取这个 mod 的完整介绍…\r\n\r\n" + Thunderstore.DescribeForViewer(p, _ts);
            if (_offlineCheck) { _detailReadme = "离线示例介绍"; _picDetail.Visible = false; RenderDetail(); return; }
            SetDetailStatus("正在读取介绍…");
            int token = ++_detailToken;

            Task.Factory.StartNew<object>(delegate { return (object)Thunderstore.EnsureIcon(p); })
                .ContinueWith(delegate (Task<object> t)
                {
                    string path = (t.Status == TaskStatus.RanToCompletion) ? (string)t.Result : null;
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            if (token != _detailToken) return;
                            if (path == null) { _picDetail.Visible = false; return; }
                            try
                            {
                                byte[] bytes = File.ReadAllBytes(path);
                                using (MemoryStream ms = new MemoryStream(bytes)) _picDetail.Image = Image.FromStream(ms);
                            }
                            catch { _picDetail.Visible = false; }
                        });
                    }
                    catch { }
                });

            if (p.FileSize > (p.Source == "github" ? 200L * 1024 * 1024 : Thunderstore.ReadmeSizeLimit) && !File.Exists(Thunderstore.ReadmeCachePath(p)))
            {
                _detailReadme = "（这个包有 " + Fs.Human(p.FileSize) + "，为了不白下载整个压缩包，这里不自动读取完整说明）\r\n"
                    + "想看介绍可以点“大窗口看介绍”强制下载，或点“打开网页”去浏览器看。";
                _bDetailTrans.Enabled = false;
                SetDetailStatus("包体较大，已跳过自动读取");
                RenderDetail();
                return;
            }

            Task.Factory.StartNew<object>(delegate
            {
                try
                {
                    if (p.Source == "github" && string.IsNullOrEmpty(p.DownloadUrl))
                    {
                        string rerr;
                        GitHub.ResolveDownload(p, out rerr);
                    }
                    return (object)Thunderstore.ReadmeFor(p);
                }
                catch (Exception ex) { return (object)("!!ERR " + ex.Message); }
            }).ContinueWith(delegate (Task<object> t)
            {
                string md = (t.Status == TaskStatus.RanToCompletion) ? (string)t.Result : null;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (token != _detailToken) return;
                        if (md == null) md = "!!ERR 没有拿到内容";
                        if (p.Version.Length > 0)
                            _lblDetailHead.Text = p.Name + "   " + p.Version
                                + "\r\n作者 " + p.Owner + "    星标/点赞 " + p.Rating + "    下载 " + p.Downloads + "    大小 " + Fs.Human(p.FileSize)
                                + "\r\n" + p.Description;
                        if (md.StartsWith("!!ERR "))
                        {
                            _detailReadme = "（没能读取到完整说明：" + md.Substring(6) + "）\r\n可以点“大窗口看介绍”或“打开网页”去浏览器里看。";
                            _bDetailTrans.Enabled = false;
                            SetDetailStatus("没有读到完整说明");
                        }
                        else
                        {
                            _detailReadme = md;
                            _bDetailTrans.Enabled = true;
                            SetDetailStatus("介绍已加载（" + md.Length + " 字）。"
                                + (Thunderstore.AutoTranslate ? "自动翻译中…" : "点“翻译全部”可翻成中文。"));
                        }
                        RenderDetail();
                        if (Thunderstore.AutoTranslate && _detailReadme != null && !_detailReadme.StartsWith("（没能")) TranslateDetail();
                    });
                }
                catch { }
            });
        }

        private void SetDetailStatus(string text)
        {
            if (_lblDetailStatus != null) _lblDetailStatus.Text = text;
        }

        private void RenderDetail()
        {
            if (_detailPkg == null) return;
            TsPackage p = _detailPkg;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Thunderstore.DescribeForViewer(p, _ts));
            sb.AppendLine();
            if (_showDetailZh)
            {
                if (_detailZhTitle.Length > 0) sb.AppendLine("中译名：" + _detailZhTitle);
                if (_detailZhDesc.Length > 0) sb.AppendLine("简介（机翻）：" + _detailZhDesc);
                sb.AppendLine();
                sb.AppendLine("========== 完整介绍（机器翻译，仅供参考）==========");
                sb.AppendLine();
                sb.Append(_detailZhReadme == null ? "" : _detailZhReadme);
            }
            else
            {
                sb.AppendLine("========== 完整介绍（来自 mod 压缩包里的 README，英文原文）==========");
                sb.AppendLine();
                sb.Append(_detailReadme == null ? "（正在读取…）" : _detailReadme);
            }
            _txtOnlineDetail.Text = sb.ToString();
            _txtOnlineDetail.SelectionStart = 0;
            _bDetailTrans.Text = _showDetailZh ? "显示原文" : (_detailZhReadme != null ? "显示中文" : "翻译全部");
        }

        /// <summary>Translates title + description + full README for the mod shown in the side panel.</summary>
        internal void TranslateDetail()
        {
            if (_detailPkg == null || _detailReadme == null) return;
            if (_showDetailZh) { _showDetailZh = false; RenderDetail(); SetDetailStatus("已切回英文原文"); return; }
            if (_detailZhReadme != null) { _showDetailZh = true; RenderDetail(); SetDetailStatus("显示已缓存的中文翻译"); return; }

            TsPackage p = _detailPkg;
            string cache = Thunderstore.TranslatedCachePath(p, "zh-CN");
            try { if (File.Exists(cache)) _detailZhReadme = File.ReadAllText(cache, Encoding.UTF8); }
            catch { }
            if (_detailZhReadme != null) { _showDetailZh = true; RenderDetail(); SetDetailStatus("已读取缓存的中文翻译"); return; }

            _bDetailTrans.Enabled = false;
            _detailProgress = "正在翻译…";
            DateTime t0 = DateTime.Now;
            Task<object> task = Task.Factory.StartNew<object>(delegate
            {
                string title = "";
                string desc = "";
                try { title = Thunderstore.Translate(p.Name, "zh-CN"); }
                catch { }
                try { desc = Thunderstore.Translate(p.Description, "zh-CN"); }
                catch { }
                List<string> parts = Thunderstore.SplitForTranslation(_detailReadme, Thunderstore.ChunkSizeFor(Thunderstore.CurrentSource));
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < parts.Count; i++)
                {
                    List<string> one = Thunderstore.TranslateBatch(new List<string>(new string[] { parts[i] }), "zh-CN");
                    sb.Append(one.Count > 0 && one[0].Length > 0 ? one[0] : parts[i]);
                    _detailProgress = "正在翻译… " + (i + 1) + "/" + parts.Count + " 段（" + Thunderstore.CurrentSource + "）";
                }
                string body = sb.ToString();
                try
                {
                    Fs.EnsureDir(Paths.ReadmeCacheDir);
                    File.WriteAllText(cache, body, new UTF8Encoding(false));
                }
                catch { }
                return new string[] { title, desc, body };
            });
            task.ContinueWith(delegate (Task<object> t)
            {
                Exception err = t.Exception;
                string[] res = (err == null && t.Status == TaskStatus.RanToCompletion) ? (string[])t.Result : null;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _bDetailTrans.Enabled = true;
                        _detailProgress = "";
                        if (res == null)
                        {
                            string msg = err == null ? "没有拿到翻译结果" : err.GetBaseException().Message;
                            SetDetailStatus("翻译失败：" + msg);
                            if (!_suppressDialogs)
                                MessageBox.Show(this, "翻译失败：" + msg + "\r\n\r\n可以换一个翻译源（右上角“翻译源”）再试，或先看英文原文。", "出错了");
                            return;
                        }
                        _detailZhTitle = res[0];
                        _detailZhDesc = res[1];
                        _detailZhReadme = res[2];
                        _showDetailZh = true;
                        RenderDetail();
                        SetDetailStatus("翻译完成（" + Math.Round((DateTime.Now - t0).TotalSeconds) + " 秒 · "
                            + (Thunderstore.LastTranslateFellBack ? "所选源无响应，已自动改用 " + Thunderstore.TranslateSources[0] : Thunderstore.CurrentSource)
                            + "），已缓存");
                    });
                }
                catch { }
            });
        }

        private void OpenSelectedOnlinePage()
        {
            if (_lvOnline.SelectedItems.Count == 0) return;
            TsPackage p = _lvOnline.SelectedItems[0].Tag as TsPackage;
            if (p == null || p.PackageUrl.Length == 0) return;
            Process.Start(p.PackageUrl);
        }

        /// <summary>Shows the full introduction (metadata + the mod's own README) in a separate window.</summary>
        private void ShowOnlineReadme()
        {
            if (_lvOnline.SelectedItems.Count == 0) { MessageBox.Show(this, "先在列表里选一个 mod。", "提示"); return; }
            TsPackage p = _lvOnline.SelectedItems[0].Tag as TsPackage;
            if (p == null) return;

            string meta = Thunderstore.DescribeForViewer(p, _ts);
            string cache = Thunderstore.ReadmeCachePath(p);
            if (File.Exists(cache))
            {
                string md = "";
                try { md = File.ReadAllText(cache, Encoding.UTF8); } catch { }
                new ReadmeForm(p, meta, md).ShowDialog(this);
                return;
            }
            if (p.FileSize > Thunderstore.ReadmeSizeLimit && p.Source != "github"
                && MessageBox.Show(this, "这个包有 " + Fs.Human(p.FileSize) + "，读取说明需要下载整个压缩包（约 "
                    + Fs.Human(p.FileSize) + "）。要继续吗？\r\n\r\n选“否”可以直接用浏览器打开介绍页。", "包体较大", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                Process.Start(p.PackageUrl);
                return;
            }

            RunAsync("正在下载 " + p.Name + " 的说明文件（约 " + Fs.Human(p.FileSize) + "）…", delegate
            {
                try { return (object)Thunderstore.ReadmeFor(p); }
                catch (Exception ex) { return (object)("!!ERR " + ex.Message); }
            },
            delegate (object o)
            {
                string md = (string)o;
                if (md.StartsWith("!!ERR "))
                {
                    SetStatus("读取说明文件失败，已显示简介信息。");
                    new ReadmeForm(p, meta,
                        "（没能读取到完整说明：" + md.Substring(6) + "）\r\n\r\n点上面的“打开 Thunderstore 页面”可以在浏览器里看完整介绍。").ShowDialog(this);
                    return;
                }
                SetStatus("介绍已读取并缓存，下次点开是秒开。");
                new ReadmeForm(p, meta, md).ShowDialog(this);
            });
        }

        private void InstallSelectedOnline() { InstallOnline(false); }
        private void InstallSelectedOnlineWithDeps() { InstallOnline(true); }

        private void InstallOnline(bool withDeps)
        {
            if (_lvOnline.SelectedItems.Count == 0) { MessageBox.Show(this, "先选一个要安装的 mod。", "提示"); return; }
            List<TsPackage> picks = new List<TsPackage>();
            foreach (ListViewItem it in _lvOnline.SelectedItems)
            {
                TsPackage p = it.Tag as TsPackage;
                if (p != null) picks.Add(p);
            }
            string names = "";
            bool hasTool = false;
            foreach (TsPackage p in picks)
            {
                names += (names.Length > 0 ? "、" : "") + p.Name;
                if (Thunderstore.IsModManager(p)) hasTool = true;
            }
            if (MessageBox.Show(this, "安装：" + names + (withDeps ? "\r\n（包含依赖）" : "")
                + (hasTool ? "\r\n\r\n!! 注意：其中包含 mod 管理器工具（如 r2modman），它不是游戏 mod，装进 Mods 没有意义。" : "")
                + "\r\n\r\n已存在的同名 mod 会被覆盖。继续吗？", "确认安装", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            if (ModLibrary.IsGameRunning() && MessageBox.Show(this, "游戏正在运行，安装完成后需要重启游戏才会生效。现在继续安装吗？", "提示", MessageBoxButtons.YesNo) != DialogResult.Yes) return;

            RunAsync("正在下载并安装…", delegate
            {
                List<string> lines = new List<string>();
                foreach (TsPackage p in picks)
                {
                    Thunderstore.InstallOutcome o = Thunderstore.Install(p, withDeps);
                    foreach (string s in o.Installed) lines.Add("已安装 " + s);
                    foreach (string s in o.Warnings) lines.Add("注意：" + s);
                }
                return lines;
            },
            delegate (object o)
            {
                List<string> lines = (List<string>)o;
                RefreshMods();
                FillOnline();
                SetStatus(lines.Count > 0 ? lines[lines.Count - 1] : "完成");
                if (!_suppressDialogs)
                    MessageBox.Show(this, (lines.Count == 0 ? "没有安装任何东西。" : string.Join("\r\n", lines.ToArray())) + "\r\n\r\n重启游戏后生效。", "安装结果");
            });
        }

        // ================= report tab =================

        private void RunReport()
        {
            RefreshMods();
            _txtReport.Text = "正在检测…";
            RunAsync("正在检测…", delegate
            {
                return Report.Build(_s, _mods, _chkLog.Checked);
            },
            delegate (object o)
            {
                _txtReport.Text = (string)o;
                _txtReport.SelectionStart = 0;
                List<string> ok;
                List<Issue> issues = Deps.Analyze(_mods, _s.GameDir, out ok);
                SetStatus("检测完成：错误 " + Deps.Count(issues, "错误") + "，警告 " + Deps.Count(issues, "警告") + "，提示 " + Deps.Count(issues, "提示"));
            });
        }

        // ================= log tab =================

        private void RefreshLogList()
        {
            string keep = _cbLogs.SelectedItem as string;
            _cbLogs.Items.Clear();
            foreach (string f in ModLibrary.LogFiles()) _cbLogs.Items.Add(f);
            if (keep != null && _cbLogs.Items.Contains(keep)) _cbLogs.SelectedItem = keep;
            else if (_cbLogs.Items.Count > 0) _cbLogs.SelectedIndex = 0;
            else _txtLog.Text = "还没有日志。启动一次游戏后，Lovely 会把日志写到：\r\n" + Paths.LogDir;
        }

        private void LoadLog()
        {
            string path = _cbLogs.SelectedItem as string;
            if (path == null) { RefreshLogList(); return; }
            try
            {
                if (_chkProblems.Checked)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("== 关键问题行（每类最多 40 条）==\r\n");
                    int errors = 0, black = 0, nomatch = 0, invalid = 0;
                    foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        if (line.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0 && errors < 40) { sb.AppendLine(Clean(line)); errors++; continue; }
                        if (line.IndexOf("blacklist", StringComparison.OrdinalIgnoreCase) >= 0 && line.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) < 0 && black < 40) { sb.AppendLine(Clean(line)); black++; continue; }
                        if (line.IndexOf("resulted in no matches", StringComparison.OrdinalIgnoreCase) >= 0 && nomatch < 40) { sb.AppendLine(Clean(line)); nomatch++; continue; }
                        if (line.IndexOf("invalid metadata", StringComparison.OrdinalIgnoreCase) >= 0 && invalid < 40) { sb.AppendLine(Clean(line)); invalid++; continue; }
                    }
                    if (errors + black + nomatch + invalid == 0) sb.AppendLine("这一类里没有发现问题行，一切正常。");
                    _txtLog.Text = sb.ToString();
                }
                else
                {
                    string text = Fs.ReadShared(path);
                    if (text.Length > 400000) text = text.Substring(text.Length - 400000);
                    _txtLog.Text = text;
                }
                _txtLog.SelectionStart = 0;
                _txtLog.ScrollToCaret();
            }
            catch (Exception ex) { _txtLog.Text = "读取日志失败：" + ex.Message; }
        }

        private static string Clean(string line)
        {
            string s = line.Trim();
            int i = s.IndexOf("INFO - ", StringComparison.Ordinal);
            if (i >= 0) s = s.Substring(i + 7);
            return s;
        }

        // ================= settings tab =================

        private void RefreshBackups()
        {
            _backups = SaveManager.List();
            _lstBackups.Items.Clear();
            foreach (BackupItem b in _backups) _lstBackups.Items.Add(b);
        }

        private void LaunchGame()
        {
            if (_s.GameDir.Length == 0 || !File.Exists(Path.Combine(_s.GameDir, "Balatro.exe")))
            {
                MessageBox.Show(this, "没找到 Balatro.exe，请先在“设置 / 工具”页设置游戏目录。", "提示");
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(_s.GameDir, "Balatro.exe")) { WorkingDirectory = _s.GameDir });
                SetStatus("已启动游戏。");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "启动失败"); }
        }
    }

    /// <summary>Introduction window: cover image, metadata, the mod's own README and an optional machine translation.</summary>
    internal class ReadmeForm : Form
    {
        private readonly TsPackage _pkg;
        private readonly string _meta;
        private readonly string _readme;
        private readonly string _zhCache;
        private string _zh;
        private string _zhDesc = "";
        private bool _showZh;
        private volatile string _progress = "";
        private readonly Timer _pump;
        private TextBox _box;
        private PictureBox _pic;
        private Label _lblHead;
        private Label _lblStatus;
        private Button _bTrans;

        public ReadmeForm(TsPackage pkg, string meta, string readme)
        {
            _pkg = pkg;
            _meta = meta;
            _readme = readme;
            _zhCache = Thunderstore.TranslatedCachePath(pkg, "zh-CN");
            try { if (File.Exists(_zhCache)) _zh = File.ReadAllText(_zhCache, Encoding.UTF8); }
            catch { }

            Text = pkg.Name + " 的介绍  ·  v" + pkg.Version;
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1000, 780);
            MinimumSize = new Size(680, 480);
            ShowInTaskbar = false;

            Panel bar = new Panel();
            bar.Dock = DockStyle.Top;
            bar.Height = 42;
            Button bPage = new Button();
            bPage.Text = "打开 Thunderstore 页面";
            bPage.Width = 180; bPage.Height = 28; bPage.Location = new Point(10, 8);
            bPage.Click += delegate { if (pkg.PackageUrl.Length > 0) Process.Start(pkg.PackageUrl); };
            Button bSite = new Button();
            bSite.Text = "打开作者主页";
            bSite.Width = 130; bSite.Height = 28; bSite.Location = new Point(196, 8);
            bSite.Click += delegate { if (pkg.WebsiteUrl.Length > 0) Process.Start(pkg.WebsiteUrl); };
            Button bCopy = new Button();
            bCopy.Text = "复制全部";
            bCopy.Width = 92; bCopy.Height = 28; bCopy.Location = new Point(332, 8);
            Button bClose = new Button();
            bClose.Text = "关闭";
            bClose.Width = 92; bClose.Height = 28; bClose.Location = new Point(430, 8);
            bClose.Click += delegate { Close(); };
            bar.Controls.AddRange(new Control[] { bPage, bSite, bCopy, bClose });

            Panel bar2 = new Panel();
            bar2.Dock = DockStyle.Top;
            bar2.Height = 38;
            _bTrans = new Button();
            _bTrans.Width = 130; _bTrans.Height = 28; _bTrans.Location = new Point(10, 5);
            _bTrans.Click += delegate { ToggleTranslate(); };
            _lblStatus = new Label();
            _lblStatus.AutoSize = true;
            _lblStatus.Location = new Point(152, 11);
            _lblStatus.ForeColor = Color.DimGray;
            bar2.Controls.Add(_lblStatus);
            bar2.Controls.Add(_bTrans);

            Panel head = new Panel();
            head.Dock = DockStyle.Top;
            head.Height = 158;
            head.Padding = new Padding(8, 6, 8, 0);
            _pic = new PictureBox();
            _pic.Dock = DockStyle.Left;
            _pic.Width = 150;
            _pic.SizeMode = PictureBoxSizeMode.Zoom;
            _pic.BorderStyle = BorderStyle.FixedSingle;
            _pic.BackColor = Color.White;
            _lblHead = new Label();
            _lblHead.Dock = DockStyle.Fill;
            _lblHead.Padding = new Padding(12, 4, 6, 4);
            _lblHead.Text = pkg.Name + "    v" + pkg.Version + "    作者：" + pkg.Owner + "    点赞 " + pkg.Rating + "    下载 " + pkg.Downloads
                + "\r\n\r\n" + pkg.Description;
            head.Controls.Add(_lblHead);
            head.Controls.Add(_pic);

            _box = new TextBox();
            _box.Multiline = true;
            _box.ReadOnly = true;
            _box.ScrollBars = ScrollBars.Both;
            _box.WordWrap = false;
            _box.Dock = DockStyle.Fill;
            _box.Font = new Font("Consolas", 9F);
            _box.BackColor = Color.White;
            bCopy.Click += delegate
            {
                Clipboard.SetText(_box.Text);
                MessageBox.Show(this, "已复制到剪贴板。", "提示");
            };

            Controls.Add(_box);
            Controls.Add(head);
            Controls.Add(bar2);
            Controls.Add(bar);
            ActiveControl = bClose;
            CancelButton = bClose;
            RenderText();

            _pump = new Timer();
            _pump.Interval = 400;
            _pump.Tick += delegate { if (_progress.Length > 0) _lblStatus.Text = _progress; };
            _pump.Start();

            Load += delegate
            {
                RefreshButton();
                StartIcon();
                if (Thunderstore.AutoTranslate)
                {
                    if (_zh != null)
                    {
                        _showZh = true;
                        RenderText();
                        RefreshButton();
                        _lblStatus.Text = "已按设置自动显示缓存的中文翻译";
                    }
                    else
                    {
                        ToggleTranslate();
                    }
                }
            };
            FormClosed += delegate { _pump.Stop(); };
        }

        private void RefreshButton()
        {
            _bTrans.Text = _showZh ? "显示原文" : (_zh != null ? "显示中文" : "翻译成中文");
            _lblStatus.Text = _showZh
                ? "正在显示中文（机器翻译，仅供参考）"
                : (_zh != null ? "已缓存中文翻译，点“显示中文”即可" : "点“翻译成中文”调用在线翻译（免费接口，需要联网）");
        }

        private void RenderText()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(ToCrLf(_meta));
            if (_showZh && _zhDesc.Length > 0) { sb.AppendLine(); sb.AppendLine("简介（机翻）：" + _zhDesc); }
            sb.AppendLine();
            sb.AppendLine(_showZh ? "========== 完整介绍（机器翻译，仅供参考）==========" : "========== 完整介绍（来自 mod 压缩包里的 README，英文原文）==========");
            sb.AppendLine();
            sb.Append(ToCrLf(_showZh ? (_zh == null ? "" : _zh) : _readme));
            _box.Text = sb.ToString();
            _box.SelectionStart = 0;
            _box.SelectionLength = 0;
        }

        internal bool ShowingTranslated { get { return _showZh; } }
        internal bool TranslationBusy { get { return !_bTrans.Enabled; } }

        internal void ToggleTranslate()        {
            if (_showZh) { _showZh = false; RenderText(); RefreshButton(); return; }
            if (_zh != null) { _showZh = true; RenderText(); RefreshButton(); return; }

            _bTrans.Enabled = false;
            _progress = "正在翻译…";
            DateTime started = DateTime.Now;
            Task<object> task = Task.Factory.StartNew<object>(delegate
            {
                List<string> parts = Thunderstore.SplitForTranslation(_readme, 1200);
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < parts.Count; i++)
                {
                    List<string> one = Thunderstore.TranslateBatch(new List<string>(new string[] { parts[i] }), "zh-CN");
                    sb.Append(one.Count > 0 && one[0].Length > 0 ? one[0] : parts[i]);
                    _progress = "正在翻译… " + (i + 1) + "/" + parts.Count + " 段";
                }
                string desc = "";
                try { desc = Thunderstore.Translate(_pkg.Description, "zh-CN"); }
                catch { }
                string[] output = new string[] { sb.ToString(), desc };
                try
                {
                    Fs.EnsureDir(Paths.ReadmeCacheDir);
                    File.WriteAllText(_zhCache, output[0], new UTF8Encoding(false));
                }
                catch { }
                return output;
            });
            task.ContinueWith(delegate (Task<object> t)
            {
                Exception err = t.Exception;
                string[] output = null;
                if (err == null && t.Status == TaskStatus.RanToCompletion) output = (string[])t.Result;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _bTrans.Enabled = true;
                        _progress = "";
                        if (err != null || output == null)
                        {
                            string msg = err == null ? "没有拿到翻译结果" : err.GetBaseException().Message;
                            _lblStatus.Text = "翻译失败：" + msg;
                            MessageBox.Show(this, "翻译失败：" + msg + "\r\n（部分网络环境访问不了免费翻译接口，可以先看英文原文）", "出错了");
                            return;
                        }
                        _zh = output[0];
                        _zhDesc = output[1];
                        _showZh = true;
                        RenderText();
                        RefreshButton();
                        _lblStatus.Text = "翻译完成（" + Math.Round((DateTime.Now - started).TotalSeconds) + " 秒），已缓存，下次秒开";
                    });
                }
                catch { }
            });
        }

        private void StartIcon()
        {
            Task<object> task = Task.Factory.StartNew<object>(delegate { return (object)Thunderstore.EnsureIcon(_pkg); });
            task.ContinueWith(delegate (Task<object> t)
            {
                string path = (t.Status == TaskStatus.RanToCompletion) ? (string)t.Result : null;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (path == null)
                        {
                            _pic.Visible = false;
                            return;
                        }
                        try
                        {
                            byte[] bytes = File.ReadAllBytes(path);
                            using (MemoryStream ms = new MemoryStream(bytes)) _pic.Image = Image.FromStream(ms);
                        }
                        catch { _pic.Visible = false; }
                    });
                }
                catch { }
            });
        }

        private static string ToCrLf(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
        }
    }

    /// <summary>Renders a window straight into a PNG (works even when the window is not in front).</summary>
    internal static class WinCapture
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        public static bool Save(IntPtr hwnd, string file)
        {
            RECT r;
            if (!GetWindowRect(hwnd, out r)) return false;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0) return false;
            using (Bitmap bmp = new Bitmap(w, h))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    bool ok = PrintWindow(hwnd, hdc, 2);   // PW_RENDERFULLCONTENT
                    g.ReleaseHdc(hdc);
                    if (!ok) g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h));
                }
                bmp.Save(file, ImageFormat.Png);
            }
            return true;
        }
    }
}
