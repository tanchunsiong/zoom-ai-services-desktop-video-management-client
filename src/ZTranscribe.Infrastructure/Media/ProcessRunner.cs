using System.Diagnostics;
using System.Text;

namespace ZTranscribe.Infrastructure.Media;

internal static class ProcessRunner
{
    public static async Task<string> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        Action<string>? onError,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            error.AppendLine(e.Data);
            onError?.Invoke(e.Data);
        };

        try
        {
            if (!process.Start()) throw new InvalidOperationException($"Could not start {executable}");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException($"Could not find '{executable}'. Configure its path in Settings.", exception);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(executable)} failed: {LastUsefulLine(error.ToString())}");
        return output.ToString();
    }

    private static string LastUsefulLine(string error) =>
        error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "Unknown process error";
}

