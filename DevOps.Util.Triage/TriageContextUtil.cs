using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DevOps.Util;
using DevOps.Util.DotNet;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.Extensions.Options;

namespace DevOps.Util.Triage
{
    public enum ModelBuildKind
    {
        All,
        Rolling,
        PullRequest,
        MergedPullRequest
    }

    public sealed class TriageContextUtil
    {
        public TriageContext Context { get; }

        public TriageContextUtil(TriageContext context)
        {
            Context = context;
        }

        public static string GetModelBuildId(BuildKey buildKey) => 
            $"{buildKey.Organization}-{buildKey.Project}-{buildKey.Number}";

        public static GitHubPullRequestKey? GetGitHubPullRequestKey(ModelBuild build) =>
            build.PullRequestNumber.HasValue
                ? (GitHubPullRequestKey?)new GitHubPullRequestKey(build.GitHubOrganization, build.GitHubRepository, build.PullRequestNumber.Value)
                : null;

        public async Task<ModelBuildDefinition> EnsureBuildDefinitionAsync(BuildDefinitionInfo definitionInfo)
        {
            var buildDefinition = Context.ModelBuildDefinitions
                .Where(x =>
                    x.AzureOrganization == definitionInfo.Organization &&
                    x.AzureProject == definitionInfo.Project &&
                    x.DefinitionId == definitionInfo.Id)
                .FirstOrDefault();
            if (buildDefinition is object)
            {
                if (buildDefinition.DefinitionName != definitionInfo.Name)
                {
                    buildDefinition.DefinitionName = definitionInfo.Name;
                    await Context.SaveChangesAsync().ConfigureAwait(false);
                }

                return buildDefinition;
            }

            buildDefinition = new ModelBuildDefinition()
            {
                AzureOrganization = definitionInfo.Organization,
                AzureProject = definitionInfo.Project,
                DefinitionId = definitionInfo.Id,
                DefinitionName = definitionInfo.Name,
            };

            Context.ModelBuildDefinitions.Add(buildDefinition);
            await Context.SaveChangesAsync().ConfigureAwait(false);
            return buildDefinition;
        }

        public async Task<ModelBuild> EnsureBuildAsync(BuildInfo buildInfo)
        {
            var modelBuildId = GetModelBuildId(buildInfo.Key);
            var modelBuild = Context.ModelBuilds
                .Where(x => x.Id == modelBuildId)
                .FirstOrDefault();
            if (modelBuild is object)
            {
                if (modelBuild.BuildResult != buildInfo.BuildResult)
                {
                    modelBuild.StartTime = buildInfo.StartTime;
                    modelBuild.FinishTime = buildInfo.FinishTime;
                    modelBuild.BuildResult = buildInfo.BuildResult;
                    await Context.SaveChangesAsync().ConfigureAwait(false);
                }

                return modelBuild;
            }

            var prKey = buildInfo.PullRequestKey;
            var modelBuildDefinition = await EnsureBuildDefinitionAsync(buildInfo.DefinitionInfo).ConfigureAwait(false);
            modelBuild = new ModelBuild()
            {
                Id = modelBuildId,
                ModelBuildDefinitionId = modelBuildDefinition.Id,
                GitHubOrganization = buildInfo.GitHubInfo?.Organization ?? null,
                GitHubRepository = buildInfo.GitHubInfo?.Repository ?? null,
                PullRequestNumber = prKey?.Number,
                StartTime = buildInfo.StartTime,
                FinishTime = buildInfo.FinishTime,
                BuildNumber = buildInfo.Number,
                BuildResult = buildInfo.BuildResult,
            };
            Context.ModelBuilds.Add(modelBuild);
            Context.SaveChanges();
            return modelBuild;
        }

        public async Task EnsureResultAsync(ModelBuild modelBuild, Build build)
        {
            if (modelBuild.BuildResult != build.Result)
            {
                var buildInfo = build.GetBuildInfo();
                modelBuild.BuildResult = build.Result;
                modelBuild.StartTime = buildInfo.StartTime;
                modelBuild.FinishTime = buildInfo.FinishTime;
                await Context.SaveChangesAsync().ConfigureAwait(false);
            }
        }

        public async Task<ModelBuildAttempt> EnsureBuildAttemptAsync(BuildInfo buildInfo, Timeline timeline)
        {
            var modelBuild = await EnsureBuildAsync(buildInfo).ConfigureAwait(false);
            return await EnsureBuildAttemptAsync(modelBuild, buildInfo.BuildResult, timeline).ConfigureAwait(false);
        }

