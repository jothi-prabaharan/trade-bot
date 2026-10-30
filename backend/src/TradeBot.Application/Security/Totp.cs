using System.Security.Cryptography;
using System.Text;

namespace TradeBot.Application.Security;

/// <summary>
/// RFC 6238 TOTP (HMAC-SHA1, 30 s step, 6 digits) from a Base32 secret. The code depends only on the Unix time, so IST
/// and UTC give the same code for the same instant; what matters is that the server clock is right (NTP).
/// </summary>
public static class Totp
{
    public static string Compute(string base32Secret, DateTime utcNow, int digits = 6, int stepSeconds = 30) =>
        ComputeAt(Base32Decode(base32Secret), Step(utcNow, stepSeconds), digits);

    /// <summary>Same instant, any offset: 14:00 IST (+05:30) and 08:30 UTC give the same code.</summary>
    public static string Compute(string base32Secret, DateTimeOffset now, int digits = 6, int stepSeconds = 30) =>
        Compute(base32Secret, now.UtcDateTime, digits, stepSeconds);

    /// <summary>Seconds left in the current step; callers wait for a fresh code when this is small.</summary>
    public static int SecondsRemaining(DateTime utcNow, int stepSeconds = 30) =>
        stepSeconds - (int)((Utc(utcNow) - DateTime.UnixEpoch).TotalSeconds % stepSeconds);

    /// <summary>
    /// Which step <paramref name="code"/> belongs to relative to now: 0 = current, -1 = the previous 30 s, +1 = the next;
    /// null when it matches none within <paramref name="window"/> steps (wrong secret, or clocks minutes apart).
    /// </summary>
    public static int? MatchStep(string base32Secret, string code, DateTime utcNow, int window = 2, int digits = 6, int stepSeconds = 30)
    {
        var key = Base32Decode(base32Secret);
        var now = Step(utcNow, stepSeconds);
        var candidate = new string(code.Where(char.IsDigit).ToArray());
        if (candidate.Length != digits) return null;
        // Nearest steps first, so a code valid in two windows reports the smaller drift
        foreach (var offset in Enumerable.Range(0, window + 1).SelectMany(d => d == 0 ? new[] { 0 } : [-d, d]))
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(ComputeAt(key, now + offset, digits)), Encoding.ASCII.GetBytes(candidate)))
                return offset;
        return null;
    }

    private static string ComputeAt(byte[] key, long counter, int digits)
    {
        Span<byte> msg = stackalloc byte[8];
        for (var i = 7; i >= 0; i--) { msg[i] = (byte)(counter & 0xFF); counter >>= 8; }
        var hash = HMACSHA1.HashData(key, msg);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % (int)Math.Pow(10, digits)).ToString().PadLeft(digits, '0');
    }

    private static long Step(DateTime utcNow, int stepSeconds) => (long)Math.Floor((Utc(utcNow) - DateTime.UnixEpoch).TotalSeconds / stepSeconds);

    // DateTime subtraction ignores Kind: a local (IST) value would be read as UTC and land 5.5 h off
    private static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : t;

    public static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        // Authenticator apps show the secret in lowercase groups ("jbsw y3dp …")
        var clean = new string(input.Where(c => !char.IsWhiteSpace(c)).ToArray()).TrimEnd('=').ToUpperInvariant();
        var bytes = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var ch in clean)
        {
            var v = alphabet.IndexOf(ch);
            if (v < 0) throw new FormatException($"Invalid Base32 character '{ch}'.");
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits < 8) continue;
            bytes.Add((byte)(buffer >> (bits - 8)));
            bits -= 8;
        }
        return [.. bytes];
    }
}
