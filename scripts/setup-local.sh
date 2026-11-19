#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

command -v dotnet >/dev/null || { echo 'Install .NET 10 SDK first.' >&2; exit 1; }
docker info >/dev/null 2>&1 || { echo 'Start Docker Desktop first.' >&2; exit 1; }
if [[ ! -f .env ]]; then
    cp .env.example .env
    chmod 600 .env
fi
set -a
source .env
set +a
: "${ConnectionStrings__Default:?Set ConnectionStrings__Default in .env}"

docker compose up -d --wait --wait-timeout 180 mysql
dotnet restore Jogeoai.slnx
for project in src/Jogeoai.Api src/Jogeoai.Worker; do
    dotnet user-secrets set 'ConnectionStrings:Default' "$ConnectionStrings__Default" --project "$project" >/dev/null
done
dotnet build Jogeoai.slnx --no-restore
echo 'Ready. Run ./scripts/run-local.sh api and ./scripts/run-local.sh worker in separate terminals, or use VS Code F5.'
