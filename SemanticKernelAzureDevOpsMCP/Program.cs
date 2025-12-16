using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using ModelContextProtocol.Client;

#pragma warning disable SKEXP0001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

var kernel = Kernel
    .CreateBuilder()
    .AddAzureOpenAIChatCompletion(
        deploymentName: "gpt-4.1",
        endpoint: "https://yo.azure.com/",
        apiKey: ""
    )
    .Build();

var promptExecutionSettings = new PromptExecutionSettings()
{
    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
};

var chatService = kernel.GetRequiredService<IChatCompletionService>();
var chatHistory = new ChatHistory(
    """
       You are an AI assistant who likes to follow the rules.
    """
);

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
    tools.Skip(15).Take(5),
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

kernel.Plugins.AddFromFunctions("ADO", tools.Select(aiFunction => aiFunction.AsKernelFunction()));

while (true)
{
    Console.ForegroundColor = ConsoleColor.White;
    Console.Write("User > ");
    chatHistory.AddUserMessage(Console.ReadLine());

    var updates = chatService.GetStreamingChatMessageContentsAsync(
        chatHistory,
        promptExecutionSettings,
        kernel
    );

    Console.ForegroundColor = ConsoleColor.Green;
    Console.Write("Assistant > ");
    var sb = new StringBuilder();
    await foreach (var update in updates)
    {
        sb.Append(update.Content);
        Console.Write(update.Content);
    }

    chatHistory.AddAssistantMessage(sb.ToString());

    Console.WriteLine();
}