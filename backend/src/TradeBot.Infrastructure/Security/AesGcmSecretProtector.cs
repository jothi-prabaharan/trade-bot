using System.Security.Cryptography;
using System.Text;

namespace TradeBot.Infrastructure.Security;

public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}

/// <summary>AES-256-GCM. Stored format: "v1:" + base64(nonce[12] | tag[16] | cipher).</summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const string Prefix = "v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public AesGcmSecretProtector(byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("Master key must be 32 bytes (AES-256).", nameof(key));
        _key = key;
    }

    public static AesGcmSecretProtector FromBase64(string base64Key) => new(Convert.FromBase64String(base64Key));

    public string Protect(string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var buffer = new byte[NonceSize + TagSize + plain.Length];
        var nonce = buffer.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, buffer.AsSpan(NonceSize + TagSize), buffer.AsSpan(NonceSize, TagSize));
        return Prefix + Convert.ToBase64String(buffer);
    }

    public string Unprotect(string ciphertext)
    {
        if (!ciphertext.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CryptographicException("Unsupported secret format.");
        var buffer = Convert.FromBase64String(ciphertext[Prefix.Length..]);
        var plain = new byte[buffer.Length - NonceSize - TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(buffer.AsSpan(0, NonceSize), buffer.AsSpan(NonceSize + TagSize), buffer.AsSpan(NonceSize, TagSize), plain);
        return Encoding.UTF8.GetString(plain);
    }
}
