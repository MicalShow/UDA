using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace UDA
{
    public sealed class ClusterPeer
    {
        public string Url = "";
        public string Token = "";
    }

    public sealed class ClusterNodeStatus
    {
        public string Url = "";
        public string ManifestHash = "";
        public int ActiveDownloads;
        public bool Ready;
        public DateTime SeenUtc;
    }

    public sealed class ClusterNodeFile
    {
        public int Version = 1;
        public string ProtectedPeers = "";
    }

    /// <summary>Small control-plane router. Peer nodes are probed out of band and clients receive a
    /// direct download endpoint with their match response. The selected node must advertise the same
    /// signed catalog hash; driver bytes never pass through the routing node.</summary>
    public sealed class ClusterRouter : IDisposable
    {
        private readonly object _gate = new object();
        private readonly string _repo;
        private readonly string _selfUrl;
        private readonly string _token;
        private readonly Func<string> _manifestHash;
        private readonly Func<int> _activeDownloads;
        private List<ClusterPeer> _peers = new List<ClusterPeer>();
        private List<ClusterNodeStatus> _healthy = new List<ClusterNodeStatus>();
        private Timer _timer;
        private int _polling;

        public ClusterRouter(string repo, string selfUrl, string token, Func<string> manifestHash, Func<int> activeDownloads)
        {
            _repo = repo ?? ""; _selfUrl = Normalize(selfUrl); _token = token ?? "";
            _manifestHash = manifestHash; _activeDownloads = activeDownloads;
            _peers = Load(_repo).Where(x => string.Equals(x.Token, _token, StringComparison.Ordinal)).ToList();
            _healthy.Add(Self());
        }

        private string ConfigPath { get { return Path.Combine(_repo, "cluster-nodes.json"); } }
        private static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 }; }
        private static string Normalize(string url) { return (url ?? "").Trim().TrimEnd('/'); }

        public List<ClusterPeer> Peers() { lock (_gate) return _peers.Select(x => new ClusterPeer { Url = x.Url, Token = "" }).ToList(); }

        private static List<ClusterPeer> Load(string repo)
        {
            string path = Path.Combine(repo ?? "", "cluster-nodes.json");
            if (!File.Exists(path)) return new List<ClusterPeer>();
            try
            {
                var file = Json().Deserialize<ClusterNodeFile>(File.ReadAllText(path));
                byte[] clear = ProtectedData.Unprotect(Convert.FromBase64String(file.ProtectedPeers), null, DataProtectionScope.LocalMachine);
                var list = Json().Deserialize<List<ClusterPeer>>(Encoding.UTF8.GetString(clear));
                return (list ?? new List<ClusterPeer>()).Where(x => x != null && ValidPeer(x)).GroupBy(x => Normalize(x.Url), StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList();
            }
            catch { return new List<ClusterPeer>(); }
        }

        private static bool ValidPeer(ClusterPeer peer)
        {
            Uri uri; return Uri.TryCreate(peer.Url, UriKind.Absolute, out uri) && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.UserInfo) && (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback) &&
                !string.IsNullOrWhiteSpace(peer.Token) && peer.Token.Length <= 4096;
        }

        public void Update(IList<ClusterPeer> peers)
        {
            List<ClusterPeer> current; lock (_gate) current = _peers.ToList();
            var requested = peers ?? new List<ClusterPeer>();
            var next = requested.Select(x => {
                string url = Normalize(x.Url); string token = x.Token ?? "";
                if (token.Length == 0) { var prior = current.FirstOrDefault(p => string.Equals(p.Url, url, StringComparison.OrdinalIgnoreCase)); if (prior != null) token = prior.Token; }
                return new ClusterPeer { Url = url, Token = token };
            })
                .Where(x => !string.Equals(x.Url, _selfUrl, StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => x.Url, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).Take(64).ToList();
            if (next.Any(x => !ValidPeer(x) || !string.Equals(x.Token, _token, StringComparison.Ordinal)))
                throw new ArgumentException("集群节点必须使用相同的客户端 Token，且 URL 必须为 HTTPS（仅回环地址允许 HTTP）");
            Directory.CreateDirectory(_repo);
            byte[] clear = Encoding.UTF8.GetBytes(Json().Serialize(next));
            var file = new ClusterNodeFile { ProtectedPeers = Convert.ToBase64String(ProtectedData.Protect(clear, null, DataProtectionScope.LocalMachine)) };
            string path = ConfigPath, temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, Json().Serialize(file), new UTF8Encoding(false));
                ServerStorage.SecureFile(temp);
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            lock (_gate) { _peers = next; _healthy = new List<ClusterNodeStatus> { Self() }; }
            Poll();
        }

        private ClusterNodeStatus Self()
        {
            string hash = ""; try { hash = _manifestHash != null ? _manifestHash() ?? "" : ""; } catch { }
            return new ClusterNodeStatus { Url = _selfUrl, ManifestHash = hash, ActiveDownloads = _activeDownloads != null ? _activeDownloads() : 0, Ready = hash.Length > 0, SeenUtc = DateTime.UtcNow };
        }

        public void Start()
        {
            if (_timer != null) return;
            _timer = new Timer(delegate { Poll(); }, null, 0, 1500);
        }

        private void Poll()
        {
            if (Interlocked.Exchange(ref _polling, 1) != 0) return;
            try
            {
                List<ClusterPeer> peers; lock (_gate) peers = _peers.ToList();
                var nodes = new List<ClusterNodeStatus> { Self() };
                string localHash = nodes[0].ManifestHash;
                // Probe nodes concurrently: serial 1.1s timeouts multiplied by the 64-node
                // configuration limit could otherwise leave the cluster view stale for a minute.
                Parallel.ForEach(peers, new ParallelOptions { MaxDegreeOfParallelism = 32 }, peer =>
                {
                    try
                    {
                        var req = (HttpWebRequest)WebRequest.Create(peer.Url + "/api/cluster/health");
                        req.Method = "GET"; req.Timeout = 1100; req.ReadWriteTimeout = 1100; req.Proxy = null;
                        req.Headers[LanProtocol.HeaderProtocol] = LanProtocol.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        req.Headers[LanProtocol.HeaderToken] = peer.Token;
                        using (var response = (HttpWebResponse)req.GetResponse())
                        using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                        {
                            var node = Json().Deserialize<ClusterNodeStatus>(reader.ReadToEnd());
                            if (node != null && node.Ready && string.Equals(node.ManifestHash, localHash, StringComparison.OrdinalIgnoreCase))
                            {
                                node.Url = Normalize(peer.Url); node.SeenUtc = DateTime.UtcNow;
                                lock (nodes) nodes.Add(node);
                            }
                        }
                    }
                    catch { }
                });
                lock (_gate) _healthy = nodes;
            }
            finally { Interlocked.Exchange(ref _polling, 0); }
        }

        public ClusterNodeStatus Select(string machine)
        {
            List<ClusterNodeStatus> nodes;
            lock (_gate) nodes = _healthy.Where(x => x.Ready && x.SeenUtc > DateTime.UtcNow.AddSeconds(-6)).ToList();
            if (nodes.Count == 0) return Self();
            int minimum = nodes.Min(x => x.ActiveDownloads);
            nodes = nodes.Where(x => x.ActiveDownloads == minimum).OrderBy(x => x.Url, StringComparer.OrdinalIgnoreCase).ToList();
            if (nodes.Count == 1) return nodes[0];
            byte[] digest;
            using (var sha = SHA256.Create()) digest = sha.ComputeHash(Encoding.UTF8.GetBytes((machine ?? "") + "|" + nodes.Count));
            uint slot = BitConverter.ToUInt32(digest, 0) % (uint)nodes.Count;
            return nodes[(int)slot];
        }

        public ClusterNodeStatus Health()
        {
            return Self();
        }

        public List<ClusterNodeStatus> Status()
        { lock (_gate) return _healthy.Select(x => new ClusterNodeStatus { Url = x.Url, ManifestHash = x.ManifestHash, ActiveDownloads = x.ActiveDownloads, Ready = x.Ready, SeenUtc = x.SeenUtc }).ToList(); }

        public void Dispose() { if (_timer != null) { try { _timer.Dispose(); } catch { } _timer = null; } }
    }
}
