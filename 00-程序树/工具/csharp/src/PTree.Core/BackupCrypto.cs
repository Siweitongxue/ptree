using System.Security.Cryptography;
using System.Text;

namespace PTree.Core;

/// <summary>
/// 备份凭据加密：AES-256-GCM，随机 12 字节 nonce，密文 = base64(nonce|tag|ciphertext)。
/// 密钥只存在全局配置（%USERPROFILE%\.ptree\global.yaml，不进项目仓库），缺失时报「未配置」，绝不静默生成。
/// 任何对外输出都必须脱敏：token 只显示前 4 后 2，用户名可全显。
/// </summary>
public static class BackupCrypto
{
    public const int KeyBytes = 32;
    public const int NonceBytes = 12;
    public const int TagBytes = 16;

    /// <summary>生成 32 字节随机密钥，返回 base64。</summary>
    public static string NewKeyBase64() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeyBytes));

    public static bool TryParseKey(string? base64, out byte[] key)
    {
        key = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(base64)) return false;
        try
        {
            var bytes = Convert.FromBase64String(base64.Trim());
            if (bytes.Length != KeyBytes) return false;
            key = bytes;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string Encrypt(byte[] key, string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        var tag = new byte[TagBytes];
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, data, cipher, tag);
        var payload = new byte[NonceBytes + TagBytes + cipher.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, NonceBytes);
        Buffer.BlockCopy(tag, 0, payload, NonceBytes, TagBytes);
        Buffer.BlockCopy(cipher, 0, payload, NonceBytes + TagBytes, cipher.Length);
        return Convert.ToBase64String(payload);
    }

    public static string Decrypt(byte[] key, string base64)
    {
        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(base64.Trim());
        }
        catch (FormatException)
        {
            throw new PTreeException("凭据解密失败：credentials.enc 不是合法 base64");
        }
        if (payload.Length < NonceBytes + TagBytes + 1)
        {
            throw new PTreeException("凭据解密失败：credentials.enc 长度不足");
        }
        var nonce = payload[..NonceBytes];
        var tag = payload[NonceBytes..(NonceBytes + TagBytes)];
        var cipher = payload[(NonceBytes + TagBytes)..];
        var data = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, cipher, tag, data);
        }
        catch (CryptographicException)
        {
            throw new PTreeException("凭据解密失败：密钥不匹配或 credentials.enc 被改动");
        }
        return Encoding.UTF8.GetString(data);
    }

    /// <summary>token 脱敏：前 4 后 2；长度不足时整段打码，绝不回显原文。</summary>
    public static string MaskToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return "";
        if (token.Length <= 6) return "****";
        return token[..4] + "****" + token[^2..];
    }
}
