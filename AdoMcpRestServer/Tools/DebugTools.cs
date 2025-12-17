using System.ComponentModel;
using ModelContextProtocol.Server;

namespace AdoMcpRestServer.Tools;

[McpServerToolType]
public static class DebugTools
{
    [McpServerTool, Description("A simple ping tool to verify connectivity and authentication.")]
    public static string Ping([Description("Message to echo")] string message)
    {
        return $"Pong: {message}";
    }
}
