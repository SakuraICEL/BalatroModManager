using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BMM
{
    /// <summary>A comparable version: "0.5.17c", "v26.1002.0", "1.0.0~BETA-0624a".</summary>
    public class Ver : IComparable<Ver>
    {
        public readonly List<object> Parts = new List<object>();
        public readonly string Raw;
        private string[] _prerelease;
        private sealed class LargeNumber
        {
            public readonly string Digits;
            public LargeNumber(string value) { Digits = value.TrimStart('0'); if (Digits.Length == 0) Digits = "0"; }
        }

        private Ver(string raw) { Raw = raw; }

        public static Ver Parse(string s)
        {
            Ver v = new Ver(s == null ? "" : s.Trim());
            if (string.IsNullOrEmpty(s)) return v;
            string t = s.Trim();
            if (t.Length > 0 && (t[0] == 'v' || t[0] == 'V')) t = t.Substring(1);
            int metadata = t.IndexOf('+');
            if (metadata >= 0) t = t.Substring(0, metadata);
            int prerelease = t.IndexOfAny(new char[] { '-', '~' });
            if (prerelease >= 0) { v._prerelease = t.Substring(prerelease + 1).ToLowerInvariant().Split('.'); t = t.Substring(0, prerelease); }
            MatchCollection ms = Regex.Matches(t, @"[0-9]+|[A-Za-z]+");
            foreach (Match m in ms)
            {
                long n;
                if (long.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                    v.Parts.Add(n);
                else if (char.IsDigit(m.Value[0]))
                    v.Parts.Add(new LargeNumber(m.Value));
                else
                    v.Parts.Add(m.Value.ToLowerInvariant());
            }
            return v;
        }

        private static int CmpToken(object a, object b)
        {
            bool an = a is long || a is LargeNumber, bn = b is long || b is LargeNumber;
            if (an && bn)
            {
                string left = a is LargeNumber ? ((LargeNumber)a).Digits : ((long)a).ToString(CultureInfo.InvariantCulture);
                string right = b is LargeNumber ? ((LargeNumber)b).Digits : ((long)b).ToString(CultureInfo.InvariantCulture);
                int length = left.Length.CompareTo(right.Length);
                return length != 0 ? length : string.CompareOrdinal(left, right);
            }
            if (an && !bn) return -1;   // a number sorts before a word
            if (!an && bn) return 1;
            return string.CompareOrdinal((string)a, (string)b);
        }

        public int CompareTo(Ver other)
        {
            if (other == null) return 1;
            int n = Math.Max(Parts.Count, other.Parts.Count);
            for (int i = 0; i < n; i++)
            {
                object a = i < Parts.Count ? Parts[i] : null;
                object b = i < other.Parts.Count ? other.Parts[i] : null;
                if (a == null && b == null) continue;
                if (a == null) a = (b is long || b is LargeNumber) ? (object)0L : (object)"";
                if (b == null) b = (a is long || a is LargeNumber) ? (object)0L : (object)"";
                int c = CmpToken(a, b);
                if (c != 0) return c;
            }
            if (_prerelease == null && other._prerelease == null) return 0;
            if (_prerelease == null) return 1;
            if (other._prerelease == null) return -1;
            for (int i = 0; i < Math.Min(_prerelease.Length, other._prerelease.Length); i++)
            {
                string a = _prerelease[i], b = other._prerelease[i];
                bool an = Regex.IsMatch(a, @"^\d+$"), bn = Regex.IsMatch(b, @"^\d+$");
                int c = an && bn ? CmpToken(new LargeNumber(a), new LargeNumber(b)) : an != bn ? (an ? -1 : 1) : string.CompareOrdinal(a, b);
                if (c != 0) return c;
            }
            return _prerelease.Length.CompareTo(other._prerelease.Length);
        }

        public override string ToString() { return Raw; }
    }

    /// <summary>One "(>=1.2.3)" clause of a dependency or conflict string.</summary>
    public class VerClause
    {
        public string Op = "";
        public Ver Version;

        public bool Test(Ver installed)
        {
            if (Version == null || Version.Parts.Count == 0) return true;
            int c = installed.CompareTo(Version);
            switch (Op)
            {
                case ">=": case "~": case "": return c >= 0;
                case "<=": return c <= 0;
                case ">": case ">>": return c > 0;
                case "<": case "<<": return c < 0;
                case "=": case "==": return c == 0;
                default: return c >= 0;
            }
        }

        public override string ToString() { return Op + (Version == null ? "" : Version.Raw); }
    }

    /// <summary>"Amulet (>=2.7) (<<3.0.0)" -> name + clauses.</summary>
    public class DepSpec
    {
        public string Name = "";
        public readonly List<VerClause> Clauses = new List<VerClause>();

        public static DepSpec Parse(string s)
        {
            DepSpec d = new DepSpec();
            if (string.IsNullOrEmpty(s)) return d;
            string t = s.Trim();
            int paren = t.IndexOf('(');
            d.Name = (paren < 0 ? t : t.Substring(0, paren)).Trim();
            if (paren >= 0)
            {
                foreach (Match m in Regex.Matches(t.Substring(paren), @"\(([^)]*)\)"))
                {
                    string body = m.Groups[1].Value.Trim();
                    if (body.Length == 0) continue;
                    string op = "";
                    string[] ops = new string[] { ">>", "<<", ">=", "<=", "==", ">", "<", "=", "~" };
                    foreach (string o in ops)
                    {
                        if (body.StartsWith(o, StringComparison.Ordinal)) { op = o; body = body.Substring(o.Length).Trim(); break; }
                    }
                    // Thunderstore style "Owner-Package-1.2.3": keep only the trailing version-ish piece
                    VerClause c = new VerClause();
                    c.Op = op;
                    c.Version = Ver.Parse(body);
                    d.Clauses.Add(c);
                }
            }
            return d;
        }

        public bool Test(Ver installed)
        {
            foreach (VerClause c in Clauses) if (!c.Test(installed)) return false;
            return true;
        }

        public override string ToString() { return Name; }
    }

    public class Issue
    {
        public string Severity;   // 错误 / 警告 / 提示
        public string Target;     // mod folder / "环境"
        public string Message;

        public Issue(string severity, string target, string message)
        {
            Severity = severity; Target = target; Message = message;
        }
    }

    /// <summary>Dependency + conflict analysis over the installed mod list.</summary>
    public static class Deps
    {
        public static ModInfo FindProvider(List<ModInfo> mods, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string want = name.Trim().ToLowerInvariant();
            foreach (ModInfo m in mods)
            {
                if (Eq(m.Id, want) || Eq(m.DisplayName, want) || Eq(m.FolderName, want)) return m;
                foreach (string p in m.Provides) if (Eq(p, want)) return m;
            }
            return null;
        }

        private static bool Eq(string a, string lowerWant)
        {
            return !string.IsNullOrEmpty(a) && a.Trim().ToLowerInvariant() == lowerWant;
        }

        private static bool IsCoreName(string name)
        {
            string n = (name ?? "").Trim().ToLowerInvariant();
            return n == "lovely" || n == "smods" || n == "steamodded";
        }

        public static List<Issue> Analyze(List<ModInfo> mods, string gameDir, out List<string> okLines)
        {
            List<Issue> issues = new List<Issue>();
            okLines = new List<string>();

            // virtual entries for the two things that are not plain folders: the injector and the framework
            List<ModInfo> pool = new List<ModInfo>(mods);
            string lovelyVersion = GameLocator.LovelyVersion(gameDir);
            if (lovelyVersion != null)
            {
                ModInfo lovely = new ModInfo();
                lovely.FolderName = "Lovely";
                lovely.DisplayName = "Lovely";
                lovely.Id = "Lovely";
                lovely.IsFramework = true;
                lovely.Version = Ver.Parse(lovelyVersion);
                lovely.VersionUnknown = lovelyVersion.Length == 0;
                pool.Add(lovely);
            }

            List<ModInfo> enabled = new List<ModInfo>();
            foreach (ModInfo m in mods) if (m.Enabled && !m.IsFramework) enabled.Add(m);

            // duplicate ids / provides
            Dictionary<string, ModInfo> byId = new Dictionary<string, ModInfo>();
            Dictionary<string, ModInfo> byProvide = new Dictionary<string, ModInfo>();
            foreach (ModInfo m in enabled)
            {
                if (!string.IsNullOrEmpty(m.Id))
                {
                    string key = m.Id.ToLowerInvariant();
                    if (byId.ContainsKey(key))
                        issues.Add(new Issue("错误", m.FolderName, "与 " + byId[key].FolderName + " 使用同一个 id [" + m.Id + "]，框架只会加载其中一个"));
                    else byId[key] = m;
                }
                foreach (string p in m.Provides)
                {
                    string key = p.ToLowerInvariant();
                    if (byProvide.ContainsKey(key) && byProvide[key] != m)
                        issues.Add(new Issue("警告", m.FolderName, "和 " + byProvide[key].FolderName + " 都声明提供 [" + p + "]，可能导致冲突"));
                    else byProvide[key] = m;
                }
            }

            foreach (ModInfo m in mods)
            {
                if (m.NestingProblem)
                    issues.Add(new Issue("错误", m.FolderName, "目录套娃（" + m.FolderName + "\\" + m.NestedInner + "），框架无法加载；请把内层文件上移一层"));
                if (!m.HasMetadata && !m.IsFramework)
                    issues.Add(new Issue("提示", m.FolderName, "没找到 mod 元数据（.json / manifest.json），可能不是 Steamodded/Lovely mod"));
            }

            foreach (ModInfo m in mods)
            {
                if (!m.Enabled) continue;

                List<string> missing = new List<string>();
                List<string> disabledDeps = new List<string>();
                foreach (string dep in m.Dependencies)
                {
                    if (string.IsNullOrEmpty(dep)) continue;
                    if (dep.StartsWith("Thunderstore-", StringComparison.OrdinalIgnoreCase)) continue; // that is Lovely itself
                    DepSpec spec = DepSpec.Parse(dep);
                    ModInfo provider = FindProvider(pool, spec.Name);
                    if (provider == null)
                    {
                        if (IsCoreName(spec.Name)) continue;   // already covered by the environment section
                        missing.Add(dep);
                        continue;
                    }
                    if (!provider.Enabled) { disabledDeps.Add(provider.FolderName); continue; }
                    if (provider.VersionUnknown) continue;     // cannot judge, do not cry wolf
                    Ver installed = provider.Version;
                    if (!spec.Test(installed))
                        issues.Add(new Issue("错误", m.FolderName, "依赖 " + spec.Name + " 版本过低/不符：需要 " + spec + "，已装 " + installed.Raw));
                }
                if (missing.Count > 0)
                    issues.Add(new Issue("错误", m.FolderName, "缺少依赖：" + string.Join("，", missing.ToArray())));
                if (disabledDeps.Count > 0)
                    issues.Add(new Issue("警告", m.FolderName, "依赖已被禁用：" + string.Join("，", disabledDeps.ToArray())));

                // this mod declares conflicts
                foreach (string con in m.Conflicts)
                {
                    if (string.IsNullOrEmpty(con)) continue;
                    DepSpec spec = DepSpec.Parse(con);
                    ModInfo other = FindProvider(pool, spec.Name);
                    if (other == null || !other.Enabled || other == m) continue;
                    if (other.VersionUnknown) continue;
                    if (spec.Test(other.Version))
                        issues.Add(new Issue("错误", m.FolderName, "与已启用的 " + other.FolderName + " (" + other.Version.Raw + ") 冲突：声明了 " + con));
                }

                // somebody else declares a conflict with this mod
                foreach (ModInfo other in enabled)
                {
                    if (other == m) continue;
                    foreach (string con in other.Conflicts)
                    {
                        DepSpec spec = DepSpec.Parse(con);
                        if (!Eq(m.Id, spec.Name.Trim().ToLowerInvariant()) && !Eq(m.DisplayName, spec.Name.Trim().ToLowerInvariant()) && !Eq(m.FolderName, spec.Name.Trim().ToLowerInvariant())) continue;
                        if (spec.Test(m.Version))
                            issues.Add(new Issue("错误", m.FolderName, "被 " + other.FolderName + " 声明为冲突：" + con));
                    }
                }

                if (m.Dependencies.Count == 0 && m.Conflicts.Count == 0) { }
                else okLines.Add(m.FolderName + "：依赖 " + m.Dependencies.Count + " 条，冲突声明 " + m.Conflicts.Count + " 条");
            }

            if (lovelyVersion == null)
                issues.Add(new Issue("错误", "环境", "游戏目录里没找到 Lovely 注入器（winmm.dll 或 version.dll），任何 mod 都不会生效"));
            else if (lovelyVersion.Length == 0)
                okLines.Add("Lovely 已安装（读不到版本号，先启动一次游戏就能读出来）");
            else if (Ver.Parse(lovelyVersion).CompareTo(Ver.Parse("0.9.0")) < 0)
                issues.Add(new Issue("提示", "环境", "Lovely 版本较旧（" + lovelyVersion + "），新 mod 通常要求 0.9.0+"));
            else
                okLines.Add("Lovely " + lovelyVersion + " 已安装");

            ModInfo framework = FindProvider(pool, "Steamodded");
            if (framework == null)
                issues.Add(new Issue("错误", "环境", "Mods 目录里没有 smods（Steamodded 框架），所有 mod 都不会加载"));

            return issues;
        }

        public static int Count(List<Issue> issues, string severity)
        {
            int n = 0;
            foreach (Issue i in issues) if (i.Severity == severity) n++;
            return n;
        }

        public static string Format(List<Issue> issues)
        {
            StringBuilder sb = new StringBuilder();
            foreach (Issue i in issues)
                sb.AppendLine("[" + i.Severity + "] " + i.Target + " — " + i.Message);
            return sb.ToString();
        }
    }
}
