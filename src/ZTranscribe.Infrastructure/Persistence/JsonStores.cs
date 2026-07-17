using System.Text.Json;
using System.Text.Json.Serialization;
using ZTranscribe.Core.Models;
using ZTranscribe.Core.Services;

namespace ZTranscribe.Infrastructure.Persistence;

public sealed class AppPaths
{
    public string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Z Transcribe");
    public string QueueFile => Path.Combine(Root, "queue.json");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string WorkRoot => Path.Combine(Root, "work");
}

internal static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };
}

public sealed class JsonSettingsStore(AppPaths paths) : ISettingsStore
{
    public async Task<UserSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.SettingsFile)) return new UserSettings();
        await using var stream = File.OpenRead(paths.SettingsFile);
        return await JsonSerializer.DeserializeAsync<UserSettings>(stream, JsonDefaults.Options, cancellationToken)
               ?? new UserSettings();
    }

    public async Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(paths.Root);
        await using var stream = File.Create(paths.SettingsFile);
        await JsonSerializer.SerializeAsync(stream, settings, JsonDefaults.Options, cancellationToken);
    }
}

public sealed class JsonQueueStore(AppPaths paths) : IQueueStore
{
    public async Task<IReadOnlyList<QueueJob>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.QueueFile)) return [];
        await using var stream = File.OpenRead(paths.QueueFile);
        var jobs = await JsonSerializer.DeserializeAsync<List<QueueJob>>(stream, JsonDefaults.Options, cancellationToken) ?? [];
        foreach (var job in jobs)
        {
            if (job.TranslationLanguage == job.SourceLanguage) job.TranslationLanguage = "";
            if (job.State is JobState.Preparing or JobState.Transcribing or JobState.Translating)
                job.Report(JobState.Queued, 0, "Recovered after the application closed");
        }
        return jobs;
    }

    public async Task SaveAsync(IEnumerable<QueueJob> jobs, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(paths.Root);
        var temporary = paths.QueueFile + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, jobs, JsonDefaults.Options, cancellationToken);
        File.Move(temporary, paths.QueueFile, true);
    }
}

