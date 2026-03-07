namespace OwnerAI.Shared.Abstractions;

/// <summary>
/// 插件元数据
/// </summary>
public sealed record PluginManifest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string? Description { get; init; }
    public string? Author { get; init; }
    public required string EntryPoint { get; init; }
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    public IReadOnlyList<string> Permissions { get; init; } = [];
}

/// <summary>
/// 插件上下文
/// </summary>
public sealed record PluginContext
{
    public required PluginManifest Manifest { get; init; }
    public required string DataDirectory { get; init; }
    public required IServiceProvider HostServices { get; init; }
}

/// <summary>
/// 插件接口
/// </summary>
public interface IPlugin : IAsyncDisposable
{
    /// <summary>插件初始化</summary>
    Task InitializeAsync(PluginContext context, CancellationToken ct);

    /// <summary>插件启动</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>插件停止</summary>
    Task StopAsync(CancellationToken ct);
}
