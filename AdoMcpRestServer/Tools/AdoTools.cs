using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Azure.Pipelines.WebApi;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.TeamFoundation.Work.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Identity;
using ModelContextProtocol.Server;

namespace AdoMcpRestServer.Tools;

public record ProjectSummary(string Id, string Name, string State);
public record TeamSummary(string Id, string Name, string ProjectId);
public record IdentitySummary(string Id, string DisplayName, string? MailAddress, string Origin);
public record IterationSummary(string Id, string Name, string Path, DateTime? StartDate, DateTime? FinishDate);
public record NewIteration(string IterationName, string? StartDate, string? FinishDate);
public record CreatedIterations(IReadOnlyList<IterationSummary> Created);

// Iteration/Capacity DTOs
public record IterationNode(string Id, string Name, string Path, DateTime? StartDate, DateTime? FinishDate, List<IterationNode>? Children);
public record IterationToAssign(string Identifier, string Path);
public record AssignedIterationResult(string Id, string Path, bool Success, string? Error);
public record ActivityCapacity(string Name, double CapacityPerDay);
public record DayOff(string Start, string End);
public record CapacityMemberDto(string TeamMemberId, string DisplayName, List<ActivityCapacity> Activities, List<DayOff> DaysOff);
public record TeamCapacityResult(string TeamName, string IterationId, List<CapacityMemberDto> Members);
public record IterationCapacityResult(string IterationId, string Project, List<TeamCapacityResult> Teams);

// Pipeline DTOs
public record BuildChangeDto(string Id, string Message, string Author, string Timestamp, string? Location);
public record BuildChangesResult(List<BuildChangeDto> Changes, string? ContinuationToken);
public record PipelineRunDto(int Id, string Name, string State, string Result, DateTime? CreatedDate, DateTime? FinishedDate, string? Url);
public record BuildStatusDto(int Id, string BuildNumber, string Status, string? Result, string? SourceBranch, string? SourceVersion, DateTime? StartTime, DateTime? FinishTime, string? Url);

