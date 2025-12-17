using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using ModelContextProtocol.Client;

var mcpClient = await McpClient.CreateAsync(
    new StdioClientTransport(
        new StdioClientTransportOptions
        {
            Name = "ADO",
            Command = "npx",
            Arguments = ["-y", "@azure-devops/mcp", "bhsolutions"],
        }
    )
);

var tools = await mcpClient.ListToolsAsync();

var toolsJsonPath = Path.Combine(AppContext.BaseDirectory, "tools.json");
var toolsJson = JsonSerializer.Serialize(
    tools.Skip(50).Take(5),
    new JsonSerializerOptions { WriteIndented = true }
);
await File.WriteAllTextAsync(toolsJsonPath, toolsJson);
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine($"Saved tool catalog to {toolsJsonPath}");

foreach (var tool in tools)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"Discovered tool: {tool.Name} - {tool.Description}");
}