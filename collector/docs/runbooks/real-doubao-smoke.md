# Real Doubao smoke (native, explicit opt-in)

## Status and boundary

Implementation, synthetic DOM tests and one bounded native four-query smoke exist. On 2026-09-07 the latest four real responses were read back through the API with body lengths `659/1579/1663/553` and visible-source counts `9/22/24/10`. The first controlled `ulinktek` retry resumed only after the user manually completed a visible challenge; automation neither solved nor bypassed it. Three earlier prompt-echo records remain immutable and explicitly suspect; three approved `sampleIndex=2` recovery queries produced the corrected latest samples without resetting the original attempt journal. This proves the reviewed native smoke only, not unattended or multi-account production readiness. The user approved default-conversation sampling because no independent active search toggle was found, so network use remains unverified per answer. No authenticated selectors or credentials are shipped. Compliant CN egress, an authorized test account and a freshly operator-reviewed page contract remain prerequisites. Never bypass CAPTCHA, access controls, region restrictions or account limits.

Use the API repository's dedicated smoke database/API at `http://127.0.0.1:4314`, not this repository's Mock API 4311 or the development fixture queue 4312. Keep the matrix disabled until `collector:probe` returns `ready`. The lease protocol cannot filter project; the client checks project ID, tenant, region, `doubao.consumer_web`, `zh-CN` and these four exact texts before collection:

- 管道内窥镜
- 优联科技
- ulinktek
- 下水道内窥镜制造商

These four are the built-in fallback. Since 2026-10-21 an operator can instead set `COLLECTOR_APPROVED_QUESTIONS_FILE` (see the 2026-10-21 second update below); the exact-text rule is unchanged.

This slice writes raw answers and actual visible citation URLs. It does not run brand parsing, citation normalization or metric rollups. GEO prompt citation counts and source aggregation can remain empty/zero; use audited answer detail for legacy submitted citations. A missing brand mention is a valid observation.

## Install and profile-only login

Use Node 24 and pnpm 10.34.5:

```bash
pnpm install --frozen-lockfile
pnpm exec playwright install chromium
```

The current Compose image lacks browser binaries/system libraries and remains Mock-only.

Choose a dedicated absolute directory containing an `aivc/` component, outside all Git checkouts and personal browser profiles, for example `/Users/<operator>/Library/Application Support/aivc/real-smoke/doubao-cn`. Profile/spool leaves must be owner-owned `0700`, without symlink ancestors. New directories are restricted automatically; existing permissive directories are refused. Use host disk/volume encryption: persistent Chromium profiles are **not application-encrypted**. Do not export cookies or reuse a daily browser profile.

Login requires no API token or database config:

```bash
COLLECTOR_PROFILE_DIRECTORY='/absolute/path/aivc/real-smoke/doubao-cn' pnpm collector:login
```

Complete authorized login/verification manually. Close the browser or press Ctrl+C when finished. The CLI never accepts passwords, OTPs or cookies as arguments. A Profile lock refuses concurrent login/collection. After a crash, verify the recorded PID and associated Chromium processes have exited before recovering the **specific** `.collector.lock`; never remove an active lock.

Browser traffic explicitly uses direct networking (`--no-proxy-server`); inherited proxy and secret environment variables are removed from Chromium. `COLLECTOR_EGRESS_COUNTRY=CN` is a configuration assertion, **not an IP geolocation measurement**. Independently verify compliant CN egress. This slice does not add a proxy or geolocation service.

## Review the live page contract

Probe navigates/reads the landing page without submitting a prompt:

```bash
COLLECTOR_PROFILE_DIRECTORY='/absolute/path/aivc/real-smoke/doubao-cn' pnpm collector:probe
```

Non-ready states include `region_restricted`, `login_required`, `challenge_required`, `rate_limited`, `page_contract_unverified` and `page_contract_changed`. Non-ready probe exits 2; startup/config/browser errors exit 1. Account access alone does not prove selectors or the selected conversation mode.

After access is resolved, an operator/developer must inspect the authorized live page and record an external JSON file at `DOUBAO_PAGE_CONTRACT_PATH`. The schema in `apps/collector/src/adapters/doubao.ts` requires:

