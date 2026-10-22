// Temporary probe (delete after use): parse a synthetic INF, run ranker + applicability.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UDA;

public static class Probe
{
    private static int _fail;
    private static void T(bool c, string m) { Console.WriteLine((c ? "[ok] " : "[BAD] ") + m); if (!c) _fail++; }

    public static int Main(string[] args)
    {
        string dir = Path.Combine(Path.GetTempPath(), "uda-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string inf = Path.Combine(dir, "dev.inf");
        File.WriteAllText(inf, new StringBuilder()
            .AppendLine("[Version]")
            .AppendLine("Signature=\"$Windows NT$\"")
            .AppendLine("Class=Net")
            .AppendLine("Provider=%Vendor%")
            .AppendLine("DriverVer=03/14/2026,2.0.0.0")
            .AppendLine("")
            .AppendLine("[Manufacturer]")
            .AppendLine("%Vendor%=Models.NTamd64.10")
            .AppendLine("")
            .AppendLine("[Models.NTamd64.10]")
            .AppendLine("%Dev%=Install,PCI\\VEN_1000&DEV_1000")
            .AppendLine("")
            .AppendLine("[Strings]")
            .AppendLine("Vendor=\"Test Vendor\"")
            .AppendLine("Dev=\"Test Device\"")
            .ToString(), new UTF8Encoding(false));

        var rec = InfParser.Parse(inf, "dev.inf");
        T(rec.HardwareIds.Count == 1, "parsed hwids=" + rec.HardwareIds.Count + " [" + string.Join(",", rec.HardwareIds.ToArray()) + "]");
        T(rec.Decorations.Count == 1 && rec.Decorations[0] == "NTamd64.10", "decorations=" + string.Join(",", rec.Decorations.ToArray()));

        var target = new OsTarget { Architecture = "amd64", MajorVersion = 10 };
        var ranker = new MatchRanker(new[] { rec }, new string[0]);
        var cands = ranker.Rank(new List<string> { "PCI\\VEN_1000&DEV_1000" }, new List<string>(), target);
        T(cands.Count == 1, "rank candidates=" + cands.Count);

        var v = InfApplicability.Evaluate(rec, target, new List<string> { "PCI\\VEN_1000&DEV_1000" }, null);
        T(v.Applicable, "applicable=" + v.Applicable + " code=" + v.ReasonCode + " reason=" + v.Reason);

        // Repo round-trip: generate + open like the server does.
        string repo = Path.Combine(dir, "repo");
        try
        {
        string srcDir = Path.Combine(dir, "src");
        Directory.CreateDirectory(srcDir);
        File.Move(inf, Path.Combine(srcDir, "dev.inf"));
        new OfflineRepoGenerator(new TrustingSignatureVerifier(), null).Generate(repo, new[] { srcDir },
            target, null, "2026-07-28T00:00:00Z", System.Threading.CancellationToken.None, null);
        using (var r = OfflineRepo.Open(repo, null))
        {
            T(r.Records.Count == 1, "repo records=" + r.Records.Count);
            var rec2 = r.Records[0];
            T(rec2.HardwareIds.Count == 1, "repo rec hwids=" + rec2.HardwareIds.Count + " decorations=" + string.Join(",", rec2.Decorations.ToArray()));
            var rk = r.CreateRanker();
            var c2 = rk.Rank(new List<string> { "PCI\\VEN_1000&DEV_1000" }, new List<string>(), target);
            T(c2.Count == 1, "repo rank candidates=" + c2.Count);
        }
        }
        catch (Exception ex)
        {
            T(false, "generate/open threw " + ex.GetType().FullName + ": " + ex.Message);
            if (ex.StackTrace != null) Console.WriteLine(ex.StackTrace);
        }
        // --- Server-level probe: same config as LanHarness.StartEnv ---
        try
        {
        string repo2 = Path.Combine(dir, "repo2");
        string src2 = Path.Combine(dir, "src2"); Directory.CreateDirectory(src2);
        WriteTestInf(src2);
        new OfflineRepoGenerator(new TrustingSignatureVerifier(), null).Generate(repo2, new[] { src2 },
            new OsTarget { Architecture = "amd64", MajorVersion = 10 }, null, "2026-07-28T00:00:00Z",
            System.Threading.CancellationToken.None, null);
        var lsn = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); lsn.Start();
        int freePort = ((System.Net.IPEndPoint)lsn.LocalEndpoint).Port; lsn.Stop();
        var scfg = new LanServerConfig { Port = freePort, RepoRoot = repo2, Token = "", OsArchitecture = "amd64", OsMajorVersion = 10, Host = "localhost" };
        var log = new L(Path.Combine(dir, "logs"));
        var server = new LanDriverServer(scfg, log);
        server.ImportSignatureVerifier = new TrustingSignatureVerifier();
        server.Start();
        try
        {
            string url = "http://localhost:" + freePort.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/api/report";
            string body = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new ReportShape {
                Protocol = 1, RequestId = "probe-1", OsArchitecture = "amd64", OsMajorVersion = 10,
                Devices = new List<DevShape> { new DevShape { DeviceInstanceId = "DEV1", Class = "Net",
                    HardwareIds = new List<string> { "PCI\\VEN_1000&DEV_1000" } } }
            });
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            req.Method = "POST"; req.ContentType = "application/json"; req.Timeout = 10000;
            req.Headers["X-UDA-Protocol"] = "1";
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            string respText;
            using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) respText = sr.ReadToEnd();
            Console.WriteLine("REPORT RESPONSE: " + respText);
        }
        catch (Exception ex)
        {
            T(false, "http report threw " + ex.GetType().FullName + ": " + ex.Message);
        }
        using (var r = OfflineRepo.Open(repo2, log))
        {
            var rk = r.CreateRanker();
            var c = rk.Rank(new List<string> { "PCI\\VEN_1000&DEV_1000" }, new List<string>(),
                new OsTarget { Architecture = "amd64", MajorVersion = 10 });
            T(c.Count == 1, "post-server repo rank=" + c.Count);
            var rr = r.Records[0];
            Console.WriteLine("repo rec: decorations=[" + string.Join(",", rr.Decorations.ToArray()) + "] models=" + rr.Models.Count);
            foreach (var m in rr.Models) Console.WriteLine("  model: desc=" + m.Description + " deco=[" + m.Decoration + "] hw=" + string.Join(",", m.HardwareIds.ToArray()));
            var v2 = InfApplicability.Evaluate(rr, new OsTarget { Architecture = "amd64", MajorVersion = 10 },
                new List<string> { "PCI\\VEN_1000&DEV_1000" }, null);
            T(v2.Applicable, "repo rec evaluate=" + v2.Applicable + " code=" + v2.ReasonCode);
            T(InfApplicability.DecorationApplicable(rr.Models.Count > 0 ? rr.Models[0].Decoration : "X",
                new OsTarget { Architecture = "amd64", MajorVersion = 10 }), "decoration applicable");
        }
        server.Stop(); server.Dispose();
        }
        catch (Exception ex)
        {
            T(false, "server probe threw " + ex.GetType().FullName + ": " + ex.Message);
            if (ex.StackTrace != null) Console.WriteLine(ex.StackTrace);
        }
        try { Directory.Delete(dir, true); } catch { }
        Console.WriteLine(_fail == 0 ? "PROBE PASS" : "PROBE FAIL");
        return _fail;
    }

    private static void WriteTestInf(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "dev.inf"), new StringBuilder()
            .AppendLine("[Version]")
            .AppendLine("Signature=\"$Windows NT$\"")
            .AppendLine("Class=Net")
            .AppendLine("Provider=%Vendor%")
            .AppendLine("DriverVer=03/14/2026,2.0.0.0")
            .AppendLine("")
            .AppendLine("[Manufacturer]")
            .AppendLine("%Vendor%=Models.NTamd64.10")
            .AppendLine("")
            .AppendLine("[Models.NTamd64.10]")
            .AppendLine("%Dev%=Install,PCI\\VEN_1000&DEV_1000")
            .AppendLine("")
            .AppendLine("[Strings]")
            .AppendLine("Vendor=\"Test Vendor\"")
            .AppendLine("Dev=\"Test Device\"")
            .ToString(), new UTF8Encoding(false));
    }

    public sealed class ReportShape
    {
        public int Protocol; public string RequestId = ""; public string OsArchitecture = ""; public int OsMajorVersion;
        public List<DevShape> Devices = new List<DevShape>();
    }
    public sealed class DevShape
    {
        public string DeviceInstanceId = ""; public string Class = "";
        public List<string> HardwareIds = new List<string>();
    }
}
