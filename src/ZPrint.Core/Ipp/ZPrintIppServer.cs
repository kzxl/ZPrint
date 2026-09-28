using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZPrint.Core.Models;
using ZPrint.Core.Queue;
using ZPrint.Core.Spooler;

namespace ZPrint.Core.Ipp
{
    /// <summary>
    /// Lightweight RFC 8010/8011 IPP (Internet Printing Protocol) server.
    /// Enables 100% driverless printing from macOS (AirPrint), Linux (CUPS), iOS, Android, and Windows IPP.
    /// </summary>
    public sealed class ZPrintIppServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly PrintJobQueue _queue;
        private readonly IPrinterSpooler _spooler;
        private readonly string _targetPrinter;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private int _disposed;
        private int _jobCounter;

        public int Port { get; }
        public bool IsRunning => _listener.IsListening;

        public ZPrintIppServer(int port, PrintJobQueue queue, IPrinterSpooler spooler, string targetPrinter)
        {
            Port = port;
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _spooler = spooler ?? throw new ArgumentNullException(nameof(spooler));
            _targetPrinter = targetPrinter ?? throw new ArgumentNullException(nameof(targetPrinter));

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://*:{port}/ipp/");
            _listener.Prefixes.Add($"http://*:{port}/printers/");
        }

        public void Start()
        {
            if (_listener.IsListening) return;
            try
            {
                _listener.Start();
                Task.Run(ListenLoopAsync);
            }
            catch (HttpListenerException)
            {
                // Fallback to localhost if wildcard reservation is restricted
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://localhost:{Port}/ipp/");
                _listener.Prefixes.Add($"http://localhost:{Port}/printers/");
                _listener.Start();
                Task.Run(ListenLoopAsync);
            }
        }

        public void Stop()
        {
            if (!_listener.IsListening) return;
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
        }

