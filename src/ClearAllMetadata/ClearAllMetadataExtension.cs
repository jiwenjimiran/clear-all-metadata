using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using System.Security.Claims;
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

public sealed class ClearAllMetadataExtension : IExtension, IUIExtension, IApiExtension
{
    private IExtensionServiceScopeFactory? _scopes;
    private readonly CancellationTokenSource _shutdown = new();
    public string Id => "cove.community.clear-all-metadata";
    public string Name => "clear-all-metadata";
    public string Version => "0.1.1";
    public string? Description => "Clear video identification metadata without rescanning.";
    public string? Author => "jiwenji";
    public string? Url => "https://github.com/jiwenjimiran/clear-all-metadata";
    public string? IconUrl => null;
    public string? MinCoveVersion => "1.5.1";
    public IReadOnlyList<string> Categories => ["video", "tools"];
    public void ConfigureServices(IServiceCollection services, ExtensionContext context) { }
    public Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        _scopes = services.GetRequiredService<IExtensionServiceScopeFactory>();
        return Task.CompletedTask;
    }
    public Task ShutdownAsync(CancellationToken ct = default) => _shutdown.CancelAsync();
    public UIManifest GetUIManifest() => new()
    {
        Actions = [new("clear-all-metadata", "Clear all metadata", Id, "toolbar", ["video"],
            Icon: "eraser", HandlerName: "clearMetadata", Order: 999, SuppressSuccessAlert: true)
            { RequiredPermission = Permissions.VideosWrite }]
    };

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/ext/clear-all-metadata/clear", async (ClearRequest request, HttpContext http, CancellationToken ct) =>
        {
            var ids = request.EntityIds;
            if (ids is not { Length: 1 } || ids[0] <= 0)
                return Results.BadRequest(new { message = "Select exactly one video." });
            var services = http.RequestServices;
            var principal = services.GetRequiredService<ICurrentPrincipalAccessor>().Current;
            if (principal is null) return Results.Unauthorized();
            var auth = services.GetRequiredService<IAuthorizationService>();
            if (!principal.Has(Permissions.JobsRun))
                return Results.Json(new { message = "Missing jobs.run permission." }, statusCode: 403);
            foreach (var permission in new[] { Permissions.VideosRead, Permissions.VideosWrite })
            {
                var decision = await auth.AuthorizeAsync(principal, permission, EntityRef.Of("video", ids[0]), ct);
                if (!decision.Allowed) return Results.Json(new { message = decision.Reason }, statusCode: 403);
            }
            var db = services.GetRequiredService<CoveContext>();
            var id = ids[0];
            if (!await db.Videos.AnyAsync(v => v.Id == id, ct))
                return Results.NotFound(new { message = "Video no longer exists." });
            var owner = Snapshot(principal);
            var scopes = _scopes ?? throw new InvalidOperationException("Extension is not initialized.");
            var shutdown = _shutdown.Token;
            var jobId = services.GetRequiredService<IJobService>().EnqueueFor(JobOwner.FromPrincipal(owner),
                $"ext:{Id}:clear", $"Clear all metadata for video #{id}", async (progress, jobCt) =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(jobCt, shutdown);
                    await using var scope = scopes.CreateAsyncScope();
                    await RunJobAsync(scope.ServiceProvider, owner, id, progress, linked.Token);
                }, resultUrl: $"/video/{id}");
            return Results.Accepted(value: new { jobId, suppressToast = true });
        }).RequireCovePermission(Permissions.VideosWrite);
    }

    public static async Task RunJobAsync(IServiceProvider services, CovePrincipal principal, int id, Cove.Core.Interfaces.IJobProgress progress, CancellationToken ct)
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
            progress.Report(0, "Clearing identification metadata");
            if (!await ClearAsync(services.GetRequiredService<CoveContext>(), services.GetRequiredService<BlobReferenceTransactionCoordinator>(), id, ct))
                throw new InvalidOperationException("Video no longer exists.");
            services.GetRequiredService<IEventBus>().Publish(new EntityEvent(EventType.VideoUpdated, "Video", id));
            progress.SetSummary($"All identification metadata cleared for video #{id}.");
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

    public static Task<bool> ClearAsync(CoveContext db, BlobReferenceTransactionCoordinator blobs, int id, CancellationToken ct = default)
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
            Reset(video);
            db.TagApplications.RemoveRange(await db.TagApplications
                .Where(p => p.HostType == AffinityHostType.Video && p.HostId == id).ToListAsync(ct));
            db.FieldProvenance.RemoveRange(await db.FieldProvenance
                .Where(p => p.HostType == AffinityHostType.Video && p.HostId == id).ToListAsync(ct));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            await blobTransaction.CompleteAsync();
            return true;
        });

    public static void Reset(Video video)
    {
        video.Title = video.Code = video.Details = video.Director = null;
        video.Date = null;
        video.DatePrecision = default;
        video.StudioId = null;
        video.Studio = null;
        video.ImageBlobId = null;
        video.Organized = false;
        video.Urls.Clear();
        video.RemoteIds.Clear();
        video.VideoTags.Clear();
        video.VideoPerformers.Clear();
        video.UpdatedAt = DateTime.UtcNow;
    }
}

public sealed record ClearRequest(int[]? EntityIds);
