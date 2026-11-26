using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace YundouScore
{
    internal static class AnswerArchive
    {
        private const int MaxEntries = 10000;
        private const int MaxNameBytes = 128;
        private const int MaxDirectoryBytes = 8 * 1024 * 1024;
        private const int MaxSourceBytes = 16 * 1024 * 1024;
        private const long MaxArchiveBytes = 256L * 1024 * 1024;
        private const long MaxTotalSourceBytes = 256L * 1024 * 1024;
        private const string TemporaryPrefix = ".yundou-answers-";

        // No artifact names are part of the raw export format. Fail closed on everything
        // except flat C/C++ source entries; the target judge expects .cpp for these.
        private static readonly Regex SourceName = new Regex(
            @"\AU(?<uid>[+-]?[0-9]+)_P(?<pid>[0-9]+)_R[0-9a-f]{24}_S[0-9]+@[+-]?[0-9]+(?:\.[0-9]+)?\.(?:c|cc|cpp|cxx)\.ccnoio2\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex DeviceName = new Regex(
            @"\A(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|CLOCK\$|(?:COM|LPT)[0-9¹²³])\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly uint[] CrcTable = MakeCrcTable();

        private sealed class Source
        {
            internal ZipArchiveEntry Entry;
            internal string User;
            internal string Problem;
            internal string RelativePath;
            internal uint Crc;
        }

        internal static int Convert(string input, string output, IDictionary<string, string> users,
            IDictionary<string, string> problems, bool compress, CancellationToken ct)
        {
            if (String.IsNullOrWhiteSpace(input)) throw new ArgumentException("An input ZIP path is required.", "input");
            if (String.IsNullOrWhiteSpace(output)) throw new ArgumentException("An output path is required.", "output");
            if (users == null) throw new ArgumentNullException("users");
            if (problems == null) throw new ArgumentNullException("problems");
            ct.ThrowIfCancellationRequested();

            char[] separators = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
            string destination = Path.GetFullPath(output);
            if (String.Equals(destination, Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The output must not be a filesystem root.");
            destination = destination.TrimEnd(separators);
            ValidateComponent(Path.GetFileName(output.TrimEnd(separators)), 255, "output name", true);
            string parent = Path.GetDirectoryName(destination);
            if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Output parent does not exist: " + parent);
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException("Refusing to overwrite existing output: " + destination);

            string temporary = Path.Combine(parent, TemporaryPrefix + Guid.NewGuid().ToString("N") + ".tmp");
            bool ownsTemporary = false;
            try
            {
                int count;
                using (var file = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    uint[] crcs = ReadDirectory(file, ct);
                    using (var zip = new ZipArchive(file, ZipArchiveMode.Read, true, StrictUtf8))
                    {
                        List<Source> sources = Plan(zip, crcs, users, problems, ct);
                        count = sources.Count;
                        ct.ThrowIfCancellationRequested();
                        if (compress)
                        {
                            using (var staging = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            {
                                ownsTemporary = true;
                                using (var result = new ZipArchive(staging, ZipArchiveMode.Create, true, StrictUtf8))
                                    WriteSources(sources, result, null, ct);
                            }
                        }
                        else
                        {
                            CreateNewDirectory(temporary);
                            ownsTemporary = true;
                            WriteSources(sources, null, temporary, ct);
                        }
                    }
                }

                // Move only after the whole output is closed. Neither operation replaces a
                // destination that appeared after the initial existence check.
                ct.ThrowIfCancellationRequested();
                if (compress) File.Move(temporary, destination);
                else Directory.Move(temporary, destination);
                ownsTemporary = false;
                return count;
            }
            finally
            {
                if (ownsTemporary)
                {
                    try
                    {
                        if (compress) File.Delete(temporary);
                        else Directory.Delete(temporary, true);
                    }
                    catch (IOException) { } // Do not mask the conversion error.
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        // ZipArchive materializes the central directory before exposing Entries.Count.
        // Bound the actual record count/size first, not just the advertised EOCD count.
        // The limits fit ZIP32; split archives and ZIP64 end records aren't needed here.
        private static uint[] ReadDirectory(FileStream file, CancellationToken ct)
        {
            const int endHeaderSize = 22;
            const int allowedFlags = 0x080e; // Deflate options, data descriptor and UTF-8 names.
            const uint rejectedAttributes = 0x418; // Windows reparse point, directory or volume label.
            if (file.Length < endHeaderSize || file.Length > MaxArchiveBytes)
                throw new InvalidDataException("Input ZIP is missing its end record or exceeds the archive size limit.");

            byte[] tail = new byte[(int)Math.Min(file.Length, endHeaderSize + UInt16.MaxValue)];
            file.Position = file.Length - tail.Length;
            ReadFully(file, tail);
            int end = tail.Length - 4;
            while (end >= 0 && ReadUInt32(tail, end) != 0x06054b50) end--;
            if (end < 0 || end > tail.Length - endHeaderSize || end + endHeaderSize + ReadUInt16(tail, end + 20) != tail.Length)
                throw new InvalidDataException("Invalid ZIP end record.");

            int count = ReadUInt16(tail, end + 10);
            long bytes = ReadUInt32(tail, end + 12);
            long start = ReadUInt32(tail, end + 16);
            long limit = file.Length - tail.Length + end;
            if (ReadUInt16(tail, end + 4) != 0 || ReadUInt16(tail, end + 6) != 0 ||
                count != ReadUInt16(tail, end + 8) || count < 1 || count > MaxEntries ||
                bytes > MaxDirectoryBytes || start + bytes != limit)
                throw new InvalidDataException("Unsupported ZIP layout, empty archive, or ZIP metadata limit exceeded.");

            var crcs = new uint[count];
            var record = new byte[46];
            var localHeader = new byte[30];
            var nameBuffer = new byte[MaxNameBytes];
            var localNameBuffer = new byte[MaxNameBytes];
            var locals = new List<KeyValuePair<long, long>>(count);
            long compressedTotal = 0;
            file.Position = start;
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (limit - file.Position < record.Length)
                    throw new InvalidDataException("Truncated ZIP central directory.");
                ReadFully(file, record);
                int nameBytes = ReadUInt16(record, 28);
                int variableBytes = nameBytes + ReadUInt16(record, 30) + ReadUInt16(record, 32);
                int flags = ReadUInt16(record, 8);
                int method = ReadUInt16(record, 10);
                uint attributes = ReadUInt32(record, 38);
                uint unixType = (attributes >> 16) & 0xf000;
                long compressedLength = ReadUInt32(record, 20);
                long localOffset = ReadUInt32(record, 42);
                compressedTotal += compressedLength;
                if (ReadUInt32(record, 0) != 0x02014b50 || nameBytes == 0 || nameBytes > MaxNameBytes ||
                    variableBytes > limit - file.Position || ReadUInt16(record, 34) != 0 ||
                    (flags & ~allowedFlags) != 0 || (method != 0 && method != 8) ||
                    (attributes & rejectedAttributes) != 0 || (unixType != 0 && unixType != 0x8000) ||
                    ReadUInt32(record, 24) > MaxSourceBytes || compressedTotal > MaxArchiveBytes ||
                    compressedLength > start - localOffset - 30)
                    throw new InvalidDataException("Invalid, unsupported, or oversized ZIP entry at index " + (i + 1) + ".");
                long next = file.Position + variableBytes;
                ReadFully(file, nameBuffer, nameBytes);
                file.Position = localOffset;
                ReadFully(file, localHeader);
                long dataStart = localOffset + localHeader.Length + nameBytes + ReadUInt16(localHeader, 28);
                if (ReadUInt32(localHeader, 0) != 0x04034b50 || ReadUInt16(localHeader, 26) != nameBytes ||
                    ReadUInt16(localHeader, 6) != flags || ReadUInt16(localHeader, 8) != method ||
                    compressedLength > start - dataStart)
                    throw new InvalidDataException("Invalid ZIP local header at index " + (i + 1) + ".");
                ReadFully(file, localNameBuffer, nameBytes);
                for (int j = 0; j < nameBytes; j++)
                {
                    if (nameBuffer[j] != localNameBuffer[j] || nameBuffer[j] < 0x20 || nameBuffer[j] >= 0x7f)
                        throw new InvalidDataException("Invalid or inconsistent ZIP entry name at index " + (i + 1) + ".");
                }
                // Check grammar before passing even ASCII names to Framework's path helpers.
                if (!SourceName.IsMatch(Encoding.ASCII.GetString(nameBuffer, 0, nameBytes)))
                    throw new InvalidDataException("Unrecognized entry at index " + (i + 1) + ": expected a flat contest C/C++ source name.");
                locals.Add(new KeyValuePair<long, long>(localOffset, dataStart + compressedLength));
                crcs[i] = ReadUInt32(record, 16);
                file.Position = next;
            }
            if (file.Position != limit) throw new InvalidDataException("ZIP central directory count does not match its records.");
            locals.Sort((left, right) => left.Key.CompareTo(right.Key));
            for (int i = 1; i < locals.Count; i++)
                if (locals[i].Key < locals[i - 1].Value) throw new InvalidDataException("Overlapping ZIP entries.");
            file.Position = 0;
            return crcs;
        }

        private static List<Source> Plan(ZipArchive zip, uint[] crcs, IDictionary<string, string> users,
            IDictionary<string, string> problems, CancellationToken ct)
        {
            var entries = zip.Entries;
            if (entries.Count != crcs.Length) throw new InvalidDataException("ZIP entry count mismatch.");
            var result = new List<Source>(entries.Count);
            var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var userOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var problemOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                ZipArchiveEntry entry = entries[i];
                Match match = SourceName.Match(entry.FullName);
                if (!match.Success)
                    throw new InvalidDataException("Unrecognized entry at index " + (i + 1) + ": expected a flat contest C/C++ source name.");
                if (entry.Length < 0 || entry.Length > MaxSourceBytes || entry.Length > MaxTotalSourceBytes - total)
                    throw new InvalidDataException("Source size limit exceeded: " + entry.FullName);
                total += entry.Length;
                string user = Resolve(users, userOwners, match.Groups["uid"].Value, "UID", 255);
                string problem = Resolve(problems, problemOwners, match.Groups["pid"].Value, "PID", 251);
                string path = user + "/" + problem + "/" + problem + ".cpp";
                if (!destinations.Add(path)) throw new InvalidDataException("Multiple submissions map to " + path + ".");
                result.Add(new Source { Entry = entry, User = user, Problem = problem, RelativePath = path, Crc = crcs[i] });
            }
            return result;
        }

        private static string Resolve(IDictionary<string, string> names, Dictionary<string, string> owners,
            string id, string kind, int maxLength)
        {
            string name;
            if (!names.TryGetValue(id, out name)) throw new InvalidDataException("Unmapped " + kind + ": " + id + ".");
            ValidateComponent(name, maxLength, "mapped name for " + kind + " " + id, false);
            string owner;
            if (owners.TryGetValue(name, out owner) && owner != id)
                throw new InvalidDataException(kind + " " + owner + " and " + id + " map to the same name: " + name + ".");
            owners[name] = id;
            return name;
        }

        private static void ValidateComponent(string name, int maxLength, string label, bool allowTilde)
        {
            if (String.IsNullOrEmpty(name) || name.Length > maxLength || name.EndsWith(".", StringComparison.Ordinal) ||
                name.EndsWith(" ", StringComparison.Ordinal) || DeviceName.IsMatch(name.Split('.')[0].TrimEnd(' ')))
                throw new InvalidDataException("Unsafe Windows " + label + ".");
            foreach (char ch in name)
            {
                // A mapped name must not name an auto-generated NTFS short alias.
                if (Char.IsControl(ch) || "<>:\"/\\|?*".IndexOf(ch) >= 0 || (!allowTilde && ch == '~'))
                    throw new InvalidDataException("Unsafe Windows " + label + ".");
            }
            try { StrictUtf8.GetByteCount(name); }
            catch (EncoderFallbackException ex) { throw new InvalidDataException("Invalid Unicode in " + label + ".", ex); }
        }

        private static void WriteSources(List<Source> sources, ZipArchive zip, string directory, CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            var createdUsers = new HashSet<string>(StringComparer.Ordinal);
            long total = 0;
            foreach (Source source in sources)
            {
                ct.ThrowIfCancellationRequested();
                using (Stream from = source.Entry.Open())
                {
                    Stream to;
                    if (zip != null)
                        to = zip.CreateEntry(source.RelativePath, CompressionLevel.Optimal).Open();
                    else
                    {
                        string userPath = Path.Combine(directory, source.User);
                        if (createdUsers.Add(source.User)) CreateNewDirectory(userPath);
                        string problemPath = Path.Combine(userPath, source.Problem);
                        CreateNewDirectory(problemPath);
                        to = new FileStream(Path.Combine(problemPath, source.Problem + ".cpp"),
                            FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    }
                    using (to)
                    {
                        long length = source.Entry.Length;
                        long copied = 0;
                        uint crc = UInt32.MaxValue;
                        for (;;)
                        {
                            ct.ThrowIfCancellationRequested();
                            int read = from.Read(buffer, 0, (int)Math.Min(buffer.Length, length - copied + 1));
                            if (read == 0) break;
                            if (read > length - copied || read > MaxSourceBytes - copied || read > MaxTotalSourceBytes - total)
                                throw new InvalidDataException("ZIP entry exceeds its declared size or the source size limit: " + source.Entry.FullName);
                            for (int j = 0; j < read; j++) crc = CrcTable[(crc ^ buffer[j]) & 255] ^ (crc >> 8);
                            to.Write(buffer, 0, read);
                            copied += read;
                            total += read;
                        }
                        if (copied != length || (crc ^ UInt32.MaxValue) != source.Crc)
                            throw new InvalidDataException("ZIP source has an invalid size or CRC: " + source.Entry.FullName);
                    }
                }
            }
        }

        // Directory.CreateDirectory succeeds for existing directories (including junctions).
        // Use an exclusive create so we never claim an existing path as ours, and Windows
        // also catches filesystem aliases that ordinal comparison might not detect.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);

        private static void CreateNewDirectory(string path)
        {
            if (!CreateDirectoryW(path, IntPtr.Zero))
                throw new IOException("Cannot create a new directory: " + path, new Win32Exception(Marshal.GetLastWin32Error()));
        }

        private static void ReadFully(Stream file, byte[] buffer)
        {
            ReadFully(file, buffer, buffer.Length);
        }

        private static void ReadFully(Stream file, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = file.Read(buffer, offset, count - offset);
                if (read == 0) throw new InvalidDataException("Truncated ZIP metadata.");
                offset += read;
            }
        }

        private static int ReadUInt16(byte[] buffer, int offset)
        {
            return buffer[offset] | buffer[offset + 1] << 8;
        }

        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            return (uint)ReadUInt16(buffer, offset) | (uint)ReadUInt16(buffer, offset + 2) << 16;
        }

        private static uint[] MakeCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) != 0 ? 0xedb88320u : 0u);
                table[i] = value;
            }
            return table;
        }

        internal static void SelfTest()
        {
            string scratch = Path.Combine(Path.GetTempPath(), "yundou-answer-test-" + Guid.NewGuid().ToString("N"));
            CreateNewDirectory(scratch);
            try
            {
                const string record = "_R0123456789abcdefABCDEF01_S2@35";
                string cc = "U-9_P114" + record + ".cc.ccnoio2";
                string c = "U12_P115" + record + ".c.ccnoio2";
                string input = Path.Combine(scratch, "input.zip");
                string folder = Path.Combine(scratch, "result");
                string archive = Path.Combine(scratch, "result.zip");
                string failed = Path.Combine(scratch, "failed");
                var users = new Dictionary<string, string> { { "-9", "TJ-S12345" }, { "12", "TJ-S12346" } };
                var problems = new Dictionary<string, string> { { "114", "crystal" }, { "115", "subway" } };
                byte[] payload = { 239, 187, 191, 47, 47, 13, 10, 0, 128, 255 };
                MakeTestZip(input, payload, cc, c);
                Assert(Convert(input, folder, users, problems, false, CancellationToken.None) == 2, "directory count");
                Assert(Convert(input, archive, users, problems, true, CancellationToken.None) == 2, "ZIP count");
                string ccPath = "TJ-S12345/crystal/crystal.cpp";
                string cPath = "TJ-S12346/subway/subway.cpp";
                Assert(Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Length == 2, "directory only contains source files");
                CheckBytes(File.ReadAllBytes(Path.Combine(folder, ccPath.Replace('/', Path.DirectorySeparatorChar))), payload);
                CheckBytes(File.ReadAllBytes(Path.Combine(folder, cPath.Replace('/', Path.DirectorySeparatorChar))), payload);
                using (var file = File.OpenRead(archive))
                using (var zip = new ZipArchive(file, ZipArchiveMode.Read))
                {
                    Assert(zip.Entries.Count == 2, "ZIP only contains source files");
                    foreach (string path in new[] { ccPath, cPath })
                    {
                        ZipArchiveEntry entry = zip.GetEntry(path);
                        Assert(entry != null, "ZIP root layout");
                        using (Stream stream = entry.Open())
                        using (var data = new MemoryStream())
                        {
                            stream.CopyTo(data);
                            CheckBytes(data.ToArray(), payload);
                        }
                    }
                }

                Expect<IOException>(delegate { Convert(input, folder, users, problems, true, CancellationToken.None); });
                Expect<IOException>(delegate { Convert(input, archive, users, problems, false, CancellationToken.None); });
                Expect<DirectoryNotFoundException>(delegate
                {
                    Convert(input, Path.Combine(failed, "missing-parent"), users, problems, false, CancellationToken.None);
                });
                Expect<OperationCanceledException>(delegate { Convert(input, failed, users, problems, true, new CancellationToken(true)); });

                CheckRejected(input, failed, new Dictionary<string, string>(), problems, false);
                CheckRejected(input, failed, users, new Dictionary<string, string>(), true);
                foreach (string unsafeName in new[] { "..", "../escape", "a\\b", "dd:stream", "NUL.txt", "COM¹", "trailing.", "trailing ", "SHORT~1" })
                {
                    users["-9"] = unsafeName;
                    CheckRejected(input, failed, users, problems, false);
                }
                users["-9"] = "tj-s12346";
                CheckRejected(input, failed, users, problems, true);
                users["-9"] = "TJ-S12345";
                problems["115"] = "CRYSTAL";
                CheckRejected(input, failed, users, problems, true);
                problems["115"] = "subway";

                foreach (string badName in new[] { "../" + cc, "dir\\" + cc, "/" + cc, cc + "/", "readme.txt", "U-9_P114" + record + ".exe.ccnoio2" })
                {
                    MakeTestZip(input, payload, cc, badName);
                    CheckRejected(input, failed, users, problems, true);
                }
                MakeTestZip(input, payload, cc, "U-9_P114" + record + ".cpp.ccnoio2");
                CheckRejected(input, failed, users, problems, false);
                MakeTestZip(input, payload, cc);
                byte[] original = File.ReadAllBytes(input);
                int end = original.Length - 22;
                int central = (int)ReadUInt32(original, end + 16);
                MutateTestZip(input, original, end + 10, 0); // EOCD entry count mismatch
                CheckRejected(input, failed, users, problems, true);
                MutateTestZip(input, original, (int)ReadUInt32(original, central + 42) + 30, (byte)'/'); // local name differs
                CheckRejected(input, failed, users, problems, true);
                MutateTestZip(input, original, central + 27, 2); // declared size > 16 MiB
                CheckRejected(input, failed, users, problems, true);
                MutateTestZip(input, original, central + 24, 1); // actual bytes exceed the declared size
                CheckRejected(input, failed, users, problems, false);
                MutateTestZip(input, original, central + 24, 100); // truncated source relative to declared size
                CheckRejected(input, failed, users, problems, true);
                MutateTestZip(input, original, central + 16, (byte)(original[central + 16] ^ 1)); // wrong CRC
                CheckRejected(input, failed, users, problems, true);
                MutateTestZip(input, original, central + 41, 0xa0); // Unix symlink dressed as source
                CheckRejected(input, failed, users, problems, false);
                Assert(!File.Exists(failed) && !Directory.Exists(failed), "no output after cancellation or failure");
            }
            finally { Directory.Delete(scratch, true); }
        }

        private static void MakeTestZip(string path, byte[] payload, params string[] names)
        {
            using (var file = File.Create(path))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
                foreach (string name in names)
                    using (Stream stream = zip.CreateEntry(name).Open()) stream.Write(payload, 0, payload.Length);
        }

        private static void MutateTestZip(string path, byte[] original, int index, byte replacement)
        {
            byte[] bytes = (byte[])original.Clone();
            bytes[index] = replacement;
            File.WriteAllBytes(path, bytes);
        }

        private static void CheckRejected(string input, string output, IDictionary<string, string> users,
            IDictionary<string, string> problems, bool compress)
        {
            Expect<InvalidDataException>(delegate { Convert(input, output, users, problems, compress, CancellationToken.None); });
            Assert(!File.Exists(output) && !Directory.Exists(output), "no partial result");
            Assert(Directory.GetFileSystemEntries(Path.GetDirectoryName(output), TemporaryPrefix + "*").Length == 0,
                "temporary output was cleaned up");
        }

        private static void CheckBytes(byte[] actual, byte[] expected)
        {
            Assert(actual.Length == expected.Length, "source length preserved");
            for (int i = 0; i < expected.Length; i++) Assert(actual[i] == expected[i], "source bytes preserved");
        }

        private static void Expect<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException("Answer archive self-test expected " + typeof(T).Name + ".");
        }

        private static void Assert(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("Answer archive self-test failed: " + description + ".");
        }
    }
}
