using System;
using System.Collections.Generic;
using ZPrint.Core.Models;

namespace ZPrint.Core.Spooler
{
    /// <summary>
    /// Abstraction for low-level operating system print spooling services.
    /// </summary>
    public interface IPrinterSpooler
    {
        /// <summary>
        /// Enumerates all local and network printers configured on this host.
        /// </summary>
        IReadOnlyList<PrinterInfo> GetPrinters();

        /// <summary>
        /// Retrieves the current status and properties of a specific printer.
        /// </summary>
        PrinterInfo? GetPrinter(string printerName);

        /// <summary>
        /// Sends raw binary document data directly into the printer spooler.
        /// </summary>
        /// <param name="printerName">Name of the target printer.</param>
        /// <param name="documentName">Title of the print job.</param>
        /// <param name="rawData">Raw binary bytes (PCL, PostScript, CAPT, EMF, or PDF).</param>
        /// <param name="dataType">Spool data type (typically "RAW").</param>
        /// <returns>True if the job was successfully spooled; otherwise false.</returns>
        bool PrintRaw(string printerName, string documentName, ReadOnlySpan<byte> rawData, string dataType = "RAW");
    }
}
