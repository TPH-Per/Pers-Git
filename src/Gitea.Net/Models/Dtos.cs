namespace Gitea.Net.Models;

// Safe Public Response DTOs (Never expose PasswordHash, PasswordSalt, or Webhook Secret)
public sealed record UserDto(
    long Id,
    string Username,
    string Email,
    string FullName,
    bool IsAdmin,
    bool MustChangePassword,
    string AvatarColor,
    DateTimeOffset CreatedAt);

public sealed record PublicUserProfileDto(
    long Id,
    string Username,
    string FullName,
    string AvatarColor,
    DateTimeOffset CreatedAt);

public sealed record WebhookDto(
    long Id,
    long RepoId,
    string TargetUrl,
    string EventsCsv,
    bool IsActive,
    bool HasSecret,
    DateTimeOffset CreatedAt);

public sealed record PatSummaryDto(
    long Id,
    string Name,
    string TokenPrefix,
    string Scopes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? ExpiresAt);

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record RegisterRequest(string? Username, string? Email, string? FullName, string? Password);
public sealed record LoginRequest(string? Username, string? Password);
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
public sealed record AuthResponse(string Token, UserDto User, DateTimeOffset ExpiresAt);

public sealed record CreatePatRequest(
    string? Name,
    string? Scopes = "repo:read,repo:write,issue:write,pull:write",
    int? ExpiresInDays = 30);

public sealed record CreatedPatResponse(
    long Id,
    string Name,
    string RawToken,
    string TokenPrefix,
    string Scopes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt);

public sealed record AddCollaboratorRequest(string? Username, string? Permission = "write");

public sealed record CreateRepoRequest(
    string? Name,
    string? Description,
    bool IsPrivate,
    string? DefaultBranch = "main",
    bool InitializeReadme = true);

public sealed record CreateFileCommitRequest(
    string? Branch,
    string? FilePath,
    string? Content,
    string? CommitMessage);

public sealed record CreateBranchRequest(
    string? NewBranchName,
    string? FromRef = "main");

public sealed record CreateIssueRequest(
    string? Title,
    string? Body,
    string? LabelsCsv = "bug");

public sealed record UpdateIssueStateRequest(
    string? State,
    long ExpectedRowVersion);

public sealed record UpdatePullStateRequest(
    string? State,
    long ExpectedRowVersion);

public sealed record CreateCommentRequest(string? Body);

public sealed record CreatePullRequest(
    string? Title,
    string? Body,
    string? SourceBranch,
    string? TargetBranch = "main");

public sealed record MergePullRequest(
    string? MergeStrategy, // "merge" | "squash" | "fast-forward"
    string? CustomCommitMessage,
    long ExpectedRowVersion);

public sealed record CreateWebhookRequest(
    string? TargetUrl,
    string? Secret,
    string? EventsCsv = "push,issues,pull_request");

public sealed record GitTreeEntryDto(
    string Mode,
    string Type, // "blob" | "tree"
    string ObjectSha,
    long Size,
    string Path,
    string Name);

public sealed record GitBlobDto(
    string Ref,
    string Path,
    long Size,
    bool IsBinary,
    bool IsTruncated,
    string? Content,
    string? Base64Preview);

public sealed record GitCommitDto(
    string Sha,
    string ShortSha,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset CommittedAt,
    string Subject,
    string Body);

public sealed record GitDiffDto(
    string BaseRef,
    string HeadRef,
    int FilesChanged,
    bool IsTruncated,
    string UnifiedDiff,
    IReadOnlyList<GitCommitDto> Commits);

public sealed record IssueDetailBundleDto(
    IssueEntity Issue,
    IReadOnlyList<IssueCommentEntity> Comments);

public sealed record TriggerCiWorkflowRequest(
    string? WorkflowName = "Per's Git Production CI/CD Pipeline",
    string? Branch = "main",
    IReadOnlyDictionary<string, string>? Inputs = null);
