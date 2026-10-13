using Cove.ClearAllMetadata;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Entities.Galleries.Zip;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using Cove.Api.Services;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using HostProgress = Cove.Core.Interfaces.IJobProgress;
using HostUnit = Cove.Core.Interfaces.IJobUnit;

void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
var defaults = new ClearSettings();
Check(!defaults.Thumbnails && !defaults.SpriteSheets && !defaults.Phash && !defaults.VideoPreviews && !defaults.Md5, "Generated data must be kept by default.");
var metadataExtension = new ClearAllMetadataExtension();
var store = new TestStore(); metadataExtension.SetStore(store);
Check(await metadataExtension.SettingsAsync() == defaults, "Fresh install defaults incorrect.");
await store.SetAsync("settings", "{\"title\":false,\"md5\":true}");
Check(!(await metadataExtension.SettingsAsync()).Title && (await metadataExtension.SettingsAsync()).Md5 && !(await metadataExtension.SettingsAsync()).Thumbnails, "Saved settings did not survive load.");
Check(metadataExtension.GetUIManifest().SettingsPanels.Count == 1 && metadataExtension.GetUIManifest().Actions.Count == 2, "Missing settings or actions.");
Check(ClearAllMetadataExtension.SelectionIds([1, 2, 1]).SequenceEqual(new[] { 1, 2 }), "IDs not deduplicated.");
Console.WriteLine("PASS: settings defaults, persistence, UI declarations, selection deduplication.");
if (args.Length == 0) return;

var generated = Path.GetFullPath(Path.Combine("artifacts", "settings-regression-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(generated);
var services = new ServiceCollection();
services.AddLogging(); services.AddSingleton<IEventBus, EventBus>();
services.AddCoveData(args[0], runBackgroundMaterializers: false);
services.AddSingleton<IBlobService, TestBlobService>();
services.AddSingleton<VideoGeneratedAssetCoordinator>();
services.AddSingleton<IThumbnailService>(sp => new ThumbnailService(sp.GetRequiredService<IServiceScopeFactory>(),
    DispatchProxy.Create<IJobService, UnusedDependency>(), new CoveConfiguration { GeneratedPath = generated },
    DispatchProxy.Create<IZipFileReader, UnusedDependency>(), sp.GetRequiredService<IBlobService>(), NullLogger<ThumbnailService>.Instance,
    sp.GetRequiredService<VideoGeneratedAssetCoordinator>()));
services.AddScoped<IGeneratedAssetCleaner, GeneratedAssetCleaner>();
await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();
var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
await db.Database.EnsureCreatedAsync();
await db.Database.OpenConnectionAsync();
await using (var command = db.Database.GetDbConnection().CreateCommand())
{ command.CommandText = Cove.Data.Auth.AuthorizationSqlDefinitions.CreateFunctionsSql; await command.ExecuteNonQueryAsync(); }
await db.Database.CloseConnectionAsync();
var thumbnails = scope.ServiceProvider.GetRequiredService<IThumbnailService>();
var concrete = (ThumbnailService)thumbnails;
var scopes = new TestScopeFactory(provider.GetRequiredService<IServiceScopeFactory>());
async Task<int> Seed(string label)
{
    db.ChangeTracker.Clear();
    var folder = Path.Combine(generated, label); Directory.CreateDirectory(folder);
    await File.WriteAllTextAsync(Path.Combine(folder, "source.mp4"), "original-media");
    var video = new Video { Title = label, Details = "details", Studio = new Studio { Name = label + " studio" },
        Organized = true, ImageBlobId = label + " cover", Date = new DateOnly(2020, 1, 1),
        VideoTags = [new() { Tag = new Tag { Name = label + " tag" } }],
        Files = [new() { Basename = "source.mp4", ParentFolder = new Folder { Path = folder.Replace('\\', '/') }, Duration = 123,
            Fingerprints = [new() { Type = "md5", Value = "md5-value" }, new() { Type = "phash", Value = "phash-value" }, new() { Type = "oshash", Value = "keep-oshash" }] }] };
    db.Videos.Add(video); await db.SaveChangesAsync();
    db.FieldProvenance.AddRange(new FieldProvenance { HostType = AffinityHostType.Video, HostId = video.Id, FieldKey = "title" },
        new FieldProvenance { HostType = AffinityHostType.Video, HostId = video.Id, FieldKey = "details" });
    db.TagApplications.Add(new() { HostType = AffinityHostType.Video, HostId = video.Id, TagId = video.VideoTags.Single().TagId });
    await db.SaveChangesAsync();
    foreach (var path in Paths(video.Id)) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, "generated-asset"); }
    return video.Id;
}
string[] Paths(int id) => [thumbnails.GetThumbnailPathForVideo(id), thumbnails.GetTimestampedThumbnailPath(id, 1), concrete.GetVrCardPath(id),
    thumbnails.GetSpritePath(id), thumbnails.GetSpriteVttPath(id), thumbnails.GetPreviewPath(id), concrete.GetVrPreviewPath(id), thumbnails.GetSegmentAnimatedPreviewPath(id, 1)];
