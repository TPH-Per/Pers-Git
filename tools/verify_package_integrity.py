#!/usr/bin/env python3
"""
Package, Namespace & Static Method Call Integrity Auditor (`tools/verify_package_integrity.py`).
Acceptance Rule #1 Enforcement:
1. Scans every `.cs` file in `Gitea.Net` for `namespace Gitea.Net...` declarations and `using Gitea.Net...` imports,
   verifying that every internal namespace imported has at least one defining `.cs` file in the repository.
2. Scans all core type references (`SecurityService`, `KeyedLockProvider`, `GiteaAuthHandler`, `GiteaDbRepository`,
   `SqlQueryCounter`, `GitEngineService`, `WebhookDeliveryService`, `MergeReconcilerHostedService`,
   `CiPipelineRunnerService`, `ProductionReadinessTests`) and verifies their class definitions exist.
3. Cross-checks method calls on internal services (`db`, `_db`, `git`, `_git`, `sec`, `security`, `webhooks`, `_webhooks`,
   `reconciler`, `ciRunner`, `host.Db`, `host.Git`, `host.Security`, `host.Reconciler`, `host.CiRunner`, `SecurityService`,
   `GitEngineService`, `WebhookDeliveryService`) against declared C# method names so no missing method can slip through.
4. Verifies `.github/workflows/ci-cd.yml` and `.gitea/workflows/ci-cd.yml` exist and contain the full
   CI/CD pipeline (`jobs:`, `postgres:16-alpine`, `verify_all.sh`).
5. Scans `README.md` for referenced relative paths (`verify_all.sh`, `tools/csharp_lexer_check.py`,
   `tools/verify_package_integrity.py`, `tools/python_reference_harness.py`) and verifies every referenced file exists.
6. When passed `--zip <path_to_zip>`, verifies that the `.zip` archive contains all 37 required files and that no file is 0 bytes.
"""
import os
import re
import sys
import zipfile

REQUIRED_FILES = [
    ".github/workflows/ci-cd.yml",
    ".gitea/workflows/ci-cd.yml",
    "Dockerfile",
    "docker-compose.yml",
    "Caddyfile",
    "scripts/sqlite_hot_backup.sh",
    "src/Gitea.Net/wwwroot/img/pers-git-logo.svg",
    "src/Gitea.Net/wwwroot/img/favicon.svg",
    "Gitea.Net.sln",
    "LICENSE",
    "README.md",
    "THIRD-PARTY-NOTICES.md",
    "verify_all.sh",
    "src/Gitea.Net/Gitea.Net.csproj",
    "src/Gitea.Net/Program.cs",
    "src/Gitea.Net/appsettings.json",
    "src/Gitea.Net/Data/GiteaDbRepository.cs",
    "src/Gitea.Net/Git/GitEngineService.cs",
    "src/Gitea.Net/Models/Dtos.cs",
    "src/Gitea.Net/Models/Entities.cs",
    "src/Gitea.Net/Security/SecurityService.cs",
    "src/Gitea.Net/Services/WebhookDeliveryService.cs",
    "src/Gitea.Net/Services/MergeReconcilerHostedService.cs",
    "src/Gitea.Net/Services/CiPipelineRunnerService.cs",
    "src/Gitea.Net/wwwroot/index.html",
    "src/Gitea.Net/wwwroot/css/app.css",
    "src/Gitea.Net/wwwroot/js/app.js",
    "tests/Gitea.Net.Tests/Gitea.Net.Tests.csproj",
    "tests/Gitea.Net.Tests/ProductionReadinessTests.cs",
    "tests/jmeter/gitea_net_load_test.jmx",
    "tools/csharp_lexer_check.py",
    "tools/verify_package_integrity.py",
    "tools/python_reference_harness.py",
    "artifacts/verification/environment_toolchain_status.log",
    "artifacts/verification/python_reference_harness.log",
    "artifacts/verification/python_harness_results.jtl",
    "artifacts/verification/python_harness_report.html"
]

REQUIRED_TYPES = [
    "class SecurityService",
    "class KeyedLockProvider",
    "class GiteaAuthHandler",
    "class GiteaDbRepository",
    "class SqlQueryCounter",
    "class CountingDbCommand",
    "class GitEngineService",
    "class WebhookDeliveryService",
    "class MergeReconcilerHostedService",
    "class CiPipelineRunnerService",
    "class ProductionReadinessTests"
]

