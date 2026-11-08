#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT_DIR"

echo "=============================================================================="
echo "Gitea.Net Unified Verification Pipeline (verify_all.sh)"
echo "=============================================================================="

# 1. Strict C# 12 Lexer, Escape & Token Balance Audit (All 8 .cs files)
python3 tools/csharp_lexer_check.py "$ROOT_DIR"

# 2. Run Python/SQLite/Git-CLI Reference Harness (Generates artifacts/verification/*)
python3 tools/python_reference_harness.py

# 3. Verify Package, Namespace & README Reference Integrity
python3 tools/verify_package_integrity.py "$ROOT_DIR"

# 4. If .NET 8.0 SDK is installed on this host, run Release build (TreatWarningsAsErrors=true) and xUnit tests
if command -v dotnet >/dev/null 2>&1; then
    echo ""
    echo "[DOTNET] Running 'dotnet build Gitea.Net.sln -c Release --warnaserror'..."
    dotnet build Gitea.Net.sln -c Release --warnaserror | tee artifacts/verification/dotnet_build_release.log

    echo ""
    echo "[DOTNET] Running xUnit Test Suite on SQLite provider..."
    GITEA_TEST_PROVIDER="Sqlite" dotnet test tests/Gitea.Net.Tests/Gitea.Net.Tests.csproj -c Release \
        --logger "trx;LogFileName=test_results_sqlite.trx" \
        --results-directory "$ROOT_DIR/artifacts/verification"

    if [[ -n "${GITEA_POSTGRES_CONN:-}" ]]; then
        echo ""
        echo "[DOTNET] Running xUnit Test Suite on PostgreSQL provider..."
        GITEA_TEST_PROVIDER="Postgres" GITEA_POSTGRES_CONN="$GITEA_POSTGRES_CONN" \
            dotnet test tests/Gitea.Net.Tests/Gitea.Net.Tests.csproj -c Release \
            --logger "trx;LogFileName=test_results_postgres.trx" \
            --results-directory "$ROOT_DIR/artifacts/verification"
    fi
else
    echo ""
    echo "[STATUS: CHƯA CHẠY .NET SDK] 'dotnet' CLI is not installed on this machine."
    echo "C# lexical/syntax audit, namespace/file integrity check, and Git/SQLite reference harness PASSED."
fi

echo ""
echo "Verification pipeline completed successfully (exit code 0)."
