#!/usr/bin/env bash
# ==============================================================================
# Per's Git (Gitea.Net) — Online SQLite WAL Hot Backup Script
# Uses SQLite's atomic `VACUUM INTO` command (via sqlite3 CLI or python3 sqlite3)
# to create a consistent snapshot while the server is actively handling writes,
# verifies `PRAGMA integrity_check`, writes a `.sha256` digest, and prunes old backups.
# ==============================================================================
set -euo pipefail

SOURCE_DB="${1:-App_Data/gitea_net.db}"
BACKUP_DIR="${2:-App_Data/backups}"
RETENTION_DAYS="${3:-7}"

if [[ ! -f "$SOURCE_DB" ]]; then
    echo "[BACKUP SKIP] Source SQLite database '$SOURCE_DB' does not exist yet."
    exit 0
fi

mkdir -p "$BACKUP_DIR"
TIMESTAMP="$(date -u +"%Y%m%d_%H%M%S")"
TARGET_FILE="$BACKUP_DIR/gitea_backup_${TIMESTAMP}.db"

echo "[BACKUP START] Creating online hot backup of '$SOURCE_DB' -> '$TARGET_FILE' via VACUUM INTO..."
if command -v sqlite3 >/dev/null 2>&1; then
    sqlite3 "$SOURCE_DB" ".timeout 15000" "VACUUM INTO '$TARGET_FILE';"
    INTEGRITY="$(sqlite3 "$TARGET_FILE" "PRAGMA integrity_check;")"
else
    INTEGRITY="$(python3 -c "
import sqlite3, sys
src, dst = sys.argv[1], sys.argv[2]
conn = sqlite3.connect(src, timeout=15.0)
conn.execute('VACUUM INTO ?', (dst,))
conn.close()
chk = sqlite3.connect(dst)
res = chk.execute('PRAGMA integrity_check;').fetchone()[0]
chk.close()
print(res)
" "$SOURCE_DB" "$TARGET_FILE")"
fi

if [[ "$INTEGRITY" != "ok" ]]; then
    echo "[BACKUP ERROR] Integrity check failed on '$TARGET_FILE': $INTEGRITY" >&2
    rm -f "$TARGET_FILE"
    exit 1
fi

if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$TARGET_FILE" > "${TARGET_FILE}.sha256"
elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$TARGET_FILE" > "${TARGET_FILE}.sha256"
fi

chmod 0600 "$TARGET_FILE"
echo "[BACKUP SUCCESS] Verified hot backup '$TARGET_FILE' (integrity_check=ok)."

find "$BACKUP_DIR" -name "gitea_backup_*.db*" -type f -mtime +"$RETENTION_DAYS" -delete 2>/dev/null || true
