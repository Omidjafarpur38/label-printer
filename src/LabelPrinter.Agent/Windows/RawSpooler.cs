using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LabelPrinter.Agent.Core.Spooling;

namespace LabelPrinter.Agent.Windows;

[SupportedOSPlatform("windows")]
internal static class Winspool
{
    public const int JOB_STATUS_PAUSED = 0x1, JOB_STATUS_ERROR = 0x2, JOB_STATUS_DELETING = 0x4, JOB_STATUS_OFFLINE = 0x20,
        JOB_STATUS_PAPEROUT = 0x40, JOB_STATUS_PRINTED = 0x80, JOB_STATUS_DELETED = 0x100, JOB_STATUS_COMPLETE = 0x1000;
    public const int PRINTER_ENUM_LOCAL = 0x2, PRINTER_ENUM_CONNECTIONS = 0x4;
    public const int ERROR_INSUFFICIENT_BUFFER = 122, ERROR_INVALID_PARAMETER = 87;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DocInfo1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string DocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string Datatype;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PrinterInfo4 { public IntPtr PrinterName; public IntPtr ServerName; public int Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobInfo1
    {
        public int JobId; public IntPtr PrinterName, MachineName, UserName, Document, Datatype, StatusText;
        public int Status, Priority, Position, TotalPages, PagesPrinted;
        public long SubmittedLow, SubmittedHigh; // SYSTEMTIME (16 bytes), unused
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool OpenPrinter(string printerName, out IntPtr handle, IntPtr defaults);
    [DllImport("winspool.drv", SetLastError = true)] public static extern bool ClosePrinter(IntPtr handle);
    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int StartDocPrinter(IntPtr handle, int level, ref DocInfo1 doc);
    [DllImport("winspool.drv", SetLastError = true)] public static extern bool EndDocPrinter(IntPtr handle);
    [DllImport("winspool.drv", SetLastError = true)] public static extern bool StartPagePrinter(IntPtr handle);
    [DllImport("winspool.drv", SetLastError = true)] public static extern bool EndPagePrinter(IntPtr handle);
    [DllImport("winspool.drv", SetLastError = true)] public static extern bool AbortPrinter(IntPtr handle);
    [DllImport("winspool.drv", SetLastError = true)] public static extern bool WritePrinter(IntPtr handle, IntPtr buffer, int count, out int written);
    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool EnumPrinters(int flags, string? name, int level, IntPtr buffer, int size, out int needed, out int returned);
    [DllImport("winspool.drv", EntryPoint = "GetJobW", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool GetJob(IntPtr handle, int jobId, int level, IntPtr buffer, int size, out int needed);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool SetJob(IntPtr handle, int jobId, int level, IntPtr jobInfo, int command);
    public const int JOB_CONTROL_DELETE = 5;

    /// <summary>Asks the Windows spooler to delete one job. True if the spooler accepted the request.</summary>
    public static bool DeleteJob(string queue, int jobId)
    {
        if (!OpenPrinter(queue, out var handle, IntPtr.Zero)) return false;
        try { return SetJob(handle, jobId, 0, IntPtr.Zero, JOB_CONTROL_DELETE); }
        finally { ClosePrinter(handle); }
    }

    /// <summary>Queue names visible to this user. The default printer is deliberately not identified.</summary>
    public static IReadOnlyList<string> PrinterNames()
    {
        const int flags = PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS;
        EnumPrinters(flags, null, 4, IntPtr.Zero, 0, out var needed, out _);
        if (needed == 0) return [];
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!EnumPrinters(flags, null, 4, buffer, needed, out _, out var count)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var names = new List<string>();
            var size = Marshal.SizeOf<PrinterInfo4>();
            for (var i = 0; i < count; i++)
                if (Marshal.PtrToStringUni(Marshal.PtrToStructure<PrinterInfo4>(buffer + i * size).PrinterName) is { } n) names.Add(n);
            return names;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Status flags of a spooler job, or null when the spooler no longer lists it.</summary>
    public static int? JobStatus(string queue, int jobId)
    {
        if (!OpenPrinter(queue, out var handle, IntPtr.Zero)) return null;
        try
        {
            GetJob(handle, jobId, 1, IntPtr.Zero, 0, out var needed);
            if (needed == 0) return null;
            var buffer = Marshal.AllocHGlobal(needed);
            try { return GetJob(handle, jobId, 1, buffer, needed, out _) ? Marshal.PtrToStructure<JobInfo1>(buffer).Status : null; }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { ClosePrinter(handle); }
    }
}

/// <summary>
/// Sends the approved bytes, unchanged and once, as one RAW document to one named Windows print queue.
/// Outcome rules:
/// - queue cannot be opened, or the document cannot be started → RejectedNothingWritten;
/// - every byte written and the document closed → Accepted (with the spooler job id);
/// - anything that fails after the first successful write, or a short write that cannot be completed →
///   Indeterminate (the job becomes Unknown; it is never retried here);
/// - a failure before any byte was written is RejectedNothingWritten only if the document could be aborted.
/// "Accepted" means the Windows spooler holds the job. It does not mean a label came out.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsRawSpooler(string queue, Guid erpPrinterId, Action<string> log) : ISpooler
{
    private const int Chunk = 64 * 1024;

    /// <summary>Spooler job ids of documents submitted by this process, for completion watching.</summary>
    public Dictionary<Guid, int> Submitted { get; } = [];

    public SpoolResult Submit(SpoolRequest request)
    {
        if (request.PrinterId != erpPrinterId) return new SpoolResult(SpoolOutcome.RejectedNothingWritten, ErrorCode: "PRINTER_NOT_SELECTED");
        if (request.Payload.Length == 0) return new SpoolResult(SpoolOutcome.RejectedNothingWritten, ErrorCode: "EMPTY_PAYLOAD");

        if (!Winspool.OpenPrinter(queue, out var printer, IntPtr.Zero))
            return Fail(SpoolOutcome.RejectedNothingWritten, "PRINTER_OPEN_FAILED");

        var written = 0;
        var docStarted = false;
        try
        {
            var doc = new Winspool.DocInfo1 { DocName = "LabelPrinterAgent " + request.JobId.ToString("D"), OutputFile = null, Datatype = "RAW" };
            var spoolJob = Winspool.StartDocPrinter(printer, 1, ref doc);
            if (spoolJob == 0) return Fail(SpoolOutcome.RejectedNothingWritten, "SPOOLER_STARTDOC_FAILED");
            docStarted = true;
            if (!Winspool.StartPagePrinter(printer)) return AbortBeforeWrite(printer, "SPOOLER_STARTPAGE_FAILED");

            using var pin = request.Payload.Pin();
            unsafe
            {
                var start = (IntPtr)pin.Pointer;
                while (written < request.Payload.Length)
                {
                    var count = Math.Min(Chunk, request.Payload.Length - written);
                    if (!Winspool.WritePrinter(printer, start + written, count, out var done) || done <= 0)
                        return written == 0 ? AbortBeforeWrite(printer, "SPOOLER_WRITE_FAILED") : Fail(SpoolOutcome.Indeterminate, "SPOOLER_PARTIAL_WRITE");
                    written += done; // a short write is continued from where it stopped
                }
            }

            if (!Winspool.EndPagePrinter(printer)) return Fail(SpoolOutcome.Indeterminate, "SPOOLER_ENDPAGE_FAILED");
            docStarted = false;
            if (!Winspool.EndDocPrinter(printer)) return Fail(SpoolOutcome.Indeterminate, "SPOOLER_ENDDOC_FAILED");
            Submitted[request.JobId] = spoolJob;
            return new SpoolResult(SpoolOutcome.Accepted, spoolJob.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            if (docStarted && written > 0) Winspool.EndDocPrinter(printer); // release the handle state; outcome is already Indeterminate
            Winspool.ClosePrinter(printer);
        }
    }

    private SpoolResult AbortBeforeWrite(IntPtr printer, string code) =>
        Winspool.AbortPrinter(printer) ? Fail(SpoolOutcome.RejectedNothingWritten, code) : Fail(SpoolOutcome.Indeterminate, code + "_ABORT_FAILED");

    private SpoolResult Fail(SpoolOutcome outcome, string code)
    {
        log($"spooler: {code} (win32 {Marshal.GetLastWin32Error()}) → {outcome}");
        return new SpoolResult(outcome, ErrorCode: code);
    }
}
