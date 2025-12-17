using System.ComponentModel;
using Microsoft.VisualStudio.Services.WebApi.Patch;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.TeamFoundation.Work.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using ModelContextProtocol.Server;

namespace AdoMcpRestServer.Tools;

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

// Backlog DTOs
public record BacklogDto(string Id, string Name, string Type, int? Rank, List<string>? WorkItemTypes);

// Work Item DTOs
public record WorkItemDto(int Id, string Title, string Type, string State, string Url);
public record WorkItemCommentDto(int Id, string Text, string CreatedBy, DateTime CreatedDate);
public record ChildWorkItemInput(string Title, string Description, string? Format, string? AreaPath, string? IterationPath);
public record WorkItemUpdate(string Op, string Path, object Value);
public record WorkItemLinkResult(int WorkItemId, string TargetId, string RelationType, bool Success, string? Error);
public record WorkItemTypeDto(string Name, string Description, List<string> Fields);
public record FieldInput(string Name, string Value, string? Format);
public record QueryHierarchyItemDto(string Id, string Name, string Path, bool IsFolder, bool HasChildren);
public record WorkItemReferenceDto(int Id, string Url);
public record WorkItemQueryResultDto(string QueryType, DateTime AsOf, List<WorkItemReferenceDto> WorkItems);
public record BatchWorkItemUpdate(int Id, string Op, string Path, string Value, string? Format);

[McpServerToolType]
public static class WorkItemTools
{
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

    [McpServerTool, Description("Add comment to a work item by ID.")]
    public static async Task<WorkItemCommentDto> WitAddWorkItemComment(
        WorkItemTrackingHttpClient witClient,
        [Description("The name or ID of the Azure DevOps project.")] string project,
        [Description("The ID of the work item to add a comment to.")] int workItemId,
        [Description("The text of the comment to add to the work item.")] string comment,
        [Description("Format: markdown or html (default html)")] string? format = "html",
        CancellationToken cancellationToken = default
    )
    {
        var request = new CommentCreate
        {
            Text = comment
        };

        var created = await witClient.AddCommentAsync(request, project, workItemId, cancellationToken: cancellationToken);

        return new WorkItemCommentDto(
            created.Id,
            created.Text,
            created.CreatedBy.DisplayName,
            created.CreatedDate
        );
    }

    [McpServerTool, Description("Create one or many child work items from a parent by work item type and parent id.")]
    public static async Task<IReadOnlyList<WorkItemDto>> WitAddChildWorkItems(
        WorkItemTrackingHttpClient witClient,
        [Description("The ID of the parent work item.")] int parentId,
        [Description("The name or ID of the Azure DevOps project.")] string project,
        [Description("The type of the child work item to create.")] string workItemType,
        [Description("List of child items to create.")] ChildWorkItemInput[] items,
        CancellationToken cancellationToken = default
    )
    {
        var createdItems = new List<WorkItemDto>();

        foreach (var item in items)
        {
            var patchDocument = new JsonPatchDocument
            {
                new JsonPatchOperation
                {
                    Operation = Operation.Add,
                    Path = "/fields/System.Title",
                    Value = item.Title
                },
                new JsonPatchOperation
                {
                    Operation = Operation.Add,
                    Path = "/fields/System.Description",
                    Value = item.Description
                }
            };

            if (!string.IsNullOrEmpty(item.AreaPath))
            {
                patchDocument.Add(new JsonPatchOperation
                {
                    Operation = Operation.Add,
                    Path = "/fields/System.AreaPath",
                    Value = item.AreaPath
                });
            }

            if (!string.IsNullOrEmpty(item.IterationPath))
            {
                patchDocument.Add(new JsonPatchOperation
                {
                    Operation = Operation.Add,
                    Path = "/fields/System.IterationPath",
                    Value = item.IterationPath
                });
            }

            // Link to parent
            patchDocument.Add(new JsonPatchOperation
            {
                Operation = Operation.Add,
                Path = "/relations/-",
                Value = new
                {
                    rel = "System.LinkTypes.Hierarchy-Reverse",
                    url = (await witClient.GetWorkItemAsync(project, parentId, cancellationToken: cancellationToken)).Url,
                    attributes = new { comment = "Created via MCP" }
                }
            });

            var created = await witClient.CreateWorkItemAsync(patchDocument, project, workItemType, cancellationToken: cancellationToken);
            
            createdItems.Add(new WorkItemDto(
                created.Id ?? 0,
                created.Fields.TryGetValue("System.Title", out var t) ? t.ToString() ?? "" : "",
                created.Fields.TryGetValue("System.WorkItemType", out var type) ? type.ToString() ?? "" : "",
                created.Fields.TryGetValue("System.State", out var s) ? s.ToString() ?? "" : "",
                created.Url
            ));
        }

        return createdItems;
    }

