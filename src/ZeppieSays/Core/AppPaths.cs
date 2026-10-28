using System.IO;

namespace ZeppieSays.Core;

public static class AppPaths
{
    /// <summary>This PC only: models, logs, recordings in progress, API keys and the pointer to <see cref="Settings"/>.</summary>
    public static string Data { get; } = Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZeppieSays"));

    public static string Models { get; } = Ensure(Path.Combine(Data, "models"));
    public static string Logs { get; } = Ensure(Path.Combine(Data, "logs"));

    /// <summary>
    /// Where the user's own data lives (settings, voices, stats): the local data folder unless the user picked
    /// another one, such as a OneDrive folder, so it survives a reinstall and can follow them to another PC.
    /// </summary>
    public static string Settings { get; } = ReadSettingsFolder();

    /// <summary>The files that belong in <see cref="Settings"/>; a new kind of user data is added here.</summary>
    public static readonly string[] SettingsFiles = ["settings.json", "voices.json", "usage.json"];

    static string PointerPath => Path.Combine(Data, "location.json");

    /// <summary>
    /// Why the chosen settings folder couldn't be used and the local one is used instead, or null. Kept for the
    /// log: logging here would read <see cref="Settings"/> while it is still being worked out.
    /// </summary>
    public static string? SettingsFolderProblem { get; private set; }

    static string ReadSettingsFolder()
    {
        try
        {
            if (File.Exists(PointerPath) &&
                System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(PointerPath)) is { } pointer &&
                pointer.TryGetValue("SettingsFolder", out var folder) && !string.IsNullOrWhiteSpace(folder))
                return Ensure(folder);
        }
        catch (Exception e)
        {
            // An unreachable folder (a OneDrive that isn't there yet) must not stop the app: fall back to local.
            SettingsFolderProblem = $"{e.GetType().Name}: {e.Message}";
        }
        return Data;
    }

    /// <summary>Remembers the settings folder for the next start; null goes back to the local data folder.</summary>
    public static void SetSettingsFolder(string? folder)
    {
        if (folder is null || Path.GetFullPath(folder).TrimEnd('\\').Equals(Data.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            File.Delete(PointerPath);
        else
            File.WriteAllText(PointerPath, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["SettingsFolder"] = folder }));
    }

    static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}

public static class Log
{
    static readonly object Gate = new();
    static string FilePath => Path.Combine(AppPaths.Logs, $"zeppie-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? e = null) => Write("ERROR", e is null ? message : $"{message}: {e}");

    static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {level} {message}";
        System.Diagnostics.Debug.WriteLine(line);
        lock (Gate)
        {
            try { File.AppendAllText(FilePath, line + Environment.NewLine); }
            catch { /* logging must never take the app down */ }
        }
    }
}