- `version: 2`, `origin: "https://www.doubao.com"`
- `samplingMode: "default_conversation"`: explicit approved mode, not evidence of network use
- `reviewedAt`: UTC ISO timestamp, not in the future and at most seven days old
- `verifiedOnAuthorizedPage: true`: explicit operator assertion, never inferred from fixture tests
- `selectors`: all fields below, with no wildcard/page-wide answer fallback

| Selector                                                                | Required meaning                                                                                            |
| ----------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------- |
| `authenticated`                                                         | One visible logged-in account marker                                                                        |
| `newConversation`                                                       | One visible control that starts an empty conversation                                                       |
| `composer`                                                              | One editable prompt input, empty after new conversation                                                     |
| `conversationActive`                                                    | One visible marker identifying the selected default conversation mode, not a claim of network search        |
| `submit`                                                                | One visible submit control                                                                                  |
| `assistantMessage`                                                      | Exactly the new assistant message, not the prompt or prior conversation                                     |
| `answerBody`                                                            | Rendered answer body relative to `assistantMessage`, excluding navigation/follow-ups                        |
| `generating`                                                            | Visible page-level generation-in-progress indicator                                                         |
| `completed`                                                             | Positive completion indicator relative to `assistantMessage`                                                |
| `citations`                                                             | Answer-associated citation anchors relative to `assistantMessage`                                           |
| `sourcesToggle` (optional)                                              | One source-panel expansion control relative to `assistantMessage`, with an observed `参考 N 篇资料` caption |
| `answerIgnore` (optional)                                               | Elements inside `answerBody` left out of the Markdown answer (media carousels, inline source chips)         |
| `loginRequired`, `regionRestricted`, `challengeRequired`, `rateLimited` | Visible platform state panels, not matching ordinary answer prose                                           |

Do not copy selectors from `doubao-synthetic.html`: its invented local test DOM is not a production manifest. Do not choose permanently visible elements merely to satisfy a gate. The 2026-09-07 smoke used an authorized, externally stored reviewed manifest; DOM changes or a review older than seven days require reinspection. Stable text, positive completion, and absence of generation are all required.

Do not identify assistant messages by Markdown-renderer presence alone: outgoing
user messages can have the same renderer and completed marker. The reviewed local
manifest now narrows the root to the assistant grid rather than all message IDs.
Exact prompt echoes are rejected both during extraction and at real submission.
Already accepted erroneous evidence is immutable: preserve it, report suspect
sample IDs, pause collection and obtain approval before extra live queries.

When the reviewed page presents collapsed sources, configure `sourcesToggle` and
scope `citations` to that message's source links. After positive completion and
stable answer text, the adapter clicks the single visible toggle once and waits
up to five seconds (within the collection deadline) for stable public URLs whose
deduplicated count equals the caption's positive integer count (1–100). Multiple
toggles, malformed/ambiguous captions, incomplete/loading panels or a count
mismatch fail with `page_contract_changed`; they never yield a false empty list.
Keep `answerBody` scoped to the answer renderer, excluding source summaries,
captions and follow-ups. An absent/hidden optional toggle preserves existing
inline-citation or genuinely empty-source behavior; it does not claim networking.

Probe allows up to five seconds (bounded by collection timeout) for reviewed
account/composer/conversation markers to hydrate after DOMContentLoaded. A visible
`#captcha_container iframe` is always `challenge_required` in probe and collection,
even when main-page text does not mention verification. Only iframe visibility is
inspected; no frame document, CAPTCHA content or solution is read. Stop and ask
the authorized user to complete verification manually before any retry.

Version 1 manifests and `searchActive` fields are rejected, not automatically migrated. Review the actual default-conversation state and write a version 2 manifest explicitly; do not rename an arbitrary search toggle to satisfy the new marker. Deep Research is out of scope. The first approved keyword may verify completion/citation selectors under its owned lease, but must be persisted once and never resent solely for selector discovery. Login, empty-new-chat, completion, stability, citations, abort and attempt-limit guards remain in force.

With the reviewed file configured, probe must report `ready` before scheduling. Probe cannot exercise answer completion; the first authorized query remains a manual acceptance gate.

## Runtime configuration

Place the following in a `0600` ignored `.env.real-smoke` file or inject through a secret manager. `.env.*` is excluded from Git and Docker contexts. Do not use shell tracing or print environment contents. Unset `DEBUG`, `PWDEBUG` and `PLAYWRIGHT_TRACE_DIR`: session commands reject these to prevent verbose prompt/session diagnostics.