    [McpServerTool, Description("Link a single work item to an existing pull request.")]
    public static async Task<WorkItemLinkResult> WitLinkWorkItemToPullRequest(
        WorkItemTrackingHttpClient witClient,
        [Description("The project ID of the Azure DevOps project.")] string projectId,
        [Description("The ID of the repository containing the pull request.")] string repositoryId,
        [Description("The ID of the pull request to link to.")] int pullRequestId,
        [Description("The ID of the work item to link to the pull request.")] int workItemId,
        [Description("The project ID containing the pull request (optional).")] string? pullRequestProjectId = null,
        CancellationToken cancellationToken = default
    )
    {
        var prProjectId = pullRequestProjectId ?? projectId;
        // Artifact URI for PR: vstfs:///Git/PullRequestId/{projectId}/{repositoryId}/{pullRequestId}
        var artifactUri = $"vstfs:///Git/PullRequestId/{prProjectId}/{repositoryId}/{pullRequestId}";

        var patchDocument = new JsonPatchDocument
        {
            new JsonPatchOperation
            {
                Operation = Operation.Add,
                Path = "/relations/-",
                Value = new
                {
                    rel = "ArtifactLink",
                    url = artifactUri,
                    attributes = new { name = "Pull Request" }
                }
            }
        };

        try
        {
            await witClient.UpdateWorkItemAsync(patchDocument, workItemId, cancellationToken: cancellationToken);
            return new WorkItemLinkResult(workItemId, pullRequestId.ToString(), "Pull Request", true, null);
        }
        catch (Exception ex)
        {
            return new WorkItemLinkResult(workItemId, pullRequestId.ToString(), "Pull Request", false, ex.Message);
        }
    }

    [McpServerTool, Description("Retrieve a list of work items for a specified iteration.")]
    public static async Task<IReadOnlyList<WorkItemDto>> WitGetWorkItemsForIteration(
        WorkHttpClient workClient,
        WorkItemTrackingHttpClient witClient,
        [Description("The name or ID of the Azure DevOps project.")] string project,
        [Description("The ID of the iteration to retrieve work items for.")] string iterationId,
        [Description("The name or ID of the Azure DevOps team (optional).")] string? team = null,
        CancellationToken cancellationToken = default
    )
    {
        var teamName = team ?? project;
        var teamContext = new Microsoft.TeamFoundation.Core.WebApi.Types.TeamContext(project, teamName);
        
        var iterationWorkItems = await workClient.GetIterationWorkItemsAsync(teamContext, Guid.Parse(iterationId), cancellationToken: cancellationToken);
        
        if (iterationWorkItems.WorkItemRelations == null || !iterationWorkItems.WorkItemRelations.Any())
        {
            return new List<WorkItemDto>();
        }

        var ids = iterationWorkItems.WorkItemRelations.Select(r => r.Target.Id).ToList();
        var workItems = await witClient.GetWorkItemsAsync(ids, fields: new[] { "System.Id", "System.Title", "System.WorkItemType", "System.State" }, cancellationToken: cancellationToken);

        return workItems.Select(wi => new WorkItemDto(
            wi.Id ?? 0,
            wi.Fields.TryGetValue("System.Title", out var t) ? t.ToString() ?? "" : "",
            wi.Fields.TryGetValue("System.WorkItemType", out var type) ? type.ToString() ?? "" : "",
            wi.Fields.TryGetValue("System.State", out var s) ? s.ToString() ?? "" : "",
            wi.Url
        )).ToList();
    }

