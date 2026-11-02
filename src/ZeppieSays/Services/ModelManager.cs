using System.IO;
using System.Net.Http;
using SharpCompress.Readers;
using ZeppieSays.Core;

namespace ZeppieSays.Services;

/// <summary>A model that is ready to load: its catalog entry plus where it lives on disk.</summary>
public sealed record InstalledModel(ModelInfo Info, string Path);

/// <summary>Downloads, unpacks and removes local speech models under %LOCALAPPDATA%\ZeppieSays\models.</summary>
public sealed class ModelManager(AppSettings settings)
{
    const string Marker = ".installed";
    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public event Action? Changed;

    static string DirFor(string id) => Path.Combine(AppPaths.Models, id);

    public bool IsInstalled(string id) =>
        settings.CustomModels.Any(c => c.Id == id && File.Exists(c.Path)) ||
        File.Exists(Path.Combine(DirFor(id), Marker)) ||
        ModelCatalog.Cloud.Any(m => m.Id == id && HasCloudKey(m));

    /// <summary>A cloud model counts as installed while its API key is saved.</summary>
    bool HasCloudKey(ModelInfo model) => model.Engine switch
    {
        EngineKind.ElevenLabs => settings.Llm.ElevenLabsKeyProtected.Length > 0,
        _ => false,
    };

    public IReadOnlyList<InstalledModel> Installed()
    {
        var list = ModelCatalog.BuiltIn
            .Where(m => IsInstalled(m.Id))
            .Select(m => new InstalledModel(m, ModelPath(m)))
            .ToList();
        list.AddRange(settings.CustomModels
            .Where(c => File.Exists(c.Path))
            .Select(c => new InstalledModel(CustomInfo(c), c.Path)));
        list.AddRange(ModelCatalog.Cloud.Where(HasCloudKey).Select(m => new InstalledModel(m, "")));
        return list;
    }

    /// <summary>For changes that make models available or not without an install, like saving an API key.</summary>
    public void NotifyChanged() => Changed?.Invoke();

    public InstalledModel? Find(string id) => Installed().FirstOrDefault(m => m.Info.Id == id);

    public static ModelInfo CustomInfo(CustomModel c) =>
        new(c.Id, c.Name, EngineKind.Whisper, "Custom Whisper model", "Custom", new FileInfo(c.Path).Length, "",
            SupportsPrompt: true);

    static string ModelPath(ModelInfo m) => m.Engine switch
    {
        EngineKind.Whisper => Path.Combine(DirFor(m.Id), "model.bin"),
        _ => DirFor(m.Id),
    };

    public async Task InstallAsync(ModelInfo model, IProgress<double> progress, CancellationToken ct)
    {
        var dir = DirFor(model.Id);
        var partial = Path.Combine(AppPaths.Models, model.Id + ".download");
        Directory.CreateDirectory(dir);
        Log.Info($"Downloading {model.Id} from {model.DownloadUrl}");

        try
        {
            await DownloadAsync(model.DownloadUrl, partial, model.ApproxBytes, progress, ct);

            if (model.Engine == EngineKind.Whisper)
                File.Move(partial, ModelPath(model), overwrite: true);
            else
                await Task.Run(() => ExtractFlattened(partial, dir), ct);

            File.WriteAllText(Path.Combine(dir, Marker), DateTime.UtcNow.ToString("O"));
            progress.Report(1);
            Log.Info($"Installed {model.Id}");
        }
        catch
        {
            TryDelete(dir);
            throw;
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
            Changed?.Invoke();
        }
    }

    /// <summary>Downloads a file, reporting progress up to 99%.</summary>
    internal static async Task DownloadAsync(string url, string path, long approxBytes, IProgress<double> progress, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? approxBytes;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(path);
        var buffer = new byte[1 << 20];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            progress.Report(Math.Min(0.99, (double)done / total));
        }
    }

    /// <summary>Unpacks a .tar.bz2 release, dropping its single top-level folder.</summary>
    internal static void ExtractFlattened(string archive, string dir)
    {
        using var stream = File.OpenRead(archive);
        using var reader = ReaderFactory.OpenReader(stream, new ReaderOptions());
        while (reader.MoveToNextEntry())
        {
            if (reader.Entry.IsDirectory || reader.Entry.Key is null) continue;
            var parts = reader.Entry.Key.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var relative = parts.Length > 1 ? Path.Combine(parts[1..]) : parts[0];
            var target = Path.GetFullPath(Path.Combine(dir, relative));
            if (!target.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase))
                continue; // ignore entries that try to escape the model folder
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var output = File.Create(target);
            reader.WriteEntryTo(output);
        }
    }

    public void Uninstall(string id)
    {
        var custom = settings.CustomModels.FirstOrDefault(c => c.Id == id);
        if (custom is not null)
            settings.CustomModels.Remove(custom); // the user's own file stays where it is
        else
            TryDelete(DirFor(id));
        Changed?.Invoke();
    }

    public CustomModel AddCustom(string path)
    {
        var model = new CustomModel
        {
            Id = "custom-" + Path.GetFileNameWithoutExtension(path).ToLowerInvariant(),
            Name = Path.GetFileNameWithoutExtension(path),
            Path = path,
        };
        settings.CustomModels.RemoveAll(c => c.Id == model.Id);
        settings.CustomModels.Add(model);
        Changed?.Invoke();
        return model;
    }

    static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception e) { Log.Error($"Could not delete {dir}", e); }
    }
}
