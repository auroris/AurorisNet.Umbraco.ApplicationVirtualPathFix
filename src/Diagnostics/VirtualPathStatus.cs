using System.Collections.Concurrent;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Diagnostics;

/// <summary>Records what the fix did at startup and what it has seen since, for the health check to report.</summary>
internal sealed class VirtualPathStatus
{
    private readonly ConcurrentQueue<string> _problems = new();
    private long _requestsOutside;
    private string? _lastRequestOutside;

    /// <summary>How many Umbraco files were patched, or null until the patched copies are prepared.</summary>
    public int? PatchedFiles { get; private set; }

    /// <summary>Changes that could not be applied, as the startup log reports them.</summary>
    public IReadOnlyCollection<string> Problems => _problems;

    /// <summary>How many requests have reached the site outside its virtual path since it started.</summary>
    public long RequestsOutside => Interlocked.Read(ref _requestsOutside);

    /// <summary>The path of the latest request outside the virtual path, or <see langword="null"/> before the first.</summary>
    public string? LastRequestOutside => Volatile.Read(ref _lastRequestOutside);

    /// <summary>Records that the patched copies are ready, with the number of files patched.</summary>
    internal void AssetsPrepared(int patchedFiles) => PatchedFiles = patchedFiles;

    /// <summary>Records a change that could not be applied, worded for the health check.</summary>
    internal void Problem(string message) => _problems.Enqueue(message);

    /// <summary>Counts a request that arrived outside the virtual path and keeps its path.</summary>
    internal void RequestOutside(PathString path)
    {
        Interlocked.Increment(ref _requestsOutside);
        Volatile.Write(ref _lastRequestOutside, path.Value);
    }
}