```dotenv
COLLECTOR_MODE=real
COLLECTOR_API_URL=http://127.0.0.1:4314
COLLECTOR_SERVICE_TOKEN=<dedicated API CN service token>
COLLECTOR_TENANT_ID=<smoke tenant UUID>
COLLECTOR_EXPECTED_PROJECT_ID=<approved project UUID>
COLLECTOR_NODE_ID=doubao-cn-real-smoke
COLLECTOR_REGION=cn
COLLECTOR_EGRESS_COUNTRY=CN
COLLECTOR_SUPPORTED_PLATFORMS=doubao
COLLECTOR_BUILD_REVISION=<reviewed revision>-real-doubao
COLLECTOR_CAPACITY=1
COLLECTOR_LEASE_SECONDS=300
COLLECTOR_HEARTBEAT_INTERVAL_MS=60000
COLLECTOR_HTTP_TIMEOUT_MS=10000
COLLECTOR_COLLECTION_TIMEOUT_MS=180000
COLLECTOR_MIN_REQUEST_INTERVAL_MS=15000
COLLECTOR_MAX_TASKS_PER_RUN=4
COLLECTOR_RUN_ONCE=false
COLLECTOR_PROFILE_DIRECTORY=/absolute/path/aivc/real-smoke/doubao-cn
COLLECTOR_BROWSER_HEADLESS=false
DOUBAO_PAGE_CONTRACT_PATH=/absolute/path/aivc/real-smoke/doubao-page-contract.json
COLLECTOR_SPOOL_DIRECTORY=/absolute/path/aivc/real-smoke/spool-doubao-cn
COLLECTOR_SPOOL_KEY=<unique random 32-byte base64 key>
```

Quote dotenv paths containing spaces. `COLLECTOR_RUN_ONCE=true` means one lease cycle (one task at capacity one), **not** all four queries. Use `false` for the bounded four-task run; real mode exits at an empty queue, four processed tasks, or any unhealthy state/error. Mock retains polling/retry behavior.

Load the file with Node 24 without displaying it:

```bash
node --env-file=.env.real-smoke --import tsx apps/collector/src/login.ts probe
node --env-file=.env.real-smoke --import tsx apps/collector/src/main.ts
```

Or inject environment and use `pnpm collector:probe` / `pnpm collector:run`. CLI commands do not automatically source dotenv files. Collector does not require/use database credentials.

## Failures and retries

- HTTP calls have a 10-second default timeout, disallow redirects and decode bounded responses. Raw HTTP/Playwright errors never enter logs.
- Immediate heartbeat confirms ownership before collection, then renews throughout work/submission. Stale ownership, local lease deadline, collection timeout or shutdown aborts the browser and suppresses publication.
- Completed results are AES-256-GCM spooled **before** their first POST. The immutable key hashes task, lease version, answer and sorted unique citations, not completion time.
- Replay precedes new leases and reuses the identical payload. Accepted/duplicate/stale removes its encrypted file only after the API response. Stale means audited late evidence, not a sample. Ambiguous failure retains ciphertext for restart.
- Real spool is bound to mode, API, tenant, project and node; old/unbound nonempty Mock spools are rejected. Use distinct profile/spool directories and preserve the same key for replay.
- A durable Profile journal reserves each approved task/question once before collection and survives restarts. The shipped automatic path caps the original campaign at four attempts, conservatively including interruptions before submission. A new lease version does not authorize another search. The three `sampleIndex=2` prompt-echo recoveries were explicit operator-driven exceptions tied to exact approved tasks and did not reset the original journal; this is not a general automatic retry facility. Never reset/replace a profile or increase limits automatically.
- Mismatched tasks, unsupported adapters or unhealthy sessions stop the process. The frozen protocol has no failure-result API; no placeholder/Mock result is submitted and the lease expires for recovery.
- Corrupt ciphertext blocks replay. Preserve it and investigate; do not delete evidence to force progress. Never copy profiles, bodies or tokens into Git, task notes or logs.

## Verification and read-back

```bash
pnpm exec vitest run apps/collector packages/config
pnpm lint
pnpm typecheck
pnpm test
pnpm secrets:check
pnpm compose:config
```

Tests intercept page requests with synthetic HTML; CI uses no platform account or external search. PostgreSQL integration is separate and requires an explicitly authorized disposable database, never the real smoke database.

