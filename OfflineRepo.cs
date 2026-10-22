// OfflineRepo.cs - Phase 3A: self-contained, removable offline driver repository.
//
// Built on the Phase 1 index core (InfParser / DriverIndex / MatchRanker) and the Phase 2 install
// seams (IAdminChecker / ISignatureVerifier / IPnpInstallRunner / ISystemOps / IHardwareCollector).
// No third-party dependencies; compiled by Build.ps1. C# 5.0 syntax (in-box CSharpCodeProvider).
//
// A repository is a directory containing:
//   * the driver packages (copied in, so the repo is portable / removable),
//   * repo.manifest.json   - the trust anchor: schema/version, OS+arch+machine metadata, one entry per
//                            INF (relative path, SHA-256, signature status, provider/class/guid, driver
//                            ver, hardware/compatible IDs, decorations, size), conflict/duplicate warnings,
//                            the index DB hash, and a canonical integrity hash of the manifest itself,
//   * repo.udadb           - a portable binary index DB (records carry RELATIVE paths) for fast loading.
//
// The offline main program trusts ONLY the repo root + the manifest. It verifies the manifest integrity
// hash, verifies the index DB hash against the manifest (rebuilding from the manifest on mismatch), then
// uses an in-memory MatchRanker for fast scan matching. Before installing any INF it re-checks the file
// SHA-256 against the manifest entry (defense in depth on top of the install signature gate).
//
// Generation is atomic (stage -> atomic swap with .bak/.old recovery) and supports incremental updates
// (added/modified/removed diff vs. the previous manifest) plus conflict/duplicate detection.
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace UDA
{
    // =================================================================================
    //  Manifest data model
    // =================================================================================

    /// <summary>One INF in the offline repository, as recorded in the manifest.</summary>
    public sealed class OfflineRepoEntry
    {
        public string RelativePath = "";        // relative to repo root, e.g. "packages/0-src/net.inf"
        public string SourceName = "";          // the imported package this INF came from
        public string ContentHash = "";         // SHA-256 hex of the INF file bytes
        public string SignatureStatus = "";     // trusted | untrusted | unknown | deferred
        public string Provider = "";
        public string Manufacturer = "";
        public string Class = "";
        public string ClassGuid = "";
        public string DriverVerDate = "";
        public string DriverVerVersion = "";
        public List<string> Decorations = new List<string>();
        public List<string> HardwareIds = new List<string>();
        public List<string> CompatibleIds = new List<string>();
        // Per-model entries parsed from the decorated [Models.*] sections. Required by
        // InfApplicability's device-ID gate: a record with only aggregated IDs is rejected as
        // NoMatchingModelEntry, so the manifest MUST carry these for the engine to work.
        // Null (absent) on legacy manifests written before this field existed; Open() backfills
        // from the INF itself in that case. Excluded from ComputeIntegrityHash: the entries are
        // derived from the INF whose ContentHash is already pinned above.
        public List<InfModelEntry> Models;
        public string PackageRoot = "", PackageHash = "";
        public string SourceIdentity = "";
        public long Size;
        public bool Duplicate;                  // same content hash as another entry
        public bool Conflict;                   // same canonical VEN/DEV, different content
    }

    /// <summary>Optional machine hints the repo was built for. Empty = universal.</summary>
    public sealed class OfflineRepoMachineHints
    {
        public string Manufacturer = "";
        public string Model = "";
        public string SystemSKU = "";
    }

    /// <summary>The trust anchor of an offline repository.</summary>
    public sealed class OfflineRepoManifest
    {
        public const int CurrentSchema = 1;
        public int SchemaVersion = CurrentSchema;
        public string GeneratorVersion = "3.0";
        public string CreatedAt = "";                 // ISO 8601, supplied by the caller
        public string OsArchitecture = "amd64";
        public int OsMajorVersion = 10;
        public string MachineManufacturer = "";
        public string MachineModel = "";
        public string MachineSku = "";
        public List<OfflineRepoEntry> Entries = new List<OfflineRepoEntry>();
        public List<string> HiddenPaths = new List<string>();
        public int FileCount;
        public long TotalBytes;
        public List<string> Warnings = new List<string>();  // duplicate / conflict notices
        public string IndexDbHash = "";              // SHA-256 of repo.udadb, anchoring the derived index
        public string IntegrityHash = "";            // SHA-256 over the canonical manifest (this field excluded)
    }

    /// <summary>Outcome of a generation / update pass.</summary>
    public sealed class OfflineRepoGenerationReport
    {
        public string RepoDir = "";
        public int Added, Modified, Removed, Unchanged;
        public int Duplicates, Conflicts;
        public int FileCount;
        public long TotalBytes;
        public string IndexDbHash = "";
        public string IntegrityHash = "";
        public List<string> Warnings = new List<string>();
        public bool Rebuilt;
    }

    /// <summary>One group of repo entries that share the same strict duplicate identity
    /// (driver version + normalized hardware/compatible IDs + decorations): the first entry is
    /// authoritative, the rest are exact duplicates of it.</summary>
    public sealed class OfflineRepoDuplicateGroup
    {
        public string Identity = "";
        public string DriverVerVersion = "";
        public string KeptRelativePath = "";
        public List<string> DuplicateRelativePaths = new List<string>();
    }

    /// <summary>Result of a duplicate audit (read-only) or cleanup (mutating) pass.</summary>
    public sealed class OfflineRepoDuplicateReport
    {
        public string RepoDir = "";
        public List<OfflineRepoDuplicateGroup> Groups = new List<OfflineRepoDuplicateGroup>();
        public int TotalCount;
        public int KeptCount;
        public int DuplicateCount;
        /// <summary>True when the cleanup actually rewrote the repository (audit-only stays false).</summary>
        public bool Cleaned;
        public List<string> Warnings = new List<string>();
    }

    // =================================================================================
    //  Generator (atomic, incremental, conflict/duplicate detection, integrity)
    // =================================================================================

    /// <summary>
    /// Builds a portable offline repository from one or more driver package directories. Each package is
    /// copied into the repo (so the repo is self-contained and removable), every INF is parsed with the
    /// Phase 1 InfParser, hashed (SHA-256), signature-checked, and recorded in the manifest. A portable
    /// index DB (repo.udadb, relative paths) is emitted and hash-pinned in the manifest. The whole write
    /// is staged and atomically swapped; a crash never leaves a half-written repo.
    /// </summary>
    public sealed class OfflineRepoGenerator
    {
        private readonly ISignatureVerifier _sig;
        private readonly L _logger;

        public OfflineRepoGenerator(ISignatureVerifier sig, L logger) { _sig = sig; _logger = logger; }

        public OfflineRepoGenerationReport Generate(string repoDir, IList<string> sourceDirs, OsTarget osTarget,
            OfflineRepoMachineHints hints, string createdAt, CancellationToken ct, IProgress<string> progress)
        {
            if (string.IsNullOrWhiteSpace(repoDir)) throw new ArgumentException("repoDir");
            repoDir = Path.GetFullPath(repoDir);
            if (osTarget == null) osTarget = new OsTarget();
            if (hints == null) hints = new OfflineRepoMachineHints();
            if (createdAt == null) createdAt = "";

            RecoverOrganization(repoDir);
            OfflineRepoManifest old = TryLoadManifest(repoDir);
            HashSet<string> deletedHashes = LoadDeletedHashes(repoDir);
            var report = new OfflineRepoGenerationReport { RepoDir = repoDir, Rebuilt = old == null };

            string staging = repoDir + ".staging-" + SafeSuffix();
            try
            {
                if (Directory.Exists(staging)) { try { Directory.Delete(staging, true); } catch { } }
                Directory.CreateDirectory(staging);
                var packagesDir = Path.Combine(staging, "packages");
                Directory.CreateDirectory(packagesDir);

                var entries = new List<OfflineRepoEntry>();
                var srcList = (sourceDirs ?? new string[0]).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < srcList.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    string src = Path.GetFullPath(srcList[i]);
                    string name = UniqueName(SafeName(src), usedNames);
                    string dest = Path.Combine(packagesDir, name);
                    if (progress != null) progress.Report("复制驱动源 (" + (i + 1) + "/" + srcList.Count + "): " + src);
                    // Incremental atomic rebuild: unchanged files are hard-linked from the current
                    // repository into the sibling staging tree.  Both names refer to the same disk
                    // clusters, so a 60 GB repository no longer needs another 60 GB just to append a
                    // small driver source. Changed/new files alone consume additional space. If a
                    // filesystem does not support hard links, CopyTree safely falls back to copying.
                    string oldPackage = Path.Combine(repoDir, "packages", name);
                    CopyTree(src, dest, Directory.Exists(oldPackage) ? oldPackage : null, ct, progress, i + 1, srcList.Count);
                    int parsed = 0;
                    foreach (var inf in EnumerateInfs(dest, ct))
                    {
                        ct.ThrowIfCancellationRequested();
                        var entry = BuildEntry(inf, staging, name, osTarget);
                        if (entry != null && deletedHashes.Contains(entry.ContentHash ?? ""))
                        { try { File.Delete(inf); } catch { } }
                        else if (entry != null) entries.Add(entry);
                        parsed++;
                        if (progress != null && (parsed == 1 || parsed % 25 == 0))
                            progress.Report("解析 INF：" + parsed + " 个（驱动源 " + (i + 1) + "/" + srcList.Count + "）");
                    }
                }

                if (progress != null) progress.Report("检查重复项与驱动冲突，共 " + entries.Count + " 个 INF");
                RemoveExactDuplicatesAndDetectConflicts(entries, report);

                var manifest = new OfflineRepoManifest
                {
                    CreatedAt = createdAt,
                    // The repository is a multi-platform catalog. Applicability is
                    // evaluated only when matching for a requesting client.
                    OsArchitecture = "multi",
                    OsMajorVersion = 0,
                    MachineManufacturer = hints.Manufacturer ?? "",
                    MachineModel = hints.Model ?? "",
                    MachineSku = hints.SystemSKU ?? "",
                    Entries = entries,
                    FileCount = entries.Count,
                    TotalBytes = entries.Sum(e => e.Size),
                    Warnings = new List<string>(report.Warnings)
                };

                // Portable index DB: records carry RELATIVE paths so the repo is movable across machines.
                if (progress != null) progress.Report("构建硬件 ID 倒排索引，共 " + entries.Count + " 个 INF");
                string dbPath = Path.Combine(staging, "repo.udadb");
                var idx = new DriverIndex(dbPath);
                idx.ReplaceAll(entries.Select(ToPortableRecord));
                idx.Commit();
                manifest.IndexDbHash = Sha256File(dbPath);

                manifest.IntegrityHash = ComputeIntegrityHash(manifest);
                File.WriteAllText(Path.Combine(staging, "repo.manifest.json"),
                    SerializeManifest(manifest), new UTF8Encoding(false));
                if (deletedHashes.Count > 0) File.WriteAllLines(Path.Combine(staging, "repo.deleted.txt"), deletedHashes.OrderBy(x => x).ToArray(), new UTF8Encoding(false));

                DiffAgainstOld(manifest, old, report);

                if (progress != null) progress.Report("原子提交新仓库");
                ServerStorage.RunExclusive(repoDir, delegate { ServerStorage.PreserveState(repoDir, staging); AtomicSwap(staging, repoDir); ServerStorage.Publish(repoDir, "repo.manifest.json"); });
                if (progress != null) progress.Report("仓库生成完成，共 " + entries.Count + " 个 INF");

                report.FileCount = manifest.FileCount;
                report.TotalBytes = manifest.TotalBytes;
                report.IndexDbHash = manifest.IndexDbHash;
                report.IntegrityHash = manifest.IntegrityHash;
                if (_logger != null) _logger.W("[INFO] 离线仓库生成 / Offline repo generated: " + repoDir +
                    " (新增 " + report.Added + "，修改 " + report.Modified + "，删除 " + report.Removed +
                    "，未变 " + report.Unchanged + "，重复 " + report.Duplicates + "，冲突 " + report.Conflicts + ")");
                return report;
            }
            catch
            {
                // Never leave a partial staging dir behind on failure.
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
                throw;
            }
        }

        /// <summary>
        /// Merge one changed/new source into an existing repository without staging or copying the
        /// rest of the repository.  The temporary delta contains only changedSource; after it is
        /// complete, its one package directory is swapped into packages/, then only the small index
        /// and manifest files are atomically replaced.  Extra disk usage is therefore bounded by the
        /// changed source, not by the size of the whole repository.  This works on FAT/exFAT as well
        /// as NTFS and deliberately does not depend on hard-link support.
        /// </summary>
        public OfflineRepoGenerationReport MergeSource(string repoDir, IList<string> allSources, string changedSource,
            OsTarget osTarget, OfflineRepoMachineHints hints, string createdAt, CancellationToken ct, IProgress<string> progress)
        {
            if (string.IsNullOrWhiteSpace(changedSource)) throw new ArgumentException("changedSource");
            repoDir = Path.GetFullPath(repoDir);
            changedSource = Path.GetFullPath(changedSource);
            OfflineRepoManifest old = TryLoadManifest(repoDir);
            if (old == null) return Generate(repoDir, allSources, osTarget, hints, createdAt, ct, progress);

            // Reproduce the stable package name that a full build would assign to this source.
            string desiredName = null;
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in allSources ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string full = Path.GetFullPath(raw);
                string n = UniqueName(SafeName(full), used);
                if (string.Equals(full.TrimEnd('\\', '/'), changedSource.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    desiredName = n;
            }
            if (string.IsNullOrEmpty(desiredName)) desiredName = UniqueName(SafeName(changedSource), used);

            string delta = repoDir + ".delta-" + SafeSuffix();
            string incoming = null, finalPackage = Path.Combine(repoDir, "packages", desiredName), backupPackage = null;
            string dbStage = Path.Combine(repoDir, "repo.udadb.merge-" + SafeSuffix());
            string manifestStage = Path.Combine(repoDir, "repo.manifest.json.merge-" + SafeSuffix());
            bool packageSwapped = false;
            try
            {
                if (progress != null) progress.Report("仅处理新增/变化的驱动源：" + changedSource);
                OfflineRepoGenerationReport deltaReport = Generate(delta, new[] { changedSource }, osTarget, hints, createdAt, ct, progress);
                OfflineRepoManifest add = TryLoadManifest(delta);
                if (add == null) throw new InvalidDataException("incremental manifest missing");
                HashSet<string> deletedHashes = LoadDeletedHashes(repoDir);
                foreach (OfflineRepoEntry blocked in add.Entries.Where(e => deletedHashes.Contains(e.ContentHash ?? "")).ToList())
                {
                    string blockedFile = SafeAbsolute(delta, blocked.RelativePath);
                    try { if (blockedFile != null && File.Exists(blockedFile)) File.Delete(blockedFile); } catch { }
                    add.Entries.Remove(blocked);
                }
                string generatedName = add.Entries.Count > 0 ? add.Entries[0].SourceName : SafeName(changedSource);
                incoming = Path.Combine(delta, "packages", generatedName);

                foreach (OfflineRepoEntry e in add.Entries)
                {
                    string prefix = "packages/" + generatedName + "/";
                    if (e.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        e.RelativePath = "packages/" + desiredName + "/" + e.RelativePath.Substring(prefix.Length);
                    e.SourceName = desiredName;
                }

                var mergedEntries = old.Entries
                    .Where(e => !string.Equals(e.SourceName, desiredName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                mergedEntries.AddRange(add.Entries);
                var report = new OfflineRepoGenerationReport { RepoDir = repoDir, Rebuilt = false };
                RemoveExactDuplicatesAndDetectConflicts(mergedEntries, report);
                var merged = new OfflineRepoManifest
                {
                    CreatedAt = createdAt ?? "", OsArchitecture = "multi", OsMajorVersion = 0,
                    MachineManufacturer = hints != null ? hints.Manufacturer ?? "" : "",
                    MachineModel = hints != null ? hints.Model ?? "" : "",
                    MachineSku = hints != null ? hints.SystemSKU ?? "" : "",
                    Entries = mergedEntries, FileCount = mergedEntries.Count, HiddenPaths = new List<string>(old.HiddenPaths ?? new List<string>()),
                    TotalBytes = mergedEntries.Sum(e => e.Size), Warnings = new List<string>(report.Warnings)
                };

                if (progress != null) progress.Report("合并增量索引（不会复制现有仓库）");
                var idx = new DriverIndex(dbStage);
                idx.ReplaceAll(mergedEntries.Select(ToPortableRecord));
                idx.Commit();
                merged.IndexDbHash = Sha256File(dbStage);
                merged.IntegrityHash = ComputeIntegrityHash(merged);
                File.WriteAllText(manifestStage, SerializeManifest(merged), new UTF8Encoding(false));

                Directory.CreateDirectory(Path.Combine(repoDir, "packages"));
                if (Directory.Exists(finalPackage))
                {
                    backupPackage = finalPackage + ".old-" + SafeSuffix();
                    Directory.Move(finalPackage, backupPackage);
                }
                if (Directory.Exists(incoming)) Directory.Move(incoming, finalPackage);
                else Directory.CreateDirectory(finalPackage);
                packageSwapped = true;

                string db = Path.Combine(repoDir, "repo.udadb");
                string mf = Path.Combine(repoDir, "repo.manifest.json");
                if (File.Exists(db)) File.Replace(dbStage, db, null); else File.Move(dbStage, db);
                if (File.Exists(mf)) File.Replace(manifestStage, mf, null); else File.Move(manifestStage, mf);
                ServerStorage.Publish(repoDir, "repo.manifest.json");
                if (backupPackage != null) { try { Directory.Delete(backupPackage, true); } catch { } }

                DiffAgainstOld(merged, old, report);
                report.FileCount = merged.FileCount; report.TotalBytes = merged.TotalBytes;
                report.IndexDbHash = merged.IndexDbHash; report.IntegrityHash = merged.IntegrityHash;
                if (progress != null) progress.Report("增量合并完成：新增 " + report.Added + "，修改 " + report.Modified + "，删除 " + report.Removed);
                return report;
            }
            catch
            {
                if (packageSwapped)
                {
                    try { if (Directory.Exists(finalPackage)) Directory.Delete(finalPackage, true); } catch { }
                    try { if (!string.IsNullOrEmpty(backupPackage) && Directory.Exists(backupPackage)) Directory.Move(backupPackage, finalPackage); } catch { }
                }
                throw;
            }
            finally
            {
                try { if (Directory.Exists(delta)) Directory.Delete(delta, true); } catch { }
                try { if (File.Exists(dbStage)) File.Delete(dbStage); } catch { }
                try { if (File.Exists(dbStage + ".tmp")) File.Delete(dbStage + ".tmp"); } catch { }
                try { if (File.Exists(dbStage + ".bak")) File.Delete(dbStage + ".bak"); } catch { }
                try { if (File.Exists(manifestStage)) File.Delete(manifestStage); } catch { }
            }
        }

        /// <summary>Re-verify an existing repository: manifest integrity hash, index DB hash, and every
        /// file's SHA-256 against its manifest entry. Returns the list of problems found (empty = OK).</summary>
        public List<string> Verify(string repoDir, CancellationToken ct)
        {
            var problems = new List<string>();
            repoDir = Path.GetFullPath(repoDir);
            string manifestPath = Path.Combine(repoDir, "repo.manifest.json");
            if (!File.Exists(manifestPath)) { problems.Add("manifest missing"); return problems; }
            OfflineRepoManifest manifest;
            try { manifest = DeserializeManifest(File.ReadAllText(manifestPath, Encoding.UTF8)); }
            catch (Exception ex) { problems.Add("manifest unreadable: " + ex.Message); return problems; }
            if (manifest.SchemaVersion != OfflineRepoManifest.CurrentSchema)
                problems.Add("schema version mismatch: " + manifest.SchemaVersion);
            string recomputed = ComputeIntegrityHash(manifest);
            if (!string.Equals(recomputed, manifest.IntegrityHash, StringComparison.OrdinalIgnoreCase))
                problems.Add("manifest integrity hash mismatch (expected " + manifest.IntegrityHash + ", got " + recomputed + ")");

            string dbPath = Path.Combine(repoDir, "repo.udadb");
            if (File.Exists(dbPath))
            {
                string dbHash = Sha256File(dbPath);
                if (!string.IsNullOrEmpty(manifest.IndexDbHash) &&
                    !string.Equals(dbHash, manifest.IndexDbHash, StringComparison.OrdinalIgnoreCase))
                    problems.Add("index DB hash mismatch");
            }
            else problems.Add("index DB missing");

            foreach (var e in manifest.Entries)
            {
                ct.ThrowIfCancellationRequested();
                string abs = SafeAbsolute(repoDir, e.RelativePath);
                if (abs == null) { problems.Add("path traversal in entry: " + e.RelativePath); continue; }
                if (!File.Exists(abs)) { problems.Add("missing file: " + e.RelativePath); continue; }
                string hash = Sha256File(abs);
                if (!string.Equals(hash, e.ContentHash, StringComparison.OrdinalIgnoreCase))
                    problems.Add("hash mismatch: " + e.RelativePath);
            }
            return problems;
        }

        /// <summary>Read-only duplicate audit: groups manifest entries by the same strict identity the
        /// generator uses when omitting exact duplicates, so an audit names precisely what a
        /// regeneration would drop. Never touches any file.</summary>
        public OfflineRepoDuplicateReport AuditDuplicates(string repoDir, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(repoDir)) throw new ArgumentException("repoDir");
            repoDir = Path.GetFullPath(repoDir);
            OfflineRepoManifest manifest = TryLoadManifest(repoDir);
            if (manifest == null) throw new InvalidDataException("离线仓库缺少 manifest / repo manifest not found: " + repoDir);
            return BuildDuplicateReport(ManifestWithOrphanInfs(manifest, repoDir, ct), repoDir, ct);
        }

        /// <summary>Clean exact duplicates from an existing repository in place. Keeps the first entry
        /// of each identity group (identical to RemoveExactDuplicatesAndDetectConflicts), rewrites the
        /// index DB and manifest with updated integrity hashes BEFORE deleting any file, then removes
        /// the duplicate INF files and prunes package directories that no longer hold a kept INF.
        /// Every intermediate state verifies: manifest entries always point at existing files, so a
        /// crash can only leave harmless orphans that a re-run removes. Idempotent: a second run on a
        /// clean repo reports DuplicateCount=0 and rewrites nothing.</summary>
        public OfflineRepoDuplicateReport Clean(string repoDir, CancellationToken ct, IProgress<string> progress)
        {
            if (string.IsNullOrWhiteSpace(repoDir)) throw new ArgumentException("repoDir");
            repoDir = Path.GetFullPath(repoDir);
            OfflineRepoManifest manifest = TryLoadManifest(repoDir);
            if (manifest == null) throw new InvalidDataException("离线仓库缺少 manifest / repo manifest not found: " + repoDir);
            ct.ThrowIfCancellationRequested();

            // The generator omits duplicate records from the manifest but their copied INF files may
            // still exist under packages/. Include those orphan INFs in the audit so cleanup can see
            // and remove the disk-consuming duplicates users actually have.
            OfflineRepoDuplicateReport report = BuildDuplicateReport(ManifestWithOrphanInfs(manifest, repoDir, ct), repoDir, ct);
            if (report.DuplicateCount == 0) return report;
            Report(progress, "发现 " + report.DuplicateCount + " 个重复 INF，开始清理");

            // ----- compute the new manifest (kept entries only) -----
            var removedRels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (OfflineRepoDuplicateGroup g in report.Groups)
                foreach (string dup in g.DuplicateRelativePaths) removedRels.Add(dup ?? "");
            var keptEntries = new List<OfflineRepoEntry>();
            var keptDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (OfflineRepoEntry e in manifest.Entries)
            {
                if (removedRels.Contains(e.RelativePath ?? "")) continue;
                keptEntries.Add(e);
                keptDirs.Add(NormalizeDirRel(e.RelativePath ?? ""));
                if (!string.IsNullOrEmpty(e.PackageRoot)) keptDirs.Add(e.PackageRoot.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant());
            }
            var cleaned = new OfflineRepoManifest
            {
                SchemaVersion = manifest.SchemaVersion,
                GeneratorVersion = manifest.GeneratorVersion,
                CreatedAt = manifest.CreatedAt,
                OsArchitecture = manifest.OsArchitecture,
                OsMajorVersion = manifest.OsMajorVersion,
                MachineManufacturer = manifest.MachineManufacturer,
                MachineModel = manifest.MachineModel,
                MachineSku = manifest.MachineSku,
                Entries = keptEntries,
                HiddenPaths = (manifest.HiddenPaths ?? new List<string>()).Concat(removedRels).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                FileCount = keptEntries.Count,
                TotalBytes = keptEntries.Sum(e => e.Size),
                Warnings = new List<string>(manifest.Warnings)
            };
            string note = "dedupe-clean: removed " + report.DuplicateCount + " exact duplicate INF(s) / 清理 " + report.DuplicateCount + " 个严格重复 INF (" +
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + ")";
            cleaned.Warnings.Add(note);

            // ----- stage the new index DB + manifest, then swap them in BEFORE deleting files -----
            // After the swap every manifest entry still points at an existing file (Verify stays green);
            // the files behind removed entries become orphans that the deletion step below reclaims.
            Report(progress, "重建硬件 ID 倒排索引，共 " + keptEntries.Count + " 个 INF");
            CommitCatalog(repoDir, cleaned);

            // ----- delete the duplicate INF files, then prune dead package directories -----
            int deletedFiles = 0;
            foreach (string rel in removedRels)
            {
                ct.ThrowIfCancellationRequested();
                string abs = SafeAbsolute(repoDir, rel);
                if (abs == null) { report.Warnings.Add("skipped unsafe path: " + rel); continue; }
                if (keptEntries.Any(e => !string.IsNullOrEmpty(e.PackageRoot) && rel.StartsWith(e.PackageRoot + "/", StringComparison.OrdinalIgnoreCase))) continue;
                try { if (File.Exists(abs)) { File.Delete(abs); deletedFiles++; } }
                catch (Exception ex) { report.Warnings.Add("delete failed: " + rel + " (" + ex.Message + ")"); }
            }
            Report(progress, "已删除 " + deletedFiles + " 个重复 INF 文件，正在剪枝空目录");
            PruneDeadPackageDirs(repoDir, keptDirs, report);
            report.Cleaned = true;
            report.KeptCount = cleaned.FileCount;
            if (_logger != null) _logger.W("[INFO] 仓库重复清理完成 / Repo dedupe clean done: " + repoDir +
                " (移除 " + report.DuplicateCount + "，保留 " + report.KeptCount + ", 删除文件 " + deletedFiles + ")");
            return report;
        }

        /// <summary>Atomically remove administrator-selected manifest entries, rebuild the index,
        /// then delete their INF files and package directories that no longer contain a kept INF.</summary>
        public int DeleteEntries(string repoDir, IList<string> relativePaths, CancellationToken ct, IProgress<string> progress)
        {
            repoDir = Path.GetFullPath(repoDir);
            OfflineRepoManifest manifest = TryLoadManifest(repoDir);
            if (manifest == null) throw new InvalidDataException("repo manifest not found");
            var requested = new HashSet<string>((relativePaths ?? new string[0]).Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
            var removed = (manifest.Entries ?? new List<OfflineRepoEntry>()).Where(e => requested.Contains((e.RelativePath ?? "").Replace('\\', '/'))).ToList();
            if (removed.Count == 0) return 0;
            ct.ThrowIfCancellationRequested();
            var kept = manifest.Entries.Except(removed).ToList();
            var updated = new OfflineRepoManifest
            {
                SchemaVersion = manifest.SchemaVersion, GeneratorVersion = manifest.GeneratorVersion,
                CreatedAt = manifest.CreatedAt, OsArchitecture = manifest.OsArchitecture, OsMajorVersion = manifest.OsMajorVersion,
                MachineManufacturer = manifest.MachineManufacturer, MachineModel = manifest.MachineModel, MachineSku = manifest.MachineSku,
                Entries = kept, HiddenPaths = (manifest.HiddenPaths ?? new List<string>()).Concat(removed.Select(e => e.RelativePath)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), FileCount = kept.Count, TotalBytes = kept.Sum(e => e.Size), Warnings = new List<string>(manifest.Warnings)
            };
            updated.Warnings.Add("admin-delete: removed " + removed.Count + " INF(s) at " + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            Report(progress, "正在重建索引，保留 " + kept.Count + " 个 INF");
            CommitCatalog(repoDir, updated);
            foreach (OfflineRepoEntry e in removed)
            {
                ct.ThrowIfCancellationRequested();
                string abs = SafeAbsolute(repoDir, e.RelativePath);
                if (!string.IsNullOrEmpty(e.PackageRoot) && kept.Any(k => string.Equals(k.PackageRoot, e.PackageRoot, StringComparison.OrdinalIgnoreCase))) continue;
                try { if (abs != null && File.Exists(abs)) File.Delete(abs); } catch { }
            }
            var deletedHashes = LoadDeletedHashes(repoDir);
            foreach (OfflineRepoEntry e in removed) if (string.IsNullOrEmpty(e.PackageRoot) && !string.IsNullOrWhiteSpace(e.ContentHash)) deletedHashes.Add(e.ContentHash.Trim());
            File.WriteAllLines(Path.Combine(repoDir, "repo.deleted.txt"), deletedHashes.OrderBy(x => x).ToArray(), new UTF8Encoding(false));
            ServerStorage.Publish(repoDir, "repo.deleted.txt");
            var keptDirs = new HashSet<string>(kept.Select(e => NormalizeDirRel(e.RelativePath ?? "")).Concat(kept.Where(e => !string.IsNullOrEmpty(e.PackageRoot)).Select(e => e.PackageRoot.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant())), StringComparer.OrdinalIgnoreCase);
            var deleteReport = new OfflineRepoDuplicateReport();
            PruneDeadPackageDirs(repoDir, keptDirs, deleteReport);
            Report(progress, "已删除 " + removed.Count + " 个驱动");
            return removed.Count;
        }

        /// <summary>Content-addressed append: hash packages, copy only absent content, keep old versions.</summary>
        public OfflineRepoGenerationReport AppendOrganizedSource(string repoDir, string source, CancellationToken ct, IProgress<string> progress, string dedicatedSource = null)
        {
            if (dedicatedSource != null && !System.Text.RegularExpressions.Regex.IsMatch(dedicatedSource, @"^Machine-[a-f0-9]{32}$")) throw new ArgumentException("Invalid dedicated package identity");
            repoDir = Path.GetFullPath(repoDir); source = Path.GetFullPath(source);
            RecoverOrganization(repoDir);
            if ((source + "\\").StartsWith(repoDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("驱动源不能位于仓库内部");
            Directory.CreateDirectory(repoDir);
            var previous = TryLoadManifest(repoDir);
            var manifest = previous == null ? new OfflineRepoManifest() : DeserializeManifest(SerializeManifest(previous));
            var report = new OfflineRepoGenerationReport { RepoDir = repoDir, Rebuilt = previous == null };
            var deleted = LoadDeletedHashes(repoDir);
            var existingPaths = new HashSet<string>(manifest.Entries.Select(e => e.RelativePath), StringComparer.OrdinalIgnoreCase);
            var newlyCreated = new List<string>();
            try
            {
                var roots = dedicatedSource == null ? PackageUnits(source, ct) : new List<string> { source };
                int ordinal = 0;
                foreach (string root in roots)
                {
                    ct.ThrowIfCancellationRequested();
                    Report(progress, "校验驱动包 " + (++ordinal) + "/" + roots.Count + "：" + root);
                    string packageHash = HashPackage(root, ct, progress);
                    string sourceIdentity;
                    using (var origin = SHA256.Create()) sourceIdentity = BitConverter.ToString(origin.ComputeHash(Encoding.UTF8.GetBytes(source.ToLowerInvariant() + "|" + RelPath(source, root).ToLowerInvariant()))).Replace("-", "");
                    var infs = EnumerateInfs(root, ct).ToList();
                    var records = infs.Select(f => InfParser.Parse(f, SafeName(source))).ToList();
                    string category = ResolveCategory(repoDir, packageHash, records);
                    string[] versions = records.Select(r => r.DriverVerVersion).Distinct().ToArray();
                    string version = versions.Length == 1 ? versions[0] : "multi-version";
                    string label = SafeName(version); if (label.Length > 24) label = label.Substring(0, 24);
                    string relative = "packages/" + category + "/" + label + "-" + packageHash.Substring(0, 24);
                    // Package content, rather than source-folder identity, controls deduplication.
                    var known = manifest.Entries.FirstOrDefault(e => e.PackageHash == packageHash && !string.IsNullOrEmpty(e.PackageRoot));
                    if (dedicatedSource != null) relative = "packages/" + dedicatedSource + "/" + packageHash.Substring(0, 24);
                    else if (known != null) relative = known.PackageRoot;
                    var baselineEntry = manifest.Entries.Where(e => e.SourceIdentity == sourceIdentity && !string.IsNullOrEmpty(e.PackageRoot)).OrderByDescending(e => MatchRanker.VersionScore(e.DriverVerVersion)).FirstOrDefault();
                    string baseline = baselineEntry == null ? null : SafeAbsolute(repoDir, baselineEntry.PackageRoot);
                    string target = SafeAbsolute(repoDir, relative);
                    if (target == null) throw new IOException("Invalid package path");
                    bool visible = infs.Select((file, index) => new { file = file, record = records[index] }).Any(item => !deleted.Contains(item.record.ContentHash) && !(manifest.HiddenPaths ?? new List<string>()).Contains(relative + "/" + RelPath(root, item.file).Replace('\\', '/'), StringComparer.OrdinalIgnoreCase));
                    if (!visible) { report.Warnings.Add("该包全部 INF 已由管理员取消发布，未复制暂存包：" + root); continue; }
                    if (known != null && dedicatedSource == null)
                    {
                        var published = infs.Select((file, index) => new { Path = relative + "/" + RelPath(root, file).Replace('\\', '/'), Record = records[index] })
                            .Where(x => !deleted.Contains(x.Record.ContentHash) && !(manifest.HiddenPaths ?? new List<string>()).Contains(x.Path, StringComparer.OrdinalIgnoreCase)).ToList();
                        if (published.Count > 0 && published.All(x => existingPaths.Contains(x.Path)) && File.Exists(DownloadArchivePath(repoDir, known)))
                        { report.Unchanged += published.Count; Report(progress, "内容已发布，跳过复制与归档：" + root); continue; }
                    }
                    if (!Directory.Exists(target))
                    {
                        string temporary = Path.Combine(repoDir, ".incoming-" + Guid.NewGuid().ToString("N").Substring(0, 12));
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            CopyTree(root, temporary, baseline, ct, progress, ordinal, roots.Count, baseline != null);
                            if (HashPackage(temporary, ct, progress) != packageHash) throw new IOException("驱动源复制期间发生变化，稍后重试");
                            Directory.Move(temporary, target); newlyCreated.Add(target);
                        }
                        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
                    }
                    else if (HashPackage(target, ct) != packageHash) throw new IOException("目标包内容与指纹不符，拒绝复用");
                    foreach (string inf in infs)
                    {
                        string destination = Path.Combine(target, RelPath(root, inf));
                        var entry = BuildEntry(destination, repoDir, dedicatedSource ?? SafeName(source), new OsTarget());
                        if (entry == null || deleted.Contains(entry.ContentHash) || (manifest.HiddenPaths ?? new List<string>()).Contains(entry.RelativePath, StringComparer.OrdinalIgnoreCase)) continue;
                        entry.PackageRoot = relative; entry.PackageHash = packageHash;
                        entry.SourceIdentity = sourceIdentity;
                        if (existingPaths.Add(entry.RelativePath)) { manifest.Entries.Add(entry); report.Added++; }
                        else report.Unchanged++;
                    }
                }
                manifest.CreatedAt = DateTime.UtcNow.ToString("o"); manifest.OsArchitecture = "multi"; manifest.OsMajorVersion = 0;
                CommitCatalog(repoDir, manifest);
                report.FileCount = manifest.Entries.Count; report.TotalBytes = manifest.TotalBytes;
                Report(progress, "增量入库完成：新增 " + report.Added + "，已有 " + report.Unchanged + "；历史版本保留，未复制原仓库");
                return report;
            }
            catch
            {
                foreach (string path in newlyCreated) { try { Directory.Delete(path, true); } catch { } }
                throw;
            }
        }

        public sealed class OrganizationJournal
        {
            public bool Committed;
            public bool HadAliases;
            public List<string[]> Moves = new List<string[]>();
        }

        public static void RecoverOrganization(string repoDir)
        {
            string path = Path.Combine(repoDir, "repo.organization-journal.json");
            if (!File.Exists(path)) return;
            var journal = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Deserialize<OrganizationJournal>(File.ReadAllText(path));
            if (journal == null) throw new IOException("整理恢复日志损坏，请保留仓库并检查");
            if (!journal.Committed)
            {
                foreach (string[] move in journal.Moves.AsEnumerable().Reverse())
                {
                    string from = SafeAbsolute(repoDir, move[0]), to = SafeAbsolute(repoDir, move[1]);
                    if (from == null || to == null) throw new IOException("Invalid organization journal");
                    if (!Directory.Exists(from) && Directory.Exists(to)) { Directory.CreateDirectory(Path.GetDirectoryName(from)); Directory.Move(to, from); }
                }
                foreach (string file in new[] { "repo.udadb", "repo.manifest.json" })
                {
                    string backup = Path.Combine(repoDir, file + ".organization-backup");
                    if (File.Exists(backup)) File.Copy(backup, Path.Combine(repoDir, file), true);
                }
                string aliasPath = Path.Combine(repoDir, "repo.path-aliases.json");
                if (journal.HadAliases) File.Copy(aliasPath + ".organization-backup", aliasPath, true);
                else ServerStorage.Delete(repoDir, "repo.path-aliases.json");
                ServerStorage.Publish(repoDir, "repo.manifest.json");
            }
            File.Delete(path);
            foreach (string file in new[] { "repo.udadb", "repo.manifest.json" }) { string backup = Path.Combine(repoDir, file + ".organization-backup"); if (File.Exists(backup)) File.Delete(backup); }
            string aliasBackup = Path.Combine(repoDir, "repo.path-aliases.json.organization-backup"); if (File.Exists(aliasBackup)) File.Delete(aliasBackup);
        }

        public static string ResolvePathAlias(string repoDir, string relative)
        {
            string file = Path.Combine(repoDir, "repo.path-aliases.json");
            string aliasText = ServerStorage.Read(repoDir, "repo.path-aliases.json");
            if (aliasText == null) return relative;
            var aliases = new Dictionary<string, string>(new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Deserialize<Dictionary<string, string>>(aliasText), StringComparer.OrdinalIgnoreCase);
            string path = (relative ?? "").Replace('\\', '/'), next;
            for (int i = 0; i < 8 && aliases.TryGetValue(path, out next); i++) path = next;
            return path;
        }

        public int Organize(string repoDir, CancellationToken ct, IProgress<string> progress)
        {
            repoDir = Path.GetFullPath(repoDir); RecoverOrganization(repoDir);
            var previous = TryLoadManifest(repoDir);
            if (previous == null) throw new IOException("驱动仓库未就绪");
            var manifest = DeserializeManifest(SerializeManifest(previous));
            var journal = new OrganizationJournal();
            string aliasesPath = Path.Combine(repoDir, "repo.path-aliases.json");
            string aliasText = ServerStorage.Read(repoDir, "repo.path-aliases.json");
            journal.HadAliases = aliasText != null;
            var aliases = journal.HadAliases ? new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Deserialize<Dictionary<string, string>>(aliasText) : new Dictionary<string, string>();
            var packages = new List<string>();
            foreach (string directory in Directory.GetDirectories(Path.Combine(repoDir, "packages")))
            {
                string relative = RelPath(repoDir, directory).Replace('\\', '/') + "/";
                var knownRoots = manifest.Entries.Where(e => !string.IsNullOrEmpty(e.PackageRoot) && (e.PackageRoot + "/").StartsWith(relative, StringComparison.OrdinalIgnoreCase)).Select(e => SafeAbsolute(repoDir, e.PackageRoot)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (knownRoots.Count > 0) packages.AddRange(knownRoots); else packages.AddRange(PackageUnits(directory, ct));
            }
            int number = 0;
            foreach (string root in packages)
            {
                ct.ThrowIfCancellationRequested();
                string oldRelative = RelPath(repoDir, root).Replace('\\', '/');
                var entries = manifest.Entries.Where(e => e.RelativePath.StartsWith(oldRelative + "/", StringComparison.OrdinalIgnoreCase)).ToList();
                if (entries.Count == 0) continue;
                Report(progress, "分析并分类驱动包 " + (++number) + "/" + packages.Count);
                string hash = HashPackage(root, ct), category = ResolveCategory(repoDir, hash, entries.Select(ToPortableRecord));
                string prefix = "packages/" + category + "/";
                string newRelative = oldRelative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || oldRelative == prefix.TrimEnd('/') ? oldRelative : prefix + "pkg-" + hash.Substring(0, 24);
                string target = SafeAbsolute(repoDir, newRelative);
                if (!string.Equals(root, target, StringComparison.OrdinalIgnoreCase) && Directory.Exists(target)) newRelative += "-" + Guid.NewGuid().ToString("N");
                foreach (var entry in entries) { string oldPath = entry.RelativePath; entry.RelativePath = newRelative + entry.RelativePath.Substring(oldRelative.Length); entry.PackageRoot = newRelative; entry.PackageHash = hash; if (entry.RelativePath != oldPath) aliases[oldPath] = entry.RelativePath; }
                manifest.HiddenPaths = (manifest.HiddenPaths ?? new List<string>()).Select(p => p.StartsWith(oldRelative + "/", StringComparison.OrdinalIgnoreCase) ? newRelative + p.Substring(oldRelative.Length) : p).ToList();
                if (!string.Equals(oldRelative, newRelative, StringComparison.OrdinalIgnoreCase)) journal.Moves.Add(new[] { oldRelative, newRelative });
            }
            string journalPath = Path.Combine(repoDir, "repo.organization-journal.json");
            foreach (string file in new[] { "repo.udadb", "repo.manifest.json" }) File.Copy(Path.Combine(repoDir, file), Path.Combine(repoDir, file + ".organization-backup"), true);
            if (journal.HadAliases) File.Copy(aliasesPath, aliasesPath + ".organization-backup", true);
            File.WriteAllText(journalPath, new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Serialize(journal), new UTF8Encoding(false));
            try
            {
                int moved = 0;
                foreach (string[] move in journal.Moves)
                {
                    ct.ThrowIfCancellationRequested();
                    Report(progress, "移动完整驱动包 " + (++moved) + "/" + journal.Moves.Count + "（同盘移动，不复制驱动文件）");
                    string from = SafeAbsolute(repoDir, move[0]), to = SafeAbsolute(repoDir, move[1]);
                    Directory.CreateDirectory(Path.GetDirectoryName(to)); Directory.Move(from, to);
                }
                CommitCatalog(repoDir, manifest);
                File.WriteAllText(aliasesPath, new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Serialize(aliases), new UTF8Encoding(false));
                ServerStorage.Publish(repoDir, "repo.path-aliases.json");
                journal.Committed = true;
                File.WriteAllText(journalPath, new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Serialize(journal), new UTF8Encoding(false));
                RecoverOrganization(repoDir);
                return journal.Moves.Count;
            }
            catch { RecoverOrganization(repoDir); throw; }
        }

        // A historical PackageRoot may encompass hundreds of unrelated DriverStore folders.
        // Keep ZIP paths relative to that root, but send only the target's dependency subtree.
        public static string DownloadPackageRoot(string packageRoot, string infPath, CancellationToken ct)
        {
            string boundary = Path.GetFullPath(packageRoot).TrimEnd('\\');
            string selected = Path.GetDirectoryName(Path.GetFullPath(infPath));
            if (!(selected + "\\").StartsWith(boundary + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("INF 超出驱动包范围");
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                string expanded = selected;
                foreach (string inf in EnumerateInfs(selected, ct))
                {
                    string required = RequiredPackageRoot(inf, boundary);
                    while (!(required + "\\").StartsWith(expanded + "\\", StringComparison.OrdinalIgnoreCase)) expanded = Path.GetDirectoryName(expanded);
                    // Include may reference a vendor INF elsewhere in the historical package.
                    // Ambiguous references retain the full package, rather than dropping dependencies.
                    foreach (string line in File.ReadAllLines(inf))
                    {
                        string content = line.Split(';')[0];
                        var include = System.Text.RegularExpressions.Regex.Match(content, @"^\s*Include\s*=\s*(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (!include.Success) continue;
                        foreach (string value in include.Groups[1].Value.Split(','))
                        {
                            string name = value.Trim().Trim('"');
                            if (name.IndexOf('%') >= 0) return boundary;
                            if (name.Length == 0 || File.Exists(Path.Combine(Path.GetDirectoryName(inf), name))) continue;
                            if (EnumerateInfs(boundary, ct).Any(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase))) return boundary;
                        }
                    }
                }
                if (string.Equals(expanded, selected, StringComparison.OrdinalIgnoreCase)) return selected;
                selected = expanded;
            }
        }

        private static readonly object DownloadArchiveGate = new object();

        private static List<string> SafePackageFiles(string root)
        {
            var files = new List<string>(); var pending = new Stack<string>(); pending.Push(Path.GetFullPath(root));
            string boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("驱动下载目录含重解析点");
                foreach (string item in Directory.GetFileSystemEntries(directory))
                {
                    string full = Path.GetFullPath(item);
                    if (!full.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("驱动下载树含越界或重解析文件：" + item);
                    if (Directory.Exists(full)) pending.Push(full); else files.Add(full);
                }
            }
            return files;
        }

        public static string DownloadArchivePath(string repoDir, OfflineRepoEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.RelativePath) || string.IsNullOrWhiteSpace(entry.ContentHash)) return null;
            string identity = (entry.SourceName ?? "").StartsWith("Machine-", StringComparison.OrdinalIgnoreCase) ? "machine|" + entry.PackageRoot + "|" + entry.PackageHash : (entry.RelativePath ?? "") + "|" + (entry.ContentHash ?? "") + "|" + (entry.PackageHash ?? "");
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "").ToLowerInvariant();
            return Path.Combine(Path.GetFullPath(repoDir), ".uda-downloads", hash + ".zip");
        }

        /// <summary>Prepare an uncompressed, per-INF ZIP once during server installation or repo update.
        /// Runtime downloads then read the immutable archive directly from disk; no request scans,
        /// compresses or allocates a package. Fastest ZIP compression is paid once during installation/update;
        /// runtime traffic is immutable file streaming and client extraction stays quick.</summary>
        public static int PrepareDownloadPackages(string repoDir, IProgress<string> progress)
        { return PrepareDownloadPackages(repoDir, progress, 0); }

        /// <summary>Prepare independent packages using a bounded worker pool. A zero worker count
        /// detects the host's logical processor count for this run; callers and the CLI can override it.</summary>
        public static int PrepareDownloadPackages(string repoDir, IProgress<string> progress, int maxWorkers)
        {
            if (maxWorkers < 0) throw new ArgumentOutOfRangeException("maxWorkers", "package worker count must be zero (auto) or positive");
            if (maxWorkers == 0) maxWorkers = ClientScanParallelism.LogicalProcessors();
            lock (DownloadArchiveGate)
            {
                var manifest = TryLoadManifest(repoDir);
                if (manifest == null || manifest.SchemaVersion != OfflineRepoManifest.CurrentSchema ||
                    !string.Equals(manifest.IntegrityHash, ComputeIntegrityHash(manifest), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("驱动目录清单校验失败，无法生成下载包");
                string root = Path.GetFullPath(repoDir), cache = Path.Combine(root, ".uda-downloads");
                int created = 0, total = (manifest.Entries ?? new List<OfflineRepoEntry>()).Count, current = 0;
                if (progress != null) progress.Report("驱动包并行工作线程 / package workers: " + maxWorkers);
                Directory.CreateDirectory(cache);
                var destinationGates = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                int minWorkers, minCompletionPorts;
                ThreadPool.GetMinThreads(out minWorkers, out minCompletionPorts);
                if (minWorkers < maxWorkers && !ThreadPool.SetMinThreads(maxWorkers, minCompletionPorts))
                    throw new ArgumentOutOfRangeException("maxWorkers", "package worker count exceeds the process thread-pool limit");
                var options = new ParallelOptions { MaxDegreeOfParallelism = maxWorkers };
                try
                {
                    Parallel.ForEach(manifest.Entries ?? new List<OfflineRepoEntry>(), options, item =>
                    {
                        try
                        {
                            string destination = DownloadArchivePath(root, item);
                            if (destination == null) throw new InvalidDataException("驱动条目缺少路径或哈希");
                            object destinationGate = destinationGates.GetOrAdd(destination, key => new object());
                            lock (destinationGate)
                            {
                                if (!File.Exists(destination))
                                {
                                    string inf = SafeAbsolute(root, item.RelativePath);
                                    if (inf == null || !File.Exists(inf) || !string.Equals(Sha256File(inf), item.ContentHash, StringComparison.OrdinalIgnoreCase))
                                        throw new InvalidDataException("驱动 INF 与仓库清单不一致：" + item.RelativePath);
                                    string package = string.IsNullOrWhiteSpace(item.PackageRoot) ? Path.GetDirectoryName(inf) : SafeAbsolute(root, item.PackageRoot);
                                    if (package == null || !Directory.Exists(package)) throw new DirectoryNotFoundException("驱动包目录不存在：" + item.RelativePath);
                                    string downloadRoot = (item.SourceName ?? "").StartsWith("Machine-", StringComparison.OrdinalIgnoreCase) ? package : DownloadPackageRoot(package, inf, CancellationToken.None);
                                    string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
                                    try
                                    {
                                        List<string> sourceFiles = SafePackageFiles(downloadRoot);
                                        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
                                        {
                                            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
                                            {
                                                foreach (string file in sourceFiles)
                                                {
                                                    string full = Path.GetFullPath(file), boundary = Path.GetFullPath(package).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                                                    if (!full.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
                                                        throw new IOException("驱动下载树含越界或重解析文件：" + file);
                                                    string name = full.Substring(Path.GetFullPath(package).TrimEnd(Path.DirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/');
                                                    var zipEntry = archive.CreateEntry(name, CompressionLevel.Fastest);
                                                    using (var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
                                                    using (var target = zipEntry.Open()) source.CopyTo(target, 1024 * 1024);
                                                }
                                            }
                                            output.Flush(true);
                                        }
                                        if (File.Exists(destination)) File.Delete(destination);
                                        File.Move(temporary, destination);
                                        Interlocked.Increment(ref created);
                                    }
                                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                                }
                            }
                            int done = Interlocked.Increment(ref current);
                            if (progress != null && (done == total || done == 1 || done % Math.Max(1, total / 200) == 0))
                                progress.Report("归档预检 " + done + "/" + total + "，本次新建 " + created + "（既有归档直接复用）：" + Path.GetFileName(item.RelativePath));
                        }
                        catch (Exception ex) { throw new InvalidDataException("驱动包预生成失败：" + (item == null ? "(null)" : item.RelativePath) + "；" + ex.Message, ex); }
                    });
                }
                catch (AggregateException ex)
                {
                    var first = ex.Flatten().InnerExceptions.FirstOrDefault();
                    if (first != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
                    throw;
                }
                return created;
            }
        }

        private static string RequiredPackageRoot(string inf, string boundary)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(inf));
            string root = Path.GetFullPath(boundary).TrimEnd('\\');
            foreach (string reference in InfSourceReferences(inf))
            {
                string relative = reference.Trim().Trim('"').Replace('/', '\\');
                if (relative.Length == 0) continue;
                if (relative.IndexOf('%') >= 0 && relative.Contains("..")) throw new IOException("INF 源文件路径含无法解析的变量：" + inf + " -> " + reference);
                // Source-media paths beginning with a single slash are relative to the disk root,
                // unlike drive-qualified and UNC paths, which are outside a portable package.
                if (relative.StartsWith(@"\\", StringComparison.Ordinal) || relative.IndexOf(':') >= 0) throw new IOException("INF 源文件引用不是包内相对路径：" + inf + " -> " + reference);
                relative = relative.TrimStart('\\');
                string resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(inf)), relative));
                if (!(resolved + "\\").StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("INF 引用了驱动包范围之外的父级文件：" + inf + " -> " + reference);
                while (!(resolved + "\\").StartsWith(directory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) directory = Path.GetDirectoryName(directory);
            }
            return directory;
        }

        // Registry values, ServiceBinary and DestinationDirs describe installed/runtime paths.
        // Only source-media and explicit file-copy/include references determine package boundaries.
        private static IEnumerable<string> InfSourceReferences(string inf)
        {
            var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            string section = "", pending = "";
            foreach (string raw in File.ReadAllLines(inf))
            {
                bool quoted = false; int end = raw.Length;
                for (int i = 0; i < raw.Length; i++) { if (raw[i] == '"') quoted = !quoted; else if (raw[i] == ';' && !quoted) { end = i; break; } }
                string line = pending + raw.Substring(0, end).Trim(); pending = "";
                if (line.EndsWith("\\", StringComparison.Ordinal)) { pending = line.Substring(0, line.Length - 1); continue; }
                if (line.StartsWith("[", StringComparison.Ordinal) && line.Contains("]")) { section = line.Substring(1, line.IndexOf(']') - 1).Trim(); if (!sections.ContainsKey(section)) sections[section] = new List<string>(); continue; }
                if (line.Length > 0 && sections.ContainsKey(section)) sections[section].Add(line);
            }
            var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<string> stringLines;
            if (sections.TryGetValue("Strings", out stringLines)) foreach (string line in stringLines) { int eq = line.IndexOf('='); if (eq > 0) strings[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim().Trim('"'); }
            Func<string, string> expand = value => {
                for (int i = 0; i < 16; i++)
                {
                    string next = System.Text.RegularExpressions.Regex.Replace(value, @"%([^%]+)%", match => { string replacement; return strings.TryGetValue(match.Groups[1].Value, out replacement) ? replacement : match.Value; });
                    if (next == value) break; value = next;
                }
                return value.Trim().Trim('"');
            };
            Func<string, string> mediaPath = value => {
                string path = expand(value).Replace('/', '\\');
                if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.IndexOf(':') >= 0) throw new IOException("INF 源文件引用不是包内相对路径：" + inf + " -> " + value);
                return path.TrimStart('\\');
            };
            var refs = new List<string>(); var copySections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var disks = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in sections.Where(p => p.Key.Equals("SourceDisksNames", StringComparison.OrdinalIgnoreCase) || p.Key.StartsWith("SourceDisksNames.", StringComparison.OrdinalIgnoreCase)))
                foreach (string line in part.Value)
                {
                    int eq = line.IndexOf('='); if (eq <= 0) continue;
                    string id = line.Substring(0, eq).Trim(); var fields = InfCsv(line.Substring(eq + 1));
                    string diskPath = fields.Count > 3 ? mediaPath(fields[3]) : "";
                    List<string> paths; if (!disks.TryGetValue(id, out paths)) disks[id] = paths = new List<string>(); paths.Add(diskPath);
                    refs.Add(diskPath);
                }
            foreach (var part in sections)
                foreach (string line in part.Value)
                {
                    int eq = line.IndexOf('='); if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim(); var fields = InfCsv(line.Substring(eq + 1));
                    if (part.Key.Equals("SourceDisksFiles", StringComparison.OrdinalIgnoreCase) || part.Key.StartsWith("SourceDisksFiles.", StringComparison.OrdinalIgnoreCase))
                    {
                        List<string> paths;
                        if (fields.Count == 0 || !disks.TryGetValue(fields[0].Trim(), out paths)) paths = new List<string> { "" };
                        string subdir = fields.Count > 1 ? mediaPath(fields[1]) : "";
                        foreach (string path in paths) refs.Add(Path.Combine(path, subdir, expand(key)));
                    }
                    else if (key.Equals("CopyFiles", StringComparison.OrdinalIgnoreCase))
                        foreach (string field in fields) { string value = expand(field); if (value.StartsWith("@", StringComparison.Ordinal)) refs.Add(value.Substring(1)); else copySections.Add(value); }
                    else if (key.Equals("Include", StringComparison.OrdinalIgnoreCase) || key.Equals("CopyINF", StringComparison.OrdinalIgnoreCase))
                        foreach (string field in fields) refs.Add(expand(field));
                }
            foreach (string name in copySections)
            {
                List<string> lines; if (!sections.TryGetValue(name, out lines)) continue;
                foreach (string line in lines)
                {
                    var fields = InfCsv(line);
                    if (fields.Count > 0) refs.Add(expand(fields.Count > 1 && !string.IsNullOrWhiteSpace(fields[1]) ? fields[1] : fields[0]));
                }
            }
            return refs.Distinct(StringComparer.OrdinalIgnoreCase);
        }
        private static List<string> InfCsv(string value)
        {
            var fields = new List<string>(); bool quoted = false; int start = 0;
            for (int i = 0; i < value.Length; i++) { if (value[i] == '"') quoted = !quoted; else if (value[i] == ',' && !quoted) { fields.Add(value.Substring(start, i - start).Trim().Trim('"')); start = i + 1; } }
            fields.Add(value.Substring(start).Trim().Trim('"')); return fields;
        }

        private static List<string> PackageUnits(string root, CancellationToken ct)
        {
            var infs = EnumerateInfs(root, ct).ToList();
            // Ascend only as far as each INF's explicit parent references require, not to the
            // entire import source merely because any one package uses ..\lib.
            var required = new List<string>();
            foreach (string inf in infs)
            {
                required.Add(RequiredPackageRoot(inf, root));
            }
            var dirs = required.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Length).ToList();
            var result = new List<string>();
            foreach (string dir in dirs)
            {
                ct.ThrowIfCancellationRequested();
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) throw new IOException("不导入符号链接或联接目录");
                if (!result.Any(parent => (dir + "\\").StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase))) result.Add(dir);
            }
            return result;
        }

        private static string HashFileProgress(string file, CancellationToken ct, Action<long> progress)
        {
            using (var stream = File.OpenRead(file)) using (var sha = SHA256.Create())
            {
                var buffer = new byte[1024 * 1024]; long total = 0; int read; DateTime last = DateTime.UtcNow;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested(); sha.TransformBlock(buffer, 0, read, buffer, 0); total += read;
                    if (progress != null && (DateTime.UtcNow - last).TotalMilliseconds >= 500) { progress(total); last = DateTime.UtcNow; }
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static string HashPackage(string root, CancellationToken ct, IProgress<string> progress = null)
        {
            using (var hash = SHA256.Create())
            {
                var identity = new StringBuilder();
                var files = PackageFiles(root, ct).OrderBy(f => RelPath(root, f), StringComparer.OrdinalIgnoreCase).ToList();
                long bytes = 0; int number = 0; DateTime last = DateTime.UtcNow;
                foreach (string file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("不导入符号链接文件");
                    number++;
                    string digest = HashFileProgress(file, ct, count => Report(progress, "校验驱动包文件 " + number + "/" + files.Count + "，累计读取 " + ((bytes + count) / 1073741824.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB：" + Path.GetFileName(file)));
                    identity.Append(RelPath(root, file).ToLowerInvariant()).Append('|').Append(digest).Append('\n'); bytes += new FileInfo(file).Length;
                    if (number == 1 || number == files.Count || (DateTime.UtcNow - last).TotalMilliseconds >= 500) { Report(progress, "校验驱动包文件 " + number + "/" + files.Count + "，已读取 " + (bytes / 1073741824.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB"); last = DateTime.UtcNow; }
                }
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(identity.ToString()))).Replace("-", "").ToLowerInvariant();
            }
        }

        public static readonly string[] PackageCategories = { "芯片组", "USB", "显卡", "声卡", "有线网卡", "无线网卡", "其他网络设备", "打印机", "蓝牙", "HID", "存储控制器", "其他", "其他-混合驱动包" };
        private static string ResolveCategory(string repo, string hash, IEnumerable<InfRecord> records)
        {
            string path = Path.Combine(repo, "repo.category-overrides.json");
            string categoryText = ServerStorage.Read(repo, "repo.category-overrides.json");
            if (categoryText != null)
            {
                var map = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Deserialize<Dictionary<string, string>>(categoryText);
                string category;
                if (map != null && map.TryGetValue(hash, out category) && PackageCategories.Contains(category)) return category;
            }
            return ClassifyPackage(records);
        }

        public static string ClassifyPackage(IEnumerable<InfRecord> records)
        {
            var categories = records.Select(record => {
                string c = (record.Class ?? "").ToLowerInvariant();
                if (c == "display") return "显卡";
                if (c == "media" || c == "audioendpoint" || c == "audio processing object") return "声卡";
                if (c == "usb" || c == "usbdevice") return "USB";
                if (c == "system" || c == "processor") return "芯片组";
                if (c == "net") {
                    string text = string.Join(" ", (record.Models ?? new List<InfModelEntry>()).Select(m => m.Description)).ToLowerInvariant();
                    return text.Contains("wireless") || text.Contains("wi-fi") || text.Contains("wlan") || text.Contains("802.11") ? "无线网卡" : text.Contains("ethernet") || text.Contains("gbe") || text.Contains("lan controller") ? "有线网卡" : "其他网络设备";
                }
                if (c == "printer" || c == "printqueue") return "打印机";
                if (c == "bluetooth") return "蓝牙";
                if (c == "hidclass") return "HID";
                if (c == "hdc" || c == "scsiadapter") return "存储控制器";
                return "其他";
            }).Distinct().ToArray();
            return categories.Length == 1 ? categories[0] : "其他-混合驱动包";
        }

        private static void ReplaceCatalogFile(string staged, string live)
        {
            for (int retry = 0; ; retry++)
            {
                try { if (File.Exists(live)) File.Replace(staged, live, null); else File.Move(staged, live); return; }
                catch (IOException) { if (retry >= 5) throw; Thread.Sleep(100 * (retry + 1)); }
            }
        }

        private static void CommitCatalog(string root, OfflineRepoManifest manifest)
        {
            string db = Path.Combine(root, "repo.udadb"), mf = Path.Combine(root, "repo.manifest.json"), suffix = ".txn-" + SafeSuffix();
            string stagedDb = db + suffix, stagedMf = mf + suffix, backupDb = db + suffix + ".bak", backupMf = mf + suffix + ".bak";
            bool hadDb = File.Exists(db), hadMf = File.Exists(mf), touched = false;
            try
            {
                manifest.FileCount = manifest.Entries.Count; manifest.TotalBytes = manifest.Entries.Sum(e => e.Size);
                var index = new DriverIndex(stagedDb); index.ReplaceAll(manifest.Entries.Select(ToPortableRecord)); index.Commit();
                manifest.IndexDbHash = Sha256File(stagedDb); manifest.IntegrityHash = ComputeIntegrityHash(manifest);
                File.WriteAllText(stagedMf, SerializeManifest(manifest), new UTF8Encoding(false));
                if (hadDb) File.Copy(db, backupDb); if (hadMf) File.Copy(mf, backupMf);
                touched = true; ReplaceCatalogFile(stagedDb, db); ReplaceCatalogFile(stagedMf, mf);
                ServerStorage.Publish(root, "repo.manifest.json");
            }
            catch
            {
                if (touched) { if (hadDb) File.Copy(backupDb, db, true); else if (File.Exists(db)) File.Delete(db); if (hadMf) File.Copy(backupMf, mf, true); else if (File.Exists(mf)) File.Delete(mf); }
                if (touched && hadMf && ServerStorage.Config(root).Enabled) ServerStorage.Write(root, "repo.manifest.json", File.ReadAllText(mf), false);
                throw;
            }
            finally { foreach (string path in new[] { stagedDb, stagedMf, backupDb, backupMf }) try { if (File.Exists(path)) File.Delete(path); } catch { } }
        }

        private static HashSet<string> LoadDeletedHashes(string repoDir)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = Path.Combine(repoDir, "repo.deleted.txt");
                string deletedText = ServerStorage.Read(repoDir, "repo.deleted.txt");
                if (deletedText != null) foreach (string line in deletedText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    if (!string.IsNullOrWhiteSpace(line)) result.Add(line.Trim());
            }
            catch { }
            return result;
        }

        private static IEnumerable<string> PackageFiles(string root, CancellationToken ct)
        {
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested(); string directory = pending.Pop();
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("驱动包包含链接或联接目录，拒绝越界扫描");
                foreach (string file in Directory.GetFiles(directory))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("驱动包包含链接文件");
                    yield return file;
                }
                foreach (string child in Directory.GetDirectories(directory)) pending.Push(child);
            }
        }

        private OfflineRepoDuplicateReport BuildDuplicateReport(OfflineRepoManifest manifest, string repoDir, CancellationToken ct)
        {
            var report = new OfflineRepoDuplicateReport { RepoDir = repoDir };
            report.TotalCount = manifest.Entries.Count;
            var groups = new List<OfflineRepoDuplicateGroup>();
            var byIdentity = new Dictionary<string, OfflineRepoDuplicateGroup>(StringComparer.Ordinal);
            var evidence = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (OfflineRepoEntry e in manifest.Entries)
            {
                ct.ThrowIfCancellationRequested();
                string absolute = SafeAbsolute(repoDir, e.RelativePath);
                if (absolute == null || !File.Exists(absolute)) continue;
                string package = string.IsNullOrEmpty(e.PackageRoot) ? Path.GetDirectoryName(absolute) : SafeAbsolute(repoDir, e.PackageRoot);
                if (package == null) throw new IOException("Invalid package root");
                if (string.IsNullOrEmpty(e.PackageRoot) && File.ReadAllText(absolute).Contains(".."))
                {
                    string[] parts = e.RelativePath.Replace('\\', '/').Split('/');
                    if (parts.Length >= 3) package = SafeAbsolute(repoDir, parts[0] + "/" + parts[1]);
                }
                string fingerprint;
                if (!evidence.TryGetValue(package, out fingerprint))
                {
                    // INF aliases can have different file names, but every companion binary and
                    // catalog must have identical relative names and bytes before deletion is safe.
                    var members = new HashSet<string>(StringComparer.Ordinal);
                    foreach (string file in PackageFiles(package, ct))
                        members.Add((Path.GetExtension(file).Equals(".inf", StringComparison.OrdinalIgnoreCase) ? "INF" : RelPath(package, file).ToLowerInvariant()) + "|" + Sha256File(file));
                    using (var hash = SHA256.Create()) fingerprint = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", members.OrderBy(x => x, StringComparer.Ordinal))))).Replace("-", "");
                    evidence[package] = fingerprint;
                }
                string identity = DuplicateIdentity(e) + "\u001fpayload:" + fingerprint;
                OfflineRepoDuplicateGroup group;
                if (!byIdentity.TryGetValue(identity, out group))
                {
                    group = new OfflineRepoDuplicateGroup
                    {
                        Identity = identity,
                        DriverVerVersion = (e.DriverVerVersion ?? "").Trim(),
                        KeptRelativePath = e.RelativePath ?? ""
                    };
                    byIdentity[identity] = group;
                    groups.Add(group);
                }
                else
                {
                    group.DuplicateRelativePaths.Add(e.RelativePath ?? "");
                }
            }
            report.Groups = groups.Where(g => g.DuplicateRelativePaths.Count > 0).ToList();
            report.DuplicateCount = report.Groups.Sum(g => g.DuplicateRelativePaths.Count);
            report.KeptCount = report.TotalCount - report.DuplicateCount;
            return report;
        }

        private OfflineRepoManifest ManifestWithOrphanInfs(OfflineRepoManifest manifest, string repoDir, CancellationToken ct)
        {
            var expanded = new OfflineRepoManifest { Entries = new List<OfflineRepoEntry>(manifest.Entries ?? new List<OfflineRepoEntry>()) };
            var known = new HashSet<string>(expanded.Entries.Select(e => (e.RelativePath ?? "").Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
            string packages = Path.Combine(repoDir, "packages");
            if (!Directory.Exists(packages)) return expanded;
            foreach (string inf in Directory.EnumerateFiles(packages, "*.inf", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                string rel;
                try { rel = RelPath(repoDir, inf).Replace('\\', '/'); } catch { continue; }
                if (!known.Add(rel)) continue;
                if ((manifest.HiddenPaths ?? new List<string>()).Contains(rel, StringComparer.OrdinalIgnoreCase)) continue;
                try
                {
                    string[] parts = rel.Split('/');
                    string source = parts.Length > 1 ? parts[1] : "orphan";
                    InfRecord rec = InfParser.Parse(inf, source);
                    expanded.Entries.Add(new OfflineRepoEntry
                    {
                        RelativePath = rel, SourceName = source, ContentHash = rec.ContentHash,
                        Provider = rec.Provider, Manufacturer = rec.Manufacturer, Class = rec.Class,
                        ClassGuid = rec.ClassGuid, DriverVerDate = rec.DriverVerDate,
                        DriverVerVersion = rec.DriverVerVersion, Decorations = new List<string>(rec.Decorations),
                        HardwareIds = new List<string>(rec.HardwareIds), CompatibleIds = new List<string>(rec.CompatibleIds),
                        Models = rec.Models != null ? new List<InfModelEntry>(rec.Models) : new List<InfModelEntry>(),
                        Size = new FileInfo(inf).Length
                    });
                }
                catch (Exception ex) { if (_logger != null) _logger.W("[WARN] 查重解析 INF 失败 / duplicate audit parse failed: " + inf + " => " + ex.Message); }
            }
            return expanded;
        }

        private static void Report(IProgress<string> progress, string message)
        {
            if (progress != null) progress.Report(message ?? "");
        }

        /// <summary>Delete every directory under the repo's packages root that contains no kept INF
        /// anywhere beneath it — its companion files could only ever be referenced by removed entries.
        /// Runs deepest-first so nested dead directories collapse into their dead parents.</summary>
        private void PruneDeadPackageDirs(string repoRoot, HashSet<string> keptDirs, OfflineRepoDuplicateReport report)
        {
            string packagesRoot = Path.Combine(repoRoot, "packages");
            if (!Directory.Exists(packagesRoot)) return;
            string rootPrefix = packagesRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var dirs = Directory.GetDirectories(packagesRoot, "*", SearchOption.AllDirectories)
                .OrderByDescending(d => d.Length).ToList();
            foreach (string dir in dirs)
            {
                // Both sides are "packages\..."-prefixed relative DIRECTORIES, normalized identically.
                // (No filename stripping here: dir is already a directory, not a file path.)
                string relDir = ("packages\\" + dir.Substring(rootPrefix.Length)).Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
                bool live = keptDirs.Contains(relDir) ||
                    keptDirs.Any(k => k.StartsWith(relDir + "\\", StringComparison.OrdinalIgnoreCase) ||
                                      relDir.StartsWith(k + "\\", StringComparison.OrdinalIgnoreCase));
                if (live) continue;
                try
                {
                    Directory.Delete(dir, true);
                    report.Warnings.Add("pruned dead package dir: " + relDir);
                }
                catch (Exception ex) { report.Warnings.Add("prune failed: " + relDir + " (" + ex.Message + ")"); }
            }
        }

        /// <summary>Directory of a repo entry's relative FILE path, normalized ("packages/src/a.inf"
        /// -> "packages\src"). Pure string work — never resolves against the current directory.</summary>
        private static string NormalizeDirRel(string relFilePath)
        {
            string path = (relFilePath ?? "").Replace('/', '\\').TrimEnd('\\');
            int slash = path.LastIndexOf('\\');
            string dir = slash >= 0 ? path.Substring(0, slash) : "";
            return dir.ToLowerInvariant();
        }

        // ----- helpers -----

        private static string SafeSuffix()
        {
            try { return Path.GetRandomFileName(); }
            catch { return "tmp"; }
        }

        private static string SafeName(string path)
        {
            try
            {
                string n = Path.GetFileName(path.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(n)) n = new DirectoryInfo(path).Name;
                var sb = new StringBuilder();
                foreach (char c in n ?? "src")
                    sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');
                string s = sb.ToString().Trim('-');
                return s.Length == 0 ? "src" : s;
            }
            catch { return "src"; }
        }

        /// <summary>Deterministic, collision-free package name derived from the source directory name.
        /// Stable across regenerations with the same source list, so incremental diffs key on stable paths.</summary>
        private static string UniqueName(string baseName, HashSet<string> used)
        {
            string name = baseName;
            int n = 2;
            while (used.Contains(name)) { name = baseName + "-" + n.ToString(CultureInfo.InvariantCulture); n++; }
            used.Add(name);
            return name;
        }

        private static IEnumerable<string> EnumerateInfs(string root, CancellationToken ct)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                string dir = stack.Pop();
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) throw new IOException("不处理符号链接或联接目录：" + dir);
                string[] files, dirs;
                try { files = Directory.GetFiles(dir, "*.inf"); dirs = Directory.GetDirectories(dir); }
                catch { continue; }
                foreach (var f in files) yield return f;
                foreach (var d in dirs) stack.Push(d);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

        private static bool SameFileMetadata(string source, string existing, CancellationToken ct, IProgress<string> progress)
        {
            try
            {
                var a = new FileInfo(source);
                var b = new FileInfo(existing);
                return b.Exists && a.Length == b.Length && string.Equals(HashFileProgress(source, ct, count => Report(progress, "校验可复用源文件：" + Path.GetFileName(source) + " " + (count / 1048576) + " MiB")), HashFileProgress(existing, ct, count => Report(progress, "校验仓库已有文件：" + Path.GetFileName(existing) + " " + (count / 1048576) + " MiB")), StringComparison.OrdinalIgnoreCase);
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }

        private static void CopyTree(string src, string dest, string oldDest, CancellationToken ct, IProgress<string> progress, int sourceIndex, int sourceCount, bool requireReuse = false)
        {
            if (!Directory.Exists(src)) return;
            Directory.CreateDirectory(dest);
            long copiedBytes = 0;
            int copiedFiles = 0;
            int reusedFiles = 0;
            DateTime lastReport = DateTime.UtcNow;
            var stack = new Stack<KeyValuePair<string, string>>();
            var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            stack.Push(new KeyValuePair<string, string>(src, dest));
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var kv = stack.Pop();
                Directory.CreateDirectory(kv.Value);
                string[] files;
                try { files = Directory.GetFiles(kv.Key); }
                catch { continue; }
                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    string target = Path.Combine(kv.Value, Path.GetFileName(f));
                    if (!destinations.Add(Path.GetFullPath(target))) throw new IOException("驱动源包含 Windows 同名路径冲突，未覆盖任何已有文件");
                    try
                    {
                        string relative = f.Substring(src.TrimEnd('\\', '/').Length).TrimStart('\\', '/');
                        string existing = string.IsNullOrEmpty(oldDest) ? null : Path.Combine(oldDest, relative);
                        bool unchanged = !string.IsNullOrEmpty(existing) && SameFileMetadata(f, existing, ct, progress);
                        Func<string, string> nativePath = path => path.StartsWith("\\\\?\\", StringComparison.Ordinal) ? path : path.StartsWith("\\\\", StringComparison.Ordinal) ? "\\\\?\\UNC\\" + path.Substring(2) : "\\\\?\\" + Path.GetFullPath(path);
                        bool reused = unchanged && CreateHardLink(nativePath(target), nativePath(existing), IntPtr.Zero);
                        if (unchanged && !reused && requireReuse) throw new IOException("仓库磁盘不支持复用旧版本文件，已拒绝复制全包；请使用支持硬链接的 NTFS 仓库或分包导入（Windows 错误 " + Marshal.GetLastWin32Error() + "）");
                        if (!reused)
                        {
                            using (var input = File.OpenRead(f)) using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            {
                                var buffer = new byte[1024 * 1024]; int read; long written = 0; DateTime last = DateTime.UtcNow;
                                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    ct.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); written += read;
                                    if ((DateTime.UtcNow - last).TotalMilliseconds >= 500) { Report(progress, "增量写入 " + ((copiedBytes + written) / 1073741824.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB：" + Path.GetFileName(f)); last = DateTime.UtcNow; }
                                }
                            }
                            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(f));
                        }
                        copiedFiles++;
                        if (reused) reusedFiles++;
                        else { try { copiedBytes += new FileInfo(f).Length; } catch { } }
                        if (progress != null && (copiedFiles == 1 || copiedFiles % 200 == 0 || DateTime.UtcNow.Subtract(lastReport).TotalSeconds >= 1))
                        {
                            progress.Report("增量处理驱动文件：" + copiedFiles + " 个（复用 " + reusedFiles + " 个），新增写入 " + (copiedBytes / 1073741824.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB（驱动源 " + sourceIndex + "/" + sourceCount + "）");
                            lastReport = DateTime.UtcNow;
                        }
                    }
                    catch (IOException ex)
                    {
                        long free = -1;
                        try { free = new DriveInfo(Path.GetPathRoot(target)).AvailableFreeSpace; } catch { }
                        throw new IOException("增量写入驱动仓库失败 / incremental repository write failed: " + target +
                            (free >= 0 ? "；仓库磁盘剩余 " + (free / 1073741824.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB" : "") +
                            "。未变化文件不会重复占用空间。", ex);
                    }
                    catch (UnauthorizedAccessException) { }
                }
                string[] dirs;
                try { dirs = Directory.GetDirectories(kv.Key); }
                catch { continue; }
                foreach (var d in dirs)
                {
                    // Never follow junctions/symbolic links. Driver shares sometimes contain directory
                    // links back to a parent; following them can copy the same tree until the disk fills.
                    try { if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue; }
                    catch { continue; }
                    stack.Push(new KeyValuePair<string, string>(d, Path.Combine(kv.Value, Path.GetFileName(d))));
                }
            }
            if (progress != null) progress.Report("驱动文件增量处理完成：" + copiedFiles + " 个（复用 " + reusedFiles + " 个），新增写入 " + (copiedBytes / 1073741824.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB");
        }

        private OfflineRepoEntry BuildEntry(string infAbs, string stagingRoot, string sourceName, OsTarget target)
        {
            InfRecord rec;
            try { rec = InfParser.Parse(infAbs, sourceName); }
            catch (Exception ex) { if (_logger != null) _logger.W("[WARN] 解析失败 / Parse failed: " + infAbs + " => " + ex.Message); return null; }
            string rel;
            try { rel = RelPath(stagingRoot, infAbs); }
            catch { return null; }
            long size;
            try { size = new FileInfo(infAbs).Length; } catch { size = 0; }
            string sigStatus = "unknown";
            try { sigStatus = (_sig != null && _sig.IsTrusted(infAbs, _logger)) ? "trusted" : "untrusted"; }
            catch { sigStatus = "deferred"; }
            var entry = new OfflineRepoEntry
            {
                RelativePath = rel.Replace('\\', '/'),
                SourceName = sourceName,
                ContentHash = rec.ContentHash,
                SignatureStatus = sigStatus,
                Provider = rec.Provider,
                Manufacturer = rec.Manufacturer,
                Class = rec.Class,
                ClassGuid = rec.ClassGuid,
                DriverVerDate = rec.DriverVerDate,
                DriverVerVersion = rec.DriverVerVersion,
                Decorations = new List<string>(rec.Decorations),
                HardwareIds = new List<string>(rec.HardwareIds),
                CompatibleIds = new List<string>(rec.CompatibleIds),
                Models = rec.Models != null && rec.Models.Count > 0
                    ? rec.Models.Select(m => new InfModelEntry
                    {
                        Description = m.Description, InstallSection = m.InstallSection, Decoration = m.Decoration,
                        HardwareIds = new List<string>(m.HardwareIds), CompatibleIds = new List<string>(m.CompatibleIds)
                    }).ToList()
                    : new List<InfModelEntry>(),
                Size = size
            };
            return entry;
        }

        private void RemoveExactDuplicatesAndDetectConflicts(List<OfflineRepoEntry> entries, OfflineRepoGenerationReport report)
        {
            var byIdentity = new Dictionary<string, OfflineRepoEntry>(StringComparer.OrdinalIgnoreCase);
            var kept = new List<OfflineRepoEntry>();
            foreach (var e in entries)
            {
                string key = DuplicateIdentity(e);
                OfflineRepoEntry first;
                if (byIdentity.TryGetValue(key, out first))
                {
                    report.Duplicates++;
                    string msg = "duplicate-driver: " + e.RelativePath + " duplicates hardware/version/applicability of " + first.RelativePath;
                    report.Warnings.Add(msg);
                    if (_logger != null) _logger.W("[WARN] " + msg);
                    continue;
                }
                byIdentity[key] = e;
                kept.Add(e);
            }
            entries.Clear();
            entries.AddRange(kept);

            var byCanonical = new Dictionary<string, List<OfflineRepoEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
            {
                // Conflict: same canonical VEN/DEV but different content.
                foreach (var id in e.HardwareIds)
                {
                    string canon = HardwareIdNormalizer.CanonicalVenDev(id);
                    if (canon.Length == 0) continue;
                    List<OfflineRepoEntry> list;
                    if (!byCanonical.TryGetValue(canon, out list)) { list = new List<OfflineRepoEntry>(); byCanonical[canon] = list; }
                    if (!list.Contains(e)) list.Add(e);
                }
            }
            foreach (var kv in byCanonical)
            {
                var distinct = kv.Value.Where(e => !e.Duplicate).Select(e => e.ContentHash).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (distinct.Count > 1)
                {
                    foreach (var e in kv.Value) e.Conflict = true;
                    report.Conflicts++;
                    string msg = "conflict: canonical " + kv.Key + " has " + distinct.Count + " distinct packages (" +
                        string.Join(", ", kv.Value.Select(e => e.RelativePath).Distinct()) + ")";
                    report.Warnings.Add(msg);
                    if (_logger != null) _logger.W("[WARN] " + msg);
                }
            }
        }

        /// <summary>Strict-duplicate identity (driver version + normalized IDs + decorations).
        /// Internal so the harnesses compiling in the same assembly can reuse it; AuditDuplicates
        /// and Clean must report exactly what RemoveExactDuplicatesAndDetectConflicts omits.</summary>
        internal static string DuplicateIdentity(OfflineRepoEntry e)
        {
            Func<IEnumerable<string>, string> ids = values => string.Join("\n", (values ?? Enumerable.Empty<string>())
                .Select(HardwareIdNormalizer.Normalize).Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            Func<IEnumerable<string>, string> decorations = values => string.Join("\n", (values ?? Enumerable.Empty<string>())
                .Select(x => (x ?? "").Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            string hardware = ids(e.HardwareIds);
            string compatible = ids(e.CompatibleIds);
            string version = (e.DriverVerVersion ?? "").Trim();
            // Without both a version and at least one applicability identifier we
            // cannot prove equivalence, so retain the entry.
            if (version.Length == 0 || (hardware.Length == 0 && compatible.Length == 0))
                return "unique-path\u001f" + (e.RelativePath ?? "");
            return ((e.SourceName ?? "").StartsWith("Machine-", StringComparison.OrdinalIgnoreCase) ? e.SourceName + "\u001f" : "") + version + "\u001f" +
                   hardware + "\u001f" + compatible + "\u001f" +
                   decorations(e.Decorations) + "\u001f" + (e.ClassGuid ?? "").ToUpperInvariant() + "\u001f" + (e.Provider ?? "").ToUpperInvariant() + "\u001f" + (e.PackageHash ?? "");
        }

        // Internal (not private) so the test harnesses compiled into the same assembly can rebuild a
        // portable index DB when they construct legacy/tampered repo fixtures.
        internal static InfRecord ToPortableRecord(OfflineRepoEntry e)
        {
            return new InfRecord
            {
                Path = e.RelativePath,
                SourceName = e.SourceName,
                Provider = e.Provider,
                Manufacturer = e.Manufacturer,
                Class = e.Class,
                ClassGuid = e.ClassGuid,
                DriverVerDate = e.DriverVerDate,
                DriverVerVersion = e.DriverVerVersion,
                ContentHash = e.ContentHash,
                Decorations = new List<string>(e.Decorations),
                HardwareIds = new List<string>(e.HardwareIds),
                CompatibleIds = new List<string>(e.CompatibleIds),
                Models = e.Models != null ? new List<InfModelEntry>(e.Models) : new List<InfModelEntry>()
            };
        }

        private static void DiffAgainstOld(OfflineRepoManifest neu, OfflineRepoManifest old, OfflineRepoGenerationReport report)
        {
            if (old == null) { report.Added = neu.Entries.Count; return; }
            var oldByRel = old.Entries.ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);
            var newRels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in neu.Entries)
            {
                newRels.Add(e.RelativePath);
                OfflineRepoEntry o;
                if (!oldByRel.TryGetValue(e.RelativePath, out o)) report.Added++;
                else if (!string.Equals(o.ContentHash, e.ContentHash, StringComparison.OrdinalIgnoreCase)) report.Modified++;
                else report.Unchanged++;
            }
            foreach (var kv in oldByRel)
                if (!newRels.Contains(kv.Key)) report.Removed++;
        }

        private static void AtomicSwap(string staging, string repoDir)
        {
            string oldDir = null;
            if (Directory.Exists(repoDir))
            {
                oldDir = repoDir + ".old-" + SafeSuffix();
                Directory.Move(repoDir, oldDir);
            }
            try
            {
                Directory.Move(staging, repoDir);
                if (oldDir != null) { try { Directory.Delete(oldDir, true); } catch { } }
            }
            catch
            {
                // Roll back to the previous repo so we never end up with nothing.
                if (oldDir != null && Directory.Exists(oldDir) && !Directory.Exists(repoDir))
                { try { Directory.Move(oldDir, repoDir); } catch { } }
                throw;
            }
        }

        // ----- manifest (de)serialization + integrity -----

        private static string RelPath(string root, string full)
        {
            string f = Path.GetFullPath(full).TrimEnd('\\') + "\\";
            string r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            if (!f.StartsWith(r, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("path not under root: " + full);
            return f.Substring(r.Length).TrimEnd('\\');
        }

        /// <summary>Resolve a relative path under repoRoot, rejecting traversal. Returns null if unsafe.</summary>
        public static string SafeAbsolute(string repoRoot, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) return null;
            string combined = Path.Combine(repoRoot, relative.Replace('/', '\\'));
            string full;
            try { full = Path.GetFullPath(combined); }
            catch { return null; }
            string root = Path.GetFullPath(repoRoot).TrimEnd('\\') + "\\";
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            // Reject any segment that tries to escape via ".." even if it normalizes back.
            if (Path.IsPathRooted(relative) || relative.Contains(":") || relative.Split('/', '\\').Any(p => p == ".." || p == ".")) return null;
            return full;
        }

        public static string SerializeManifest(OfflineRepoManifest manifest)
        {
            return Pretty(CreateManifestSerializer().Serialize(manifest));
        }

        public static OfflineRepoManifest DeserializeManifest(string json)
        {
            var manifest = CreateManifestSerializer().Deserialize<OfflineRepoManifest>(json);
            if (manifest == null) throw new InvalidDataException("empty manifest");
            if (manifest.Entries == null) manifest.Entries = new List<OfflineRepoEntry>();
            if (manifest.Warnings == null) manifest.Warnings = new List<string>();
            if (manifest.HiddenPaths == null) manifest.HiddenPaths = new List<string>();
            foreach (var e in manifest.Entries)
            {
                if (e.HardwareIds == null) e.HardwareIds = new List<string>();
                if (e.CompatibleIds == null) e.CompatibleIds = new List<string>();
                if (e.Decorations == null) e.Decorations = new List<string>();
            }
            return manifest;
        }

        private static JavaScriptSerializer CreateManifestSerializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 512 };
        }

        public static OfflineRepoManifest TryLoadManifest(string repoDir)
        {
            try
            {
                string p = Path.Combine(repoDir, "repo.manifest.json");
                string text = ServerStorage.Read(repoDir, "repo.manifest.json");
                return text == null ? null : DeserializeManifest(text);
            }
            catch { return null; }
        }

        /// <summary>Deterministic SHA-256 over a canonical rendering of the manifest (IntegrityHash excluded).
        /// Independent of JSON serializer field ordering, so it is stable across machines and runs.</summary>
        public static string ComputeIntegrityHash(OfflineRepoManifest manifest)
        {
            var sb = new StringBuilder();
            sb.Append(manifest.SchemaVersion).Append('|')
              .Append(manifest.GeneratorVersion).Append('|')
              .Append(manifest.CreatedAt).Append('|')
              .Append(manifest.OsArchitecture).Append('|')
              .Append(manifest.OsMajorVersion).Append('|')
              .Append(manifest.MachineManufacturer).Append('|')
              .Append(manifest.MachineModel).Append('|')
              .Append(manifest.MachineSku).Append('|')
              .Append(manifest.FileCount).Append('|')
              .Append(manifest.TotalBytes).Append('|')
              .Append(manifest.IndexDbHash).Append('|');
            foreach (var e in manifest.Entries.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(e.PackageRoot) || !string.IsNullOrEmpty(e.PackageHash)) sb.Append("package:").Append(e.PackageRoot).Append('~').Append(e.PackageHash).Append('|');
                if (!string.IsNullOrEmpty(e.SourceIdentity)) sb.Append("source:").Append(e.SourceIdentity).Append('|');
                sb.Append(e.RelativePath).Append('~')
                  .Append(e.SourceName).Append('~')
                  .Append(e.ContentHash).Append('~')
                  .Append(e.SignatureStatus).Append('~')
                  .Append(e.Provider).Append('~')
                  .Append(e.Class).Append('~')
                  .Append(e.ClassGuid).Append('~')
                  .Append(e.DriverVerDate).Append('~')
                  .Append(e.DriverVerVersion).Append('~')
                  .Append(e.Size).Append('~')
                  .Append(string.Join(",", e.Decorations)).Append('~')
                  .Append(string.Join(",", e.HardwareIds)).Append('~')
                  .Append(string.Join(",", e.CompatibleIds)).Append('|');
            }
            if (manifest.HiddenPaths != null && manifest.HiddenPaths.Count > 0) sb.Append("hidden:").Append(string.Join("|", manifest.HiddenPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))).Append('|');
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }

        public static string Sha256File(string path)
        {
            using (var s = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(s);
                var hex = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }

        private static string Pretty(string json)
        {
            // Lightweight pretty-printer so the manifest is reviewable by humans; deterministic enough for diffing.
            var sb = new StringBuilder(json.Length * 2);
            int depth = 0; bool inStr = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inStr) { sb.Append(c); if (c == '"' && json[i - 1] != '\\') inStr = false; continue; }
                if (c == '"') { inStr = true; sb.Append(c); continue; }
                if (c == '{' || c == '[') { sb.Append(c); sb.AppendLine(); sb.Append(new string(' ', ++depth * 2)); continue; }
                if (c == '}' || c == ']') { sb.AppendLine(); sb.Append(new string(' ', --depth * 2)); sb.Append(c); continue; }
                if (c == ',') { sb.Append(c); sb.AppendLine(); sb.Append(new string(' ', depth * 2)); continue; }
                sb.Append(c);
            }
            return sb.ToString();
        }
    }

    // =================================================================================
    //  Loader (trusts repo root + manifest only; fast in-memory ranker; per-file verify)
    // =================================================================================

    /// <summary>
    /// Opens an offline repository and exposes a fast MatchRanker. Trusts ONLY the manifest: verifies the
    /// manifest integrity hash, then verifies the index DB hash against the manifest (rebuilding the index
    /// from the manifest entries on any mismatch). Records carry relative paths; callers resolve to absolute
    /// via AbsoluteFor (which enforces path-traversal safety).
    /// </summary>
    public sealed class OfflineRepo : IDisposable
    {
        public string RepoRoot;
        public OfflineRepoManifest Manifest;
        public List<InfRecord> Records;
        private readonly Dictionary<string, OfflineRepoEntry> _byRel;

        private OfflineRepo(string repoRoot, OfflineRepoManifest manifest, List<InfRecord> records)
        {
            RepoRoot = repoRoot;
            Manifest = manifest;
            Records = records;
            _byRel = new Dictionary<string, OfflineRepoEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in manifest.Entries) _byRel[NormRel(e.RelativePath)] = e;
        }

        public int Count { get { return Records.Count; } }

        /// <summary>Number of legacy-index records whose Models were re-parsed from their INFs by the
        /// last Open(). >0 means the on-disk index is a legacy index and PersistUpgradedIndex can make
        /// the backfill a one-time migration instead of a per-start rescan of the whole repo.</summary>
        public int BackfilledModels;

        internal static OfflineRepo FromCatalog(string root, OfflineRepoManifest manifest)
        {
            return new OfflineRepo(root, manifest, manifest.Entries.Select(ToPortableRecord).ToList());
        }

        public static OfflineRepo Open(string repoDir, L logger)
        {
            OfflineRepoGenerator.RecoverOrganization(Path.GetFullPath(repoDir));
            repoDir = Path.GetFullPath(repoDir);
            string manifestPath = Path.Combine(repoDir, "repo.manifest.json");
            string manifestText = ServerStorage.Read(repoDir, "repo.manifest.json");
            if (manifestText == null) throw new DirectoryNotFoundException("离线仓库缺少 manifest / repo manifest not found: " + manifestPath);
            var manifest = OfflineRepoGenerator.DeserializeManifest(manifestText);
            if (manifest.SchemaVersion != OfflineRepoManifest.CurrentSchema)
                throw new InvalidOperationException("离线仓库 manifest schema 版本不匹配: " + manifest.SchemaVersion);
            string recomputed = OfflineRepoGenerator.ComputeIntegrityHash(manifest);
            if (!string.Equals(recomputed, manifest.IntegrityHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("离线仓库 manifest 完整性校验失败 / manifest integrity hash mismatch (refusing to load untrusted manifest)");
            if (logger != null) logger.W("[INFO] 离线仓库 manifest 完整性校验通过 / Manifest integrity OK: " + repoDir);

            List<InfRecord> records = null;
            string dbPath = Path.Combine(repoDir, "repo.udadb");
            if (File.Exists(dbPath) && !string.IsNullOrEmpty(manifest.IndexDbHash))
            {
                string dbHash = Sha256FileStatic(dbPath);
                if (string.Equals(dbHash, manifest.IndexDbHash, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var idx = new DriverIndex(dbPath);
                        idx.Open();
                        records = idx.Records.ToList();
                        if (logger != null) logger.W("[INFO] 离线仓库索引数据库已加载 / Index DB loaded: " + idx.Count + " 个 INF");
                    }
                    catch (Exception ex) { if (logger != null) logger.W("[WARN] 索引数据库读取失败，将从 manifest 重建 / Index DB unreadable, rebuilding from manifest: " + ex.Message); records = null; }
                }
                else if (logger != null) logger.W("[WARN] 索引数据库哈希不匹配，将从 manifest 重建 / Index DB hash mismatch, rebuilding from manifest");
            }
            if (records == null)
            {
                records = manifest.Entries.Select(ToPortableRecord).ToList();
                if (logger != null) logger.W("[INFO] 从 manifest 重建索引 / Rebuilt index from manifest: " + records.Count + " 个 INF");
            }
            int backfilled = 0;
            records = BackfillModels(repoDir, manifest, records, logger, out backfilled);
            var repo = new OfflineRepo(repoDir, manifest, records);
            repo.BackfilledModels = backfilled;
            return repo;
        }

        /// <summary>One-time legacy-index migration: after Open() backfilled Models from the repo's own
        /// INFs, atomically rewrite repo.udadb (now carrying Models) plus a manifest with the matching
        /// IndexDbHash/integrity hash, using the same stage -> File.Replace pattern as the generator, so
        /// a crash can only leave the previous (valid) index in place. Subsequent starts load Models
        /// straight from the index DB and never rescan the INF tree again. Idempotent: returns false when
        /// there is nothing to persist. Never throws; a failed persistence only means the next start
        /// re-runs the backfill, it never invalidates the in-memory repo.</summary>
        public bool PersistUpgradedIndex(L logger)
        {
            try
            {
                if (BackfilledModels <= 0 || Manifest == null || Records == null) return false;
                string stagingDb = Path.Combine(RepoRoot, "repo.udadb.staging-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
                string stagingManifest = Path.Combine(RepoRoot, "repo.manifest.json.staging-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
                var idx = new DriverIndex(stagingDb);
                idx.ReplaceAll(Records);
                idx.Commit();
                var upgraded = new OfflineRepoManifest
                {
                    SchemaVersion = Manifest.SchemaVersion,
                    GeneratorVersion = Manifest.GeneratorVersion,
                    CreatedAt = Manifest.CreatedAt,
                    OsArchitecture = Manifest.OsArchitecture,
                    OsMajorVersion = Manifest.OsMajorVersion,
                    MachineManufacturer = Manifest.MachineManufacturer,
                    MachineModel = Manifest.MachineModel,
                    MachineSku = Manifest.MachineSku,
                    Entries = Manifest.Entries,
                    HiddenPaths = Manifest.HiddenPaths,
                    FileCount = Manifest.FileCount,
                    TotalBytes = Manifest.TotalBytes,
                    Warnings = new List<string>(Manifest.Warnings ?? new List<string>())
                };
                upgraded.IndexDbHash = Sha256FileStatic(stagingDb);
                upgraded.IntegrityHash = OfflineRepoGenerator.ComputeIntegrityHash(upgraded);
                File.WriteAllText(stagingManifest, OfflineRepoGenerator.SerializeManifest(upgraded), new UTF8Encoding(false));
                string dbPath = Path.Combine(RepoRoot, "repo.udadb");
                if (File.Exists(dbPath)) File.Replace(stagingDb, dbPath, null); else File.Move(stagingDb, dbPath);
                string manifestPath = Path.Combine(RepoRoot, "repo.manifest.json");
                if (File.Exists(manifestPath)) File.Replace(stagingManifest, manifestPath, null); else File.Move(stagingManifest, manifestPath);
                ServerStorage.Publish(RepoRoot, "repo.manifest.json");
                // Upgrade marker for operator diagnostics: the manifest on disk is now self-sufficient.
                File.WriteAllText(Path.Combine(RepoRoot, "repo.index-upgraded.flag"),
                    "index upgraded with Models backfill at " + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) +
                    " (" + BackfilledModels.ToString(CultureInfo.InvariantCulture) + " records)", new UTF8Encoding(false));
                Manifest = upgraded;
                try { if (File.Exists(stagingDb)) File.Delete(stagingDb); } catch { }
                try { if (File.Exists(stagingDb + ".tmp")) File.Delete(stagingDb + ".tmp"); } catch { }
                try { if (File.Exists(stagingDb + ".bak")) File.Delete(stagingDb + ".bak"); } catch { }
                try { if (File.Exists(stagingManifest)) File.Delete(stagingManifest); } catch { }
                if (logger != null) logger.W("[INFO] 旧索引已原子升级持久化 / Legacy index upgraded and persisted: " + RepoRoot +
                    " (" + BackfilledModels.ToString(CultureInfo.InvariantCulture) + " records backfilled; future starts will not rescan)");
                return true;
            }
            catch (Exception ex)
            {
                if (logger != null) logger.W("[WARN] 旧索引升级持久化失败（下次启动将重新回填，不影响当前可用仓库）/ Legacy index upgrade persist failed (backfill will rerun next start, current repo unaffected): " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Legacy-index compatibility: manifests written before the Models field existed yield records
        /// whose Models list is empty, which InfApplicability's device-ID gate rejects as
        /// NoMatchingModelEntry — every driver would be silently refused. Instead, any record with no
        /// Models is re-parsed from the INF stored in the repo itself (after verifying the file's
        /// SHA-256 against the manifest ContentHash). Records whose INF is missing, fails verification,
        /// or cannot be parsed are DROPPED with an explicit warning (never kept in a state where they
        /// would be silently rejected); the log tells the operator to rebuild the repository.
        /// </summary>
        private static List<InfRecord> BackfillModels(string repoDir, OfflineRepoManifest manifest,
            List<InfRecord> records, L logger, out int backfilled)
        {
            backfilled = 0;
            var byRel = new Dictionary<string, OfflineRepoEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in manifest.Entries) byRel[NormRel(e.RelativePath)] = e;
            var kept = new List<InfRecord>(records.Count);
            int dropped = 0;
            foreach (var rec in records)
            {
                bool unresolvedIds = rec.Models != null && rec.Models.Any(m =>
                    m.HardwareIds.Concat(m.CompatibleIds).Any(id => id.IndexOf('%') >= 0));
                bool invalidDecoration = (rec.Decorations ?? new List<string>()).Concat(
                    rec.Models != null ? rec.Models.Select(m => m.Decoration) : Enumerable.Empty<string>())
                    .Any(d => !string.IsNullOrEmpty(d) && !System.Text.RegularExpressions.Regex.IsMatch(d,
                        @"^NT(?:amd64|x86|arm64|arm)?(?:\.[0-9]*)*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                if (rec.Models != null && rec.Models.Count > 0 && !unresolvedIds && !invalidDecoration) { kept.Add(rec); continue; }
                OfflineRepoEntry e;
                if (!byRel.TryGetValue(NormRel(rec.Path), out e)) { kept.Add(rec); continue; }
                string abs = OfflineRepoGenerator.SafeAbsolute(repoDir, e.RelativePath);
                bool ok = false;
                if (abs != null && File.Exists(abs))
                {
                    bool hashOk = string.Equals(Sha256FileStatic(abs), e.ContentHash, StringComparison.OrdinalIgnoreCase);
                    if (hashOk)
                    {
                        try
                        {
                            var parsed = InfParser.Parse(abs, e.SourceName);
                            rec.Models = parsed.Models != null ? parsed.Models : new List<InfModelEntry>();
                            rec.Decorations = parsed.Decorations;
                            e.Models = rec.Models;
                            e.Decorations = rec.Decorations;
                            if (unresolvedIds || invalidDecoration)
                            {
                                rec.HardwareIds = parsed.HardwareIds;
                                rec.CompatibleIds = parsed.CompatibleIds;
                                e.HardwareIds = parsed.HardwareIds;
                                e.CompatibleIds = parsed.CompatibleIds;
                            }
                            backfilled++;
                            ok = true;
                        }
                        catch (Exception ex)
                        {
                            if (logger != null) logger.W("[WARN] 旧版索引补解析失败 / Legacy-index Models backfill parse failed: " + e.RelativePath + " => " + ex.Message);
                        }
                    }
                    else if (logger != null) logger.W("[WARN] 旧版索引补解析：INF 哈希不匹配，已剔除 / Legacy-index backfill: INF hash mismatch, record dropped: " + e.RelativePath);
                }
                else if (logger != null) logger.W("[WARN] 旧版索引补解析：INF 文件缺失，已剔除 / Legacy-index backfill: INF file missing, record dropped: " + e.RelativePath);
                if (ok) kept.Add(rec); else dropped++;
            }
            if (backfilled > 0 || dropped > 0)
            {
                if (logger != null)
                    logger.W("[INFO] 旧版索引兼容：从 INF 补解析 Models 条目 " + backfilled + " 条，剔除 " + dropped +
                        " 条（建议重建仓库以生成完整索引 / legacy index: backfilled " + backfilled + " records from their INFs, dropped " +
                        dropped + "; rebuild the repository for a complete index）");
            }
            return kept;
        }

        public MatchRanker CreateRanker() { return new MatchRanker(Records, new string[0]); }

        public OfflineRepoEntry EntryForRelative(string relative)
        {
            OfflineRepoEntry e;
            return _byRel.TryGetValue(NormRel(relative), out e) ? e : null;
        }

        /// <summary>Resolve a relative path to absolute under the repo root. Throws on traversal.</summary>
        public string AbsoluteFor(string relative)
        {
            string abs = OfflineRepoGenerator.SafeAbsolute(RepoRoot, relative);
            if (abs == null) throw new InvalidOperationException("路径越界 / path traversal rejected: " + relative);
            return abs;
        }

        /// <summary>Verify a single INF file's SHA-256 against its manifest entry.</summary>
        public bool VerifyFile(string relative)
        {
            var e = EntryForRelative(relative);
            if (e == null) return false;
            string abs = AbsoluteFor(relative);
            if (!File.Exists(abs))
            {
                string archivePath = OfflineRepoGenerator.DownloadArchivePath(RepoRoot, e);
                if (archivePath == null || !File.Exists(archivePath)) return false;
                string parent = string.IsNullOrEmpty(e.PackageRoot) ? Path.GetDirectoryName(e.RelativePath).Replace('\\', '/') : e.PackageRoot;
                string name = e.RelativePath.Substring(parent.Length + 1);
                using (var archive = System.IO.Compression.ZipFile.OpenRead(archivePath))
                {
                    var member = archive.GetEntry(name); if (member == null) return false;
                    using (var sha = SHA256.Create()) using (var stream = member.Open()) return string.Equals(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""), e.ContentHash, StringComparison.OrdinalIgnoreCase);
                }
            }
            return string.Equals(Sha256FileStatic(abs), e.ContentHash, StringComparison.OrdinalIgnoreCase);
        }

        public List<string> VerifyAll(CancellationToken ct)
        {
            var problems = new List<string>();
            foreach (var e in Manifest.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (!VerifyFile(e.RelativePath)) problems.Add(e.RelativePath);
            }
            return problems;
        }

        private static string NormRel(string rel) { return (rel ?? "").Replace('\\', '/').TrimStart('/'); }

        private static InfRecord ToPortableRecord(OfflineRepoEntry e)
        {
            return new InfRecord
            {
                Path = e.RelativePath, SourceName = e.SourceName, Provider = e.Provider, Manufacturer = e.Manufacturer,
                Class = e.Class, ClassGuid = e.ClassGuid, DriverVerDate = e.DriverVerDate, DriverVerVersion = e.DriverVerVersion,
                ContentHash = e.ContentHash, Decorations = new List<string>(e.Decorations),
                HardwareIds = new List<string>(e.HardwareIds), CompatibleIds = new List<string>(e.CompatibleIds),
                Models = e.Models != null ? new List<InfModelEntry>(e.Models) : new List<InfModelEntry>()
            };
        }

        private static string Sha256FileStatic(string path)
        {
            using (var s = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(s);
                var hex = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }

        public void Dispose() { }
    }

    // =================================================================================
    //  Shared install executor (gate sequence + runner; mirrors E.Install)
    // =================================================================================

    /// <summary>
    /// Executes the install gate sequence for a set of candidate INF records against an approved root,
    /// reusing the Phase 2 install seams. Used by the offline installer and the LAN client (after it has
    /// downloaded and verified a package into a client-controlled staging dir). WhatIf -> admin gate
    /// (deployment) -> registry export -> per-item: existence -> signature -> optional integrity (SHA-256)
    /// -> pnputil runner -> exit-code classification.
    /// </summary>
    public static class DriverInstallExecutor
    {
        /// <summary>Readable explanation for a pnputil "no applicable device" outcome (exit 259).</summary>
        public static string PnputilSkipText(InstallResult res)
        {
            if ((res.Output ?? "").IndexOf("设备上的驱动程序包是最新的", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (res.Output ?? "").IndexOf("best driver", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Windows 判定设备现有驱动已是最佳匹配，未更新；不代表候选已绑定 / Windows retained the best existing driver; candidate binding is not confirmed";
            return "pnputil 返回 " + res.ExitCode.ToString(CultureInfo.InvariantCulture) +
                "（无适用设备）：该 INF 与当前设备不匹配，或系统已安装同版/更高版驱动 / pnputil reported no applicable device for this INF";
        }

        public static Sum Execute(IList<InfRecord> candidates, IList<string> approvedRoots, bool dedupeByContent,
            bool whatIf, bool deploy, int installTimeoutSeconds, bool verifySignature,
            L logger, IAdminChecker admin, ISignatureVerifier sig, IPnpInstallRunner runner, ISystemOps sys,
            Func<string, bool> integrityCheck, Action<int, int, string> progress)
        {
            var z = new Sum();
            var plan = InstallPlanner.BuildPlan(candidates ?? new InfRecord[0], approvedRoots, dedupeByContent);
            if (whatIf)
            {
                z.Skip = plan.Accepted;
                foreach (var item in plan.Items)
                    z.Items.Add(new InstallItemResult { InfPath = item.InfPath, Order = item.Order, SkipReason = item.SkipReason.Length > 0 ? item.SkipReason : "WhatIf", Ran = false });
                if (logger != null) logger.W("[INFO] WhatIf 跳过安装 / Skipped install: 计划 " + plan.Accepted + " 项，规划阶段跳过 " + plan.Skipped + " 项");
                return z;
            }
            var adminChecker = admin ?? new WindowsAdminChecker();
            bool isAdmin = adminChecker.IsAdministrator();
            if (!isAdmin)
            {
                if (logger != null) logger.W("[WARN] 当前未以管理员身份运行 / Not elevated; pnputil may fail");
                if (deploy)
                {
                    foreach (var item in plan.Items) if (item.Order > 0)
                    {
                        z.Items.Add(new InstallItemResult { InfPath = item.InfPath, Order = item.Order, SkipReason = "需要管理员权限", Ran = false });
                        z.Fail++;
                    }
                    if (logger != null) logger.W("[ERROR] 部署模式要求管理员权限，已中止安装 / Deployment requires admin");
                    return z;
                }
            }
            if (sys != null) sys.ExportServicesRegistry(Path.GetTempPath(), logger);
            bool sigActive = verifySignature || sig != null;
            ISignatureVerifier sigVer = sig;
            if (sigVer == null) sigVer = sigActive ? (ISignatureVerifier)new WinTrustSignatureVerifier() : (ISignatureVerifier)new TrustingSignatureVerifier();
            IPnpInstallRunner run = runner ?? new PnpInstallRunner();
            int accepted = plan.Items.Count(i => i.Order > 0), done = 0;
            foreach (var item in plan.Items)
            {
                if (item.Order == 0) { z.Items.Add(new InstallItemResult { InfPath = item.InfPath, SkipReason = item.SkipReason, Ran = false }); continue; }
                if (!File.Exists(item.InfPath)) { z.Items.Add(new InstallItemResult { InfPath = item.InfPath, Order = item.Order, SkipReason = "INF 文件不存在", Ran = false }); z.Skip++; if (logger != null) logger.W("[WARN] 跳过（文件不存在）/ Missing: " + item.InfPath); continue; }
                if (integrityCheck != null && !integrityCheck(item.InfPath)) { z.Items.Add(new InstallItemResult { InfPath = item.InfPath, Order = item.Order, SkipReason = "完整性校验失败 (SHA-256)", Ran = false }); z.Skip++; if (logger != null) logger.W("[WARN] 跳过（哈希不匹配）/ Integrity check failed: " + item.InfPath); continue; }
                if (sigActive && !sigVer.IsTrusted(item.InfPath, logger)) { z.Items.Add(new InstallItemResult { InfPath = item.InfPath, Order = item.Order, SkipReason = "签名验证失败", Ran = false }); z.Skip++; if (logger != null) logger.W("[WARN] 跳过（签名验证失败）/ Signature not trusted: " + item.InfPath); continue; }
                done++; if (progress != null) progress(done, accepted, Path.GetFileName(item.InfPath));
                var res = run.AddInstall(item.InfPath, installTimeoutSeconds, logger);
                // A pnputil "skipped" outcome (exit 259 = no applicable device) must carry a readable
                // reason, never a bare count: a race between scan and install (driver already installed,
                // device gone, or the INF not applying to the live device) has to be explainable.
                string ranSkip = res.Outcome == InstallOutcome.Skipped ? PnputilSkipText(res) : "";
                z.Items.Add(new InstallItemResult { InfPath = item.InfPath, Order = item.Order, ExitCode = res.ExitCode, Outcome = res.Outcome, Output = res.Output, SkipReason = ranSkip, Ran = true });
                if (res.Outcome == InstallOutcome.Success) z.Ok++;
                else if (res.Outcome == InstallOutcome.SuccessReboot) { z.Ok++; z.Reboot = true; }
                else if (res.Outcome == InstallOutcome.Skipped) { z.Skip++; if (logger != null) logger.W("[INFO] 跳过 / Skipped " + Path.GetFileName(item.InfPath) + ": " + ranSkip); }
                else z.Fail++;
            }
            if (sys != null) sys.ScanDevices(60, logger);
            return z;
        }
    }

    // =================================================================================
    //  Offline installer (scan + install from a repo, reusing the engine seams)
    // =================================================================================

    /// <summary>
    /// Scans the target machine against a loaded offline repository and installs matched drivers. The scan
    /// builds an R result (compatible with the existing engine's reporting) using the repo's in-memory
    /// ranker; the install reuses DriverInstallExecutor with ApprovedRoots = { repo root } and a per-file
    /// SHA-256 integrity check against the manifest.
    /// </summary>
    public sealed class OfflineInstaller
    {
        private readonly OfflineRepo _repo;
        private readonly OsTarget _osTarget;
        private readonly IHardwareCollector _hc;
        private readonly Machine _machine;
        private readonly L _logger;
        private readonly ISignatureVerifier _sig;
        private readonly IAdminChecker _admin;
        private readonly IPnpInstallRunner _runner;
        private readonly ISystemOps _sys;
        private readonly bool _deploy, _whatIf, _force;
        private readonly int _installTimeout;
        private readonly bool _dedupe, _verifySig;
        private readonly Dictionary<string, OfflineRepoEntry> _entryByAbs = new Dictionary<string, OfflineRepoEntry>(StringComparer.OrdinalIgnoreCase);

        public OfflineInstaller(OfflineRepo repo, OsTarget osTarget, IHardwareCollector hc, Machine machine,
            L logger, bool deploy, bool whatIf, bool force, int installTimeoutSeconds,
            bool dedupeByContent, bool verifySignature,
            ISignatureVerifier sig, IAdminChecker admin, IPnpInstallRunner runner, ISystemOps sys)
        {
            _repo = repo;
            _osTarget = osTarget ?? new OsTarget();
            _hc = hc;
            _machine = machine;
            _logger = logger;
            _deploy = deploy; _whatIf = whatIf; _force = force;
            _installTimeout = installTimeoutSeconds;
            _dedupe = dedupeByContent; _verifySig = verifySignature;
            _sig = sig; _admin = admin; _runner = runner; _sys = sys;
        }

        public R Scan(CancellationToken ct, IProgress<string> progress)
        {
            var r = new R { PC = _machine ?? new Machine() };
            if (_logger != null) _logger.W("[INFO] 离线仓库扫描 / Offline scan: " + _repo.RepoRoot + " (" + _repo.Count + " 个 INF)");
            r.Mode = "离线仓库";
            r.ApprovedRoots = new List<string> { _repo.RepoRoot };

            var seenInf = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rec in _repo.Records)
            {
                if (ct.IsCancellationRequested) break;
                string abs = _repo.AbsoluteFor(rec.Path);
                if (!seenInf.Add(abs)) continue;
                _entryByAbs[abs] = _repo.EntryForRelative(rec.Path);
                r.Infs.Add(ToI(rec, abs));
            }

            var collector = _hc ?? new WmiHardwareCollector();
            var devices = collector.Collect(_logger);
            var ranker = _repo.CreateRanker();
            foreach (var d in devices)
            {
                if (ct.IsCancellationRequested) break;
                var cands = ranker.Rank(d.H, d.K, _osTarget);
                if (cands.Count == 0)
                {
                    // A device that genuinely needs a driver but has no applicable INF in the repo must be
                    // reported as "没有匹配驱动", never as an installable candidate that gets skipped later.
                    if (d.Bad || string.IsNullOrEmpty(d.Inf) || string.IsNullOrEmpty(d.Version))
                        r.Matches.Add(new X { D = d, I = new I { Path = "" }, State = "没有匹配驱动", Reason = "仓库中没有适用当前系统/架构且匹配该设备 ID 的驱动", Pick = false });
                    continue;
                }
                var best = cands[0];
                var rec = best.Record;
                string abs = _repo.AbsoluteFor(rec.Path);
                _entryByAbs[abs] = _repo.EntryForRelative(rec.Path);
                bool newer = DriverVersion.IsNewer(rec.DriverVerVersion, d.Version);
                var x = new X
                {
                    D = d,
                    I = ToI(rec, abs),
                    Type = best.Tier >= 3 ? "精确硬件 ID" : "兼容 ID",
                    MatchTier = best.Tier,
                    Reason = best.MatchedId,
                    Level = "L" + best.Tier,
                    Score = best.Score.MatchTypeRank * 100000 + best.Score.Specificity,
                    State = d.Bad ? "设备异常" : d.Inf == "" ? "缺少驱动" : newer ? "可更新" : "已是最新"
                };
                x.Pick = d.Bad || d.Inf == "" || newer || _force;
                r.Matches.Add(x);
            }
            if (_logger != null) _logger.W("[INFO] 离线匹配 / Offline matches: " + r.Matches.Count + " 项");
            return r;
        }

        public Sum Install(R r, bool full, Action<int, int, string> progress)
        {
            var pickedPaths = (full ? r.Infs.Select(x => x.Path) : r.Matches.Where(x => x.Pick).Select(x => x.I.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var candidates = new List<InfRecord>();
            foreach (var abs in pickedPaths)
            {
                OfflineRepoEntry e;
                if (!_entryByAbs.TryGetValue(abs, out e)) e = _repo.EntryForRelative(_repoRelative(abs));
                if (e == null) { candidates.Add(new InfRecord { Path = abs, ContentHash = "" }); continue; }
                candidates.Add(RecordFromEntry(e, abs));
            }
            return DriverInstallExecutor.Execute(candidates, r.ApprovedRoots, _dedupe, _whatIf, _deploy,
                _installTimeout, _verifySig, _logger, _admin, _sig, _runner, _sys,
                integrityCheck, progress);
        }

        private bool integrityCheck(string abs)
        {
            OfflineRepoEntry e;
            if (_entryByAbs.TryGetValue(abs, out e)) return _repo.VerifyFile(e.RelativePath);
            return _repo.VerifyFile(_repoRelative(abs));
        }

        private string _repoRelative(string abs)
        {
            try
            {
                string f = Path.GetFullPath(abs).TrimEnd('\\') + "\\";
                string root = _repo.RepoRoot.TrimEnd('\\') + "\\";
                if (f.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return f.Substring(root.Length).Replace('\\', '/');
            }
            catch { }
            return Path.GetFileName(abs);
        }

        private static InfRecord RecordFromEntry(OfflineRepoEntry e, string abs)
        {
            return new InfRecord
            {
                Path = abs, SourceName = e.SourceName, Provider = e.Provider, Manufacturer = e.Manufacturer,
                Class = e.Class, ClassGuid = e.ClassGuid, DriverVerDate = e.DriverVerDate, DriverVerVersion = e.DriverVerVersion,
                ContentHash = e.ContentHash, Decorations = new List<string>(e.Decorations),
                HardwareIds = new List<string>(e.HardwareIds), CompatibleIds = new List<string>(e.CompatibleIds),
                Models = e.Models != null ? new List<InfModelEntry>(e.Models) : new List<InfModelEntry>()
            };
        }

        private static I ToI(InfRecord rec, string abs)
        {
            var i = new I { Path = abs, Class = rec.Class, Version = rec.DriverVerVersion, Date = rec.DriverVerDate, DT = rec.Date() };
            foreach (var id in rec.HardwareIds) i.Ids.Add(id);
            foreach (var id in rec.CompatibleIds) i.Ids.Add(id);
            return i;
        }
    }

    // =================================================================================
    //  CLI entry point (OfflineRepoGenerator.exe) -- create / update / verify / scan / install
    // =================================================================================

    public static class OfflineRepoProgram
    {
        private sealed class ConsoleProgress : IProgress<string>
        {
            public void Report(string message) { Console.WriteLine("PROGRESS: " + message); Console.Out.Flush(); }
        }

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
                AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
                var p = OfflineRepoCli.Parse(args);
                if (p.Help || p.Action == null)
                {
                    Console.WriteLine("OfflineRepoGenerator --repo <dir> [--source <pkgdir>...] [--os amd64 --osver 10]");
                    Console.WriteLine("  [--machine Maker/Model/Sku] --create|--update|--verify|--prepare-packages");
                    Console.WriteLine("  --prepare-packages [--package-workers N] (default: all detected logical processors)");
                    Console.WriteLine("  [--scan|--install|--whatif] [--timeout 120]");
                    return 0;
                }
                string logDir = Environment.ExpandEnvironmentVariables("%ProgramData%\\UniversalDriverAssistant\\Logs");
                using (var log = new L(logDir))
                {
                    var sig = new WinTrustSignatureVerifier();
                    var gen = new OfflineRepoGenerator(sig, log);
                    if (p.Action == "verify")
                    {
                        var problems = gen.Verify(p.Repo, CancellationToken.None);
                        foreach (var prob in problems) Console.WriteLine("PROBLEM: " + prob);
                        Console.WriteLine(problems.Count == 0 ? "VERIFY: OK" : "VERIFY: FAILED (" + problems.Count + " problems)");
                        return problems.Count == 0 ? 0 : 1;
                    }
                    if (p.Action == "prepare-packages")
                    {
                        int created = OfflineRepoGenerator.PrepareDownloadPackages(p.Repo, new ConsoleProgress(), p.PackageWorkers);
                        Console.WriteLine("PACKAGES: READY (created " + created + ")");
                        return 0;
                    }
                    if (p.Action == "create" || p.Action == "update")
                    {
                        var osTarget = new OsTarget { Architecture = string.IsNullOrEmpty(p.OsArch) ? "amd64" : p.OsArch, MajorVersion = p.OsVer <= 0 ? 10 : p.OsVer };
                        var hints = new OfflineRepoMachineHints();
                        if (p.Machine != null)
                        {
                            string[] parts = p.Machine.Split('/');
                            if (parts.Length > 0) hints.Manufacturer = parts[0];
                            if (parts.Length > 1) hints.Model = parts[1];
                            if (parts.Length > 2) hints.SystemSKU = parts[2];
                        }
                        var progress = new ConsoleProgress();
                        var report = gen.Generate(p.Repo, p.Sources, osTarget, hints, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), CancellationToken.None, progress);
                        Console.WriteLine("Repo: " + report.RepoDir);
                        Console.WriteLine("  Added=" + report.Added + " Modified=" + report.Modified + " Removed=" + report.Removed + " Unchanged=" + report.Unchanged);
                        Console.WriteLine("  Duplicates=" + report.Duplicates + " Conflicts=" + report.Conflicts);
                        Console.WriteLine("  Files=" + report.FileCount + " Bytes=" + report.TotalBytes);
                        Console.WriteLine("  IndexDbHash=" + report.IndexDbHash);
                        Console.WriteLine("  IntegrityHash=" + report.IntegrityHash);
                        foreach (var w in report.Warnings) Console.WriteLine("  WARN: " + w);
                        return 0;
                    }
                    if (p.Action == "scan" || p.Action == "install")
                    {
                        var repo = OfflineRepo.Open(p.Repo, log);
                        var osTarget = new OsTarget { Architecture = string.IsNullOrEmpty(p.OsArch) ? "amd64" : p.OsArch, MajorVersion = p.OsVer <= 0 ? 10 : p.OsVer };
                        var installer = new OfflineInstaller(repo, osTarget, new WmiHardwareCollector(), null, log,
                            deploy: false, whatIf: p.WhatIf, force: false, installTimeoutSeconds: p.Timeout <= 0 ? 120 : p.Timeout,
                            dedupeByContent: true, verifySignature: true, sig: sig, admin: new WindowsAdminChecker(),
                            runner: null, sys: new RealSystemOps());
                        var r = installer.Scan(CancellationToken.None, null);
                        Console.WriteLine("Matches: " + r.Matches.Count);
                        foreach (var x in r.Matches)
                            Console.WriteLine("  " + x.D.Name + " <= " + Path.GetFileName(x.I.Path) + " [" + x.Level + "] " + x.State);
                        if (p.Action == "install")
                        {
                            foreach (var x in r.Matches) x.Pick = x.Pick || true;
                            var s = installer.Install(r, false, (a, b, n) => Console.WriteLine("  " + a + "/" + b + " " + n));
                            Console.WriteLine("Ok=" + s.Ok + " Skip=" + s.Skip + " Fail=" + s.Fail + (s.Reboot ? " (reboot)" : ""));
                            return s.Fail > 0 ? (s.Ok > 0 ? 1 : 2) : (s.Reboot ? 3010 : 0);
                        }
                        return 0;
                    }
                    return 0;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("OfflineRepoGenerator ERROR: " + ex);
                return 2;
            }
        }
    }

    public sealed class OfflineRepoCli
    {
        public string Action;       // create | update | verify | prepare-packages | scan | install
        public string Repo = "";
        public List<string> Sources = new List<string>();
        public string OsArch = "";
        public int OsVer;
        public string Machine;
        public bool WhatIf;
        public int Timeout;
        public int PackageWorkers;
        public bool Help;

        public static OfflineRepoCli Parse(string[] args)
        {
            var p = new OfflineRepoCli();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--create") p.Action = "create";
                else if (a == "--update") p.Action = "update";
                else if (a == "--verify") p.Action = "verify";
                else if (a == "--prepare-packages") p.Action = "prepare-packages";
                else if (a == "--scan") p.Action = "scan";
                else if (a == "--install") p.Action = "install";
                else if (a == "--whatif") p.WhatIf = true;
                else if (a == "--help" || a == "-h") p.Help = true;
                else if (a == "--repo" && i + 1 < args.Length) p.Repo = args[++i];
                else if (a == "--source" && i + 1 < args.Length) p.Sources.Add(args[++i]);
                else if (a == "--os" && i + 1 < args.Length) p.OsArch = args[++i];
                else if (a == "--osver" && i + 1 < args.Length) { int v; if (int.TryParse(args[++i], out v)) p.OsVer = v; }
                else if (a == "--machine" && i + 1 < args.Length) p.Machine = args[++i];
                else if (a == "--timeout" && i + 1 < args.Length) { int v; if (int.TryParse(args[++i], out v)) p.Timeout = v; }
                else if (a == "--package-workers" && i + 1 < args.Length) { int v; if (int.TryParse(args[++i], out v)) p.PackageWorkers = v; }
            }
            return p;
        }
    }
}
