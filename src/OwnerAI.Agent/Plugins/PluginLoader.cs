using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Plugins;

/// <summary>
/// 插件加载器 — 收集 DI 注册的 IPlugin 实现，管理 Init→Start→Stop 生命周期
/// </summary>
public sealed class PluginLoader(
    IEnumerable<IPlugin> plugins,
    IServiceProvider services,
    ILogger<PluginLoader> logger) : IHostedService
{
    private readonly List<IPlugin> _activePlugins = [];

    public IReadOnlyList<IPlugin> ActivePlugins => _activePlugins;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var plugin in plugins)
        {
            var typeName = plugin.GetType().Name;
            try
            {
                var manifest = new PluginManifest
                {
                    Id = typeName.ToLowerInvariant(),
                    Name = typeName,
                    Version = "1.0.0",
                    EntryPoint = plugin.GetType().FullName ?? typeName,
                    Description = $"Built-in plugin: {typeName}",
                };

                var dataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "OwnerAI", "plugins", manifest.Id);
                Directory.CreateDirectory(dataDir);

                var context = new PluginContext
                {
                    Manifest = manifest,
                    DataDirectory = dataDir,
                    HostServices = services,
                };

                await plugin.InitializeAsync(context, cancellationToken);
                await plugin.StartAsync(cancellationToken);
                _activePlugins.Add(plugin);

                logger.LogInformation("[PluginLoader] Plugin '{Name}' started successfully", typeName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[PluginLoader] Failed to start plugin '{Name}'", typeName);
            }
        }

        logger.LogInformation("[PluginLoader] {Count} plugin(s) loaded", _activePlugins.Count);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var plugin in _activePlugins)
        {
            var typeName = plugin.GetType().Name;
            try
            {
                await plugin.StopAsync(cancellationToken);
                await plugin.DisposeAsync();
                logger.LogInformation("[PluginLoader] Plugin '{Name}' stopped", typeName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[PluginLoader] Error stopping plugin '{Name}'", typeName);
            }
        }
        _activePlugins.Clear();
    }
}
