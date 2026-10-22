// LanHarness.cs - Phase 3 LAN protocol end-to-end tests.
//
// Compiles alongside the real sources (Run-LanTests.ps1). Uses the real LanDriverServer (HttpListener)
// on a loopback ephemeral port and the real LanDriverClient, with synthetic INFs, a trusting signature
// verifier, a fake pnputil runner and no-op system ops. No real drivers are installed, no service is
// registered, no firewall is touched.
// Covers: health, report+match, download+verify+install, download hash tamper, token auth, path
// traversal, idempotent duplicate requests, cancellation, disconnect retry/backoff, NIC bootstrap
// (present and absent), and rejection of arbitrary client/import paths.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace UDA
{
    internal static class LanHarness
    {
        private static int _asserts;
        private static int _failed;
        private static string _current;
        private static L _log;

        private sealed class InlineProgress<T> : IProgress<T>
        {
            private readonly Action<T> _report;
            public InlineProgress(Action<T> report) { _report = report; }
            public void Report(T value) { if (_report != null) _report(value); }
        }

        private static int Main(string[] args)
        {
            string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "uda-lan-tests");
            Directory.CreateDirectory(root);
            // Never write fake installation receipts into the real machine's ProgramData.
            LanInstallReceiptStore.UseTestStore(Path.Combine(root, "test-receipts.txt"));
            LanPnpRejectionStore.UseTestStore(Path.Combine(root, "test-pnp-rejections.txt"));
            // Persistent service event log goes to a test-scoped directory so the resilience tests can
            // assert on it and never touch the real ProgramData log.
            ServiceEventLog.Initialize(Path.Combine(root, "events"));
            ServiceEventLog.InstallGlobalHandlers();
            using (_log = new L(Path.Combine(root, "logs")))
            {
                Run("Lan.Health", () => TestHealth(root));
                Run("Lan.ReportAndMatch", () => TestReportAndMatch(root));
                Run("Lan.ModelPackageSharedDownload", () => TestModelPackageSharedDownload(root));
                Run("Lan.ModelArchiveCompactPaths", () => TestModelArchiveCompactPaths(root));
                Run("Lan.InstallEventTelemetryProtocol", () => TestInstallEventTelemetryProtocol(root));
                Run("Lan.DownloadPackagePreGeneration", () => TestDownloadPackagePreGeneration(root));
                Run("Lan.DownloadVerifyInstall", () => TestDownloadVerifyInstall(root));
                Run("Lan.ClusterDirectDownloadRouting", () => TestClusterDirectDownloadRouting(root));
                Run("Lan.MachineImportJobs", () => TestMachineImportJobs(root));
                Run("Lan.AdminAccountsAndAudit", () => TestAdminAccountsAndAudit(root));
                Run("Lan.SlowLargeDownload", () => TestSlowLargeDownload(root));
                Run("Lan.DownloadHashTamper", () => TestDownloadHashTamper(root));
                Run("Lan.Token", () => TestToken(root));
                Run("Lan.PathTraversal", () => TestPathTraversal(root));
                Run("Lan.Idempotent", () => TestIdempotent(root));
                Run("Lan.Cancel", () => TestCancel(root));
                Run("Lan.DisconnectRetry", () => TestDisconnectRetry(root));
                Run("Lan.NicBootstrap", () => TestNicBootstrap(root));
                Run("Lan.NoArbitraryPath", () => TestNoArbitraryPath(root));
                Run("Lan.CacheCleanupNormal", () => TestCacheCleanupNormal(root));
                Run("Lan.CacheCleanupFailure", () => TestCacheCleanupFailure(root));
                Run("Lan.CacheCleanupCancel", () => TestCacheCleanupCancel(root));
                Run("Lan.CacheOtherInstanceSafe", () => TestCacheOtherInstanceSafe(root));
                Run("Lan.CacheNoOutsideDelete", () => TestCacheNoOutsideDelete(root));
                Run("Lan.CacheStaleCleanup", () => TestCacheStaleCleanup(root));
                Run("Lan.ScanMissingBadEmptyVersion", () => TestScanMissingBadEmptyVersion(root));
                Run("Lan.PnputilLatestSuppression", () => TestPnputilLatestSuppression(root));
                Run("Lan.DriverStoreFullRanking", () => TestDriverStoreFullRanking());
                Run("Lan.ScanRanksUnstagedPackage", () => TestScanRanksUnstagedPackage(root));
                Run("Lan.ScanSharedFileCache", () => TestScanSharedFileCache(root));
                Run("Lan.ScanParallelGroupingAndCancel", () => TestScanParallelGroupingAndCancel(root));
                Run("Lan.ReceiptNoMaskMissingDriver", () => TestReceiptNoMaskMissingDriver(root));
                Run("Lan.ClosedLoopNewDeviceThenDone", () => TestClosedLoopNewDeviceThenDone(root));
                Run("Lan.ClosedLoopDedupSameItem", () => TestClosedLoopDedupSameItem(root));
                Run("Lan.ClosedLoopMaxRounds", () => TestClosedLoopMaxRounds(root));
                Run("Lan.ClosedLoopCancel", () => TestClosedLoopCancel(root));
                Run("Lan.ClosedLoopNoAutoUpdate", () => TestClosedLoopNoAutoUpdate(root));
                Run("Lan.InfApplicability.ServerGates", () => TestApplicabilityServerGates(root));
                Run("Lan.InfApplicability.ExactIdPriority", () => TestApplicabilityExactIdPriority(root));
                Run("Lan.InfApplicability.UnmatchedReported", () => TestApplicabilityUnmatchedReported(root));
                Run("Lan.InfApplicability.ScanInstallConsistency", () => TestApplicabilityScanInstallConsistency(root));
                Run("Lan.InfApplicability.PreInstallRevalidation", () => TestApplicabilityPreInstallRevalidation(root));
                Run("Lan.Resilience.AcceptLoopRecovers", () => TestAcceptLoopRecovers(root));
                Run("Lan.Resilience.RepoReloadKeepsOldRepo", () => TestRepoReloadKeepsOldRepo(root));
                Run("Lan.Resilience.WatcherErrorTriggersRescan", () => TestWatcherErrorTriggersRescan(root));
                Run("Lan.Resilience.WatchEventsSingleFlight", () => TestWatchEventsSingleFlight(root));
                Run("Lan.Resilience.StopReleasesResources", () => TestStopReleasesResources(root));
                Run("Lan.Resilience.ErrorLogPersisted", () => TestErrorLogPersisted(root));
                Run("Lan.AdminDedupe", () => TestAdminDedupe(root));

                Console.WriteLine("-------------------------------------------------");
                Console.WriteLine("Asserts: " + _asserts + "  Failed: " + _failed);
                if (_failed > 0) { Console.WriteLine("RESULT: FAIL"); return 1; }
                Console.WriteLine("RESULT: PASS");
                return 0;
            }
        }

        private static void Run(string name, Action body)
        {
            string filter = Environment.GetEnvironmentVariable("UDA_TEST_FILTER");
            if (!string.IsNullOrEmpty(filter) && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) return;
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
            File.WriteAllText(path, new StringBuilder()
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
                .ToString(), new UTF8Encoding(false));
            return path;
        }

        private static string MakeRepo(string repoDir, string srcDir)
        {
            new OfflineRepoGenerator(new TrustingSignatureVerifier(), _log).Generate(repoDir, new[] { srcDir },
                new OsTarget { Architecture = "amd64", MajorVersion = 10 }, null, "2026-07-28T00:00:00Z", CancellationToken.None, null);
            return repoDir;
        }

        private static int GetFreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        private sealed class Env : IDisposable
        {
            public LanDriverServer Server;
            public string Url;
            public void Dispose() { if (Server != null) { try { Server.Stop(); } catch { } try { Server.Dispose(); } catch { } } }
        }

        private static Env StartEnv(string repoDir, string token, List<string> importAllowRoots, string advertiseUrl = null, string adminCredentialsFile = null)
        {
            int port = GetFreePort();
            string url = "http://localhost:" + port.ToString(CultureInfo.InvariantCulture);
            if (string.Equals(advertiseUrl, "$self", StringComparison.Ordinal)) advertiseUrl = url;
            var cfg = new LanServerConfig { Port = port, RepoRoot = repoDir ?? "", Token = token ?? "", OsArchitecture = "amd64", OsMajorVersion = 10, Host = "localhost", AdvertiseUrl = advertiseUrl ?? "" };
            cfg.AdminCredentialsFile = adminCredentialsFile ?? "";
            if (importAllowRoots != null) cfg.ImportAllowRoots = importAllowRoots;
            var server = new LanDriverServer(cfg, _log);
            server.ImportSignatureVerifier = new TrustingSignatureVerifier();   // test seam: avoid cold WinVerifyTrust
            server.Start();
            var env = new Env { Server = server, Url = url };
            // Wait until the listener is accepting AND the background repo init finished (loaded,
            // failed, or nothing to load) — the repo now loads asynchronously by design.
            for (int i = 0; i < 600; i++)
            {
                try
                {
                    var r = HttpGet(env.Url + "/api/health", null);
                    if (r.Item1 == 200)
                    {
                        var h = new JavaScriptSerializer().Deserialize<LanHealth>(r.Item2);
                        bool noRepo = string.IsNullOrEmpty(repoDir) || !File.Exists(Path.Combine(repoDir, "repo.manifest.json"));
                        if (h != null && ((!h.ImportRunning && h.RepoLoaded) || noRepo || string.Equals(h.ImportStage, "failed", StringComparison.Ordinal))) return env;
                    }
                }
                catch { }
                Thread.Sleep(50);
            }
            return env;
        }

        private static D Device(string hwid, string currentInf, string currentVersion, string deviceInstance)
        {
            return new D { Name = "Device " + hwid, Id = deviceInstance ?? hwid, Class = "Net", Inf = currentInf ?? "", Version = currentVersion ?? "",
                ClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}", H = new List<string> { hwid }, K = new List<string>() };
        }

        private static LanDriverClient NewClient(string url, string token, string bootstrapRepo, string stagingDir, List<D> devices,
            FakePnpInstallRunner runner, int controlTimeoutMs = 30000)
        {
            var cfg = new LanClientConfig
            {
                ServerUrl = url, Token = token ?? "", BootstrapRepoDir = bootstrapRepo ?? "",
                TimeoutMs = controlTimeoutMs, DownloadTimeoutMs = 10000, MaxRetries = 4, BackoffInitialMs = 50, StagingDir = stagingDir,
                Deploy = false, WhatIf = false, Force = false, InstallTimeoutSeconds = 5,
                DedupeByContent = true, VerifySignature = true
            };
            return new LanDriverClient(cfg, new Machine { Maker = "TestVendor", Model = "ModelA", Sku = "SKU1" },
                new StaticHardwareCollector(devices), new OsTarget { Architecture = "amd64", MajorVersion = 10 },
                _log, new TrustingSignatureVerifier(), new AlwaysAdminChecker(), runner, new NoOpSystemOps());
        }

        // ----- raw HTTP -----

        private static Tuple<int, string> HttpGet(string url, string token)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET"; req.Timeout = 10000; req.ReadWriteTimeout = 10000;
                req.Headers["X-UDA-Protocol"] = LanProtocol.Version.ToString(CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(token)) req.Headers["X-UDA-Token"] = token;
                using (var resp = (HttpWebResponse)req.GetResponse()) return Tuple.Create((int)resp.StatusCode, ReadBody(resp));
            }
            catch (WebException wex) { var r = wex.Response as HttpWebResponse; return Tuple.Create(r != null ? (int)r.StatusCode : 0, r != null ? ReadBody(r) : (wex.Message ?? "")); }
        }

        private static Tuple<int, string> HttpPost(string url, string body, string token)
        { return HttpPostProtocol(url, body, token, LanProtocol.Version.ToString(CultureInfo.InvariantCulture)); }

        private static Tuple<int, string> HttpPostProtocol(string url, string body, string token, string protocol)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST"; req.Timeout = 10000; req.ReadWriteTimeout = 10000; req.ContentType = "application/json";
                req.Headers[LanProtocol.HeaderProtocol] = protocol;
                if (!string.IsNullOrEmpty(token)) req.Headers["X-UDA-Token"] = token;
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                using (var rs = req.GetRequestStream()) rs.Write(bytes, 0, bytes.Length);
                using (var resp = (HttpWebResponse)req.GetResponse()) return Tuple.Create((int)resp.StatusCode, ReadBody(resp));
            }
            catch (WebException wex) { var r = wex.Response as HttpWebResponse; return Tuple.Create(r != null ? (int)r.StatusCode : 0, r != null ? ReadBody(r) : (wex.Message ?? "")); }
        }

        private static Tuple<int, string> HttpAdmin(string url, string method, string body, CookieContainer cookies)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url); req.Method = method; req.Timeout = 10000; req.ReadWriteTimeout = 10000;
                req.CookieContainer = cookies; req.Headers[LanProtocol.HeaderProtocol] = "1";
                if (body != null)
                {
                    req.ContentType = "application/json"; byte[] bytes = Encoding.UTF8.GetBytes(body); req.ContentLength = bytes.Length;
                    using (var stream = req.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
                }
                using (var resp = (HttpWebResponse)req.GetResponse()) return Tuple.Create((int)resp.StatusCode, ReadBody(resp));
            }
            catch (WebException wex) { var resp = wex.Response as HttpWebResponse; return Tuple.Create(resp != null ? (int)resp.StatusCode : 0, resp != null ? ReadBody(resp) : (wex.Message ?? "")); }
        }

        private static string ReadBody(HttpWebResponse resp)
        {
            try { using (var s = resp.GetResponseStream()) using (var sr = new StreamReader(s, Encoding.UTF8)) return sr.ReadToEnd(); }
            catch { return ""; }
        }

        private static string J(object o) { return new JavaScriptSerializer().Serialize(o); }

        // ---------------------------------------------------------------------------
        //  1. Health.
        // ---------------------------------------------------------------------------
        private static void TestHealth(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "health"));
            using (var env = StartEnv(null, null, null))
            {
                var r = HttpGet(env.Url + "/api/health", null);
                T(r.Item1 == 200, "health status=" + r.Item1);
                var h = new JavaScriptSerializer().Deserialize<LanHealth>(r.Item2);
                T(h != null && h.Protocol == LanProtocol.Version, "health protocol=" + (h != null ? h.Protocol : -1));
                T(h != null && h.ServerVersion == LanProtocol.ServerVersion, "serverVersion=" + (h != null ? h.ServerVersion : ""));
                T(h != null && !h.RepoLoaded, "repoLoaded=false when no repo");
                T(h != null && !h.TokenRequired, "tokenRequired=false");
            }
        }

        // ---------------------------------------------------------------------------
        //  2. Report + match (server returns the right candidate for the device).
        // ---------------------------------------------------------------------------
        private static void TestReportAndMatch(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "report"));
            string src = Path.Combine(dir, "PkgA"); Directory.CreateDirectory(src);
            WriteInf(src, "dev.inf", "PCI\\VEN_1000&DEV_1000", "03/14/2026", "2.0.0.0", "NTamd64.10");
            WriteInf(src, "acpi.inf", "ACPI\\INT34BB", "05/12/2020", "30.100.2020.7", "NTamd64.10");
            string repo = MakeRepo(Path.Combine(dir, "repo"), src);
            using (var env = StartEnv(repo, null, null))
            {
                var req = new LanReportRequest
                {
                    Protocol = LanProtocol.Version, RequestId = "req-match-1",
                    MachineManufacturer = "TestVendor", MachineModel = "ModelA", MachineSku = "SKU1",
                    OsArchitecture = "amd64", OsMajorVersion = 10,
                    Devices = new List<LanDeviceDto> { new LanDeviceDto { DeviceInstanceId = "DEV1", Class = "Net",
                        ClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}", HardwareIds = new List<string> { "PCI\\VEN_1000&DEV_1000" } } }
                };
                var r = HttpPost(env.Url + "/api/report", J(req), null);
                T(r.Item1 == 200, "report status=" + r.Item1);
                var resp = new JavaScriptSerializer().Deserialize<LanReportResponse>(r.Item2);
                T(resp != null && resp.Ok, "report ok");
                T(resp != null && resp.Candidates.Count == 1, "candidates=" + (resp != null ? resp.Candidates.Count : -1));
                var c = resp.Candidates[0];
                T(c.InfRelativePath.EndsWith("dev.inf", StringComparison.OrdinalIgnoreCase), "candidate inf=" + c.InfRelativePath);
                T(c.Tier == 4, "candidate tier=" + c.Tier);
                T(c.MatchedId == "PCI\\VEN_1000&DEV_1000", "candidate matchedId=" + c.MatchedId);
                T(!string.IsNullOrEmpty(c.ContentHash), "candidate hash present");
                T(c.DriverVerVersion == "2.0.0.0", "candidate version=" + c.DriverVerVersion);
                // An unmatched device yields no candidate.
                var req2 = new LanReportRequest { Protocol = LanProtocol.Version, RequestId = "req-match-2", OsArchitecture = "amd64", OsMajorVersion = 10,
                    Devices = new List<LanDeviceDto> { new LanDeviceDto { DeviceInstanceId = "DEVX", HardwareIds = new List<string> { "PCI\\VEN_FFFF&DEV_FFFF" } } } };
                var r2 = HttpPost(env.Url + "/api/report", J(req2), null);
                var resp2 = new JavaScriptSerializer().Deserialize<LanReportResponse>(r2.Item2);
                T(resp2 != null && resp2.Candidates.Count == 0, "unmatched candidates=" + (resp2 != null ? resp2.Candidates.Count : -1));
                // Missing ACPI/HID-class drivers may expose only a generic WMI HardwareID while
                // the exact ID remains in DeviceInstanceId. The server must enrich old/new clients.
                var req3 = new LanReportRequest { Protocol = LanProtocol.Version, RequestId = "req-match-acpi", OsArchitecture = "amd64", OsMajorVersion = 10,
                    Devices = new List<LanDeviceDto> { new LanDeviceDto { DeviceInstanceId = "ACPI\\VEN_INT&DEV_34BB\\0",
                        HardwareIds = new List<string> { "ACPI\\PNP0C02" }, CompatibleIds = new List<string>() } } };
                var resp3 = new JavaScriptSerializer().Deserialize<LanReportResponse>(HttpPost(env.Url + "/api/report", J(req3), null).Item2);
                T(resp3 != null && resp3.Candidates.Count == 1, "ACPI instance-ID fallback candidates=" + (resp3 != null ? resp3.Candidates.Count : -1));
                T(resp3 != null && resp3.Candidates[0].InfRelativePath.EndsWith("acpi.inf", StringComparison.OrdinalIgnoreCase), "ACPI fallback selected SIO INF");
            }
        }

        // Current protocol 2 is accepted, and protocol 1 remains accepted only for authenticated
        // install telemetry to drain queues created by v5.2.0 clients.
        private static void TestInstallEventTelemetryProtocol(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "install-event-telemetry"));
            string src = Path.Combine(dir, "PkgA"); Directory.CreateDirectory(src);
            WriteInf(src, "telemetry.inf", "PCI\\VEN_1A00&DEV_0001", "03/14/2026", "2.0.0.0", "NTamd64.10");
            string repo = MakeRepo(Path.Combine(dir, "repo"), src);
            using (var env = StartEnv(repo, "telemetry-token", null))
            {
                var entry = new DriverInstallEvent
                {
                    EventId = Guid.NewGuid().ToString("N"), RunId = Guid.NewGuid().ToString("N"), Machine = "telemetry-test-machine",
                    Mac = "001122334455", Device = "DEV-TELEMETRY", Name = "Telemetry device", Inf = "telemetry.inf",
                    Version = "2.0.0.0", Status = "skipped", Detail = "ok=0 skip=1 fail=0", Utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                };
                string url = env.Url + "/api/client/install-event", body = J(entry);
                var oldProtocol = HttpPostProtocol(url, body, "telemetry-token", "1");
                T(oldProtocol.Item1 == 200, "legacy telemetry protocol 1 accepted: " + oldProtocol.Item1);
                string eventDirectory = Path.Combine(repo, "install-events");
                string[] legacyFiles = Directory.Exists(eventDirectory) ? Directory.GetFiles(eventDirectory, "*.json") : new string[0];
                T(legacyFiles.Length == 1, "legacy queued telemetry persisted");
                var accepted = HttpPostProtocol(url, body, "telemetry-token", LanProtocol.Version.ToString(CultureInfo.InvariantCulture));
                T(accepted.Item1 == 200, "current protocol telemetry accepted: " + accepted.Item1 + " " + accepted.Item2);
                string[] files = Directory.Exists(eventDirectory) ? Directory.GetFiles(eventDirectory, "*.json") : new string[0];
                T(files.Length == 1, "duplicate event ID remains idempotent in install-events");
                var saved = files.Length == 1 ? new JavaScriptSerializer().Deserialize<DriverInstallEvent>(File.ReadAllText(files[0])) : null;
                T(saved != null && saved.Machine == entry.Machine && saved.Status == "skipped" && saved.Device == entry.Device, "persisted install event retains machine/device/result fields");
            }
        }

        // ---------------------------------------------------------------------------
        //  3. Full client flow: report -> download -> verify SHA-256 -> install.
        // ---------------------------------------------------------------------------
        private static void TestDownloadPackagePreGeneration(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "package-pregeneration"));
            string source = Path.Combine(dir, "source"); Directory.CreateDirectory(source);
            const int count = 24;
            for (int i = 0; i < count; i++)
            {
                string package = Path.Combine(source, "pkg-" + i.ToString("D3", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(package);
                WriteInf(package, "device.inf", "PCI\\VEN_B001&DEV_" + i.ToString("X4", CultureInfo.InvariantCulture), "03/14/2026", "2.0.0.0", "NTamd64.10");
                File.WriteAllBytes(Path.Combine(package, "payload.sys"), Enumerable.Range(0, 32768).Select(n => (byte)((n + i) % 251)).ToArray());
            }
            string repo = MakeRepo(Path.Combine(dir, "repo"), source);
            var progress = new List<string>();
            object progressGate = new object();
            int created = OfflineRepoGenerator.PrepareDownloadPackages(repo, new InlineProgress<string>(message => { lock (progressGate) progress.Add(message); }), 4);
            T(created == count, "parallel package count=" + created);
            string cache = Path.Combine(repo, ".uda-downloads");
            string[] archives = Directory.GetFiles(cache, "*.zip");
            T(archives.Length == count, "archive count=" + archives.Length);
            foreach (var entry in OfflineRepoGenerator.TryLoadManifest(repo).Entries)
            {
                string archivePath = OfflineRepoGenerator.DownloadArchivePath(repo, entry);
                T(File.Exists(archivePath), "archive exists for " + entry.RelativePath);
                using (var fs = File.OpenRead(archivePath)) using (var archive = new ZipArchive(fs, ZipArchiveMode.Read))
                    T(archive.Entries.Any(x => x.Name.Equals("device.inf", StringComparison.OrdinalIgnoreCase)) && archive.Entries.Any(x => x.Name.Equals("payload.sys", StringComparison.OrdinalIgnoreCase)), "archive contains INF and companion for " + entry.RelativePath);
            }
            T(progress.Any(x => x.IndexOf("workers: 4", StringComparison.Ordinal) >= 0), "worker count reported");
            T(progress.Any(x => x.StartsWith("归档预检 " + count + "/" + count, StringComparison.Ordinal)), "completion progress reaches total");
            T(OfflineRepoGenerator.PrepareDownloadPackages(repo, null, 4) == 0, "existing immutable packages reused");
            var autoProgress = new List<string>();
            T(OfflineRepoGenerator.PrepareDownloadPackages(repo, new InlineProgress<string>(message => autoProgress.Add(message)), 0) == 0, "auto worker cache reuse");
            T(autoProgress.Any(x => x.IndexOf("workers: " + ClientScanParallelism.LogicalProcessors(), StringComparison.Ordinal) >= 0), "auto worker count detects all logical processor groups");
            T(OfflineRepoGenerator.PrepareDownloadPackages(repo, null, 128) == 0, "128 worker override accepted");
            int threadPoolMinimum, completionPortMinimum;
            ThreadPool.GetMinThreads(out threadPoolMinimum, out completionPortMinimum);
            T(threadPoolMinimum >= 128, "thread-pool minimum allows 128 package workers");
            bool rejected = false;
            try { OfflineRepoGenerator.PrepareDownloadPackages(repo, null, -1); } catch (ArgumentOutOfRangeException) { rejected = true; }
            T(rejected, "negative worker count rejected");
        }

        private static void TestDownloadVerifyInstall(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "dl"));
            string src = Path.Combine(dir, "PkgA"); Directory.CreateDirectory(src);
            WriteInf(src, "dev.inf", "PCI\\VEN_2000&DEV_2000", "03/14/2026", "2.0.0.0", "NTamd64.10");
            File.WriteAllText(Path.Combine(src, "dev.cat"), "fake-cat", new UTF8Encoding(false));
            string repo = MakeRepo(Path.Combine(dir, "repo"), src);
            using (var env = StartEnv(repo, null, null))
            {
                var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var devices = new List<D> { Device("PCI\\VEN_2000&DEV_2000", "", "", "DEV1") };
                var client = NewClient(env.Url, null, null, Path.Combine(dir, "staging"), devices, runner);
                LanReportResponse report = client.Report(devices, new Machine { Maker = "TestVendor", Model = "ModelA", Sku = "SKU1" }, CancellationToken.None, null);
                T(report != null && report.Ok, "reported (err=" + (report != null ? report.Error : "null") + ")");
                T(report != null && report.Candidates.Count == 1, "candidates=" + (report != null ? report.Candidates.Count : -1));
                var transfer = new List<LanInstallProgress>();
                var res = client.InstallCandidates(report.Candidates, devices, CancellationToken.None, new InlineProgress<LanInstallProgress>(p => transfer.Add(p)));
                T(res.Downloaded == 1, "downloaded=" + res.Downloaded);
                T(res.InstalledOk == 1 && runner.Calls.Count == 1, "installed ok=" + res.InstalledOk + " calls=" + runner.Calls.Count);
                T(runner.Calls[0].EndsWith("dev.inf", StringComparison.OrdinalIgnoreCase), "installed the downloaded inf");
                T(transfer.Any(p => p.TotalBytes > 0 && p.DownloadedBytes > 0 && p.BytesPerSecond >= 0 && p.InfRelativePath.EndsWith("dev.inf", StringComparison.OrdinalIgnoreCase)), "per-driver transfer progress includes total, downloaded bytes, speed and INF");
            }
        }

        private static void TestClusterDirectDownloadRouting(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "cluster-routing"));
            string src = Path.Combine(dir, "PkgA"); Directory.CreateDirectory(src);
            WriteInf(src, "cluster.inf", "PCI\\VEN_C1A0&DEV_0001", "03/14/2026", "2.0.0.0", "NTamd64.10");
            File.WriteAllText(Path.Combine(src, "payload.bin"), new string('x', 8192), new UTF8Encoding(false));
            string repo = MakeRepo(Path.Combine(dir, "repo"), src);
            const string sharedToken = "cluster-shared-token";
            using (var a = StartEnv(repo, sharedToken, null, "$self"))
            using (var b = StartEnv(repo, sharedToken, null, "$self"))
            {
                T(a.Server.TestConfigureClusterPeers(new List<ClusterPeer> { new ClusterPeer { Url = b.Url, Token = sharedToken } }), "valid peer with shared client token accepted");
                bool sawA = false, sawB = false, downloadedFromB = false;
                string candidateUrl = "", candidatePath = "";
                for (int i = 0; i < 32; i++)
                {
                    var request = new LanReportRequest
                    {
                        Protocol = LanProtocol.Version, RequestId = Guid.NewGuid().ToString("N"), MachineName = "cluster-machine-" + i,
                        Token = sharedToken, OsArchitecture = "amd64", OsMajorVersion = 10,
                        Devices = new List<LanDeviceDto> { new LanDeviceDto { DeviceInstanceId = "CLUSTER-DEVICE", Class = "Net", HardwareIds = new List<string> { "PCI\\VEN_C1A0&DEV_0001" } } }
                    };
                    var result = HttpPost(a.Url + "/api/report", J(request), sharedToken);
                    var response = result.Item1 == 200 ? new JavaScriptSerializer().Deserialize<LanReportResponse>(result.Item2) : null;
                    T(response != null && response.Candidates.Count == 1, "cluster candidate returned for request " + i + ", HTTP=" + result.Item1);
                    if (response == null || response.Candidates.Count == 0) continue;
                    candidateUrl = response.Candidates[0].DownloadBaseUrl;
                    candidatePath = response.Candidates[0].InfRelativePath;
                    sawA |= string.Equals(candidateUrl, a.Url, StringComparison.OrdinalIgnoreCase);
                    sawB |= string.Equals(candidateUrl, b.Url, StringComparison.OrdinalIgnoreCase);
                    T(result.Item2.IndexOf("DownloadToken", StringComparison.OrdinalIgnoreCase) < 0, "report response contains no node token");
                    if (sawB && !downloadedFromB)
                    {
                        downloadedFromB = HttpGet(b.Url + "/api/download?path=" + Uri.EscapeDataString(candidatePath), sharedToken).Item1 == 200;
                    }
                }
                T(sawA && sawB, "least-load/hash tie routing selects both cluster nodes");
                T(downloadedFromB, "client can fetch the selected peer archive directly using the shared token");
                var parallelRoutes = new Task<Tuple<int, string>>[40];
                for (int i = 0; i < parallelRoutes.Length; i++)
                {
                    int requestNumber = i;
                    parallelRoutes[i] = Task.Factory.StartNew(delegate
                    {
                        var request = new LanReportRequest
                        {
                            Protocol = LanProtocol.Version, RequestId = Guid.NewGuid().ToString("N"), MachineName = "cluster-peak-" + requestNumber,
                            Token = sharedToken, OsArchitecture = "amd64", OsMajorVersion = 10,
                            Devices = new List<LanDeviceDto> { new LanDeviceDto { DeviceInstanceId = "CLUSTER-PEAK-" + requestNumber, Class = "Net", HardwareIds = new List<string> { "PCI\\VEN_C1A0&DEV_0001" } } }
                        };
                        var result = HttpPost(a.Url + "/api/report", J(request), sharedToken);
                        var response = result.Item1 == 200 ? new JavaScriptSerializer().Deserialize<LanReportResponse>(result.Item2) : null;
                        return new Tuple<int, string>(result.Item1, response != null && response.Candidates.Count == 1 ? response.Candidates[0].DownloadBaseUrl : "");
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }
                Task.WaitAll(parallelRoutes);
                int parallelAccepted = parallelRoutes.Count(t => t.Result.Item1 == 200 && t.Result.Item2.Length > 0);
                bool parallelSawA = parallelRoutes.Any(t => string.Equals(t.Result.Item2, a.Url, StringComparison.OrdinalIgnoreCase));
                bool parallelSawB = parallelRoutes.Any(t => string.Equals(t.Result.Item2, b.Url, StringComparison.OrdinalIgnoreCase));
                T(parallelAccepted == parallelRoutes.Length, "40 simultaneous report requests all receive a driver candidate");
                T(parallelSawA && parallelSawB, "40 simultaneous report requests distribute candidates across both healthy cluster nodes");
                byte[] slowPayload = new byte[32 * 1024 * 1024];
                b.Server.TestDownloadOverride = rel => slowPayload;
                var slowRequest = (HttpWebRequest)WebRequest.Create(b.Url + "/api/download?path=" + Uri.EscapeDataString(candidatePath));
                slowRequest.Proxy = null; slowRequest.Timeout = 10000; slowRequest.ReadWriteTimeout = 10000;
                slowRequest.Headers[LanProtocol.HeaderProtocol] = LanProtocol.Version.ToString(CultureInfo.InvariantCulture);
                slowRequest.Headers[LanProtocol.HeaderToken] = sharedToken;
                HttpWebResponse slowResponse = null; Stream slowStream = null;
                try
                {
                    slowResponse = (HttpWebResponse)slowRequest.GetResponse(); slowStream = slowResponse.GetResponseStream();
                    T(slowStream.ReadByte() >= 0, "busy node begins a long archive transfer");
                    Thread.Sleep(1800); // allow the peer-health poll to observe its active download counter
                    var loadedRequest = new LanReportRequest
                    {
                        Protocol = LanProtocol.Version, RequestId = Guid.NewGuid().ToString("N"), MachineName = "cluster-load-check",
                        Token = sharedToken, OsArchitecture = "amd64", OsMajorVersion = 10,
                        Devices = new List<LanDeviceDto> { new LanDeviceDto { DeviceInstanceId = "CLUSTER-LOAD-DEVICE", Class = "Net", HardwareIds = new List<string> { "PCI\\VEN_C1A0&DEV_0001" } } }
                    };
                    var loadedResult = HttpPost(a.Url + "/api/report", J(loadedRequest), sharedToken);
                    var loadedResponse = loadedResult.Item1 == 200 ? new JavaScriptSerializer().Deserialize<LanReportResponse>(loadedResult.Item2) : null;
                    T(loadedResponse != null && loadedResponse.Candidates.Count == 1 && string.Equals(loadedResponse.Candidates[0].DownloadBaseUrl, a.Url, StringComparison.OrdinalIgnoreCase),
                        "new request is routed away from the peer with an active download");
                }
                finally
                {
                    try { slowRequest.Abort(); } catch { }
                    try { if (slowStream != null) slowStream.Dispose(); } catch { }
                    try { if (slowResponse != null) slowResponse.Dispose(); } catch { }
                    b.Server.TestDownloadOverride = null;
                }
                T(!a.Server.TestConfigureClusterPeers(new List<ClusterPeer> { new ClusterPeer { Url = b.Url, Token = "different-token" } }), "peer with a different client token is rejected");
            }
        }

        private sealed class HeldSignatureVerifier : ISignatureVerifier
        {
            public readonly ManualResetEventSlim Entered = new ManualResetEventSlim(false), Release = new ManualResetEventSlim(false);
            public bool IsTrusted(string path, L logger) { Entered.Set(); if (!Release.Wait(10000)) throw new IOException("test import release timeout"); return true; }
        }
        private static MachineImportJob PollMachineJob(string url, string id, CookieContainer cookies)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(30))
            {
                var response = HttpAdmin(url + "/api/admin/machine-packages/import-status?id=" + id, "GET", null, cookies);
                if (response.Item1 != 200) throw new Exception(response.Item2);
                var job = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(response.Item2)["job"];
                var parsed = new JavaScriptSerializer().Deserialize<MachineImportJob>(J(job));
                if (parsed.State != "running" && parsed.State != "queued") return parsed;
                Thread.Sleep(50);
            }
            throw new Exception("machine import task did not finish");
        }
        private static void TestMachineImportJobs(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "machine-job")), source = Path.Combine(dir, "model-source"); Directory.CreateDirectory(source);
            string inf = WriteInf(source, "model..driver.inf", "PCI\\VEN_AD02&DEV_0001", "03/14/2026", "2.0.0.0", "NTamd64.10");
            string repo = MakeRepo(Path.Combine(dir, "repo"), source), credentials = Path.Combine(dir, "admin.json");
            AdminCredentialStore.Create(credentials, "job-admin", "Task-Admin-Password-123!");
            using (var env = StartEnv(repo, null, null, null, credentials))
            {
                var cookies = new CookieContainer();
                T(HttpAdmin(env.Url + "/api/admin/machine-packages/import-status", "GET", null, cookies).Item1 == 401, "task progress requires administrator authentication");
                T(HttpAdmin(env.Url + "/api/admin/machine-packages/import-log?id=" + Guid.NewGuid().ToString("N"), "GET", null, cookies).Item1 == 401, "task log download requires administrator authentication");
                T(HttpAdmin(env.Url + "/api/admin/login", "POST", J(new { user = "job-admin", password = "Task-Admin-Password-123!" }), cookies).Item1 == 200, "job administrator logs in");
                var rejected = HttpAdmin(env.Url + "/api/admin/machine-packages/import/start", "POST", J(new { Source = source, Mapping = new MachineDriverPackage() }), cookies);
                var rejectedData = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(rejected.Item2);
                T(rejected.Item1 == 400 && rejectedData.ContainsKey("jobId") && !string.IsNullOrEmpty(Convert.ToString(rejectedData["jobId"])), "rejected submission returns a persistent failure record ID");
                T(PollMachineJob(env.Url, Convert.ToString(rejectedData["jobId"]), cookies).State == "failed", "submission errors can be viewed after reopening the browser");
                var policy = new MachineDriverPackage { Manufacturer = "Test Maker", Model = "Async Model", Architecture = "amd64", MajorVersion = 10, MaxBuild = 21999 };
                var held = new HeldSignatureVerifier(); env.Server.ImportSignatureVerifier = held;
                string jobId;
                try
                {
                    var accepted = HttpAdmin(env.Url + "/api/admin/machine-packages/import/start", "POST", J(new { Source = source, Mapping = policy }), cookies);
                    T(accepted.Item1 == 202, "long-running model import is accepted without waiting for repository mutation: " + accepted.Item2);
                    var document = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(accepted.Item2);
                    jobId = new JavaScriptSerializer().Deserialize<MachineImportJob>(J(document["job"])).Id;
                    T(held.Entered.Wait(5000), "server import continues after the submitting HTTP request has returned");
                    var running = HttpAdmin(env.Url + "/api/admin/machine-packages/import-status?id=" + jobId, "GET", null, cookies);
                    T(running.Item1 == 200 && running.Item2.Contains("running") && running.Item2.Contains("Logs"), "live stage and logs remain available while import is blocked");
                }
                finally { held.Release.Set(); }
                var completed = PollMachineJob(env.Url, jobId, cookies);
                T(completed.State == "completed" && completed.Percent == 100 && completed.LogCount > 3, "completed task reports success and actual phase logs");
                var history = HttpAdmin(env.Url + "/api/admin/machine-packages/import-status", "GET", null, cookies);
                T(history.Item1 == 200 && history.Item2.Contains(jobId), "completed task remains in history for a reopened browser");
                var log = HttpAdmin(env.Url + "/api/admin/machine-packages/import-log?id=" + jobId, "GET", null, cookies);
                T(log.Item1 == 200 && log.Item2.Contains("导入成功") && log.Item2.Contains("阶段："), "full persisted task log can be downloaded");
                env.Server.ImportSignatureVerifier = new TrustingSignatureVerifier();
                string noInf = Path.Combine(dir, "empty-model"); Directory.CreateDirectory(noInf); File.WriteAllText(Path.Combine(noInf, "readme.txt"), "no INF");
                string previous = ServerStorage.Read(repo, "repo.manifest.json");
                var failedStart = HttpAdmin(env.Url + "/api/admin/machine-packages/import/start", "POST", J(new { Source = noInf, Mapping = policy }), cookies);
                string failedId = new JavaScriptSerializer().Deserialize<MachineImportJob>(J(new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(failedStart.Item2)["job"])).Id;
                var failure = PollMachineJob(env.Url, failedId, cookies);
                T(failure.State == "failed" && failure.Error.Contains("INF") && Directory.Exists(noInf), "failed import retains its source and exposes an actionable error instead of a generic HTTP 400");
                T(ServerStorage.Read(repo, "repo.manifest.json") == previous, "failed import restores the previous catalog");
                T(LanSecurity.SafeAbsolute(repo, "folder/file..name.inf") != null && OfflineRepoGenerator.SafeAbsolute(repo, "folder/file..name.inf") != null, "normal filenames containing two dots are not mistaken for traversal");
                T(LanSecurity.SafeAbsolute(repo, "folder/../file.inf") == null && LanSecurity.SafeAbsolute(repo, "file.inf:stream") == null, "parent traversal and alternate file streams remain blocked");
                string text = File.ReadAllText(inf); int bytes = Encoding.UTF8.GetByteCount(text);
                var batchResponse = HttpAdmin(env.Url + "/api/admin/upload/start", "POST", J(new { FileCount = 1, TotalBytes = bytes }), cookies);
                var batch = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(batchResponse.Item2); string batchId = Convert.ToString(batch["batch"]);
                string query = "?batch=" + batchId + "&path=" + Uri.EscapeDataString("drivers..2026/model..driver.inf") + "&total=" + bytes + "&stamp=job-upload";
                var bad = HttpAdmin(env.Url + "/api/admin/upload/chunk" + query + "&offset=1", "POST", text, cookies);
                T(bad.Item1 == 400 && bad.Item2.Contains("上传分片") && bad.Item2.Contains("model..driver.inf"), "upload 400 includes stage and failing filename");
                var session = new JavaScriptSerializer().Deserialize<UploadSessionInfo>(File.ReadAllText(Path.Combine(WebAdminController.UploadedBatchRoot(repo, batchId), "session.json")));
                T(session.LastError.Length > 0 && session.LastFile.Contains("model..driver.inf"), "upload failure diagnostics persist in the upload session");
                var uploaded = HttpAdmin(env.Url + "/api/admin/upload/chunk" + query + "&offset=0", "POST", text, cookies);
                T(uploaded.Item1 == 200, "valid filenames with consecutive dots upload successfully: " + uploaded.Item2);
                string parts = Path.Combine(WebAdminController.UploadedBatchRoot(repo, batchId), "parts");
                T(Directory.Exists(parts) && Directory.GetDirectories(parts).Length == 0, "upload partials use flat hashed paths rather than deep source paths");
                var uploadedJob = HttpAdmin(env.Url + "/api/admin/machine-packages/import/start", "POST", J(new { Source = Convert.ToString(batch["source"]), Mapping = new MachineDriverPackage { Manufacturer = "Test Maker", Model = "Upload Model", MaxBuild = 21999 } }), cookies);
                var uploadedId = new JavaScriptSerializer().Deserialize<MachineImportJob>(J(new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(uploadedJob.Item2)["job"])).Id;
                T(PollMachineJob(env.Url, uploadedId, cookies).State == "completed", "uploaded machine folder completes through the asynchronous task endpoint");
                T(!Directory.Exists(WebAdminController.UploadedBatchRoot(repo, batchId)), "uploaded source is reclaimed only after the background task succeeds");
                held.Entered.Dispose(); held.Release.Dispose();
            }
        }

        private static void TestAdminAccountsAndAudit(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "admin-accounts"));
            string src = Path.Combine(dir, "PkgA"); Directory.CreateDirectory(src);
            WriteInf(src, "admin.inf", "PCI\\VEN_AD01&DEV_0001", "03/14/2026", "2.0.0.0", "NTamd64.10");
            string repo = MakeRepo(Path.Combine(dir, "repo"), src);
            string credentials = Path.Combine(dir, "admin-credentials.json"), auditDir = Path.Combine(dir, "audit");
            const string adminPassword = "Initial-Admin-Password-123!", operatorPassword = "Operator-Password-456!";
            AdminCredentialStore.Create(credentials, "rootadmin", adminPassword);
            WebAdminController.TestAuditDirectory = auditDir;
            try
            {
                using (var env = StartEnv(repo, null, null, null, credentials))
                {
                    var cookies = new CookieContainer();
                    T(HttpAdmin(env.Url + "/api/admin/machine-packages", "GET", null, cookies).Item1 == 401, "machine mappings require an authenticated administrator");
                    var failed = HttpAdmin(env.Url + "/api/admin/login", "POST", J(new Dictionary<string, string> { { "user", "unknown-user" }, { "password", "invalid-password" } }), cookies);
                    T(failed.Item1 == 401, "invalid admin login rejected");
                    var login = HttpAdmin(env.Url + "/api/admin/login", "POST", J(new Dictionary<string, string> { { "user", "rootadmin" }, { "password", adminPassword } }), cookies);
                    T(login.Item1 == 200 && cookies.GetCookies(new Uri(env.Url))["UDAADMIN"] != null, "valid admin login creates authenticated session");
                    var adminPage = HttpAdmin(env.Url + "/admin", "GET", null, cookies);
                    T(adminPage.Item1 == 200 && adminPage.Item2.Contains("serviceProtoHeader={'X-UDA-Protocol':'2'}"), "served console injects current LAN protocol for repository operations");
                    T(adminPage.Item2.Contains("const protoHeader={'X-UDA-Protocol':'1'}"), "legacy management API protocol remains unchanged");
                    var users = HttpAdmin(env.Url + "/api/admin/users", "GET", null, cookies);
                    T(users.Item1 == 200 && users.Item2.Contains("rootadmin"), "existing admin account is listed");
                    var mapping = new MachineDriverPackage { Manufacturer = "Admin Test", Model = "Dedicated", SourceName = OfflineRepoGenerator.TryLoadManifest(repo).Entries[0].SourceName };
                    var modelSaved = HttpAdmin(env.Url + "/api/admin/machine-packages", "POST", J(new[] { mapping }), cookies);
                    T(modelSaved.Item1 == 200 && modelSaved.Item2.Contains("Dedicated") && modelSaved.Item2.Contains("sources"), "authenticated model editor saves mappings and lists repository packages");
                    T(HttpAdmin(env.Url + "/api/admin/machine-packages", "POST", J(new[] { mapping, mapping }), cookies).Item1 == 400, "overlapping model mappings rejected through admin API");
                    T(HttpAdmin(env.Url + "/api/admin/machine-packages", "POST", "[]", cookies).Item1 == 200, "administrator can remove a model mapping");
                    var added = HttpAdmin(env.Url + "/api/admin/users", "POST", J(new Dictionary<string, string> { { "action", "add" }, { "user", "operator" }, { "password", operatorPassword } }), cookies);
                    T(added.Item1 == 200 && added.Item2.Contains("operator"), "additional admin account can be created");
                    var removed = HttpAdmin(env.Url + "/api/admin/users", "POST", J(new Dictionary<string, string> { { "action", "remove" }, { "user", "operator" } }), cookies);
                    T(removed.Item1 == 200 && !removed.Item2.Contains("operator"), "additional admin account can be removed");
                    var audit = HttpAdmin(env.Url + "/api/admin/audit", "GET", null, cookies);
                    T(audit.Item1 == 200 && audit.Item2.Contains("登录失败") && audit.Item2.Contains("unknown-user"), "failed login records attempted user");
                    T(audit.Item2.Contains("登录成功") && audit.Item2.Contains("rootadmin"), "successful login records admin identity");
                    T(audit.Item2.Contains("创建管理员账号: operator") && audit.Item2.Contains("移除管理员账号: operator"), "audit names administrator account actions");
                    T(audit.Item2.Contains("/api/admin/users") && (audit.Item2.Contains("127.0.0.1") || audit.Item2.Contains("::1")), "admin operation audit includes endpoint and source IP");
                    T(audit.Item2.Contains("Utc") && !audit.Item2.Contains(operatorPassword) && !audit.Item2.Contains(adminPassword), "audit includes time and omits passwords");
                }
            }
            finally { WebAdminController.TestAuditDirectory = null; }
        }

        private static void TestSlowLargeDownload(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "slow-download"));
            string src = Path.Combine(dir, "PkgA"); Directory.CreateDirectory(src);
            WriteInf(src, "slow.inf", "PCI\\VEN_2A00&DEV_2A00", "03/14/2026", "2.0.0.0", "NTamd64.10");
            File.WriteAllBytes(Path.Combine(src, "payload.bin"), new byte[2 * 1024 * 1024]);
            string repo = MakeRepo(Path.Combine(dir, "repo"), src);
            using (var env = StartEnv(repo, null, null))
            {
                string package = Directory.GetDirectories(Path.Combine(repo, "packages"), "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length).First(x => Directory.GetFiles(x, "slow.inf", SearchOption.TopDirectoryOnly).Length > 0);
                env.Server.TestDownloadOverride = rel => { Thread.Sleep(3500); return PackageZip(package); };
                var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var devices = new List<D> { Device("PCI\\VEN_2A00&DEV_2A00", "", "", "SLOW1") };
                var client = NewClient(env.Url, null, null, Path.Combine(dir, "staging"), devices, runner, 3000);
                var res = client.Run(CancellationToken.None);
                T(res.Downloaded == 1 && res.InstalledOk == 1, "slow download survives control timeout, downloaded=" + res.Downloaded + " ok=" + res.InstalledOk);
            }
        }

        private static byte[] PackageZip(string directory)
        {
            using (var ms = new MemoryStream())
            {
                using (var za = new ZipArchive(ms, ZipArchiveMode.Create, true))
                    foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                    {
                        var entry = za.CreateEntry(file.Substring(directory.Length).TrimStart('\\').Replace('\\', '/'), CompressionLevel.Fastest);
                        using (var input = File.OpenRead(file)) using (var output = entry.Open()) input.CopyTo(output);
                    }
                return ms.ToArray();
            }
        }

        // ---------------------------------------------------------------------------
        //  4. Download hash tamper: a tampered ZIP must be rejected (no install).
        // ---------------------------------------------------------------------------
        private static void TestDownloadHashTamper(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "tamper"));
            string src = Path.Combine(dir, "PkgA"); Directory.CreateDirectory(src);
            WriteInf(src, "dev.inf", "PCI\\VEN_3000&DEV_3000", "03/14/2026", "2.0.0.0", "NTamd64.10");
            string repo = MakeRepo(Path.Combine(dir, "repo"), src);
            using (var env = StartEnv(repo, null, null))
            {
                env.Server.TestDownloadOverride = rel => TamperedZip(Path.GetFileName(rel));
                var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var devices = new List<D> { Device("PCI\\VEN_3000&DEV_3000", "", "", "DEV1") };
                var client = NewClient(env.Url, null, null, Path.Combine(dir, "staging"), devices, runner);
                var res = client.Run(CancellationToken.None);
                T(res.Reported, "reported");
                T(res.Downloaded == 1, "downloaded=" + res.Downloaded);
                T(res.InstalledOk == 0, "tampered not installed, ok=" + res.InstalledOk);
                T(res.InstalledSkip >= 1, "tampered skipped, skip=" + res.InstalledSkip);
                T(runner.Calls.Count == 0, "runner not called=" + runner.Calls.Count);
            }
        }

        private static byte[] TamperedZip(string infName)
        {
            using (var ms = new MemoryStream())
            {
                using (var za = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    var entry = za.CreateEntry(infName);
                    using (var es = entry.Open())
                    using (var sw = new StreamWriter(es, new UTF8Encoding(false))) sw.Write("TAMPERED CONTENT - NOT THE REAL INF");
                }
                return ms.ToArray();
            }
        }

        // ---------------------------------------------------------------------------
        //  5. Token auth: missing / wrong / correct.
        // ---------------------------------------------------------------------------
        private static void TestToken(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "token"));
            string src = Path.Combine(dir, "PkgA"); Directory.CreateDirectory(src);
            WriteInf(src, "dev.inf", "PCI\\VEN_4000&DEV_4000", "03/14/2026", "2.0.0.0", "NTamd64.10");
            string repo = MakeRepo(Path.Combine(dir, "repo"), src);
            using (var env = StartEnv(repo, "secret-token", null))
            {
                // Raw protocol: missing token -> 401, wrong -> 401, correct -> 200.
                var req = new LanReportRequest { Protocol = LanProtocol.Version, RequestId = "tok-1", OsArchitecture = "amd64", OsMajorVersion = 10,
                    Devices = new List<LanDeviceDto> { new LanDeviceDto { DeviceInstanceId = "D", HardwareIds = new List<string> { "PCI\\VEN_4000&DEV_4000" } } } };
                T(HttpPost(env.Url + "/api/report", J(req), null).Item1 == 401, "missing token -> 401");
                T(HttpPost(env.Url + "/api/report", J(req), "wrong").Item1 == 401, "wrong token -> 401");
                T(HttpPost(env.Url + "/api/report", J(req), "secret-token").Item1 == 200, "correct token -> 200");
                // Client flow: wrong token fails to report; correct token succeeds.
                var devices = new List<D> { Device("PCI\\VEN_4000&DEV_4000", "", "", "D") };
                var badClient = NewClient(env.Url, "wrong", null, Path.Combine(dir, "s1"), devices, new FakePnpInstallRunner { DefaultExitCode = 0 });
                var badRes = badClient.Run(CancellationToken.None);
                T(!badRes.Reported, "wrong-token client not reported (err=" + badRes.Error + ")");
                var goodClient = NewClient(env.Url, "secret-token", null, Path.Combine(dir, "s2"), devices, new FakePnpInstallRunner { DefaultExitCode = 0 });
                var goodRes = goodClient.Run(CancellationToken.None);
                T(goodRes.Reported && goodRes.Candidates == 1, "correct-token client reported, candidates=" + goodRes.Candidates);
            }
        }

        // ---------------------------------------------------------------------------
        //  6. Path traversal: download rejects escaping paths.
        // ---------------------------------------------------------------------------
        private static void TestPathTraversal(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "traversal"));
            string src = Path.Combine(dir, "PkgA"); Directory.CreateDirectory(src);
            WriteInf(src, "dev.inf", "PCI\\VEN_5000&DEV_5000", "03/14/2026", "2.0.0.0", "NTamd64.10");
            string repo = MakeRepo(Path.Combine(dir, "repo"), src);
            using (var env = StartEnv(repo, null, null))
            {
                // First find a valid relative path via a report.
                var req = new LanReportRequest { Protocol = LanProtocol.Version, RequestId = "tr-1", OsArchitecture = "amd64", OsMajorVersion = 10,
                    Devices = new List<LanDeviceDto> { new LanDeviceDto { DeviceInstanceId = "D", HardwareIds = new List<string> { "PCI\\VEN_5000&DEV_5000" } } } };
                var resp = new JavaScriptSerializer().Deserialize<LanReportResponse>(HttpPost(env.Url + "/api/report", J(req), null).Item2);
                string validRel = resp.Candidates[0].InfRelativePath;
                // Repo-ready can be published just before the init worker releases its write lock.
                // Retry only maintenance 503, as the real client does; preserve all other errors.
                var download = HttpGet(env.Url + "/api/download?path=" + Uri.EscapeDataString(validRel), null);
                for (int attempt = 0; download.Item1 == 503 && attempt < 20; attempt++)
                {
                    Thread.Sleep(25);
                    download = HttpGet(env.Url + "/api/download?path=" + Uri.EscapeDataString(validRel), null);
                }
                T(download.Item1 == 200, "valid download -> 200, actual=" + download.Item1);
                T(HttpGet(env.Url + "/api/download?path=" + Uri.EscapeDataString("../../../../Windows/win.ini"), null).Item1 == 400, "traversal ../ -> 400");
                T(HttpGet(env.Url + "/api/download?path=" + Uri.EscapeDataString("C:\\Windows\\win.ini"), null).Item1 == 400, "absolute path -> 400");
                T(HttpGet(env.Url + "/api/download?path=" + Uri.EscapeDataString("packages/../../../win.ini"), null).Item1 == 400, "mid traversal -> 400");
                T(HttpGet(env.Url + "/api/download?path=", null).Item1 == 400, "empty path -> 400");
            }
        }

        // ---------------------------------------------------------------------------
        //  7. Idempotent: a duplicate RequestId returns the cached response.
        // ---------------------------------------------------------------------------
        private static void TestIdempotent(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "idem"));
            string src = Path.Combine(dir, "PkgA"); Directory.CreateDirectory(src);
            WriteInf(src, "dev.inf", "PCI\\VEN_6000&DEV_6000", "03/14/2026", "2.0.0.0", "NTamd64.10");
            string repo = MakeRepo(Path.Combine(dir, "repo"), src);
            using (var env = StartEnv(repo, null, null))
            {
                var req = new LanReportRequest { Protocol = LanProtocol.Version, RequestId = "idem-1", OsArchitecture = "amd64", OsMajorVersion = 10,
                    Devices = new List<LanDeviceDto> { new LanDeviceDto { DeviceInstanceId = "D", HardwareIds = new List<string> { "PCI\\VEN_6000&DEV_6000" } } } };
                var r1 = new JavaScriptSerializer().Deserialize<LanReportResponse>(HttpPost(env.Url + "/api/report", J(req), null).Item2);
                var r2 = new JavaScriptSerializer().Deserialize<LanReportResponse>(HttpPost(env.Url + "/api/report", J(req), null).Item2);
                T(r1.Candidates.Count == 1 && r2.Candidates.Count == 1, "both responses have 1 candidate");
                T(r2.Cached, "second response cached=" + r2.Cached);
                T(r1.Candidates[0].InfRelativePath == r2.Candidates[0].InfRelativePath, "same candidate both times");
                // Different RequestId -> fresh (not cached).
                var req3 = new LanReportRequest { Protocol = LanProtocol.Version, RequestId = "idem-2", OsArchitecture = "amd64", OsMajorVersion = 10,
                    Devices = req.Devices };
                var r3 = new JavaScriptSerializer().Deserialize<LanReportResponse>(HttpPost(env.Url + "/api/report", J(req3), null).Item2);
                T(!r3.Cached, "fresh requestId not cached");
                // Client-side dedup: two devices matching the same INF -> one install.
                var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var devices = new List<D> { Device("PCI\\VEN_6000&DEV_6000", "", "", "D1"), Device("PCI\\VEN_6000&DEV_6000", "", "", "D2") };
                var client = NewClient(env.Url, null, null, Path.Combine(dir, "staging"), devices, runner);
                var res = client.Run(CancellationToken.None);
                T(res.InstalledOk == 1, "dedup: one install for two devices, ok=" + res.InstalledOk);
                T(runner.Calls.Count == 1, "dedup: one pnputil call, calls=" + runner.Calls.Count);
            }
        }

        // ---------------------------------------------------------------------------
        //  8. Cancellation: a pre-cancelled token stops the client gracefully.
        // ---------------------------------------------------------------------------
        private static void TestCancel(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "cancel"));
            var devices = new List<D> { Device("PCI\\VEN_7000&DEV_7000", "", "", "D") };
            var client = NewClient("http://localhost:" + GetFreePort().ToString(CultureInfo.InvariantCulture), null, null,
                Path.Combine(dir, "staging"), devices, new FakePnpInstallRunner { DefaultExitCode = 0 });
            var cts = new CancellationTokenSource(); cts.Cancel();
            var res = client.Run(cts.Token);
            T(res.Cancelled, "cancelled=" + res.Cancelled);
            T(!res.Reported, "not reported after cancel");
        }

        // ---------------------------------------------------------------------------
        //  9. Disconnect retry: server down -> retries with backoff -> gives up gracefully;
        //    and server starts late -> client eventually succeeds.
        // ---------------------------------------------------------------------------
        private static void TestDisconnectRetry(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "retry"));
            int port = GetFreePort();
            string url = "http://localhost:" + port.ToString(CultureInfo.InvariantCulture);

            // (a) Server never comes up -> graceful give-up.
            var devices = new List<D> { Device("PCI\\VEN_8000&DEV_8000", "", "", "D") };
            var cfgDown = new LanClientConfig { ServerUrl = url, TimeoutMs = 800, MaxRetries = 2, BackoffInitialMs = 40,
                StagingDir = Path.Combine(dir, "s1"), InstallTimeoutSeconds = 5, DedupeByContent = true, VerifySignature = true };
            var clientDown = new LanDriverClient(cfgDown, new Machine(), new StaticHardwareCollector(devices),
                new OsTarget { Architecture = "amd64", MajorVersion = 10 }, _log,
                new TrustingSignatureVerifier(), new AlwaysAdminChecker(), new FakePnpInstallRunner { DefaultExitCode = 0 }, new NoOpSystemOps());
            var resDown = clientDown.Run(CancellationToken.None);
            T(!resDown.Reported, "down: not reported");
            T(resDown.Error.IndexOf("unreachable", StringComparison.OrdinalIgnoreCase) >= 0, "down: error=" + resDown.Error);
            T(!resDown.Cancelled, "down: not cancelled, just gave up");

            // (b) Server starts after a delay -> client retries then succeeds.
            string src = Path.Combine(dir, "PkgA"); Directory.CreateDirectory(src);
            WriteInf(src, "dev.inf", "PCI\\VEN_8000&DEV_8000", "03/14/2026", "2.0.0.0", "NTamd64.10");
            string repo = MakeRepo(Path.Combine(dir, "repo"), src);
            LanDriverServer lateServer = null;
            var starter = new Thread(() =>
            {
                Thread.Sleep(350);
                lateServer = new LanDriverServer(new LanServerConfig { Port = port, RepoRoot = repo, OsArchitecture = "amd64", OsMajorVersion = 10, Host = "localhost" }, _log);
                try { lateServer.Start(); } catch { }
            }) { IsBackground = true };
            starter.Start();
            var cfgUp = new LanClientConfig { ServerUrl = url, TimeoutMs = 1000, MaxRetries = 10, BackoffInitialMs = 100,
                StagingDir = Path.Combine(dir, "s2"), InstallTimeoutSeconds = 5, DedupeByContent = true, VerifySignature = true };
            var clientUp = new LanDriverClient(cfgUp, new Machine { Maker = "TestVendor" }, new StaticHardwareCollector(devices),
                new OsTarget { Architecture = "amd64", MajorVersion = 10 }, _log,
                new TrustingSignatureVerifier(), new AlwaysAdminChecker(), new FakePnpInstallRunner { DefaultExitCode = 0 }, new NoOpSystemOps());
            var resUp = clientUp.Run(CancellationToken.None);
            try { if (lateServer != null) { lateServer.Stop(); lateServer.Dispose(); } } catch { }
            starter.Join(2000);
            T(resUp.Reported, "late: reported (err=" + resUp.Error + ")");
            T(resUp.InstalledOk == 1, "late: installed ok=" + resUp.InstalledOk);
        }

        // ---------------------------------------------------------------------------
        //  10. NIC bootstrap: present repo installs the NIC driver offline; absent/lacking
        //      driver degrades gracefully.
        // ---------------------------------------------------------------------------
        private static void TestNicBootstrap(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "nic"));
            // Bootstrap repo with a NIC driver.
            string bootSrc = Path.Combine(dir, "BootSrc"); Directory.CreateDirectory(bootSrc);
            WriteInf(bootSrc, "nic.inf", "PCI\\VEN_9000&DEV_9000", "01/01/2026", "1.0.0.0", "NTamd64.10");
            string bootRepo = MakeRepo(Path.Combine(dir, "bootRepo"), bootSrc);
            // Main server repo (different device) so the post-bootstrap report also works.
            string src = Path.Combine(dir, "PkgA"); Directory.CreateDirectory(src);
            WriteInf(src, "audio.inf", "PCI\\VEN_9100&DEV_9100", "03/14/2026", "2.0.0.0", "NTamd64.10");
            string repo = MakeRepo(Path.Combine(dir, "repo"), src);

            using (var env = StartEnv(repo, null, null))
            {
                // (a) Bootstrap present + NIC lacking -> bootstrap installs NIC offline, then reports audio.
                var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var devices = new List<D> {
                    new D { Name = "NIC", Id = "NIC1", Class = "Net", ClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}", Inf = "", Version = "",
                        H = new List<string> { "PCI\\VEN_9000&DEV_9000" }, K = new List<string>() },
                    Device("PCI\\VEN_9100&DEV_9100", "", "", "AUD1")
                };
                var client = NewClient(env.Url, null, bootRepo, Path.Combine(dir, "s1"), devices, runner);
                var res = client.Run(CancellationToken.None);
                T(res.BootstrapAttempted, "bootstrap attempted");
                T(res.BootstrapOk == 1, "bootstrap installed nic, ok=" + res.BootstrapOk);
                T(res.Reported, "reported after bootstrap (err=" + res.Error + ")");
                T(res.InstalledOk == 1, "audio installed ok=" + res.InstalledOk);
                T(runner.Calls.Count == 2, "runner calls (nic+audio)=" + runner.Calls.Count);
            }

            // (b) Bootstrap lacks the NIC driver -> graceful, no crash, proceeds to network.
            string bootSrcEmpty = Path.Combine(dir, "BootEmpty"); Directory.CreateDirectory(bootSrcEmpty);
            WriteInf(bootSrcEmpty, "other.inf", "PCI\\VEN_9999&DEV_9999", "01/01/2026", "1.0.0.0", "NTamd64.10");
            string bootRepoEmpty = MakeRepo(Path.Combine(dir, "bootRepoEmpty"), bootSrcEmpty);
            using (var env = StartEnv(repo, null, null))
            {
                var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var devices = new List<D> {
                    new D { Name = "NIC", Id = "NIC1", Class = "Net", ClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}", Inf = "", Version = "",
                        H = new List<string> { "PCI\\VEN_9000&DEV_9000" }, K = new List<string>() }
                };
                var client = NewClient(env.Url, null, bootRepoEmpty, Path.Combine(dir, "s2"), devices, runner);
                var res = client.Run(CancellationToken.None);
                T(res.BootstrapAttempted, "bootstrap attempted (empty)");
                T(res.BootstrapOk == 0, "bootstrap no match, ok=" + res.BootstrapOk);
                T(res.Reported, "still reported (err=" + res.Error + ")");
            }

            // (c) No bootstrap repo at all -> graceful handling, no crash.
            using (var env = StartEnv(repo, null, null))
            {
                var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var devices = new List<D> {
                    new D { Name = "NIC", Id = "NIC1", Class = "Net", ClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}", Inf = "", Version = "",
                        H = new List<string> { "PCI\\VEN_9000&DEV_9000" }, K = new List<string>() }
                };
                var client = NewClient(env.Url, null, "", Path.Combine(dir, "s3"), devices, runner);
                var res = client.Run(CancellationToken.None);
                T(res.BootstrapAttempted, "bootstrap attempted (none)");
                T(res.BootstrapOk == 0, "no bootstrap, ok=" + res.BootstrapOk);
                T(res.Reported, "still reported without bootstrap (err=" + res.Error + ")");
            }
        }

        // ---------------------------------------------------------------------------
        //  11. No arbitrary paths: import only accepts allowlisted server paths; traversal rejected.
        // ---------------------------------------------------------------------------
        private static void TestNoArbitraryPath(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "noarbitrary"));
            string src = Path.Combine(dir, "PkgA"); Directory.CreateDirectory(src);
            WriteInf(src, "dev.inf", "PCI\\VEN_A000&DEV_A000", "03/14/2026", "2.0.0.0", "NTamd64.10");
            string repo = MakeRepo(Path.Combine(dir, "repo"), src);
            string allowRoot = Path.Combine(dir, "allow"); Directory.CreateDirectory(allowRoot);
            string allowSrc = Path.Combine(allowRoot, "NewPkg"); Directory.CreateDirectory(allowSrc);
            WriteInf(allowSrc, "new.inf", "PCI\\VEN_A001&DEV_A001", "03/14/2026", "3.0.0.0", "NTamd64.10");
            string outsideRoot = Path.Combine(dir, "outside"); Directory.CreateDirectory(outsideRoot);

            using (var env = StartEnv(repo, "imp-token", new List<string> { allowRoot }))
            {
                // No token -> 401.
                T(HttpPost(env.Url + "/api/admin/import?src=" + Uri.EscapeDataString(allowSrc), "", null).Item1 == 401, "import no token -> 401");
                // Non-allowlisted path -> 403.
                T(HttpPost(env.Url + "/api/admin/import?src=" + Uri.EscapeDataString(outsideRoot), "", "imp-token").Item1 == 403, "import non-allowlisted -> 403");
                // Traversal -> 400.
                T(HttpPost(env.Url + "/api/admin/import?src=" + Uri.EscapeDataString(allowRoot + "/../../outside"), "", "imp-token").Item1 == 400, "import traversal -> 400");
                // Allowlisted path -> 200 and repo reloaded with the new INF.
                var r = HttpPost(env.Url + "/api/admin/import?src=" + Uri.EscapeDataString(allowSrc), "", "imp-token");
                T(r.Item1 == 200, "import allowlisted -> 200, got " + r.Item1 + ": " + r.Item2);
                // After import, a report for the new device matches (token required by this server).
                var req = new LanReportRequest { Protocol = LanProtocol.Version, RequestId = "imp-1", OsArchitecture = "amd64", OsMajorVersion = 10,
                    Devices = new List<LanDeviceDto> { new LanDeviceDto { DeviceInstanceId = "D", HardwareIds = new List<string> { "PCI\\VEN_A001&DEV_A001" } } } };
                var resp = new JavaScriptSerializer().Deserialize<LanReportResponse>(HttpPost(env.Url + "/api/report", J(req), "imp-token").Item2);
                T(resp != null && resp.Candidates.Count == 1, "post-import match candidates=" + (resp != null ? resp.Candidates.Count : -1));
                T(resp.Candidates[0].DriverVerVersion == "3.0.0.0", "post-import version=" + resp.Candidates[0].DriverVerVersion);
            }
        }

        // ---------------------------------------------------------------------------
        //  12. Run-scoped cache hygiene: each InstallCandidates / Run download pass uses a
        //      unique per-run subdirectory under the cache root, that subdirectory is removed
        //      in finally on success/failure/cancel, other instances' directories and anything
        //      outside the root are never touched, and startup hygiene removes only >24h-old
        //      leftover directories. Helpers live on LanDriverClient (internal use, same file).
        // ---------------------------------------------------------------------------

        private static List<LanCandidateDto> ReportCandidates(LanDriverClient client, string hwid, List<D> devices)
        {
            var resp = client.Report(devices, new Machine { Maker = "TestVendor", Model = "ModelA", Sku = "SKU1" }, CancellationToken.None, null);
            return resp.Candidates;
        }

        private static string[] RunDirsLeft(string cacheRoot)
        {
            return Directory.GetDirectories(cacheRoot, "run-*", SearchOption.TopDirectoryOnly);
        }

        // Normal completion: install succeeds, the per-run dir is gone, root and other instance dirs stay.
        private static void TestCacheCleanupNormal(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "cache-normal"));
            string cacheRoot = PrepareDir(Path.Combine(dir, "cache"));
            string sibling = Path.Combine(cacheRoot, "other-instance"); Directory.CreateDirectory(sibling);
            File.WriteAllText(Path.Combine(sibling, "keep.txt"), "keep");
            string src = Path.Combine(dir, "PkgA"); Directory.CreateDirectory(src);
            WriteInf(src, "dev.inf", "PCI\\VEN_C000&DEV_C000", "03/14/2026", "2.0.0.0", "NTamd64.10");
            string repo = MakeRepo(Path.Combine(dir, "repo"), src);
            using (var env = StartEnv(repo, null, null))
            {
                var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var devices = new List<D> { Device("PCI\\VEN_C000&DEV_C000", "", "", "DEV1") };
                var client = NewClient(env.Url, null, null, cacheRoot, devices, runner);
                var cands = ReportCandidates(client, "PCI\\VEN_C000&DEV_C000", devices);
                T(cands.Count == 1, "candidates=" + cands.Count);
                var res = client.InstallCandidates(cands, devices, CancellationToken.None, null);
                T(res.InstalledOk == 1 && runner.Calls.Count == 1, "installed ok=" + res.InstalledOk + " calls=" + runner.Calls.Count + " err=" + res.Error);
                T(Directory.Exists(cacheRoot), "cache root kept");
                T(RunDirsLeft(cacheRoot).Length == 0, "no run-* dirs left after success");
                T(Directory.Exists(sibling) && File.Exists(Path.Combine(sibling, "keep.txt")), "other instance dir untouched");
            }
        }

        // Download failure: the per-run dir is still removed in finally.
        private static void TestCacheCleanupFailure(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "cache-fail"));
            string cacheRoot = PrepareDir(Path.Combine(dir, "cache"));
            var devices = new List<D> { Device("PCI\\VEN_C100&DEV_C100", "", "", "DEV1") };
            var client = NewClient("http://localhost:" + GetFreePort().ToString(CultureInfo.InvariantCulture), null, null,
                cacheRoot, devices, new FakePnpInstallRunner { DefaultExitCode = 0 });
            var cands = new List<LanCandidateDto> { new LanCandidateDto { DeviceInstanceId = "DEV1", InfRelativePath = "packages/x/dev.inf",
                ContentHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", DriverVerVersion = "1.0.0.0" } };
            var res = client.InstallCandidates(cands, devices, CancellationToken.None, null);
            T(res.Downloaded == 0 && res.InstalledFail >= 1, "download failed as expected, fail=" + res.InstalledFail);
            T(RunDirsLeft(cacheRoot).Length == 0, "no run-* dirs left after download failure");
        }

        // Exception path: a null candidate list entry must not leave a run dir behind.
        private static void TestCacheCleanupCancel(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "cache-cancel"));
            string cacheRoot = PrepareDir(Path.Combine(dir, "cache"));
            var devices = new List<D> { Device("PCI\\VEN_C200&DEV_C200", "", "", "DEV1") };
            var client = NewClient("http://localhost:" + GetFreePort().ToString(CultureInfo.InvariantCulture), null, null,
                cacheRoot, devices, new FakePnpInstallRunner { DefaultExitCode = 0 });
            var cands = new List<LanCandidateDto> { new LanCandidateDto { DeviceInstanceId = "DEV1", InfRelativePath = "p.inf", ContentHash = "ff" } };
            var cts = new CancellationTokenSource(); cts.Cancel();
            var res = client.InstallCandidates(cands, devices, cts.Token, null);
            T(res.Cancelled, "cancelled=" + res.Cancelled);
            T(RunDirsLeft(cacheRoot).Length == 0, "no run-* dirs left after cancel");
        }

        // Concurrent-instance safety: a run dir created before the install (another instance) survives.
        private static void TestCacheOtherInstanceSafe(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "cache-other"));
            string cacheRoot = PrepareDir(Path.Combine(dir, "cache"));
            var devices = new List<D> { Device("PCI\\VEN_C300&DEV_C300", "", "", "DEV1") };
            var client = NewClient("http://localhost:" + GetFreePort().ToString(CultureInfo.InvariantCulture), null, null,
                cacheRoot, devices, new FakePnpInstallRunner { DefaultExitCode = 0 });
            string other = LanDriverClient.CreateRunCacheDir(cacheRoot, null);
            T(!string.IsNullOrEmpty(other) && Directory.Exists(other), "other instance run dir created");
            var cands = new List<LanCandidateDto> { new LanCandidateDto { DeviceInstanceId = "DEV1", InfRelativePath = "p.inf", ContentHash = "ee" } };
            client.InstallCandidates(cands, devices, CancellationToken.None, null);
            T(Directory.Exists(other), "other instance run dir survives this run's cleanup");
            T(RunDirsLeft(cacheRoot).Length == 1, "only the other instance's run dir remains");
        }

        // Path-escape guard: deleting outside the root, or the root itself, is refused.
        private static void TestCacheNoOutsideDelete(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "cache-outside"));
            string cacheRoot = PrepareDir(Path.Combine(dir, "cache"));
            string outside = PrepareDir(Path.Combine(dir, "outside"));
            File.WriteAllText(Path.Combine(outside, "user.txt"), "user data");
            File.WriteAllText(Path.Combine(cacheRoot, "rootfile.txt"), "root data");
            Action<string> noop = null;
            // .. traversal out of the root.
            LanDriverClient.SafeDeleteRunCacheDir(Path.Combine(cacheRoot, "..", "outside"), cacheRoot, noop);
            T(Directory.Exists(outside) && File.Exists(Path.Combine(outside, "user.txt")), "outside dir not deleted via ..");
            // Root passed as the run dir.
            LanDriverClient.SafeDeleteRunCacheDir(cacheRoot, cacheRoot, noop);
            T(Directory.Exists(cacheRoot) && File.Exists(Path.Combine(cacheRoot, "rootfile.txt")), "cache root not deleted");
            // Empty root refuses everything.
            LanDriverClient.SafeDeleteRunCacheDir(cacheRoot, "", noop);
            T(Directory.Exists(cacheRoot), "empty root refuses deletion");
            // Grandchild paths are also refused (only direct children of the root are ever run dirs).
            string grandchild = Path.Combine(cacheRoot, "a", "b"); Directory.CreateDirectory(grandchild);
            File.WriteAllText(Path.Combine(grandchild, "x.txt"), "x");
            LanDriverClient.SafeDeleteRunCacheDir(grandchild, cacheRoot, noop);
            T(Directory.Exists(grandchild) && File.Exists(Path.Combine(grandchild, "x.txt")), "grandchild path refused for deletion");
        }

        // Startup hygiene: only >24h-old leftover directories are removed; fresh ones and the root stay.
        private static void TestCacheStaleCleanup(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "cache-stale"));
            string cacheRoot = PrepareDir(Path.Combine(dir, "cache"));
            string stale = Path.Combine(cacheRoot, "run-stale"); Directory.CreateDirectory(stale);
            Directory.CreateDirectory(Path.Combine(stale, "nested"));
            File.WriteAllText(Path.Combine(stale, "nested", "old.bin"), "old");
            string fresh = Path.Combine(cacheRoot, "run-fresh"); Directory.CreateDirectory(fresh);
            File.WriteAllText(Path.Combine(fresh, "new.bin"), "new");
            string other = Path.Combine(cacheRoot, "other-instance"); Directory.CreateDirectory(other);
            DateTime now = DateTime.UtcNow;
            try { Directory.SetLastWriteTimeUtc(stale, now - TimeSpan.FromHours(25)); } catch { }
            LanDriverClient.CleanStaleClientCacheDirs(cacheRoot, now, null);
            T(!Directory.Exists(stale), "stale (>24h) run dir removed including contents");
            T(Directory.Exists(fresh), "fresh run dir kept");
            T(Directory.Exists(other), "non run-named dir kept");
            T(Directory.Exists(cacheRoot), "cache root kept");
            // Two runs get distinct, existing per-run directories.
            string r1 = LanDriverClient.CreateRunCacheDir(cacheRoot, null);
            string r2 = LanDriverClient.CreateRunCacheDir(cacheRoot, null);
            T(!string.IsNullOrEmpty(r1) && !string.IsNullOrEmpty(r2) && !string.Equals(r1, r2, StringComparison.OrdinalIgnoreCase), "run dirs unique");
            T(Directory.Exists(r1) && Directory.Exists(r2), "run dirs exist");
            // Startup hygiene with a fresh empty root is a no-op that keeps the root.
            LanDriverClient.CleanStaleClientCacheDirs(r1, now, null);
            T(Directory.Exists(r1), "stale cleanup keeps fresh root");
        }

        // ---------------------------------------------------------------------------
        //  13. Scan classification: Bad / missing CurrentInf / empty current version devices
        //      are INSTALL candidates whenever the repo matches; only a genuinely installed
        //      older driver is an UPDATE; an up-to-date driver is skipped.
        // ---------------------------------------------------------------------------
        private static D Dev(string hwid, string id, string inf, string version, bool bad)
        {
            return new D { Name = "Device " + id, Id = id, Class = "Net", ClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}",
                Inf = inf ?? "", Version = version ?? "", Error = bad ? 1 : 0, H = new List<string> { hwid }, K = new List<string>() };
        }

        private static void TestScanMissingBadEmptyVersion(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "scan-class"));
            string src = Path.Combine(dir, "PkgA"); Directory.CreateDirectory(src);
            WriteInf(src, "dev.inf", "PCI\\VEN_B000&DEV_B000", "03/14/2026", "2.0.0.0", "NTamd64.10");
            string repo = MakeRepo(Path.Combine(dir, "repo"), src);
            using (var env = StartEnv(repo, null, null))
            {
                // (a) Missing CurrentInf -> installs.
                var runnerA = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var a = NewClient(env.Url, null, null, Path.Combine(dir, "s-a"), new List<D> { Dev("PCI\\VEN_B000&DEV_B000", "DA", "", "", false) }, runnerA);
                var ra = a.Run(CancellationToken.None);
                T(ra.InstalledOk == 1, "missing CurrentInf installs, ok=" + ra.InstalledOk + " err=" + ra.Error);
                // (b) Bad device -> installs.
                var runnerB = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var b = NewClient(env.Url, null, null, Path.Combine(dir, "s-b"), new List<D> { Dev("PCI\\VEN_B000&DEV_B000", "DB", "oem9.inf", "2.0.0.0", true) }, runnerB);
                var rb = b.Run(CancellationToken.None);
                T(rb.InstalledOk == 1, "bad device installs, ok=" + rb.InstalledOk + " err=" + rb.Error);
                // (c) Empty current version (no valid installed driver) -> installs.
                var runnerC = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var c = NewClient(env.Url, null, null, Path.Combine(dir, "s-c"), new List<D> { Dev("PCI\\VEN_B000&DEV_B000", "DC", "oem9.inf", "", false) }, runnerC);
                var rc = c.Run(CancellationToken.None);
                T(rc.InstalledOk == 1, "empty version installs, ok=" + rc.InstalledOk + " err=" + rc.Error);
                // (d) Genuinely installed older version -> update installs.
                var runnerD = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var d = NewClient(env.Url, null, null, Path.Combine(dir, "s-d"), new List<D> { Dev("PCI\\VEN_B000&DEV_B000", "DD", "oem9.inf", "1.0.0.0", false) }, runnerD);
                var rd = d.Run(CancellationToken.None);
                T(rd.InstalledOk == 1, "older installed version updates, ok=" + rd.InstalledOk + " err=" + rd.Error);
                // (e) Up-to-date driver -> skipped, no install call.
                var runnerE = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var e = NewClient(env.Url, null, null, Path.Combine(dir, "s-e"), new List<D> { Dev("PCI\\VEN_B000&DEV_B000", "DE", "oem9.inf", "2.0.0.0", false) }, runnerE);
                var re = e.Run(CancellationToken.None);
                T(re.InstalledOk == 0 && runnerE.Calls.Count == 0, "up-to-date skipped, ok=" + re.InstalledOk + " calls=" + runnerE.Calls.Count);
                T(re.InstalledSkip >= 1, "up-to-date counted as skip=" + re.InstalledSkip);
                // Policy helper agrees with each case.
                T(LanDriverClient.NeedsInstall(null, "2.0.0.0"), "null device needs install");
                T(LanDriverClient.NeedsInstall(Dev("X", "I", "", "", false), "2.0.0.0"), "no inf needs install");
                T(LanDriverClient.NeedsInstall(Dev("X", "I", "oem.inf", "", false), "1.0.0.0"), "empty version needs install");
                T(LanDriverClient.NeedsInstall(Dev("X", "I", "oem.inf", "1.0.0.0", true), "1.0.0.0"), "bad device needs install even if same version");
                T(LanDriverClient.NeedsInstall(Dev("X", "I", "oem.inf", "1.0.0.0", false), "2.0.0.0"), "older version is update");
                T(!LanDriverClient.NeedsInstall(Dev("X", "I", "oem.inf", "2.0.0.0", false), "2.0.0.0"), "same version not installed");
                T(!LanDriverClient.NeedsInstall(Dev("X", "I", "oem.inf", "3.0.0.0", false), "2.0.0.0"), "newer installed not downgraded");
            }
        }

        private static void TestPnputilLatestSuppression(string root)
        {
            var a = Dev("PCI\\VEN_B200&DEV_B200", "INSTANCE-A", "oem1.inf", "1.0.0.0", false);
            var b = Dev("PCI\\VEN_B200&DEV_B201", "INSTANCE-B", "oem2.inf", "1.0.0.0", false);
            var c = Dev("PCI\\VEN_B200&DEV_B202", "INSTANCE-A2", "oem3.inf", "1.0.0.0", false);
            var ca = new LanCandidateDto { DeviceInstanceId = a.Id, InfRelativePath = "Pkg\\shared.inf", ContentHash = "ABC123", DriverVerVersion = "2.0.0.0" };
            var cb = new LanCandidateDto { DeviceInstanceId = b.Id, InfRelativePath = "Pkg\\shared.inf", ContentHash = "ABC123", DriverVerVersion = "2.0.0.0" };
            var cc = new LanCandidateDto { DeviceInstanceId = c.Id, InfRelativePath = "Pkg\\shared.inf", ContentHash = "ABC123", DriverVerVersion = "2.0.0.0" };
            var candidates = new List<LanCandidateDto> { ca, cb, cc };
            var devices = new Dictionary<string, D>(StringComparer.OrdinalIgnoreCase) { { a.Id, a }, { b.Id, b }, { c.Id, c } };
            var target = new OsTarget { Architecture = "amd64", MajorVersion = 10, BuildNumber = 19045 };
            var item = new InstallItemResult
            {
                InfPath = "C:\\stage\\shared.inf", Ran = true, ExitCode = 259, Outcome = InstallOutcome.Skipped,
                Output = "设备上的驱动程序包是最新的: INSTANCE-A\r\n设备上的驱动程序包是最新的: INSTANCE-B"
            };
            int recorded = LanPnpRejectionStore.RecordPnputilLatest(candidates, "ABC123", item, devices, target, null);
            T(recorded == 2, "explicit pnputil latest lines record both shared-package devices");
            T(LanPnpRejectionStore.Contains(ca, a, target) && LanPnpRejectionStore.Contains(cb, b, target), "unchanged candidates suppressed on next scan");
            T(!LanPnpRejectionStore.Contains(cc, c, target), "device ID prefix is not mistaken for an exact pnputil latest line");
            T(!LanPnpRejectionStore.Contains(ca, Dev(a.H[0], a.Id, a.Inf, "3.0.0.0", false), target), "new current driver version invalidates suppression");
            T(!LanPnpRejectionStore.Contains(new LanCandidateDto { DeviceInstanceId = ca.DeviceInstanceId, ContentHash = "NEW-HASH" }, a, target), "new candidate package invalidates suppression");
            T(!LanPnpRejectionStore.Contains(ca, a, new OsTarget { Architecture = "amd64", MajorVersion = 10, BuildNumber = 26100 }), "OS change invalidates suppression");
            var rankRejected = new LanCandidateDto { DeviceInstanceId = a.Id, InfRelativePath = "Pkg\\rank-rejected.inf", ContentHash = "RANK-REJECTED" };
            T(LanPnpRejectionStore.Record(rankRejected, a, target), "a pre-install full Driver Store rejection is persisted");
            T(LanPnpRejectionStore.Contains(rankRejected, a, target), "a rank-rejected candidate is suppressed on subsequent scans");
            T(!LanPnpRejectionStore.Record(rankRejected, a, target), "a repeated rank rejection does not duplicate the persisted entry");
            var unrelated = new InstallItemResult { Ran = true, ExitCode = 259, Outcome = InstallOutcome.Skipped, Output = "No applicable devices" };
            T(LanPnpRejectionStore.RecordPnputilLatest(candidates, "ABC123", unrelated, devices, target, null) == 0, "generic 259 without authoritative latest text is not persisted");
        }

        private static void TestDriverStoreFullRanking()
        {
            DateTime oldDate = new DateTime(2020, 1, 1), newDate = new DateTime(2024, 1, 1);
            var candidate = Tuple.Create(0u, newDate, "2.0.0.0", "candidate-hash");
            T(WindowsDriverPreference.IsUniqueBestCandidate(new[] { candidate, Tuple.Create(1u, newDate, "9.0.0.0", "other-hash") }, "candidate-hash"), "candidate wins when no compatible package ranks ahead");
            T(!WindowsDriverPreference.IsUniqueBestCandidate(new[] { candidate, Tuple.Create(0u, newDate.AddDays(1), "9.0.0.0", "other-hash") }, "candidate-hash"), "another equal-rank newer-date package blocks candidate");
            T(!WindowsDriverPreference.IsUniqueBestCandidate(new[] { candidate, Tuple.Create(0u, newDate, "3.0.0.0", "other-hash") }, "candidate-hash"), "another equal-rank/date newer-version package blocks candidate");
            T(!WindowsDriverPreference.IsUniqueBestCandidate(new[] { candidate, Tuple.Create(0u, newDate, "2.0.0.0", "other-hash") }, "candidate-hash"), "ambiguous equal-ranked packages are not offered as deterministic updates");
            T(!WindowsDriverPreference.IsUniqueBestCandidate(new[] { candidate, Tuple.Create(0u, oldDate, "1.0.0.0", "other-hash") }, "missing-hash"), "candidate absent from full driver list is rejected");
        }

        private static void TestModelArchiveCompactPaths(string root)
        {
            string directory = Path.Combine(PrepareDir(Path.Combine(root, "long-model-path")), "p");
            string prefix = "packages/Machine-959f6d60d5a5447ea799a107e1cb3320/d09619ee17bedb3a072c3036/rtkhda_cc_gafx_103c904c.inf_amd64_b60b16d115abd076/";
            string payload = "1_0_0_0_20260327_RicottaSI_14inch_Poly_VoiceEQ_Limiter_MergedPresets.bin";
            var extract = typeof(LanDriverClient).GetMethod("ExtractZipStream", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            var locate = typeof(LanDriverClient).GetMethod("LocateInf", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            using (var bytes = new MemoryStream())
            {
                using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
                {
                    zip.CreateEntry(prefix); // Explicit directory entries must not become files.
                    using (var writer = new StreamWriter(zip.CreateEntry(prefix + "driver.inf").Open())) writer.Write("[Version]\nSignature=\"$Windows NT$\"\n");
                    using (var writer = new StreamWriter(zip.CreateEntry(prefix + "data/" + payload).Open())) writer.Write("sample driver dependency");
                }
                T(Path.Combine(directory, (prefix + "data/" + payload).Replace('/', '\\')).Length >= 260, "fixture reproduces repository-prefixed MAX_PATH overflow");
                bytes.Position = 0; extract.Invoke(null, new object[] { bytes, directory, CancellationToken.None });
                T(File.ReadAllText(Path.Combine(directory, "data", payload)) == "sample driver dependency", "long-named dependency extracts after common-ancestor removal");
                string inf = (string)locate.Invoke(null, new object[] { directory, prefix + "driver.inf", prefix + "driver.inf" });
                T(inf == Path.Combine(directory, "driver.inf"), "original server INF path maps to compact client path");
                T(File.Exists(Path.Combine(Path.GetDirectoryName(inf), "data", payload)), "relative INF dependency layout is unchanged");
                T(!Directory.Exists(Path.Combine(directory, "packages")), "server-only scaffolding does not occupy client paths");
            }
            using (var bytes = new MemoryStream())
            {
                using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true)) using (var writer = new StreamWriter(zip.CreateEntry("../escape.inf").Open())) writer.Write("bad");
                bytes.Position = 0; bool rejected = false;
                try { extract.Invoke(null, new object[] { bytes, directory, CancellationToken.None }); }
                catch (System.Reflection.TargetInvocationException e) { rejected = e.InnerException is InvalidDataException; }
                T(rejected && File.Exists(Path.Combine(directory, "driver.inf")), "unsafe archive fails before replacing existing extracted data");
            }
        }

        private static void TestModelPackageSharedDownload(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "model-shared"));
            string source = Path.Combine(dir, "DedicatedModel"); Directory.CreateDirectory(source);
            string modelFolder = Path.Combine(source, "M437"), otherFolder = Path.Combine(source, "M438"); Directory.CreateDirectory(modelFolder); Directory.CreateDirectory(otherFolder);
            WriteInf(modelFolder, "model-a.inf", "PCI\\VEN_B230&DEV_0001", "03/14/2026", "2.0.0.0", "NTamd64.10");
            WriteInf(modelFolder, "model-b.inf", "PCI\\VEN_B230&DEV_0002", "03/14/2026", "2.0.0.0", "NTamd64.10");
            WriteInf(modelFolder, "extension-only.inf", "PCI\\VEN_B230&DEV_9999", "03/14/2026", "2.0.0.0", "NTamd64.10");
            WriteInf(otherFolder, "other-model.inf", "PCI\\VEN_B230&DEV_0001", "03/14/2026", "99.0.0.0", "NTamd64.10");
            string rootRepo = MakeRepo(Path.Combine(dir, "repo"), source);
            using (var env = StartEnv(rootRepo, null, null))
            {
                string sourceName = OfflineRepoGenerator.TryLoadManifest(rootRepo).Entries[0].SourceName;
                string modelPrefix = Path.GetDirectoryName(OfflineRepoGenerator.TryLoadManifest(rootRepo).Entries.First(e => e.RelativePath.Contains("/M437/")).RelativePath).Replace('\\', '/');
                MachineDriverPackages.Save(rootRepo, new List<MachineDriverPackage> { new MachineDriverPackage { Manufacturer = "TestVendor", Model = "ModelA", SourceName = sourceName, PackagePrefix = modelPrefix } });
                string stableArchive = MachineDriverPackages.Archive(rootRepo, sourceName, modelPrefix);
                string legacyIdentity;
                using (var sha = System.Security.Cryptography.SHA256.Create()) legacyIdentity = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sourceName + "|" + modelPrefix + "|" + OfflineRepoGenerator.TryLoadManifest(rootRepo).IntegrityHash))).Replace("-", "").ToLowerInvariant();
                string legacyArchive = Path.Combine(rootRepo, ".uda-downloads", "model-" + legacyIdentity + ".zip");
                File.Move(stableArchive, legacyArchive);
                long legacyTicks = File.GetLastWriteTimeUtc(legacyArchive).Ticks;
                T(MachineDriverPackages.Archive(rootRepo, sourceName, modelPrefix) == legacyArchive, "legacy model ZIP is adopted without recompression");
                string unrelated = Path.Combine(dir, "UnrelatedAppend"); Directory.CreateDirectory(unrelated);
                WriteInf(unrelated, "unrelated.inf", "PCI\\VEN_B230&DEV_8877", "03/14/2026", "1.0.0.0", "NTamd64.10");
                new OfflineRepoGenerator(new TrustingSignatureVerifier(), _log).AppendOrganizedSource(rootRepo, unrelated, CancellationToken.None, null);
                T(MachineDriverPackages.Archive(rootRepo, sourceName, modelPrefix) == legacyArchive && File.GetLastWriteTimeUtc(legacyArchive).Ticks == legacyTicks, "unrelated append preserves adopted legacy model identity and bytes");
                env.Server.LoadRepo();
                var devices = new List<D> { Dev("PCI\\VEN_B230&DEV_0001", "MODEL-1", "oem-model-a.inf", "1.0.0.0", false), Dev("PCI\\VEN_B230&DEV_0002", "MODEL-2", "oem-model-b.inf", "1.0.0.0", false) };
                var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var client = NewClient(env.Url, null, null, Path.Combine(dir, "staging"), devices, runner);
                int downloads = 0, probes = 0;
                Action<string> count = line => { if (line.Contains("Download starting:") && line.Contains("model-")) Interlocked.Increment(ref downloads); };
                WindowsDriverPreference.TestPreferenceProbe = (device, inf) => {
                    var actual = InfParser.Parse(inf, "model-test");
                    T(actual.HardwareIds.Contains(device.H[0]), "each model-package device ranks its own INF");
                    Interlocked.Increment(ref probes); return true;
                };
                _log.E += count;
                try
                {
                    var report = client.Report(devices, new Machine { Maker = "TestVendor", Model = "ModelA" }, CancellationToken.None, null);
                    T(report.Candidates.Count == 2 && report.Candidates.All(c => c.NeedInstall), "dedicated model offers two independently applicable INFs");
                    T(report.Candidates.All(c => c.DriverVerVersion == "2.0.0.0" && c.InfRelativePath.Contains("/M437/")), "model folder scope excludes higher-version drivers for another model within the same imported source");
                    T(report.Candidates.Select(c => c.PackageId).Distinct().Count() == 1 && report.Candidates[0].PackageId.StartsWith("model-"), "model candidates share a whole-package identity");
                    T(downloads == 1 && probes == 2, "scan downloads/extracts the whole model package once and ranks both devices");
                    var result = client.InstallCandidates(report.Candidates, devices, CancellationToken.None, null);
                    T(result.Downloaded == 1 && runner.Calls.Count == 1 && runner.PackageCalls.Count == 1, "interactive installation downloads/extracts once and imports the entire model in one invocation");
                    T(runner.PackageInfFiles.Contains("extension-only.inf") && runner.PackageInfFiles.Count == 3, "whole-package import includes INF not independently recommended for a detected device");
                    T(result.InstalledOk == 2 && result.InstalledFail == 0, "whole-package import preserves per-device result accounting");
                    runner.Calls.Clear(); runner.PackageCalls.Clear(); runner.PackageInfFiles.Clear();
                    var oneShot = client.Run(CancellationToken.None);
                    T(oneShot.Downloaded == 1 && runner.PackageCalls.Count == 1 && runner.Calls.Count == 1, "one-shot deployment also imports the complete model package only once");
                    runner.Calls.Clear(); runner.PackageCalls.Clear();
                    runner.DefaultExitCode = 5;
                    var failed = client.InstallCandidates(report.Candidates, devices, CancellationToken.None, null);
                    T(failed.InstalledFail == 2 && failed.InstalledOk == 0 && runner.PackageCalls.Count == 1, "failed batch is not retried independently for each device");
                    runner.DefaultExitCode = 0; runner.Calls.Clear(); runner.PackageCalls.Clear();
                    var loop = client.RunControlledLoop(new List<LanCandidateDto> { report.Candidates[0] }, CancellationToken.None, null, null, new LanDriverClient.LanClosedLoopOptions { AllowUpdates = true });
                    T(runner.PackageCalls.Count == 1 && loop.NoCandidatesRemaining, "closed loop never reimports the same whole model for another device in that package");
                    runner.Calls.Clear(); runner.PackageCalls.Clear();
                    var rejectClient = new LanDriverClient(new LanClientConfig { ServerUrl = env.Url, StagingDir = Path.Combine(dir, "reject-cache"), VerifySignature = true, TimeoutMs = 10000, DownloadTimeoutMs = 10000 }, new Machine { Maker = "TestVendor", Model = "ModelA" }, new StaticHardwareCollector(devices), new OsTarget { Architecture = "amd64", MajorVersion = 10 }, _log, new RejectingSignatureVerifier(), new AlwaysAdminChecker(), runner, new NoOpSystemOps());
                    var rejected = rejectClient.InstallCandidates(report.Candidates, devices, CancellationToken.None, null);
                    T(!string.IsNullOrEmpty(rejected.Error) && runner.Calls.Count == 0, "signature failure prevents every import invocation for the model package");
                    var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                    var cancelResult = client.InstallCandidates(report.Candidates, devices, cancelled.Token, null);
                    T(cancelResult.Cancelled && runner.Calls.Count == 0, "cancelled model import never invokes pnputil");
                    T(!Directory.Exists(Path.Combine(dir, "staging")) || Directory.GetDirectories(Path.Combine(dir, "staging")).Length == 0, "model-package temporary cache is cleaned");
                    var generic = client.Report(devices, new Machine { Maker = "TestVendor", Model = "Unmapped" }, CancellationToken.None, null);
                    T(generic.Candidates.Count == 1 && generic.Candidates[0].DriverVerVersion == "99.0.0.0", "generic fallback excludes only mapped model folders, rather than their entire imported source");
                }
                finally { _log.E -= count; WindowsDriverPreference.TestPreferenceProbe = null; }
            }
        }
        private static void TestScanRanksUnstagedPackage(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "scan-unstaged-rank"));
            string src = Path.Combine(dir, "source"); Directory.CreateDirectory(src);
            WriteInf(src, "fresh.inf", "PCI\\VEN_B210&DEV_B210", "03/14/2026", "2.0.0.0", "NTamd64.10");
            string repo = MakeRepo(Path.Combine(dir, "repo"), src);
            using (var env = StartEnv(repo, null, null))
            {
                var device = Dev("PCI\\VEN_B210&DEV_B210", "SCAN-RANK-DEVICE", "oem-scan.inf", "1.0.0.0", false);
                var devices = new List<D> { device };
                var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var client = NewClient(env.Url, null, null, Path.Combine(dir, "staging"), devices, runner);
                int probes = 0;
                WindowsDriverPreference.TestPreferenceProbe = (d, inf) =>
                {
                    probes++;
                    T(File.Exists(inf), "scan ranking uses an existing extracted INF, not just version metadata");
                    return false;
                };
                try
                {
                    var first = client.Report(devices, new Machine { Maker = "T" }, CancellationToken.None, null);
                    T(first.Candidates.Count == 1 && !first.Candidates[0].NeedInstall && !string.IsNullOrEmpty(first.Candidates[0].LocalRejectionReason), "first scan rejects unstaged outranked package before display or installation");
                    T(probes == 1 && runner.Calls.Count == 0, "scan checks ranking without running pnputil");
                    var second = client.Report(devices, new Machine { Maker = "T" }, CancellationToken.None, null);
                    T(!second.Candidates[0].NeedInstall && probes == 1, "next scan uses persisted rejection without downloading/probing again");
                    device.Version = "1.1.0.0";
                    WindowsDriverPreference.TestPreferenceProbe = (d, inf) => { probes++; return true; };
                    var changed = client.Report(devices, new Machine { Maker = "T" }, CancellationToken.None, null);
                    T(changed.Candidates[0].NeedInstall && probes == 2, "binding change reevaluates and retains a better candidate");
                    device.Inf = ""; device.Version = "";
                    var missing = client.Report(devices, new Machine { Maker = "T" }, CancellationToken.None, null);
                    T(missing.Candidates[0].NeedInstall && probes == 2, "missing-driver devices are not suppressed by update ranking");
                    T(!Directory.Exists(Path.Combine(dir, "staging")) || Directory.GetDirectories(Path.Combine(dir, "staging")).Length == 0, "scan validation cleans temporary packages");
                }
                finally { WindowsDriverPreference.TestPreferenceProbe = null; }
            }
        }

        private static void TestScanSharedFileCache(string root)
        {
            string folder = PrepareDir(Path.Combine(root, "scan-file-sharing"));
            WriteInf(folder, "shared.inf", "PCI\\VEN_B220&DEV_0001", "03/14/2026", "2.0.0.0", "NTamd64.10");
            string path = Path.Combine(folder, "shared.inf"); var files = new DriverScanFiles();
            string expected = OfflineRepoGenerator.Sha256File(path);
            ClientScanParallelism.Run(Enumerable.Range(0, 128).ToList(), 128, CancellationToken.None, (i, token) => {
                if (!string.Equals(files.Hash(path), expected, StringComparison.OrdinalIgnoreCase) || files.Parse(path, "test").DriverVerVersion != "2.0.0.0") throw new Exception("shared INF result mismatch");
            });
            T(files.HashReads == 1 && files.ParseReads == 1, "128 concurrent device consumers hash/parse an unchanged INF only once");
            File.AppendAllText(path, "\r\n; changed while scanning\r\n");
            T(!string.Equals(files.Hash(path), expected, StringComparison.OrdinalIgnoreCase) && files.HashReads == 2, "file changes invalidate the scan-local hash entry");
            files.Parse(path, "test"); T(files.ParseReads == 2, "file changes invalidate the scan-local parsed INF entry");
            var nextScan = new DriverScanFiles(); nextScan.Hash(path);
            T(nextScan.HashReads == 1, "a new scan never reuses a previous scan's cached file state");
            File.Delete(path); bool missing = false; try { files.Hash(path); } catch (IOException) { missing = true; }
            T(missing, "deleted INF cannot be hidden by a successful cached read");
        }

        private static void TestScanParallelGroupingAndCancel(string root)
        {
            T(ClientScanParallelism.Workers(128, 256) == 128, "128 logical processors allow 128 scan workers, with no eight-thread cap");
            T(ClientScanParallelism.Workers(128, 3) == 3, "scan worker count does not exceed available jobs");
            T(ClientScanParallelism.Workers(1, 8) == 1, "single-core host uses one worker");
            string dir = PrepareDir(Path.Combine(root, "scan-parallel"));
            string src = Path.Combine(dir, "source"); Directory.CreateDirectory(src);
            WriteInf(src, "parallel-a.inf", "PCI\\VEN_B220&DEV_0001", "03/14/2026", "2.0.0.0", "NTamd64.10");
            WriteInf(src, "parallel-b.inf", "PCI\\VEN_B220&DEV_0002", "03/14/2026", "2.0.0.0", "NTamd64.10");
            string repo = MakeRepo(Path.Combine(dir, "repo"), src);
            using (var env = StartEnv(repo, null, null))
            {
                var devices = Enumerable.Range(0, 8).Select(i => Dev("PCI\\VEN_B220&DEV_000" + (i % 2 + 1), "SCAN-PARALLEL-" + i, "oem-parallel.inf", "1.0.0.0", false)).ToList();
                var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var client = NewClient(env.Url, null, null, Path.Combine(dir, "staging"), devices, runner);
                int active = 0, peak = 0, calls = 0;
                int slowPreparationFinished = 0, rankBeforeSlowPreparation = 0;
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var gate = new object();
                var overlapping = new ManualResetEventSlim(false);
                ClientScanParallelism.TestProcessorCount = () => 4;
                WindowsDriverPreference.TestPreferenceProbe = (device, inf) =>
                {
                    if (Volatile.Read(ref slowPreparationFinished) == 0) Interlocked.Exchange(ref rankBeforeSlowPreparation, 1);
                    int count = Interlocked.Increment(ref active);
                    lock (gate) { peak = Math.Max(peak, count); paths.Add(inf); }
                    if (count >= 2) overlapping.Set();
                    overlapping.Wait(5000);
                    Thread.Sleep(30);
                    Interlocked.Increment(ref calls);
                    Interlocked.Decrement(ref active);
                    return true;
                };
                try
                {
                    int downloads = 0;
                    Action<string> countDownloads = message => { if (message.Contains("Download starting:") && (message.Contains("parallel-a.inf") || message.Contains("parallel-b.inf"))) Interlocked.Increment(ref downloads); };
                    ServicePointManager.FindServicePoint(new Uri(env.Url)).ConnectionLimit = 1;
                    LanReportResponse report;
                    _log.E += countDownloads;
                    try { report = client.Report(devices, new Machine { Maker = "T" }, CancellationToken.None, new InlineProgress<string>(message => {
                        if (message.StartsWith("正在并行下载", StringComparison.Ordinal) && message.EndsWith("parallel-b.inf", StringComparison.Ordinal)) { Thread.Sleep(1000); Volatile.Write(ref slowPreparationFinished, 1); }
                    })); }
                    finally { _log.E -= countDownloads; }
                    T(rankBeforeSlowPreparation == 1 && slowPreparationFinished == 1, "a ready package starts device ranking before the slow package finishes preparation");
                    T(peak >= 2 && peak <= 4, "independent device rank probes overlap within hardware worker budget; peak=" + peak);
                    T(calls == 8 && report.Candidates.All(c => c.NeedInstall), "parallel scan preserves each device's independent result");
                    T(paths.Count == 2, "eight devices share only two downloaded/extracted INF packages, got " + paths.Count);
                    T(downloads == 2, "same-package devices cause exactly two HTTP package downloads, got " + downloads);
                    T(ServicePointManager.FindServicePoint(new Uri(env.Url)).ConnectionLimit >= 2, "scan raises a restrictive HTTP per-host connection limit to its download worker budget");
                    T(runner.Calls.Count == 0, "parallel scanning never invokes driver installation");
                    T(Directory.GetDirectories(Path.Combine(dir, "staging")).Length == 0, "parallel scan cleans all shared package directories");
                    WindowsDriverPreference.TestPreferenceProbe = (device, inf) => { Thread.Sleep(80); return true; };
                    ClientScanParallelism.TestProcessorCount = () => 1;
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    client.Report(devices, new Machine { Maker = "T" }, CancellationToken.None, null);
                    long serialMs = clock.ElapsedMilliseconds;
                    ClientScanParallelism.TestProcessorCount = () => 4;
                    clock.Restart();
                    client.Report(devices, new Machine { Maker = "T" }, CancellationToken.None, null);
                    Console.WriteLine("[INFO] Scan benchmark (8 devices / 2 real HTTP packages / simulated Windows probe latency 80ms): serial=" + serialMs + "ms; four workers=" + clock.ElapsedMilliseconds + "ms; observed rank peak=" + peak);
                    using (var cancel = new CancellationTokenSource())
                    {
                        WindowsDriverPreference.TestPreferenceProbe = (device, inf) => { cancel.Cancel(); return true; };
                        bool cancelled = false;
                        try { client.Report(devices, new Machine { Maker = "T" }, cancel.Token, null); }
                        catch (OperationCanceledException) { cancelled = true; }
                        T(cancelled, "cancellation of parallel ranking is surfaced as cancellation");
                        T(Directory.GetDirectories(Path.Combine(dir, "staging")).Length == 0, "cancelled parallel ranking cleans every shared package directory");
                    }
                    WindowsDriverPreference.TestPreferenceProbe = (device, inf) => { throw new IOException("injected rank failure"); };
                    bool failed = false;
                    try { client.Report(devices, new Machine { Maker = "T" }, CancellationToken.None, null); }
                    catch (InvalidOperationException ex) { failed = ex.Message.Contains("injected rank failure"); }
                    T(failed && Directory.GetDirectories(Path.Combine(dir, "staging")).Length == 0, "parallel worker failure cancels siblings and cleans all package directories");
                }
                finally { overlapping.Dispose(); WindowsDriverPreference.TestPreferenceProbe = null; ClientScanParallelism.TestProcessorCount = null; }
            }
        }

        // ---------------------------------------------------------------------------
        //  14. Receipt semantics: a historical receipt must never mask a device that is
        //      missing / bad / empty-version; suppression requires the device to currently
        //      actually run the same package (its in-effect INF hashes to the content hash).
        // ---------------------------------------------------------------------------
        private static void TestReceiptNoMaskMissingDriver(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "receipt"));
            string src = Path.Combine(dir, "PkgA"); Directory.CreateDirectory(src);
            WriteInf(src, "dev.inf", "PCI\\VEN_B100&DEV_B100", "03/14/2026", "2.0.0.0", "NTamd64.10");
            string repo = MakeRepo(Path.Combine(dir, "repo"), src);
            using (var env = StartEnv(repo, null, null))
            {
                // Discover the candidate content hash for the device.
                var probe = NewClient(env.Url, null, null, Path.Combine(dir, "s-probe"), new List<D> { Dev("PCI\\VEN_B100&DEV_B100", "DR", "", "", false) }, new FakePnpInstallRunner { DefaultExitCode = 0 });
                var resp = probe.Report(new List<D> { Dev("PCI\\VEN_B100&DEV_B100", "DR", "", "", false) }, new Machine { Maker = "T" }, CancellationToken.None, null);
                T(resp.Candidates.Count == 1, "probe candidate present");
                string hash = resp.Candidates[0].ContentHash;
                T(!string.IsNullOrEmpty(hash), "probe hash present");
                // Mark the receipt as if this very device+package had been installed before.
                LanInstallReceiptStore.Mark("DR", hash);
                T(LanInstallReceiptStore.Contains("DR", hash), "receipt recorded");
                // (a) The client install path is NOT suppressed by the receipt: device still missing -> installed.
                var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var client = NewClient(env.Url, null, null, Path.Combine(dir, "s-install"), new List<D> { Dev("PCI\\VEN_B100&DEV_B100", "DR", "", "", false) }, runner);
                var res = client.Run(CancellationToken.None);
                T(res.InstalledOk == 1, "receipt does not mask missing driver, ok=" + res.InstalledOk + " err=" + res.Error);
                T(runner.Calls.Count == 1, "runner called despite receipt, calls=" + runner.Calls.Count);
                // (b) Suppression requires the device to actually run the same package (test seam).
                // The fake in-effect INF is a copy of the repo INF, so its hash equals the content hash.
                string tmpInf = Path.Combine(dir, "fake-current.inf");
                File.Copy(Directory.GetFiles(repo, "dev.inf", SearchOption.AllDirectories).First(), tmpInf, true);
                string currentHash = Sha256Of(tmpInf);
                LanInstalledPackage.TestCurrentInfHash = inf => string.Equals(inf, "oemX.inf", StringComparison.OrdinalIgnoreCase) ? currentHash : null;
                try
                {
                    var missing = Dev("PCI\\VEN_B100&DEV_B100", "DR", "", "", false);
                    var broken = Dev("PCI\\VEN_B100&DEV_B100", "DR", "oemX.inf", "9.9.9.9", true);
                    var emptyVer = Dev("PCI\\VEN_B100&DEV_B100", "DR", "oemX.inf", "", false);
                    T(!LanDriverClient.IsReceiptSuppressed(resp.Candidates[0], missing), "receipt never suppresses a missing driver");
                    T(!LanDriverClient.IsReceiptSuppressed(resp.Candidates[0], broken), "receipt never suppresses a bad device");
                    T(!LanDriverClient.IsReceiptSuppressed(resp.Candidates[0], emptyVer), "receipt never suppresses an empty-version device");
                    var inEffect = Dev("PCI\\VEN_B100&DEV_B100", "DR", "oemX.inf", "2.0.0.0", false);
                    T(LanDriverClient.IsReceiptSuppressed(resp.Candidates[0], inEffect), "receipt suppresses only when package actually in effect");
                    var noReceipt = Dev("PCI\\VEN_B100&DEV_B100", "NO-RECEIPT", "oemX.inf", "1.0.0.0", false);
                    T(LanDriverClient.IsReceiptSuppressed(resp.Candidates[0], noReceipt), "live identical INF does not require receipt or version inference");
                    var otherPkg = Dev("PCI\\VEN_B100&DEV_B100", "DR", "oemY.inf", "2.0.0.0", false);
                    T(!LanDriverClient.IsReceiptSuppressed(resp.Candidates[0], otherPkg), "a different in-effect package is not suppressed");
                    // File-based check agrees.
                    T(LanInstalledPackage.MatchesCurrentInfFile(tmpInf, currentHash), "MatchesCurrentInfFile true on exact hash");
                    T(!LanInstalledPackage.MatchesCurrentInfFile(tmpInf, "deadbeef"), "MatchesCurrentInfFile false on hash mismatch");
                    T(!LanInstalledPackage.MatchesCurrentInfFile(null, currentHash), "MatchesCurrentInfFile false on empty path");
                }
                finally { LanInstalledPackage.TestCurrentInfHash = null; }
            }
        }

        private static string Sha256Of(string path)
        {
            using (var s = File.OpenRead(path))
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var sb = new StringBuilder();
                foreach (byte b in sha.ComputeHash(s)) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        // ---------------------------------------------------------------------------
        //  15. Controlled closed loop. A mutating collector presents a fresh hardware
        //      snapshot each round; snapshots model installs actually taking effect.
        // ---------------------------------------------------------------------------

        private sealed class MutatingCollector : IHardwareCollector
        {
            private readonly List<List<D>> _snapshots;
            private readonly Action<int> _onCollect;
            private int _index = -1;
            public MutatingCollector(List<List<D>> snapshots, Action<int> onCollect) { _snapshots = snapshots; _onCollect = onCollect; }
            public List<D> Collect(L logger)
            {
                _index++;
                if (_onCollect != null) _onCollect(_index);
                return _index < _snapshots.Count ? _snapshots[_index] : _snapshots[_snapshots.Count - 1];
            }
        }

        private static LanDriverClient LoopClient(string url, string stagingDir, IHardwareCollector collector, FakePnpInstallRunner runner)
        {
            var cfg = new LanClientConfig
            {
                ServerUrl = url, Token = "", BootstrapRepoDir = "", TimeoutMs = 30000, DownloadTimeoutMs = 10000,
                MaxRetries = 4, BackoffInitialMs = 50, StagingDir = stagingDir, Deploy = false, WhatIf = false, Force = false,
                InstallTimeoutSeconds = 5, DedupeByContent = true, VerifySignature = true
            };
            return new LanDriverClient(cfg, new Machine { Maker = "TestVendor", Model = "ModelA", Sku = "SKU1" }, collector,
                new OsTarget { Architecture = "amd64", MajorVersion = 10 }, _log, new TrustingSignatureVerifier(), new AlwaysAdminChecker(), runner, new NoOpSystemOps());
        }

        private static List<string> MakeSourceInfs(string dir, string prefix, int count)
        {
            string src = Path.Combine(dir, prefix); Directory.CreateDirectory(src);
            for (int i = 1; i <= count; i++)
                WriteInf(src, prefix + i + ".inf", "PCI\\VEN_" + prefix.ToUpperInvariant() + "&DEV_" + i.ToString("000", CultureInfo.InvariantCulture),
                    "03/14/2026", "2.0.0.0", "NTamd64.10");
            return new List<string> { src };
        }

        private static string MakeRepoFromDirs(string repoDir, List<string> srcDirs)
        {
            new OfflineRepoGenerator(new TrustingSignatureVerifier(), _log).Generate(repoDir, srcDirs.ToArray(),
                new OsTarget { Architecture = "amd64", MajorVersion = 10 }, null, "2026-07-28T00:00:00Z", CancellationToken.None, null);
            return repoDir;
        }

        private static LanCandidateDto FirstCandidate(LanDriverClient client, List<D> devices)
        {
            var resp = client.Report(devices, new Machine { Maker = "T" }, CancellationToken.None, null);
            return resp.Candidates.Count > 0 ? resp.Candidates[0] : null;
        }

        // Install round 0 for device A; round 1 finds new device B missing and installs it; round 2 has none -> done.
        private static void TestClosedLoopNewDeviceThenDone(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "loop-new"));
            string repo = MakeRepoFromDirs(Path.Combine(dir, "repo"), MakeSourceInfs(dir, "loopa", 1).Concat(MakeSourceInfs(dir, "loopb", 1)).ToList());
            using (var env = StartEnv(repo, null, null))
            {
                string hwA = "PCI\\VEN_LOOPA&DEV_001", hwB = "PCI\\VEN_LOOPB&DEV_001";
                var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var snapshots = new List<List<D>>
                {
                    new List<D> { Dev(hwA, "LA", "oemA.inf", "2.0.0.0", false), Dev(hwB, "LB", "", "", false) }, // round 1 re-collect: A installed, B missing
                    new List<D> { Dev(hwA, "LA", "oemA.inf", "2.0.0.0", false), Dev(hwB, "LB", "oemB.inf", "2.0.0.0", false) } // round 2: all in effect
                };
                var collector = new MutatingCollector(snapshots, null);
                var client = LoopClient(env.Url, Path.Combine(dir, "staging"), collector, runner);
                var initial = FirstCandidate(client, new List<D> { Dev(hwA, "LA", "", "", false) });
                T(initial != null, "initial candidate for A");
                var rounds = new List<string>();
                var loop = client.RunControlledLoop(new List<LanCandidateDto> { initial }, CancellationToken.None, null,
                    new Progress<string>(s => rounds.Add(s)), null);
                T(loop.Rounds == 2, "rounds=2 (initial + 1 continuation), got " + loop.Rounds);
                T(loop.NoCandidatesRemaining, "loop ended with no candidates remaining");
                T(loop.InstalledOk == 2, "both A and B installed, ok=" + loop.InstalledOk);
                T(loop.Cancelled == false && string.IsNullOrEmpty(loop.Error), "no cancel/error, err=" + loop.Error);
                T(runner.Calls.Count == 2, "runner called twice, calls=" + runner.Calls.Count);
                T(rounds.Count > 0, "round progress reported to UI, count=" + rounds.Count);
                T(loop.ProcessedKeys.Count == 2, "two processed keys, keys=" + loop.ProcessedKeys.Count);
                T(loop.ProcessedKeys.All(k => k.Contains("|")), "processed keys are device|hash");
            }
        }

        // The device keeps appearing missing (install never takes effect): the same item is never
        // re-processed, so the loop terminates instead of looping forever.
        private static void TestClosedLoopDedupSameItem(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "loop-dedup"));
            string repo = MakeRepoFromDirs(Path.Combine(dir, "repo"), MakeSourceInfs(dir, "loopd", 1));
            using (var env = StartEnv(repo, null, null))
            {
                string hw = "PCI\\VEN_LOOPD&DEV_001";
                var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
                // Every collect returns the same still-missing device.
                var collector = new MutatingCollector(new List<List<D>> { new List<D> { Dev(hw, "LD", "", "", false) } }, null);
                var client = LoopClient(env.Url, Path.Combine(dir, "staging"), collector, runner);
                var loop = client.RunControlledLoop(null, CancellationToken.None, null, null, null);
                T(loop.NoCandidatesRemaining, "loop terminated despite device still missing");
                T(loop.Rounds == 1, "only one round processed the item, rounds=" + loop.Rounds);
                T(loop.InstalledOk == 1, "item installed once, ok=" + loop.InstalledOk);
                T(runner.Calls.Count == 1, "exactly one install call, calls=" + runner.Calls.Count);
                T(loop.ProcessedKeys.Count == 1, "one processed key, keys=" + loop.ProcessedKeys.Count);
            }
        }

        // A different device appears missing each round: the loop runs exactly 5 rounds and then stops.
        private static void TestClosedLoopMaxRounds(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "loop-max"));
            string repo = MakeRepoFromDirs(Path.Combine(dir, "repo"), MakeSourceInfs(dir, "loopm", 8));
            using (var env = StartEnv(repo, null, null))
            {
                var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var snapshots = new List<List<D>>();
                for (int round = 0; round < 8; round++)
                {
                    var snap = new List<D>();
                    for (int i = 0; i <= round; i++)
                    {
                        bool inEffect = i < round;   // the device installed in the previous round is now in effect
                        snap.Add(Dev("PCI\\VEN_LOOPM&DEV_" + (i + 1).ToString("000", CultureInfo.InvariantCulture), "LM" + i,
                            inEffect ? "oem" + i + ".inf" : "", inEffect ? "2.0.0.0" : "", false));
                    }
                    snapshots.Add(snap);
                }
                var collector = new MutatingCollector(snapshots, null);
                var client = LoopClient(env.Url, Path.Combine(dir, "staging"), collector, runner);
                var loop = client.RunControlledLoop(null, CancellationToken.None, null, null, null);
                T(loop.Rounds == 5, "exactly 5 rounds, rounds=" + loop.Rounds);
                T(loop.StoppedAtMaxRounds, "stopped at max rounds");
                T(!loop.NoCandidatesRemaining, "did not end because no candidates (there were more)");
                T(loop.InstalledOk == 5, "one install per round, ok=" + loop.InstalledOk);
                T(runner.Calls.Count == 5, "5 install calls, calls=" + runner.Calls.Count);
                T(loop.ProcessedKeys.Count == 5, "5 processed keys, keys=" + loop.ProcessedKeys.Count);
            }
        }

        // Cancellation mid-loop: cancelling after the first install round stops the loop with Cancelled=true
        // and the round-1 results are kept in the accumulated totals.
        private static void TestClosedLoopCancel(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "loop-cancel"));
            string repo = MakeRepoFromDirs(Path.Combine(dir, "repo"), MakeSourceInfs(dir, "loopc", 3));
            using (var env = StartEnv(repo, null, null))
            {
                var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var cts = new CancellationTokenSource();
                var snapshots = new List<List<D>>();
                for (int round = 0; round < 3; round++)
                {
                    var snap = new List<D>();
                    for (int i = 0; i <= round; i++)
                    {
                        bool inEffect = i < round;
                        snap.Add(Dev("PCI\\VEN_LOOPC&DEV_" + (i + 1).ToString("000", CultureInfo.InvariantCulture), "LC" + i,
                            inEffect ? "oem" + i + ".inf" : "", inEffect ? "2.0.0.0" : "", false));
                    }
                    snapshots.Add(snap);
                }
                // Cancel as soon as the second re-collect happens (i.e. after round 1 completed).
                var collector = new MutatingCollector(snapshots, index => { if (index >= 1) { try { cts.Cancel(); } catch { } } });
                var client = LoopClient(env.Url, Path.Combine(dir, "staging"), collector, runner);
                var loop = client.RunControlledLoop(null, cts.Token, null, null, null);
                T(loop.Cancelled, "loop cancelled=" + loop.Cancelled);
                T(loop.Rounds == 1, "round 1 ran then cancellation stopped round 2, rounds=" + loop.Rounds);
                T(loop.InstalledOk == 1, "round 1 result kept, ok=" + loop.InstalledOk);
            }
        }

        // A device that genuinely has an older driver installed is an UPDATE candidate: the loop must
        // never auto-install it (it requires explicit user consent) and therefore finds nothing safe.
        private static void TestClosedLoopNoAutoUpdate(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "loop-noupd"));
            string repo = MakeRepoFromDirs(Path.Combine(dir, "repo"), MakeSourceInfs(dir, "loopu", 1));
            using (var env = StartEnv(repo, null, null))
            {
                string hw = "PCI\\VEN_LOOPU&DEV_001";
                var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
                // Device present with an old but valid installed driver; repo offers 2.0.0.0.
                var collector = new MutatingCollector(new List<List<D>> { new List<D> { Dev(hw, "LU", "oemOld.inf", "1.0.0.0", false) } }, null);
                var client = LoopClient(env.Url, Path.Combine(dir, "staging"), collector, runner);
                var loop = client.RunControlledLoop(null, CancellationToken.None, null, null, null);
                T(loop.InstalledOk == 0, "update-type candidate not auto-installed, ok=" + loop.InstalledOk);
                T(runner.Calls.Count == 0, "runner never called, calls=" + runner.Calls.Count);
                T(loop.NoCandidatesRemaining, "loop reports no (safe) candidates remaining");
                // An explicit user pick of the same candidate still installs via the initial round.
                var initial = FirstCandidate(client, new List<D> { Dev(hw, "LU", "oemOld.inf", "1.0.0.0", false) });
                T(initial != null, "server still offers the update candidate");
                var loop2 = client.RunControlledLoop(new List<LanCandidateDto> { initial }, CancellationToken.None, null, null, null);
                T(loop2.InstalledOk == 1, "user-confirmed update installed in round 0, ok=" + loop2.InstalledOk);
                T(runner.Calls.Count == 1, "runner called once after user confirm, calls=" + runner.Calls.Count);
            }
        }

        // ---------------------------------------------------------------------------
        //  16. InfApplicability integration (Phase 2): the server candidate stage and the
        //      client pre-install stage both run the full InfApplicability.Evaluate gates
        //      (architecture, OS major, minimum build, device-ID model match with exact
        //      hardware ID priority). INFs that only passed the coarse OsArchFilter must
        //      never reach the candidate list, and devices without an applicable candidate
        //      are reported explicitly with a precise rejection reason.
        // ---------------------------------------------------------------------------

        // Multi-section INF: two decorated [Models.*] sections with DIFFERENT device IDs, so the
        // record-level aggregated ID index can match a device whose ID only lives in a section
        // that is NOT applicable to the target OS. Only the full per-model-entry Evaluate gate
        // catches this; OsArchFilter (record level) lets it through.
        private static string WriteTwoSectionInf(string dir, string name, string x86Hwid, string amd64Hwid)
        {
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, new StringBuilder()
                .AppendLine("[Version]")
                .AppendLine("Signature=\"$Windows NT$\"")
                .AppendLine("Class=Net")
                .AppendLine("ClassGuid={4d36e972-e325-11ce-bfc1-08002be10318}")
                .AppendLine("Provider=%Vendor%")
                .AppendLine("DriverVer=03/14/2026,2.0.0.0")
                .AppendLine("")
                .AppendLine("[Manufacturer]")
                .AppendLine("%Vendor%=Models,NTx86.10,NTamd64.10")
                .AppendLine("")
                .AppendLine("[Models.NTx86.10]")
                .AppendLine("%X86Dev%=Install," + x86Hwid)
                .AppendLine("")
                .AppendLine("[Models.NTamd64.10]")
                .AppendLine("%Amd64Dev%=Install," + amd64Hwid)
                .AppendLine("")
                .AppendLine("[Strings]")
                .AppendLine("Vendor=\"Test Vendor\"")
                .AppendLine("X86Dev=\"X86 Device\"")
                .AppendLine("Amd64Dev=\"Amd64 Device\"")
                .ToString(), new UTF8Encoding(false));
            return path;
        }

        // Wrong architecture / minimum build / models entries: the INF must never appear as a candidate.
        private static void TestApplicabilityServerGates(string root)
        {
            // (a) Wrong architecture: an x86-only INF is never offered to an amd64 device.
            string dirA = PrepareDir(Path.Combine(root, "app-arch"));
            string srcA = Path.Combine(dirA, "PkgA"); Directory.CreateDirectory(srcA);
            WriteInf(srcA, "x86.inf", "PCI\\VEN_AA00&DEV_AA00", "03/14/2026", "2.0.0.0", "NTx86.10");
            string repoA = MakeRepo(Path.Combine(dirA, "repo"), srcA);
            using (var env = StartEnv(repoA, null, null))
            {
                var req = new LanReportRequest { Protocol = LanProtocol.Version, RequestId = "app-arch-1", OsArchitecture = "amd64", OsMajorVersion = 10,
                    Devices = new List<LanDeviceDto> { new LanDeviceDto { DeviceInstanceId = "DA", HardwareIds = new List<string> { "PCI\\VEN_AA00&DEV_AA00" } } } };
                var resp = new JavaScriptSerializer().Deserialize<LanReportResponse>(HttpPost(env.Url + "/api/report", J(req), null).Item2);
                T(resp.Candidates.Count == 0, "wrong arch: no candidate, got " + resp.Candidates.Count);
                T(resp.UnmatchedDevices.Count == 1, "wrong arch: device reported unmatched, got " + resp.UnmatchedDevices.Count);
            }

            // (b) Minimum build: NTamd64.10.0...30000 requires build >= 30000. With the client's build
            //     number reported (19045) the INF must be excluded; the same request WITHOUT the build
            //     (old protocol clients, build unknown) must still match — protocol compatible.
            string dirB = PrepareDir(Path.Combine(root, "app-build"));
            string srcB = Path.Combine(dirB, "PkgB"); Directory.CreateDirectory(srcB);
            WriteInf(srcB, "build.inf", "PCI\\VEN_AA01&DEV_AA01", "03/14/2026", "2.0.0.0", "NTamd64.10.0...30000");
            string repoB = MakeRepo(Path.Combine(dirB, "repo"), srcB);
            using (var env = StartEnv(repoB, null, null))
            {
                var withBuild = new LanReportRequest { Protocol = LanProtocol.Version, RequestId = "app-build-1", OsArchitecture = "amd64", OsMajorVersion = 10, OsBuildNumber = 19045,
                    Devices = new List<LanDeviceDto> { new LanDeviceDto { DeviceInstanceId = "DB", HardwareIds = new List<string> { "PCI\\VEN_AA01&DEV_AA01" } } } };
                var respB = new JavaScriptSerializer().Deserialize<LanReportResponse>(HttpPost(env.Url + "/api/report", J(withBuild), null).Item2);
                T(respB.Candidates.Count == 0, "build too low: no candidate, got " + respB.Candidates.Count);
                T(respB.UnmatchedDevices.Count == 1 && respB.UnmatchedDevices[0].ReasonCode.IndexOf("Build") >= 0,
                    "build too low: explicit reason code, got " + (respB.UnmatchedDevices.Count > 0 ? respB.UnmatchedDevices[0].ReasonCode : "<none>"));
                var noBuild = new LanReportRequest { Protocol = LanProtocol.Version, RequestId = "app-build-2", OsArchitecture = "amd64", OsMajorVersion = 10,
                    Devices = withBuild.Devices };
                var respNB = new JavaScriptSerializer().Deserialize<LanReportResponse>(HttpPost(env.Url + "/api/report", J(noBuild), null).Item2);
                T(respNB.Candidates.Count == 0, "unknown build must not recommend build-restricted driver, got " + respNB.Candidates.Count);
                T(respNB.UnmatchedDevices.Count == 1 && respNB.UnmatchedDevices[0].ReasonCode == "BuildUnknown", "unknown build is reported explicitly");
            }

            // (c) Models entries: the device ID only appears in a section that is NOT applicable to
            //     this OS — the record matches the coarse filter but has no applicable model entry.
            string dirC = PrepareDir(Path.Combine(root, "app-models"));
            string srcC = Path.Combine(dirC, "PkgC"); Directory.CreateDirectory(srcC);
            WriteTwoSectionInf(srcC, "dual.inf", "PCI\\VEN_AA02&DEV_X86", "PCI\\VEN_AA02&DEV_AMD64");
            string repoC = MakeRepo(Path.Combine(dirC, "repo"), srcC);
            using (var env = StartEnv(repoC, null, null))
            {
                var req = new LanReportRequest { Protocol = LanProtocol.Version, RequestId = "app-models-1", OsArchitecture = "amd64", OsMajorVersion = 10, OsBuildNumber = 19045,
                    Devices = new List<LanDeviceDto> { new LanDeviceDto { DeviceInstanceId = "DC", HardwareIds = new List<string> { "PCI\\VEN_AA02&DEV_X86" } } } };
                var resp = new JavaScriptSerializer().Deserialize<LanReportResponse>(HttpPost(env.Url + "/api/report", J(req), null).Item2);
                T(resp.Candidates.Count == 0, "no applicable model entry: no candidate, got " + resp.Candidates.Count);
                T(resp.UnmatchedDevices.Count == 1 && resp.UnmatchedDevices[0].ReasonCode == "NoMatchingModelEntry",
                    "no applicable model entry: precise reason, got " + (resp.UnmatchedDevices.Count > 0 ? resp.UnmatchedDevices[0].ReasonCode : "<none>"));
                // The amd64 device still matches the applicable section.
                var reqAmd = new LanReportRequest { Protocol = LanProtocol.Version, RequestId = "app-models-2", OsArchitecture = "amd64", OsMajorVersion = 10, OsBuildNumber = 19045,
                    Devices = new List<LanDeviceDto> { new LanDeviceDto { DeviceInstanceId = "DC2", HardwareIds = new List<string> { "PCI\\VEN_AA02&DEV_AMD64" } } } };
                var respAmd = new JavaScriptSerializer().Deserialize<LanReportResponse>(HttpPost(env.Url + "/api/report", J(reqAmd), null).Item2);
                T(respAmd.Candidates.Count == 1 && respAmd.Candidates[0].MatchLevel == 4, "applicable section matches with exact hardware ID level");
            }
        }

        // Exact hardware-ID match must beat a compatible-ID match for the same device.
        private static void TestApplicabilityExactIdPriority(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "app-priority"));
            string srcCompat = Path.Combine(dir, "Compat"); Directory.CreateDirectory(srcCompat);
            // Compatible-ID-only INF: model line lists the hardware ID then a compatible ID.
            File.WriteAllText(Path.Combine(srcCompat, "compat.inf"), new StringBuilder()
                .AppendLine("[Version]")
                .AppendLine("Signature=\"$Windows NT$\"")
                .AppendLine("Class=Net")
                .AppendLine("ClassGuid={4d36e972-e325-11ce-bfc1-08002be10318}")
                .AppendLine("Provider=%Vendor%")
                .AppendLine("DriverVer=03/14/2026,2.0.0.0")
                .AppendLine("")
                .AppendLine("[Manufacturer]")
                .AppendLine("%Vendor%=Models")
                .AppendLine("")
                .AppendLine("[Models.NTamd64.10]")
                .AppendLine("%Dev%=Install,PCI\\VEN_AB00&DEV_AB00")
                .AppendLine("%Gen%=Install,PCI\\VEN_AB00&CC_0200")
                .AppendLine("")
                .AppendLine("[Strings]")
                .AppendLine("Vendor=\"Test Vendor\"")
                .AppendLine("Dev=\"Exact Device\"")
                .AppendLine("Gen=\"Generic Device\"")
                .ToString(), new UTF8Encoding(false));
            string srcExact = Path.Combine(dir, "Exact"); Directory.CreateDirectory(srcExact);
            WriteInf(srcExact, "exact.inf", "PCI\\VEN_AB00&DEV_AB00&SUBSYS_9999", "03/14/2026", "2.0.0.0", "NTamd64.10");
            string repo = MakeRepoFromDirs(Path.Combine(dir, "repo"), new List<string> { srcCompat, srcExact });
            using (var env = StartEnv(repo, null, null))
            {
                var req = new LanReportRequest { Protocol = LanProtocol.Version, RequestId = "app-prio-1", OsArchitecture = "amd64", OsMajorVersion = 10, OsBuildNumber = 19045,
                    Devices = new List<LanDeviceDto> { new LanDeviceDto { DeviceInstanceId = "DP",
                        HardwareIds = new List<string> { "PCI\\VEN_AB00&DEV_AB00&SUBSYS_9999" },
                        CompatibleIds = new List<string> { "PCI\\VEN_AB00&DEV_AB00" } } } };
                var resp = new JavaScriptSerializer().Deserialize<LanReportResponse>(HttpPost(env.Url + "/api/report", J(req), null).Item2);
                T(resp.Candidates.Count == 1, "priority: one best candidate, got " + resp.Candidates.Count);
                if (resp.Candidates.Count == 1)
                {
                    T(resp.Candidates[0].InfRelativePath.EndsWith("exact.inf", StringComparison.OrdinalIgnoreCase),
                        "priority: exact hardware-ID INF wins over compatible-ID INF, got " + resp.Candidates[0].InfRelativePath);
                    T(resp.Candidates[0].MatchLevel == 4, "priority: match level 4 (device HW vs INF HW), got " + resp.Candidates[0].MatchLevel);
                    T(resp.Candidates[0].MatchedInfId.IndexOf("SUBSYS_9999", StringComparison.OrdinalIgnoreCase) >= 0,
                        "priority: matched INF id is the exact one, got " + resp.Candidates[0].MatchedInfId);
                    T(resp.Candidates[0].NeedInstall, "priority: missing-driver device flagged NeedInstall");
                }
            }
        }

        // A device with no applicable candidate is reported in UnmatchedDevices; the full client
        // scan still succeeds and the closed loop terminates cleanly (no silent gaps, no crash).
        private static void TestApplicabilityUnmatchedReported(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "app-unmatched"));
            string src = Path.Combine(dir, "PkgA"); Directory.CreateDirectory(src);
            WriteInf(src, "have.inf", "PCI\\VEN_AC00&DEV_AC00", "03/14/2026", "2.0.0.0", "NTamd64.10");
            string repo = MakeRepo(Path.Combine(dir, "repo"), src);
            using (var env = StartEnv(repo, null, null))
            {
                var req = new LanReportRequest { Protocol = LanProtocol.Version, RequestId = "app-unmatched-1", OsArchitecture = "amd64", OsMajorVersion = 10, OsBuildNumber = 19045,
                    Devices = new List<LanDeviceDto> {
                        new LanDeviceDto { DeviceInstanceId = "HAVE", HardwareIds = new List<string> { "PCI\\VEN_AC00&DEV_AC00" }, FriendlyName = "Matched Device" },
                        new LanDeviceDto { DeviceInstanceId = "MISS", HardwareIds = new List<string> { "PCI\\VEN_ACFF&DEV_ACFF" }, FriendlyName = "Unmatched Device" } } };
                var resp = new JavaScriptSerializer().Deserialize<LanReportResponse>(HttpPost(env.Url + "/api/report", J(req), null).Item2);
                T(resp.Candidates.Count == 1 && resp.Candidates[0].DeviceInstanceId == "HAVE", "matched device got its candidate");
                T(resp.UnmatchedDevices.Count == 1, "unmatched device reported, got " + resp.UnmatchedDevices.Count);
                if (resp.UnmatchedDevices.Count == 1)
                {
                    T(resp.UnmatchedDevices[0].DeviceInstanceId == "MISS", "unmatched id=" + resp.UnmatchedDevices[0].DeviceInstanceId);
                    T(resp.UnmatchedDevices[0].FriendlyName == "Unmatched Device", "unmatched friendly name kept");
                    T(resp.UnmatchedDevices[0].ReasonCode.Length > 0 && resp.UnmatchedDevices[0].Reason.Length > 0, "unmatched has explicit reason");
                }
                // The client black-box flow with only the unmatched device: report succeeds, nothing installs.
                var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var devices = new List<D> { Device("PCI\\VEN_ACFF&DEV_ACFF", "", "", "MISS") };
                var client = NewClient(env.Url, null, null, Path.Combine(dir, "staging"), devices, runner);
                var res = client.Run(CancellationToken.None);
                T(res.Reported, "unmatched-only client reported (err=" + res.Error + ")");
                T(res.Candidates == 0 && runner.Calls.Count == 0, "unmatched-only client installed nothing, candidates=" + res.Candidates);
            }
        }

        // Scan and install must agree: with the device and OS unchanged, re-reporting returns the
        // SAME candidate (path + content hash) the first scan installed.
        private static void TestApplicabilityScanInstallConsistency(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "app-consistency"));
            string src = Path.Combine(dir, "PkgA"); Directory.CreateDirectory(src);
            WriteInf(src, "same.inf", "PCI\\VEN_AD00&DEV_AD00", "03/14/2026", "2.0.0.0", "NTamd64.10");
            string repo = MakeRepo(Path.Combine(dir, "repo"), src);
            using (var env = StartEnv(repo, null, null))
            {
                var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var devices = new List<D> { Device("PCI\\VEN_AD00&DEV_AD00", "", "", "DEV1") };
                var client = NewClient(env.Url, null, null, Path.Combine(dir, "staging"), devices, runner);
                var first = client.Report(devices, new Machine { Maker = "T" }, CancellationToken.None, null);
                T(first.Candidates.Count == 1, "first scan returned one candidate");
                var res = client.InstallCandidates(first.Candidates, devices, CancellationToken.None, null);
                T(res.InstalledOk == 1, "install succeeded, ok=" + res.InstalledOk + " err=" + res.Error);
                // Same device, same OS, new RequestId -> identical verdict.
                var second = client.Report(devices, new Machine { Maker = "T" }, CancellationToken.None, null);
                T(second.Candidates.Count == 1, "second scan returned one candidate");
                T(string.Equals(first.Candidates[0].InfRelativePath, second.Candidates[0].InfRelativePath, StringComparison.OrdinalIgnoreCase),
                    "same candidate path across scans");
                T(string.Equals(first.Candidates[0].ContentHash, second.Candidates[0].ContentHash, StringComparison.OrdinalIgnoreCase),
                    "same candidate content hash across scans");
                T(first.Candidates[0].MatchLevel == second.Candidates[0].MatchLevel, "same match level across scans");
            }
        }

        // Pre-install revalidation on the extracted INF: when the device's IDs changed between scan
        // and install (or the OS changed), the download is refused with an explicit precise reason —
        // never a bare "skipped" count and never a pnputil call.
        private static void TestApplicabilityPreInstallRevalidation(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "app-revalidate"));
            string src = Path.Combine(dir, "PkgA"); Directory.CreateDirectory(src);
            WriteInf(src, "reval.inf", "PCI\\VEN_AE00&DEV_AE00", "03/14/2026", "2.0.0.0", "NTamd64.10");
            string repo = MakeRepo(Path.Combine(dir, "repo"), src);
            using (var env = StartEnv(repo, null, null))
            {
                var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var devices = new List<D> { Device("PCI\\VEN_AE00&DEV_AE00", "", "", "DEV1") };
                var client = NewClient(env.Url, null, null, Path.Combine(dir, "staging"), devices, runner);
                var cands = client.Report(devices, new Machine { Maker = "T" }, CancellationToken.None, null).Candidates;
                T(cands.Count == 1, "revalidation setup: candidate present");

                // (a) Device IDs changed after the scan: the actual INF no longer matches the device.
                var changed = new List<D> { Device("PCI\\VEN_AEFF&DEV_AEFF", "", "", "DEV1") };
                var resChanged = client.InstallCandidates(cands, changed, CancellationToken.None, null);
                T(resChanged.InstalledOk == 0 && runner.Calls.Count == 0, "device change: nothing installed, calls=" + runner.Calls.Count);
                T(resChanged.Log.Any(l => l.IndexOf("NoMatchingModelEntry", StringComparison.OrdinalIgnoreCase) >= 0),
                    "device change: explicit NoMatchingModelEntry reason logged");

                // (b) OS changed after the scan (client retargeted to x86): architecture gate rejects.
                var cfgX86 = new LanClientConfig
                {
                    ServerUrl = env.Url, Token = "", BootstrapRepoDir = "", TimeoutMs = 30000, DownloadTimeoutMs = 10000,
                    MaxRetries = 4, BackoffInitialMs = 50, StagingDir = Path.Combine(dir, "s-x86"), Deploy = false, WhatIf = false, Force = false,
                    InstallTimeoutSeconds = 5, DedupeByContent = true, VerifySignature = true
                };
                var clientX86 = new LanDriverClient(cfgX86, new Machine { Maker = "T" }, new StaticHardwareCollector(devices),
                    new OsTarget { Architecture = "x86", MajorVersion = 10, BuildNumber = 19045 }, _log,
                    new TrustingSignatureVerifier(), new AlwaysAdminChecker(), runner, new NoOpSystemOps());
                var resX86 = clientX86.InstallCandidates(cands, devices, CancellationToken.None, null);
                T(resX86.InstalledOk == 0 && runner.Calls.Count == 0, "os change: nothing installed, calls=" + runner.Calls.Count);
                T(resX86.Log.Any(l => l.IndexOf("ArchitectureMismatch", StringComparison.OrdinalIgnoreCase) >= 0),
                    "os change: explicit ArchitectureMismatch reason logged");

                // (c) Unchanged device + OS: the same candidate installs (no false rejections).
                var runnerC = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var clientC = NewClient(env.Url, null, null, Path.Combine(dir, "s-ok"), devices, runnerC);
                var resOk = clientC.InstallCandidates(cands, devices, CancellationToken.None, null);
                T(resOk.InstalledOk == 1 && runnerC.Calls.Count == 1, "unchanged device installs, ok=" + resOk.InstalledOk + " calls=" + runnerC.Calls.Count);
                T(!resOk.Log.Any(l => l.IndexOf("二次校验拒绝", StringComparison.Ordinal) >= 0), "no revalidation rejection on the happy path");
            }
        }

        // ---------------------------------------------------------------------------
        //  17. Server-side abnormal-condition resilience. The service process must never exit
        //      or stop responding because of a transient accept-loop fault, a failed repo
        //      reload, a FileSystemWatcher error, or overlapping source-change events — and
        //      every failure must be visible in /api/health and the persistent event log.
        //      (No real Windows service is registered, started or broken here.)
        // ---------------------------------------------------------------------------

        // Server config that also watches a driver source dir (used by the watcher tests).
        private static Env StartEnvWithSource(string repoDir, string sourceDir)
        {
            int port = GetFreePort();
            var cfg = new LanServerConfig { Port = port, RepoRoot = repoDir ?? "", Sources = new List<string> { sourceDir },
                OsArchitecture = "amd64", OsMajorVersion = 10, Host = "localhost" };
            var server = new LanDriverServer(cfg, _log);
            server.ImportSignatureVerifier = new TrustingSignatureVerifier();
            server.TestWatchDebounceMs = 200;
            server.Start();
            var env = new Env { Server = server, Url = "http://localhost:" + port.ToString(CultureInfo.InvariantCulture) };
            for (int i = 0; i < 40; i++)
            {
                try { var r = HttpGet(env.Url + "/api/health", null); if (r.Item1 == 200) return env; }
                catch { }
                Thread.Sleep(25);
            }
            return env;
        }

        // A transient exception inside the accept loop must not kill the server: the loop recovers
        // with bounded backoff and keeps serving; /api/health reports the listener up + last error.
        private static void TestAcceptLoopRecovers(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "res-accept"));
            string src = Path.Combine(dir, "PkgA"); Directory.CreateDirectory(src);
            WriteInf(src, "dev.inf", "PCI\\VEN_F000&DEV_F000", "03/14/2026", "2.0.0.0", "NTamd64.10");
            string repo = MakeRepo(Path.Combine(dir, "repo"), src);
            using (var env = StartEnv(repo, null, null))
            {
                env.Server.TestAcceptFault = () => new InvalidOperationException("transient accept fault");
                // The hook fires between GetContext calls, so this first request both gets served and
                // lets the loop return to the hook; the fault then fires and recovery runs.
                T(HttpGet(env.Url + "/api/health", null).Item1 == 200, "server responds around the fault window");
                var r = Tuple.Create(0, "");
                for (int i = 0; i < 60; i++)
                {
                    r = HttpGet(env.Url + "/api/health", null);
                    var hh = new JavaScriptSerializer().Deserialize<LanHealth>(r.Item2);
                    if (r.Item1 == 200 && hh != null && hh.LastError.IndexOf("accept loop", StringComparison.OrdinalIgnoreCase) >= 0) break;
                    Thread.Sleep(50);
                }
                T(r.Item1 == 200, "server still serving after accept-loop fault, status=" + r.Item1);
                var h = new JavaScriptSerializer().Deserialize<LanHealth>(r.Item2);
                T(h != null && h.ListenerRunning, "health reports listener running after recovery");
                T(h != null && h.LastError.IndexOf("accept loop", StringComparison.OrdinalIgnoreCase) >= 0,
                    "health carries the recorded accept-loop error, lastError=" + (h != null ? h.LastError : "<null>"));
                // And the server still serves real traffic after the fault.
                var req = new LanReportRequest { Protocol = LanProtocol.Version, RequestId = "res-accept-1", OsArchitecture = "amd64", OsMajorVersion = 10,
                    Devices = new List<LanDeviceDto> { new LanDeviceDto { DeviceInstanceId = "D", HardwareIds = new List<string> { "PCI\\VEN_F000&DEV_F000" } } } };
                T(HttpPost(env.Url + "/api/report", J(req), null).Item1 == 200, "report still works after accept-loop recovery");
            }
        }

        // A failed repo reload (corrupt manifest) keeps the previous usable repo: the server keeps
        // serving the old index, the process stays up, and the failure is visible in health.
        private static void TestRepoReloadKeepsOldRepo(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "res-reload"));
            string src = Path.Combine(dir, "PkgA"); Directory.CreateDirectory(src);
            WriteInf(src, "dev.inf", "PCI\\VEN_F100&DEV_F100", "03/14/2026", "2.0.0.0", "NTamd64.10");
            string repo = MakeRepo(Path.Combine(dir, "repo"), src);
            using (var env = StartEnv(repo, null, null))
            {
                var req1 = new LanReportRequest { Protocol = LanProtocol.Version, RequestId = "res-reload-1", OsArchitecture = "amd64", OsMajorVersion = 10,
                    Devices = new List<LanDeviceDto> { new LanDeviceDto { DeviceInstanceId = "D", HardwareIds = new List<string> { "PCI\\VEN_F100&DEV_F100" } } } };
                T(HttpPost(env.Url + "/api/report", J(req1), null).Item1 == 200, "baseline report works");
                // Corrupt the manifest so any reload attempt throws, then force a reload.
                string manifestPath = Path.Combine(repo, "repo.manifest.json");
                string original = File.ReadAllText(manifestPath, Encoding.UTF8);
                File.WriteAllText(manifestPath, "{ THIS IS NOT VALID JSON", new UTF8Encoding(false));
                try
                {
                    env.Server.LoadRepo();   // must not throw, must not blank the repo
                    var h = new JavaScriptSerializer().Deserialize<LanHealth>(HttpGet(env.Url + "/api/health", null).Item2);
                    T(h.RepoLoaded, "repo still loaded after failed reload");
                    T(h.InfCount > 0, "old repo still has entries, infCount=" + h.InfCount);
                    T(h.LastError.IndexOf("repo load failed", StringComparison.OrdinalIgnoreCase) >= 0,
                        "health records the reload failure, lastError=" + h.LastError);
                    var req2 = new LanReportRequest { Protocol = LanProtocol.Version, RequestId = "res-reload-2", OsArchitecture = "amd64", OsMajorVersion = 10,
                        Devices = req1.Devices };
                    var resp = new JavaScriptSerializer().Deserialize<LanReportResponse>(HttpPost(env.Url + "/api/report", J(req2), null).Item2);
                    T(resp != null && resp.Candidates.Count == 1, "old repo still serves matches after failed reload");
                }
                finally { File.WriteAllText(manifestPath, original, new UTF8Encoding(false)); }
            }
        }

        // A FileSystemWatcher error (buffer overflow / dead watcher) is recorded and triggers ONE
        // controlled full rescan through the debounce + single-flight path; the watcher is recreated.
        private static void TestWatcherErrorTriggersRescan(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "res-watcher"));
            string src = Path.Combine(dir, "Src"); Directory.CreateDirectory(src);
            WriteInf(src, "dev.inf", "PCI\\VEN_F200&DEV_F200", "03/14/2026", "2.0.0.0", "NTamd64.10");
            string repo = MakeRepo(Path.Combine(dir, "repo"), src);
            using (var env = StartEnvWithSource(repo, src))
            {
                T(env.Server.WatcherCount >= 1, "watcher active for the source dir, count=" + env.Server.WatcherCount);
                env.Server.TestRaiseWatcherError();
                // Exactly one single-flight rescan completes; stage walks pending -> ... -> completed.
                bool completed = false;
                for (int i = 0; i < 200 && !completed; i++)
                {
                    if (string.Equals(env.Server.CurrentImportStage, "completed", StringComparison.OrdinalIgnoreCase)) completed = true;
                    else Thread.Sleep(50);
                }
                T(completed, "watcher error scheduled a controlled full rescan that completed, stage=" + env.Server.CurrentImportStage);
                T(env.Server.ImportRunsStarted == 1, "exactly one rescan for one watcher error, runs=" + env.Server.ImportRunsStarted);
                T(env.Server.WatcherCount >= 1, "watcher recreated after error, count=" + env.Server.WatcherCount);
                var h = new JavaScriptSerializer().Deserialize<LanHealth>(HttpGet(env.Url + "/api/health", null).Item2);
                T(h.LastError.IndexOf("watcher error", StringComparison.OrdinalIgnoreCase) >= 0, "health records the watcher error");
                T(!h.ImportRunning, "no import still running after rescan completed");
            }
        }

        // A burst of concurrent source-change events collapses into exactly one single-flight rescan;
        // events arriving while a rescan runs are coalesced into at most one follow-up, never a storm.
        private static void TestWatchEventsSingleFlight(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "res-storm"));
            string src = Path.Combine(dir, "Src"); Directory.CreateDirectory(src);
            WriteInf(src, "dev.inf", "PCI\\VEN_F300&DEV_F300", "03/14/2026", "2.0.0.0", "NTamd64.10");
            string repo = MakeRepo(Path.Combine(dir, "repo"), src);
            using (var env = StartEnvWithSource(repo, src))
            {
                // Burst: many change signals inside one debounce window -> one rescan.
                for (int i = 0; i < 10; i++) env.Server.TestSignalSourceChange();
                bool completed = false;
                for (int i = 0; i < 200 && !completed; i++)
                {
                    if (string.Equals(env.Server.CurrentImportStage, "completed", StringComparison.OrdinalIgnoreCase)) completed = true;
                    else Thread.Sleep(50);
                }
                T(completed, "burst produced a completed rescan, stage=" + env.Server.CurrentImportStage);
                int firstRuns = env.Server.ImportRunsStarted;
                T(firstRuns == 1, "10 concurrent events collapsed into 1 single-flight rescan, runs=" + firstRuns);
                // Second burst AFTER the run finished -> exactly one more rescan (no storm accumulation).
                for (int i = 0; i < 5; i++) env.Server.TestSignalSourceChange();
                completed = false;
                for (int i = 0; i < 200 && !completed; i++)
                {
                    if (env.Server.ImportRunsStarted >= firstRuns + 1 &&
                        string.Equals(env.Server.CurrentImportStage, "completed", StringComparison.OrdinalIgnoreCase)) completed = true;
                    else Thread.Sleep(50);
                }
                T(completed, "second burst completed, stage=" + env.Server.CurrentImportStage);
                T(env.Server.ImportRunsStarted == firstRuns + 1, "second burst also collapsed into exactly 1 rescan, runs=" + env.Server.ImportRunsStarted);
            }
        }

        // Stop deterministically releases the listener, watcher, timer and worker threads, is
        // idempotent (double Stop/Dispose safe), and the accept loop thread actually exits.
        private static void TestStopReleasesResources(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "res-stop"));
            string src = Path.Combine(dir, "Src"); Directory.CreateDirectory(src);
            WriteInf(src, "dev.inf", "PCI\\VEN_F400&DEV_F400", "03/14/2026", "2.0.0.0", "NTamd64.10");
            string repo = MakeRepo(Path.Combine(dir, "repo"), src);
            var env = StartEnvWithSource(repo, src);
            try
            {
                T(env.Server.IsListening, "server listening before stop");
                T(env.Server.AcceptLoopAlive, "accept loop alive before stop");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                env.Server.Stop();
                sw.Stop();
                T(sw.ElapsedMilliseconds < 8000, "Stop returned promptly, ms=" + sw.ElapsedMilliseconds);
                T(!env.Server.IsListening, "listener released after stop");
                T(!env.Server.AcceptLoopAlive, "accept loop thread exited after stop");
                T(env.Server.WatcherCount == 0, "watchers disposed after stop, count=" + env.Server.WatcherCount);
                // Double stop must be safe (idempotent), and further Dispose must not throw.
                env.Server.Stop();
                env.Server.Dispose();
            }
            finally { try { env.Server.Dispose(); } catch { } }
            // After stop the endpoint no longer accepts connections.
            T(HttpGet(env.Url + "/api/health", null).Item1 == 0, "no listener answers after stop");
        }

        // Server failures are persisted to the service event log (crash/start/stop diagnosability).
        private static void TestErrorLogPersisted(string root)
        {
            string path = ServiceEventLog.LogFilePath;
            T(!string.IsNullOrEmpty(path) && File.Exists(path), "event log file exists: " + path);
            string content = File.ReadAllText(path, Encoding.UTF8);
            T(content.IndexOf("accept loop", StringComparison.OrdinalIgnoreCase) >= 0, "accept-loop failure persisted");
            T(content.IndexOf("watcher error", StringComparison.OrdinalIgnoreCase) >= 0, "watcher failure persisted");
            T(content.IndexOf("repo load failed", StringComparison.OrdinalIgnoreCase) >= 0, "repo reload failure persisted");
            T(content.IndexOf("服务事件日志已初始化", StringComparison.Ordinal) >= 0, "service lifecycle events persisted");
            // The ProgramData default path is used when Initialize was never called with a test dir
            // (product mode) — verify the computed default, without writing to it.
            string expectedDefaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UniversalDriverAssistant", "Logs");
            T(expectedDefaultDir.Length > 0, "ProgramData default log dir resolvable: " + expectedDefaultDir);
        }

        // ---------------------------------------------------------------------------
        //  Driver library duplicate audit/cleanup over the live admin API. The fixture hand-writes
        //  an exact duplicate into the repo (the way a pre-dedupe legacy repo carries one), then:
        //  unauthorized audit -> 401, audit names the group, cleanup removes it and hot-reloads,
        //  the kept driver still serves candidates, and a re-run is a no-op.
        // ---------------------------------------------------------------------------
        private static void TestAdminDedupe(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "admindedupe"));
            string src = Path.Combine(dir, "src"); Directory.CreateDirectory(src);
            WriteInf(src, "net.inf", "PCI\\VEN_B000&DEV_B000", "03/14/2026", "2.0.0.0", "NTamd64.10");
            string repo = MakeRepo(Path.Combine(dir, "repo"), src);

            // Inject an exact duplicate of the only INF at a second path (legacy-repo simulation).
            OfflineRepoManifest manifest = OfflineRepoGenerator.TryLoadManifest(repo);
            T(manifest != null && manifest.Entries.Count == 1, "fixture repo has 1 entry");
            OfflineRepoEntry original = manifest.Entries[0];
            string dupeRel = original.RelativePath.Replace(".inf", ".dupe.inf");
            string dupeAbs = Path.Combine(repo, dupeRel.Replace('/', Path.DirectorySeparatorChar));
            string dupeDir = Path.GetDirectoryName(dupeAbs);
            if (!Directory.Exists(dupeDir)) Directory.CreateDirectory(dupeDir);
            File.Copy(Path.Combine(repo, original.RelativePath.Replace('/', Path.DirectorySeparatorChar)), dupeAbs, true);
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
            string dbPath = Path.Combine(repo, "repo.udadb");
            var idx = new DriverIndex(dbPath);
            idx.ReplaceAll(manifest.Entries.Select(OfflineRepoGenerator.ToPortableRecord));
            idx.Commit();
            manifest.IndexDbHash = Sha256FileLan(dbPath);
            manifest.IntegrityHash = OfflineRepoGenerator.ComputeIntegrityHash(manifest);
            File.WriteAllText(Path.Combine(repo, "repo.manifest.json"), OfflineRepoGenerator.SerializeManifest(manifest), new UTF8Encoding(false));

            using (var env = StartEnv(repo, "dedupe-token", null))
            {
                // Auth gate: no token -> 401 for both audit and cleanup.
                T(HttpGet(env.Url + "/api/admin/duplicates", null).Item1 == 401, "audit no token -> 401");
                T(HttpPost(env.Url + "/api/admin/dedupe", "", null).Item1 == 401, "dedupe no token -> 401");

                // Audit names the duplicate group.
                var audit = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(HttpGet(env.Url + "/api/admin/duplicates", "dedupe-token").Item2);
                T(audit != null && Convert.ToInt32(audit["duplicateCount"], CultureInfo.InvariantCulture) == 1, "audit duplicateCount=1");
                T(Convert.ToInt32(audit["total"], CultureInfo.InvariantCulture) == 2 && Convert.ToInt32(audit["kept"], CultureInfo.InvariantCulture) == 1, "audit total=2 kept=1");

                // The duplicate does not double the candidates: matching returns exactly one.
                var req = new LanReportRequest { Protocol = LanProtocol.Version, RequestId = "dedupe-1", OsArchitecture = "amd64", OsMajorVersion = 10,
                    Devices = new List<LanDeviceDto> { new LanDeviceDto { DeviceInstanceId = "D", HardwareIds = new List<string> { "PCI\\VEN_B000&DEV_B000" } } } };
                var before = new JavaScriptSerializer().Deserialize<LanReportResponse>(HttpPost(env.Url + "/api/report", J(req), "dedupe-token").Item2);
                T(before != null && before.Candidates.Count == 1, "match before cleanup candidates=" + (before != null ? before.Candidates.Count : -1));

                // Token-only legacy mode cleans synchronously and hot-reloads.
                var clean = HttpPost(env.Url + "/api/admin/dedupe", "", "dedupe-token");
                T(clean.Item1 == 200, "dedupe with token -> 200, got " + clean.Item1 + ": " + clean.Item2);
                var cleanObj = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(clean.Item2);
                T(cleanObj != null && Convert.ToInt32(cleanObj["removed"], CultureInfo.InvariantCulture) == 1, "dedupe removed=1");
                T(!File.Exists(dupeAbs), "duplicate file deleted on the live server");

                // The kept driver still serves, and the audit now reports a clean library.
                var afterReq = new LanReportRequest { Protocol = LanProtocol.Version, RequestId = "dedupe-2", OsArchitecture = "amd64", OsMajorVersion = 10,
                    Devices = new List<LanDeviceDto> { new LanDeviceDto { DeviceInstanceId = "D", HardwareIds = new List<string> { "PCI\\VEN_B000&DEV_B000" } } } };
                var after = new JavaScriptSerializer().Deserialize<LanReportResponse>(HttpPost(env.Url + "/api/report", J(afterReq), "dedupe-token").Item2);
                T(after != null && after.Candidates.Count == 1 && after.Candidates[0].DriverVerVersion == "2.0.0.0", "kept driver still serves after cleanup");
                var audit2 = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(HttpGet(env.Url + "/api/admin/duplicates", "dedupe-token").Item2);
                T(Convert.ToInt32(audit2["duplicateCount"], CultureInfo.InvariantCulture) == 0, "audit after cleanup duplicateCount=0");

                // Idempotent re-run: 200, nothing removed, repo still healthy.
                var again = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(HttpPost(env.Url + "/api/admin/dedupe", "", "dedupe-token").Item2);
                T(again != null && Convert.ToInt32(again["removed"], CultureInfo.InvariantCulture) == 0, "second dedupe removed=0");
                var health = HttpGet(env.Url + "/api/health", null);
                var h = new JavaScriptSerializer().Deserialize<LanHealth>(health.Item2);
                T(h != null && h.RepoLoaded && h.InfCount == 1, "health after dedupe repoLoaded=" + (h != null ? h.RepoLoaded : false) + " inf=" + (h != null ? h.InfCount : -1));
            }
        }

        private static string Sha256FileLan(string path)
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
    }
}
