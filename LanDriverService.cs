// LanDriverService.cs - Phase 3B: self-hosted LAN driver service + client.
//
// A pure .NET Framework (System.Net.HttpListener) self-hosted driver service for Windows Server, plus the
// matching client that bootstraps a missing NIC driver from a local offline repo, then reports the target
// machine's hardware inventory to the server, downloads server-matched INF packages, verifies their SHA-256
// and signature, and installs them. No third-party dependencies; compiled by Build.ps1. C# 5.0 syntax.
//
// Protocol (version 1, JSON over HTTP):
//   GET  /api/health            - liveness + repo stats (no token, no version check).
//   POST /api/report            - body: LanReportRequest  -> LanReportResponse (matched candidates).
//   GET  /api/download?path=rel - the package directory containing the INF, as a ZIP; X-UDA-Inf-Hash header.
//   POST /api/admin/import?src= - regenerate/reload the repo from a server-side allowlisted source dir.
//   GET  /api/admin/duplicates  - read-only audit: strict-duplicate groups in the loaded repo.
//   POST /api/admin/dedupe      - clean exact duplicates in place (keep first per group), then hot-reload.
//
// Safety guarantees:
//   * Protocol versioning: every report/download/import request must carry X-UDA-Protocol: 1.
//   * Request size limit: bodies larger than MaxRequestBytes are rejected (413) before reading.
//   * Timeouts: server TimeoutManager + client WebRequest timeouts; both honour a CancellationToken.
//   * Idempotency: report results are cached by RequestId; a duplicate request returns the same response.
//   * Path traversal: every client-supplied path is resolved under the repo root (or an import allow-root)
//     and rejected if it escapes (no "..", no absolute/UNC leakage).
//   * Optional LAN API token: if configured it is required on report/download/import; compared in constant
//     time and NEVER written to logs.
//   * The server never executes commands and never installs drivers; it only serves files it indexed. The
//     only pnputil invocation is client-side, on files the client downloaded, verified, and extracted into a
//     client-controlled staging directory.
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace UDA
{
    internal static class ClientScanParallelism
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetActiveProcessorCount(ushort group);
        internal static Func<int> TestProcessorCount;

        internal static int LogicalProcessors()
        {
            if (TestProcessorCount != null) return Math.Max(1, TestProcessorCount());
            try { uint count = GetActiveProcessorCount(0xffff); if (count > 0 && count <= int.MaxValue) return (int)count; }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
            return Math.Max(1, Environment.ProcessorCount);
        }

        internal static int Workers(int processors, int jobs) { return Math.Max(1, Math.Min(Math.Max(1, processors), Math.Max(1, jobs))); }

        internal static void Run<T>(IList<T> jobs, int processors, CancellationToken ct, Action<T, CancellationToken> action)
        {
            ct.ThrowIfCancellationRequested();
            if (jobs.Count == 0) return;
            int workers = Workers(processors, jobs.Count), minimum, completionPorts;
            ThreadPool.GetMinThreads(out minimum, out completionPorts);
            if (minimum < workers) ThreadPool.SetMinThreads(workers, completionPorts);
            using (var stop = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                Exception failure = null;
                try
                {
                    Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = Workers(processors, jobs.Count), CancellationToken = stop.Token }, job =>
                    {
                        try { stop.Token.ThrowIfCancellationRequested(); action(job, stop.Token); }
                        catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); stop.Cancel(); throw; }
                    });
                }
                catch
                {
                    ct.ThrowIfCancellationRequested();
                    if (failure != null) throw new InvalidOperationException(failure.Message, failure);
                    throw;
                }
            }
        }
    }

    // =================================================================================
    //  Protocol DTOs
    // =================================================================================

    public sealed class LanDeviceDto
    {
        public string DeviceInstanceId = "";
        public string FriendlyName = "";
        public string Class = "";
        public string ClassGuid = "";
        public List<string> HardwareIds = new List<string>();
        public List<string> CompatibleIds = new List<string>();
        public string CurrentInf = "";
        public string CurrentVersion = "";
        public bool Bad;
    }

    public sealed class LanReportRequest
    {
        public bool SupportsExtensionPackages;
        public int Protocol = LanProtocol.Version;
        public string RequestId = "";
        public string MachineName = Environment.MachineName;
        public string MachineManufacturer = "";
        public string MachineModel = "";
        public string MachineSku = "";
        public string OsArchitecture = "amd64";
        public int OsMajorVersion = 10;
        public int OsMinorVersion = -1; // Optional v1 field; old NT 6.x clients are inferred from build.
        // Optional (protocol v1 compatible): the client's exact Windows build number. When >0 the
        // server feeds it into InfApplicability so decorations with a minimum build (NTamd64.10.0...22000)
        // are enforced, not just architecture/major. Old clients omit it and the build gate is skipped.
        public int OsBuildNumber;
        public string Token = "";
        public List<LanDeviceDto> Devices = new List<LanDeviceDto>();
    }

    /// <summary>A device that got NO applicable candidate. Reported explicitly so the client UI can
    /// show "没有匹配驱动" instead of silently hiding the device. Optional field: absent on old servers.</summary>
    public sealed class LanUnmatchedDeviceDto
    {
        public string DeviceInstanceId = "";
        public string FriendlyName = "";
        /// <summary>Machine-readable reason: NoMatchingDriver, or the InfApplicability rejection code
        /// (ArchitectureMismatch / OsVersionTooLow / BuildTooLow / NoMatchingModelEntry / ...).</summary>
        public string ReasonCode = "";
        /// <summary>Human-readable (bilingual) reason.</summary>
        public string Reason = "";
    }

    public sealed class LanCandidateDto
    {
        public string ExtensionId = "";
        public bool IsExtension;
        public string DeviceInstanceId = "";
        public string DownloadBaseUrl = "";
        public string FriendlyName = "";
        public string InfRelativePath = "";
        public string PackageInfPath = "";
        public string PackageId = "";
        public string ContentHash = "";
        public string SignatureStatus = "";
        public string Provider = "";
        public string Class = "";
        public string ClassGuid = "";
        public string DriverVerDate = "";
        public string DriverVerVersion = "";
        public string MatchedId = "";
        public int Tier;
        // Optional (protocol v1 compatible, empty/0 on old servers): InfApplicability verdict details.
        // MatchLevel mirrors InfMatchLevel (4 = exact hardware ID ... 1 = compatible ID); MatchedInfId is
        // the concrete INF hardware/compatible ID that matched; NeedInstall is the server-side install
        // policy verdict (install or update = true, already up to date = false).
        public int MatchLevel;
        public string MatchedInfId = "";
        public bool NeedInstall;
        public string OemBrand = "", MachineBrand = "";
        // Optional flat alternatives; old clients ignore these fields. Model packages remain atomic.
        public string ReleaseChannel = "";
        public List<LanCandidateDto> Versions = new List<LanCandidateDto>();
        [System.Web.Script.Serialization.ScriptIgnore]
        public bool UserSelectedVersion;
        public string LocalRejectionReason = "";
        // Decorated Models sections of the matched INF (e.g. "NTamd64.10"). Lets the client re-run the
        // SAME OS/arch applicability rule the server used, so a candidate shown as installable can never
        // be skipped at install time for an applicability reason. Empty on old servers = trust the server.
        public List<string> Decorations = new List<string>();
    }

    public static class LanExtensionPackages
    {
        private static readonly ConcurrentDictionary<string, Lazy<InfRecord>> LegacyModels = new ConcurrentDictionary<string, Lazy<InfRecord>>(StringComparer.OrdinalIgnoreCase);
        public static InfRecord CompleteRepositoryModels(OfflineRepo repo, InfRecord record)
        {
            if (record == null || (record.ModelSections != null && record.ModelSections.Count > 0) || record.Models == null || record.Models.Count == 0) return record;
            string key = repo.RepoRoot + "|" + record.Path + "|" + record.ContentHash;
            try {
                return LegacyModels.GetOrAdd(key, k => new Lazy<InfRecord>(() => ReadRepositoryRecord(repo, record), true)).Value;
            } catch { Lazy<InfRecord> failed; LegacyModels.TryRemove(key, out failed); throw; }
        }
        private static readonly ConcurrentDictionary<string, string> Ids = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static bool IsIntelSgpcPlatformExtension(InfRecord record, Guid extensionId)
        {
            // The signed Intel HdBusExt lineage configures the PCI audio controller,
            // not OEM speaker calibration. Its deliberate VEN/DEV-only targets are generic.
            if (record == null || extensionId != new Guid("69CB4EDD-BAFE-4F9A-9135-C1F284D6860D") ||
                (record.Provider ?? "").IndexOf("Intel", StringComparison.OrdinalIgnoreCase) < 0) return false;
            var ids = (record.HardwareIds ?? new List<string>()).Concat(record.CompatibleIds ?? new List<string>()).ToList();
            return ids.Count > 0 && ids.All(id => System.Text.RegularExpressions.Regex.IsMatch(id ?? "", @"^PCI\\VEN_8086&DEV_[0-9A-F]{4}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        }
        public static bool MatchesExtensionTarget(InfRecord record, Guid extensionId, InfApplicabilityResult verdict)
        {
            if (verdict == null || !verdict.Applicable) return false;
            if (verdict.MatchLevel == InfMatchLevel.HardwareId) return true;
            // PCI VEN/DEV is normally in CompatibleIds, not the subsystem-specific HardwareIds.
            // Only this known generic controller lineage may use that match; OEM calibration stays exact.
            return verdict.MatchLevel == InfMatchLevel.CompatibleIdVsHardwareId &&
                IsIntelSgpcPlatformExtension(record, extensionId) &&
                System.Text.RegularExpressions.Regex.IsMatch(verdict.MatchedInfId ?? "", @"^PCI\\VEN_8086&DEV_[0-9A-F]{4}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
                string.Equals(verdict.MatchedDeviceId, verdict.MatchedInfId, StringComparison.OrdinalIgnoreCase);
        }
        public static bool IsExtension(InfRecord record)
        { return record != null && (string.Equals(record.Class, "Extension", StringComparison.OrdinalIgnoreCase) || string.Equals(record.ClassGuid, "{e2f84ce7-8efa-411c-aa69-97454ca4cb57}", StringComparison.OrdinalIgnoreCase)); }
        public static string RepositoryId(OfflineRepo repo, InfRecord record)
        {
            if (!IsExtension(record)) return "";
            Guid id;
            if (Guid.TryParse(record.ExtensionId, out id)) return id.ToString("B");
            string cached;
            if (Ids.TryGetValue(record.ContentHash, out cached)) return cached;
            cached = ReadRepositoryRecord(repo, record).ExtensionId ?? "";
            Ids.TryAdd(record.ContentHash, cached);
            return cached;
        }
        private static InfRecord ReadRepositoryRecord(OfflineRepo repo, InfRecord record)
        {
            var entry = repo.EntryForRelative(record.Path);
            if (entry == null) throw new InvalidDataException("仓库 INF 元数据不存在：" + record.Path);
            InfRecord actual;
            string local = LanSecurity.SafeAbsolute(repo.RepoRoot, entry.RelativePath);
            if (local != null && File.Exists(local)) actual = InfParser.Parse(local, entry.SourceName);
            else
            {
                string relative = string.IsNullOrEmpty(entry.PackageRoot) ? Path.GetFileName(entry.RelativePath) : entry.RelativePath.Substring(entry.PackageRoot.Length + 1);
                using (var zip = ZipFile.OpenRead(OfflineRepoGenerator.DownloadArchivePath(repo.RepoRoot, entry)))
                {
                    var member = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName.Replace('\\', '/'), relative.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
                    if (member == null || member.Length > 16 * 1024 * 1024) throw new InvalidDataException("INF 归档内容缺失或过大：" + entry.RelativePath);
                    using (var input = member.Open()) using (var bytes = new MemoryStream()) { input.CopyTo(bytes); actual = InfParser.ParseBytes(bytes.ToArray(), local, entry.SourceName); }
                }
            }
            if (!string.Equals(actual.ContentHash, entry.ContentHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("INF 哈希校验失败：" + entry.RelativePath);
            actual.Path = record.Path; actual.SourceName = record.SourceName;
            return actual;
        }
        public static bool Satisfied(LanCandidateDto candidate, D device, OsTarget target, DriverScanFiles files = null)
        {
            if (candidate == null || !candidate.IsExtension || device == null) return false;
            var paths = CurrentDriverState.ExtensionInfPaths(device.Id);
            if (paths == null) return false;
            foreach (string path in paths)
            {
                try
                {
                    var actual = files == null ? InfParser.Parse(path, "installed") : files.Parse(path, "installed");
                    if (!IsExtension(actual) || !string.Equals(actual.ExtensionId, candidate.ExtensionId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!InfApplicability.Evaluate(actual, target, device.H, device.K).Applicable) continue;
                    if (string.Equals(actual.ContentHash, candidate.ContentHash, StringComparison.OrdinalIgnoreCase)) return true;
                    DateTime date;
                    InfParser.TryParseDriverDate(candidate.DriverVerDate, out date);
                    if (actual.Date() > date || (actual.Date() == date && !DriverVersion.IsNewer(candidate.DriverVerVersion, actual.DriverVerVersion))) return true;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return false;
        }
        public static bool HasAppliedLineage(LanCandidateDto candidate, D device)
        {
            if (device == null) return false;
            foreach (string path in CurrentDriverState.ExtensionInfPaths(device.Id) ?? new List<string>())
                try { if (string.Equals(InfParser.Parse(path, "installed").ExtensionId, candidate.ExtensionId, StringComparison.OrdinalIgnoreCase)) return true; }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            return false;
        }
    }

    public sealed class LanReportResponse
    {
        public int Protocol = LanProtocol.Version;
        public string ServerVersion = LanProtocol.ServerVersion;
        public bool Ok = true;
        public string Error = "";
        public bool Cached;
        public List<LanCandidateDto> Candidates = new List<LanCandidateDto>();
        // Optional (protocol v1 compatible): devices with no applicable candidate. The client UI shows
        // these as "没有匹配驱动" instead of silently dropping them. Empty on old servers.
        public List<LanUnmatchedDeviceDto> UnmatchedDevices = new List<LanUnmatchedDeviceDto>();
    }

    public sealed class LanHealth
    {
        public string RepositoryUpdatedUtc = "";
        public int Protocol = LanProtocol.Version;
        public string ServerVersion = LanProtocol.ServerVersion;
        public bool RepoLoaded;
        public int InfCount;
        public string OsArchitecture = "";
        public int OsMajorVersion;
        public bool TokenRequired;
        // Optional (protocol v1 compatible): operational diagnostics. Old clients ignore unknown
        // fields; /api/health must let an operator see WHY the service is degraded, not just that
        // it answers. ListenerRunning=false or a non-empty LastError means the process is alive
        // but something failed and was recorded, never silently swallowed.
        public bool ListenerRunning;
        public int WatcherCount;
        public bool ImportRunning;
        public bool MaintenanceRunning;
        public string ImportStage = "";
        public string LastError = "";
        public string LastErrorUtc = "";
        public string RepoRetryUtc = "";
    }

    /// <summary>
    /// Persistent service event log under %ProgramData%\UniversalDriverAssistant\Logs\service-events.log.
    /// Records service start/stop, crash causes, listener/port failures, repo reload failures, watcher
    /// errors and globally unhandled exceptions so an abnormal service exit or hang is always diagnosable
    /// after the fact. Every write is guarded: logging must never be able to take the service down.
    /// </summary>
    public static class ServiceEventLog
    {
        private static readonly object Gate = new object();
        private static string _file;

        /// <summary>Path of the current event log file; null until initialized or lazily created.</summary>
        public static string LogFilePath { get { lock (Gate) return _file; } }
        internal static void Maintain(Action action) { lock (Gate) action(); }

        /// <summary>Initialize the log directory (test isolation or explicit --logdir). Falls back to
        /// the ProgramData default when empty. Safe to call repeatedly; never throws.</summary>
        public static void Initialize(string dir)
        {
            try
            {
                string d = Environment.ExpandEnvironmentVariables(dir ?? "");
                if (string.IsNullOrWhiteSpace(d))
                    d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UniversalDriverAssistant", "Logs");
                Directory.CreateDirectory(d);
                lock (Gate) { _file = Path.Combine(d, "service-events.log"); }
                Write("event", "服务事件日志已初始化 / service event log initialized");
            }
            catch { }
        }

        /// <summary>Register process-wide last-resort exception handlers. Unhandled exceptions and
        /// unobserved task exceptions are persisted before the CLR terminates (or forgets) them, so a
        /// crash never happens without a written cause. Unobserved exceptions are marked observed —
        /// they are recorded, but must not themselves terminate the process.</summary>
        public static void InstallGlobalHandlers()
        {
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                var ex = e.ExceptionObject as Exception;
                Write("fatal", "全局未处理异常 / UnhandledException (terminating=" + e.IsTerminating + "): " + (ex != null ? ex.ToString() : Convert.ToString(e.ExceptionObject, CultureInfo.InvariantCulture)));
            };
            TaskScheduler.UnobservedTaskException += delegate(object sender, UnobservedTaskExceptionEventArgs e)
            {
                Write("error", "未观察的任务异常 / UnobservedTaskException: " + e.Exception);
                e.SetObserved();   // record, never let GC-timed finalization kill the service
            };
        }

        /// <summary>Append one timestamped line. Lazily defaults to the ProgramData log directory so
        /// product code records events even when Initialize was never called. Never throws.</summary>
        public static void Write(string category, string message)
        {
            try
            {
                string file;
                lock (Gate)
                {
                    if (string.IsNullOrEmpty(_file))
                    {
                        string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UniversalDriverAssistant", "Logs");
                        Directory.CreateDirectory(d);
                        _file = Path.Combine(d, "service-events.log");
                    }
                    file = _file;
                }
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                    " [" + (category ?? "event") + "] " + (message ?? "");
                lock (Gate) File.AppendAllText(file, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }
    }

    public static class LanProtocol
    {
        public const int Version = 2;
        public const string ServerVersion = "5.3.45";
        public const string HeaderProtocol = "X-UDA-Protocol";
        public const string HeaderToken = "X-UDA-Token";
        public const string HeaderInfHash = "X-UDA-Inf-Hash";
        public const string HeaderRequestId = "X-UDA-Request-Id";
    }

    public static class LanVersion
    {
        /// <summary>Numeric component-wise comparison, shared with the offline engine (DriverVersion),
        /// so the scan stage and the install stage always compare versions the same way.</summary>
        public static bool IsNewer(string candidate, string current)
        {
            return DriverVersion.IsNewer(candidate, current);
        }
    }

    /// <summary>
    /// The install policy shared by the scan display (BuildLanScanResult), the ShouldInstall gate and the
    /// pre-install revalidation. Because every path calls into this one class, the three results — 安装驱动,
    /// 更新驱动, 没有匹配驱动 / 已是最新 — agree between what the scan shows and what the installer does.
    /// </summary>
    public static class LanInstallPolicy
    {
        /// <summary>install = device missing/broken driver (安装驱动); update = older installed driver
        /// (更新驱动); skip = same or newer version already in effect (已是最新).</summary>
        public enum Decision { Install, Update, Skip }

        /// <summary>Classify a candidate against the device's currently installed driver. A device that is
        /// Bad, has no in-effect INF, or has an unknown version is always an INSTALL — a historical receipt
        /// or a missing version must never hide a genuinely needed driver.</summary>
        public static Decision Classify(D d, string candidateVersion)
        {
            if (d == null) return Decision.Install;
            if (d.Bad || string.IsNullOrEmpty(d.Inf) || string.IsNullOrEmpty(d.Version)) return Decision.Install;
            return DriverVersion.IsNewer(candidateVersion, d.Version) ? Decision.Update : Decision.Skip;
        }

        /// <summary>Pre-install revalidation. Returns null when the candidate is still installable right
        /// now; otherwise a human-readable reason (shown in the log instead of a bare "skipped" count).
        /// Uses the SAME applicability engine as the scan (InfApplicability), so a revalidation skip can
        /// only come from a real race (device changed, driver installed in between), never from a rule
        /// mismatch between the two stages.</summary>
        public static string Revalidate(LanCandidateDto cand, D d, OsTarget target, bool verifySignature)
        {
            if (cand == null) return "候选为空 / candidate is null";
            if (string.IsNullOrEmpty(cand.ContentHash)) return "候选缺少内容哈希 / candidate has no content hash";
            if (verifySignature && string.Equals(cand.SignatureStatus, "untrusted", StringComparison.OrdinalIgnoreCase))
                return "候选驱动签名不受信任 / candidate signature is untrusted";
            if (cand.Decorations != null && cand.Decorations.Count > 0)
            {
                var rec = new InfRecord { Decorations = cand.Decorations };
                var verdict = InfApplicability.Evaluate(rec, target);
                if (!verdict.Applicable)
                {
                    // A decorations-only record has no Models; when the engine can only say
                    // "NoModels", fall back to the readable decoration explanation.
                    if (verdict.ReasonCode == "NoModels")
                    {
                        string why = DriverApplicability.Explain(rec, target);
                        if (why != null) return why;
                    }
                    else return verdict.ReasonCode + " " + verdict.Reason;
                }
            }
            Decision decision = Classify(d, cand.DriverVerVersion);
            if (cand.IsExtension)
            {
                Guid id;
                if (d == null || !Guid.TryParse(cand.ExtensionId, out id) || target == null || target.MajorVersion < 10) return "扩展驱动缺少有效目标或 ExtensionId";
                return LanExtensionPackages.Satisfied(cand, d, target) ? "该扩展的同版或更高优先版本已应用于设备" : null;
            }
            if (decision == Decision.Skip && !cand.UserSelectedVersion)
            {
                string current = d != null ? d.Version : "";
                return "设备当前驱动已是同版或更高版本 / device already runs the same or a newer driver" +
                    " (current=" + (string.IsNullOrEmpty(current) ? "unknown" : current) +
                    ", candidate=" + (cand.DriverVerVersion ?? "") + ")";
            }
            return null;
        }
    }

    public static class LanInstallReceiptStore
    {
        private static readonly object Gate = new object();
        private static string _testOverridePath;                           // test isolation only; always null in product
        private static string ReceiptPath
        {
            get
            {
                if (_testOverridePath != null) return _testOverridePath;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UniversalDriverAssistant", "installed-candidates.txt");
            }
        }
        // Tests only: redirect receipts to a scratch file (cleared) so runs never leak into real user receipts.
        internal static void UseTestStore(string path)
        {
            lock (Gate) { _testOverridePath = path; try { File.Delete(path); } catch { } }
        }
        internal static void ResetToProductStore() { lock (Gate) { _testOverridePath = null; } }
        public static bool Contains(string deviceInstanceId, string contentHash)
        {
            string key = Key(deviceInstanceId, contentHash); if (string.IsNullOrEmpty(key)) return false;
            lock (Gate) { try { return File.Exists(ReceiptPath) && File.ReadAllLines(ReceiptPath).Any(x => string.Equals(x.Trim(), key, StringComparison.OrdinalIgnoreCase)); } catch { return false; } }
        }
        public static void Mark(string deviceInstanceId, string contentHash)
        {
            string key = Key(deviceInstanceId, contentHash); if (string.IsNullOrEmpty(key)) return;
            lock (Gate) { try { Directory.CreateDirectory(Path.GetDirectoryName(ReceiptPath)); var values = File.Exists(ReceiptPath) ? new HashSet<string>(File.ReadAllLines(ReceiptPath), StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase); if (values.Add(key)) File.WriteAllLines(ReceiptPath, values.OrderBy(x => x).ToArray(), new UTF8Encoding(false)); } catch { } }
        }
        private static string Key(string deviceInstanceId, string contentHash)
        {
            if (string.IsNullOrWhiteSpace(deviceInstanceId) || string.IsNullOrWhiteSpace(contentHash)) return "";
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(deviceInstanceId.Trim().ToUpperInvariant() + "|" + contentHash.Trim().ToUpperInvariant()))).Replace("-", "");
        }
    }

    /// <summary>
    /// Remembers a Windows-confirmed "already best/latest" result for one exact device, candidate
    /// package, current binding, and OS. SetupAPI's early single-INF probe can disagree with the full
    /// Driver Store ranking or pnputil on real machines; after either authoritative check rejects the
    /// candidate, do not keep offering the unchanged (device, package) pair as an update. Any binding,
    /// candidate, or OS change naturally invalidates the record.
    /// </summary>
    public static class LanPnpRejectionStore
    {
        private static readonly object Gate = new object();
        private static string _testPath;
        private static string StorePath
        {
            get { return _testPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UniversalDriverAssistant", "pnp-rejections.txt"); }
        }
        internal static void UseTestStore(string path)
        {
            lock (Gate) { _testPath = path; try { File.Delete(path); } catch { } }
        }
        internal static void ResetToProductStore() { lock (Gate) _testPath = null; }

        public static bool Contains(LanCandidateDto candidate, D device, OsTarget target)
        {
            string key = Key(candidate, device, target);
            if (string.IsNullOrEmpty(key)) return false;
            lock (Gate)
            {
                try { return File.Exists(StorePath) && File.ReadAllLines(StorePath).Any(x => string.Equals(x.Trim(), key, StringComparison.OrdinalIgnoreCase)); }
                catch { return false; }
            }
        }

        public static bool Record(LanCandidateDto candidate, D device, OsTarget target)
        {
            string key = Key(candidate, device, target);
            if (string.IsNullOrEmpty(key)) return false;
            lock (Gate)
            {
                try
                {
                    var entries = File.Exists(StorePath) ? File.ReadAllLines(StorePath).Where(x => !string.IsNullOrWhiteSpace(x)).ToList() : new List<string>();
                    if (entries.Any(x => string.Equals(x.Trim(), key, StringComparison.OrdinalIgnoreCase))) return false;
                    entries.Add(key);
                    if (entries.Count > 5000) entries = entries.Skip(entries.Count - 5000).ToList();
                    string dir = Path.GetDirectoryName(StorePath);
                    Directory.CreateDirectory(dir);
                    string tmp = StorePath + ".tmp";
                    File.WriteAllLines(tmp, entries, new UTF8Encoding(false));
                    if (File.Exists(StorePath)) File.Delete(StorePath);
                    File.Move(tmp, StorePath);
                    return true;
                }
                catch { return false; }
            }
        }

        public static int RecordPnputilLatest(IList<LanCandidateDto> candidates, string contentHash, InstallItemResult item,
            IDictionary<string, D> devices, OsTarget target, L logger)
        {
            if (item == null || !item.Ran || item.ExitCode != 259 || item.Outcome != InstallOutcome.Skipped) return 0;
            string output = item.Output ?? "";
            bool authoritativeLatest = output.IndexOf("设备上的驱动程序包是最新的", StringComparison.OrdinalIgnoreCase) >= 0 ||
                output.IndexOf("driver package is already the best", StringComparison.OrdinalIgnoreCase) >= 0 ||
                output.IndexOf("best driver is already installed", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!authoritativeLatest) return 0;
            var samePackage = (candidates ?? new LanCandidateDto[0]).Where(x => x != null &&
                string.Equals(x.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase)).ToList();
            // Prefer the explicit per-device "latest" lines. If the OS localized/changed that format,
            // exit 259 plus the explicit latest phrase still means this package updated none of the
            // selected devices, so apply it to this package's offered device set.
            var reported = samePackage.Where(x => !string.IsNullOrEmpty(x.DeviceInstanceId) &&
                ReportsLatestForDevice(output, x.DeviceInstanceId)).ToList();
            var targets = reported.Count > 0 ? reported : samePackage;
            int count = 0;
            foreach (var cand in targets)
            {
                D device;
                if (devices == null || !devices.TryGetValue(cand.DeviceInstanceId ?? "", out device)) continue;
                if (Record(cand, device, target))
                {
                    count++;
                    if (logger != null) logger.W("[INFO] 暂停重复推荐 / Suppressed Windows-rejected candidate until device/package/OS changes: " + cand.DeviceInstanceId + " " + cand.InfRelativePath);
                }
            }
            return count;
        }

        private static bool ReportsLatestForDevice(string output, string instanceId)
        {
            foreach (string line in (output ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (line.IndexOf("设备上的驱动程序包是最新的", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    int colon = line.LastIndexOf(':');
                    if (colon >= 0 && string.Equals(line.Substring(colon + 1).Trim(), instanceId, StringComparison.OrdinalIgnoreCase)) return true;
                }
                if (line.IndexOf("driver package is already the best", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("best driver is already installed", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (line.TrimEnd().EndsWith(instanceId, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        private static string Key(LanCandidateDto candidate, D device, OsTarget target)
        {
            if (candidate == null || device == null || string.IsNullOrWhiteSpace(candidate.ContentHash) || string.IsNullOrWhiteSpace(device.Id)) return "";
            string value = string.Join("|", new[] { device.Id, candidate.ContentHash, device.Inf ?? "", device.Version ?? "",
                device.Bad ? "1" : "0", target != null ? (target.Architecture ?? "") : "", target != null ? target.MajorVersion.ToString(CultureInfo.InvariantCulture) : "0",
                target != null ? target.EffectiveMinorVersion().ToString(CultureInfo.InvariantCulture) : "0", target != null ? target.BuildNumber.ToString(CultureInfo.InvariantCulture) : "0" });
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value.ToUpperInvariant()))).Replace("-", "");
        }
    }

    public static class LanInstalledPackage
    {
        /// <summary>Test seam: when set, replaces the "hash of the INF currently in effect" lookup.
        /// Maps the device's CurrentInf string to the SHA-256 of the driver actually in effect (or null).
        /// Null in production.</summary>
        public static Func<string, string> TestCurrentInfHash;

        public static bool MatchesCurrentInf(string currentInf, string contentHash)
        {
            if (string.IsNullOrWhiteSpace(currentInf) || string.IsNullOrWhiteSpace(contentHash)) return false;
            if (TestCurrentInfHash != null)
            {
                string h = TestCurrentInfHash(currentInf);
                return !string.IsNullOrEmpty(h) && string.Equals(h, contentHash.Trim(), StringComparison.OrdinalIgnoreCase);
            }
            string path = CurrentInfPath(currentInf);
            return !string.IsNullOrEmpty(path) && MatchesCurrentInfFile(path, contentHash);
        }

        private static string CurrentInfPath(string currentInf)
        {
            try { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", Path.GetFileName(currentInf.Trim())); }
            catch { return null; }
        }

        /// <summary>True when the file at <paramref name="infPath"/> hashes exactly to <paramref name="contentHash"/> —
        /// i.e. the device currently actually runs this driver package. Used by both the receipt suppression
        /// check and its tests.</summary>
        public static bool MatchesCurrentInfFile(string infPath, string contentHash)
        {
            if (string.IsNullOrWhiteSpace(infPath) || string.IsNullOrWhiteSpace(contentHash) || !File.Exists(infPath)) return false;
            try
            {
                using (var sha = SHA256.Create())
                    return string.Equals(BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(infPath))).Replace("-", ""), contentHash.Trim(), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static string FindImportedInf(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return null;
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
                foreach (string path in Directory.EnumerateFiles(folder, "oem*.inf"))
                    if (MatchesCurrentInfFile(path, hash)) return path;
            }
            catch { }
            return null;
        }

        internal static IDictionary<string, string> FindImportedInfs(IEnumerable<string> hashes, int processors, CancellationToken ct, DriverScanFiles files = null)
        {
            var wanted = new HashSet<string>(hashes.Where(h => !string.IsNullOrEmpty(h)), StringComparer.OrdinalIgnoreCase);
            var found = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (wanted.Count == 0) return found;
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
            if (!Directory.Exists(folder)) return found;
            // Enumerate once per scan, rather than re-reading every OEM INF twice for every device.
            ClientScanParallelism.Run(Directory.GetFiles(folder, "oem*.inf"), processors, ct, (path, token) =>
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    string hash;
                    if (files != null) hash = files.Hash(path);
                    else using (var file = File.OpenRead(path))
                    using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
                    if (wanted.Contains(hash)) found.TryAdd(hash, path);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            });
            return found;
        }
    }

    public static class DriverCategoryMapper
    {
        public static string Friendly(string driverClass)
        {
            string c = (driverClass ?? "").Trim().ToLowerInvariant();
            if (c == "system" || c == "computer" || c == "firmware") return "主板与系统设备";
            if (c == "display") return "显卡";
            if (c == "net") return "网卡与网络设备";
            if (c == "media" || c == "audioendpoint") return "声卡与音频设备";
            if (c == "usb" || c == "usbdevice") return "USB 设备";
            if (c == "printer" || c == "printqueue" || c == "image") return "打印机与扫描设备";
            if (c == "bluetooth") return "蓝牙设备";
            if (c == "hidclass" || c == "keyboard" || c == "mouse") return "输入设备";
            if (c == "hdc" || c == "scsiadapter" || c == "diskdrive" || c == "storagevolumes") return "存储与磁盘控制器";
            if (c == "camera") return "摄像头";
            if (c == "battery") return "电源与电池";
            if (c == "monitor") return "显示器";
            if (c == "softwarecomponent" || c == "softwaredevice") return "软件组件";
            return string.IsNullOrWhiteSpace(driverClass) ? "其他设备" : "其他设备 · " + driverClass;
        }
    }

    public sealed class LanServerConfig
    {
        [ScriptIgnore] public Action<Action> StorageMaintenance;
        [ScriptIgnore] public Action<Action> CatalogMetadataMutation;
        [ScriptIgnore] public Action<string, MachineDriverPackage, Action<string>> ImportMachinePackage;
        [ScriptIgnore] public Func<IList<ClusterPeer>, bool> UpdateClusterPeers;
        [ScriptIgnore] public Func<List<ClusterPeer>> ReadClusterPeers;
        [ScriptIgnore] public Func<List<ClusterNodeStatus>> ReadClusterStatus;
        public string RepoRoot = "";
        public List<string> Sources = new List<string>();
        public List<string> ImportAllowRoots = new List<string>();
        public string Token = "";
        public string AdminCredentialsFile = "";
        public string ClientTemplateDir = "";
        public int AdminSessionMinutes = 30;
        public int Port;
        public int MaxRequestBytes = 8 * 1024 * 1024;
        public int RequestTimeoutMs = 30000;
        public string OsArchitecture = "amd64";
        public int OsMajorVersion = 10;
        public int OsBuildNumber;           // 0 = unknown; fed to InfApplicability build gates when >0
        public string Host = "localhost";   // localhost prefix needs no netsh/admin
        public string AdvertiseUrl = "";
    }

    public sealed class LanClientConfig
    {
        public string ServerUrl = "";          // e.g. http://localhost:PORT
        public string Token = "";
        public string BootstrapRepoDir = "";   // local NIC bootstrap offline repo (may be "")
        public int TimeoutMs = 8000;
        public int DownloadTimeoutMs = 10 * 60 * 1000;
        public int MaxRetries = 5;
        public int BackoffInitialMs = 100;
        public string StagingDir = "";         // client-controlled download staging root
        public bool Deploy, WhatIf, Force;
        public int InstallTimeoutSeconds = 600;
        public bool DedupeByContent = true, VerifySignature = true;
    }

    public sealed class LanClientResult
    {
        public bool BootstrapAttempted;
        public int BootstrapOk, BootstrapFail;
        public bool Reported;
        public bool Cached;
        public int Candidates;
        public int Downloaded;
        public int InstalledOk, InstalledSkip, InstalledFail, InstalledUnverified;
        public bool Reboot;
        public bool Cancelled;
        public string Error = "";
        public List<string> Log = new List<string>();
    }

    /// <summary>Per-candidate progress reported by LanDriverClient.InstallCandidates for the interactive GUI.
    /// Stage is one of: 下载 / 解压 / 校验 / 安装.</summary>
    public sealed class LanInstallProgress
    {
        public int Current;
        public int Total;
        public string Name = "";
        public string Stage = "";
        public string InfRelativePath = "";
        public long DownloadedBytes;
        public long TotalBytes;
        public double BytesPerSecond;
    }

    internal sealed class PreparedLanCandidate
    {
        public LanCandidateDto Candidate;
        public string ExtractDir = "";
        public string InfPath = "";
    }

    // =================================================================================
    //  Security helpers
    // =================================================================================

    public static class LanSecurity
    {
        public static bool ConstantTimeEquals(string a, string b)
        {
            if (a == null || b == null) return ReferenceEquals(a, b);
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        /// <summary>Resolve a client-supplied relative path under a root, rejecting traversal. Null = unsafe.</summary>
        public static string SafeAbsolute(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) return null;
            if (Path.IsPathRooted(relative) || relative.Contains(":") || relative.Split('/', '\\').Any(p => p == ".." || p == ".")) return null;
            string combined = Path.Combine(root, relative.Replace('/', '\\'));
            string full;
            try { full = Path.GetFullPath(combined); }
            catch { return null; }
            string r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            if (!full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return null;
            return full;
        }

        /// <summary>True if <paramref name="path"/> resolves under one of the allow-roots.</summary>
        public static bool UnderAny(string path, IEnumerable<string> allowRoots)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string full;
            try { full = Path.GetFullPath(path).TrimEnd('\\') + "\\"; }
            catch { return false; }
            foreach (var raw in allowRoots ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string r;
                try { r = Path.GetFullPath(raw).TrimEnd('\\') + "\\"; }
                catch { continue; }
                if (full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }

    // =================================================================================
    //  Server (HttpListener)
    // =================================================================================

    /// <summary>
    /// Self-hosted LAN driver service. Serves a prebuilt offline repository (loaded at startup and verified
    /// through its manifest), matches reported hardware against the repo index, and serves matched packages as
    /// signed ZIPs. All client-supplied paths are confined to the repo root; the import endpoint additionally
    /// confines server-side source paths to a configured allow-list. The server never runs pnputil or any
    /// process on client data.
    /// </summary>
    public sealed class LanDriverServer : IDisposable
    {
        private readonly LanServerConfig _config;
        private readonly L _logger;
        private HttpListener _listener;
        private readonly object _listenerLock = new object();
        private readonly SemaphoreSlim _requestSlots = new SemaphoreSlim(256, 256);
        private readonly ReaderWriterLockSlim _packageAccess = new ReaderWriterLockSlim();

        private T MutateRepository<T>(Func<string, T> action, IProgress<string> progress = null, Action<string> rollback = null, bool expandArchives = true)
        {
            if (ServerStorage.Config(_config.RepoRoot).DatabaseOnly)
            {
                lock (_maintenanceGate)
                {
                    _onlineMaintenance = true;
                    try
                    {
                        _cts.Token.ThrowIfCancellationRequested();
                        foreach (var model in MachineDriverPackages.Load(_config.RepoRoot)) MachineDriverPackages.Archive(_config.RepoRoot, model.SourceName, model.PackagePrefix, false);
                        using (var work = new ServerStorage.OnlineWorkspace(_config.RepoRoot))
                        {
                            if (progress != null) progress.Report("在线维护：后台准备，已发布驱动继续提供服务");
                            if (expandArchives) DatabaseArchiveRepository.ExpandForMaintenance(work.Root, progress);
                            T result = action(work.Root);
                            DatabaseArchiveRepository.Compact(work.Root, progress);
                            var catalog = OfflineRepoGenerator.TryLoadManifest(work.Root);
                            var prepared = PreparePublication(OfflineRepo.FromCatalog(_config.RepoRoot, catalog), work.Root);
                            var retired = RetireCurrentDownloads(prepared);
                            if (TestOnlineBeforeCommit != null) TestOnlineBeforeCommit();
                            _cts.Token.ThrowIfCancellationRequested();
                            work.Commit(retired);
                            foreach (var ticket in retired) _retiredDownloads[DownloadKey(ticket.Path, ticket.Package)] = ticket;
                            lock (_repoLock) { _repo = prepared.Repo; _publication = prepared; _archiveSetReady = true; }
                            lock (_cacheLock) _cache.Clear();
                            if (progress != null) progress.Report("在线维护：新目录已切换，旧扫描下载保留7天");
                            try { ServerStorage.CleanupRetiredDownloads(_config.RepoRoot, catalog.Entries.Select(e => Path.GetFileName(OfflineRepoGenerator.DownloadArchivePath(_config.RepoRoot, e))).Concat(prepared.ModelFiles.Values)); }
                            catch (Exception cleanup) { ServiceEventLog.Write("warning", "在线归档延后清理：" + cleanup.GetType().Name); }
                            return result;
                        }
                    }
                    finally { _onlineMaintenance = false; }
                }
            }
            if (progress != null) progress.Report("阶段：等待仓库维护锁");
            while (!_packageAccess.TryEnterWriteLock(200)) _cts.Token.ThrowIfCancellationRequested();
            try {
                _cts.Token.ThrowIfCancellationRequested(); if (progress != null) progress.Report("阶段：准备仓库维护");
                // Prime scoped identities for legacy model archives without reading ZIP contents.
                foreach (var policy in MachineDriverPackages.Load(_config.RepoRoot)) MachineDriverPackages.Archive(_config.RepoRoot, policy.SourceName, policy.PackagePrefix, false);
                if (expandArchives) DatabaseArchiveRepository.ExpandForMaintenance(_config.RepoRoot, progress);
                else if (progress != null) progress.Report("追加增量：保留旧归档，仅处理本次输入目录");
                T result = action(_config.RepoRoot);
                if (ServerStorage.Config(_config.RepoRoot).DatabaseOnly) { if (progress != null) progress.Report("阶段：核验归档并提交数据库"); DatabaseArchiveRepository.Compact(_config.RepoRoot, progress); }
                if (progress != null) progress.Report("阶段：热加载仓库");
                var expected = OfflineRepoGenerator.TryLoadManifest(_config.RepoRoot); LoadRepo();
                lock (_repoLock) if (_repo == null || !_archiveSetReady || expected == null || !string.Equals(expected.IntegrityHash, _repo.Manifest.IntegrityHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("仓库热加载未成功，当前仍保留旧仓库，请查看服务日志");
                return result;
            }
            catch (Exception failure)
            {
                try { if (rollback != null) rollback(_config.RepoRoot); }
                catch (Exception restore) { throw new AggregateException("导入失败，恢复原仓库清单也未完成，需要管理员核对", failure, restore); }
                throw;
            }
            finally { _packageAccess.ExitWriteLock(); }
        }
        private readonly object _maintenanceGate = new object();
        private volatile bool _onlineMaintenance;
        internal Action TestOnlineBeforeCommit;
        private sealed class Publication
        {
            public OfflineRepo Repo;
            public List<MachineDriverPackage> Models;
            public Dictionary<string, string> ModelFiles;
        }
        private Publication _publication;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RetiredDriverDownload> _retiredDownloads = new System.Collections.Concurrent.ConcurrentDictionary<string, RetiredDriverDownload>(StringComparer.OrdinalIgnoreCase);
        private static string DownloadKey(string path, string package) { return path + "|" + (package ?? ""); }
        private Publication PreparePublication(OfflineRepo repo, string metadataRoot)
        {
            var models = MachineDriverPackages.Load(metadataRoot);
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in models)
                files[model.SourceName + "|" + model.PackagePrefix] = Path.GetFileName(MachineDriverPackages.Archive(metadataRoot, model.SourceName, model.PackagePrefix, false));
            return new Publication { Repo = repo, Models = models, ModelFiles = files };
        }
        private List<RetiredDriverDownload> RetireCurrentDownloads(Publication next)
        {
            Publication published; lock (_repoLock) published = _publication;
            var tickets = new List<RetiredDriverDownload>(); if (published == null) return tickets;
            string expiry = DateTime.UtcNow.AddDays(7).ToString("o");
            foreach (var entry in published.Repo.Manifest.Entries)
            {
                tickets.Add(new RetiredDriverDownload { Path = entry.RelativePath, Hash = entry.ContentHash, Archive = Path.GetFileName(OfflineRepoGenerator.DownloadArchivePath(_config.RepoRoot, entry)), ExpiresUtc = expiry });
                foreach (var model in published.Models.Where(m => MachineDriverPackages.Contains(entry, m)))
                {
                    string file = published.ModelFiles[model.SourceName + "|" + model.PackagePrefix];
                    tickets.Add(new RetiredDriverDownload { Path = entry.RelativePath, Hash = entry.ContentHash, Archive = file, Package = Path.GetFileNameWithoutExtension(file), ExpiresUtc = expiry });
                }
            }
            return tickets.Where(ticket => {
                var entry = next.Repo.EntryForRelative(ticket.Path);
                if (entry == null || entry.ContentHash != ticket.Hash) return true;
                if (ticket.Package.Length == 0) return Path.GetFileName(OfflineRepoGenerator.DownloadArchivePath(_config.RepoRoot, entry)) != ticket.Archive;
                return !next.Models.Any(model => MachineDriverPackages.Contains(entry, model) && next.ModelFiles[model.SourceName + "|" + model.PackagePrefix] == ticket.Archive);
            }).ToList();
        }
        private volatile bool _listenerRunning;
        private int _activeDownloads;
        private volatile bool _archiveSetReady;
        private Thread _loop;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private OfflineRepo _repo;
        private readonly object _repoLock = new object();
        private readonly object _cacheLock = new object();
        private readonly Dictionary<string, LanReportResponse> _cache = new Dictionary<string, LanReportResponse>(StringComparer.OrdinalIgnoreCase);
        private const int CacheCap = 10000;
        private readonly WebAdminController _webAdmin;
        private readonly AiDriverTaskRunner _aiTasks;
        private readonly ClusterRouter _clusterRouter;
        private readonly object _importLock = new object();
        private bool _importRunning;
        private volatile bool _catalogDeleteRunning;
        private string _importStage = "idle";
        private string _importMessage = "";
        private string _importError = "";
        private DateTime _importStartedUtc;
        private DateTime _importFinishedUtc;
        private int _importInfCount;
        private int _importSourceInfCount;
        private int _importDuplicateCount;
        private int _importAddedCount;
        private int _importModifiedCount;
        private int _importRunsStarted;
        private readonly List<FileSystemWatcher> _sourceWatchers = new List<FileSystemWatcher>();
        private Timer _watchDebounceTimer;
        private Timer _storageRecovery;
        private Timer _repoRecovery;
        private DateTime _repoRetryUtc = DateTime.MinValue;
        private int _repoRetrySeconds = 5;
        private bool _watchUpdatePending;
        // Explicit source roots waiting for an incremental merge. Unlike the old boolean-only
        // queue, retaining the roots prevents a queued add/change from falling back to a full
        // repository rebuild after startup loading finishes.
        private readonly List<string> _pendingIncrementalSources = new List<string>();
        private string _lastError = "";
        private DateTime _lastErrorUtc = DateTime.MinValue;

        // ---- test seams (null/0 in production) ----
        /// <summary>Test seam: when set, the accept loop raises this exception once instead of calling
        /// GetContext, to prove a transient accept-loop fault does not kill the server.</summary>
        public Func<Exception> TestAcceptFault;
        /// <summary>Test seam: replaces OfflineRepo.Open when (re)loading the repo. Null in production.</summary>
        public Func<string, OfflineRepo> TestRepoOpenOverride;
        /// <summary>Test seam: debounce delay in ms for source-watch driven rescans. 0 = production 10s.</summary>
        internal int TestWatchDebounceMs;

        private sealed class InlineProgress : IProgress<string>
        {
            private readonly Action<string> _callback;
            public InlineProgress(Action<string> callback) { _callback = callback; }
            public void Report(string value) { _callback(value); }
        }

        /// <summary>Test seam: if set, replaces the ZIP bytes returned for a download (used to simulate a
        /// tampered/in-flight modification so the client's SHA-256 check can be exercised). Null in production.</summary>
        public Func<string, byte[]> TestDownloadOverride;

        /// <summary>Test seam: signature verifier used when (re)generating the repo on import. Null in production
        /// (the real WinVerifyTrust records authentic signature status); tests inject a trusting verifier so the
        /// cold-start cost of the trust subsystem does not perturb timing.</summary>
        public ISignatureVerifier ImportSignatureVerifier;

        public LanDriverServer(LanServerConfig config, L logger)
        {
            _config = config;
            _logger = logger ?? new L(Path.GetTempPath());
            _config.ImportMachinePackage = (source, policy, notify) => {
                var progress = notify == null ? null : new InlineProgress(notify);
                if (progress != null) progress.Report("阶段：检查上传完整性");
                WebAdminController.ValidateUploadedSource(_config.RepoRoot, source);
                string oldManifest = null, oldPolicies = null;
                MutateRepository(workRoot => {
                    oldManifest = ServerStorage.Read(workRoot, "repo.manifest.json"); oldPolicies = ServerStorage.Read(workRoot, "repo.machine-packages.json");
                    string dedicated = "Machine-" + Guid.NewGuid().ToString("N");
                    if (progress != null) progress.Report("阶段：导入机型驱动文件");
                    var generator = new OfflineRepoGenerator(ImportSignatureVerifier ?? new WinTrustSignatureVerifier(), _logger);
                    var report = generator.AppendOrganizedSource(workRoot, source, _cts.Token, progress, dedicated);
                    if (report.Added == 0) throw new InvalidDataException("没有可发布的 INF：请检查文件夹是否包含完整 INF 驱动，以及这些驱动是否已被管理员取消发布。上传原件已保留。");
                    if (progress != null) progress.Report("阶段：机型驱动已入库（新增 " + report.Added + " 个 INF）");
                    if (progress != null) progress.Report("阶段：生成下载压缩包");
                    OfflineRepoGenerator.PrepareDownloadPackages(workRoot, progress);
                    policy.SourceName = dedicated; policy.PackagePrefix = "";
                    var policies = MachineDriverPackages.Load(workRoot);
                    policies.RemoveAll(p => MachineDriverPackages.SameSystemMapping(p, policy));
                    policies.Add(policy); if (progress != null) progress.Report("阶段：生成机型整包与保存对应关系");
                    MachineDriverPackages.Save(workRoot, policies, progress); return true;
                }, progress, workRoot => {
                    if (oldManifest == null) return;
                    if (progress != null) progress.Report("阶段：恢复原仓库清单和机型对应关系");
                    ServerStorage.Write(workRoot, "repo.manifest.json", oldManifest, false);
                    if (oldPolicies == null) ServerStorage.Delete(workRoot, "repo.machine-packages.json"); else ServerStorage.Write(workRoot, "repo.machine-packages.json", oldPolicies, false);
                    string index = Path.Combine(workRoot, "repo.udadb");
                    if (ServerStorage.Config(workRoot).DatabaseOnly && File.Exists(index)) File.Delete(index);
                    LoadRepo();
                }, false);
                if (progress != null) progress.Report("阶段：清理已消费上传暂存");
                FinalizeUploadedSource(source);
            };
            _config.StorageMaintenance = action => { lock (_maintenanceGate) { _packageAccess.EnterWriteLock(); try { action(); LoadRepo(); lock (_cacheLock) _cache.Clear(); } finally { _packageAccess.ExitWriteLock(); } } };
            _config.CatalogMetadataMutation = action => {
                lock (_maintenanceGate)
                {
                    action(); OfflineRepo current; lock (_repoLock) current = _repo;
                    if (current != null) { var prepared = PreparePublication(current, _config.RepoRoot); lock (_repoLock) _publication = prepared; }
                    lock (_cacheLock) _cache.Clear();
                }
            };
            _clusterRouter = new ClusterRouter(_config.RepoRoot, _config.AdvertiseUrl, _config.Token,
                delegate { lock (_repoLock) return _archiveSetReady && _repo != null && _repo.Manifest != null ? _repo.Manifest.IntegrityHash : ""; },
                delegate { return Math.Max(0, Volatile.Read(ref _activeDownloads)); });
            _config.UpdateClusterPeers = peers => { try { _clusterRouter.Update(peers); return true; } catch (Exception ex) { if (_logger != null) _logger.W("[ERROR] cluster config update failed: " + ex.Message); return false; } };
            _config.ReadClusterPeers = () => _clusterRouter.Peers();
            _config.ReadClusterStatus = () => _clusterRouter.Status();
            if (!string.IsNullOrWhiteSpace(_config.AdminCredentialsFile)) LoadPersistedImportSources();
            _webAdmin = new WebAdminController(_config, delegate { lock (_repoLock) return _repo; }, RepoLoadingStatus);
            _aiTasks = new AiDriverTaskRunner(_config.RepoRoot, delegate { lock (_repoLock) return _repo; }, _cts.Token);
        }

        /// <summary>Status text shown by the admin console while the repository is still loading.
        /// Empty string once the repo is available.</summary>
        private string RepoLoadingStatus()
        {
            lock (_repoLock) if (_repo != null) return "";
            lock (_importLock)
            {
                if (_importStage == "repo-loading" || _importStage == "pending" || _importStage == "starting")
                    return "仓库加载中 / repo loading: " + (_importMessage ?? _importStage);
                return "仓库未就绪 / repo not ready: " + (_importMessage ?? _importStage);
            }
        }

        public int Port { get { return _config.Port; } }
        public bool RepoLoaded { get { lock (_repoLock) return _repo != null; } }

        /// <summary>True while the HTTP listener is accepting (reflected by /api/health).</summary>
        public bool IsListening { get { lock (_listenerLock) return _listener != null && _listener.IsListening; } }

        // ---- diagnostics (surfaced by /api/health and the persistent event log) ----

        /// <summary>Record a server-side failure: persisted to the service event log AND held in memory
        /// so /api/health can report the most recent error. A failure must never be silently swallowed;
        /// recording it here is what keeps "alive" distinguishable from "healthy".</summary>
        private void RecordError(string message)
        {
            lock (_importLock)
            {
                _lastError = message ?? "";
                _lastErrorUtc = DateTime.UtcNow;
            }
            ServiceEventLog.Write("error", "LAN 服务错误 / LAN server error (port " + Port + "): " + message);
        }

        private void GetLastError(out string message, out string utc)
        {
            lock (_importLock)
            {
                message = _lastError;
                utc = _lastErrorUtc == DateTime.MinValue ? "" : _lastErrorUtc.ToString("o", CultureInfo.InvariantCulture);
            }
        }

        // ---- test/inspection accessors (internal; never used by product code paths) ----
        internal string CurrentImportStage { get { lock (_importLock) return _importStage; } }
        internal bool ImportRunningNow { get { lock (_importLock) return _importRunning; } }
        internal int ImportRunsStarted { get { lock (_importLock) return _importRunsStarted; } }
        internal int WatcherCount { get { lock (_importLock) return _sourceWatchers.Count; } }
        internal bool AcceptLoopAlive { get { return _loop != null && _loop.IsAlive; } }

        /// <summary>Cancellation probe that tolerates a disposed CTS (double-Stop/Dispose must be safe).
        /// A disposed CTS can only mean Stop already ran, so it reports "stopping".</summary>
        private bool IsStopping
        {
            get { try { return _cts.IsCancellationRequested; } catch (ObjectDisposedException) { return true; } }
        }

        /// <summary>Test seam: simulate a source-change notification (same path a FileSystemWatcher
        /// event takes) so tests can prove debounce coalescing + single-flight without real FS churn.</summary>
        internal void TestSignalSourceChange() { ScheduleSourceRefresh("(test signal)"); }

        /// <summary>Test seam: raise the FileSystemWatcher.Error path exactly as the .NET event would
        /// (buffer overflow / watcher death), proving the server logs it and schedules a full rescan.</summary>
        internal void TestRaiseWatcherError() { SourceWatchError(null, new ErrorEventArgs(new IOException("simulated watcher buffer overflow"))); }

        public void LoadRepo()
        {
            try
            {
                if (!string.IsNullOrEmpty(_config.RepoRoot) && (File.Exists(Path.Combine(_config.RepoRoot, "repo.manifest.json")) || ServerStorage.Config(_config.RepoRoot).Enabled))
                {
                    var opened = TestRepoOpenOverride != null ? TestRepoOpenOverride(_config.RepoRoot) : OfflineRepo.Open(_config.RepoRoot, _logger);
                    OfflineRepoGenerator.PrepareDownloadPackages(_config.RepoRoot, null);
                    var prepared = PreparePublication(opened, _config.RepoRoot);
                    if (ServerStorage.Config(_config.RepoRoot).Enabled && _retiredDownloads.Count == 0)
                        foreach (var row in ServerStorage.Documents(_config.RepoRoot, "repo.download-history."))
                            foreach (var ticket in new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<List<RetiredDriverDownload>>(row.Value))
                                if (string.CompareOrdinal(ticket.ExpiresUtc, DateTime.UtcNow.ToString("o")) > 0)
                                    _retiredDownloads.AddOrUpdate(DownloadKey(ticket.Path, ticket.Package), ticket, (key, existing) => string.CompareOrdinal(existing.ExpiresUtc, ticket.ExpiresUtc) >= 0 ? existing : ticket);
                    lock (_repoLock) { _repo = opened; _publication = prepared; _archiveSetReady = true; }
                }
                else { lock (_repoLock) { _repo = null; _publication = null; _archiveSetReady = false; } }
            }
            catch (Exception ex)
            {
                RecordError("repo load failed (previous repo kept / 仓库加载失败，保留上一份可用仓库): " + ex.Message);
                if (_logger != null) _logger.W("[ERROR] 仓库加载失败，保留上一份可用仓库: " + ex.Message);
            }
            lock (_cacheLock) _cache.Clear();
        }

        internal bool TestConfigureClusterPeers(IList<ClusterPeer> peers)
        {
            try { _clusterRouter.Update(peers); return true; }
            catch { return false; }
        }

        private void ConfigureListener()
        {
            _listener.Prefixes.Add("http://" + (_config.Host ?? "localhost") + ":" + _config.Port.ToString(CultureInfo.InvariantCulture) + "/");
            try { _listener.TimeoutManager.HeaderWait = TimeSpan.FromSeconds(15); }
            catch { }
            try { _listener.TimeoutManager.EntityBody = TimeSpan.FromSeconds(60); }
            catch { }
        }

        public void Start()
        {
            int minWorkers, minIo;
            ThreadPool.GetMinThreads(out minWorkers, out minIo);
            ThreadPool.SetMinThreads(Math.Max(128, minWorkers), Math.Max(128, minIo));
            ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 512);
            _clusterRouter.Start();
            // The listener must come up BEFORE the repo loads: OfflineRepo.Open on a large/legacy repo
            // (Models backfill reads every INF on disk) can exceed the SCM's start timeout, which used
            // to fail the service start and left the admin console unreachable. Now OnStart returns as
            // soon as HTTP is accepting; the repo loads in a controlled background job and /api/health
            // + /admin report the loading progress meanwhile.
            lock (_listenerLock)
            {
                try
                {
                    _listener = new HttpListener();
                    ConfigureListener();
                    _listener.Start();
                    _listenerRunning = true;
                }
                catch (Exception ex)
                {
                    // A port/bind failure is a fatal, diagnosable condition: record it, tear down the
                    // half-built listener, and surface to the caller (SCM marks start failure; the
                    // sc.exe failure recovery then restarts us after 5s instead of hanging silently).
                    _listenerRunning = false;
                    RecordError("listener start FAILED on port " + _config.Port + ": " + ex.Message);
                    ServiceEventLog.Write("fatal", "LAN 服务监听失败 / LAN listener start failed on port " + _config.Port + " (host=" + (_config.Host ?? "localhost") + "): " + ex);
                    try { if (_listener != null) _listener.Close(); } catch { }
                    _listener = null;
                    throw;
                }
            }
            _loop = new Thread(Loop) { IsBackground = true, Name = "LanDriverServer" };
            _loop.Start();
            _storageRecovery = ServerStorage.StartRecovery(_config.RepoRoot);
            _aiTasks.Start();
            _repoRecovery = new Timer(delegate { RetryRepository(); }, null, 1000, 1000);
            BeginRepoInit();
            ServiceEventLog.Write("event", "LAN 服务已启动 / LAN server listening on port " + _config.Port + " (repo=" + (_config.RepoRoot ?? "") + ", token=" + (string.IsNullOrEmpty(_config.Token) ? "off" : "on") + ", watchers=" + WatcherCount + ")");
            if (_logger != null) _logger.W("[INFO] LAN 服务已启动 / LAN server listening on port " + _config.Port + " (repo=" + (_config.RepoRoot ?? "") + ", token=" + (string.IsNullOrEmpty(_config.Token) ? "off" : "on") + ")");
        }

        // Startup recovery only reopens the persisted index; it never rebuilds the driver library.
        private void RetryRepository()
        {
            // Do not take _repoLock here: an ongoing large load holds it for its entire read.
            // Timer callbacks must stay nonblocking rather than queue up waiting for that load.
            if (IsStopping) return;
            lock (_importLock)
            {
                if (IsStopping || _importRunning || _repoRetryUtc == DateTime.MinValue || DateTime.UtcNow < _repoRetryUtc) return;
                _repoRetryUtc = DateTime.MinValue;
                BeginRepoInit();
            }
        }

        /// <summary>Queue a background repo load, single-flight with imports/dedupe.</summary>
        private void BeginRepoInit()
        {
            try
            {
                lock (_importLock)
                {
                    if (IsStopping || _importRunning) return;
                    _importRunning = true;
                    _importStage = "repo-loading";
                    _importMessage = "正在加载驱动仓库，管理后台可正常访问";
                    _importError = "";
                    _importStartedUtc = DateTime.UtcNow;
                    _importFinishedUtc = DateTime.MinValue;
                }
                ThreadPool.QueueUserWorkItem(delegate { RunRepoInitJob(); });
            }
            catch (Exception ex)
            {
                FinishRepoInit("failed", "仓库加载任务启动失败", ex.Message);
            }
        }

        /// <summary>Background repo initialization: open the repo, atomically persist a legacy-index
        /// Models upgrade (so the expensive backfill runs at most once per repository, never again on
        /// every start), then swap it in. On failure the service and admin console stay up and the
        /// health endpoint reports the precise error. Honours service cancellation.</summary>
        private void RunRepoInitJob()
        {
            bool ownsWriteLock = false;
            try
            {
                if (IsStopping) { FinishRepoInit("completed", "服务已停止，跳过仓库加载", ""); return; }
                bool databaseOnly = ServerStorage.Config(_config.RepoRoot).DatabaseOnly;
                if (!databaseOnly) { while (!_packageAccess.TryEnterWriteLock(200)) { if (IsStopping) return; } ownsWriteLock = true; }
                if (databaseOnly) ServerStorage.RecoverOnlineWorkspaces(_config.RepoRoot);
                // SMB authentication and unavailable shares may be slow; never do this before
                // SCM OnStart returns or before the HTTP listener is available.
                SmbSourceCredentials.Restore(_config, warning => { if (_logger != null) _logger.W("[WARN] " + warning); });
                if (!string.IsNullOrWhiteSpace(_config.AdminCredentialsFile)) LoadPersistedImportSources();
                StartSourceWatchers();
                lock (_importLock) { _importMessage = "正在加载驱动仓库（大型或旧索引仓库可能需要较长时间）"; }
                if (databaseOnly) { lock (_maintenanceGate) LoadRepo(); } else LoadRepo();
                OfflineRepo repo; lock (_repoLock) repo = _repo;
                bool manifestMissing = string.IsNullOrEmpty(_config.RepoRoot) ||
                    (!ServerStorage.Config(_config.RepoRoot).Enabled && !File.Exists(Path.Combine(_config.RepoRoot, "repo.manifest.json")));
                if (repo == null && manifestMissing)
                { FinishRepoInit("completed", "未配置驱动仓库 / no repository configured", ""); return; }
                if (repo == null)
                {
                    string lastError, lastErrorUtc; GetLastError(out lastError, out lastErrorUtc);
                    FinishRepoInit("failed", "仓库加载失败，服务保持可用 / repo load failed; service stays up", lastError);
                    return;
                }
                // One-time legacy-index migration: never rescan the INF tree on every start.
                if (repo.BackfilledModels > 0)
                {
                    lock (_importLock) { _importMessage = "检测到旧版索引，正在升级持久化（仅此一次），已回填 " + repo.BackfilledModels.ToString(CultureInfo.InvariantCulture) + " 条"; }
                    ServiceEventLog.Write("event", "旧索引 Models 回填 " + repo.BackfilledModels.ToString(CultureInfo.InvariantCulture) + " 条，开始原子升级持久化 / legacy index backfill, persisting upgraded index");
                    repo.PersistUpgradedIndex(_logger);
                }
                FinishRepoInit("completed", "驱动仓库就绪，共 " + RepoCount().ToString(CultureInfo.InvariantCulture) + " 个 INF", "");
            }
            catch (Exception ex)
            {
                FinishRepoInit("failed", "仓库初始化失败，服务保持可用 / repo init failed; service stays up", ex.Message);
            }
            finally { if (ownsWriteLock) _packageAccess.ExitWriteLock(); }
        }

        private void FinishRepoInit(string stage, string message, string error)
        {
            lock (_importLock)
            {
                _importStage = stage;
                _importMessage = message ?? "";
                _importError = error ?? "";
                _importFinishedUtc = DateTime.UtcNow;
                _importRunning = false;
                _repoRetryUtc = stage == "failed" && !IsStopping ? DateTime.UtcNow.AddSeconds(_repoRetrySeconds) : DateTime.MinValue;
                _repoRetrySeconds = stage == "failed" ? Math.Min(60, _repoRetrySeconds * 2) : 5;
            }
            if (stage == "failed")
            {
                RecordError("repo init FAILED (service and admin console remain available): " + error);
                if (_logger != null) _logger.W("[ERROR] 仓库初始化失败 / repo init failed: " + error);
            }
            else
            {
                ServiceEventLog.Write("event", "仓库初始化完成 / repo init finished: " + message);
                if (_logger != null) _logger.W("[INFO] 仓库初始化完成 / repo init finished: " + message);
            }
            // An administrator may add one or more sources while a large legacy repository is
            // still being opened.  Those requests are coalesced in _watchUpdatePending; once the
            // initial read-only load releases the single-flight gate, start exactly one rebuild
            // containing every source that has already been persisted in the server config.
            ReschedulePendingWatchUpdate();
        }

        private void Loop()
        {
            int backoffMs = 250;
            while (!IsStopping)
            {
                HttpListenerContext ctx;
                try
                {
                    if (TestAcceptFault != null) { Func<Exception> f = TestAcceptFault; TestAcceptFault = null; if (f != null) throw f(); }
                    ctx = _listener.GetContext();
                    backoffMs = 250;   // healthy pass resets the recovery backoff
                }
                catch (Exception ex)
                {
                    // A transient accept-loop exception (listener reset, brief socket fault) must NOT
                    // terminate the loop: recover or recreate the listener and keep serving, with
                    // bounded exponential backoff so a permanently broken port never busy-loops.
                    if (IsStopping || !RecoverListener(ex)) break;
                    try { _cts.Token.WaitHandle.WaitOne(backoffMs); }
                    catch (ObjectDisposedException) { break; }
                    backoffMs = Math.Min(backoffMs * 2, 5000);
                    continue;
                }
                if (!_requestSlots.Wait(0))
                {
                    SafeRespond(ctx, 503, "server busy; retry shortly"); try { ctx.Response.Close(); } catch { } continue;
                }
                HttpListenerContext requestContext = ctx;
                ThreadPool.QueueUserWorkItem(delegate {
                    try { Handle(requestContext); }
                    catch (Exception ex) { SafeRespond(requestContext, 500, "internal error"); RecordError("request handler: " + ex.GetType().Name); if (_logger != null) _logger.W("[ERROR] LAN 请求处理异常 / request error: " + ex.Message); }
                    finally { try { _webAdmin.RecordAuditRequest(requestContext); } catch { } try { requestContext.Response.Close(); } catch { } _requestSlots.Release(); }
                });
            }
            if (_logger != null) _logger.W("[INFO] LAN 接受循环退出 / accept loop exited (stopping=" + IsStopping + ")");
        }

        /// <summary>Attempt to recover from an accept-loop exception. Returns true to keep the loop
        /// running (transient fault while still listening, or listener recreated), false only when the
        /// service is stopping or the listener was deliberately disposed.</summary>
        private bool RecoverListener(Exception ex)
        {
            if (ex is ObjectDisposedException) return false;   // deliberate disposal during Stop
            RecordError("accept loop: " + ex.Message);
            if (_logger != null) _logger.W("[WARN] LAN 接受循环异常，尝试恢复 / accept loop exception, recovering: " + ex.Message);
            if (IsStopping) return false;
            lock (_listenerLock)
            {
                if (IsStopping) return false;
                if (_listener == null) return false;
                try { if (_listener.IsListening) return true; } catch { }   // transient; keep serving
                // The listener died (e.g. transient HttpListenerException): rebuild it once here.
                // If the rebuild itself fails the caller backs off and the next fault retries —
                // the loop stays alive for the lifetime of the service.
                try { _listener.Close(); } catch { }
                try
                {
                    _listener = new HttpListener();
                    ConfigureListener();
                    _listener.Start();
                    _listenerRunning = true;
                    if (_logger != null) _logger.W("[INFO] LAN 监听器已恢复 / listener recovered on port " + _config.Port);
                    return true;
                }
                catch (Exception rex)
                {
                    RecordError("listener restart failed (will retry with backoff): " + rex.Message);
                    return true;   // keep retrying; never kill the loop while the service is meant to run
                }
            }
        }

        public void Stop()
        {
            _aiTasks.Dispose();
            if (_repoRecovery != null) { _repoRecovery.Dispose(); _repoRecovery = null; }
            if (_storageRecovery != null) { _storageRecovery.Dispose(); _storageRecovery = null; }
            if (!IsStopping)
            {
                ServiceEventLog.Write("event", "LAN 服务停止请求 / LAN server stop requested (port " + _config.Port + ")");
                if (_logger != null) _logger.W("[INFO] LAN 服务停止中 / LAN server stopping");
            }
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            lock (_importLock)
            {
                if (_watchDebounceTimer != null) { try { _watchDebounceTimer.Dispose(); } catch { } _watchDebounceTimer = null; }
                _watchUpdatePending = false;
                foreach (var watcher in _sourceWatchers) try { watcher.EnableRaisingEvents = false; watcher.Dispose(); } catch { }
                _sourceWatchers.Clear();
            }
            // Bounded wait for a running repo import so a shutdown never races the repo writer
            // (deterministic release: watchers + timer + in-flight job all quiesce before the
            // listener goes away).
            DateTime importDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < importDeadline)
            {
                bool running = ImportRunningNow || _aiTasks.IsRunning;
                if (!running) break;
                Thread.Sleep(50);
            }
            lock (_listenerLock)
            {
                _listenerRunning = false;
                try { if (_listener != null && _listener.IsListening) _listener.Stop(); } catch { }
            }
            if (_loop != null && _loop.IsAlive) _loop.Join(3000);
            lock (_listenerLock)
            {
                try { if (_listener != null) _listener.Close(); } catch { }
                _listener = null;
            }
            _clusterRouter.Dispose();
            if (_logger != null) _logger.W("[INFO] LAN 服务已停止 / LAN server stopped cleanly");
            ServiceEventLog.Write("event", "LAN 服务已停止 / LAN server stopped cleanly (port " + _config.Port + ")");
        }

        public void Dispose() { Stop(); try { _cts.Dispose(); } catch { } }

        private void StartSourceWatchers()
        {
            foreach (string raw in (_config.Sources ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase)) AddSourceWatcher(raw);
        }

        private void AddSourceWatcher(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || !Directory.Exists(raw)) return;
            lock (_importLock)
            {
                if (_sourceWatchers.Any(x => string.Equals(x.Path, Path.GetFullPath(raw), StringComparison.OrdinalIgnoreCase))) return;
                try
                {
                    var watcher = new FileSystemWatcher(Path.GetFullPath(raw))
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        EnableRaisingEvents = true
                    };
                    watcher.Created += SourceChanged; watcher.Changed += SourceChanged; watcher.Deleted += SourceChanged; watcher.Renamed += SourceRenamed;
                    watcher.Error += SourceWatchError;
                    _sourceWatchers.Add(watcher);
                    if (_logger != null) _logger.W("[INFO] 已监控驱动源热更新 / Watching driver source: " + watcher.Path);
                }
                catch (Exception ex) { if (_logger != null) _logger.W("[WARN] 无法监控驱动源 / Cannot watch source: " + raw + " => " + ex.Message); }
            }
        }

        private static string ImportSourcesPath() { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UniversalDriverAssistant", "import-sources.txt"); }

        private void SavePersistedImportSources()
        {
            var sources = _config.Sources.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
            if (ServerStorage.Config(_config.RepoRoot).Enabled) ServerStorage.Write(_config.RepoRoot, "repo.import-sources.json", new JavaScriptSerializer().Serialize(sources), false);
            else { string path = ImportSourcesPath(); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllLines(path, sources, new UTF8Encoding(false)); }
        }
        private void LoadPersistedImportSources()
        {
            try
            {
                string stored = ServerStorage.Config(_config.RepoRoot).Enabled ? ServerStorage.Read(_config.RepoRoot, "repo.import-sources.json") : null;
                string[] sources = stored != null ? new JavaScriptSerializer().Deserialize<string[]>(stored) : File.Exists(ImportSourcesPath()) ? File.ReadAllLines(ImportSourcesPath()) : new string[0];
                foreach (string source in sources)
                    if (!string.IsNullOrWhiteSpace(source) && !_config.Sources.Contains(source, StringComparer.OrdinalIgnoreCase)) _config.Sources.Add(Path.GetFullPath(source));
            }
            catch { }
        }

        private void RegisterImportSource(string source)
        {
            string full = Path.GetFullPath(source);
            lock (_importLock) lock (_config.Sources) if (!_config.Sources.Contains(full, StringComparer.OrdinalIgnoreCase)) _config.Sources.Add(full);
            try
            {
                if (!string.IsNullOrWhiteSpace(_config.AdminCredentialsFile))
                {
                    SavePersistedImportSources();
                }
            }
            catch { }
            AddSourceWatcher(full);
        }

        private void FinalizeUploadedSource(string source)
        {
            try
            {
            string batch = WebAdminController.FindUploadedBatch(_config.RepoRoot, source);
            if (batch == null) return;
            if ((File.GetAttributes(batch) & FileAttributes.ReparsePoint) != 0) throw new IOException("上传暂存目录被替换为链接，拒绝清理");
            lock (_importLock)
            {
                int removed;
                lock (_config.Sources) removed = _config.Sources.RemoveAll(s => string.Equals(Path.GetFullPath(s).TrimEnd('\\'), Path.GetFullPath(source).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                _pendingIncrementalSources.RemoveAll(s => string.Equals(s, source, StringComparison.OrdinalIgnoreCase));
                foreach (var watcher in _sourceWatchers.Where(w => string.Equals(w.Path.TrimEnd('\\'), source.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToList())
                { watcher.EnableRaisingEvents = false; watcher.Dispose(); _sourceWatchers.Remove(watcher); }
                if (_pendingIncrementalSources.Count == 0) _watchUpdatePending = false;
                if (removed > 0 && !string.IsNullOrWhiteSpace(_config.AdminCredentialsFile)) SavePersistedImportSources();
            }
            // Only our GUID upload session is consumed. Local/SMB user source directories never are.
            Directory.Delete(batch, true);
            ServerStorage.RemoveUploadState(_config.RepoRoot, Path.GetFileName(batch));
            if (_logger != null) _logger.W("[INFO] 上传暂存已在入库后清理 / uploaded staging reclaimed: " + Path.GetFileName(batch));
            }
            catch (Exception ex) { RecordError("uploaded staging cleanup deferred (repository already committed): " + ex.Message); }
        }

        private void SourceChanged(object sender, FileSystemEventArgs e) { ScheduleSourceRefresh(e.FullPath); }
        private void SourceRenamed(object sender, RenamedEventArgs e) { ScheduleSourceRefresh(e.FullPath); }

        /// <summary>FileSystemWatcher.Error: the watcher overflowed its buffer or died, so events were
        /// LOST. Record the cause, rebuild the watcher so future events are seen again, and schedule ONE
        /// full rescan through the shared debounce + single-flight path — never a burst of concurrent
        /// rebuilds (a storm would thrash the repo and the disk for large source trees).</summary>
        private void SourceWatchError(object sender, ErrorEventArgs e)
        {
            Exception ex = e != null ? e.GetException() : null;
            RecordError("source watcher error (full rescan scheduled / 监控错误，安排完整重扫): " + (ex != null ? ex.Message : "unknown"));
            if (_logger != null) _logger.W("[ERROR] 驱动源监控错误，安排完整重扫 / source watcher error, full rescan scheduled: " + (ex != null ? ex.GetType().Name + ": " + ex.Message : "unknown"));
            FileSystemWatcher dead = sender as FileSystemWatcher;
            if (dead != null) RecreateWatcher(dead);
            ScheduleSourceRefresh("(watcher error: " + (ex != null ? ex.GetType().Name : "unknown") + ")");
        }

        /// <summary>Replace a dead watcher with a fresh one on the same path, under the import lock so
        /// it cannot race Stop or a concurrent recreation.</summary>
        private void RecreateWatcher(FileSystemWatcher dead)
        {
            string path;
            try { path = dead.Path; }
            catch { return; }
            lock (_importLock)
            {
                _sourceWatchers.RemoveAll(x => ReferenceEquals(x, dead));
                try { dead.Dispose(); } catch { }
            }
            AddSourceWatcher(path);
        }

        private int DebounceDelayMs() { return TestWatchDebounceMs > 0 ? TestWatchDebounceMs : 10000; }

        private void ScheduleSourceRefresh(string changedPath)
        {
            if (IsStopping) return;
            lock (_importLock)
            {
                _watchUpdatePending = true;
                string changedFull = "";
                try { changedFull = Path.GetFullPath(changedPath); } catch { }
                foreach (string source in _config.Sources ?? new List<string>())
                {
                    string root;
                    try { root = Path.GetFullPath(source).TrimEnd('\\') + "\\"; } catch { continue; }
                    if ((changedFull + "\\").StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                        !_pendingIncrementalSources.Contains(source, StringComparer.OrdinalIgnoreCase))
                        _pendingIncrementalSources.Add(source);
                }
                // Watcher overflow/test signals do not carry a file path. Queue the configured
                // sources as separate incremental jobs instead of doing one full repository copy.
                if (_pendingIncrementalSources.Count == 0 && changedPath != null && changedPath.StartsWith("(", StringComparison.Ordinal))
                {
                    foreach (string source in _config.Sources ?? new List<string>())
                        if (!string.IsNullOrWhiteSpace(source) && !_pendingIncrementalSources.Contains(source, StringComparer.OrdinalIgnoreCase))
                            _pendingIncrementalSources.Add(source);
                }
                if (_pendingIncrementalSources.Count == 0) { _watchUpdatePending = false; return; }
                int delay = DebounceDelayMs();
                if (_watchDebounceTimer == null) _watchDebounceTimer = new Timer(WatchDebounceElapsed, null, delay, Timeout.Infinite);
                else _watchDebounceTimer.Change(delay, Timeout.Infinite);
                _importStage = "pending";
                _importMessage = "检测到驱动源变化，等待文件复制完成后自动增量更新";
            }
            if (_logger != null) _logger.W("[INFO] 检测到驱动源变化 / Driver source changed: " + changedPath);
        }

        private void WatchDebounceElapsed(object state)
        {
            // Timer callbacks run on arbitrary thread-pool threads: an unhandled exception here would
            // tear down the whole service process, so this callback carries a top-level boundary.
            string incrementalSource = null;
            try
            {
                lock (_importLock)
                {
                    if (IsStopping) return;
                    if (_importRunning) { _watchUpdatePending = true; if (_watchDebounceTimer != null) _watchDebounceTimer.Change(DebounceDelayMs(), Timeout.Infinite); return; }   // single-flight: one rebuild at a time
                    if (_pendingIncrementalSources.Count > 0)
                    {
                        incrementalSource = _pendingIncrementalSources[0];
                        _pendingIncrementalSources.RemoveAt(0);
                    }
                    // A watcher event that cannot be tied to one concrete source must never fall
                    // back to a full repository build.  Full builds copy every source and are only
                    // valid for the initial repository creation.
                    if (string.IsNullOrWhiteSpace(incrementalSource))
                    {
                        _watchUpdatePending = false;
                        _importStage = "idle";
                        _importMessage = "未识别到具体变化目录，已跳过；不会复制整个驱动仓库";
                        return;
                    }
                    _watchUpdatePending = _pendingIncrementalSources.Count > 0;
                    _importRunning = true;
                    _importRunsStarted++;
                    _importStage = "starting";
                    _importMessage = "驱动源变化已稳定，开始自动增量更新";
                    _importError = "";
                    _importStartedUtc = DateTime.UtcNow;
                    _importFinishedUtc = DateTime.MinValue;
                }
                ThreadPool.QueueUserWorkItem(delegate { RunImportJob(incrementalSource); });
            }
            catch (Exception ex)
            {
                RecordError("watch debounce failed: " + ex.Message);
                lock (_importLock) { _importRunning = false; _importStage = "failed"; _importError = ex.Message; _importFinishedUtc = DateTime.UtcNow; }
            }
        }

        // ----- routing -----

        private void Handle(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url.AbsolutePath.TrimEnd('/');
            string method = ctx.Request.HttpMethod;
            // Visiting the bare server address (http://ip:port/) must land on the admin console,
            // not a 404. 302 keeps deep links and the health endpoint untouched.
            if ((path.Length == 0) && method == "GET")
            {
                ctx.Response.StatusCode = 302;
                ctx.Response.Headers["Location"] = "/admin";
                ctx.Response.ContentLength64 = 0;
                return;
            }
            if (path == "/api/admin/import-status" && method == "GET") { HandleImportStatus(ctx); return; }
            // Duplicate audit/cleanup must register BEFORE WebAdmin.TryHandle: the admin console
            // claims every /api/admin/* path it does not itself know about.
            if (path == "/api/admin/duplicates" && method == "GET") { HandleDuplicates(ctx); return; }
            if (path == "/api/admin/dedupe" && method == "POST") { HandleDedupe(ctx); return; }
            if (path == "/api/admin/organize" && method == "POST") { HandleOrganize(ctx); return; }
            if (path == "/api/admin/ai/status" && method == "GET")
            {
                if (!_webAdmin.IsAuthenticated(ctx)) { SafeRespond(ctx, 401, "admin login required"); return; }
                RespondJson(ctx, 200, _aiTasks.Status()); return;
            }
            if (path == "/api/admin/ai/schedule")
            {
                if (!_webAdmin.IsAuthenticated(ctx)) { SafeRespond(ctx, 401, "admin login required"); return; }
                try
                {
                    if (method == "POST")
                    {
                        if (!IsRepoMutatorAuthorized(ctx)) return;
                        string body = ReadBody(ctx); if (body == null) return;
                        _aiTasks.SaveSchedule(new JavaScriptSerializer().Deserialize<AiAutoTaskSettings>(body));
                    }
                    else if (method != "GET") { SafeRespond(ctx, 405, "GET or POST required"); return; }
                    RespondJson(ctx, 200, _aiTasks.ScheduleStatus());
                }
                catch (ArgumentException ex) { SafeRespond(ctx, 400, ex.Message); }
                catch (InvalidOperationException ex) { SafeRespond(ctx, 409, ex.Message); }
                catch (Exception) { SafeRespond(ctx, 500, "自动计划读取或保存失败，请检查数据库连接和日志"); }
                return;
            }
            if (path == "/api/admin/ai/tasks" && method == "POST") { HandleAiTask(ctx); return; }
            if (path == "/api/admin/drivers/delete" && method == "POST") { HandleDeleteDrivers(ctx); return; }
            if (_webAdmin.TryHandle(ctx)) return;
            if (path == "/api/client/install-event" && method == "POST")
            {
                if (!CheckTelemetryProtocol(ctx) || !CheckToken(ctx, null)) { SafeRespond(ctx, 401, "client authentication required"); return; }
                _webAdmin.RecordInstallEvent(ctx); return;
            }
            if (path == "/api/cluster/health" && method == "GET") { HandleClusterHealth(ctx); return; }
            if (path == "/api/health" && method == "GET") { HandleHealth(ctx); return; }
            if (path == "/api/report" && method == "POST") { HandleReport(ctx); return; }
            if (path == "/api/download" && method == "GET") { HandleDownload(ctx); return; }
            if (path == "/api/admin/import" && method == "POST") { HandleImport(ctx); return; }
            SafeRespond(ctx, 404, "not found");
        }

        private void HandleHealth(HttpListenerContext ctx)
        {
            try
            {
                var health = new LanHealth
                {
                    RepoLoaded = RepoLoaded,
                    InfCount = RepoCount(),
                    OsArchitecture = _config.OsArchitecture,
                    OsMajorVersion = _config.OsMajorVersion,
                    TokenRequired = !string.IsNullOrEmpty(_config.Token),
                    ListenerRunning = IsListening,
                    WatcherCount = WatcherCount,
                    ImportRunning = ImportRunningNow && !_catalogDeleteRunning && !(RepoLoaded && ServerStorage.Config(_config.RepoRoot).DatabaseOnly),
                    MaintenanceRunning = ImportRunningNow || _onlineMaintenance,
                    ImportStage = CurrentImportStage
                };
                lock (_repoLock) health.RepositoryUpdatedUtc = _repo != null ? _repo.Manifest.CreatedAt : "";
                string lastError, lastErrorUtc;
                GetLastError(out lastError, out lastErrorUtc);
                health.LastError = lastError;
                health.LastErrorUtc = lastErrorUtc;
                lock (_importLock) health.RepoRetryUtc = _repoRetryUtc == DateTime.MinValue ? "" : _repoRetryUtc.ToString("o", CultureInfo.InvariantCulture);
                RespondJson(ctx, 200, health);
            }
            catch (Exception ex) { SafeRespond(ctx, 500, "health error: " + ex.Message); }
        }

        private void HandleClusterHealth(HttpListenerContext ctx)
        {
            if (!CheckProtocol(ctx) || !CheckToken(ctx, null)) { SafeRespond(ctx, 401, "cluster authentication required"); return; }
            RespondJson(ctx, 200, _clusterRouter.Health());
        }

        private void HandleReport(HttpListenerContext ctx)
        {
            if (!CheckProtocol(ctx)) { SafeRespond(ctx, 400, "protocol version required"); return; }
            if (!CheckToken(ctx, null)) { SafeRespond(ctx, 401, "client token invalid; regenerate the client package if the server token changed"); return; }
            string body = ReadBody(ctx);
            if (body == null) return;   // 413 / oversized already responded
            LanReportRequest req;
            try
            {
                var json = new JavaScriptSerializer { MaxJsonLength = _config.MaxRequestBytes, RecursionLimit = 256 };
                req = json.Deserialize<LanReportRequest>(body);
            }
            catch { SafeRespond(ctx, 400, "invalid report JSON"); return; }
            if (req == null) { SafeRespond(ctx, 400, "empty report"); return; }
            if (req.Devices == null) req.Devices = new List<LanDeviceDto>();
            string policyCacheKey = req.RequestId + "|oem:" + DriverOemBrands.Revision + "|policy:" + DriverReleasePolicies.Revision + "|machine:" + MachineDriverPackages.Revision + "|" + req.MachineManufacturer + "|" + req.MachineModel + "|" + req.OsArchitecture + "|" + req.OsMajorVersion + "." + req.OsMinorVersion + "." + req.OsBuildNumber;

            policyCacheKey += "|extensions:" + req.SupportsExtensionPackages;
            // Idempotency: a repeated RequestId returns the cached response without re-matching.
            if (!string.IsNullOrEmpty(req.RequestId))
            {
                LanReportResponse cached;
                lock (_cacheLock) { if (_cache.TryGetValue(policyCacheKey, out cached)) { cached.Cached = true; RespondJson(ctx, 200, cached); if (_logger != null) _logger.W("[INFO] 重复请求命中缓存 / Idempotent cache hit: " + Redact(req.RequestId)); return; } }
            }

            var resp = new LanReportResponse { Protocol = LanProtocol.Version, ServerVersion = LanProtocol.ServerVersion };
            Publication published; OfflineRepo repo; lock (_repoLock) { repo = _repo; published = _publication; }
            if (repo == null) { resp.Ok = false; lock (_importLock) resp.Error = "驱动仓库尚未就绪 / repo not loaded: " + _importError + (_repoRetryUtc == DateTime.MinValue ? "" : "；服务端将自动重试加载"); RespondJson(ctx, 503, resp); return; }
            var osTarget = new OsTarget { Architecture = string.IsNullOrEmpty(req.OsArchitecture) ? _config.OsArchitecture : req.OsArchitecture, MajorVersion = req.OsMajorVersion <= 0 ? _config.OsMajorVersion : req.OsMajorVersion, MinorVersion = req.OsMinorVersion, BuildNumber = Math.Max(0, req.OsBuildNumber) };
            MachineDriverPackages.Observe(repo.RepoRoot, req, osTarget);
            var machinePolicies = published.Models;
            MachineDriverPackage machinePackage;
            try { machinePackage = MachineDriverPackages.Select(machinePolicies, req.MachineManufacturer, req.MachineModel, osTarget); }
            catch (Exception ex) { resp.Ok = false; resp.Error = ex.Message; RespondJson(ctx, 409, resp); return; }
            var scopedRecords = repo.Records.Where(r => { var e = repo.EntryForRelative(r.Path); return e != null && (machinePackage != null ? MachineDriverPackages.Contains(e, machinePackage) : !e.SourceName.StartsWith("Machine-", StringComparison.OrdinalIgnoreCase) && !machinePolicies.Any(p => MachineDriverPackages.Contains(e, p))); }).ToList();
            if (machinePackage != null && scopedRecords.Count == 0) { resp.Ok = false; resp.Error = "专用机型整包已不存在，禁止回退通用驱动"; RespondJson(ctx, 409, resp); return; }
            string machineArchive = machinePackage == null ? null : Path.Combine(repo.RepoRoot, ".uda-downloads", published.ModelFiles[machinePackage.SourceName + "|" + machinePackage.PackagePrefix]);
            var ranker = new MatchRanker(scopedRecords, new string[0]);
            // The fallback path must reuse the same inverted index. Rebuilding a MatchRanker for
            // every unmatched device is O(devices * repository size) and made a real 210-device
            // report exceed the old eight-second client timeout on large repositories.
            var unfilteredRanker = ranker;
            var releasePolicies = DriverReleasePolicies.Load(repo.RepoRoot);
            var oemBrands = DriverOemBrands.Load(repo.RepoRoot); string machineBrand = DriverOemBrands.Normalize(req.MachineManufacturer);
            var brandScores = new Dictionary<InfRecord, int>();
            Func<MatchCandidate, int> brandAffinity = c => { int score; if (!brandScores.TryGetValue(c.Record, out score)) brandScores[c.Record] = score = DriverOemBrands.Affinity(machineBrand, DriverOemBrands.Resolve(oemBrands, c.Record.ContentHash, c.Record.SourceName, c.Record.Path, c.Record.Manufacturer, c.Record.Provider)); return score; };
            foreach (var dev in req.Devices)
            {
                var devHardware = dev.HardwareIds ?? new List<string>();
                var devCompatible = dev.CompatibleIds ?? new List<string>();
                HardwareIdNormalizer.AddSafeMatchingAliases(devHardware, devCompatible, dev.DeviceInstanceId);
                var cands = ranker.Rank(devHardware, devCompatible, osTarget);
                if (req.SupportsExtensionPackages && machinePackage == null && osTarget.MajorVersion >= 10)
                {
                    var extensions = new List<LanCandidateDto>();
                    foreach (var extension in cands.Where(c => LanExtensionPackages.IsExtension(c.Record)))
                    {
                        try { extension.Record = LanExtensionPackages.CompleteRepositoryModels(repo, extension.Record); }
                        catch (IOException ex) { if (_logger != null) _logger.W("[WARN] 配套扩展元数据不可读取：" + extension.Record.Path + " " + ex.Message); continue; }
                        catch (UnauthorizedAccessException ex) { if (_logger != null) _logger.W("[WARN] 配套扩展元数据不可读取：" + extension.Record.Path + " " + ex.Message); continue; }
                        var v = InfApplicability.Evaluate(extension.Record, osTarget, devHardware, devCompatible);
                        if (!v.Applicable) continue;
                        var e = repo.EntryForRelative(extension.Record.Path);
                        if (e == null || string.Equals(e.SignatureStatus, "untrusted", StringComparison.OrdinalIgnoreCase)) continue;
                        string brand = DriverOemBrands.Resolve(oemBrands, e.ContentHash, extension.Record.SourceName, extension.Record.Path, extension.Record.Manufacturer, extension.Record.Provider);
                        if (machineBrand.Length > 0 && brand.Length > 0 && brand != machineBrand) continue;
                        // A broad ACPI/codec ID plus unknown OEM provenance cannot prove that
                        // another laptop's calibration applies. Prefer explicit subsystem targeting.

                        string extensionId;
                        try { extensionId = LanExtensionPackages.RepositoryId(repo, extension.Record); }
                        catch (IOException ex) { if (_logger != null) _logger.W("[WARN] 无法读取配套扩展，基础驱动匹配继续：" + e.RelativePath + " " + ex.Message); continue; }
                        catch (UnauthorizedAccessException ex) { if (_logger != null) _logger.W("[WARN] 配套扩展不可读取：" + e.RelativePath + " " + ex.Message); continue; }
                        Guid parsedId; if (!Guid.TryParse(extensionId, out parsedId)) continue;
                        if (!LanExtensionPackages.MatchesExtensionTarget(extension.Record, parsedId, v)) continue;
                        if (v.MatchedInfId.IndexOf("SUBSYS_", StringComparison.OrdinalIgnoreCase) < 0 &&
                            (machineBrand.Length == 0 || brand != machineBrand) && !LanExtensionPackages.IsIntelSgpcPlatformExtension(extension.Record, parsedId)) continue;
                        extensions.Add(new LanCandidateDto {
                            IsExtension = true, ExtensionId = parsedId.ToString("B"), DeviceInstanceId = dev.DeviceInstanceId,
                            FriendlyName = (string.IsNullOrWhiteSpace(dev.FriendlyName) ? dev.DeviceInstanceId : dev.FriendlyName) + " · 配套扩展",
                            InfRelativePath = e.RelativePath, PackageInfPath = string.IsNullOrEmpty(e.PackageRoot) ? Path.GetFileName(e.RelativePath) : e.RelativePath.Substring(e.PackageRoot.Length + 1),
                            ContentHash = e.ContentHash, SignatureStatus = e.SignatureStatus, Provider = extension.Record.Provider,
                            Class = extension.Record.Class, ClassGuid = extension.Record.ClassGuid, DriverVerDate = extension.Record.DriverVerDate, DriverVerVersion = extension.Record.DriverVerVersion,
                            OemBrand = brand, MachineBrand = machineBrand, MatchedId = v.MatchedDeviceId, MatchedInfId = v.MatchedInfId,
                            MatchLevel = (int)v.MatchLevel, Tier = extension.Tier, Decorations = new List<string>(extension.Record.Decorations), NeedInstall = true
                        });
                    }
                    // ExtensionId is an independent lineage. Windows chooses date, then version;
                    // neither the parent's version nor a different ExtensionId competes here.
                    foreach (var lineage in extensions.GroupBy(c => c.ExtensionId, StringComparer.OrdinalIgnoreCase))
                        resp.Candidates.Add(lineage.OrderByDescending(c => { DateTime date; InfParser.TryParseDriverDate(c.DriverVerDate, out date); return date; })
                            .ThenByDescending(c => MatchRanker.VersionScore(c.DriverVerVersion)).First());
                }
                // Full applicability gate: EVERY ranked (device, INF) pair must pass
                // InfApplicability.Evaluate against the reported OS (architecture, major version,
                // build) AND the device's own hardware/compatible IDs. INFs that only passed the
                // coarse OsArchFilter inside the ranker but fail the precise gates (wrong arch,
                // OS major too low, minimum build, no matching Models entry for this device) are
                // never returned. Ranker order already puts exact hardware-ID matches first, so
                // the first applicable candidate is also the most specific one.
                MatchCandidate chosen = null;
                InfApplicabilityResult chosenVerdict = null;
                InfApplicabilityResult firstRejection = null;
                Func<MatchCandidate, bool> usable = cand => {
                    try { cand.Record = LanExtensionPackages.CompleteRepositoryModels(repo, cand.Record); }
                    catch (Exception ex) {
                        if (!(ex is IOException) && !(ex is UnauthorizedAccessException)) throw;
                        if (firstRejection == null) firstRejection = InfApplicabilityResult.Reject("RepositoryMetadataUnavailable", "候选 INF 元数据不可读取：" + cand.Record.Path + " " + ex.Message);
                        return false;
                    }
                    var rejected = CandidatePolicyGate(cand.Record, dev, osTarget);
                    var verdict = rejected ?? InfApplicability.Evaluate(cand.Record, osTarget, devHardware, devCompatible);
                    if (!verdict.Applicable && firstRejection == null) firstRejection = verdict;
                    return verdict.Applicable;
                };
                foreach (var cand in DriverReleasePolicies.Order(cands.Where(usable), releasePolicies, osTarget, devHardware.Concat(devCompatible), brandAffinity))
                {
                    InfApplicabilityResult policyRejection = CandidatePolicyGate(cand.Record, dev, osTarget);
                    if (policyRejection != null)
                    {
                        if (firstRejection == null) firstRejection = policyRejection;
                        continue;
                    }
                    var verdict = InfApplicability.Evaluate(cand.Record, osTarget, devHardware, devCompatible);
                    if (verdict.Applicable) { chosen = cand; chosenVerdict = verdict; break; }
                    if (firstRejection == null) firstRejection = verdict;   // reason of the best-ranked rejected INF
                }
                string friendly = string.IsNullOrWhiteSpace(dev.FriendlyName) ? (string.IsNullOrWhiteSpace(dev.Class) ? "未知硬件" : dev.Class) : dev.FriendlyName;
                if (chosen == null && firstRejection == null && (devHardware.Count > 0 || devCompatible.Count > 0))
                {
                    // The ranker's coarse OsArchFilter can exclude every ID-matching INF before
                    // InfApplicability ever sees it (e.g. a minimum-build decoration on an older
                    // client build). Re-rank WITHOUT the OS filter and run the full gate so the
                    // device is reported with the precise reason (BuildTooLow, ...) instead of a
                    // generic NoMatchingDriver. Evaluate is at least as strict as OsArchFilter,
                    // so an applicable verdict here can only mean the first rank already missed
                    // the record for another reason and it is safe to offer it.
                    var unfiltered = unfilteredRanker.Rank(devHardware, devCompatible, null);
                    foreach (var cand in DriverReleasePolicies.Order(unfiltered.Where(usable), releasePolicies, osTarget, devHardware.Concat(devCompatible), brandAffinity))
                    {
                        InfApplicabilityResult policyRejection = CandidatePolicyGate(cand.Record, dev, osTarget);
                        if (policyRejection != null)
                        {
                            if (firstRejection == null) firstRejection = policyRejection;
                            continue;
                        }
                        var verdict = InfApplicability.Evaluate(cand.Record, osTarget, devHardware, devCompatible);
                        if (verdict.Applicable) { chosen = cand; chosenVerdict = verdict; break; }
                        if (firstRejection == null) firstRejection = verdict;
                    }
                }
                if (chosen == null)
                {
                    // Explicit "no matching driver" instead of a silent gap: the client UI shows this
                    // device under 没有匹配驱动, with the precise reason when the repo HAD a
                    // structurally matching INF that was rejected by the applicability gates.
                    resp.UnmatchedDevices.Add(new LanUnmatchedDeviceDto
                    {
                        DeviceInstanceId = dev.DeviceInstanceId ?? "",
                        FriendlyName = friendly,
                        ReasonCode = firstRejection != null ? firstRejection.ReasonCode : "NoMatchingDriver",
                        Reason = (machinePackage != null ? "机型专用整包中没有适用驱动，已阻止通用回退；" : "") + (firstRejection != null
                            ? firstRejection.Reason
                            : "驱动仓库中没有匹配该设备硬件/兼容 ID 的驱动 / no driver in the repo matches this device's IDs；Instance=" + (dev.DeviceInstanceId ?? "") +
                              "；Hardware=[" + string.Join(",", devHardware.ToArray()) + "]；Compatible=[" + string.Join(",", devCompatible.ToArray()) + "]")
                    });
                    continue;
                }
                var entry = repo.EntryForRelative(chosen.Record.Path);
                // Server-side install-policy verdict (same rule as the client): install/update = true.
                bool needInstall = dev.Bad || string.IsNullOrEmpty(dev.CurrentInf) || string.IsNullOrEmpty(dev.CurrentVersion) ||
                    LanVersion.IsNewer(chosen.Record.DriverVerVersion, dev.CurrentVersion);
                ClusterNodeStatus downloadNode = _clusterRouter != null ? _clusterRouter.Select(req.MachineName) : null;
                Func<MatchCandidate, LanCandidateDto> describe = match => {
                var matchEntry = repo.EntryForRelative(match.Record.Path);
                var matchVerdict = InfApplicability.Evaluate(match.Record, osTarget, devHardware, devCompatible);
                var rule = releasePolicies.Where(p => devHardware.Concat(devCompatible).Select(HardwareIdNormalizer.Normalize).Contains(p.HardwareId) && p.Architecture == osTarget.Architecture && p.MajorVersion == osTarget.MajorVersion && osTarget.BuildNumber >= p.MinBuild && (p.MaxBuild == 0 || osTarget.BuildNumber <= p.MaxBuild) && (string.IsNullOrEmpty(p.DriverClass) || string.Equals(p.DriverClass, match.Record.Class, StringComparison.OrdinalIgnoreCase))).OrderByDescending(p => HardwareIdNormalizer.Specificity(p.HardwareId)).ThenByDescending(p => p.MinBuild).FirstOrDefault();
                return new LanCandidateDto
                {
                    DeviceInstanceId = dev.DeviceInstanceId ?? "",
                    DownloadBaseUrl = downloadNode != null ? downloadNode.Url : "",
                    FriendlyName = friendly,
                    InfRelativePath = match.Record.Path,
                    PackageInfPath = machinePackage != null ? match.Record.Path : matchEntry != null && !string.IsNullOrEmpty(matchEntry.PackageRoot) ? match.Record.Path.Substring(matchEntry.PackageRoot.Length + 1) : Path.GetFileName(match.Record.Path),
                    PackageId = machineArchive == null ? "" : Path.GetFileNameWithoutExtension(machineArchive),
                    ContentHash = matchEntry != null ? matchEntry.ContentHash : match.Record.ContentHash,
                    SignatureStatus = matchEntry != null ? matchEntry.SignatureStatus : "",
                    Provider = match.Record.Provider,
                    OemBrand = DriverOemBrands.Resolve(oemBrands, match.Record.ContentHash, match.Record.SourceName, match.Record.Path, match.Record.Manufacturer, match.Record.Provider), MachineBrand = machineBrand,
                    Class = match.Record.Class,
                    ClassGuid = match.Record.ClassGuid,
                    DriverVerDate = match.Record.DriverVerDate,
                    DriverVerVersion = match.Record.DriverVerVersion,
                    MatchedId = matchVerdict.MatchedDeviceId.Length > 0 ? matchVerdict.MatchedDeviceId : match.MatchedId,
                    MatchedInfId = matchVerdict.MatchedInfId,
                    MatchLevel = (int)matchVerdict.MatchLevel,
                    NeedInstall = dev.Bad || string.IsNullOrEmpty(dev.CurrentInf) || string.IsNullOrEmpty(dev.CurrentVersion) || LanVersion.IsNewer(match.Record.DriverVerVersion, dev.CurrentVersion),
                    Tier = match.Tier,
                    ReleaseChannel = rule == null ? "" : (string.Equals(rule.StableHash, match.Record.ContentHash, StringComparison.OrdinalIgnoreCase) ? "稳定版" : "") + (string.Equals(rule.LatestHash, match.Record.ContentHash, StringComparison.OrdinalIgnoreCase) ? " 最新版" : ""),
                    Decorations = new List<string>(match.Record.Decorations ?? new List<string>())
                }; };
                var recommended = describe(chosen);
                if (machinePackage == null)
                    recommended.Versions = DriverReleasePolicies.Order(cands.Where(usable), releasePolicies, osTarget, devHardware.Concat(devCompatible), brandAffinity)
                        .Where(c => string.Equals(c.Record.Class, chosen.Record.Class, StringComparison.OrdinalIgnoreCase))
                        .GroupBy(c => c.Record.ContentHash, StringComparer.OrdinalIgnoreCase).Select(g => describe(g.First())).ToList();
                resp.Candidates.Add(recommended);
            }
            if (!string.IsNullOrEmpty(req.RequestId))
            {
                lock (_cacheLock)
                {
                    if (_cache.Count >= CacheCap) _cache.Clear();
                    _cache[policyCacheKey] = resp;
                }
            }
            if (_logger != null) _logger.W("[INFO] 上报匹配 / Report: " + req.Devices.Count + " devices => " + resp.Candidates.Count + " candidates, " + resp.UnmatchedDevices.Count + " unmatched (machine=" + Redact(req.MachineModel) + ")");
            RespondJson(ctx, 200, resp);
        }

        private static InfApplicabilityResult CandidatePolicyGate(InfRecord record, LanDeviceDto device, OsTarget target)
        {
            if (record == null) return InfApplicabilityResult.Reject("NullRecord", "候选为空");
            // Extension INFs attach metadata/components to a parent device. Comparing their package
            // version with the parent function driver's version creates false updates; only offer an
            // Extension package when the reported node itself belongs to the Extension setup class.
            const string extensionGuid = "{e2f84ce7-8efa-411c-aa69-97454ca4cb57}";
            bool extensionInf = string.Equals(record.Class, "Extension", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(record.ClassGuid, extensionGuid, StringComparison.OrdinalIgnoreCase);
            bool extensionDevice = device != null && (string.Equals(device.Class, "Extension", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(device.ClassGuid, extensionGuid, StringComparison.OrdinalIgnoreCase));
            if (extensionInf && !extensionDevice)
                return InfApplicabilityResult.Reject("ExtensionParentVersionNotComparable",
                    "扩展驱动不能用父设备版本判断更新 / extension package version is not comparable with the parent device driver");

            // Microsoft inbox driver versions commonly encode the Windows build as 10.0.BUILD.*.
            // A Win11 26100 inbox INF can expose generic NTamd64.10 model decorations yet pnputil
            // correctly rejects it on Win10 19045. Enforce the build encoded by that version here.
            if (target != null && target.BuildNumber > 0 &&
                !string.IsNullOrEmpty(record.Provider) && record.Provider.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string[] p = (record.DriverVerVersion ?? "").Split('.');
                int major, minor, build;
                if (p.Length >= 3 && int.TryParse(p[0], out major) && int.TryParse(p[1], out minor) && int.TryParse(p[2], out build) &&
                    major == 10 && minor == 0 && build > target.BuildNumber)
                    return InfApplicabilityResult.Reject("InboxBuildTooHigh",
                        "微软内置驱动属于更高Windows构建 / Microsoft inbox driver targets newer Windows build: " + build + " > " + target.BuildNumber);
            }
            return null;
        }

        private void HandleDownload(HttpListenerContext ctx)
        {
            bool guarded = !ServerStorage.Config(_config.RepoRoot).DatabaseOnly;
            if (guarded && !_packageAccess.TryEnterReadLock(0)) { SafeRespond(ctx, 503, "repository maintenance running; retry download shortly"); return; }
            Interlocked.Increment(ref _activeDownloads);
            try
            {
            if (!CheckProtocol(ctx)) { SafeRespond(ctx, 400, "protocol version required"); return; }
            if (!CheckToken(ctx, null)) { SafeRespond(ctx, 401, "token required"); return; }
            if (guarded && ImportRunningNow && !_catalogDeleteRunning) { SafeRespond(ctx, 503, "repository maintenance running; retry download shortly"); return; }
            string rel = Query(ctx, "path"), requestedPackage = Query(ctx, "package") ?? "";
            Publication published; OfflineRepo repo; lock (_repoLock) { repo = _repo; published = _publication; }
            if (repo == null) { SafeRespond(ctx, 503, "repo not loaded"); return; }
            if (string.IsNullOrWhiteSpace(rel) || LanSecurity.SafeAbsolute(repo.RepoRoot, rel) == null) { SafeRespond(ctx, 400, "path traversal rejected"); return; }
            if (requestedPackage.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(requestedPackage, "^model-[a-f0-9]{64}$")) { SafeRespond(ctx, 400, "invalid model package"); return; }
            var entry = repo.EntryForRelative(rel);
            string archivePath = null, downloadHash = entry == null ? "" : entry.ContentHash;
            bool currentPackage = entry != null && (requestedPackage.Length == 0 || published.Models.Any(m => MachineDriverPackages.Contains(entry, m) && Path.GetFileNameWithoutExtension(published.ModelFiles[m.SourceName + "|" + m.PackagePrefix]) == requestedPackage));
            RetiredDriverDownload retired;
            if (!currentPackage && _retiredDownloads.TryGetValue(DownloadKey(rel, requestedPackage), out retired) && string.CompareOrdinal(retired.ExpiresUtc, DateTime.UtcNow.ToString("o")) > 0)
            {
                string revoked = ServerStorage.Read(repo.RepoRoot, "repo.revoked-download-paths.json");
                var revokedPaths = new HashSet<string>(revoked == null ? new List<string>() : new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<List<string>>(revoked), StringComparer.OrdinalIgnoreCase);
                if (!revokedPaths.Contains(rel) && !revokedPaths.Contains(OfflineRepoGenerator.ResolvePathAlias(repo.RepoRoot, rel)))
                { archivePath = LanSecurity.SafeAbsolute(repo.RepoRoot, ".uda-downloads/" + retired.Archive); downloadHash = retired.Hash; }
            }
            if (archivePath == null)
            {
                if (entry == null) { rel = OfflineRepoGenerator.ResolvePathAlias(repo.RepoRoot, rel); entry = repo.EntryForRelative(rel); }
                if (entry == null) { SafeRespond(ctx, 404, "driver is not published in repository"); return; }
                downloadHash = entry.ContentHash;
                archivePath = OfflineRepoGenerator.DownloadArchivePath(repo.RepoRoot, entry);
                var selected = published.Models.FirstOrDefault(m => MachineDriverPackages.Contains(entry, m) && (requestedPackage.Length == 0 || Path.GetFileNameWithoutExtension(published.ModelFiles[m.SourceName + "|" + m.PackagePrefix]) == requestedPackage));
                if (requestedPackage.Length > 0 && selected == null) { SafeRespond(ctx, 404, "model package does not contain this driver"); return; }
                if (selected != null) archivePath = Path.Combine(repo.RepoRoot, ".uda-downloads", published.ModelFiles[selected.SourceName + "|" + selected.PackagePrefix]);
            }
            if (TestDownloadOverride == null && !File.Exists(archivePath)) { SafeRespond(ctx, 503, "driver archive is being prepared; retry shortly"); return; }
            string downloadDir = entry == null ? Path.GetDirectoryName(rel) : entry.PackageRoot ?? Path.GetDirectoryName(rel);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/zip";
            ctx.Response.Headers[LanProtocol.HeaderInfHash] = downloadHash;
            try
            {
                if (_logger != null) _logger.W("[INFO] 开始流式下载 / Download starting: " + rel + "; active=" + Volatile.Read(ref _activeDownloads));
                if (TestDownloadOverride != null)
                {
                    byte[] zip = TestDownloadOverride(rel);
                    ctx.Response.ContentLength64 = zip.Length;
                    ctx.Response.OutputStream.Write(zip, 0, zip.Length);
                }
                else
                {
                    var archiveInfo = new FileInfo(archivePath);
                    ctx.Response.SendChunked = false;
                    ctx.Response.KeepAlive = true;
                    ctx.Response.ContentLength64 = archiveInfo.Length;
                    using (var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
                        input.CopyTo(ctx.Response.OutputStream, 1024 * 1024);
                }
                if (_logger != null) _logger.W("[INFO] 下载完成 / Download completed: " + rel);
            }
            catch (Exception ex)
            {
                if (_logger != null) _logger.W("[WARN] 下载流中断 / Download stream failed: " + rel + " => " + ex.Message);
                try { ctx.Response.Abort(); } catch { }
            }
            }
            finally { Interlocked.Decrement(ref _activeDownloads); if (guarded) _packageAccess.ExitReadLock(); }
        }

        private void HandleImport(HttpListenerContext ctx)
        {
            if (!CheckProtocol(ctx)) { SafeRespond(ctx, 400, "protocol version required"); return; }
            // Read-only client APIs may be anonymous on a trusted LAN, but repository
            // mutation must never become anonymous merely because no client token is set.
            if (!string.IsNullOrWhiteSpace(_config.AdminCredentialsFile))
            {
                if (!_webAdmin.IsAuthenticated(ctx)) { SafeRespond(ctx, 401, "admin login required"); return; }
            }
            else if (string.IsNullOrEmpty(_config.Token) || !CheckToken(ctx, null))
            {
                SafeRespond(ctx, 401, "admin authentication required"); return;
            }
            string src = Query(ctx, "src");
            if (string.IsNullOrWhiteSpace(src)) { SafeRespond(ctx, 400, "missing src"); return; }
            if (src.StartsWith("\\\\", StringComparison.Ordinal) || src.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    src = SmbSourceCredentials.Normalize(src);
                    var saved = SmbSourceCredentials.Load(_config).Where(e => (src + "\\").StartsWith(e.Root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)).OrderByDescending(e => e.Root.Length).FirstOrDefault();
                    if (saved != null) SmbSourceCredentials.Connect(saved);
                }
                catch (Exception ex) { SafeRespond(ctx, 400, ex.Message); return; }
            }
            if (src.Contains("..")) { SafeRespond(ctx, 400, "path traversal rejected"); return; }
            if (!Directory.Exists(src)) { SafeRespond(ctx, 404, "source directory not found on server"); return; }
            try { WebAdminController.ValidateUploadedSource(_config.RepoRoot, src); }
            catch (Exception ex) { SafeRespond(ctx, 400, ex.Message); return; }
            // Credential-authenticated administrators may register a new local source. Token-only legacy
            // deployments remain confined to explicit allow-roots.
            if (string.IsNullOrWhiteSpace(_config.AdminCredentialsFile) && (_config.ImportAllowRoots == null || _config.ImportAllowRoots.Count == 0 || !LanSecurity.UnderAny(src, _config.ImportAllowRoots)))
            { if (_logger != null) _logger.W("[WARN] 拒绝非白名单导入路径 / Import source not allowlisted: " + src); SafeRespond(ctx, 403, "import source not allowlisted"); return; }
            RegisterImportSource(src);
            if (!string.IsNullOrWhiteSpace(_config.AdminCredentialsFile))
            {
                lock (_importLock)
                {
                    if (_importRunning)
                    {
                        _watchUpdatePending = true;
                        if (!_pendingIncrementalSources.Contains(src, StringComparer.OrdinalIgnoreCase))
                            _pendingIncrementalSources.Add(src);
                        _importMessage = (_importMessage ?? "") + "；新驱动源已保存并排队，等待当前任务完成后增量追加";
                        RespondJson(ctx, 202, new { ok = true, accepted = true, queued = true, statusUrl = "/api/admin/import-status" });
                        return;
                    }
                    _importRunning = true;
                    _importRunsStarted++;
                    _importStage = "starting";
                    _importMessage = "正在准备驱动增量追加";
                    _importError = "";
                    _importStartedUtc = DateTime.UtcNow;
                    _importFinishedUtc = DateTime.MinValue;
                    _importInfCount = 0;
                    _importSourceInfCount = 0;
                    _importDuplicateCount = 0;
                    _importAddedCount = 0;
                    _importModifiedCount = 0;
                }
                ThreadPool.QueueUserWorkItem(delegate { RunImportJob(src); });
                RespondJson(ctx, 202, new { ok = true, accepted = true, statusUrl = "/api/admin/import-status" });
                return;
            }
            try
            {
                int sourceInfCount = 0;
                try { sourceInfCount = Directory.EnumerateFiles(src, "*.inf", SearchOption.AllDirectories).Count(); }
                catch { }
                lock (_importLock)
                {
                    _importSourceInfCount = sourceInfCount;
                    _importMessage = "发现 " + sourceInfCount + " 个源 INF，开始复制与解析";
                }
                var gen = new OfflineRepoGenerator(ImportSignatureVerifier ?? new WinTrustSignatureVerifier(), _logger);
                var osTarget = new OsTarget { Architecture = _config.OsArchitecture, MajorVersion = _config.OsMajorVersion };
                var sources = BuildSources(src);
                MutateRepository(workRoot => gen.AppendOrganizedSource(workRoot, src, _cts.Token, null), null, null, false);
                FinalizeUploadedSource(src);
                if (_logger != null) _logger.W("[INFO] 仓库导入/重建完成 / Repo import/rebuild done: " + src);
                RespondJson(ctx, 200, new { ok = true, infCount = RepoCount() });
            }
            catch (Exception ex) { RecordError("sync repo import failed: " + ex.Message); SafeRespond(ctx, 500, "import failed: " + ex.Message); }
        }

        private void RunImportJob(string src)
        {
            try
            {
                int sourceInfCount = 0;
                if (!string.IsNullOrWhiteSpace(src) && Directory.Exists(src))
                {
                    try
                    {
                        sourceInfCount = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
                            .Count(x => string.Equals(Path.GetExtension(x), ".inf", StringComparison.OrdinalIgnoreCase));
                    }
                    catch (Exception ex)
                    {
                        if (_logger != null) _logger.W("[WARN] 统计源 INF 失败 / Failed to count source INFs: " + ex.Message);
                    }
                }
                lock (_importLock)
                {
                    _importSourceInfCount = sourceInfCount;
                    _importMessage = "发现 " + sourceInfCount + " 个源 INF，开始增量解析";
                }
                var gen = new OfflineRepoGenerator(ImportSignatureVerifier ?? new WinTrustSignatureVerifier(), _logger);
                var osTarget = new OsTarget { Architecture = _config.OsArchitecture, MajorVersion = _config.OsMajorVersion };
                var sources = BuildSources(src);
                var progress = new InlineProgress(delegate(string message)
                {
                    lock (_importLock) { _importStage = "building"; _importMessage = message ?? ""; }
                });
                OfflineRepoGenerationReport report;
                bool repositoryExists = OfflineRepoGenerator.TryLoadManifest(_config.RepoRoot) != null;
                if (repositoryExists && string.IsNullOrWhiteSpace(src))
                    throw new InvalidOperationException("已有仓库的追加任务必须指定新增驱动目录；已拒绝整库重建");
                report = MutateRepository(workRoot => gen.AppendOrganizedSource(workRoot, src, _cts.Token, progress), progress, null, false);
                FinalizeUploadedSource(src);
                lock (_importLock) { _importStage = "loading"; _importMessage = "增量索引已发布并热加载"; }
                lock (_importLock)
                {
                    _importInfCount = RepoCount();
                    _importDuplicateCount = report.Duplicates;
                    _importAddedCount = report.Added;
                    _importModifiedCount = report.Modified;
                    _importStage = "completed";
                    _importMessage = repositoryExists ? "新增驱动目录已追加完成（未复制原仓库）" : "首次驱动仓库创建完成";
                    _importFinishedUtc = DateTime.UtcNow;
                    _importRunning = false;
                }
                if (_logger != null) _logger.W("[INFO] 仓库导入/重建完成 / Repo import/rebuild done: " + src);
                ReschedulePendingWatchUpdate();
            }
            catch (Exception ex)
            {
                lock (_importLock)
                {
                    _importStage = "failed";
                    _importMessage = "驱动增量导入失败";
                    _importError = ex.Message;
                    _importFinishedUtc = DateTime.UtcNow;
                    _importRunning = false;
                }
                RecordError("repo import/rebuild FAILED (previous repo kept / 仓库重建失败，保留上一份可用仓库): " + ex.Message);
                if (_logger != null) _logger.W("[ERROR] 仓库导入/重建失败 / Repo import failed: " + ex.Message);
                ReschedulePendingWatchUpdate();
            }
        }

        private List<string> BuildSources(string extra)
        {
            var result = new List<string>();
            foreach (string source in _config.Sources ?? new List<string>())
                if (!string.IsNullOrWhiteSpace(source) && !result.Contains(source, StringComparer.OrdinalIgnoreCase)) result.Add(source);
            if (!string.IsNullOrWhiteSpace(extra) && !result.Contains(extra, StringComparer.OrdinalIgnoreCase)) result.Add(extra);
            return result;
        }

        private void ReschedulePendingWatchUpdate()
        {
            lock (_importLock)
            {
                if (!_watchUpdatePending || IsStopping) return;
                _importStage = "pending";
                _importMessage = "新驱动源已保存，等待当前任务释放后合并重建";
                if (_watchDebounceTimer == null) _watchDebounceTimer = new Timer(WatchDebounceElapsed, null, 10000, Timeout.Infinite);
                else _watchDebounceTimer.Change(10000, Timeout.Infinite);
            }
        }

        // ----- duplicate audit / cleanup (driver library maintenance) -----

        /// <summary>Same mutation-safety gate as HandleImport: anonymous read-only APIs may exist on a
        /// trusted LAN, but repository mutation must never become anonymous.</summary>
        private bool IsRepoMutatorAuthorized(HttpListenerContext ctx)
        {
            if (!CheckProtocol(ctx)) { SafeRespond(ctx, 400, "protocol version required"); return false; }
            if (!string.IsNullOrWhiteSpace(_config.AdminCredentialsFile))
            {
                if (!_webAdmin.IsAuthenticated(ctx)) { SafeRespond(ctx, 401, "admin login required"); return false; }
            }
            else if (string.IsNullOrEmpty(_config.Token) || !CheckToken(ctx, null))
            {
                SafeRespond(ctx, 401, "admin authentication required"); return false;
            }
            return true;
        }

        private void HandleDuplicates(HttpListenerContext ctx)
        {
            if (!IsRepoMutatorAuthorized(ctx)) return;   // read-only audit, but it exposes library contents
            string repoRoot = _config.RepoRoot;
            if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) { SafeRespond(ctx, 503, "repo not loaded"); return; }
            try
            {
                var gen = new OfflineRepoGenerator(ImportSignatureVerifier ?? new WinTrustSignatureVerifier(), _logger);
                OfflineRepoDuplicateReport report = gen.AuditDuplicates(repoRoot, _cts.Token);
                RespondJson(ctx, 200, new
                {
                    ok = true,
                    total = report.TotalCount,
                    kept = report.KeptCount,
                    duplicateCount = report.DuplicateCount,
                    groups = report.Groups.Select(g => new
                    {
                        version = g.DriverVerVersion,
                        kept = g.KeptRelativePath,
                        duplicates = g.DuplicateRelativePaths
                    }).ToList()
                });
            }
            catch (Exception ex) { RecordError("duplicate audit failed: " + ex.Message); SafeRespond(ctx, 500, "duplicate audit failed: " + ex.Message); }
        }

        private void HandleAiTask(HttpListenerContext ctx)
        {
            if (!IsRepoMutatorAuthorized(ctx)) return;
            string body = ReadBody(ctx); if (body == null) return;
            try
            {
                var args = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
                string action = args != null && args.ContainsKey("action") ? Convert.ToString(args["action"]) : "";
                if (action == "pause" || action == "resume" || action == "cancel")
                {
                    string taskId = args.ContainsKey("id") ? Convert.ToString(args["id"]) : "";
                    if (!_aiTasks.Control(taskId, action)) { SafeRespond(ctx, 409, "任务已结束或当前状态不支持此操作，请刷新任务列表"); return; }
                    RespondJson(ctx, 202, new { ok = true, id = taskId, statusUrl = "/api/admin/ai/status" }); return;
                }
                if (action != "releases" && action != "news") { SafeRespond(ctx, 400, "仅支持版本整理、驱动资讯和任务控制操作"); return; }
                string id;
                if (!_aiTasks.Begin(action, false, out id)) { RespondJson(ctx, 409, new { ok = false, id, error = "同类型任务正在运行，请在任务列表中查看、暂停或中断；不同类型任务可并行" }); return; }
                RespondJson(ctx, 202, new { ok = true, id, statusUrl = "/api/admin/ai/status" });
            }
            catch (InvalidOperationException ex) { SafeRespond(ctx, 409, ex.Message); }
            catch (Exception) { SafeRespond(ctx, 400, "invalid AI task"); }
        }

        private void HandleOrganize(HttpListenerContext ctx)
        {
            if (!IsRepoMutatorAuthorized(ctx)) return;
            lock (_importLock)
            {
                if (_importRunning) { RespondJson(ctx, 409, new { ok = false, error = "repo mutation job already running" }); return; }
                _importRunning = true; _importStage = "organizing"; _importMessage = "正在分析驱动分类"; _importError = ""; _importStartedUtc = DateTime.UtcNow; _importFinishedUtc = DateTime.MinValue;
            }
            ThreadPool.QueueUserWorkItem(delegate {
                try
                {
                    var generator = new OfflineRepoGenerator(ImportSignatureVerifier ?? new WinTrustSignatureVerifier(), _logger);
                    int moved = MutateRepository(workRoot => generator.Organize(workRoot, _cts.Token, new InlineProgress(message => { lock (_importLock) _importMessage = message; })));
                    if (!ServerStorage.Config(_config.RepoRoot).DatabaseOnly) LoadRepo();
                    lock (_importLock) { _importStage = "completed"; _importMessage = "整理完成：移动 " + moved + " 个完整驱动包，未复制仓库"; }
                }
                catch (Exception ex) { lock (_importLock) { _importStage = "failed"; _importError = ex.Message; _importMessage = "整理失败，已尝试回滚"; } RecordError("organization failed: " + ex); }
                finally { lock (_importLock) { _importRunning = false; _importFinishedUtc = DateTime.UtcNow; } ReschedulePendingWatchUpdate(); }
            });
            RespondJson(ctx, 202, new { ok = true, statusUrl = "/api/admin/import-status" });
        }

        private void HandleDedupe(HttpListenerContext ctx)
        {
            if (!IsRepoMutatorAuthorized(ctx)) return;
            string repoRoot = _config.RepoRoot;
            if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) { SafeRespond(ctx, 503, "repo not loaded"); return; }
            if (string.IsNullOrWhiteSpace(_config.AdminCredentialsFile))
            {
                // Token-only legacy deployments run the cleanup synchronously, exactly like the
                // legacy import path: the caller is a script holding the server token.
                try
                {
                    var gen = new OfflineRepoGenerator(ImportSignatureVerifier ?? new WinTrustSignatureVerifier(), _logger);
                    OfflineRepoDuplicateReport report = MutateRepository(workRoot => gen.Clean(workRoot, _cts.Token, null));
                    if (!ServerStorage.Config(_config.RepoRoot).DatabaseOnly) LoadRepo();
                    RespondJson(ctx, 200, new { ok = true, removed = report.DuplicateCount, kept = report.KeptCount, cleaned = report.Cleaned });
                }
                catch (Exception ex) { RecordError("repo dedupe failed (previous repo kept / 仓库清理失败，保留上一份可用仓库): " + ex.Message); SafeRespond(ctx, 500, "dedupe failed: " + ex.Message); }
                return;
            }
            lock (_importLock)
            {
                // Import and dedupe both rewrite the repo; never run them concurrently.
                if (_importRunning) { RespondJson(ctx, 409, new { ok = false, error = "repo mutation job already running" }); return; }
                _importRunning = true;
                _importRunsStarted++;
                _importStage = "dedupe-starting";
                _importMessage = "正在检查重复驱动";
                _importError = "";
                _importStartedUtc = DateTime.UtcNow;
                _importFinishedUtc = DateTime.MinValue;
            }
            ThreadPool.QueueUserWorkItem(delegate { RunDedupeJob(); });
            RespondJson(ctx, 202, new { ok = true, accepted = true, statusUrl = "/api/admin/import-status" });
        }

        private void HandleDeleteDrivers(HttpListenerContext ctx)
        {
            if (!IsRepoMutatorAuthorized(ctx)) return;
            string body = ReadBody(ctx);
            if (body == null) return;
            var ids = new List<string>();
            try
            {
                var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
                object raw;
                if (data != null && data.TryGetValue("ids", out raw))
                    foreach (object value in (raw as System.Collections.IEnumerable) ?? new object[0])
                        if (value != null && !string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture)))
                            ids.Add(Convert.ToString(value, CultureInfo.InvariantCulture));
            }
            catch { SafeRespond(ctx, 400, "invalid delete request"); return; }
            if (ids.Count == 0) { SafeRespond(ctx, 400, "no drivers selected"); return; }
            lock (_importLock)
            {
                if (_importRunning) { RespondJson(ctx, 409, new { ok = false, error = "repo mutation job already running" }); return; }
                _catalogDeleteRunning = ServerStorage.Config(_config.RepoRoot).DatabaseOnly;
                _importRunning = true; _importStage = "delete-running"; _importMessage = "正在删除选中的驱动";
                _importStartedUtc = DateTime.UtcNow; _importFinishedUtc = DateTime.MinValue;
            }
            try
            {
                var gen = new OfflineRepoGenerator(ImportSignatureVerifier ?? new WinTrustSignatureVerifier(), _logger);
                int removed;
                if (ServerStorage.Config(_config.RepoRoot).DatabaseOnly)
                {
                    _catalogDeleteRunning = true;
                    // Serialize with archive writers while allowing concurrent download readers.
                    _packageAccess.EnterUpgradeableReadLock();
                    try {
                        lock (_maintenanceGate) removed = ServerStorage.DeleteCatalogEntries(_config.RepoRoot, ids, snapshot => {
                            var prepared = PreparePublication(snapshot, _config.RepoRoot);
                            lock (_repoLock) { _repo = snapshot; _publication = prepared; _archiveSetReady = true; }
                            lock (_cacheLock) _cache.Clear();
                        });
                    }
                    finally { _packageAccess.ExitUpgradeableReadLock(); }
                }
                else removed = MutateRepository(workRoot => gen.DeleteEntries(workRoot, ids, _cts.Token, null));
                lock (_importLock) { _importRunning = false; _importStage = "completed"; _importMessage = "已删除 " + removed + " 个驱动"; _importFinishedUtc = DateTime.UtcNow; }
                RespondJson(ctx, 200, new { ok = true, removed = removed, kept = RepoCount() });
            }
            catch (Exception ex)
            {
                lock (_importLock) { _importRunning = false; _importStage = "failed"; _importError = ex.Message; _importFinishedUtc = DateTime.UtcNow; }
                RecordError("driver delete failed: " + ex.Message); SafeRespond(ctx, 500, "driver delete failed: " + ex.Message);
            }
            finally { _catalogDeleteRunning = false; ReschedulePendingWatchUpdate(); }
        }

        private void RunDedupeJob()
        {
            try
            {
                var gen = new OfflineRepoGenerator(ImportSignatureVerifier ?? new WinTrustSignatureVerifier(), _logger);
                var progress = new InlineProgress(delegate(string message)
                {
                    lock (_importLock) { _importStage = "dedupe-running"; _importMessage = message ?? ""; }
                });
                OfflineRepoDuplicateReport report = MutateRepository(workRoot => gen.Clean(workRoot, _cts.Token, progress));
                lock (_importLock) { _importStage = "loading"; _importMessage = "清理完成，正在热加载仓库"; }
                if (!ServerStorage.Config(_config.RepoRoot).DatabaseOnly) LoadRepo();
                lock (_importLock)
                {
                    _importInfCount = RepoCount();
                    _importDuplicateCount = report.DuplicateCount;
                    _importStage = "completed";
                    _importMessage = report.DuplicateCount == 0
                        ? "未发现重复驱动"
                        : "重复清理完成：移除 " + report.DuplicateCount + " 个重复 INF，保留 " + report.KeptCount + " 个";
                    _importFinishedUtc = DateTime.UtcNow;
                    _importRunning = false;
                }
                if (_logger != null) _logger.W("[INFO] 驱动库重复清理完成 / Repo dedupe done: removed " + report.DuplicateCount);
                ReschedulePendingWatchUpdate();
            }
            catch (Exception ex)
            {
                lock (_importLock)
                {
                    _importStage = "failed";
                    _importMessage = "重复清理失败";
                    _importError = ex.Message;
                    _importFinishedUtc = DateTime.UtcNow;
                    _importRunning = false;
                }
                RecordError("repo dedupe FAILED (previous repo kept / 仓库清理失败，保留上一份可用仓库): " + ex.Message);
                if (_logger != null) _logger.W("[ERROR] 驱动库重复清理失败 / Repo dedupe failed: " + ex.Message);
                ReschedulePendingWatchUpdate();
            }
        }

        private void HandleImportStatus(HttpListenerContext ctx)
        {
            if (!_webAdmin.IsAuthenticated(ctx)) { SafeRespond(ctx, 401, "admin login required"); return; }
            lock (_importLock)
            {
                bool activeOrQueued = _importRunning || _watchUpdatePending;
                RespondJson(ctx, 200, new
                {
                    ok = true,
                    running = activeOrQueued,
                    stage = _importStage,
                    message = _importMessage,
                    error = _importError,
                    startedAt = _importStartedUtc == DateTime.MinValue ? "" : _importStartedUtc.ToString("o", CultureInfo.InvariantCulture),
                    finishedAt = _importFinishedUtc == DateTime.MinValue ? "" : _importFinishedUtc.ToString("o", CultureInfo.InvariantCulture),
                    elapsedSeconds = _importStartedUtc == DateTime.MinValue ? 0 : (int)((activeOrQueued ? DateTime.UtcNow : _importFinishedUtc) - _importStartedUtc).TotalSeconds,
                    infCount = _importInfCount,
                    sourceInfCount = _importSourceInfCount,
                    duplicateInfCount = _importDuplicateCount,
                    addedInfCount = _importAddedCount,
                    modifiedInfCount = _importModifiedCount
                });
            }
        }

        // ----- request helpers -----

        private bool CheckProtocol(HttpListenerContext ctx)
        {
            string v = ctx.Request.Headers[LanProtocol.HeaderProtocol];
            return !string.IsNullOrEmpty(v) && v == LanProtocol.Version.ToString(CultureInfo.InvariantCulture);
        }

        // v5.2.0 clients accidentally sent protocol 1 for install telemetry while using protocol 2
        // for matching/download. Keep this authenticated endpoint backward-compatible so their
        // locally queued logs can be delivered after the server upgrade.
        private bool CheckTelemetryProtocol(HttpListenerContext ctx)
        {
            string v = ctx.Request.Headers[LanProtocol.HeaderProtocol];
            return CheckProtocol(ctx) || string.Equals(v, "1", StringComparison.Ordinal);
        }

        private bool CheckToken(HttpListenerContext ctx, string bodyToken)
        {
            if (string.IsNullOrEmpty(_config.Token)) return true;   // token optional
            string presented = ctx.Request.Headers[LanProtocol.HeaderToken];
            if (string.IsNullOrEmpty(presented) && bodyToken != null) presented = bodyToken;
            return LanSecurity.ConstantTimeEquals(presented ?? "", _config.Token);
        }

        private string ReadBody(HttpListenerContext ctx)
        {
            long len = ctx.Request.ContentLength64;
            if (len > _config.MaxRequestBytes) { SafeRespond(ctx, 413, "request too large"); return null; }
            using (var ms = new MemoryStream())
            {
                byte[] buf = new byte[16 * 1024];
                long read = 0;
                using (var s = ctx.Request.InputStream)
                {
                    while (read < _config.MaxRequestBytes)
                    {
                        int n = s.Read(buf, 0, (int)Math.Min(buf.Length, _config.MaxRequestBytes - read));
                        if (n <= 0) break;
                        ms.Write(buf, 0, n);
                        read += n;
                        if (ms.Length > _config.MaxRequestBytes) { SafeRespond(ctx, 413, "request too large"); return null; }
                    }
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        private static string Query(HttpListenerContext ctx, string key)
        {
            // HttpListener.QueryString uses the system code page on some .NET Framework/Windows
            // combinations, corrupting UTF-8 percent-encoded paths. Decode the raw query explicitly.
            string raw = ctx.Request.Url != null ? ctx.Request.Url.Query : "";
            if (raw.StartsWith("?", StringComparison.Ordinal)) raw = raw.Substring(1);
            foreach (string part in raw.Split('&'))
            {
                int equals = part.IndexOf('=');
                string rawName = equals >= 0 ? part.Substring(0, equals) : part;
                string rawValue = equals >= 0 ? part.Substring(equals + 1) : "";
                string name;
                try { name = Uri.UnescapeDataString(rawName.Replace("+", " ")); }
                catch { continue; }
                if (!string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) continue;
                try { return Uri.UnescapeDataString(rawValue.Replace("+", " ")); }
                catch { return null; }
            }
            return null;
        }

        private int RepoCount() { lock (_repoLock) return _repo != null ? _repo.Count : 0; }

        // Some .NET Framework ZIP implementations read Position even for a non-seekable output.
        // Track bytes without seeking the HTTP stream; ownership stays with HttpListener.
        private sealed class ForwardZipStream : Stream
        {
            private readonly Stream inner;
            private long written;
            public ForwardZipStream(Stream stream) { inner = stream; }
            public override bool CanRead { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { return written; } }
            public override long Position { get { return written; } set { throw new NotSupportedException(); } }
            public override void Flush() { inner.Flush(); }
            public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); written += count; }
            public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
        }

        private static byte[] BuildPackageZip(string packageDir)
        {
            using (var output = new MemoryStream())
            {
                using (var za = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    foreach (string file in Directory.GetFiles(packageDir, "*", SearchOption.AllDirectories))
                    {
                        string entryName = PathRelative(packageDir, file).Replace('\\', '/');
                        var item = za.CreateEntry(entryName, CompressionLevel.Fastest);
                        using (var es = item.Open()) using (var fs = File.OpenRead(file)) fs.CopyTo(es, 1024 * 1024);
                    }
                }
                return output.ToArray();
            }
        }

        private static string PathRelative(string root, string full)
        {
            string f = Path.GetFullPath(full).TrimEnd('\\') + "\\";
            string r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            if (!f.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return Path.GetFileName(full);
            return f.Substring(r.Length).TrimEnd('\\');
        }

        private static void RespondJson(HttpListenerContext ctx, int status, object body)
        {
            string json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024, RecursionLimit = 256 }.Serialize(body);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            try { ctx.Response.OutputStream.Write(bytes, 0, bytes.Length); } catch { }
        }

        private static void SafeRespond(HttpListenerContext ctx, int status, string message)
        {
            try
            {
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                byte[] bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new { ok = false, error = message }));
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch { }
        }

        private static string Redact(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= 4 ? "****" : s.Substring(0, 2) + "***" + s.Substring(s.Length - 2);
        }
    }

    // =================================================================================
    //  Client
    // =================================================================================

    /// <summary>
    /// LAN driver client. On Run it: (1) detects NICs lacking a driver and, if a local bootstrap repo is
    /// available, installs the NIC driver offline so the machine can reach the network; (2) probes the server
    /// with retry/backoff; (3) reports the hardware inventory; (4) downloads each matched package, verifies the
    /// INF SHA-256 against the manifest and its signature, and installs it into a client-controlled staging dir.
    /// All network calls honour a CancellationToken and use bounded timeouts; duplicate reports are idempotent.
    /// </summary>
    public static class ClientTelemetryQueue
    {
        private static readonly object Gate = new object();
        internal static string TestDirectory;
        internal static void UseTestDirectory(string root) { TestDirectory = Path.GetFullPath(root); }
        public static void Enqueue(string directory, DriverInstallEvent entry)
        {
            lock (Gate)
            {
                Guid id;
                if (!Guid.TryParseExact(entry.EventId, "N", out id)) throw new ArgumentException("Invalid event ID");
                Directory.CreateDirectory(directory);
                if (Directory.EnumerateFiles(directory, "*.json").Sum(f => new FileInfo(f).Length) > 100L * 1024 * 1024) throw new IOException("日志重传队列已达 100 MiB，原日志继续保存在 Logs 目录");
                string path = Path.Combine(directory, entry.EventId + ".json");
                if (File.Exists(path)) return;
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(entry), new UTF8Encoding(false));
                File.Move(temporary, path);
            }
        }
        private static readonly ConcurrentDictionary<string, object> SendLocks = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        public static int Flush(string directory, Func<DriverInstallEvent, bool> send, int maximum)
        {
            lock (SendLocks.GetOrAdd(directory, key => new object()))
            {
                string[] files;
                lock (Gate) {
                    if (!Directory.Exists(directory)) return 0;
                    files = Directory.EnumerateFiles(directory, "*.json").OrderBy(f => File.GetLastWriteTimeUtc(f)).Take(maximum).ToArray();
                }
                int sent = 0;
                foreach (string file in files)
                {
                    DriverInstallEvent entry;
                    lock (Gate) { if (!File.Exists(file)) continue; entry = new JavaScriptSerializer().Deserialize<DriverInstallEvent>(File.ReadAllText(file)); }
                    // Never hold the local persistence lock during a potentially slow network call.
                    if (!send(entry)) break;
                    lock (Gate) { if (File.Exists(file)) File.Delete(file); } sent++;
                }
                return sent;
            }
        }
    }

    public sealed class LanDriverClient
    {
        private string _telemetryRun = Guid.NewGuid().ToString("N");
        private readonly string _telemetryMachineId = TelemetryMachineId();
        private string _telemetryMac;
        private int _telemetryChunk, _telemetryLoopDepth;
        private static string TelemetryMachineId()
        {
            string seed = Environment.MachineName;
            try { using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography")) if (key != null) seed += "|" + Convert.ToString(key.GetValue("MachineGuid")); } catch { }
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(seed))).Replace("-", "").ToLowerInvariant();
        }
        private LiveClientDiagnostics _liveDiagnostics;
        public string DiagnosticRunId { get { return _telemetryRun; } }
        public void PublishDiagnosticState(string status, string detail)
        {
            if (_liveDiagnostics == null) return;
            try { var entry = NewTelemetryEvent(status, RedactDiagnosticText(detail)); entry.Device = "__UDA_SESSION__"; entry.Name = "部署状态"; entry.CountInUsage = false;
                ClientTelemetryQueue.Enqueue(TelemetryDirectory, entry); _liveDiagnostics.LastState = status; FlushTelemetry();
            } catch (Exception ex) { ServiceEventLog.Write("warning", "Client session state retained locally: " + ex.Message); }
        }

        private void BeginTelemetryRun() {
            if (_liveDiagnostics != null && _liveDiagnostics.PreserveRun) return;
            if (_liveDiagnostics != null) { _liveDiagnostics.Persist(); if (_liveDiagnostics.LastState == "running") PublishDiagnosticState("log", "扫描阶段结束，开始安装任务"); }
            _telemetryRun = Guid.NewGuid().ToString("N"); _telemetryChunk = 0; _telemetryMac = null;
            if (_liveDiagnostics != null) PublishDiagnosticState("running", "安装任务开始");
        }
        public IDisposable StartLiveDiagnostics(string mode, bool preserveRun, string runId = null)
        {
            if (_liveDiagnostics != null) throw new InvalidOperationException("Client diagnostics already started");
            Guid id; if (!string.IsNullOrEmpty(runId)) { if (!Guid.TryParseExact(runId, "N", out id)) throw new ArgumentException("Invalid session ID"); _telemetryRun = id.ToString("N"); }
            var session = new LiveClientDiagnostics(this, preserveRun); _liveDiagnostics = session;
            session.Start(mode); PublishDiagnosticState("running", "客户端已启动：" + mode); return session;
        }
        internal static string RedactDiagnosticText(string text)
        {
            text = System.Text.RegularExpressions.Regex.Replace(text ?? "", @"(?i)(--(?:token|api-key|password)\s+)(?:""[^""]*""|\S+)", "$1***");
            return System.Text.RegularExpressions.Regex.Replace(text, @"(?i)((?:authorization|api[_-]?key|password|pwd|token)\s*[:=]\s*(?:Bearer\s+)?)[^\s;&]+", "$1***");
        }
        private sealed class LiveClientDiagnostics : IDisposable
        {
            readonly LanDriverClient owner; readonly object gate = new object(); readonly StringBuilder buffer = new StringBuilder();
            readonly ManualResetEvent idle = new ManualResetEvent(true); System.Threading.Timer timer; int working, stopped;
            public readonly bool PreserveRun; public string LastState = "running";
            public LiveClientDiagnostics(LanDriverClient client, bool preserve) { owner = client; PreserveRun = preserve; }
            public void Start(string mode) {
                owner._logger.E += Capture;
                try { string path = owner._logger.Path; using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                    long skip = Math.Max(0, stream.Length - 65536); stream.Position = skip;
                    using (var reader = new StreamReader(stream, Encoding.UTF8)) { if (skip > 0) reader.ReadLine(); Capture(reader.ReadToEnd()); }
                } } catch { }
                owner._logger.W("[INFO] Client diagnostics started: mode=" + mode + "; client=" + LanProtocol.ServerVersion + "; identity=" + System.Security.Principal.WindowsIdentity.GetCurrent().Name +
                    "; os=" + Environment.OSVersion.Version + "; process64=" + Environment.Is64BitProcess + "; run=" + owner._telemetryRun);
                Persist(); timer = new System.Threading.Timer(state => { Persist(); Kick(); }, null, 0, 2000);
            }
            void Capture(string line) {
                lock (gate) buffer.AppendLine(RedactDiagnosticText(line));
                if (buffer.Length >= 8192) Persist();
            }
            public void Persist() {
                try { lock (gate) { while (buffer.Length > 0) {
                    int length = Math.Min(8192, buffer.Length); var entry = owner.NewTelemetryEvent("log", buffer.ToString(0, length)); entry.CountInUsage = false;
                    ClientTelemetryQueue.Enqueue(owner.TelemetryDirectory, entry); buffer.Remove(0, length);
                } } } catch (Exception ex) { ServiceEventLog.Write("warning", "Client diagnostic queue: " + ex.Message); }
            }
            public void Kick() {
                if (Interlocked.CompareExchange(ref working, 1, 0) != 0) return;
                idle.Reset();
                ThreadPool.QueueUserWorkItem(state => {
                    try { var clock = System.Diagnostics.Stopwatch.StartNew(); ClientTelemetryQueue.Flush(owner.TelemetryDirectory, entry => clock.ElapsedMilliseconds < 2500 && owner.TransmitTelemetry(entry), 16); }
                    catch (Exception ex) { ServiceEventLog.Write("warning", "Client diagnostics retained: " + ex.Message); }
                    finally { Interlocked.Exchange(ref working, 0); idle.Set(); }
                });
            }
            public void Dispose() {
                if (Interlocked.Exchange(ref stopped, 1) != 0) return;
                if (timer != null) timer.Dispose();
                if (LastState == "running") owner.PublishDiagnosticState("log", "客户端已退出；未收到最终安装核验结果");
                owner._logger.E -= Capture;
                Persist(); Kick(); idle.WaitOne(3000); owner._liveDiagnostics = null;
            }
        }

        private string TelemetryDirectory
        {
            get
            {
                using (var sha = SHA256.Create())
                {
                    string endpoint = (_config.ServerUrl ?? "").TrimEnd('/').ToLowerInvariant() + "|" + (_config.Token ?? "");
                    string scope = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(endpoint))).Replace("-", "");
                    return string.IsNullOrEmpty(ClientTelemetryQueue.TestDirectory) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UniversalDriverAssistant", "TelemetryQueue", scope) : Path.Combine(ClientTelemetryQueue.TestDirectory, scope);
                }
            }
        }
        private DriverInstallEvent NewTelemetryEvent(string status, string detail)
        {
            var macs = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Where(n => n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback).Select(n => n.GetPhysicalAddress().ToString()).Where(m => m.Length > 0).OrderBy(m => m);
            if (_telemetryMac == null) _telemetryMac = string.Join(",", macs);
            return new DriverInstallEvent { EventId = Guid.NewGuid().ToString("N"), RunId = _telemetryRun, MachineId = _telemetryMachineId, Machine = Environment.MachineName, Mac = _telemetryMac, ChunkIndex = Interlocked.Increment(ref _telemetryChunk), Status = status, Detail = detail, Utc = DateTime.UtcNow.ToString("o") };
        }
        internal Func<DriverInstallEvent, bool> TestTelemetryTransport;
        private bool TransmitTelemetry(DriverInstallEvent entry)
        {
            if (TestTelemetryTransport != null) return TestTelemetryTransport(entry);
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(entry));
                var request = (HttpWebRequest)WebRequest.Create(_config.ServerUrl.TrimEnd('/') + "/api/client/install-event");
                request.Method = "POST"; request.Proxy = null; request.Timeout = 2000; request.ReadWriteTimeout = 2000; request.ContentType = "application/json";
                request.Headers[LanProtocol.HeaderProtocol] = LanProtocol.Version.ToString(CultureInfo.InvariantCulture); request.Headers[LanProtocol.HeaderToken] = _config.Token ?? ""; request.ContentLength = bytes.Length;
                using (var output = request.GetRequestStream()) output.Write(bytes, 0, bytes.Length);
                using (var response = request.GetResponse()) { return true; }
            }
            catch { return false; }
        }
        private void FlushTelemetry()
        {
            if (_liveDiagnostics != null) { _liveDiagnostics.Persist(); _liveDiagnostics.Kick(); return; }
            try { ClientTelemetryQueue.Flush(TelemetryDirectory, TransmitTelemetry, 100); }
            catch (Exception ex) { if (_logger != null) _logger.W("[WARN] 日志队列保留待重传 / telemetry queue retained: " + ex.Message); }
        }
        private void SendInstallEvent(LanCandidateDto candidate, Sum sum, D previous, bool verified)
        {
            try
            {
                var entry = NewTelemetryEvent(sum.Ok > 0 && verified ? "installed" : sum.Fail > 0 ? "failed" : sum.Skip > 0 ? "skipped" : "unverified", "ok=" + sum.Ok + " skip=" + sum.Skip + " fail=" + sum.Fail + " reboot=" + sum.Reboot);
                entry.Device = candidate.DeviceInstanceId; entry.Name = candidate.FriendlyName; entry.Inf = candidate.InfRelativePath; entry.Version = candidate.DriverVerVersion;
                entry.Update = previous != null && !string.IsNullOrEmpty(previous.Inf);
                ClientTelemetryQueue.Enqueue(TelemetryDirectory, entry);
                FlushTelemetry();
            }
            catch (Exception ex) { if (_logger != null) _logger.W("[WARN] 安装日志上报失败（不影响安装） / telemetry failed: " + ex.Message); }
        }
        private readonly LanClientConfig _config;
        private readonly L _logger;
        private Machine _machine;
        private readonly IHardwareCollector _hc;
        private readonly OsTarget _osTarget;
        private readonly ISignatureVerifier _sig;
        private readonly IAdminChecker _admin;
        private readonly IPnpInstallRunner _runner;
        private readonly ISystemOps _sys;
        private List<D> _lastReportedDevices;
        private int _scanDownloadConnections = 2;

        public LanDriverClient(LanClientConfig config, Machine machine, IHardwareCollector hc, OsTarget osTarget,
            L logger, ISignatureVerifier sig, IAdminChecker admin, IPnpInstallRunner runner, ISystemOps sys)
        {
            _config = config;
            _logger = logger ?? new L(Path.GetTempPath());
            _machine = machine;
            _hc = hc;
            _osTarget = osTarget ?? new OsTarget();
            _sig = sig; _admin = admin; _runner = runner; _sys = sys;
        }

        public LanClientResult Run(CancellationToken ct)
        {
            BeginTelemetryRun();
            var result = new LanClientResult();
            Action<string> log = m => { result.Log.Add(m); if (_logger != null) _logger.W("[INFO] " + m); };
            string cacheRoot = null, staging = null;
            try
            {
                if (ct.IsCancellationRequested) { result.Cancelled = true; result.Error = "cancelled"; return result; }
                var collector = _hc ?? new WmiHardwareCollector();
                var devices = collector.Collect(_logger);

                // (1) NIC bootstrap: install a missing NIC driver from the local offline bootstrap repo.
                var nicLacking = devices.Where(IsNicLackingDriver).ToList();
                if (nicLacking.Count > 0)
                {
                    result.BootstrapAttempted = true;
                    if (string.IsNullOrEmpty(_config.BootstrapRepoDir) || !File.Exists(Path.Combine(_config.BootstrapRepoDir, "repo.manifest.json")))
                    {
                        log("无网卡引导仓库可用，无法离线安装 NIC 驱动 / No NIC bootstrap repo available; cannot install NIC driver offline");
                    }
                    else
                    {
                        try
                        {
                            var boot = OfflineRepo.Open(_config.BootstrapRepoDir, _logger);
                            var installer = new OfflineInstaller(boot, _osTarget, new StaticHardwareCollector(nicLacking), _machine, _logger,
                                _config.Deploy, _config.WhatIf, _config.Force, _config.InstallTimeoutSeconds,
                                _config.DedupeByContent, _config.VerifySignature, _sig, _admin, _runner, _sys);
                            var r = installer.Scan(ct, null);
                            foreach (var x in r.Matches) x.Pick = true;
                            var s = installer.Install(r, false, null);
                            result.BootstrapOk = s.Ok; result.BootstrapFail = s.Fail;
                            log("网卡引导安装 / NIC bootstrap: matches=" + r.Matches.Count + " ok=" + s.Ok + " fail=" + s.Fail);
                        }
                        catch (Exception ex) { log("网卡引导失败 / NIC bootstrap failed: " + ex.Message); result.BootstrapFail++; }
                    }
                }

                if (ct.IsCancellationRequested) { result.Cancelled = true; result.Error = "cancelled"; return result; }

                _lastReportedDevices = devices;
                // (2) Probe server with retry/backoff.
                if (!WaitForServer(ct, log))
                {
                    result.Error = "server unreachable after " + _config.MaxRetries + " retries";
                    return result;
                }
                if (ct.IsCancellationRequested) { result.Cancelled = true; result.Error = "cancelled"; return result; }

                // (3) Report hardware inventory.
                var req = new LanReportRequest
                {
                    Protocol = LanProtocol.Version, SupportsExtensionPackages = true,
                    RequestId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                    MachineManufacturer = _machine != null ? _machine.Maker : "",
                    MachineModel = _machine != null ? _machine.Model : "",
                    MachineSku = _machine != null ? _machine.Sku : "",
                    OsArchitecture = _osTarget.Architecture,
                    OsMajorVersion = _osTarget.MajorVersion, OsMinorVersion = _osTarget.EffectiveMinorVersion(),
                    OsBuildNumber = _osTarget.BuildNumber,
                    Token = _config.Token
                };
                foreach (var d in devices)
                {
                    req.Devices.Add(new LanDeviceDto
                    {
                        DeviceInstanceId = d.Id,
                        FriendlyName = string.IsNullOrWhiteSpace(d.Name) ? (string.IsNullOrWhiteSpace(d.Class) ? "未知硬件" : d.Class) : d.Name,
                        Class = d.Class,
                        ClassGuid = d.ClassGuid,
                        HardwareIds = d.H,
                        CompatibleIds = d.K,
                        CurrentInf = d.Inf,
                        CurrentVersion = d.Version,
                        Bad = d.Bad
                    });
                }
                LanReportResponse resp;
                if (!PostJson(_config.ServerUrl + "/api/report", req, ct, out resp))
                {
                    result.Error = "report failed: " + (resp != null ? resp.Error : "no response");
                    return result;
                }
                result.Reported = true;
                result.Cached = resp.Cached;
                result.Candidates = resp.Candidates.Count;
                log("上报完成 / Reported: " + resp.Candidates.Count + " candidates" + (resp.Cached ? " (cached)" : ""));
                if (ct.IsCancellationRequested) { result.Cancelled = true; result.Error = "cancelled"; return result; }

                // (4) Download + verify + install each matched candidate whose device still needs a driver,
                //     inside a unique per-run cache subdirectory that is always cleaned up in finally.
                cacheRoot = string.IsNullOrEmpty(_config.StagingDir) ? Path.Combine(Path.GetTempPath(), "UDA-Lan-Staging") : _config.StagingDir;
                CleanStaleClientCacheDirs(cacheRoot, DateTime.UtcNow, log);
                staging = CreateRunCacheDir(cacheRoot, log);
                if (string.IsNullOrEmpty(staging)) { result.Error = "无法创建本轮缓存目录 / failed to create run cache dir"; return result; }
                var installedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var deviceById = devices.Where(d => !string.IsNullOrEmpty(d.Id)).ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
                InstallModelPackages(resp.Candidates, devices, staging, result, ct, null, log);
                foreach (var cand in resp.Candidates.Where(c => string.IsNullOrEmpty(c.PackageId)).OrderBy(c => c.IsExtension))
                {
                    if (ct.IsCancellationRequested) { result.Cancelled = true; result.Error = "cancelled"; break; }
                    if (string.IsNullOrEmpty(cand.ContentHash)) { log("跳过无哈希候选 / Skip candidate without hash: " + cand.InfRelativePath); continue; }
                    // Only install if the device still lacks a driver or is outdated (same rule as the scan).
                    string skipWhy;
                    if (!ShouldInstall(cand, deviceById, out skipWhy)) { result.InstalledSkip++; log("跳过 / Skip " + Path.GetFileName(cand.InfRelativePath ?? "") + ": " + skipWhy); continue; }
                    // Idempotency: deduplicate only after confirming this device needs the package.
                    if (!installedHashes.Add(cand.UserSelectedVersion || cand.IsExtension ? cand.DeviceInstanceId + "|" + cand.ContentHash : cand.ContentHash)) { log("跳过重复内容 / Skip duplicate content: " + cand.InfRelativePath); continue; }

                    if (!string.IsNullOrEmpty(cand.PackageId) && !System.Text.RegularExpressions.Regex.IsMatch(cand.PackageId, "^model-[a-f0-9]{64}$")) throw new InvalidDataException("Invalid model package identity");
                    string extractDir = Path.Combine(staging, string.IsNullOrEmpty(cand.PackageId) ? cand.ContentHash.Substring(0, Math.Min(8, cand.ContentHash.Length)) : cand.PackageId.Substring(0, 22));
                    string zip = extractDir + ".zip";
                    string downloadServer = string.IsNullOrWhiteSpace(cand.DownloadBaseUrl) ? _config.ServerUrl : cand.DownloadBaseUrl.TrimEnd('/');
                    if (string.IsNullOrEmpty(cand.PackageId) || LocateInf(extractDir, cand.InfRelativePath, cand.PackageInfPath) == null) {
                    if (!DownloadToFile(downloadServer + "/api/download?path=" + Uri.EscapeDataString(cand.InfRelativePath) + (string.IsNullOrEmpty(cand.PackageId) ? "" : "&package=" + Uri.EscapeDataString(cand.PackageId)), zip, ct, null))
                    { result.InstalledFail++; log("下载失败 / Download failed: " + cand.InfRelativePath); continue; }
                    result.Downloaded++;
                    try { ExtractZipFile(zip, extractDir, ct); }
                    catch (Exception ex) { result.InstalledFail++; log("解压失败 / Extract failed: " + ex.Message); continue; }
                    }

                    string infAbs = LocateInf(extractDir, cand.InfRelativePath, cand.PackageInfPath);
                    if (infAbs == null) { result.InstalledFail++; log("未找到 INF / INF not found in package: " + cand.InfRelativePath); continue; }
                    string hash = Sha256File(infAbs);
                    if (!string.Equals(hash, cand.ContentHash, StringComparison.OrdinalIgnoreCase))
                    { result.InstalledSkip++; log("哈希不匹配，拒绝安装 / Hash mismatch, refusing to install: " + cand.InfRelativePath); continue; }
                    // Pre-install revalidation on the ACTUAL extracted INF with the current device IDs
                    // and current OS; a rejection is logged with its precise reason, never a bare skip.
                    D revalDev; deviceById.TryGetValue(cand.DeviceInstanceId, out revalDev);
                    string revalCode;
                    string revalWhy = RevalidateCandidateInf(cand, infAbs, revalDev, out revalCode);
                    if (revalWhy != null)
                    {
                        if (string.Equals(revalCode, "CurrentDriverPreferred", StringComparison.OrdinalIgnoreCase))
                            RecordCurrentDriverPreferred(cand, revalDev, log);
                        result.InstalledSkip++; log(revalWhy + " — " + Path.GetFileName(infAbs)); continue;
                    }

                    if (!ExtensionParentReady(cand, deviceById, log)) { result.InstalledSkip++; continue; }
                    var rec = new InfRecord { Path = infAbs, ContentHash = cand.ContentHash, Class = cand.Class, ClassGuid = cand.ClassGuid,
                        DriverVerDate = cand.DriverVerDate, DriverVerVersion = cand.DriverVerVersion, Provider = cand.Provider,
                        HardwareIds = new List<string>(), CompatibleIds = new List<string>() };
                    var sum = DriverInstallExecutor.Execute(new List<InfRecord> { rec }, new List<string> { extractDir }, _config.DedupeByContent,
                        _config.WhatIf, _config.Deploy, _config.InstallTimeoutSeconds, _config.VerifySignature,
                        _logger, _admin, _sig, InstallationRunner(cand), _sys, abs => string.Equals(Sha256File(abs), cand.ContentHash, StringComparison.OrdinalIgnoreCase), null);
                    ConfirmInstalledBinding(cand, sum, result, ct, log);
                    result.InstalledOk += sum.Ok; result.InstalledSkip += sum.Skip; result.InstalledFail += sum.Fail;
                    SendInstallEvent(cand, sum, revalDev, !_config.WhatIf && (_runner == null || _runner is PnpInstallRunner));
                    if (sum.Ok > 0) foreach (var completed in resp.Candidates.Where(x => string.Equals(x.ContentHash, cand.ContentHash, StringComparison.OrdinalIgnoreCase))) LanInstallReceiptStore.Mark(completed.DeviceInstanceId, completed.ContentHash);
                    if (sum.Reboot) result.Reboot = true;
                    log("安装 / Install " + Path.GetFileName(infAbs) + ": ok=" + sum.Ok + " skip=" + sum.Skip + " fail=" + sum.Fail);
                }
                return result;
            }
            catch (OperationCanceledException) { result.Cancelled = true; result.Error = "cancelled"; return result; }
            catch (Exception ex) { result.Error = "client error: " + ex.Message; if (_logger != null) _logger.W("[ERROR] LAN 客户端异常 / client error: " + ex); return result; }
            finally { SafeDeleteRunCacheDir(staging, cacheRoot, log); }
        }

        // ---------------------------------------------------------------------------------
        //  Granular API for the interactive LAN GUI. Run() stays the one-shot black box used by the CLI
        //  (--deployment/--whatif) and the protocol tests; these methods expose the same bootstrap / probe /
        //  report / install steps individually so the full ResponsiveForm UI can drive them with live server
        //  status, a candidate list, confirm + cancel, and real-time progress. They reuse Run()'s private
        //  HTTP helpers and install path, so the verification (SHA-256 + signature), OS/arch filtering and
        //  strict-duplicate dedup semantics are identical to the black-box flow.
        // ---------------------------------------------------------------------------------

        /// <summary>Single health probe (GET /api/health). Returns null when the server is unreachable.</summary>
        public LanHealth Probe(CancellationToken ct)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                LanHealth h;
                if (GetJson(_config.ServerUrl + "/api/health", ct, out h)) return h;
                if (attempt < 2 && ct.WaitHandle.WaitOne(500 * (attempt + 1))) ct.ThrowIfCancellationRequested();
            }
            return null;
        }

        /// <summary>Authenticated protocol preflight used by the UI connection test. Health is
        /// intentionally public and therefore cannot prove that /api/report accepts this client's
        /// embedded token or that the repository is ready.</summary>
        public LanReportResponse ProbeReportAccess(CancellationToken ct)
        {
            var req = new LanReportRequest
            {
                Protocol = LanProtocol.Version,
                RequestId = "probe-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                OsArchitecture = _osTarget.Architecture,
                OsMajorVersion = _osTarget.MajorVersion, OsMinorVersion = _osTarget.EffectiveMinorVersion(),
                OsBuildNumber = _osTarget.BuildNumber,
                Token = _config.Token,
                Devices = new List<LanDeviceDto>()
            };
            LanReportResponse response;
            if (!PostJson(_config.ServerUrl + "/api/report", req, ct, out response))
                return response ?? new LanReportResponse { Ok = false, Error = "无法访问上报接口" };
            return response;
        }

        /// <summary>Report the supplied devices and return the server-matched candidates. Neither bootstraps
        /// nor installs; the caller has already collected the hardware inventory. Throws on a failed report.</summary>
        public LanReportResponse Report(IEnumerable<D> devices, Machine machine, CancellationToken ct, IProgress<string> progress)
        {
            FlushTelemetry();
            if (machine != null) _machine = machine; else machine = _machine;
            var reportClock = System.Diagnostics.Stopwatch.StartNew();
            _lastReportedDevices = (devices ?? Enumerable.Empty<D>()).ToList();
            devices = _lastReportedDevices;
            CurrentDriverState.Refresh(_lastReportedDevices, _logger);
            if (progress != null) progress.Report("正在上报硬件清单到服务器…");
            var req = new LanReportRequest
            {
                Protocol = LanProtocol.Version, SupportsExtensionPackages = true,
                RequestId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                MachineManufacturer = machine != null ? machine.Maker : "",
                MachineModel = machine != null ? machine.Model : "",
                MachineSku = machine != null ? machine.Sku : "",
                OsArchitecture = _osTarget.Architecture,
                OsMajorVersion = _osTarget.MajorVersion, OsMinorVersion = _osTarget.EffectiveMinorVersion(),
                OsBuildNumber = _osTarget.BuildNumber,
                Token = _config.Token
            };
            foreach (var d in devices ?? new List<D>())
            {
                req.Devices.Add(new LanDeviceDto
                {
                    DeviceInstanceId = d.Id,
                    FriendlyName = string.IsNullOrWhiteSpace(d.Name) ? (string.IsNullOrWhiteSpace(d.Class) ? "未知硬件" : d.Class) : d.Name,
                    Class = d.Class,
                    ClassGuid = d.ClassGuid,
                    HardwareIds = d.H,
                    CompatibleIds = d.K,
                    CurrentInf = d.Inf,
                    CurrentVersion = d.Version,
                    Bad = d.Bad
                });
            }
            LanReportResponse resp;
            if (!PostJson(_config.ServerUrl + "/api/report", req, ct, out resp))
                throw new InvalidOperationException("上报失败 / report failed: " + (resp != null ? resp.Error : "无法连接服务器"));
            // Keep missing-device evidence in the diagnostic log, not only in the UI.
            if (_logger != null)
                foreach (var missing in resp.UnmatchedDevices ?? new List<LanUnmatchedDeviceDto>())
                {
                    D device = _lastReportedDevices.FirstOrDefault(d => string.Equals(d.Id, missing.DeviceInstanceId, StringComparison.OrdinalIgnoreCase));
                    if (device != null && device.Bad)
                        _logger.W("[WARN] 未匹配异常设备 / Unmatched device: " + device.Id + "; code=" + device.Error + "; hardware=[" + string.Join(",", device.H.ToArray()) + "]; reason=" + missing.ReasonCode + " " + missing.Reason);
                }
            if (_liveDiagnostics != null && _logger != null) {
                _logger.W("[INFO] Scan diagnostic: devices=" + _lastReportedDevices.Count + "; candidates=" + resp.Candidates.Count + "; unmatched=" + (resp.UnmatchedDevices == null ? 0 : resp.UnmatchedDevices.Count));
                foreach (var device in _lastReportedDevices.Where(d => d.NeedsAttention)) _logger.W("[INFO] Problem device: " + device.Id + "; code=" + device.Error + "; INF=" + device.Inf + "; H=[" + string.Join(",", device.H) + "]; K=[" + string.Join(",", device.K) + "]");
                foreach (var candidate in resp.Candidates) _logger.W("[INFO] Candidate decision: " + candidate.DeviceInstanceId + "; INF=" + candidate.InfRelativePath + "; version=" + candidate.DriverVerVersion + "; need=" + candidate.NeedInstall + "; extension=" + candidate.IsExtension);
            }
            long matchMs = reportClock.ElapsedMilliseconds;
            ValidateScanCandidates(resp.Candidates.Where(c => c.NeedInstall).ToList(), ct, progress);
            if (_logger != null) _logger.W("[INFO] Scan report timing: bindingAndServerMs=" + matchMs + "; validationMs=" + (reportClock.ElapsedMilliseconds - matchMs) + "; totalMs=" + reportClock.ElapsedMilliseconds + "; devices=" + _lastReportedDevices.Count);
            return resp;
        }

        private void ValidateScanCandidates(IList<LanCandidateDto> candidates, CancellationToken ct, IProgress<string> progress)
        {
            var scanFiles = new DriverScanFiles();
            var scanClock = System.Diagnostics.Stopwatch.StartNew();
            int processors = ClientScanParallelism.LogicalProcessors();
            var devices = _lastReportedDevices.Where(d => !string.IsNullOrEmpty(d.Id)).GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            if (_logger != null) _logger.W("[INFO] Client scan parallelism: logical processors=" + processors + "; workers=" + ClientScanParallelism.Workers(processors, candidates.Count) + "; candidates=" + candidates.Count);
            // Cheap persistent/binding checks precede any filesystem inventory or network work.
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                D device; devices.TryGetValue(candidate.DeviceInstanceId ?? "", out device);
                if (candidate.IsExtension)
                {
                    if (LanExtensionPackages.Satisfied(candidate, device, _osTarget, scanFiles)) RejectScanCandidate(candidate, "配套扩展已应用于设备");
                    continue;
                }
                bool windowsRejected = LanPnpRejectionStore.Contains(candidate, device, _osTarget);
                if (windowsRejected || IsReceiptSuppressed(candidate, device))
                {
                    RejectScanCandidate(candidate, windowsRejected
                        ? "Windows 已确认当前驱动优先于此包；设备绑定、候选包或系统变化前不再重复推荐"
                        : "当前绑定已相同");
                }
            }
            var updates = candidates.Where(candidate =>
            {
                D device; devices.TryGetValue(candidate.DeviceInstanceId ?? "", out device);
                return !candidate.IsExtension && candidate.NeedInstall && device != null && !device.Bad && !string.IsNullOrEmpty(device.Inf) && !string.IsNullOrEmpty(device.Version);
            }).ToList();
            if (updates.Count == 0) return;
            var imported = LanInstalledPackage.FindImportedInfs(updates.Select(c => c.ContentHash), processors, ct, scanFiles);
            var unstaged = new ConcurrentBag<LanCandidateDto>();
            ClientScanParallelism.Run(updates, processors, ct, (candidate, token) =>
            {
                D device = devices[candidate.DeviceInstanceId];
                string local;
                if (imported.TryGetValue(candidate.ContentHash ?? "", out local))
                {
                    if (WindowsDriverPreference.IsBetterForDevice(device, local,
                        message => { if (_logger != null) _logger.W("[INFO] Driver Store rank / " + device.Id + " " + candidate.InfRelativePath + ": " + message); }, scanFiles) == false)
                        RejectScanCandidate(candidate, "当前绑定已相同，或 Windows 排名/日期/版本不优于当前驱动");
                }
                else if (WindowsDriverPreference.TestPreferenceProbe != null || CurrentDriverState.ReadRank(device.Id).HasValue) unstaged.Add(candidate);
                else if (_logger != null) _logger.W("[WARN] 扫描排名不可用 / Scan rank unavailable: " + device.Id + " " + candidate.InfRelativePath + "; current driver rank could not be read");
            });
            // Package identity includes path and source, since identical INF text alone does not prove
            // that differently signed packages have the same catalog or driver payload.
            var groups = unstaged.GroupBy(c => (c.DownloadBaseUrl ?? "") + "\n" + (string.IsNullOrEmpty(c.PackageId) ? c.InfRelativePath + "\n" + c.ContentHash : c.PackageId),
                StringComparer.OrdinalIgnoreCase).Select(g => g.ToList()).ToList();
            _scanDownloadConnections = ClientScanParallelism.Workers(processors, groups.Count);
            string root = string.IsNullOrEmpty(_config.StagingDir) ? DefaultClientCacheRoot() : _config.StagingDir;
            var directories = new ConcurrentBag<string>();
            int completed = 0;
            using (var rankSlots = new SemaphoreSlim(ClientScanParallelism.Workers(processors, unstaged.Count)))
            {
            if (_logger != null) _logger.W("[INFO] Scan package preparation: unique packages=" + groups.Count + "; download workers=" + _scanDownloadConnections);
            try
            {
                // Each package enters ranking as soon as it is ready; a slow download no longer
                // holds back every other device. The shared semaphore bounds native rank probes
                // across all package groups to the hardware worker budget.
                ClientScanParallelism.Run(groups, processors, ct, (group, token) =>
                {
                    var candidate = group[0];
                    string name = Path.GetFileName(candidate.InfRelativePath ?? "");
                    if (progress != null) progress.Report("正在并行下载、解包校验候选：" + name);
                    string runDir = CreateRunCacheDir(root, null);
                    if (string.IsNullOrEmpty(runDir)) throw new IOException("无法创建扫描排名校验目录 / cannot create scan validation directory");
                    directories.Add(runDir);
                    string server = string.IsNullOrWhiteSpace(candidate.DownloadBaseUrl) ? _config.ServerUrl : candidate.DownloadBaseUrl.TrimEnd('/');
                    string zip = Path.Combine(runDir, "candidate.zip");
                    if (!DownloadToFile(server + "/api/download?path=" + Uri.EscapeDataString(candidate.InfRelativePath) + (string.IsNullOrEmpty(candidate.PackageId) ? "" : "&package=" + Uri.EscapeDataString(candidate.PackageId)), zip, token, null))
                        throw new IOException("扫描排名校验下载失败 / scan validation download failed: " + name);
                    string extracted = Path.Combine(runDir, "p");
                    ExtractZipFile(zip, extracted, token);
                    var prepared = new List<PreparedLanCandidate>();
                    foreach (var member in group)
                    {
                        string inf = LocateInf(extracted, member.InfRelativePath, member.PackageInfPath);
                        if (inf == null || !string.Equals(scanFiles.Hash(inf), member.ContentHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("扫描排名校验 INF 哈希不匹配 / scan validation INF hash mismatch: " + member.InfRelativePath);
                        prepared.Add(new PreparedLanCandidate { Candidate = member, InfPath = inf, ExtractDir = extracted });
                    }
                    ClientScanParallelism.Run(prepared, processors, token, (item, rankToken) =>
                    {
                        var member = item.Candidate;
                        D device = devices[member.DeviceInstanceId]; string memberName = Path.GetFileName(member.InfRelativePath ?? "");
                        rankSlots.Wait(rankToken);
                        try
                        {
                            string code;
                            string reason = RevalidateExtractedInf(item.InfPath, device, _osTarget, out code,
                                message => { if (_logger != null) _logger.W("[INFO] Scan Windows rank / " + device.Id + " " + memberName + ": " + message); }, scanFiles);
                            rankToken.ThrowIfCancellationRequested();
                            if (reason != null)
                            {
                                RejectScanCandidate(member, reason);
                                if (code == "CurrentDriverPreferred") RecordCurrentDriverPreferred(member, device, null);
                                if (_logger != null) _logger.W("[INFO] 扫描阶段排除 / Scan validation rejected: " + device.Id + " " + memberName + " [" + code + "] " + reason);
                            }
                            int done = Interlocked.Increment(ref completed);
                            if (progress != null) progress.Report("并行校验 Windows 驱动排名 " + done + "/" + unstaged.Count + "：" + memberName);
                        }
                        finally { rankSlots.Release(); }
                    });
                });
            }
            finally { foreach (string directory in directories) SafeDeleteRunCacheDir(directory, root, null); if (_logger != null) _logger.W("[INFO] Scan validation timing: elapsedMs=" + scanClock.ElapsedMilliseconds + "; uniqueHashReads=" + scanFiles.HashReads + "; uniqueInfParses=" + scanFiles.ParseReads); }
            }
        }

        private void RejectScanCandidate(LanCandidateDto candidate, string reason)
        {
            candidate.NeedInstall = false;
            candidate.LocalRejectionReason = reason;
            if (_logger != null) _logger.W("[INFO] 不列为更新 / Current driver preferred: " + candidate.DeviceInstanceId + " " + candidate.InfRelativePath);
        }

        /// <summary>Install a missing NIC driver from the local bootstrap repo so the machine can reach the
        /// server. No-op when no NIC lacks a driver. Mirrors the bootstrap step of Run().</summary>
        public LanClientResult Bootstrap(IEnumerable<D> devices, CancellationToken ct, IProgress<string> progress)
        {
            var result = new LanClientResult();
            Action<string> log = m => { result.Log.Add(m); if (_logger != null) _logger.W("[INFO] " + m); };
            try
            {
                var list = (devices ?? new List<D>()).Where(IsNicLackingDriver).ToList();
                if (list.Count == 0) { log("无需网卡引导 / No NIC lacking a driver"); return result; }
                result.BootstrapAttempted = true;
                if (progress != null) progress.Report("检测到 " + list.Count + " 个缺驱动网卡，尝试离线引导…");
                if (string.IsNullOrEmpty(_config.BootstrapRepoDir) || !File.Exists(Path.Combine(_config.BootstrapRepoDir, "repo.manifest.json")))
                {
                    log("无网卡引导仓库可用，无法离线安装 NIC 驱动 / No NIC bootstrap repo available");
                    return result;
                }
                try
                {
                    var boot = OfflineRepo.Open(_config.BootstrapRepoDir, _logger);
                    var installer = new OfflineInstaller(boot, _osTarget, new StaticHardwareCollector(list), _machine, _logger,
                        _config.Deploy, _config.WhatIf, _config.Force, _config.InstallTimeoutSeconds,
                        _config.DedupeByContent, _config.VerifySignature, _sig, _admin, _runner, _sys);
                    var r = installer.Scan(ct, null);
                    foreach (var x in r.Matches) x.Pick = true;
                    var s = installer.Install(r, false, null);
                    result.BootstrapOk = s.Ok; result.BootstrapFail = s.Fail;
                    log("网卡引导安装 / NIC bootstrap: matches=" + r.Matches.Count + " ok=" + s.Ok + " fail=" + s.Fail);
                    if (progress != null) progress.Report("网卡引导完成：成功 " + s.Ok + "，失败 " + s.Fail);
                }
                catch (Exception ex) { log("网卡引导失败 / NIC bootstrap failed: " + ex.Message); result.BootstrapFail++; }
            }
            catch (OperationCanceledException) { result.Cancelled = true; result.Error = "cancelled"; }
            catch (Exception ex) { result.Error = "bootstrap error: " + ex.Message; if (_logger != null) _logger.W("[ERROR] LAN 网卡引导异常 / bootstrap error: " + ex); }
            return result;
        }

        /// <summary>Download, verify (SHA-256 + signature) and install the supplied candidates into a
        /// client-controlled staging dir. Idempotent within the run (content-hash dedup) and skips devices that
        /// already have an up-to-date driver. Reports per-step progress. Mirrors the install loop of Run() but
        /// over a caller-chosen candidate subset, so the GUI can install exactly the user-confirmed picks.</summary>
        // A model package is one deployment unit, not one download/install per device.
        private void InstallModelPackages(IList<LanCandidateDto> candidates, IList<D> devices, string staging,
            LanClientResult result, CancellationToken ct, IProgress<LanInstallProgress> progress, Action<string> log)
        {
            var byDevice = (devices ?? new List<D>()).Where(d => !string.IsNullOrEmpty(d.Id)).GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var group in candidates.Where(c => !string.IsNullOrEmpty(c.PackageId)).GroupBy(c => c.PackageId, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                var members = group.ToList(); var first = members[0];
                if (!System.Text.RegularExpressions.Regex.IsMatch(first.PackageId, "^model-[a-f0-9]{64}$")) throw new InvalidDataException("Invalid model package identity");
                string why;
                var targets = members.Where(c => ShouldInstall(c, byDevice, out why)).ToList();
                result.InstalledSkip += members.Count - targets.Count;
                if (targets.Count == 0) continue;
                string directory = Path.Combine(staging, "m-" + Guid.NewGuid().ToString("N").Substring(0, 8)), zip = directory + ".zip";
                string server = string.IsNullOrWhiteSpace(first.DownloadBaseUrl) ? _config.ServerUrl : first.DownloadBaseUrl.TrimEnd('/');
                ReportStep(progress, 1, 1, "机型驱动整包", "下载整包（一次）");
                if (!DownloadToFile(server + "/api/download?path=" + Uri.EscapeDataString(first.InfRelativePath) + "&package=" + Uri.EscapeDataString(first.PackageId), zip, ct,
                    (bytes, length, rate) => ReportTransfer(progress, 1, 1, "机型驱动整包", first.PackageId, bytes, length, rate)))
                { result.InstalledFail += targets.Count; log("机型整包下载失败：" + first.PackageId); continue; }
                result.Downloaded++;
                ReportStep(progress, 1, 1, "机型驱动整包", "解压整包（一次）");
                ExtractZipFile(zip, directory, ct);
                var files = Directory.GetFiles(directory, "*.inf", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                if (files.Count == 0) throw new InvalidDataException("机型整包没有 INF，拒绝导入");
                foreach (var member in members)
                {
                    string inf = LocateInf(directory, member.InfRelativePath, member.PackageInfPath);
                    if (inf == null || !string.Equals(Sha256File(inf), member.ContentHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("机型整包候选 INF 校验失败：" + member.InfRelativePath);
                }
                foreach (string file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if ((_config.VerifySignature || _sig != null) && !(_sig ?? new WinTrustSignatureVerifier()).IsTrusted(file, _logger)) throw new InvalidDataException("机型整包签名校验失败：" + Path.GetFileName(file));
                }
                if (!_config.WhatIf && !(_admin ?? new WindowsAdminChecker()).IsAdministrator()) throw new UnauthorizedAccessException("整包导入需要管理员权限");
                ct.ThrowIfCancellationRequested();
                ReportStep(progress, 1, 1, "机型驱动整包", "将整包导入本机驱动存储");
                log("整包统一导入 / Import complete model package: " + files.Count + " INF；Windows 自动匹配适用设备，不强制覆盖排名更高的驱动");
                var packageRunner = (_runner ?? new PnpInstallRunner()) as IPnpPackageInstallRunner;
                if (packageRunner == null) throw new NotSupportedException("安装执行器不支持机型整包导入");
                if (!_config.WhatIf && _sys != null) _sys.ExportServicesRegistry(Path.GetTempPath(), _logger);
                var imported = _config.WhatIf ? InstallPlanner.ResultFrom(259, false, "WhatIf: model package import") : packageRunner.AddPackage(directory, _config.InstallTimeoutSeconds, _logger);
                if (!_config.WhatIf && _sys != null) _sys.ScanDevices(60, _logger);
                bool ok = imported.Outcome == InstallOutcome.Success || imported.Outcome == InstallOutcome.SuccessReboot;
                bool skipped = imported.Outcome == InstallOutcome.Skipped;
                foreach (var member in targets)
                {
                    var sum = new Sum { Ok = ok ? 1 : 0, Skip = skipped ? 1 : 0, Fail = ok || skipped ? 0 : 1, Reboot = imported.Outcome == InstallOutcome.SuccessReboot };
                    ConfirmInstalledBinding(member, sum, result, ct, log);
                    result.InstalledOk += sum.Ok; result.InstalledSkip += sum.Skip; result.InstalledFail += sum.Fail; result.Reboot |= sum.Reboot;
                    D previous; byDevice.TryGetValue(member.DeviceInstanceId, out previous);
                    SendInstallEvent(member, sum, previous, !_config.WhatIf && (_runner == null || _runner is PnpInstallRunner));
                    if (sum.Ok > 0 && !_config.WhatIf) LanInstallReceiptStore.Mark(member.DeviceInstanceId, member.ContentHash);
                }
                log("机型整包导入结束 / Model package import finished: " + imported.Outcome + "；exit=" + imported.ExitCode + "；" + imported.Output);
            }
        }

        public LanClientResult InstallCandidates(IList<LanCandidateDto> candidates, IList<D> devices, CancellationToken ct, IProgress<LanInstallProgress> progress)
        {
            if (Volatile.Read(ref _telemetryLoopDepth) == 0) BeginTelemetryRun();
            bool captureInstallOnly = _liveDiagnostics == null;
            var logBuffer = new StringBuilder(); var logGate = new object();
            Action<string> capture = line => {
                lock (logGate)
                {
                    logBuffer.AppendLine(line);
                    while (logBuffer.Length >= 8192) { ClientTelemetryQueue.Enqueue(TelemetryDirectory, NewTelemetryEvent("log", logBuffer.ToString(0, 8192))); logBuffer.Remove(0, 8192); }
                }
            };
            if (captureInstallOnly && _logger != null) _logger.E += capture;
            var result = new LanClientResult();
            Action<string> log = m => { result.Log.Add(m); if (_logger != null) _logger.W("[INFO] " + m); };
            string cacheRoot = null, runDir = null;
            try
            {
                if (candidates == null || candidates.Count == 0) { log("没有需要安装的候选 / No candidates to install"); return result; }
                // Unique per-run cache subdirectory; always cleaned up in finally, whatever the outcome.
                cacheRoot = string.IsNullOrEmpty(_config.StagingDir) ? DefaultClientCacheRoot() : _config.StagingDir;
                CleanStaleClientCacheDirs(cacheRoot, DateTime.UtcNow, log);
                runDir = CreateRunCacheDir(cacheRoot, log);
                if (string.IsNullOrEmpty(runDir)) { result.Error = "无法创建本轮缓存目录 / failed to create run cache dir"; return result; }
                string staging = runDir;
                var installedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var deviceById = (devices ?? new List<D>()).Where(d => !string.IsNullOrEmpty(d.Id)).ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
                var prepared = new List<PreparedLanCandidate>();
                int total = candidates.Count, current = 0;
                // Phase 1: cache, extract and verify every selected package before installing anything.
                InstallModelPackages(candidates, devices, staging, result, ct, progress, log);
                foreach (var cand in candidates.Where(c => string.IsNullOrEmpty(c.PackageId)))
                {
                    if (ct.IsCancellationRequested) { result.Cancelled = true; result.Error = "cancelled"; break; }
                    current++;
                    string infName = Path.GetFileName(cand.InfRelativePath ?? "");
                    string displayName = string.IsNullOrWhiteSpace(cand.FriendlyName) ? (string.IsNullOrWhiteSpace(cand.Class) ? "未知硬件" : cand.Class) : cand.FriendlyName;
                    ReportStep(progress, current, total, displayName, "下载缓存");
                    if (string.IsNullOrEmpty(cand.ContentHash)) { log("跳过无哈希候选 / Skip candidate without hash: " + cand.InfRelativePath); result.InstalledSkip++; continue; }
                    string skipWhy;
                    if (!ShouldInstall(cand, deviceById, out skipWhy)) { result.InstalledSkip++; log("跳过已是最新 / Skip up-to-date: " + infName + ": " + skipWhy); continue; }
                    if (!installedHashes.Add(cand.UserSelectedVersion || cand.IsExtension ? cand.DeviceInstanceId + "|" + cand.ContentHash : cand.ContentHash)) { log("合并同包设备，安装后逐设备核验 / Shared package, verify each target after installation: " + cand.DeviceInstanceId); continue; }

                    if (!string.IsNullOrEmpty(cand.PackageId) && !System.Text.RegularExpressions.Regex.IsMatch(cand.PackageId, "^model-[a-f0-9]{64}$")) throw new InvalidDataException("Invalid model package identity");
                    string extractDir = Path.Combine(staging, string.IsNullOrEmpty(cand.PackageId) ? cand.ContentHash.Substring(0, Math.Min(8, cand.ContentHash.Length)) : cand.PackageId.Substring(0, 22));
                    string zip = extractDir + ".zip";
                    string downloadServer = string.IsNullOrWhiteSpace(cand.DownloadBaseUrl) ? _config.ServerUrl : cand.DownloadBaseUrl.TrimEnd('/');
                    if (string.IsNullOrEmpty(cand.PackageId) || LocateInf(extractDir, cand.InfRelativePath, cand.PackageInfPath) == null) {
                    if (!DownloadToFile(downloadServer + "/api/download?path=" + Uri.EscapeDataString(cand.InfRelativePath) + (string.IsNullOrEmpty(cand.PackageId) ? "" : "&package=" + Uri.EscapeDataString(cand.PackageId)), zip, ct,
                        (bytes, length, rate) => ReportTransfer(progress, current, total, displayName, cand.InfRelativePath, bytes, length, rate)))
                    { result.InstalledFail++; log("下载失败 / Download failed: " + cand.InfRelativePath); continue; }
                    result.Downloaded++;
                    ReportStep(progress, current, total, displayName, "解压缓存");
                    try { ExtractZipFile(zip, extractDir, ct); }
                    catch (Exception ex) { result.InstalledFail++; log("解压失败 / Extract failed: " + ex.Message); continue; }
                    }

                    string infAbs = LocateInf(extractDir, cand.InfRelativePath, cand.PackageInfPath);
                    if (infAbs == null) { result.InstalledFail++; log("未找到 INF / INF not found in package: " + cand.InfRelativePath); continue; }
                    ReportStep(progress, current, total, displayName, "校验缓存");
                    string hash = Sha256File(infAbs);
                    if (!string.Equals(hash, cand.ContentHash, StringComparison.OrdinalIgnoreCase))
                    { result.InstalledSkip++; log("哈希不匹配，拒绝安装 / Hash mismatch, refusing to install: " + cand.InfRelativePath); continue; }
                    // Pre-install revalidation on the ACTUAL extracted INF with the current device IDs
                    // and current OS; a rejection is logged with its precise reason, never a bare skip.
                    D revalDev; deviceById.TryGetValue(cand.DeviceInstanceId, out revalDev);
                    string revalCode;
                    string revalWhy = RevalidateCandidateInf(cand, infAbs, revalDev, out revalCode);
                    if (revalWhy != null)
                    {
                        if (string.Equals(revalCode, "CurrentDriverPreferred", StringComparison.OrdinalIgnoreCase))
                            RecordCurrentDriverPreferred(cand, revalDev, log);
                        result.InstalledSkip++; log(revalWhy + " — " + Path.GetFileName(infAbs)); continue;
                    }
                    prepared.Add(new PreparedLanCandidate { Candidate = cand, ExtractDir = extractDir, InfPath = infAbs });
                    log("已缓存并校验 / Cached and verified: " + infName);
                }

                if (result.Cancelled) return result;
                prepared = prepared.OrderBy(p => p.Candidate.IsExtension).ToList();
                log("全部候选缓存完成，开始统一安装 / All packages cached; starting installation: " + prepared.Count);
                // Phase 2: only after every downloadable package has been cached and verified, install them.
                for (int i = 0; i < prepared.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var item = prepared[i];
                    var cand = item.Candidate;
                    string infName = Path.GetFileName(cand.InfRelativePath ?? "");
                    ReportStep(progress, i + 1, prepared.Count, string.IsNullOrWhiteSpace(cand.FriendlyName) ? infName : cand.FriendlyName, "安装");
                    if (!ExtensionParentReady(cand, deviceById, log)) { result.InstalledSkip++; continue; }
                    var rec = new InfRecord { Path = item.InfPath, ContentHash = cand.ContentHash, Class = cand.Class, ClassGuid = cand.ClassGuid,
                        DriverVerDate = cand.DriverVerDate, DriverVerVersion = cand.DriverVerVersion, Provider = cand.Provider,
                        HardwareIds = new List<string>(), CompatibleIds = new List<string>() };
                    var sum = DriverInstallExecutor.Execute(new List<InfRecord> { rec }, new List<string> { item.ExtractDir }, _config.DedupeByContent,
                        _config.WhatIf, _config.Deploy, _config.InstallTimeoutSeconds, _config.VerifySignature,
                        _logger, _admin, _sig, InstallationRunner(cand), _sys, abs => string.Equals(Sha256File(abs), cand.ContentHash, StringComparison.OrdinalIgnoreCase), null);
                    if (!_config.WhatIf && !cand.UserSelectedVersion && !cand.IsExtension && (_runner == null || _runner is PnpInstallRunner))
                    {
                        foreach (var installItem in sum.Items)
                            LanPnpRejectionStore.RecordPnputilLatest(candidates, cand.ContentHash, installItem, deviceById, _osTarget, _logger);
                    }
                    ConfirmInstalledBinding(cand, sum, result, ct, log);
                    result.InstalledOk += sum.Ok; result.InstalledSkip += sum.Skip; result.InstalledFail += sum.Fail;
                    D previous; deviceById.TryGetValue(cand.DeviceInstanceId, out previous);
                    SendInstallEvent(cand, sum, previous, !_config.WhatIf && (_runner == null || _runner is PnpInstallRunner));
                    if (sum.Ok > 0) LanInstallReceiptStore.Mark(cand.DeviceInstanceId, cand.ContentHash);
                    if (!_config.WhatIf && (_runner == null || _runner is PnpInstallRunner))
                    {
                        foreach (var shared in candidates.Where(x => !cand.UserSelectedVersion && !cand.IsExtension && !x.IsExtension && !x.UserSelectedVersion && x.DeviceInstanceId != cand.DeviceInstanceId && string.Equals(x.ContentHash, cand.ContentHash, StringComparison.OrdinalIgnoreCase)))
                        {
                            var actual = new D { Id = shared.DeviceInstanceId };
                            bool bound = CurrentDriverState.RefreshDevice(actual, _logger) && !actual.Bad && LanInstalledPackage.MatchesCurrentInf(actual.Inf, shared.ContentHash);
                            D prior; deviceById.TryGetValue(shared.DeviceInstanceId, out prior);
                            SendInstallEvent(shared, new Sum { Ok = bound ? 1 : 0, Fail = bound ? 0 : sum.Fail, Reboot = sum.Reboot }, prior, bound);
                            if (bound) LanInstallReceiptStore.Mark(shared.DeviceInstanceId, shared.ContentHash);
                            else result.InstalledUnverified++;
                        }
                    }
                    if (sum.Reboot) result.Reboot = true;
                    log("安装 / Install " + infName + ": ok=" + sum.Ok + " skip=" + sum.Skip + " fail=" + sum.Fail);
                }
                return result;
            }
            catch (OperationCanceledException) { result.Cancelled = true; result.Error = "cancelled"; return result; }
            catch (Exception ex) { result.Error = "install error: " + ex.Message; if (_logger != null) _logger.W("[ERROR] LAN 安装异常 / install error: " + ex); return result; }
            finally
            {
                SafeDeleteRunCacheDir(runDir, cacheRoot, log);
                if (captureInstallOnly && _logger != null) _logger.E -= capture;
                try { lock (logGate) if (logBuffer.Length > 0) ClientTelemetryQueue.Enqueue(TelemetryDirectory, NewTelemetryEvent("log", logBuffer.ToString())); }
                catch (Exception ex) { if (_logger != null) _logger.W("[WARN] 日志队列写入失败，详细日志仍保存在本机 / telemetry write failed: " + ex.Message); }
                FlushTelemetry();
            }
        }

        // ---------------------------------------------------------------------------------
        //  Controlled closed loop: after the user-confirmed install round, re-collect hardware,
        //  re-report and re-scan; keep auto-installing only candidates that match the original
        //  user selection / the recommended safe policy, until no candidate remains, at most
        //  MaxRounds rounds. Each DeviceInstanceId+ContentHash is processed at most once per
        //  inventory state; dependency changes permit at most three attempts. Every round honours cancellation.
        // ---------------------------------------------------------------------------------

        /// <summary>Round budget for the controlled closed loop.</summary>
        public const int ClosedLoopMaxRounds = 5;

        public sealed class LanClosedLoopOptions
        {
            /// <summary>Maximum number of continuation rounds (default 5).</summary>
            public int MaxRounds = ClosedLoopMaxRounds;
            public bool AllowUpdates;
            // Multiple quiet snapshots are necessary: bus drivers publish children asynchronously.
            public int QuietPasses = 1;
            public int QuietDelayMs;
            public bool RescanBeforeCollection;
            /// <summary>Optional extra authorization filter applied to auto-continue candidates in
            /// addition to the built-in recommended safe policy. Null = policy only.</summary>
            public Func<LanCandidateDto, bool> ExtraCandidateFilter;
        }

        public sealed class LanCompletionProblem
        {
            public string DeviceInstanceId = "", FriendlyName = "", ReasonCode = "", Reason = "", CurrentInf = "", CurrentVersion = "";
            public int ProblemCode;
            public List<string> HardwareIds = new List<string>(), CompatibleIds = new List<string>();
        }

        public sealed class LanClosedLoopResult
        {
            public int Rounds;
            public int Downloaded;
            public int InstalledOk, InstalledSkip, InstalledFail, InstalledUnverified;
            public bool Reboot, Cancelled;
            public bool NoCandidatesRemaining;
            public bool StoppedAtMaxRounds;
            public int RemainingProblemDevices;
            public bool CompletionAudited;
            public List<LanCompletionProblem> FinalProblems = new List<LanCompletionProblem>();
            public string Error = "";
            /// <summary>DeviceInstanceId|ContentHash keys processed this session (dedup guard).</summary>
            public List<string> ProcessedKeys = new List<string>();
            public List<string> Log = new List<string>();
        }

        /// <summary>Recommended safe policy for AUTO-continue rounds: only INSTALL-type candidates
        /// (device Bad / no CurrentInf / empty current version — i.e. genuinely missing or broken
        /// drivers) with a trusted signature. Version UPDATE candidates and untrusted-signature
        /// candidates are never auto-installed; those always require explicit user consent.</summary>
        private bool IsSafeAutoCandidate(LanCandidateDto c, Dictionary<string, D> deviceById, LanClosedLoopOptions opts)
        {
            if (!string.IsNullOrEmpty(c.LocalRejectionReason)) return false;
            D d;
            deviceById.TryGetValue(c.DeviceInstanceId, out d);
            if (c.IsExtension)
            {
                if (!c.NeedInstall || d == null || LanExtensionPackages.Satisfied(c, d, _osTarget)) return false;
                if (!opts.AllowUpdates && LanExtensionPackages.HasAppliedLineage(c, d)) return false;
                if (_config.VerifySignature && string.Equals(c.SignatureStatus, "untrusted", StringComparison.OrdinalIgnoreCase)) return false;
                return opts.ExtraCandidateFilter == null || opts.ExtraCandidateFilter(c);
            }
            bool installType = d == null || d.Bad || string.IsNullOrEmpty(d.Inf) || string.IsNullOrEmpty(d.Version);
            if (!installType && !opts.AllowUpdates) return false;   // SCCM explicitly authorizes updates.
            if (!installType && !NeedsInstall(d, c.DriverVerVersion)) return false;
            if (_config.VerifySignature && string.Equals(c.SignatureStatus, "untrusted", StringComparison.OrdinalIgnoreCase)) return false;
            return opts.ExtraCandidateFilter == null || opts.ExtraCandidateFilter(c);
        }

        internal static string DeviceInventoryFingerprint(IEnumerable<D> devices)
        {
            return string.Join("\n", devices.Where(d => d != null).Select(d => d.Id + "|" + d.Error + "|" + d.Inf + "|" + d.Version + "|" +
                string.Join(",", (d.H ?? new List<string>()).OrderBy(v => v, StringComparer.OrdinalIgnoreCase)) + "|" +
                string.Join(",", (d.K ?? new List<string>()).OrderBy(v => v, StringComparer.OrdinalIgnoreCase)))
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase));
        }
        private void AuditCompletion(LanClosedLoopResult result, IList<D> devices, LanReportResponse report, IEnumerable<LanCandidateDto> attempted, Action<string> log)
        {
            result.FinalProblems.Clear();
            var tried = attempted.ToList();
            foreach (var d in devices)
            {
                var candidates = (report.Candidates ?? new List<LanCandidateDto>()).Where(c => c.DeviceInstanceId == d.Id).ToList();
                var previous = tried.Where(c => c.DeviceInstanceId == d.Id).ToList();
                bool unresolved = d.NeedsAttention || previous.Any(c => c.IsExtension ? !LanExtensionPackages.Satisfied(c, d, _osTarget) :
                    c.UserSelectedVersion ? !IsReceiptSuppressed(c, d) : candidates.Any(next => !next.IsExtension && next.NeedInstall));
                if (!unresolved) continue;
                var rejected = candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c.LocalRejectionReason));
                var missing = (report.UnmatchedDevices ?? new List<LanUnmatchedDeviceDto>()).FirstOrDefault(m => m.DeviceInstanceId == d.Id);
                string code = rejected != null ? "CandidateRejected" : missing != null ? missing.ReasonCode : result.Reboot ? "RestartRequiredOrUnverified" : previous.Count > 0 ? "BindingNotConfirmed" : "NotInstalled";
                string reason = rejected != null ? rejected.LocalRejectionReason : missing != null ? missing.Reason : "设备或所需配套驱动未核验完成；请查看安装记录";
                result.FinalProblems.Add(new LanCompletionProblem { DeviceInstanceId = d.Id, FriendlyName = d.Name, ProblemCode = d.Error,
                    CurrentInf = d.Inf, CurrentVersion = d.Version, HardwareIds = new List<string>(d.H ?? new List<string>()), CompatibleIds = new List<string>(d.K ?? new List<string>()), ReasonCode = code, Reason = reason });
                log("最终未完成 / Final incomplete: " + d.Id + "; code=" + d.Error + "; reason=" + code + "; " + reason);
            }
            foreach (var c in tried.Where(c => !devices.Any(d => d.Id == c.DeviceInstanceId)).GroupBy(c => c.DeviceInstanceId).Select(g => g.First()))
                result.FinalProblems.Add(new LanCompletionProblem { DeviceInstanceId = c.DeviceInstanceId, FriendlyName = c.FriendlyName, ReasonCode = "DeviceNotPresent", Reason = "目标设备未出现在最终硬件清单中" });
            result.CompletionAudited = true;
            log("最终核验 / Completion audit: devices=" + devices.Count + "; incomplete=" + result.FinalProblems.Count);
        }

        private static void AccumulateLoop(LanClosedLoopResult acc, LanClientResult res)
        {
            acc.Downloaded += res.Downloaded;
            acc.InstalledOk += res.InstalledOk;
            acc.InstalledSkip += res.InstalledSkip;
            acc.InstalledFail += res.InstalledFail;
            acc.InstalledUnverified += res.InstalledUnverified;
            acc.Reboot |= res.Reboot;
            foreach (var line in res.Log) acc.Log.Add(line);
        }

        /// <summary>Run the controlled closed loop. <paramref name="initialCandidates"/> (the user-confirmed
        /// picks, may be null) are installed first as round 0; then up to MaxRounds continuation rounds of
        /// collect → report → scan → install only safe, authorized, not-yet-processed candidates. Returns
        /// the accumulated outcome. Never installs an update-type or untrusted candidate automatically.</summary>
        public LanClosedLoopResult RunControlledLoop(IList<LanCandidateDto> initialCandidates, CancellationToken ct,
            IProgress<LanInstallProgress> installProgress, IProgress<string> roundProgress, LanClosedLoopOptions options)
        {
            BeginTelemetryRun(); Interlocked.Increment(ref _telemetryLoopDepth);
            var acc = new LanClosedLoopResult();
            var opts = options != null ? options : new LanClosedLoopOptions();
            int maxRounds = Math.Max(1, opts.MaxRounds);
            int quietPasses = Math.Max(1, opts.QuietPasses), quietCount = 0, quietPolls = 0;
            string quietInventory = null;
            Action<string> log = m => { acc.Log.Add(m); if (_logger != null) _logger.W("[INFO] " + m); };
            var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var processedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var attempts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var attemptedInventory = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var attemptedCandidates = new Dictionary<string, LanCandidateDto>(StringComparer.OrdinalIgnoreCase);
            var explicitlySelectedDevices = new HashSet<string>((initialCandidates ?? new List<LanCandidateDto>()).Where(c => c != null && c.UserSelectedVersion).Select(c => c.DeviceInstanceId), StringComparer.OrdinalIgnoreCase);
            var collector = _hc ?? new WmiHardwareCollector();
            try
            {
                // Round 0: exactly what the user confirmed.
                if (initialCandidates != null && initialCandidates.Count > 0)
                {
                    acc.Rounds++;
                    if (roundProgress != null) roundProgress.Report("第 0 轮：安装用户确认的 " + initialCandidates.Count + " 个候选…");
                    foreach (var c in initialCandidates)
                        if (c != null && !string.IsNullOrEmpty(c.ContentHash) && processed.Add(CandidateKey(c.DeviceInstanceId, c.ContentHash)))
                            acc.ProcessedKeys.Add(CandidateKey(c.DeviceInstanceId, c.ContentHash));
                    var initialDevices = _lastReportedDevices ?? collector.Collect(_logger) ?? new List<D>();
                    foreach (var c in initialCandidates.Where(c => c != null && !string.IsNullOrEmpty(c.ContentHash))) {
                        string key = CandidateKey(c.DeviceInstanceId, c.ContentHash);
                        attempts[key] = 1; attemptedInventory[key] = DeviceInventoryFingerprint(initialDevices); attemptedCandidates[key] = c;
                    }
                    var res0 = InstallCandidates(initialCandidates, initialDevices, ct, installProgress);
                    AccumulateLoop(acc, res0);
                    foreach (var c in initialCandidates) if (c != null && !string.IsNullOrEmpty(c.PackageId)) processedModels.Add(c.PackageId);
                    if (res0.Cancelled) { acc.Cancelled = true; return acc; }
                }
                // Continuation rounds.
                for (int round = 1; round <= maxRounds; round++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (roundProgress != null) roundProgress.Report("第 " + round + "/" + maxRounds + " 轮：重新采集硬件并上报…");
                    if (opts.RescanBeforeCollection && !_config.WhatIf && _sys != null) _sys.ScanDevices(60, _logger);
                    var devices = collector.Collect(_logger) ?? new List<D>();
                    if (ct.IsCancellationRequested) { acc.Cancelled = true; log("取消：第 " + round + " 轮采集后停止 / cancelled after round " + round + " collection"); break; }
                    LanReportResponse resp = Report(devices, _machine, ct, null);
                    var deviceById = devices.Where(d => !string.IsNullOrEmpty(d.Id)).ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
                    string currentInventory = DeviceInventoryFingerprint(devices);
                    var pending = resp.Candidates.Where(delegate(LanCandidateDto c)
                    {
                        if (c == null || string.IsNullOrEmpty(c.ContentHash)) return false;
                        if (!c.IsExtension && explicitlySelectedDevices.Contains(c.DeviceInstanceId)) return false;
                        string key = CandidateKey(c.DeviceInstanceId, c.ContentHash);
                        if (!string.IsNullOrEmpty(c.PackageId) && processedModels.Contains(c.PackageId)) return false;
                        D d;
                        deviceById.TryGetValue(c.DeviceInstanceId, out d);
                        if (processed.Contains(key)) {
                            int count; string previous;
                            if ((c.IsExtension && (!attemptedInventory.TryGetValue(key, out previous) || previous == currentInventory)) ||
                                !attempts.TryGetValue(key, out count) || count >= 3 ||
                                (!c.IsExtension && (d == null || (!d.NeedsAttention && !string.IsNullOrEmpty(d.Inf) && !string.IsNullOrEmpty(d.Version)))) ) return false;
                        }
                        if (IsReceiptSuppressed(c, d)) return false;                      // receipt only when actually in effect
                        return IsSafeAutoCandidate(c, deviceById, opts);
                    }).ToList();
                    if (pending.Count == 0)
                    {
                        string inventory = currentInventory;
                        quietCount = string.Equals(quietInventory, inventory, StringComparison.Ordinal) ? quietCount + 1 : 1;
                        quietInventory = inventory;
                        quietPolls++;
                        if (quietCount < quietPasses && quietPolls >= Math.Max(12, quietPasses))
                        {
                            acc.RemainingProblemDevices = devices.Count(d => d.NeedsAttention);
                            acc.Error = "设备枚举持续变化，未确认部署完成 / PnP inventory did not settle";
                            log(acc.Error);
                            break;
                        }
                        if (quietCount < quietPasses)
                        {
                            log("等待设备枚举稳定 / Waiting for PnP enumeration: " + quietCount + "/" + quietPasses);
                            if (ct.WaitHandle.WaitOne(Math.Max(0, opts.QuietDelayMs))) ct.ThrowIfCancellationRequested();
                            round--; // Quiet polls do not consume an installation round.
                            continue;
                        }
                        acc.RemainingProblemDevices = devices.Count(d => d.NeedsAttention);
                        acc.NoCandidatesRemaining = !resp.Candidates.Any(c => c != null && (c.IsExtension || !explicitlySelectedDevices.Contains(c.DeviceInstanceId)) && (string.IsNullOrEmpty(c.PackageId) || !processedModels.Contains(c.PackageId)) && IsSafeAutoCandidate(c, deviceById, opts) && !IsReceiptSuppressed(c, deviceById.ContainsKey(c.DeviceInstanceId) ? deviceById[c.DeviceInstanceId] : null));
                        AuditCompletion(acc, devices, resp, attemptedCandidates.Values, log);
                        foreach (var remaining in devices.Where(d => d.Bad))
                            log("安装后仍有异常设备 / Final device problem: " + remaining.Id + "; code=" + remaining.Error + "; INF=" + remaining.Inf + "; hardware=[" + string.Join(",", remaining.H.ToArray()) + "]");
                        log("第 " + round + " 轮无可自动继续安装的候选，结束自动安装；不表示被跳过的驱动已安装 / Round " + round + ": no safe unprocessed candidates; skipped drivers are not confirmed installed");
                        break;
                    }
                    quietCount = 0; quietPolls = 0; quietInventory = null;
                    acc.Rounds++;
                    log("第 " + round + " 轮发现 " + pending.Count + " 个符合安全策略的候选 / Round " + round + ": " + pending.Count + " safe candidates");
                    foreach (var c in pending)
                    {
                        string key = CandidateKey(c.DeviceInstanceId, c.ContentHash);
                        if (processed.Add(key)) acc.ProcessedKeys.Add(key);
                        int count; attempts.TryGetValue(key, out count); attempts[key] = count + 1;
                        attemptedInventory[key] = currentInventory; attemptedCandidates[key] = c;
                        if (count > 0) log("依赖状态变化后重试 / Retry after PnP dependency change / fresh scan: " + c.DeviceInstanceId + " attempt=" + (count + 1) + "/3");
                    }
                    if (roundProgress != null) roundProgress.Report("第 " + round + "/" + maxRounds + " 轮：安装 " + pending.Count + " 个新候选…");
                    var res = InstallCandidates(pending, devices, ct, installProgress);
                    AccumulateLoop(acc, res);
                    foreach (var c in pending) if (!string.IsNullOrEmpty(c.PackageId)) processedModels.Add(c.PackageId);
                    if (res.Cancelled) { acc.Cancelled = true; break; }
                    if (round == maxRounds) { acc.StoppedAtMaxRounds = true; log("已达最大轮数 " + maxRounds + "，停止闭环 / max rounds reached; loop stopped"); }
                }
                return acc;
            }
            catch (OperationCanceledException) { acc.Cancelled = true; acc.Error = "cancelled"; return acc; }
            catch (Exception ex)
            {
                // A cancellation mid-request surfaces as a failed report/download, not an OCE.
                if (ct.IsCancellationRequested) { acc.Cancelled = true; acc.Error = "cancelled"; return acc; }
                acc.Error = "closed loop error: " + ex.Message;
                if (_logger != null) _logger.W("[ERROR] 闭环异常 / closed loop error: " + ex);
                return acc;
            }
            finally {
                if (_liveDiagnostics != null) PublishDiagnosticState(acc.Cancelled ? "cancelled" : !string.IsNullOrEmpty(acc.Error) ? "failed" :
                    acc.CompletionAudited && !acc.StoppedAtMaxRounds && acc.FinalProblems.Count == 0 ? "completed" : "unverified",
                    "rounds=" + acc.Rounds + "; audited=" + acc.CompletionAudited + "; incomplete=" + acc.FinalProblems.Count + "; reboot=" + acc.Reboot + "; error=" + acc.Error);
                Interlocked.Decrement(ref _telemetryLoopDepth);
            }
        }

        // ---------------------------------------------------------------------------------
        //  Run-scoped cache directory hygiene. Every download/install run works inside a
        //  unique per-run subdirectory of the cache root; that subdirectory — and only that
        //  one — is removed in a finally block, whatever the outcome. Startup hygiene removes
        //  leftover run directories older than 24 hours from the cache root. Every delete is
        //  guarded so the cache root itself, other running instances' directories, and any
        //  path outside the root are never touched; cleanup failures are logged, never thrown.
        // ---------------------------------------------------------------------------------

        /// <summary>Default client cache root: %ProgramData%\UDA\C (short for native INF APIs).</summary>
        public static string DefaultClientCacheRoot()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UDA", "C");
        }

        private static string NormalizeDirPath(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        /// <summary>True when dir is a direct child of root (case-insensitive full-path comparison).
        /// Used as the safety guard for every cache delete: the root itself and anything outside or
        /// nested deeper than one level are refused, and a drive root is never accepted as a cache root.</summary>
        public static bool IsDirectChildDir(string dir, string root)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(root)) return false;
                string d = NormalizeDirPath(dir), r = NormalizeDirPath(root);
                if (string.Equals(d, r, StringComparison.OrdinalIgnoreCase)) return false;
                if (r.Length <= 3) return false; // never treat a drive root as a cache root
                string rp = r + Path.DirectorySeparatorChar;
                string dp = d + Path.DirectorySeparatorChar;
                return dp.StartsWith(rp, StringComparison.OrdinalIgnoreCase)
                    && d.IndexOf(Path.DirectorySeparatorChar, rp.Length) < 0;
            }
            catch { return false; }
        }

        /// <summary>Create a unique per-run cache subdirectory under the cache root. Returns "" on
        /// failure (already logged); the caller then runs without a cache directory.</summary>
        public static string CreateRunCacheDir(string root, Action<string> log)
        {
            try
            {
                string r = string.IsNullOrWhiteSpace(root) ? DefaultClientCacheRoot() : root;
                Directory.CreateDirectory(r);
                string name = "run-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture).Substring(0, 12);
                string dir = Path.Combine(r, name);
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch (Exception ex) { if (log != null) log("创建本轮缓存目录失败 / Failed to create run cache dir under " + root + ": " + ex.Message); return ""; }
        }

        /// <summary>Delete exactly the given per-run cache directory, whatever the run's outcome.
        /// Refuses to delete anything that is not a direct child of the cache root — including the
        /// root itself, other instances' directories, or any path outside it. Failures are logged, never thrown.</summary>
        public static void SafeDeleteRunCacheDir(string runDir, string root, Action<string> log)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(runDir)) return;
                if (!IsDirectChildDir(runDir, root))
                { if (log != null) log("跳过本轮缓存清理，路径不在缓存根目录内 / Skip run cache cleanup, path not a direct child of cache root: " + runDir); return; }
                if (!Directory.Exists(runDir)) return;
                Directory.Delete(runDir, true);
            }
            catch (Exception ex) { if (log != null) log("本轮缓存清理失败（忽略）/ Run cache cleanup failed (ignored): " + runDir + " => " + ex.Message); }
        }

        /// <summary>Startup hygiene: delete direct-child directories of the cache root whose last write
        /// time is older than 24 hours. Never touches anything outside the root or the root itself;
        /// reparse points are skipped and failures are logged, never thrown.</summary>
        public static void CleanStaleClientCacheDirs(string root, DateTime nowUtc, Action<string> log)
        {
            const int StaleHours = 24;
            try
            {
                string r = string.IsNullOrWhiteSpace(root) ? DefaultClientCacheRoot() : root;
                if (NormalizeDirPath(r).Length <= 3 || !Directory.Exists(r)) return;
                foreach (string dir in Directory.GetDirectories(r))
                {
                    try
                    {
                        if (!IsDirectChildDir(dir, r)) continue;                            // path-escape guard
                        var info = new DirectoryInfo(dir);
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue; // never follow links
                        if (nowUtc - info.LastWriteTimeUtc < TimeSpan.FromHours(StaleHours)) continue;
                        Directory.Delete(dir, true);
                    }
                    catch (Exception ex) { if (log != null) log("遗留缓存目录清理失败（忽略）/ Stale cache cleanup failed (ignored): " + dir + " => " + ex.Message); }
                }
            }
            catch (Exception ex) { if (log != null) log("遗留缓存清理失败（忽略）/ Stale cache cleanup failed (ignored): " + root + " => " + ex.Message); }
        }

        private static void ReportStep(IProgress<LanInstallProgress> progress, int current, int total, string name, string stage)
        {
            if (progress != null) progress.Report(new LanInstallProgress { Current = current, Total = total, Name = name, Stage = stage });
        }

        private static void ReportTransfer(IProgress<LanInstallProgress> progress, int current, int total, string name, string inf,
            long downloaded, long length, double bytesPerSecond)
        {
            if (progress != null) progress.Report(new LanInstallProgress { Current = current, Total = total, Name = name, Stage = "下载",
                InfRelativePath = inf ?? "", DownloadedBytes = downloaded, TotalBytes = length, BytesPerSecond = bytesPerSecond });
        }

        public static bool IsNicLackingDriver(D d)
        {
            if (d == null) return false;
            string cg = (d.ClassGuid ?? "").Trim();
            bool isNic = string.Equals(d.Class, "Net", StringComparison.OrdinalIgnoreCase)
                || cg.Equals("{4d36e972-e325-11ce-bfc1-08002be10318}", StringComparison.OrdinalIgnoreCase);
            return isNic && (string.IsNullOrEmpty(d.Inf) || d.Bad);
        }

        /// <summary>Install policy: a device that is Bad, has no CurrentInf, has an empty current version, or
        /// otherwise has no valid installed driver is an INSTALL candidate whenever the repo matches it — a
        /// historical receipt must never hide it. Only a genuinely installed OLDER driver is an UPDATE.</summary>
        public static bool NeedsInstall(D d, string candidateVersion)
        {
            if (d == null) return true;
            if (d.Bad || string.IsNullOrEmpty(d.Inf) || string.IsNullOrEmpty(d.Version)) return true;
            return LanVersion.IsNewer(candidateVersion, d.Version);
        }

        /// <summary>A receipt may only suppress a candidate when the device is healthy (not Bad, with a
        /// known INF and version) AND currently ACTUALLY RUNS that exact package (its in-effect INF hashes
        /// exact package (its in-effect INF hashes to the candidate's content hash). A receipt left behind by
        /// a failed install, a changed driver, or a device that since lost its driver must not hide anything.</summary>
        public static bool IsReceiptSuppressed(LanCandidateDto cand, D d)
        {
            if (cand == null || string.IsNullOrEmpty(cand.ContentHash)) return false;
            if (cand.IsExtension) return false; // extensions have independent, live per-device verification
            // Live INF bytes, not an installation receipt, prove that this exact package is active.
            if (d == null || d.Bad || string.IsNullOrEmpty(d.Inf) || string.IsNullOrEmpty(d.Version)) return false;
            return LanInstalledPackage.MatchesCurrentInf(d.Inf, cand.ContentHash);
        }

        public static bool IsOutrankedByCurrentDriver(LanCandidateDto candidate, D device)
        {
            return IsOutrankedByCurrentDriver(candidate, device, null);
        }

        private static bool IsOutrankedByCurrentDriver(LanCandidateDto candidate, D device, Action<string> trace)
        {
            if (candidate == null || device == null || device.Bad || string.IsNullOrEmpty(device.Inf) || string.IsNullOrEmpty(device.Version)) return false;
            string local = LanInstalledPackage.FindImportedInf(candidate.ContentHash);
            return local != null && WindowsDriverPreference.IsBetterForDevice(device, local, trace) == false;
        }

        /// <summary>Session dedup key: DeviceInstanceId + ContentHash (case-insensitive). Used by the
        /// controlled closed loop so the same item is never processed twice in one session.</summary>
        public static string CandidateKey(string deviceInstanceId, string contentHash)
        {
            return (deviceInstanceId ?? "").Trim().ToUpperInvariant() + "|" + (contentHash ?? "").Trim().ToUpperInvariant();
        }

        /// <summary>Install gate shared with the scan display via LanInstallPolicy. Returns false exactly
        /// when the scan would not have offered this candidate as installable, with a readable reason.</summary>
        private void ConfirmInstalledBinding(LanCandidateDto cand, Sum sum, LanClientResult result, CancellationToken ct, Action<string> log)
        {
            if (sum.Ok <= 0 || _config.WhatIf || (_runner != null && !(_runner is PnpInstallRunner))) return;
            if (cand.IsExtension)
            {
                var parent = (_lastReportedDevices ?? new List<D>()).FirstOrDefault(d => string.Equals(d.Id, cand.DeviceInstanceId, StringComparison.OrdinalIgnoreCase)) ??
                    new D { Id = cand.DeviceInstanceId, H = new List<string> { cand.MatchedId } };
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (LanExtensionPackages.Satisfied(cand, parent, _osTarget)) { log("配套扩展核验通过 / Extension applied: " + cand.DeviceInstanceId + " ExtensionId=" + cand.ExtensionId + " INF=" + cand.InfRelativePath); return; }
                    if (attempt < 2 && ct.WaitHandle.WaitOne(500)) ct.ThrowIfCancellationRequested();
                }
                result.InstalledUnverified += sum.Ok; sum.Ok = 0;
                log("扩展包安装命令成功，但设备尚未报告应用此 ExtensionId，不计为完成 / Extension application not confirmed: " + cand.DeviceInstanceId + " " + cand.ExtensionId + (sum.Reboot ? "；需要重启后复核" : ""));
                return;
            }
            var actual = new D { Id = cand.DeviceInstanceId };
            bool read = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                read = CurrentDriverState.RefreshDevice(actual, _logger);
                if (read && !actual.Bad && LanInstalledPackage.MatchesCurrentInf(actual.Inf, cand.ContentHash))
                {
                    log("绑定核验通过 / Binding verified: " + actual.Id + " INF=" + actual.Inf + " version=" + actual.Version);
                    return;
                }
                if (attempt < 2 && ct.WaitHandle.WaitOne(500)) ct.ThrowIfCancellationRequested();
            }
            result.InstalledUnverified += sum.Ok;
            sum.Ok = 0;
            log("安装命令成功，但尚未确认候选驱动绑定，不计为已完成 / Binding not confirmed: " + cand.DeviceInstanceId +
                " expected=" + cand.DriverVerVersion + " actual=" + (read || actual.Bad ? actual.Inf + "/" + actual.Version + " problem=" + actual.Error : "设备状态不可读取") +
                (sum.Reboot ? "；系统报告需要重启" : "；请检查设备状态或后续回退记录"));
        }

        private IPnpInstallRunner InstallationRunner(LanCandidateDto candidate)
        {
            var runner = _runner ?? new PnpInstallRunner();
            return candidate.UserSelectedVersion && !candidate.IsExtension ? new SelectedDeviceInstallRunner(runner as IPnpDeviceInstallRunner, candidate.DeviceInstanceId) : runner;
        }
        private bool ExtensionParentReady(LanCandidateDto candidate, Dictionary<string, D> devices, Action<string> log)
        {
            if (!candidate.IsExtension) return true;
            D device;
            if (!devices.TryGetValue(candidate.DeviceInstanceId, out device)) return false;
            if (_runner == null || _runner is PnpInstallRunner) CurrentDriverState.RefreshDevice(device, _logger);
            bool ready = !device.Bad && !string.IsNullOrEmpty(device.Inf);
            if (!ready) log("配套扩展等待基础驱动完成 / Extension requires healthy base driver: " + candidate.DeviceInstanceId + " " + candidate.ExtensionId);
            return ready;
        }
        private string RevalidateCandidateInf(LanCandidateDto candidate, string path, D device, out string reasonCode)
        {
            if (!candidate.IsExtension) return RevalidateExtractedInf(path, device, _osTarget, out reasonCode, null, null, candidate.UserSelectedVersion);
            reasonCode = "ExtensionIdentityMismatch";
            var actual = InfParser.Parse(path, "downloaded-extension");
            if (!LanExtensionPackages.IsExtension(actual) || !string.Equals(actual.ExtensionId, candidate.ExtensionId, StringComparison.OrdinalIgnoreCase)) return "扩展 INF 的类别或 ExtensionId 与候选不一致";
            if (device == null) return "扩展 INF 找不到目标设备";
            var hardware = new List<string>(device.H ?? new List<string>()); var compatible = new List<string>(device.K ?? new List<string>());
            HardwareIdNormalizer.AddSafeMatchingAliases(hardware, compatible, device.Id);
            var verdict = InfApplicability.Evaluate(actual, _osTarget, hardware, compatible);
            reasonCode = verdict.ReasonCode;
            Guid extensionId;
            if (!Guid.TryParse(actual.ExtensionId, out extensionId) || !LanExtensionPackages.MatchesExtensionTarget(actual, extensionId, verdict)) return "扩展 INF 不适用于当前设备/系统：" + verdict.Reason;
            reasonCode = ""; return null;
        }
        private bool ShouldInstall(LanCandidateDto cand, Dictionary<string, D> deviceById, out string reason)
        {
            D d;
            deviceById.TryGetValue(cand != null ? cand.DeviceInstanceId : "", out d);
            if (cand != null && cand.IsExtension) { reason = LanInstallPolicy.Revalidate(cand, d, _osTarget, _config.VerifySignature); return reason == null; }
            if (!cand.UserSelectedVersion && LanPnpRejectionStore.Contains(cand, d, _osTarget))
            { reason = "Windows 已确认当前驱动优先于此包；设备绑定、候选包或系统变化前不再重复尝试 / Windows previously retained the current driver"; return false; }
            if (IsReceiptSuppressed(cand, d)) { reason = "当前设备实际绑定的 INF 与候选完全一致 / exact candidate already bound"; return false; }
            reason = LanInstallPolicy.Revalidate(cand, d, _osTarget, _config.VerifySignature);
            if (reason == null && !cand.UserSelectedVersion && IsOutrankedByCurrentDriver(cand, d,
                message => { if (_logger != null) _logger.W("[INFO] Driver Store rank / " + cand.DeviceInstanceId + " " + cand.InfRelativePath + ": " + message); }))
                reason = "Windows 完整 Driver Store 排名判定候选不是唯一最佳适用驱动 / Windows full Driver Store ranking prefers another candidate";
            return reason == null;
        }

        private void RecordCurrentDriverPreferred(LanCandidateDto candidate, D device, Action<string> log)
        {
            // The exact package is only available locally after the scan result was built. Once the
            // extracted INF proves Windows already prefers the current binding, remember that result
            // for this device/package/binding/OS so the next scan does not keep offering it.
            if (!LanPnpRejectionStore.Record(candidate, device, _osTarget)) return;
            string message = "暂停重复推荐 / Suppressed Windows-ranked candidate until device/package/OS changes: " +
                (candidate != null ? candidate.DeviceInstanceId + " " + candidate.InfRelativePath : "unknown candidate");
            if (log != null) log(message);
            else if (_logger != null) _logger.W("[INFO] " + message);
        }

        /// <summary>
        /// Second applicability gate, run against the ACTUAL downloaded-and-extracted INF file right
        /// before pnputil. The server ranked candidates against its indexed copy; this re-parses the
        /// bytes that would really be installed with the same InfParser and re-judges them with the
        /// SAME InfApplicability.Evaluate using the CURRENT device IDs and the CURRENT OS target, so a
        /// stale index, a device that changed between scan and install, or an OS change can never let
        /// an inapplicable INF reach the driver store. Returns null when the INF is applicable;
        /// otherwise an explicit, precise rejection reason (with a machine-readable code) that the
        /// caller logs instead of counting a bare "skip". Scan and install agree whenever the device
        /// and OS are unchanged, because both stages evaluate the same parsed record with the same
        /// engine and the same OsTarget.
        /// </summary>
        internal static string RevalidateExtractedInf(string infAbs, D d, OsTarget target, out string reasonCode)
        {
            return RevalidateExtractedInf(infAbs, d, target, out reasonCode, null);
        }

        private static string RevalidateExtractedInf(string infAbs, D d, OsTarget target, out string reasonCode, Action<string> trace, DriverScanFiles files = null, bool explicitVersion = false)
        {
            reasonCode = "";
            if (string.IsNullOrEmpty(infAbs) || !File.Exists(infAbs))
            { reasonCode = "InfNotFound"; return "二次校验拒绝 / pre-install revalidation rejected: [InfNotFound] 已解压的 INF 不存在 / extracted INF file not found"; }
            InfRecord parsed;
            try { parsed = files == null ? InfParser.Parse(infAbs, Path.GetFileName(infAbs)) : files.Parse(infAbs, Path.GetFileName(infAbs)); }
            catch (Exception ex)
            { reasonCode = "InfParseFailed"; return "二次校验拒绝 / pre-install revalidation rejected: [InfParseFailed] 无法解析已解压的 INF / failed to parse the extracted INF: " + ex.Message; }
            if (d == null)
            { reasonCode = "DeviceNotFound"; return "二次校验拒绝: [DeviceNotFound] 当前硬件清单中未找到设备，请重新扫描"; }
            var hardwareIds = new List<string>(d.H ?? new List<string>());
            var compatibleIds = new List<string>(d.K ?? new List<string>());
            HardwareIdNormalizer.AddSafeMatchingAliases(hardwareIds, compatibleIds, d.Id);
            var verdict = InfApplicability.Evaluate(parsed, target, hardwareIds, compatibleIds);
            if (verdict.Applicable)
            {
                if ((d.Bad || string.IsNullOrEmpty(d.Inf)) && WindowsDriverPreference.IsCompatibleWithDevice(d, infAbs, trace) == false)
                { reasonCode = "WindowsNoCompatibleDriver"; return "Windows 未将候选列为该设备的兼容驱动 / Windows found no compatible driver in candidate INF"; }
                bool? better = explicitVersion ? (bool?)null : WindowsDriverPreference.IsBetterForDevice(d, infAbs, trace, files);
                if (trace != null) trace("candidate selection verdict=" + (better.HasValue ? better.Value.ToString() : "unknown"));
                if (better == false)
                { reasonCode = "CurrentDriverPreferred"; return "Windows 排名/日期/版本判定现有驱动更优或等效 / current driver outranks candidate"; }
                return null;
            }
            reasonCode = verdict.ReasonCode;
            return "二次校验拒绝 / pre-install revalidation rejected: [" + verdict.ReasonCode + "] " + verdict.Reason;
        }

        private bool WaitForServer(CancellationToken ct, Action<string> log)
        {
            int delay = _config.BackoffInitialMs;
            for (int i = 0; i < _config.MaxRetries; i++)
            {
                if (ct.IsCancellationRequested) return false;
                LanHealth h;
                if (GetJson(_config.ServerUrl + "/api/health", ct, out h) && h != null)
                {
                    if (i > 0) log("服务器恢复 / Server reachable after " + (i + 1) + " attempts");
                    return true;
                }
                try { ct.WaitHandle.WaitOne(delay); }
                catch { return false; }
                delay = Math.Min(delay * 2, 5000);
            }
            return false;
        }

        // ----- HTTP helpers (HttpWebRequest) -----

        private bool GetJson<T>(string url, CancellationToken ct, out T result)
        {
            result = default(T);
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = _config.TimeoutMs;
                req.Proxy = null;
                req.ReadWriteTimeout = _config.TimeoutMs;
                using (ct.Register(() => req.Abort()))
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if (resp.StatusCode != HttpStatusCode.OK) return false;
                    string body = ReadResponse(resp);
                    result = new JavaScriptSerializer().Deserialize<T>(body);
                    return true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (WebException ex)
            {
                if (ex.Response != null) ex.Response.Dispose();
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                if (_logger != null) _logger.W("[WARN] 连接探测失败 / health probe failed: " + ex.Status + " " + ex.Message);
                return false;
            }
            catch { return false; }
        }

        private bool PostJson(string url, object payload, CancellationToken ct, out LanReportResponse result)
        {
            result = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                bool retryable;
                if (PostJsonOnce(url, payload, ct, out result, out retryable)) return true;
                if (!retryable || attempt == 2) return false;
                if (_logger != null) _logger.W("[WARN] 上报暂时失败，重试 / report retry " + (attempt + 1) + ": " + result.Error);
                if (ct.WaitHandle.WaitOne(500 * (attempt + 1))) ct.ThrowIfCancellationRequested();
            }
            return false;
        }

        private bool PostJsonOnce(string url, object payload, CancellationToken ct, out LanReportResponse result, out bool retryable)
        {
            result = null;
            retryable = false;
            try
            {
                var json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024, RecursionLimit = 256 };
                byte[] body = Encoding.UTF8.GetBytes(json.Serialize(payload));
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.Proxy = null;
                req.KeepAlive = false;
                req.ContentType = "application/json; charset=utf-8";
                // Matching a full hardware inventory against a large repository is a server-side
                // compute request, not a lightweight connectivity probe. Keep it cancellable but
                // allow enough time for older CPUs and 200+ devices.
                int reportTimeout = Math.Max(_config.TimeoutMs, 2 * 60 * 1000);
                req.Timeout = reportTimeout;
                req.ReadWriteTimeout = reportTimeout;
                req.Headers[LanProtocol.HeaderProtocol] = LanProtocol.Version.ToString(CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(_config.Token)) req.Headers[LanProtocol.HeaderToken] = _config.Token;
                using (ct.Register(() => req.Abort()))
                {
                    using (var rs = req.GetRequestStream()) rs.Write(body, 0, body.Length);
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    {
                        string text = ReadResponse(resp);
                        result = json.Deserialize<LanReportResponse>(text);
                        return resp.StatusCode == HttpStatusCode.OK && result != null;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (WebException wex)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                var errorResponse = wex.Response as HttpWebResponse;
                int status = errorResponse == null ? 0 : (int)errorResponse.StatusCode;
                retryable = status == 408 || status == 429 || status == 502 || status == 503 || status == 504 ||
                    (status == 0 && (wex.Status == WebExceptionStatus.ConnectFailure || wex.Status == WebExceptionStatus.ConnectionClosed || wex.Status == WebExceptionStatus.ReceiveFailure || wex.Status == WebExceptionStatus.SendFailure || wex.Status == WebExceptionStatus.KeepAliveFailure));
                try { result = ReadErrorResponse(wex); }
                finally { if (wex.Response != null) wex.Response.Dispose(); }
                if (result == null)
                    result = new LanReportResponse { Ok = false, Error = wex.Status == WebExceptionStatus.Timeout
                        ? "服务器匹配驱动超时（已等待两分钟）"
                        : (wex.Status + ": " + wex.Message) };
                if (_logger != null) _logger.W("[WARN] 上报 HTTP 失败 / report HTTP failure: status=" + status + " transport=" + wex.Status + " " + result.Error);
                return false;
            }
            catch (Exception ex)
            {
                result = new LanReportResponse { Ok = false, Error = ex.GetType().Name + ": " + ex.Message };
                if (_logger != null) _logger.W("[ERROR] 硬件清单上报失败 / report failed: " + ex);
                return false;
            }
        }

        private bool Download(string url, CancellationToken ct, out byte[] bytes)
        {
            using (var output = new MemoryStream())
            {
                bool ok = DownloadStream(url, ct, () => { output.SetLength(0); return output; }, false, null);
                bytes = ok ? output.ToArray() : null;
                return ok;
            }
        }

        private bool DownloadToFile(string url, string path, CancellationToken ct, Action<long, long, double> progress)
        {
            try { return DownloadStream(url, ct, () => new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None), true, progress); }
            catch { try { File.Delete(path); } catch { } throw; }
        }

        private bool DownloadStream(string url, CancellationToken ct, Func<Stream> createOutput, bool disposeOutput, Action<long, long, double> progress)
        {
            int attempts = Math.Max(1, Math.Min(3, _config.MaxRetries));
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method = "GET";
                    // Desktop .NET defaults to two connections per host, which would otherwise
                    // serialize most parallel package workers despite available client CPU cores.
                    req.ServicePoint.ConnectionLimit = Math.Max(req.ServicePoint.ConnectionLimit, _scanDownloadConnections);
                    int totalTimeout = Math.Max(_config.TimeoutMs, _config.DownloadTimeoutMs);
                    req.Timeout = totalTimeout;
                    req.ReadWriteTimeout = 60000;
                    req.Proxy = null;
                    req.Headers[LanProtocol.HeaderProtocol] = LanProtocol.Version.ToString(CultureInfo.InvariantCulture);
                    if (!string.IsNullOrEmpty(_config.Token)) req.Headers[LanProtocol.HeaderToken] = _config.Token;
                    using (var deadline = new System.Threading.Timer(delegate(object ignored) { try { req.Abort(); } catch { } }, null, totalTimeout, Timeout.Infinite))
                    using (ct.Register(() => req.Abort()))
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    {
                        if (resp.StatusCode != HttpStatusCode.OK) return false;
                        Stream output = createOutput();
                        try
                        {
                            long received = 0, intervalBytes = 0; var clock = System.Diagnostics.Stopwatch.StartNew();
                            byte[] buffer = new byte[1024 * 1024];
                            using (var s = resp.GetResponseStream())
                            {
                                int count;
                                while ((count = s.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    ct.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); received += count; intervalBytes += count;
                                    if (progress != null && clock.ElapsedMilliseconds >= 250)
                                    { long elapsed = Math.Max(1, clock.ElapsedMilliseconds); progress(received, resp.ContentLength, intervalBytes * 1000.0 / elapsed); intervalBytes = 0; clock.Restart(); }
                                }
                            }
                            if (resp.ContentLength >= 0 && received != resp.ContentLength) throw new IOException("驱动下载不完整 / truncated driver download");
                            if (progress != null) progress(received, resp.ContentLength, intervalBytes * 1000.0 / Math.Max(1, clock.ElapsedMilliseconds));
                        }
                        finally { if (disposeOutput) output.Dispose(); }
                        return true;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (WebException ex)
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException();
                    if (_logger != null) _logger.W("[WARN] 下载尝试失败 / Download attempt " + attempt + "/" + attempts + ": " + ex.Status + " · " + ex.Message);
                    if (ex.Status == WebExceptionStatus.ProtocolError)
                    {
                        var response = ex.Response as HttpWebResponse;
                        int status = response == null ? 0 : (int)response.StatusCode;
                        if (response != null) response.Dispose();
                        if (status != 408 && status != 429 && status != 500 && status != 502 && status != 503 && status != 504) return false;
                    }
                }
                catch (Exception ex) { if (_logger != null) _logger.W("[WARN] 下载尝试失败 / Download attempt " + attempt + "/" + attempts + ": " + ex.Message); }
                if (attempt < attempts) ct.WaitHandle.WaitOne(Math.Max(250, Math.Min(2000, _config.BackoffInitialMs * (1 << (attempt - 1)))));
            }
            return false;
        }

        private static string ReadResponse(HttpWebResponse resp)
        {
            using (var s = resp.GetResponseStream())
            using (var sr = new StreamReader(s, Encoding.UTF8))
                return sr.ReadToEnd();
        }

        private static LanReportResponse ReadErrorResponse(WebException wex)
        {
            try
            {
                var resp = wex.Response as HttpWebResponse;
                if (resp == null) return null;
                using (var s = resp.GetResponseStream())
                using (var sr = new StreamReader(s, Encoding.UTF8))
                    return new JavaScriptSerializer().Deserialize<LanReportResponse>(sr.ReadToEnd());
            }
            catch { return null; }
        }

        private static void ExtractZip(byte[] zipBytes, string destDir, CancellationToken ct)
        {
            using (var stream = new MemoryStream(zipBytes)) ExtractZipStream(stream, destDir, ct);
        }

        private static void ExtractZipFile(string zipPath, string destDir, CancellationToken ct)
        {
            try { using (var stream = File.OpenRead(zipPath)) ExtractZipStream(stream, destDir, ct); }
            finally { try { File.Delete(zipPath); } catch { } }
        }

        private const string ExtractionMap = ".uda-extraction.json";
        private static string ArchiveMemberName(string name)
        {
            name = (name ?? "").Replace('\\', '/');
            if (name.StartsWith("/") || name.Contains(":") || name.Split('/').Any(p => p == "." || p == "..")) throw new InvalidDataException("Unsafe ZIP member: " + name);
            return name;
        }
        private static void ExtractZipStream(Stream zipStream, string destDir, CancellationToken ct)
        {
            string root = Path.GetFullPath(destDir).TrimEnd('\\') + "\\";
            // Inspect the complete archive before writing. Removing only a common ancestor keeps
            // every INF-relative reference intact while dropping server repository scaffolding.
            using (var za = new ZipArchive(zipStream, ZipArchiveMode.Read, true))
            {
                var files = za.Entries.Where(e => !e.FullName.EndsWith("/") && !e.FullName.EndsWith("\\")).ToList();
                foreach (var e in za.Entries) ArchiveMemberName(e.FullName);
                string prefix = files.Count == 0 ? "" : ArchiveMemberName(files[0].FullName);
                int slash = prefix.LastIndexOf('/'); prefix = slash < 0 ? "" : prefix.Substring(0, slash + 1);
                foreach (var entry in files)
                    while (prefix.Length > 0 && !ArchiveMemberName(entry.FullName).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    { prefix = prefix.TrimEnd('/'); int last = prefix.LastIndexOf('/'); prefix = last < 0 ? "" : prefix.Substring(0, last + 1); }
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in files)
                {
                    string name = ArchiveMemberName(entry.FullName).Substring(prefix.Length);
                    string dest = Path.GetFullPath(Path.Combine(root, name.Replace('/', '\\')));
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !names.Add(dest) || string.Equals(name, ExtractionMap, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Conflicting or unsafe ZIP member: " + name);
                    if (dest.Length >= 260 || Path.GetDirectoryName(dest).Length >= 248) throw new PathTooLongException("驱动包解压路径过长（" + dest.Length + " 字符）。已移除公共仓库前缀，请缩短客户端缓存目录或原包内部层级：" + name);
                }
                if (Directory.Exists(destDir)) Directory.Delete(destDir, true);
                Directory.CreateDirectory(destDir);
                foreach (var entry in files)
                {
                    ct.ThrowIfCancellationRequested();
                    string name = ArchiveMemberName(entry.FullName).Substring(prefix.Length);
                    string dest = Path.Combine(root, name.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    using (var es = entry.Open()) using (var fs = File.Create(dest)) es.CopyTo(fs);
                }
                File.WriteAllText(Path.Combine(root, ExtractionMap), new JavaScriptSerializer().Serialize(new { Prefix = prefix }), new UTF8Encoding(false));
            }
        }

        private static string LocateInf(string dir, string infRelativePath, string packageInfPath = null)
        {
            if (!string.IsNullOrEmpty(packageInfPath))
            {
                string mappedPath = packageInfPath.Replace('\\', '/');
                string mapPath = Path.Combine(dir, ExtractionMap);
                if (File.Exists(mapPath))
                {
                    var map = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(File.ReadAllText(mapPath));
                    string prefix;
                    if (map != null && map.TryGetValue("Prefix", out prefix) && !string.IsNullOrEmpty(prefix))
                    {
                        if (!mappedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
                        mappedPath = mappedPath.Substring(prefix.Length);
                    }
                }
                string exact = LanSecurity.SafeAbsolute(dir, mappedPath);
                return exact != null && File.Exists(exact) ? exact : null;
            }
            string basename = Path.GetFileName(infRelativePath ?? "");
            string rootFile = LanSecurity.SafeAbsolute(dir, basename);
            if (rootFile != null && File.Exists(rootFile)) return rootFile;
            var matches = Directory.EnumerateFiles(dir, "*.inf", SearchOption.AllDirectories).Where(f => string.Equals(Path.GetFileName(f), basename, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            return matches.Count == 1 ? matches[0] : null;
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
    //  Windows Service host (--service mode)
    // =================================================================================

    /// <summary>Runs LanDriverServer as a Windows Service (ServiceBase). Started by Install-LanService.ps1
    /// via sc.exe with the --service flag; OnStart/OnStop drive the HTTP listener lifecycle.</summary>
    public sealed class UdaLanService : ServiceBase
    {
        private readonly LanServerConfig _config;
        private readonly string _logDir;
        private LanDriverServer _server;
        private L _logger;

        public UdaLanService(LanServerConfig config, string logDir) { _config = config; _logDir = logDir; ServiceName = "UdaLanDriverService"; }

        protected override void OnStart(string[] args)
        {
            // Every failure here is recorded with its cause: the SCM marks the start as failed and the
            // configured sc.exe failure recovery restarts us — but only a persisted log explains why.
            ServiceEventLog.Write("event", "服务 OnStart / service starting (port=" + _config.Port + ", repo=" + (_config.RepoRoot ?? "") + ")");
            try
            {
                if (!ServerStorage.Config(_config.RepoRoot).DatabaseOnly) throw new InvalidOperationException("此版本服务要求先将原数据迁移至 MySQL，并通过新版安装程序完成升级");
                _logger = new L(_logDir);
                _server = new LanDriverServer(_config, _logger);
                // Start() only brings up the HTTP listener (fast); repo loading continues in a
                // background job so OnStart always returns well inside the SCM start timeout.
                _server.Start();
                ServiceEventLog.Write("event", "服务 OnStart 完成：监听已就绪，仓库后台加载中 / service started, listening on port " + _config.Port + "; repo loading in background");
            }
            catch (Exception ex)
            {
                ServiceEventLog.Write("fatal", "服务启动失败 / service OnStart FAILED: " + ex);
                throw;   // let the SCM see the failure; do not pretend to be healthy
            }
        }

        protected override void OnStop()
        {
            ServiceEventLog.Write("event", "服务 OnStop / service stop requested");
            try
            {
                if (_server != null) { _server.Stop(); _server.Dispose(); }
                ServiceEventLog.Write("event", "服务 OnStop 完成 / service stopped cleanly");
            }
            catch (Exception ex)
            {
                ServiceEventLog.Write("error", "服务 OnStop 异常 / service OnStop error (resources still released as far as possible): " + ex);
            }
            finally
            {
                if (_logger != null) { try { _logger.Dispose(); } catch { } }
                _server = null; _logger = null;
            }
        }
    }

    // =================================================================================
    //  CLI entry point (LanDriverService.exe)
    // =================================================================================

    public static class LanServerProgram
    {
        public static string SafeArguments(string[] args)
        {
            var result = new List<string>(); bool secret = false;
            foreach (string value in args ?? new string[0])
            {
                if (secret) { result.Add("[REDACTED]"); secret = false; continue; }
                string lower = (value ?? "").ToLowerInvariant();
                if (lower.StartsWith("--token=") || lower.StartsWith("--password=") || lower.StartsWith("--api-key=")) { result.Add(value.Substring(0, value.IndexOf('=') + 1) + "[REDACTED]"); continue; }
                result.Add(value); secret = lower == "--token" || lower == "--password" || lower == "--api-key";
            }
            return string.Join(" ", result);
        }
        [STAThread]
        public static int Main(string[] args)
        {
            // Process-wide last-resort diagnostics, registered before anything else can fail: startup,
            // shutdown, crash and unobserved-task causes all land in the persistent ProgramData log.
            ServiceEventLog.InstallGlobalHandlers();
            ServiceEventLog.Initialize(Environment.ExpandEnvironmentVariables("%ProgramData%\\UniversalDriverAssistant\\Logs"));
            ServiceEventLog.Write("event", "进程启动 / process started (version " + LanProtocol.ServerVersion + "): " + SafeArguments(args));
            try
            {
                if (args.Length == 3 && string.Equals(args[0], "--create-admin", StringComparison.OrdinalIgnoreCase))
                {
                    if (Console.IsInputRedirected) { Console.Error.WriteLine("Interactive console required."); return 2; }
                    Console.Write("New password (minimum 12 characters): ");
                    string first = ReadSecret();
                    Console.Write("Confirm password: ");
                    string second = ReadSecret();
                    if (!string.Equals(first, second, StringComparison.Ordinal)) { Console.Error.WriteLine("Passwords do not match."); return 2; }
                    AdminCredentialStore.Create(args[2], args[1], first);
                    Console.WriteLine("Admin credential file created: " + Path.GetFullPath(args[2]));
                    return 0;
                }
                var cfg = new LanServerConfig();
                string logDir = Environment.ExpandEnvironmentVariables("%ProgramData%\\UniversalDriverAssistant\\Logs");
                bool serviceMode = false;
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i].ToLowerInvariant();
                    if ((a == "--port" || a == "-p") && i + 1 < args.Length) { int p; if (int.TryParse(args[++i], out p)) cfg.Port = p; }
                    else if (a == "--repo" && i + 1 < args.Length) cfg.RepoRoot = args[++i];
                    else if (a == "--source" && i + 1 < args.Length) cfg.Sources.Add(args[++i]);
                    else if (a == "--import-allow" && i + 1 < args.Length) cfg.ImportAllowRoots.Add(args[++i]);
                    else if (a == "--token" && i + 1 < args.Length) cfg.Token = args[++i];
                    else if (a == "--admin-credentials" && i + 1 < args.Length) cfg.AdminCredentialsFile = args[++i];
                    else if (a == "--client-template" && i + 1 < args.Length) cfg.ClientTemplateDir = args[++i];
                    else if (a == "--admin-session-minutes" && i + 1 < args.Length) { int m; if (int.TryParse(args[++i], out m)) cfg.AdminSessionMinutes = Math.Max(5, Math.Min(1440, m)); }
                    else if (a == "--bind" && i + 1 < args.Length) cfg.Host = args[++i];
                    else if (a == "--advertise" && i + 1 < args.Length) cfg.AdvertiseUrl = args[++i].TrimEnd('/');
                    else if (a == "--os" && i + 1 < args.Length) cfg.OsArchitecture = args[++i];
                    else if (a == "--osver" && i + 1 < args.Length) { int v; if (int.TryParse(args[++i], out v)) cfg.OsMajorVersion = v; }
                    else if (a == "--logdir" && i + 1 < args.Length) logDir = args[++i];
                    else if (a == "--service") serviceMode = true;
                    else if (a == "--help" || a == "-h")
                    {
                        Console.WriteLine("LanDriverService --port <port> --repo <repoDir> [--source <dir>...] [--import-allow <dir>...]");
                        Console.WriteLine("  [--token <client-token>] [--admin-credentials <file>] [--client-template <dir>] [--admin-session-minutes 30]");
                        Console.WriteLine("  Create admin credentials: LanDriverService --create-admin <user> <file>");
                        Console.WriteLine("  [--bind localhost] [--os amd64 --osver 10] [--logdir <dir>] [--service]");
                        return 0;
                    }
                }
                if (cfg.Port <= 0) { Console.Error.WriteLine("--port is required"); return 2; }
                if (serviceMode)
                {
                    // Run as a Windows Service. The SCM calls OnStart/OnStop; no console I/O.
                    // Event log follows the same --logdir as the rotating application log.
                    ServiceEventLog.Initialize(logDir);
                    ServiceBase.Run(new ServiceBase[] { new UdaLanService(cfg, logDir) });
                    return 0;
                }
                using (var log = new L(logDir))
                using (var server = new LanDriverServer(cfg, log))
                {
                    server.Start();
                    Console.WriteLine("LanDriverService listening on http://" + cfg.Host + ":" + cfg.Port + "/api/health (Ctrl+C to stop)");
                    Console.WriteLine("Repo: " + (cfg.RepoRoot ?? "(none)") + "  Token: " + (string.IsNullOrEmpty(cfg.Token) ? "off" : "on"));
                    using (var done = new ManualResetEventSlim(false))
                    {
                        Console.CancelKeyPress += (s, e) => { e.Cancel = true; done.Set(); };
                        done.Wait();
                    }
                    return 0;
                }
            }
            catch (Exception ex)
            {
                ServiceEventLog.Write("fatal", "进程致命错误 / process fatal: " + ex);
                Console.Error.WriteLine("LanDriverService ERROR: " + ex);
                return 2;
            }
        }

        private static string ReadSecret()
        {
            var value = new StringBuilder();
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return value.ToString(); }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (value.Length > 0) value.Length--;
                    continue;
                }
                if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
            }
        }
    }
}

