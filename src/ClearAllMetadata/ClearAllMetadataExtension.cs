using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using System.Security.Claims;
using System.Text.Json;
using Cove.Data;
using Cove.Data.Services;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.ClearAllMetadata;

public sealed class ClearAllMetadataExtension : IExtension, IUIExtension, IApiExtension, IStatefulExtension
{
    private IExtensionServiceScopeFactory? _scopes;
    private readonly CancellationTokenSource _shutdown = new();
    private IExtensionStore? _store;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public void SetStore(IExtensionStore store) => _store = store;
    public async Task<ClearSettings> SettingsAsync(CancellationToken ct = default)
    {
        var json = await (_store ?? throw new InvalidOperationException("Extension storage is unavailable.")).GetAsync("settings", ct);
        return json is null ? new() : JsonSerializer.Deserialize<ClearSettings>(json, Json) ?? new();
    }
    public string Id => "cove.community.clear-all-metadata";
    public string Name => "clear-all-metadata";
    public string Version => "0.1.6";
    public string? Description => "Clear video identification metadata without rescanning.";
    public string? Author => "jiwenji";
    public string? Url => "https://github.com/jiwenjimiran/clear-all-metadata";
    public string? IconUrl => null;
    public string? MinCoveVersion => "1.5.1";
    public IReadOnlyList<string> Categories => ["video", "tools"];
    public void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
        services.AddScoped<IGeneratedAssetCleaner, GeneratedAssetCleaner>();
        services.AddScoped<IDefaultCoverResetter, DefaultCoverResetter>();
    }
    public Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        _scopes = services.GetRequiredService<IExtensionServiceScopeFactory>();
        return Task.CompletedTask;
    }
    public Task ShutdownAsync(CancellationToken ct = default) => _shutdown.CancelAsync();
    public UIManifest GetUIManifest() => new()
    {
        SettingsPanels = [new($"{Id}:settings", "Clear all metadata", Id, "ClearMetadataSettings", Order: 270, TargetTab: "extensions")],
        Actions = [new("clear-all-metadata", "Clear all metadata", Id, "toolbar", ["video"],
            Icon: "eraser", HandlerName: "clearMetadata", Order: 999, SuppressSuccessAlert: true)
            { RequiredPermission = Permissions.VideosWrite },
            new("clear-all-metadata-selected", "Clear all metadata", Id, "bulk", ["video", "videos"],
            Icon: "eraser", HandlerName: "clearMetadata", Order: 999, SuppressSuccessAlert: true)
            { RequiredPermission = Permissions.VideosWrite }]
    };

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/ext/clear-all-metadata/settings", async (CancellationToken ct) => Results.Json(await SettingsAsync(ct), Json))
            .RequireCovePermission(Permissions.JobsRun);
        endpoints.MapPut("/api/ext/clear-all-metadata/settings", async (ClearSettings settings, CancellationToken ct) =>
        {
            await (_store ?? throw new InvalidOperationException("Extension storage is unavailable.")).SetAsync("settings", JsonSerializer.Serialize(settings, Json), ct);
            return Results.Json(settings, Json);
        }).RequireCovePermission(Permissions.SystemSettingsWrite);
        endpoints.MapPost("/api/ext/clear-all-metadata/clear", async (ClearRequest request, HttpContext http, CancellationToken ct) =>
        {
            int[] ids;
            try { ids = SelectionIds(request.EntityIds); }
            catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
            var services = http.RequestServices;
            var principal = services.GetRequiredService<ICurrentPrincipalAccessor>().Current;
            if (principal is null) return Results.Unauthorized();
            var auth = services.GetRequiredService<IAuthorizationService>();
            if (!principal.Has(Permissions.JobsRun))
                return Results.Json(new { message = "Missing jobs.run permission." }, statusCode: 403);
            foreach (var id in ids)
            foreach (var permission in new[] { Permissions.VideosRead, Permissions.VideosWrite })
            {
                var decision = await auth.AuthorizeAsync(principal, permission, EntityRef.Of("video", id), ct);
                if (!decision.Allowed) return Results.Json(new { message = decision.Reason }, statusCode: 403);
            }
            var db = services.GetRequiredService<CoveContext>();
            if (await db.Videos.CountAsync(v => ids.Contains(v.Id), ct) != ids.Length)
                return Results.NotFound(new { message = "One or more selected videos no longer exist." });
            var owner = Snapshot(principal);
            var settings = request.Settings ?? await SettingsAsync(ct);
            if ((settings.Phash || settings.Md5) && !principal.Has(Permissions.FilesWrite))
                return Results.Json(new { message = "Clearing file fingerprints requires files.write permission." }, statusCode: 403);
            var scopes = _scopes ?? throw new InvalidOperationException("Extension is not initialized.");
            var shutdown = _shutdown.Token;
            var jobId = services.GetRequiredService<IJobService>().EnqueueFor(JobOwner.FromPrincipal(owner),
                $"ext:{Id}:clear", ids.Length == 1 ? $"Clear all metadata for video #{ids[0]}" : $"Clear all metadata for {ids.Length} videos", async (progress, jobCt) =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(jobCt, shutdown);
                    await RunBatchAsync(scopes, owner, ids, progress, linked.Token, settings);
                }, resultUrl: ids.Length == 1 ? $"/video/{ids[0]}" : "/videos");
            return Results.Accepted(value: new { jobId, suppressToast = true });
        }).RequireCovePermission(Permissions.VideosWrite);
    }

    public static int[] SelectionIds(int[]? ids)
    {
        if (ids is null || ids.Length == 0 || ids.Length > 10000 || ids.Any(id => id <= 0))
            throw new ArgumentException("Select between 1 and 10000 videos with positive IDs.");
        return ids.Distinct().ToArray();
    }

    public static async Task RunBatchAsync(IExtensionServiceScopeFactory scopes, CovePrincipal principal, int[] ids, Cove.Core.Interfaces.IJobProgress progress, CancellationToken ct, ClearSettings? settings = null)
    {
        ids = SelectionIds(ids);
        progress.DeclareUnits(ids.Select(id => (id.ToString(), (string?)$"Video #{id}")));
        var succeeded = 0;
        var failed = 0;
        for (var index = 0; index < ids.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var id = ids[index];
            using var unit = progress.StartUnit(id.ToString(), $"Video #{id}");
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await RunJobAsync(scope.ServiceProvider, principal, id, new UnitProgress(unit, progress, index, ids.Length), ct, settings);
                unit.Complete(JobUnitOutcome.Succeeded, "Selected fields cleared");
                succeeded++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                unit.Complete(JobUnitOutcome.Failed, ex.Message);
                failed++;
            }
            progress.Report((double)(index + 1) / ids.Length, $"Processed {index + 1}/{ids.Length} videos");
        }
        progress.SetSummary($"{succeeded} videos cleared; {failed} failed.");
        if (failed > 0) throw new InvalidOperationException($"{failed} videos failed. See per-video job details; successful resets were retained.");
    }

    private sealed class UnitProgress(Cove.Core.Interfaces.IJobUnit unit, Cove.Core.Interfaces.IJobProgress overall, int index, int count) : Cove.Core.Interfaces.IJobProgress
    {
        public void Report(double value, string? message = null)
        {
            unit.Report(value, message);
            overall.Report((index + value) / count, message);
        }
        public void SetSummary(string summary) => Report(1, summary);
    }

    public static async Task RunJobAsync(IServiceProvider services, CovePrincipal principal, int id, Cove.Core.Interfaces.IJobProgress progress, CancellationToken ct, ClearSettings? settings = null)
    {
        var accessor = services.GetRequiredService<ICurrentPrincipalAccessor>();
        var previous = accessor.Current;
        accessor.Set(principal);
        try
        {
            var auth = services.GetRequiredService<IAuthorizationService>();
            foreach (var permission in new[] { Permissions.VideosRead, Permissions.VideosWrite, Permissions.JobsRun })
            {
                var decision = await auth.AuthorizeAsync(principal, permission, EntityRef.Of("video", id), ct);
                if (!decision.Allowed) throw new UnauthorizedAccessException(decision.Reason ?? $"Missing {permission}.");
            }
            settings ??= new();
            if ((settings.Phash || settings.Md5) && !principal.Has(Permissions.FilesWrite))
                throw new UnauthorizedAccessException("Clearing file fingerprints requires files.write permission.");
            progress.Report(0, "Clearing selected fields");
            if (!await ClearAsync(services.GetRequiredService<CoveContext>(), services.GetRequiredService<BlobReferenceTransactionCoordinator>(), id, ct, settings))
                throw new InvalidOperationException("Video no longer exists.");
            try
            {
                progress.Report(0.5, "Clearing selected generated assets");
                await services.GetRequiredService<IGeneratedAssetCleaner>().ClearAsync(id, settings, ct);
                if (settings.Cover && !settings.Thumbnails)
                {
                    progress.Report(0.75, "Restoring default frame cover");
                    await services.GetRequiredService<IDefaultCoverResetter>().ResetAsync(id, ct);
                    // Change the media URL only after the replacement is ready,
                    // avoiding a cached response for the pre-regeneration file.
                    var db = services.GetRequiredService<CoveContext>();
                    db.ChangeTracker.Clear();
                    var video = await db.Videos.SingleAsync(v => v.Id == id, ct);
                    video.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
                progress.SetSummary($"Selected fields cleared for video #{id}.");
            }
            finally
            {
                services.GetRequiredService<IEventBus>().Publish(new EntityEvent(EventType.VideoUpdated, "Video", id));
            }
        }
        finally { accessor.Set(previous); }
    }

    private static CovePrincipal Snapshot(CovePrincipal p) => new()
    {
        UserId = p.UserId, Username = p.Username, Kind = p.Kind,
        Roles = p.Roles.ToHashSet(StringComparer.OrdinalIgnoreCase), Permissions = p.Permissions.ToHashSet(StringComparer.OrdinalIgnoreCase),
        ReadRestrictedEntityKinds = p.ReadRestrictedEntityKinds.ToHashSet(StringComparer.OrdinalIgnoreCase),
        ReadGrantedEntityKinds = p.ReadGrantedEntityKinds.ToHashSet(StringComparer.OrdinalIgnoreCase),
        ClaimsPrincipal = p.ClaimsPrincipal is null ? null : new ClaimsPrincipal(p.ClaimsPrincipal), TokenId = p.TokenId, Ip = p.Ip, UserAgent = p.UserAgent
    };

    public static Task<bool> ClearAsync(CoveContext db, BlobReferenceTransactionCoordinator blobs, int id, CancellationToken ct = default, ClearSettings? settings = null)
        => db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            // Re-read on retries so a rolled-back attempt never leaves stale tracked entities.
            db.ChangeTracker.Clear();
            await using var blobTransaction = await blobs.BeginAsync(db, ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var video = await db.Videos.Include(v => v.Urls).Include(v => v.RemoteIds)
                .Include(v => v.VideoTags).Include(v => v.VideoPerformers)
                .FirstOrDefaultAsync(v => v.Id == id, ct);
            if (video is null) return false;
            settings ??= new();
            var applications = settings.Tags ? await db.TagApplications
                .Where(p => p.HostType == AffinityHostType.Video && p.HostId == id).ToListAsync(ct) : [];
            var keptTagIds = settings.AlsoClearAiTags ? new HashSet<int>() : applications
                .Where(p => IsAiTaggingSource(p.SourceKey)).Select(p => p.TagId).ToHashSet();
            Reset(video, settings, keptTagIds);
            if (settings.Tags) db.TagApplications.RemoveRange(applications
                .Where(p => settings.AlsoClearAiTags || !IsAiTaggingSource(p.SourceKey)));
            var keys = settings.ProvenanceKeys();
            db.FieldProvenance.RemoveRange(await db.FieldProvenance
                .Where(p => p.HostType == AffinityHostType.Video && p.HostId == id && keys.Contains(p.FieldKey)
                    && (p.FieldKey != "tags" || settings.AlsoClearAiTags || p.SourceKey != AiTaggingSource)).ToListAsync(ct));
            if (settings.Phash || settings.Md5)
            {
                var fileIds = db.VideoFiles.Where(f => f.VideoId == id).Select(f => f.Id);
                db.FileFingerprints.RemoveRange(await db.FileFingerprints.Where(f => fileIds.Contains(f.FileId)
                    && ((settings.Phash && f.Type == "phash") || (settings.Md5 && f.Type == "md5"))).ToListAsync(ct));
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            await blobTransaction.CompleteAsync();
            return true;
        });

    public const string AiTaggingSource = "ext:ai.tagging";
    public static bool IsAiTaggingSource(string sourceKey) => sourceKey == AiTaggingSource;

    public static void Reset(Video video, ClearSettings? settings = null, IReadOnlySet<int>? keptTagIds = null)
    {
        settings ??= new();
        if (settings.Title) video.Title = null;
        if (settings.Code) video.Code = null;
        if (settings.Details) video.Details = null;
        if (settings.Director) video.Director = null;
        if (settings.Date) { video.Date = null; video.DatePrecision = default; }
        if (settings.Studio) { video.StudioId = null; video.Studio = null; }
        if (settings.Cover) video.ImageBlobId = null;
        if (settings.Organized) video.Organized = false;
        if (settings.Urls) video.Urls.Clear();
        if (settings.RemoteIds) video.RemoteIds.Clear();
        if (settings.Tags) foreach (var link in video.VideoTags
            .Where(link => settings.AlsoClearAiTags || keptTagIds?.Contains(link.TagId) != true).ToArray()) video.VideoTags.Remove(link);
        if (settings.Performers) video.VideoPerformers.Clear();
        video.UpdatedAt = DateTime.UtcNow;
    }
}

public sealed record ClearRequest(int[]? EntityIds, ClearSettings? Settings = null);
