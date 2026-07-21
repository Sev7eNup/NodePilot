using Microsoft.Extensions.Caching.Memory;
using NodePilot.Core.Models;

namespace NodePilot.Api.Security;

internal static class UserSessionInvalidation
{
    internal static string UserStateCacheKey(Guid userId) => "tv:user-state:" + userId;

    internal static void BumpSecurityStamp(User user)
    {
        user.SecurityStamp++;
    }

    internal static void InvalidateUserStateCache(IMemoryCache cache, Guid userId)
    {
        cache.Remove(UserStateCacheKey(userId));
    }
}
