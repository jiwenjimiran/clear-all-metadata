using Cove.ClearAllMetadata;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Core.Auth;
using Cove.Core.Events;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cove.Api.Services;
using Cove.Core.Entities.Galleries.Zip;
using System.Reflection;

var video = new Video {
    Id = 42, Title = "Wrong scene", Code = "WRONG", Details = "Wrong details", Director = "Wrong director",
    Date = new DateOnly(2020, 1, 1), StudioId = 8, Organized = true, ImageBlobId = "wrong-cover",
    PrimaryFileId = 9, Captions = "keep", IsVr = true, ParentVideoId = 3, ClipStartSec = 12,
    MaxDuration = 120, MinPath = "/media/keep.mp4",
    Urls = [new() { Url = "https://wrong.example" }], RemoteIds = [new() { RemoteId = "wrong" }],
    VideoTags = [new() { TagId = 5 }], VideoPerformers = [new() { PerformerId = 6 }],
    Files = [new() { Id = 9 }], VideoGalleries = [new() { GalleryId = 7 }],
    PlayHistory = [new() { Id = 1 }]
};
ClearAllMetadataExtension.Reset(video);
if (video.Title != null || video.Code != null || video.Details != null || video.Director != null || video.Date != null || video.StudioId != null || video.ImageBlobId != null || video.Organized || video.Urls.Count != 0 || video.RemoteIds.Count != 0 || video.VideoTags.Count != 0 || video.VideoPerformers.Count != 0)
    throw new Exception("Misidentification was not fully cleared.");
if (video.Id != 42 || video.PrimaryFileId != 9 || video.Files.Single().Id != 9 || video.Captions != "keep" || !video.IsVr || video.ParentVideoId != 3 || video.ClipStartSec != 12 || video.MaxDuration != 120 || video.MinPath != "/media/keep.mp4" || video.VideoGalleries.Single().GalleryId != 7 || video.PlayHistory.Single().Id != 1)
    throw new Exception("Reset damaged preserved media or user data.");
ClearAllMetadataExtension.Reset(video);
Console.WriteLine("PASS: complete reset, media preservation, repeated reset.");

