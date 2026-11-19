#!/usr/bin/env bash
# Builds the public repository tree (github.com/aimerhuang/Jogeoai.Api) from two local sources:
#   - this Jogeoai.Api directory       -> repository root
#   - the Ai-visiblility-collector dir -> collector/
# The target directory keeps its own .git, so re-running the script and committing
# publishes the next snapshot. Nothing in the two source directories is modified.
#
# Usage: ./scripts/export-public-repo.sh [target-dir] [collector-dir]
#   target-dir     default /Library/Project/Jogeoai-public
#   collector-dir  default /Library/Project/Ai-visiblility-collector
#
# The exclusion lists below are the publication boundary: secrets, database backups,
# local data, development records (.trellis holds real collected data and the security
# audit), agent tooling, third-party references and build output never leave this machine.
set -euo pipefail

api_dir="$(cd "$(dirname "$0")/.." && pwd)"
target_dir="${1:-/Library/Project/Jogeoai-public}"
collector_dir="${2:-/Library/Project/Ai-visiblility-collector}"

if [[ ! -f "$api_dir/Jogeoai.slnx" ]]; then
    echo "Not a Jogeoai.Api checkout: $api_dir" >&2
    exit 1
fi
if [[ ! -f "$collector_dir/pnpm-workspace.yaml" ]]; then
    echo "Not a collector checkout: $collector_dir" >&2
    exit 1
fi
case "$target_dir" in
    "$api_dir"|"$api_dir"/*|"$collector_dir"|"$collector_dir"/*)
        echo "The target must be outside both source directories: $target_dir" >&2
        exit 1 ;;
esac
mkdir -p "$target_dir"

common_excludes=(
    --exclude=.git/
    --include='.env.example'
    --exclude=.env
    --exclude='.env.*'
    --exclude=bin/
    --exclude=obj/
    --exclude=node_modules/
    --exclude=dist/
    --exclude=coverage/
    --exclude=.DS_Store
    --exclude=.idea/
    --exclude=.vscode/
    --exclude=.trellis/
    --exclude=.claude/
    --exclude=.codex/
    --exclude=.agents/
    --exclude=.aoci/
    --exclude='aoci*.txt'
    --exclude='.mcp.json*'
    --exclude=.turbo/
    --exclude=playwright-report/
    --exclude=test-results/
)

# The target's own .git is moved aside during the sync and always put back: macOS ships
# openrsync, which does not honour protect (P) filter rules, and --delete-excluded would
# otherwise delete the target repository.
git_stash_dir=""
if [[ -d "$target_dir/.git" ]]; then
    git_stash_dir="$(mktemp -d "${TMPDIR:-/tmp}/jogeoai-export-git.XXXXXX")"
    mv "$target_dir/.git" "$git_stash_dir/.git"
fi
restore_git() {
    if [[ -n "$git_stash_dir" && -d "$git_stash_dir/.git" ]]; then
        rm -rf "$target_dir/.git"
        mv "$git_stash_dir/.git" "$target_dir/.git"
        rmdir "$git_stash_dir"
    fi
}
trap restore_git EXIT

# Root: Jogeoai.Api. collector/ is rebuilt by the second rsync below.
rsync -a --delete --delete-excluded \
    --include='/deploy/.env.production.example' \
    "${common_excludes[@]}" \
    --exclude=.backups/ \
    --exclude=.data/ \
    --exclude=.local/ \
    --exclude=/references/ \
    --exclude=/collector/ \
    "$api_dir/" "$target_dir/"

# collector/: Ai-visiblility-collector working tree (including uncommitted changes).
mkdir -p "$target_dir/collector"
rsync -a --delete --delete-excluded \
    "${common_excludes[@]}" \
    --exclude='/AGENTS.md.backup.*' \
    --exclude=.collector-spool/ \
    "$collector_dir/" "$target_dir/collector/"

echo "Exported to $target_dir"
echo "Review before publishing: cd \"$target_dir\" && git status"
