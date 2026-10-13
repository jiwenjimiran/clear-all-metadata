namespace Cove.ClearAllMetadata;

public interface IGeneratedAssetCleaner
{
    Task ClearAsync(int videoId, ClearSettings settings, CancellationToken ct);
}

public sealed class GeneratedAssetCleaner(IServiceProvider services) : IGeneratedAssetCleaner
{
    public async Task ClearAsync(int videoId, ClearSettings settings, CancellationToken ct)
    {
        if (!settings.Thumbnails && !settings.SpriteSheets && !settings.VideoPreviews) return;
        var contract = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Cove.Api.Services.IThumbnailService", false))
            .FirstOrDefault(t => t is not null) ?? throw new NotSupportedException("Cove's generated asset service is unavailable.");
        var host = services.GetService(contract) ?? throw new NotSupportedException("Cove's generated asset service is unavailable.");
        var coordinatorType = contract.Assembly.GetType("Cove.Api.Services.VideoGeneratedAssetCoordinator")
            ?? throw new NotSupportedException("Cove's generated asset coordinator is unavailable.");
        var coordinator = services.GetService(coordinatorType)
            ?? throw new NotSupportedException("Cove's generated asset coordinator is unavailable.");
        await using var lease = await (Task<IAsyncDisposable>)coordinatorType.GetMethod("AcquireAsync")!.Invoke(coordinator, [videoId, ct])!;
        coordinatorType.GetMethod("Advance")!.Invoke(coordinator, [videoId]);
        string PathFor(string name) => (string)(contract.GetMethod(name, [typeof(int)])
            ?? host.GetType().GetMethod(name, [typeof(int)])
            ?? throw new NotSupportedException($"Cove does not expose {name}." )).Invoke(host, [videoId])!;
        // Resolve all paths before deleting anything. Use only host-generated paths,
        // never source-media paths or settings-supplied filesystem locations.
        var paths = new List<string>();
        if (settings.Thumbnails)
        {
            paths.Add(PathFor("GetThumbnailPathForVideo"));
            paths.Add(PathFor("GetVrCardPath"));
            var timestamped = (string)contract.GetMethod("GetTimestampedThumbnailPath")!.Invoke(host, [videoId, 0d])!;
            AddMatches(timestamped, $"{videoId}_t*.jpg");
        }
        if (settings.SpriteSheets) { paths.Add(PathFor("GetSpritePath")); paths.Add(PathFor("GetSpriteVttPath")); }
        if (settings.VideoPreviews)
        {
            paths.Add(PathFor("GetPreviewPath"));
            paths.Add(PathFor("GetVrPreviewPath"));
            var segment = (string)contract.GetMethod("GetSegmentAnimatedPreviewPath")!.Invoke(host, [videoId, 0d])!;
            AddMatches(segment, $"{videoId}_t*.webp");
        }
        foreach (var path in paths.Distinct()) { ct.ThrowIfCancellationRequested(); if (File.Exists(path)) File.Delete(path); }
        void AddMatches(string example, string pattern)
        {
            var directory = Path.GetDirectoryName(example)!;
            if (Directory.Exists(directory)) paths.AddRange(Directory.EnumerateFiles(directory, pattern));
        }
    }
}
