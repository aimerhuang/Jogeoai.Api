#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ ! -f .env ]]; then
    echo 'Run ./scripts/setup-local.sh first.' >&2
    exit 1
fi
set -a
source .env
set +a
case "${1:-api}" in
    api) exec dotnet run --project src/Jogeoai.Api --launch-profile http ;;
    worker) exec dotnet run --project src/Jogeoai.Worker --launch-profile Jogeoai.Worker ;;
    research) exec dotnet run --project src/Jogeoai.Worker --launch-profile Jogeoai.Worker -- --ResearchOnly=true ;;
    monitoring) exec dotnet run --project src/Jogeoai.Worker --launch-profile Jogeoai.Worker -- --MonitoringOnly=true ;;
    *) echo 'Usage: ./scripts/run-local.sh [api|worker|research|monitoring]' >&2; exit 1 ;;
esac
