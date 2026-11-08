# Gitea.Net — Self-Hosted .NET 8.0 Git Service (Gitea-Inspired MVP)

`Gitea.Net` is a self-hosted Git service MVP inspired by Gitea, engineered in **.NET Core 8.0 (`ASP.NET Core`)** with **PostgreSQL (`Npgsql`)**, **SQLite (`Microsoft.Data.Sqlite`)**, **Git for Windows (`git.exe`) / POSIX `git` CLI**, and a **100% Pure Vanilla HTML5/CSS3/ES6 Frontend**.

---

## 1. Honest MVP Scope Matrix (vs. Upstream Go Gitea)

Upstream Gitea is a multi-hundred-thousand-line Go platform. `Gitea.Net` is a **focused, production-hardened MVP** implementing core repository hosting, Git Smart HTTP transport, plumbing commits/merges, RBAC authorization, issue tracking, pull request merging, and IP-pinned SSRF-safe webhook delivery:

| Feature Area | Implemented in `Gitea.Net` MVP | Out of Scope (Not Implemented in MVP) |
| :--- | :--- | :--- |
| **Git Transport & Engine** | Startup `git --version >= 2.38` verification, bare repo init (`receive.fsckObjects=true`, `safe.directory=*`), Git Smart HTTP (`info/refs`, `git-upload-pack`, `git-receive-pack`) with sliding idle timeout (`120s`), concurrent process semaphore (`32`), unlimited packfile body size, O(1) Git plumbing commits (`hash-object`, `update-index --add --replace`, `write-tree`, `commit-tree`, `update-ref`), zero-OID atomic branch creation, pure C# `ValidateRefFormat`, binary blob detection & size truncation | SSH protocol server (`git-shell` over port 22), Git LFS (Large File Storage), signed GPG commit verification, repository mirrors & migrations |
| **Authentication & RBAC** | `GiteaAuthHandler` (`HttpOnly` `SameSite=Strict` session cookies + `X-Gitea-Csrf: 1` + `Origin` CSRF validation, `Bearer` tokens, `Basic` auth with 60s HMAC cache & per-`(IP, username)` brute-force lockout, scoped & revocable Personal Access Tokens `pat_...` with `last_used_at`), 600,000-iteration PBKDF2-SHA256 (`v1$600000$...`) with transparent legacy `100k` rehash, Owner / Collaborator (`read`/`write`/`admin`) / Public-Read authorization matrix (returning `404` on unauthorized private repos) | Organizations & Teams hierarchy, OAuth2/OIDC/LDAP/SAML SSO, TOTP/WebAuthn 2FA, fine-grained branch protection rules |
| **Issues & Pull Requests** | Shared per-repository atomic sequence (`repo_counters.last_issue_index`) across Issues & PRs, O(1) maintained counter columns (`open_issues_count`, `open_prs_count`, `comments_count`) + admin reconciliation endpoint, optimistic concurrency (`row_version` CAS), crash-safe 3-phase atomic PR merge (`open` -> `merging` -> `merged`/`open`) via `git merge-tree --write-tree` + `MergeReconcilerHostedService` (`git merge-base --is-ancestor`), PR close/reopen (`PATCH .../state`) | Line-by-line code review comments, PR approvals/dismissals, Milestones, Kanban project boards, issue dependencies & attachments |
| **Webhooks & Audit** | Background outbox webhook dispatcher (`WebhookDeliveryService` via `Channel<T>`) with mandatory/auto-generated HMAC-SHA256 signing (`X-Gitea-Signature-256`) and IP-pinned `SocketsHttpHandler.ConnectCallback` SSRF protection (blocking IPv4/IPv6 loopback, RFC1918, ULA `fc00::/7`, link-local `169.254.0.0/16`, IPv4-mapped IPv6, and DNS rebinding); Smart HTTP `git-receive-pack` push triggers webhooks & `updated_at`; background token/audit cleanup | Gitea Actions (CI/CD runner engine), Package Registry (NuGet/Docker/npm), Wiki, Releases & Tag assets, Email/Matrix notifications |

---

## 2. Verification & Package Integrity Tools Included in This Archive

- `verify_all.sh`: Unified verification pipeline script (`set -euo pipefail`).
- `tools/csharp_lexer_check.py`: Static C# 12 lexer, escape sequence, and token-balance verifier across all `.cs` files.
- `tools/verify_package_integrity.py`: Cross-checks every `using Gitea.Net...` namespace, `.csproj` reference, and `README.md` file path against the physical directory tree and the packaged `.zip` archive so no file is ever omitted.
- `tools/python_reference_harness.py`: Python/SQLite/Git-CLI reference harness used inside the offline sandbox (where `.NET 8.0 SDK` is not installed) to test Git Smart HTTP (`git clone`, `git push` >30MB, slow stream >60s, `401`/`403`/`404` RBAC) and SQLite concurrency.

### Running on Your Machine (With `.NET 8.0 SDK`)
```bash
chmod +x verify_all.sh
./verify_all.sh
```
When `dotnet` is installed on your machine, `./verify_all.sh` automatically executes:
1. `dotnet build Gitea.Net.sln -c Release --warnaserror` (saving `artifacts/verification/dotnet_build_release.log`)
2. `dotnet test tests/Gitea.Net.Tests/Gitea.Net.Tests.csproj -c Release --logger "trx;LogFileName=test_results_sqlite.trx"`
3. If `GITEA_POSTGRES_CONN` is set, runs the full `xUnit` suite against PostgreSQL (`test_results_postgres.trx`).
