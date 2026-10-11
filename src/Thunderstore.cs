using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace BMM
{
    public class TsPackage
    {
        public string Source = "thunderstore";   // thunderstore | github
        public string FullName = "";
        public long SizeKb;
        public string DefaultBranch = "main";
        public string Name = "";
        public string Owner = "";
        public string Description = "";
        public string Version = "";
        public string DownloadUrl = "";
        public string WebsiteUrl = "";
        public string PackageUrl = "";
        public string DateUpdated = "";
        public long Downloads;
        public long FileSize;
        public long Rating;
        public string Icon = "";
        public bool Deprecated;
        public List<string> Categories = new List<string>();
        public List<string> Dependencies = new List<string>();

        public string Display { get { return Name; } }
    }

    /// <summary>Thunderstore (thunderstore.io/c/balatro) client: browse, search, download, update check.</summary>
    public static class Thunderstore
    {
        public const string ApiUrl = "https://thunderstore.io/c/balatro/api/v1/package/";
        private static List<TsPackage> _cache;
        private static string _lastError = "";

        public static string LastError { get { return _lastError; } }

        public static bool CacheIsFresh()
        {
            try
            {
                if (!File.Exists(Paths.TsCacheFile)) return false;
                return (DateTime.Now - File.GetLastWriteTime(Paths.TsCacheFile)).TotalHours < 6;
            }
            catch { return false; }
        }

        /// <summary>Loads the package list; uses the on-disk cache when it is fresh unless forceRefresh.</summary>
        public static List<TsPackage> Load(bool forceRefresh)
        {
            if (!forceRefresh && _cache != null && _cache.Count > 0) return _cache;
            if (!forceRefresh && CacheIsFresh())
            {
                try
                {
                    List<TsPackage> cached = Parse(File.ReadAllText(Paths.TsCacheFile, Encoding.UTF8));
                    if (cached.Count > 0) { _cache = cached; _lastError = ""; return cached; }
                }
                catch { }
            }
            try
            {
                string body = HttpGet(ApiUrl, 60000);
                List<TsPackage> list = Parse(body);
                if (list.Count > 0)
                {
                    _cache = list;
                    _lastError = "";
                    try { Fs.WriteAtomic(Paths.TsCacheFile, body); } catch { }
                    return list;
                }
                _lastError = "服务器返回了空列表";
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
            }
            // fall back to a stale cache so the app still works offline
            try
            {
                if (File.Exists(Paths.TsCacheFile))
                {
                    List<TsPackage> stale = Parse(File.ReadAllText(Paths.TsCacheFile, Encoding.UTF8));
                    if (stale.Count > 0) { _cache = stale; _lastError = _lastError + "（已使用本地缓存）"; return stale; }
                }
            }
            catch { }
            return new List<TsPackage>();
        }

        private static List<TsPackage> Parse(string json)
        {
            List<TsPackage> res = new List<TsPackage>();
            foreach (object raw in Json.List(Json.Parse(json)))
            {
                Dictionary<string, object> d = Json.Dict(raw);
                if (d == null) continue;
                TsPackage p = new TsPackage();
                p.Name = Json.Str(d, "name", "");
                p.Owner = Json.Str(d, "owner", "");
                p.FullName = Json.Str(d, "full_name", "");
                p.PackageUrl = Json.Str(d, "package_url", "");
                p.DateUpdated = Json.Str(d, "date_updated", "");
                p.Deprecated = Json.Bool(d, "is_deprecated", false);
                p.Rating = Json.Num(d, "rating_score", 0);
                p.Categories = Json.StrList(d, "categories");
                List<object> versions = Json.List(d.ContainsKey("versions") ? d["versions"] : null);
                if (versions.Count > 0)
                {
                    Dictionary<string, object> v = Json.Dict(versions[0]);
                    p.Version = Json.Str(v, "version_number", "");
                    p.Description = Json.Str(v, "description", "");
                    p.DownloadUrl = Json.Str(v, "download_url", "");
                    p.WebsiteUrl = Json.Str(v, "website_url", "");
                    p.Icon = Json.Str(v, "icon", "");
                    p.Downloads = Json.Num(v, "downloads", 0);
                    p.FileSize = Json.Num(v, "file_size", 0);
                    p.Dependencies = Json.StrList(v, "dependencies");
                }
                res.Add(p);
            }
            return res;
        }

        /// <summary>
        /// .NET Framework defaults to SSL3/TLS1.0 which api.github.com refuses ("未能创建 SSL/TLS 安全通道").
        /// Turn on TLS 1.1/1.2 once, using the numeric values so it compiles against older reference assemblies.
        /// </summary>
        private static bool _tlsReady;

        public static void EnsureTls()
        {
            if (_tlsReady) return;
            _tlsReady = true;
            try
            {
                ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol
                    | (SecurityProtocolType)768     // Tls11
                    | (SecurityProtocolType)3072;   // Tls12
            }
            catch { }
        }

        public static string HttpGet(string url, int timeoutMs)
        {
            EnsureTls();
            using (TimeoutWebClient wc = new TimeoutWebClient(timeoutMs))
            {
                wc.Encoding = Encoding.UTF8;
                wc.Headers["User-Agent"] = "BalatroModManager/1.0";
                return wc.DownloadString(url);
            }
        }

        public static void HttpDownload(string url, string destPath, int timeoutMs)
        {
            EnsureTls();
            Fs.EnsureDir(Path.GetDirectoryName(destPath));
            using (TimeoutWebClient wc = new TimeoutWebClient(timeoutMs))
            {
                wc.Headers["User-Agent"] = "BalatroModManager/1.0";
                wc.DownloadFile(url, destPath);
            }
        }

        private class TimeoutWebClient : WebClient
        {
            private readonly int _timeout;
            public TimeoutWebClient(int timeout) { _timeout = timeout; }
            protected override WebRequest GetWebRequest(Uri address)
            {
                WebRequest r = base.GetWebRequest(address);
                if (r != null) r.Timeout = _timeout;
                return r;
            }
        }

        // ---------- search ----------

        public static List<TsPackage> Search(List<TsPackage> all, string query, string category, bool hideDeprecated)
        {
            List<TsPackage> res = new List<TsPackage>();
            string q = (query ?? "").Trim().ToLowerInvariant();
            foreach (TsPackage p in all)
            {
                if (hideDeprecated && p.Deprecated) continue;
                if (!string.IsNullOrEmpty(category) && category != "全部")
                {
                    bool hit = false;
                    foreach (string c in p.Categories) if (c.Equals(category, StringComparison.OrdinalIgnoreCase)) { hit = true; break; }
                    if (!hit) continue;
                }
                if (q.Length > 0)
                {
                    string hay = (p.Name + " " + p.Owner + " " + p.Description + " " + string.Join(" ", p.Categories.ToArray())).ToLowerInvariant();
                    bool ok = hay.IndexOf(q, StringComparison.Ordinal) >= 0;
                    if (!ok)
                    {
                        // all query words must appear
                        ok = true;
                        foreach (string w in q.Split(' '))
                            if (w.Length > 0 && hay.IndexOf(w, StringComparison.Ordinal) < 0) { ok = false; break; }
                    }
                    if (!ok) continue;
                }
                res.Add(p);
            }
            return res;
        }

        public static readonly string[] SortModes = new string[] { "下载量", "点赞量", "星标", "更新日期", "名称", "文件大小", "作者" };

        /// <summary>Each source has its own meaningful sort options.</summary>
        public static string[] SortModesFor(string source)
        {
            if (source == "github") return new string[] { "星标", "更新日期", "名称", "作者" };
            if (source == "index") return new string[] { "名称", "作者" };
            return new string[] { "下载量", "点赞量", "更新日期", "名称", "文件大小", "作者" };
        }

        /// <summary>Set from settings; the detail panel and the big window honour it.</summary>
        public static bool AutoTranslate;

        public static List<TsPackage> Sort(List<TsPackage> list, string mode)
        {
            List<TsPackage> res = new List<TsPackage>(list);
            if (mode == "点赞量" || mode == "星标")
                res.Sort(delegate (TsPackage a, TsPackage b) { int c = b.Rating.CompareTo(a.Rating); return c != 0 ? c : b.Downloads.CompareTo(a.Downloads); });
            else if (mode == "更新日期")
                res.Sort(delegate (TsPackage a, TsPackage b) { int c = string.CompareOrdinal(b.DateUpdated, a.DateUpdated); return c != 0 ? c : b.Downloads.CompareTo(a.Downloads); });
            else if (mode == "名称")
                res.Sort(delegate (TsPackage a, TsPackage b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            else if (mode == "文件大小")
                res.Sort(delegate (TsPackage a, TsPackage b) { return b.FileSize.CompareTo(a.FileSize); });
            else if (mode == "作者")
                res.Sort(delegate (TsPackage a, TsPackage b) { int c = string.Compare(a.Owner, b.Owner, StringComparison.OrdinalIgnoreCase); return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            else
                res.Sort(delegate (TsPackage a, TsPackage b) { int c = b.Downloads.CompareTo(a.Downloads); return c != 0 ? c : b.Rating.CompareTo(a.Rating); });
            return res;
        }

        public static List<string> AllCategories(List<TsPackage> all)
        {
            List<string> res = new List<string>();
            res.Add("全部");
            foreach (TsPackage p in all)
                foreach (string c in p.Categories)
                    if (!res.Contains(c)) res.Add(c);
            res.Sort(1, res.Count - 1, StringComparer.OrdinalIgnoreCase);
            return res;
        }

        // ---------- install ----------

        public class InstallOutcome
        {
            public List<string> Installed = new List<string>();
            public List<string> Warnings = new List<string>();
            public List<string> Skipped = new List<string>();
        }

        public static InstallOutcome Install(TsPackage pkg, bool withDependencies)
        {
            InstallOutcome outcome = new InstallOutcome();
            HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<TsPackage> all = (pkg != null && pkg.Source == "github") ? new List<TsPackage>() : Load(false);
            InstallRecursive(pkg, withDependencies, all, visited, outcome, 0);
            return outcome;
        }

        private static void InstallRecursive(TsPackage pkg, bool withDeps, List<TsPackage> all, HashSet<string> visited, InstallOutcome outcome, int depth)
        {
            if (pkg == null || depth > 4) return;
            string key = pkg.Owner + "/" + pkg.Name;
            if (visited.Contains(key)) return;
            visited.Add(key);

            if (pkg.Source == "github")
            {
                string rerr;
                if (string.IsNullOrEmpty(pkg.DownloadUrl) || string.IsNullOrEmpty(pkg.Version))
                {
                    if (!GitHub.ResolveDownload(pkg, out rerr) || string.IsNullOrEmpty(pkg.DownloadUrl))
                    {
                        outcome.Warnings.Add(pkg.Name + " 无法安装：" + (string.IsNullOrEmpty(rerr) ? "拿不到下载地址" : rerr));
                        return;
                    }
                }
            }

            if (withDeps)
            {
                if (pkg.Source == "github")
                    outcome.Warnings.Add(pkg.Name + "：GitHub 源没有依赖信息，需要额外的库时请自行安装（例如 Cryptid 需要 Amulet）");
                foreach (string dep in pkg.Dependencies)
                {
                    TsPackage dp = ResolveDependency(all, dep);
                    if (dp == null)
                    {
                        if (dep.StartsWith("Thunderstore-lovely", StringComparison.OrdinalIgnoreCase)) continue; // the injector itself
                        if (dep.StartsWith("Steamodded-Steamodded", StringComparison.OrdinalIgnoreCase))
                        {
                            if (Directory.Exists(Path.Combine(Paths.ModsDir, "smods"))) continue;
                        }
                        outcome.Warnings.Add("依赖 " + dep + " 在 Thunderstore 上找不到，请手动安装");
                        continue;
                    }
                    if (dp.Name.Equals("Steamodded", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(Paths.ModsDir, "smods")))
                        continue;
                    if (dp.Name.Equals("Lovely", StringComparison.OrdinalIgnoreCase) || dp.Owner.Equals("Thunderstore", StringComparison.OrdinalIgnoreCase))
                        continue;
                    InstallRecursive(dp, true, all, visited, outcome, depth + 1);
                }
            }

            string zip = Path.Combine(Paths.TempDir, (pkg.Source == "github" ? "gh_" : "ts_") + pkg.Owner + "-" + pkg.Name + ".zip");
            try
            {
                Fs.EnsureDir(Paths.TempDir);
                HttpDownload(pkg.DownloadUrl, zip, pkg.Source == "github" ? 600000 : 180000);
                ModLibrary.InstallResult r = ModLibrary.InstallZip(zip, pkg.Name, true);
                foreach (string w in r.Warnings) outcome.Warnings.Add(pkg.Name + "：" + w);
                outcome.Installed.Add(pkg.Name + " " + (pkg.Version.Length > 0 ? pkg.Version : "(无版本号)"));
                if (pkg.Source == "github") ModLibrary.RememberSource(r.Name, "github", pkg.Owner + "/" + pkg.Name, pkg.Version);
                else ModLibrary.RememberSource(r.Name, pkg.Owner, pkg.Name, pkg.Version);
            }
            catch (Exception ex)
            {
                outcome.Warnings.Add(pkg.Name + " 安装失败：" + ex.Message);
            }
            finally
            {
                try { if (File.Exists(zip)) File.Delete(zip); } catch { }
            }
        }

        /// <summary>Thunderstore dependency strings look like "Owner-Package-1.2.3".</summary>
        public static TsPackage ResolveDependency(List<TsPackage> all, string dep)
        {
            if (string.IsNullOrEmpty(dep)) return null;
            foreach (TsPackage p in all)
            {
                if (dep.Equals(p.Owner + "-" + p.Name + "-" + p.Version, StringComparison.OrdinalIgnoreCase)) return p;
            }
            foreach (TsPackage p in all)
            {
                if (dep.StartsWith(p.Owner + "-" + p.Name + "-", StringComparison.OrdinalIgnoreCase)) return p;
            }
            return null;
        }

        // ---------- update check ----------

        public static int MarkUpdates(List<ModInfo> mods, List<TsPackage> all)
        {
            int n = 0;
            foreach (ModInfo m in mods)
            {
                TsPackage match = null;
                if (m.TsNamespace.Length > 0)
                    foreach (TsPackage p in all)
                        if (p.Owner.Equals(m.TsNamespace, StringComparison.OrdinalIgnoreCase) && p.Name.Equals(m.TsPackage, StringComparison.OrdinalIgnoreCase)) { match = p; break; }
                if (match == null) match = FindByName(all, m);
                if (match == null) continue;
                Ver remote = Ver.Parse(match.Version);
                if (remote.Parts.Count == 0) continue;
                if (m.Version.Parts.Count == 0)
                {
                    m.UpdateTo = match.Version;
                    n++;
                    continue;
                }
                if (remote.CompareTo(m.Version) > 0) { m.UpdateTo = match.Version; n++; }
            }
            return n;
        }

        public static TsPackage FindByName(List<TsPackage> all, ModInfo m)
        {
            string[] keys = new string[] { m.Id, m.DisplayName, m.FolderName };
            foreach (TsPackage p in all)
            {
                foreach (string k in keys)
                {
                    if (string.IsNullOrEmpty(k)) continue;
                    if (p.Name.Equals(k, StringComparison.OrdinalIgnoreCase)) return p;
                    if (Norm(p.Name) == Norm(k)) return p;
                }
            }
            return null;
        }

        private static string Norm(string s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in s.ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        public static string Human(long n)
        {
            if (n >= 1000000) return (n / 1000000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
            if (n >= 1000) return (n / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";
            return n.ToString(CultureInfo.InvariantCulture);
        }

        // ---------- mod introduction (README) ----------

        /// <summary>Reading a README means downloading the whole package; skip that for huge (usually non-mod) packages.</summary>
        public const long ReadmeSizeLimit = 40L * 1024 * 1024;

        /// <summary>Thunderstore lists mod managers in the same community; they are not game mods.</summary>
        public static bool IsModManager(TsPackage p)
        {
            if (p == null) return false;
            string n = (p.Name ?? "").ToLowerInvariant();
            return n == "r2modman" || n == "galemodmanager" || n == "thunderstoremodmanager" || n == "modmanager";
        }

        public static string ReadmeCachePath(TsPackage p)
        {
            return Path.Combine(Paths.ReadmeCacheDir, p.Owner + "-" + p.Name + "-" + p.Version + ".md");
        }

        /// <summary>
        /// Thunderstore has no readme API and the web page only carries it inside a serialized SPA payload,
        /// so the reliable source is the README.md inside the package zip. Result is cached per version.
        /// </summary>
        public static string ReadmeFor(TsPackage p)
        {
            string cache = ReadmeCachePath(p);
            if (File.Exists(cache))
            {
                try { return File.ReadAllText(cache, Encoding.UTF8); }
                catch { }
            }
            if (string.IsNullOrEmpty(p.DownloadUrl)) throw new InvalidOperationException("这个包没有下载地址，无法读取介绍");

            string zip = Path.Combine(Paths.TempDir, "readme_" + p.Owner + "-" + p.Name + "-" + p.Version + ".zip");
            try
            {
                Fs.EnsureDir(Paths.TempDir);
                HttpDownload(p.DownloadUrl, zip, 300000);
                string text = ExtractReadme(zip);
                if (text == null) throw new InvalidOperationException("这个 mod 的压缩包里没有 README.md，点“打开网页”看作者的主页吧");
                Fs.EnsureDir(Paths.ReadmeCacheDir);
                File.WriteAllText(cache, text, new UTF8Encoding(false));
                return text;
            }
            finally
            {
                try { if (File.Exists(zip)) File.Delete(zip); } catch { }
            }
        }

        /// <summary>Pulls README.md (or readme.txt) out of a mod zip and flattens the markdown.</summary>
        public static string ExtractReadme(string zipPath)
        {
            string entryName;
            return ExtractReadme(zipPath, out entryName);
        }

        /// <summary>
        /// Picks the mod's own README. Packages often bundle libraries that ship their own README.md
        /// (e.g. libs/nativefs/README.md), so the shallowest path wins, then the name preference order.
        /// </summary>
        public static string ExtractReadme(string zipPath, out string entryName)
        {
            entryName = null;
            using (System.IO.Compression.ZipArchive zip = System.IO.Compression.ZipFile.OpenRead(zipPath))
            {
                string[] wanted = new string[] { "readme.md", "readme.markdown", "readme.txt", "readme" };
                System.IO.Compression.ZipArchiveEntry found = null;
                int bestRank = int.MaxValue;
                int bestDepth = int.MaxValue;
                foreach (System.IO.Compression.ZipArchiveEntry e in zip.Entries)
                {
                    string name = Path.GetFileName(e.FullName).ToLowerInvariant();
                    int rank = Array.IndexOf(wanted, name);
                    if (rank < 0) continue;
                    int depth = e.FullName.Split('/').Length - 1;
                    if (depth < bestDepth || (depth == bestDepth && rank < bestRank))
                    {
                        found = e;
                        bestRank = rank;
                        bestDepth = depth;
                    }
                }
                if (found == null) return null;
                entryName = found.FullName;
                using (Stream s = found.Open())
                using (StreamReader sr = new StreamReader(s, Encoding.UTF8, true))
                    return FlattenMarkdown(sr.ReadToEnd());
            }
        }

        /// <summary>Markdown -> readable plain text (images dropped, links inlined).</summary>
        public static string FlattenMarkdown(string md)
        {
            if (string.IsNullOrEmpty(md)) return "";
            List<string> outLines = new List<string>();
            foreach (string raw in md.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = raw.TrimEnd();
                string t = line.Trim();
                if (t.StartsWith("![") || t.StartsWith("<img") || t.StartsWith("<div") || t.StartsWith("<p ") || t.StartsWith("</")) continue;
                if (t.StartsWith("[![")) continue;
                line = System.Text.RegularExpressions.Regex.Replace(line, @"!\[([^\]]*)\]\([^)]*\)", "[图片：$1]");
                line = System.Text.RegularExpressions.Regex.Replace(line, @"\[([^\]]+)\]\(([^)]+)\)", "$1 ($2)");
                line = System.Text.RegularExpressions.Regex.Replace(line, @"<[^>]+>", "");
                outLines.Add(line);
            }
            string res = string.Join("\n", outLines.ToArray());
            res = System.Text.RegularExpressions.Regex.Replace(res, @"\n{4,}", "\n\n\n");
            return res.Trim();
        }

        /// <summary>Everything the "查看介绍" window shows above the readme.</summary>
        public static string DescribeForViewer(TsPackage p, List<TsPackage> all)        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("名称：" + p.Name + "    作者：" + p.Owner + "    最新版本：" + p.Version);
            sb.AppendLine((p.Source == "github" ? "星标：" + p.Rating + "    下载量：来源未提供    仓库估算大小：" : "下载量：" + p.Downloads + "    大小：") + Fs.Human(p.FileSize) + "    更新：" + (p.DateUpdated.Length >= 10 ? p.DateUpdated.Substring(0, 10) : p.DateUpdated));
            sb.AppendLine("分类：" + (p.Categories.Count > 0 ? string.Join("，", p.Categories.ToArray()) : "-") + (p.Deprecated ? "    !! 已被标记为废弃" : ""));
            if (p.WebsiteUrl.Length > 0) sb.AppendLine("作者主页：" + p.WebsiteUrl);
            sb.AppendLine((p.Source == "github" ? "GitHub 页面：" : "Thunderstore 页面：") + p.PackageUrl);
            if (p.Dependencies.Count > 0)
            {
                List<string> deps = new List<string>();
                foreach (string d in p.Dependencies)
                {
                    TsPackage dp = ResolveDependency(all, d);
                    deps.Add(dp == null ? d : dp.Name + " " + dp.Version);
                }
                sb.AppendLine("依赖：" + string.Join("，", deps.ToArray()));
            }
            else sb.AppendLine(p.Source == "github" ? "依赖：来源未提供，请查看 README 确认" : "依赖：来源未声明");
            sb.AppendLine();
            sb.AppendLine("简介：" + p.Description);
            return sb.ToString();
        }

        // ---------- cover image ----------

        public static string IconCachePath(TsPackage p)
        {
            return Path.Combine(Paths.IconCacheDir, p.Owner + "-" + p.Name + ".png");
        }

        /// <summary>Downloads the package icon once and returns the local file, or null when there is none.</summary>
        public static string EnsureIcon(TsPackage p)
        {
            string cache = IconCachePath(p);
            try
            {
                if (File.Exists(cache) && new FileInfo(cache).Length > 0) return cache;
            }
            catch { }
            if (string.IsNullOrEmpty(p.Icon)) return null;
            try
            {
                Fs.EnsureDir(Paths.IconCacheDir);
                HttpDownload(p.Icon, cache, 60000);
                return (File.Exists(cache) && new FileInfo(cache).Length > 0) ? cache : null;
            }
            catch { return null; }
        }

        // ---------- translation ----------

        /// <summary>All sources are free, key-less public endpoints: no account, no API key, no personal data.</summary>
        public static readonly string[] TranslateSources = new string[]
        {
            "Google 免费（推荐）",
            "MyMemory 免费（限 500 字/次）",
            "Google 备用（gtx）"
        };

        /// <summary>Currently selected source; set from settings by the UI.</summary>
        public static string CurrentSource = TranslateSources[0];

        private const string GoogleBatchUrl = "https://clients5.google.com/translate_a/t?client=dict-chrome-ex";
        private const string GoogleGtxUrl = "https://translate.googleapis.com/translate_a/single?client=gtx&dt=t";
        private const string MyMemoryUrl = "https://api.mymemory.translated.net/get";

        public static int ChunkSizeFor(string source)
        {
            return source != null && source.StartsWith("MyMemory") ? 450 : 1200;
        }

        private static bool CanBatch(string source)
        {
            return source == null || source.StartsWith("Google 免费");
        }

        public static string TranslatedCachePath(TsPackage p, string lang)
        {
            return Path.Combine(Paths.ReadmeCacheDir, p.Owner + "-" + p.Name + "-" + p.Version + "." + lang + ".md");
        }

        public static string Translate(string text, string toLang)
        {
            return Translate(text, toLang, CurrentSource);
        }

        public static string Translate(string text, string toLang, string source)
        {
            if (string.IsNullOrEmpty(text)) return "";
            List<string> parts = SplitForTranslation(text, ChunkSizeFor(source));
            StringBuilder sb = new StringBuilder();
            List<string> done = TranslateBatch(parts, toLang, source);
            for (int i = 0; i < parts.Count; i++)
            {
                sb.Append(i < done.Count && done[i].Length > 0 ? done[i] : parts[i]);
            }
            return sb.ToString();
        }

        /// <summary>Splits text into translation-sized parts at paragraph / line / sentence boundaries.</summary>
        public static List<string> SplitForTranslation(string text, int maxChars)
        {
            List<string> res = new List<string>();
            string[] blocks = text.Replace("\r\n", "\n").Split(new string[] { "\n\n" }, StringSplitOptions.None);
            StringBuilder cur = new StringBuilder();
            foreach (string raw in blocks)
            {
                string block = raw;
                if (cur.Length > 0 && cur.Length + block.Length + 2 > maxChars)
                {
                    res.Add(cur.ToString());
                    cur.Length = 0;
                }
                if (block.Length <= maxChars)
                {
                    if (cur.Length > 0) cur.Append("\n\n");
                    cur.Append(block);
                    continue;
                }
                // single oversized block: cut at sentence ends
                string rest = block;
                while (rest.Length > maxChars)
                {
                    int cut = rest.LastIndexOf(". ", Math.Min(maxChars, rest.Length - 1));
                    if (cut < maxChars / 2) cut = maxChars;
                    res.Add(rest.Substring(0, cut + 1));
                    rest = rest.Substring(cut + 1);
                }
                if (cur.Length > 0) { res.Add(cur.ToString()); cur.Length = 0; }
                if (rest.Length > 0) { cur.Append(rest); }
            }
            if (cur.Length > 0) res.Add(cur.ToString());
            return res;
        }

        public static List<string> TranslateBatch(List<string> parts, string toLang)
        {
            return TranslateBatch(parts, toLang, CurrentSource);
        }

        /// <summary>True when the selected source returned nothing and the default one was used instead.</summary>
        public static bool LastTranslateFellBack;

        public static List<string> TranslateBatch(List<string> parts, string toLang, string source)
        {
            LastTranslateFellBack = false;
            List<string> result = TranslateBatchCore(parts, toLang, source);
            bool allEmpty = true;
            foreach (string r in result) if (r.Length > 0) { allEmpty = false; break; }
            if (allEmpty && source != TranslateSources[0])
            {
                List<string> retry = TranslateBatchCore(parts, toLang, TranslateSources[0]);
                bool any = false;
                foreach (string r in retry) if (r.Length > 0) { any = true; break; }
                if (any)
                {
                    LastTranslateFellBack = true;
                    return retry;
                }
            }
            return result;
        }

        private static List<string> TranslateBatchCore(List<string> parts, string toLang, string source)
        {
            List<string> result = new List<string>();
            int i = 0;
            while (i < parts.Count)
            {
                List<string> group = new List<string>();
                if (CanBatch(source))
                {
                    int len = 0;
                    while (i < parts.Count && (group.Count == 0 || len + parts[i].Length <= 1200))
                    {
                        group.Add(parts[i]);
                        len += parts[i].Length;
                        i++;
                    }
                }
                else
                {
                    group.Add(parts[i]);   // MyMemory / gtx: one query per request
                    i++;
                }
                List<string> got = TranslateGroup(group, toLang, source);
                for (int k = 0; k < group.Count; k++) result.Add(k < got.Count ? got[k] : "");
            }
            return result;
        }

        private static List<string> TranslateGroup(List<string> group, string toLang, string source)
        {
            if (source != null && source.StartsWith("MyMemory")) return TranslateMyMemory(group, toLang);
            string baseUrl = (source != null && source.StartsWith("Google 备用")) ? GoogleGtxUrl : GoogleBatchUrl;
            string url = baseUrl + "&sl=auto&tl=" + toLang;
            foreach (string g in group) url += "&q=" + Uri.EscapeDataString(g);
            string body = HttpGet(url, 60000);
            List<string> res = new List<string>();
            foreach (object o in Json.List(Json.Parse(body))) res.Add(ExtractTranslated(o));
            while (res.Count < group.Count) res.Add("");
            return res;
        }

        private static List<string> TranslateMyMemory(List<string> group, string toLang)
        {
            List<string> res = new List<string>();
            foreach (string g in group)
            {
                string text = "";
                try
                {
                    // MyMemory needs an explicit source language (no "auto") and caps the query at 500 chars
                    string url = MyMemoryUrl + "?q=" + Uri.EscapeDataString(g) + "&langpair=" + Uri.EscapeDataString("en|" + toLang);
                    Dictionary<string, object> d = Json.Dict(Json.Parse(HttpGet(url, 60000)));
                    Dictionary<string, object> data = d == null ? null : Json.Dict(d.ContainsKey("responseData") ? d["responseData"] : null);
                    if (data != null) text = Json.Str(data, "translatedText", "");
                }
                catch { }
                string upper = text.ToUpperInvariant();
                if (upper.IndexOf("QUERY LENGTH LIMIT") >= 0 || upper.IndexOf("MYMEMORY WARNING") >= 0 || upper.IndexOf("INVALID") >= 0)
                    text = "";
                res.Add(text);
            }
            return res;
        }

        /// <summary>
        /// The endpoint answers ["text", ...] (flat), [["text","en"], ...] (with detected language)
        /// or [[["text","src",...],...],...] (sentence segments), so dig for the text only.
        /// </summary>
        private static string ExtractTranslated(object node)
        {
            string s = node as string;
            if (s != null) return s;
            List<object> list = Json.List(node);
            if (list.Count == 0) return "";
            string first = list[0] as string;
            if (first != null) return first;      // ["译文","en"] -> 译文
            StringBuilder sb = new StringBuilder();
            foreach (object child in list) sb.Append(ExtractTranslated(child));
            return sb.ToString();
        }
    }
}
