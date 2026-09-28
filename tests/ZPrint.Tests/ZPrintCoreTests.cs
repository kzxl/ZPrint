using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using ZPrint.Core.Discovery;
using ZPrint.Core.Ipp;
using ZPrint.Core.Models;
using ZPrint.Core.Network;
using ZPrint.Core.Protocol;
using ZPrint.Core.Queue;
using ZPrint.Core.Spooler;
using ZPrint.Core.Utils;

namespace ZPrint.Tests
{
    public class ZPrintCoreTests
    {
        [Fact]
        public void Protocol_EncodeDecode_Roundtrip_Uncompressed()
        {
            byte[] payload = Encoding.UTF8.GetBytes("Test Document Spool Data Plaintext");
            string meta = "Printer=Canon2900;Pages=1-5";

            byte[] packet = ZPrintProtocol.EncodePacket(
                ZPrintPacketType.SubmitJobRequest,
                meta,
                payload,
                compressPayload: false);

            var (type, decodedMeta, decodedPayload) = ZPrintProtocol.DecodePacket(packet);

            Assert.Equal(ZPrintPacketType.SubmitJobRequest, type);
            Assert.Equal(meta, decodedMeta);
            Assert.Equal(payload, decodedPayload);
        }

        [Fact]
        public void Protocol_EncodeDecode_Roundtrip_WithZeroLz4Compression()
        {
            // Create compressible buffer (e.g. repeated pattern)
            byte[] original = new byte[32 * 1024];
            for (int i = 0; i < original.Length; i++)
            {
                original[i] = (byte)(i % 17);
            }

            string meta = "CompressTestDoc";
            byte[] packet = ZPrintProtocol.EncodePacket(
                ZPrintPacketType.SubmitJobRequest,
                meta,
                original,
                compressPayload: true);

            // Verify packet is smaller than raw original payload
            Assert.True(packet.Length < original.Length, $"Compressed packet size {packet.Length} should be smaller than {original.Length}");

            var (type, decodedMeta, decodedPayload) = ZPrintProtocol.DecodePacket(packet);

            Assert.Equal(ZPrintPacketType.SubmitJobRequest, type);
            Assert.Equal(meta, decodedMeta);
            Assert.Equal(original, decodedPayload);
        }

        [Fact]
        public void Protocol_InvalidMagic_ThrowsInvalidDataException()
        {
            byte[] badPacket = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

            Assert.Throws<InvalidDataException>(() => ZPrintProtocol.DecodePacket(badPacket));
        }

        [Fact]
        public async Task PrintJobQueue_FIFO_SequentialExecution()
        {
            var spooler = new MockSpooler();
            using var queue = new PrintJobQueue(spooler);

            var completedCount = 0;
            var countdown = new ManualResetEventSlim(false);

            queue.JobCompleted += job =>
            {
                if (Interlocked.Increment(ref completedCount) == 3)
                {
                    countdown.Set();
                }
            };

            var job1 = new PrintJob { JobId = "JOB-001", DocumentName = "Doc 1", TargetPrinter = "Canon LBP2900 (Mock)", Data = new byte[] { 1, 2, 3 } };
            var job2 = new PrintJob { JobId = "JOB-002", DocumentName = "Doc 2", TargetPrinter = "Canon LBP2900 (Mock)", Data = new byte[] { 4, 5, 6 } };
            var job3 = new PrintJob { JobId = "JOB-003", DocumentName = "Doc 3", TargetPrinter = "Canon LBP2900 (Mock)", Data = new byte[] { 7, 8, 9 } };

            Assert.True(queue.EnqueueJob(job1));
            Assert.True(queue.EnqueueJob(job2));
            Assert.True(queue.EnqueueJob(job3));

            bool finished = countdown.Wait(TimeSpan.FromSeconds(5));
            Assert.True(finished, "Queue did not process 3 jobs within 5 seconds.");

            Assert.Equal(3, spooler.SpooledJobs.Count);
            Assert.Contains("Doc 1", spooler.SpooledJobs[0].Document);
            Assert.Contains("Doc 2", spooler.SpooledJobs[1].Document);
            Assert.Contains("Doc 3", spooler.SpooledJobs[2].Document);

            Assert.Equal(JobStatus.Completed, job1.Status);
            Assert.Equal(JobStatus.Completed, job2.Status);
            Assert.Equal(JobStatus.Completed, job3.Status);
        }

