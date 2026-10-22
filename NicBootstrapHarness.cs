// NicBootstrapHarness.cs - Phase 4 NIC bootstrap supply chain tests.
//
// Compiles alongside the real sources (Run-NicBootstrapTests.ps1). Uses the real NicBootstrapImporter /
// NicBootstrapCoverageBuilder / NicBootstrapVerifier / NicBootstrapPackager / NicBootstrap.TryInstall with
// synthetic Net-class INFs, a trusting signature verifier (and a rejecting one for the unsigned case), a fake
// pnputil runner and no-op system ops. No real drivers are installed, nothing is downloaded.
// Covers: license allow/deny, Net-class filter, signature + expected-hash rejection, path traversal
// (tampered catalog), duplicate-old-version supersession, coverage report gaps, two-version packaging,
// missing-repo safe downgrade, and verify (happy path + packaging gate).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace UDA
{
    internal static class NicBootstrapHarness
    {
        private static int _asserts;
        private static int _failed;
        private static string _current;
        private static L _log;
        private static string _sourcesJson;

        private static int Main(string[] args)
        {
            string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "uda-nic-tests");
            Directory.CreateDirectory(root);
            // Use the real curated sources catalog shipped next to the sources.
            _sourcesJson = Path.Combine(Path.GetDirectoryName(typeof(NicBootstrapHarness).Assembly.Location ?? "") ?? "", NicBootstrapSources.DefaultFileName);
            if (!File.Exists(_sourcesJson)) _sourcesJson = Path.Combine(Environment.CurrentDirectory, NicBootstrapSources.DefaultFileName);
            using (_log = new L(Path.Combine(root, "logs")))
            {
                Run("Nic.LicenseAllowDeny", () => TestLicenseAllowDeny(root));
                Run("Nic.NetClassFilter", () => TestNetClassFilter(root));
                Run("Nic.SignatureAndHash", () => TestSignatureAndHash(root));
                Run("Nic.PathTraversal", () => TestPathTraversal(root));
                Run("Nic.DuplicateOldVersion", () => TestDuplicateOldVersion(root));
                Run("Nic.CoverageReport", () => TestCoverageReport(root));
                Run("Nic.TwoVersionPackaging", () => TestTwoVersionPackaging(root));
                Run("Nic.MissingRepoSafeDowngrade", () => TestMissingRepoSafeDowngrade(root));
                Run("Nic.VerifyHappyPath", () => TestVerifyHappyPath(root));

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

        private static string PrepareDir(string dir) { if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } } Directory.CreateDirectory(dir); return dir; }

        private static NicBootstrapSourcesCatalog Sources() { return NicBootstrapSources.LoadOrDefault(_sourcesJson); }

        /// <summary>Write a Net-class INF (or another class when class/classGuid are overridden) for a hwid.</summary>
        private static string WriteInf(string dir, string name, string hwid, string date, string version, string decoration,
            string cls = "Net", string classGuid = NetClassGuid)
        {
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, new StringBuilder()
                .AppendLine("[Version]")
                .AppendLine("Signature=\"$Windows NT$\"")
                .AppendLine("Class=" + cls)
                .AppendLine("ClassGuid=" + classGuid)
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
                .ToString(), new UTF8Encoding(false));
            return path;
        }

        private const string NetClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}";
        private const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";

        private static string Sha256File(string path)
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

        private static NicBootstrapImportReport Import(string repoDir, IList<NicBootstrapImportSource> sources,
            ISignatureVerifier sig, bool acceptUnsigned = false)
        {
            var importer = new NicBootstrapImporter(sig, _log);
            return importer.Import(repoDir, sources, Sources(), new OsTarget { Architecture = "amd64", MajorVersion = 10 },
                "2026-07-28T00:00:00Z", acceptUnsigned, CancellationToken.None, null);
        }

        private static NicBootstrapCatalog CatalogOf(string repoDir) { return NicBootstrapCatalogStore.TryLoad(repoDir); }

        private sealed class RejectingSignatureVerifier : ISignatureVerifier { public bool IsTrusted(string infPath, L logger) { return false; } }

        // ---------------------------------------------------------------------------
        //  1. License allow/deny: VirtIO (redistributable) vs Intel (restricted) vs unknown.
        // ---------------------------------------------------------------------------
        private static void TestLicenseAllowDeny(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "license"));
            string virtioSrc = Path.Combine(dir, "VirtIO"); Directory.CreateDirectory(virtioSrc);
            WriteInf(virtioSrc, "virtio.inf", "PCI\\VEN_1AF4&DEV_1000", "01/01/2026", "1.0.0.0", "NTamd64.10");
            string intelSrc = Path.Combine(dir, "Intel"); Directory.CreateDirectory(intelSrc);
            WriteInf(intelSrc, "intel.inf", "PCI\\VEN_8086&DEV_15F3", "02/01/2026", "1.2.3.4", "NTamd64.10");
            string unknownSrc = Path.Combine(dir, "Unknown"); Directory.CreateDirectory(unknownSrc);
            WriteInf(unknownSrc, "unk.inf", "PCI\\VEN_9999&DEV_9999", "01/01/2026", "0.9.0.0", "NTamd64.10");

            var sources = new List<NicBootstrapImportSource> {
                new NicBootstrapImportSource { Directory = virtioSrc, SourceName = "virtio" },
                new NicBootstrapImportSource { Directory = intelSrc, SourceName = "intel" },
                new NicBootstrapImportSource { Directory = unknownSrc, SourceName = "unknown" }
            };
            var report = Import(Path.Combine(dir, "bootRepo"), sources, new TrustingSignatureVerifier());
            T(report.Rebuilt, "import rebuilt (rejections=" + report.RejectedInfs + ")");
            var catalog = CatalogOf(Path.Combine(dir, "bootRepo"));
            T(catalog != null, "catalog written");
            var byName = catalog.Packages.ToDictionary(p => p.SourceName, StringComparer.OrdinalIgnoreCase);
            T(byName.ContainsKey("virtio") && byName["virtio"].RedistributionAllowed, "VirtIO redistributable (license allow)");
            T(byName.ContainsKey("virtio") && byName["virtio"].Vendor == "VirtIO (Red Hat)", "VirtIO vendor resolved");
            T(byName.ContainsKey("intel") && !byName["intel"].RedistributionAllowed, "Intel NOT redistributable (license deny, default)");
            T(byName.ContainsKey("intel") && byName["intel"].LicenseStatus == "restricted", "Intel license=restricted");
            T(byName.ContainsKey("unknown") && !byName["unknown"].RedistributionAllowed, "unknown vendor NOT redistributable by default");
            // The override flag forces redistributable even for a restricted vendor (operator confirmed license).
            var sources2 = new List<NicBootstrapImportSource> {
                new NicBootstrapImportSource { Directory = intelSrc, SourceName = "intel", RedistributionAllowedOverride = true }
            };
            var report2 = Import(Path.Combine(dir, "bootRepo2"), sources2, new TrustingSignatureVerifier());
            var cat2 = CatalogOf(Path.Combine(dir, "bootRepo2"));
            T(cat2.Packages[0].RedistributionAllowed, "Intel override -> redistributable when operator confirms license");
        }

        // ---------------------------------------------------------------------------
        //  2. Net-class filter: a Display INF is rejected; only the Net INF is cataloged.
        // ---------------------------------------------------------------------------
        private static void TestNetClassFilter(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "netclass"));
            string src = Path.Combine(dir, "MixedPkg"); Directory.CreateDirectory(src);
            WriteInf(src, "nic.inf", "PCI\\VEN_8086&DEV_15F3", "01/01/2026", "1.0.0.0", "NTamd64.10", "Net", NetClassGuid);
            WriteInf(src, "gpu.inf", "PCI\\VEN_10DE&DEV_1B80", "01/01/2026", "2.0.0.0", "NTamd64.10", "Display", DisplayClassGuid);

            var report = Import(Path.Combine(dir, "bootRepo"), new List<NicBootstrapImportSource> {
                new NicBootstrapImportSource { Directory = src, SourceName = "mixed" }
            }, new TrustingSignatureVerifier());
            T(report.RejectedInfs >= 1, "Display INF rejected, rejections=" + report.RejectedInfs);
            T(report.Rejections.Any(r => r.IndexOf("not a Net class", StringComparison.OrdinalIgnoreCase) >= 0), "rejection reason mentions Net class");
            var catalog = CatalogOf(Path.Combine(dir, "bootRepo"));
            T(catalog != null && catalog.Packages.Count == 1, "one package cataloged");
            T(catalog.Packages[0].Infs.Count == 1, "only the Net INF cataloged, infs=" + (catalog.Packages[0].Infs.Count));
            T(catalog.Packages[0].Infs[0].RelativePath.EndsWith("nic.inf", StringComparison.OrdinalIgnoreCase), "cataloged INF is nic.inf");
        }

        // ---------------------------------------------------------------------------
        //  3. Signature + expected-hash: unsigned rejected; hash mismatch rejects the source.
        // ---------------------------------------------------------------------------
        private static void TestSignatureAndHash(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "sighash"));
            string src = Path.Combine(dir, "Pkg"); Directory.CreateDirectory(src);
            string infPath = WriteInf(src, "nic.inf", "PCI\\VEN_1AF4&DEV_1000", "01/01/2026", "1.0.0.0", "NTamd64.10");
            string hash = Sha256File(infPath);

            // (a) Rejecting verifier -> unsigned rejected, repo not built.
            var repReject = Import(Path.Combine(dir, "bootReject"), new List<NicBootstrapImportSource> {
                new NicBootstrapImportSource { Directory = src, SourceName = "pkg" }
            }, new RejectingSignatureVerifier());
            T(repReject.RejectedInfs >= 1, "unsigned rejected, rejections=" + repReject.RejectedInfs);
            T(repReject.Rejections.Any(r => r.IndexOf("unsigned", StringComparison.OrdinalIgnoreCase) >= 0), "rejection reason mentions unsigned");
            T(!repReject.Rebuilt, "unsigned: repo not built");

            // (a2) accept-unsigned -> accepted with status accepted-unsigned.
            var repAccept = Import(Path.Combine(dir, "bootAccept"), new List<NicBootstrapImportSource> {
                new NicBootstrapImportSource { Directory = src, SourceName = "pkg" }
            }, new RejectingSignatureVerifier(), acceptUnsigned: true);
            T(repAccept.Rebuilt, "accept-unsigned: repo built");
            var catAccept = CatalogOf(Path.Combine(dir, "bootAccept"));
            T(catAccept.Packages[0].SignatureStatus == "accepted-unsigned", "signature status accepted-unsigned, got " + catAccept.Packages[0].SignatureStatus);

            // (b) Correct expected hash -> accepted; wrong -> source rejected.
            var repOk = Import(Path.Combine(dir, "bootHashOk"), new List<NicBootstrapImportSource> {
                new NicBootstrapImportSource { Directory = src, SourceName = "pkg", ExpectedSha256 = hash }
            }, new TrustingSignatureVerifier());
            T(repOk.Rebuilt, "correct expected hash: repo built");
            var repBad = Import(Path.Combine(dir, "bootHashBad"), new List<NicBootstrapImportSource> {
                new NicBootstrapImportSource { Directory = src, SourceName = "pkg", ExpectedSha256 = new string('0', 64) }
            }, new TrustingSignatureVerifier());
            T(repBad.Rejections.Any(r => r.IndexOf("SHA-256 mismatch", StringComparison.OrdinalIgnoreCase) >= 0), "wrong hash: mismatch rejection");
            T(!repBad.Rebuilt, "wrong hash: repo not built");
        }

        // ---------------------------------------------------------------------------
        //  4. Path traversal: a tampered catalog entry with ".." is flagged by verify.
        // ---------------------------------------------------------------------------
        private static void TestPathTraversal(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "traversal"));
            string src = Path.Combine(dir, "Pkg"); Directory.CreateDirectory(src);
            WriteInf(src, "nic.inf", "PCI\\VEN_1AF4&DEV_1000", "01/01/2026", "1.0.0.0", "NTamd64.10");
            string repo = Path.Combine(dir, "bootRepo");
            Import(repo, new List<NicBootstrapImportSource> { new NicBootstrapImportSource { Directory = src, SourceName = "pkg" } }, new TrustingSignatureVerifier());

            // Tamper: rewrite the catalog with an entry whose relative path escapes the repo, recomputing the
            // integrity hash so the only problem verify reports is the path traversal.
            string catPath = Path.Combine(repo, NicBootstrapCatalogStore.FileName);
            var catalog = NicBootstrapCatalogStore.Deserialize(File.ReadAllText(catPath, Encoding.UTF8));
            T(catalog.Packages.Count == 1 && catalog.Packages[0].Infs.Count == 1, "catalog has one inf to tamper");
            catalog.Packages[0].Infs[0].RelativePath = "../../../Windows/evil.inf";
            catalog.IntegrityHash = NicBootstrapCatalogStore.ComputeIntegrityHash(catalog);
            File.WriteAllText(catPath, NicBootstrapCatalogStore.Serialize(catalog), new UTF8Encoding(false));

            var problems = NicBootstrapVerifier.Verify(repo, false, false, _log);
            T(problems.Any(p => p.IndexOf("path traversal", StringComparison.OrdinalIgnoreCase) >= 0), "verify flags path traversal, problems=" + string.Join("; ", problems));
        }

        // ---------------------------------------------------------------------------
        //  5. Duplicate old version: same canonical + arch -> keep newest, supersede older.
        // ---------------------------------------------------------------------------
        private static void TestDuplicateOldVersion(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "dup"));
            string oldSrc = Path.Combine(dir, "Old"); Directory.CreateDirectory(oldSrc);
            WriteInf(oldSrc, "old.inf", "PCI\\VEN_8086&DEV_15F3", "01/01/2025", "1.0.0.0", "NTamd64.10");
            string newSrc = Path.Combine(dir, "New"); Directory.CreateDirectory(newSrc);
            WriteInf(newSrc, "new.inf", "PCI\\VEN_8086&DEV_15F3", "03/14/2026", "2.0.0.0", "NTamd64.10");

            var report = Import(Path.Combine(dir, "bootRepo"), new List<NicBootstrapImportSource> {
                new NicBootstrapImportSource { Directory = oldSrc, SourceName = "old" },
                new NicBootstrapImportSource { Directory = newSrc, SourceName = "new" }
            }, new TrustingSignatureVerifier());
            T(report.Superseded >= 1, "older version superseded, superseded=" + report.Superseded);
            T(report.Rejections.Any(r => r.IndexOf("superseded", StringComparison.OrdinalIgnoreCase) >= 0), "superseded rejection recorded");
            var catalog = CatalogOf(Path.Combine(dir, "bootRepo"));
            int totalInfs = catalog.Packages.Sum(p => p.Infs.Count);
            T(totalInfs == 1, "only the newer INF cataloged, totalInfs=" + totalInfs);
            T(catalog.Packages.Count == 1, "only the newer package emitted, packages=" + catalog.Packages.Count);
            T(catalog.Packages[0].Infs[0].RelativePath.EndsWith("new.inf", StringComparison.OrdinalIgnoreCase), "newer INF is the survivor");
        }

        // ---------------------------------------------------------------------------
        //  6. Coverage report: covered vendors/IDs vs gaps (missing vendor + missing USB NIC).
        // ---------------------------------------------------------------------------
        private static void TestCoverageReport(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "coverage"));
            // Cover Intel (PCI 8086) and ASIX AX88179 (USB 0B95:1790); leave Realtek and RTL8153 as gaps.
            string intelSrc = Path.Combine(dir, "Intel"); Directory.CreateDirectory(intelSrc);
            WriteInf(intelSrc, "intel.inf", "PCI\\VEN_8086&DEV_15F3", "01/01/2026", "1.0.0.0", "NTamd64.10");
            string asixSrc = Path.Combine(dir, "Asix"); Directory.CreateDirectory(asixSrc);
            WriteInf(asixSrc, "asix.inf", "USB\\VID_0B95&PID_1790", "01/01/2026", "1.0.0.0", "NTamd64.10");
            string repo = Path.Combine(dir, "bootRepo");
            Import(repo, new List<NicBootstrapImportSource> {
                new NicBootstrapImportSource { Directory = intelSrc, SourceName = "intel" },
                new NicBootstrapImportSource { Directory = asixSrc, SourceName = "asix" }
            }, new TrustingSignatureVerifier());

            var coverage = NicBootstrapCoverageBuilder.Build(repo, Sources());
            var intel = coverage.Vendors.FirstOrDefault(v => v.Vendor == "Intel");
            T(intel != null && intel.CoveredPciIds.Contains("8086"), "Intel PCI 8086 covered");
            var realtek = coverage.Vendors.FirstOrDefault(v => v.Vendor == "Realtek");
            T(realtek != null && !realtek.Covered, "Realtek is a gap (not covered)");
            T(coverage.CommonUsbNicCoverage.Any(c => c.IndexOf("AX88179", StringComparison.OrdinalIgnoreCase) >= 0 && c.StartsWith("COVERED", StringComparison.OrdinalIgnoreCase)), "ASIX AX88179 USB NIC covered");
            T(coverage.Gaps.Any(g => g.IndexOf("RTL8153", StringComparison.OrdinalIgnoreCase) >= 0), "RTL8153 USB NIC flagged as gap");
            T(coverage.Gaps.Any(g => g.IndexOf("Realtek", StringComparison.OrdinalIgnoreCase) >= 0), "Realtek vendor flagged as gap");
            T(coverage.OsArchBuckets["Win10-x64"] >= 2, "Win10-x64 bucket counted both INFs, got " + coverage.OsArchBuckets["Win10-x64"]);
            T(coverage.OsArchBuckets["Server-x64"] >= 2, "Server-x64 bucket counted (NT 10 amd64 covers Server), got " + coverage.OsArchBuckets["Server-x64"]);
            // The text rendering should mention gaps and the as-of date.
            string text = NicBootstrapCoverageBuilder.ToText(coverage);
            T(text.IndexOf("gaps", StringComparison.OrdinalIgnoreCase) >= 0, "coverage text mentions gaps");
        }

        // ---------------------------------------------------------------------------
        //  7. Two-version packaging: licensed+verified repo copied into Offline AND Lan;
        //    non-redistributable repo is skipped (license deny).
        // ---------------------------------------------------------------------------
        private static void TestTwoVersionPackaging(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "package"));

            // (a) VirtIO (redistributable, trusted) -> packaged into both Offline and Lan.
            string virtioSrc = Path.Combine(dir, "VirtIO"); Directory.CreateDirectory(virtioSrc);
            WriteInf(virtioSrc, "virtio.inf", "PCI\\VEN_1AF4&DEV_1000", "01/01/2026", "1.0.0.0", "NTamd64.10");
            string repo = Path.Combine(dir, "bootRepo");
            Import(repo, new List<NicBootstrapImportSource> { new NicBootstrapImportSource { Directory = virtioSrc, SourceName = "virtio" } }, new TrustingSignatureVerifier());
            string offlineDir = PrepareDir(Path.Combine(dir, "Offline"));
            string lanDir = PrepareDir(Path.Combine(dir, "Lan"));
            string reason;
            bool ok = NicBootstrapPackager.Package(repo, offlineDir, lanDir, _log, out reason);
            T(ok, "packaging VirtIO succeeded: " + reason);
            T(File.Exists(Path.Combine(offlineDir, "NicBootstrap", "repo.manifest.json")), "Offline/NicBootstrap/repo.manifest.json exists");
            T(File.Exists(Path.Combine(lanDir, "NicBootstrap", NicBootstrapCatalogStore.FileName)), "Lan/NicBootstrap/nic-bootstrap.catalog.json exists");
            T(File.Exists(Path.Combine(offlineDir, "NicBootstrap", NicBootstrapCatalogStore.FileName)), "Offline also has the catalog");

            // (b) Intel only (restricted, not redistributable, no override) -> packaging skipped, nothing copied.
            string intelSrc = Path.Combine(dir, "Intel"); Directory.CreateDirectory(intelSrc);
            WriteInf(intelSrc, "intel.inf", "PCI\\VEN_8086&DEV_15F3", "01/01/2026", "1.0.0.0", "NTamd64.10");
            string repo2 = Path.Combine(dir, "bootRepoIntel");
            Import(repo2, new List<NicBootstrapImportSource> { new NicBootstrapImportSource { Directory = intelSrc, SourceName = "intel" } }, new TrustingSignatureVerifier());
            string offlineDir2 = PrepareDir(Path.Combine(dir, "Offline2"));
            string lanDir2 = PrepareDir(Path.Combine(dir, "Lan2"));
            bool ok2 = NicBootstrapPackager.Package(repo2, offlineDir2, lanDir2, _log, out reason);
            T(!ok2, "packaging Intel-only (not redistributable) skipped");
            T(!Directory.Exists(Path.Combine(offlineDir2, "NicBootstrap")), "no NicBootstrap copied when license denied");
            T(reason.IndexOf("redistributable", StringComparison.OrdinalIgnoreCase) >= 0 || reason.IndexOf("verification", StringComparison.OrdinalIgnoreCase) >= 0, "skip reason mentions license/verification: " + reason);
        }

        // ---------------------------------------------------------------------------
        //  8. Missing-repo safe downgrade: no bootstrap -> no-op; valid bootstrap -> NIC installed.
        // ---------------------------------------------------------------------------
        private static void TestMissingRepoSafeDowngrade(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "downgrade"));
            var devices = new List<D> {
                new D { Name = "NIC", Id = "NIC1", Class = "Net", ClassGuid = NetClassGuid, Inf = "", Version = "",
                    H = new List<string> { "PCI\\VEN_1AF4&DEV_1000" }, K = new List<string>() }
            };

            // (a) No bootstrap dir -> Attempted=false, no throw.
            var none = NicBootstrap.TryInstall("", new OsTarget { Architecture = "amd64", MajorVersion = 10 },
                new StaticHardwareCollector(devices), null, false, false, false, 5, true, true, _log,
                new TrustingSignatureVerifier(), new AlwaysAdminChecker(), new FakePnpInstallRunner { DefaultExitCode = 0 }, new NoOpSystemOps(), CancellationToken.None);
            T(!none.Attempted && none.Ok == 0, "no bootstrap dir: not attempted, no crash");

            var missing = NicBootstrap.TryInstall(Path.Combine(dir, "does-not-exist"), new OsTarget { Architecture = "amd64", MajorVersion = 10 },
                new StaticHardwareCollector(devices), null, false, false, false, 5, true, true, _log,
                new TrustingSignatureVerifier(), new AlwaysAdminChecker(), new FakePnpInstallRunner { DefaultExitCode = 0 }, new NoOpSystemOps(), CancellationToken.None);
            T(!missing.Attempted, "nonexistent bootstrap dir: not attempted, no crash");

            // (b) Valid bootstrap repo + NIC lacking driver -> NIC installed offline.
            string src = Path.Combine(dir, "Pkg"); Directory.CreateDirectory(src);
            WriteInf(src, "nic.inf", "PCI\\VEN_1AF4&DEV_1000", "01/01/2026", "1.0.0.0", "NTamd64.10");
            string repo = Path.Combine(dir, "bootRepo");
            Import(repo, new List<NicBootstrapImportSource> { new NicBootstrapImportSource { Directory = src, SourceName = "pkg" } }, new TrustingSignatureVerifier());
            var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
            var ok = NicBootstrap.TryInstall(repo, new OsTarget { Architecture = "amd64", MajorVersion = 10 },
                new StaticHardwareCollector(devices), null, false, false, false, 5, true, true, _log,
                new TrustingSignatureVerifier(), new AlwaysAdminChecker(), runner, new NoOpSystemOps(), CancellationToken.None);
            T(ok.Attempted, "valid bootstrap: attempted");
            T(ok.Ok == 1, "valid bootstrap: NIC installed, ok=" + ok.Ok);
            T(runner.Calls.Count == 1, "valid bootstrap: one pnputil call, calls=" + runner.Calls.Count);

            // (c) NIC already has a driver -> not attempted (nothing lacking).
            var devicesOk = new List<D> {
                new D { Name = "NIC", Id = "NIC1", Class = "Net", ClassGuid = NetClassGuid, Inf = "oem1.inf", Version = "1.0.0.0",
                    H = new List<string> { "PCI\\VEN_1AF4&DEV_1000" }, K = new List<string>() }
            };
            var skip = NicBootstrap.TryInstall(repo, new OsTarget { Architecture = "amd64", MajorVersion = 10 },
                new StaticHardwareCollector(devicesOk), null, false, false, false, 5, true, true, _log,
                new TrustingSignatureVerifier(), new AlwaysAdminChecker(), new FakePnpInstallRunner { DefaultExitCode = 0 }, new NoOpSystemOps(), CancellationToken.None);
            T(!skip.Attempted, "NIC already driven: not attempted");

            // (d) Import with no acceptable candidates -> Rebuilt=false, no crash, no repo manifest.
            string emptySrc = Path.Combine(dir, "Empty"); Directory.CreateDirectory(emptySrc);
            File.WriteAllText(Path.Combine(emptySrc, "readme.txt"), "not a driver", new UTF8Encoding(false));
            var rep = Import(Path.Combine(dir, "bootEmpty"), new List<NicBootstrapImportSource> { new NicBootstrapImportSource { Directory = emptySrc, SourceName = "empty" } }, new TrustingSignatureVerifier());
            T(!rep.Rebuilt, "empty import: not rebuilt");
            T(rep.RejectedInfs >= 1, "empty import: rejection recorded");
            T(!File.Exists(Path.Combine(dir, "bootEmpty", "repo.manifest.json")), "empty import: no manifest written");
        }

        // ---------------------------------------------------------------------------
        //  9. Verify happy path: a clean VirtIO repo verifies OK; packaging mode also OK.
        // ---------------------------------------------------------------------------
        private static void TestVerifyHappyPath(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "verify"));
            string src = Path.Combine(dir, "Pkg"); Directory.CreateDirectory(src);
            WriteInf(src, "nic.inf", "PCI\\VEN_1AF4&DEV_1000", "01/01/2026", "1.0.0.0", "NTamd64.10");
            string repo = Path.Combine(dir, "bootRepo");
            Import(repo, new List<NicBootstrapImportSource> { new NicBootstrapImportSource { Directory = src, SourceName = "pkg" } }, new TrustingSignatureVerifier());

            var problems = NicBootstrapVerifier.Verify(repo, false, false, _log);
            T(problems.Count == 0, "clean repo verifies OK, problems=" + string.Join("; ", problems));
            // Catalog integrity tamper -> flagged.
            string catPath = Path.Combine(repo, NicBootstrapCatalogStore.FileName);
            var catalog = NicBootstrapCatalogStore.Deserialize(File.ReadAllText(catPath, Encoding.UTF8));
            catalog.Packages[0].Vendor = "Tampered";
            // Do NOT recompute the integrity hash -> mismatch must be detected.
            File.WriteAllText(catPath, NicBootstrapCatalogStore.Serialize(catalog), new UTF8Encoding(false));
            var problems2 = NicBootstrapVerifier.Verify(repo, false, false, _log);
            T(problems2.Any(p => p.IndexOf("catalog integrity hash mismatch", StringComparison.OrdinalIgnoreCase) >= 0), "tampered catalog flagged, problems=" + string.Join("; ", problems2));
        }
    }
}
