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
Check(!defaults.AlsoClearAiTags, "AI tags must be kept by default.");
Check(!defaults.Thumbnails && !defaults.SpriteSheets && !defaults.Phash && !defaults.VideoPreviews && !defaults.Md5, "Generated data must be kept by default.");
var metadataExtension = new ClearAllMetadataExtension();
var store = new TestStore(); metadataExtension.SetStore(store);
Check(await metadataExtension.SettingsAsync() == defaults, "Fresh install defaults incorrect.");
await store.SetAsync("settings", "{\"title\":false,\"md5\":true}");
Check(!(await metadataExtension.SettingsAsync()).Title && (await metadataExtension.SettingsAsync()).Md5 && !(await metadataExtension.SettingsAsync()).Thumbnails, "Saved settings did not survive load.");
Check(!(await metadataExtension.SettingsAsync()).AlsoClearAiTags, "Existing settings must preserve AI tags after upgrade.");
await store.SetAsync("settings", "{\"tags\":true,\"alsoClearAiTags\":true}");
Check((await metadataExtension.SettingsAsync()).AlsoClearAiTags, "AI wipe override must survive settings persistence.");
Check(ClearAllMetadataExtension.IsAiTaggingSource("ext:ai.tagging") && !ClearAllMetadataExtension.IsAiTaggingSource("scraper:local")
    && !ClearAllMetadataExtension.IsAiTaggingSource("ext:ai.other") && !ClearAllMetadataExtension.IsAiTaggingSource("ext:ai.tagging-fake"), "Only exact AI Tagging provenance protects a tag.");
var resetFixture = new Video { VideoTags = [new() { TagId = 1 }, new() { TagId = 2 }] };
ClearAllMetadataExtension.Reset(resetFixture, defaults, new HashSet<int> { 1 });
Check(resetFixture.VideoTags.Single().TagId == 1, "Reset must keep AI-backed tag links.");
ClearAllMetadataExtension.Reset(resetFixture, defaults with { Tags = false, AlsoClearAiTags = true });
Check(resetFixture.VideoTags.Count == 1, "Tags parent must gate AI tag clearing.");
ClearAllMetadataExtension.Reset(resetFixture, defaults with { AlsoClearAiTags = true }, new HashSet<int> { 1 });
Check(resetFixture.VideoTags.Count == 0, "Explicit override must clear AI tag links.");
Check(metadataExtension.GetUIManifest().SettingsPanels.Count == 1 && metadataExtension.GetUIManifest().Actions.Count == 2, "Missing settings or actions.");
Check(ClearAllMetadataExtension.SelectionIds([1, 2, 1]).SequenceEqual(new[] { 1, 2 }), "IDs not deduplicated.");
Console.WriteLine("PASS: settings defaults, persistence, UI declarations, selection deduplication.");
if (args.Length == 0) return;
if (args.Length < 3) throw new ArgumentException("Provide the PostgreSQL connection, FFmpeg executable, and a 5.37-second source fixture.");

