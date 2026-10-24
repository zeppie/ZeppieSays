using System.IO;

namespace ZeppieSays.Core;

public static class AppPaths
{
    public static string Data { get; } = Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZeppieSays"));

    public static string Models { get; } = Ensure(Path.Combine(Data, "models"));
    public static string Logs { get; } = Ensure(Path.Combine(Data, "logs"));

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
