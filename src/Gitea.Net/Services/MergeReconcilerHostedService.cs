using Gitea.Net.Data;
using Gitea.Net.Git;

namespace Gitea.Net.Services;

/// <summary>
/// Background reconciler (`BackgroundService`) that runs at startup and periodically every 60 seconds:
/// 1. Inspects any Pull Requests stuck in `state = 'merging'` for longer than `StuckThreshold` (e.g. after a crash or power loss).
///    - Checks `git merge-base --is-ancestor` on the bare repository:
///      - If `sourceBranch` is already merged into `targetBranch`, finalizes the PR as `merged`.
///      - Otherwise, rolls the PR state back from `merging` to `open`.
/// 2. Purges expired `revoked_tokens` and old `audit_logs` (&gt;90 days) in the background (avoiding synchronous O(N) DELETEs on user requests).
/// </summary>
public sealed class MergeReconcilerHostedService(
    GiteaDbRepository db,
    GitEngineService git,
    ILogger<MergeReconcilerHostedService> logger)
    : BackgroundService
{
    public static readonly TimeSpan StuckMergeThreshold = TimeSpan.FromMinutes(2);

    public async Task ReconcileStuckPullRequestsAsync(TimeSpan threshold, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(threshold);
        var stuckPrs = await db.ListStuckMergingPullRequestsAsync(cutoff, ct);

        foreach (var pr in stuckPrs)
        {
            try
            {
                var (isMergedInGit, targetHeadSha) = await git.CheckIfBranchMergedIntoTargetAsync(
                    pr.OwnerUsername, pr.RepoName, pr.SourceBranch, pr.TargetBranch, ct);

                if (isMergedInGit && !string.IsNullOrEmpty(targetHeadSha))
                {
                    await db.FinalizePullRequestMergedAsync(pr.RepoId, pr.PullIndex, targetHeadSha, pr.AuthorId, ct);
                    logger.LogInformation(
                        "Reconciler finalized stuck PR #{PullIndex} in {Owner}/{Repo} as merged ({Sha})",
                        pr.PullIndex, pr.OwnerUsername, pr.RepoName, targetHeadSha[..Math.Min(8, targetHeadSha.Length)]);
                }
                else
                {
                    await db.RollbackPullRequestMergeLockAsync(pr.RepoId, pr.PullIndex, ct);
                    logger.LogInformation(
                        "Reconciler rolled back stuck PR #{PullIndex} in {Owner}/{Repo} from 'merging' to 'open'",
                        pr.PullIndex, pr.OwnerUsername, pr.RepoName);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to reconcile stuck PR #{PullIndex} in {Owner}/{Repo}", pr.PullIndex, pr.OwnerUsername, pr.RepoName);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial startup reconciliation pass
        try
        {
            await git.EnsureMinimumGitVersionAsync(stoppingToken);
            await ReconcileStuckPullRequestsAsync(StuckMergeThreshold, stoppingToken);
            await db.CleanupExpiredTokensAndOldAuditLogsAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Startup reconciliation pass encountered an error.");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ReconcileStuckPullRequestsAsync(StuckMergeThreshold, stoppingToken);
                await db.CleanupExpiredTokensAndOldAuditLogsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Periodic reconciliation pass encountered an error.");
            }
        }
    }
}
