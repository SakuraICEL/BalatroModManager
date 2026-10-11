using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace BMM
{
    public class ModInfo
    {
        public string FolderName = "";      // folder name, or file name for single-file mods
        public string FullPath = "";
        public bool IsFile;
        public bool IsFramework;            // the "smods" folder itself
        public string Id = "";
        public string DisplayName = "";
        public string Author = "";
        public string Description = "";
        public string MainFile = "";
        public long Priority;
        public Ver Version = Ver.Parse("");
        public List<string> Dependencies = new List<string>();
        public List<string> Conflicts = new List<string>();
        public List<string> Provides = new List<string>();
        public string Website = "";
        public string MetadataKind = "";    // smods | manifest | legacy | ""
        public bool HasMetadata { get { return MetadataKind.Length > 0; } }
        public bool Enabled = true;
        public bool NestingProblem;
        public string NestedInner = "";
        public long SizeBytes;
        public bool VersionUnknown;         // e.g. Lovely: installed but its version cannot be read
        public string TsNamespace = "";     // set for mods installed from Thunderstore
        public string TsPackage = "";
        public string UpdateTo = "";        // filled in by the update check

        public bool IsSystem { get { return IsFramework; } }

        public override string ToString()
        {
            string n = DisplayName.Length > 0 ? DisplayName : FolderName;
            return n + (Version.Parts.Count > 0 ? " v" + Version.Raw : "");
        }
    }

    public class Blacklist
    {
        public readonly List<string> Lines = new List<string>();
        private readonly HashSet<string> _names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public string Path;

        public static Blacklist Load(string path)
        {
            Blacklist b = new Blacklist();
            b.Path = path;
            try
            {
                if (File.Exists(path))
                {
                    foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        b.Lines.Add(line);
                        string t = line.Trim();
                        if (t.Length > 0 && !t.StartsWith("#")) b._names.Add(t);
                    }
                }
            }
            catch { }
            return b;
        }

        public bool Contains(string name) { return _names.Contains(name); }

        public void Set(string name, bool disabled)
        {
            if (disabled)
            {
                if (_names.Contains(name)) return;
                _names.Add(name);
                Lines.Add(name);
            }
            else
            {
                if (!_names.Contains(name)) return;
                _names.Remove(name);
                for (int i = Lines.Count - 1; i >= 0; i--)
                    if (Lines[i].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) Lines.RemoveAt(i);
            }
        }

        public void Save()
        {
            // nothing enabled/disabled left and no comments to keep -> do not leave a stray file behind
            if (_names.Count == 0)
            {
                bool meaningful = false;
                foreach (string l in Lines)
                {
                    string t = l.Trim();
                    if (t.Length > 0 && !t.StartsWith("#")) { meaningful = true; break; }
                }
                if (!meaningful)
                {
                    try { if (File.Exists(Path)) File.Delete(Path); } catch { }
                    return;
                }
            }
            Fs.EnsureDir(System.IO.Path.GetDirectoryName(Path));
            StringBuilder sb = new StringBuilder();
            foreach (string l in Lines) sb.AppendLine(l);
            Fs.WriteAtomic(Path, sb.ToString());
        }
    }

    public static class ModLibrary
    {
        public static bool IsGameRunning()
        {
            try { return Process.GetProcessesByName("Balatro").Length > 0; }
            catch { return false; }
        }

        /// <summary>Reads every folder / single-file mod in the Mods directory.</summary>
        public static List<ModInfo> Scan()
        {
            List<ModInfo> mods = new List<ModInfo>();
            Blacklist bl = Blacklist.Load(Paths.BlacklistFile);
            if (!Directory.Exists(Paths.ModsDir)) return mods;

            foreach (string dir in Directory.GetDirectories(Paths.ModsDir))
            {
                string name = System.IO.Path.GetFileName(dir);
                if (name.Equals("lovely", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.StartsWith(".")) continue;
                ModInfo m = ReadFolder(dir);
                m.Enabled = !bl.Contains(name);
                mods.Add(m);
            }

            foreach (string file in Directory.GetFiles(Paths.ModsDir))
            {
                string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                if (ext != ".lua" && ext != ".disabled") continue;
                string name = System.IO.Path.GetFileName(file);
                ModInfo m = new ModInfo();
                m.FolderName = name;
                m.FullPath = file;
                m.IsFile = true;
                m.DisplayName = System.IO.Path.GetFileNameWithoutExtension(name);
                m.MetadataKind = "single-file";
                m.SizeBytes = new FileInfo(file).Length;
                m.Enabled = !name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) && !bl.Contains(name);
                mods.Add(m);
            }

            mods.Sort(delegate (ModInfo a, ModInfo b) { return string.Compare(a.FolderName, b.FolderName, StringComparison.OrdinalIgnoreCase); });
            ApplyInstalledState(mods);
            return mods;
        }

        private static ModInfo ReadFolder(string dir)
        {
            ModInfo m = new ModInfo();
            m.FolderName = System.IO.Path.GetFileName(dir);
            m.FullPath = dir;
            m.IsFramework = m.FolderName.Equals("smods", StringComparison.OrdinalIgnoreCase);
            m.SizeBytes = Fs.DirSize(dir);

            string bestJson = null;
            bool bestHasId = false;
            string manifestJson = null;
            foreach (string f in Directory.GetFiles(dir, "*.json"))
            {
                string fn = System.IO.Path.GetFileName(f);
                if (fn.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) { manifestJson = f; continue; }
                try
                {
                    Dictionary<string, object> d = Json.Dict(Json.Parse(File.ReadAllText(f, Encoding.UTF8)));
                    bool hasId = d != null && Json.Str(d, "id", "").Length > 0;
                    if (hasId && (!bestHasId || fn.StartsWith(m.FolderName, StringComparison.OrdinalIgnoreCase)))
                    {
                        bestJson = f; bestHasId = true;
                    }
                    else if (bestJson == null) bestJson = f;
                }
                catch { }
            }

            if (bestJson != null)
            {
                try
                {
                    Dictionary<string, object> d = Json.Dict(Json.Parse(File.ReadAllText(bestJson, Encoding.UTF8)));
                    if (d != null)
                    {
                        m.MetadataKind = "smods";
                        m.Id = Json.Str(d, "id", "");
                        m.DisplayName = Json.Str(d, "display_name", Json.Str(d, "name", m.FolderName));
                        List<string> authors = Json.StrList(d, "author");
                        m.Author = string.Join(", ", authors.ToArray());
                        m.Description = Json.Str(d, "description", "");
                        m.MainFile = Json.Str(d, "main_file", "");
                        m.Priority = Json.Num(d, "priority", 0);
                        m.Version = Ver.Parse(Json.Str(d, "version", ""));
                        m.Dependencies = Json.StrList(d, "dependencies");
                        m.Conflicts = Json.StrList(d, "conflicts");
                        m.Provides = Json.StrList(d, "provides");
                        m.Website = Json.Str(d, "website_url", "");
                    }
                }
                catch { }
            }

            if (!m.HasMetadata && manifestJson != null)
            {
                try
                {
                    Dictionary<string, object> d = Json.Dict(Json.Parse(File.ReadAllText(manifestJson, Encoding.UTF8)));
                    if (d != null)
                    {
                        m.MetadataKind = "manifest";
                        m.DisplayName = Json.Str(d, "name", m.FolderName);
                        m.Description = Json.Str(d, "description", "");
                        m.Version = Ver.Parse(Json.Str(d, "version_number", ""));
                        m.Website = Json.Str(d, "website_url", "");
                        List<string> deps = Json.StrList(d, "dependencies");
                        foreach (string dep in deps)
                        {
                            string[] bits = dep.Split('-');
                            m.Dependencies.Add(bits.Length >= 2 ? bits[1] : dep);
                        }
                    }
                }
                catch { }
            }

            if (!m.HasMetadata)
            {
                string legacy = System.IO.Path.Combine(dir, "steamodded_metadata.lua");
                if (File.Exists(legacy))
                {
                    try
                    {
                        string text = File.ReadAllText(legacy, Encoding.UTF8);
                        m.MetadataKind = "legacy";
                        m.DisplayName = FirstMatch(text, "name") ?? m.FolderName;
                        m.Version = Ver.Parse(FirstMatch(text, "version") ?? "");
                        m.Author = FirstMatch(text, "author") ?? "";
                        m.Description = FirstMatch(text, "description") ?? "";
                    }
                    catch { }
                }
            }

            // SMODS mods ship version.lua; it is more reliable than manifest.json (which upstream forgets to bump)
            string versionLua = System.IO.Path.Combine(dir, "version.lua");
            if (File.Exists(versionLua))
            {
                try
                {
                    Match mm = Regex.Match(File.ReadAllText(versionLua, Encoding.UTF8), "\"([^\"]+)\"");
                    string v = mm.Success ? mm.Groups[1].Value : null;
                    if (v != null && Ver.Parse(v).Parts.Count > 0) m.Version = Ver.Parse(v);
                }
                catch { }
            }

            if (!m.HasMetadata)
            {
                string[] subs = Directory.GetDirectories(dir);
                if (subs.Length == 1 && Directory.GetFiles(dir).Length == 0)
                {
                    m.NestingProblem = true;
                    m.NestedInner = System.IO.Path.GetFileName(subs[0]);
                }
                m.DisplayName = m.FolderName;
            }

            return m;
        }

        private static string FirstMatch(string text, string key)
        {
            Match m = Regex.Match(text, key + @"\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        // ---------- enable / disable ----------

        public static void SetEnabled(ModInfo m, bool enabled)
        {
            if (m.IsFramework && !enabled)
                throw new InvalidOperationException("Steamodded 框架本身不能禁用。");
            if (m.IsFile)
            {
                string target = enabled
                    ? (m.FullPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? m.FullPath.Substring(0, m.FullPath.Length - ".disabled".Length) : null)
                    : (m.FullPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? null : m.FullPath + ".disabled");
                if (target != null)
                {
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(m.FullPath, target);
                }
                return;
            }
            Blacklist bl = Blacklist.Load(Paths.BlacklistFile);
            bl.Set(m.FolderName, !enabled);
            bl.Save();
        }

        public static void SetAllEnabled(List<ModInfo> mods, bool enabled)
        {
            Blacklist bl = Blacklist.Load(Paths.BlacklistFile);
            foreach (ModInfo m in mods)
            {
                if (m.IsFramework) continue;
                if (m.IsFile) { SetEnabled(m, enabled); continue; }
                bl.Set(m.FolderName, !enabled);
            }
            bl.Save();
        }

        public static int DisabledCount()
        {
            Blacklist bl = Blacklist.Load(Paths.BlacklistFile);
            int n = 0;
            foreach (string s in bl.Lines)
            {
                string t = s.Trim();
                if (t.Length > 0 && !t.StartsWith("#")) n++;
            }
            return n;
        }

        // ---------- uninstall ----------

        /// <summary>Moves the mod into the manager's backup area so it can be restored later.</summary>
        public static string UninstallToBackup(ModInfo m)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string target = System.IO.Path.Combine(Paths.RemovedDir, m.FolderName + "_" + stamp);
            Fs.EnsureDir(Paths.RemovedDir);
            if (m.IsFile) { Directory.CreateDirectory(target); File.Move(m.FullPath, System.IO.Path.Combine(target, m.FolderName)); }
            else Directory.Move(m.FullPath, target);
            ForgetState(m.FolderName);
            return target;
        }

        public static void UninstallToRecycleBin(ModInfo m)
        {
            Fs.DeleteToRecycleBin(m.FullPath);
            ForgetState(m.FolderName);
        }

        // ---------- install ----------

        public class InstallResult
        {
            public string Name = "";
            public string Path = "";
            public bool Replaced;
            public List<string> Warnings = new List<string>();
        }

        /// <summary>Installs a zip: works for GitHub source archives (wrapped in a folder) and Thunderstore zips (files at the root).</summary>
        public static InstallResult InstallZip(string zipPath, string nameOverride, bool force)
        {
            if (!string.IsNullOrEmpty(nameOverride)) InstallTarget(nameOverride);
            Fs.EnsureDir(Paths.TempDir);
            string tmp = System.IO.Path.Combine(Paths.TempDir, "x_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);
            try
            {
                ValidateArchive(zipPath, tmp);
                ZipFile.ExtractToDirectory(zipPath, tmp);
                string root = FindModRoot(tmp);
                string name = nameOverride;
                if (string.IsNullOrEmpty(name)) name = DetectName(root, System.IO.Path.GetFileNameWithoutExtension(zipPath));
                string target = InstallTarget(name);
                InstallResult res = new InstallResult();
                res.Name = name;
                res.Path = target;
                if (Directory.Exists(target) || File.Exists(target))
                {
                    if (!force)
                    {
                        res.Warnings.Add("已存在同名 mod：" + name);
                        res.Replaced = false;
                        return res;
                    }
                    Fs.DeleteToRecycleBin(target);
                    res.Replaced = true;
                }
                Fs.EnsureDir(Paths.ModsDir);
                MoveDir(root, target);
                PostInstallCheck(target, res);
                return res;
            }
            finally
            {
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            }
        }

        public static InstallResult InstallLua(string luaPath, string nameOverride, bool force)
        {
            InstallResult res = new InstallResult();
            string name = nameOverride;
            if (string.IsNullOrEmpty(name)) name = System.IO.Path.GetFileNameWithoutExtension(luaPath);
            string target = InstallTarget(name);
            res.Name = name;
            res.Path = target;
            Fs.EnsureDir(Paths.ModsDir);
            if (Directory.Exists(target) || File.Exists(target))
            {
                if (!force) { res.Warnings.Add("已存在同名 mod：" + name); return res; }
                Fs.DeleteToRecycleBin(target);
                res.Replaced = true;
            }
            if (name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(luaPath, target, true);
                res.Path = target;
            }
            else
            {
                Directory.CreateDirectory(target);
                File.Copy(luaPath, System.IO.Path.Combine(target, System.IO.Path.GetFileName(luaPath)), true);
            }
            return res;
        }

        private static string InstallTarget(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.EndsWith(".") || name == "." || name == ".."
                || Path.IsPathRooted(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0
                || Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase))
                throw new ArgumentException("安装名称必须是有效的单个文件或文件夹名，不能包含路径。", "name");
            string root = Path.GetFullPath(Paths.ModsDir).TrimEnd(Path.DirectorySeparatorChar);
            string target = Path.GetFullPath(Path.Combine(root, name));
            if (!string.Equals(Path.GetDirectoryName(target), root, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("安装位置必须位于 Mods 目录内。", "name");
            if ((File.Exists(target) || Directory.Exists(target)) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("安装位置是链接目录或文件，请先确认并移除该链接。");
            return target;
        }

        private static void ValidateArchive(string zipPath, string destination)
        {
            string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string name = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    string target = Path.GetFullPath(Path.Combine(root, name));
                    if (Path.IsPathRooted(name) || !target.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                        || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                        throw new InvalidDataException("压缩包包含不安全的路径或链接。");
                }
        }

        private static void MoveDir(string from, string to)
        {
            try { Directory.Move(from, to); }
            catch (IOException)
            {
                Fs.CopyDir(from, to);
                Directory.Delete(from, true);
            }
        }

        private static void PostInstallCheck(string target, InstallResult res)
        {
            string inner = System.IO.Path.Combine(target, System.IO.Path.GetFileName(target));
            if (Directory.Exists(inner)) res.Warnings.Add("检测到套娃目录 " + System.IO.Path.GetFileName(target) + "\\" + System.IO.Path.GetFileName(target) + "，可能无法加载");
            if (Directory.GetFiles(target, "*.json").Length == 0 && Directory.GetFiles(target, "*.lua").Length == 0)
                res.Warnings.Add("这个 mod 里没有 .json / .lua 文件，可能不是有效的 mod 包");
        }

        private static string FindModRoot(string extracted)
        {
            string cur = extracted;
            for (int depth = 0; depth < 4; depth++)
            {
                string[] dirs = Directory.GetDirectories(cur);
                string[] files = Directory.GetFiles(cur);
                if (dirs.Length == 1 && files.Length == 0 && !LooksLikeModRoot(dirs[0]))
                {
                    cur = dirs[0];
                    continue;
                }
                if (dirs.Length == 1 && files.Length == 0) return dirs[0];
                return cur;
            }
            return cur;
        }

        private static bool LooksLikeModRoot(string dir)
        {
            if (Directory.GetFiles(dir, "*.json").Length > 0) return true;
            if (Directory.GetFiles(dir, "*.lua").Length > 0) return true;
            foreach (string sub in Directory.GetDirectories(dir))
            {
                string n = System.IO.Path.GetFileName(sub).ToLowerInvariant();
                if (n == "src" || n == "lovely" || n == "items" || n == "assets") return true;
            }
            return false;
        }

        private static string DetectName(string root, string fallback)
        {
            string manifestName = null;
            foreach (string f in Directory.GetFiles(root, "*.json"))
            {
                try
                {
                    Dictionary<string, object> d = Json.Dict(Json.Parse(File.ReadAllText(f, Encoding.UTF8)));
                    if (d == null) continue;
                    string id = Json.Str(d, "id", "");
                    if (id.Length > 0 && !Path.GetFileName(f).Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) return Sanitize(id);
                }
                catch { }
            }
            foreach (string f in Directory.GetFiles(root, "*.json"))
            {
                try
                {
                    Dictionary<string, object> d = Json.Dict(Json.Parse(File.ReadAllText(f, Encoding.UTF8)));
                    if (d == null) continue;
                    string id = Json.Str(d, "id", "");
                    if (id.Length > 0) return Sanitize(id);
                    string nm = Json.Str(d, "name", "");
                    if (nm.Length > 0 && manifestName == null) manifestName = nm;
                }
                catch { }
            }
            if (manifestName != null) return Sanitize(manifestName);
            string baseName = Path.GetFileName(root);
            if (baseName.Equals("x", StringComparison.OrdinalIgnoreCase)) baseName = fallback;
            baseName = Regex.Replace(baseName, @"[-_ ]?v?\d+(\.\d+)+[A-Za-z]?$", "");
            return Sanitize(baseName.Length == 0 ? fallback : baseName);
        }

        private static string Sanitize(string s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
                if (Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0) sb.Append(c);
            string r = sb.ToString().Trim().TrimEnd('.');
            return r.Length == 0 ? "UnnamedMod" : r;
        }

        // ---------- thunderstore install bookkeeping ----------

        private static Dictionary<string, object> _state;

        private static Dictionary<string, object> State()
        {
            if (_state != null) return _state;
            try
            {
                if (File.Exists(Paths.StateFile))
                    _state = Json.Dict(Json.Parse(File.ReadAllText(Paths.StateFile, Encoding.UTF8)));
            }
            catch { }
            if (_state == null) _state = new Dictionary<string, object>();
            return _state;
        }

        public static void RememberSource(string folder, string ns, string pkg, string version)
        {
            Dictionary<string, object> st = State();
            Dictionary<string, object> e = new Dictionary<string, object>();
            e["namespace"] = ns; e["package"] = pkg; e["version"] = version;
            st[folder] = e;
            SaveState();
        }

        private static void ForgetState(string folder)
        {
            Dictionary<string, object> st = State();
            if (st.Remove(folder)) SaveState();
        }

        private static void SaveState()
        {
            try
            {
                Fs.EnsureDir(Paths.ManagerDir);
                File.WriteAllText(Paths.StateFile, Json.Write(_state), new UTF8Encoding(false));
            }
            catch { }
        }

        private static void ApplyInstalledState(List<ModInfo> mods)
        {
            Dictionary<string, object> st = State();
            foreach (ModInfo m in mods)
            {
                object raw;
                if (!st.TryGetValue(m.FolderName, out raw)) continue;
                Dictionary<string, object> e = Json.Dict(raw);
                if (e == null) continue;
                m.TsNamespace = Json.Str(e, "namespace", "");
                m.TsPackage = Json.Str(e, "package", "");
            }
        }

        // ---------- logs ----------

        public static List<string> LogFiles()
        {
            List<string> res = new List<string>();
            try
            {
                if (Directory.Exists(Paths.LogDir))
                {
                    res.AddRange(Directory.GetFiles(Paths.LogDir, "*.log"));
                    res.Sort(delegate (string a, string b) { return File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)); });
                }
            }
            catch { }
            return res;
        }

        public static List<string> LogProblems(string logPath, int maxPerKind)
        {
            List<string> res = new List<string>();
            try
            {
                int errors = 0, blacklist = 0, nomatch = 0, invalid = 0;
                foreach (string line in File.ReadAllLines(logPath, Encoding.UTF8))
                {
                    if (line.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0 && errors < maxPerKind)
                    { res.Add(Trim(line)); errors++; continue; }
                    if (line.IndexOf("blacklist", StringComparison.OrdinalIgnoreCase) >= 0 && line.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) < 0 && blacklist < maxPerKind)
                    { res.Add(Trim(line)); blacklist++; continue; }
                    if (line.IndexOf("resulted in no matches", StringComparison.OrdinalIgnoreCase) >= 0 && nomatch < maxPerKind)
                    { res.Add(Trim(line)); nomatch++; continue; }
                    if (line.IndexOf("invalid metadata", StringComparison.OrdinalIgnoreCase) >= 0 && invalid < maxPerKind)
                    { res.Add(Trim(line)); invalid++; continue; }
                }
            }
            catch { }
            return res;
        }

        private static string Trim(string s)
        {
            s = s.Trim();
            int i = s.IndexOf("INFO - ", StringComparison.Ordinal);
            if (i >= 0) s = s.Substring(i + 7);
            return s.Length > 300 ? s.Substring(0, 300) + "..." : s;
        }
    }
}
