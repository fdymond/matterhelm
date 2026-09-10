namespace MatterHelm.Infrastructure;

/// <summary>Abstracts the directory operations needed to stage and clean up Matter storage.</summary>
internal interface IStorageDirectoryOperations
{
    bool Exists(string path);

    void Move(string source, string destination);

    void Delete(string path, bool recursive);

    IEnumerable<string> EnumerateDirectories(string path, string searchPattern);
}

/// <summary>Performs Matter-storage staging and cleanup with the system directory APIs.</summary>
internal sealed class StorageDirectoryOperations : IStorageDirectoryOperations
{
    public bool Exists(string path) => Directory.Exists(path);

    public void Move(string source, string destination) => Directory.Move(source, destination);

    public void Delete(string path, bool recursive) => Directory.Delete(path, recursive);

    public IEnumerable<string> EnumerateDirectories(string path, string searchPattern) =>
        Directory.EnumerateDirectories(path, searchPattern, SearchOption.TopDirectoryOnly);
}

/// <summary>Reports whether storage staging completed and whether best-effort cleanup left residue.</summary>
internal readonly record struct MatterStorageResetResult(
    bool Completed,
    string? Error,
    string? ResiduePath);

/// <summary>Atomically detaches live Matter storage before best-effort deletion.</summary>
internal sealed class MatterStorageResetter
{
    private static readonly TimeSpan RetryWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly IStorageDirectoryOperations _directories;
    private readonly TimeProvider _timeProvider;
    private readonly Action<TimeSpan> _delay;
    private readonly Action<string, string> _log;

    internal MatterStorageResetter(
        Action<string, string> log,
        IStorageDirectoryOperations? directories = null,
        TimeProvider? timeProvider = null,
        Action<TimeSpan>? delay = null)
    {
        _log = log;
        _directories = directories ?? new StorageDirectoryOperations();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _delay = delay ?? (duration => Thread.Sleep(duration));
    }

    internal MatterStorageResetResult Reset(string directory)
    {
        string livePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        SweepStaleResidue(livePath);
        if (!_directories.Exists(livePath))
        {
            return new MatterStorageResetResult(true, null, null);
        }

        string stagedPath = livePath + ".reset-" + Guid.NewGuid().ToString("N");
        string? error = Retry(() => _directories.Move(livePath, stagedPath));
        if (error is not null)
        {
            return new MatterStorageResetResult(false, error, null);
        }

        error = Retry(() => _directories.Delete(stagedPath, recursive: true));
        if (error is not null)
        {
            _log(
                "WARN",
                $"bridge: factory reset completed but cleanup failed; residue left at '{stagedPath}' ({error}).");
            return new MatterStorageResetResult(true, null, stagedPath);
        }

        return new MatterStorageResetResult(true, null, null);
    }

    internal void SweepStaleResidue(string directory)
    {
        string livePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string? parent = Path.GetDirectoryName(livePath);
        if (string.IsNullOrEmpty(parent))
        {
            return;
        }

        try
        {
            string pattern = Path.GetFileName(livePath) + ".reset-*";
            foreach (string stalePath in _directories.EnumerateDirectories(parent, pattern))
            {
                try
                {
                    _directories.Delete(stalePath, recursive: true);
                    _log("DEBUG", $"bridge: removed stale Matter reset residue at '{stalePath}'.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log("DEBUG", $"bridge: stale Matter reset residue at '{stalePath}' remains ({ex.Message}).");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log("DEBUG", $"bridge: could not enumerate stale Matter reset residue beside '{livePath}' ({ex.Message}).");
        }
    }

    private string? Retry(Action operation)
    {
        long startedAt = _timeProvider.GetTimestamp();
        while (true)
        {
            try
            {
                operation();
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (_timeProvider.GetElapsedTime(startedAt) >= RetryWindow)
                {
                    return ex.Message;
                }

                _delay(RetryDelay);
            }
        }
    }
}
