using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZPrint.Core.Models;
using ZPrint.Core.Protocol;
using ZPrint.Core.Queue;
using ZPrint.Core.Spooler;

namespace ZPrint.Core.Network
{
    /// <summary>
    /// High-throughput TCP server coordinating print job reception, ZeroLz4 decompression, and spooler injection.
    /// </summary>
    public sealed class ZPrintServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly PrintJobQueue _queue;
        private readonly IPrinterSpooler _spooler;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private int _boundPort;
        private int _disposed;

        public int Port => _boundPort > 0 ? _boundPort : ((IPEndPoint)_listener.LocalEndpoint).Port;
        public bool IsRunning => _listener.Server.IsBound;

        public ZPrintServer(int port, PrintJobQueue queue, IPrinterSpooler spooler, string host = "0.0.0.0")
        {
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _spooler = spooler ?? throw new ArgumentNullException(nameof(spooler));

            IPAddress ip = host == "0.0.0.0" ? IPAddress.Any : IPAddress.Parse(host);
            _listener = new TcpListener(ip, port);
        }

        public void Start()
        {
            _listener.Start();
            _boundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Task.Run(AcceptLoopAsync);
        }

        public void Stop()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
        }

        private async Task AcceptLoopAsync()
        {
            var token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    client.NoDelay = true;
                    _ = Task.Run(() => HandleClientAsync(client, token), token);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }
                catch { }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken token)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                byte[] lengthBuffer = new byte[13]; // Header length
                while (!token.IsCancellationRequested && client.Connected)
                {
                    if (!await ReadExactAsync(stream, lengthBuffer, 0, 13, token).ConfigureAwait(false))
                        break;

                    ushort metaLen = (ushort)((lengthBuffer[7] << 8) | lengthBuffer[8]);
                    int payloadLen = (lengthBuffer[9] << 24) | (lengthBuffer[10] << 16) | (lengthBuffer[11] << 8) | lengthBuffer[12];

                    int remaining = metaLen + payloadLen;
                    byte[] fullPacket = new byte[13 + remaining];
                    Array.Copy(lengthBuffer, 0, fullPacket, 0, 13);

                    if (remaining > 0)
                    {
                        if (!await ReadExactAsync(stream, fullPacket, 13, remaining, token).ConfigureAwait(false))
                            break;
                    }

                    if (ZPrintProtocol.TryDecodePacket(fullPacket, out var pType, out var metaJson, out var payload, out _))
                    {
                        await ProcessPacketAsync(stream, pType, metaJson, payload, token).ConfigureAwait(false);
                    }
                }
            }
        }

        private async Task ProcessPacketAsync(NetworkStream stream, ZPrintPacketType type, string metaJson, byte[] payload, CancellationToken token)
        {
            switch (type)
            {
                case ZPrintPacketType.DiscoverPing:
                    var printers = _spooler.GetPrinters();
                    var sb = new StringBuilder();
                    for (int i = 0; i < printers.Count; i++)
                    {
                        if (i > 0) sb.Append(",");
                        sb.Append(printers[i].Name);
                    }
                    byte[] pong = ZPrintProtocol.EncodePacket(ZPrintPacketType.DiscoverPong, sb.ToString(), Array.Empty<byte>());
                    await stream.WriteAsync(pong, 0, pong.Length, token).ConfigureAwait(false);
                    break;

                case ZPrintPacketType.SubmitJobRequest:
                    string jobId = Guid.NewGuid().ToString("N");
                    string targetPrinter = metaJson; // Printer name in metadata

                    var job = new PrintJob
                    {
                        JobId = jobId,
                        DocumentName = $"RemoteJob_{jobId.Substring(0, 8)}",
                        TargetPrinter = targetPrinter,
                        Data = payload,
                        Format = DocumentFormat.Raw,
                        Status = JobStatus.Queued
                    };

                    bool enqueued = _queue.EnqueueJob(job);
                    string replyMeta = enqueued ? $"OK|{jobId}" : "ERR|QueueFull";

                    byte[] resp = ZPrintProtocol.EncodePacket(ZPrintPacketType.SubmitJobResponse, replyMeta, Array.Empty<byte>());
                    await stream.WriteAsync(resp, 0, resp.Length, token).ConfigureAwait(false);
                    break;

                case ZPrintPacketType.JobStatusQuery:
                    string queryId = metaJson;
                    var found = _queue.GetJob(queryId);
                    string statusReply = found != null ? found.Status.ToString() : "NotFound";

                    byte[] statusPkt = ZPrintProtocol.EncodePacket(ZPrintPacketType.JobStatusUpdate, statusReply, Array.Empty<byte>());
                    await stream.WriteAsync(statusPkt, 0, statusPkt.Length, token).ConfigureAwait(false);
                    break;
            }
        }

        private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken token)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int r = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead, token).ConfigureAwait(false);
                if (r <= 0) return false;
                totalRead += r;
            }
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
            _cts.Dispose();
        }
    }
}
