using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZPrint.Core.Discovery;
using ZPrint.Core.Ipp;
using ZPrint.Core.Models;
using ZPrint.Core.Network;
using ZPrint.Core.Queue;
using ZPrint.Core.Spooler;
using ZPrint.Core.Utils;

namespace ZPrint.Tool
{
    public static class Program
    {
        private const string Version = "1.0.0";

        public static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            if (args.Length == 0)
            {
                PrintBanner();
                PrintUsage();
                return 0;
            }

            string command = args[0].ToLowerInvariant().TrimStart('-');

            // Support drag-and-drop file directly onto zprint.exe
            if (File.Exists(args[0]) && !IsKnownCommand(command))
            {
                return await ExecutePrintAsync(new[] { "print" }.Concat(args).ToArray());
            }

            switch (command)
            {
                case "server":
                case "serve":
                case "s":
                    return await ExecuteServerAsync(args);

                case "print":
                case "p":
                    return await ExecutePrintAsync(args);

                case "list":
                case "discover":
                case "l":
                    return await ExecuteListAsync(args);

                case "install-printer":
                case "setup":
                    return ExecuteInstallPrinterGuide(args);

                case "help":
                case "h":
                case "?":
                    PrintBanner();
                    PrintUsage();
                    return 0;

                default:
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[ERROR] Unknown command: '{args[0]}'");
                    Console.ResetColor();
                    PrintUsage();
                    return 1;
            }
        }

        private static bool IsKnownCommand(string cmd)
        {
            return cmd is "server" or "serve" or "s" or "print" or "p" or "list" or "discover" or "l" or "install-printer" or "setup" or "help" or "h";
        }

        #region Server Command

