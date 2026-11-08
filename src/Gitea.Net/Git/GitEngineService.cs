using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Gitea.Net.Data;
using Gitea.Net.Models;
using Gitea.Net.Security;

namespace Gitea.Net.Git;

public sealed class GitRefNotFoundException(string message) : Exception(message);
public sealed class GitConflictException(string message) : Exception(message);
public sealed class GitValidationException(string message) : Exception(message);

/// <summary>
/// Production Git Engine powered by Git for Windows (git.exe) / POSIX git CLI.
/// - Verifies Git version >= 2.38 at startup (`EnsureMinimumGitVersionAsync`) required for `merge-tree --write-tree`.
/// - Limits concurrent Git OS processes via `_processConcurrencyLimiter` (`MaxConcurrentProcesses = 32`).
/// - Uses sliding idle timeout (`_streamIdleTimeout`) on Smart HTTP streams so multi-minute clones and >30MB pushes succeed.
/// - Sanitizes author/committer names (`&lt;`, `&gt;`, `\r`, `\n`), configures `safe.directory=*`, and uses `--replace` on `update-index`.
/// </summary>
public sealed partial class GitEngineService
{
    public const string ZeroOid = "0000000000000000000000000000000000000000";
    public const int MaxBlobPreviewBytes = 1024 * 1024; // 1 MB cap for text preview
    public const int MaxDiffBytes = 512 * 1024;         // 512 KB cap for unified diff preview
    public const int MaxCommitContentBytes = 10 * 1024 * 1024; // 10 MB cap for web UI file commit
    public const int MaxCommitMessageLength = 4096;

    private readonly string _gitBinary;
    private readonly string _reposRoot;
    private readonly string _reposRootWithSlash;
    private readonly string _tempWorktreesRoot;
    private readonly string _isolatedHomeDir;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _streamIdleTimeout;
    private readonly SemaphoreSlim _processConcurrencyLimiter;
    private readonly KeyedLockProvider _lockProvider;
    private readonly ILogger<GitEngineService> _logger;

    private static readonly char[] LineSplitChars = ['\r', '\n'];