        public async Task<ModelBuildAttempt> EnsureBuildAttemptAsync(ModelBuild modelBuild, BuildResult buildResult, Timeline timeline)
        {
            var attempt = timeline.GetAttempt();
            var modelBuildAttempt = await Context.ModelBuildAttempts
                .Where(x => x.ModelBuildId == modelBuild.Id && x.Attempt == attempt)
                .FirstOrDefaultAsync().ConfigureAwait(false);
            if (modelBuildAttempt is object)
            {
                return modelBuildAttempt;
            }

            var startTimeQuery = timeline
                .Records
                .Where(x => x.Attempt == attempt)
                .Select(x => DevOpsUtil.ConvertFromRestTime(x.StartTime))
                .SelectNullableValue()
                .Select(x => (DateTime?)x.DateTime);
            var startTime = startTimeQuery.Any()
                ? startTimeQuery.Min()
                : modelBuild.StartTime;
            
            var finishTimeQuery = timeline
                .Records
                .Where(x => x.Attempt == attempt)
                .Select(x => DevOpsUtil.ConvertFromRestTime(x.FinishTime))
                .SelectNullableValue()
                .Select(x => (DateTime?)x.DateTime);
            var finishTime = finishTimeQuery.Any()
                ? finishTimeQuery.Max()
                : modelBuild.FinishTime;

            modelBuildAttempt = new ModelBuildAttempt()
            {
                Attempt = attempt,
                BuildResult = buildResult,
                StartTime = startTime,
                FinishTime = finishTime,
                ModelBuild = modelBuild,
            };
            Context.ModelBuildAttempts.Add(modelBuildAttempt);

            var timelineTree = TimelineTree.Create(timeline);
            foreach (var record in timeline.Records)
            {
                if (record.Issues is null ||
                    !timelineTree.TryGetJob(record, out var job))
                {
                    continue;
                }

                foreach (var issue in record.Issues)
                {
                    var timelineIssue = new ModelTimelineIssue()
                    {
                        Attempt = attempt,
                        JobName = job.Name,
                        RecordName = record.Name,
                        RecordId = record.Id,
                        Message = issue.Message,
                        ModelBuild = modelBuild,
                        IssueType = issue.Type,
                        ModelBuildAttempt = modelBuildAttempt,
                    };
                    Context.ModelTimelineIssues.Add(timelineIssue);
                }
            }

            await Context.SaveChangesAsync().ConfigureAwait(false);
            return modelBuildAttempt;
        }

        public Task<ModelBuild> FindModelBuildAsync(string organization, string project, int buildNumber) =>
            Context.
            ModelBuilds
            .Where(x =>
                x.BuildNumber == buildNumber &&
                x.ModelBuildDefinition.AzureOrganization == organization &&
                x.ModelBuildDefinition.AzureProject == project)
            .FirstOrDefaultAsync();

        public Task<ModelTestRun> FindModelTestRunAsync(ModelBuild modelBuild, int testRunid) =>
            Context
            .ModelTestRuns
            .Where(x => x.ModelBuildId == modelBuild.Id && x.TestRunId == testRunid)
            .FirstOrDefaultAsync();

        public async Task<ModelTestRun> EnsureTestRunAsync(ModelBuild modelBuild, int attempt, DotNetTestRun testRun, Dictionary<HelixInfo, HelixLogInfo> helixMap)
        {
            var modelTestRun = await FindModelTestRunAsync(modelBuild, testRun.TestRun.Id).ConfigureAwait(false);
            if (modelTestRun is object)
            {
                return modelTestRun;
            }

            var buildInfo = testRun.Build.GetBuildInfo();
            modelTestRun = new ModelTestRun()
            {
                AzureOrganization = buildInfo.Organization,
                AzureProject = buildInfo.Project,
                ModelBuild = modelBuild,
                TestRunId = testRun.TestRun.Id,
                Name = testRun.TestRun.Name,
                Attempt = attempt,
            };
            Context.ModelTestRuns.Add(modelTestRun);

            foreach (var dotnetTestCaseResult in testRun.TestCaseResults)
            {
                var testCaseResult = dotnetTestCaseResult.TestCaseResult;
                var testResult = new ModelTestResult()
                {
                    TestFullName = testCaseResult.TestCaseTitle,
                    Outcome = testCaseResult.Outcome,
                    ModelTestRun = modelTestRun,
                    ModelBuild = modelBuild,
                };

                if (dotnetTestCaseResult.HelixInfo is { } helixInfo &&
                    helixMap.TryGetValue(helixInfo, out var helixLogInfo))
                {
                    testResult.IsHelixTestResult = true;
                    testResult.HelixConsoleUri = helixLogInfo.ConsoleUri;
                    testResult.HelixCoreDumpUri = helixLogInfo.CoreDumpUri;
                    testResult.HelixRunClientUri = helixLogInfo.RunClientUri;
                    testResult.HelixTestResultsUri = helixLogInfo.TestResultsUri;
                }

                Context.ModelTestResults.Add(testResult);
            }

            await Context.SaveChangesAsync().ConfigureAwait(false);
            return modelTestRun;
        }

