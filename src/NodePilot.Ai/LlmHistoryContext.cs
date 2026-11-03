using System.Security.Cryptography;
using System.Text;

namespace NodePilot.Ai;

public static class LlmHistoryContext
{
    // The model is part of the recipient: behind a gateway, one endpoint routes to different providers.
    public static string Key(string profileId, LlmProfileOptions profile)
    {
        var material = $"{profileId.Trim()}\n{profile.BaseUrl}\n{profile.Model}\n{profile.ShareScriptsWithAssistant}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}