async Task<Video> Reload(int id)
{
    db.ChangeTracker.Clear();
    return await db.Videos.Include(v => v.Files).ThenInclude(f => f.Fingerprints).Include(v => v.VideoTags).SingleAsync(v => v.Id == id);
}
async Task Run(int id, ClearSettings? settings = null) => await ClearAllMetadataExtension.RunJobAsync(scope.ServiceProvider, CovePrincipal.System(), id, new TestProgress(), CancellationToken.None, settings);

var first = await Seed("default");
await Run(first);
var cleared = await Reload(first);
Check(cleared.Title == null && cleared.Details == null && cleared.StudioId == null && cleared.Date == null && cleared.ImageBlobId == null && !cleared.Organized && cleared.VideoTags.Count == 0, "Default metadata reset incomplete.");
Check(Paths(first).All(p => File.ReadAllText(p) == "generated-asset"), "Default reset changed generated assets.");
Check(cleared.Files.Single().Fingerprints.Count == 3 && cleared.Files.Single().Duration == 123, "Default reset changed hashes or technical metadata.");
Check(await File.ReadAllTextAsync(cleared.Files.Single().Path) == "original-media", "Source media changed.");
Check(!await db.TagApplications.AnyAsync(p => p.HostId == first) && !await db.FieldProvenance.AnyAsync(p => p.HostId == first), "Selected provenance remains.");
Console.WriteLine("PASS: default reset preserves every generated asset and hash, including with an undecodable source; no generation is run.");

var selective = await Seed("selective");
await Run(selective, defaults with { Title = false, Date = false, Tags = false, Cover = false, Md5 = true, SpriteSheets = true });
var kept = await Reload(selective);
Check(kept.Title == "selective" && kept.Date != null && kept.ImageBlobId != null && kept.VideoTags.Count == 1 && kept.Details == null, "Unchecked metadata fields were not kept.");
Check(await db.TagApplications.AnyAsync(p => p.HostId == selective) && await db.FieldProvenance.AnyAsync(p => p.HostId == selective && p.FieldKey == "title") && !await db.FieldProvenance.AnyAsync(p => p.HostId == selective && p.FieldKey == "details"), "Selective provenance cleanup incorrect.");
Check(kept.Files.Single().Fingerprints.Select(f => f.Type).Order().SequenceEqual(new[] { "oshash", "phash" }), "MD5 wipe removed another hash.");
Check(!File.Exists(thumbnails.GetSpritePath(selective)) && !File.Exists(thumbnails.GetSpriteVttPath(selective)) && File.Exists(thumbnails.GetThumbnailPathForVideo(selective)) && File.Exists(thumbnails.GetPreviewPath(selective)), "Sprite wipe removed unrelated assets.");
await Run(selective, defaults with { Thumbnails = true, Phash = true });
Check(!File.Exists(thumbnails.GetThumbnailPathForVideo(selective)) && !File.Exists(thumbnails.GetTimestampedThumbnailPath(selective, 1)) && !File.Exists(concrete.GetVrCardPath(selective)) && File.Exists(thumbnails.GetPreviewPath(selective)), "Thumbnail wipe not isolated.");
Check((await Reload(selective)).Files.Single().Fingerprints.Single().Type == "oshash", "pHash wipe removed another hash.");
await Run(selective, defaults with { VideoPreviews = true });
Check(!File.Exists(thumbnails.GetPreviewPath(selective)) && !File.Exists(concrete.GetVrPreviewPath(selective)) && !File.Exists(thumbnails.GetSegmentAnimatedPreviewPath(selective, 1)), "Preview wipe incomplete.");
Check(Paths(first).All(File.Exists), "Another video's generated assets were changed.");
Console.WriteLine("PASS: individual metadata, provenance, MD5, pHash, sprite/VTT, thumbnail/VR-card, and preview/VR/segment wipes are isolated.");