        /// <summary>
        /// Determine if this build has already been processed for this query
        /// </summary>
        public bool IsProcessed(ModelTriageIssue modelTriageIssue, ModelBuild modelBuild) =>
            Context.ModelTriageIssueResultCompletes.Any(x =>
                x.ModelTriageIssueId == modelTriageIssue.Id &&
                x.ModelBuildId == modelBuild.Id);

        public bool TryGetTriageIssue(
            SearchKind searchKind, 
            string searchText,
            [NotNullWhen(true)] out ModelTriageIssue? modelTriageIssue)
        {
            modelTriageIssue = Context.ModelTriageIssues
                .Include(x => x.ModelTriageGitHubIssues)
                .Where(x => x.SearchKind == searchKind && x.SearchText == searchText)
                .FirstOrDefault();
            return modelTriageIssue is object;
        }
        public bool TryGetTriageIssue(
            GitHubIssueKey issueKey,
            [NotNullWhen(true)] out ModelTriageIssue? modelTriageIssue)
        {
            var model = Context.ModelTriageGitHubIssues
                .Include(x => x.ModelTriageIssue)
                .Where(x => 
                    x.Organization == issueKey.Organization &&
                    x.Repository == issueKey.Repository && 
                    x.IssueNumber == issueKey.Number)
                .FirstOrDefault();
            if (model is object)
            {
                modelTriageIssue = model.ModelTriageIssue;
                return true;
            }
            else
            {
                modelTriageIssue = null;
                return false;
            }
        }

        public ModelTriageIssue EnsureTriageIssue(
            TriageIssueKind issueKind,
            SearchKind searchKind, 
            string searchText,
            params ModelTriageGitHubIssue[] gitHubIssues)
        {
            if (TryGetTriageIssue(searchKind, searchText, out var modelTriageIssue))
            {
                if (modelTriageIssue.TriageIssueKind != issueKind)
                {
                    modelTriageIssue.TriageIssueKind = issueKind;
                }
            }
            else
            {
                modelTriageIssue = new ModelTriageIssue()
                {
                    TriageIssueKind = issueKind,
                    SearchKind = searchKind,
                    SearchText = searchText,
                    ModelTriageGitHubIssues = new List<ModelTriageGitHubIssue>(),
                };
                Context.ModelTriageIssues.Add(modelTriageIssue);
            }

            foreach (var gitHubIssue in gitHubIssues)
            {
                var existing = modelTriageIssue.ModelTriageGitHubIssues
                    .Where(x => x.IssueKey.IssueUri == gitHubIssue.IssueKey.IssueUri)
                    .FirstOrDefault();
                if (existing is null)
                {
                    modelTriageIssue.ModelTriageGitHubIssues.Add(gitHubIssue);
                }
                else
                {
                    existing.SearchBuildsQueryString = gitHubIssue.SearchBuildsQueryString;
                    existing.IncludeDefinitions = gitHubIssue.IncludeDefinitions;
                }
            }

            Context.SaveChanges();

            return modelTriageIssue;
        }

