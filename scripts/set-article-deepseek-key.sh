#!/usr/bin/env bash
set -euo pipefail

usage() {
    echo 'Usage: set-article-deepseek-key.sh [--help|-h]'
    echo 'Prompt for a DeepSeek API key without echo and store it in research_provider_settings'
    echo "row 'deepseek-article' (article generation), creating the row if migration 033 has not run."
    echo 'The key travels only through stdin to the running Compose mysql service.'
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
    echo 'Missing .env. Run ./scripts/setup-local.sh first.' >&2
    exit 1
fi

set -a
source .env
set +a

: "${MYSQL_DATABASE:?Set MYSQL_DATABASE in .env}"
: "${MYSQL_USER:?Set MYSQL_USER in .env}"
: "${MYSQL_PASSWORD:?Set MYSQL_PASSWORD in .env}"

if ! mysql_container=$(docker compose ps --status running -q mysql) || [[ -z "$mysql_container" ]]; then
    echo 'The Compose mysql service must already be running.' >&2
    exit 1
fi

read -r -s -p 'DeepSeek API key: ' key
echo
# Pasting into a terminal with bracketed paste on wraps the text in ESC[200~ / ESC[201~;
# drop those markers and any whitespace or control characters around the key.
key=${key//$'\e[200~'/}
key=${key//$'\e[201~'/}
key=$(printf '%s' "$key" | tr -d '[:space:][:cntrl:]')
# Restricting the alphabet keeps the key safe to embed in a SQL string literal.
if [[ ! "$key" =~ ^[A-Za-z0-9._-]{16,255}$ ]]; then
    echo 'The key must be 16-255 characters of letters, digits, ".", "_" or "-". Nothing was written.' >&2
    exit 1
fi
hint="****${key: -4}"

docker compose exec -T -e MYSQL_PWD="$MYSQL_PASSWORD" mysql \
    mysql --protocol=TCP --default-character-set=utf8mb4 --batch --skip-column-names \
    -u"$MYSQL_USER" "$MYSQL_DATABASE" <<SQL
INSERT INTO research_provider_settings
    (provider, model, api_url, credential, credential_hint, priority, enabled,
     web_search_enabled, timeout_seconds, created_at, updated_at)
VALUES
    ('deepseek-article', 'deepseek-flash', 'https://api.deepseek.com/chat/completions',
     '$key', '$hint', 100, TRUE, FALSE, 600, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE credential = VALUES(credential), credential_hint = VALUES(credential_hint),
    enabled = TRUE, updated_at = UTC_TIMESTAMP(6);
SELECT provider, model, api_url, credential_hint, enabled FROM research_provider_settings
WHERE provider = 'deepseek-article';
SQL
unset key
