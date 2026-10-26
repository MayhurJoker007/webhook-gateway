using System.Security.Cryptography;
using System.Text;

namespace WebhookGateway;

/// <summary>
/// HMAC-SHA256 over "{timestamp}.{body}", sent as "t=1728900000,v1=hex". Signing the timestamp together with the
/// body means an attacker can't take an old valid request and resend it with a fresh timestamp.
/// </summary>
public static class Signatures
{
    public static string Sign(string secret, long timestamp, ReadOnlySpan<byte> body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var prefix = Encoding.UTF8.GetBytes($"{timestamp}.");
        var data = new byte[prefix.Length + body.Length];
        prefix.CopyTo(data, 0);
        body.CopyTo(data.AsSpan(prefix.Length));
        return $"t={timestamp},v1={Convert.ToHexStringLower(hmac.ComputeHash(data))}";
    }

    public enum Result { Valid, Missing, Malformed, Expired, Mismatch }

    public static Result Verify(string? header, string secret, ReadOnlySpan<byte> body, DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrEmpty(header)) return Result.Missing;

        long? timestamp = null;
        var candidates = new List<string>();
        foreach (var part in header.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) return Result.Malformed;
            if (kv[0] == "t" && long.TryParse(kv[1], out var t)) timestamp = t;
            else if (kv[0] == "v1") candidates.Add(kv[1]);   // several v1 values allowed during secret rotation
        }
        if (timestamp is null || candidates.Count == 0) return Result.Malformed;

        var age = now - DateTimeOffset.FromUnixTimeSeconds(timestamp.Value);
        if (age.Duration() > tolerance) return Result.Expired;

        var expected = Encoding.ASCII.GetBytes(Sign(secret, timestamp.Value, body).Split("v1=")[1]);
        foreach (var candidate in candidates)
        {
            // Constant time, so response timing doesn't leak how many characters matched
            if (CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(candidate)))
                return Result.Valid;
        }
        return Result.Mismatch;
    }
}
