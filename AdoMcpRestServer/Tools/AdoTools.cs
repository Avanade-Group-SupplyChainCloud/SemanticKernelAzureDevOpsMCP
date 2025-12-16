using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Azure.Pipelines.WebApi;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.TeamFoundation.Work.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Identity;
using Microsoft.VisualStudio.Services.WebApi;
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
public record UpdateBuildStageResult(int BuildId, string StageName, string Status);

// PR/Branch DTOs
public record BranchCreatedResult(string Name, string CommitId, bool Success, string? Error);
public record PullRequestDto(int Id, string Title, string Status, string SourceBranch, string TargetBranch, string CreatedBy, DateTime CreationDate, string? Url);
public record ReviewerUpdateResult(int PullRequestId, string Action, int ReviewersUpdated);

// Repository DTOs
public record RepositoryDto(string Id, string Name, string DefaultBranch, string? Url, string ProjectId, string ProjectName);
public record BranchDto(string Name, string ObjectId, string? Creator, bool? IsLocked);
public record PrThreadDto(int Id, string Status, string? FilePath, int CommentCount, DateTime? LastUpdated);
public record PrCommentDto(int Id, string Content, string Author, DateTime PublishedDate, int? ParentCommentId);
public record PullRequestDetailDto(int Id, string Title, string Description, string Status, string SourceBranch, string TargetBranch, string CreatedBy, DateTime CreationDate, string? Url, bool IsDraft, List<string>? WorkItemIds, List<string>? ReviewerNames);
public record CommentReplyResult(int CommentId, int ThreadId, string Content, string Author, DateTime PublishedDate);

// Comment Thread DTOs
public record ThreadCreatedResult(int ThreadId, string Status, string Content, string? FilePath, DateTime CreatedDate);
public record ThreadResolvedResult(int ThreadId, string Status, bool Success);

// Commit DTOs
public record CommitDto(string CommitId, string Message, string Author, string AuthorEmail, string Committer, string CommitterEmail, DateTime AuthorDate, DateTime CommitDate, string? Url, List<string>? WorkItemIds);
public record CommitSearchResult(List<CommitDto> Commits, int Count);
public record PullRequestByCommitDto(int PullRequestId, string Title, string Status, string SourceBranch, string TargetBranch, string CommitId);