    [McpServerTool, Description("Update a work item by ID with specified fields.")]
    public static async Task<WorkItemDto> WitUpdateWorkItem(
        WorkItemTrackingHttpClient witClient,
        [Description("The ID of the work item to update.")] int id,
        [Description("An array of field updates to apply to the work item.")] WorkItemUpdate[] updates,
        CancellationToken cancellationToken = default
    )
    {
        var patchDocument = new JsonPatchDocument();

        foreach (var update in updates)
        {
            Operation op = update.Op.ToLowerInvariant() switch
            {
                "add" => Operation.Add,
                "replace" => Operation.Replace,
                "remove" => Operation.Remove,
                "copy" => Operation.Copy,
                "move" => Operation.Move,
                "test" => Operation.Test,
                _ => Operation.Add
            };

            patchDocument.Add(new JsonPatchOperation
            {
                Operation = op,
                Path = update.Path,
                Value = update.Value
            });
        }

        var updated = await witClient.UpdateWorkItemAsync(patchDocument, id, cancellationToken: cancellationToken);

        return new WorkItemDto(
            updated.Id ?? 0,
            updated.Fields.TryGetValue("System.Title", out var t) ? t.ToString() ?? "" : "",
            updated.Fields.TryGetValue("System.WorkItemType", out var type) ? type.ToString() ?? "" : "",
            updated.Fields.TryGetValue("System.State", out var s) ? s.ToString() ?? "" : "",
            updated.Url
        );
    }

    [McpServerTool, Description("Get a specific work item type.")]
    public static async Task<WorkItemTypeDto> WitGetWorkItemType(
        WorkItemTrackingHttpClient witClient,
        [Description("The name or ID of the Azure DevOps project.")] string project,
        [Description("The name of the work item type to retrieve.")] string workItemType,
        CancellationToken cancellationToken = default
    )
    {
        var type = await witClient.GetWorkItemTypeAsync(project, workItemType, cancellationToken: cancellationToken);
        return new WorkItemTypeDto(type.Name, type.Description, type.FieldInstances?.Select(f => f.ReferenceName).ToList() ?? new List<string>());
    }

    [McpServerTool, Description("Create a new work item in a specified project and work item type.")]
    public static async Task<WorkItemDto> WitCreateWorkItem(
        WorkItemTrackingHttpClient witClient,
        [Description("The name or ID of the Azure DevOps project.")] string project,
        [Description("The type of work item to create.")] string workItemType,
        [Description("Fields to set on the new work item.")] FieldInput[] fields,
        CancellationToken cancellationToken = default
    )
    {
        var patchDocument = new JsonPatchDocument();
        foreach(var field in fields)
        {
            patchDocument.Add(new JsonPatchOperation
            {
                Operation = Operation.Add,
                Path = $"/fields/{field.Name}",
                Value = field.Value
            });
        }

        var created = await witClient.CreateWorkItemAsync(patchDocument, project, workItemType, cancellationToken: cancellationToken);
        return new WorkItemDto(
            created.Id ?? 0,
            created.Fields.TryGetValue("System.Title", out var t) ? t.ToString() ?? "" : "",
            created.Fields.TryGetValue("System.WorkItemType", out var type) ? type.ToString() ?? "" : "",
            created.Fields.TryGetValue("System.State", out var s) ? s.ToString() ?? "" : "",
            created.Url
        );
    }

