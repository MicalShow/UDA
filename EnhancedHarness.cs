using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace UDA
{
    internal static class EnhancedHarness
    {
        private static int Main(string[] args)
        {
            if (args.Length < 1) return 2;
            string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
            using (var logger = new L(Path.Combine(root, "logs")))
            {
                string package = Path.Combine(root, "drivers", "GA", "Z790UD"); Directory.CreateDirectory(package);
                File.WriteAllText(Path.Combine(package, "test.inf"), "[Version]\r\nSignature=\"$Windows NT$\"\r\nClass=System\r\nDriverVer=07/19/2026,1.0.0.0\r\n[Models]\r\nDevice=Install,PCI\\VEN_1234&DEV_5678\r\n", Encoding.Default);
                var config = new C(); config.General.ScanAllSourcesWhenModelPackageMissing = true; config.General.ScanAllSourcesAfterModelPackage = false;
                config.Sources.Add(new S { Name = "Local", Path = Path.Combine(root, "drivers"), Enabled = true, Priority = 1 });
                config.ModelMappings.Add(new M { Name = "Disabled", ManufacturerPattern = "GIGABYTE", ModelPattern = "Z790 UD AX", DriverPath = "missing", Enabled = false, Priority = 0 });
                config.ModelMappings.Add(new M { Name = "FirstMissing", ManufacturerPattern = "GIGABYTE", ModelPattern = "Z790 UD AX", DriverPath = "missing", Enabled = true, Priority = 1 });
                config.ModelMappings.Add(new M { Name = "WorkingPackage", ManufacturerPattern = "GIGABYTE", ModelPattern = "Z790 UD AX", DriverPath = "GA/Z790UD", Enabled = true, Priority = 2 });
                var machine = new Machine { Maker = "Gigabyte Technology Co., Ltd.", Model = "Z790 UD AX", Sku = "Default string" };
                using (var engine = new E(config, new O { WhatIf = true }, logger, machine))
                {
                    R scan = engine.Scan();
                    if (!scan.Package || scan.Mapping != "WorkingPackage" || scan.PackageInfs.Count != 1) return 10;
                }

                Machine actual = WM.Get(logger); HardwareProbe.Enrich(actual, logger);
                if (actual.Info.Any(x => x.Length > 1 && x[1] == "内存字节")) return 11;
                string memory = actual.Info.Where(x => x.Length > 2 && x[0] == "内存" && x[1] == "物理内存").Select(x => x[2]).FirstOrDefault();
                if (String.IsNullOrWhiteSpace(memory) || memory.IndexOf("条", StringComparison.Ordinal) < 0) return 12;
                if (!actual.Info.Any(x => x.Length > 2 && x[0] == "存储" && x[2].IndexOf("健康度:", StringComparison.Ordinal) >= 0)) return 13;

                var sampler = new SystemResourceSampler(); sampler.Sample(); Thread.Sleep(250); var sample = sampler.Sample();
                if (sample.Item1 < 0 || sample.Item1 > 100 || sample.Item2 < 0 || sample.Item2 > 100) return 14;
                bool rejected = false; try { DriverExporter.ExportOne("", root, 1, logger); } catch (InvalidOperationException) { rejected = true; }
                if (!rejected) return 15;
                if (args.Length > 1 && !String.IsNullOrWhiteSpace(args[1]))
                {
                    string exportPath = Path.Combine(root, "actual-driver-export");
                    DriverExporter.ExportOne(args[1], exportPath, 120, logger);
                    if (!Directory.EnumerateFiles(exportPath, "*", SearchOption.AllDirectories).Any()) return 16;
                }
                Console.WriteLine("PASS mapping fallback; memory=" + memory + "; CPU=" + sample.Item1.ToString("0") + "%; RAM=" + sample.Item2.ToString("0") + "%");
                return 0;
            }
        }
    }
}