For a clean reproduction, schedule exactly four tasks and verify four completed tasks/unique latest samples. If correcting retained immutable evidence, keep historical suspect samples separate and require explicit approval for exact new task/sample slots. Read:

1. `GET /api/v1/brand-projects/{projectId}/geo/prompts?from={date}&to={date}&platformCode=doubao`
2. `GET /api/v1/brand-projects/{projectId}/geo/prompts/{questionId}?from={date}&to={date}&platformCode=doubao`
3. `GET /api/v1/brand-projects/{projectId}/geo/answers/{sampleId}?includeBody=true`

Record safe IDs, dispositions, citation counts and whether `优联科技`/`ulinktek` appeared. Do not rewrite missing mentions or fabricate citations. API owns audited reads and formal lineage. Stop Collector and disable future scheduling for rollback; retain accepted answers and audit history.

## 2026-10-21 update: first live closure against Jogeoai

- Control plane: Jogeoai API `http://127.0.0.1:5260` (`/internal/collector/*`), config in
  `~/Library/Application Support/aivc/real-smoke/.env.real-jogeoai` with its own spool dir.
  Run `e5e27060-77a1-4271-9543-5e1ae40e7938`: 4/4 accepted.
- Live page contract re-reviewed: `authenticated`, `composer`, `submit`, `assistantMessage`,
  `answerBody`, `generating`, `completed` now use `data-testid` hooks
  (`chat_header_avatar_button`, `chat_input_input`, `chat_input_send_button`,
  `receive_message`, `message_text_content[data-streaming]`); `sourcesToggle` is narrowed to the
  `参考 N 篇资料` caption (`[data-copy-ignore]:has-text("篇资料")`), and `challengeRequired`
  also matches the image-selection CAPTCHA modal text.
- `DoubaoAdapter` waits for the answer body (multiple-body mode) and reads sources through
  `readDoubaoSources`: Doubao renders at most 12 inline source links and has no expander, so a
  list shorter than the caption count is accepted; more than the caption, unparsable captions
  and zero links still fail closed. Toggle is clicked once.
- The first automated send triggered an image CAPTCHA; the operator completed it manually in
  the profile window (`collector:login`) before retries. The attempt ledger still caps one try
  per task; retries used `COLLECTOR_MAX_TASKS_PER_RUN=1 COLLECTOR_RETRY_TASK_ID=<task>` per run.
- Observed: logged-in answers carry personalization ("你之前关注…"), and with a non-CN egress
  the sources skew to English sites. Verify CN egress before treating results as consumer-side
  evidence.

## 2026-10-21 second update: operator-approved list, titles and keywords (优联科技, 8/8)

- Approved questions: optional `COLLECTOR_APPROVED_QUESTIONS_FILE` (absolute path) replaces the
  built-in four in real mode. JSON with exactly `version` (1), `tenantId`, `projectId`,
  `approvedAt`, `questions` (1–50 unique strings, 1–500 chars, compared by exact equality). The
  file must be a regular non-symlink file owned by the operator with mode 0600; it is validated at
  startup before the spool lock, browser or any lease, and its tenant/project must equal
  `COLLECTOR_TENANT_ID` / `COLLECTOR_EXPECTED_PROJECT_ID`. Generate it from the control plane's
  current prompt text and compare with the run's `question_snapshot` before starting.
- `COLLECTOR_MAX_TASKS_PER_RUN` now accepts 1–10 (default 4); the ledger capacity is checked
  before leasing. The ledger is still one attempt per task and per question per binding:
  re-collecting the same questions later needs `COLLECTOR_RETRY_TASK_ID` per task or a new binding.
- One binding per tenant/project: use a separate env file and an empty spool directory per tenant
  (a spool is bound to its first tenant). The `doubao-cn` profile is shared (single-process lock).
- Page contract: optional `searchQueries` selector. Live value (reviewed 2026-10-21T07:00:42Z):
  `[data-plugin-identifier*="search_query_result_block"] div:text-matches("^“[^”]+”")`, read inside
  the assistant message after the sources toggle is clicked. Keywords are sent only when their
  count equals N in `搜索 N 个关键词`; titles are the source link text without the leading number.
  Both are best effort: unreadable or unsafe values are dropped, never failing the answer.
- Result contract: optional `citationTitles` (aligned with `citations`, items may be null) and
  `searchQueries`; older spooled payloads without them still replay.