var last = await Seed("bulk");
var progress = new TestProgress();
try { await ClearAllMetadataExtension.RunBatchAsync(scopes, CovePrincipal.System(), [first, int.MaxValue, last, first], progress, CancellationToken.None); throw new Exception("Missing video must fail."); }
catch (InvalidOperationException ex) when (ex.Message.Contains("1 videos failed")) { }
Check(progress.Units.Count == 3 && progress.Units.Count(u => u.Outcome == JobUnitOutcome.Succeeded) == 2 && (await Reload(last)).Title == null && Paths(last).All(File.Exists), "Bulk continuation or default asset preservation failed.");
var denied = await Seed("denied");
try { await ClearAllMetadataExtension.RunJobAsync(scope.ServiceProvider, CovePrincipal.Anonymous(), denied, new TestProgress(), CancellationToken.None); throw new Exception("Anonymous reset accepted."); }
catch (UnauthorizedAccessException) { }
try { await ClearAllMetadataExtension.RunBatchAsync(scopes, CovePrincipal.System(), [denied], new TestProgress(), new CancellationToken(true)); throw new Exception("Cancellation ignored."); }
catch (OperationCanceledException) { }
Check((await Reload(denied)).Title == "denied", "Denied/cancelled job changed metadata.");
Check(scope.ServiceProvider.GetRequiredService<ICurrentPrincipalAccessor>().Current == null, "Principal leaked from job.");
Console.WriteLine("PASS: bulk deduplication, continuation, per-video outcomes, authorization, cancellation, and principal restoration.");

sealed class TestStore : IExtensionStore
{
    private readonly Dictionary<string, string> values = [];
    public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(values.GetValueOrDefault(key));
    public Task SetAsync(string key, string value, CancellationToken ct = default) { values[key] = value; return Task.CompletedTask; }
    public Task DeleteAsync(string key, CancellationToken ct = default) { values.Remove(key); return Task.CompletedTask; }
    public Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default) => Task.FromResult(new Dictionary<string, string>(values));
}
sealed class TestBlobService : IBlobService
{
    public Task<string> StoreBlobAsync(Stream data, string contentType, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<(Stream Stream, string ContentType)?> GetBlobAsync(string blobId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeleteBlobAsync(string blobId, CancellationToken ct = default) => Task.CompletedTask;
}
sealed class TestProgress : HostProgress
{
    public List<TestUnit> Units { get; } = [];
    public void Report(double progress, string? message = null) { }
    public HostUnit StartUnit(string id, string? label = null) { var unit = new TestUnit(); Units.Add(unit); return unit; }
}
sealed class TestUnit : HostUnit
{
    public JobUnitOutcome? Outcome { get; private set; }
    public void Report(double progress, string? message = null) { }
    public void Complete(JobUnitOutcome outcome, string? message = null) => Outcome = outcome;
    public void Dispose() { }
}
sealed class TestScopeFactory(IServiceScopeFactory factory) : IExtensionServiceScopeFactory
{
    public IServiceScope CreateScope() => factory.CreateScope();
    public AsyncServiceScope CreateAsyncScope() => factory.CreateAsyncScope();
}
public class UnusedDependency : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? arguments) => throw new NotSupportedException($"Unexpected call: {method?.Name}");
}