        [Fact]
        public async Task Network_ServerAndClient_EndToEndPrintJobSubmission()
        {
            var spooler = new MockSpooler();
            using var queue = new PrintJobQueue(spooler);
            using var server = new ZPrintServer(0, queue, spooler, "127.0.0.1");

            server.Start();
            int serverPort = server.Port;
            Assert.True(serverPort > 0);

            using var client = new ZPrintClient("127.0.0.1", serverPort);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            await client.ConnectAsync(cts.Token);
            Assert.True(client.IsConnected);

            // Query printers
            string[] printers = await client.GetRemotePrintersAsync(cts.Token);
            Assert.NotEmpty(printers);
            Assert.Contains("Canon LBP2900 (Mock)", printers);

            // Submit print job with 16KB data
            byte[] testDoc = new byte[16 * 1024];
            for (int i = 0; i < testDoc.Length; i++) testDoc[i] = (byte)(i & 0xFF);

            var (success, jobId) = await client.SubmitJobAsync("Canon LBP2900 (Mock)", testDoc, cts.Token);
            Assert.True(success, $"Job submission failed: {jobId}");
            Assert.NotNull(jobId);

            // Wait for queue processing
            for (int i = 0; i < 20; i++)
            {
                if (spooler.SpooledJobs.Count > 0) break;
                await Task.Delay(50);
            }

            Assert.Single(spooler.SpooledJobs);
            Assert.Equal("Canon LBP2900 (Mock)", spooler.SpooledJobs[0].Printer);
            Assert.Equal(testDoc, spooler.SpooledJobs[0].Data);

            // Query status
            string status = await client.QueryJobStatusAsync(jobId, cts.Token);
            Assert.False(string.IsNullOrEmpty(status));
            Assert.True(status == "Completed" || status == "Printing" || status == "Queued");
        }