        private static async Task<int> ExecuteServerAsync(string[] args)
        {
            PrintBanner();

            int tcpPort = 9200;
            int ippPort = 6310;
            string? targetPrinter = null;
            bool enableIpp = true;
            bool enableDiscovery = true;

            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.Equals("--port", StringComparison.OrdinalIgnoreCase) || arg.Equals("-p", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int p)) tcpPort = p;
                }
                else if (arg.Equals("--ipp", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int ip)) ippPort = ip;
                }
                else if (arg.Equals("--printer", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) targetPrinter = args[++i];
                }
                else if (arg.Equals("--no-ipp", StringComparison.OrdinalIgnoreCase))
                {
                    enableIpp = false;
                }
                else if (arg.Equals("--no-discovery", StringComparison.OrdinalIgnoreCase))
                {
                    enableDiscovery = false;
                }
            }

            IPrinterSpooler spooler;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                spooler = new WindowsSpooler();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[WARN] Non-Windows OS detected: Using MockSpooler.");
                Console.ResetColor();
                spooler = new MockSpooler();
            }

            var printers = spooler.GetPrinters();
            if (printers.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[WARN] No physical printers detected. Adding Virtual Fallback Printer.");
                Console.ResetColor();
                if (spooler is MockSpooler mock)
                {
                    mock.AddPrinter(new PrinterInfo { Name = "ZPrint-Virtual", IsDefault = true, Status = PrinterStatus.Ready });
                }
                printers = spooler.GetPrinters();
            }

            // Determine target printer
            PrinterInfo? activePrinter = null;
            if (!string.IsNullOrEmpty(targetPrinter))
            {
                activePrinter = printers.FirstOrDefault(p => p.Name.Equals(targetPrinter, StringComparison.OrdinalIgnoreCase));
            }
            activePrinter ??= printers.FirstOrDefault(p => p.IsDefault) ?? printers.FirstOrDefault();

            string defaultPrinterName = activePrinter?.Name ?? "Default-Printer";

            Console.WriteLine($"[INIT] Server Host: {Environment.MachineName}");
            Console.WriteLine($"[INIT] Spooler Engine: {spooler.GetType().Name}");
            Console.WriteLine($"[INIT] Default Target Printer: '{defaultPrinterName}'");
            Console.WriteLine();
            Console.WriteLine("Available Local Printers:");
            foreach (var p in printers)
            {
                string defMarker = p.IsDefault ? " (Default)" : "";
                Console.WriteLine($"  * {p.Name}{defMarker} [{p.Status}]");
            }
            Console.WriteLine();

            using var queue = new PrintJobQueue(spooler);

            queue.JobEnqueued += job =>
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[QUEUE] Job Enqueued: {job.JobId} | Doc: {job.DocumentName} | Printer: {job.TargetPrinter} | Size: {job.Data.Length:N0} bytes");
                Console.ResetColor();
            };

            queue.JobStarted += job =>
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[SPOOL] Job Started: {job.JobId} -> Spooling to '{job.TargetPrinter}'...");
                Console.ResetColor();
            };

            queue.JobCompleted += job =>
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[SUCCESS] Job Completed: {job.JobId} printed successfully.");
                Console.ResetColor();
            };

            queue.JobFaulted += (job, ex) =>
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ERROR] Job Faulted: {job.JobId} failed: {ex.Message}");
                Console.ResetColor();
            };

            // Start TCP server
            using var tcpServer = new ZPrintServer(tcpPort, queue, spooler);
            tcpServer.Start();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[READY] ZPrint Fast TCP Server listening on port {tcpPort} (ZeroLz4 active)");
            Console.ResetColor();

            // Start IPP server
            ZPrintIppServer? ippServer = null;
            if (enableIpp)
            {
                try
                {
                    ippServer = new ZPrintIppServer(ippPort, queue, spooler, defaultPrinterName);
                    ippServer.Start();
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[READY] Driverless IPP / AirPrint Server listening on port {ippPort}");
                    Console.WriteLine($"        Universal IPP Endpoint: http://localhost:{ippPort}/ipp/");
                    Console.ResetColor();
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[WARN] Could not bind IPP port {ippPort} ({ex.Message}). Continuing without IPP.");
                    Console.ResetColor();
                }
            }

            // Start UDP Discovery beacon broadcaster
            ZPrintDiscovery? discovery = null;
            CancellationTokenSource? beaconCts = null;
            if (enableDiscovery)
            {
                try
                {
                    discovery = new ZPrintDiscovery();
                    beaconCts = new CancellationTokenSource();
                    var localIp = GetPrimaryLocalIpAddress();

                    var beacon = new ServerBeacon
                    {
                        ServerId = Guid.NewGuid().ToString("N").Substring(0, 8),
                        MachineName = Environment.MachineName,
                        HostIp = localIp,
                        TcpPort = tcpPort,
                        IppPort = enableIpp ? ippPort : 0,
                        Printers = printers.Select(p => p.Name).ToArray()
                    };

                    _ = Task.Run(async () =>
                    {
                        while (!beaconCts.Token.IsCancellationRequested)
                        {
                            try
                            {
                                await discovery.BroadcastBeaconAsync(beacon).ConfigureAwait(false);
                            }
                            catch { }
                            await Task.Delay(5000, beaconCts.Token).ConfigureAwait(false);
                        }
                    }, beaconCts.Token);

                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[READY] UDP Auto-Discovery beacon broadcasting on port {ZPrintDiscovery.DefaultDiscoveryPort}");
                    Console.ResetColor();
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[WARN] UDP Discovery failed to bind: {ex.Message}");
                    Console.ResetColor();
                }
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("================================================================================");
            Console.WriteLine(" ZPrint Server is RUNNING. Press [Ctrl+C] to safely shutdown.");
            Console.WriteLine("================================================================================");
            Console.ResetColor();

            var exitEvent = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("\n[SHUTDOWN] Stopping services...");
                exitEvent.Set();
            };

            exitEvent.Wait();

            beaconCts?.Cancel();
            discovery?.Dispose();
            ippServer?.Dispose();
            tcpServer.Dispose();

            Console.WriteLine("[SHUTDOWN] Server stopped safely.");
            return 0;
        }

        #endregion

        #region Print Command

        private static async Task<int> ExecutePrintAsync(string[] args)
        {
            if (args.Length < 2)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[ERROR] Missing file path. Usage: zprint print <filePath> [options]");
                Console.ResetColor();
                return 1;
            }

            string filePath = args[1];
            if (!File.Exists(filePath))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ERROR] File not found: '{filePath}'");
                Console.ResetColor();
                return 1;
            }

            string? serverHost = null;
            int serverPort = 9200;
            string? printerName = null;
            string? pagesSpec = null;
            int copies = 1;

            for (int i = 2; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.Equals("--server", StringComparison.OrdinalIgnoreCase) || arg.Equals("-s", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                    {
                        string hostArg = args[++i];
                        if (hostArg.Contains(":"))
                        {
                            var parts = hostArg.Split(':');
                            serverHost = parts[0];
                            if (int.TryParse(parts[1], out int p)) serverPort = p;
                        }
                        else
                        {
                            serverHost = hostArg;
                        }
                    }
                }
                else if (arg.Equals("--printer", StringComparison.OrdinalIgnoreCase) || arg.Equals("-p", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) printerName = args[++i];
                }
                else if (arg.Equals("--pages", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) pagesSpec = args[++i];
                }
                else if (arg.Equals("--copies", StringComparison.OrdinalIgnoreCase) || arg.Equals("-c", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int c)) copies = Math.Max(1, c);
                }
            }

            // Auto-discover server if not provided
            if (string.IsNullOrEmpty(serverHost))
            {
                Console.WriteLine("[DISCOVERY] Searching for active ZPrint servers on LAN...");
                var discovered = await DiscoverServersAsync(1500);
                if (discovered.Count == 0)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[ERROR] No ZPrint server discovered on LAN. Please specify --server <host:port>.");
                    Console.ResetColor();
                    return 1;
                }

                var targetServer = discovered[0];
                serverHost = targetServer.HostIp;
                serverPort = targetServer.TcpPort > 0 ? targetServer.TcpPort : 9200;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[DISCOVERY] Selected server '{targetServer.MachineName}' at {serverHost}:{serverPort}");
                Console.ResetColor();

                if (string.IsNullOrEmpty(printerName) && targetServer.Printers.Length > 0)
                {
                    printerName = targetServer.Printers[0];
                }
            }

            byte[] fileBytes = await File.ReadAllBytesAsync(filePath);
            var fileInfo = new FileInfo(filePath);

            Console.WriteLine($"[FILE] '{fileInfo.Name}' ({fileBytes.Length:N0} bytes)");
            if (!string.IsNullOrEmpty(pagesSpec))
            {
                var selectedPages = PageRangeParser.Parse(pagesSpec);
                Console.WriteLine($"[PAGE-FILTER] Selected pages: {string.Join(", ", selectedPages)}");
            }

            using var client = new ZPrintClient(serverHost, serverPort);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            Console.WriteLine($"[CONNECT] Connecting to ZPrint server {serverHost}:{serverPort}...");
            try
            {
                await client.ConnectAsync(cts.Token);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ERROR] Failed to connect to server: {ex.Message}");
                Console.ResetColor();
                return 1;
            }

            if (string.IsNullOrEmpty(printerName))
            {
                var remotePrinters = await client.GetRemotePrintersAsync(cts.Token);
                if (remotePrinters.Length > 0)
                {
                    printerName = remotePrinters[0];
                }
                else
                {
                    printerName = "Default";
                }
            }

            Console.WriteLine($"[SUBMIT] Submitting job to printer '{printerName}' (ZeroLz4 compressed)...");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            for (int copy = 1; copy <= copies; copy++)
            {
                string copySuffix = copies > 1 ? $" (Copy {copy}/{copies})" : "";
                var (success, jobId) = await client.SubmitJobAsync(printerName, fileBytes, cts.Token);

                if (!success)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[FAILED] Job submission failed: {jobId}");
                    Console.ResetColor();
                    return 1;
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[ACCEPTED] Job ID: {jobId}{copySuffix}");
                Console.ResetColor();

                // Poll status
                while (true)
                {
                    await Task.Delay(300);
                    string status = await client.QueryJobStatusAsync(jobId, cts.Token);
                    if (status.Equals("Completed", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"[DONE] Print job {jobId} completed successfully! ({sw.ElapsedMilliseconds} ms)");
                        Console.ResetColor();
                        break;
                    }
                    else if (status.StartsWith("Failed", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"[FAILED] Print job {jobId} failed on spooler: {status}");
                        Console.ResetColor();
                        return 1;
                    }
                }
            }

            return 0;
        }

        #endregion

        #region List Command

        private static async Task<int> ExecuteListAsync(string[] args)
        {
            PrintBanner();
            Console.WriteLine("[DISCOVERY] Scanning local network for ZPrint servers (UDP 9201)...");
            Console.WriteLine();

            var servers = await DiscoverServersAsync(2000);

            if (servers.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("No ZPrint servers found on the current local network.");
                Console.WriteLine("Ensure a ZPrint host is running: `zprint server`");
                Console.ResetColor();
                return 0;
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(string.Format("{0,-18} {1,-16} {2,-8} {3,-8} {4}", "Machine Name", "IP Address", "TCP Port", "IPP Port", "Shared Printers"));
            Console.WriteLine(new string('-', 78));
            Console.ResetColor();

            foreach (var s in servers)
            {
                string printerList = string.Join(", ", s.Printers);
                if (string.IsNullOrEmpty(printerList)) printerList = "(None)";
                Console.WriteLine(string.Format("{0,-18} {1,-16} {2,-8} {3,-8} {4}", s.MachineName, s.HostIp, s.TcpPort, s.IppPort, printerList));
            }

            Console.WriteLine();
            return 0;
        }

        private static async Task<List<ServerBeacon>> DiscoverServersAsync(int waitMs)
        {
            var results = new List<ServerBeacon>();
            try
            {
                using var discovery = new ZPrintDiscovery();
                discovery.ServerDiscovered += beacon =>
                {
                    lock (results)
                    {
                        if (!results.Any(r => r.HostIp == beacon.HostIp && r.TcpPort == beacon.TcpPort))
                        {
                            results.Add(beacon);
                        }
                    }
                };

                await discovery.SendPingAsync().ConfigureAwait(false);
                await Task.Delay(waitMs).ConfigureAwait(false);

                // In case any were already collected in discovery cache
                foreach (var b in discovery.DiscoveredServers)
                {
                    lock (results)
                    {
                        if (!results.Any(r => r.HostIp == b.HostIp && r.TcpPort == b.TcpPort))
                        {
                            results.Add(b);
                        }
                    }
                }
            }
            catch { }

            return results;
        }

        #endregion

        #region Install Guide Command

        private static int ExecuteInstallPrinterGuide(string[] args)
        {
            PrintBanner();
            string serverIp = "<SERVER-IP>";
            int ippPort = 6310;

            for (int i = 1; i < args.Length; i++)
            {
                if (args[i].Equals("--server", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    serverIp = args[++i];
                else if (args[i].Equals("--port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[++i], out int p))
                    ippPort = p;
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("================================================================================");
            Console.WriteLine(" Option 1: Transparent Virtual Driverless Printer Setup Guide");
            Console.WriteLine("================================================================================");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("With ZPrint RFC 8011 IPP, client devices require ZERO vendor drivers!");
            Console.WriteLine();

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("--- [WINDOWS CLIENT] ---");
            Console.ResetColor();
            Console.WriteLine("1. Open Settings -> Bluetooth & devices -> Printers & scanners");
            Console.WriteLine("2. Click 'Add device' -> 'Add manually'");
            Console.WriteLine($"3. Select 'Select a shared printer by name' and enter:");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"   http://{serverIp}:{ippPort}/ipp/printers/canon");
            Console.ResetColor();
            Console.WriteLine("4. Select 'Microsoft IPP Class Driver' (built into Windows 10 & 11).");
            Console.WriteLine("5. Done! You can now print transparently from Word, Excel, Chrome using Ctrl+P.");
            Console.WriteLine();

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("--- [MACOS & IOS AIRPRINT] ---");
            Console.ResetColor();
            Console.WriteLine("1. macOS System Settings -> Printers & Scanners -> Add Printer");
            Console.WriteLine($"2. Choose 'IP' tab:");
            Console.WriteLine($"   - Address: {serverIp}:{ippPort}");
            Console.WriteLine($"   - Protocol: Internet Printing Protocol - IPP");
            Console.WriteLine($"   - Queue: /ipp/");
            Console.WriteLine($"   - Use: Generic PostScript or IPP Everywhere");
            Console.WriteLine();

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("--- [LINUX CUPS] ---");
            Console.ResetColor();
            Console.WriteLine("Run terminal command:");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"   lpadmin -p ZPrint -E -v ipp://{serverIp}:{ippPort}/ipp/ -m everywhere");
            Console.ResetColor();
            Console.WriteLine();

            return 0;
        }

        #endregion

        #region Helpers

        private static void PrintBanner()
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(@"  ███████╗██████╗ ██████╗ ██╗███╗   ██╗████████╗");
            Console.WriteLine(@"  ╚══███╔╝██╔══██╗██╔══██╗██║████╗  ██║╚══██╔══╝");
            Console.WriteLine(@"    ███╔╝ ██████╔╝██████╔╝██║██╔██╗ ██║   ██║   ");
            Console.WriteLine(@"   ███╔╝  ██╔═══╝ ██╔══██╗██║██║╚██╗██║   ██║   ");
            Console.WriteLine(@"  ███████╗██║     ██║  ██║██║██║ ╚████║   ██║   ");
            Console.WriteLine(@"  ╚══════╝╚═╝     ╚═╝  ╚═╝╚═╝╚═╝  ╚═══╝   ╚═╝   ");
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine($"  Universal LAN Zero-Config Print Bridge v{Version}");
            Console.WriteLine("  Copyright (c) 2026 ZeroUniverse. All rights reserved.\n");
            Console.ResetColor();
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage: zprint <command> [options]");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  server           Starts host print server (TCP + IPP + UDP discovery)");
            Console.WriteLine("                   Options: --port <9200> --ipp <6310> --printer <name> [--no-ipp]");
            Console.WriteLine();
            Console.WriteLine("  print <file>     Submits a document file to remote printer");
            Console.WriteLine("                   Options: --server <ip[:port]> --printer <name> --pages <range> --copies <n>");
            Console.WriteLine("                   (Drag & drop file directly onto zprint.exe also supported)");
            Console.WriteLine();
            Console.WriteLine("  list             Scans and lists active ZPrint servers and shared printers on LAN");
            Console.WriteLine();
            Console.WriteLine("  install-printer  Displays setup instructions for driverless virtual printer");
            Console.WriteLine("                   Options: --server <ip> --port <6310>");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  zprint server --printer \"Canon LBP2900\"");
            Console.WriteLine("  zprint print contract.pdf --pages \"1, 3, 5-8\" --copies 2");
            Console.WriteLine("  zprint list");
            Console.WriteLine();
        }

        private static string GetPrimaryLocalIpAddress()
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                socket.Connect("8.8.8.8", 65530);
                if (socket.LocalEndPoint is IPEndPoint endPoint)
                {
                    return endPoint.Address.ToString();
                }
            }
            catch { }

            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    {
                        return ip.ToString();
                    }
                }
            }
            catch { }

            return "127.0.0.1";
        }

        #endregion
    }
}