var generated = Path.GetFullPath(Path.Combine("artifacts", "settings-regression-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(generated);
var services = new ServiceCollection();
services.AddLogging(); services.AddSingleton<IEventBus, EventBus>();
services.AddCoveData(args[0], runBackgroundMaterializers: false);
services.AddSingleton<IBlobService, TestBlobService>();
services.AddSingleton<VideoGeneratedAssetCoordinator>();
services.AddSingleton<IThumbnailService>(sp => new ThumbnailService(sp.GetRequiredService<IServiceScopeFactory>(),
    DispatchProxy.Create<IJobService, UnusedDependency>(), new CoveConfiguration { GeneratedPath = generated, FfmpegPath = args[1] },
    DispatchProxy.Create<IZipFileReader, UnusedDependency>(), sp.GetRequiredService<IBlobService>(), NullLogger<ThumbnailService>.Instance,
    sp.GetRequiredService<VideoGeneratedAssetCoordinator>()));
services.AddScoped<IGeneratedAssetCleaner, GeneratedAssetCleaner>();
services.AddScoped<IDefaultCoverResetter, DefaultCoverResetter>();
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
    File.Copy(args[2], Path.Combine(folder, "source.mp4"));
    var video = new Video { Title = label, Details = "details", Studio = new Studio { Name = label + " studio" },
        Organized = true, ImageBlobId = label + " cover", Date = new DateOnly(2020, 1, 1),
        VideoTags = [new() { Tag = new Tag { Name = label + " tag" } }],
        Files = [new() { Basename = "source.mp4", ParentFolder = new Folder { Path = folder.Replace('\\', '/') }, Duration = 5.37,
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
var streams = new StreamService(provider.GetRequiredService<IServiceScopeFactory>(), thumbnails, scope.ServiceProvider.GetRequiredService<IBlobService>());
async Task<byte[]> ServedCover(int id)
{
    var result = await streams.GetVideoScreenshot(id, null) ?? throw new Exception("Cover stream missing.");
    await using var stream = result.stream;
    using var output = new MemoryStream(); await stream.CopyToAsync(output); return output.ToArray();
}
Check(System.Text.Encoding.UTF8.GetString(await ServedCover(first)) == "scraped-cover", "Fixture did not serve the scraped cover before reset.");
Check(await thumbnails.RegenerateVideoThumbnailAsync(first), "Default generation baseline failed.");
var expectedCover = await File.ReadAllBytesAsync(thumbnails.GetThumbnailPathForVideo(first));
await File.WriteAllTextAsync(thumbnails.GetThumbnailPathForVideo(first), "stale-incorrect-cover");
await Run(first);
Check((await ServedCover(first)).SequenceEqual(expectedCover), "Cover-serving path did not restore the default frame.");
var cleared = await Reload(first);
Check(cleared.Title == null && cleared.Details == null && cleared.StudioId == null && cleared.Date == null && cleared.ImageBlobId == null && !cleared.Organized && cleared.VideoTags.Count == 0, "Default metadata reset incomplete.");
Check(Paths(first).Skip(1).All(p => File.ReadAllText(p) == "generated-asset"), "Default reset changed unrelated generated assets.");
Check(cleared.Files.Single().Fingerprints.Count == 3 && cleared.Files.Single().Duration == 5.37, "Default reset changed hashes or technical metadata.");
var sourceBytes = await File.ReadAllBytesAsync(cleared.Files.Single().Path);
var fixtureBytes = await File.ReadAllBytesAsync(args[2]);
Check(sourceBytes.SequenceEqual(fixtureBytes), "Source media changed.");
Check(!await db.TagApplications.AnyAsync(p => p.HostId == first) && !await db.FieldProvenance.AnyAsync(p => p.HostId == first), "Selected provenance remains.");
Console.WriteLine("PASS: served cover replaces stale cached artwork with the exact default frame; other thumbnails/assets/hashes and source media stay unchanged.");

async Task<(int Id, int Boobs, int Custom)> SeedAiTags(string label)
{
    var id = await Seed(label);
    var names = new[] { "Boobs", "Pussy", "Tongue", "Chastity", "69", "Custom AI tag outside model lists", "Untrusted AI source" };
    var tags = new List<Tag>();
    foreach (var name in names) {
        var tag = await db.Tags.SingleOrDefaultAsync(t => t.Name == name);
        if (tag is null) { tag = new Tag { Name = name }; db.Tags.Add(tag); }
        tags.Add(tag);
    }
    await db.SaveChangesAsync();
    var video = await db.Videos.Include(v => v.VideoTags).SingleAsync(v => v.Id == id);
    foreach (var tag in tags) video.VideoTags.Add(new VideoTag { VideoId = id, TagId = tag.Id });
    for (var index = 0; index < tags.Count; index++) db.TagApplications.Add(new TagApplication {
        HostType = AffinityHostType.Video, HostId = id, TagId = tags[index].Id,
        SourceKey = index is 0 or 5 ? "ext:ai.tagging" : index == 2 ? "user" : index == 6 ? "ext:ai.other" : "scraper:local",
        ModelKey = index is 0 or 5 ? "bodyparts" : "", SourceRunId = index is 0 or 5 ? "recorded-by-ai-tagging" : ""
    });
    // The same visible tag has both AI and scraped evidence: only the scraped evidence should be wiped.
    db.TagApplications.Add(new TagApplication { HostType = AffinityHostType.Video, HostId = id, TagId = tags[0].Id, SourceKey = "scraper:local" });
    db.FieldProvenance.AddRange(new FieldProvenance { HostType = AffinityHostType.Video, HostId = id, FieldKey = "tags", SourceKey = "ext:ai.tagging", ValueJson = "[" + tags[0].Id + "]" },
        new FieldProvenance { HostType = AffinityHostType.Video, HostId = id, FieldKey = "tags", SourceKey = "scraper:local", ValueJson = "[" + tags[1].Id + "]" });
    await db.SaveChangesAsync();
    return (id, tags[0].Id, tags[5].Id);
}
var aiDefault = await SeedAiTags("ai-default");
var unselectedAi = await SeedAiTags("ai-unselected");
await Run(aiDefault.Id);
var aiKept = await Reload(aiDefault.Id);
Check(aiKept.VideoTags.Select(t => t.TagId).Order().SequenceEqual(new[] { aiDefault.Boobs, aiDefault.Custom }.Order()), "Default must keep only AI-backed tag links, including custom names.");
var aiApplications = await db.TagApplications.Where(p => p.HostType == AffinityHostType.Video && p.HostId == aiDefault.Id).ToListAsync();
Check(aiApplications.Count == 2 && aiApplications.All(p => p.SourceKey == "ext:ai.tagging" && p.SourceRunId == "recorded-by-ai-tagging" && p.ModelKey == "bodyparts"), "AI applications and exact model/run provenance must survive; mixed scraper evidence must be removed.");
Check(await db.FieldProvenance.CountAsync(p => p.HostType == AffinityHostType.Video && p.HostId == aiDefault.Id) == 1
    && await db.FieldProvenance.AnyAsync(p => p.HostId == aiDefault.Id && p.FieldKey == "tags" && p.SourceKey == "ext:ai.tagging"), "Only AI tag field provenance should remain.");
Check((await Reload(unselectedAi.Id)).VideoTags.Count == 8, "Unselected video's tag links must remain untouched.");
var scrapedOnly = await Seed("scraped-boobs-only");
var scrapedVideo = await db.Videos.Include(v => v.VideoTags).SingleAsync(v => v.Id == scrapedOnly);
scrapedVideo.VideoTags.Add(new VideoTag { VideoId = scrapedOnly, TagId = aiDefault.Boobs });
db.TagApplications.Add(new TagApplication { HostType = AffinityHostType.Video, HostId = scrapedOnly, TagId = aiDefault.Boobs,
    SourceKey = "scraper:local", ModelKey = "bodyparts" });
await db.SaveChangesAsync(); await Run(scrapedOnly);
Check((await Reload(scrapedOnly)).VideoTags.Count == 0 && !await db.TagApplications.AnyAsync(p => p.HostId == scrapedOnly),
    "Scraped-only Boobs must clear despite its model name/key and AI evidence on another video.");
var allTags = await SeedAiTags("ai-clear-all");
await Run(allTags.Id, defaults with { AlsoClearAiTags = true });
Check((await Reload(allTags.Id)).VideoTags.Count == 0 && !await db.TagApplications.AnyAsync(p => p.HostId == allTags.Id)
    && !await db.FieldProvenance.AnyAsync(p => p.HostId == allTags.Id), "Explicit AI override must wipe all tag links and provenance.");
var tagsOff = await SeedAiTags("ai-tags-off");
await Run(tagsOff.Id, defaults with { Tags = false, AlsoClearAiTags = true });
Check((await Reload(tagsOff.Id)).VideoTags.Count == 8 && await db.TagApplications.CountAsync(p => p.HostId == tagsOff.Id) == 9
    && await db.FieldProvenance.CountAsync(p => p.HostId == tagsOff.Id && p.FieldKey == "tags") == 2, "Unchecked Tags must preserve every tag source despite enabled child setting.");
Console.WriteLine("PASS: exact AI Tagging provenance, custom AI names, scraped/manual model names, mixed sources, preserved run/model/field evidence, opt-in AI wipe, parent gating and unselected isolation.");

var selective = await Seed("selective");
await Run(selective, defaults with { Title = false, Date = false, Tags = false, Cover = false, Md5 = true, SpriteSheets = true });
var kept = await Reload(selective);
Check(kept.Title == "selective" && kept.Date != null && kept.ImageBlobId != null && kept.VideoTags.Count == 1 && kept.Details == null, "Unchecked metadata fields were not kept.");
Check(File.ReadAllText(thumbnails.GetThumbnailPathForVideo(selective)) == "generated-asset", "Disabled cover option changed the generated cover.");
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
var missing = await Seed("unreadable");
File.Delete((await Reload(missing)).Files.Single().Path);
try { await Run(missing); throw new Exception("Unavailable media must not report successful cover restoration."); }
catch (InvalidOperationException ex) when (ex.Message.Contains("default cover restoration failed")) { }
Check((await Reload(missing)).ImageBlobId == null, "Failed generation restored the scraped cover reference.");
Console.WriteLine("PASS: unavailable source reports partial cover-restoration failure; disabled cover option preserves the cover.");

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
    public Task<(Stream Stream, string ContentType)?> GetBlobAsync(string blobId, CancellationToken ct = default)
        => Task.FromResult<(Stream Stream, string ContentType)?>((new MemoryStream(System.Text.Encoding.UTF8.GetBytes("scraped-cover")), "image/jpeg"));
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
