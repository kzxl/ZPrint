using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ZPrint.Core.Models;
using ZPrint.Core.Protocol;

namespace ZPrint.Core.Network
{
    /// <summary>
    /// Lightweight network print client responsible for ZeroLz4 payload compression and TCP transmission to ZPrintServer.
    /// </summary>
    public sealed class ZPrintClient : IDisposable
    {
        private readonly string _host;
        private readonly int _port;
        private TcpClient? _tcpClient;
        private NetworkStream? _stream;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private int _disposed;

        public bool IsConnected => _tcpClient != null && _tcpClient.Connected;

        public ZPrintClient(string host, int port)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _port = port;
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (IsConnected) return;

            _tcpClient = new TcpClient();
            _tcpClient.NoDelay = true;
            await _tcpClient.ConnectAsync(_host, _port).ConfigureAwait(false);
            _stream = _tcpClient.GetStream();
        }

        /// <summary>
        /// Retrieves the list of physical/local printers available on the remote server.
        /// </summary>
        public async Task<string[]> GetRemotePrintersAsync(CancellationToken cancellationToken = default)
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                byte[] pingPkt = ZPrintProtocol.EncodePacket(ZPrintPacketType.DiscoverPing, string.Empty, Array.Empty<byte>());
                await _stream!.WriteAsync(pingPkt, 0, pingPkt.Length, cancellationToken).ConfigureAwait(false);

                var (pType, meta, _) = await ReadPacketAsync(_stream, cancellationToken).ConfigureAwait(false);
                if (pType == ZPrintPacketType.DiscoverPong && !string.IsNullOrEmpty(meta))
                {
                    return meta.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                }
                return Array.Empty<string>();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Compresses the document payload with ZeroLz4 and submits the print job to the remote server.
        /// </summary>
        public async Task<(bool Success, string JobIdOrError)> SubmitJobAsync(string printerName, ReadOnlyMemory<byte> rawDocumentData, CancellationToken cancellationToken = default)
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                byte[] requestPkt = ZPrintProtocol.EncodePacket(ZPrintPacketType.SubmitJobRequest, printerName, rawDocumentData.Span, compressPayload: true);
                await _stream!.WriteAsync(requestPkt, 0, requestPkt.Length, cancellationToken).ConfigureAwait(false);

                var (pType, meta, _) = await ReadPacketAsync(_stream, cancellationToken).ConfigureAwait(false);
                if (pType == ZPrintPacketType.SubmitJobResponse)
                {
                    if (meta.StartsWith("OK|", StringComparison.Ordinal))
                    {
                        return (true, meta.Substring(3));
                    }
                    return (false, meta);
                }
                return (false, "Unexpected response from print server.");
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Queries the real-time execution status of a submitted print job.
        /// </summary>
        public async Task<string> QueryJobStatusAsync(string jobId, CancellationToken cancellationToken = default)
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                byte[] queryPkt = ZPrintProtocol.EncodePacket(ZPrintPacketType.JobStatusQuery, jobId, Array.Empty<byte>());
                await _stream!.WriteAsync(queryPkt, 0, queryPkt.Length, cancellationToken).ConfigureAwait(false);

                var (pType, meta, _) = await ReadPacketAsync(_stream, cancellationToken).ConfigureAwait(false);
                return meta;
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task EnsureConnectedAsync(CancellationToken token)
        {
            if (!IsConnected)
            {
                await ConnectAsync(token).ConfigureAwait(false);
            }
        }

        private static async Task<(ZPrintPacketType Type, string Meta, byte[] Payload)> ReadPacketAsync(NetworkStream stream, CancellationToken token)
        {
            byte[] lengthBuffer = new byte[13];
            int totalRead = 0;
            while (totalRead < 13)
            {
                int r = await stream.ReadAsync(lengthBuffer, totalRead, 13 - totalRead, token).ConfigureAwait(false);
                if (r <= 0) throw new EndOfStreamException("Connection terminated prematurely.");
                totalRead += r;
            }

            ushort metaLen = (ushort)((lengthBuffer[7] << 8) | lengthBuffer[8]);
            int payloadLen = (lengthBuffer[9] << 24) | (lengthBuffer[10] << 16) | (lengthBuffer[11] << 8) | lengthBuffer[12];

            int remaining = metaLen + payloadLen;
            byte[] fullPacket = new byte[13 + remaining];
            Array.Copy(lengthBuffer, 0, fullPacket, 0, 13);

            totalRead = 0;
            while (totalRead < remaining)
            {
                int r = await stream.ReadAsync(fullPacket, 13 + totalRead, remaining - totalRead, token).ConfigureAwait(false);
                if (r <= 0) throw new EndOfStreamException("Connection terminated prematurely.");
                totalRead += r;
            }

            if (ZPrintProtocol.TryDecodePacket(fullPacket, out var pType, out var meta, out var payload, out _))
            {
                return (pType, meta, payload);
            }

            throw new InvalidDataException("Failed to decode ZPrint packet.");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _stream?.Dispose(); } catch { }
            try { _tcpClient?.Close(); } catch { }
            _lock.Dispose();
        }
    }
}
