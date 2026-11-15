using System.Collections.Concurrent;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>What the fix did at startup and has seen since, which its health check reports.</summary>
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

    public string? LastRequestOutside => Volatile.Read(ref _lastRequestOutside);

    internal void AssetsPrepared(int patchedFiles) => PatchedFiles = patchedFiles;

    internal void Problem(string message) => _problems.Enqueue(message);

    internal void RequestOutside(PathString path)
    {
        Interlocked.Increment(ref _requestsOutside);
        Volatile.Write(ref _lastRequestOutside, path.Value);
    }
}
