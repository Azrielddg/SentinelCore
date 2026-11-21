using System.Security.Cryptography;
using System.Text.Json;

namespace SentinelCore.Demon1.Learning;

/// <summary>Short-lived, single-use handoff proving that the local controller authorized one collector launch.</summary>
public static class OneTimeAuthorizationTicket
{
    private sealed record Ticket(string Sid, DateTimeOffset CreatedUtc, string Nonce);

    public static string Create(string path, string sid)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        var clear = JsonSerializer.SerializeToUtf8Bytes(new Ticket(sid, DateTimeOffset.UtcNow, Convert.ToBase64String(nonce)));
        var protectedData = CurrentUserDpapi.Protect(clear, sid);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllBytes(path, protectedData);
            return Convert.ToBase64String(nonce);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(protectedData);
        }
    }

    public static bool Consume(string path, string proof, string sid)
    {
        if (!File.Exists(path)) return false;
        byte[]? protectedData = null;
        try
        {
            protectedData = File.ReadAllBytes(path);
            var clear = CurrentUserDpapi.Unprotect(protectedData, sid);
            try
            {
                var ticket = JsonSerializer.Deserialize<Ticket>(clear);
                if (ticket is null || !StringComparer.Ordinal.Equals(ticket.Sid, sid)) return false;
                var age = DateTimeOffset.UtcNow - ticket.CreatedUtc;
                if (age < TimeSpan.Zero || age > TimeSpan.FromSeconds(30)) return false;
                var expected = Convert.FromBase64String(ticket.Nonce);
                var actual = Convert.FromBase64String(proof);
                try { return CryptographicOperations.FixedTimeEquals(expected, actual); }
                finally { CryptographicOperations.ZeroMemory(expected); CryptographicOperations.ZeroMemory(actual); }
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or FormatException or JsonException or ArgumentException) { return false; }
        finally
        {
            if (protectedData is not null) CryptographicOperations.ZeroMemory(protectedData);
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}
