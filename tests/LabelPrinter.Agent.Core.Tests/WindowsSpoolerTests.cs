using System.Runtime.Versioning;
using LabelPrinter.Agent.Core.Contracts;
using LabelPrinter.Agent.Core.Erp;
using LabelPrinter.Agent.Core.Jobs;
using LabelPrinter.Agent.Core.Journal;
using LabelPrinter.Agent.Core.Spooling;
using LabelPrinter.Agent.Core.Tests.Fakes;
using LabelPrinter.Agent.Core.Tests.Harness;
using LabelPrinter.Agent.Windows;

namespace LabelPrinter.Agent.Core.Tests;

/// <summary>
/// The real Windows spooler path. Needs Windows and a print queue set up for the test whose port writes to a
/// file: LP_TEST_PRINT_QUEUE (queue name) and LP_TEST_PRINT_FILE (that port's file). No physical printer.
/// What this shows: the bytes handed to the Windows spooler are the approved bytes. It shows nothing about
/// a label printer's behaviour.
/// </summary>
public sealed class WindowsSpoolerTests
{
    private static (string Queue, string File) Target()
    {
        if (!OperatingSystem.IsWindows()) throw new SkipException("requires Windows");
        var queue = Environment.GetEnvironmentVariable("LP_TEST_PRINT_QUEUE");
        var file = Environment.GetEnvironmentVariable("LP_TEST_PRINT_FILE");
        if (string.IsNullOrEmpty(queue) || string.IsNullOrEmpty(file)) throw new SkipException("LP_TEST_PRINT_QUEUE / LP_TEST_PRINT_FILE not set: no test print queue");
        return (queue, file);
    }

    [SupportedOSPlatform("windows")]
    private static byte[] WaitForOutput(string file, int expectedLength)
    {
        for (var i = 0; i < 120; i++)
        {
            try
            {
                if (File.Exists(file))
                {
                    var bytes = File.ReadAllBytes(file);
                    if (bytes.Length == expectedLength) return bytes;
                }
            }
            catch (IOException) { /* the spooler is still writing */ }
            Thread.Sleep(250);
        }
        throw new CheckFailedException("the spooler did not produce the expected output file in 30 s");
    }

    [Test] public void RawBytesReachTheSpoolerPortUnchanged()
    {
        var (queue, file) = Target();
        if (!OperatingSystem.IsWindows()) return;
        Check.True(Winspool.PrinterNames().Contains(queue, StringComparer.OrdinalIgnoreCase), "test queue is listed");
        if (File.Exists(file)) File.Delete(file);

        // Larger than one write chunk, and containing every byte value including NUL and control codes.
        var payload = new byte[200_000];
        new Random(7).NextBytes(payload);
        var printer = Guid.NewGuid();
        var spooler = new WindowsRawSpooler(queue, printer, Console.WriteLine);
        var result = spooler.Submit(new SpoolRequest(Guid.NewGuid(), printer, PrinterLanguage.Tspl, "tspl-bitmap-v1", Transport.SpoolerRaw, payload));
        Check.Equal(SpoolOutcome.Accepted, result.Outcome, "spooler accepted");
        Check.True(int.TryParse(result.SpoolerJobRef, out var job) && job > 0, "spooler job id returned");
        Check.Equal(EnvelopeValidator.Sha256Hex(payload), EnvelopeValidator.Sha256Hex(WaitForOutput(file, payload.Length)), "bytes at the port equal the approved bytes");
    }

    [Test] public void MissingQueueAndWrongPrinterAreRejectedWithNothingWritten()
    {
        Target();
        if (!OperatingSystem.IsWindows()) return;
        var printer = Guid.NewGuid();
        var request = new SpoolRequest(Guid.NewGuid(), printer, PrinterLanguage.Tspl, "tspl-bitmap-v1", Transport.SpoolerRaw, new byte[] { 1, 2, 3 });
        var missing = new WindowsRawSpooler("No Such Queue " + Guid.NewGuid().ToString("N"), printer, _ => { }).Submit(request);
        Check.Equal(SpoolOutcome.RejectedNothingWritten, missing.Outcome, "missing queue");
        Check.Equal("PRINTER_OPEN_FAILED", missing.ErrorCode, "code");
        var (queue, _) = Target();
        var wrong = new WindowsRawSpooler(queue, Guid.NewGuid(), _ => { }).Submit(request);
        Check.Equal("PRINTER_NOT_SELECTED", wrong.ErrorCode, "a job for another printer id is not sent to this queue");
    }

    [Test] public void AgentCycleOnWindowsWithFileJournalDpapiAndRealSpooler()
    {
        var (queue, file) = Target();
        if (!OperatingSystem.IsWindows()) return;
        if (File.Exists(file)) File.Delete(file);
        var dir = Path.Combine(Path.GetTempPath(), "lp-win-" + Guid.NewGuid().ToString("N"));
        var clock = new ManualClock(Rig.T0);
        using var server = new MockErpHttpServer(new MockErp(clock));
        try
        {
            // Credential through DPAPI and a restricted ACL, as the installed agent stores it.
            var host = new AgentHost(dir);
            host.EnsureDataDir();
            var identities = host.Identities();
            var identity = new Runtime.AgentIdentity(server.Origin, Rig.Agent, identities.InstallId(), "ci", "0.1", Rig.T0, Rig.T0, 30, 0);
            identities.SavePairing(identity, server.Credential);
            Check.True(!System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(dir, "credential.bin"))).Contains(new string('s', 43), StringComparison.Ordinal), "credential file is not plain text");

            var payload = Rig.Payload(1, 30_000);
            server.Erp.Enqueue(Rig.Envelope(payload), payload);
            using var http = new HttpErpClient(server.Origin, identities.Current, new HttpErpOptions { ClaimWaitSec = 0 });

            JobLedger Open() => JobLedger.Open(new FileJournalStore(host.JournalPath), new FileBlobStore(host.PayloadDir), Rig.Agent, server.Origin, clock, Guid.NewGuid, out _);
            var spooler = new WindowsRawSpooler(queue, Rig.Printer, Console.WriteLine);
            using (var ledger = Open())
            {
                new ErpSync(ledger, http, spooler, clock).RunCycle();
                Check.Equal(JobState.SubmittedToSpooler, ledger.Find(Rig.JobId(1))!.State, "agent state");
            }
            Check.Equal("submitted_to_spooler", server.Erp.Job(Rig.JobId(1)).State, "server state");
            Check.Equal(EnvelopeValidator.Sha256Hex(payload), EnvelopeValidator.Sha256Hex(WaitForOutput(file, payload.Length)), "bytes at the port");
            var writtenAt = File.GetLastWriteTimeUtc(file);

            // Restart on NTFS: the journal is reread from disk and nothing is sent to the spooler again.
            using (var ledger = Open())
            {
                for (var i = 0; i < 3; i++) new ErpSync(ledger, http, spooler, clock).RunCycle();
                Check.Equal(JobState.SubmittedToSpooler, ledger.Find(Rig.JobId(1))!.State, "state after restart");
            }
            Check.Equal(1, spooler.Submitted.Count, "one spooler document in total");
            Thread.Sleep(1500);
            Check.Equal(writtenAt, File.GetLastWriteTimeUtc(file), "port file was not written again");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
