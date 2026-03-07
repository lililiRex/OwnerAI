namespace OwnerAI.Shared.Abstractions;

/// <summary>
/// 秘钥存储接口 (DPAPI 加密)
/// </summary>
public interface ISecretStore
{
    /// <summary>获取秘钥</summary>
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>存储秘钥</summary>
    Task SetAsync(string key, string value, CancellationToken ct = default);

    /// <summary>删除秘钥</summary>
    Task RemoveAsync(string key, CancellationToken ct = default);

    /// <summary>检查秘钥是否存在</summary>
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
}
