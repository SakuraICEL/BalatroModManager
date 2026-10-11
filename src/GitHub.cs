using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BMM
{
    public class GhRepo
    {
        public string Name = "";
        public string Owner = "";
        public string FullName = "";
        public string Description = "";
        public string HtmlUrl = "";
        public string AvatarUrl = "";
        public string DefaultBranch = "main";
        public string UpdatedAt = "";
        public long Stars;
        public long SizeKb;
        public bool Archived;
    }

    /// <summary>
    /// Second mod source: GitHub. Anonymous API (no token, no account, nothing personal),
    /// so it is rate limited: 60 core requests/hour and 10 searches/minute.
    /// </summary>
    public static class GitHub
    {
        private const string Api = "https://api.github.com";

        public static string LastError = "";

        public static readonly string[] TopicFilters = new string[] { "全部", "balatro-mod", "smods", "balatro" };

        private static List<TsPackage> _lastSearch;
        private static string _lastQuery = "";

        private static string Friendly(Exception ex)
        {
            string m = ex.Message;
            if (m.IndexOf("403", StringComparison.Ordinal) >= 0 || m.IndexOf("429", StringComparison.Ordinal) >= 0)
                return "GitHub 接口限流了（未登录每分钟最多搜 10 次），等 1 分钟再试，或直接看已缓存的结果";
            if (m.IndexOf("404", StringComparison.Ordinal) >= 0) return "GitHub 上没有这个东西（404）";
            return m;
        }

        public static List<TsPackage> SearchPackages(string query, string topic, out string error)
        {
            error = "";
            LastError = "";
            string t = (query ?? "").Trim();
            string q;
            if (t.Length == 0) q = "topic:" + ((topic == null || topic == "全部") ? "balatro-mod" : topic);
            else if (t.IndexOf(':') >= 0) q = t;
            else q = t + " balatro";

            string url = Api + "/search/repositories?q=" + Uri.EscapeDataString(q) + "&sort=stars&order=desc&per_page=50";
            try
            {
                Dictionary<string, object> root = Json.Dict(Json.Parse(Thunderstore.HttpGet(url, 60000)));
                List<TsPackage> res = new List<TsPackage>();
                foreach (object o in Json.List(root == null ? null : (root.ContainsKey("items") ? root["items"] : null)))
                {
                    Dictionary<string, object> d = Json.Dict(o);
                    if (d == null) continue;
                    res.Add(ToPackage(d));
                }
                _lastSearch = res;
                _lastQuery = q;
                return res;
            }
            catch (Exception ex)
            {
                error = Friendly(ex);
                LastError = error;
                if (_lastSearch != null && _lastSearch.Count > 0)
                {
                    error = error + "；已显示上一次的搜索结果";
                    return _lastSearch;
                }
                return new List<TsPackage>();
            }
        }

        public static TsPackage ToPackage(Dictionary<string, object> d)
        {
            TsPackage p = new TsPackage();
            p.Source = "github";
            p.Name = Json.Str(d, "name", "");
            p.FullName = Json.Str(d, "full_name", "");
            p.PackageUrl = Json.Str(d, "html_url", "");
            p.WebsiteUrl = Json.Str(d, "homepage", "");
            p.Description = Json.Str(d, "description", "");
            p.DateUpdated = Json.Str(d, "updated_at", "");
            p.Rating = Json.Num(d, "stargazers_count", 0);
            p.SizeKb = Json.Num(d, "size", 0);
            p.FileSize = p.SizeKb * 1024;          // rough estimate, replaced by the asset size when known
            p.Deprecated = Json.Bool(d, "archived", false);
            p.DefaultBranch = Json.Str(d, "default_branch", "main");
            p.Categories = Json.StrList(d, "topics");
            Dictionary<string, object> owner = Json.Dict(d.ContainsKey("owner") ? d["owner"] : null);
            if (owner != null)
            {
                p.Owner = Json.Str(owner, "login", "");
                p.Icon = Json.Str(owner, "avatar_url", "");
            }
            if (p.WebsiteUrl.Length == 0) p.WebsiteUrl = p.PackageUrl;
            return p;
        }

        /// <summary>Fills Version + DownloadUrl for a GitHub package (release asset zip, tag archive, or branch archive).</summary>
        public static bool ResolveDownload(TsPackage p, out string error)
        {
            error = "";
            if (p == null) return false;
            string full = p.Owner + "/" + p.Name;

            try
            {
                Dictionary<string, object> rel = Json.Dict(Json.Parse(Thunderstore.HttpGet(Api + "/repos/" + full + "/releases/latest", 30000)));
                if (rel != null)
                {
                    string tag = Json.Str(rel, "tag_name", "");
                    if (tag.Length > 0) p.Version = tag;
                    long best = -1;
                    foreach (object a in Json.List(rel.ContainsKey("assets") ? rel["assets"] : null))
                    {
                        Dictionary<string, object> ad = Json.Dict(a);
                        if (ad == null) continue;
                        string nm = Json.Str(ad, "name", "");
                        if (!nm.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                        long sz = Json.Num(ad, "size", 0);
                        if (sz > best)
                        {
                            best = sz;
                            p.DownloadUrl = Json.Str(ad, "browser_download_url", "");
                            p.FileSize = sz;
                        }
                    }
                    if (p.DownloadUrl.Length > 0) return true;
                    if (tag.Length > 0)
                    {
                        p.DownloadUrl = "https://github.com/" + full + "/archive/refs/tags/" + tag + ".zip";
                        return true;
                    }
                }
            }
            catch { }

            string tagOnly = LatestTagOrEmpty(full);
            if (tagOnly.Length > 0)
            {
                p.Version = tagOnly;
                p.DownloadUrl = "https://github.com/" + full + "/archive/refs/tags/" + tagOnly + ".zip";
                return true;
            }
            if (!string.IsNullOrEmpty(p.DefaultBranch))
            {
                p.DownloadUrl = "https://github.com/" + full + "/archive/refs/heads/" + p.DefaultBranch + ".zip";
                return true;
            }
            error = "这个仓库没有 release，也拿不到分支压缩包";
            return false;
        }

        public static string LatestTagOrEmpty(string full)
        {
            try
            {
                Dictionary<string, object> d = Json.Dict(Json.Parse(Thunderstore.HttpGet(Api + "/repos/" + full + "/releases/latest", 30000)));
                string tag = Json.Str(d, "tag_name", "");
                if (tag.Length > 0) return tag;
            }
            catch { }
            try
            {
                List<object> arr = Json.List(Json.Parse(Thunderstore.HttpGet(Api + "/repos/" + full + "/tags?per_page=1", 30000)));
                if (arr.Count > 0)
                {
                    Dictionary<string, object> t = Json.Dict(arr[0]);
                    string nm = Json.Str(t, "name", "");
                    if (nm.Length > 0) return nm;
                }
            }
            catch { }
            return "";
        }

        /// <summary>Compares installed GitHub mods (recorded as github/owner/repo) with their latest tag.</summary>
        public static int MarkUpdates(List<ModInfo> mods)
        {
            int n = 0;
            foreach (ModInfo m in mods)
            {
                if (m.TsNamespace != "github" || m.TsPackage.Length == 0) continue;
                string tag = LatestTagOrEmpty(m.TsPackage);
                if (tag.Length == 0) continue;
                Ver remote = Ver.Parse(tag);
                if (remote.Parts.Count == 0) continue;
                if (m.Version.Parts.Count == 0 || remote.CompareTo(m.Version) > 0) { m.UpdateTo = tag; n++; }
            }
            return n;
        }

        /// <summary>Third mod source: the community curated list (jie65535/awesome-balatro) reduced to GitHub repos.</summary>
        public static List<TsPackage> FromAwesomeList(out string error)
        {
            error = "";
            const string url = "https://raw.githubusercontent.com/jie65535/awesome-balatro/main/README.md";
            string md = Thunderstore.HttpGet(url, 60000);
            List<TsPackage> res = new List<TsPackage>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Regex line = new Regex(@"^\s*[-*]\s*\[([^\]]+)\]\((https?://github\.com/[A-Za-z0-9_.\-]+/[A-Za-z0-9_.\-]+)\)\s*(?:[-–—:]\s*(.+))?$", RegexOptions.Multiline);
            foreach (Match m in line.Matches(md))
            {
                string title = m.Groups[1].Value.Trim();
                string link = m.Groups[2].Value.Trim().TrimEnd('/');
                string desc = m.Groups[3].Success ? m.Groups[3].Value.Trim() : "";
                string[] bits = link.Substring("https://github.com/".Length).Split('/');
                if (bits.Length != 2) continue;
                string owner = bits[0];
                string repo = bits[1];
                if (repo.Equals("awesome-balatro", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(owner + "/" + repo)) continue;
                TsPackage p = new TsPackage();
                p.Source = "github";
                p.Name = repo;
                p.Owner = owner;
                p.FullName = owner + "/" + repo;
                p.PackageUrl = "https://github.com/" + owner + "/" + repo;
                p.WebsiteUrl = p.PackageUrl;
                p.Description = desc.Length > 0 ? desc : title;
                p.Icon = "https://github.com/" + owner + ".png?size=200";   // owner avatar
                p.Categories = new List<string>(new string[] { "精选清单" });
                res.Add(p);
            }
            if (res.Count == 0) error = "清单里没有解析到可用的 GitHub 链接";
            return res;
        }
    }
}
