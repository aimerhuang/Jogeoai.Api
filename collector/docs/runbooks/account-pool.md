# Account pool and challenge cooldown (native, explicit opt-in)

## Status and boundary

This runbook covers several authorized accounts per platform, the control-plane quota and
breaker gates, the challenge (CAPTCHA/verification) wait, and the local pool supervisor. It
builds on the per-platform real-smoke runbooks (for example `real-doubao-smoke.md`): page
contracts, approved question files, spool keys, profile permissions and egress checks still
apply unchanged.

The pool only spreads authorized load and makes recovery faster. It does not make a platform
accept more traffic than a single human would send, and it is not a way around platform
controls. Whether several accounts on one platform are permitted is a business/terms decision
made before any account is registered.

Never, in any configuration:

- solve, outsource or automate a CAPTCHA, image selection or verification step;
- change browser fingerprints, user agents or stealth switches;
- humanize input (mouse paths, typing cadence, random delays meant to look human);
- give each account its own proxy or residential IP to hide that accounts are related;
- register or buy accounts in bulk.

A challenge is resolved only by the operator, by hand, in that account's profile window.

The pool needs a control plane that implements the account-pool endpoints added in this
repository (`/internal/collector/eligibility`, `/failures`, `/session-events`, profile-aware
leases) and migration `0016_session_profiles_and_challenge_cooldown.sql`. As of 2026-10-28 only
this repository's API does. The production control plane, Jogeoai (`127.0.0.1:5260`), does not
yet; implementing the same contract there is a follow-up, and the attended two-account smoke
runs against Jogeoai after that. Single-account runs without `COLLECTOR_SESSION_PROFILE_ID` work
against any of them unchanged.

## Concepts

- **Session profile**: one authorized account on one platform in one region, registered in
  the control plane. It stores only metadata (label, status, quotas, timestamps). Cookies,
  passwords, proxy credentials and account names never enter the database, Git or logs.
- **One profile directory per account**: each account gets its own persistent Chromium profile
  directory (or its own user-managed CDP Chrome). Log in once and reuse it. Never switch
  accounts by logging out and in inside one browser profile.
- **The control plane decides who works**: on every lease it checks the account's status,
  cooldown, hourly/daily quota and minimum interval, prefers the least recently used online
  account, and applies the platform gate (minimum dispatch interval, daily cap, breaker). The
  collector and the pool never grant themselves work.
- **Profile statuses**: `active`, `cooling` (rate limited, returns to `active` automatically
  after `cooldownUntil`), `needs_verification` (challenge; human recovery), `expired` (login
  lost; human recovery), `disabled` (operator). Every change and every challenge-related event
  is an immutable `session_health_events` row.
