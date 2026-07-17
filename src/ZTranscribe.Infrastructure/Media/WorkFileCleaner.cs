namespace ZTranscribe.Infrastructure.Media;

internal static class WorkFileCleaner
{
    private const int Attempts = 6;

    public static async Task<bool> DeleteFileAsync(string path)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return true;
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == Attempts - 1) return false;
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)));
            }
        }
        return !File.Exists(path);
    }

    public static async Task<bool> DeleteDirectoryAsync(string path)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            try
            {
                if (!Directory.Exists(path)) return true;
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(path, true);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == Attempts - 1) return false;
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)));
            }
        }
        return !Directory.Exists(path);
    }
}
