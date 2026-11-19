# AI Visibility Collector

Independent control plane and regional collector foundation for monitoring synthetic question matrices across ten consumer AI web platforms.

The default Compose topology uses Mock Collectors. It proves tenant-scoped scheduling, region-safe leases, lease recovery, idempotent result submission, and atomic answer persistence. An explicit, bounded native `real` mode adds manual profile login, heartbeat, approval guard and encrypted retry spool, with browser adapters for nine of the ten platforms (all except Claude). Doubao's four-query native smoke passed on 2026-09-07, but each reviewed live page contract remains an operator-managed dependency; real mode is not production readiness, and synthetic fixture tests are not evidence of live search success.

Two attended, explicit opt-in extensions build on real mode: an account pool with challenge cooldown for several authorized accounts per platform, and a local Android-emulator App channel that runs either as an attended local trial or, since 2026-11-17, as a resident node that leases App tasks from the Jogeoai control plane (`collector:app:serve`). See [Runbooks](#runbooks).

## Requirements

- Node.js 24.22.3 (WebdriverIO, used by the App channel, declares 24.22.1 as its minimum on the 24 line)
- pnpm 10.34.5
- Docker 29+ with Compose
- PostgreSQL 18 (provided by Compose)

Playwright is pinned to 1.62.1; no browser is launched by the Mock Collector. See [real Doubao smoke setup](docs/runbooks/real-doubao-smoke.md) for the isolated API 4314 path. Do not point a real Collector at the 4311 Mock queue or the 4312 development fixture queue.

On 2026-10-22, the Tencent Yuanbao adapter completed 10 approved questions for
Ulinktek against the local Jogeoai API on port 5260 (run
`f7d6bcaf-a645-4d3d-8f57-9263456385a9`). All 10 tasks succeeded with one attempt
each; 263 citation URLs were persisted, 262 with titles, and all 10 analyses
completed. The adapter reads the reviewed source drawer and closes its actual
button; answers remain plain text and search queries are unavailable. This was
a manually initiated round using an authorized dedicated profile. See the
[Yuanbao runbook](docs/runbooks/real-yuanbao-smoke.md) for current page-review
requirements and recovery rules.

On 2026-10-22, Qwen collection against the local Jogeoai API on port 5260
completed all 10 approved Ulinktek questions (run
`f021f463-d56e-494e-9cd8-689bb3b550c8`), with 145 titled citations across
47 domains and all 10 analyses completed. Monitoring record IDs match the
run's 10 checks. After manual platform verification, existing answers were
recovered from their original conversations; one expired lease required
reacquisition without another browser prompt. The attempt ledger contains
10 distinct tasks/questions, and no encrypted submissions remain pending.
Answers are plain text, search keywords are unknown, and this round was
manually initiated. Qwen's source reader and empty-composer regression checks
pass 22 targeted tests, type checking, scoped lint, and formatting. See the
[Qwen runbook](docs/runbooks/real-qwen-smoke.md).

## Browser question prefix

Removed on 2026-11-16. The Web channel and the local App channel
(`docs/runbooks/app-channel-local.md`) both send the approved question as it is,
with its whitespace preserved and nothing before it.

Between 2026-10-23 and 2026-11-16 the shared browser flow put a fixed instruction
(clear the context, treat every question as independent, always search the web)
and two newline characters before each question. Answers collected in that period
were asked with that prefix; they are not retrofitted, and this change does not
initiate any collection or historical retry. Without the instruction a platform
may answer without searching, so an answer can carry no sources.

Start a new Collector process on this code for the change to apply; already
running processes keep the text they were started with. Mock collection, Cloro
and official model API channels never used the prefix.

## Qingyan answer formatting

Since 2026-10-23, `QingyanAdapter` uses the shared `readAnswerMarkdown` formatter.
New answers preserve headings, paragraph spacing, lists, tables and links;
multiple visible body chunks are separated by blank lines. A reviewed body
selector and `answerIgnore` exclude reasoning, diagrams, source summaries and
inline source chips. The existing source-sidebar reader still collects the
citation URLs. An empty or failed conversion retains the existing plain-text
fallback; read back accepted answers to verify the actual result.

The approved Ulinktek round completed on 2026-10-23 against Jogeoai API 5260
(run `84c9650d-0060-449b-bac5-7e074fa7f671`): all 10 checks and analyses succeeded,
with 140 citation URLs across 98 domains. All answers retain Markdown structure
(2,447–3,883 Unicode characters and 10–31 paragraph blocks). Each question used
the approved prefix above in a new conversation. The ninth answer was recovered
from its original conversation after exact question/plain/Markdown hash checks;
its expired lease was reacquired, while the browser ledger stayed at one send
per task. All 10 monitoring records match the run's checks, no encrypted results
remain pending, and profile/spool locks are released. Citation titles and search
queries are unavailable. The 69 focused offline tests establish adapter behavior;
API, read-only SQL and ledger checks separately establish this live acceptance.
The round was manually initiated and did not backfill historical answers.
See the [Qingyan runbook](docs/runbooks/real-qingyan-smoke.md) for page review,
queue isolation and original-conversation recovery requirements.

## Runbooks

- [Local development](docs/runbooks/local-development.md): regions and service tokens, resetting fixture data, the collector spool.
- Real mode, one runbook per platform: [Doubao](docs/runbooks/real-doubao-smoke.md), [Ernie](docs/runbooks/real-ernie-smoke.md), [Qingyan](docs/runbooks/real-qingyan-smoke.md), [Qwen](docs/runbooks/real-qwen-smoke.md), [DeepSeek](docs/runbooks/real-deepseek-smoke.md), [Tencent Yuanbao](docs/runbooks/real-yuanbao-smoke.md), [ChatGPT](docs/runbooks/real-chatgpt-smoke.md), [Gemini](docs/runbooks/real-gemini-smoke.md). Kimi has an adapter but no runbook.
- [Account pool and challenge cooldown](docs/runbooks/account-pool.md): several accounts per platform, quotas, breaker, the pool supervisor.
- [App channel on local emulators](docs/runbooks/app-channel-local.md): the `collector:app:*` commands, limits, contracts, failure classes.
- [App channel node (control-plane mode)](docs/runbooks/app-channel-node.md): `collector:app:serve`, the resident unattended node that leases App tasks from Jogeoai and submits answers.

## Local setup

```bash
cp .env.example .env
npx -y pnpm@10.34.5 install --frozen-lockfile
docker compose up -d postgres
DATABASE_URL=postgres://ai_visibility:local-development-only@localhost:55432/ai_visibility npx -y pnpm@10.34.5 db:migrate
DATABASE_URL=postgres://ai_visibility:local-development-only@localhost:55432/ai_visibility npx -y pnpm@10.34.5 db:seed
DATABASE_URL=postgres://ai_visibility:local-development-only@localhost:55432/ai_visibility npx -y pnpm@10.34.5 db:schedule
```

The fixture is explicitly synthetic: one tenant, one brand project, ten stable platform entries, and thirty Chinese questions prefixed with `【合成测试】`. Scheduling the same Shanghai calendar date repeatedly reports 300 planned tasks and inserts no duplicates after the first run.

Start the complete Mock topology:

```bash
docker compose --env-file .env up -d --build
npx -y pnpm@10.34.5 compose:health
```

Local endpoints use non-default ports:

- API health: `http://127.0.0.1:4311/health`
- PostgreSQL: `127.0.0.1:55432`

## Quality gates

```bash
npx -y pnpm@10.34.5 format:check
npx -y pnpm@10.34.5 lint
npx -y pnpm@10.34.5 typecheck
npx -y pnpm@10.34.5 test
npx -y pnpm@10.34.5 test:integration
npx -y pnpm@10.34.5 test:infrastructure
npx -y pnpm@10.34.5 secrets:check
npx -y pnpm@10.34.5 compose:config
```

The integration suite expects PostgreSQL to be running on port `55432` unless `DATABASE_URL` is overridden.

## Security boundary

Collectors receive only a tenant identifier, regional configuration, and a region-specific service token. The API maps the CN and overseas tokens to fixed regional identities and rejects cross-region lease, heartbeat, and result operations. Collectors do not receive PostgreSQL credentials. Real platform credentials, cookies, proxy URLs, and session state must remain outside Git and images. The sample values in `.env.example` are development-only placeholders.
