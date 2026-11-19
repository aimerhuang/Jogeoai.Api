#!/usr/bin/env bash
set -euo pipefail

usage() {
    echo 'Usage: migrate-local-feature.sh [--help|-h]'
    echo 'With no arguments, apply local migrations 019 then 020 to the running Compose mysql service.'
    echo 'Read .env and check database prerequisites before any migration. This script never starts MySQL.'
}

if (( $# > 0 )); then
    if (( $# == 1 )) && [[ "$1" == '--help' || "$1" == '-h' ]]; then
        usage
        exit 0
    fi
    echo 'Unexpected arguments. Only --help or -h is supported.' >&2
    usage >&2
    exit 2
fi

cd "$(dirname "$0")/.."

if [[ ! -f .env ]]; then
    echo 'Missing .env. Copy .env.example to .env and set the local MySQL values first.' >&2
    exit 1
fi

# Load the project .env as the only configuration source for this local,
# explicit migration entry point. MYSQL_PWD keeps mysql from prompting.
set -a
source .env
set +a

: "${MYSQL_DATABASE:?Set MYSQL_DATABASE in .env}"
: "${MYSQL_USER:?Set MYSQL_USER in .env}"
: "${MYSQL_PASSWORD:?Set MYSQL_PASSWORD in .env}"

migrations=(
    db/migrations/019_prompt_monitoring_credits.sql
    db/migrations/020_prompt_groups.sql
)

for migration in "${migrations[@]}"; do
    if [[ ! -f "$migration" ]]; then
        echo "Migration file not found: $migration" >&2
        exit 1
    fi
done

command -v docker >/dev/null || { echo 'Install Docker Desktop first.' >&2; exit 1; }
docker info >/dev/null 2>&1 || { echo 'Start Docker Desktop first.' >&2; exit 1; }

if ! mysql_container=$(docker compose ps --status running -q mysql) || [[ -z "$mysql_container" ]]; then
    echo 'The Compose mysql service must already be running. No feature migration was run.' >&2
    exit 1
fi

echo "Checking the running local MySQL service (database: ${MYSQL_DATABASE})."
if ! docker compose exec -T -e MYSQL_PWD="$MYSQL_PASSWORD" mysql \
    mysql --protocol=TCP -u"$MYSQL_USER" "$MYSQL_DATABASE" --execute 'SELECT 1' >/dev/null; then
    echo 'The local database is not ready or its credentials are invalid; no feature migration was run.' >&2
    exit 1
fi

# 016–018 are already deployed prerequisites for this feature. Check both the
# objects created by those migrations and the base tables referenced by 019/020
# before changing anything, so a partially upgraded database fails up front.
if ! missing_prerequisites=$(
    docker compose exec -T -e MYSQL_PWD="$MYSQL_PASSWORD" mysql \
        mysql --protocol=TCP --batch --skip-column-names -u"$MYSQL_USER" "$MYSQL_DATABASE" <<'SQL'
SELECT expected.table_name
FROM (
    SELECT 'analytics_snapshot_projections' AS table_name
    UNION ALL SELECT 'check_detailed_completions'
    UNION ALL SELECT 'question_groups'
    UNION ALL SELECT 'questions'
    UNION ALL SELECT 'analytics_snapshots'
    UNION ALL SELECT 'check_analysis'
    UNION ALL SELECT 'visibility_checks'
    UNION ALL SELECT 'analysis_releases'
    UNION ALL SELECT 'analysis_events'
    UNION ALL SELECT 'credit_accounts'
    UNION ALL SELECT 'sites'
    UNION ALL SELECT 'visibility_runs'
    UNION ALL SELECT 'credit_ledger'
    UNION ALL SELECT 'schedule_slots'
    UNION ALL SELECT 'prompts'
) AS expected
LEFT JOIN information_schema.tables AS actual
    ON actual.table_schema = DATABASE()
   AND actual.table_name = expected.table_name
WHERE actual.table_name IS NULL
ORDER BY expected.table_name;
SQL
); then
    echo 'Unable to inspect the local database for 016–018 prerequisites; no feature migration was run.' >&2
    exit 1
fi

if [[ -n "$missing_prerequisites" ]]; then
    echo 'Missing required 016–018 prerequisite tables; no feature migration was run:' >&2
    printf '  %s\n' "$missing_prerequisites" >&2
    echo 'Apply and verify migrations 016–018 first, then rerun this script.' >&2
    exit 1
fi

for migration in "${migrations[@]}"; do
    name="${migration##*/}"
    echo "Applying ${name} (safe to rerun)..."
    if ! docker compose exec -T -e MYSQL_PWD="$MYSQL_PASSWORD" mysql \
        mysql --protocol=TCP -u"$MYSQL_USER" "$MYSQL_DATABASE" <"$migration"; then
        echo "${name} failed; later migrations were not run. Fix the database and rerun this script." >&2
        exit 1
    fi
    echo "Applied ${name}."
done

echo 'Feature migrations 019 and 020 completed.'
