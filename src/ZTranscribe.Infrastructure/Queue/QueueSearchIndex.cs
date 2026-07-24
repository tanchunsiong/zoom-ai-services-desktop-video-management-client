using System.Collections.Concurrent;
using ZTranscribe.Core.Models;

namespace ZTranscribe.Infrastructure.Queue;

public sealed class QueueSearchIndex
{
    private const int MaxConcurrentReads = 4;
    private const long MaxCachedCharacters = 16L * 1024 * 1024;
    private readonly Dictionary<string, CachedFile> _fileCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<CachedFileKey> _cacheOrder = new();
    private readonly object _cacheGate = new();
    private long _cachedCharacters;

    public async Task<HashSet<Guid>> FindMatchesAsync(
        IReadOnlyCollection<QueueJob> jobs,
        string query,
        CancellationToken cancellationToken = default)
    {
        query = query.Trim();
        if (query.Length == 0) return jobs.Select(job => job.Id).ToHashSet();

        var matches = new ConcurrentDictionary<Guid, byte>();
        await Parallel.ForEachAsync(
            jobs,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = MaxConcurrentReads
            },
            async (job, token) =>
            {
                if (job.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    matches.TryAdd(job.Id, 0);
                    return;
                }

                foreach (var path in ArtifactPathsFor(job))
                {
                    var text = await ReadTextAsync(path, token);
                    if (text?.Contains(query, StringComparison.OrdinalIgnoreCase) != true) continue;
                    matches.TryAdd(job.Id, 0);
                    return;
                }
            });

        return matches.Keys.ToHashSet();
    }

    private async Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0) return null;

            var stamp = new FileStamp(file.Length, file.LastWriteTimeUtc.Ticks);
            lock (_cacheGate)
            {
                if (_fileCache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
                    return cached.Text;
            }

            var text = await File.ReadAllTextAsync(path, cancellationToken);
            Cache(path, stamp, text);
            return text;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private void Cache(string path, FileStamp stamp, string text)
    {
        lock (_cacheGate)
        {
            if (_fileCache.TryGetValue(path, out var previous))
                _cachedCharacters -= previous.Text.Length;

            _fileCache[path] = new CachedFile(stamp, text);
            _cacheOrder.Enqueue(new CachedFileKey(path, stamp));
            _cachedCharacters += text.Length;

            while (_cachedCharacters > MaxCachedCharacters && _cacheOrder.TryDequeue(out var oldest))
            {
                if (!_fileCache.TryGetValue(oldest.Path, out var cached) ||
                    cached.Stamp != oldest.Stamp)
                    continue;

                _fileCache.Remove(oldest.Path);
                _cachedCharacters -= cached.Text.Length;
            }
        }
    }

    private static IReadOnlyCollection<string> ArtifactPathsFor(QueueJob job)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Add(job.OriginalVttPath);
        Add(job.TranslatedVttPath);
        Add(job.TranscriptJsonPath);
        Add(job.SummaryPath);

        try
        {
            var outputs = JobQueueService.OutputPathsFor(job);
            Add(outputs.OriginalVtt);
            Add(outputs.TranscriptJson);
            Add(outputs.Summary);
            if (!string.IsNullOrWhiteSpace(job.TranslationLanguage))
                Add(outputs.TranslatedVtt(job.TranslationLanguage));

            foreach (var translated in Directory.EnumerateFiles(
                         outputs.Directory,
                         $"{outputs.Stem}.translated-*.vtt",
                         SearchOption.TopDirectoryOnly))
                Add(translated);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Explicit artifact paths above remain searchable when a directory cannot be enumerated.
        }

        return paths;

        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path)) paths.Add(path);
        }
    }

    private readonly record struct FileStamp(long Length, long LastWriteTicks);
    private readonly record struct CachedFileKey(string Path, FileStamp Stamp);
    private sealed record CachedFile(FileStamp Stamp, string Text);
}
