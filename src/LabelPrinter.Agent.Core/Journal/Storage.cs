namespace LabelPrinter.Agent.Core.Journal;

/// <summary>Exclusive handle on the journal file. Holding an instance is the single-writer guarantee.</summary>
public interface IJournalStore : IDisposable
{
    byte[] ReadAll();
    void Append(ReadOnlySpan<byte> data);
    /// <summary>Returns only after the data is as durable as the platform can make it.</summary>
    void Flush();
    void Truncate(long length);
}

/// <summary>Payload bytes of accepted, not yet submitted jobs. The journal decides which blobs are meaningful.</summary>
public interface IBlobStore
{
    void Put(Guid jobId, ReadOnlySpan<byte> data);
    byte[]? TryGet(Guid jobId);
    void Delete(Guid jobId);
    IReadOnlyList<Guid> List();
}

public sealed class JournalLockedException(string path, Exception inner)
    : IOException($"Journal '{path}' is in use by another writer.", inner);

public sealed class FileJournalStore : IJournalStore
{
    private readonly FileStream _fs;

    /// <summary>
    /// Opens with FileShare.None. On Windows that is a mandatory share-mode lock. On Unix .NET takes an advisory
    /// lock that only other cooperating (.NET) openers honour.
    /// </summary>
    public FileJournalStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try
        {
            _fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.None);
        }
        catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
        {
            throw new JournalLockedException(path, ex);
        }
    }

    public byte[] ReadAll()
    {
        _fs.Seek(0, SeekOrigin.Begin);
        var buffer = new byte[_fs.Length];
        _fs.ReadExactly(buffer);
        return buffer;
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        _fs.Seek(0, SeekOrigin.End);
        _fs.Write(data);
    }

    public void Flush() => _fs.Flush(flushToDisk: true);

    public void Truncate(long length) => _fs.SetLength(length);

    public void Dispose() => _fs.Dispose();
}

public sealed class FileBlobStore : IBlobStore
{
    private readonly string _dir;

    public FileBlobStore(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(_dir);
        foreach (var tmp in Directory.EnumerateFiles(_dir, "*.tmp")) File.Delete(tmp); // leftovers of interrupted Put
    }

    private string PathOf(Guid id) => Path.Combine(_dir, id.ToString("N") + ".bin");

    public void Put(Guid jobId, ReadOnlySpan<byte> data)
    {
        var tmp = Path.Combine(_dir, jobId.ToString("N") + ".tmp");
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(data);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, PathOf(jobId), overwrite: true);
    }

    public byte[]? TryGet(Guid jobId)
    {
        try { return File.ReadAllBytes(PathOf(jobId)); }
        catch (FileNotFoundException) { return null; }
    }

    public void Delete(Guid jobId) => File.Delete(PathOf(jobId));

    public IReadOnlyList<Guid> List() =>
        [.. Directory.EnumerateFiles(_dir, "*.bin")
            .Select(p => Guid.TryParseExact(Path.GetFileNameWithoutExtension(p), "N", out var g) ? g : (Guid?)null)
            .OfType<Guid>()];
}
