using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace YundouScore
{
    internal sealed class ScoreCache
    {
        internal static readonly string DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YundouScore");
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private const int Limit = 8 * 1024 * 1024;
        private readonly string root;

        internal ScoreCache(string account, string dataRoot = null)
        {
            if (!Regex.IsMatch(account ?? "", @"\A[1-9][0-9]{0,15}\z")) throw new ArgumentException("无法确认云斗账号，未使用本地缓存。");
            root = Path.Combine(dataRoot ?? DataRoot, "Scores", "v1", account);
        }

        private string Entry(string contest)
        {
            var id = Regex.Match(contest ?? "", @"\A/d/NOIPC/contest/([0-9a-f]{24})\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!id.Success) throw new ArgumentException("比赛编号无效。");
            return Path.Combine(root, id.Groups[1].Value.ToLowerInvariant() + ".cache");
        }

        internal string Read(string contest, out DateTime captured)
        {
            captured = default(DateTime);
            try
            {
                using (var f = File.OpenRead(Entry(contest)))
                using (var reader = new BinaryReader(f, Utf8))
                {
                    if (f.Length < 44 || f.Length > Limit + 44) return null;
                    long ticks = reader.ReadInt64();
                    if (ticks <= 0 || ticks > DateTime.MaxValue.Ticks) return null;
                    var length = reader.ReadInt32();
                    if (length <= 0 || length > Limit || f.Length != length + 44) return null;
                    var expected = reader.ReadBytes(32);
                    var data = reader.ReadBytes(length);
                    if (data.Length != length || !expected.SequenceEqual(Digest(data))) return null;
                    captured = new DateTime(ticks, DateTimeKind.Utc);
                    return Utf8.GetString(data);
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (DecoderFallbackException) { return null; }
        }

        internal DateTime Save(string contest, string csv)
        {
            var path = Entry(contest);
            var data = Utf8.GetBytes(csv);
            if (data.Length == 0 || data.Length > Limit) throw new InvalidDataException("成绩文件为空或超出本地缓存大小限制。");
            var timestamp = DateTime.UtcNow;
            Directory.CreateDirectory(root);
            var temp = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(f, Utf8, true))
                {
                    writer.Write(timestamp.Ticks);
                    writer.Write(data.Length);
                    writer.Write(Digest(data));
                    writer.Write(data);
                    writer.Flush();
                    f.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
                return timestamp;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        private static byte[] Digest(byte[] data)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(data);
        }
    }
}
