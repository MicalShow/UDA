using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace UDA
{
    internal static class CoreHarness
    {
        private static int Main(string[] args)
        {
            if (args.Length < 2) return 2;
            bool expectedPackage = string.Equals(args[1], "package", StringComparison.OrdinalIgnoreCase);
            var config = C.Load(args[0]);
            var options = new O { WhatIf = true };
            using (var logger = new L(config.General.LogDirectory))
            using (var engine = new E(config, options, logger))
            {
                R result = engine.Scan();
                if (result.Package != expectedPackage) return 3;
                if (result.Infs.Count < 1) return 4;
                if (result.Matches.Count < 1) return 5;
                foreach (X match in result.Matches) match.Pick = true;
                Sum summary = engine.Install(result, expectedPackage, delegate { });
                if (summary.Fail != 0 || summary.Skip < 1) return 6;
                Rep.Save(config, result, summary);
                MethodInfo runner = typeof(E).GetMethod("P", BindingFlags.NonPublic | BindingFlags.Static);
                var watch = Stopwatch.StartNew();
                object processResult = runner.Invoke(null, new object[] { "powershell.exe", "-NoProfile -Command \"Start-Sleep -Seconds 5\"", 1 });
                watch.Stop();
                bool timedOut = (bool)processResult.GetType().GetProperty("Item3").GetValue(processResult, null);
                if (!timedOut || watch.Elapsed.TotalSeconds > 4.5) return 7;
                System.Threading.Tasks.Parallel.For(0, 50, delegate(int i) { logger.W("[TEST] concurrent log " + i); });
                Console.WriteLine("mode={0}; inf={1}; matches={2}; skipped={3}", result.Mode, result.Infs.Count, result.Matches.Count, summary.Skip);
                return 0;
            }
        }
    }
}
