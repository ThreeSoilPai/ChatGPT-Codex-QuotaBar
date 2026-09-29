using System.Text;

namespace CodexQuotaBar;

internal static class WatcherLog
{
    private const long MaximumLogBytes = 256 * 1024;
    private static readonly object Gate = new();

    internal static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexQuotaBar",
        "watcher.log");

    internal static void Write(string source, Exception exception) =>
        Write(source, $"{exception.GetType().Name}: {exception.Message}");

    internal static void Write(string source, string message)
    {
        try
        {
            lock (Gate)
            {
                var path = LogPath;
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (File.Exists(path) && new FileInfo(path).Length > MaximumLogBytes)
                {
                    File.WriteAllText(path, string.Empty, Encoding.UTF8);
                }

                var singleLine = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
                File.AppendAllText(
                    path,
                    $"{DateTimeOffset.Now:O} [{source}] {singleLine}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}
