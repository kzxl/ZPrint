using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ZPrint.Core.Models;

namespace ZPrint.Core.Spooler
{
    /// <summary>
    /// In-memory mock print spooler for automated testing and non-Windows platform fallback.
    /// </summary>
    public sealed class MockSpooler : IPrinterSpooler
    {
        private readonly ConcurrentDictionary<string, PrinterInfo> _printers = new ConcurrentDictionary<string, PrinterInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string Printer, string Document, byte[] Data)> _spooledJobs = new List<(string, string, byte[])>();
        private readonly object _lock = new object();

        public IReadOnlyList<(string Printer, string Document, byte[] Data)> SpooledJobs
        {
            get { lock (_lock) return _spooledJobs.ToArray(); }
        }

        public MockSpooler()
        {
            AddPrinter(new PrinterInfo
            {
                Name = "Canon LBP2900 (Mock)",
                DriverName = "Canon LBP2900 CAPT",
                PortName = "USB001",
                IsDefault = true,
                Status = PrinterStatus.Ready
            });
        }

        public void AddPrinter(PrinterInfo printer)
        {
            _printers[printer.Name] = printer;
        }

        public IReadOnlyList<PrinterInfo> GetPrinters()
        {
            return _printers.Values.ToList();
        }

        public PrinterInfo? GetPrinter(string printerName)
        {
            _printers.TryGetValue(printerName, out var p);
            return p;
        }

        public bool PrintRaw(string printerName, string documentName, ReadOnlySpan<byte> rawData, string dataType = "RAW")
        {
            if (!_printers.ContainsKey(printerName))
                return false;

            lock (_lock)
            {
                _spooledJobs.Add((printerName, documentName, rawData.ToArray()));
            }
            return true;
        }

        public void ClearHistory()
        {
            lock (_lock)
            {
                _spooledJobs.Clear();
            }
        }
    }
}