- Live run: Jogeoai Run `ddc04f7e-14da-47dd-af10-7e2bca91888a`, env
  `.env.real-jogeoai-ulinktek`, spool `spool-doubao-ulinktek`; 8/8 accepted in one pass (~8 min,
  no CAPTCHA, no retry), 181 citations all titled, 4–8 keywords per answer. `www.doubao.com` was
  confirmed DIRECT via the Clash connection rule (`GeoSite cn`), not via DNS (fake-ip always
  answers 198.18.x). Source counts were 14–32 per answer, so the earlier "at most 12" observation
  does not hold for every answer.

## 2026-10-21 third update: answer Markdown (code)

- `DoubaoAdapter` returns `answerText` as Markdown: `answerToMarkdown`
  (`apps/collector/src/adapters/answer-markdown.ts`) runs in the page on every visible
  `answerBody` and the parts are joined by a blank line. Completion, stability, source and
  prompt-echo checks still use the plain `innerText`; the prompt-echo check also applies to the
  Markdown. Empty Markdown or any conversion error falls back to the plain `innerText` answer.
- Conversion: h1–h6, paragraphs, nested ul/ol (`start`/`value`), blockquote, GFM table, fenced
  code, `**`/`*`, inline code, http(s) links, `<br>` and empty `<div></div>` as hard breaks. Media,
  script/style, buttons, `display: none` and `answerIgnore` matches are dropped; an invalid
  `answerIgnore` disables ignoring. Text escapes ``\ * _ ~ ` [ ] |``, entity-like `&` and
  line-leading `# > - + < =` / `1.` / `1)`. Where CommonMark could not open/close `**` next to
  CJK punctuation, the edge punctuation moves outside the delimiters (`**推索式（DN30**）和`).
- Page contract: optional `answerIgnore`. Live value, added to the reviewed contract on
  2026-10-21T08:52:51Z after read-only checks on collected conversations:
  `[data-testid="carousel-item"], [data-testid="mdbox_image"], [data-testid="video-box-bg-image"], span[class*="spacing-"] + span`.
- Read-only scripts (e.g. a backfill) use `readAnswerMarkdown(message, contract)` (Markdown or
  `undefined`, never throws) and `readAnswerPlainText(message, contract)` (the exact pre-Markdown
  `innerText` join, for comparing with stored plain answers), exported from `answer-markdown.ts`
  and re-exported by `doubao.ts`. Load them with `node --import tsx`; `answerToMarkdown` keeps its
  helpers as object methods so tsx's `keepNames` cannot inject `__name` into the page (tested).
- 2026-10-21 backfill: the 8 优联 answers (Jogeoai Run `ddc04f7e-…`) were re-read from the account's
  existing conversations with `backfill-doubao-markdown.mjs` (aivc dir, no send). Conversations are
  matched by the `send_message` bubble text with all whitespace removed (Doubao inserts spaces
  between CJK and Latin text when displaying the question). 6/8 plain texts matched exactly; 2 differed
  only by a lazily rendered carousel image caption (5–12 chars) and were accepted with an explicit
  single-segment tolerance. Jogeoai then swapped the stored answers and re-ran a scoped analysis job.

## 2026-10-24 update: prefixed question readback in paragraph editors (code)

- The live composer renders one `<p>` per line and an empty `<p>` for a blank line. Selected text
  separates paragraphs by a blank line, so the shared-prefix question (prefix, blank line, question)
  read back with four newlines and every send stopped with `page_contract_changed` before submit.
- `readFilledComposer` (`consumer-web.ts`) now reads such composers as the paragraphs' text joined
  by single newlines (zero-width `\uFEFF` removed); other composers keep the selected-text reading.
  Synthetic tests cover a question with a blank line and a paragraph editor that drops the blank
  line (no submit click); since 2026-11-16 no prefix is sent, so only a question that itself has a
  blank line takes this path. Verified read-only on the live page (fill, compare, clear; nothing sent).
- Live run 2026-10-24 (Jogeoai Run `dce8a3cd-…`): 10/10 accepted. Pre-send `challenge_required`
  stops left no conversation in history and were resumed once each with `COLLECTOR_RETRY_TASK_ID`
  after the operator cleared the verification in the profile window. Do not type into the
  collector window during a run; an operator message there preceded a full-page verification.
