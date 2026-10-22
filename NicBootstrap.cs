// NicBootstrap.cs - Phase 4: NIC bootstrap supply chain (no third-party driver binaries downloaded).
//
// Phase 4 layers a curated, license-aware NIC driver catalog on top of the Phase 3 offline repository
// format. The bootstrap REPOSITORY is a standard OfflineRepo (repo.manifest.json + repo.udadb + packages/),
// so the existing OfflineRepo.Open / OfflineInstaller / LanDriverClient bootstrap path consume it unchanged.
// Phase 4 adds a companion catalog (nic-bootstrap.catalog.json) recording, per imported package: source
// vendor / official page / version / OS / arch / license status / redistribution-allowed flag / expected
// SHA-256 / import time / signature status / INF list / HWID coverage, plus a canonical integrity hash.
//
// NicBootstrapImporter imports from three operator-authorized origins:
//   * a user-extracted official package directory (--source),
//   * an existing offline repository (--from-repo, re-imports its packages/<name>/ subdirs),
//   * a pnputil /export-driver output directory (--from-export).
// It accepts ONLY Net-class / network-adapter ClassGuid INFs and rejects: non-Net INFs, unsigned INFs
// (unless --accept-unsigned), expected-hash mismatches, path traversal, and duplicate older versions
// (same canonical VEN/DEV or VID/PID + same arch -> keep newest DriverVer). It then delegates the atomic
// repo build to OfflineRepoGenerator and writes the catalog.
//
// NicBootstrapCoverage produces a gap report by vendor, PCI/USB ID, Win10/Win11/Server/x64 (and arm64)
// buckets, pointing out vendors/IDs present in the sources catalog but not covered by imported packages.
//
// Build.ps1 invokes `NicBootstrapManager.exe package` to copy a LICENSED + VERIFIED bootstrap repo into
// both bin/Offline and bin/Lan; if none is prepared or it fails verification, the build skips it and both
// clients degrade gracefully (the LAN client already handles a missing bootstrap repo; the offline client
// gains a --bootstrap-first path via NicBootstrap.TryInstall).
//
// No third-party dependencies; compiled by Build.ps1 and the Phase 3/4 harnesses. C# 5.0 syntax.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace UDA
{
    // =================================================================================
    //  Sources catalog (NicBootstrapSources.json) - confirmed official entry points, no fabricated URLs/hashes
    // =================================================================================

    public sealed class NicBootstrapSourceVendor
    {
        public string Vendor = "";
        public List<string> PciVendorIds = new List<string>();
        public List<string> UsbVendorIds = new List<string>();
        public string OfficialPage = "";
        public string LicenseStatus = "";          // restricted | redistributable | oem | inbox
        public bool RedistributionAllowedByDefault;
        public string Notes = "";
    }

    public sealed class NicBootstrapCommonUsbNic
    {
        public string Name = "";
        public string UsbVid = "";
        public string UsbPid = "";
        public string Vendor = "";
    }

    public sealed class NicBootstrapSourcesCatalog
    {
        public const int CurrentSchema = 1;
        public int SchemaVersion = CurrentSchema;
        public string AsOf = "";
        public string Note = "";
        public List<NicBootstrapSourceVendor> Vendors = new List<NicBootstrapSourceVendor>();
        public List<NicBootstrapCommonUsbNic> CommonUsbNics = new List<NicBootstrapCommonUsbNic>();
    }

    /// <summary>Loads and queries the curated official sources catalog.</summary>
    public static class NicBootstrapSources
    {
        public const string DefaultFileName = "NicBootstrapSources.json";

        public static NicBootstrapSourcesCatalog Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("NIC bootstrap sources catalog not found: " + (path ?? "(null)"));
            var cat = new JavaScriptSerializer().Deserialize<NicBootstrapSourcesCatalog>(File.ReadAllText(path, Encoding.UTF8));
            if (cat == null) throw new InvalidDataException("empty NIC bootstrap sources catalog");
            if (cat.Vendors == null) cat.Vendors = new List<NicBootstrapSourceVendor>();
            if (cat.CommonUsbNics == null) cat.CommonUsbNics = new List<NicBootstrapCommonUsbNic>();
            foreach (var v in cat.Vendors)
            {
                if (v.PciVendorIds == null) v.PciVendorIds = new List<string>();
                if (v.UsbVendorIds == null) v.UsbVendorIds = new List<string>();
            }
            return cat;
        }

        /// <summary>Load the catalog, or return an empty catalog if the file is absent (so coverage/verify
        /// still work without a sources file; vendor lookups simply return null).</summary>
        public static NicBootstrapSourcesCatalog LoadOrDefault(string path)
        {
            try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return Load(path); }
            catch { }
            return new NicBootstrapSourcesCatalog();
        }

        /// <summary>Find the default path: next to the entry assembly, then the current directory.</summary>
        public static string ResolveDefaultPath(string overridePath)
        {
            if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath;
            foreach (string dir in new[] {
                Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location ?? ""),
                Environment.CurrentDirectory })
            {
                if (!string.IsNullOrEmpty(dir))
                {
                    string p = Path.Combine(dir, DefaultFileName);
                    if (File.Exists(p)) return p;
                }
            }
            return DefaultFileName;
        }

        /// <summary>Look up the vendor whose PCI/USB IDs contain the given 4-hex vendor id (VEN_/VID_ value).</summary>
        public static NicBootstrapSourceVendor LookupVendorByVendorId(NicBootstrapSourcesCatalog cat, string vendorId)
        {
            if (cat == null || string.IsNullOrEmpty(vendorId)) return null;
            string id = vendorId.ToUpperInvariant();
            foreach (var v in cat.Vendors)
            {
                foreach (var x in v.PciVendorIds) if (x.ToUpperInvariant() == id) return v;
                foreach (var x in v.UsbVendorIds) if (x.ToUpperInvariant() == id) return v;
            }
            return null;
        }

        /// <summary>Extract the VEN_ or VID_ 4-hex value from a hardware id, or "" if none.</summary>
        public static string ExtractVendorId(string hardwareId)
        {
            string n = HardwareIdNormalizer.Normalize(hardwareId);
            var m = Regex.Match(n, "VEN_([0-9A-F]{4})");
            if (m.Success) return m.Groups[1].Value;
            m = Regex.Match(n, "VID_([0-9A-F]{4})");
            if (m.Success) return m.Groups[1].Value;
            return "";
        }

        /// <summary>Extract the DEV_ or PID_ 4-hex value from a hardware id, or "" if none.</summary>
        public static string ExtractDeviceId(string hardwareId)
        {
            string n = HardwareIdNormalizer.Normalize(hardwareId);
            var m = Regex.Match(n, "DEV_([0-9A-F]{4})");
            if (m.Success) return m.Groups[1].Value;
            m = Regex.Match(n, "PID_([0-9A-F]{4})");
            if (m.Success) return m.Groups[1].Value;
            return "";
        }
    }

    // =================================================================================
    //  Catalog manifest (nic-bootstrap.catalog.json) - the license/coverage companion to repo.manifest.json
    // =================================================================================

    public sealed class NicBootstrapCatalogInf
    {
        public string RelativePath = "";
        public string ContentHash = "";
        public string Provider = "";
        public string Class = "";
        public string ClassGuid = "";
        public string DriverVerDate = "";
        public string DriverVerVersion = "";
        public List<string> Decorations = new List<string>();
        public List<string> HardwareIds = new List<string>();
        public List<string> CompatibleIds = new List<string>();
        public List<string> VendorIdsCovered = new List<string>();   // e.g. ["8086"] or ["0B95"]
        public List<string> BusIdsCovered = new List<string>();      // ["PCI"] / ["USB"]
    }

    public sealed class NicBootstrapCatalogPackage
    {
        public string SourceName = "";
        public string Vendor = "";
        public string OfficialPage = "";
        public string LicenseStatus = "";
        public bool RedistributionAllowed;
        public string Version = "";
        public int OsMajorVersion = 10;
        public string OsArchitecture = "amd64";
        public string ExpectedSha256 = "";        // operator-recorded expected hash ("" if none)
        public string ImportTime = "";
        public string SignatureStatus = "";        // trusted | untrusted | accepted-unsigned | deferred
        public List<NicBootstrapCatalogInf> Infs = new List<NicBootstrapCatalogInf>();
    }

    public sealed class NicBootstrapCatalog
    {
        public const int CurrentSchema = 1;
        public int SchemaVersion = CurrentSchema;
        public string GeneratorVersion = "4.0";
        public string AsOf = "";
        public string RepoManifest = "repo.manifest.json";
        public string RepoManifestIntegrityHash = "";   // pins the companion repo manifest
        public List<NicBootstrapCatalogPackage> Packages = new List<NicBootstrapCatalogPackage>();
        public List<string> Warnings = new List<string>();
        public string IntegrityHash = "";               // canonical integrity hash (this field excluded)
    }

    public static class NicBootstrapCatalogStore
    {
        public const string FileName = "nic-bootstrap.catalog.json";

        public static string Serialize(NicBootstrapCatalog catalog)
        {
            return Pretty(new JavaScriptSerializer().Serialize(catalog));
        }

        public static NicBootstrapCatalog Deserialize(string json)
        {
            var catalog = new JavaScriptSerializer().Deserialize<NicBootstrapCatalog>(json);
            if (catalog == null) throw new InvalidDataException("empty catalog");
            if (catalog.Packages == null) catalog.Packages = new List<NicBootstrapCatalogPackage>();
            if (catalog.Warnings == null) catalog.Warnings = new List<string>();
            foreach (var p in catalog.Packages)
            {
                if (p.Infs == null) p.Infs = new List<NicBootstrapCatalogInf>();
                foreach (var e in p.Infs)
                {
                    if (e.HardwareIds == null) e.HardwareIds = new List<string>();
                    if (e.CompatibleIds == null) e.CompatibleIds = new List<string>();
                    if (e.Decorations == null) e.Decorations = new List<string>();
                    if (e.VendorIdsCovered == null) e.VendorIdsCovered = new List<string>();
                    if (e.BusIdsCovered == null) e.BusIdsCovered = new List<string>();
                }
            }
            return catalog;
        }

        public static NicBootstrapCatalog TryLoad(string repoDir)
        {
            try
            {
                string p = Path.Combine(repoDir, FileName);
                if (!File.Exists(p)) return null;
                return Deserialize(File.ReadAllText(p, Encoding.UTF8));
            }
            catch { return null; }
        }

        /// <summary>Deterministic SHA-256 over a canonical rendering of the catalog (IntegrityHash excluded),
        /// independent of JSON serializer field ordering.</summary>
        public static string ComputeIntegrityHash(NicBootstrapCatalog catalog)
        {
            var sb = new StringBuilder();
            sb.Append(catalog.SchemaVersion).Append('|')
              .Append(catalog.GeneratorVersion).Append('|')
              .Append(catalog.AsOf).Append('|')
              .Append(catalog.RepoManifest).Append('|')
              .Append(catalog.RepoManifestIntegrityHash).Append('|')
              .Append(catalog.Packages.Count).Append('|');
            foreach (var p in catalog.Packages.OrderBy(x => x.SourceName, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(p.SourceName).Append('~')
                  .Append(p.Vendor).Append('~')
                  .Append(p.OfficialPage).Append('~')
                  .Append(p.LicenseStatus).Append('~')
                  .Append(p.RedistributionAllowed).Append('~')
                  .Append(p.Version).Append('~')
                  .Append(p.OsMajorVersion).Append('~')
                  .Append(p.OsArchitecture).Append('~')
                  .Append(p.ExpectedSha256).Append('~')
                  .Append(p.ImportTime).Append('~')
                  .Append(p.SignatureStatus).Append('~')
                  .Append(p.Infs.Count).Append('~');
                foreach (var e in p.Infs.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
                {
                    sb.Append(e.RelativePath).Append('^')
                      .Append(e.ContentHash).Append('^')
                      .Append(e.Provider).Append('^')
                      .Append(e.Class).Append('^')
                      .Append(e.ClassGuid).Append('^')
                      .Append(e.DriverVerDate).Append('^')
                      .Append(e.DriverVerVersion).Append('^')
                      .Append(string.Join(",", e.Decorations)).Append('^')
                      .Append(string.Join(",", e.HardwareIds)).Append('^')
                      .Append(string.Join(",", e.CompatibleIds)).Append('^')
                      .Append(string.Join(",", e.VendorIdsCovered)).Append('^')
                      .Append(string.Join(",", e.BusIdsCovered)).Append('#');
                }
                sb.Append('|');
            }
            foreach (var w in catalog.Warnings) sb.Append(w).Append('\n');
            return Sha256String(sb.ToString());
        }

        public static string Sha256String(string text)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var hex = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }

        // Same lightweight pretty-printer used by the repo manifest, so the catalog is human-reviewable.
        private static string Pretty(string json)
        {
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
    //  Import source + report
    // =================================================================================

    public sealed class NicBootstrapImportSource
    {
        public string Directory = "";      // the dir to import (a package dir, a repo packages/<name> dir, or an export dir)
        public string SourceName = "";     // friendly / stable name
        public string Origin = "package";  // package | repo | export
        public string ExpectedSha256 = ""; // optional; anchors the primary Net INF of this source
        public bool? RedistributionAllowedOverride;  // null => use vendor default
    }

    public sealed class NicBootstrapImportReport
    {
        public string RepoDir = "";
        public int SourcesProcessed;
        public int AcceptedInfs, RejectedInfs;
        public int PackagesEmitted;
        public int Superseded;
        public List<string> Rejections = new List<string>();   // human-readable, one per rejected INF/source
        public List<string> Warnings = new List<string>();
        public string CatalogIntegrityHash = "";
        public string RepoIntegrityHash = "";
        public bool Rebuilt = true;
    }

    // =================================================================================
    //  Importer
    // =================================================================================

    /// <summary>
    /// Builds a license-aware NIC bootstrap repository from operator-authorized sources. Accepts only Net-class
    /// network-adapter INFs; rejects non-Net, unsigned (unless accepted), expected-hash mismatches, path
    /// traversal, and duplicate older versions (same canonical VEN/DEV or VID/PID + same arch). Delegates the
    /// atomic repo build (packages copy + repo.manifest.json + repo.udadb) to OfflineRepoGenerator, then writes
    /// the companion nic-bootstrap.catalog.json with vendor/license/coverage metadata and an integrity hash.
    /// </summary>
    public sealed class NicBootstrapImporter
    {
        public const string NetClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}";

        private readonly ISignatureVerifier _sig;
        private readonly L _logger;

        public NicBootstrapImporter(ISignatureVerifier sig, L logger) { _sig = sig; _logger = logger; }

        public NicBootstrapImportReport Import(string repoDir, IList<NicBootstrapImportSource> sources,
            NicBootstrapSourcesCatalog sourcesCat, OsTarget osTarget, string asOf, bool acceptUnsigned,
            CancellationToken ct, IProgress<string> progress)
        {
            if (string.IsNullOrWhiteSpace(repoDir)) throw new ArgumentException("repoDir");
            repoDir = Path.GetFullPath(repoDir);
            if (osTarget == null) osTarget = new OsTarget();
            if (sourcesCat == null) sourcesCat = new NicBootstrapSourcesCatalog();
            if (asOf == null) asOf = "";
            var report = new NicBootstrapImportReport { RepoDir = repoDir };

            // ----- Phase A: gather accepted candidates per source, applying per-INF gates -----
            var allCandidates = new List<Candidate>();      // accepted, pre-supersession
            var srcList = (sources ?? new NicBootstrapImportSource[0]).Where(s => !string.IsNullOrWhiteSpace(s.Directory)).ToList();
            string stagingInput = repoDir + ".import-staging-" + SafeSuffix();
            try
            {
                if (Directory.Exists(stagingInput)) { try { Directory.Delete(stagingInput, true); } catch { } }
                Directory.CreateDirectory(stagingInput);
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < srcList.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var src = srcList[i];
                    string srcDir = Path.GetFullPath(src.Directory);
                    report.SourcesProcessed++;
                    if (progress != null) progress.Report("NIC 引入 (" + (i + 1) + "/" + srcList.Count + "): " + srcDir);
                    if (!Directory.Exists(srcDir)) { recordRejection(report, src.SourceName + " (origin=" + src.Origin + ")", "source directory not found"); continue; }

                    var infPaths = EnumerateInfs(srcDir, ct).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                    if (infPaths.Count == 0) { recordRejection(report, src.SourceName, "no INF files in source"); continue; }

                    var accepted = new List<Candidate>();
                    bool sourceHashRejected = false;
                    string primaryNetHash = null;
                    foreach (var infAbs in infPaths)
                    {
                        ct.ThrowIfCancellationRequested();
                        string relWithinSrc;
                        try { relWithinSrc = RelPath(srcDir, infAbs); }
                        catch { recordRejection(report, infAbs, "path traversal rejected (not under source)"); continue; }
                        if (relWithinSrc.Contains("..")) { recordRejection(report, infAbs, "path traversal rejected"); continue; }

                        InfRecord rec;
                        try { rec = InfParser.Parse(infAbs, src.SourceName); }
                        catch (Exception ex) { recordRejection(report, infAbs, "parse failed: " + ex.Message); continue; }

                        // Gate: Net class / network-adapter ClassGuid.
                        if (!IsNicInf(rec)) { recordRejection(report, infAbs, "not a Net class / network-adapter INF (Class=" + rec.Class + ", ClassGuid=" + rec.ClassGuid + ")"); continue; }

                        // Gate: OS/arch applicability (so the bootstrap repo only carries applicable NIC drivers).
                        if (!OsArchFilter.Applicable(rec, osTarget)) { recordRejection(report, infAbs, "not applicable to target OS/arch (decorations: " + string.Join(",", rec.Decorations) + ")"); continue; }

                        // Gate: signature trusted (unless explicitly accepting unsigned).
                        string sigStatus;
                        try { sigStatus = (_sig != null && _sig.IsTrusted(infAbs, _logger)) ? "trusted" : "untrusted"; }
                        catch { sigStatus = "deferred"; }
                        if (sigStatus != "trusted")
                        {
                            if (acceptUnsigned) sigStatus = "accepted-unsigned";
                            else { recordRejection(report, infAbs, "unsigned / not trusted (signature=" + sigStatus + ")"); continue; }
                        }

                        // Gate: expected hash anchors the source's primary Net INF (the first accepted Net INF).
                        if (!string.IsNullOrEmpty(src.ExpectedSha256))
                        {
                            if (primaryNetHash == null) primaryNetHash = rec.ContentHash;
                            if (!string.Equals(primaryNetHash, src.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                            {
                                recordRejection(report, src.SourceName, "expected SHA-256 mismatch (expected " + src.ExpectedSha256 + ", got " + primaryNetHash + ") - source rejected");
                                sourceHashRejected = true;
                                break;
                            }
                        }

                        accepted.Add(new Candidate { Source = src, InfAbs = infAbs, RelWithinSource = relWithinSrc, Record = rec, SignatureStatus = sigStatus });
                    }
                    if (sourceHashRejected) continue;
                    if (accepted.Count == 0) { recordRejection(report, src.SourceName, "no acceptable Net INFs after gating"); continue; }
                    allCandidates.AddRange(accepted);
                }

                // ----- Phase B: supersede duplicate older versions (same canonical + same primary arch -> keep newest) -----
                var survivors = Supersede(allCandidates, report);

                // ----- Phase C: build filtered staging input (whole source copied, rejected INFs pruned) -----
                var stagingDirs = new List<string>();
                var bySource = survivors.GroupBy(c => c.Source).ToList();
                foreach (var grp in bySource)
                {
                    ct.ThrowIfCancellationRequested();
                    var src = grp.Key;
                    string safeName = UniqueName(SafeName(!string.IsNullOrEmpty(src.SourceName) ? src.SourceName : src.Directory), usedNames);
                    string dest = Path.Combine(stagingInput, safeName);
                    CopyTreePrunedInf(src.Directory, dest, grp.Select(c => c.RelWithinSource).ToHashSet(StringComparer.OrdinalIgnoreCase), ct);
                    foreach (var c in grp) c.StagedSourceName = safeName;
                    stagingDirs.Add(dest);
                }

                if (stagingDirs.Count == 0)
                {
                    report.Rebuilt = false;
                    report.AcceptedInfs = 0;
                    report.RejectedInfs = report.Rejections.Count;
                    if (_logger != null) _logger.W("[WARN] NIC 引入无可用候选 / NIC import produced no candidates; bootstrap repo not (re)built");
                    return report;
                }

                // ----- Phase D: delegate the atomic repo build to OfflineRepoGenerator -----
                var gen = new OfflineRepoGenerator(_sig, _logger);
                var genReport = gen.Generate(repoDir, stagingDirs, osTarget, null, asOf, ct, progress);
                report.RepoIntegrityHash = genReport.IntegrityHash;

                // ----- Phase E: build the companion catalog from the generated repo manifest -----
                var repoManifest = OfflineRepoGenerator.TryLoadManifest(repoDir);
                if (repoManifest == null) throw new InvalidOperationException("bootstrap repo manifest missing after generation");
                var catalog = BuildCatalog(repoManifest, survivors, sourcesCat, osTarget, asOf);
                catalog.RepoManifestIntegrityHash = repoManifest.IntegrityHash;
                catalog.IntegrityHash = NicBootstrapCatalogStore.ComputeIntegrityHash(catalog);
                File.WriteAllText(Path.Combine(repoDir, NicBootstrapCatalogStore.FileName),
                    NicBootstrapCatalogStore.Serialize(catalog), new UTF8Encoding(false));

                report.CatalogIntegrityHash = catalog.IntegrityHash;
                report.PackagesEmitted = catalog.Packages.Count;
                report.AcceptedInfs = survivors.Count;
                report.RejectedInfs = report.Rejections.Count;
                report.Warnings = new List<string>(catalog.Warnings);
                if (_logger != null) _logger.W("[INFO] NIC 引入完成 / NIC bootstrap import: packages=" + catalog.Packages.Count +
                    " infs=" + survivors.Count + " superseded=" + report.Superseded + " rejected=" + report.RejectedInfs);
                return report;
            }
            finally
            {
                try { if (Directory.Exists(stagingInput)) Directory.Delete(stagingInput, true); } catch { }
            }
        }

        // ----- gates / helpers -----

        public static bool IsNicInf(InfRecord rec)
        {
            if (rec == null) return false;
            if (string.Equals(rec.Class, "Net", StringComparison.OrdinalIgnoreCase)) return true;
            string cg = (rec.ClassGuid ?? "").Trim();
            return cg.Equals(NetClassGuid, StringComparison.OrdinalIgnoreCase);
        }

        private void recordRejection(NicBootstrapImportReport report, string what, string reason)
        {
            string msg = what + " => " + reason;
            report.Rejections.Add(msg);
            if (_logger != null) _logger.W("[WARN] NIC 引入拒绝 / NIC import reject: " + msg);
        }

        private sealed class Candidate
        {
            public NicBootstrapImportSource Source;
            public string InfAbs;
            public string RelWithinSource;
            public InfRecord Record;
            public string SignatureStatus;
            public string StagedSourceName;
            public string PrimaryArch;
            public List<string> Canonicals;
        }

        private List<Candidate> Supersede(List<Candidate> candidates, NicBootstrapImportReport report)
        {
            // Annotate each candidate with its canonical IDs and primary arch.
            foreach (var c in candidates)
            {
                c.Canonicals = new List<string>();
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in c.Record.HardwareIds)
                {
                    string canon = HardwareIdNormalizer.CanonicalVenDev(id);
                    if (canon.Length > 0 && set.Add(canon)) c.Canonicals.Add(canon);
                }
                c.PrimaryArch = "any";
                foreach (var dec in c.Record.Decorations)
                {
                    string a = OsArchFilter.ArchFromDecoration(dec);
                    if (a.Length > 0 && a != "any") { c.PrimaryArch = a; break; }
                }
            }
            // Group by (canonical, primaryArch). Within a group keep the newest; reject the rest as superseded.
            // Candidate has no Equals override, so the default HashSet uses reference identity.
            var keep = new HashSet<Candidate>();
            var groups = new Dictionary<string, List<Candidate>>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in candidates)
            {
                if (c.Canonicals.Count == 0) { keep.Add(c); continue; }   // no canonical -> nothing to supersede against
                foreach (var canon in c.Canonicals)
                {
                    string key = canon + "@" + c.PrimaryArch;
                    List<Candidate> list;
                    if (!groups.TryGetValue(key, out list)) { list = new List<Candidate>(); groups[key] = list; }
                    list.Add(c);
                }
            }
            foreach (var kv in groups)
            {
                if (kv.Value.Count <= 1) { foreach (var c in kv.Value) keep.Add(c); continue; }
                var ordered = kv.Value
                    .OrderByDescending(c => c.Record.Date())
                    .ThenByDescending(c => c.Record.DriverVerVersion, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(c => c.InfAbs, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                keep.Add(ordered[0]);
                for (int i = 1; i < ordered.Count; i++)
                {
                    var loser = ordered[i];
                    if (keep.Contains(loser)) continue;   // may survive via another canonical group
                    report.Superseded++;
                    string reason = string.Equals(loser.Record.ContentHash, ordered[0].Record.ContentHash, StringComparison.OrdinalIgnoreCase)
                        ? "duplicate content, superseded by " + Path.GetFileName(ordered[0].InfAbs)
                        : "older version, superseded by " + Path.GetFileName(ordered[0].InfAbs) + " (" + ordered[0].Record.DriverVerDate + "," + ordered[0].Record.DriverVerVersion + ")";
                    recordRejection(report, loser.InfAbs, reason);
                }
            }
            // Preserve original order for deterministic staging.
            return candidates.Where(c => keep.Contains(c)).ToList();
        }

        private NicBootstrapCatalog BuildCatalog(OfflineRepoManifest repoManifest, List<Candidate> survivors,
            NicBootstrapSourcesCatalog sourcesCat, OsTarget osTarget, string asOf)
        {
            var catalog = new NicBootstrapCatalog { AsOf = asOf };
            catalog.RepoManifest = "repo.manifest.json";
            // Map survivor relative-within-source -> candidate (for vendor/license/expected-hash lookup).
            var byStagedName = survivors.GroupBy(c => c.StagedSourceName).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var pkgGroup in repoManifest.Entries.GroupBy(e => e.SourceName, StringComparer.OrdinalIgnoreCase))
            {
                string stagedName = pkgGroup.Key;
                List<Candidate> pkgCandidates;
                if (!byStagedName.TryGetValue(stagedName, out pkgCandidates) || pkgCandidates == null || pkgCandidates.Count == 0) continue;
                var firstSrc = pkgCandidates[0].Source;
                // Vendor/license lookup from the first candidate's hardware IDs.
                NicBootstrapSourceVendor vendor = null;
                foreach (var c in pkgCandidates)
                    foreach (var id in c.Record.HardwareIds)
                    {
                        string vid = NicBootstrapSources.ExtractVendorId(id);
                        if (vid.Length > 0) { vendor = NicBootstrapSources.LookupVendorByVendorId(sourcesCat, vid); if (vendor != null) break; }
                        if (vendor != null) break;
                    }
                var pkg = new NicBootstrapCatalogPackage
                {
                    SourceName = stagedName,
                    Vendor = vendor != null ? vendor.Vendor : "",
                    OfficialPage = vendor != null ? vendor.OfficialPage : "",
                    LicenseStatus = vendor != null ? vendor.LicenseStatus : "unknown",
                    RedistributionAllowed = firstSrc.RedistributionAllowedOverride ?? (vendor != null && vendor.RedistributionAllowedByDefault),
                    Version = pkgCandidates.Select(c => c.Record.DriverVerVersion).Where(v => !string.IsNullOrEmpty(v)).OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? "",
                    OsMajorVersion = osTarget.MajorVersion,
                    OsArchitecture = osTarget.Architecture ?? "amd64",
                    ExpectedSha256 = firstSrc.ExpectedSha256 ?? "",
                    ImportTime = asOf,
                    SignatureStatus = pkgCandidates.All(c => c.SignatureStatus == "trusted") ? "trusted" :
                                      pkgCandidates.All(c => c.SignatureStatus == "accepted-unsigned" || c.SignatureStatus == "trusted") ? "accepted-unsigned" : "mixed"
                };
                foreach (var e in pkgGroup.OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase))
                    pkg.Infs.Add(ToCatalogInf(e));
                if (!pkg.RedistributionAllowed)
                    catalog.Warnings.Add("not-redistributable: " + stagedName + " (vendor=" + (pkg.Vendor.Length > 0 ? pkg.Vendor : "unknown") + ", license=" + pkg.LicenseStatus + ")");
                catalog.Packages.Add(pkg);
            }
            return catalog;
        }

        // ----- file helpers (mirror OfflineRepoGenerator) -----

        private static NicBootstrapCatalogInf ToCatalogInf(OfflineRepoEntry e)
        {
            var inf = new NicBootstrapCatalogInf
            {
                RelativePath = e.RelativePath,
                ContentHash = e.ContentHash,
                Provider = e.Provider,
                Class = e.Class,
                ClassGuid = e.ClassGuid,
                DriverVerDate = e.DriverVerDate,
                DriverVerVersion = e.DriverVerVersion,
                Decorations = new List<string>(e.Decorations),
                HardwareIds = new List<string>(e.HardwareIds),
                CompatibleIds = new List<string>(e.CompatibleIds)
            };
            var vidSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var busSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in e.HardwareIds)
            {
                string vid = NicBootstrapSources.ExtractVendorId(id);
                if (vid.Length > 0) vidSet.Add(vid);
                string bus = HardwareIdNormalizer.BusOf(id);
                if (bus.Length > 0) busSet.Add(bus);
            }
            inf.VendorIdsCovered = vidSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            inf.BusIdsCovered = busSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            return inf;
        }

        // ----- file helpers (mirror OfflineRepoGenerator) -----

        private static string SafeSuffix() { try { return Path.GetRandomFileName(); } catch { return "tmp"; } }

        private static string SafeName(string path)
        {
            try
            {
                string n = Path.GetFileName(path.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(n)) n = new DirectoryInfo(path).Name;
                var sb = new StringBuilder();
                foreach (char c in n ?? "src") sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');
                string s = sb.ToString().Trim('-');
                return s.Length == 0 ? "src" : s;
            }
            catch { return "src"; }
        }

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
                string[] files, dirs;
                try { files = Directory.GetFiles(dir, "*.inf"); dirs = Directory.GetDirectories(dir); }
                catch { continue; }
                foreach (var f in files) yield return f;
                foreach (var d in dirs) stack.Push(d);
            }
        }

        private static string RelPath(string root, string full)
        {
            string f = Path.GetFullPath(full).TrimEnd('\\') + "\\";
            string r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            if (!f.StartsWith(r, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("path not under root: " + full);
            return f.Substring(r.Length).TrimEnd('\\');
        }

        /// <summary>Copy a source tree to dest, but delete every .inf whose source-relative path is NOT in
        /// keepRelSet (so the repo only indexes the surviving NIC INFs; companion files are preserved).</summary>
        private static void CopyTreePrunedInf(string src, string dest, HashSet<string> keepRelSet, CancellationToken ct)
        {
            if (!Directory.Exists(src)) return;
            Directory.CreateDirectory(dest);
            var stack = new Stack<KeyValuePair<string, string>>();
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
                    if (f.EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
                    {
                        string rel;
                        try { rel = RelPath(src, f).Replace('\\', '/'); }
                        catch { continue; }
                        if (!keepRelSet.Contains(rel)) continue;   // prune rejected/duplicate INF
                    }
                    try { File.Copy(f, target, true); } catch { }
                }
                string[] dirs;
                try { dirs = Directory.GetDirectories(kv.Key); }
                catch { continue; }
                foreach (var d in dirs) stack.Push(new KeyValuePair<string, string>(d, Path.Combine(kv.Value, Path.GetFileName(d))));
            }
        }
    }

    // =================================================================================
    //  Coverage report
    // =================================================================================

    public sealed class NicBootstrapCoverageVendor
    {
        public string Vendor = "";
        public List<string> ExpectedPciIds = new List<string>();
        public List<string> CoveredPciIds = new List<string>();
        public List<string> MissingPciIds = new List<string>();
        public List<string> ExpectedUsbIds = new List<string>();
        public List<string> CoveredUsbIds = new List<string>();
        public List<string> MissingUsbIds = new List<string>();
        public bool Covered;
    }

    public sealed class NicBootstrapCoverageId
    {
        public string Canonical = "";   // VEN_xxxx&DEV_xxxx or VID_xxxx&PID_xxxx
        public string Bus = "";         // PCI / USB
        public string Vendor = "";
        public List<string> Buckets = new List<string>();   // OS/arch buckets covered
        public string Version = "";
        public string SourceName = "";
    }

    public sealed class NicBootstrapCoverage
    {
        public string RepoDir = "";
        public string AsOf = "";
        public int TotalPackages, TotalInfs;
        public int ExpectedVendorCount, CoveredVendorCount;
        public List<NicBootstrapCoverageVendor> Vendors = new List<NicBootstrapCoverageVendor>();
        public List<NicBootstrapCoverageId> Ids = new List<NicBootstrapCoverageId>();
        public Dictionary<string, int> OsArchBuckets = new Dictionary<string, int>();
        public List<string> Gaps = new List<string>();
        public List<string> CommonUsbNicCoverage = new List<string>();   // one line per common USB NIC
    }

    /// <summary>Builds a coverage/gap report by vendor, PCI/USB ID, and OS/arch bucket.</summary>
    public static class NicBootstrapCoverageBuilder
    {
        // Win10/Win11/Server share the NT 10.0 kernel; an amd64 NT 10 INF typically covers all three.
        public static readonly string[] Amd64Buckets = { "Win10-x64", "Win11-x64", "Server-x64" };
        public static readonly string[] Arm64Buckets = { "Win10-arm64", "Win11-arm64", "Server-arm64" };
        public static readonly string[] X86Buckets = { "Win10-x86" };

        public static NicBootstrapCoverage Build(string repoDir, NicBootstrapSourcesCatalog sourcesCat)
        {
            repoDir = Path.GetFullPath(repoDir);
            var coverage = new NicBootstrapCoverage { RepoDir = repoDir };
            if (sourcesCat == null) sourcesCat = new NicBootstrapSourcesCatalog();
            foreach (var b in Amd64Buckets) coverage.OsArchBuckets[b] = 0;
            foreach (var b in Arm64Buckets) coverage.OsArchBuckets[b] = 0;
            foreach (var b in X86Buckets) coverage.OsArchBuckets[b] = 0;

            var catalog = NicBootstrapCatalogStore.TryLoad(repoDir);
            if (catalog == null)
            {
                coverage.Gaps.Add("nic-bootstrap.catalog.json missing; no coverage data");
                foreach (var v in sourcesCat.Vendors) coverage.Vendors.Add(VendorSkeleton(v));
                coverage.ExpectedVendorCount = sourcesCat.Vendors.Count;
                return coverage;
            }
            coverage.AsOf = catalog.AsOf;
            coverage.TotalPackages = catalog.Packages.Count;

            // Covered vendor IDs (per bus) + canonical IDs + buckets, aggregated across all catalog INFs.
            var coveredPciVids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var coveredUsbVids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var coveredCanonicals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pkg in catalog.Packages)
            {
                foreach (var inf in pkg.Infs)
                {
                    coverage.TotalInfs++;
                    foreach (var id in inf.HardwareIds)
                    {
                        string canon = HardwareIdNormalizer.CanonicalVenDev(id);
                        if (canon.Length == 0) continue;
                        if (coveredCanonicals.Add(canon))
                        {
                            var cid = new NicBootstrapCoverageId
                            {
                                Canonical = canon,
                                Bus = HardwareIdNormalizer.BusOf(id),
                                Vendor = pkg.Vendor,
                                Version = inf.DriverVerVersion,
                                SourceName = pkg.SourceName,
                                Buckets = BucketsFor(inf.Decorations)
                            };
                            coverage.Ids.Add(cid);
                            foreach (var b in cid.Buckets) if (coverage.OsArchBuckets.ContainsKey(b)) coverage.OsArchBuckets[b]++;
                        }
                        string vid = NicBootstrapSources.ExtractVendorId(id);
                        if (vid.Length > 0)
                        {
                            string bus = HardwareIdNormalizer.BusOf(id);
                            if (string.Equals(bus, "USB", StringComparison.OrdinalIgnoreCase)) coveredUsbVids.Add(vid);
                            else coveredPciVids.Add(vid);
                        }
                    }
                }
            }

            // Per-vendor expected vs covered (PCI and USB tracked separately).
            foreach (var v in sourcesCat.Vendors)
            {
                var cv = new NicBootstrapCoverageVendor { Vendor = v.Vendor, ExpectedPciIds = new List<string>(v.PciVendorIds), ExpectedUsbIds = new List<string>(v.UsbVendorIds) };
                foreach (var id in v.PciVendorIds) if (coveredPciVids.Contains(id)) cv.CoveredPciIds.Add(id);
                foreach (var id in v.UsbVendorIds) if (coveredUsbVids.Contains(id)) cv.CoveredUsbIds.Add(id);
                cv.MissingPciIds = v.PciVendorIds.Where(id => !coveredPciVids.Contains(id)).ToList();
                cv.MissingUsbIds = v.UsbVendorIds.Where(id => !coveredUsbVids.Contains(id)).ToList();
                cv.Covered = cv.CoveredPciIds.Count > 0 || cv.CoveredUsbIds.Count > 0;
                if (cv.Covered) coverage.CoveredVendorCount++;
                coverage.Vendors.Add(cv);
                if (!cv.Covered) coverage.Gaps.Add("vendor '" + v.Vendor + "': no covered package (expected PCI=[" + string.Join(",", v.PciVendorIds) + "] USB=[" + string.Join(",", v.UsbVendorIds) + "])");
                else
                {
                    if (cv.MissingPciIds.Count > 0) coverage.Gaps.Add("vendor '" + v.Vendor + "': PCI vendor IDs not covered: " + string.Join(",", cv.MissingPciIds));
                    if (cv.MissingUsbIds.Count > 0) coverage.Gaps.Add("vendor '" + v.Vendor + "': USB vendor IDs not covered: " + string.Join(",", cv.MissingUsbIds));
                }
            }
            coverage.ExpectedVendorCount = sourcesCat.Vendors.Count;

            // Common USB NIC coverage (VID:PID granularity).
            foreach (var nic in sourcesCat.CommonUsbNics)
            {
                string canon = "VID_" + nic.UsbVid.ToUpperInvariant() + "&PID_" + nic.UsbPid.ToUpperInvariant();
                bool covered = coveredCanonicals.Contains(canon);
                coverage.CommonUsbNicCoverage.Add((covered ? "COVERED  " : "MISSING  ") + nic.Name + " (" + canon + ", vendor=" + nic.Vendor + ")");
                if (!covered) coverage.Gaps.Add("common USB NIC not covered: " + nic.Name + " (" + canon + ")");
            }

            return coverage;
        }

        private static NicBootstrapCoverageVendor VendorSkeleton(NicBootstrapSourceVendor v)
        {
            return new NicBootstrapCoverageVendor
            {
                Vendor = v.Vendor, ExpectedPciIds = new List<string>(v.PciVendorIds), ExpectedUsbIds = new List<string>(v.UsbVendorIds),
                MissingPciIds = new List<string>(v.PciVendorIds), MissingUsbIds = new List<string>(v.UsbVendorIds), Covered = false
            };
        }

        private static List<string> BucketsFor(List<string> decorations)
        {
            var buckets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (decorations == null || decorations.Count == 0) { foreach (var b in Amd64Buckets) buckets.Add(b); return buckets.ToList(); }
            bool any = false;
            foreach (var dec in decorations)
            {
                string arch = OsArchFilter.ArchFromDecoration(dec);
                int osMajor = OsArchFilter.OsMajorFromDecoration(dec);
                if (osMajor <= 0 && arch == "") continue;
                any = true;
                string[] arr = arch == "arm64" ? Arm64Buckets : arch == "arm" ? Arm64Buckets : arch == "x86" ? X86Buckets : Amd64Buckets;
                // OS major < 10 (e.g. NT.x86 for Win8) is not a Win10/11/Server target; skip those buckets.
                if (osMajor > 0 && osMajor < 10) continue;
                foreach (var b in arr) buckets.Add(b);
            }
            if (!any) foreach (var b in Amd64Buckets) buckets.Add(b);
            return buckets.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static string ToText(NicBootstrapCoverage c)
        {
            var sb = new StringBuilder();
            sb.AppendLine("NIC bootstrap coverage for " + c.RepoDir + " (as of " + c.AsOf + ")");
            sb.AppendLine("  packages=" + c.TotalPackages + " infs=" + c.TotalInfs + " vendors covered=" + c.CoveredVendorCount + "/" + c.ExpectedVendorCount);
            sb.AppendLine("  OS/arch buckets:");
            foreach (var kv in c.OsArchBuckets) sb.AppendLine("    " + kv.Key.PadRight(16) + " " + kv.Value + " id(s)");
            sb.AppendLine("  vendors:");
            foreach (var v in c.Vendors)
            {
                sb.AppendLine("    " + v.Vendor + ": " + (v.Covered ? "covered" : "GAP") +
                    " PCI covered=[" + string.Join(",", v.CoveredPciIds) + "] missing=[" + string.Join(",", v.MissingPciIds) + "]" +
                    " USB covered=[" + string.Join(",", v.CoveredUsbIds) + "] missing=[" + string.Join(",", v.MissingUsbIds) + "]");
            }
            sb.AppendLine("  common USB NICs:");
            foreach (var line in c.CommonUsbNicCoverage) sb.AppendLine("    " + line);
            sb.AppendLine("  gaps (" + c.Gaps.Count + "):");
            foreach (var g in c.Gaps) sb.AppendLine("    - " + g);
            return sb.ToString();
        }
    }

    // =================================================================================
    //  Verifier (repo manifest + catalog integrity + Net class + signature + license)
    // =================================================================================

    public static class NicBootstrapVerifier
    {
        /// <summary>Verify a bootstrap repo. Returns the list of problems (empty = OK). When packaging=true,
        /// every package must be redistributable and properly signed (trusted).</summary>
        public static List<string> Verify(string repoDir, bool packaging, bool acceptUnsigned, L logger)
        {
            var problems = new List<string>();
            repoDir = Path.GetFullPath(repoDir);
            string manifestPath = Path.Combine(repoDir, "repo.manifest.json");
            if (!File.Exists(manifestPath)) { problems.Add("repo.manifest.json missing"); return problems; }

            // Repo manifest integrity + per-file hashes (delegated to the Phase 3 verifier).
            var gen = new OfflineRepoGenerator(null, logger);
            var repoProblems = gen.Verify(repoDir, CancellationToken.None);
            foreach (var p in repoProblems) problems.Add("repo: " + p);

            OfflineRepoManifest repoManifest;
            try { repoManifest = OfflineRepoGenerator.DeserializeManifest(File.ReadAllText(manifestPath, Encoding.UTF8)); }
            catch (Exception ex) { problems.Add("repo manifest unreadable: " + ex.Message); return problems; }
            if (repoManifest.SchemaVersion != OfflineRepoManifest.CurrentSchema) problems.Add("repo manifest schema mismatch: " + repoManifest.SchemaVersion);

            // Catalog integrity.
            string catalogPath = Path.Combine(repoDir, NicBootstrapCatalogStore.FileName);
            if (!File.Exists(catalogPath)) { problems.Add(NicBootstrapCatalogStore.FileName + " missing"); return problems; }
            NicBootstrapCatalog catalog;
            try { catalog = NicBootstrapCatalogStore.Deserialize(File.ReadAllText(catalogPath, Encoding.UTF8)); }
            catch (Exception ex) { problems.Add("catalog unreadable: " + ex.Message); return problems; }
            if (catalog.SchemaVersion != NicBootstrapCatalog.CurrentSchema) problems.Add("catalog schema mismatch: " + catalog.SchemaVersion);
            string catHash = NicBootstrapCatalogStore.ComputeIntegrityHash(catalog);
            if (!string.Equals(catHash, catalog.IntegrityHash, StringComparison.OrdinalIgnoreCase))
                problems.Add("catalog integrity hash mismatch (expected " + catalog.IntegrityHash + ", got " + catHash + ")");
            // Pin catalog to the companion repo manifest.
            if (!string.IsNullOrEmpty(repoManifest.IntegrityHash) &&
                !string.Equals(repoManifest.IntegrityHash, catalog.RepoManifestIntegrityHash, StringComparison.OrdinalIgnoreCase))
                problems.Add("catalog repo-manifest integrity hash does not match repo.manifest.json (catalog=" + catalog.RepoManifestIntegrityHash + ", repo=" + repoManifest.IntegrityHash + ")");

            // Per-INF checks: path traversal, Net class, signature status.
            foreach (var pkg in catalog.Packages)
            {
                if (packaging && !pkg.RedistributionAllowed)
                    problems.Add("packaging: package '" + pkg.SourceName + "' is not redistributable (vendor=" + pkg.Vendor + ", license=" + pkg.LicenseStatus + ")");
                // Signature status is recorded per package (the catalog file's .cat covers its INFs).
                bool sigOk = pkg.SignatureStatus == "trusted" || (acceptUnsigned && pkg.SignatureStatus == "accepted-unsigned");
                if (!sigOk)
                    problems.Add("signature not trusted: package '" + pkg.SourceName + "' (status=" + pkg.SignatureStatus + (packaging ? ", packaging requires trusted" : "") + ")");
                foreach (var inf in pkg.Infs)
                {
                    string abs = OfflineRepoGenerator.SafeAbsolute(repoDir, inf.RelativePath);
                    if (abs == null) { problems.Add("path traversal in catalog entry: " + inf.RelativePath); continue; }
                    if (!File.Exists(abs)) { problems.Add("missing file: " + inf.RelativePath); continue; }
                    bool isNic = string.Equals(inf.Class, "Net", StringComparison.OrdinalIgnoreCase)
                        || (inf.ClassGuid ?? "").Trim().Equals(NicBootstrapImporter.NetClassGuid, StringComparison.OrdinalIgnoreCase);
                    if (!isNic) problems.Add("non-Net INF in catalog: " + inf.RelativePath + " (Class=" + inf.Class + ")");
                    if (inf.ContentHash.Length > 0)
                    {
                        string rawHash = Sha256File(abs);
                        if (!string.Equals(rawHash, inf.ContentHash, StringComparison.OrdinalIgnoreCase))
                            problems.Add("hash mismatch: " + inf.RelativePath);
                    }
                }
            }
            return problems;
        }

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
    }

    // =================================================================================
    //  Packaging (copy a licensed+verified bootstrap repo into Offline and Lan products)
    // =================================================================================

    public static class NicBootstrapPackager
    {
        /// <summary>Verify (packaging mode) then copy the bootstrap repo into each target product dir under
        /// a 'NicBootstrap' subfolder. Returns true if copied; false (with reason) if skipped.</summary>
        public static bool Package(string srcRepoDir, string offlineDir, string lanDir, L logger, out string reason)
        {
            reason = "";
            if (string.IsNullOrWhiteSpace(srcRepoDir) || !Directory.Exists(srcRepoDir)) { reason = "bootstrap source repo not found: " + (srcRepoDir ?? "(null)"); return false; }
            var problems = NicBootstrapVerifier.Verify(srcRepoDir, true, false, logger);
            if (problems.Count > 0)
            {
                reason = "bootstrap repo failed packaging verification (" + problems.Count + " problems): " + string.Join("; ", problems);
                if (logger != null) logger.W("[WARN] NIC bootstrap 打包跳过 / packaging skipped: " + reason);
                return false;
            }
            foreach (var target in new[] { offlineDir, lanDir })
            {
                if (string.IsNullOrWhiteSpace(target)) continue;
                string dest = Path.Combine(target, "NicBootstrap");
                if (Directory.Exists(dest)) { try { Directory.Delete(dest, true); } catch { } }
                CopyTree(srcRepoDir, dest);
                if (logger != null) logger.W("[INFO] NIC bootstrap 打包 / packaged into " + dest);
            }
            reason = "packaged into Offline+Lan";
            return true;
        }

        private static void CopyTree(string src, string dest)
        {
            Directory.CreateDirectory(dest);
            var stack = new Stack<KeyValuePair<string, string>>();
            stack.Push(new KeyValuePair<string, string>(src, dest));
            while (stack.Count > 0)
            {
                var kv = stack.Pop();
                Directory.CreateDirectory(kv.Value);
                string[] files;
                try { files = Directory.GetFiles(kv.Key); }
                catch { continue; }
                foreach (var f in files)
                {
                    string target = Path.Combine(kv.Value, Path.GetFileName(f));
                    try { File.Copy(f, target, true); } catch { }
                }
                string[] dirs;
                try { dirs = Directory.GetDirectories(kv.Key); }
                catch { continue; }
                foreach (var d in dirs) stack.Push(new KeyValuePair<string, string>(d, Path.Combine(kv.Value, Path.GetFileName(d))));
            }
        }
    }

    // =================================================================================
    //  NIC bootstrap install helper (used by the offline --repo path; LAN client has its own inline copy)
    // =================================================================================

    public sealed class NicBootstrapResult
    {
        public bool Attempted;
        public int Ok, Fail;
        public string Error = "";
    }

    /// <summary>Installs a missing NIC driver from a local bootstrap offline repo so the machine can reach the
    /// network, mirroring the LAN client's bootstrap step. No-op (Attempted=false) when no bootstrap repo is
    /// configured or no NIC lacks a driver.</summary>
    public static class NicBootstrap
    {
        public static NicBootstrapResult TryInstall(string bootstrapRepoDir, OsTarget osTarget, IHardwareCollector hc,
            Machine machine, bool deploy, bool whatIf, bool force, int installTimeoutSeconds,
            bool dedupeByContent, bool verifySignature, L logger,
            ISignatureVerifier sig, IAdminChecker admin, IPnpInstallRunner runner, ISystemOps sys, CancellationToken ct)
        {
            var result = new NicBootstrapResult();
            if (string.IsNullOrWhiteSpace(bootstrapRepoDir) || !File.Exists(Path.Combine(bootstrapRepoDir, "repo.manifest.json")))
                return result;
            var collector = hc ?? new WmiHardwareCollector();
            var devices = collector.Collect(logger);
            var nicLacking = devices.Where(LanDriverClient.IsNicLackingDriver).ToList();
            if (nicLacking.Count == 0) return result;
            result.Attempted = true;
            try
            {
                var boot = OfflineRepo.Open(bootstrapRepoDir, logger);
                var installer = new OfflineInstaller(boot, osTarget ?? new OsTarget(), new StaticHardwareCollector(nicLacking), machine, logger,
                    deploy, whatIf, force, installTimeoutSeconds, dedupeByContent, verifySignature, sig, admin, runner, sys);
                var r = installer.Scan(ct, null);
                foreach (var x in r.Matches) x.Pick = true;
                var s = installer.Install(r, false, null);
                result.Ok = s.Ok; result.Fail = s.Fail;
                if (logger != null) logger.W("[INFO] 离线 NIC 引导 / Offline NIC bootstrap: matches=" + r.Matches.Count + " ok=" + s.Ok + " fail=" + s.Fail);
            }
            catch (Exception ex) { result.Error = ex.Message; if (logger != null) logger.W("[WARN] 离线 NIC 引导失败 / Offline NIC bootstrap failed: " + ex.Message); result.Fail++; }
            return result;
        }
    }

    // =================================================================================
    //  CLI entry point (NicBootstrapManager.exe)
    // =================================================================================

    public static class NicBootstrapProgram
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                var cli = NicBootstrapCli.Parse(args);
                if (cli.Help || cli.Action == null) { PrintHelp(); return 0; }
                string logDir = Environment.ExpandEnvironmentVariables("%ProgramData%\\UniversalDriverAssistant\\Logs");
                using (var log = new L(logDir))
                {
                    string sourcesJson = NicBootstrapSources.ResolveDefaultPath(cli.SourcesJson);
                    var sourcesCat = NicBootstrapSources.LoadOrDefault(sourcesJson);

                    if (cli.Action == "list-sources")
                    {
                        Console.WriteLine("NIC bootstrap sources catalog: " + sourcesJson + " (as of " + sourcesCat.AsOf + ")");
                        foreach (var v in sourcesCat.Vendors)
                            Console.WriteLine("  " + v.Vendor + "  PCI=[" + string.Join(",", v.PciVendorIds) + "] USB=[" + string.Join(",", v.UsbVendorIds) +
                                "]  license=" + v.LicenseStatus + "  redistributable=" + v.RedistributionAllowedByDefault + "  " + v.OfficialPage);
                        Console.WriteLine("  common USB NICs:");
                        foreach (var n in sourcesCat.CommonUsbNics)
                            Console.WriteLine("    " + n.Name + " (VID_" + n.UsbVid + "&PID_" + n.UsbPid + ", " + n.Vendor + ")");
                        return 0;
                    }
                    if (cli.Action == "import") return DoImport(cli, sourcesCat, sourcesJson, log);
                    if (cli.Action == "coverage") return DoCoverage(cli, sourcesCat, log);
                    if (cli.Action == "verify") return DoVerify(cli, log);
                    if (cli.Action == "package") return DoPackage(cli, log);
                    PrintHelp(); return 0;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("NicBootstrapManager ERROR: " + ex);
                return 2;
            }
        }

        private static int DoImport(NicBootstrapCli cli, NicBootstrapSourcesCatalog sourcesCat, string sourcesJson, L log)
        {
            var importSources = BuildImportSources(cli);
            if (importSources.Count == 0) { Console.Error.WriteLine("no --source/--from-repo/--from-export given"); return 2; }
            var osTarget = new OsTarget { Architecture = string.IsNullOrEmpty(cli.OsArch) ? "amd64" : cli.OsArch, MajorVersion = cli.OsVer <= 0 ? 10 : cli.OsVer };
            string asOf = string.IsNullOrEmpty(cli.AsOf) ? DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) : cli.AsOf;
            var importer = new NicBootstrapImporter(new WinTrustSignatureVerifier(), log);
            var report = importer.Import(cli.Repo, importSources, sourcesCat, osTarget, asOf, cli.AcceptUnsigned, CancellationToken.None, null);
            Console.WriteLine("NIC bootstrap import -> " + report.RepoDir);
            Console.WriteLine("  sources=" + report.SourcesProcessed + " acceptedInfs=" + report.AcceptedInfs + " superseded=" + report.Superseded + " packages=" + report.PackagesEmitted);
            Console.WriteLine("  rejections=" + report.RejectedInfs);
            foreach (var r in report.Rejections) Console.WriteLine("    REJECT: " + r);
            foreach (var w in report.Warnings) Console.WriteLine("    WARN:   " + w);
            Console.WriteLine("  repoIntegrityHash=" + report.RepoIntegrityHash);
            Console.WriteLine("  catalogIntegrityHash=" + report.CatalogIntegrityHash);
            if (!report.Rebuilt) { Console.WriteLine("  (bootstrap repo not (re)built: no acceptable candidates)"); return 1; }
            return 0;
        }

        private static int DoCoverage(NicBootstrapCli cli, NicBootstrapSourcesCatalog sourcesCat, L log)
        {
            var coverage = NicBootstrapCoverageBuilder.Build(cli.Repo, sourcesCat);
            Console.WriteLine(NicBootstrapCoverageBuilder.ToText(coverage));
            File.WriteAllText(Path.Combine(cli.Repo, "coverage-report.json"),
                new JavaScriptSerializer().Serialize(coverage), new UTF8Encoding(false));
            return 0;   // gaps are informational, not a failure
        }

        private static int DoVerify(NicBootstrapCli cli, L log)
        {
            var problems = NicBootstrapVerifier.Verify(cli.Repo, cli.Packaging, cli.AcceptUnsigned, log);
            foreach (var p in problems) Console.WriteLine("PROBLEM: " + p);
            Console.WriteLine(problems.Count == 0 ? "VERIFY: OK" : "VERIFY: FAILED (" + problems.Count + " problems)");
            return problems.Count == 0 ? 0 : 1;
        }

        private static int DoPackage(NicBootstrapCli cli, L log)
        {
            string reason;
            bool ok = NicBootstrapPackager.Package(cli.Repo, cli.OfflineDir, cli.LanDir, log, out reason);
            Console.WriteLine(ok ? "PACKAGE: " + reason : "PACKAGE: skipped - " + reason);
            return ok ? 0 : 1;
        }

        /// <summary>Turn the CLI flags into a list of import sources. --from-repo enumerates packages/*,
        /// --from-export enumerates immediate subdirs (and the dir itself) that contain INFs.</summary>
        private static List<NicBootstrapImportSource> BuildImportSources(NicBootstrapCli cli)
        {
            var list = new List<NicBootstrapImportSource>();
            foreach (var s in cli.Sources) list.Add(new NicBootstrapImportSource { Directory = s.Path, SourceName = NameOrAuto(s.Path, "pkg"), Origin = "package", ExpectedSha256 = s.ExpectedSha256, RedistributionAllowedOverride = s.Redistributable });
            foreach (var r in cli.FromRepos)
            {
                string pkgs = Path.Combine(r, "packages");
                string root = Directory.Exists(pkgs) ? pkgs : r;
                foreach (var d in SafeSubdirs(root))
                    list.Add(new NicBootstrapImportSource { Directory = d, SourceName = NameOrAuto(d, "repo"), Origin = "repo" });
            }
            foreach (var e in cli.FromExports)
            {
                if (HasInf(e)) list.Add(new NicBootstrapImportSource { Directory = e, SourceName = NameOrAuto(e, "export"), Origin = "export" });
                foreach (var d in SafeSubdirs(e))
                    if (HasInf(d)) list.Add(new NicBootstrapImportSource { Directory = d, SourceName = NameOrAuto(d, "export"), Origin = "export" });
            }
            return list;
        }

        private static List<string> SafeSubdirs(string root)
        {
            var result = new List<string>();
            if (!Directory.Exists(root)) return result;
            string[] dirs;
            try { dirs = Directory.GetDirectories(root); }
            catch { return result; }
            foreach (var d in dirs) result.Add(d);
            return result;
        }

        private static bool HasInf(string dir)
        {
            if (!Directory.Exists(dir)) return false;
            try { return Directory.GetFiles(dir, "*.inf", SearchOption.AllDirectories).Length > 0; }
            catch { return false; }
        }

        private static string NameOrAuto(string path, string prefix)
        {
            try
            {
                string n = Path.GetFileName(path.TrimEnd('\\', '/'));
                if (!string.IsNullOrEmpty(n)) return n;
            }
            catch { }
            return prefix;
        }

        private static void PrintHelp()
        {
            Console.WriteLine("NicBootstrapManager - Phase 4 NIC bootstrap supply chain (no driver binaries downloaded)");
            Console.WriteLine("  import --repo <dir> --source <pkgdir>[::<sha256>] [--source ...] [--from-repo <offlineRepoDir>]");
            Console.WriteLine("         [--from-export <pnputilExportDir>] [--sources-json <file>] [--os amd64 --osver 10]");
            Console.WriteLine("         [--accept-unsigned] [--redistributable] [--as-of <isoDate>]");
            Console.WriteLine("  coverage --repo <dir> [--sources-json <file>]");
            Console.WriteLine("  verify --repo <dir> [--sources-json <file>] [--packaging] [--accept-unsigned]");
            Console.WriteLine("  package --repo <srcRepo> --offline <dir> --lan <dir>    # copy licensed+verified repo into both products");
            Console.WriteLine("  list-sources [--sources-json <file>]");
            Console.WriteLine("Flags --redistributable and an inline ::<sha256> apply to the preceding --source (operator confirms license/hash).");
        }
    }

    public sealed class NicBootstrapCli
    {
        public string Action;       // import | coverage | verify | package | list-sources
        public string Repo = "";
        public string SourcesJson = "";
        public string OsArch = "";
        public int OsVer;
        public string AsOf = "";
        public bool AcceptUnsigned, Packaging, Help;
        public string OfflineDir = "", LanDir = "";
        public List<NicBootstrapCliSource> Sources = new List<NicBootstrapCliSource>();
        public List<string> FromRepos = new List<string>();
        public List<string> FromExports = new List<string>();

        public static NicBootstrapCli Parse(string[] args)
        {
            var p = new NicBootstrapCli();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--help" || a == "-h") p.Help = true;
                else if (a == "import" || a == "coverage" || a == "verify" || a == "package" || a == "list-sources") p.Action = a;
                else if (a == "--repo" && i + 1 < args.Length) p.Repo = args[++i];
                else if (a == "--sources-json" && i + 1 < args.Length) p.SourcesJson = args[++i];
                else if (a == "--offline" && i + 1 < args.Length) p.OfflineDir = args[++i];
                else if (a == "--lan" && i + 1 < args.Length) p.LanDir = args[++i];
                else if (a == "--os" && i + 1 < args.Length) p.OsArch = args[++i];
                else if (a == "--osver" && i + 1 < args.Length) { int v; if (int.TryParse(args[++i], out v)) p.OsVer = v; }
                else if (a == "--as-of" && i + 1 < args.Length) p.AsOf = args[++i];
                else if (a == "--accept-unsigned") p.AcceptUnsigned = true;
                else if (a == "--packaging") p.Packaging = true;
                else if (a == "--redistributable") { if (p.Sources.Count > 0) p.Sources[p.Sources.Count - 1].Redistributable = true; }
                else if (a == "--source" && i + 1 < args.Length)
                {
                    string v = args[++i];
                    string path = v, hash = "";
                    int idx = v.IndexOf("::", StringComparison.Ordinal);
                    if (idx > 0) { path = v.Substring(0, idx); hash = v.Substring(idx + 2); }
                    p.Sources.Add(new NicBootstrapCliSource { Path = path, ExpectedSha256 = hash.Trim() });
                }
                else if (a == "--from-repo" && i + 1 < args.Length) p.FromRepos.Add(args[++i]);
                else if (a == "--from-export" && i + 1 < args.Length) p.FromExports.Add(args[++i]);
            }
            return p;
        }
    }

    public sealed class NicBootstrapCliSource
    {
        public string Path = "";
        public string ExpectedSha256 = "";
        public bool Redistributable;
    }
}
