using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace BMM
{
    /// <summary>All well-known locations the manager works with.</summary>
    public static class Paths
    {
        public static string AppData { get { return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } }
        public static string BalatroData { get { return Path.Combine(AppData, "Balatro"); } }
        public static string ModsDir { get { return Path.Combine(BalatroData, "Mods"); } }
        public static string LovelyDir { get { return Path.Combine(ModsDir, "lovely"); } }
        public static string BlacklistFile { get { return Path.Combine(LovelyDir, "blacklist.txt"); } }
        public static string LogDir { get { return Path.Combine(LovelyDir, "log"); } }
        public static string ManagerDir { get { return Path.Combine(AppData, "BalatroModManager"); } }
        public static string SaveBackupDir { get { return Path.Combine(ManagerDir, "backups_save"); } }
        public static string RemovedDir { get { return Path.Combine(ManagerDir, "removed_mods"); } }
        public static string SettingsFile { get { return Path.Combine(ManagerDir, "settings.json"); } }
        public static string StateFile { get { return Path.Combine(ManagerDir, "installed_state.json"); } }
        public static string TsCacheFile { get { return Path.Combine(ManagerDir, "thunderstore_cache.json"); } }
        public static string ReadmeCacheDir { get { return Path.Combine(ManagerDir, "readme_cache"); } }
        public static string IconCacheDir { get { return Path.Combine(ManagerDir, "icon_cache"); } }
        public static string TempDir { get { return Path.Combine(ManagerDir, "temp"); } }
    }

    public class Settings
    {
        public string GameDir = "";
        public bool IncludeLogInReport = true;
        public string TranslateSource = "";
        public bool AutoTranslate = true;
        public string OnlineSource = "thunderstore";

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (File.Exists(Paths.SettingsFile))
                {
                    Dictionary<string, object> d = Json.Dict(Json.Parse(File.ReadAllText(Paths.SettingsFile, Encoding.UTF8)));
                    if (d != null)
                    {
                        s.GameDir = Json.Str(d, "GameDir", "");
                        s.IncludeLogInReport = Json.Bool(d, "IncludeLogInReport", true);
                        s.TranslateSource = Json.Str(d, "TranslateSource", "");
                        s.AutoTranslate = Json.Bool(d, "AutoTranslate", true);
                        s.OnlineSource = Json.Str(d, "OnlineSource", "thunderstore");
                    }
                }
            }
            catch { }
            if (string.IsNullOrEmpty(s.GameDir)) s.GameDir = GameLocator.Detect();
            if (string.IsNullOrEmpty(s.TranslateSource)) s.TranslateSource = Thunderstore.TranslateSources[0];
            if (string.IsNullOrEmpty(s.OnlineSource)) s.OnlineSource = "thunderstore";
            return s;
        }

        public void Save()
        {
            try
            {
                Fs.EnsureDir(Paths.ManagerDir);
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["GameDir"] = GameDir;
                d["IncludeLogInReport"] = IncludeLogInReport;
                d["TranslateSource"] = TranslateSource;
                d["AutoTranslate"] = AutoTranslate;
                d["OnlineSource"] = OnlineSource;
                File.WriteAllText(Paths.SettingsFile, Json.Write(d), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    /// <summary>Thin wrapper around JavaScriptSerializer (netfx has no System.Text.Json).</summary>
    public static class Json
    {
        private static readonly JavaScriptSerializer Ser = Make();

        private static JavaScriptSerializer Make()
        {
            JavaScriptSerializer s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            s.RecursionLimit = 200;
            return s;
        }

        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            return Ser.DeserializeObject(text);
        }

        public static string Write(object value)
        {
            return Ser.Serialize(value);
        }

        public static Dictionary<string, object> Dict(object o)
        {
            return o as Dictionary<string, object>;
        }

        public static List<object> List(object o)
        {
            List<object> res = new List<object>();
            IEnumerable e = o as IEnumerable;
            if (e == null) return res;
            foreach (object item in e) res.Add(item);
            return res;
        }

        public static string Str(Dictionary<string, object> d, string key, string fallback)
        {
            if (d == null) return fallback;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return fallback;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static long Num(Dictionary<string, object> d, string key, long fallback)
        {
            if (d == null) return fallback;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return fallback;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public static bool Bool(Dictionary<string, object> d, string key, bool fallback)
        {
            if (d == null) return fallback;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return fallback;
            try { return Convert.ToBoolean(v, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public static List<string> StrList(Dictionary<string, object> d, string key)
        {
            List<string> res = new List<string>();
            if (d == null) return res;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return res;
            string single = v as string;
            if (single != null) { res.Add(single); return res; }
            foreach (object item in List(v)) if (item != null) res.Add(Convert.ToString(item, CultureInfo.InvariantCulture));
            return res;
        }
    }

    public static class Fs
    {
        public static void EnsureDir(string dir)
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        public static string ReadShared(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader sr = new StreamReader(fs, Encoding.UTF8, true))
                return sr.ReadToEnd();
        }

        public static void WriteAtomic(string path, string content)
        {
            EnsureDir(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static void CopyDir(string from, string to)
        {
            EnsureDir(to);
            foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(to, Rel(from, dir)));
            foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(to, Rel(from, file));
                EnsureDir(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
        }

        private static string Rel(string root, string full)
        {
            string r = root.TrimEnd('\\') + "\\";
            return full.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? full.Substring(r.Length) : Path.GetFileName(full);
        }

        public static long DirSize(string dir)
        {
            long total = 0;
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
            }
            catch { }
            return total;
        }

        public static void DeleteToRecycleBin(string path)
        {
            if (Directory.Exists(path))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else if (File.Exists(path))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }

        public static string Human(long bytes)
        {
            string[] u = new string[] { "B", "KB", "MB", "GB" };
            double v = bytes;
            int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return v.ToString("0.#", CultureInfo.InvariantCulture) + " " + u[i];
        }
    }

    /// <summary>Finds the Balatro install by reading the Steam registry + libraryfolders.vdf.</summary>
    public static class GameLocator
    {
        public static string Detect()
        {
            try
            {
                string steam = null;
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                    if (k != null) steam = k.GetValue("InstallPath") as string;
                if (string.IsNullOrEmpty(steam))
                    using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam"))
                        if (k != null) steam = (k.GetValue("SteamPath") as string);

                if (string.IsNullOrEmpty(steam) || !Directory.Exists(steam)) return "";

                List<string> libs = new List<string>();
                libs.Add(steam);
                string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    foreach (string line in File.ReadAllLines(vdf))
                    {
                        string t = line.Trim();
                        if (!t.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;
                        int first = t.IndexOf('"', 6);
                        int last = t.LastIndexOf('"');
                        if (first > 0 && last > first)
                        {
                            string p = t.Substring(first + 1, last - first - 1).Replace("\\\\", "\\");
                            if (Directory.Exists(p) && !libs.Contains(p)) libs.Add(p);
                        }
                    }
                }

                foreach (string lib in libs)
                {
                    string acf = Path.Combine(lib, "steamapps", "appmanifest_2379780.acf");
                    string dir = null;
                    if (File.Exists(acf))
                    {
                        foreach (string line in File.ReadAllLines(acf))
                        {
                            string t = line.Trim();
                            if (t.StartsWith("\"installdir\""))
                            {
                                int first = t.IndexOf('"', 12);
                                int last = t.LastIndexOf('"');
                                if (first > 0 && last > first) dir = t.Substring(first + 1, last - first - 1);
                            }
                        }
                    }
                    string candidate = dir == null
                        ? Path.Combine(lib, "steamapps", "common", "Balatro")
                        : Path.Combine(lib, "steamapps", "common", dir);
                    if (File.Exists(Path.Combine(candidate, "Balatro.exe"))) return candidate;
                }
            }
            catch { }
            return "";
        }

        /// <summary>True when a Lovely injector dll sits next to Balatro.exe.</summary>
        public static bool LovelyPresent(string gameDir)
        {
            if (string.IsNullOrEmpty(gameDir)) return false;
            return File.Exists(Path.Combine(gameDir, "winmm.dll")) || File.Exists(Path.Combine(gameDir, "version.dll"));
        }

        /// <summary>
        /// Lovely version, or null when the injector is missing.
        /// The dll carries no version resource, so fall back to the version line Lovely writes into its log.
        /// </summary>
        public static string LovelyVersion(string gameDir)
        {
            if (!LovelyPresent(gameDir)) return null;
            try
            {
                foreach (string dll in new string[] { "winmm.dll", "version.dll" })
                {
                    string p = Path.Combine(gameDir, dll);
                    if (!File.Exists(p)) continue;
                    FileVersionInfo vi = FileVersionInfo.GetVersionInfo(p);
                    string v = vi.FileVersion;
                    if (string.IsNullOrEmpty(v)) v = vi.ProductVersion;
                    if (!string.IsNullOrEmpty(v)) return v.Trim();
                }
            }
            catch { }
            try
            {
                string dir = Path.Combine(Paths.LovelyDir, "log");
                if (Directory.Exists(dir))
                {
                    List<string> logs = new List<string>(Directory.GetFiles(dir, "*.log"));
                    logs.Sort(delegate (string a, string b) { return File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)); });
                    if (logs.Count > 0)
                    {
                        foreach (string line in Fs.ReadShared(logs[0]).Split('\n'))
                        {
                            System.Text.RegularExpressions.Match m =
                                System.Text.RegularExpressions.Regex.Match(line, @"Lovely\s+(\d+\.\d+(\.\d+)?)");
                            if (m.Success) return m.Groups[1].Value;
                        }
                    }
                }
            }
            catch { }
            return "";   // installed but version unknown
        }
    }
}
