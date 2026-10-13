using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Data;
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
    public string Id => "cove.community.clear-all-metadata";
    public string Name => "clear-all-metadata";
    public string Version => "0.1.0";
    public string? Description => "Clear video identification metadata without rescanning.";
    public string? Author => "jiwenji";
    public string? Url => "https://github.com/jiwenjimiran/clear-all-metadata";
    public string? IconUrl => null;
    public string? MinCoveVersion => "1.5.1";
    public IReadOnlyList<string> Categories => ["video", "tools"];
    public void ConfigureServices(IServiceCollection services, ExtensionContext context) { }
    public UIManifest GetUIManifest() => new()
    {
        Actions = [new("clear-all-metadata", "Clear all metadata", Id, "toolbar", ["video"],
            Icon: "eraser", HandlerName: "clearMetadata", Order: 999)
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
            foreach (var permission in new[] { Permissions.VideosRead, Permissions.VideosWrite })
            {
                var decision = await auth.AuthorizeAsync(principal, permission, EntityRef.Of("video", ids[0]), ct);
                if (!decision.Allowed) return Results.Json(new { message = decision.Reason }, statusCode: 403);
            }
            var db = services.GetRequiredService<CoveContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var video = await db.Videos.Include(v => v.Urls).Include(v => v.RemoteIds)
                .Include(v => v.VideoTags).Include(v => v.VideoPerformers)
                .FirstOrDefaultAsync(v => v.Id == ids[0], ct);
            if (video is null) return Results.NotFound(new { message = "Video no longer exists." });
            Reset(video);
            db.TagApplications.RemoveRange(await db.TagApplications
                .Where(p => p.HostType == AffinityHostType.Video && p.HostId == video.Id).ToListAsync(ct));
            db.FieldProvenance.RemoveRange(await db.FieldProvenance
                .Where(p => p.HostType == AffinityHostType.Video && p.HostId == video.Id).ToListAsync(ct));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            services.GetRequiredService<IEventBus>().Publish(new EntityEvent(EventType.VideoUpdated, "Video", video.Id));
            return Results.Ok(new { message = "All identification metadata cleared. You can identify this video again." });
        }).RequireCovePermission(Permissions.VideosWrite);
    }

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
