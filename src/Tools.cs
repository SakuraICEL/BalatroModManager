using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BMM
{
    public class BackupItem
    {
        public string Name = "";
        public string Path = "";
        public DateTime Time;
        public long Size;
        public override string ToString()
        {
            return Time.ToString("yyyy-MM-dd HH:mm:ss") + "   " + Fs.Human(Size) + "   " + Name;
        }
    }

    /// <summary>Backup / restore of %AppData%\Balatro profile data (never touches the Mods folder).</summary>
    public static class SaveManager
    {
        private static readonly string[] Items = new string[] { "1", "settings.jkr" };

        public static string BackupNow(string label)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string name = (string.IsNullOrEmpty(label) ? "save_" : label + "_") + stamp;
            string dir = Path.Combine(Paths.SaveBackupDir, name);
            Fs.EnsureDir(dir);
            int copied = 0;
            foreach (string item in Items)
            {
                string src = Path.Combine(Paths.BalatroData, item);
                string dst = Path.Combine(dir, item);
                if (Directory.Exists(src)) { Fs.CopyDir(src, dst); copied++; }
                else if (File.Exists(src)) { File.Copy(src, dst, true); copied++; }
            }
            if (copied == 0) throw new InvalidOperationException("找不到存档（" + Paths.BalatroData + "）");
            return dir;
        }

        public static List<BackupItem> List()
        {
            List<BackupItem> res = new List<BackupItem>();
            try
            {
                if (!Directory.Exists(Paths.SaveBackupDir)) return res;
                foreach (string d in Directory.GetDirectories(Paths.SaveBackupDir))
                {
                    BackupItem b = new BackupItem();
                    b.Path = d;
                    b.Name = Path.GetFileName(d);
                    b.Time = Directory.GetLastWriteTime(d);
                    b.Size = Fs.DirSize(d);
                    res.Add(b);
                }
                res.Sort(delegate (BackupItem a, BackupItem b) { return b.Time.CompareTo(a.Time); });
            }
            catch { }
            return res;
        }

        public static string Restore(BackupItem item)
        {
            if (item == null) throw new InvalidOperationException("没有选中备份");
            if (!Directory.Exists(item.Path)) throw new InvalidOperationException("备份不存在：" + item.Path);
            string safety = BackupNow("before_restore");
            AddLog("恢复前自动备份 -> " + safety);
            foreach (string name in Items)
            {
                string src = Path.Combine(item.Path, name);
                string dst = Path.Combine(Paths.BalatroData, name);
                if (Directory.Exists(src))
                {
                    if (Directory.Exists(dst)) Directory.Delete(dst, true);
                    Fs.CopyDir(src, dst);
                }
                else if (File.Exists(src))
                {
                    File.Copy(src, dst, true);
                }
            }
            return safety;
        }

        public static void AddLog(string line)
        {
            try
            {
                Fs.EnsureDir(Paths.ManagerDir);
                File.AppendAllText(Path.Combine(Paths.ManagerDir, "manager.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }
    }

    public static class Report
    {
        public static string LovelyText(string gameDir)
        {
            string v = GameLocator.LovelyVersion(gameDir);
            if (v == null) return "缺失（游戏目录里没有 winmm.dll）";
            if (v.Length == 0) return "已安装（版本未知，启动一次游戏后即可读出）";
            return "已安装，版本 " + v;
        }

        public static string Build(Settings s, List<ModInfo> mods, bool includeLog)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("========== 环境 ==========");
            sb.AppendLine("游戏目录：" + (string.IsNullOrEmpty(s.GameDir) ? "(未设置)" : s.GameDir));
            bool exeOk = s.GameDir.Length > 0 && File.Exists(Path.Combine(s.GameDir, "Balatro.exe"));
            sb.AppendLine("Balatro.exe：" + (exeOk ? "找到" : "没找到（请在“设置”页修正游戏目录）"));
            sb.AppendLine("Lovely 注入器：" + LovelyText(s.GameDir));
            sb.AppendLine("Mods 目录：" + Paths.ModsDir);
            sb.AppendLine("Steamodded 框架：" + (Directory.Exists(Path.Combine(Paths.ModsDir, "smods")) ? "已安装" : "缺失（没有 mod 会被加载）"));
            if (exeOk && File.Exists(Path.Combine(s.GameDir, "version.dll")) && File.Exists(Path.Combine(s.GameDir, "winmm.dll")))
                sb.AppendLine("提示：游戏目录里同时存在 winmm.dll 和 version.dll，旧的 version.dll 建议删掉");
            sb.AppendLine("游戏是否正在运行：" + (ModLibrary.IsGameRunning() ? "是（改动需要重启游戏才生效）" : "否"));

            int enabled = 0, disabled = 0;
            foreach (ModInfo m in mods) { if (m.Enabled) enabled++; else disabled++; }
            sb.AppendLine();
            sb.AppendLine("========== 概览 ==========");
            sb.AppendLine("共 " + mods.Count + " 项：启用 " + enabled + "，禁用 " + disabled);

            List<string> okLines;
            List<Issue> issues = Deps.Analyze(mods, s.GameDir, out okLines);

            sb.AppendLine();
            sb.AppendLine("========== 检测结果 ==========");
            sb.AppendLine("错误 " + Deps.Count(issues, "错误") + " 项，警告 " + Deps.Count(issues, "警告") + " 项，提示 " + Deps.Count(issues, "提示") + " 项");
            if (issues.Count == 0) sb.AppendLine("没有发现问题，可以放心玩。");
            foreach (string sev in new string[] { "错误", "警告", "提示" })
            {
                foreach (Issue i in issues)
                {
                    if (i.Severity != sev) continue;
                    sb.AppendLine("[" + i.Severity + "] " + i.Target + " — " + i.Message);
                }
            }

            sb.AppendLine();
            sb.AppendLine("========== Mod 清单 ==========");
            foreach (ModInfo m in mods)
            {
                sb.Append("[" + (m.Enabled ? "启用" : "禁用") + "] " + m.FolderName);
                if (m.Version.Parts.Count > 0) sb.Append("  版本 " + m.Version.Raw);
                if (m.Author.Length > 0) sb.Append("  作者 " + m.Author);
                if (m.HasMetadata) sb.Append("  元数据 " + m.MetadataKind);
                else sb.Append("  元数据 无");
                sb.Append("  " + Fs.Human(m.SizeBytes));
                sb.AppendLine();
                if (m.Dependencies.Count > 0) sb.AppendLine("        依赖：" + string.Join("，", m.Dependencies.ToArray()));
                if (m.Conflicts.Count > 0) sb.AppendLine("        冲突声明：" + string.Join("，", m.Conflicts.ToArray()));
                if (m.NestingProblem) sb.AppendLine("        ! 套娃目录：" + m.NestedInner);
            }

            sb.AppendLine();
            sb.AppendLine("========== 黑名单（被禁用 / 被框架自动跳过）==========");
            Blacklist bl = Blacklist.Load(Paths.BlacklistFile);
            int shown = 0;
            foreach (string line in bl.Lines)
            {
                string t = line.Trim();
                if (t.Length == 0 || t.StartsWith("#")) continue;
                sb.AppendLine("  " + t);
                shown++;
            }
            if (shown == 0) sb.AppendLine("  (空)");

            if (includeLog)
            {
                List<string> logs = ModLibrary.LogFiles();
                sb.AppendLine();
                sb.AppendLine("========== 最近一次启动日志里的问题 ==========");
                if (logs.Count == 0) sb.AppendLine("还没有日志（先启动一次游戏）");
                else
                {
                    sb.AppendLine("日志文件：" + Path.GetFileName(logs[0]));
                    List<string> problems = ModLibrary.LogProblems(logs[0], 12);
                    if (problems.Count == 0) sb.AppendLine("没有发现错误行。");
                    foreach (string p in problems) sb.AppendLine("  " + p);
                }
            }
            return sb.ToString();
        }
    }
}
