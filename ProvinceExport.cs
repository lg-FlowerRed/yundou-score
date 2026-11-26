using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace YundouScore
{
    internal static class ProvinceExport
    {
        private static readonly Dictionary<string, string> Codes = ("北京:BJ 天津:TJ 河北:HE 山西:SX 内蒙古:NM 辽宁:LN 吉林:JL 黑龙江:HL 上海:SH 江苏:JS 浙江:ZJ 安徽:AH 福建:FJ 江西:JX 山东:SD 河南:HA 湖北:HB 湖南:HN 广东:GD 广西:GX 海南:HI 重庆:CQ 四川:SC 贵州:GZ 云南:YN 西藏:XZ 陕西:SN 甘肃:GS 青海:QH 宁夏:NX 新疆:XJ 香港:HK 澳门:MO 台湾:TW").Split(' ').ToDictionary(s => s.Split(':')[0], s => s.Split(':')[1], StringComparer.Ordinal);
        private static readonly object SettingsLock = new object();
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        internal static readonly string Root = Path.Combine(ScoreCache.DataRoot, "Exports");

        internal sealed class Selection
        {
            internal string Mode, Group;
            internal string[] Provinces;
            internal bool Compress;
            internal Dictionary<string, int> Cutoffs;
        }

        internal static Selection Validate(string mode, string query, string group, string selected, string zip, string cutoffs)
        {
            if (mode != "province" && mode != "code") throw new ArgumentException("不支持的导出模式。");
            var provinces = (selected ?? "").Split(',').Distinct(StringComparer.Ordinal).ToArray();
            if (provinces.Length == 0 || provinces.Length > Codes.Count || provinces.Any(s => !Codes.ContainsKey(s))) throw new ArgumentException("请选择至少一个有效省份。");
            if (mode == "code" && zip != "0" && zip != "1") throw new ArgumentException("请选择代码输出格式。");
            if (mode == "code") group = "full";
            if (group != "full" && group != "high" && group != "junior") throw new ArgumentException("请选择成绩范围。");
            var values = new Dictionary<string, int>(StringComparer.Ordinal);
            if (group != "full")
            {
                if (!IsNoip(query)) throw new ArgumentException("仅 NOIP 支持按学段筛选。");
                Dictionary<string, object> raw;
                try { raw = new JavaScriptSerializer().DeserializeObject(cutoffs ?? "") as Dictionary<string, object>; }
                catch (Exception ex) { if (!(ex is ArgumentException || ex is InvalidOperationException || ex is FormatException || ex is OverflowException)) throw; throw new ArgumentException("高中实际参赛人数格式不正确。"); }
                foreach (var p in provinces)
                {
                    object value;
                    if (raw == null || !raw.TryGetValue(p, out value) || !(value is int) || (int)value < 0 || (int)value > 9999999)
                        throw new ArgumentException("请为每个所选省份填写有效的高中实际参赛人数（可为 0）。");
                    values.Add(p, (int)value);
                }
            }
            return new Selection { Mode = mode, Group = group, Provinces = provinces, Compress = zip == "1", Cutoffs = values };
        }

        internal static bool IsNoip(string query) { return Regex.IsMatch(query, @"\ANOIP [0-9]{4}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
        internal static string GroupName(string group) { return group == "high" ? "高中" : group == "junior" ? "初中" : "完整"; }
        internal static string Code(string province) { return Codes[province]; }
        internal static bool MatchesGroup(string user, string province, string group, int cutoff)
        {
            if (group == "full") return true;
            var m = Regex.Match(user.Trim(), "\\A" + Code(province) + "-([0-9]+)\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            int number;
            if (!m.Success || !Int32.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number) || number <= 0)
                throw new InvalidDataException(province + "有成绩行不符合「" + Code(province) + "-数字」的准考证号格式。未生成该省学段表；可改选完整成绩表。");
            return group == "high" ? number <= cutoff : number > cutoff;
        }

        internal static Dictionary<string, int> ReadCutoffs(string query, string root = null)
        {
            if (!IsNoip(query)) return new Dictionary<string, int>();
            lock (SettingsLock) return ReadSettings(query, root ?? ScoreCache.DataRoot);
        }

        private static Dictionary<string, int> ReadSettings(string query, string root)
        {
            string path = Path.Combine(root, "NoipCutoffs", query.ToUpperInvariant().Replace(' ', '-') + ".json");
            if (!File.Exists(path)) return new Dictionary<string, int>();
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException("本地高中人数设置文件过大。");
            var settings = new JavaScriptSerializer().Deserialize<Dictionary<string, int>>(File.ReadAllText(path, Utf8));
            if (settings == null || settings.Any(p => !Codes.ContainsKey(p.Key) || p.Value < 0 || p.Value > 9999999)) throw new InvalidDataException("本地高中人数设置无效。");
            return settings;
        }

        internal static void Remember(string query, Selection choice, string root = null)
        {
            if (choice == null || choice.Cutoffs.Count == 0) return;
            root = root ?? ScoreCache.DataRoot;
            lock (SettingsLock)
            {
                var current = ReadSettings(query, root);
                foreach (var pair in choice.Cutoffs) current[pair.Key] = pair.Value;
                var dir = Path.Combine(root, "NoipCutoffs");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, query.ToUpperInvariant().Replace(' ', '-') + ".json");
                var temp = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (var f = new FileStream(temp, FileMode.CreateNew))
                    {
                        var bytes = Utf8.GetBytes(new JavaScriptSerializer().Serialize(current));
                        f.Write(bytes, 0, bytes.Length);
                        f.Flush(true);
                    }
                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }

        internal static string NewOutput(string query, string kind)
        {
            var name = new string(query.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).Take(48).ToArray()).TrimEnd('.', ' ');
            return Path.Combine(Root, "导出-" + name + "-" + kind + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }

        internal static void SelfTest()
        {
            if (!MatchesGroup("TJ-0012", "天津", "high", 160) || !MatchesGroup("TJ-0160", "天津", "high", 160) || MatchesGroup("TJ-0178", "天津", "high", 160) || !MatchesGroup("TJ-0190", "天津", "junior", 160) || !MatchesGroup("TJ-0001", "天津", "junior", 0))
                throw new Exception("NOIP cutoff boundary incorrect");
            bool rejected = false;
            try { MatchesGroup("username", "天津", "high", 160); } catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new Exception("Unrecognized exam ID was assigned a school stage");
            var root = Path.Combine(Path.GetTempPath(), "yundou-cutoffs-" + Guid.NewGuid().ToString("N"));
            try
            {
                Remember("NOIP 2025", Validate("province", "NOIP 2025", "high", "天津,浙江", null, "{\"天津\":160,\"浙江\":0}"), root);
                Remember("noip 2025", Validate("province", "noip 2025", "junior", "天津", null, "{\"天津\":170}"), root);
                var settings = ReadCutoffs("NOIP 2025", root);
                if (settings["天津"] != 170 || settings["浙江"] != 0 || ReadCutoffs("NOIP 2026", root).Count != 0) throw new Exception("Cutoff persistence or scoping incorrect");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
            Console.WriteLine("Provincial scores: NOIP boundaries, validation and persisted counts passed.");
        }
    }
}
