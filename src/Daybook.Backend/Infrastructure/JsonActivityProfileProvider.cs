using Daybook.Backend.Application;
using Daybook.Backend.Options;
using Microsoft.Extensions.Options;

namespace Daybook.Backend.Infrastructure;

public sealed class JsonActivityProfileProvider(IOptionsMonitor<ActivityProfileOptions> options)
    : IActivityProfileProvider
{
    public Task<bool> ExistsAsync(string activityCode, CancellationToken ct) =>
        Task.FromResult(options.CurrentValue.Profiles.Any(x =>
            x.ActivityCode.Equals(activityCode, StringComparison.OrdinalIgnoreCase)));

    public Task<ActivityProcessingProfile> GetAsync(string activityCode, CancellationToken ct)
    {
        var profile = options.CurrentValue.Profiles.FirstOrDefault(x =>
            x.ActivityCode.Equals(activityCode, StringComparison.OrdinalIgnoreCase));
        return profile is null
            ? throw new KeyNotFoundException($"Activity profile '{activityCode}' was not found.")
            : Task.FromResult(profile);
    }

    public Task<IReadOnlyList<ActivityProcessingProfile>> GetAllAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ActivityProcessingProfile>>(options.CurrentValue.Profiles);
}
