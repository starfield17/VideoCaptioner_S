using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Captioner.Engine;

public static class StageFingerprint
{
    public static string Create(string stage, int version, string upstreamSha256, object settings)
    {
        var payload = JsonSerializer.Serialize(new
        {
            Stage = stage,
            Version = version,
            Upstream = upstreamSha256,
            Settings = settings
        });

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
