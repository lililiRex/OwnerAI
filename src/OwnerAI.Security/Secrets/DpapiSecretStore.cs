using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Security.Secrets;

/// <summary>
/// DPAPI 加密秘钥存储 (Windows)
/// </summary>
public sealed class DpapiSecretStore(ILogger<DpapiSecretStore> logger) : ISecretStore
{
    private static readonly string StoreDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OwnerAI", "secrets");

    public Task<string?> GetAsync(string key, CancellationToken ct)
    {
        var path = GetPath(key);
        if (!File.Exists(path))
            return Task.FromResult<string?>(null);

        try
        {
            var encrypted = File.ReadAllBytes(path);
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Task.FromResult<string?>(Encoding.UTF8.GetString(decrypted));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SecretStore] Failed to decrypt key: {Key}", key);
            return Task.FromResult<string?>(null);
        }
    }

    public Task SetAsync(string key, string value, CancellationToken ct)
    {
        Directory.CreateDirectory(StoreDir);
        var path = GetPath(key);
        var bytes = Encoding.UTF8.GetBytes(value);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, encrypted);
        logger.LogDebug("[SecretStore] Stored key: {Key}", key);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken ct)
    {
        var path = GetPath(key);
        if (File.Exists(path))
        {
            File.Delete(path);
            logger.LogDebug("[SecretStore] Removed key: {Key}", key);
        }
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct)
        => Task.FromResult(File.Exists(GetPath(key)));

    private static string GetPath(string key)
    {
        // 安全的文件名
        var safeName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
        return Path.Combine(StoreDir, $"{safeName}.secret");
    }
}