    [GeneratedRegex(@"git version (\d+)\.(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex GitVersionRegex();

    public GitEngineService(
        IConfiguration configuration,
        IHostEnvironment env,
        KeyedLockProvider lockProvider,
        ILogger<GitEngineService> logger)
    {
        _lockProvider = lockProvider;
        _logger = logger;
        _gitBinary = ResolveGitBinary(configuration);

        string rawReposRoot = configuration["Git:RepositoriesRootPath"] ?? "App_Data/repositories";
        string rawTempRoot = configuration["Git:WorkingTempRootPath"] ?? "App_Data/temp_worktrees";

        _reposRoot = Path.GetFullPath(Path.IsPathRooted(rawReposRoot)
            ? rawReposRoot
            : Path.Combine(env.ContentRootPath, rawReposRoot));

        _reposRootWithSlash = _reposRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _reposRoot
            : _reposRoot + Path.DirectorySeparatorChar;

        _tempWorktreesRoot = Path.GetFullPath(Path.IsPathRooted(rawTempRoot)
            ? rawTempRoot
            : Path.Combine(env.ContentRootPath, rawTempRoot));

        _isolatedHomeDir = Path.Combine(_tempWorktreesRoot, "_git_home");

        int timeoutSecs = int.TryParse(configuration["Git:CommandTimeoutSeconds"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) ? s : 60;
        int idleSecs = int.TryParse(configuration["Git:StreamIdleTimeoutSeconds"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int idle) ? idle : 120;
        int maxProcs = int.TryParse(configuration["Git:MaxConcurrentProcesses"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mp) ? mp : 32;

        _timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSecs, 5, 600));
        _streamIdleTimeout = TimeSpan.FromSeconds(Math.Clamp(idleSecs, 30, 1800));
        _processConcurrencyLimiter = new SemaphoreSlim(Math.Clamp(maxProcs, 4, 256), Math.Clamp(maxProcs, 4, 256));

        Directory.CreateDirectory(_reposRoot);
        Directory.CreateDirectory(_tempWorktreesRoot);
        Directory.CreateDirectory(_isolatedHomeDir);

        // Write isolated global .gitconfig with safe.directory = *
        string globalGitConfig = Path.Combine(_isolatedHomeDir, ".gitconfig");
        if (!File.Exists(globalGitConfig))
        {
            File.WriteAllText(globalGitConfig, "[safe]\n\tdirectory = *\n", Encoding.UTF8);
        }
    }

    public string GitBinaryPath => _gitBinary;

    private static string ResolveGitBinary(IConfiguration configuration)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string? configuredWin = configuration["Git:WindowsBinaryPath"];
            if (!string.IsNullOrWhiteSpace(configuredWin) && File.Exists(configuredWin))
                return configuredWin;

            string[] candidates =
            [
                @"C:\Program Files\Git\cmd\git.exe",
                @"C:\Program Files\Git\bin\git.exe",
                @"C:\Program Files (x86)\Git\cmd\git.exe"
            ];
            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            return "git.exe";
        }

        return configuration["Git:FallbackBinaryPath"] ?? "git";
    }

    /// <summary>
    /// Verifies at startup that Git is installed and at least version 2.38 (required for `git merge-tree --write-tree`).
    /// </summary>
    public async Task<string> EnsureMinimumGitVersionAsync(CancellationToken ct = default)
    {
        string versionOutput = (await RunGitAsync(_reposRoot, ["--version"], null, ct)).Trim();
        var match = GitVersionRegex().Match(versionOutput);
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int major) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minor))
        {
            throw new InvalidOperationException($"Unable to parse Git version from output: '{versionOutput}'.");
        }

        if (major < 2 || (major == 2 && minor < 38))
        {
            throw new InvalidOperationException(
                $"FATAL: Git version >= 2.38 is required for 'git merge-tree --write-tree', but found {major}.{minor} ('{versionOutput}').");
        }

        _logger.LogInformation("Verified Git CLI engine: {Version}", versionOutput);
        return versionOutput;
    }

    public static string SanitizeGitIdentityName(string? rawName, string fallback = "Gitea User")
    {
        if (string.IsNullOrWhiteSpace(rawName)) return fallback;
        var sb = new StringBuilder(rawName.Length);
        foreach (char c in rawName)
        {
            if (c is '<' or '>' or '\r' or '\n' or '\0') continue;
            sb.Append(c);
        }
        string cleaned = sb.ToString().Trim();
        return string.IsNullOrEmpty(cleaned) ? fallback : (cleaned.Length > 100 ? cleaned[..100] : cleaned);
    }

    public static string SanitizeGitIdentityEmail(string? rawEmail, string fallback = "user@gitea.net.local")
    {
        if (string.IsNullOrWhiteSpace(rawEmail)) return fallback;
        var sb = new StringBuilder(rawEmail.Length);
        foreach (char c in rawEmail)
        {
            if (c is '<' or '>' or ' ' or '\r' or '\n' or '\0') continue;
            sb.Append(c);
        }
        string cleaned = sb.ToString().Trim();
        return string.IsNullOrEmpty(cleaned) ? fallback : (cleaned.Length > 160 ? cleaned[..160] : cleaned);
    }

    public string GetBareRepoPath(string owner, string repoName)
    {
        if (!SecurityService.IsSafeIdentifier(owner) || !SecurityService.IsSafeIdentifier(repoName))
            throw new GitValidationException("Invalid repository owner or name.");

        string combined = Path.GetFullPath(Path.Combine(_reposRoot, owner.ToLowerInvariant(), $"{repoName.ToLowerInvariant()}.git"));
        if (!combined.StartsWith(_reposRootWithSlash, StringComparison.OrdinalIgnoreCase))
            throw new GitValidationException("Path traversal detected.");
        return combined;
    }

    private string RequireExistingBareRepoPath(string owner, string repoName)
    {
        string barePath = GetBareRepoPath(owner, repoName);
        if (!Directory.Exists(barePath))
            throw new GitRefNotFoundException($"Bare repository '{owner}/{repoName}' does not exist on disk.");
        return barePath;
    }

    /// <summary>
    /// Pure C# validation of Git ref format (zero OS process spawns).
    /// </summary>
    public static bool ValidateRefFormat(string? refName) => SecurityService.IsSafeGitRefName(refName);

    public async Task<bool> BranchExistsAsync(string owner, string repoName, string branchName, CancellationToken ct = default)
    {
        if (!ValidateRefFormat(branchName)) return false;
        string barePath = RequireExistingBareRepoPath(owner, repoName);
        string? sha = await TryResolveRefShaAsync(barePath, $"refs/heads/{branchName}", ct);
        return !string.IsNullOrEmpty(sha);
    }

    /// <summary>
    /// Checks whether `sourceBranch` is already an ancestor of `targetBranch` (used by `MergeReconcilerHostedService`).
    /// </summary>
    public async Task<(bool IsMergedIntoTarget, string? TargetHeadSha)> CheckIfBranchMergedIntoTargetAsync(
        string owner, string repoName, string sourceBranch, string targetBranch, CancellationToken ct = default)
    {
        string barePath = RequireExistingBareRepoPath(owner, repoName);
        string? sourceSha = await TryResolveRefShaAsync(barePath, $"refs/heads/{sourceBranch}", ct);
        string? targetSha = await TryResolveRefShaAsync(barePath, $"refs/heads/{targetBranch}", ct);

        if (string.IsNullOrEmpty(sourceSha) || string.IsNullOrEmpty(targetSha))
            return (false, targetSha);

        try
        {
            await RunGitAsync(barePath, ["merge-base", "--is-ancestor", sourceSha, targetSha], null, ct);
            return (true, targetSha);
        }
        catch
        {
            return (false, targetSha);
        }
    }

    public async Task InitializeBareRepositoryAsync(
        string owner,
        string repoName,
        string defaultBranch,
        bool initializeWithReadme,
        string authorName,
        string authorEmail,
        string description,
        CancellationToken ct = default)
    {
        if (!ValidateRefFormat(defaultBranch))
            throw new GitValidationException("Invalid default branch name.");

        string barePath = GetBareRepoPath(owner, repoName);
        using var _ = await _lockProvider.AcquireAsync($"repo:{owner}/{repoName}", _timeout, ct);

        Directory.CreateDirectory(barePath);
        await RunGitAsync(barePath, ["init", "--bare", $"--initial-branch={defaultBranch}"], null, ct);
        await RunGitAsync(barePath, ["config", "http.receivepack", "true"], null, ct);
        await RunGitAsync(barePath, ["config", "http.uploadpack", "true"], null, ct);
        await RunGitAsync(barePath, ["config", "receive.fsckObjects", "true"], null, ct);
        await RunGitAsync(barePath, ["config", "transfer.fsckObjects", "true"], null, ct);

        if (initializeWithReadme)
        {
            string readmeContent = string.Join("\n",
                $"# {repoName}",
                "",
                description,
                "",
                "## Getting Started",
                "",
                "```bash",
                $"git clone http://localhost:5000/{owner}/{repoName}.git",
                "```",
                "");

            await CommitFilePlumbingInternalAsync(
                barePath,
                defaultBranch,
                "README.md",
                readmeContent,
                "Initial commit with README.md",
                authorName,
                authorEmail,
                ct);
        }
    }

    public void DeleteBareRepositoryIfExists(string owner, string repoName)
    {
        try
        {
            string barePath = GetBareRepoPath(owner, repoName);
            if (Directory.Exists(barePath))
            {
                foreach (var file in Directory.GetFiles(barePath, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(barePath, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up bare repository {Owner}/{Repo}", owner, repoName);
        }
    }

    public async Task<string> CommitFileAsync(
        string owner,
        string repoName,
        string branch,
        string relativeFilePath,
        string fileContent,
        string commitMessage,
        string authorName,
        string authorEmail,
        CancellationToken ct = default)
    {
        if (!ValidateRefFormat(branch))
            throw new GitValidationException("Invalid branch reference name.");
        if (!SecurityService.IsSafeRelativeFilePath(relativeFilePath))
            throw new GitValidationException("Invalid relative file path.");

        int contentByteCount = Encoding.UTF8.GetByteCount(fileContent ?? string.Empty);
        if (contentByteCount > MaxCommitContentBytes)
            throw new GitValidationException($"File content exceeds maximum web commit limit of {MaxCommitContentBytes} bytes.");
        if (!string.IsNullOrEmpty(commitMessage) && commitMessage.Length > MaxCommitMessageLength)
            throw new GitValidationException($"Commit message cannot exceed {MaxCommitMessageLength} characters.");

        string barePath = RequireExistingBareRepoPath(owner, repoName);
        using var _ = await _lockProvider.AcquireAsync($"repo:{owner}/{repoName}", _timeout, ct);

        return await CommitFilePlumbingInternalAsync(
            barePath, branch, relativeFilePath, fileContent ?? string.Empty, commitMessage, authorName, authorEmail, ct);
    }

    private async Task<string> CommitFilePlumbingInternalAsync(
        string barePath,
        string branch,
        string relativeFilePath,
        string fileContent,
        string commitMessage,
        string authorName,
        string authorEmail,
        CancellationToken ct)
    {
        string tempIndexFile = Path.Combine(_tempWorktreesRoot, $"idx_{Guid.NewGuid():N}");
        try
        {
            byte[] contentBytes = Encoding.UTF8.GetBytes(fileContent);
            string blobSha = (await RunGitAsync(barePath, ["hash-object", "-w", "--stdin"], contentBytes, ct)).Trim();

            string? parentCommitSha = await TryResolveRefShaAsync(barePath, $"refs/heads/{branch}", ct);
            var extraEnv = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = tempIndexFile };

            if (!string.IsNullOrEmpty(parentCommitSha))
            {
                await RunGitAsync(barePath, ["read-tree", parentCommitSha], null, ct, extraEnv);
            }

            // Use --add --replace so replacing a directory with a file (or vice-versa) does not fail
            await RunGitAsync(
                barePath,
                ["update-index", "--add", "--replace", "--cacheinfo", $"100644,{blobSha},{relativeFilePath}"],
                null,
                ct,
                extraEnv);

            string treeSha = (await RunGitAsync(barePath, ["write-tree"], null, ct, extraEnv)).Trim();

            string cleanName = SanitizeGitIdentityName(authorName);
            string cleanEmail = SanitizeGitIdentityEmail(authorEmail);

            var commitEnv = new Dictionary<string, string>(extraEnv)
            {
                ["GIT_AUTHOR_NAME"] = cleanName,
                ["GIT_AUTHOR_EMAIL"] = cleanEmail,
                ["GIT_COMMITTER_NAME"] = cleanName,
                ["GIT_COMMITTER_EMAIL"] = cleanEmail
            };

            List<string> commitArgs = ["commit-tree", treeSha];
            if (!string.IsNullOrEmpty(parentCommitSha))
            {
                commitArgs.Add("-p");
                commitArgs.Add(parentCommitSha);
            }
            commitArgs.Add("-m");
            commitArgs.Add(string.IsNullOrWhiteSpace(commitMessage) ? $"Update {relativeFilePath}" : commitMessage);

            string commitSha = (await RunGitAsync(barePath, commitArgs, null, ct, commitEnv)).Trim();

            string expectedOld = parentCommitSha ?? ZeroOid;
            await RunGitAsync(barePath, ["update-ref", $"refs/heads/{branch}", commitSha, expectedOld], null, ct);

            return commitSha;
        }
        finally
        {
            if (File.Exists(tempIndexFile))
            {
                try { File.Delete(tempIndexFile); } catch { }
            }
        }
    }

    public async Task CreateBranchAsync(string owner, string repoName, string newBranch, string fromRef, CancellationToken ct = default)
    {
        if (!ValidateRefFormat(newBranch) || !ValidateRefFormat(fromRef))
            throw new GitValidationException("Invalid branch reference format.");

        string barePath = RequireExistingBareRepoPath(owner, repoName);
        using var _ = await _lockProvider.AcquireAsync($"repo:{owner}/{repoName}", _timeout, ct);

        string? existingSha = await TryResolveRefShaAsync(barePath, $"refs/heads/{newBranch}", ct);
        if (!string.IsNullOrEmpty(existingSha))
            throw new DuplicateResourceException($"Branch '{newBranch}' already exists.");

        string? targetSha = await TryResolveRefShaAsync(barePath, fromRef, ct);
        if (string.IsNullOrEmpty(targetSha))
            throw new GitRefNotFoundException($"Source reference '{fromRef}' does not exist.");

        await RunGitAsync(barePath, ["update-ref", $"refs/heads/{newBranch}", targetSha, ZeroOid], null, ct);
    }

    public async Task<IReadOnlyList<string>> ListBranchesAsync(string owner, string repoName, CancellationToken ct = default)
    {
        string barePath = RequireExistingBareRepoPath(owner, repoName);
        return await ListBranchesInternalAsync(barePath, ct);
    }

    private async Task<IReadOnlyList<string>> ListBranchesInternalAsync(string barePath, CancellationToken ct)
    {
        if (!Directory.Exists(barePath)) return [];
        string output = await RunGitAsync(barePath, ["for-each-ref", "--format=%(refname:lstrip=2)", "refs/heads/"], null, ct);
        return output
            .Split(LineSplitChars, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    public async Task<IReadOnlyList<GitTreeEntryDto>> ListTreeAsync(
        string owner, string repoName, string gitRef, string? subPath = null, CancellationToken ct = default)
    {
        if (!ValidateRefFormat(gitRef))
            throw new GitValidationException("Invalid git reference.");
        if (!string.IsNullOrWhiteSpace(subPath) && !SecurityService.IsSafeRelativeFilePath(subPath))
            throw new GitValidationException("Invalid tree path.");

        string barePath = RequireExistingBareRepoPath(owner, repoName);
        var branches = await ListBranchesInternalAsync(barePath, ct);
        if (branches.Count == 0) return [];

        string? resolvedRef = await TryResolveRefShaAsync(barePath, gitRef, ct);
        if (string.IsNullOrEmpty(resolvedRef))
            throw new GitRefNotFoundException($"Git reference '{gitRef}' was not found.");

        string treeSpec = string.IsNullOrWhiteSpace(subPath) ? gitRef : $"{gitRef}:{subPath.Trim('/')}";
        string output;
        try
        {
            output = await RunGitAsync(barePath, ["ls-tree", "-l", "-z", treeSpec], null, ct);
        }
        catch (InvalidOperationException)
        {
            throw new GitRefNotFoundException($"Tree path '{subPath}' was not found in '{gitRef}'.");
        }

        var entries = new List<GitTreeEntryDto>();
        foreach (string record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int tabIdx = record.IndexOf('\t');
            if (tabIdx <= 0) continue;
            string meta = record[..tabIdx];
            string path = record[(tabIdx + 1)..];
            string[] parts = meta.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;

            long size = long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out long s) ? s : 0;
            string name = Path.GetFileName(path);
            entries.Add(new GitTreeEntryDto(parts[0], parts[1], parts[2], size, path, name));
        }

        return entries
            .OrderByDescending(e => e.Type == "tree")
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<GitBlobDto> GetBlobContentAsync(string owner, string repoName, string gitRef, string filePath, CancellationToken ct = default)
    {
        if (!ValidateRefFormat(gitRef))
            throw new GitValidationException("Invalid git reference.");
        if (!SecurityService.IsSafeRelativeFilePath(filePath))
            throw new GitValidationException("Invalid file path.");

        string barePath = RequireExistingBareRepoPath(owner, repoName);
        string objectSpec = $"{gitRef}:{filePath.Trim('/')}";

        long blobSize;
        try
        {
            string sizeStr = (await RunGitAsync(barePath, ["cat-file", "-s", objectSpec], null, ct)).Trim();
            blobSize = long.TryParse(sizeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long s) ? s : 0;
        }
        catch (InvalidOperationException)
        {
            throw new GitRefNotFoundException($"File '{filePath}' does not exist at reference '{gitRef}'.");
        }

        byte[] rawBytes = await RunGitBytesAsync(barePath, ["cat-file", "blob", objectSpec], MaxBlobPreviewBytes + 1, ct);
        bool isTruncated = rawBytes.Length > MaxBlobPreviewBytes;
        if (isTruncated)
        {
            rawBytes = rawBytes[..MaxBlobPreviewBytes];
        }

        int checkLen = Math.Min(rawBytes.Length, 8192);
        bool isBinary = false;
        for (int i = 0; i < checkLen; i++)
        {
            if (rawBytes[i] == 0)
            {
                isBinary = true;
                break;
            }
        }

        if (isBinary)
        {
            int previewLen = Math.Min(rawBytes.Length, 65536);
            return new GitBlobDto(
                gitRef,
                filePath,
                blobSize,
                IsBinary: true,
                IsTruncated: blobSize > previewLen,
                Content: null,
                Base64Preview: Convert.ToBase64String(rawBytes, 0, previewLen));
        }

        string text = Encoding.UTF8.GetString(rawBytes);
        return new GitBlobDto(gitRef, filePath, blobSize, IsBinary: false, IsTruncated: isTruncated, Content: text, Base64Preview: null);
    }

    public async Task<IReadOnlyList<GitCommitDto>> ListCommitsAsync(
        string owner, string repoName, string gitRef, int maxCount = 30, CancellationToken ct = default)
    {
        if (!ValidateRefFormat(gitRef))
            throw new GitValidationException("Invalid git reference.");

        string barePath = RequireExistingBareRepoPath(owner, repoName);
        var branches = await ListBranchesInternalAsync(barePath, ct);
        if (branches.Count == 0) return [];

        string? resolved = await TryResolveRefShaAsync(barePath, gitRef, ct);
        if (string.IsNullOrEmpty(resolved))
            throw new GitRefNotFoundException($"Git reference '{gitRef}' was not found.");

        string format = "%H%x1f%h%x1f%an%x1f%ae%x1f%aI%x1f%s%x1f%b%x1e";
        string output = await RunGitAsync(barePath, ["log", gitRef, $"-n{Math.Clamp(maxCount, 1, 100)}", $"--format={format}"], null, ct);

        return ParseCommitRecords(output);
    }

    public async Task<GitDiffDto> CompareRefsAsync(string owner, string repoName, string baseRef, string headRef, CancellationToken ct = default)
    {
        if (!ValidateRefFormat(baseRef) || !ValidateRefFormat(headRef))
            throw new GitValidationException("Invalid branch reference.");

        string barePath = RequireExistingBareRepoPath(owner, repoName);
        if (await TryResolveRefShaAsync(barePath, baseRef, ct) is null)
            throw new GitRefNotFoundException($"Base reference '{baseRef}' was not found.");
        if (await TryResolveRefShaAsync(barePath, headRef, ct) is null)
            throw new GitRefNotFoundException($"Head reference '{headRef}' was not found.");

        byte[] diffBytes = await RunGitBytesAsync(barePath, ["diff", $"{baseRef}...{headRef}"], MaxDiffBytes + 1, ct);
        bool isTruncated = diffBytes.Length > MaxDiffBytes;
        if (isTruncated)
        {
            diffBytes = diffBytes[..MaxDiffBytes];
        }
        string diffText = Encoding.UTF8.GetString(diffBytes);

        string nameOnly = await RunGitAsync(barePath, ["diff", "--name-only", $"{baseRef}...{headRef}"], null, ct);
        int filesChanged = nameOnly.Split(LineSplitChars, StringSplitOptions.RemoveEmptyEntries).Length;

        string format = "%H%x1f%h%x1f%an%x1f%ae%x1f%aI%x1f%s%x1f%b%x1e";
        string logOutput = await RunGitAsync(barePath, ["log", $"{baseRef}..{headRef}", $"--format={format}"], null, ct);
        var commits = ParseCommitRecords(logOutput);

        return new GitDiffDto(baseRef, headRef, filesChanged, isTruncated, diffText, commits);
    }

    public async Task<(bool Success, string? MergeCommitSha, string UserFacingError)> MergeBranchesAsync(
        string owner,
        string repoName,
        string sourceBranch,
        string targetBranch,
        string mergeStrategy,
        string commitMessage,
        string mergerName,
        string mergerEmail,
        CancellationToken ct = default)
    {
        if (!ValidateRefFormat(sourceBranch) || !ValidateRefFormat(targetBranch))
            throw new GitValidationException("Invalid branch reference.");

        string normalizedStrategy = (mergeStrategy ?? "merge").Trim().ToLowerInvariant();
        if (normalizedStrategy is not ("merge" or "squash" or "fast-forward"))
            throw new GitValidationException("MergeStrategy must be 'merge', 'squash', or 'fast-forward'.");

        string barePath = RequireExistingBareRepoPath(owner, repoName);
        using var _ = await _lockProvider.AcquireAsync($"repo:{owner}/{repoName}", _timeout, ct);

        try
        {
            string? targetSha = await TryResolveRefShaAsync(barePath, $"refs/heads/{targetBranch}", ct);
            string? sourceSha = await TryResolveRefShaAsync(barePath, $"refs/heads/{sourceBranch}", ct);

            if (string.IsNullOrEmpty(targetSha) || string.IsNullOrEmpty(sourceSha))
                return (false, null, "Source or target branch reference does not exist.");

            if (normalizedStrategy == "fast-forward")
            {
                try
                {
                    await RunGitAsync(barePath, ["merge-base", "--is-ancestor", targetSha, sourceSha], null, ct);
                }
                catch
                {
                    return (false, null, "Fast-forward merge is not possible because the target branch has diverged.");
                }

                await RunGitAsync(barePath, ["update-ref", $"refs/heads/{targetBranch}", sourceSha, targetSha], null, ct);
                return (true, sourceSha, string.Empty);
            }

            string mergedTreeSha;
            try
            {
                string mergeTreeOut = await RunGitAsync(barePath, ["merge-tree", "--write-tree", targetSha, sourceSha], null, ct);
                mergedTreeSha = mergeTreeOut.Split(LineSplitChars, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "Merge conflict between {Source} and {Target} in {Owner}/{Repo}", sourceBranch, targetBranch, owner, repoName);
                return (false, null, "Merge conflict detected between source and target branches.");
            }

            string cleanName = SanitizeGitIdentityName(mergerName);
            string cleanEmail = SanitizeGitIdentityEmail(mergerEmail);

            var commitEnv = new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = cleanName,
                ["GIT_AUTHOR_EMAIL"] = cleanEmail,
                ["GIT_COMMITTER_NAME"] = cleanName,
                ["GIT_COMMITTER_EMAIL"] = cleanEmail
            };

            List<string> commitArgs = ["commit-tree", mergedTreeSha, "-p", targetSha];
            if (normalizedStrategy == "merge")
            {
                commitArgs.Add("-p");
                commitArgs.Add(sourceSha);
            }
            commitArgs.Add("-m");
            commitArgs.Add(string.IsNullOrWhiteSpace(commitMessage) ? $"Merge {sourceBranch} into {targetBranch}" : commitMessage);

            string newCommitSha = (await RunGitAsync(barePath, commitArgs, null, ct, commitEnv)).Trim();
            await RunGitAsync(barePath, ["update-ref", $"refs/heads/{targetBranch}", newCommitSha, targetSha], null, ct);
            return (true, newCommitSha, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Git merge failed for {Owner}/{Repo} ({Source} -> {Target})", owner, repoName, sourceBranch, targetBranch);
            return (false, null, "Unable to complete Git merge operation.");
        }
    }

    // ==================== GIT SMART HTTP PROTOCOL STREAMING (SLIDING IDLE TIMEOUT) ====================

    public async Task WriteInfoRefsAsync(string owner, string repoName, string service, Stream responseStream, CancellationToken ct = default)
    {
        if (service is not ("git-upload-pack" or "git-receive-pack"))
            throw new GitValidationException("Unsupported Git service.");

        string barePath = RequireExistingBareRepoPath(owner, repoName);
        string subCommand = service == "git-upload-pack" ? "upload-pack" : "receive-pack";

        string headerLine = $"# service={service}\n";
        string pktPrefix = (headerLine.Length + 4).ToString("x4", CultureInfo.InvariantCulture);
        byte[] preamble = Encoding.ASCII.GetBytes($"{pktPrefix}{headerLine}0000");
        await responseStream.WriteAsync(preamble, ct);

        await RunGitStreamWithIdleTimeoutAsync(barePath, [subCommand, "--stateless-rpc", "--advertise-refs", barePath], null, responseStream, ct);
    }

    public async Task ExecuteStatelessRpcAsync(
        string owner, string repoName, string service, Stream requestStream, Stream responseStream, CancellationToken ct = default)
    {
        if (service is not ("git-upload-pack" or "git-receive-pack"))
            throw new GitValidationException("Unsupported Git service.");

        string barePath = RequireExistingBareRepoPath(owner, repoName);
        string subCommand = service == "git-upload-pack" ? "upload-pack" : "receive-pack";

        await RunGitStreamWithIdleTimeoutAsync(barePath, [subCommand, "--stateless-rpc", barePath], requestStream, responseStream, ct);
    }

    // ==================== PROCESS EXECUTION, CONCURRENCY CAP & IDLE TIMEOUT PUMPING ====================

    private async Task<string?> TryResolveRefShaAsync(string barePath, string refSpec, CancellationToken ct)
    {
        try
        {
            string sha = (await RunGitAsync(barePath, ["rev-parse", "--verify", "--quiet", $"{refSpec}^{{commit}}"], null, ct)).Trim();
            return string.IsNullOrEmpty(sha) ? null : sha;
        }
        catch
        {
            return null;
        }
    }

    private void ConfigureIsolatedGitEnvironment(ProcessStartInfo psi, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["HOME"] = _isolatedHomeDir;
        psi.Environment["XDG_CONFIG_HOME"] = _isolatedHomeDir;
        if (extraEnv is not null)
        {
            foreach (var kv in extraEnv)
            {
                psi.Environment[kv.Key] = kv.Value;
            }
        }
    }

    private async Task<string> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        byte[]? stdinBytes,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        bool acquired = await _processConcurrencyLimiter.WaitAsync(_timeout, ct);
        if (!acquired)
            throw new TimeoutException("Server is busy processing concurrent Git operations.");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _gitBinary,
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = stdinBytes is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            ConfigureIsolatedGitEnvironment(psi, extraEnv);

            using var process = new Process { StartInfo = psi };
            process.Start();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);

            try
            {
                if (stdinBytes is not null)
                {
                    await process.StandardInput.BaseStream.WriteAsync(stdinBytes, timeoutCts.Token);
                    process.StandardInput.Close();
                }

                Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                Task<string> stdErrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

                await Task.WhenAll(stdOutTask, stdErrTask, process.WaitForExitAsync(timeoutCts.Token));

                string stdout = await stdOutTask;
                string stderr = await stdErrTask;

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Git command failed (exit {process.ExitCode}): {stderr.Trim()}");
                }

                return stdout;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKillProcessTree(process);
                throw new TimeoutException($"Git command timed out after {_timeout.TotalSeconds}s and was terminated.");
            }
            catch
            {
                TryKillProcessTree(process);
                throw;
            }
        }
        finally
        {
            _processConcurrencyLimiter.Release();
        }
    }

    private async Task<byte[]> RunGitBytesAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        int maxBytesToRead,
        CancellationToken ct)
    {
        bool acquired = await _processConcurrencyLimiter.WaitAsync(_timeout, ct);
        if (!acquired)
            throw new TimeoutException("Server is busy processing concurrent Git operations.");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _gitBinary,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            ConfigureIsolatedGitEnvironment(psi);

            using var process = new Process { StartInfo = psi };
            process.Start();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);

            try
            {
                using var ms = new MemoryStream();
                Task readOutTask = Task.Run(async () =>
                {
                    byte[] buffer = new byte[16384];
                    int read;
                    while (ms.Length < maxBytesToRead &&
                           (read = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeoutCts.Token)) > 0)
                    {
                        int toWrite = (int)Math.Min(read, maxBytesToRead - ms.Length);
                        ms.Write(buffer, 0, toWrite);
                        if (ms.Length >= maxBytesToRead)
                        {
                            TryKillProcessTree(process);
                            break;
                        }
                    }
                }, timeoutCts.Token);

                Task<string> stdErrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
                await Task.WhenAll(readOutTask, stdErrTask, process.WaitForExitAsync(timeoutCts.Token));

                if (process.ExitCode != 0 && ms.Length < maxBytesToRead)
                {
                    string stderr = await stdErrTask;
                    throw new InvalidOperationException($"Git binary read failed (exit {process.ExitCode}): {stderr.Trim()}");
                }

                return ms.ToArray();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKillProcessTree(process);
                throw new TimeoutException($"Git binary command timed out after {_timeout.TotalSeconds}s.");
            }
            catch
            {
                TryKillProcessTree(process);
                throw;
            }
        }
        finally
        {
            _processConcurrencyLimiter.Release();
        }
    }

    /// <summary>
    /// Streams Smart HTTP `git-upload-pack` / `git-receive-pack` using a SLIDING IDLE TIMEOUT (`_streamIdleTimeout`).
    /// Every chunk read from `inputStream` or written to `outputStream` resets the idle timer,
    /// allowing large (>30MB) pushes or multi-minute clones to run to completion while still killing hung processes.
    /// </summary>
    private async Task RunGitStreamWithIdleTimeoutAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        Stream? inputStream,
        Stream outputStream,
        CancellationToken ct)
    {
        bool acquired = await _processConcurrencyLimiter.WaitAsync(_timeout, ct);
        if (!acquired)
            throw new TimeoutException("Server is busy processing concurrent Git streams.");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _gitBinary,
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = inputStream is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            ConfigureIsolatedGitEnvironment(psi);

            using var process = new Process { StartInfo = psi };
            process.Start();

            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idleCts.CancelAfter(_streamIdleTimeout);

            void TouchIdleTimer()
            {
                try { idleCts.CancelAfter(_streamIdleTimeout); } catch { }
            }

            try
            {
                Task inputTask = Task.CompletedTask;
                if (inputStream is not null)
                {
                    inputTask = Task.Run(async () =>
                    {
                        byte[] buffer = new byte[32768];
                        int read;
                        while ((read = await inputStream.ReadAsync(buffer, idleCts.Token)) > 0)
                        {
                            TouchIdleTimer();
                            await process.StandardInput.BaseStream.WriteAsync(buffer.AsMemory(0, read), idleCts.Token);
                            TouchIdleTimer();
                        }
                        process.StandardInput.Close();
                    }, idleCts.Token);
                }

                Task outputTask = Task.Run(async () =>
                {
                    byte[] buffer = new byte[32768];
                    int read;
                    while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, idleCts.Token)) > 0)
                    {
                        TouchIdleTimer();
                        await outputStream.WriteAsync(buffer.AsMemory(0, read), idleCts.Token);
                        TouchIdleTimer();
                    }
                }, idleCts.Token);

                Task<string> stderrTask = process.StandardError.ReadToEndAsync(idleCts.Token);

                await Task.WhenAll(inputTask, outputTask, stderrTask, process.WaitForExitAsync(idleCts.Token));

                if (process.ExitCode != 0)
                {
                    string stderr = await stderrTask;
                    throw new InvalidOperationException($"Git RPC failed (exit {process.ExitCode}): {stderr.Trim()}");
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKillProcessTree(process);
                throw new TimeoutException($"Git Smart HTTP stream was idle for over {_streamIdleTimeout.TotalSeconds}s and was terminated.");
            }
            catch
            {
                TryKillProcessTree(process);
                throw;
            }
        }
        finally
        {
            _processConcurrencyLimiter.Release();
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static List<GitCommitDto> ParseCommitRecords(string output)
    {
        var commits = new List<GitCommitDto>();
        foreach (string record in output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] fields = record.Split('\x1f');
            if (fields.Length < 6) continue;
            DateTimeOffset.TryParse(fields[4].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var committedAt);
            commits.Add(new GitCommitDto(
                Sha: fields[0].Trim(),
                ShortSha: fields[1].Trim(),
                AuthorName: fields[2].Trim(),
                AuthorEmail: fields[3].Trim(),
                CommittedAt: committedAt,
                Subject: fields[5].Trim(),
                Body: fields.Length > 6 ? fields[6].Trim() : string.Empty));
        }
        return commits;
    }
}
