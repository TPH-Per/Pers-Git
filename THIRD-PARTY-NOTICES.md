# Third-Party Open Source Licenses & Transitive Dependency Audit

This project strictly complies with the technology and open-source licensing policy:

## 1. Direct & Transitive .NET NuGet Dependencies
| Package / Component | License | Scope | Notes |
| :--- | :--- | :--- | :--- |
| **.NET 8.0 SDK / ASP.NET Core** | `MIT License` | Runtime & Web Framework | Official Microsoft .NET Core runtime |
| **`Microsoft.Data.Sqlite` (8.0.10)** | `MIT License` | SQLite ADO.NET Driver | Official Microsoft ADO.NET provider for SQLite |
| **`SQLitePCLRaw.bundle_e_sqlite3`** | `Apache-2.0 License` | Transitive dependency of `Microsoft.Data.Sqlite` | Low-level P/Invoke binding for SQLite |
| **Native `e_sqlite3` / SQLite Engine** | `Public Domain` | Embedded C library | Dedicated to the public domain by SQLite authors |
| **`Npgsql` (8.0.5)** | `PostgreSQL License` | PostgreSQL ADO.NET Driver | OSI-approved permissive license compatible with MIT & Apache-2.0 |
| **`xUnit` (2.9.2)** | `Apache-2.0 License` | Automated Test Runner | Used in `tests/Gitea.Net.Tests` |
| **`Microsoft.AspNetCore.Mvc.Testing`** | `MIT License` | In-Memory Integration Test Host | Used in `tests/Gitea.Net.Tests` |

## 2. External CLI Process Integration
- **Git for Windows (`git.exe`) / POSIX `git` CLI**: Licensed under `GPLv2`.
  - **Architectural Isolation Note**: `Gitea.Net` does **not** link against, bundle, or distribute any GPL source code or binary libraries (`libgit2` is not used). It invokes the operating system's installed `git` executable strictly out-of-process via standard CLI arguments (`System.Diagnostics.ProcessStartInfo.ArgumentList`) and standard input/output streams.

## 3. Frontend Assets (`wwwroot/`)
- **100% Pure Vanilla HTML5, CSS3, and ES6+ JavaScript** (`MIT License`).
- Zero third-party frontend frameworks, zero CDN scripts, and zero unlicensed UI libraries.
- Custom native `<dialog>` modal system and animated Snackbar/Toast queue (`GiteaSnackbar`) written from scratch.