    [McpServerTool, Description("Get a query by its ID or path.")]
    public static async Task<QueryHierarchyItemDto> WitGetQuery(
        WorkItemTrackingHttpClient witClient,
        [Description("The name or ID of the Azure DevOps project.")] string project,
        [Description("The ID or path of the query to retrieve.")] string query,
        [Description("Expand options: None, Wiql, Clauses, All, Minimal")] string? expand = "None",
        [Description("Depth to expand")] int? depth = 0,
        [Description("Include deleted items")] bool? includeDeleted = false,
        CancellationToken cancellationToken = default
    )
    {
        QueryExpand expandEnum = expand?.ToLowerInvariant() switch {
            "wiql" => QueryExpand.Wiql,
            "clauses" => QueryExpand.Clauses,
            "all" => QueryExpand.All,
            "minimal" => QueryExpand.Minimal,
            _ => QueryExpand.None
        };

        var item = await witClient.GetQueryAsync(project, query, expandEnum, depth, includeDeleted, cancellationToken: cancellationToken);
        return new QueryHierarchyItemDto(item.Id.ToString(), item.Name, item.Path, item.IsFolder ?? false, item.HasChildren ?? false);
    }

    [McpServerTool, Description("Retrieve the results of a work item query given the query ID.")]
    public static async Task<WorkItemQueryResultDto> WitGetQueryResultsById(
        WorkItemTrackingHttpClient witClient,
        [Description("The ID of the query.")] string id,
        [Description("The name or ID of the Azure DevOps project.")] string? project = null,
        [Description("The name or ID of the Azure DevOps team.")] string? team = null,
        [Description("Include time precision")] bool? timePrecision = false,
        [Description("Max results")] int? top = 50,
        CancellationToken cancellationToken = default
    )
    {
        WorkItemQueryResult result;
        var queryId = Guid.Parse(id);

        if (!string.IsNullOrEmpty(project))
        {
             var teamContext = new Microsoft.TeamFoundation.Core.WebApi.Types.TeamContext(project, team);
             result = await witClient.QueryByIdAsync(teamContext, queryId, timePrecision, top, cancellationToken: cancellationToken);
        }
        else
        {
             result = await witClient.QueryByIdAsync(queryId, timePrecision: timePrecision, top: top, cancellationToken: cancellationToken);
        }
        
        var items = result.WorkItems?.Select(wi => new WorkItemReferenceDto(wi.Id, wi.Url)).ToList() 
                    ?? result.WorkItemRelations?.Select(wir => new WorkItemReferenceDto(wir.Target.Id, wir.Target.Url)).ToList()
                    ?? new List<WorkItemReferenceDto>();

        return new WorkItemQueryResultDto(result.QueryType.ToString(), result.AsOf, items);
    }

    [McpServerTool, Description("Update work items in batch.")]
    public static async Task<IReadOnlyList<WorkItemDto>> WitUpdateWorkItemsBatch(
        WorkItemTrackingHttpClient witClient,
        [Description("Array of updates to apply.")] BatchWorkItemUpdate[] updates,
        CancellationToken cancellationToken = default
    )
    {
        var results = new List<WorkItemDto>();
        var groupedUpdates = updates.GroupBy(u => u.Id);

        foreach(var group in groupedUpdates)
        {
            var id = group.Key;
            var patchDocument = new JsonPatchDocument();
            foreach(var update in group)
            {
                 Operation op = update.Op.ToLowerInvariant() switch
                {
                    "add" => Operation.Add,
                    "replace" => Operation.Replace,
                    "remove" => Operation.Remove,
                    _ => Operation.Add
                };
                
                patchDocument.Add(new JsonPatchOperation
                {
                    Operation = op,
                    Path = update.Path,
                    Value = update.Value
                });
            }

            try 
            {
                var updated = await witClient.UpdateWorkItemAsync(patchDocument, id, cancellationToken: cancellationToken);
                results.Add(new WorkItemDto(
                    updated.Id ?? 0,
                    updated.Fields.TryGetValue("System.Title", out var t) ? t.ToString() ?? "" : "",
                    updated.Fields.TryGetValue("System.WorkItemType", out var type) ? type.ToString() ?? "" : "",
                    updated.Fields.TryGetValue("System.State", out var s) ? s.ToString() ?? "" : "",
                    updated.Url
                ));
            }
            catch
            {
                // Continue with other items
            }
        }
        return results;
    }
}
