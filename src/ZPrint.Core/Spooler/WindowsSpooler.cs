using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using ZPrint.Core.Models;

namespace ZPrint.Core.Spooler
{
    /// <summary>
    /// Direct Win32 Print Spooler implementation interacting via winspool.drv.
    /// Provides low-level RAW injection bypassing Windows RPC SMB network sharing errors.
    /// </summary>
    public sealed class WindowsSpooler : IPrinterSpooler
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct DOCINFOA
        {
            [MarshalAs(UnmanagedType.LPStr)]
            public string pDocName;
            [MarshalAs(UnmanagedType.LPStr)]
            public string? pOutputFile;
            [MarshalAs(UnmanagedType.LPStr)]
            public string pDataType;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PRINTER_INFO_2
        {
            public string? pServerName;
            public string? pPrinterName;
            public string? pShareName;
            public string? pPortName;
            public string? pDriverName;
            public string? pComment;
            public string? pLocation;
            public IntPtr pDevMode;
            public string? pSepFile;
            public string? pPrintProcessor;
            public string? pDatatype;
            public string? pParameters;
            public IntPtr pSecurityDescriptor;
            public uint Attributes;
            public uint Priority;
            public uint DefaultPriority;
            public uint StartTime;
            public uint UntilTime;
            public uint Status;
            public uint cJobs;
            public uint AveragePPM;
        }

        private const int PRINTER_ENUM_LOCAL = 0x00000002;
        private const int PRINTER_ENUM_CONNECTIONS = 0x00000004;
        private const uint PRINTER_ATTRIBUTE_DEFAULT = 0x00000004;

        [DllImport("winspool.drv", EntryPoint = "OpenPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        private static extern bool OpenPrinter([MarshalAs(UnmanagedType.LPStr)] string szPrinter, out IntPtr hPrinter, IntPtr pd);

        [DllImport("winspool.drv", EntryPoint = "ClosePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        private static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "StartDocPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        private static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In] ref DOCINFOA di);

        [DllImport("winspool.drv", EntryPoint = "EndDocPrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        private static extern bool EndDocPrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "StartPagePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        private static extern bool StartPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "EndPagePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        private static extern bool EndPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "WritePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

        [DllImport("winspool.drv", EntryPoint = "EnumPrintersA", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern bool EnumPrinters(int flags, string? name, int level, IntPtr pPrinterEnum, int cbBuf, out int pcbNeeded, out int pcReturned);

        public IReadOnlyList<PrinterInfo> GetPrinters()
        {
            var results = new List<PrinterInfo>();
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                return results;
            }

            int flags = PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS;
            int cbNeeded = 0;
            int cReturned = 0;

            EnumPrinters(flags, null, 2, IntPtr.Zero, 0, out cbNeeded, out cReturned);
            if (cbNeeded == 0) return results;

            IntPtr pAddr = Marshal.AllocHGlobal(cbNeeded);
            try
            {
                if (EnumPrinters(flags, null, 2, pAddr, cbNeeded, out cbNeeded, out cReturned))
                {
                    IntPtr pCurrent = pAddr;
                    for (int i = 0; i < cReturned; i++)
                    {
                        var info = Marshal.PtrToStructure<PRINTER_INFO_2>(pCurrent);
                        results.Add(new PrinterInfo
                        {
                            Name = info.pPrinterName ?? string.Empty,
                            DriverName = info.pDriverName ?? string.Empty,
                            PortName = info.pPortName ?? string.Empty,
                            IsDefault = (info.Attributes & PRINTER_ATTRIBUTE_DEFAULT) != 0,
                            Status = (PrinterStatus)info.Status,
                            QueuedJobsCount = (int)info.cJobs
                        });
                        pCurrent = new IntPtr(pCurrent.ToInt64() + Marshal.SizeOf(typeof(PRINTER_INFO_2)));
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pAddr);
            }

            return results;
        }

        public PrinterInfo? GetPrinter(string printerName)
        {
            var printers = GetPrinters();
            for (int i = 0; i < printers.Count; i++)
            {
                if (string.Equals(printers[i].Name, printerName, StringComparison.OrdinalIgnoreCase))
                {
                    return printers[i];
                }
            }
            return null;
        }

        public bool PrintRaw(string printerName, string documentName, ReadOnlySpan<byte> rawData, string dataType = "RAW")
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                throw new PlatformNotSupportedException("WindowsSpooler requires Windows OS.");
            }

            if (string.IsNullOrEmpty(printerName)) throw new ArgumentNullException(nameof(printerName));
            if (rawData.IsEmpty) return false;

            if (!OpenPrinter(printerName, out IntPtr hPrinter, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to open printer '{printerName}'.");
            }

            try
            {
                var di = new DOCINFOA
                {
                    pDocName = documentName,
                    pDataType = dataType
                };

                if (!StartDocPrinter(hPrinter, 1, ref di))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "StartDocPrinter failed.");
                }

                try
                {
                    if (!StartPagePrinter(hPrinter))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "StartPagePrinter failed.");
                    }

                    try
                    {
                        unsafe
                        {
                            fixed (byte* p = rawData)
                            {
                                int totalWritten = 0;
                                while (totalWritten < rawData.Length)
                                {
                                    int chunkSize = Math.Min(rawData.Length - totalWritten, 65536);
                                    if (!WritePrinter(hPrinter, (IntPtr)(p + totalWritten), chunkSize, out int written) || written <= 0)
                                    {
                                        throw new Win32Exception(Marshal.GetLastWin32Error(), "WritePrinter failed during spooling.");
                                    }
                                    totalWritten += written;
                                }
                            }
                        }
                    }
                    finally
                    {
                        EndPagePrinter(hPrinter);
                    }
                }
                finally
                {
                    EndDocPrinter(hPrinter);
                }

                return true;
            }
            finally
            {
                ClosePrinter(hPrinter);
            }
        }
    }
}
