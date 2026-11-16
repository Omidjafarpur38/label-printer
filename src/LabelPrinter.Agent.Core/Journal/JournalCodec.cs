using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LabelPrinter.Agent.Core.Contracts;
using LabelPrinter.Agent.Core.Jobs;

namespace LabelPrinter.Agent.Core.Journal;

/// <summary>The journal contains a record that is neither valid nor an incomplete tail. Never skipped.</summary>
public sealed class JournalCorruptException(long offset, string reason)
    : IOException($"Journal corrupt at byte {offset}: {reason}")
{
    public long Offset { get; } = offset;
}

/// <summary>One journal record. Flat on purpose: unused fields are omitted from the JSON.</summary>
public sealed class JournalEntry
{
    public const int FormatVersion = 1;

    public const string Header = "header", Accepted = "accepted", Redelivered = "redelivered",
        Transition = "transition", AcceptAcked = "accept_acked", EventsAcked = "events_acked",
        Rejected = "rejected", OutboxBlocked = "outbox_blocked", Quarantine = "quarantine", QuarantineCleared = "quarantine_cleared";

    public int V { get; set; } = FormatVersion;
    public long N { get; set; }
    public string T { get; set; } = "";
    public DateTimeOffset At { get; set; }

    public Guid? AgentId { get; set; }        // header
    public JobEnvelope? Job { get; set; }     // accepted
    public string? ContentHash { get; set; }  // accepted
    public Guid? JobId { get; set; }          // all job-scoped records except accepted/rejected
    public string? Origin { get; set; }       // header: ERP origin this journal is bound to
    public int? Attempt { get; set; }         // redelivered
    public JobState? From { get; set; }       // transition
    public JobState? To { get; set; }
    public int? Seq { get; set; }             // transition, events_acked (inclusive upper bound)
    public Guid? EventId { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? SpoolerJobRef { get; set; }
    public string? EvidenceLevel { get; set; }
    public string? EvidenceDetail { get; set; }
}

/// <summary>
/// Frame: "LPJ1" | u32 LE payload length | 4-byte header check | payload (UTF-8 JSON) | 32-byte record hash.
/// header check = first 4 bytes of SHA-256(magic | length | previous record hash).
/// record hash  = SHA-256(previous record hash | payload). The first record chains from 32 zero bytes.
/// The chain detects corruption, reordering and removal of earlier records. It is unkeyed: it does not
/// protect against someone who can rewrite the file.
/// </summary>
public static class JournalCodec
{
    public const int HeaderSize = 12, HashSize = 32, MaxPayload = 1 << 20;
    private static readonly byte[] Magic = "LPJ1"u8.ToArray();

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static byte[] Encode(JournalEntry entry, ReadOnlySpan<byte> prevHash, out byte[] hash)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(entry, Json);
        if (payload.Length > MaxPayload) throw new InvalidOperationException("journal record too large");
        var frame = new byte[HeaderSize + payload.Length + HashSize];
        Magic.CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), (uint)payload.Length);
        HeaderCheck(frame.AsSpan(0, 8), prevHash).CopyTo(frame.AsSpan(8));
        payload.CopyTo(frame, HeaderSize);
        hash = RecordHash(prevHash, payload);
        hash.CopyTo(frame, HeaderSize + payload.Length);
        return frame;
    }

    public sealed record DecodeResult(IReadOnlyList<JournalEntry> Entries, long GoodLength, long TornBytes, byte[] LastHash);

    /// <summary>
    /// Reads every complete record. A final record that is cut short (or a zero-filled tail) is reported as
    /// torn and left for the caller to truncate. Anything else that fails verification throws.
    /// </summary>
    public static DecodeResult Decode(ReadOnlySpan<byte> data)
    {
        var entries = new List<JournalEntry>();
        var prev = new byte[HashSize];
        var offset = 0;
        while (offset < data.Length)
        {
            var rest = data[offset..];
            if (rest.Length < HeaderSize) break; // header itself is incomplete
            var length = BinaryPrimitives.ReadUInt32LittleEndian(rest[4..]);
            var headerOk = rest[..4].SequenceEqual(Magic) && length <= MaxPayload
                && rest.Slice(8, 4).SequenceEqual(HeaderCheck(rest[..8], prev));
            if (!headerOk)
            {
                if (rest.IndexOfAnyExcept((byte)0) < 0) break; // space allocated but never written
                throw new JournalCorruptException(offset, "invalid record header");
            }
            var total = HeaderSize + (int)length + HashSize;
            if (rest.Length < total) break; // body or hash incomplete
            var payload = rest.Slice(HeaderSize, (int)length);
            var hash = RecordHash(prev, payload);
            if (!rest.Slice(HeaderSize + (int)length, HashSize).SequenceEqual(hash))
                throw new JournalCorruptException(offset, "record hash mismatch");

            JournalEntry? entry;
            try { entry = JsonSerializer.Deserialize<JournalEntry>(payload, Json); }
            catch (JsonException ex) { throw new JournalCorruptException(offset, "unreadable record: " + ex.Message); }
            if (entry is null) throw new JournalCorruptException(offset, "empty record");
            if (entry.V != JournalEntry.FormatVersion) throw new JournalCorruptException(offset, $"unsupported format version {entry.V}");
            if (entry.N != entries.Count) throw new JournalCorruptException(offset, $"record number {entry.N}, expected {entries.Count}");

            entries.Add(entry);
            prev = hash;
            offset += total;
        }
        return new DecodeResult(entries, offset, data.Length - offset, prev);
    }

    private static byte[] HeaderCheck(ReadOnlySpan<byte> magicAndLength, ReadOnlySpan<byte> prevHash)
    {
        Span<byte> input = stackalloc byte[8 + HashSize];
        magicAndLength.CopyTo(input);
        prevHash.CopyTo(input[8..]);
        return SHA256.HashData(input)[..4];
    }

    private static byte[] RecordHash(ReadOnlySpan<byte> prevHash, ReadOnlySpan<byte> payload)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(prevHash);
        h.AppendData(payload);
        return h.GetHashAndReset();
    }
}
