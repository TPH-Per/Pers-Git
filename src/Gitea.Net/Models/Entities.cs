namespace Gitea.Net.Models;

public sealed class UserEntity
{
    public long Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    public bool MustChangePassword { get; set; }
    public string AvatarColor { get; set; } = "#2563eb";
    public DateTimeOffset CreatedAt { get; set; }
    public long RowVersion { get; set; }

    public UserDto ToDto() => new(Id, Username, Email, FullName, IsAdmin, MustChangePassword, AvatarColor, CreatedAt);
}

public sealed class RepositoryEntity
{
    public long Id { get; set; }
    public long OwnerId { get; set; }
    public string OwnerUsername { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsPrivate { get; set; }
    public string DefaultBranch { get; set; } = "main";
    public int StarsCount { get; set; }
    public int ForksCount { get; set; }
    public int OpenIssuesCount { get; set; }
    public int OpenPrsCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long RowVersion { get; set; }
}

public sealed class CollaboratorEntity
{
    public long RepoId { get; set; }
    public long UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Permission { get; set; } = "write"; // "read" | "write" | "admin"
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PersonalAccessTokenEntity
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TokenPrefix { get; set; } = string.Empty;
    public string Scopes { get; set; } = "repo:read,repo:write,issue:write,pull:write";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    public PatSummaryDto ToDto() => new(Id, Name, TokenPrefix, Scopes, CreatedAt, LastUsedAt, ExpiresAt);
}

public sealed class IssueEntity
{
    public long Id { get; set; }
    public long RepoId { get; set; }
    public long IssueIndex { get; set; } // Shared per-repository atomic sequence across Issues & PRs
    public long AuthorId { get; set; }
    public string AuthorUsername { get; set; } = string.Empty;
    public string AuthorAvatarColor { get; set; } = "#2563eb";
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string State { get; set; } = "open"; // "open" | "closed"
    public string LabelsCsv { get; set; } = string.Empty;
    public int CommentsCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long RowVersion { get; set; }
}

public sealed class IssueCommentEntity
{
    public long Id { get; set; }
    public long IssueId { get; set; }
    public long RepoId { get; set; }
    public long AuthorId { get; set; }
    public string AuthorUsername { get; set; } = string.Empty;
    public string AuthorAvatarColor { get; set; } = "#2563eb";
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PullRequestEntity
{
    public long Id { get; set; }
    public long RepoId { get; set; }
    public long PullIndex { get; set; } // Shared per-repository atomic sequence across Issues & PRs
    public long AuthorId { get; set; }
    public string AuthorUsername { get; set; } = string.Empty;
    public string AuthorAvatarColor { get; set; } = "#2563eb";
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string SourceBranch { get; set; } = string.Empty;
    public string TargetBranch { get; set; } = "main";
    public string State { get; set; } = "open"; // "open" | "merging" | "merged" | "closed"
    public bool IsMerged { get; set; }
    public string? MergeCommitSha { get; set; }
    public long? MergedByUserId { get; set; }
    public string? MergedByUsername { get; set; }
    public DateTimeOffset? MergedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long RowVersion { get; set; }
}

public sealed class WebhookEntity
{
    public long Id { get; set; }
    public long RepoId { get; set; }
    public string TargetUrl { get; set; } = string.Empty;
    public string Secret { get; set; } = string.Empty;
    public string EventsCsv { get; set; } = "push,issues,pull_request";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }

    public WebhookDto ToDto() => new(Id, RepoId, TargetUrl, EventsCsv, IsActive, !string.IsNullOrEmpty(Secret), CreatedAt);
}

public sealed class AuditLogEntity
{
    public long Id { get; set; }
    public long? ActorUserId { get; set; }
    public string ActorUsername { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class WebhookDeliveryEntity
{
    public long Id { get; set; }
    public long WebhookId { get; set; }
    public long RepoId { get; set; }
    public string EventName { get; set; } = string.Empty;
    public string TargetUrl { get; set; } = string.Empty;
    public string SignatureHeader { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public int AttemptCount { get; set; }
    public string Status { get; set; } = "delivered"; // "delivered" | "retrying" | "failed_ssrf" | "failed_http"
    public double DurationMs { get; set; }
    public DateTimeOffset DeliveredAt { get; set; }
}

public sealed class CiWorkflowRunEntity
{
    public long Id { get; set; }
    public long RepoId { get; set; }
    public long RunIndex { get; set; } // Atomic per-repository CI run sequence (#1, #2, ...)
    public long TriggeredByUserId { get; set; }
    public string TriggeredByUsername { get; set; } = string.Empty;
    public string WorkflowName { get; set; } = "Per's Git Production CI/CD Pipeline";
    public string TriggerEvent { get; set; } = "push"; // "push" | "pull_request" | "workflow_dispatch"
    public string EventType => TriggerEvent;
    public string Branch { get; set; } = "main";
    public string CommitSha { get; set; } = string.Empty;
    public string Status { get; set; } = "queued"; // "queued" | "running" | "success" | "failure"
    public string LogsText { get; set; } = string.Empty;
    public double DurationMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long RowVersion { get; set; }
}