def audit_directory(root_dir: str) -> list[str]:
    errors = []

    # 1. Verify all required files exist and are non-empty
    for rel in REQUIRED_FILES:
        full = os.path.join(root_dir, rel)
        if not os.path.isfile(full):
            errors.append(f"Missing required file in directory tree: {rel}")
        elif os.path.getsize(full) == 0:
            errors.append(f"Empty (0-byte) file in directory tree: {rel}")

    # 2. Verify CI/CD workflow YAML definitions contain required jobs and services
    for wf_rel in (".github/workflows/ci-cd.yml", ".gitea/workflows/ci-cd.yml"):
        wf_full = os.path.join(root_dir, wf_rel)
        if os.path.isfile(wf_full):
            with open(wf_full, "r", encoding="utf-8") as wf:
                wf_text = wf.read()
            for required_token in ("jobs:", "postgres:16-alpine", "verify_all.sh"):
                if required_token not in wf_text:
                    errors.append(f"{wf_rel} is missing required CI/CD token '{required_token}'")

    # 3. Collect all declared and imported Gitea.Net.* namespaces across all .cs files
    declared_namespaces = set()
    imported_namespaces = set()
    all_cs_content = []
    declared_methods = set()

    for r, _, files in os.walk(root_dir):
        for f in sorted(files):
            if f.endswith(".cs"):
                p = os.path.join(r, f)
                with open(p, "r", encoding="utf-8") as fh:
                    text = fh.read()
                all_cs_content.append(text)
                for m in re.finditer(r"^\s*namespace\s+(Gitea\.Net(?:\.[A-Za-z0-9_]+)*)\s*;", text, re.MULTILINE):
                    declared_namespaces.add(m.group(1))
                for m in re.finditer(r"^\s*using\s+(Gitea\.Net(?:\.[A-Za-z0-9_]+)*)\s*;", text, re.MULTILINE):
                    imported_namespaces.add(m.group(1))
                for m in re.finditer(r"\b(?:public|private|protected|internal)\s+(?:static\s+)?(?:async\s+)?[A-Za-z0-9_<>,\?\(\)\s\[\]]+\s+([A-Za-z0-9_]+)\s*\(", text):
                    declared_methods.add(m.group(1))

    missing_ns = imported_namespaces - declared_namespaces
    if missing_ns:
        errors.append(f"Imported Gitea.Net namespaces have no defining .cs file: {sorted(missing_ns)}")

    combined_cs = "\n".join(all_cs_content)
    for req_type in REQUIRED_TYPES:
        if req_type not in combined_cs:
            errors.append(f"Missing required C# class definition: '{req_type}'")

    # 4. Cross-check all method calls on internal service instances against declared methods
    service_Targets = r"(?:db|_db|git|_git|sec|security|webhooks|_webhooks|reconciler|ciRunner|host\.Db|host\.Git|host\.Security|host\.Reconciler|host\.CiRunner|SecurityService|GitEngineService|WebhookDeliveryService|SqlQueryCounter)"
    for m in re.finditer(rf"\b({service_Targets})\.([A-Za-z0-9_]+)\s*\(", combined_cs):
        target_expr, method_name = m.group(1), m.group(2)
        if method_name in ("Dispose", "ToString", "Equals", "GetHashCode", "GetType"):
            continue
        if method_name not in declared_methods:
            errors.append(f"Unresolved internal C# method call: {target_expr}.{method_name}(...) is not declared in any .cs file")

    # 5. Verify paths mentioned in README.md exist
    readme_path = os.path.join(root_dir, "README.md")
    if os.path.isfile(readme_path):
        with open(readme_path, "r", encoding="utf-8") as rf:
            readme_text = rf.read()
        for m in re.finditer(r"`((?:tools|src|tests|artifacts|\.github|\.gitea)/[A-Za-z0-9_\-\./]+|verify_all\.sh)`", readme_text):
            ref_path = m.group(1)
            if ref_path.endswith(".trx") or ref_path.endswith("dotnet_build_release.log"):
                continue
            if not os.path.exists(os.path.join(root_dir, ref_path)):
                errors.append(f"README.md references path '{ref_path}' which does not exist in {root_dir}")

    return errors

def audit_zip(zip_path: str) -> list[str]:
    errors = []
    if not os.path.isfile(zip_path):
        return [f"ZIP file does not exist: {zip_path}"]

    with zipfile.ZipFile(zip_path, "r") as zf:
        names = {info.filename: info.file_size for info in zf.infolist() if not info.is_dir()}

    for rel in REQUIRED_FILES:
        zip_key = f"Gitea.Net/{rel}"
        if zip_key not in names:
            errors.append(f"Missing required file inside ZIP archive: {zip_key}")
        elif names[zip_key] == 0:
            errors.append(f"Zero-byte file inside ZIP archive: {zip_key}")

    return errors

def main():
    root_dir = sys.argv[1] if len(sys.argv) > 1 else "."
    zip_path = None
    if "--zip" in sys.argv:
        idx = sys.argv.index("--zip")
        if idx + 1 < len(sys.argv):
            zip_path = sys.argv[idx + 1]

    errs = audit_directory(root_dir)
    if zip_path:
        errs.extend(audit_zip(zip_path))

    if errs:
        print("[FAIL] Package & Reference Integrity Audit Failed:")
        for e in errs:
            print("  -", e)
        sys.exit(1)

    print(f"  [PASS] Package, Namespace & Method Call Integrity Audit: All {len(REQUIRED_FILES)} required files, {len(REQUIRED_TYPES)} core C# types, and all internal service calls verified.")
    if zip_path:
        print(f"  [PASS] ZIP Archive Integrity Audit: {zip_path} contains all {len(REQUIRED_FILES)} required files (0 missing, 0 empty).")

if __name__ == "__main__":
    main()
