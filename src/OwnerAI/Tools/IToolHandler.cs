using OwnerAI.Models;

namespace OwnerAI.Tools;

public interface IToolHandler
{
    string Name { get; }
    ToolDefinition GetDefinition();
    Task<string> ExecuteAsync(Dictionary<string, object?> parameters);
}
