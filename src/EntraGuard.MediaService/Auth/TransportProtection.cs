using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EntraGuard.MediaService.Auth;

/// <summary>Short-lived, path-bound capability for ACS callbacks and media upgrades.</summary>
public sealed class TransportProtection(IConfiguration configuration, TimeProvider time)
{
    private byte[] Key()
    {
        var key = configuration["CALLBACK_SIGNING_KEY"] ?? string.Empty;
        if (key.Length < 43) throw new InvalidOperationException("CALLBACK_SIGNING_KEY must contain at least 32 bytes encoded as base64.");
        var bytes = Convert.FromBase64String(key);
        if (bytes.Length < 32) throw new InvalidOperationException("Callback signing key is too short.");
        return bytes;
    }
    public string Url(string url)
    {
        var uri = new Uri(url);
        var expires = time.GetUtcNow().AddMinutes(20).ToUnixTimeSeconds();
        var signature = Sign(uri.AbsolutePath, expires);
        return $"{url}{(uri.Query.Length == 0 ? '?' : '&')}expires={expires}&signature={signature}";
    }
    private string Sign(string path, long expires) => Convert.ToHexString(HMACSHA256.HashData(Key(), Encoding.UTF8.GetBytes($"v1\n{path}\n{expires}")));
    public bool Validate(string path, string expires, string signature)
    {
        if (!long.TryParse(expires, NumberStyles.None, CultureInfo.InvariantCulture, out var exp)
            || exp < time.GetUtcNow().ToUnixTimeSeconds() || exp > time.GetUtcNow().AddMinutes(21).ToUnixTimeSeconds()) return false;
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Sign(path, exp)), Convert.FromHexString(signature)); }
        catch (FormatException) { return false; }
    }
    public bool ValidateEventGrid(string supplied)
    {
        var expected = configuration["EVENTGRID_WEBHOOK_KEY"];
        return expected is { Length: >= 32 } && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(supplied));
    }
}
