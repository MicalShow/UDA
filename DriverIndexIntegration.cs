// DriverIndexIntegration.cs - Phase 2: wires the Phase 1 driver index core into the engine.
//
// This file is the integration layer between DriverIndexCore.cs (Phase 1, offline index) and the
// existing UniversalDriverAssistant engine (E). It provides:
//   * A testable PnP hardware collector abstraction (IHardwareCollector) with WMI / machine-adaptor /
//     static implementations, collecting HardwareID/CompatibleID/ClassGuid/DeviceInstanceId with
//     deduplication and graceful exception degradation.
//   * A shared, cancellable process runner (ProcessRunner) used by both the legacy E.P path and the
//     new pnputil install runner, so behaviour stays identical.
//   * DriverIndexService: opens / incrementally builds the persistent *.udadb index from the resolved
//     driver roots, reporting progress and honouring a CancellationToken. No per-package process spawn.
//   * Install seams: IAdminChecker / ISignatureVerifier (WinVerifyTrust) / IPnpInstallRunner, each with
//     a real default and a fake for offline tests, plus InstallItemResult for per-item recording.
//
// Compiled by Build.ps1 alongside the real sources. No third-party dependencies. C# 5.0 syntax.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace UDA
{
    // Scan-local file work is shared, but every device still gets its own live Windows rank probe.
    public sealed class DriverScanFiles
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<string>> _hashes = new System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<InfRecord>> _records = new System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<InfRecord>>(StringComparer.OrdinalIgnoreCase);
        private int _hashReads, _parseReads;
        public int HashReads { get { return _hashReads; } }
        public int ParseReads { get { return _parseReads; } }
        private static string Identity(string path)
        {
            var file = new FileInfo(Path.GetFullPath(path));
            return file.FullName + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks + "|" + file.CreationTimeUtc.Ticks;
        }
        public string Hash(string path)
        {
            string key = Identity(path);
            return _hashes.GetOrAdd(key, ignored => new Lazy<string>(() => {
                Interlocked.Increment(ref _hashReads);
                using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }
        public InfRecord Parse(string path, string source)
        {
            string key = Identity(path) + "|" + source;
            return _records.GetOrAdd(key, ignored => new Lazy<InfRecord>(() => {
                Interlocked.Increment(ref _parseReads); return InfParser.Parse(path, source);
            }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }
    }
    /// <summary>Read the device's configured driver binding directly; never infer it from package history.</summary>
    public static class CurrentDriverState
    {
        public static uint? ReadRank(string instanceId)
        {
            try
            {
                uint node, type, size = 4;
                if (CM_Locate_DevNodeW(out node, instanceId, 0) != 0) return null;
                var key = new PropertyKey { Format = new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), Id = 14 };
                var bytes = new byte[4];
                return CM_Get_DevNode_PropertyW(node, ref key, out type, bytes, ref size, 0) == 0 && type == 7 && size == 4 ? (uint?)BitConverter.ToUInt32(bytes, 0) : null;
            }
            catch { return null; }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey { public Guid Format; public uint Id; }
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint CM_Locate_DevNodeW(out uint node, string instanceId, uint flags);
        [DllImport("cfgmgr32.dll", ExactSpelling = true)]
        private static extern uint CM_Get_DevNode_PropertyW(uint node, ref PropertyKey key, out uint type, byte[] buffer, ref uint size, uint flags);
        [DllImport("cfgmgr32.dll", ExactSpelling = true)]
        private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);
        private static string Read(uint node, uint id)
        {
            var key = new PropertyKey { Format = new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), Id = id };
            uint type, size = 0;
            uint code = CM_Get_DevNode_PropertyW(node, ref key, out type, null, ref size, 0);
            if ((code != 0 && code != 0x1A) || size == 0 || size > 65536) return null;
            var bytes = new byte[size];
            if (CM_Get_DevNode_PropertyW(node, ref key, out type, bytes, ref size, 0) != 0 || type != 0x12) return null;
            return Encoding.Unicode.GetString(bytes, 0, (int)size).TrimEnd('\0');
        }
        public static bool RefreshDevice(D device, L logger)
        {
            if (device == null || string.IsNullOrWhiteSpace(device.Id)) return false;
            try
            {
                uint node;
                if (CM_Locate_DevNodeW(out node, device.Id, 0) != 0) return false;
                string inf = Read(node, 5), version = Read(node, 3);
                if (string.IsNullOrWhiteSpace(inf) || string.IsNullOrWhiteSpace(version)) return false;
                ApplyBinding(device, inf, version, logger);
                uint status, problem;
                if (CM_Get_DevNode_Status(out status, out problem, node, 0) == 0) device.Error = (int)problem;
                return true;
            }
            catch (Exception ex) { if (logger != null) logger.W("[WARN] 原生驱动状态读取失败，保留 WMI / Native binding unavailable: " + device.Id + " " + ex.Message); return false; }
        }
        public static bool ApplyBinding(D device, string inf, string version, L logger)
        {
            if (device == null || string.IsNullOrWhiteSpace(inf) || string.IsNullOrWhiteSpace(version)) return false;
            if (logger != null && (!string.Equals(device.Inf, inf, StringComparison.OrdinalIgnoreCase) || device.Version != version))
                logger.W("[INFO] 校正设备驱动状态 / Native binding: " + device.Id + " WMI=" + device.Inf + "/" + device.Version + " native=" + inf + "/" + version);
            device.Inf = inf; device.Version = version;
            return true;
        }
        public static void Refresh(IEnumerable<D> devices, L logger)
        {
            if (devices != null) ClientScanParallelism.Run(devices.ToList(), ClientScanParallelism.LogicalProcessors(), CancellationToken.None, (device, token) => RefreshDevice(device, logger));
        }
    }
    // =================================================================================
    //  PnP hardware collection (testable abstraction)
    // =================================================================================

    /// <summary>
    /// Collects the target machine's PnP devices as <see cref="D"/> records carrying HardwareID (H),
    /// CompatibleID (K), ClassGuid, DeviceInstanceId (Id), class, manufacturer, status, error and the
    /// currently-installed driver INF/version. Implementations must deduplicate by DeviceInstanceId and
    /// degrade gracefully on WMI errors (return whatever was collected, never throw on a bad query).
    /// </summary>
    public static class WindowsDriverPreference
    {
        // Test-only replacement of the read-only Windows ranking probe; production leaves this null.
        internal static Func<D, string, bool?> TestPreferenceProbe;
        [StructLayout(LayoutKind.Sequential)] private struct Device { public uint Size; public Guid Class; public uint Node; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Parameters
        {
            public uint Size, Flags, FlagsEx; public IntPtr Window, Callback, Context, Queue, ClassReserved; public uint Reserved;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Path;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Driver
        {
            public uint Size, Type; public IntPtr Reserved;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Manufacturer;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Provider;
            public System.Runtime.InteropServices.ComTypes.FILETIME Date; public ulong Version;
        }
        [StructLayout(LayoutKind.Sequential)] private struct DriverParameters { public uint Size, Rank, Flags; public IntPtr Private; public uint Reserved; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DriverDetail
        {
            public uint Size; public System.Runtime.InteropServices.ComTypes.FILETIME InfDate;
            public uint CompatIdsOffset, CompatIdsLength; public IntPtr Reserved;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string SectionName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string InfFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1)] public string HardwareId;
        }
        [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr guid, IntPtr parent);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiOpenDeviceInfoW(IntPtr set, string id, IntPtr parent, uint flags, ref Device device);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInstallParamsW(IntPtr set, ref Device device, ref Parameters parameters);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiSetDeviceInstallParamsW(IntPtr set, ref Device device, ref Parameters parameters);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiBuildDriverInfoList(IntPtr set, ref Device device, uint type);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiEnumDriverInfoW(IntPtr set, ref Device device, uint type, uint index, ref Driver driver);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDriverInstallParamsW(IntPtr set, ref Device device, ref Driver driver, ref DriverParameters parameters);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDriverInfoDetailW(IntPtr set, ref Device device, ref Driver driver, IntPtr detail, uint detailSize, out uint requiredSize);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        private static int CompareRanked(Tuple<uint, DateTime, string, string> a, Tuple<uint, DateTime, string, string> b)
        {
            if (a.Item1 != b.Item1) return a.Item1 < b.Item1 ? -1 : 1;
            if (a.Item2.Date != b.Item2.Date) return a.Item2.Date > b.Item2.Date ? -1 : 1;
            if (DriverVersion.IsNewer(a.Item3, b.Item3)) return -1;
            if (DriverVersion.IsNewer(b.Item3, a.Item3)) return 1;
            return 0;
        }

        internal static bool IsUniqueBestCandidate(IEnumerable<Tuple<uint, DateTime, string, string>> drivers, string candidateHash)
        {
            var list = (drivers ?? Enumerable.Empty<Tuple<uint, DateTime, string, string>>()).ToList();
            if (list.Count == 0 || string.IsNullOrEmpty(candidateHash)) return false;
            var candidate = list.Where(x => string.Equals(x.Item4, candidateHash, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Item1).ThenByDescending(x => x.Item2).FirstOrDefault();
            if (candidate == null) return false;
            foreach (var other in list)
            {
                if (string.Equals(other.Item4, candidateHash, StringComparison.OrdinalIgnoreCase)) continue;
                if (CompareRanked(other, candidate) <= 0) return false; // another package wins or ties; Windows may retain it
            }
            return true;
        }

        private static string HashFile(string path, DriverScanFiles files)
        {
            try { if (files != null) return files.Hash(path); using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""); }
            catch { return ""; }
        }

        private static string ReadDriverInfPath(IntPtr set, ref Device device, ref Driver driver)
        {
            uint required;
            SetupDiGetDriverInfoDetailW(set, ref device, ref driver, IntPtr.Zero, 0, out required);
            int fixedSize = Marshal.SizeOf(typeof(DriverDetail));
            if (required < fixedSize || required > 1024 * 1024) return null;
            IntPtr buffer = Marshal.AllocHGlobal((int)required);
            try
            {
                Marshal.WriteInt32(buffer, fixedSize);
                if (!SetupDiGetDriverInfoDetailW(set, ref device, ref driver, buffer, required, out required)) return null;
                var detail = (DriverDetail)Marshal.PtrToStructure(buffer, typeof(DriverDetail));
                return detail.InfFileName;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static bool? IsBetterConsideringDriverStore(D device, string candidateInf, uint? currentRank, InfRecord current, DriverScanFiles files, out string trace)
        {
            trace = "";
            string windowsInf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF") + Path.DirectorySeparatorChar;
            if (string.IsNullOrEmpty(candidateInf) || !Path.GetFullPath(candidateInf).StartsWith(windowsInf, StringComparison.OrdinalIgnoreCase))
            { trace = "candidate is not an imported Windows INF; use single-INF fallback"; return null; }
            string candidateHash = HashFile(candidateInf, files);
            if (candidateHash.Length == 0) { trace = "cannot hash imported candidate INF; use single-INF fallback"; return null; }
            IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
            if (set == new IntPtr(-1)) { trace = "SetupDiCreateDeviceInfoList failed, Win32=" + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture); return null; }
            try
            {
                var data = new Device { Size = (uint)Marshal.SizeOf(typeof(Device)) };
                if (!SetupDiOpenDeviceInfoW(set, device.Id, IntPtr.Zero, 0, ref data))
                { trace = "SetupDiOpenDeviceInfo failed, Win32=" + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture); return null; }
                var options = new Parameters { Size = (uint)Marshal.SizeOf(typeof(Parameters)) };
                if (!SetupDiGetDeviceInstallParamsW(set, ref data, ref options))
                { trace = "SetupDiGetDeviceInstallParams failed, Win32=" + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture); return null; }
                options.FlagsEx |= 0x00000800; // DI_FLAGSEX_ALLOWEXCLUDEDDRVS
                options.Path = "";             // all staged compatible packages, not only candidate INF
                if (!SetupDiSetDeviceInstallParamsW(set, ref data, ref options))
                { trace = "SetupDiSetDeviceInstallParams failed, Win32=" + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture); return null; }
                if (!SetupDiBuildDriverInfoList(set, ref data, 2))
                { trace = "SetupDiBuildDriverInfoList failed, Win32=" + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture); return null; }
                var ranked = new List<Tuple<uint, DateTime, string, string>>();
                bool foundCandidate = false;
                for (uint i = 0; i < 4096; i++)
                {
                    var driver = new Driver { Size = (uint)Marshal.SizeOf(typeof(Driver)) };
                    if (!SetupDiEnumDriverInfoW(set, ref data, 2, i, ref driver)) break;
                    var parameters = new DriverParameters { Size = (uint)Marshal.SizeOf(typeof(DriverParameters)) };
                    if (!SetupDiGetDriverInstallParamsW(set, ref data, ref driver, ref parameters))
                    { trace = "SetupDiGetDriverInstallParams failed at index " + i.ToString(CultureInfo.InvariantCulture) + ", Win32=" + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture); return null; }
                    string infPath = ReadDriverInfPath(set, ref data, ref driver);
                    if (string.IsNullOrEmpty(infPath)) continue;
                    string hash = HashFile(infPath, files);
                    if (string.Equals(hash, candidateHash, StringComparison.OrdinalIgnoreCase)) foundCandidate = true;
                    long ticks = ((long)driver.Date.dwHighDateTime << 32) | (uint)driver.Date.dwLowDateTime;
                    string version = (driver.Version >> 48) + "." + ((driver.Version >> 32) & 65535) + "." + ((driver.Version >> 16) & 65535) + "." + (driver.Version & 65535);
                    ranked.Add(Tuple.Create(parameters.Rank, DateTime.FromFileTimeUtc(ticks), version, hash));
                }
                // If staged but absent from this device's complete compatible list, Windows cannot select it.
                if (!foundCandidate)
                { trace = "candidate package is absent from the device's complete compatible Driver Store list"; return false; }
                if (!IsUniqueBestCandidate(ranked, candidateHash))
                {
                    var top = ranked.OrderBy(x => x.Item1).ThenByDescending(x => x.Item2).ThenByDescending(x => MatchRanker.VersionScore(x.Item3)).FirstOrDefault();
                    trace = "candidate is not the unique best Driver Store package; top rank=" + (top != null ? top.Item1.ToString(CultureInfo.InvariantCulture) : "unknown") +
                        " date=" + (top != null ? top.Item2.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "unknown") +
                        " version=" + (top != null ? top.Item3 : "unknown") + " hash=" + (top != null ? top.Item4.Substring(0, Math.Min(12, top.Item4.Length)) : "unknown");
                    return false;
                }
                var candidate = ranked.Where(x => string.Equals(x.Item4, candidateHash, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.Item1).ThenByDescending(x => x.Item2).FirstOrDefault();
                if (candidate == null) { trace = "candidate is absent from ranked driver entries"; return false; }
                string currentInf = Path.Combine(windowsInf, Path.GetFileName(device.Inf));
                string currentHash = HashFile(currentInf, files);
                var bound = ranked.FirstOrDefault(x => !string.IsNullOrEmpty(currentHash) && string.Equals(x.Item4, currentHash, StringComparison.OrdinalIgnoreCase));
                if (bound == null && !currentRank.HasValue)
                { trace = "candidate is unique best, but current driver rank could not be read"; return null; }
                bool better = bound != null
                    ? IsBetter(candidate.Item1, candidate.Item2, candidate.Item3, bound.Item1, bound.Item2, bound.Item3)
                    : IsBetter(candidate.Item1, candidate.Item2, candidate.Item3, currentRank.Value, current.Date(), current.DriverVerVersion);
                trace = "candidate unique-best rank=" + candidate.Item1.ToString(CultureInfo.InvariantCulture) + " date=" + candidate.Item2.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                    " version=" + candidate.Item3 + "; bound=" + (bound != null ? ("rank=" + bound.Item1.ToString(CultureInfo.InvariantCulture) + " date=" + bound.Item2.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " version=" + bound.Item3) : "current DEVPROP rank/version") +
                    "; better=" + better.ToString();
                return better;
            }
            catch (Exception ex) { trace = "full Driver Store probe exception: " + ex.GetType().Name + ": " + ex.Message; return null; }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        public static bool IsBetter(uint candidateRank, DateTime candidateDate, string candidateVersion, uint currentRank, DateTime currentDate, string currentVersion)
        {
            if (candidateRank != currentRank) return candidateRank < currentRank;
            if (candidateDate.Date != currentDate.Date) return candidateDate.Date > currentDate.Date;
            return DriverVersion.IsNewer(candidateVersion, currentVersion);
        }

        // Builds a private, read-only compatibility list. No package is imported and no device is changed.
        public static bool? IsBetterForDevice(D device, string candidateInf)
        {
            return IsBetterForDevice(device, candidateInf, null);
        }

        public static bool? IsBetterForDevice(D device, string candidateInf, Action<string> trace, DriverScanFiles files = null)
        {
            if (TestPreferenceProbe != null) return TestPreferenceProbe(device, candidateInf);
            if (device == null || device.Bad || string.IsNullOrEmpty(device.Inf) || !File.Exists(candidateInf)) return null;
            uint? currentRank = CurrentDriverState.ReadRank(device.Id);
            string currentInf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", Path.GetFileName(device.Inf));
            if (!File.Exists(currentInf)) return null;
            InfRecord current;
            try { current = files == null ? InfParser.Parse(currentInf, "current") : files.Parse(currentInf, "current"); }
            catch { return null; }
            string storeTrace;
            bool? fullStoreVerdict = IsBetterConsideringDriverStore(device, candidateInf, currentRank, current, files, out storeTrace);
            if (fullStoreVerdict.HasValue)
            {
                if (trace != null) trace("full Driver Store rank probe: " + storeTrace + " => " + fullStoreVerdict.Value.ToString());
                return fullStoreVerdict;
            }
            if (trace != null && !string.IsNullOrEmpty(storeTrace)) trace("full Driver Store probe unavailable: " + storeTrace + "; using single-INF fallback");
            if (!currentRank.HasValue) return null;
            IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
            if (set == new IntPtr(-1)) return null;
            try
            {
                var data = new Device { Size = (uint)Marshal.SizeOf(typeof(Device)) };
                if (!SetupDiOpenDeviceInfoW(set, device.Id, IntPtr.Zero, 0, ref data)) return null;
                var options = new Parameters { Size = (uint)Marshal.SizeOf(typeof(Parameters)) };
                if (!SetupDiGetDeviceInstallParamsW(set, ref data, ref options)) return null;
                options.Flags |= 0x00010000; // DI_ENUMSINGLEINF
                options.FlagsEx |= 0x00000800; // DI_FLAGSEX_ALLOWEXCLUDEDDRVS
                options.Path = Path.GetFullPath(candidateInf);
                if (options.Path.Length >= 260 || !SetupDiSetDeviceInstallParamsW(set, ref data, ref options) || !SetupDiBuildDriverInfoList(set, ref data, 2)) return null;
                bool found = false;
                for (uint i = 0; ; i++)
                {
                    var driver = new Driver { Size = (uint)Marshal.SizeOf(typeof(Driver)) };
                    if (!SetupDiEnumDriverInfoW(set, ref data, 2, i, ref driver)) break;
                    var parameters = new DriverParameters { Size = (uint)Marshal.SizeOf(typeof(DriverParameters)) };
                    if (!SetupDiGetDriverInstallParamsW(set, ref data, ref driver, ref parameters)) return null;
                    found = true;
                    long ticks = ((long)driver.Date.dwHighDateTime << 32) | (uint)driver.Date.dwLowDateTime;
                    string version = (driver.Version >> 48) + "." + ((driver.Version >> 32) & 65535) + "." + ((driver.Version >> 16) & 65535) + "." + (driver.Version & 65535);
                    if (IsBetter(parameters.Rank, DateTime.FromFileTimeUtc(ticks), version, currentRank.Value, current.Date(), current.DriverVerVersion)) return true;
                }
                return found ? (bool?)false : null;
            }
            catch { return null; }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
    }

    public interface IHardwareCollector
    {
        List<D> Collect(L logger);
    }

    /// <summary>
    /// Adapts an already-built <see cref="Machine"/> (e.g. one passed into the engine, or produced by
    /// WM.Get). This is the engine's default collector so the normal maintenance path does a single WMI
    /// pass (the one that built the machine) instead of querying again.
    /// </summary>
    public sealed class MachineHardwareCollector : IHardwareCollector
    {
        private readonly Machine _machine;
        public MachineHardwareCollector(Machine machine) { _machine = machine; }
        public List<D> Collect(L logger)
        {
            var list = new List<D>();
            if (_machine != null && _machine.Devices != null)
                foreach (var d in _machine.Devices) list.Add(d);
            return list;
        }
    }

    /// <summary>
    /// Performs a fresh WMI pass. Used when no machine is supplied and a standalone collection is wanted.
    /// Delegates to WM.Get (which already deduplicates by DeviceID and degrades on errors) so the real
    /// WMI logic stays in one proven place.
    /// </summary>
    public sealed class WmiHardwareCollector : IHardwareCollector
    {
        public List<D> Collect(L logger)
        {
            var l = logger ?? new L(Path.GetTempPath());
            var m = WM.Get(l);
            CurrentDriverState.Refresh(m.Devices, l);
            return m.Devices ?? new List<D>();
        }
    }

    /// <summary>Injectable, fully deterministic collector for unit/integration tests.</summary>
    public sealed class StaticHardwareCollector : IHardwareCollector
    {
        private readonly List<D> _devices;
        public StaticHardwareCollector(IEnumerable<D> devices) { _devices = (devices ?? new D[0]).ToList(); }
        public List<D> Collect(L logger) { return _devices.ToList(); }
    }

    // =================================================================================
    //  Shared process runner
    // =================================================================================

    /// <summary>
    /// Runs a console process with captured output and a hard timeout. Returns (exitCode, output, timedOut).
    /// This is the single implementation shared by the legacy E.P reflection seam and the pnputil runner,
    /// so exit-code / timeout behaviour is identical everywhere.
    /// </summary>
    internal static class ProcessRunner
    {
        public static Tuple<int, string, bool> Run(string file, string args, int timeoutSeconds)
        {
            var output = new StringBuilder();
            var gate = new object();
            using (var p = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo(file, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            })
            {
                p.OutputDataReceived += delegate(object s, System.Diagnostics.DataReceivedEventArgs e) { if (e.Data != null) lock (gate) output.AppendLine(e.Data); };
                p.ErrorDataReceived += delegate(object s, System.Diagnostics.DataReceivedEventArgs e) { if (e.Data != null) lock (gate) output.AppendLine(e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                bool ok = p.WaitForExit(Math.Max(1, timeoutSeconds) * 1000);
                if (!ok)
                {
                    try { p.Kill(); p.WaitForExit(5000); } catch { }
                    lock (gate) return Tuple.Create(-1, output.ToString(), true);
                }
                p.WaitForExit();
                lock (gate) return Tuple.Create(p.ExitCode, output.ToString(), false);
            }
        }
    }

    // =================================================================================
    //  Driver index service (cancellable, incremental, persistent)
    // =================================================================================

    /// <summary>One resolved driver root plus the stable source name used to tag its records.</summary>
    public sealed class RootSpec
    {
        public string Path = "";
        public string SourceName = "";
        public bool IsModelPackage;
    }

    /// <summary>Aggregate outcome of an index build pass, mirroring IndexUpdateReport totals.</summary>
    public sealed class IndexBuildResult
    {
        public DriverIndex Index;
        public int Roots;
        public int Added, Modified, Deleted, Unchanged;
        public bool Rebuilt;
        public string IndexPath = "";
    }

    /// <summary>
    /// Owns the on-disk index location and drives incremental builds from the resolved roots. Each root
    /// is updated and committed in turn, so a crash or cancellation always leaves a consistent, usable
    /// index at the last fully-committed root. The build never spawns a process per package; it parses
    /// INF files directly (InfParser) and consults the persisted inverted index for queries.
    /// </summary>
    public sealed class DriverIndexService
    {
        private readonly string _indexFile;

        public DriverIndexService(string indexFile) { _indexFile = indexFile; }

        public IndexBuildResult Build(IList<RootSpec> roots, OsTarget target, CancellationToken ct, IProgress<string> progress)
        {
            if (roots == null) roots = new RootSpec[0];
            var result = new IndexBuildResult { Index = new DriverIndex(_indexFile), IndexPath = _indexFile };
            result.Index.Open();
            if (result.Index.NeedsRebuild)
            {
                result.Rebuilt = true;
                if (progress != null) progress.Report("建索引 / Rebuild: schema 版本不匹配或新索引，全量重建");
            }
            for (int i = 0; i < roots.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var root = roots[i];
                if (string.IsNullOrWhiteSpace(root.Path) || !Directory.Exists(root.Path))
                {
                    if (progress != null) progress.Report("建索引: 跳过不可访问的根 " + root.SourceName);
                    continue;
                }
                if (progress != null) progress.Report("建索引 (" + (i + 1) + "/" + roots.Count + "): " + root.SourceName);
                var report = result.Index.UpdateFromDirectory(root.Path, root.SourceName, target, ct);
                result.Index.Commit();
                result.Added += report.Added;
                result.Modified += report.Modified;
                result.Deleted += report.Deleted;
                result.Unchanged += report.Unchanged;
                result.Roots++;
                if (progress != null)
                    progress.Report("  新增 " + report.Added + "，修改 " + report.Modified + "，删除 " + report.Deleted + "，未变 " + report.Unchanged + "，累计 " + result.Index.Count);
            }
            return result;
        }

        /// <summary>Stable short file-name suffix derived from the root set + OS/arch target, so different
        /// machines / configs never share an index file and the same config reuses its index across runs.</summary>
        public static string FingerprintFor(IEnumerable<string> roots, OsTarget target)
        {
            var sb = new StringBuilder();
            foreach (var rt in (roots ?? new string[0]).OrderBy(delegate(string x) { return x; }, StringComparer.OrdinalIgnoreCase))
                sb.Append((rt ?? "").ToLowerInvariant()).Append('|');
            if (target == null) sb.Append("any|0");
            else sb.Append(target.Architecture ?? "").Append('|').Append(target.MajorVersion.ToString(CultureInfo.InvariantCulture));
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder();
                for (int i = 0; i < 8; i++) hex.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }
    }

    // =================================================================================
    //  Install seams: admin / signature / runner (real default + offline fakes)
    // =================================================================================

    /// <summary>Checks whether the current process is elevated. The main EXE manifest requires admin,
    /// so this is a sanity gate; deployment without admin fails fast instead of spawning failing pnputil.</summary>
    public interface IAdminChecker { bool IsAdministrator(); }

    public sealed class WindowsAdminChecker : IAdminChecker
    {
        public bool IsAdministrator()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    public sealed class AlwaysAdminChecker : IAdminChecker { public bool IsAdministrator() { return true; } }
    public sealed class NeverAdminChecker : IAdminChecker { public bool IsAdministrator() { return false; } }

    /// <summary>Verifies a driver package's signature before pnputil is invoked. The default uses
    /// WinVerifyTrust on the catalog (.cat) beside the INF. On any API error it degrades to "trusted"
    /// (pnputil itself enforces signatures), so verification can never block a legitimate install due to
    /// an environment issue; only an explicit verification failure causes a skip.</summary>
    public interface ISignatureVerifier { bool IsTrusted(string infPath, L logger); }

    public sealed class TrustingSignatureVerifier : ISignatureVerifier { public bool IsTrusted(string infPath, L logger) { return true; } }
    public sealed class RejectingSignatureVerifier : ISignatureVerifier { public bool IsTrusted(string infPath, L logger) { return false; } }

    public sealed class WinTrustSignatureVerifier : ISignatureVerifier
    {
        private const uint WTD_UI_NONE = 2;
        private const uint WTD_REVOKE_NONE = 0;
        private const uint WTD_CHOICE_FILE = 1;
        private const uint WTD_STATEACTION_IGNORE = 0;
        private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new Guid("{00AAC56B-CD44-11d0-8CC2-00C04FC295EE}");

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public uint policyFlags;
            public IntPtr hWindow;
            public IntPtr pgSubjectProviderId;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hWindow, [In] ref Guid pgActionId, [In] ref WINTRUST_DATA pData);

        public bool IsTrusted(string infPath, L logger)
        {
            if (string.IsNullOrEmpty(infPath)) return true;
            try
            {
                string dir = Path.GetDirectoryName(infPath);
                if (string.IsNullOrEmpty(dir)) return true;
                // A folder may contain several independent INF/CAT pairs (Intel Serial IO is a
                // common example). Always honor this INF's CatalogFile directive; choosing the
                // directory's first CAT can validate one package against another package's catalog.
                string target = ResolveCatalogPath(infPath);
                if (!File.Exists(target)) return true;

                var fi = new WINTRUST_FILE_INFO
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)),
                    pcwszFilePath = Marshal.StringToCoTaskMemUni(target)
                };
                int fiSize = Marshal.SizeOf(fi);
                IntPtr pFi = Marshal.AllocCoTaskMem(fiSize);
                try
                {
                    Marshal.StructureToPtr(fi, pFi, false);
                    var data = new WINTRUST_DATA
                    {
                        cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)),
                        dwUIChoice = WTD_UI_NONE,
                        fdwRevocationChecks = WTD_REVOKE_NONE,
                        dwUnionChoice = WTD_CHOICE_FILE,
                        pFile = pFi,
                        dwStateAction = WTD_STATEACTION_IGNORE
                    };
                    Guid actionId = WINTRUST_ACTION_GENERIC_VERIFY_V2;
                    int hr = WinVerifyTrust(IntPtr.Zero, ref actionId, ref data);
                    return hr == 0;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pFi);
                    Marshal.FreeCoTaskMem(fi.pcwszFilePath);
                }
            }
            catch (Exception ex)
            {
                if (logger != null) logger.W("[WARN] 签名验证不可用 / Signature verify unavailable, deferring to pnputil: " + ex.Message);
                return true;
            }
        }

        internal static string ResolveCatalogPath(string infPath)
        {
            string dir = Path.GetDirectoryName(infPath) ?? "";
            try
            {
                foreach (string raw in File.ReadLines(infPath, Encoding.Default))
                {
                    string line = raw ?? "";
                    int comment = line.IndexOf(';');
                    if (comment >= 0) line = line.Substring(0, comment);
                    int equals = line.IndexOf('=');
                    if (equals <= 0) continue;
                    string key = line.Substring(0, equals).Trim();
                    if (!key.Equals("CatalogFile", StringComparison.OrdinalIgnoreCase) &&
                        !key.StartsWith("CatalogFile.", StringComparison.OrdinalIgnoreCase)) continue;
                    string name = line.Substring(equals + 1).Trim().Trim('"');
                    if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
                    string candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) return candidate;
                }
                // Legacy packages without CatalogFile: retain the old single-CAT behavior only
                // when it is unambiguous. Multiple unrelated CAT files must not be guessed.
                string[] cats = Directory.GetFiles(dir, "*.cat");
                return cats.Length == 1 ? cats[0] : infPath;
            }
            catch { return infPath; }
        }
    }

    /// <summary>Runs pnputil /add-driver ... /install for one accepted INF. The default shells out to
    /// pnputil via ProcessRunner; the fake records the call and returns a scripted result for tests.</summary>
    public interface IPnpInstallRunner { InstallResult AddInstall(string infPath, int timeoutSeconds, L logger); }

    public interface IPnpPackageInstallRunner { InstallResult AddPackage(string directory, int timeoutSeconds, L logger); }

    public sealed class PnpInstallRunner : IPnpInstallRunner, IPnpPackageInstallRunner
    {
        public InstallResult AddPackage(string directory, int timeoutSeconds, L logger)
        {
            string root = Path.GetFullPath(directory);
            if (!Directory.Exists(root) || root.Contains("\"")) throw new InvalidDataException("Invalid model package directory");
            string args = "/add-driver \"" + Path.Combine(root, "*.inf") + "\" /subdirs /install";
            var t = ProcessRunner.Run("pnputil.exe", args, timeoutSeconds);
            if (logger != null) logger.W("[INFO] 整包导入 / Model package import: " + args + " exit=" + t.Item1 + "\n" + t.Item2);
            return InstallPlanner.ResultFrom(t.Item1, t.Item3, t.Item2);
        }

        public InstallResult AddInstall(string infPath, int timeoutSeconds, L logger)
        {
            string args = InstallPlanner.BuildArguments(infPath);
            var t = ProcessRunner.Run("pnputil.exe", args, timeoutSeconds);
            var result = InstallPlanner.ResultFrom(t.Item1, t.Item3, t.Item2);
            if (logger != null) logger.W("[INFO] pnputil exit=" + result.ExitCode + " (" + result.Outcome + ") " + infPath);
            if (logger != null && !string.IsNullOrWhiteSpace(result.Output)) logger.W("[INFO] pnputil output: " + result.Output.Trim());
            return result;
        }
    }

    /// <summary>Offline runner for tests. Records every invocation (with the safe arguments) and returns
    /// scripted exit codes from a queue, or DefaultExitCode when the queue is empty.</summary>
    public sealed class FakePnpInstallRunner : IPnpInstallRunner, IPnpPackageInstallRunner
    {
        public readonly List<string> PackageCalls = new List<string>();
        public readonly List<string> PackageInfFiles = new List<string>();
        public InstallResult AddPackage(string directory, int timeoutSeconds, L logger)
        {
            PackageCalls.Add(directory); PackageInfFiles.AddRange(Directory.GetFiles(directory, "*.inf", SearchOption.AllDirectories).Select(Path.GetFileName)); Calls.Add(Path.Combine(directory, "*.inf"));
            int code = ExitCodes.Count > 0 ? ExitCodes.Dequeue() : DefaultExitCode;
            return InstallPlanner.ResultFrom(code, false, "fake package import");
        }
        public readonly List<string> Calls = new List<string>();
        public readonly Queue<int> ExitCodes = new Queue<int>();
        public int DefaultExitCode = 0;

        public InstallResult AddInstall(string infPath, int timeoutSeconds, L logger)
        {
            Calls.Add(infPath);
            int code = ExitCodes.Count > 0 ? ExitCodes.Dequeue() : DefaultExitCode;
            return InstallPlanner.ResultFrom(code, false, "fake");
        }
    }

    /// <summary>Per-item install outcome recorded for reporting and verification.</summary>
    public sealed class InstallItemResult
    {
        public string InfPath = "";
        public int Order;
        public int ExitCode = -1;
        public InstallOutcome Outcome = InstallOutcome.Failure;
        public string Output = "";
        public string SkipReason = "";
        public bool Ran;
    }

    /// <summary>Side-effecting system operations that surround an install: exporting the services
    /// registry (for rollback reference) and triggering a PnP device scan afterwards. The default runs
    /// the real reg.exe / pnputil; the no-op fake lets tests exercise the install plan without touching
    /// the live Driver Store or device manager.</summary>
    public interface ISystemOps
    {
        void ExportServicesRegistry(string reportDirectory, L logger);
        void ScanDevices(int timeoutSeconds, L logger);
    }

    public sealed class RealSystemOps : ISystemOps
    {
        public void ExportServicesRegistry(string reportDirectory, L logger)
        {
            string dir = Environment.ExpandEnvironmentVariables(reportDirectory ?? "");
            if (dir.Length == 0) return;
            try { Directory.CreateDirectory(dir); } catch { }
            string file = Path.Combine(dir, "Services-" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".reg");
            var t = ProcessRunner.Run("reg.exe", "export HKLM\\SYSTEM\\CurrentControlSet\\Services \"" + file + "\" /y", 60);
            if (logger != null) logger.W("[INFO] 服务注册表导出 / Services export: exit=" + t.Item1 + " => " + file);
        }

        public void ScanDevices(int timeoutSeconds, L logger)
        {
            var t = ProcessRunner.Run("pnputil.exe", "/scan-devices", timeoutSeconds);
            if (logger != null) logger.W("[INFO] 设备扫描 / scan-devices: exit=" + t.Item1);
        }
    }

    public sealed class NoOpSystemOps : ISystemOps
    {
        public void ExportServicesRegistry(string reportDirectory, L logger) { }
        public void ScanDevices(int timeoutSeconds, L logger) { }
    }
}
