using System.Diagnostics;
using System.Globalization;

namespace ZeppieSays.Services;

/// <summary>How full the graphics card's memory is, as reported by NVIDIA's nvidia-smi (installed with the driver).</summary>
public static class GpuMemory
{
    public sealed record Usage(long UsedBytes, long TotalBytes);

    /// <summary>Memory used by every program together; null without an NVIDIA card or driver.</summary>
    public static async Task<Usage?> QueryAsync()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("nvidia-smi",
                "--query-gpu=memory.used,memory.total --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return null;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var output = await process.StandardOutput.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
            return Parse(output);
        }
        catch
        {
            return null; // not installed, or too slow
        }
    }

    /// <summary>"6144, 8192" (MiB) per card, one line each; the first card is the one that counts.</summary>
    public static Usage? Parse(string output)
    {
        var parts = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault()?.Split(',', StringSplitOptions.TrimEntries);
        if (parts is { Length: 2 } &&
            long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var used) &&
            long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var total) && total > 0)
            return new Usage(used * 1024 * 1024, total * 1024 * 1024);
        return null;
    }
}
