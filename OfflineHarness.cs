// OfflineHarness.cs - Phase 3 offline repository end-to-end tests.
//
// Compiles alongside the real sources (Run-OfflineTests.ps1). Fully offline: synthetic INFs, a trusting
// signature verifier, a fake pnputil runner and no-op system ops. No real drivers are installed.
// Covers: generate + load + integrity, incremental update diff, conflict/duplicate detection, atomic
// generation (staging/old cleanup + consistency), integrity tamper detection, scan->install with the
// whitelist + per-file SHA-256 gate, OS/arch metadata + decoration filtering, and old-config compatibility.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace UDA
{
    internal static class OfflineHarness
    {
        private static int _asserts;
        private static int _failed;
        private static string _current;
        private static L _log;

        private static int Main(string[] args)
        {
            string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "uda-offline-tests");
            Directory.CreateDirectory(root);
            using (_log = new L(Path.Combine(root, "logs")))
            {
                Run("Repo.GenerateAndLoad", () => TestGenerateAndLoad(root));
                Run("Repo.Incremental", () => TestIncremental(root));
                Run("Repo.ConflictAndDuplicate", () => TestConflictAndDuplicate(root));
                Run("Repo.DedupeAuditAndClean", () => TestDedupeAuditAndClean(root));
                Run("Repo.AtomicGeneration", () => TestAtomicGeneration(root));
                Run("Repo.IntegrityTamper", () => TestIntegrityTamper(root));
                Run("Repo.ScanAndInstall", () => TestScanAndInstall(root));
                Run("Repo.OsArchMetadata", () => TestOsArchMetadata(root));
                Run("Repo.OldConfigCompat", () => TestOldConfigCompat(root));

                Console.WriteLine("-------------------------------------------------");
                Console.WriteLine("Asserts: " + _asserts + "  Failed: " + _failed);
                if (_failed > 0) { Console.WriteLine("RESULT: FAIL"); return 1; }
                Console.WriteLine("RESULT: PASS");
                return 0;
            }
        }

        private static void Run(string name, Action body)
        {
            _current = name;
            int before = _failed;
            try { body(); }
            catch (Exception ex) { Fail("threw: " + ex.GetType().Name + ": " + ex.Message); }
            if (_failed == before) Console.WriteLine("[PASS] " + name);
        }

        private static void T(bool condition, string message) { _asserts++; if (!condition) Fail(message); }
        private static void Fail(string message) { _failed++; Console.WriteLine("[FAIL] " + _current + ": " + message); }

        // ----- helpers -----

        private static string PrepareDir(string dir)
        {
            if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } }
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string WriteInf(string dir, string name, string hwid, string date, string version, string decoration)
        {
            string path = Path.Combine(dir, name);
            var sb = new StringBuilder()
                .AppendLine("[Version]")
                .AppendLine("Signature=\"$Windows NT$\"")
                .AppendLine("Class=Net")
                .AppendLine("ClassGuid={4d36e972-e325-11ce-bfc1-08002be10318}")
                .AppendLine("Provider=%Vendor%")
                .AppendLine("DriverVer=" + date + "," + version)
                .AppendLine("")
                .AppendLine("[Manufacturer]")
                .AppendLine("%Vendor%=Models" + (string.IsNullOrEmpty(decoration) ? "" : "," + decoration))
                .AppendLine("")
                .AppendLine("[Models" + (string.IsNullOrEmpty(decoration) ? "" : "." + decoration) + "]")
                .AppendLine("%Dev%=Install," + hwid)
                .AppendLine("")
                .AppendLine("[Strings]")
                .AppendLine("Vendor=\"Test Vendor\"")
                .AppendLine("Dev=\"Test Device " + hwid + "\"")
                .ToString();
            File.WriteAllText(path, sb, new UTF8Encoding(false));
            return path;
        }

        private static void WriteCompanion(string dir, string name, string content)
        {
            File.WriteAllText(Path.Combine(dir, name), content ?? "companion", new UTF8Encoding(false));
        }

        private static OfflineRepoGenerator NewGenerator() { return new OfflineRepoGenerator(new TrustingSignatureVerifier(), _log); }

        private static D Device(string hwid, string currentInf, string currentVersion)
        {
            return new D { Name = "Device " + hwid, Class = "Net", Inf = currentInf ?? "", Version = currentVersion ?? "",
                H = new List<string> { hwid }, K = new List<string>() };
        }

        // ---------------------------------------------------------------------------
        //  1. Generate + load + integrity.
        // ---------------------------------------------------------------------------
        private static void TestGenerateAndLoad(string root)
        {
            string baseDir = PrepareDir(Path.Combine(root, "gen"));
            string srcA = Path.Combine(baseDir, "PkgA"); Directory.CreateDirectory(srcA);
            string srcB = Path.Combine(baseDir, "PkgB"); Directory.CreateDirectory(srcB);
            WriteInf(srcA, "net_a.inf", "PCI\\VEN_1000&DEV_1000", "03/14/2026", "1.0.0.0", "NTamd64.10");
            WriteCompanion(srcA, "net_a.sys", "driver-bytes-A");
            WriteInf(srcB, "net_b.inf", "USB\\VID_2000&PID_2000", "06/01/2026", "2.0.0.0", "NTamd64.10");
            string repo = Path.Combine(baseDir, "repo");
            var gen = NewGenerator();
            var report = gen.Generate(repo, new[] { srcA, srcB }, new OsTarget { Architecture = "amd64", MajorVersion = 10 },
                new OfflineRepoMachineHints { Manufacturer = "TestVendor", Model = "ModelA", SystemSKU = "SKU1" },
                "2026-07-28T00:00:00Z", CancellationToken.None, null);

            T(report.Added == 2, "added=" + report.Added);
            T(report.FileCount == 2, "fileCount=" + report.FileCount);
            T(!string.IsNullOrEmpty(report.IntegrityHash), "integrity hash present");
            T(!string.IsNullOrEmpty(report.IndexDbHash), "index db hash present");
            T(File.Exists(Path.Combine(repo, "repo.manifest.json")), "manifest written");
            T(File.Exists(Path.Combine(repo, "repo.udadb")), "index db written");
            T(Directory.Exists(Path.Combine(repo, "packages")), "packages dir written");

            var repoObj = OfflineRepo.Open(repo, _log);
            T(repoObj.Count == 2, "loaded count=" + repoObj.Count);
            T(repoObj.Manifest.OsArchitecture == "multi", "osArch=" + repoObj.Manifest.OsArchitecture);
            T(repoObj.Manifest.OsMajorVersion == 0, "osMajor=" + repoObj.Manifest.OsMajorVersion);
            T(repoObj.Manifest.MachineManufacturer == "TestVendor", "machineMaker=" + repoObj.Manifest.MachineManufacturer);
            T(repoObj.Manifest.Entries.All(e => !string.IsNullOrEmpty(e.ContentHash)), "all entries hashed");
            T(repoObj.Manifest.Entries.All(e => e.SignatureStatus == "trusted"), "signature status trusted");
            T(repoObj.Manifest.Entries.All(e => e.Class == "Net"), "class metadata");
            T(repoObj.Manifest.Entries.All(e => e.ClassGuid == "{4d36e972-e325-11ce-bfc1-08002be10318}"), "classguid metadata");
            var problems = repoObj.VerifyAll(CancellationToken.None);
            T(problems.Count == 0, "verifyAll problems=" + problems.Count);
            // Index DB hash matched manifest, so the loaded records came from the .udadb (relative paths).
            T(repoObj.Records.All(r => !Path.IsPathRooted(r.Path)), "records carry relative paths (portable)");
        }

        // ---------------------------------------------------------------------------
        //  2. Incremental update diff (added / modified / removed / unchanged).
        // ---------------------------------------------------------------------------
        private static void TestIncremental(string root)
        {
            string baseDir = PrepareDir(Path.Combine(root, "incr"));
            string srcA = Path.Combine(baseDir, "PkgA"); Directory.CreateDirectory(srcA);
            string srcB = Path.Combine(baseDir, "PkgB"); Directory.CreateDirectory(srcB);
            WriteInf(srcA, "a.inf", "PCI\\VEN_2001&DEV_0001", "01/01/2026", "1.0.0.0", "NTamd64.10");
            WriteInf(srcB, "b.inf", "PCI\\VEN_2002&DEV_0002", "01/01/2026", "1.0.0.0", "NTamd64.10");
            string repo = Path.Combine(baseDir, "repo");
            var gen = NewGenerator();
            var r1 = gen.Generate(repo, new[] { srcA }, new OsTarget { Architecture = "amd64", MajorVersion = 10 }, null, "t1", CancellationToken.None, null);
            T(r1.Added == 1 && r1.FileCount == 1, "initial added=" + r1.Added);

            // Add B -> Added=1 (B), Unchanged=1 (A).
            var r2 = gen.MergeSource(repo, new[] { srcA, srcB }, srcB, new OsTarget { Architecture = "amd64", MajorVersion = 10 }, null, "t2", CancellationToken.None, null);
            T(r2.Added == 1 && r2.Unchanged == 1 && r2.Modified == 0 && r2.Removed == 0, "add: added=" + r2.Added + " unchanged=" + r2.Unchanged);

            // Modify A's INF content -> Modified=1, Unchanged=1 (B).
            WriteInf(srcA, "a.inf", "PCI\\VEN_2001&DEV_0001", "02/02/2026", "9.9.9.9", "NTamd64.10");
            var r3 = gen.MergeSource(repo, new[] { srcA, srcB }, srcA, new OsTarget { Architecture = "amd64", MajorVersion = 10 }, null, "t3", CancellationToken.None, null);
            T(r3.Modified == 1 && r3.Unchanged == 1, "modify: modified=" + r3.Modified + " unchanged=" + r3.Unchanged);

            // Remove A -> Removed=1, Unchanged=1 (B).
            var r4 = gen.Generate(repo, new[] { srcB }, new OsTarget { Architecture = "amd64", MajorVersion = 10 }, null, "t4", CancellationToken.None, null);
            T(r4.Removed == 1 && r4.Unchanged == 1 && r4.Added == 0, "remove: removed=" + r4.Removed + " unchanged=" + r4.Unchanged);
            T(OfflineRepo.Open(repo, _log).Count == 1, "after remove count=1");
        }

        // ---------------------------------------------------------------------------
        //  3. Conflict + duplicate detection.
        // ---------------------------------------------------------------------------
        private static void TestConflictAndDuplicate(string root)
        {
            string baseDir = PrepareDir(Path.Combine(root, "conf"));
            string srcA = Path.Combine(baseDir, "PkgA"); Directory.CreateDirectory(srcA);
            string srcB = Path.Combine(baseDir, "PkgB"); Directory.CreateDirectory(srcB);
            string srcC = Path.Combine(baseDir, "PkgC"); Directory.CreateDirectory(srcC);
            // A and B target the SAME canonical VEN/DEV but with DIFFERENT content -> conflict.
            WriteInf(srcA, "a.inf", "PCI\\VEN_3000&DEV_3000&SUBSYS_0001ABCD&REV_10", "01/01/2026", "1.0.0.0", "NTamd64.10");
            WriteInf(srcB, "b.inf", "PCI\\VEN_3000&DEV_3000&SUBSYS_0002ABCD&REV_10", "01/01/2026", "2.0.0.0", "NTamd64.10");
            // C has the same hardware applicability, version and decorations as A -> exact duplicate.
            WriteInf(srcC, "c.inf", "PCI\\VEN_3000&DEV_3000&SUBSYS_0001ABCD&REV_10", "01/01/2026", "1.0.0.0", "NTamd64.10");
            // Make C's INF byte-identical to A by rewriting A exactly (same text already).
            string repo = Path.Combine(baseDir, "repo");
            var gen = NewGenerator();
            var report = gen.Generate(repo, new[] { srcA, srcB, srcC }, new OsTarget { Architecture = "amd64", MajorVersion = 10 }, null, "t", CancellationToken.None, null);
            T(report.Conflicts >= 1, "conflicts=" + report.Conflicts);
            T(report.Duplicates >= 1, "duplicates=" + report.Duplicates);
            T(report.Warnings.Any(w => w.StartsWith("conflict:")), "conflict warning present");
            T(report.Warnings.Any(w => w.StartsWith("duplicate-driver:")), "duplicate warning present");
            var repoObj = OfflineRepo.Open(repo, _log);
            T(repoObj.Manifest.Entries.Any(e => e.Conflict), "an entry flagged conflict");
            T(repoObj.Manifest.Entries.Count == 2, "exact duplicate omitted, entries=" + repoObj.Manifest.Entries.Count);
        }

        // ---------------------------------------------------------------------------
        //  3b. Duplicate audit + in-place cleanup of an existing (legacy) repo.
        //      A generated repo is always deduped at build time, so the fixture hand-writes a
        //      duplicate entry + file into the manifest the way a pre-dedupe legacy repo would
        //      carry one, then drives AuditDuplicates / Clean end to end.
        // ---------------------------------------------------------------------------
        private static void TestDedupeAuditAndClean(string root)
        {
            string baseDir = PrepareDir(Path.Combine(root, "dedupe"));
            string srcA = Path.Combine(baseDir, "PkgA"); Directory.CreateDirectory(srcA);
            string srcB = Path.Combine(baseDir, "PkgB"); Directory.CreateDirectory(srcB);
            WriteInf(srcA, "a.inf", "PCI\\VEN_5100&DEV_5100", "01/01/2026", "1.0.0.0", "NTamd64.10");
            WriteCompanion(srcA, "a.cat", "catalog-A");
            WriteInf(srcB, "b.inf", "PCI\\VEN_5200&DEV_5200", "01/01/2026", "1.5.0.0", "NTamd64.10");
            string repo = Path.Combine(baseDir, "repo");
            var gen = NewGenerator();
            gen.Generate(repo, new[] { srcA, srcB }, new OsTarget { Architecture = "amd64", MajorVersion = 10 }, null, "t1", CancellationToken.None, null);

            // ----- legacy-repo fixture: inject an exact duplicate of A at a second path -----
            OfflineRepoManifest manifest = OfflineRepoGenerator.TryLoadManifest(repo);
            T(manifest != null && manifest.Entries.Count == 2, "fixture repo has 2 entries");
            OfflineRepoEntry original = manifest.Entries.First(e => e.RelativePath.EndsWith("a.inf", StringComparison.OrdinalIgnoreCase));
            // The duplicate lives in its OWN package directory, the way a duplicate copy shipped in a
            // second source package would — so its dead dir can be pruned wholesale after cleanup.
            string dupeRel = "packages/dupe-pkg/a-dupe.inf";
            string dupeAbs = Path.Combine(repo, dupeRel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dupeAbs));
            File.Copy(Path.Combine(repo, original.RelativePath.Replace('/', Path.DirectorySeparatorChar)), dupeAbs, true);
            // A real duplicate must carry the same catalog/companions, not just the same INF.
            File.Copy(Path.Combine(repo, Path.GetDirectoryName(original.RelativePath.Replace('/', Path.DirectorySeparatorChar)), "a.cat"), Path.Combine(Path.GetDirectoryName(dupeAbs), "a.cat"), true);
            var dupe = new OfflineRepoEntry
            {
                RelativePath = dupeRel,
                SourceName = original.SourceName,
                ContentHash = original.ContentHash,
                SignatureStatus = original.SignatureStatus,
                Provider = original.Provider,
                Manufacturer = original.Manufacturer,
                Class = original.Class,
                ClassGuid = original.ClassGuid,
                DriverVerDate = original.DriverVerDate,
                DriverVerVersion = original.DriverVerVersion,
                Decorations = new List<string>(original.Decorations),
                HardwareIds = new List<string>(original.HardwareIds),
                CompatibleIds = new List<string>(original.CompatibleIds),
                Size = original.Size
            };
            manifest.Entries.Add(dupe);
            manifest.FileCount = manifest.Entries.Count;
            manifest.TotalBytes = manifest.Entries.Sum(e => e.Size);
            RewriteRepoDerived(repo, manifest);

            // ----- audit: read-only, finds the group without touching anything -----
            var audit = gen.AuditDuplicates(repo, CancellationToken.None);
            T(audit.TotalCount == 3 && audit.KeptCount == 2 && audit.DuplicateCount == 1, "audit counts total=" + audit.TotalCount + " kept=" + audit.KeptCount + " dups=" + audit.DuplicateCount);
            T(audit.Groups.Count == 1 && audit.Groups[0].DuplicateRelativePaths.Count == 1, "audit one group");
            T(audit.Groups[0].KeptRelativePath.Equals(original.RelativePath, StringComparison.OrdinalIgnoreCase), "audit keeps the original");
            T(audit.Groups[0].DuplicateRelativePaths[0].Equals(dupeRel, StringComparison.OrdinalIgnoreCase), "audit names the duplicate");
            T(!audit.Cleaned, "audit is read-only");
            T(File.Exists(dupeAbs), "audit leaves files alone");
            T(OfflineRepo.Open(repo, _log).Count == 3, "audit leaves the repo loadable with all 3");

            // ----- clean: rewrite manifest+index, delete the duplicate, prune its dead dir -----
            var clean = gen.Clean(repo, CancellationToken.None, null);
            T(clean.Cleaned && clean.DuplicateCount == 1 && clean.KeptCount == 2, "clean removed=" + clean.DuplicateCount + " kept=" + clean.KeptCount);
            T(!File.Exists(dupeAbs), "duplicate INF file deleted");
            T(!Directory.Exists(Path.GetDirectoryName(dupeAbs)), "dead package dir pruned");
            T(File.Exists(Path.Combine(repo, original.RelativePath.Replace('/', Path.DirectorySeparatorChar))), "kept INF file still present");
            string keptPkgDir = Path.GetDirectoryName(Path.Combine(repo, original.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            T(Directory.GetFiles(keptPkgDir).Any(f => f.EndsWith(".cat", StringComparison.OrdinalIgnoreCase)), "kept package companion (cat) preserved");
            // The download zip is recursive (SearchOption.AllDirectories), so anything beneath a kept
            // package dir ships with it and must never be pruned: keep a sibling subdir alive.
            string subdir = Path.Combine(keptPkgDir, "sub"); Directory.CreateDirectory(subdir);
            File.WriteAllText(Path.Combine(subdir, "shared.dll"), "shared", new UTF8Encoding(false));
            gen.Clean(repo, CancellationToken.None, null);   // no-op pass; must not touch the subtree
            T(Directory.Exists(subdir) && File.Exists(Path.Combine(subdir, "shared.dll")), "subdir of kept package survives pruning");
            T(new DirectoryInfo(Path.Combine(repo, "packages")).GetFiles("repo.udadb.staging-*", SearchOption.AllDirectories).Length == 0, "no staging leftovers in packages");
            T(Directory.GetFiles(repo, "repo.udadb.staging-*").Length == 0 && Directory.GetFiles(repo, "repo.manifest.json.staging-*").Length == 0, "no staging leftovers in repo root");

            // Repo fully consistent after cleanup: verify + load + match the kept driver.
            T(NewGenerator().Verify(repo, CancellationToken.None).Count == 0, "verify clean after cleanup");
            var opened = OfflineRepo.Open(repo, _log);
            T(opened.Count == 2, "reload count=2");
            T(opened.Manifest.Warnings.Any(w => w.StartsWith("dedupe-clean:", StringComparison.Ordinal)), "cleanup warning recorded in manifest");
            var ranker = opened.CreateRanker();
            var verdicts = ranker.Rank(new[] { "PCI\\VEN_5100&DEV_5100" }, new string[0], new OsTarget { Architecture = "amd64", MajorVersion = 10 });
            T(verdicts.Count == 1 && verdicts[0].Record.DriverVerVersion == "1.0.0.0", "kept driver still matches after cleanup");

            // Idempotent: second audit finds nothing, second clean rewrites nothing.
            var audit2 = gen.AuditDuplicates(repo, CancellationToken.None);
            T(audit2.DuplicateCount == 0 && audit2.Groups.Count == 0, "second audit finds none");
            var clean2 = gen.Clean(repo, CancellationToken.None, null);
            T(!clean2.Cleaned && clean2.DuplicateCount == 0, "second clean is a no-op");
            T(NewGenerator().Verify(repo, CancellationToken.None).Count == 0, "verify still clean after no-op");
            // Same hardware/version/INF with a different catalog or binary is NOT a duplicate.
            string variant = Path.Combine(repo, "packages", "different-payload"); Directory.CreateDirectory(variant);
            File.Copy(Path.Combine(repo, original.RelativePath.Replace('/', Path.DirectorySeparatorChar)), Path.Combine(variant, "a.inf"));
            File.WriteAllText(Path.Combine(variant, "a.cat"), "different catalog bytes", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(variant, "a.sys"), "different binary bytes", new UTF8Encoding(false));
            var safetyAudit = gen.AuditDuplicates(repo, CancellationToken.None);
            T(safetyAudit.DuplicateCount == 0, "same-version different-payload legacy package retained");
            gen.Clean(repo, CancellationToken.None, null);
            T(File.Exists(Path.Combine(variant, "a.sys")), "cleanup never removes different-payload package");
        }

        /// <summary>Test fixture helper: after hand-editing a manifest, rebuild the index DB and both
        /// hashes so the repo still verifies like a legitimately written one.</summary>
        private static void RewriteRepoDerived(string repo, OfflineRepoManifest manifest)
        {
            string dbPath = Path.Combine(repo, "repo.udadb");
            var idx = new DriverIndex(dbPath);
            idx.ReplaceAll(manifest.Entries.Select(OfflineRepoGenerator.ToPortableRecord));
            idx.Commit();
            manifest.IndexDbHash = Sha256FileTest(dbPath);
            manifest.IntegrityHash = OfflineRepoGenerator.ComputeIntegrityHash(manifest);
            File.WriteAllText(Path.Combine(repo, "repo.manifest.json"), OfflineRepoGenerator.SerializeManifest(manifest), new UTF8Encoding(false));
        }

        private static string Sha256FileTest(string path)
        {
            using (var s = File.OpenRead(path))
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(s);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        // ---------------------------------------------------------------------------
        //  4. Atomic generation: staging/old cleanup + repo consistency.
        // ---------------------------------------------------------------------------
        private static void TestAtomicGeneration(string root)
        {
            string baseDir = PrepareDir(Path.Combine(root, "atomic"));
            string srcA = Path.Combine(baseDir, "PkgA"); Directory.CreateDirectory(srcA);
            WriteInf(srcA, "a.inf", "PCI\\VEN_4000&DEV_4000", "01/01/2026", "1.0.0.0", "NTamd64.10");
            string repo = Path.Combine(baseDir, "repo");
            var gen = NewGenerator();
            gen.Generate(repo, new[] { srcA }, new OsTarget { Architecture = "amd64", MajorVersion = 10 }, null, "t1", CancellationToken.None, null);
            // Update (regenerate) -> the old repo is swapped out and removed; no staging/old leftovers.
            WriteInf(srcA, "a.inf", "PCI\\VEN_4000&DEV_4000", "02/02/2026", "2.0.0.0", "NTamd64.10");
            gen.Generate(repo, new[] { srcA }, new OsTarget { Architecture = "amd64", MajorVersion = 10 }, null, "t2", CancellationToken.None, null);
            var di = new DirectoryInfo(baseDir);
            T(!di.GetDirectories("*.staging-*").Any(), "no staging leftover");
            T(!di.GetDirectories("*.old-*").Any(), "no old leftover");
            // Repo remains consistent: manifest integrity recomputes, all files verify.
            var repoObj = OfflineRepo.Open(repo, _log);
            T(repoObj.Count == 1, "repo count=1 after update");
            T(repoObj.VerifyAll(CancellationToken.None).Count == 0, "repo consistent after update");
            // The new version is reflected.
            T(repoObj.Manifest.Entries[0].DriverVerVersion == "2.0.0.0", "updated version=" + repoObj.Manifest.Entries[0].DriverVerVersion);
        }

        // ---------------------------------------------------------------------------
        //  5. Integrity tamper detection.
        // ---------------------------------------------------------------------------
        private static void TestIntegrityTamper(string root)
        {
            string baseDir = PrepareDir(Path.Combine(root, "tamper"));
            string srcA = Path.Combine(baseDir, "PkgA"); Directory.CreateDirectory(srcA);
            WriteInf(srcA, "a.inf", "PCI\\VEN_5000&DEV_5000", "01/01/2026", "1.0.0.0", "NTamd64.10");
            string repo = Path.Combine(baseDir, "repo");
            var gen = NewGenerator();
            gen.Generate(repo, new[] { srcA }, new OsTarget { Architecture = "amd64", MajorVersion = 10 }, null, "t", CancellationToken.None, null);
            T(gen.Verify(repo, CancellationToken.None).Count == 0, "clean verify");

            // Tamper a driver file -> hash mismatch detected by Verify.
            string infAbs = Directory.GetFiles(Path.Combine(repo, "packages"), "a.inf", SearchOption.AllDirectories)[0];
            File.AppendAllText(infAbs, "TAMPERED", new UTF8Encoding(false));
            var problems = gen.Verify(repo, CancellationToken.None);
            T(problems.Any(p => p.StartsWith("hash mismatch:")), "file tamper detected: " + string.Join("; ", problems));
            // Restore and tamper the manifest integrity hash instead -> Open must refuse to load.
            File.WriteAllText(infAbs, File.ReadAllText(infAbs, Encoding.UTF8).Replace("TAMPERED", ""), new UTF8Encoding(false));
            T(gen.Verify(repo, CancellationToken.None).Count == 0, "clean after restore");

            string manifestPath = Path.Combine(repo, "repo.manifest.json");
            string json = File.ReadAllText(manifestPath, Encoding.UTF8);
            // Flip one hex digit of the integrity hash.
            string origHash = OfflineRepoGenerator.DeserializeManifest(json).IntegrityHash;
            char c = origHash[0];
            char bad = c == 'a' ? 'b' : 'a';
            string tamperedJson = json.Replace("\"IntegrityHash\":\"" + origHash + "\"", "\"IntegrityHash\":\"" + bad + origHash.Substring(1) + "\"");
            File.WriteAllText(manifestPath, tamperedJson, new UTF8Encoding(false));
            bool threw = false;
            try { OfflineRepo.Open(repo, _log); }
            catch (InvalidOperationException) { threw = true; }
            T(threw, "tampered manifest integrity hash refused on load");
        }

        // ---------------------------------------------------------------------------
        //  6. Scan + install from repo (whitelist = repo root + per-file SHA-256 gate).
        // ---------------------------------------------------------------------------
        private static void TestScanAndInstall(string root)
        {
            string baseDir = PrepareDir(Path.Combine(root, "scan"));
            string srcA = Path.Combine(baseDir, "PkgA"); Directory.CreateDirectory(srcA);
            string inf = WriteInf(srcA, "dev.inf", "PCI\\VEN_6000&DEV_6000", "03/14/2026", "2.0.0.0", "NTamd64.10");
            string repo = Path.Combine(baseDir, "repo");
            var gen = NewGenerator();
            gen.Generate(repo, new[] { srcA }, new OsTarget { Architecture = "amd64", MajorVersion = 10 }, null, "t", CancellationToken.None, null);
            var repoObj = OfflineRepo.Open(repo, _log);

            var devices = new List<D> { Device("PCI\\VEN_6000&DEV_6000", "", "") };
            var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
            var installer = new OfflineInstaller(repoObj, new OsTarget { Architecture = "amd64", MajorVersion = 10 },
                new StaticHardwareCollector(devices), null, _log, deploy: false, whatIf: false, force: false,
                installTimeoutSeconds: 5, dedupeByContent: true, verifySignature: true,
                sig: new TrustingSignatureVerifier(), admin: new AlwaysAdminChecker(), runner: runner, sys: new NoOpSystemOps());
            var r = installer.Scan(CancellationToken.None, null);
            T(r.Matches.Count == 1, "matches=" + r.Matches.Count);
            T(r.ApprovedRoots.Count == 1 && string.Equals(r.ApprovedRoots[0], repoObj.RepoRoot, StringComparison.OrdinalIgnoreCase), "approved root = repo root");
            T(r.Matches[0].MatchTier == 4, "tier=" + r.Matches[0].MatchTier);
            T(r.Matches[0].I.Path.EndsWith("dev.inf", StringComparison.OrdinalIgnoreCase), "match inf path");
            r.Matches[0].Pick = true;
            var s = installer.Install(r, false, null);
            T(s.Ok == 1 && runner.Calls.Count == 1, "install ok=" + s.Ok + " calls=" + runner.Calls.Count);
            // The installed path is the repo's INF (under the approved repo root).
            T(runner.Calls[0].EndsWith("dev.inf", StringComparison.OrdinalIgnoreCase), "installed repo inf");

            // Whitelist: an INF outside the repo root is rejected. Build a candidate pointing elsewhere.
            var outside = new List<InfRecord> { new InfRecord { Path = inf, ContentHash = "" } }; // srcA path, not under repo root
            var sOut = DriverInstallExecutor.Execute(outside, new List<string> { repoObj.RepoRoot }, true, false, false, 5, false,
                _log, new AlwaysAdminChecker(), new TrustingSignatureVerifier(), runner, new NoOpSystemOps(), null, null);
            T(sOut.Items.Count == 1 && sOut.Items[0].SkipReason == "outside approved source root", "outside repo rejected: " + sOut.Items[0].SkipReason);

            // Integrity gate: tamper the repo INF after load -> install skips with a hash-mismatch reason.
            string repoInf = Directory.GetFiles(Path.Combine(repo, "packages"), "dev.inf", SearchOption.AllDirectories)[0];
            File.AppendAllText(repoInf, "X", new UTF8Encoding(false));
            var runner2 = new FakePnpInstallRunner { DefaultExitCode = 0 };
            var installer2 = new OfflineInstaller(repoObj, new OsTarget { Architecture = "amd64", MajorVersion = 10 },
                new StaticHardwareCollector(devices), null, _log, false, false, false, 5, true, true,
                new TrustingSignatureVerifier(), new AlwaysAdminChecker(), runner2, new NoOpSystemOps());
            var r2 = installer2.Scan(CancellationToken.None, null);
            r2.Matches[0].Pick = true;
            var s2 = installer2.Install(r2, false, null);
            T(s2.Skip == 1 && s2.Ok == 0 && runner2.Calls.Count == 0, "tampered inf skipped: skip=" + s2.Skip + " calls=" + runner2.Calls.Count);
            T(s2.Items[0].SkipReason == "完整性校验失败 (SHA-256)", "integrity skip reason=" + s2.Items[0].SkipReason);
        }

        // ---------------------------------------------------------------------------
        //  7. OS/arch metadata + decoration filtering.
        // ---------------------------------------------------------------------------
        private static void TestOsArchMetadata(string root)
        {
            string baseDir = PrepareDir(Path.Combine(root, "osarch"));
            string srcA = Path.Combine(baseDir, "PkgA"); Directory.CreateDirectory(srcA);
            WriteInf(srcA, "amd64.inf", "PCI\\VEN_7000&DEV_7000", "01/01/2026", "1.0.0.0", "NTamd64.10");
            WriteInf(srcA, "x86.inf", "PCI\\VEN_7001&DEV_7001", "01/01/2026", "1.0.0.0", "NTx86.10");
            WriteInf(srcA, "universal.inf", "PCI\\VEN_7002&DEV_7002", "01/01/2026", "1.0.0.0", "");
            string repo = Path.Combine(baseDir, "repo");
            var gen = NewGenerator();
            var report = gen.Generate(repo, new[] { srcA }, new OsTarget { Architecture = "amd64", MajorVersion = 10 }, null, "t", CancellationToken.None, null);
            // Repository construction is platform-neutral: every architecture is retained.
            T(report.FileCount == 3, "fileCount=" + report.FileCount + " (all architectures retained)");
            var repoObj = OfflineRepo.Open(repo, _log);
            T(repoObj.Manifest.Entries.Any(e => e.RelativePath.Contains("amd64")), "amd64 entry present");
            T(repoObj.Manifest.Entries.Any(e => e.RelativePath.Contains("universal")), "universal entry present");
            T(repoObj.Manifest.Entries.Any(e => e.RelativePath.Contains("x86")), "x86 entry retained");
            T(repoObj.Manifest.OsArchitecture == "multi" && repoObj.Manifest.OsMajorVersion == 0, "repository marked multi-platform");
            var ranker = repoObj.CreateRanker();
            T(ranker.Rank(new[] { "PCI\\VEN_7001&DEV_7001" }, new string[0],
                new OsTarget { Architecture = "amd64", MajorVersion = 10 }).Count == 0, "x86 rejected at request time for amd64 client");
            T(ranker.Rank(new[] { "PCI\\VEN_7001&DEV_7001" }, new string[0],
                new OsTarget { Architecture = "x86", MajorVersion = 10 }).Count == 1, "x86 accepted at request time for x86 client");
            T(repoObj.Manifest.Entries.All(e => e.Decorations != null && e.Decorations.Count > 0), "decorations recorded");
            T(repoObj.Manifest.Entries.All(e => e.HardwareIds.Count > 0), "hardware ids recorded");
        }

        // ---------------------------------------------------------------------------
        //  8. Old config compatibility: an old DriverConfig.json (no Phase 2/3 fields) loads with
        //     defaults and drives the offline installer end-to-end.
        // ---------------------------------------------------------------------------
        private static void TestOldConfigCompat(string root)
        {
            string baseDir = PrepareDir(Path.Combine(root, "compat"));
            string srcA = Path.Combine(baseDir, "PkgA"); Directory.CreateDirectory(srcA);
            WriteInf(srcA, "dev.inf", "PCI\\VEN_8000&DEV_8000", "03/14/2026", "2.0.0.0", "NTamd64.10");
            string repo = Path.Combine(baseDir, "repo");
            NewGenerator().Generate(repo, new[] { srcA }, new OsTarget { Architecture = "amd64", MajorVersion = 10 }, null, "t", CancellationToken.None, null);

            // Minimal old-style config (no Phase 2 fields at all).
            string configPath = Path.Combine(baseDir, "old-config.json");
            File.WriteAllText(configPath, "{\"General\":{\"LogDirectory\":\"" + (baseDir + "\\logs").Replace("\\", "\\\\") +
                "\",\"ReportDirectory\":\"" + (baseDir + "\\reports").Replace("\\", "\\\\") +
                "\",\"InstallTimeoutSeconds\":5,\"DeploymentCountdownSeconds\":0,\"AutoReboot\":false,\"CreateRestorePoint\":false," +
                "\"ScanAllSourcesWhenModelPackageMissing\":true,\"ScanAllSourcesAfterModelPackage\":false,\"IncludeDisconnectedDevices\":false}," +
                "\"Sources\":[],\"ModelMappings\":[],\"Categories\":{\"Other\":[]},\"Filters\":{\"IncludeCategories\":[],\"ExcludeHardwareIds\":[],\"OnlyMissingOrProblemDevicesInMaintenance\":false}}",
                new UTF8Encoding(false));
            C c = C.Load(configPath);
            T(c.General.DedupeInfByContent, "dedupe default");
            T(c.General.VerifyDriverSignature, "verifySig default");
            T(c.General.InstallTimeoutSeconds == 5, "installTimeout=" + c.General.InstallTimeoutSeconds);

            // The offline path reads these general settings even from an old config.
            var repoObj = OfflineRepo.Open(repo, _log);
            var devices = new List<D> { Device("PCI\\VEN_8000&DEV_8000", "", "") };
            var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
            var installer = new OfflineInstaller(repoObj, new OsTarget { Architecture = "amd64", MajorVersion = 10 },
                new StaticHardwareCollector(devices), null, _log, c.General == null ? false : false, false, false,
                c.General.InstallTimeoutSeconds, c.General.DedupeInfByContent, c.General.VerifyDriverSignature,
                new TrustingSignatureVerifier(), new AlwaysAdminChecker(), runner, new NoOpSystemOps());
            var r = installer.Scan(CancellationToken.None, null);
            r.Matches[0].Pick = true;
            var s = installer.Install(r, false, null);
            T(s.Ok == 1, "old-config offline install ok=" + s.Ok);
        }
    }
}
