namespace Cove.ClearAllMetadata;

public interface IDefaultCoverResetter
{
    Task ResetAsync(int videoId, CancellationToken ct);
}

public sealed class DefaultCoverResetter(IServiceProvider services) : IDefaultCoverResetter
{
    public async Task ResetAsync(int videoId, CancellationToken ct)
    {
        var contract = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Cove.Api.Services.IThumbnailService", false))
            .FirstOrDefault(t => t is not null) ?? throw new NotSupportedException("Cove's cover generator is unavailable.");
        var host = services.GetService(contract) ?? throw new NotSupportedException("Cove's cover generator is unavailable.");
        var method = contract.GetMethod("RegenerateVideoThumbnailAsync", [typeof(int), typeof(double?), typeof(CancellationToken)])
            ?? throw new NotSupportedException("Cove cannot regenerate the default cover.");
        // Null selects Generate's default timestamp. Only the primary cover file
        // is replaced; timestamped thumbnails, VR cards, sprites and previews stay.
        if (!await (Task<bool>)method.Invoke(host, [videoId, null, ct])!)
            throw new InvalidOperationException("Metadata cleared, but default cover restoration failed. Check that the primary video file is readable and FFmpeg is available.");
    }
}
