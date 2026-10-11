using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BMM
{
    internal class DarkComboBox : ComboBox
    {
        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (message.Msg == 0xF || message.Msg == 0x317 || message.Msg == 0x318)
            {
                using (Graphics g = message.Msg == 0xF ? Graphics.FromHwnd(Handle) : Graphics.FromHdc(message.WParam))
                {
                    Rectangle r = new Rectangle(2, 2, Math.Max(1, Width - 22), Height - 4);
                    using (SolidBrush brush = new SolidBrush(Theme.Surface)) g.FillRectangle(brush, r);
                    TextRenderer.DrawText(g, Text, Font, r, Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                }
            }
        }
    }
    internal static class Theme
    {
        public static readonly Color Background = Color.FromArgb(17, 20, 27), Surface = Color.FromArgb(26, 31, 41), Border = Color.FromArgb(49, 59, 75), Text = Color.FromArgb(230, 235, 243), Muted = Color.FromArgb(158, 173, 195), Green = Color.FromArgb(95, 218, 175), Gold = Color.FromArgb(242, 190, 101);
        public static void Apply(Control c)
        {
            c.BackColor = c is TextBox || c is ListView || c is ComboBox || c is ListBox ? Surface : Background;
            c.ForeColor = Text;
            Button b = c as Button;
            if (b != null) { b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderColor = Border; b.BackColor = Surface; b.Cursor = Cursors.Hand; }
            TextBox t = c as TextBox;
            if (t != null) t.BorderStyle = BorderStyle.FixedSingle;
            ComboBox combo = c as ComboBox;
            if (combo != null) { combo.FlatStyle = FlatStyle.Flat; combo.DrawMode = DrawMode.OwnerDrawFixed; combo.ItemHeight = 24; combo.DrawItem += delegate(object sender, DrawItemEventArgs e) { using (SolidBrush brush = new SolidBrush(Surface)) e.Graphics.FillRectangle(brush, e.Bounds); string text = e.Index >= 0 ? Convert.ToString(combo.Items[e.Index]) : combo.Text; TextRenderer.DrawText(e.Graphics, text, combo.Font, e.Bounds, Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis); }; }
            foreach (Control child in c.Controls) Apply(child);
        }
    }

    internal partial class MainForm
    {
        private ComboBox _browseFilter;
        private Button _favorite;
        private ImageList _cardSpacing;
        private bool _cards = true, _offlineCheck;
        private readonly HashSet<string> _favorites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Image> _covers = new Dictionary<string, Image>();
        private readonly HashSet<string> _coverPending = new HashSet<string>();
        private int _coverJobs;
        private readonly string _browseFile = Path.Combine(Paths.ManagerDir, "browse_preferences.json");
        private readonly string _sourceFile = Path.Combine(Paths.ManagerDir, "custom_repositories.txt");

        private static string PackageKey(TsPackage p) { return p.Source + ":" + p.Owner + "/" + p.Name; }

        private Control BuildBrowseToolbar(FlowLayoutPanel old)
        {
            Panel wrapper = new Panel { Dock = DockStyle.Fill };
            old.Dock = DockStyle.Top; old.Height = 46; old.AutoScroll = true;
            old.Controls.Remove(_lblOnlineStatus); _lblOnlineStatus.AutoSize = false; _lblOnlineStatus.Dock = DockStyle.Bottom; _lblOnlineStatus.Height = 26; _lblOnlineStatus.Padding = new Padding(8, 2, 0, 0);
            FlowLayoutPanel second = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(6, 8, 0, 0), WrapContents = false, AutoScroll = true };
            _browseFilter = new DarkComboBox { Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
            _browseFilter.Items.AddRange(new object[] { "全部 Mod", "我的收藏", "已安装", "可更新", "未安装" });
            _browseFilter.SelectedIndex = 0;
            _browseFilter.SelectedIndexChanged += delegate { FillOnline(); };
            Button view = MakeButton("切换为列表", 104);
            view.Click += delegate { _cards = !_cards; ConfigureBrowseView(); view.Text = _cards ? "切换为列表" : "切换为卡片"; };
            _favorite = MakeButton("☆ 收藏 / 取消", 116);
            _favorite.Click += delegate
            {
                if (_lvOnline.SelectedItems.Count == 0) return;
                TsPackage p = _lvOnline.SelectedItems[0].Tag as TsPackage;
                string key = PackageKey(p);
                if (!_favorites.Remove(key)) _favorites.Add(key);
                try { Fs.EnsureDir(Paths.ManagerDir); File.WriteAllText(_browseFile, Json.Write(new List<string>(_favorites)), new UTF8Encoding(false)); }
                catch (Exception ex) { SetStatus("收藏保存失败：" + ex.Message); }
                FillOnline();
            };
            Button sources = MakeButton("管理来源…", 104); sources.Click += delegate { EditSources(); };
            second.Controls.AddRange(new Control[] { _browseFilter, view, _favorite, sources });
            second.Dock = DockStyle.Top;
            wrapper.Controls.Add(_lblOnlineStatus); wrapper.Controls.Add(second); wrapper.Controls.Add(old);
            return wrapper;
        }

        private void InitializeRedesign()
        {
            try { if (!_offlineCheck && File.Exists(_browseFile)) foreach (object key in Json.List(Json.Parse(File.ReadAllText(_browseFile)))) _favorites.Add(Convert.ToString(key)); } catch { }
            _tabs.Appearance = TabAppearance.FlatButtons; _tabs.ItemSize = new Size(0, 1); _tabs.SizeMode = TabSizeMode.Fixed;
            Panel sidebar = new Panel { Dock = DockStyle.Left, Width = 174, Padding = new Padding(12, 20, 12, 12) };
            Label brand = new Label { Text = "♠ BALATRO\nMOD LIBRARY", Font = new Font("Segoe UI", 12, FontStyle.Bold), Dock = DockStyle.Top, Height = 96, ForeColor = Theme.Gold };
            FlowLayoutPanel nav = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            string[] names = { "▣  已安装", "✦  发现 Mod", "✓  环境检测", "≡  游戏日志", "⚙  设置与备份" };
            List<Button> buttons = new List<Button>();
            for (int i = 0; i < names.Length; i++)
            {
                int index = i; Button b = MakeButton(names[i], 146); b.Height = 46; b.Margin = new Padding(0, 0, 0, 10); b.TextAlign = ContentAlignment.MiddleLeft;
                b.Click += delegate { _tabs.SelectedIndex = index; }; nav.Controls.Add(b); buttons.Add(b);
            }
            sidebar.Controls.Add(nav); sidebar.Controls.Add(brand); Controls.Add(sidebar);
            _tabs.SelectedIndexChanged += delegate { for (int i = 0; i < buttons.Count; i++) { buttons[i].ForeColor = _tabs.SelectedIndex == i ? Theme.Green : Theme.Muted; buttons[i].BackColor = _tabs.SelectedIndex == i ? Theme.Surface : Theme.Background; } };
            _lvOnline.OwnerDraw = true;
            _lvOnline.DrawColumnHeader += delegate(object sender, DrawListViewColumnHeaderEventArgs e) { using (SolidBrush brush = new SolidBrush(Theme.Surface)) e.Graphics.FillRectangle(brush, e.Bounds); TextRenderer.DrawText(e.Graphics, e.Header.Text, Font, e.Bounds, Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter); };
            _lvOnline.DrawItem += DrawBrowseItem;
            _lvOnline.DrawSubItem += delegate(object sender, DrawListViewSubItemEventArgs e) { if (_cards) return; Color bg = e.Item.Selected ? Color.FromArgb(39, 65, 69) : Theme.Surface; using (SolidBrush brush = new SolidBrush(bg)) e.Graphics.FillRectangle(brush, e.Bounds); TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Font, e.Bounds, e.Item.ForeColor, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.Left); };
            _cardSpacing = new ImageList { ImageSize = new Size(112, 112), ColorDepth = ColorDepth.Depth32Bit };
            _cardSpacing.Images.Add(new Bitmap(112, 112)); _lvOnline.LargeImageList = _cardSpacing;
            ConfigureBrowseView();
            FormClosed += delegate { foreach (Image cover in _covers.Values) cover.Dispose(); _cardSpacing.Dispose(); };
            Load += delegate { if (!_offlineCheck) _tabs.SelectedTab = _onlineTab; };
        }

        private void ConfigureBrowseView()
        {
            _lvOnline.View = _cards ? View.Tile : View.Details;
            _lvOnline.TileSize = new Size(328, 208);
            _lvOnline.Invalidate();
        }

        private string BrowseState(TsPackage p, string installed)
        {
            if (installed.Length == 0) return "未安装";
            if (p.Version.Length > 0 && Ver.Parse(p.Version).CompareTo(Ver.Parse(installed)) > 0) return "可更新 · " + installed + " → " + p.Version;
            return "已安装 · " + installed;
        }
        private bool MatchesBrowseFilter(TsPackage p, string installed)
        {
            if (_browseFilter == null) return true;
            int filter = _browseFilter.SelectedIndex;
            return filter == 0 || (filter == 1 && _favorites.Contains(PackageKey(p))) || (filter == 2 && installed.Length > 0) || (filter == 3 && BrowseState(p, installed).StartsWith("可更新")) || (filter == 4 && installed.Length == 0);
        }

        private void DrawBrowseItem(object sender, DrawListViewItemEventArgs e)
        {
            if (!_cards) return;
            TsPackage p = e.Item.Tag as TsPackage; if (p == null) return;
            Rectangle r = e.Bounds; r.Inflate(-5, -5);
            using (SolidBrush brush = new SolidBrush(e.Item.Selected ? Color.FromArgb(34, 53, 61) : Theme.Surface)) e.Graphics.FillRectangle(brush, r);
            using (Pen pen = new Pen(e.Item.Selected ? Theme.Green : Theme.Border)) e.Graphics.DrawRectangle(pen, r);
            Rectangle image = new Rectangle(r.X + 14, r.Y + 14, 104, 104);
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(42, 48, 65))) e.Graphics.FillRectangle(brush, image);
            Image cover; string key = PackageKey(p);
            if (_covers.TryGetValue(key, out cover) && cover != null) e.Graphics.DrawImage(cover, image);
            else { using (Font symbol = new Font("Segoe UI", 32)) TextRenderer.DrawText(e.Graphics, "♠", symbol, image, Theme.Gold, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); QueueCover(p); }
            using (Font title = new Font(Font.FontFamily, 11, FontStyle.Bold)) TextRenderer.DrawText(e.Graphics, (_favorites.Contains(key) ? "★ " : "") + p.Name, title, new Rectangle(r.X + 132, r.Y + 14, r.Width - 144, 46), Theme.Text, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            DrawCardText(e.Graphics, p.Owner, new Rectangle(r.X + 132, r.Y + 63, r.Width - 144, 23), Theme.Muted);
            DrawCardText(e.Graphics, p.Source == "github" ? "GitHub · ★ " + Thunderstore.Human(p.Rating) : "Thunderstore · ↓ " + Thunderstore.Human(p.Downloads), new Rectangle(r.X + 132, r.Y + 87, r.Width - 144, 25), Theme.Gold);
            DrawCardText(e.Graphics, FirstLine(p.Description), new Rectangle(r.X + 14, r.Y + 128, r.Width - 28, 24), Theme.Muted);
            DrawCardText(e.Graphics, (p.Version.Length == 0 ? "版本待读取" : "v" + p.Version) + "  ·  " + ShortDate(p.DateUpdated) + "  ·  依赖 " + (p.Source == "github" ? "待确认" : p.Dependencies.Count.ToString()), new Rectangle(r.X + 14, r.Y + 153, r.Width - 28, 20), Theme.Muted);
            DrawCardText(e.Graphics, BrowseState(p, InstalledVersionOf(p)), new Rectangle(r.X + 14, r.Y + 176, r.Width - 28, 20), Theme.Green);
        }
        private void DrawCardText(Graphics g, string text, Rectangle r, Color color) { TextRenderer.DrawText(g, text, Font, r, color, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter); }
        private void QueueCover(TsPackage p)
        {
            string key = PackageKey(p); if (_offlineCheck || _coverJobs >= 4 || _coverPending.Contains(key) || _covers.ContainsKey(key)) return;
            _coverPending.Add(key); _coverJobs++;
            Task.Factory.StartNew(delegate { return Thunderstore.EnsureIcon(p); }).ContinueWith(delegate(Task<string> task)
            {
                try { BeginInvoke((MethodInvoker)delegate {
                    _coverJobs--; Image image = null;
                    if (task.Status == TaskStatus.RanToCompletion && task.Result != null) try { using (MemoryStream stream = new MemoryStream(File.ReadAllBytes(task.Result))) using (Image original = Image.FromStream(stream)) image = new Bitmap(original); } catch { }
                    _covers[key] = image; _lvOnline.Invalidate();
                }); } catch { }
            });
        }

        private void EditSources()
        {
            using (Form dialog = new Form { Text = "管理来源 · 自定义 GitHub 仓库", Size = new Size(640, 450), StartPosition = FormStartPosition.CenterParent })
            {
                Label tip = new Label { Dock = DockStyle.Top, Height = 70, Padding = new Padding(16), Text = "内置：Thunderstore / GitHub 搜索 / 社区精选清单\n每行添加一个 GitHub 仓库链接或 owner/repo；删除一行即可移除。" };
                TextBox input = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, Text = File.Exists(_sourceFile) ? File.ReadAllText(_sourceFile) : "" };
                Button save = new Button { Dock = DockStyle.Bottom, Height = 48, Text = "保存来源并浏览" };
                save.Click += delegate {
                    List<string> repos = new List<string>();
                    foreach (string line in input.Lines) { string repo = line.Trim().Replace("https://github.com/", "").TrimEnd('/'); if (repo.Length == 0) continue; if (!Regex.IsMatch(repo, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")) { MessageBox.Show(dialog, "无效的仓库链接：" + line); return; } if (!repos.Contains(repo)) repos.Add(repo); }
                    try { Fs.EnsureDir(Paths.ManagerDir); File.WriteAllLines(_sourceFile, repos.ToArray(), new UTF8Encoding(false)); dialog.DialogResult = DialogResult.OK; } catch (Exception ex) { MessageBox.Show(dialog, "保存失败：" + ex.Message); }
                };
                dialog.Controls.Add(input); dialog.Controls.Add(tip); dialog.Controls.Add(save); Theme.Apply(dialog);
                if (dialog.ShowDialog(this) == DialogResult.OK) { if (_cbSource.SelectedIndex == 3) LoadOnline(true); else _cbSource.SelectedIndex = 3; }
            }
        }
        private List<TsPackage> LoadCustomSources(out string error)
        {
            error = ""; List<TsPackage> packages = new List<TsPackage>();
            if (!File.Exists(_sourceFile)) return packages;
            foreach (string repo in File.ReadAllLines(_sourceFile))
            {
                try { string cache = Path.Combine(Paths.ManagerDir, "repo_" + repo.Replace('/', '_') + ".json"); string json;
                    try { json = Thunderstore.HttpGet("https://api.github.com/repos/" + repo, 15000); File.WriteAllText(cache, json, new UTF8Encoding(false)); }
                    catch { if (!File.Exists(cache)) throw; json = File.ReadAllText(cache); error += repo + " 使用离线缓存；"; }
                    packages.Add(GitHub.ToPackage(Json.Dict(Json.Parse(json))));
                } catch (Exception ex) { error += repo + ": " + ex.Message + "；"; }
            }
            return packages;
        }

        internal static void UiCheck(string directory)
        {
            Directory.CreateDirectory(directory);
            using (MainForm form = new MainForm(true))
            {
                form._offlineCheck = true; form._tsLoading = true; form.Show(); form._tabs.SelectedTab = form._onlineTab;
                string[] names = { "Sample Cards", "Sample Jokers", "Sample Sleeves", "Sample Framework", "Sample Controls", "Sample Themes" };
                for (int i = 0; i < names.Length; i++) form._ts.Add(new TsPackage { Name = names[i], Owner = "Preview Author", Description = "布局测试数据 · 扩展你的 Balatro 体验，探索更多玩法与卡牌。", Version = "1.2.0", Downloads = 125000 - i * 12000, DateUpdated = "2026-10-01", FileSize = 100000000 });
                form._cbCategory.Items.Add("全部"); form._cbCategory.SelectedIndex = 0; form.FillOnline(); Application.DoEvents();
                if (form._lvOnline.Items.Count != 6) throw new Exception("Browse count mismatch");
                form._favorites.Add(PackageKey(form._ts[0])); form._browseFilter.SelectedIndex = 1;
                if (form._lvOnline.Items.Count != 1) throw new Exception("Favorite filter mismatch");
                form._browseFilter.SelectedIndex = 0; form._lblOnlineStatus.Text = "离线布局测试 · 6 项示例，非真实下载数据";
                TsPackage github = new TsPackage { Source = "github", Owner = "sample", Name = "sample", Version = "2.0" };
                ModInfo installed = new ModInfo { TsNamespace = "github", TsPackage = "sample/sample", Version = Ver.Parse("1.0") };
                form._mods.Add(installed);
                if (form.InstalledVersionOf(github) != "1.0" || !form.BrowseState(github, "1.0").StartsWith("可更新")) throw new Exception("GitHub install status mismatch");
                if (SourceCodeFromIndex(4) != "all" || SourceCodeFromIndex(3) != "custom") throw new Exception("Source routing mismatch");
                form._txtSearch.Text = "no-matching-preview-package";
                if (form._lvOnline.Items.Count != 0 || form._detailPkg != null) throw new Exception("Empty result retained stale details");
                form._txtSearch.Clear();
                form._txtOnlineDetail.Text = "界面布局预览\r\n\r\n这是离线测试数据。正式运行会读取真实 Mod 来源、已安装版本和介绍。\r\n\r\n安装与依赖\r\n选择 Mod 后可直接安装，也可安装包含依赖的完整组合。\r\n\r\n中文介绍\r\n保留自动翻译、翻译源选择与原文切换。";
                Application.DoEvents();
                using (Bitmap bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(directory, "preview-cards.png")); }
                form._cards = false; form.ConfigureBrowseView(); Application.DoEvents();
                using (Bitmap bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(directory, "preview-list.png")); }
                File.WriteAllText(Path.Combine(directory, "ui-check.txt"), "PASS: offline startup, six cards, favorites filtering, GitHub installed/update matching, source routing, empty-result details reset, card/list switch, screenshots. No installation or save mutation.");
                form.Close();
            }
        }
    }
}