if (args.Length > 0)
{
    if (args.Length < 3) throw new ArgumentException("Pass a disposable PostgreSQL connection string, FFmpeg executable, and a five-second test video.");
    var testVideoPath = Path.GetFullPath(args[2]);
    var config = new CoveConfiguration { FfmpegPath = args[1], GeneratedPath = Path.Combine(Path.GetDirectoryName(testVideoPath)!, "generated") };
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IEventBus, EventBus>();
    services.AddCoveData(args[0], runBackgroundMaterializers: false);
    var cleanup = new TestBlobService();
    services.AddSingleton<IBlobService>(cleanup);
    services.AddSingleton<IThumbnailService>(sp => new ThumbnailService(
        sp.GetRequiredService<IServiceScopeFactory>(), DispatchProxy.Create<IJobService, UnusedDependency>(), config,
        DispatchProxy.Create<IZipFileReader, UnusedDependency>(), cleanup, NullLogger<ThumbnailService>.Instance));
    services.AddScoped<IDefaultCoverGenerator, DefaultCoverGenerator>();
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
    await db.Database.EnsureCreatedAsync();
    await db.Database.OpenConnectionAsync();
    await using (var command = db.Database.GetDbConnection().CreateCommand())
    {
        command.CommandText = Cove.Data.Auth.AuthorizationSqlDefinitions.CreateFunctionsSql;
        await command.ExecuteNonQueryAsync();
    }
    await db.Database.CloseConnectionAsync();
    var coordinator = scope.ServiceProvider.GetRequiredService<BlobReferenceTransactionCoordinator>();
    var identified = new Video {
        Title = "Incorrect match", Details = "Wrong details", Organized = true, ImageBlobId = "cover-to-clear",
        Studio = new Studio { Name = "Wrong studio" },
        VideoTags = [new() { Tag = new Tag { Name = "Wrong tag" } }],
        VideoPerformers = [new() { Performer = new Performer { Name = "Wrong performer" } }],
        Urls = [new() { Url = "https://wrong.example/scene" }],
        RemoteIds = [new() { Endpoint = "https://wrong.example", RemoteId = "wrong" }],
        Files = [new() { Basename = Path.GetFileName(testVideoPath), ParentFolder = new Folder { Path = Path.GetDirectoryName(testVideoPath)!.Replace('\\', '/') }, Duration = 5 }]
    };
    db.Videos.Add(identified);
    await db.SaveChangesAsync();
    var id = identified.Id;
    db.TagApplications.Add(new() { HostType = AffinityHostType.Video, HostId = id, TagId = identified.VideoTags.Single().TagId, SourceKey = "metadata" });
    db.FieldProvenance.Add(new() { HostType = AffinityHostType.Video, HostId = id, FieldKey = "title", SourceKey = "metadata", ValueJson = "\"Incorrect match\"" });
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();
    var thumbnails = scope.ServiceProvider.GetRequiredService<IThumbnailService>();
    if (!await thumbnails.RegenerateVideoThumbnailAsync(id)) throw new Exception("Host baseline default-cover generation failed.");
    var thumbnailPath = thumbnails.GetThumbnailPathForVideo(id);
    var expectedCover = await File.ReadAllBytesAsync(thumbnailPath);
    await File.WriteAllTextAsync(thumbnailPath, "stale-incorrect-cover");

    // Reproduce both v0.1.0 failures with the actual production provider/interceptors.
    await using (var transaction = await db.Database.BeginTransactionAsync())
    {
        try { await db.Videos.FirstAsync(); throw new Exception("Expected retry strategy to reject the old transaction pattern."); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("execution strategy"))
        { Console.WriteLine("REPRODUCED: v0.1.0 transaction rejected by PostgreSQL retry strategy."); }
    }
    await db.Database.CreateExecutionStrategy().ExecuteAsync(async () => {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var loaded = await db.Videos.SingleAsync(v => v.Id == id);
        loaded.ImageBlobId = null;
        try { await db.SaveChangesAsync(); throw new Exception("Expected cover changes to require blob coordination."); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Blob reference changes"))
        { Console.WriteLine("REPRODUCED: uncoordinated cover reset rejected by host interceptor."); }
    });
    db.ChangeTracker.Clear();
    var progress = new TestProgress();
    var updated = false;
    using var subscription = scope.ServiceProvider.GetRequiredService<IEventBus>().Subscribe(EventType.VideoUpdated, _ => updated = true);
    try
    {
        await ClearAllMetadataExtension.RunJobAsync(scope.ServiceProvider, CovePrincipal.Anonymous(), id, progress, CancellationToken.None);
        throw new Exception("Unauthorized background job should fail.");
    }
    catch (UnauthorizedAccessException) { }
    if (await db.Videos.IgnoreQueryFilters().Where(v => v.Id == id).Select(v => v.Title).SingleAsync() != "Incorrect match")
        throw new Exception("Unauthorized job mutated video.");
    await ClearAllMetadataExtension.RunJobAsync(scope.ServiceProvider, CovePrincipal.System(), id, progress, CancellationToken.None);
    if (!updated || !progress.Completed || scope.ServiceProvider.GetRequiredService<ICurrentPrincipalAccessor>().Current != null)
        throw new Exception("Job event, summary, or principal cleanup failed.");
    db.ChangeTracker.Clear();
    var cleared = await db.Videos.Include(v => v.Files).Include(v => v.Urls).Include(v => v.RemoteIds)
        .Include(v => v.VideoTags).Include(v => v.VideoPerformers).SingleAsync(v => v.Id == id);
    if (cleared.Title != null || cleared.Details != null || cleared.StudioId != null || cleared.ImageBlobId != null || cleared.Organized || cleared.VideoTags.Count != 0 || cleared.VideoPerformers.Count != 0 || cleared.Urls.Count != 0 || cleared.RemoteIds.Count != 0 || cleared.TagIds.Length != 0 || cleared.PerformerIds.Length != 0)
        throw new Exception("Persisted metadata was not fully cleared.");
    if (await db.TagApplications.AnyAsync() || await db.FieldProvenance.AnyAsync()) throw new Exception("Provenance remains.");
    if (cleared.Files.Single().Path != testVideoPath.Replace('\\', '/') || cleared.Files.Single().Duration != 5 || !await db.Studios.AnyAsync() || !await db.Performers.AnyAsync() || !await db.Tags.AnyAsync())
        throw new Exception("Media or shared entities damaged.");
    if (!(await File.ReadAllBytesAsync(thumbnailPath)).SequenceEqual(expectedCover))
        throw new Exception("Reset did not restore exactly the host-generated default cover.");
    if (!cleanup.Deleted.Contains("cover-to-clear")) throw new Exception("Detached cover cleanup was not coordinated.");
    if (!await ClearAllMetadataExtension.ClearAsync(db, coordinator, id)) throw new Exception("Repeated reset failed.");
    if (await ClearAllMetadataExtension.ClearAsync(db, coordinator, int.MaxValue)) throw new Exception("Missing video should return false.");
    Console.WriteLine("PASS: PostgreSQL production retry strategy, blob interceptor, persisted reset, provenance cleanup, media preservation, repeated reset, missing video.");
    Console.WriteLine("PASS: background job authorization, principal restoration, completion summary, and UI update event.");
    Console.WriteLine("PASS: real host FFmpeg generation replaced a stale cover with the exact default-generated frame.");

    // An unreadable source must fail the job rather than report a restored cover.
    var file = cleared.Files.Single();
    db.ChangeTracker.Clear();
    var missingFile = await db.VideoFiles.SingleAsync(f => f.Id == file.Id);
    missingFile.Basename = "missing-source.mp4";
    await db.SaveChangesAsync();
    var failureNotified = false;
    using var failureSubscription = scope.ServiceProvider.GetRequiredService<IEventBus>().Subscribe(EventType.VideoUpdated, _ => failureNotified = true);
    try
    {
        await ClearAllMetadataExtension.RunJobAsync(scope.ServiceProvider, CovePrincipal.System(), id, new TestProgress(), CancellationToken.None);
        throw new Exception("Missing source should fail cover restoration.");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("default cover could not be generated")) { }
    if (!failureNotified) throw new Exception("Partial failure did not refresh cleared metadata.");
    Console.WriteLine("PASS: missing source reports cover-generation failure and still notifies metadata refresh.");
}

sealed class TestBlobService : IBlobService
{
    public HashSet<string> Deleted { get; } = [];
    public Task<string> StoreBlobAsync(Stream data, string contentType, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<(Stream Stream, string ContentType)?> GetBlobAsync(string blobId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeleteBlobAsync(string blobId, CancellationToken ct = default) { Deleted.Add(blobId); return Task.CompletedTask; }
}

sealed class TestProgress : IJobProgress
{
    public bool Completed { get; private set; }
    public void Report(double progress, string? subTask = null) { Completed = progress == 1; }
}

public class UnusedDependency : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? arguments)
        => throw new NotSupportedException($"Unexpected dependency call: {targetMethod?.Name}");
}