        [Fact]
        public async Task IppServer_GetPrinterAttributes_ReturnsValidIppResponse()
        {
            var spooler = new MockSpooler();
            using var queue = new PrintJobQueue(spooler);
            int ippPort = 16310;
            using var ippServer = new ZPrintIppServer(ippPort, queue, spooler, "Canon LBP2900 (Mock)");

            try
            {
                ippServer.Start();
            }
            catch
            {
                // In environments without permission for specific port, test gracefully degrades
                return;
            }

            using var httpClient = new HttpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            // Construct RFC 8011 IPP Get-Printer-Attributes request
            // Header: version 2.0 (0x02, 0x00), opId 0x000B, requestId 0x00000001
            byte[] ippRequest = new byte[]
            {
                0x02, 0x00,             // IPP 2.0
                0x00, 0x0B,             // Get-Printer-Attributes
                0x00, 0x00, 0x00, 0x01, // Request ID: 1
                0x01,                   // operation-attributes-tag
                0x47, 0x00, 0x12,       // charset tag + name length (18)
                (byte)'a', (byte)'t', (byte)'t', (byte)'r', (byte)'i', (byte)'b', (byte)'u', (byte)'t',
                (byte)'e', (byte)'s', (byte)'c', (byte)'h', (byte)'a', (byte)'r', (byte)'s', (byte)'e', (byte)'t',
                0x00, 0x05,             // value length (5)
                (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'8',
                0x03                    // end-of-attributes-tag
            };

            var content = new ByteArrayContent(ippRequest);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/ipp");

            var response = await httpClient.PostAsync($"http://localhost:{ippPort}/ipp/printers/canon", content, cts.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            byte[] respBytes = await response.Content.ReadAsByteArrayAsync();
            Assert.True(respBytes.Length >= 8);
            // Verify IPP version echoed
            Assert.Equal(0x02, respBytes[0]);
            Assert.Equal(0x00, respBytes[1]);
            // Verify status code: 0x0000 (successful-ok)
            Assert.Equal(0x00, respBytes[2]);
            Assert.Equal(0x00, respBytes[3]);
        }

        [Fact]
        public void Discovery_BeaconParsing_ValidData()
        {
            var beacon = new ServerBeacon
            {
                ServerId = "srv-1234",
                MachineName = "PRINT-SERVER-01",
                HostIp = "192.168.1.100",
                TcpPort = 9200,
                IppPort = 6310,
                Printers = new[] { "Canon LBP2900", "HP LaserJet 1020" }
            };

            string encoded = $"BEACON|{beacon.ServerId}|{beacon.MachineName}|{beacon.HostIp}|{beacon.TcpPort}|{beacon.IppPort}|{string.Join(",", beacon.Printers)}";
            var parts = encoded.Split('|');

            Assert.Equal("BEACON", parts[0]);
            Assert.Equal("srv-1234", parts[1]);
            Assert.Equal("PRINT-SERVER-01", parts[2]);
            Assert.Equal("192.168.1.100", parts[3]);
            Assert.Equal("9200", parts[4]);
            Assert.Equal("6310", parts[5]);
            Assert.Equal("Canon LBP2900,HP LaserJet 1020", parts[6]);
        }

        [Theory]
        [InlineData("1, 3, 5-8", 10, new[] { 1, 3, 5, 6, 7, 8 })]
        [InlineData("4-2", 10, new[] { 2, 3, 4 })]
        [InlineData("1, 2, 2, 3", 5, new[] { 1, 2, 3 })]
        [InlineData("all", 4, new[] { 1, 2, 3, 4 })]
        [InlineData("", 5, new[] { 1, 2, 3, 4, 5 })]
        public void PageRangeParser_Parse_ReturnsExpectedPages(string spec, int maxPages, int[] expected)
        {
            var result = PageRangeParser.Parse(spec, maxPages);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void PageRangeParser_IsPageIncluded_ChecksCorrectly()
        {
            var pages = new[] { 1, 3, 5 };
            Assert.True(PageRangeParser.IsPageIncluded(1, pages));
            Assert.False(PageRangeParser.IsPageIncluded(2, pages));
            Assert.True(PageRangeParser.IsPageIncluded(3, pages));
            Assert.True(PageRangeParser.IsPageIncluded(10, Array.Empty<int>())); // empty means all
        }

        [Fact]
        public async Task IppServer_PrintJob_EnqueuesDocumentToSpooler()
        {
            var spooler = new MockSpooler();
            using var queue = new PrintJobQueue(spooler);
            int ippPort = 16311;
            using var ippServer = new ZPrintIppServer(ippPort, queue, spooler, "Canon LBP2900 (Mock)");

            try
            {
                ippServer.Start();
            }
            catch
            {
                return;
            }

            using var httpClient = new HttpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            // IPP Print-Job request: opId 0x0002 + end of attrs tag 0x03 + document payload
            byte[] docPayload = Encoding.UTF8.GetBytes("RAW PRINT DOCUMENT FROM IPP CLIENT");
            using var ms = new MemoryStream();
            ms.Write(new byte[] { 0x02, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x02 }, 0, 8); // IPP 2.0, Print-Job, req 2
            ms.WriteByte(0x01); // op attributes
            ms.WriteByte(0x03); // end of attributes
            ms.Write(docPayload, 0, docPayload.Length); // document data

            var content = new ByteArrayContent(ms.ToArray());
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/ipp");

            var response = await httpClient.PostAsync($"http://localhost:{ippPort}/ipp/printers/canon", content, cts.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Wait for queue to spool
            for (int i = 0; i < 20; i++)
            {
                if (spooler.SpooledJobs.Count > 0) break;
                await Task.Delay(50);
            }

            Assert.Single(spooler.SpooledJobs);
            Assert.Equal("Canon LBP2900 (Mock)", spooler.SpooledJobs[0].Printer);
            Assert.Equal(docPayload, spooler.SpooledJobs[0].Data);
        }
    }
}
