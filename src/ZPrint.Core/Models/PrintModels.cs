using System;

namespace ZPrint.Core.Models
{
    /// <summary>
    /// Document format delivered in the print job payload.
    /// </summary>
    public enum DocumentFormat : byte
    {
        Raw = 0,
        Pdf = 1,
        Xps = 2,
        Emf = 3,
        PostScript = 4,
        Text = 5,
        ImagePng = 6,
        ImageJpeg = 7
    }

    /// <summary>
    /// Print orientation mode.
    /// </summary>
    public enum PrintOrientation : byte
    {
        Portrait = 0,
        Landscape = 1
    }

    /// <summary>
    /// Standard paper sizes.
    /// </summary>
    public enum PaperSize : byte
    {
        A4 = 0,
        A5 = 1,
        A3 = 2,
        Letter = 3,
        Legal = 4,
        Custom = 5
    }

    /// <summary>
    /// Lifecycle status of a print job.
    /// </summary>
    public enum JobStatus : byte
    {
        Queued = 0,
        Rendering = 1,
        Printing = 2,
        Completed = 3,
        Faulted = 4,
        Cancelled = 5
    }

    /// <summary>
    /// Hardware/Driver operational status of a printer.
    /// </summary>
    [Flags]
    public enum PrinterStatus : uint
    {
        Ready = 0,
        Paused = 0x00000001,
        Error = 0x00000002,
        PendingDeletion = 0x00000004,
        PaperJam = 0x00000008,
        PaperOut = 0x00000010,
        ManualFeed = 0x00000020,
        PaperProblem = 0x00000040,
        Offline = 0x00000080,
        IOActive = 0x00000100,
        Busy = 0x00000200,
        Printing = 0x00000400,
        OutputBinFull = 0x00000800,
        NotAvailable = 0x00001000,
        Waiting = 0x00002000,
        Processing = 0x00004000,
        Initializing = 0x00008000,
        WarmingUp = 0x00010000,
        TonerLow = 0x00020000,
        NoToner = 0x00040000,
        PagePunt = 0x00080000,
        UserIntervention = 0x00100000,
        OutOfMemory = 0x00200000,
        DoorOpen = 0x00400000,
        ServerUnknown = 0x00800000,
        PowerSave = 0x01000000
    }

    /// <summary>
    /// Metadata descriptor for an available printer on a ZPrint node.
    /// </summary>
    public sealed class PrinterInfo
    {
        public string Name { get; set; } = string.Empty;
        public string DriverName { get; set; } = string.Empty;
        public string PortName { get; set; } = string.Empty;
        public bool IsDefault { get; set; }
        public PrinterStatus Status { get; set; } = PrinterStatus.Ready;
        public int QueuedJobsCount { get; set; }
        public string ServerHost { get; set; } = string.Empty;
        public int ServerPort { get; set; }

        public override string ToString() => $"{Name} ({DriverName} on {PortName}) - {Status}";
    }

    /// <summary>
    /// Represents a discrete print job dispatched from client to server node.
    /// </summary>
    public sealed class PrintJob
    {
        public string JobId { get; set; } = Guid.NewGuid().ToString("N");
        public string DocumentName { get; set; } = "Untitled Document";
        public string TargetPrinter { get; set; } = string.Empty;
        public string ClientMachineName { get; set; } = Environment.MachineName;
        public string ClientUserName { get; set; } = Environment.UserName;
        public int Copies { get; set; } = 1;
        public string PageRange { get; set; } = "all";
        public PrintOrientation Orientation { get; set; } = PrintOrientation.Portrait;
        public PaperSize PaperSize { get; set; } = PaperSize.A4;
        public DocumentFormat Format { get; set; } = DocumentFormat.Raw;
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public DateTimeOffset SubmittedAt { get; set; } = DateTimeOffset.UtcNow;
        public JobStatus Status { get; set; } = JobStatus.Queued;
        public string? ErrorMessage { get; set; }
        public int TotalPages { get; set; } = 1;
        public int CurrentPage { get; set; } = 0;
    }
}
