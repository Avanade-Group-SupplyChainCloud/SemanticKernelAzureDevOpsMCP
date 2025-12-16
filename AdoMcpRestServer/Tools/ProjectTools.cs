using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using Microsoft.TeamFoundation.Core.WebApi;
using ModelContextProtocol.Server;

namespace AdoMcpRestServer.Tools;

public record ProjectSummary(string Id, string Name, string State);
public record TeamSummary(string Id, string Name, string ProjectId);
public record IdentitySummary(string Id, string DisplayName, string? MailAddress, string Origin);

[McpServerToolType]
public static class ProjectTools
{
    [McpServerTool, Description("Retrieve a list of projects in your Azure DevOps organization.")]
    public static async Task<IReadOnlyList<ProjectSummary>> ListProjects(
        ProjectHttpClient projectClient,
        [Description("Project state filter: all, wellFormed, createPending, deleted (optional)")] string? stateFilter = null,
        [Description("Max number of projects to return (optional)")] int? top = null,
        [Description("Number of projects to skip (optional)")] int? skip = null,
        [Description("Filter projects by name (optional)")] string? projectNameFilter = null,
        CancellationToken cancellationToken = default
    )
    {
        ProjectState? state = stateFilter?.ToLowerInvariant() switch
        {
            "wellformed" => ProjectState.WellFormed,
            "createpending" => ProjectState.CreatePending,
            "deleted" => ProjectState.Deleted,
            "all" => null,
            null or "" => null,
            _ => null,
        };

        var projects = await projectClient.GetProjects(state, top, skip, cancellationToken);

        var filtered = string.IsNullOrWhiteSpace(projectNameFilter)
            ? projects
            : projects.Where(p => p.Name.Contains(projectNameFilter, StringComparison.OrdinalIgnoreCase));

        return filtered
            .Select(p => new ProjectSummary(p.Id.ToString(), p.Name, p.State.ToString()))
            .ToList();
    }

    [McpServerTool, Description("Retrieve a list of teams for the specified Azure DevOps project.")]
    public static async Task<IReadOnlyList<TeamSummary>> ListProjectTeams(
        TeamHttpClient teamClient,
        [Description("Project name or id")] string project,
        [Description("If true, only return teams that the authenticated user is a member of (optional)")] bool? mine = null,
        [Description("Max number of teams to return (optional)")] int? top = null,
        [Description("Number of teams to skip (optional)")] int? skip = null,
        CancellationToken cancellationToken = default
    )
    {
        var teams = await teamClient.GetTeamsAsync(project, mine, top, skip);

        return teams
            .Select(t => new TeamSummary(t.Id.ToString(), t.Name, t.ProjectId.ToString()))
            .ToList();
    }

    [McpServerTool, Description("Retrieve Azure DevOps identity IDs for a provided search filter.")]
    public static async Task<JsonDocument> GetIdentityIds(
        IHttpClientFactory httpClientFactory,
        [Description("Search filter (unique name, display name, email)")] string searchFilter,
        CancellationToken cancellationToken = default
    )
    {
        // Azure DevOps .NET SDK does not expose identity search directly; use the Graph REST endpoint with the PAT-authenticated HttpClient.
       var client = httpClientFactory.CreateClient("ado-pat");
        var url = $"_apis/graph/identities?searchFilter=General&filterValue={Uri.EscapeDataString(searchFilter)}&api-version=7.1-preview.1";
        using var response = await client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }
}
