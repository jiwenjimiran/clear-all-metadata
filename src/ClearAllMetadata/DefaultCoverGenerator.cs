using System.Reflection;

namespace Cove.ClearAllMetadata;

public interface IDefaultCoverGenerator
{
    Task<bool> GenerateAsync(int videoId, CancellationToken ct);
}

// The host thumbnail contract lives in Cove's executable, not its extension SDK.
// Resolve the host-owned interface so the package never ships a second Cove API.
public sealed class DefaultCoverGenerator(IServiceProvider services) : IDefaultCoverGenerator
{
    public async Task<bool> GenerateAsync(int videoId, CancellationToken ct)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("Cove.Api.Services.IThumbnailService", false))
            .FirstOrDefault(t => t is not null)
            ?? throw new NotSupportedException("This Cove version does not expose its cover generator.");
        var generator = services.GetService(type)
            ?? throw new NotSupportedException("Cove's cover generator is unavailable.");
        var method = type.GetMethod("RegenerateVideoThumbnailAsync", [typeof(int), typeof(double?), typeof(CancellationToken)])
            ?? throw new NotSupportedException("This Cove version cannot regenerate default covers.");
        // A null timestamp selects exactly the default frame used by Generate.
        try
        {
            return await (Task<bool>)method.Invoke(generator, [videoId, null, ct])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
