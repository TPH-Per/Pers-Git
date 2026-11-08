# Gitea.Net (Per’s Git) — Self-Hosted .NET 8.0 Git Service (Production-Hardened MVP)

`Gitea.Net` (branded **Per’s Git**) is a self-hosted Git service MVP inspired by Gitea, engineered in **.NET Core 8.0 (`ASP.NET Core`)** with **PostgreSQL (`Npgsql`)**, **SQLite (`Microsoft.Data.Sqlite`)**, **Git for Windows (`git.exe`) / POSIX `git` CLI**, and a **100% Pure Vanilla HTML5/CSS3/ES6 Frontend**.

---

## 1. Honest MVP Scope Matrix (vs. Upstream Go Gitea)

Upstream Gitea is a multi-hundred-thousand-line Go platform. `Gitea.Net` is a **focused, production-hardened MVP** implementing core repository hosting, Git Smart HTTP transport, plumbing commits/merges, RBAC authorization, issue tracking, pull request merging, built-in CI/CD Actions pipeline runner + cloud GitHub/Gitea workflows, and IP-pinned SSRF-safe webhook delivery:

| Feature Area | Implemented in `Gitea.Net` MVP | Out of Scope (Not Implemented in MVP) |
| :--- | :--- | :--- |
| **Git Transport & Engine** | Startup `git --version >= 2.38` verification, bare repo init (`receive.fsckObjects=true`, `safe.directory=*`), Git Smart HTTP (`info/refs`, `git-upload-pack`, `git-receive-pack`) with sliding idle timeout (`120s`), concurrent process semaphore (`32`), unlimited packfile body size, O(1) Git plumbing commits (`hash-object`, `update-index --add --replace`, `write-tree`, `commit-tree`, `update-ref`), zero-OID atomic branch creation, pure C# `ValidateRefFormat`, binary blob detection & size truncation | SSH protocol server (`git-shell` over port 22), Git LFS (Large File Storage), signed GPG commit verification, repository mirrors & migrations |
| **Authentication & RBAC** | `GiteaAuthHandler` (`HttpOnly` `SameSite=Strict` session cookies + `X-Gitea-Csrf: 1` + `Origin` CSRF validation, `Bearer` tokens, `Basic` auth with 60s HMAC cache & per-`(IP, username)` brute-force lockout, scoped & revocable Personal Access Tokens `pat_...` with `last_used_at`), 600,000-iteration PBKDF2-SHA256 (`v1$600000$...`) with transparent legacy `100k` rehash, Owner / Collaborator (`read`/`write`/`admin`) / Public-Read authorization matrix (returning `404` on unauthorized private repos) | Organizations & Teams hierarchy, OAuth2/OIDC/LDAP/SAML SSO, TOTP/WebAuthn 2FA, fine-grained branch protection rules |
| **Issues & Pull Requests** | Shared per-repository atomic sequence (`repo_counters.last_issue_index`) across Issues & PRs, O(1) maintained counter columns (`open_issues_count`, `open_prs_count`, `comments_count`) + admin reconciliation endpoint, optimistic concurrency (`row_version` CAS), crash-safe 3-phase atomic PR merge (`open` -> `merging` -> `merged`/`open`) via `git merge-tree --write-tree` + `MergeReconcilerHostedService` (`git merge-base --is-ancestor`), PR close/reopen (`PATCH .../state`) | Line-by-line code review comments, PR approvals/dismissals, Milestones, Kanban project boards, issue dependencies & attachments |
| **CI/CD Actions & DevOps** | Cloud & self-hosted CI/CD workflow definitions (`.github/workflows/ci-cd.yml` & `.gitea/workflows/ci-cd.yml` with SQLite WAL, PostgreSQL 16, and Docker smoke-test stages) + built-in `CiPipelineRunnerService` (`src/Gitea.Net/Services/CiPipelineRunnerService.cs`) using `Channel<T>`, atomic per-repo `last_ci_run_index`, CAS worker claiming (`WHERE status = 'queued'`), Zero N+1 `INNER JOIN` run listing, and live UI execution logs | Distributed multi-node external container runners, artifact cache storage servers |
| **Webhooks & Audit** | Background outbox webhook dispatcher (`WebhookDeliveryService` via `Channel<T>`) with mandatory/auto-generated HMAC-SHA256 signing (`X-Gitea-Signature-256`), exponential retry (`1m, 2m, 4m, 8m, 16m`), `webhook_deliveries` persistence, and IP-pinned `SocketsHttpHandler.ConnectCallback` SSRF protection (blocking IPv4/IPv6 loopback, RFC1918, ULA `fc00::/7`, link-local `169.254.0.0/16`, IPv4-mapped IPv6, and DNS rebinding); Smart HTTP `git-receive-pack` push triggers webhooks, CI/CD runs & `updated_at` | Package Registry (NuGet/Docker/npm), Wiki, Releases & Tag assets, Email/Matrix notifications |

---

## 2. Verification & Package Integrity Tools Included in This Archive

- `.github/workflows/ci-cd.yml` & `.gitea/workflows/ci-cd.yml`: Automated multi-stage CI/CD pipelines for GitHub Actions and Gitea Actions.
- `verify_all.sh`: Unified verification pipeline script (`set -euo pipefail`).
- `tools/csharp_lexer_check.py`: Static C# 12 lexer, escape sequence, and token-balance verifier across all 10 `.cs` files.
- `tools/verify_package_integrity.py`: Cross-checks every `using Gitea.Net...` namespace, `.csproj` reference, CI/CD workflow file, and `README.md` file path against the physical directory tree and the packaged `.zip` archive so no file is ever omitted (all 37 required files verified).
- `tools/python_reference_harness.py`: Python/SQLite/Git-CLI reference harness used inside the offline sandbox (where `.NET 8.0 SDK` is not installed) to test Git Smart HTTP (`git clone`, `git push` >30MB, `401`/`403`/`404` RBAC), 60-thread Issue sequence races, 50-thread PR merge races, and 50-thread CI/CD Actions sequence & CAS claim races + Zero N+1 SQL verification.

### Running on Your Machine (With `.NET 8.0 SDK`)
```bash
chmod +x verify_all.sh
./verify_all.sh
```
When `dotnet` is installed on your machine, `./verify_all.sh` automatically executes:
1. `dotnet build Gitea.Net.sln -c Release --warnaserror` (saving `artifacts/verification/dotnet_build_release.log`)
2. `dotnet test tests/Gitea.Net.Tests/Gitea.Net.Tests.csproj -c Release --logger "trx;LogFileName=test_results_sqlite.trx"`
3. If `GITEA_POSTGRES_CONN` is set, runs the full `xUnit` suite against PostgreSQL (`test_results_postgres.trx`).
