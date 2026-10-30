// Server-only optional MySQL/MariaDB persistence. Protocol v1 and driver files are unchanged.
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace UDA
{
    public sealed class RetiredDriverDownload
    {
        public string Path = "", Package = "", Hash = "", Archive = "", ExpiresUtc = "";
    }
    public sealed class ServerStorageConfig
    {
        public int Version = 1;
        public bool Enabled;
        public bool DatabaseOnly;
        public string RepositoryId = Guid.NewGuid().ToString("N"), ProtectedConnection = "";
    }
    public sealed class StoragePending { public string Key, Text; public bool Immutable, Deleted; }
    public sealed class StorageLogBlock { public string Key, Kind, FileName, Source, Data; public long Offset; }
    public sealed class StorageLogCursor { public long Length, Modified, Created; public string Generation; }
    public sealed class MachineDriverPackage
    {
        public string Manufacturer = "", Model = "", SourceName = "", PackagePrefix = "", Architecture = "amd64";
        public int MajorVersion = 10, MinorVersion, MinBuild, MaxBuild;
    }
    public static class MachineDriverPackages
    {
        private static string Normal(string text) { return System.Text.RegularExpressions.Regex.Replace((text ?? "").Trim(), @"\s+", " ").ToUpperInvariant(); }
        public static List<MachineDriverPackage> Load(string root)
        { string text = ServerStorage.Read(root, "repo.machine-packages.json"); return text == null ? new List<MachineDriverPackage>() : new JavaScriptSerializer().Deserialize<List<MachineDriverPackage>>(text); }
        public static bool SameSystemMapping(MachineDriverPackage a, MachineDriverPackage b)
        { return Normal(a.Manufacturer) == Normal(b.Manufacturer) && Normal(a.Model) == Normal(b.Model) && string.Equals(a.Architecture, b.Architecture, StringComparison.OrdinalIgnoreCase) && a.MajorVersion == b.MajorVersion && a.MinorVersion == b.MinorVersion && (a.MajorVersion != 10 || (a.MinBuild >= 22000) == (b.MinBuild >= 22000)); }
        public static bool Contains(OfflineRepoEntry entry, MachineDriverPackage policy)
        { return entry != null && entry.SourceName == policy.SourceName && (string.IsNullOrEmpty(policy.PackagePrefix) || entry.RelativePath.StartsWith(policy.PackagePrefix.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)); }
        public static void Observe(string root, LanReportRequest report, OsTarget os)
        {
            if (!ServerStorage.Config(root).Enabled || string.IsNullOrWhiteSpace(report.MachineManufacturer) || string.IsNullOrWhiteSpace(report.MachineModel) || report.MachineManufacturer.Length > 256 || report.MachineModel.Length > 256) return;
            string key; using (var sha = SHA256.Create()) key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Normal(report.MachineManufacturer) + "\n" + Normal(report.MachineModel)))).Replace("-", "").ToLowerInvariant();
            ServerStorage.Write(root, "repo.machine-observed." + key + ".json", new JavaScriptSerializer().Serialize(new { Manufacturer = report.MachineManufacturer, Model = report.MachineModel, Architecture = (os.Architecture ?? "").ToLowerInvariant(), MajorVersion = os.MajorVersion, MinorVersion = os.EffectiveMinorVersion(), BuildNumber = os.BuildNumber, Machine = report.MachineName, Utc = DateTime.UtcNow.ToString("o") }), false);
        }
        public static object[] Choices(OfflineRepoManifest manifest)
        {
            if (manifest == null) return new object[0];
            return manifest.Entries.SelectMany(e => {
                var choices = new List<string> { "" }; string path = (e.RelativePath ?? "").Replace('\\', '/');
                int index = path.Length > "packages/".Length ? path.IndexOf('/', "packages/".Length) : -1;
                while (index > 0) { choices.Add(path.Substring(0, index)); index = path.IndexOf('/', index + 1); }
                return choices.Select(prefix => new { Source = e.SourceName, Prefix = prefix });
            }).GroupBy(x => x.Source + "\n" + x.Prefix).OrderBy(g => g.First().Source).ThenBy(g => g.First().Prefix).Select(g => (object)new { name = g.First().Source, prefix = g.First().Prefix, count = g.Count() }).ToArray();
        }
        public static MachineDriverPackage Select(IEnumerable<MachineDriverPackage> policies, string maker, string model, OsTarget os)
        {
            var machine = policies.Where(p => Normal(p.Manufacturer) == Normal(maker) && Normal(p.Model) == Normal(model)).ToList();
            if (machine.Count == 0) return null;
            if (os.MajorVersion == 10 && os.BuildNumber <= 0 && machine.Any(p => p.MajorVersion == 10 && (p.MinBuild > 0 || p.MaxBuild > 0))) return null;
            var matches = machine.Where(p => string.Equals(p.Architecture, os.Architecture, StringComparison.OrdinalIgnoreCase) && p.MajorVersion == os.MajorVersion && p.MinorVersion == os.EffectiveMinorVersion() && os.BuildNumber >= p.MinBuild && (p.MaxBuild == 0 || os.BuildNumber <= p.MaxBuild)).ToList();
            if (matches.Count == 0) return null;
            if (matches.Count != 1) throw new InvalidOperationException("机型已配置专用驱动包，但目标系统没有唯一适用配置，禁止回退通用驱动：" + model);
            return matches[0];
        }
        public static void Save(string root, List<MachineDriverPackage> policies, IProgress<string> progress = null)
        {
            if (policies == null || policies.Count > 10000) throw new ArgumentException("Invalid machine mappings");
            var manifest = OfflineRepoGenerator.TryLoadManifest(root); if (manifest == null) throw new InvalidOperationException("仓库尚未就绪");
            foreach (var p in policies)
            {
                if (p == null || (p.MajorVersion != 6 && p.MajorVersion != 10) || (p.MajorVersion == 10 && p.MinorVersion != 0) || (p.MajorVersion == 6 && (p.MinorVersion < 1 || p.MinorVersion > 3))) throw new ArgumentException("不支持的目标系统版本");
                p.PackagePrefix = (p.PackagePrefix ?? "").Replace('\\', '/').TrimEnd('/');
                if (string.IsNullOrWhiteSpace(p.Manufacturer) || string.IsNullOrWhiteSpace(p.Model) || p.Manufacturer.Length > 256 || p.Model.Length > 256 || !new[] { "amd64", "x86", "arm64" }.Contains(p.Architecture) || p.MinBuild < 0 || p.MaxBuild < 0 || (p.MaxBuild > 0 && p.MaxBuild < p.MinBuild) || !manifest.Entries.Any(e => Contains(e, p)) || (p.PackagePrefix.Length > 0 && (!p.PackagePrefix.StartsWith("packages/", StringComparison.OrdinalIgnoreCase) || OfflineRepoGenerator.SafeAbsolute(root, p.PackagePrefix) == null || p.PackagePrefix.Split('/').Any(s => s == ".." || s == ".")))) throw new ArgumentException("机型、系统或仓库整包配置无效");
                if (policies.Count(q => Normal(q.Manufacturer) == Normal(p.Manufacturer) && Normal(q.Model) == Normal(p.Model) && q.Architecture == p.Architecture && q.MajorVersion == p.MajorVersion && q.MinorVersion == p.MinorVersion && q.MinBuild <= (p.MaxBuild == 0 ? int.MaxValue : p.MaxBuild) && p.MinBuild <= (q.MaxBuild == 0 ? int.MaxValue : q.MaxBuild)) > 1) throw new ArgumentException("同一机型的系统范围不能重叠");
            }
            foreach (var p in policies.GroupBy(p => p.SourceName + "\n" + p.PackagePrefix).Select(g => g.First())) Archive(root, p.SourceName, p.PackagePrefix, true, progress);
            ServerStorage.Write(root, "repo.machine-packages.json", new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(policies), false);
            System.Threading.Interlocked.Increment(ref _revision);
        }
        private static long _revision;
        public static long Revision { get { return System.Threading.Interlocked.Read(ref _revision); } }
        private static readonly ConcurrentDictionary<string, object> ArchiveGates = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        internal static void ReleaseWorkspaceGates(string root)
        {
            string prefix = Path.GetFullPath(root) + "|";
            foreach (string key in ArchiveGates.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) { object ignored; ArchiveGates.TryRemove(key, out ignored); }
        }
        public static string Archive(string root, string source, string packagePrefix = "", bool create = true, IProgress<string> progress = null)
        {
            var manifest = OfflineRepoGenerator.TryLoadManifest(root); if (manifest == null) throw new InvalidOperationException("仓库尚未就绪");
            var scoped = manifest.Entries.Where(e => Contains(e, new MachineDriverPackage { SourceName = source, PackagePrefix = packagePrefix })).ToList();
            string scope = source + "|" + packagePrefix + "|" + string.Join("\n", scoped.Select(e => e.RelativePath + "|" + e.ContentHash + "|" + e.PackageRoot + "|" + e.PackageHash).OrderBy(x => x, StringComparer.Ordinal));
            string identity, legacy;
            using (var sha = SHA256.Create()) {
                identity = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes("model-scope-v2|" + scope))).Replace("-", "").ToLowerInvariant();
                legacy = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(source + "|" + packagePrefix + "|" + manifest.IntegrityHash))).Replace("-", "").ToLowerInvariant();
            }
            lock (ArchiveGates.GetOrAdd(Path.GetFullPath(root) + "|" + identity, key => new object()))
            {
                string aliasText = ServerStorage.Read(root, "repo.model-archive-identities.json");
                var aliases = aliasText == null ? new Dictionary<string, string>() : new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, string>>(aliasText) ?? new Dictionary<string, string>();
                string oldIdentity;
                if (aliases.TryGetValue(identity, out oldIdentity) && System.Text.RegularExpressions.Regex.IsMatch(oldIdentity, "^[a-f0-9]{64}$")) identity = oldIdentity;
                else if (File.Exists(Path.Combine(root, ".uda-downloads", "model-" + legacy + ".zip"))) {
                    aliases[identity] = legacy;
                    ServerStorage.Write(root, "repo.model-archive-identities.json", new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(aliases), false);
                    identity = legacy;
                }
                string destination = ServerStorage.ResolveOnlineArchive(root, "model-" + identity + ".zip");
                if (!create) return destination;
                if (File.Exists(destination)) return destination;
                var entries = manifest.Entries.Where(e => Contains(e, new MachineDriverPackage { SourceName = source, PackagePrefix = packagePrefix })).ToList(); if (entries.Count == 0) throw new InvalidDataException("专用机型整包已不存在，禁止通用回退");
                string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite))
                    using (var zip = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create))
                        foreach (var entry in entries.GroupBy(e => OfflineRepoGenerator.DownloadArchivePath(root, e)).Select(g => g.First()))
                        using (var input = System.IO.Compression.ZipFile.OpenRead(OfflineRepoGenerator.DownloadArchivePath(root, entry)))
                        {
                            int copied = 0, total = input.Entries.Count(e => !e.FullName.EndsWith("/"));
                            foreach (var member in input.Entries.Where(e => !e.FullName.EndsWith("/")))
                            {
                                if (progress != null) progress.Report("机型整包压缩文件 " + (++copied) + "/" + total + "：" + Path.GetFileName(member.FullName));
                                string parent = string.IsNullOrEmpty(entry.PackageRoot) ? Path.GetDirectoryName(entry.RelativePath).Replace('\\', '/') : entry.PackageRoot;
                                string name = parent.TrimEnd('/') + "/" + member.FullName.Replace('\\', '/');
                                if (OfflineRepoGenerator.SafeAbsolute(root, name) == null) throw new InvalidDataException("Invalid model package member");
                                string hash; using (var sha = SHA256.Create()) using (var stream = member.Open()) hash = BitConverter.ToString(sha.ComputeHash(stream));
                                string previous; if (seen.TryGetValue(name, out previous)) { if (previous != hash) throw new InvalidDataException("机型包中存在同路径不同内容的文件"); continue; }
                                seen[name] = hash;
                                using (var stream = member.Open()) using (var target = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Fastest).Open()) stream.CopyTo(target);
                            }
                        }
                    File.Move(temporary, destination); return destination;
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
    }
    public sealed class ArchiveVerificationRecord
    {
        public int Version = 1;
        public string FileState, EntriesHash, ArchiveHash, VerifiedUtc;
    }
    public static class DatabaseArchiveRepository
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct FileBasicInfo { public long Creation, Access, Write, Change; public uint Attributes; }
        [StructLayout(LayoutKind.Sequential)]
        private struct FileIdInfo { public ulong Volume; public Guid Id; }
        [StructLayout(LayoutKind.Sequential)]
        private struct LegacyFileInfo
        {
            public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            public uint Volume, SizeHigh, SizeLow, Links, IdHigh, IdLow;
        }
        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetFileInformationByHandleEx")]
        private static extern bool GetBasicInfo(SafeFileHandle handle, int kind, out FileBasicInfo info, uint size);
        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetFileInformationByHandleEx")]
        private static extern bool GetFileId(SafeFileHandle handle, int kind, out FileIdInfo info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out LegacyFileInfo info);
        // Metadata-only preflight. Unknown filesystems/API failures must never produce a cache hit.
        internal static string ArchiveFileState(FileStream stream)
        {
            try
            {
                FileBasicInfo basic; FileIdInfo id;
                if (!GetBasicInfo(stream.SafeFileHandle, 0, out basic, (uint)Marshal.SizeOf(typeof(FileBasicInfo))) || basic.Change <= 0 || (basic.Attributes & (uint)FileAttributes.ReparsePoint) != 0) return null;
                string identity;
                if (GetFileId(stream.SafeFileHandle, 18, out id, (uint)Marshal.SizeOf(typeof(FileIdInfo))) && id.Id != Guid.Empty) identity = id.Volume + ":" + id.Id.ToString("N");
                else
                {
                    if (!new DriveInfo(Path.GetPathRoot(stream.Name)).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) return null;
                    LegacyFileInfo legacy; if (!GetFileInformationByHandle(stream.SafeFileHandle, out legacy) || legacy.IdHigh == 0 && legacy.IdLow == 0) return null;
                    identity = legacy.Volume + ":" + legacy.IdHigh + ":" + legacy.IdLow;
                }
                return identity + "|" + stream.Length + "|" + basic.Creation + "|" + basic.Write + "|" + basic.Change + "|" + basic.Attributes;
            }
            catch (EntryPointNotFoundException) { return null; }
            catch (DllNotFoundException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
        }
        private static bool IsVerifiedRecord(ArchiveVerificationRecord record)
        {
            DateTime verified;
            return record != null && record.Version == 1 && !string.IsNullOrEmpty(record.FileState) && System.Text.RegularExpressions.Regex.IsMatch(record.ArchiveHash ?? "", @"^[A-Fa-f0-9]{64}$") && DateTime.TryParse(record.VerifiedUtc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out verified);
        }
        private static string EntriesHash(IEnumerable<OfflineRepoEntry> entries)
        {
            string text = string.Join("\n", entries.Select(e => e.RelativePath + "|" + e.ContentHash + "|" + e.PackageRoot + "|" + e.PackageHash).OrderBy(x => x, StringComparer.Ordinal));
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes("archive-verifier-v1\n" + text))).Replace("-", "");
        }
        private static string MemberPath(string root, OfflineRepoEntry entry, string name)
        {
            name = name.Replace('\\', '/');
            if (name.StartsWith("/") || name.Split('/').Any(p => p == ".." || p == ".") || name.Contains(":")) throw new InvalidDataException("Invalid archive member");
            string parent = string.IsNullOrEmpty(entry.PackageRoot) ? Path.GetDirectoryName(entry.RelativePath).Replace('\\', '/') : entry.PackageRoot;
            string relative = parent.TrimEnd('/') + "/" + name;
            string path = OfflineRepoGenerator.SafeAbsolute(root, relative);
            string packages = Path.GetFullPath(Path.Combine(root, "packages")).TrimEnd('\\') + "\\";
            if (path == null || !path.StartsWith(packages, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("驱动归档越出仓库 packages 目录");
            return path;
        }
        public static void ExpandForMaintenance(string root, IProgress<string> progress = null)
        {
            if (!ServerStorage.Config(root).DatabaseOnly) return;
            foreach (string name in new[] { "packages", ".uda-downloads" }) if (Directory.Exists(Path.Combine(root, name))) OrdinaryTree(Path.Combine(root, name));
            var manifest = OfflineRepoGenerator.TryLoadManifest(root); if (manifest == null) throw new InvalidOperationException("MySQL 仓库未就绪");
            string hashText = ServerStorage.Read(root, "repo.archive-hashes.json");
            if (hashText == null) throw new InvalidDataException("数据库缺少归档校验清单，禁止展开维护");
            var hashes = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, string>>(hashText);
            var uniqueArchives = manifest.Entries.GroupBy(e => OfflineRepoGenerator.DownloadArchivePath(root, e), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            int checkedArchives = 0;
            System.Threading.Tasks.Parallel.ForEach(uniqueArchives, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = ClientScanParallelism.LogicalProcessors() }, entry => {
                string path = OfflineRepoGenerator.DownloadArchivePath(root, entry), expected;
                if (!hashes.TryGetValue(Path.GetFileName(path), out expected) || !File.Exists(path) || !string.Equals(expected, OfflineRepoGenerator.Sha256File(path), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("驱动归档与数据库校验清单不一致，禁止展开：" + entry.RelativePath);
                int done = System.Threading.Interlocked.Increment(ref checkedArchives);
                if (progress != null) progress.Report("核对维护归档 " + done + "/" + uniqueArchives.Count);
            });
            int expanded = 0;
            foreach (var entry in uniqueArchives)
            {
            if (progress != null) progress.Report("恢复维护归档 " + (++expanded) + "/" + uniqueArchives.Count);
            using (var archive = System.IO.Compression.ZipFile.OpenRead(OfflineRepoGenerator.DownloadArchivePath(root, entry)))
                foreach (var member in archive.Entries.Where(e => !e.FullName.EndsWith("/")))
                {
                    string file = MemberPath(root, entry, member.FullName);
                    if (File.Exists(file)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    using (var input = member.Open()) using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write)) input.CopyTo(output);
                }
            }
            File.WriteAllText(Path.Combine(root, "repo.manifest.json"), OfflineRepoGenerator.SerializeManifest(manifest), new UTF8Encoding(false));
            string pathAliases = ServerStorage.Read(root, "repo.path-aliases.json");
            if (pathAliases != null) File.WriteAllText(Path.Combine(root, "repo.path-aliases.json"), pathAliases, new UTF8Encoding(false));
            if (!File.Exists(Path.Combine(root, "repo.udadb")))
            {
                string stateText = ServerStorage.Read(root, "repo.index-state.json");
                if (stateText != null)
                {
                    var state = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(stateText);
                    string prefix = state.ContainsKey("prefix") ? Convert.ToString(state["prefix"]) : "repo.index-block.";
                    using (var output = File.Create(Path.Combine(root, "repo.udadb"))) for (int i = 0; i < Convert.ToInt32(state["blocks"]); i++)
                    { var block = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(ServerStorage.Read(root, prefix + i.ToString("D12") + ".json")); byte[] data = Convert.FromBase64String(block["data"]); output.Write(data, 0, data.Length); }
                    if (!string.Equals(OfflineRepoGenerator.Sha256File(Path.Combine(root, "repo.udadb")), Convert.ToString(state["sha256"]), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("数据库匹配索引校验失败");
                }
            }
        }
        private static void OrdinaryTree(string directory)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("清理范围包含重解析点，已停止");
            foreach (string item in Directory.GetFileSystemEntries(directory))
            {
                if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw new IOException("清理范围包含重解析点，已停止");
                if (Directory.Exists(item)) OrdinaryTree(item);
            }
        }
        public static long Compact(string root, IProgress<string> progress = null)
        {
            ServerStorage.RequireMigratedDatabase(root);
            root = Path.GetFullPath(root);
            foreach (string name in new[] { "packages", ".uda-downloads" }) if (Directory.Exists(Path.Combine(root, name))) OrdinaryTree(Path.Combine(root, name));
            OfflineRepoGenerator.RecoverOrganization(root);
            using (var repository = OfflineRepo.Open(root, null)) if (repository.BackfilledModels > 0) repository.PersistUpgradedIndex(null);
            var manifest = OfflineRepoGenerator.TryLoadManifest(root); if (manifest == null) throw new InvalidDataException("仓库未就绪");
            OfflineRepoGenerator.PrepareDownloadPackages(root, progress);
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var previous = new Dictionary<string, ArchiveVerificationRecord>(StringComparer.OrdinalIgnoreCase);
            var previousHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string previousText = ServerStorage.Read(root, "repo.archive-verification.json"), hashText = ServerStorage.Read(root, "repo.archive-hashes.json");
            // A damaged optimization record is disposable: rebuild it through complete verification.
            try { if (previousText != null) previous = json.Deserialize<Dictionary<string, ArchiveVerificationRecord>>(previousText) ?? previous; } catch (ArgumentException) { } catch (InvalidOperationException) { }
            try { if (hashText != null) previousHashes = json.Deserialize<Dictionary<string, string>>(hashText) ?? previousHashes; } catch (ArgumentException) { } catch (InvalidOperationException) { }
            var archiveHashes = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var verifiedRecords = new ConcurrentDictionary<string, ArchiveVerificationRecord>(StringComparer.OrdinalIgnoreCase);
            var groups = manifest.Entries.GroupBy(e => OfflineRepoGenerator.DownloadArchivePath(root, e), StringComparer.OrdinalIgnoreCase).ToList();
            int verified = 0, skipped = 0, total = groups.Count; var progressGate = new object();
            if (progress != null) progress.Report("正在检查归档既有核验记录，共 " + total + " 个独立归档…");
            System.Threading.Tasks.Parallel.ForEach(groups, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = ClientScanParallelism.LogicalProcessors() }, group =>
            {
                string archivePath = group.Key, name = Path.GetFileName(archivePath), entriesHash = EntriesHash(group); bool reused = false;
                bool groupRawFiles = group.Any(e => { string relative = string.IsNullOrEmpty(e.PackageRoot) ? Path.GetDirectoryName(e.RelativePath) : e.PackageRoot; string path = OfflineRepoGenerator.SafeAbsolute(root, relative); return path != null && Directory.Exists(path); });
                // Hold a read handle during preflight/verification so writers cannot change the ZIP underneath us.
                using (var guard = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    string state = ArchiveFileState(guard), previousHash;
                    ArchiveVerificationRecord record;
                    bool cached = !groupRawFiles && state != null && previous.TryGetValue(name, out record) && record != null && record.Version == 1 && IsVerifiedRecord(record) && record.FileState == state && record.EntriesHash == entriesHash && !string.IsNullOrEmpty(record.ArchiveHash) && previousHashes.TryGetValue(name, out previousHash) && string.Equals(record.ArchiveHash, previousHash, StringComparison.OrdinalIgnoreCase);
                    if (cached)
                    {
                        record = previous[name]; verifiedRecords[name] = record; archiveHashes[name] = record.ArchiveHash;
                        reused = true;
                    }
                    else
                    {
                        var required = group.ToDictionary(e => OfflineRepoGenerator.SafeAbsolute(root, e.RelativePath), e => e.ContentHash, StringComparer.OrdinalIgnoreCase);
                        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        using (var archive = System.IO.Compression.ZipFile.OpenRead(archivePath))
                            foreach (var member in archive.Entries.Where(e => !e.FullName.EndsWith("/")))
                            {
                                string path = MemberPath(root, group.First(), member.FullName), hash;
                                using (var sha = SHA256.Create()) using (var input = member.Open()) hash = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
                                if (File.Exists(path) && !string.Equals(hash, OfflineRepoGenerator.Sha256File(path), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("归档与散装驱动内容不一致，拒绝清理：" + group.First().RelativePath);
                                string expected;
                                if (required.TryGetValue(path, out expected)) { found.Add(path); if (!string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("归档 INF 哈希不一致，拒绝清理"); }
                            }
                        if (found.Count != required.Count) throw new InvalidDataException("归档缺少 INF，拒绝清理：" + group.First().RelativePath);
                        string archiveHash = OfflineRepoGenerator.Sha256File(archivePath);
                        string publishedHash;
                        if (!groupRawFiles && previousHashes.TryGetValue(name, out publishedHash) && !string.Equals(publishedHash, archiveHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("归档与已入库指纹不一致，请恢复原包或重新导入：" + name);
                        if (state != ArchiveFileState(guard)) throw new IOException("核验期间归档状态发生变化，请重试");
                        archiveHashes[name] = archiveHash;
                        verifiedRecords[name] = new ArchiveVerificationRecord { FileState = state, EntriesHash = entriesHash, ArchiveHash = archiveHash, VerifiedUtc = DateTime.UtcNow.ToString("o") };
                    }
                }
                lock (progressGate)
                {
                    if (reused) skipped++; int count = ++verified;
                    if (progress != null && (count == 1 || count == total || count % Math.Max(1, total / 200) == 0)) progress.Report("归档预检 " + count + "/" + total + "，已跳过 " + skipped + "，重新核验 " + (count - skipped));
                }
            });
            ServerStorage.Write(root, "repo.archive-hashes.json", json.Serialize(archiveHashes.ToDictionary(x => x.Key, x => x.Value)), false);
            ServerStorage.Write(root, "repo.archive-verification.json", json.Serialize(verifiedRecords.ToDictionary(x => x.Key, x => x.Value)), false);
            if (progress != null) progress.Report("归档核验完成，正在事务入库硬件索引与其余业务数据…");
            ServerStorage.ActivateDatabaseOnly(root);
            string packages = Path.GetFullPath(Path.Combine(root, "packages")); long reclaimed = 0;
            if (packages == root || !packages.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid cleanup target");
            if (Directory.Exists(packages))
            {
                if (progress != null) progress.Report("数据库读回校验完成，正在清理仓库散装驱动副本…");
                OrdinaryTree(packages); reclaimed = Directory.GetFiles(packages, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
                Directory.Delete(packages, true);
            }
            // Retain unchanged derived model archives too; rebuilding them would undo the upgrade speedup.
            var modelPolicies = MachineDriverPackages.Load(root).GroupBy(p => p.SourceName + "\n" + p.PackagePrefix).Select(g => g.First()).Where(p => manifest.Entries.Any(e => MachineDriverPackages.Contains(e, p))).ToList();
            var modelNames = new HashSet<string>(modelPolicies.Select(p => Path.GetFileName(MachineDriverPackages.Archive(root, p.SourceName, p.PackagePrefix, false))), StringComparer.OrdinalIgnoreCase);
            string downloads = Path.Combine(root, ".uda-downloads");
            var retiredArchives = ServerStorage.RetainedDownloadArchiveNames(root);
            foreach (string file in Directory.GetFiles(downloads, "*.zip")) if (!archiveHashes.ContainsKey(Path.GetFileName(file)) && !modelNames.Contains(Path.GetFileName(file)) && !retiredArchives.Contains(Path.GetFileName(file))) { if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid archive cleanup target"); reclaimed += new FileInfo(file).Length; File.Delete(file); }
            var modelPrevious = new Dictionary<string, ArchiveVerificationRecord>(StringComparer.OrdinalIgnoreCase);
            string modelText = ServerStorage.Read(root, "repo.model-archive-verification.json");
            try { if (modelText != null) modelPrevious = json.Deserialize<Dictionary<string, ArchiveVerificationRecord>>(modelText) ?? modelPrevious; } catch (ArgumentException) { } catch (InvalidOperationException) { }
            var modelRecords = new Dictionary<string, ArchiveVerificationRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var policy in modelPolicies)
            {
                string path = MachineDriverPackages.Archive(root, policy.SourceName, policy.PackagePrefix, false), name = Path.GetFileName(path);
                ArchiveVerificationRecord record = null; bool reuse = false;
                if (File.Exists(path)) using (var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    string state = ArchiveFileState(guard);
                    reuse = state != null && modelPrevious.TryGetValue(name, out record) && record != null && record.Version == 1 && record.EntriesHash == name && record.FileState == state && IsVerifiedRecord(record) && !string.IsNullOrEmpty(record.ArchiveHash);
                }
                if (!reuse)
                {
                    path = ServerStorage.PrivateOnlineArchive(root, path);
                    if (File.Exists(path)) { reclaimed += new FileInfo(path).Length; File.Delete(path); }
                    MachineDriverPackages.Archive(root, policy.SourceName, policy.PackagePrefix);
                    using (var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                        record = new ArchiveVerificationRecord { FileState = ArchiveFileState(guard), EntriesHash = name, ArchiveHash = OfflineRepoGenerator.Sha256File(path), VerifiedUtc = DateTime.UtcNow.ToString("o") };
                    reclaimed -= new FileInfo(path).Length;
                }
                modelRecords[name] = record;
            }
            ServerStorage.Write(root, "repo.model-archive-verification.json", json.Serialize(modelRecords), false);
            return Math.Max(0, reclaimed);
        }
    }
    public static class ServerUpgrade
    {
        internal static int RunSetupProcess(string file, string args, Action<string> output)
        {
            using (var process = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo(file, args) {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true } }) {
                System.Diagnostics.DataReceivedEventHandler receive = (sender, e) => { if (e.Data != null && output != null) output(e.Data); };
                process.OutputDataReceived += receive; process.ErrorDataReceived += receive;
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                // Parameterless WaitForExit also waits for redirected asynchronous readers.
                process.WaitForExit();
                return process.ExitCode;
            }
        }
        internal static string SetupFailureSummary(string output)
        {
            var lines = (output ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string error = lines.FirstOrDefault(line => line.IndexOf("ERROR:", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf("FATAL:", StringComparison.OrdinalIgnoreCase) >= 0);
            if (string.IsNullOrEmpty(error)) error = lines.LastOrDefault(line => !line.StartsWith("PROGRESS:", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(error)) error = "子进程未提供具体错误，请查看详细诊断日志。";
            return error.Length > 1200 ? error.Substring(0, 1200) + "…" : error;
        }

        // A downloaded single EXE has no adjacent CLR configuration. Launch a configured
        // temporary copy so CPU groups and long paths also apply to the installer itself.
        internal static string PrepareInstallerRuntime(string executable, string directory)
        {
            Directory.CreateDirectory(directory);
            string target = Path.Combine(directory, "UDA-Server-Setup.exe");
            File.Copy(executable, target, false);
            const string runtime = "<?xml version=\"1.0\" encoding=\"utf-8\"?><configuration><startup useLegacyV2RuntimeActivationPolicy=\"true\"><supportedRuntime version=\"v4.0\" sku=\".NETFramework,Version=v4.6.2\" /></startup><runtime><gcServer enabled=\"true\"/><GCCpuGroup enabled=\"true\"/><Thread_UseAllCpuGroups enabled=\"true\"/><AppContextSwitchOverrides value=\"Switch.System.IO.UseLegacyPathHandling=false;Switch.System.IO.BlockLongPaths=false\"/></runtime></configuration>";
            File.WriteAllText(target + ".config", runtime, new UTF8Encoding(false));
            return target;
        }
        internal static bool CanReplaceFile(string path)
        {
            if (!File.Exists(path)) return true;
            try { using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return true; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        private static string LockedApplicationFile(string application)
        {
            if (!Directory.Exists(application)) return null;
            var paths = new List<string>();
            foreach (string name in new[] { "LanDriverService.exe", "UDA-Server-Manager.exe", "UDA-Database-Migration.exe", "OfflineRepoGenerator.exe" }) paths.Add(Path.Combine(application, name));
            paths.AddRange(Directory.GetFiles(application, "*.exe.config"));
            foreach (string name in new[] { "ClientTemplate", "DatabaseProvider", "database" })
            {
                string directory = Path.Combine(application, name);
                if (Directory.Exists(directory)) paths.AddRange(Directory.GetFiles(directory, "*", SearchOption.AllDirectories));
            }
            return paths.FirstOrDefault(path => !CanReplaceFile(path));
        }
        internal static bool WaitForUpdateRelease(Func<bool> stopped, Func<bool> exited, Func<bool> released, TimeSpan timeout, Action progress)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                if (stopped() && exited() && released()) return true;
                if (clock.Elapsed >= timeout) return false;
                if (progress != null) progress();
                System.Threading.Thread.Sleep(100);
            }
        }
        public static void StopServiceForUpdate(string serviceName, string executable, string application, TimeSpan timeout, Action<string> progress = null, bool checkApplicationFiles = true)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(serviceName ?? "", @"^[A-Za-z0-9_.-]+$")) throw new ArgumentException("Invalid service name");
            executable = Path.GetFullPath(executable); application = Path.GetFullPath(application);
            using (var service = new System.ServiceProcess.ServiceController(serviceName))
            {
                System.Diagnostics.Process process = null;
                try
                {
                    service.Refresh();
                    using (var searcher = new System.Management.ManagementObjectSearcher("SELECT ProcessId,PathName FROM Win32_Service WHERE Name='" + serviceName + "'"))
                    using (var rows = searcher.Get()) foreach (System.Management.ManagementObject row in rows) using (row)
                    {
                        string registered = ServiceExecutable(Convert.ToString(row["PathName"]));
                        if (!string.Equals(Path.GetFullPath(registered), executable, StringComparison.OrdinalIgnoreCase)) throw new IOException("服务程序路径已改变，已中止更新，请重新打开安装器");
                        int pid = Convert.ToInt32(row["ProcessId"]);
                        if (pid > 0)
                        {
                            try { process = System.Diagnostics.Process.GetProcessById(pid); var pinnedHandle = process.Handle; }
                            catch (ArgumentException) { if (process != null) process.Dispose(); process = null; }
                        }
                    }
                    if (service.Status == System.ServiceProcess.ServiceControllerStatus.StartPending) service.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, timeout);
                    service.Refresh();
                    if (service.Status != System.ServiceProcess.ServiceControllerStatus.Stopped && service.Status != System.ServiceProcess.ServiceControllerStatus.StopPending) service.Stop();
                    string blocked = null; DateTime lastProgress = DateTime.MinValue;
                    bool ready = WaitForUpdateRelease(
                        () => { service.Refresh(); return service.Status == System.ServiceProcess.ServiceControllerStatus.Stopped; },
                        () => { if (process == null) return true; if (!process.HasExited) return false; process.Dispose(); process = null; return true; },
                        () => { blocked = checkApplicationFiles ? LockedApplicationFile(application) : null; return blocked == null; }, timeout,
                        () => { if (progress != null && (DateTime.UtcNow - lastProgress).TotalSeconds >= 1) { lastProgress = DateTime.UtcNow; progress(blocked == null ? "正在等待旧服务进程完全退出…" : "正在等待程序文件解除占用：" + blocked); } });
                    if (!ready) throw new IOException("旧服务进程或程序文件仍被占用，已停止覆盖。" + (blocked == null ? executable : blocked) + "；请关闭正在运行的服务端管理器、迁移或仓库工具后重试。");
                }
                finally { if (process != null) process.Dispose(); }
            }
        }
        public static string ServiceExecutable(string command)
        { var m = System.Text.RegularExpressions.Regex.Match(command ?? "", "^\\s*(?:\"([^\"]+)\"|(\\S+))"); return m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : ""; }
        private static string SetArgument(string command, string name, string value)
        {
            string pattern = "(?:^|\\s)" + System.Text.RegularExpressions.Regex.Escape(name) + "\\s+(?:\"[^\"]*\"|\\S+)";
            string replacement = " " + name + " \"" + value.Replace("\"", "") + "\"";
            return System.Text.RegularExpressions.Regex.IsMatch(command, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? System.Text.RegularExpressions.Regex.Replace(command, pattern, match => replacement, System.Text.RegularExpressions.RegexOptions.IgnoreCase) : command + replacement;
        }
        public static string UpdatedCommand(string previous, string executable, string repository, int port, string template)
        { return UpdatedCommand(previous, executable, repository, port, template, null); }
        public static string UpdatedCommand(string previous, string executable, string repository, int port, string template, string advertiseUrl)
        {
            string old = ServiceExecutable(previous);
            if (old.Length == 0) throw new ArgumentException("Missing existing service command");
            var start = System.Text.RegularExpressions.Regex.Match(previous, "^\\s*(?:\"[^\"]+\"|\\S+)");
            string command = "\"" + executable + "\"" + previous.Substring(start.Length);
            command = SetArgument(command, "--repo", repository); command = SetArgument(command, "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            command = SetArgument(command, "--client-template", template);
            return string.IsNullOrWhiteSpace(advertiseUrl) ? command : SetArgument(command, "--advertise", advertiseUrl.TrimEnd('/'));
        }
        public static string Backup(string application, string data, string repository, string command)
        {
            // The installer only replaces application binaries and restores this tree if startup fails.
            // Service data and repository logs remain in place throughout the upgrade, so copying them
            // into a deeper backup tree is redundant and can exceed MAX_PATH on machines with nested
            // telemetry or repository state. Keep rollback data shallow and limited to replaced files.
            string backupRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UDA-UpgradeBackups");
            string backup = Path.Combine(backupRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backup);
            var acl = new System.Security.AccessControl.DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null), new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null), System.Security.Principal.WindowsIdentity.GetCurrent().User })
                acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            Directory.SetAccessControl(backup, acl);
            string app = Path.Combine(backup, "Application"); Directory.CreateDirectory(app);
            if (Directory.Exists(application))
            {
                foreach (string file in Directory.GetFiles(application)) CopyBackupFile(file, Path.Combine(app, Path.GetFileName(file)));
                foreach (string name in new[] { "ClientTemplate", "DatabaseProvider", "database" })
                    CopyBackupTree(Path.Combine(application, name), Path.Combine(app, name));
            }
            File.WriteAllText(Path.Combine(backup, "service-command.json"), new JavaScriptSerializer().Serialize(new { command = command }), new UTF8Encoding(false));
            return backup;
        }
        private static void CopyBackupFile(string source, string destination)
        {
            try { File.Copy(source, destination, true); }
            catch (Exception ex) { throw new IOException("升级回滚备份文件失败：" + source + " -> " + destination + "（" + ex.Message + "）", ex); }
        }
        private static void CopyBackupTree(string source, string destination)
        {
            if (!Directory.Exists(source)) return;
            try
            {
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("备份目录是重解析点");
                Directory.CreateDirectory(destination);
                foreach (string file in Directory.GetFiles(source))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("备份文件是重解析点");
                    CopyBackupFile(file, Path.Combine(destination, Path.GetFileName(file)));
                }
                foreach (string directory in Directory.GetDirectories(source))
                    CopyBackupTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
            }
            catch (Exception ex)
            {
                if (ex.Message.StartsWith("升级回滚备份", StringComparison.Ordinal)) throw;
                throw new IOException("升级回滚备份目录失败：" + source + " -> " + destination + "（" + ex.Message + "）", ex);
            }
        }
        public static void RestoreApplication(string backup, string target)
        { ServerStorage.CopyStateTree(Path.Combine(backup, "Application"), target); }
    }
    public static class ServerStorage
    {
        internal static string TestLogDirectory;
        public static string DatabaseFailureMessage(Exception failure)
        {
            int number = 0;
            string state = "";
            var messages = new StringBuilder();
            for (Exception current = failure; current != null; current = current.InnerException)
            {
                messages.AppendLine(current.Message);
                if (current is DbException)
                {
                    var property = current.GetType().GetProperty("Number");
                    if (property != null) try { number = Convert.ToInt32(property.GetValue(current, null)); } catch { }
                    property = current.GetType().GetProperty("SqlState");
                    if (property != null) try { state = Convert.ToString(property.GetValue(current, null)); } catch { }
                }
            }
            string text = messages.ToString().ToLowerInvariant();
            string hint;
            if (text.Contains("allowpublickeyretrieval") || text.Contains("rsa public key"))
                hint = "MySQL SHA-2 认证需要 TLS 加密连接或预先配置的服务器 RSA 公钥。驱动支持 MySQL 8.0 认证；请检查数据库 TLS 配置";
            else if (text.Contains("certificate") || text.Contains("certificat") || text.Contains("证书"))
                hint = "TLS 证书验证失败。VerifyFull 要求 CA 可信且数据库地址与证书名称匹配；请在迁移工具选择 CA 证书，并填写匹配的域名或 IP。Navicat 的 SSL 验证设置可能不同";
            else if (text.Contains("does not support ssl") || text.Contains("doesn't support ssl"))
                hint = "数据库未提供 TLS，但当前模式要求加密；请先为 MySQL 启用 TLS";
            else if (text.Contains("ssl") || text.Contains("tls"))
                hint = "TLS 握手失败；请检查 MySQL 是否启用 TLS、Windows TLS 支持及证书设置。Required 仍加密但不校验证书身份，可仅用于区分证书校验问题";
            else if (number == 1045 || number == 1698)
                hint = "数据库认证被拒绝；请检查 MySQL 账号、密码和该账号允许连接的 UDA 服务器来源地址";
            else if (number == 1044 || number == 1142 || number == 1143 || number == 1227)
                hint = "数据库权限不足；请检查目标库的 SELECT、INSERT、UPDATE、DELETE 权限，首次自动建表还需要 CREATE";
            else if (number == 1049)
                hint = "目标数据库不存在；请先创建数据库，再核对迁移表单中的数据库名";
            else if (number == 1153 || text.Contains("max_allowed_packet"))
                hint = "数据超过 MySQL 的 max_allowed_packet 限制；请由 DBA 调整该限制后重试迁移";
            else if (number == 1205 || number == 1213)
                hint = "数据库事务锁等待超时或发生死锁；请结束其他数据库维护任务后重试";
            else if (number == 1042 || number == 2002 || number == 2003 || text.Contains("unable to connect") || text.Contains("connection refused") || text.Contains("timed out") || text.Contains("timeout"))
                hint = "无法连接或连接超时；请检查 MySQL 地址、端口、监听地址、防火墙及数据库服务状态";
            else if (failure is UnauthorizedAccessException)
                hint = "UDA 服务账户无权读取或写入所需文件；请检查 CA 证书文件和仓库配置目录权限";
            else
                hint = "请结合 MySQL 错误码检查数据库服务、连接配置和权限";
            // Return only controlled diagnostic text/codes, never raw provider messages, connection
            // strings or exception stacks (which can include credentials supplied by an administrator).
            if (state == null || !System.Text.RegularExpressions.Regex.IsMatch(state, "^[A-Za-z0-9]{5}$")) state = "";
            return "数据库操作失败（" + (failure != null ? failure.GetType().Name : "Unknown") +
                (number != 0 ? "，MySQL " + number : "") + (state.Length > 0 ? "，SQLSTATE " + state : "") + "）：" + hint + "。请修复后重试。";
        }

        private static readonly ConcurrentDictionary<string, object> Gates = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, string> Errors = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Catalog-only removal: archives remain immutable so in-flight downloads stay valid.
        internal static int DeleteCatalogEntries(string root, IList<string> paths, Action<OfflineRepo> publish)
        {
            {
                var cfg = Config(root);
                if (!cfg.DatabaseOnly) throw new InvalidOperationException("Catalog-only deletion requires database-only storage");
                var manifest = OfflineRepoGenerator.TryLoadManifest(root);
                if (manifest == null) throw new InvalidDataException("Repository catalog missing");
                var requested = new HashSet<string>(paths.Select(x => x.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
                var removed = manifest.Entries.Where(e => requested.Contains(e.RelativePath.Replace('\\', '/'))).ToList();
                if (removed.Count == 0) return 0;
                manifest.Entries = manifest.Entries.Except(removed).ToList();
                manifest.HiddenPaths = (manifest.HiddenPaths ?? new List<string>()).Concat(removed.Select(e => e.RelativePath)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                manifest.FileCount = manifest.Entries.Count; manifest.TotalBytes = manifest.Entries.Sum(e => e.Size);
                string temporary = Path.Combine(Path.GetTempPath(), "UDA-delete-" + Guid.NewGuid().ToString("N") + ".udadb");
                try
                {
                    var index = new DriverIndex(temporary); index.ReplaceAll(manifest.Entries.Select(OfflineRepoGenerator.ToPortableRecord)); index.Commit();
                    manifest.IndexDbHash = OfflineRepoGenerator.Sha256File(temporary);
                    manifest.IntegrityHash = OfflineRepoGenerator.ComputeIntegrityHash(manifest);
                    var snapshot = OfflineRepo.FromCatalog(root, manifest);
                    string prefix = "repo.index-block." + manifest.IndexDbHash.Substring(0, 32) + ".";
                    using (var db = Open(Connection(cfg))) using (var tx = db.BeginTransaction())
                    {
                        string oldDeleted = Get(db, tx, cfg.RepositoryId, "repo.deleted.txt") ?? "";
                        var hashes = new HashSet<string>(oldDeleted.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
                        foreach (var e in removed) if (string.IsNullOrEmpty(e.PackageRoot) && !string.IsNullOrWhiteSpace(e.ContentHash)) hashes.Add(e.ContentHash);
                        using (var stream = File.OpenRead(temporary))
                        {
                            byte[] bytes = new byte[65536]; int n, blocks = 0;
                            while ((n = stream.Read(bytes, 0, bytes.Length)) > 0)
                                Put(db, tx, cfg.RepositoryId, prefix + (blocks++).ToString("D12") + ".json", Json().Serialize(new { data = Convert.ToBase64String(bytes, 0, n) }), false);
                            Put(db, tx, cfg.RepositoryId, "repo.index-state.json", Json().Serialize(new { length = stream.Length, blocks = blocks, sha256 = manifest.IndexDbHash, prefix = prefix }), false);
                        }
                        Put(db, tx, cfg.RepositoryId, "repo.deleted.txt", string.Join("\n", hashes.OrderBy(x => x)), false);
                        string revokedText = Get(db, tx, cfg.RepositoryId, "repo.revoked-download-paths.json");
                        var revoked = revokedText == null ? new List<string>() : Json().Deserialize<List<string>>(revokedText);
                        Put(db, tx, cfg.RepositoryId, "repo.revoked-download-paths.json", Json().Serialize(revoked.Concat(removed.Select(e => e.RelativePath)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()), false);
                        Put(db, tx, cfg.RepositoryId, "repo.manifest.json", OfflineRepoGenerator.SerializeManifest(manifest), false);
                        using (var cmd = Command(db, tx, "DELETE FROM uda_documents WHERE repository_id=@p0 AND document_key LIKE 'repo.index-block.%' AND document_key NOT LIKE @p1", cfg.RepositoryId, prefix + "%")) cmd.ExecuteNonQuery();
                        tx.Commit();
                    }
                    publish(snapshot);
                    return removed.Count;
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        private static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 512 }; }
        private static object Gate(string root) { return Gates.GetOrAdd(Path.GetFullPath(root), r => new object()); }
        public static void RunExclusive(string root, Action action) { lock (Gate(root)) action(); }
        private static readonly ConcurrentDictionary<string, OnlineWorkspace> OnlineWorkspaces = new ConcurrentDictionary<string, OnlineWorkspace>(StringComparer.OrdinalIgnoreCase);
        public static string ResolveOnlineArchive(string root, string name)
        {
            string local = Path.Combine(Path.GetFullPath(root), ".uda-downloads", name); OnlineWorkspace work;
            if (OnlineWorkspaces.TryGetValue(Path.GetFullPath(root), out work) && !work.PrivateNames.ContainsKey(name) && !File.Exists(local))
            { string published = Path.Combine(work.OriginalRoot, ".uda-downloads", name); if (File.Exists(published)) return published; }
            return local;
        }
        public static string PrivateOnlineArchive(string root, string path)
        {
            OnlineWorkspace work;
            if (!OnlineWorkspaces.TryGetValue(Path.GetFullPath(root), out work)) return path;
            string name = Path.GetFileName(path); work.PrivateNames[name] = true;
            return Path.Combine(work.Root, ".uda-downloads", name);
        }
        internal static void CleanupRetiredDownloads(string root, IEnumerable<string> activeArchives)
        {
            var keep = new HashSet<string>(activeArchives, StringComparer.OrdinalIgnoreCase);
            string now = DateTime.UtcNow.ToString("o");
            foreach (var row in Documents(root, "repo.download-history."))
            {
                var tickets = Json().Deserialize<List<RetiredDriverDownload>>(row.Value);
                var retained = tickets.Where(t => string.CompareOrdinal(t.ExpiresUtc, now) > 0).ToList();
                foreach (var ticket in retained) keep.Add(ticket.Archive);
                if (retained.Count == 0) Delete(root, row.Key);
            }
            string directory = Path.GetFullPath(Path.Combine(root, ".uda-downloads"));
            if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
            foreach (string file in Directory.GetFiles(directory, "*.zip"))
            {
                if (keep.Contains(Path.GetFileName(file)) || File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddDays(-7) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                // Existing download handles disallow deletion on Windows. Busy archives stay for a later pass.
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        internal static HashSet<string> RetainedDownloadArchiveNames(string root)
        {
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string now = DateTime.UtcNow.ToString("o");
            if (Config(root).Enabled) foreach (var row in Documents(root, "repo.download-history."))
                foreach (var ticket in Json().Deserialize<List<RetiredDriverDownload>>(row.Value)) if (string.CompareOrdinal(ticket.ExpiresUtc, now) > 0) keep.Add(ticket.Archive);
            return keep;
        }
        internal static void RecoverOnlineWorkspaces(string root)
        {
            string parent = Path.Combine(Path.GetPathRoot(Path.GetFullPath(root)), ".UDA-W");
            if (!Directory.Exists(parent) || (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) return;
            foreach (string directory in Directory.GetDirectories(parent))
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(directory), "^[a-f0-9]{16}$") || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                string marker = Path.Combine(directory, ".owner.json"); if (!File.Exists(marker) || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0) continue;
                try
                {
                    var owner = Json().Deserialize<Dictionary<string, string>>(File.ReadAllText(marker));
                    if (!string.Equals(owner["root"], Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) continue;
                    bool active = false;
                    try { using (var process = System.Diagnostics.Process.GetProcessById(int.Parse(owner["pid"]))) active = process.StartTime.ToUniversalTime().ToString("o") == owner["start"]; } catch (ArgumentException) { }
                    if (active) continue;
                    var staged = Config(directory); var live = Config(root);
                    if (staged.RepositoryId == live.RepositoryId || staged.RepositoryId != owner["id"]) continue;
                    using (var db = Open(Connection(staged))) using (var cmd = Command(db, null, "DELETE FROM uda_documents WHERE repository_id=@p0", staged.RepositoryId)) cmd.ExecuteNonQuery();
                    ValidateWorkspaceTree(directory); Directory.Delete(directory, true);
                }
                catch (Exception ex) { ServiceEventLog.Write("warning", "恢复中断维护工作区时保留待复查：" + ex.GetType().Name); }
            }
        }
        private static void ValidateWorkspaceTree(string directory)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Unsafe workspace cleanup target");
            foreach (string item in Directory.GetFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(item);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Unsafe workspace member");
                if ((attributes & FileAttributes.Directory) != 0) ValidateWorkspaceTree(item);
            }
        }
        private static readonly string[] CatalogNames = { "repo.manifest.json", "repo.index-state.json", "repo.archive-hashes.json", "repo.archive-verification.json", "repo.model-archive-identities.json", "repo.model-archive-verification.json", "repo.machine-packages.json", "repo.path-aliases.json", "repo.category-overrides.json", "repo.deleted.txt" };
        private static Dictionary<string, string> CatalogDocuments(DbConnection db, DbTransaction tx, string id)
        {
            var rows = new Dictionary<string, string>(StringComparer.Ordinal);
            string predicate = string.Join(" OR ", CatalogNames.Select((name, i) => "document_key=@p" + (i + 1))) + " OR document_key LIKE 'repo.index-block.%'";
            using (var cmd = Command(db, tx, "SELECT document_key,body FROM uda_documents WHERE repository_id=@p0 AND (" + predicate + ")", new object[] { id }.Concat(CatalogNames.Cast<object>()).ToArray()))
            using (var reader = cmd.ExecuteReader()) while (reader.Read()) rows[reader.GetString(0)] = reader.GetString(1);
            return rows;
        }
        // Each writer works against an independent SQL repository identity and private raw files.
        // Existing ZIPs are read through a resolver, never copied, linked, modified or deleted.
        public sealed class OnlineWorkspace : IDisposable
        {
            public readonly string Root, OriginalRoot;
            internal readonly ConcurrentDictionary<string, bool> PrivateNames = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            private readonly ServerStorageConfig live, staged;
            private readonly Dictionary<string, string> baseline;
            private bool disposed;
            public OnlineWorkspace(string root)
            {
                OriginalRoot = Path.GetFullPath(root); live = Config(root);
                if (!live.DatabaseOnly) throw new InvalidOperationException("Online maintenance requires MySQL-backed storage");
                string workspaceParent = Path.Combine(Path.GetPathRoot(OriginalRoot), ".UDA-W");
                if (Directory.Exists(workspaceParent) && (File.GetAttributes(workspaceParent) & FileAttributes.ReparsePoint) != 0) throw new IOException("维护工作区不能是重解析目录");
                Root = Path.Combine(workspaceParent, Guid.NewGuid().ToString("N").Substring(0, 16));
                Directory.CreateDirectory(Root); Directory.CreateDirectory(Path.Combine(Root, ".uda-downloads"));
                staged = Json().Deserialize<ServerStorageConfig>(Json().Serialize(live)); staged.RepositoryId = Guid.NewGuid().ToString("N");
                Atomic(ConfigPath(Root), Json().Serialize(staged), true);
                using (var process = System.Diagnostics.Process.GetCurrentProcess())
                    Atomic(Path.Combine(Root, ".owner.json"), Json().Serialize(new Dictionary<string, string> { { "root", OriginalRoot }, { "id", staged.RepositoryId }, { "pid", process.Id.ToString() }, { "start", process.StartTime.ToUniversalTime().ToString("o") } }), true);
                try
                {
                    using (var db = Open(Connection(live))) using (var tx = db.BeginTransaction())
                    {
                        baseline = CatalogDocuments(db, tx, live.RepositoryId);
                        if (!baseline.ContainsKey("repo.manifest.json")) throw new IOException("Published catalog missing");
                        foreach (var row in baseline) Put(db, tx, staged.RepositoryId, row.Key, row.Value, false);
                        tx.Commit();
                    }
                    OnlineWorkspaces[Root] = this;
                }
                catch { Dispose(); throw; }
            }
            public OfflineRepo Commit(IEnumerable<RetiredDriverDownload> previous)
            {
                var manifest = OfflineRepoGenerator.TryLoadManifest(Root);
                var snapshot = OfflineRepo.FromCatalog(OriginalRoot, manifest);
                Dictionary<string, string> rows;
                using (var db = Open(Connection(staged))) rows = CatalogDocuments(db, null, staged.RepositoryId);
                // Install immutable files before the atomic metadata commit. Failure leaves only safe orphans.
                string liveDownloads = Path.Combine(OriginalRoot, ".uda-downloads"); Directory.CreateDirectory(liveDownloads);
                var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string file in Directory.GetFiles(Path.Combine(Root, ".uda-downloads"), "*.zip"))
                {
                    string target = Path.Combine(liveDownloads, Path.GetFileName(file));
                    if (!File.Exists(target)) File.Move(file, target);
                    else if (!string.Equals(OfflineRepoGenerator.Sha256File(file), OfflineRepoGenerator.Sha256File(target), StringComparison.OrdinalIgnoreCase)) throw new IOException("Immutable archive identity collision: " + Path.GetFileName(file));
                    installed.Add(Path.GetFileName(target));
                }
                // Moving a newly verified file changes NTFS metadata, not payload. Keep its checkpoint
                // bound to the final immutable file identity so later appends do not rehash it.
                foreach (string key in new[] { "repo.archive-verification.json", "repo.model-archive-verification.json" })
                {
                    string text; if (!rows.TryGetValue(key, out text)) continue;
                    var records = Json().Deserialize<Dictionary<string, ArchiveVerificationRecord>>(text);
                    foreach (string name in installed.Where(records.ContainsKey))
                        using (var guard = new FileStream(Path.Combine(liveDownloads, name), FileMode.Open, FileAccess.Read, FileShare.Read)) records[name].FileState = DatabaseArchiveRepository.ArchiveFileState(guard);
                    rows[key] = Json().Serialize(records);
                }
                using (var db = Open(Connection(live))) using (var tx = db.BeginTransaction())
                {
                    string current;
                    using (var cmd = Command(db, tx, "SELECT body FROM uda_documents WHERE repository_id=@p0 AND document_key='repo.manifest.json' FOR UPDATE", live.RepositoryId)) current = Convert.ToString(cmd.ExecuteScalar());
                    if (current != baseline["repo.manifest.json"]) throw new IOException("仓库已被另一写入任务更新，本次未切换，请重试");
                    foreach (string key in baseline.Keys.Union(rows.Keys).Where(k => !k.StartsWith("repo.index-block.", StringComparison.Ordinal)))
                    {
                        string before, after; baseline.TryGetValue(key, out before); rows.TryGetValue(key, out after);
                        if (before == after) continue;
                        if (Get(db, tx, live.RepositoryId, key) != before) throw new IOException("仓库配置已更改，本次未覆盖：" + key);
                    }
                    foreach (var row in rows)
                    { string before; if (!baseline.TryGetValue(row.Key, out before) || before != row.Value) Put(db, tx, live.RepositoryId, row.Key, row.Value, false); }
                    foreach (string key in baseline.Keys.Except(rows.Keys)) using (var cmd = Command(db, tx, "DELETE FROM uda_documents WHERE repository_id=@p0 AND document_key=@p1", live.RepositoryId, key)) cmd.ExecuteNonQuery();
                    var tickets = previous.ToList(); string generation = Guid.NewGuid().ToString("N");
                    for (int offset = 0; offset < tickets.Count; offset += 32) Put(db, tx, live.RepositoryId, "repo.download-history." + generation + "." + offset.ToString("D12") + ".json", Json().Serialize(tickets.Skip(offset).Take(32).ToList()), false);
                    tx.Commit();
                }
                return snapshot;
            }
            public void Dispose()
            {
                if (disposed) return; disposed = true; OnlineWorkspace ignored; OnlineWorkspaces.TryRemove(Root, out ignored);
                MachineDriverPackages.ReleaseWorkspaceGates(Root);
                object unusedGate; Gates.TryRemove(Path.GetFullPath(Root), out unusedGate); string unusedError; Errors.TryRemove(Path.GetFullPath(Root), out unusedError);
                try { if (staged != null && staged.RepositoryId != live.RepositoryId) using (var db = Open(Connection(staged))) using (var cmd = Command(db, null, "DELETE FROM uda_documents WHERE repository_id=@p0", staged.RepositoryId)) cmd.ExecuteNonQuery(); } catch (Exception ex) { ServiceEventLog.Write("warning", "清理维护工作区 SQL 暂存失败：" + ex.GetType().Name); }
                string boundary = Path.GetFullPath(Path.Combine(Path.GetPathRoot(OriginalRoot), ".UDA-W")).TrimEnd('\\') + "\\";
                try { if (Path.GetFullPath(Root).StartsWith(boundary, StringComparison.OrdinalIgnoreCase) && Directory.Exists(Root)) { ValidateWorkspaceTree(Root); Directory.Delete(Root, true); } } catch (Exception ex) { ServiceEventLog.Write("warning", "清理维护工作区文件失败：" + ex.GetType().Name); }
            }
        }
        private static string ConfigPath(string root) { return Path.Combine(root, ".uda-storage.json"); }
        public static ServerStorageConfig Config(string root) { string p = ConfigPath(root); return File.Exists(p) ? Json().Deserialize<ServerStorageConfig>(File.ReadAllText(p)) : new ServerStorageConfig(); }
        static ServerStorage()
        {
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs args) {
                string name = new AssemblyName(args.Name).Name + ".dll";
                string path = Path.Combine(ProviderDirectory(), name);
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
        }
        private static string ProviderDirectory()
        {
            if (_providerOverride != null) return _providerOverride;
            string bundled = Path.Combine(Path.GetDirectoryName(typeof(ServerStorage).Assembly.Location), "DatabaseProvider");
            return Directory.Exists(bundled) ? bundled : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DatabaseProvider");
        }
        private static string _providerOverride;
        public static void UseProviderDirectory(string directory) { if (!File.Exists(Path.Combine(directory, "MySqlConnector.dll"))) throw new IOException("数据库运行库缺失"); _providerOverride = Path.GetFullPath(directory); }
        private static string Safe(string root, string key)
        {
            bool metadata = key.IndexOf('/') < 0 && key.IndexOf('\\') < 0 && ((key.StartsWith("repo.", StringComparison.Ordinal) && key.EndsWith(".json", StringComparison.Ordinal) && key != "repo.organization-journal.json") || key == "repo.deleted.txt");
            Guid eventId;
            bool installationEvent = key.StartsWith("install-events/", StringComparison.Ordinal) && key.EndsWith(".json", StringComparison.Ordinal) && Guid.TryParseExact(key.Substring("install-events/".Length, key.Length - "install-events/".Length - ".json".Length), "N", out eventId);
            bool log = System.Text.RegularExpressions.Regex.IsMatch(key, @"^server-logs/(service|audit)/[a-f0-9]{64}/[a-f0-9]{32}/[0-9]{20}\.json$");
            if (!metadata && !installationEvent && !log) throw new ArgumentException("Unsupported server storage document");
            if (log) using (var sha = SHA256.Create())
                return Path.Combine(Path.GetFullPath(root), "server-logs", key.Split('/')[1], BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").ToLowerInvariant() + ".json");
            string prefix = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            string path = Path.GetFullPath(Path.Combine(prefix, key.Replace('/', '\\')));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(key) || key.Contains(":")) throw new ArgumentException("Invalid storage key");
            return path;
        }
        private static string Marker(string root, string key)
        { using (var sha = SHA256.Create()) return Path.Combine(root, ".uda-outbox", BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "") + ".json"); }
        private static void Atomic(string path, string text, bool secret = false)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)); string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try { using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { byte[] bytes = new UTF8Encoding(false).GetBytes(text); stream.Write(bytes, 0, bytes.Length); stream.Flush(true); } if (secret) SecureFile(tmp); if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path); }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
        public static void SecureFile(string path)
        {
            var acl = new System.Security.AccessControl.FileSecurity(); acl.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null), new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null), System.Security.Principal.WindowsIdentity.GetCurrent().User })
                acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            File.SetAccessControl(path, acl);
        }
        private static DbConnection Open(string connection)
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connection };
            if (!builder.ContainsKey("Connection Timeout")) builder["Connection Timeout"] = 5;
            if (!builder.ContainsKey("SslMode") && !builder.ContainsKey("SSL Mode"))
            {
                string host = "localhost";
                foreach (string name in new[] { "Server", "Host", "Data Source", "Address" }) if (builder.ContainsKey(name)) host = Convert.ToString(builder[name]);
                if (host != "localhost" && host != "127.0.0.1" && host != "::1") builder["SslMode"] = "VerifyFull";
            }
            string path = Path.Combine(ProviderDirectory(), "MySqlConnector.dll");
            if (!File.Exists(path)) throw new InvalidOperationException("DatabaseProvider/MySqlConnector.dll missing");
            var type = Assembly.LoadFrom(path).GetType("MySqlConnector.MySqlConnection", true);
            var db = (DbConnection)Activator.CreateInstance(type); db.ConnectionString = builder.ConnectionString;
            try { db.Open(); return db; } catch { db.Dispose(); throw; }
        }
        private static string Connection(ServerStorageConfig cfg) { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(cfg.ProtectedConnection), null, DataProtectionScope.LocalMachine)); }
        private static DbCommand Command(DbConnection db, DbTransaction tx, string sql, params object[] values)
        {
            var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.Transaction = tx; cmd.CommandTimeout = 30;
            for (int i = 0; i < values.Length; i++) { var p = cmd.CreateParameter(); p.ParameterName = "@p" + i; p.Value = values[i] ?? DBNull.Value; cmd.Parameters.Add(p); } return cmd;
        }
        private static void Schema(DbConnection db)
        {
            try
            {
                using (var c = Command(db, null, "SELECT version FROM uda_schema WHERE id=1"))
                {
                    object version = c.ExecuteScalar();
                    if (version != null && Convert.ToInt32(version) != 1) throw new InvalidOperationException("Unsupported database schema; no data changed");
                    if (version != null) { using (var exists = Command(db, null, "SELECT document_key,body FROM uda_documents LIMIT 0")) exists.ExecuteNonQuery(); return; }
                }
            }
            catch (DbException) { /* A new database needs bootstrap DDL; an existing schema needs no CREATE privilege. */ }
            using (var c = Command(db, null, "CREATE TABLE IF NOT EXISTS uda_schema (id INT PRIMARY KEY, version INT NOT NULL) ENGINE=InnoDB")) c.ExecuteNonQuery();
            using (var c = Command(db, null, "INSERT IGNORE INTO uda_schema(id,version) VALUES(1,1)")) c.ExecuteNonQuery();
            using (var c = Command(db, null, "SELECT version FROM uda_schema WHERE id=1")) if (Convert.ToInt32(c.ExecuteScalar()) != 1) throw new InvalidOperationException("Unsupported database schema; no data changed");
            using (var c = Command(db, null, "CREATE TABLE IF NOT EXISTS uda_documents (repository_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL, document_key VARCHAR(512) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, body LONGTEXT CHARACTER SET utf8mb4 NOT NULL, updated_utc DATETIME(6) NOT NULL, PRIMARY KEY(repository_id,document_key)) ENGINE=InnoDB")) c.ExecuteNonQuery();
        }
        private static void Put(DbConnection db, DbTransaction tx, string id, string key, string body, bool immutable)
        {
            CheckSchema(db, tx);
            using (var cmd = Command(db, tx, "INSERT INTO uda_documents(repository_id,document_key,body,updated_utc) VALUES(@p0,@p1,@p2,UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE " + (immutable ? "document_key=document_key" : "body=VALUES(body),updated_utc=VALUES(updated_utc)"), id, key, body)) cmd.ExecuteNonQuery();
        }
        private static string Get(DbConnection db, DbTransaction tx, string id, string key)
        { CheckSchema(db, tx); using (var c = Command(db, tx, "SELECT body FROM uda_documents WHERE repository_id=@p0 AND document_key=@p1", id, key)) { object value = c.ExecuteScalar(); return value == null ? null : Convert.ToString(value); } }
        private static void CheckSchema(DbConnection db, DbTransaction tx)
        { using (var c = Command(db, tx, "SELECT version FROM uda_schema WHERE id=1")) if (Convert.ToInt32(c.ExecuteScalar()) != 1) throw new InvalidOperationException("Unsupported database schema"); }
        private static Dictionary<string, string> All(DbConnection db, string id, string prefix = null)
        {
            CheckSchema(db, null);
            var rows = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var c = prefix == null ? Command(db, null, "SELECT document_key,body FROM uda_documents WHERE repository_id=@p0", id) : Command(db, null, "SELECT document_key,body FROM uda_documents WHERE repository_id=@p0 AND document_key LIKE @p1", id, prefix + "%")) using (var r = c.ExecuteReader()) while (r.Read()) rows[r.GetString(0)] = r.GetString(1); return rows;
        }
        private static void Error(string root, Exception ex) { Errors[Path.GetFullPath(root)] = "External database unavailable (" + ex.GetType().Name + "); durable local queue active"; }
        public static object Status(string root)
        {
            var cfg = Config(root); string message; Errors.TryGetValue(Path.GetFullPath(root), out message);
            string outbox = Path.Combine(root, ".uda-outbox");
            int pendingLogs = 0;
            if (cfg.Enabled) try { foreach (var file in LogFiles()) { string checkpoint = LogCursorPath(root, LogSource(file.Key)); var info = new FileInfo(file.Key); var cursor = File.Exists(checkpoint) ? Json().Deserialize<StorageLogCursor>(File.ReadAllText(checkpoint)) : null; if (cursor == null || cursor.Length != info.Length || cursor.Modified != info.LastWriteTimeUtc.Ticks || cursor.Created != info.CreationTimeUtc.Ticks) pendingLogs++; } } catch { pendingLogs = -1; }
            return new { backend = cfg.Enabled ? "mysql" : "local", databaseOnly = cfg.DatabaseOnly, repositoryId = cfg.Enabled ? cfg.RepositoryId : "", pending = Directory.Exists(outbox) ? Directory.GetFiles(outbox, "*.json").Length : 0, pendingLogFiles = pendingLogs, error = message ?? "", schemaVersion = 1 };
        }
        public static string Read(string root, string key)
        {
            lock (Gate(root))
            {
                string path = Safe(root, key); var cfg = Config(root);
                if (cfg.DatabaseOnly)
                {
                    if (!cfg.Enabled) throw new InvalidOperationException("本版本要求 MySQL 主存储");
                    try { using (var db = Open(Connection(cfg))) return Get(db, null, cfg.RepositoryId, key); }
                    catch (Exception ex) { Error(root, ex); throw; }
                }
                if (cfg.Enabled)
                {
                    string marker = Marker(root, key);
                    if (File.Exists(marker)) return Json().Deserialize<StoragePending>(File.ReadAllText(marker)).Text;
                    try { using (var db = Open(Connection(cfg))) { string value = Get(db, null, cfg.RepositoryId, key); if (value != null) { if (!File.Exists(path)) Atomic(path, value); return value; } } }
                    catch (Exception ex) { Error(root, ex); }
                }
                return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
            }
        }
        internal sealed class InstallEventChange
        {
            public long Revision; public string Key; public DriverInstallEvent Header; public bool Immutable;
        }
        private sealed class InstallEventJournal
        {
            public long Revision; public readonly Queue<InstallEventChange> Changes = new Queue<InstallEventChange>();
        }
        private static readonly ConcurrentDictionary<string, InstallEventJournal> InstallJournals = new ConcurrentDictionary<string, InstallEventJournal>(StringComparer.OrdinalIgnoreCase);
        private static InstallEventJournal InstallJournal(string root) { return InstallJournals.GetOrAdd(Path.GetFullPath(root), p => new InstallEventJournal()); }
        internal static DriverInstallEvent InstallHeader(string text)
        {
            try { var e = Json().Deserialize<DriverInstallEvent>(text); if (e != null && e.Detail != null && e.Detail.Length > 512) e.Detail = e.Detail.Substring(0, 512) + "\n（正文按需加载）"; return e; } catch { return null; }
        }
        private static void ObserveInstallEvent(string root, string key, string text, bool immutable)
        {
            if (!key.StartsWith("install-events/", StringComparison.Ordinal)) return;
            var journal = InstallJournal(root); var header = text == null ? null : InstallHeader(text);
            if (text != null && header == null) return;
            lock (journal) { journal.Changes.Enqueue(new InstallEventChange { Revision = ++journal.Revision, Key = key, Header = header, Immutable = immutable }); while (journal.Changes.Count > 512) journal.Changes.Dequeue(); }
        }
        internal static List<InstallEventChange> InstallChanges(string root, long since, out long revision)
        {
            var journal = InstallJournal(root);
            lock (journal) { revision = journal.Revision; if (journal.Changes.Count > 0 && since < journal.Changes.Peek().Revision - 1) return null; return journal.Changes.Where(c => c.Revision > since).ToList(); }
        }
        public static void Write(string root, string key, string text, bool immutable)
        {
            lock (Gate(root))
            {
                string path = Safe(root, key); var cfg = Config(root);
                if (cfg.DatabaseOnly)
                {
                    try { using (var db = Open(Connection(cfg))) Put(db, null, cfg.RepositoryId, key, text, immutable); }
                    catch (Exception ex) { Error(root, ex); throw; }
                    ObserveInstallEvent(root, key, text, immutable);
                    return;
                }
                if (immutable && File.Exists(path)) return;
                string marker = Marker(root, key);
                if (cfg.Enabled) Atomic(marker, Json().Serialize(new StoragePending { Key = key, Text = text, Immutable = immutable }));
                Atomic(path, text);
                if (cfg.Enabled) try { using (var db = Open(Connection(cfg))) { Put(db, null, cfg.RepositoryId, key, text, immutable); if (immutable) Atomic(path, Get(db, null, cfg.RepositoryId, key)); } File.Delete(marker); }
                catch (Exception ex) { Error(root, ex); }
                ObserveInstallEvent(root, key, text, immutable);
            }
        }
        public static void Publish(string root, string key)
        {
            string path = Safe(root, key); if (File.Exists(path) && Config(root).Enabled)
            {
                Write(root, key, File.ReadAllText(path, Encoding.UTF8), false);
                if (key == "repo.manifest.json") foreach (string file in MetadataFiles(root))
                    if (Path.GetFileName(file) != key) Write(root, Path.GetFileName(file), File.ReadAllText(file, Encoding.UTF8), false);
            }
        }
        public static void Delete(string root, string key)
        {
            lock (Gate(root))
            {
                string path = Safe(root, key); var cfg = Config(root); string marker = Marker(root, key);
                if (cfg.DatabaseOnly)
                {
                    using (var db = Open(Connection(cfg))) using (var cmd = Command(db, null, "DELETE FROM uda_documents WHERE repository_id=@p0 AND document_key=@p1", cfg.RepositoryId, key)) cmd.ExecuteNonQuery();
                    if (File.Exists(path)) File.Delete(path); ObserveInstallEvent(root, key, null, false); return;
                }
                if (cfg.Enabled) Atomic(marker, Json().Serialize(new StoragePending { Key = key, Deleted = true }));
                if (File.Exists(path)) File.Delete(path);
                if (cfg.Enabled) try { using (var db = Open(Connection(cfg))) { CheckSchema(db, null); using (var c = Command(db, null, "DELETE FROM uda_documents WHERE repository_id=@p0 AND document_key=@p1", cfg.RepositoryId, key)) c.ExecuteNonQuery(); } File.Delete(marker); } catch (Exception ex) { Error(root, ex); }
                ObserveInstallEvent(root, key, null, false);
            }
        }
        public static List<string> Events(string root)
        {
            lock (Gate(root))
            {
                var rows = new Dictionary<string, string>(StringComparer.Ordinal); string dir = Path.Combine(root, "install-events");
                if (Directory.Exists(dir)) foreach (string file in Directory.GetFiles(dir, "*.json")) rows["install-events/" + Path.GetFileName(file)] = File.ReadAllText(file);
                var cfg = Config(root);
                if (cfg.Enabled) try { using (var db = Open(Connection(cfg))) foreach (var row in All(db, cfg.RepositoryId, "install-events/")) rows[row.Key] = row.Value; }
                catch (Exception ex) { Error(root, ex); if (cfg.DatabaseOnly) throw; }
                string outbox = Path.Combine(root, ".uda-outbox");
                if (Directory.Exists(outbox)) foreach (string file in Directory.GetFiles(outbox, "*.json")) { var p = Json().Deserialize<StoragePending>(File.ReadAllText(file)); if (p.Key.StartsWith("install-events/", StringComparison.Ordinal)) { if (p.Deleted) rows.Remove(p.Key); else rows[p.Key] = p.Text; } }
                return rows.Values.ToList();
            }
        }
        internal static string InstallEventStamp(string root)
        {
            var cfg = Config(root);
            if (!cfg.DatabaseOnly) return Directory.GetLastWriteTimeUtc(Path.Combine(root, "install-events")).Ticks.ToString();
            using (var db = Open(Connection(cfg))) using (var cmd = Command(db, null, "SELECT COUNT(*),MAX(updated_utc) FROM uda_documents WHERE repository_id=@p0 AND document_key LIKE @p1", cfg.RepositoryId, "install-events/%"))
            using (var reader = cmd.ExecuteReader()) { reader.Read(); return reader.GetInt64(0) + ":" + (reader.IsDBNull(1) ? 0 : reader.GetDateTime(1).Ticks); }
        }
        internal static Dictionary<string, DriverInstallEvent> InstallEventHeaders(string root, out long revision, out string stamp)
        {
            lock (Gate(root))
            {
                var result = new Dictionary<string, DriverInstallEvent>(StringComparer.Ordinal);
                var cfg = Config(root);
                if (cfg.DatabaseOnly)
                {
                    long count = 0, latest = 0;
                    using (var db = Open(Connection(cfg))) using (var cmd = Command(db, null, "SELECT document_key,body,updated_utc FROM uda_documents WHERE repository_id=@p0 AND document_key LIKE @p1", cfg.RepositoryId, "install-events/%"))
                    using (var reader = cmd.ExecuteReader(System.Data.CommandBehavior.SequentialAccess)) while (reader.Read()) { string key = reader.GetString(0); var e = InstallHeader(reader.GetString(1)); if (e != null) result[key] = e; count++; latest = Math.Max(latest, reader.GetDateTime(2).Ticks); }
                    stamp = count + ":" + latest;
                }
                else { foreach (string text in Events(root)) { var e = InstallHeader(text); if (e != null) result["install-events/" + e.EventId + ".json"] = e; } stamp = InstallEventStamp(root); }
                InstallChanges(root, long.MaxValue, out revision); return result;
            }
        }
        internal static Dictionary<string, string> InstallEventBodies(string root, IEnumerable<string> eventIds)
        {
            var keys = eventIds.Distinct().Select(id => "install-events/" + id + ".json").ToList();
            if (keys.Count > 100) throw new ArgumentException("At most 100 installation events per detail request");
            foreach (string key in keys) Safe(root, key);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (keys.Count == 0) return result;
            var cfg = Config(root);
            if (cfg.DatabaseOnly)
            {
                var args = new List<object> { cfg.RepositoryId }; args.AddRange(keys.Cast<object>());
                using (var db = Open(Connection(cfg))) using (var cmd = Command(db, null, "SELECT document_key,body FROM uda_documents WHERE repository_id=@p0 AND document_key IN (" + string.Join(",", Enumerable.Range(1, keys.Count).Select(i => "@p" + i)) + ")", args.ToArray()))
                using (var reader = cmd.ExecuteReader()) while (reader.Read()) result[reader.GetString(0)] = reader.GetString(1);
            }
            else foreach (string key in keys) { string body = Read(root, key); if (body != null) result[key] = body; }
            return result;
        }
        private static Dictionary<string, string> Snapshot(string root)
        {
            var rows = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string file in MetadataFiles(root)) rows[Path.GetFileName(file)] = File.ReadAllText(file);
            string events = Path.Combine(root, "install-events"); if (Directory.Exists(events)) foreach (string file in Directory.GetFiles(events, "*.json")) rows["install-events/" + Path.GetFileName(file)] = File.ReadAllText(file);
            string logs = Path.Combine(root, "server-logs"); if (Directory.Exists(logs)) foreach (string file in Directory.GetFiles(logs, "*.json", SearchOption.AllDirectories))
            { string body = File.ReadAllText(file); string key = Json().Deserialize<StorageLogBlock>(body).Key; Safe(root, key); rows[key] = body; }
            return rows;
        }
        // ai-subscription.json is deployment configuration (including a machine-bound
        // encrypted API key), not a business document. Never migrate or clean it up.
        private static IEnumerable<string> AiBusinessFiles(string root)
        {
            return Directory.GetFiles(root, "ai-*.json").Where(file =>
                !string.Equals(Path.GetFileName(file), "ai-subscription.json", StringComparison.OrdinalIgnoreCase));
        }
        private static IEnumerable<string> MetadataFiles(string root)
        { return Directory.GetFiles(root, "repo.*.json").Where(p => Path.GetFileName(p) != "repo.organization-journal.json").Concat(Directory.GetFiles(root, "repo.deleted.txt")); }
        public static void TestConnection(string connection) { using (var db = Open(connection)) { using (var cmd = Command(db, null, "SELECT 1")) cmd.ExecuteScalar(); } }
        internal static string SetupConnection(string root) { var cfg = Config(root); return cfg.Enabled ? Connection(cfg) : ""; }
        internal static bool SetupConnectionNeedsRebind(string root)
        {
            var cfg = Config(root);
            if (!cfg.Enabled) return false;
            try { Connection(cfg); return false; }
            catch (CryptographicException) { return true; }
            catch (FormatException) { return true; }
        }
        // Used only after the administrator explicitly enters a connection in setup.
        // Validate the exact existing dataset before changing any on-disk configuration.
        internal static void RebindSetupConnection(string root, string connection)
        {
            lock (Gate(root)) {
                var cfg = Config(root);
                if (!cfg.Enabled) throw new InvalidOperationException("仓库尚未启用数据库，请先执行首次迁移。");
                ValidateMigratedDatabase(cfg, connection);
                cfg.ProtectedConnection = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(connection), null, DataProtectionScope.LocalMachine));
                Atomic(ConfigPath(root), Json().Serialize(cfg), true);
            }
        }

        internal static void TestSetupConnection(string connection)
        {
            using (var db = Open(connection))
            {
                Schema(db);
                using (var tx = db.BeginTransaction())
                {
                    string id = Guid.NewGuid().ToString("N"), key = "repo.setup-probe.json";
                    Put(db, tx, id, key, "{}", false);
                    Put(db, tx, id, key, "{\"ok\":true}", false);
                    if (Get(db, tx, id, key) != "{\"ok\":true}") throw new IOException("数据库安装读写验证失败");
                    using (var cmd = Command(db, tx, "DELETE FROM uda_documents WHERE repository_id=@p0 AND document_key=@p1", id, key)) cmd.ExecuteNonQuery();
                    tx.Rollback();
                }
            }
        }
        public static void Reconnect(string root, string connection)
        {
            lock (Gate(root))
            {
                var cfg = Config(root); if (!cfg.Enabled) throw new InvalidOperationException("Migrate first before updating a database connection");
                using (var db = Open(connection)) if (Get(db, null, cfg.RepositoryId, "repo.manifest.json") == null) throw new InvalidOperationException("Existing repository dataset not found in target database");
                cfg.ProtectedConnection = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(connection), null, DataProtectionScope.LocalMachine));
                Atomic(ConfigPath(root), Json().Serialize(cfg), true); Sync(root);
            }
        }
        public static int Migrate(string root, string connection)
        {
            lock (Gate(root))
            {
                if (Config(root).Enabled) throw new InvalidOperationException("Already external; return to local before changing database");
                OfflineRepoGenerator.RecoverOrganization(root);
                var cfg = Config(root); cfg.RepositoryId = Guid.NewGuid().ToString("N"); var rows = Snapshot(root);
                string catalog;
                if (!rows.TryGetValue("repo.manifest.json", out catalog)) throw new InvalidOperationException("Repository catalog is not ready");
                var manifest = OfflineRepoGenerator.DeserializeManifest(catalog);
                if (manifest.SchemaVersion != OfflineRepoManifest.CurrentSchema || !string.Equals(manifest.IntegrityHash, OfflineRepoGenerator.ComputeIntegrityHash(manifest), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Catalog integrity validation failed; migration not activated");
                using (var db = Open(connection))
                {
                    Schema(db);
                    using (var tx = db.BeginTransaction(IsolationLevel.Serializable))
                    {
                        foreach (var row in rows) { Put(db, tx, cfg.RepositoryId, row.Key, row.Value, false); if (Get(db, tx, cfg.RepositoryId, row.Key) != row.Value) throw new IOException("Migration read-back mismatch"); }
                        ImportLogs(root, db, tx, cfg.RepositoryId, true);
                        tx.Commit();
                    }
                }
                cfg.ProtectedConnection = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(connection), null, DataProtectionScope.LocalMachine)); cfg.Enabled = true;
                Atomic(ConfigPath(root), Json().Serialize(cfg), true); return rows.Count;
            }
        }
        public static int Sync(string root)
        {
            lock (Gate(root))
            {
                var cfg = Config(root); if (!cfg.Enabled) return 0;
                string directory = Path.Combine(root, ".uda-outbox"); Directory.CreateDirectory(directory);
                int count = 0;
                using (var db = Open(Connection(cfg))) foreach (string file in Directory.GetFiles(directory, "*.json"))
                {
                    var p = Json().Deserialize<StoragePending>(File.ReadAllText(file));
                    if (p.Deleted) { CheckSchema(db, null); using (var c = Command(db, null, "DELETE FROM uda_documents WHERE repository_id=@p0 AND document_key=@p1", cfg.RepositoryId, p.Key)) c.ExecuteNonQuery(); if (File.Exists(Safe(root, p.Key))) File.Delete(Safe(root, p.Key)); }
                    else { Put(db, null, cfg.RepositoryId, p.Key, p.Text, p.Immutable); if (!cfg.DatabaseOnly) Atomic(Safe(root, p.Key), p.Immutable ? Get(db, null, cfg.RepositoryId, p.Key) : p.Text); }
                    File.Delete(file); count++;
                }
                SyncLogs(root);
                string ignored; Errors.TryRemove(Path.GetFullPath(root), out ignored); return count;
            }
        }
        public static void UseLocal(string root)
        {
            lock (Gate(root))
            {
                var cfg = Config(root); if (cfg.DatabaseOnly) throw new InvalidOperationException("本版本要求 MySQL，禁止切换回文件存储；如需回滚请先导出数据库并使用旧版服务"); if (!cfg.Enabled) return; Sync(root);
                using (var db = Open(Connection(cfg))) { ImportLogs(root, db, null, cfg.RepositoryId, false, true); foreach (var row in All(db, cfg.RepositoryId)) Atomic(Safe(root, row.Key), row.Value, row.Key.StartsWith("server-logs/", StringComparison.Ordinal)); }
                cfg.Enabled = false; Atomic(ConfigPath(root), Json().Serialize(cfg), true);
            }
        }
        public static System.Threading.Timer StartRecovery(string root)
        {
            if (Config(root).DatabaseOnly) _databaseRuntimeActive = true;
            int busy = 0;
            return new System.Threading.Timer(delegate {
                if (System.Threading.Interlocked.Exchange(ref busy, 1) != 0) return;
                try { if (!string.IsNullOrWhiteSpace(root) && Config(root).Enabled) Sync(root); }
                catch (Exception ex) { Error(root, ex); }
                finally { System.Threading.Interlocked.Exchange(ref busy, 0); }
            }, null, 30000, 30000);
        }
        private static volatile bool _databaseRuntimeActive;
        public static bool DatabaseRuntimeActive { get { return _databaseRuntimeActive; } }
        public static void RequireMigratedDatabase(string root)
        {
            var cfg = Config(root);
            if (!cfg.Enabled) throw new InvalidOperationException("拒绝升级：现有数据仍使用文件存储。请先使用 5.2.12 的独立数据库迁移工具完成 MySQL 迁移，再运行本安装程序。");
            ValidateMigratedDatabase(cfg, Connection(cfg));
        }
        private static void ValidateMigratedDatabase(ServerStorageConfig cfg, string connection)
        {
            using (var db = Open(connection))
            {
                string text = Get(db, null, cfg.RepositoryId, "repo.manifest.json");
                if (text == null) throw new InvalidOperationException("MySQL 中缺少现有仓库数据集，拒绝升级");
                var manifest = OfflineRepoGenerator.DeserializeManifest(text);
                if (!string.Equals(manifest.IntegrityHash, OfflineRepoGenerator.ComputeIntegrityHash(manifest), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("MySQL 仓库完整性校验失败，拒绝升级");
                using (var tx = db.BeginTransaction())
                {
                    string probe = "repo.upgrade-probe." + Guid.NewGuid().ToString("N") + ".json";
                    Put(db, tx, cfg.RepositoryId, probe, "{\"probe\":1}", false);
                    Put(db, tx, cfg.RepositoryId, probe, "{}", false);
                    if (Get(db, tx, cfg.RepositoryId, probe) != "{}") throw new IOException("MySQL 升级预检读写校验失败");
                    using (var cmd = Command(db, tx, "DELETE FROM uda_documents WHERE repository_id=@p0 AND document_key=@p1", cfg.RepositoryId, probe)) cmd.ExecuteNonQuery();
                    tx.Rollback();
                }
            }
        }
        // Caller must quiesce the server; this never deletes repository files or enables database-only mode.
        public static string PrepareCompleteMigration(string root, string connection, bool repair)
        {
            lock (Gate(root))
            {
                root = Path.GetFullPath(root);
                if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("仓库目录不存在或是重解析点");
                var cfg = Config(root);
                string target = cfg.Enabled ? Connection(cfg) : connection;
                var report = new StringBuilder();
                var rows = Snapshot(root);
                foreach (string file in AiBusinessFiles(root)) rows["repo." + Path.GetFileName(file)] = File.ReadAllText(file);
                using (var db = Open(target))
                {
                    long packet; using (var cmd = Command(db, null, "SELECT @@max_allowed_packet")) packet = Convert.ToInt64(cmd.ExecuteScalar());
                    long required = Math.Max(262144L, rows.Values.Select(x => (long)Encoding.UTF8.GetByteCount(x) * 2 + 65536).DefaultIfEmpty(0).Max());
                    report.AppendLine("连接/TLS/认证：通过；max_allowed_packet=" + packet + "；本次最低建议=" + required);
                    if (packet < required) throw new IOException("max_allowed_packet 不足：当前 " + packet + "，至少需要 " + required + " 字节。请 DBA 调整后重新连接；未启用迁移。");
                    if (repair) Schema(db); else { using (var cmd = Command(db, null, "SELECT VERSION()")) report.AppendLine("数据库版本：" + Convert.ToString(cmd.ExecuteScalar())); }
                    if (cfg.Enabled) RequireMigratedDatabase(root);
                }
                report.AppendLine("本地元数据/安装事件/归档日志：" + rows.Count + " 项；服务与审计原始日志：" + LogFiles().Count() + " 个文件");
                if (!repair) return report + "检查完成；尚未执行建表、补迁、索引上传或日志归档。点击完整补迁后才可判定升级条件。";
                if (!cfg.Enabled) Migrate(root, target);
                var catalog = OfflineRepoGenerator.TryLoadManifest(root);
                if (catalog == null) throw new IOException("缺少有效仓库目录");
                // Build missing downloads before upgrading; keep the raw folders intact.
                if (!Config(root).DatabaseOnly) OfflineRepoGenerator.PrepareDownloadPackages(root, null);
                using (var repository = OfflineRepo.Open(root, null))
                    System.Threading.Tasks.Parallel.ForEach(catalog.Entries, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = ClientScanParallelism.LogicalProcessors() }, entry => {
                        if (!repository.VerifyFile(entry.RelativePath)) throw new IOException("驱动 INF 校验失败：" + entry.RelativePath);
                    });
                report.AppendLine("仓库目录及驱动 INF 完整性、下载包生成：通过");
                Sync(root); cfg = Config(root);
                using (var db = Open(Connection(cfg))) using (var tx = db.BeginTransaction())
                {
                    foreach (var row in rows)
                    {
                        if (Get(db, tx, cfg.RepositoryId, row.Key) != null) continue;
                        Put(db, tx, cfg.RepositoryId, row.Key, row.Value, false);
                        if (Get(db, tx, cfg.RepositoryId, row.Key) != row.Value) throw new IOException("补迁读回不一致：" + row.Key);
                    }
                    tx.Commit();
                }
                ActivateDatabaseOnly(root, true);
                RequireMigratedDatabase(root);
                report.AppendLine("已完成：建表、权限读写删除验证、待写队列、遗漏业务文档、上传与来源记录、AI 数据、安装日志、服务与审计日志、硬件索引分块及读回校验。");
                report.AppendLine("数据库已有记录优先；本地原件保留。仍保持原存储兼容模式，正式升级时再清理。请在升级前再次执行完整补迁以包含期间新增数据。");
                return report.ToString();
            }
        }
        public static void ActivateDatabaseOnly(string root, bool prepareOnly = false)
        {
            lock (Gate(root))
            {
                RequireMigratedDatabase(root); Sync(root);
                var preflightConfig = Config(root);
                if (!preflightConfig.DatabaseOnly) WebAdminController.MigrateUploadMetadata(root);
                using (var db = Open(Connection(preflightConfig)))
                {
                    foreach (string file in MetadataFiles(root).Concat(AiBusinessFiles(root)).ToArray())
                    {
                        string key = Path.GetFileName(file); if (key.StartsWith("ai-", StringComparison.Ordinal)) key = "repo." + key;
                        // A leftover file is never allowed to overwrite the authoritative SQL row.
                        // Only the first transition imports documents that have never reached SQL.
                        if (Get(db, null, preflightConfig.RepositoryId, key) != null) continue;
                        if (preflightConfig.DatabaseOnly) throw new IOException("数据库缺少本地残留元数据的对应项，已保留文件供核查：" + key);
                        string body = File.ReadAllText(file); Put(db, null, preflightConfig.RepositoryId, key, body, false);
                        if (Get(db, null, preflightConfig.RepositoryId, key) != body) throw new IOException("新增业务数据读回校验失败，未清理本地数据");
                    }
                }
                string index = Path.Combine(root, "repo.udadb");
                var currentManifest = OfflineRepoGenerator.TryLoadManifest(root);
                if (!File.Exists(index) || !string.Equals(OfflineRepoGenerator.Sha256File(index), currentManifest.IndexDbHash, StringComparison.OrdinalIgnoreCase))
                {
                    var rebuilt = new DriverIndex(index); rebuilt.ReplaceAll(currentManifest.Entries.Select(OfflineRepoGenerator.ToPortableRecord)); rebuilt.Commit();
                    currentManifest.IndexDbHash = OfflineRepoGenerator.Sha256File(index); currentManifest.IntegrityHash = OfflineRepoGenerator.ComputeIntegrityHash(currentManifest);
                    string catalog = OfflineRepoGenerator.SerializeManifest(currentManifest);
                    File.WriteAllText(Path.Combine(root, "repo.manifest.json"), catalog, new UTF8Encoding(false));
                }
                if (File.Exists(index))
                {
                    var manifest = currentManifest;
                    if (manifest == null || !string.Equals(OfflineRepoGenerator.Sha256File(index), manifest.IndexDbHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("本地匹配索引与目录哈希不一致，未清理本地数据");
                    using (var stream = File.OpenRead(index))
                    {
                        string indexHash = OfflineRepoGenerator.Sha256File(index), prefix = "repo.index-block." + indexHash.Substring(0, 32) + ".";
                        var indexConfig = Config(root);
                        using (var db = Open(Connection(indexConfig))) using (var tx = db.BeginTransaction())
                        {
                            byte[] bytes = new byte[65536]; int n, offset = 0;
                            while ((n = stream.Read(bytes, 0, bytes.Length)) > 0)
                            {
                                string key = prefix + (offset++).ToString("D12") + ".json", body = Json().Serialize(new { data = Convert.ToBase64String(bytes, 0, n) });
                                Put(db, tx, indexConfig.RepositoryId, key, body, false);
                                if (Get(db, tx, indexConfig.RepositoryId, key) != body) throw new IOException("硬件索引分块读回校验失败");
                            }
                            Put(db, tx, indexConfig.RepositoryId, "repo.index-state.json", Json().Serialize(new { length = stream.Length, blocks = offset, sha256 = indexHash, prefix = prefix }), false);
                            Put(db, tx, indexConfig.RepositoryId, "repo.manifest.json", OfflineRepoGenerator.SerializeManifest(currentManifest), false);
                            using (var cmd = Command(db, tx, "DELETE FROM uda_documents WHERE repository_id=@p0 AND document_key LIKE 'repo.index-block.%' AND document_key NOT LIKE @p1", indexConfig.RepositoryId, prefix + "%")) cmd.ExecuteNonQuery();
                            tx.Commit();
                        }
                        foreach (string file in Directory.GetFiles(root, "repo.index-block.*.json")) File.Delete(file);
                        string oldState = Path.Combine(root, "repo.index-state.json"); if (File.Exists(oldState)) File.Delete(oldState);
                    }
                }
                // Full read-back must succeed before deleting local metadata or binary index.
                var cfg = Config(root);
                using (var db = Open(Connection(cfg)))
                {
                    foreach (string file in MetadataFiles(root)) if (Get(db, null, cfg.RepositoryId, Path.GetFileName(file)) == null) throw new IOException("数据库缺少已迁移元数据，未清理本地数据");
                    ImportLogs(root, db, null, cfg.RepositoryId, false, true);
                    string cursors = Path.Combine(root, ".uda-log-cursors");
                    if (!preflightConfig.DatabaseOnly && Directory.Exists(cursors)) foreach (string file in Directory.GetFiles(cursors, "*.json")) Put(db, null, cfg.RepositoryId, "repo.log-cursor." + Path.GetFileName(file), File.ReadAllText(file), false);
                }
                if (prepareOnly) return; // Staged migration keeps the old server and all local originals usable.
                cfg.DatabaseOnly = true; Atomic(ConfigPath(root), Json().Serialize(cfg), true);
                foreach (string file in MetadataFiles(root).Concat(AiBusinessFiles(root)).ToArray()) File.Delete(file);
                if (File.Exists(index)) File.Delete(index);
                string cursorDirectory = Path.Combine(root, ".uda-log-cursors"); if (Directory.Exists(cursorDirectory)) foreach (string file in Directory.GetFiles(cursorDirectory, "*.json")) File.Delete(file);
                string events = Path.Combine(root, "install-events"); if (Directory.Exists(events)) foreach (string file in Directory.GetFiles(events, "*.json"))
                { string key = "install-events/" + Path.GetFileName(file); if (Read(root, key) != File.ReadAllText(file)) throw new IOException("安装事件读回校验失败"); File.Delete(file); }
                string archives = Path.Combine(root, "server-logs"); if (Directory.Exists(archives)) foreach (string file in Directory.GetFiles(archives, "*.json", SearchOption.AllDirectories))
                { string body = File.ReadAllText(file); var block = Json().Deserialize<StorageLogBlock>(body); string stored = Read(root, block.Key); if (stored == null) throw new IOException("数据库缺少日志归档"); var latest = Json().Deserialize<StorageLogBlock>(stored); byte[] localBytes = Convert.FromBase64String(block.Data), sqlBytes = Convert.FromBase64String(latest.Data); if (sqlBytes.Length < localBytes.Length || !sqlBytes.Take(localBytes.Length).SequenceEqual(localBytes)) throw new IOException("日志归档读回校验失败"); File.Delete(file); }
                foreach (string pattern in new[] { "repo.udadb.*", "repo.manifest.json.*" }) foreach (string file in Directory.GetFiles(root, pattern)) if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) File.Delete(file);
                SyncLogs(root);
            }
        }
        public static void RestoreFileCompatibility(string root)
        { lock (Gate(root)) { DatabaseArchiveRepository.ExpandForMaintenance(root); var cfg = Config(root); cfg.DatabaseOnly = false; Atomic(ConfigPath(root), Json().Serialize(cfg), true); } }
        public static int[] ImportHistoricalEvents(string root, IList<DriverInstallEvent> entries)
        {
            lock (Gate(root))
            {
                int added = 0, skipped = 0; var cfg = Config(root); var json = Json(); var inserted = new List<DriverInstallEvent>();
                if (cfg.Enabled)
                {
                    using (var db = Open(Connection(cfg))) using (var tx = db.BeginTransaction())
                    {
                        foreach (var entry in entries)
                        {
                            string key = "install-events/" + entry.EventId + ".json";
                            if (Get(db, tx, cfg.RepositoryId, key) != null) { skipped++; continue; }
                            Put(db, tx, cfg.RepositoryId, key, json.Serialize(entry), true); added++; inserted.Add(entry);
                        }
                        tx.Commit();
                    }
                    foreach (var entry in inserted) ObserveInstallEvent(root, "install-events/" + entry.EventId + ".json", json.Serialize(entry), true);
                }
                else foreach (var entry in entries)
                {
                    string key = "install-events/" + entry.EventId + ".json";
                    if (File.Exists(Safe(root, key))) { skipped++; continue; }
                    Write(root, key, json.Serialize(entry), true); added++;
                }
                return new[] { added, skipped };
            }
        }
        public static Dictionary<string, string> RecentDocuments(string root, string prefix, int limit)
        {
            limit = Math.Max(1, Math.Min(100, limit)); var cfg = Config(root);
            if (!cfg.Enabled) return Directory.GetFiles(root, prefix + "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(limit).ToDictionary(Path.GetFileName, File.ReadAllText);
            var result = new Dictionary<string, string>();
            using (var db = Open(Connection(cfg)))
            {
                CheckSchema(db, null);
                using (var cmd = Command(db, null, "SELECT document_key,body FROM uda_documents WHERE repository_id=@p0 AND document_key LIKE @p1 ORDER BY updated_utc DESC LIMIT " + limit.ToString(System.Globalization.CultureInfo.InvariantCulture), cfg.RepositoryId, prefix + "%"))
                using (var reader = cmd.ExecuteReader()) while (reader.Read()) result[reader.GetString(0)] = reader.GetString(1);
            }
            return result;
        }
        public static Dictionary<string, string> Documents(string root, string prefix)
        {
            var cfg = Config(root); if (!cfg.Enabled) return new Dictionary<string, string>();
            using (var db = Open(Connection(cfg))) return All(db, cfg.RepositoryId, prefix);
        }
        public static void RemoveUploadState(string root, string batch)
        {
            Guid id; if (!Guid.TryParseExact(batch, "N", out id)) throw new ArgumentException("Invalid upload batch");
            var cfg = Config(root); if (!cfg.Enabled) return;
            using (var db = Open(Connection(cfg))) using (var cmd = Command(db, null, "DELETE FROM uda_documents WHERE repository_id=@p0 AND document_key LIKE @p1", cfg.RepositoryId, "repo.upload." + id.ToString("N") + ".%")) cmd.ExecuteNonQuery();
        }

        // Fixed byte blocks preserve UTF-8 and multiline exceptions without large SQL packets.
        // Originals are the durable queue: advance cursors only after a successful SQL commit.
        internal static IEnumerable<KeyValuePair<string, string>> LogFiles()
        {
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string standard = TestLogDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UniversalDriverAssistant", "Logs");
            foreach (string dir in new[] { standard, string.IsNullOrEmpty(ServiceEventLog.LogFilePath) ? null : Path.GetDirectoryName(ServiceEventLog.LogFilePath) }.Where(x => x != null).Distinct(StringComparer.OrdinalIgnoreCase))
                if (Directory.Exists(dir)) foreach (string file in Directory.GetFiles(dir, "*.log")) files[file] = "service";
            string audit = WebAdminController.ResolveAuditDirectory();
            if (Directory.Exists(audit)) foreach (string file in Directory.GetFiles(audit, "admin-*.jsonl")) files[file] = "audit";
            return files.Select(x => new KeyValuePair<string, string>(x.Key, x.Value));
        }
        private static string LogSource(string file)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(file).ToUpperInvariant()))).Replace("-", "").ToLowerInvariant(); }
        private static string LogCursorPath(string root, string source) { return Path.Combine(root, ".uda-log-cursors", source + ".json"); }
        private static void ImportLogs(string root, DbConnection db, DbTransaction tx, string id, bool all, bool unlimited = false)
        {
            foreach (var file in LogFiles()) ImportLog(root, db, tx, id, file.Key, file.Value, all, unlimited);
        }
        private static void ImportLog(string root, DbConnection db, DbTransaction tx, string id, string file, string kind, bool all, bool unlimited)
        {
            string source = LogSource(file), checkpoint = LogCursorPath(root, source);
            var info = new FileInfo(file);
            bool databaseOnly = Config(root).DatabaseOnly;
            string cursorKey = "repo.log-cursor." + source + ".json";
            string cursorText = databaseOnly ? Get(db, tx, id, cursorKey) : File.Exists(checkpoint) ? File.ReadAllText(checkpoint) : null;
            var cursor = cursorText == null ? new StorageLogCursor() : Json().Deserialize<StorageLogCursor>(cursorText);
            Action<StorageLogCursor> saveCursor = value => { if (databaseOnly) Put(db, tx, id, cursorKey, Json().Serialize(value), false); else Atomic(checkpoint, Json().Serialize(value), true); };
            if (cursor.Generation == null || info.Length < cursor.Length || cursor.Created != info.CreationTimeUtc.Ticks) { cursor = new StorageLogCursor { Generation = Guid.NewGuid().ToString("N"), Created = info.CreationTimeUtc.Ticks }; saveCursor(cursor); }
            if (info.Length == 0 || (!databaseOnly && !all && info.Length == cursor.Length && info.LastWriteTimeUtc.Ticks == cursor.Modified)) return;
            const int size = 65536;
            long start = all ? 0 : (cursor.Length / size) * size;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long length = stream.Length; stream.Position = Math.Min(start, length);
                byte[] buffer = new byte[size]; int blocks = 0;
                while (stream.Position < length && (all || unlimited || blocks < 128))
                {
                    long offset = stream.Position; int count = 0, wanted = (int)Math.Min(size, length - offset);
                    while (count < wanted) { int n = stream.Read(buffer, count, wanted - count); if (n == 0) throw new IOException("Log changed during database synchronization"); count += n; }
                    string key = "server-logs/" + kind + "/" + source + "/" + cursor.Generation + "/" + offset.ToString("D20", System.Globalization.CultureInfo.InvariantCulture) + ".json";
                    string body = Json().Serialize(new StorageLogBlock { Key = key, Kind = kind, FileName = Path.GetFileName(file), Source = source, Offset = offset, Data = Convert.ToBase64String(buffer, 0, count) });
                    Put(db, tx, id, key, body, false);
                    if (all && Get(db, tx, id, key) != body) throw new IOException("Log migration read-back mismatch");
                    blocks++;
                }
                // Migration transaction is committed by the caller; do not checkpoint uncommitted data.
                if (tx == null) saveCursor(new StorageLogCursor { Generation = cursor.Generation, Created = cursor.Created, Length = stream.Position, Modified = stream.Position == length ? info.LastWriteTimeUtc.Ticks : 0 });
            }
            if (databaseOnly && tx == null)
            {
                // Audit and service-event writers use these locks; an active L writer refuses
                // exclusive access and remains a bounded transient buffer until its next rotation.
                lock (typeof(WebAdminController)) ServiceEventLog.Maintain(delegate {
                    var latest = new FileInfo(file); var completed = Json().Deserialize<StorageLogCursor>(Get(db, null, id, cursorKey));
                    if (completed.Length != latest.Length || completed.Modified != latest.LastWriteTimeUtc.Ticks) return;
                    try { using (var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { exclusive.SetLength(0); exclusive.Flush(true); } }
                    catch (IOException) { return; }
                    saveCursor(new StorageLogCursor { Generation = Guid.NewGuid().ToString("N"), Created = latest.CreationTimeUtc.Ticks, Length = 0, Modified = 0 });
                    File.Delete(file);
                });
            }
        }
        public static void SyncLogs(string root)
        {
            lock (Gate(root))
            {
                var cfg = Config(root); if (!cfg.Enabled) return;
                try { using (var db = Open(Connection(cfg))) ImportLogs(root, db, null, cfg.RepositoryId, false); string ignored; Errors.TryRemove(Path.GetFullPath(root), out ignored); }
                catch (Exception ex) { Error(root, ex); throw; }
            }
        }
        public static List<string> AuditLogLines(string root)
        {
            lock (Gate(root))
            {
                var rows = new Dictionary<string, string>(StringComparer.Ordinal);
                string directory = Path.Combine(root, "server-logs", "audit");
                if (Directory.Exists(directory)) foreach (string file in Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories)) { string body = File.ReadAllText(file); rows[Json().Deserialize<StorageLogBlock>(body).Key] = body; }
                var cfg = Config(root);
                if (cfg.Enabled) try { using (var db = Open(Connection(cfg))) rows = All(db, cfg.RepositoryId, "server-logs/audit/"); } catch (Exception ex) { Error(root, ex); if (cfg.DatabaseOnly) throw; }
                var result = new List<string>();
                foreach (var group in rows.OrderBy(x => x.Key, StringComparer.Ordinal).GroupBy(x => x.Key.Substring(0, x.Key.LastIndexOfAny(new[] { '/', '\\' }))))
                {
                    using (var bytes = new MemoryStream())
                    {
                        foreach (var row in group) { var block = Json().Deserialize<StorageLogBlock>(row.Value); byte[] data = Convert.FromBase64String(block.Data); bytes.Write(data, 0, data.Length); }
                        result.AddRange(Encoding.UTF8.GetString(bytes.ToArray()).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
                    }
                }
                return result;
            }
        }
        // Full catalog rebuild must never discard server-owned state when swapping directories.
        public static void PreserveState(string source, string destination)
        {
            if (!Directory.Exists(source)) return;
            foreach (string file in Directory.GetFiles(source))
            {
                string name = Path.GetFileName(file);
                if (name.StartsWith("repo.manifest", StringComparison.OrdinalIgnoreCase) || name.StartsWith("repo.udadb", StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(Path.Combine(destination, name))) { File.Copy(file, Path.Combine(destination, name)); if (name == ".uda-storage.json") SecureFile(Path.Combine(destination, name)); }
            }
            foreach (string directory in Directory.GetDirectories(source))
                if (!new[] { "packages", ".uda-downloads" }.Contains(Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase)) CopyStateTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
        public static void CopyStateTree(string source, string destination)
        {
            if (!Directory.Exists(source)) return;
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("State directory is a reparse point");
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source)) { if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("State file is a reparse point"); File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true); }
            foreach (string dir in Directory.GetDirectories(source)) CopyStateTree(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }
}