[McpServerToolType]
public static class AdoTools
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

    [McpServerTool, Description("Retrieve a list of iterations for a specific team in a project.")]
    public static async Task<IReadOnlyList<IterationSummary>> ListTeamIterations(
        WorkHttpClient workClient,
        [Description("Project name or id")] string project,
        [Description("Team name or id")] string team,
        [Description("Timeframe filter (e.g., current). Optional.")] string? timeframe = null,
        CancellationToken cancellationToken = default
    )
    {
        var iterations = await workClient.GetTeamIterationsAsync(new Microsoft.TeamFoundation.Core.WebApi.Types.TeamContext(project, team), timeframe, cancellationToken);

        return iterations
            .Select(it => new IterationSummary(
                it.Id.ToString(),
                it.Name,
                it.Path,
                it.Attributes?.StartDate,
                it.Attributes?.FinishDate))
            .ToList();
    }

    // [McpServerTool, Description("Create new iterations in a specified Azure DevOps project.")]
    // public static async Task<CreatedIterations> CreateIterations(
    //     WorkItemTrackingHttpClient witClient,
    //     [Description("Project name or id")] string project,
    //     [Description("Iterations to create")] NewIteration[] iterations,
    //     CancellationToken cancellationToken = default
    // )
    // {
    //     var created = new List<IterationSummary>();
    //
    //     foreach (var it in iterations)
    //     {
    //         var node = new WorkItemClassificationNode
    //         {
    //             Name = it.IterationName,
    //             Attributes = new Dictionary<string, object?>()
    //         };
    //
    //         if (!string.IsNullOrWhiteSpace(it.StartDate) && DateTime.TryParse(it.StartDate, out var start))
    //         {
    //             node.Attributes["startDate"] = start;
    //         }
    //
    //         if (!string.IsNullOrWhiteSpace(it.FinishDate) && DateTime.TryParse(it.FinishDate, out var finish))
    //         {
    //             node.Attributes["finishDate"] = finish;
    //         }
    //
    //         var createdNode = await witClient.CreateOrUpdateClassificationNodeAsync(
    //             node,
    //             project,
    //             TreeStructureGroup.Iterations,
    //             cancellationToken: cancellationToken);
    //
    //         created.Add(new IterationSummary(
    //             createdNode.Identifier.ToString(),
    //             createdNode.Name,
    //             createdNode.Path,
    //             createdNode.Attributes?.ContainsKey("startDate") == true ? createdNode.Attributes["startDate"] as DateTime? : null,
    //             createdNode.Attributes?.ContainsKey("finishDate") == true ? createdNode.Attributes["finishDate"] as DateTime? : null));
    //     }
    //
    //     return new CreatedIterations(created);
    // }

    [McpServerTool, Description("List all iterations in a specified Azure DevOps project.")]
    public static async Task<IterationNode> WorkListIterations(
        WorkItemTrackingHttpClient witClient,
        [Description("The name or ID of the Azure DevOps project.")] string project,
        [Description("Depth of children to fetch (default: 2).")] int? depth = 2,
        CancellationToken cancellationToken = default
    )
    {
        var node = await witClient.GetClassificationNodeAsync(
            project,
            TreeStructureGroup.Iterations,
            depth: depth ?? 2,
            cancellationToken: cancellationToken);

        return MapIterationNode(node);
    }

    private static IterationNode MapIterationNode(WorkItemClassificationNode node)
    {
        DateTime? start = node.Attributes?.TryGetValue("startDate", out var s) == true ? s as DateTime? : null;
        DateTime? finish = node.Attributes?.TryGetValue("finishDate", out var f) == true ? f as DateTime? : null;

        var children = node.Children?.Select(MapIterationNode).ToList();

        return new IterationNode(
            node.Identifier.ToString(),
            node.Name,
            node.Path,
            start,
            finish,
            children);
    }

    // [McpServerTool, Description("Assign existing iterations to a specific team in a project.")]
    // public static async Task<IReadOnlyList<AssignedIterationResult>> WorkAssignIterations(
    //     WorkHttpClient workClient,
    //     [Description("The name or ID of the Azure DevOps project.")] string project,
    //     [Description("The name or ID of the Azure DevOps team.")] string team,
    //     [Description("An array of iterations to assign (identifier and path).")] IterationToAssign[] iterations,
    //     CancellationToken cancellationToken = default
    // )
    // {
    //     var results = new List<AssignedIterationResult>();
    //     var teamContext = new Microsoft.TeamFoundation.Core.WebApi.Types.TeamContext(project, team);
    //
    //     foreach (var it in iterations)
    //     {
    //         try
    //         {
    //             var postIteration = new TeamSettingsIteration { Id = Guid.Parse(it.Identifier) };
    //             var assigned = await workClient.PostTeamIterationAsync(postIteration, teamContext, cancellationToken);
    //             results.Add(new AssignedIterationResult(assigned.Id.ToString(), assigned.Path, true, null));
    //         }
    //         catch (Exception ex)
    //         {
    //             results.Add(new AssignedIterationResult(it.Identifier, it.Path, false, ex.Message));
    //         }
    //     }
    //
    //     return results;
    // }

    [McpServerTool, Description("Get the team capacity of a specific team and iteration in a project.")]
    public static async Task<TeamCapacityResult> WorkGetTeamCapacity(
        WorkHttpClient workClient,
        [Description("The name or ID of the Azure DevOps project.")] string project,
        [Description("The name or ID of the Azure DevOps team.")] string team,
        [Description("The Iteration Id to get capacity for.")] string iterationId,
        CancellationToken cancellationToken = default
    )
    {
        var teamContext = new Microsoft.TeamFoundation.Core.WebApi.Types.TeamContext(project, team);
        var capacities = await workClient.GetCapacitiesWithIdentityRefAsync(teamContext, Guid.Parse(iterationId), cancellationToken);

        var members = capacities.Select(c => new CapacityMemberDto(
            c.TeamMember?.Id.ToString() ?? "",
            c.TeamMember?.DisplayName ?? "",
            c.Activities?.Select(a => new ActivityCapacity(a.Name, a.CapacityPerDay)).ToList() ?? new List<ActivityCapacity>(),
            c.DaysOff?.Select(d => new DayOff(d.Start.ToString("o"), d.End.ToString("o"))).ToList() ?? new List<DayOff>()
        )).ToList();

        return new TeamCapacityResult(team, iterationId, members);
    }

    // [McpServerTool, Description("Update the team capacity of a team member for a specific iteration in a project.")]
    // public static async Task<CapacityMemberDto> WorkUpdateTeamCapacity(
    //     WorkHttpClient workClient,
    //     [Description("The name or ID of the Azure DevOps project.")] string project,
    //     [Description("The name or ID of the Azure DevOps team.")] string team,
    //     [Description("The team member Id for the specific team member.")] string teamMemberId,
    //     [Description("The Iteration Id to update the capacity for.")] string iterationId,
    //     [Description("Array of activities and their daily capacities for the team member.")] ActivityCapacity[] activities,
    //     [Description("Array of days off for the team member (optional).")] DayOff[]? daysOff = null,
    //     CancellationToken cancellationToken = default
    // )
    // {
    //     var teamContext = new Microsoft.TeamFoundation.Core.WebApi.Types.TeamContext(project, team);
    //
    //     var patch = new CapacityPatch
    //     {
    //         Activities = activities.Select(a => new Activity { Name = a.Name, CapacityPerDay = (float)a.CapacityPerDay }).ToList(),
    //         DaysOff = daysOff?.Select(d => new DateRange { Start = DateTime.Parse(d.Start), End = DateTime.Parse(d.End) }).ToList() ?? new List<DateRange>()
    //     };
    //
    //     var updated = await workClient.UpdateCapacityWithIdentityRefAsync(patch, teamContext, Guid.Parse(iterationId), Guid.Parse(teamMemberId), cancellationToken);
    //
    //     return new CapacityMemberDto(
    //         updated.TeamMember?.Id.ToString() ?? teamMemberId,
    //         updated.TeamMember?.DisplayName ?? "",
    //         updated.Activities?.Select(a => new ActivityCapacity(a.Name, a.CapacityPerDay)).ToList() ?? new List<ActivityCapacity>(),
    //         updated.DaysOff?.Select(d => new DayOff(d.Start.ToString("o"), d.End.ToString("o"))).ToList() ?? new List<DayOff>()
    //     );
    // }

    [McpServerTool, Description("Get an iteration's capacity for all teams in iteration and project.")]
    public static async Task<IterationCapacityResult> WorkGetIterationCapacities(
        WorkHttpClient workClient,
        TeamHttpClient teamClient,
        [Description("The name or ID of the Azure DevOps project.")] string project,
        [Description("The Iteration Id to get capacity for.")] string iterationId,
        CancellationToken cancellationToken = default
    )
    {
        var teams = await teamClient.GetTeamsAsync(project);
        var teamResults = new List<TeamCapacityResult>();

        foreach (var t in teams)
        {
            try
            {
                var teamContext = new Microsoft.TeamFoundation.Core.WebApi.Types.TeamContext(project, t.Name);
                var capacities = await workClient.GetCapacitiesWithIdentityRefAsync(teamContext, Guid.Parse(iterationId), cancellationToken);

                var members = capacities.Select(c => new CapacityMemberDto(
                    c.TeamMember?.Id.ToString() ?? "",
                    c.TeamMember?.DisplayName ?? "",
                    c.Activities?.Select(a => new ActivityCapacity(a.Name, a.CapacityPerDay)).ToList() ?? new List<ActivityCapacity>(),
                    c.DaysOff?.Select(d => new DayOff(d.Start.ToString("o"), d.End.ToString("o"))).ToList() ?? new List<DayOff>()
                )).ToList();

                teamResults.Add(new TeamCapacityResult(t.Name, iterationId, members));
            }
            catch
            {
                // Team may not have this iteration assigned; skip
            }
        }

        return new IterationCapacityResult(iterationId, project, teamResults);
    }

    // ========== Pipeline Tools ==========

    [McpServerTool, Description("Get the changes associated with a specific build.")]
    public static async Task<BuildChangesResult> PipelinesGetBuildChanges(
        BuildHttpClient buildClient,
        [Description("Project ID or name to get the build changes for")] string project,
        [Description("ID of the build to get changes for")] int buildId,
        [Description("Continuation token for pagination (optional)")] string? continuationToken = null,
        [Description("Number of changes to retrieve, defaults to 100")] int? top = 100,
        [Description("Whether to include source changes in the results")] bool? includeSourceChange = false,
        CancellationToken cancellationToken = default
    )
    {
        var changes = await buildClient.GetBuildChangesAsync(
            project,
            buildId,
            continuationToken,
            top ?? 100,
            includeSourceChange ?? false,
            cancellationToken: cancellationToken);

        var changeDtos = changes.Select(c => new BuildChangeDto(
            c.Id ?? "",
            c.Message ?? "",
            c.Author?.DisplayName ?? "",
            c.Timestamp?.ToString("o") ?? "",
            c.Location?.ToString()
        )).ToList();

        return new BuildChangesResult(changeDtos, null);
    }

    [McpServerTool, Description("Gets a run for a particular pipeline.")]
    public static async Task<PipelineRunDto> PipelinesGetRun(
        PipelinesHttpClient pipelinesClient,
        [Description("Project ID or name")] string project,
        [Description("ID of the pipeline")] int pipelineId,
        [Description("ID of the run to get")] int runId,
        CancellationToken cancellationToken = default
    )
    {
        var run = await pipelinesClient.GetRunAsync(project, pipelineId, runId, cancellationToken: cancellationToken);

        return new PipelineRunDto(
            run.Id,
            run.Name ?? "",
            run.State.ToString(),
            run.Result?.ToString() ?? "",
            run.CreatedDate,
            run.FinishedDate,
            run.Url
        );
    }

    [McpServerTool, Description("Gets top 10000 runs for a particular pipeline.")]
    public static async Task<IReadOnlyList<PipelineRunDto>> PipelinesListRuns(
        PipelinesHttpClient pipelinesClient,
        [Description("Project ID or name")] string project,
        [Description("ID of the pipeline")] int pipelineId,
        CancellationToken cancellationToken = default
    )
    {
        var runs = await pipelinesClient.ListRunsAsync(project, pipelineId, cancellationToken: cancellationToken);

        return runs.Select(r => new PipelineRunDto(
            r.Id,
            r.Name ?? "",
            r.State.ToString(),
            r.Result?.ToString() ?? "",
            r.CreatedDate,
            r.FinishedDate,
            r.Url
        )).ToList();
    }

    // [McpServerTool, Description("Starts a new run of a pipeline.")]
    // public static async Task<PipelineRunDto> PipelinesRunPipeline(
    //     PipelinesHttpClient pipelinesClient,
    //     [Description("Project ID or name to run the build in")] string project,
    //     [Description("ID of the pipeline to run")] int pipelineId,
    //     [Description("Version of the pipeline to run (optional)")] int? pipelineVersion = null,
    //     [Description("If true, returns the final YAML without creating a run (optional)")] bool? previewRun = null,
    //     [Description("Stages to skip (optional)")] string[]? stagesToSkip = null,
    //     [Description("Template parameters as JSON key-value pairs (optional)")] Dictionary<string, string>? templateParameters = null,
    //     CancellationToken cancellationToken = default
    // )
    // {
    //     var runParams = new RunPipelineParameters();
    //
    //     if (previewRun == true)
    //     {
    //         runParams.PreviewRun = true;
    //     }
    //
    //     if (stagesToSkip != null && stagesToSkip.Length > 0)
    //     {
    //         foreach (var stage in stagesToSkip)
    //         {
    //             runParams.StagesToSkip.Add(stage);
    //         }
    //     }
    //
    //     if (templateParameters != null)
    //     {
    //         runParams.TemplateParameters = templateParameters;
    //     }
    //
    //     var run = await pipelinesClient.RunPipelineAsync(runParams, project, pipelineId, pipelineVersion, cancellationToken: cancellationToken);
    //
    //     return new PipelineRunDto(
    //         run.Id,
    //         run.Name ?? "",
    //         run.State.ToString(),
    //         run.Result?.ToString() ?? "",
    //         run.CreatedDate,
    //         run.FinishedDate,
    //         run.Url
    //     );
    // }

    [McpServerTool, Description("Fetches the status of a specific build.")]
    public static async Task<BuildStatusDto> PipelinesGetBuildStatus(
        BuildHttpClient buildClient,
        [Description("Project ID or name to get the build status for")] string project,
        [Description("ID of the build to get the status for")] int buildId,
        CancellationToken cancellationToken = default
    )
    {
        var build = await buildClient.GetBuildAsync(project, buildId, cancellationToken: cancellationToken);

        return new BuildStatusDto(
            build.Id,
            build.BuildNumber ?? "",
            build.Status?.ToString() ?? "",
            build.Result?.ToString(),
            build.SourceBranch,
            build.SourceVersion,
            build.StartTime,
            build.FinishTime,
            build.Url
        );
    }
}