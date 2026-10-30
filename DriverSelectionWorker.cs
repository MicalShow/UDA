// Isolated, timeout-bounded installation of an explicitly selected compatible INF on one device.
using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Security.Cryptography;
namespace UDA
{
    public static class DriverSelectionProgram
    {
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
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupCopyOEMInfW(string inf, string media, uint type, uint flags, StringBuilder destination, uint size, out uint required, IntPtr component);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr guid, IntPtr parent);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiOpenDeviceInfoW(IntPtr set, string id, IntPtr parent, uint flags, ref Device device);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInstallParamsW(IntPtr set, ref Device device, ref Parameters parameters);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiSetDeviceInstallParamsW(IntPtr set, ref Device device, ref Parameters parameters);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiBuildDriverInfoList(IntPtr set, ref Device device, uint type);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiEnumDriverInfoW(IntPtr set, ref Device device, uint type, uint index, ref Driver driver);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDriverInstallParamsW(IntPtr set, ref Device device, ref Driver driver, ref DriverParameters parameters);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("newdev.dll", SetLastError = true)] private static extern bool DiInstallDevice(IntPtr parent, IntPtr set, ref Device device, ref Driver driver, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool reboot);
        private static void Check(bool ok, string operation) { if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), operation); }
        private static string Hash(string file) { using (var input = File.OpenRead(file)) using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(input)); }
        public static int Main(string[] args)
        {
            // This executable never installs without the parent's explicit device and INF arguments.
            if (args.Length != 4 || args[0] != "--device" || args[2] != "--inf" || string.IsNullOrWhiteSpace(args[1])) return 87;
            try
            {
                string inf = Path.GetFullPath(args[3]);
                if (!File.Exists(inf) || !inf.EndsWith(".inf", StringComparison.OrdinalIgnoreCase) || inf.Length >= 260) throw new ArgumentException("Selected INF path is missing or exceeds SetupAPI limit");
                var destination = new StringBuilder(260); uint required;
                Check(SetupCopyOEMInfW(inf, null, 1, 0, destination, 260, out required, IntPtr.Zero), "SetupCopyOEMInf");
                string staged = destination.ToString();
                if (!File.Exists(staged) || Hash(inf) != Hash(staged)) throw new InvalidDataException("Staged INF differs from selected INF");
                IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
                if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    var device = new Device { Size = (uint)Marshal.SizeOf(typeof(Device)) };
                    Check(SetupDiOpenDeviceInfoW(set, args[1], IntPtr.Zero, 0, ref device), "SetupDiOpenDeviceInfo");
                    var options = new Parameters { Size = (uint)Marshal.SizeOf(typeof(Parameters)) };
                    Check(SetupDiGetDeviceInstallParamsW(set, ref device, ref options), "SetupDiGetDeviceInstallParams");
                    options.Flags |= 0x00010000; // DI_ENUMSINGLEINF: cannot select a different package.
                    options.FlagsEx |= 0x00000800; // Allow compatible drivers marked ExcludeFromSelect.
                    options.Path = staged;
                    Check(SetupDiSetDeviceInstallParamsW(set, ref device, ref options), "SetupDiSetDeviceInstallParams");
                    Check(SetupDiBuildDriverInfoList(set, ref device, 2), "SetupDiBuildDriverInfoList");
                    Driver selected = new Driver(); uint best = uint.MaxValue; bool found = false;
                    for (uint i = 0; ; i++)
                    {
                        var driver = new Driver { Size = (uint)Marshal.SizeOf(typeof(Driver)) };
                        if (!SetupDiEnumDriverInfoW(set, ref device, 2, i, ref driver)) { int error = Marshal.GetLastWin32Error(); if (error != 259) throw new Win32Exception(error); break; }
                        var parameters = new DriverParameters { Size = (uint)Marshal.SizeOf(typeof(DriverParameters)) };
                        Check(SetupDiGetDriverInstallParamsW(set, ref device, ref driver, ref parameters), "SetupDiGetDriverInstallParams");
                        if (!found || parameters.Rank < best) { found = true; best = parameters.Rank; selected = driver; }
                    }
                    if (!found) throw new InvalidOperationException("Selected INF has no compatible driver for the specified device");
                    bool reboot;
                    Console.WriteLine("Explicit device install: " + args[1] + "; INF=" + staged + "; rank=" + best);
                    Check(DiInstallDevice(IntPtr.Zero, set, ref device, ref selected, 0, out reboot), "DiInstallDevice");
                    Console.WriteLine("Selected driver installation completed; reboot=" + reboot);
                    return reboot ? 3010 : 0;
                }
                finally { SetupDiDestroyDeviceInfoList(set); }
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message); var win32 = ex as Win32Exception; return win32 != null && win32.NativeErrorCode != 0 ? win32.NativeErrorCode : 1; }
        }
    }
}
