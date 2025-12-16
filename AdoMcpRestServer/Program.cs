using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using ModelContextProtocol.AspNetCore;
using AdoMcpRestServer.Tools;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly(Assembly.GetExecutingAssembly());

static string GetRequired(IConfiguration config, string key) =>
    config[key] ?? throw new InvalidOperationException($"Missing configuration: {key}");

var org = GetRequired(builder.Configuration, "AZDO_ORG");
var pat = GetRequired(builder.Configuration, "AZDO_PAT");

builder.Services.AddSingleton(sp =>
{
    var collectionUri = new Uri($"https://dev.azure.com/{org}");
    var credentials = new VssBasicCredential(string.Empty, pat);
    return new VssConnection(collectionUri, credentials);
});

builder.Services.AddSingleton(sp => sp.GetRequiredService<VssConnection>().GetClient<Microsoft.TeamFoundation.Core.WebApi.ProjectHttpClient>());
builder.Services.AddSingleton(sp => sp.GetRequiredService<VssConnection>().GetClient<Microsoft.TeamFoundation.Work.WebApi.WorkHttpClient>());
builder.Services.AddSingleton(sp => sp.GetRequiredService<VssConnection>().GetClient<Microsoft.TeamFoundation.WorkItemTracking.WebApi.WorkItemTrackingHttpClient>());
builder.Services.AddSingleton(sp => sp.GetRequiredService<VssConnection>().GetClient<Microsoft.VisualStudio.Services.Graph.Client.GraphHttpClient>());
builder.Services.AddSingleton(sp => sp.GetRequiredService<VssConnection>().GetClient<Microsoft.TeamFoundation.Core.WebApi.TeamHttpClient>());
builder.Services.AddSingleton(sp => sp.GetRequiredService<VssConnection>().GetClient<Microsoft.VisualStudio.Services.Identity.Client.IdentityHttpClient>());
builder.Services.AddSingleton(sp => sp.GetRequiredService<VssConnection>().GetClient<Microsoft.TeamFoundation.Build.WebApi.BuildHttpClient>());
builder.Services.AddSingleton(sp => sp.GetRequiredService<VssConnection>().GetClient<Microsoft.Azure.Pipelines.WebApi.PipelinesHttpClient>());

builder.Services.AddHttpClient("ado-pat", client =>
{
    client.BaseAddress = new Uri($"https://dev.azure.com/{org}/");
    var token = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($":{pat}"));
    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
    client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
});

var app = builder.Build();

app.MapMcp();

app.Run();