// Backlog DTOs
public record BacklogDto(string Id, string Name, string Type, int? Rank, List<string>? WorkItemTypes);

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

    [McpServerTool, Description("Create new iterations in a specified Azure DevOps project.")]
    public static async Task<CreatedIterations> CreateIterations(
        WorkItemTrackingHttpClient witClient,
        [Description("Project name or id")] string project,
        [Description("Iterations to create")] NewIteration[] iterations,
        CancellationToken cancellationToken = default
    )
    {
        var created = new List<IterationSummary>();

        foreach (var it in iterations)
        {
            var node = new WorkItemClassificationNode
            {
                Name = it.IterationName,
                Attributes = new Dictionary<string, object?>()
            };

            if (!string.IsNullOrWhiteSpace(it.StartDate) && DateTime.TryParse(it.StartDate, out var start))
            {
                node.Attributes["startDate"] = start;
            }

            if (!string.IsNullOrWhiteSpace(it.FinishDate) && DateTime.TryParse(it.FinishDate, out var finish))
            {
                node.Attributes["finishDate"] = finish;
            }

            var createdNode = await witClient.CreateOrUpdateClassificationNodeAsync(
                node,
                project,
                TreeStructureGroup.Iterations,
                cancellationToken: cancellationToken);

            created.Add(new IterationSummary(
                createdNode.Identifier.ToString(),
                createdNode.Name,
                createdNode.Path,
                createdNode.Attributes?.ContainsKey("startDate") == true ? createdNode.Attributes["startDate"] as DateTime? : null,
                createdNode.Attributes?.ContainsKey("finishDate") == true ? createdNode.Attributes["finishDate"] as DateTime? : null));
        }

        return new CreatedIterations(created);
    }

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

    [McpServerTool, Description("Assign existing iterations to a specific team in a project.")]
    public static async Task<IReadOnlyList<AssignedIterationResult>> WorkAssignIterations(
        WorkHttpClient workClient,
        [Description("The name or ID of the Azure DevOps project.")] string project,
        [Description("The name or ID of the Azure DevOps team.")] string team,
        [Description("An array of iterations to assign (identifier and path).")] IterationToAssign[] iterations,
        CancellationToken cancellationToken = default
    )
    {
        var results = new List<AssignedIterationResult>();
        var teamContext = new Microsoft.TeamFoundation.Core.WebApi.Types.TeamContext(project, team);

        foreach (var it in iterations)
        {
            try
            {
                var postIteration = new TeamSettingsIteration { Id = Guid.Parse(it.Identifier) };
                var assigned = await workClient.PostTeamIterationAsync(postIteration, teamContext, cancellationToken);
                results.Add(new AssignedIterationResult(assigned.Id.ToString(), assigned.Path, true, null));
            }
            catch (Exception ex)
            {
                results.Add(new AssignedIterationResult(it.Identifier, it.Path, false, ex.Message));
            }
        }

        return results;
    }

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

    [McpServerTool, Description("Update the team capacity of a team member for a specific iteration in a project.")]
    public static async Task<CapacityMemberDto> WorkUpdateTeamCapacity(
        WorkHttpClient workClient,
        [Description("The name or ID of the Azure DevOps project.")] string project,
        [Description("The name or ID of the Azure DevOps team.")] string team,
        [Description("The team member Id for the specific team member.")] string teamMemberId,
        [Description("The Iteration Id to update the capacity for.")] string iterationId,
        [Description("Array of activities and their daily capacities for the team member.")] ActivityCapacity[] activities,
        [Description("Array of days off for the team member (optional).")] DayOff[]? daysOff = null,
        CancellationToken cancellationToken = default
    )
    {
        var teamContext = new Microsoft.TeamFoundation.Core.WebApi.Types.TeamContext(project, team);

        var patch = new CapacityPatch
        {
            Activities = activities.Select(a => new Activity { Name = a.Name, CapacityPerDay = (float)a.CapacityPerDay }).ToList(),
            DaysOff = daysOff?.Select(d => new DateRange { Start = DateTime.Parse(d.Start), End = DateTime.Parse(d.End) }).ToList() ?? new List<DateRange>()
        };

        var updated = await workClient.UpdateCapacityWithIdentityRefAsync(patch, teamContext, Guid.Parse(iterationId), Guid.Parse(teamMemberId), cancellationToken);

        return new CapacityMemberDto(
            updated.TeamMember?.Id.ToString() ?? teamMemberId,
            updated.TeamMember?.DisplayName ?? "",
            updated.Activities?.Select(a => new ActivityCapacity(a.Name, a.CapacityPerDay)).ToList() ?? new List<ActivityCapacity>(),
            updated.DaysOff?.Select(d => new DayOff(d.Start.ToString("o"), d.End.ToString("o"))).ToList() ?? new List<DayOff>()
        );
    }

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

    [McpServerTool, Description("Starts a new run of a pipeline.")]
    public static async Task<PipelineRunDto> PipelinesRunPipeline(
        PipelinesHttpClient pipelinesClient,
        [Description("Project ID or name to run the build in")] string project,
        [Description("ID of the pipeline to run")] int pipelineId,
        [Description("Version of the pipeline to run (optional)")] int? pipelineVersion = null,
        [Description("If true, returns the final YAML without creating a run (optional)")] bool? previewRun = null,
        [Description("Stages to skip (optional)")] string[]? stagesToSkip = null,
        [Description("Template parameters as JSON key-value pairs (optional)")] Dictionary<string, string>? templateParameters = null,
        CancellationToken cancellationToken = default
    )
    {
        var runParams = new RunPipelineParameters();

        if (previewRun == true)
        {
            runParams.PreviewRun = true;
        }

        if (stagesToSkip != null && stagesToSkip.Length > 0)
        {
            foreach (var stage in stagesToSkip)
            {
                runParams.StagesToSkip.Add(stage);
            }
        }

        if (templateParameters != null)
        {
            runParams.TemplateParameters = templateParameters;
        }

        var run = await pipelinesClient.RunPipelineAsync(runParams, project, pipelineId, pipelineVersion, cancellationToken: cancellationToken);

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

    [McpServerTool, Description("Updates the stage of a specific build.")]
    public static async Task<UpdateBuildStageResult> PipelinesUpdateBuildStage(
        BuildHttpClient buildClient,
        [Description("Project ID or name to update the build stage for")] string project,
        [Description("ID of the build to update")] int buildId,
        [Description("Name of the stage to update")] string stageName,
        [Description("New status for the stage: Cancel, Retry, or Run")] string status,
        [Description("Whether to force retry all jobs in the stage")] bool? forceRetryAllJobs = false,
        CancellationToken cancellationToken = default
    )
    {
        var stageState = status switch
        {
            "Cancel" => StageUpdateType.Cancel,
            "Retry" => StageUpdateType.Retry,
            "Run" => StageUpdateType.Retry, // Run uses Retry
            _ => throw new ArgumentException($"Invalid status: {status}. Must be Cancel, Retry, or Run.")
        };

        var updateParams = new UpdateStageParameters
        {
            State = stageState,
            ForceRetryAllJobs = forceRetryAllJobs ?? false
        };

        await buildClient.UpdateStageAsync(updateParams, project, buildId, stageName, cancellationToken: cancellationToken);

        return new UpdateBuildStageResult(buildId, stageName, status);
    }

    // ========== Repository/Branch/PR Tools ==========

    [McpServerTool, Description("Create a new branch in the repository.")]
    public static async Task<BranchCreatedResult> RepoCreateBranch(
        GitHttpClient gitClient,
        [Description("The ID of the repository where the branch will be created")] string repositoryId,
        [Description("The name of the new branch to create, e.g., 'feature-branch'")] string branchName,
        [Description("The name of the source branch to create the new branch from. Defaults to 'main'")] string? sourceBranchName = "main",
        [Description("The commit ID to create the branch from. If not provided, uses the latest commit of the source branch")] string? sourceCommitId = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            // Get source commit if not provided
            if (string.IsNullOrEmpty(sourceCommitId))
            {
                var sourceRef = $"refs/heads/{sourceBranchName}";
                var refs = await gitClient.GetRefsAsync(repositoryId, filter: sourceRef, cancellationToken: cancellationToken);
                var sourceBranch = refs.FirstOrDefault();
                if (sourceBranch == null)
                    return new BranchCreatedResult(branchName, "", false, $"Source branch '{sourceBranchName}' not found");
                sourceCommitId = sourceBranch.ObjectId;
            }

            var refUpdate = new GitRefUpdate
            {
                Name = $"refs/heads/{branchName}",
                OldObjectId = new string('0', 40), // All zeros for new branch
                NewObjectId = sourceCommitId
            };

            var results = await gitClient.UpdateRefsAsync(new[] { refUpdate }, repositoryId, cancellationToken: cancellationToken);
            var result = results.FirstOrDefault();

            if (result?.Success == true)
                return new BranchCreatedResult(branchName, sourceCommitId, true, null);
            else
                return new BranchCreatedResult(branchName, "", false, result?.CustomMessage ?? "Failed to create branch");
        }
        catch (Exception ex)
        {
            return new BranchCreatedResult(branchName, "", false, ex.Message);
        }
    }

    [McpServerTool, Description("Create a new pull request.")]
    public static async Task<PullRequestDto> RepoCreatePullRequest(
        GitHttpClient gitClient,
        [Description("The ID of the repository where the pull request will be created")] string repositoryId,
        [Description("The source branch name for the pull request, e.g., 'refs/heads/feature-branch'")] string sourceRefName,
        [Description("The target branch name for the pull request, e.g., 'refs/heads/main'")] string targetRefName,
        [Description("The title of the pull request")] string title,
        [Description("The description of the pull request (max 4000 chars)")] string? description = null,
        [Description("Indicates whether the pull request is a draft")] bool? isDraft = false,
        [Description("Work item IDs to associate, space-separated")] string? workItems = null,
        [Description("The ID of the fork repository (optional, for PRs from forks)")] string? forkSourceRepositoryId = null,
        CancellationToken cancellationToken = default
    )
    {
        var pr = new GitPullRequest
        {
            SourceRefName = sourceRefName,
            TargetRefName = targetRefName,
            Title = title,
            Description = description,
            IsDraft = isDraft ?? false
        };

        if (!string.IsNullOrEmpty(forkSourceRepositoryId))
        {
            pr.ForkSource = new GitForkRef { Repository = new GitRepository { Id = Guid.Parse(forkSourceRepositoryId) } };
        }

        var created = await gitClient.CreatePullRequestAsync(pr, repositoryId, cancellationToken: cancellationToken);

        // Link work items if provided
        if (!string.IsNullOrWhiteSpace(workItems))
        {
            var ids = workItems.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var id in ids)
            {
                if (int.TryParse(id, out var wiId))
                {
                    var artifactLink = new ResourceRef { Id = wiId.ToString(), Url = $"vstfs:///WorkItemTracking/WorkItem/{wiId}" };
                    // Note: Work item linking may require additional API calls
                }
            }
        }

        return new PullRequestDto(
            created.PullRequestId,
            created.Title ?? "",
            created.Status.ToString(),
            created.SourceRefName ?? "",
            created.TargetRefName ?? "",
            created.CreatedBy?.DisplayName ?? "",
            created.CreationDate,
            created.Url
        );
    }

    [McpServerTool, Description("Update a Pull Request by ID with specified fields, including setting autocomplete.")]
    public static async Task<PullRequestDto> RepoUpdatePullRequest(
        GitHttpClient gitClient,
        [Description("The ID of the repository where the pull request exists")] string repositoryId,
        [Description("The ID of the pull request to update")] int pullRequestId,
        [Description("The new title for the pull request")] string? title = null,
        [Description("The new description (max 4000 chars)")] string? description = null,
        [Description("Whether the pull request should be a draft")] bool? isDraft = null,
        [Description("The new target branch name (e.g., 'refs/heads/main')")] string? targetRefName = null,
        [Description("The new status: Active or Abandoned")] string? status = null,
        [Description("Set the pull request to autocomplete when requirements are met")] bool? autoComplete = null,
        [Description("Merge strategy: NoFastForward, Squash, Rebase, RebaseMerge")] string? mergeStrategy = null,
        [Description("Whether to delete the source branch on autocomplete")] bool? deleteSourceBranch = false,
        [Description("Whether to transition work items on autocomplete")] bool? transitionWorkItems = true,
        [Description("Reason for bypassing branch policies")] string? bypassReason = null,
        CancellationToken cancellationToken = default
    )
    {
        // First get the current PR to get autoSetBy identity if needed
        var currentPr = await gitClient.GetPullRequestAsync(repositoryId, pullRequestId, cancellationToken: cancellationToken);

        var update = new GitPullRequest();

        if (title != null) update.Title = title;
        if (description != null) update.Description = description;
        if (isDraft != null) update.IsDraft = isDraft;
        if (targetRefName != null) update.TargetRefName = targetRefName;
        if (status != null)
        {
            update.Status = status switch
            {
                "Active" => PullRequestStatus.Active,
                "Abandoned" => PullRequestStatus.Abandoned,
                _ => throw new ArgumentException($"Invalid status: {status}")
            };
        }

        if (autoComplete == true)
        {
            update.AutoCompleteSetBy = currentPr.CreatedBy;
            update.CompletionOptions = new GitPullRequestCompletionOptions
            {
                DeleteSourceBranch = deleteSourceBranch ?? false,
                TransitionWorkItems = transitionWorkItems ?? true,
                BypassReason = bypassReason,
                BypassPolicy = !string.IsNullOrEmpty(bypassReason),
                MergeStrategy = mergeStrategy switch
                {
                    "Squash" => GitPullRequestMergeStrategy.Squash,
                    "Rebase" => GitPullRequestMergeStrategy.Rebase,
                    "RebaseMerge" => GitPullRequestMergeStrategy.RebaseMerge,
                    _ => GitPullRequestMergeStrategy.NoFastForward
                }
            };
        }
        else if (autoComplete == false)
        {
            update.AutoCompleteSetBy = null;
        }

        var updated = await gitClient.UpdatePullRequestAsync(update, repositoryId, pullRequestId, cancellationToken: cancellationToken);

        return new PullRequestDto(
            updated.PullRequestId,
            updated.Title ?? "",
            updated.Status.ToString(),
            updated.SourceRefName ?? "",
            updated.TargetRefName ?? "",
            updated.CreatedBy?.DisplayName ?? "",
            updated.CreationDate,
            updated.Url
        );
    }

    [McpServerTool, Description("Add or remove reviewers for an existing pull request.")]
    public static async Task<ReviewerUpdateResult> RepoUpdatePullRequestReviewers(
        GitHttpClient gitClient,
        [Description("The ID of the repository where the pull request exists")] string repositoryId,
        [Description("The ID of the pull request to update")] int pullRequestId,
        [Description("List of reviewer IDs to add or remove")] string[] reviewerIds,
        [Description("Action to perform: 'add' or 'remove'")] string action,
        CancellationToken cancellationToken = default
    )
    {
        int count = 0;

        if (action.Equals("add", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var reviewerId in reviewerIds)
            {
                var reviewer = new IdentityRefWithVote { Id = reviewerId };
                await gitClient.CreatePullRequestReviewerAsync(reviewer, repositoryId, pullRequestId, reviewerId, cancellationToken: cancellationToken);
                count++;
            }
        }
        else if (action.Equals("remove", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var reviewerId in reviewerIds)
            {
                await gitClient.DeletePullRequestReviewerAsync(repositoryId, pullRequestId, reviewerId, cancellationToken: cancellationToken);
                count++;
            }
        }
        else
        {
            throw new ArgumentException($"Invalid action: {action}. Must be 'add' or 'remove'.");
        }

        return new ReviewerUpdateResult(pullRequestId, action, count);
    }

    [McpServerTool, Description("Retrieve a list of repositories for a given project.")]
    public static async Task<IReadOnlyList<RepositoryDto>> RepoListReposByProject(
        GitHttpClient gitClient,
        [Description("The name or ID of the Azure DevOps project")] string project,
        [Description("The maximum number of repositories to return")] int? top = 100,
        [Description("The number of repositories to skip")] int? skip = 0,
        [Description("Filter repositories by name (contains)")] string? repoNameFilter = null,
        CancellationToken cancellationToken = default
    )
    {
        var repos = await gitClient.GetRepositoriesAsync(project, cancellationToken: cancellationToken);

        var filtered = repos.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(repoNameFilter))
            filtered = filtered.Where(r => r.Name.Contains(repoNameFilter, StringComparison.OrdinalIgnoreCase));

        return filtered
            .Skip(skip ?? 0)
            .Take(top ?? 100)
            .Select(r => new RepositoryDto(
                r.Id.ToString(),
                r.Name,
                r.DefaultBranch ?? "",
                r.RemoteUrl,
                r.ProjectReference?.Id.ToString() ?? "",
                r.ProjectReference?.Name ?? ""
            ))
            .ToList();
    }

    [McpServerTool, Description("Retrieve a list of pull requests for a repository or project.")]
    public static async Task<IReadOnlyList<PullRequestDto>> RepoListPullRequests(
        GitHttpClient gitClient,
        [Description("The ID of the repository (optional if project provided)")] string? repositoryId = null,
        [Description("The project ID or name (optional if repositoryId provided)")] string? project = null,
        [Description("Maximum number of pull requests to return")] int? top = 100,
        [Description("Number of pull requests to skip")] int? skip = 0,
        [Description("Filter PRs created by current user")] bool? created_by_me = false,
        [Description("Filter PRs created by specific user (email or unique name)")] string? created_by_user = null,
        [Description("Filter PRs where current user is reviewer")] bool? i_am_reviewer = false,
        [Description("Filter PRs where specific user is reviewer (email)")] string? user_is_reviewer = null,
        [Description("Filter by status: NotSet, Active, Abandoned, Completed, All")] string? status = "Active",
        [Description("Filter by source branch (e.g., 'refs/heads/feature')")] string? sourceRefName = null,
        [Description("Filter by target branch (e.g., 'refs/heads/main')")] string? targetRefName = null,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrEmpty(repositoryId) && string.IsNullOrEmpty(project))
            throw new ArgumentException("Either repositoryId or project must be provided.");

        var searchCriteria = new GitPullRequestSearchCriteria
        {
            Status = status switch
            {
                "NotSet" => PullRequestStatus.NotSet,
                "Active" => PullRequestStatus.Active,
                "Abandoned" => PullRequestStatus.Abandoned,
                "Completed" => PullRequestStatus.Completed,
                "All" => PullRequestStatus.All,
                _ => PullRequestStatus.Active
            },
            SourceRefName = sourceRefName,
            TargetRefName = targetRefName
        };

        if (!string.IsNullOrEmpty(created_by_user))
            searchCriteria.CreatorId = Guid.TryParse(created_by_user, out var creatorGuid) ? creatorGuid : null;

        if (!string.IsNullOrEmpty(user_is_reviewer))
            searchCriteria.ReviewerId = Guid.TryParse(user_is_reviewer, out var reviewerGuid) ? reviewerGuid : null;

        List<GitPullRequest> prs;
        if (!string.IsNullOrEmpty(repositoryId))
        {
            prs = await gitClient.GetPullRequestsAsync(repositoryId, searchCriteria, top: top, skip: skip, cancellationToken: cancellationToken);
        }
        else
        {
            prs = await gitClient.GetPullRequestsByProjectAsync(project!, searchCriteria, top: top, skip: skip, cancellationToken: cancellationToken);
        }

        return prs.Select(pr => new PullRequestDto(
            pr.PullRequestId,
            pr.Title ?? "",
            pr.Status.ToString(),
            pr.SourceRefName ?? "",
            pr.TargetRefName ?? "",
            pr.CreatedBy?.DisplayName ?? "",
            pr.CreationDate,
            pr.Url
        )).ToList();
    }

    [McpServerTool, Description("Retrieve a list of comment threads for a pull request.")]
    public static async Task<IReadOnlyList<PrThreadDto>> RepoListPullRequestThreads(
        GitHttpClient gitClient,
        [Description("The ID of the repository")] string repositoryId,
        [Description("The ID of the pull request")] int pullRequestId,
        [Description("Project ID or name (optional)")] string? project = null,
        [Description("Iteration ID (optional, defaults to latest)")] int? iteration = null,
        [Description("Base iteration ID (optional)")] int? baseIteration = null,
        [Description("Maximum number of threads to return")] int? top = 100,
        [Description("Number of threads to skip")] int? skip = 0,
        CancellationToken cancellationToken = default
    )
    {
        var threads = await gitClient.GetThreadsAsync(
            project,
            repositoryId,
            pullRequestId,
            iteration,
            baseIteration,
            cancellationToken: cancellationToken);

        return threads
            .Skip(skip ?? 0)
            .Take(top ?? 100)
            .Select(t => new PrThreadDto(
                t.Id,
                t.Status.ToString(),
                t.ThreadContext?.FilePath,
                t.Comments?.Count ?? 0,
                t.LastUpdatedDate
            ))
            .ToList();
    }

    [McpServerTool, Description("Retrieve a list of comments in a pull request thread.")]
    public static async Task<IReadOnlyList<PrCommentDto>> RepoListPullRequestThreadComments(
        GitHttpClient gitClient,
        [Description("The ID of the repository")] string repositoryId,
        [Description("The ID of the pull request")] int pullRequestId,
        [Description("The ID of the thread")] int threadId,
        [Description("Project ID or name (optional)")] string? project = null,
        [Description("Maximum number of comments to return")] int? top = 100,
        [Description("Number of comments to skip")] int? skip = 0,
        CancellationToken cancellationToken = default
    )
    {
        var comments = await gitClient.GetCommentsAsync(
            repositoryId,
            pullRequestId,
            threadId,
            project,
            cancellationToken: cancellationToken);

        return comments
            .Skip(skip ?? 0)
            .Take(top ?? 100)
            .Select(c => new PrCommentDto(
                c.Id,
                c.Content ?? "",
                c.Author?.DisplayName ?? "",
                c.PublishedDate,
                c.ParentCommentId
            ))
            .ToList();
    }

    [McpServerTool, Description("Retrieve a list of branches for a given repository.")]
    public static async Task<IReadOnlyList<BranchDto>> RepoListBranches(
        GitHttpClient gitClient,
        [Description("The ID of the repository")] string repositoryId,
        [Description("Maximum number of branches to return")] int? top = 100,
        [Description("Filter branches containing this string")] string? filterContains = null,
        CancellationToken cancellationToken = default
    )
    {
        var refs = await gitClient.GetRefsAsync(
            repositoryId,
            filter: "heads/",
            cancellationToken: cancellationToken);

        var branches = refs.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(filterContains))
            branches = branches.Where(b => b.Name.Contains(filterContains, StringComparison.OrdinalIgnoreCase));

        return branches
            .Take(top ?? 100)
            .Select(b => new BranchDto(
                b.Name.Replace("refs/heads/", ""),
                b.ObjectId,
                b.Creator?.DisplayName,
                b.IsLocked
            ))
            .ToList();
    }

    [McpServerTool, Description("Retrieve a list of my branches for a given repository.")]
    public static async Task<IReadOnlyList<BranchDto>> RepoListMyBranches(
        GitHttpClient gitClient,
        IHttpClientFactory httpClientFactory,
        [Description("The ID of the repository")] string repositoryId,
        [Description("Maximum number of branches to return")] int? top = 100,
        [Description("Filter branches containing this string")] string? filterContains = null,
        CancellationToken cancellationToken = default
    )
    {
        // Get all branches and filter by creator matching current user
        var refs = await gitClient.GetRefsAsync(
            repositoryId,
            filter: "heads/",
            cancellationToken: cancellationToken);

        // Get current user info via connection info
        var client = httpClientFactory.CreateClient("ado-pat");
        using var response = await client.GetAsync("_apis/connectionData?api-version=7.1", cancellationToken);
        var connectionData = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var currentUserId = connectionData.RootElement.GetProperty("authenticatedUser").GetProperty("id").GetString();

        var branches = refs.AsEnumerable()
            .Where(b => b.Creator?.Id == currentUserId);

        if (!string.IsNullOrWhiteSpace(filterContains))
            branches = branches.Where(b => b.Name.Contains(filterContains, StringComparison.OrdinalIgnoreCase));

        return branches
            .Take(top ?? 100)
            .Select(b => new BranchDto(
                b.Name.Replace("refs/heads/", ""),
                b.ObjectId,
                b.Creator?.DisplayName,
                b.IsLocked
            ))
            .ToList();
    }

    [McpServerTool, Description("Get the repository by project and repository name or ID.")]
    public static async Task<RepositoryDto> RepoGetByNameOrId(
        GitHttpClient gitClient,
        [Description("Project name or ID")] string project,
        [Description("Repository name or ID")] string repositoryNameOrId,
        CancellationToken cancellationToken = default
    )
    {
        var repo = await gitClient.GetRepositoryAsync(project, repositoryNameOrId, cancellationToken: cancellationToken);

        return new RepositoryDto(
            repo.Id.ToString(),
            repo.Name,
            repo.DefaultBranch ?? "",
            repo.RemoteUrl,
            repo.ProjectReference?.Id.ToString() ?? "",
            repo.ProjectReference?.Name ?? ""
        );
    }

    [McpServerTool, Description("Get a branch by its name.")]
    public static async Task<BranchDto?> RepoGetBranchByName(
        GitHttpClient gitClient,
        [Description("The ID of the repository")] string repositoryId,
        [Description("The name of the branch (e.g., 'main' or 'feature-branch')")] string branchName,
        CancellationToken cancellationToken = default
    )
    {
        var refName = branchName.StartsWith("refs/heads/") ? branchName : $"refs/heads/{branchName}";
        var refs = await gitClient.GetRefsAsync(
            repositoryId,
            filter: refName.Replace("refs/", ""),
            cancellationToken: cancellationToken);

        var branch = refs.FirstOrDefault(r => r.Name == refName);
        if (branch == null)
            return null;

        return new BranchDto(
            branch.Name.Replace("refs/heads/", ""),
            branch.ObjectId,
            branch.Creator?.DisplayName,
            branch.IsLocked
        );
    }

    [McpServerTool, Description("Get a pull request by its ID.")]
    public static async Task<PullRequestDetailDto> RepoGetPullRequestById(
        GitHttpClient gitClient,
        [Description("The ID of the repository")] string repositoryId,
        [Description("The ID of the pull request")] int pullRequestId,
        [Description("Whether to include work item references")] bool? includeWorkItemRefs = false,
        CancellationToken cancellationToken = default
    )
    {
        var pr = await gitClient.GetPullRequestAsync(
            repositoryId,
            pullRequestId,
            cancellationToken: cancellationToken);

        List<string>? workItemIds = null;
        if (includeWorkItemRefs == true)
        {
            var workItems = await gitClient.GetPullRequestWorkItemRefsAsync(
                repositoryId,
                pullRequestId,
                cancellationToken: cancellationToken);
            workItemIds = workItems.Select(w => w.Id).ToList();
        }

        var reviewerNames = pr.Reviewers?.Select(r => r.DisplayName ?? r.Id).ToList();

        return new PullRequestDetailDto(
            pr.PullRequestId,
            pr.Title ?? "",
            pr.Description ?? "",
            pr.Status.ToString(),
            pr.SourceRefName ?? "",
            pr.TargetRefName ?? "",
            pr.CreatedBy?.DisplayName ?? "",
            pr.CreationDate,
            pr.Url,
            pr.IsDraft ?? false,
            workItemIds,
            reviewerNames
        );
    }

    [McpServerTool, Description("Reply to a comment on a pull request thread.")]
    public static async Task<CommentReplyResult> RepoReplyToComment(
        GitHttpClient gitClient,
        [Description("The ID of the repository")] string repositoryId,
        [Description("The ID of the pull request")] int pullRequestId,
        [Description("The ID of the thread to reply to")] int threadId,
        [Description("The content of the reply")] string content,
        [Description("Project ID or name (optional)")] string? project = null,
        CancellationToken cancellationToken = default
    )
    {
        var comment = new Microsoft.TeamFoundation.SourceControl.WebApi.Comment { Content = content };

        var created = await gitClient.CreateCommentAsync(
            comment,
            repositoryId,
            pullRequestId,
            threadId,
            project,
            cancellationToken: cancellationToken);

        return new CommentReplyResult(
            created.Id,
            threadId,
            created.Content ?? "",
            created.Author?.DisplayName ?? "",
            created.PublishedDate
        );
    }

    [McpServerTool, Description("Creates a new comment thread on a pull request.")]
    public static async Task<ThreadCreatedResult> RepoCreatePullRequestThread(
        GitHttpClient gitClient,
        [Description("The ID of the repository where the pull request is located")] string repositoryId,
        [Description("The ID of the pull request where the comment thread will be created")] int pullRequestId,
        [Description("The content of the comment to be added")] string content,
        [Description("Project ID or project name (optional)")] string? project = null,
        [Description("The path of the file where the comment thread will be created (optional)")] string? filePath = null,
        [Description("The status of the comment thread: Unknown, Active, Fixed, WontFix, Closed, ByDesign, Pending. Defaults to Active")] string? status = "Active",
        [Description("Position of first character - line number (1-based, optional)")] int? rightFileStartLine = null,
        [Description("Position of first character - character offset (1-based, optional)")] int? rightFileStartOffset = null,
        [Description("Position of last character - line number (1-based, optional)")] int? rightFileEndLine = null,
        [Description("Position of last character - character offset (optional)")] int? rightFileEndOffset = null,
        CancellationToken cancellationToken = default
    )
    {
        var threadStatus = status switch
        {
            "Unknown" => CommentThreadStatus.Unknown,
            "Active" => CommentThreadStatus.Active,
            "Fixed" => CommentThreadStatus.Fixed,
            "WontFix" => CommentThreadStatus.WontFix,
            "Closed" => CommentThreadStatus.Closed,
            "ByDesign" => CommentThreadStatus.ByDesign,
            "Pending" => CommentThreadStatus.Pending,
            _ => CommentThreadStatus.Active
        };

        var thread = new GitPullRequestCommentThread
        {
            Status = threadStatus,
            Comments = new List<Microsoft.TeamFoundation.SourceControl.WebApi.Comment>
            {
                new Microsoft.TeamFoundation.SourceControl.WebApi.Comment { Content = content }
            }
        };

        // Set file path and position if provided
        if (!string.IsNullOrEmpty(filePath))
        {
            thread.ThreadContext = new CommentThreadContext
            {
                FilePath = filePath
            };

            if (rightFileStartLine.HasValue)
            {
                thread.ThreadContext.RightFileStart = new CommentPosition
                {
                    Line = rightFileStartLine.Value,
                    Offset = rightFileStartOffset ?? 1
                };

                if (rightFileEndLine.HasValue)
                {
                    thread.ThreadContext.RightFileEnd = new CommentPosition
                    {
                        Line = rightFileEndLine.Value,
                        Offset = rightFileEndOffset ?? 1
                    };
                }
            }
        }

        var created = await gitClient.CreateThreadAsync(
            thread,
            repositoryId,
            pullRequestId,
            project,
            cancellationToken: cancellationToken);

        return new ThreadCreatedResult(
            created.Id,
            created.Status.ToString(),
            content,
            filePath,
            created.PublishedDate
        );
    }

    [McpServerTool, Description("Resolves a specific comment thread on a pull request.")]
    public static async Task<ThreadResolvedResult> RepoResolveComment(
        GitHttpClient gitClient,
        [Description("The ID of the repository where the pull request is located")] string repositoryId,
        [Description("The ID of the pull request where the comment thread exists")] int pullRequestId,
        [Description("The ID of the thread to be resolved")] int threadId,
        [Description("Project ID or project name (optional)")] string? project = null,
        CancellationToken cancellationToken = default
    )
    {
        var threadUpdate = new GitPullRequestCommentThread
        {
            Status = CommentThreadStatus.Fixed
        };

        var updated = await gitClient.UpdateThreadAsync(
            threadUpdate,
            repositoryId,
            pullRequestId,
            threadId,
            project,
            cancellationToken: cancellationToken);

        return new ThreadResolvedResult(
            updated.Id,
            updated.Status.ToString(),
            updated.Status == CommentThreadStatus.Fixed
        );
    }

    [McpServerTool, Description("Search for commits in a repository with comprehensive filtering capabilities.")]
    public static async Task<CommitSearchResult> RepoSearchCommits(
        GitHttpClient gitClient,
        [Description("Project name or ID")] string project,
        [Description("Repository name or ID")] string repository,
        [Description("Starting commit ID (optional)")] string? fromCommit = null,
        [Description("Ending commit ID (optional)")] string? toCommit = null,
        [Description("The name of the branch, tag or commit to filter commits by (optional)")] string? version = null,
        [Description("The meaning of the version parameter: Branch, Tag, or Commit. Defaults to Branch")] string? versionType = "Branch",
        [Description("Number of commits to skip")] int? skip = 0,
        [Description("Maximum number of commits to return")] int? top = 10,
        [Description("Include commit links")] bool? includeLinks = false,
        [Description("Include associated work items")] bool? includeWorkItems = false,
        [Description("Search text to filter commits by description/comment (optional)")] string? searchText = null,
        [Description("Filter commits by author email or display name (optional)")] string? author = null,
        [Description("Filter commits from this date (ISO 8601 format, optional)")] string? fromDate = null,
        [Description("Filter commits to this date (ISO 8601 format, optional)")] string? toDate = null,
        [Description("Array of specific commit IDs to retrieve (optional)")] string[]? commitIds = null,
        CancellationToken cancellationToken = default
    )
    {
        var searchCriteria = new GitQueryCommitsCriteria
        {
            FromCommitId = fromCommit,
            ToCommitId = toCommit,
            Author = author,
            IncludeLinks = includeLinks ?? false,
            IncludeWorkItems = includeWorkItems ?? false,
            Skip = skip ?? 0,
            Top = top ?? 10
        };

        // Handle version/branch filtering
        if (!string.IsNullOrEmpty(version))
        {
            searchCriteria.ItemVersion = new GitVersionDescriptor
            {
                Version = version,
                VersionType = versionType switch
                {
                    "Tag" => GitVersionType.Tag,
                    "Commit" => GitVersionType.Commit,
                    _ => GitVersionType.Branch
                }
            };
        }

        // Handle date filtering
        if (!string.IsNullOrEmpty(fromDate) && DateTime.TryParse(fromDate, out var from))
            searchCriteria.FromDate = from.ToString("o");
        if (!string.IsNullOrEmpty(toDate) && DateTime.TryParse(toDate, out var to))
            searchCriteria.ToDate = to.ToString("o");

        // Handle specific commit IDs
        if (commitIds != null && commitIds.Length > 0)
            searchCriteria.Ids = commitIds.ToList();

        var commits = await gitClient.GetCommitsAsync(
            project,
            repository,
            searchCriteria,
            cancellationToken: cancellationToken);

        // Filter by search text if provided (comment/message contains)
        var filteredCommits = commits.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(searchText))
            filteredCommits = filteredCommits.Where(c => c.Comment?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true);

        var commitDtos = filteredCommits.Select(c => new CommitDto(
            c.CommitId,
            c.Comment ?? "",
            c.Author?.Name ?? "",
            c.Author?.Email ?? "",
            c.Committer?.Name ?? "",
            c.Committer?.Email ?? "",
            c.Author?.Date ?? DateTime.MinValue,
            c.Committer?.Date ?? DateTime.MinValue,
            c.RemoteUrl,
            c.WorkItems?.Select(w => w.Id).ToList()
        )).ToList();

        return new CommitSearchResult(commitDtos, commitDtos.Count);
    }

    [McpServerTool, Description("Lists pull requests by commit IDs to find which pull requests contain specific commits.")]
    public static async Task<IReadOnlyList<PullRequestByCommitDto>> RepoListPullRequestsByCommits(
        IHttpClientFactory httpClientFactory,
        [Description("Project name or ID")] string project,
        [Description("Repository name or ID")] string repository,
        [Description("Array of commit IDs to query for")] string[] commits,
        [Description("Type of query: NotSet, LastMergeCommit, or Commit. Defaults to LastMergeCommit")] string? queryType = "LastMergeCommit",
        CancellationToken cancellationToken = default
    )
    {
        // Use REST API since SDK's GitPullRequestQuery doesn't expose Queries property for direct assignment
        var client = httpClientFactory.CreateClient("ado-pat");
        
        var queryBody = new
        {
            queries = new[]
            {
                new
                {
                    items = commits,
                    type = queryType?.ToLowerInvariant() switch
                    {
                        "notset" => "notSet",
                        "commit" => "commit",
                        _ => "lastMergeCommit"
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(queryBody);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        
        var url = $"{project}/_apis/git/repositories/{repository}/pullrequestquery?api-version=7.1";
        using var response = await client.PostAsync(url, content, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
        
        var prDtos = new List<PullRequestByCommitDto>();
        
        if (result.RootElement.TryGetProperty("results", out var results))
        {
            foreach (var queryResult in results.EnumerateArray())
            {
                foreach (var prop in queryResult.EnumerateObject())
                {
                    var commitId = prop.Name;
                    foreach (var pr in prop.Value.EnumerateArray())
                    {
                        prDtos.Add(new PullRequestByCommitDto(
                            pr.GetProperty("pullRequestId").GetInt32(),
                            pr.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                            pr.TryGetProperty("status", out var status) ? status.GetString() ?? "" : "",
                            pr.TryGetProperty("sourceRefName", out var src) ? src.GetString() ?? "" : "",
                            pr.TryGetProperty("targetRefName", out var tgt) ? tgt.GetString() ?? "" : "",
                            commitId
                        ));
                    }
                }
            }
        }

        return prDtos;
    }

    // ========== Work Item Tools ==========

    [McpServerTool, Description("Receive a list of backlogs for a given project and team.")]
    public static async Task<IReadOnlyList<BacklogDto>> WitListBacklogs(
        WorkHttpClient workClient,
        [Description("The name or ID of the Azure DevOps project")] string project,
        [Description("The name or ID of the Azure DevOps team")] string team,
        CancellationToken cancellationToken = default
    )
    {
        var teamContext = new Microsoft.TeamFoundation.Core.WebApi.Types.TeamContext(project, team);
        var backlogs = await workClient.GetBacklogsAsync(teamContext, cancellationToken: cancellationToken);

        return backlogs.Select(b => new BacklogDto(
            b.Id.ToString(),
            b.Name ?? "",
            b.Type.ToString(),
            b.Rank,
            b.WorkItemTypes?.Select(wit => wit.Name).ToList()
        )).ToList();
    }
}