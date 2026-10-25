using System.Security.Cryptography;

namespace TradeBot.Application.Security;

/// <summary>RFC 6238 TOTP (HMAC-SHA1, 30 s step, 6 digits) from a Base32 secret.</summary>
public static class Totp
{
    public static string Compute(string base32Secret, DateTime utcNow, int digits = 6, int stepSeconds = 30)
    {
        var counter = (long)Math.Floor((utcNow - DateTime.UnixEpoch).TotalSeconds / stepSeconds);
        Span<byte> msg = stackalloc byte[8];
        for (var i = 7; i >= 0; i--) { msg[i] = (byte)(counter & 0xFF); counter >>= 8; }
        var hash = HMACSHA1.HashData(Base32Decode(base32Secret), msg);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % (int)Math.Pow(10, digits)).ToString().PadLeft(digits, '0');
    }

    /// <summary>Seconds left in the current step; callers wait for a fresh code when this is small.</summary>
    public static int SecondsRemaining(DateTime utcNow, int stepSeconds = 30) =>
        stepSeconds - (int)((utcNow - DateTime.UnixEpoch).TotalSeconds % stepSeconds);

    public static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var clean = input.Trim().TrimEnd('=').Replace(" ", "").ToUpperInvariant();
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
