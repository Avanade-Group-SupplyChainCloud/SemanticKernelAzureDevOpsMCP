using System.ComponentModel;
using Microsoft.Azure.Pipelines.WebApi;
using Microsoft.TeamFoundation.Build.WebApi;
using ModelContextProtocol.Server;

namespace AdoMcpRestServer.Tools;

// Pipeline DTOs
public record BuildChangeDto(string Id, string Message, string Author, string Timestamp, string Location);
public record BuildChangesResult(List<BuildChangeDto> Changes, string ContinuationToken);
public record PipelineRunDto(int Id, string Name, string State, string Result, DateTime CreatedDate, DateTime FinishedDate, string Url);
public record BuildStatusDto(int Id, string BuildNumber, string Status, string Result, string SourceBranch, string SourceVersion, DateTime StartTime, DateTime FinishTime, string Url);
public record UpdateBuildStageResult(int BuildId, string StageName, string Status);

[McpServerToolType]
public static class PipelineTools
{
    [McpServerTool, Description("Get the changes associated with a specific build.")]
    public static async Task<BuildChangesResult> PipelinesGetBuildChanges(
        BuildHttpClient buildClient,
        [Description("Project ID or name to get the build changes for")] string project,
        [Description("ID of the build to get changes for")] int buildId,
        [Description("Continuation token for pagination (optional)")] string continuationToken = null,
        [Description("Number of changes to retrieve, defaults to 100")] int top = 100,
        [Description("Whether to include source changes in the results")] bool includeSourceChange = false,
        CancellationToken cancellationToken = default
    )
    {
        var changes = await buildClient.GetBuildChangesAsync(
            project,
            buildId,
            continuationToken,
            top,
            includeSourceChange,
            cancellationToken: cancellationToken);

        var changeDtos = changes.Select(c => new BuildChangeDto(
            c.Id ?? "",
            c.Message ?? "",
            c.Author?.DisplayName ?? "",
            c.Timestamp?.ToString("o") ?? "",
            c.Location?.ToString() ?? ""
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
            run.FinishedDate ?? DateTime.MinValue,
            run.Url ?? ""
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
            r.FinishedDate ?? DateTime.MinValue,
            r.Url ?? ""
        )).ToList();
    }

    [McpServerTool, Description("Starts a new run of a pipeline.")]
    public static async Task<PipelineRunDto> PipelinesRunPipeline(
        PipelinesHttpClient pipelinesClient,
        [Description("Project ID or name to run the build in")] string project,
        [Description("ID of the pipeline to run")] int pipelineId,
        [Description("Version of the pipeline to run (optional)")] int pipelineVersion = 0,
        [Description("If true, returns the final YAML without creating a run (optional)")] bool previewRun = false,
        [Description("Stages to skip (optional)")] string[] stagesToSkip = null,
        [Description("Template parameters as JSON key-value pairs (optional)")] Dictionary<string, string> templateParameters = null,
        CancellationToken cancellationToken = default
    )
    {
        var runParams = new RunPipelineParameters();

        if (previewRun)
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

        var run = await pipelinesClient.RunPipelineAsync(runParams, project, pipelineId, pipelineVersion == 0 ? null : pipelineVersion, cancellationToken: cancellationToken);

        return new PipelineRunDto(
            run.Id,
            run.Name ?? "",
            run.State.ToString(),
            run.Result?.ToString() ?? "",
            run.CreatedDate,
            run.FinishedDate ?? DateTime.MinValue,
            run.Url ?? ""
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
            build.Result?.ToString() ?? "",
            build.SourceBranch ?? "",
            build.SourceVersion ?? "",
            build.StartTime ?? DateTime.MinValue,
            build.FinishTime ?? DateTime.MinValue,
            build.Url ?? ""
        );
    }

    [McpServerTool, Description("Updates the stage of a specific build.")]
    public static async Task<UpdateBuildStageResult> PipelinesUpdateBuildStage(
        BuildHttpClient buildClient,
        [Description("Project ID or name to update the build stage for")] string project,
        [Description("ID of the build to update")] int buildId,
        [Description("Name of the stage to update")] string stageName,
        [Description("New status for the stage: Cancel, Retry, or Run")] string status,
        [Description("Whether to force retry all jobs in the stage")] bool forceRetryAllJobs = false,
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
            ForceRetryAllJobs = forceRetryAllJobs
        };

        await buildClient.UpdateStageAsync(updateParams, project, buildId, stageName, cancellationToken: cancellationToken);

        return new UpdateBuildStageResult(buildId, stageName, status);
    }
}
