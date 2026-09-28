using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZPrint.Core.Discovery
{
    /// <summary>
    /// Information beacon broadcast by a ZPrint server node on the local network.
    /// </summary>
    public sealed class ServerBeacon
    {
        public string ServerId { get; set; } = string.Empty;
        public string MachineName { get; set; } = string.Empty;
        public string HostIp { get; set; } = string.Empty;
        public int TcpPort { get; set; }
        public int IppPort { get; set; }
        public string[] Printers { get; set; } = Array.Empty<string>();
        public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// UDP broadcast/multicast auto-discovery engine for ZPrint network nodes.
    /// Eliminates manual IP address configuration across office LANs.
    /// </summary>
    public sealed class ZPrintDiscovery : IDisposable
    {
        public const int DefaultDiscoveryPort = 9201;
        private readonly UdpClient _udpClient;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Dictionary<string, ServerBeacon> _discoveredServers = new Dictionary<string, ServerBeacon>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();
        private int _disposed;

        public event Action<ServerBeacon>? ServerDiscovered;

        public IReadOnlyList<ServerBeacon> DiscoveredServers
        {
            get
            {
                lock (_lock)
                {
                    return new List<ServerBeacon>(_discoveredServers.Values);
                }
            }
        }

        public ZPrintDiscovery(int port = DefaultDiscoveryPort)
        {
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            _udpClient.EnableBroadcast = true;

            Task.Run(ReceiveLoopAsync);
        }

        /// <summary>
        /// Broadcasts an announcement beacon to all nodes on the local subnet.
        /// </summary>
        public async Task BroadcastBeaconAsync(ServerBeacon beacon)
        {
            if (beacon == null) throw new ArgumentNullException(nameof(beacon));

            string json = $"BEACON|{beacon.ServerId}|{beacon.MachineName}|{beacon.HostIp}|{beacon.TcpPort}|{beacon.IppPort}|{string.Join(",", beacon.Printers)}";
            byte[] bytes = Encoding.UTF8.GetBytes(json);

            var endpoint = new IPEndPoint(IPAddress.Broadcast, DefaultDiscoveryPort);
            await _udpClient.SendAsync(bytes, bytes.Length, endpoint).ConfigureAwait(false);
        }

        /// <summary>
        /// Sends an inquiry ping to trigger immediate beacon responses from all active servers.
        /// </summary>
        public async Task SendPingAsync()
        {
            byte[] bytes = Encoding.UTF8.GetBytes("PING");
            var endpoint = new IPEndPoint(IPAddress.Broadcast, DefaultDiscoveryPort);
            await _udpClient.SendAsync(bytes, bytes.Length, endpoint).ConfigureAwait(false);
        }

        private async Task ReceiveLoopAsync()
        {
            var token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
#if NET8_0_OR_GREATER
                    var result = await _udpClient.ReceiveAsync(token).ConfigureAwait(false);
                    byte[] data = result.Buffer;
                    IPEndPoint remote = result.RemoteEndPoint;
#else
                    var result = await _udpClient.ReceiveAsync().ConfigureAwait(false);
                    byte[] data = result.Buffer;
                    IPEndPoint remote = result.RemoteEndPoint;
#endif
                    string text = Encoding.UTF8.GetString(data);

                    if (text.StartsWith("BEACON|", StringComparison.Ordinal))
                    {
                        var parts = text.Split('|');
                        if (parts.Length >= 7)
                        {
                            string hostIp = parts[3];
                            if (string.IsNullOrEmpty(hostIp) || hostIp == "0.0.0.0" || hostIp == "127.0.0.1")
                            {
                                hostIp = remote.Address.ToString();
                            }

                            var beacon = new ServerBeacon
                            {
                                ServerId = parts[1],
                                MachineName = parts[2],
                                HostIp = hostIp,
                                TcpPort = int.TryParse(parts[4], out int p) ? p : 9200,
                                IppPort = int.TryParse(parts[5], out int ip) ? ip : 6310,
                                Printers = parts[6].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries),
                                LastSeen = DateTimeOffset.UtcNow
                            };

                            bool isNew = false;
                            lock (_lock)
                            {
                                if (!_discoveredServers.ContainsKey(beacon.ServerId))
                                {
                                    isNew = true;
                                }
                                _discoveredServers[beacon.ServerId] = beacon;
                            }

                            if (isNew)
                            {
                                ServerDiscovered?.Invoke(beacon);
                            }
                        }
                    }
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }
                catch { }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _cts.Cancel(); } catch { }
            try { _udpClient.Close(); } catch { }
            _cts.Dispose();
        }
    }
}