        public async Task<List<ModelTriageIssueResult>> FindModelTriageIssueResultsAsync(ModelTriageIssue triageIssue, ModelTriageGitHubIssue triageGitHubIssue)
        {
            var searchBuildsRequest = new SearchBuildsRequest();
            searchBuildsRequest.ParseQueryString(triageGitHubIssue.SearchBuildsQueryString ?? "");

            var query = Context
                .ModelTriageIssueResults
                .Include(x => x.ModelBuild)
                .ThenInclude(x => x.ModelBuildDefinition)
                .Where(x => x.ModelTriageIssueId == triageIssue.Id);

            // TODO: need to find a way to use the full flexibility of SearchBuildsRequest here
            if (!string.IsNullOrEmpty(searchBuildsRequest.Repository))
            {
                query = query.Where(x => x.ModelBuild.GitHubRepository == searchBuildsRequest.Repository);
            }
            else if (searchBuildsRequest.DefinitionId is { } definitionId)
            {
                query = query.Where(x => x.ModelBuild.ModelBuildDefinition.DefinitionId == definitionId);
            }
            else
            {
                query = query.Where(x => x.ModelBuild.ModelBuildDefinition.AzureProject == DotNetUtil.DefaultAzureProject);
            }

            var list = await (query
                .OrderByDescending(x => x.BuildNumber)
                .ToListAsync()).ConfigureAwait(false);
            return list;
        }

        public List<ModelBuild> FindModelBuildsByDefinition(ModelTriageIssue modelTriageIssue, string project, int definitionId, int count) =>
            Context.ModelTriageIssueResults
                .Include(x => x.ModelBuild)
                .ThenInclude(x => x.ModelBuildDefinition)
                .Where(x =>
                    x.ModelTriageIssueId == modelTriageIssue.Id &&
                    x.ModelBuild.ModelBuildDefinition.AzureProject == project &&
                    x.ModelBuild.ModelBuildDefinitionId == definitionId)
                .Select(x => x.ModelBuild)
                .OrderByDescending(x => x.BuildNumber)
                .Take(count)
                .ToList();

        public List<ModelBuild> FindModelBuildsByRepository(ModelTriageIssue modelTriageIssue, string organization, string repository, int count) =>
            Context.ModelTriageIssueResults
                .Include(x => x.ModelBuild)
                .Where(x =>
                    x.ModelTriageIssueId == modelTriageIssue.Id &&
                    x.ModelBuild.GitHubOrganization == organization &&
                    x.ModelBuild.GitHubRepository == repository)
                .Select(x => x.ModelBuild)
                .OrderByDescending(x => x.BuildNumber)
                .Take(count)
                .ToList();

        public IQueryable<ModelBuild> GetModelBuildsQuery(
            bool descendingOrder = true,
            int? definitionId = null,
            string? definitionName = null,
            ModelBuildKind kind = ModelBuildKind.All,
            string? gitHubRepository = null,
            string? gitHubOrganization = null,
            int? count = null)
        {
            if (definitionId is object && definitionName is object)
            {
                throw new Exception($"Cannot specify {nameof(definitionId)} and {nameof(definitionName)}");
            }

            // Need to always include ModelBuildDefinition at this point because the GetBuildKey function
            // depends on that being there.
            IQueryable<ModelBuild> query = Context
                .ModelBuilds
                .Include(x => x.ModelBuildDefinition);

            query = descendingOrder
                ? query.OrderByDescending(x => x.BuildNumber)
                : query.OrderBy(x => x.BuildNumber);

            if (definitionId is { } d)
            {
                query = query.Where(x => x.ModelBuildDefinition.DefinitionId == definitionId);
            }
            else if (definitionName is object)
            {
                query = query.Where(x => EF.Functions.Like(definitionName, x.ModelBuildDefinition.DefinitionName));
            }

            if (gitHubOrganization is object)
            {
                gitHubOrganization = gitHubOrganization.ToLower();
                query = query.Where(x => x.GitHubOrganization == gitHubOrganization);
            }

            if (gitHubRepository is object)
            {
                gitHubRepository = gitHubRepository.ToLower();
                query = query.Where(x => x.GitHubRepository == gitHubRepository);
            }

            switch (kind)
            {
                case ModelBuildKind.All:
                    // Nothing to filter
                    break;
                case ModelBuildKind.MergedPullRequest:
                    query = query.Where(x => x.IsMergedPullRequest);
                    break;
                case ModelBuildKind.PullRequest:
                    query = query.Where(x => x.PullRequestNumber.HasValue);
                    break;
                case ModelBuildKind.Rolling:
                    query = query.Where(x => x.PullRequestNumber == null);
                    break;
                default:
                    throw new InvalidOperationException($"Invalid kind {kind}");
            }

            if (count is { } c)
            {
                query = query.Take(c);
            }

            return query;
        }
    }
}