- **Eligibility before page access**: a profile-mode collector first calls
  `POST /internal/collector/eligibility`. The control plane runs the same profile, fairness,
  platform-gate and pending-task checks as the lease, but takes no lease and locks no task; its
  only write is `lastSeenAt`, which keeps the account online for least-recently-used rotation.
  The answer is `eligible`, `retryAfterSeconds` (30–120, the earliest moment the blocking
  condition can clear: cooldown end, minimum interval, quota window sliding, platform interval,
  task retry backoff or lease expiry; 120 when only an operator or tomorrow's schedule can help)
  and, for `needs_verification`, `expired` or `disabled`, a `stopReason`. The lease stays
  authoritative: an eligible answer may still be followed by an empty lease after a race.
- **Probe-failed accounts leave the fairness queue**: an account "online" means seen within the
  last five minutes. An account whose probe keeps failing still polls eligibility and would
  otherwise block younger healthy accounts. Every failure reported with `phase: "probe"` sets
  `lastProbeFailedAt`; for `probeFailureOfflineMinutes` (platform setting, default 15) after
  that, other accounts do not wait for it, unless it has leased a task since the failure. The
  rule only affects how others see it: the failing account may still lease when it is otherwise
  eligible, and its own probe must pass first anyway. `profiles list` shows
  `lastProbeFailedAt` and `excludedFromFairness`.
- **Compatibility**: a tenant with no registered profile for a platform leases exactly as
  before. Once any profile (including a disabled one) exists for that tenant/region/platform,
  a lease without `sessionProfileId` receives no tasks, so a pooled platform never falls back
  to unmetered dispatch.

## Register accounts and platform gates

Operator commands use the control-plane database (`DATABASE_URL`) and are tenant-scoped through
`app.tenant_id`. Pass `--tenant <uuid>` or set `AIVC_TENANT_ID`. Output is one JSON line;
errors are one JSON line on stderr with a code only (exit 2 for usage, 1 otherwise).

```bash
export AIVC_TENANT_ID=<tenant UUID>
pnpm db:profiles add --platform doubao --region cn --label doubao-a
pnpm db:profiles add --platform doubao --region cn --label doubao-b --hourly 3 --daily 20 --min-interval 900
pnpm db:profiles list
pnpm db:profiles list --platform doubao --region cn
pnpm db:profiles set-status <sessionProfileId> disabled
pnpm db:profiles set-status <sessionProfileId> active

pnpm db:platforms set --platform doubao --region cn --min-interval 180 --daily-cap 60
pnpm db:platforms set --platform doubao --region cn --breaker-threshold 2 --breaker-window 60
pnpm db:platforms list
pnpm db:platforms resume --platform doubao --region cn
```

- `--label` is an operator alias (1–128 characters, unique per platform). Use something like
  `doubao-a`, never a phone number, e-mail address or account name.
- `profiles add` prints the `sessionProfileId`; record it in the local pool file.
- `set-status` accepts only `active` and `disabled`. `active` also clears cooldown and the
  challenge streak; use it only after confirming the account is healthy (prefer `probe`, below).
- `platforms set` options: `--min-interval` (s), `--daily-cap`, `--breaker-threshold`
  (distinct challenged profiles), `--breaker-window` (min), `--retry-backoff` (s),
  `--max-failures`, `--cooldown-base` and `--cooldown-max` (s, `rate_limited` cooldown),
  `--probe-offline` (min, 1–1440: how long a probe failure keeps an account out of the
  fairness queue).
  Omitted options keep their current value. The region must be the platform's region.
- Every operator command writes a health event.

`profiles list` reports per profile: `status`, `cooldownUntil`, `hourlyQuota`, `dailyQuota`,
`minIntervalSeconds`, `consecutiveChallenges`, `lastLeasedAt`, `lastSeenAt`, `leasedLastHour`,
`leasedLast24h`, `acceptedLast24h`, `challengesLast24h`, `challengeRateLast24h`,
`lastChallengeAt`, `lastProbeFailedAt` and `excludedFromFairness`. A challenge encounter is each challenge wait start plus each
`challenge_required` report that did not end such a wait (a timed-out wait counts once; a
probe-time challenge counts on its own). The rate is challenges per leased task in the last
24 hours and is `null`, not `0`, when nothing was leased. Counts come from attempt and
health-event evidence, so they cannot drift from what happened.

## Login and probe per account

Login and probe work exactly as in the platform runbooks, once per account and with that
account's own profile directory:

```bash
COLLECTOR_PROFILE_DIRECTORY='/absolute/path/aivc/pool/doubao-a' pnpm collector:login
```

Probe with the account identity so a `ready` result is reported to the control plane:

```bash
COLLECTOR_PROFILE_DIRECTORY='/absolute/path/aivc/pool/doubao-a' \
COLLECTOR_SESSION_PROFILE_ID=<doubao-a profile UUID> \
COLLECTOR_API_URL=<pool-capable control plane URL> \
COLLECTOR_SERVICE_TOKEN=<regional token> \
COLLECTOR_TENANT_ID=<tenant UUID> \
COLLECTOR_NODE_ID=<node id> \
COLLECTOR_REGION=cn \
pnpm collector:probe
```

Load these from an ignored `0600` env file or a secret manager rather than typing tokens on the
command line. With `COLLECTOR_SESSION_PROFILE_ID` set, probe refuses to run unless all of the
API, token, tenant, node and region values are present. A `ready` probe sends `verified`: the
profile returns to `active` (a `disabled` profile stays disabled) and its challenge streak is
cleared. A non-ready operator probe exits 2 and does not change the profile. If the page is
ready but the `verified` report fails, probe exits 1 and the profile is not restored; retry it.
(A probe inside a collector run is different: a non-ready result there is reported as a
`probe` failure, for example `challenge_required` moves the profile to `needs_verification`.)

## Collector identity (`COLLECTOR_SESSION_PROFILE_ID`)

`COLLECTOR_SESSION_PROFILE_ID` is optional in real mode. It is required only when collecting
against this repository's control plane with registered profiles, and the pool supervisor sets
it per entry. Without it:

- no `sessionProfileId` is sent on lease (so a pooled platform returns no tasks);
- no failure or session events are reported; a failed task's lease simply expires;
- the challenge wait and local notifications still work.

Existing single-account runs against control planes without an account pool (for example
Jogeoai) therefore keep working unchanged.

### Resident collectors (with a profile)

With `COLLECTOR_SESSION_PROFILE_ID` in real mode the collector is resident. It opens the browser
once and repeats until shutdown:

1. eligibility check (no page access); if not eligible, sleep `retryAfterSeconds` and check
   again; with a `stopReason` exit `3` without touching the page;
2. probe: if the page is already on the platform origin and every reviewed marker is visible it
   is checked in place, without reloading (a visible challenge is reported in place too); a
   fresh page, another origin or a missing marker loads the entry page as before. Without a
   profile (and in the operator `probe` command) every probe still loads the entry page;
3. lease, collect and submit; then wait `COLLECTOR_MIN_REQUEST_INTERVAL_MS`.

An empty queue, an empty lease after an eligible answer, or `COLLECTOR_MAX_TASKS_PER_RUN` does
not end the process. `COLLECTOR_RUN_ONCE=true` still means one cycle (one eligibility check and
at most one lease). Errors still end the process with the exit codes below.

### Attempt ledger

The local attempt ledger (in the profile directory, append + fsync before the submit click)
behaves differently with and without a profile:

- **With `COLLECTOR_SESSION_PROFILE_ID`**: no lifetime cap; throughput is governed by the
  control plane's quotas. The approved-question list is still enforced. The ledger dedupes
  only on task ID and lease version: the same question may be asked again on later days, and a
  task re-leased with a new lease version after a crash may run again (at worst one extra
  conversation, never a second sample: the control plane accepts one sample per task). New
  lines record `leaseVersion`; older lines without it are still read and never match.
- **Without it** (single-account runs): unchanged. At most `COLLECTOR_MAX_TASKS_PER_RUN`
  attempts per API/tenant/project binding over the ledger's lifetime, one attempt per task and
  per question, and `COLLECTOR_RETRY_TASK_ID` for an approved retry. Lines written in profile
  mode count toward this cap if the same directory is later used without a profile.

## Pool supervisor

`pnpm collector:pool` runs one collector child (`apps/collector/src/main.ts`, the same entry as
`pnpm collector:run`) per entry of a local JSON file. Set the shared real-mode environment
(API URL, regional token, tenant, expected project, region, egress, page contract paths,
approved question file, spool key, timings, `COLLECTOR_NODE_ID`) exactly as for a single run,
then add:

```dotenv
COLLECTOR_POOL_FILE=/absolute/path/aivc/pool/pool-doubao.json
COLLECTOR_POOL_IDLE_MS=60000
COLLECTOR_POOL_STAGGER_MS=15000
```

```json
[
  {
    "sessionProfileId": "<doubao-a profile UUID>",
    "platform": "doubao",
    "label": "doubao-a",
    "profileDirectory": "/absolute/path/aivc/pool/doubao-a",
    "spoolDirectory": "/absolute/path/aivc/pool/spool-doubao-a"
  },
  {
    "sessionProfileId": "<doubao-b profile UUID>",
    "platform": "doubao",
    "label": "doubao-b",
    "profileDirectory": "/absolute/path/aivc/pool/doubao-b",
    "spoolDirectory": "/absolute/path/aivc/pool/spool-doubao-b",
    "cdpUrl": "http://127.0.0.1:9223"
  }
]
```

File rules (validated before any child starts; values are never echoed in errors):

- 1–50 entries, absolute regular file, not a symlink; unknown keys are rejected.
- `sessionProfileId`, `platform`, `profileDirectory` and `spoolDirectory` are required.
  `cdpUrl` (local only) is optional; with it, Chrome is user-managed and `profileDirectory`
  still holds that account's attempt ledger, so it is always required.
- Profile IDs, directories (profile and spool share one namespace), CDP endpoints and labels
  must be unique. A spool is bound to one node ID and locked by one process, so every account
  needs its own empty spool directory.
- `label` is an optional log alias (`[A-Za-z0-9_.-]`, up to 64 characters).

Per child, the supervisor overrides `COLLECTOR_SESSION_PROFILE_ID`,
`COLLECTOR_PROFILE_DIRECTORY`, `COLLECTOR_SPOOL_DIRECTORY`, `COLLECTOR_SUPPORTED_PLATFORMS`,
`COLLECTOR_BROWSER_CDP_URL` (removed unless the entry sets `cdpUrl`) and `COLLECTOR_NODE_ID`
(`<COLLECTOR_NODE_ID>-<first 8 characters of the profile ID>`, so keep the base at most 119
characters). It removes `COLLECTOR_RETRY_TASK_ID`: a targeted retry is an attended single run,
never something every child repeats. It also removes control-plane-only secrets a shell may have
sourced from the repository `.env`: `DATABASE_URL`, every `POSTGRES_*` variable,
`COLLECTOR_CN_SERVICE_TOKEN` and `COLLECTOR_OVERSEAS_SERVICE_TOKEN` (a collector receives only
its own regional `COLLECTOR_SERVICE_TOKEN`). Everything else is inherited. One pool serves one
tenant/project binding; use a separate pool file, env file and spool directories per binding.

Run children headed (`COLLECTOR_BROWSER_HEADLESS=false`) so the operator can see and resolve a
challenge in each account's window. Children of the same platform run concurrently; the
control plane's platform gate serializes dispatch. Entries start `COLLECTOR_POOL_STAGGER_MS`
apart (default 15 s; entry _i_ starts _i_ × the stagger after the pool) so accounts do not load
their pages at the same moment. Every child is resident: it polls eligibility at most every
30–120 s, which keeps it inside the five-minute online window, and loads the platform page only
when the control plane would grant it work. `COLLECTOR_POOL_IDLE_MS` is only a fallback restart
delay, since a resident child exits `0` only on shutdown.

### Exit codes and restart policy

| Child exit | Meaning                                                                              | Supervisor                                      |
| ---------- | ------------------------------------------------------------------------------------ | ----------------------------------------------- |
| `0`        | Finished: shutdown or `COLLECTOR_RUN_ONCE` (resident children do not stop when idle) | Restart after `COLLECTOR_POOL_IDLE_MS`          |
| `1`        | Any other failure (config, browser, control plane, page contract, spool, ...)        | Restart with backoff 30 s, doubling, max 10 min |
| `3`        | The account needs a human: `challenge_required`, `login_required`, or a `stopReason` | Never restart; log and notify the operator      |

A signal death counts as `1`. A success resets the backoff, and so does a child that stayed up
longer than the 10-minute maximum backoff before failing (a new incident, not an old streak). The supervisor exits `3` when every
entry has stopped for the operator, and `0` after a shutdown. A `stopReason` from the
eligibility check (`needs_verification`, `expired`, `disabled`) is logged as
`session_profile_stopped` and also exits `3`; nothing new is reported for the account.

Ctrl+C or `SIGTERM` reaches only the supervisor (children run in their own process group); it
forwards exactly one signal to each running child and waits for them to close their browsers,
release locks and exit. Do not send a second signal to a child: the collector handles the first
signal gracefully, but a second one terminates it without cleanup. If a child was killed hard,
follow the platform runbook's lock-recovery steps before restarting.

## Challenge wait and notifications

When a challenge becomes visible before fill, before submit or while the answer is generating,
the collector does not fail immediately. It:

1. logs `challenge_wait_started`, reports it to the control plane (with a profile) and notifies
   the operator;
2. keeps heartbeating the lease and extends the collection deadline by the wait budget, so
   `COLLECTOR_COLLECTION_TIMEOUT_MS` does not interrupt the wait;
3. only checks whether the challenge is still visible; it never reads or interacts with it.

`COLLECTOR_CHALLENGE_WAIT_MS` is the total wait per task (default 600 000 = 10 minutes, maximum
1 800 000, `0` disables waiting). If the challenge disappears in time, `challenge_cleared` is
recorded, the pre-send checks (empty or matching composer, conversation still active) run
again, and collection continues; after submit, answer polling continues. If the budget runs
out, the collector reports `challenge_required` (phase `pre_send` or `post_send`), the lease is
released immediately, the account becomes `needs_verification`, and the child exits `3`. Before
submit nothing was sent; after submit the attempt is already counted in the attempt ledger.

Notifications go to the structured log (`operator_action_required`), a macOS notification with
fixed text, and optionally `COLLECTOR_NOTIFY_COMMAND`: an absolute executable run without a shell,
with an allowlisted environment, receiving the notice JSON on stdin (hook for WeCom/Feishu
later). Notices contain only identifiers and enum values (platform, reason, phase, profile and
task IDs), never question text, answers or page content. A failing notifier never fails or
blocks collection. The supervisor sends `pool_entry_stopped` when it stops an entry.

**Do not type in the collector tab during a run.** Solve challenges only when the collector is
waiting for one; an operator message typed into the collection window preceded a full-page
verification during an earlier run (`real-doubao-smoke.md`, 2026-10-24).

## Failure effects and retries

The collector reports classified failures to `POST /internal/collector/failures`; the lease is
released at once. If the report itself fails, the lease expires as before.

| Failure              | Account                                                  | Task                                  |
| -------------------- | -------------------------------------------------------- | ------------------------------------- |
| `challenge_required` | `needs_verification`, challenge streak + 1               | Requeued after the retry backoff      |
| `rate_limited`       | `cooling`, 30 min doubling per consecutive limit, max 8h | Requeued after the retry backoff      |
| `login_required`     | `expired`                                                | Requeued after the retry backoff      |
| other codes          | unchanged                                                | Requeued after the retry backoff      |
| any, 3rd failure     | as above                                                 | `failed` (no further automatic retry) |

No account can lease a requeued task before its backoff ends. A report for a stale lease
records only the account effect.

**Breaker**: when at least `breaker-threshold` distinct accounts of a platform report
`challenge_required` within `breaker-window` minutes, the platform stops dispatching to every
account (`pausedAt` in `pnpm db:platforms list`). It never resumes automatically.

## Operator recovery

1. A notification names the platform, reason and profile ID (look up the label with
   `pnpm db:profiles list`).
2. While the collector is still waiting: solve the challenge by hand in that account's visible
   profile window. The collector continues on its own.
3. If the wait already timed out (child exited `3`): if needed, run `pnpm collector:login` for
   that profile directory and complete verification or login by hand, then close it. Run
   `pnpm collector:probe` with that account's `COLLECTOR_SESSION_PROFILE_ID`. A `ready` probe
   reports `verified` and the account returns to `active`.
4. If the breaker tripped: check every challenged account, recover them as above, review the
   rate (`profiles list`) and consider lowering quotas or the platform daily cap, then run
   `pnpm db:platforms resume --platform <code> --region <region>`.
5. Restart the pool so stopped entries run again. Disable an account you cannot recover with
   `pnpm db:profiles set-status <id> disabled`.

Never reset or delete a profile directory, spool or attempt ledger to force progress.

## Defaults

| Setting                               | Default                               | Where                                                |
| ------------------------------------- | ------------------------------------- | ---------------------------------------------------- |
| Account hourly / daily quota          | 4 / 30                                | `profiles add --hourly --daily`                      |
| Account minimum interval              | 600 s                                 | `profiles add --min-interval`                        |
| Platform minimum dispatch interval    | 120 s                                 | `platforms set --min-interval`                       |
| Platform daily cap                    | 120                                   | `platforms set --daily-cap`                          |
| Challenge wait                        | 10 min                                | `COLLECTOR_CHALLENGE_WAIT_MS`                        |
| `rate_limited` cooldown               | 30 min, doubling, max 8 h             | `platforms set --cooldown-base --cooldown-max`       |
| Task retry backoff / maximum failures | 30 min / 3                            | `platforms set --retry-backoff --max-failures`       |
| Breaker                               | ≥ 2 accounts challenged within 60 min | `platforms set --breaker-threshold --breaker-window` |
| Pool idle restart (fallback)          | 60 s                                  | `COLLECTOR_POOL_IDLE_MS`                             |
| Pool start-up stagger                 | 15 s between entries                  | `COLLECTOR_POOL_STAGGER_MS`                          |
| Eligibility retry                     | 30–120 s, from the control plane      | fixed bounds                                         |
| Pool failure backoff                  | 30 s, doubling, max 10 min            | fixed                                                |

These are starting values, not known platform thresholds. Tune them from `profiles list`
(challenge rate, accepted counts) and keep them conservative; raising throughput means more
authorized accounts within the platform cap, never faster accounts.
