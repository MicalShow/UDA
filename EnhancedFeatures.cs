using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace UDA
{
    /// <summary>Collects human-readable memory and disk health information. / 采集可读的内存与磁盘健康信息。</summary>
    public static class HardwareProbe
    {
        private sealed class DiskHealth
        {
            public string Name = "";
            public string Health = "未知";
        }

        public static void Enrich(Machine machine, L logger)
        {
            if (machine == null) return;
            CurrentDriverState.Refresh(machine.Devices, logger);
            machine.Info.RemoveAll(delegate(string[] x)
            {
                return x != null && x.Length > 1 && (x[1] == "内存字节" || x[1] == "物理内存" || x[0] == "内存" || (x[0] == "存储" && x[1].StartsWith("磁盘", StringComparison.Ordinal)));
            });
            AddMemory(machine, logger);
            AddDisks(machine, logger);
        }

        private static void AddMemory(Machine machine, L logger)
        {
            try
            {
                var modules = new List<string>();
                ulong total = 0;
                using (var searcher = new ManagementObjectSearcher("SELECT BankLabel,DeviceLocator,Manufacturer,PartNumber,Capacity,Speed,ConfiguredClockSpeed,SMBIOSMemoryType FROM Win32_PhysicalMemory"))
                using (var results = searcher.Get())
                {
                    int index = 0;
                    foreach (ManagementObject item in results)
                    {
                        index++;
                        ulong capacity = ToUInt64(item["Capacity"]); total += capacity;
                        string maker = Value(item, "Manufacturer");
                        string part = Value(item, "PartNumber");
                        string locator = First(Value(item, "DeviceLocator"), Value(item, "BankLabel"), "插槽 " + index);
                        string speed = First(Value(item, "ConfiguredClockSpeed"), Value(item, "Speed"));
                        string type = MemoryType(ToInt(item["SMBIOSMemoryType"]));
                        string model = JoinNonEmpty(" · ", maker, part);
                        modules.Add(locator + " | " + FormatBytes(capacity) + (type.Length > 0 ? " " + type : "") + (speed.Length > 0 ? " " + speed + " MHz" : "") + (model.Length > 0 ? " | " + model : ""));
                    }
                }
                machine.Info.Add(new[] { "内存", "物理内存", FormatBytes(total) + "（" + modules.Count + " 条）" });
                for (int i = 0; i < modules.Count; i++) machine.Info.Add(new[] { "内存", "内存条 " + (i + 1), modules[i] });
                logger.W("[INFO] 内存 / Memory: " + FormatBytes(total) + ", modules=" + modules.Count);
            }
            catch (Exception ex)
            {
                logger.W("[WARN] 内存信息读取失败 / Memory probe failed: " + ex.Message);
                machine.Info.Add(new[] { "内存", "物理内存", "无法读取" });
            }
        }

        private static void AddDisks(Machine machine, L logger)
        {
            var health = ReadStorageHealth(logger);
            var smartFailures = ReadSmartFailures(logger);
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Index,Model,SerialNumber,Size,InterfaceType,MediaType,Status,PNPDeviceID FROM Win32_DiskDrive"))
                using (var results = searcher.Get())
                {
                    int fallback = 0;
                    foreach (ManagementObject disk in results)
                    {
                        fallback++;
                        string model = First(Value(disk, "Model"), "未知型号");
                        string serial = Value(disk, "SerialNumber");
                        string pnp = Value(disk, "PNPDeviceID");
                        string state = MatchHealth(model, health);
                        bool? smart = MatchSmart(pnp, smartFailures);
                        if (smart == true) state = "危险（SMART 预测故障）";
                        else if (smart == false && state == "未知") state = "正常（SMART）";
                        else if (state == "未知" && Value(disk, "Status").Equals("OK", StringComparison.OrdinalIgnoreCase)) state = "正常（设备状态）";
                        int index = ToInt(disk["Index"]);
                        string detail = model + " | " + FormatBytes(ToUInt64(disk["Size"])) + " | 健康度: " + state;
                        string iface = Value(disk, "InterfaceType"); if (iface.Length > 0) detail += " | " + iface;
                        if (serial.Length > 0) detail += " | SN: " + serial;
                        machine.Info.Add(new[] { "存储", "磁盘 " + (index >= 0 ? (index + 1) : fallback), detail });
                        logger.W("[INFO] 磁盘 / Disk: " + detail);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.W("[WARN] 磁盘信息读取失败 / Disk probe failed: " + ex.Message);
                machine.Info.Add(new[] { "存储", "磁盘", "无法读取健康信息" });
            }
        }

        private static List<DiskHealth> ReadStorageHealth(L logger)
        {
            var list = new List<DiskHealth>();
            try
            {
                var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage"); scope.Connect();
                using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT FriendlyName,HealthStatus,OperationalStatus FROM MSFT_PhysicalDisk")))
                using (var results = searcher.Get())
                    foreach (ManagementObject item in results) list.Add(new DiskHealth { Name = Value(item, "FriendlyName"), Health = HealthText(ToInt(item["HealthStatus"])) });
            }
            catch (Exception ex) { logger.W("[DEBUG] MSFT_PhysicalDisk 不可用: " + ex.Message); }
            return list;
        }

        private static Dictionary<string, bool> ReadSmartFailures(L logger)
        {
            var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var scope = new ManagementScope(@"\\.\root\wmi"); scope.Connect();
                using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT InstanceName,PredictFailure FROM MSStorageDriver_FailurePredictStatus")))
                using (var rows = searcher.Get()) foreach (ManagementObject row in rows) result[Normalize(Value(row, "InstanceName"))] = Convert.ToBoolean(row["PredictFailure"] ?? false);
            }
            catch (Exception ex) { logger.W("[DEBUG] SMART 状态不可用: " + ex.Message); }
            return result;
        }

        private static string MatchHealth(string model, List<DiskHealth> values)
        {
            string normalized = Normalize(model);
            DiskHealth best = values.Where(x => Normalize(x.Name).Length > 0 && normalized.Length > 0).OrderByDescending(x => Normalize(x.Name).Length).FirstOrDefault(x => normalized.Contains(Normalize(x.Name)) || Normalize(x.Name).Contains(normalized));
            return best == null ? "未知" : best.Health;
        }

        private static bool? MatchSmart(string pnp, Dictionary<string, bool> values)
        {
            string key = Normalize(pnp); if (key.Length == 0) return null; foreach (var pair in values) if (pair.Key.Length > 0 && (pair.Key.Contains(key) || key.Contains(pair.Key))) return pair.Value; return null;
        }

        private static string HealthText(int value) { return value == 0 ? "正常" : value == 1 ? "警告" : value == 2 ? "危险" : "未知"; }
        private static string MemoryType(int value) { return value == 20 ? "DDR" : value == 21 ? "DDR2" : value == 24 ? "DDR3" : value == 26 ? "DDR4" : value == 30 ? "LPDDR4" : value == 34 ? "DDR5" : value == 35 ? "LPDDR5" : ""; }
        private static string FormatBytes(ulong bytes) { if (bytes == 0) return "未知"; double gib = bytes / 1073741824D; return gib >= 1024 ? (gib / 1024D).ToString("0.##", CultureInfo.InvariantCulture) + " TB" : gib.ToString("0.##", CultureInfo.InvariantCulture) + " GB"; }
        private static string Value(ManagementBaseObject item, string name) { try { return item[name] == null ? "" : Convert.ToString(item[name]).Trim(); } catch { return ""; } }
        private static ulong ToUInt64(object value) { ulong result; return UInt64.TryParse(Convert.ToString(value), out result) ? result : 0; }
        private static int ToInt(object value) { int result; return Int32.TryParse(Convert.ToString(value), out result) ? result : -1; }
        private static string First(params string[] values) { return values.FirstOrDefault(x => !String.IsNullOrWhiteSpace(x)) ?? ""; }
        private static string JoinNonEmpty(string separator, params string[] values) { return String.Join(separator, values.Where(x => !String.IsNullOrWhiteSpace(x)).ToArray()); }
        private static string Normalize(string value) { return new string((value ?? "").ToUpperInvariant().Where(Char.IsLetterOrDigit).ToArray()); }
    }

    /// <summary>Samples machine-wide CPU and memory usage without localized performance counters.</summary>
    public sealed class SystemResourceSampler
    {
        [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint Low, High; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)] private struct MemoryStatus { public uint Length, MemoryLoad; public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual; }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
        private ulong lastIdle, lastKernel, lastUser; private bool primed;

        public Tuple<double, double> Sample()
        {
            FileTime idle, kernel, user; double cpu = 0;
            if (GetSystemTimes(out idle, out kernel, out user))
            {
                ulong i = ToUInt64(idle), k = ToUInt64(kernel), u = ToUInt64(user);
                if (primed) { ulong total = (k - lastKernel) + (u - lastUser), idleDelta = i - lastIdle; if (total > 0) cpu = Math.Max(0, Math.Min(100, (total - idleDelta) * 100D / total)); }
                lastIdle = i; lastKernel = k; lastUser = u; primed = true;
            }
            var memory = new MemoryStatus(); memory.Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)); double mem = GlobalMemoryStatusEx(ref memory) ? memory.MemoryLoad : 0;
            return Tuple.Create(cpu, mem);
        }
        private static ulong ToUInt64(FileTime value) { return ((ulong)value.High << 32) | value.Low; }
    }

    /// <summary>Exports one or all third-party installed drivers. / 导出单个或全部第三方驱动。</summary>
    public static class DriverExporter
    {
        public static string ExportOne(string publishedInf, string destination, int timeoutSeconds, L logger)
        {
            if (String.IsNullOrWhiteSpace(publishedInf)) throw new InvalidOperationException("所选设备没有可导出的当前驱动 INF。");
            if (!publishedInf.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("当前驱动名称无效: " + publishedInf);
            Directory.CreateDirectory(destination);
            var result = Run("pnputil.exe", "/export-driver \"" + publishedInf + "\" \"" + destination + "\"", timeoutSeconds);
            logger.W("[INFO] 导出单个驱动 / Export one: " + publishedInf + " => " + destination + ", exit=" + result.Item1 + Environment.NewLine + result.Item2);
            if (result.Item3) throw new TimeoutException("导出驱动超时。");
            if (result.Item1 != 0) throw new Win32Exception(result.Item1, "导出驱动失败。" + Environment.NewLine + result.Item2);
            return result.Item2;
        }

        public static string ExportAll(string destination, int timeoutSeconds, L logger)
        {
            Directory.CreateDirectory(destination);
            var result = Run("dism.exe", "/online /export-driver /destination:\"" + destination + "\" /English", timeoutSeconds);
            logger.W("[INFO] 导出全部驱动 / Export all => " + destination + ", exit=" + result.Item1 + Environment.NewLine + result.Item2);
            if (result.Item3) throw new TimeoutException("导出全部驱动超时。");
            if (result.Item1 != 0) throw new Win32Exception(result.Item1, "导出全部驱动失败。" + Environment.NewLine + result.Item2);
            return result.Item2;
        }

        internal static Tuple<int, string, bool> Run(string file, string arguments, int timeoutSeconds)
        {
            var output = new StringBuilder(); var gate = new object();
            using (var process = new Process { StartInfo = new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.Default, StandardErrorEncoding = Encoding.Default } })
            {
                process.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (gate) output.AppendLine(e.Data); };
                process.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (gate) output.AppendLine(e.Data); };
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                bool completed = process.WaitForExit(Math.Max(1, timeoutSeconds) * 1000);
                if (!completed) { try { process.Kill(); process.WaitForExit(5000); } catch { } lock (gate) return Tuple.Create(-1, output.ToString(), true); }
                process.WaitForExit(); lock (gate) return Tuple.Create(process.ExitCode, output.ToString(), false);
            }
        }
    }
}