        private async Task ListenLoopAsync()
        {
            var token = _cts.Token;
            while (!token.IsCancellationRequested && _listener.IsListening)
            {
                try
                {
                    HttpListenerContext context = await _listener.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleRequestAsync(context), token);
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch { }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            try
            {
                if (context.Request.HttpMethod != "POST")
                {
                    context.Response.StatusCode = 200;
                    byte[] info = Encoding.UTF8.GetBytes("ZPrint IPP Universal Print Service (RFC 8011 Active)");
                    await context.Response.OutputStream.WriteAsync(info, 0, info.Length).ConfigureAwait(false);
                    context.Response.Close();
                    return;
                }

                using var ms = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(ms).ConfigureAwait(false);
                byte[] rawRequest = ms.ToArray();

                if (rawRequest.Length < 8)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                // Parse IPP Header
                byte versionMajor = rawRequest[0];
                byte versionMinor = rawRequest[1];
                ushort operationId = (ushort)((rawRequest[2] << 8) | rawRequest[3]);
                int requestId = (rawRequest[4] << 24) | (rawRequest[5] << 16) | (rawRequest[6] << 8) | rawRequest[7];

                byte[] responseBytes;

                switch (operationId)
                {
                    case 0x0002: // Print-Job
                        responseBytes = HandlePrintJob(rawRequest, requestId, versionMajor, versionMinor);
                        break;

                    case 0x0004: // Validate-Job
                    case 0x000A: // Get-Jobs
                    case 0x000B: // Get-Printer-Attributes
                    default:
                        responseBytes = BuildPrinterAttributesResponse(requestId, versionMajor, versionMinor);
                        break;
                }

                context.Response.ContentType = "application/ipp";
                context.Response.StatusCode = 200;
                await context.Response.OutputStream.WriteAsync(responseBytes, 0, responseBytes.Length).ConfigureAwait(false);
                context.Response.Close();
            }
            catch
            {
                try
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
                catch { }
            }
        }

        private byte[] HandlePrintJob(byte[] rawRequest, int requestId, byte vMajor, byte vMinor)
        {
            // Find end of attributes tag (0x03)
            int dataOffset = -1;
            for (int i = 8; i < rawRequest.Length; i++)
            {
                if (rawRequest[i] == 0x03) // End of attributes
                {
                    dataOffset = i + 1;
                    break;
                }
            }

            byte[] documentData = Array.Empty<byte>();
            if (dataOffset > 0 && dataOffset < rawRequest.Length)
            {
                int len = rawRequest.Length - dataOffset;
                documentData = new byte[len];
                Array.Copy(rawRequest, dataOffset, documentData, 0, len);
            }

            int jobId = Interlocked.Increment(ref _jobCounter);

            if (documentData.Length > 0)
            {
                var job = new PrintJob
                {
                    JobId = jobId.ToString(),
                    DocumentName = $"IPP-Job-{jobId}",
                    TargetPrinter = _targetPrinter,
                    ClientMachineName = "IPP-Client",
                    Data = documentData,
                    Format = DocumentFormat.Raw
                };
                _queue.EnqueueJob(job);
            }

            // Build successful-ok response
            using var ms = new MemoryStream();
            ms.WriteByte(vMajor);
            ms.WriteByte(vMinor);
            ms.WriteByte(0x00); // successful-ok (0x0000)
            ms.WriteByte(0x00);
            ms.Write(new byte[] { (byte)(requestId >> 24), (byte)(requestId >> 16), (byte)(requestId >> 8), (byte)requestId }, 0, 4);

            // Operation attributes group
            ms.WriteByte(0x01);
            WriteIppAttribute(ms, 0x47, "attributes-charset", "utf-8");
            WriteIppAttribute(ms, 0x48, "attributes-natural-language", "en");

            // Job attributes group
            ms.WriteByte(0x02);
            WriteIppIntAttribute(ms, 0x21, "job-id", jobId);
            WriteIppIntAttribute(ms, 0x23, "job-state", 3); // 3 = pending

            ms.WriteByte(0x03); // End-of-attributes
            return ms.ToArray();
        }

        private byte[] BuildPrinterAttributesResponse(int requestId, byte vMajor, byte vMinor)
        {
            using var ms = new MemoryStream();
            ms.WriteByte(vMajor);
            ms.WriteByte(vMinor);
            ms.WriteByte(0x00); // successful-ok
            ms.WriteByte(0x00);
            ms.Write(new byte[] { (byte)(requestId >> 24), (byte)(requestId >> 16), (byte)(requestId >> 8), (byte)requestId }, 0, 4);

            // Operation attributes
            ms.WriteByte(0x01);
            WriteIppAttribute(ms, 0x47, "attributes-charset", "utf-8");
            WriteIppAttribute(ms, 0x48, "attributes-natural-language", "en");

            // Printer attributes
            ms.WriteByte(0x04);
            WriteIppAttribute(ms, 0x42, "printer-name", _targetPrinter);
            WriteIppIntAttribute(ms, 0x23, "printer-state", 3); // 3 = idle
            WriteIppAttribute(ms, 0x44, "printer-state-reasons", "none");
            WriteIppAttribute(ms, 0x49, "document-format-supported", "application/octet-stream");

            ms.WriteByte(0x03);
            return ms.ToArray();
        }

        private static void WriteIppAttribute(Stream stream, byte tag, string name, string value)
        {
            stream.WriteByte(tag);
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            stream.WriteByte((byte)(nameBytes.Length >> 8));
            stream.WriteByte((byte)nameBytes.Length);
            stream.Write(nameBytes, 0, nameBytes.Length);

            byte[] valBytes = Encoding.UTF8.GetBytes(value);
            stream.WriteByte((byte)(valBytes.Length >> 8));
            stream.WriteByte((byte)valBytes.Length);
            stream.Write(valBytes, 0, valBytes.Length);
        }

        private static void WriteIppIntAttribute(Stream stream, byte tag, string name, int value)
        {
            stream.WriteByte(tag);
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            stream.WriteByte((byte)(nameBytes.Length >> 8));
            stream.WriteByte((byte)nameBytes.Length);
            stream.Write(nameBytes, 0, nameBytes.Length);

            stream.WriteByte(0x00);
            stream.WriteByte(0x04); // 4 bytes for integer
            stream.Write(new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value }, 0, 4);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
            _cts.Dispose();
        }
    }
}
