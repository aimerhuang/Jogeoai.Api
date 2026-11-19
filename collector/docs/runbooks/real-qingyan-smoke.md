# Qingyan consumer Web collection

Use stable `zhipu_qingyan` / `zhipu_qingyan.consumer_web`. The API task
owns live acceptance, queue isolation and the exact approved question list.
The original four-question smoke is described separately below. Do not recreate
the historical disposable Doubao database. Offline fixtures do not prove live success.

Use Node.js 24 and an external environment file with the approved API URL,
tenant/project IDs, CN service token, capacity 1 and the approved task bound. Give
Qingyan a dedicated node, external owner-only profile and encrypted spool.

```bash
export PATH=/opt/homebrew/opt/node@24/bin:$PATH
export COLLECTOR_SUPPORTED_PLATFORMS=zhipu_qingyan
export COLLECTOR_PROFILE_DIRECTORY=/absolute/external/aivc/qingyan-cn
pnpm collector:login
```

Login opens `https://chatglm.cn/`; finish login manually in this dedicated browser
and close it before probing. Login requires no API credentials or manifest.
Never transfer personal or in-app browser cookies into this profile.

Set `QINGYAN_PAGE_CONTRACT_PATH` to an external reviewed JSON manifest with
version 2, exact origin `https://chatglm.cn`, samplingMode `default_conversation`,
reviewedAt within seven days and verifiedOnAuthorizedPage=true. The mode is a
sampling label, not a claim that network search was used. Set the reviewed flag
only after authorized page inspection; no live selectors ship as defaults.
The manifest uses the shared consumer-Web selector fields. Confirm authenticated
UI, new conversation, composer, current mode, assistant ownership, body-only
content, positive completion, generating and visible public citation anchors.
Inline citations use visible href attributes within the assistant message.
Qingyan supports multiple visible body chunks in DOM order, waiting
for the first chunk during generation. The completed answer uses the shared
Markdown converter to retain headings, paragraphs, ordered/unordered lists,
tables and links, with blank lines between visible body chunks. Hidden copies,
media and controls are omitted. Keep reasoning, diagrams and source snippets
outside `answerBody`; use the reviewed `answerIgnore` for inline source chips
and other non-answer elements within the body. Completion and stability checks
still use the plain body text; source extraction and both raw/formatted prompt
echo guards remain active. Empty or failed Markdown conversion keeps the legacy
plain-text answer, so body-selector review remains mandatory. Its readiness
hydration budget is 15 seconds
(still bounded by collection timeout); Doubao and Ernie retain five seconds.
For the observed separate source sidebar, provide paired `sourcePanel` and
`sourceCards` selectors plus `sourcesToggle`. The toggle is scoped to the
completed assistant message; the panel selector must uniquely identify the
drawer opened by that toggle, and cards are relative to that panel. Configure
`sourceEvidence` to detect inline reference markers even if the footer disappears.
The reader checks a caption of `N个来源` (0–100), opens the initially closed panel
once and requires exactly N visible cards. Each card is clicked once to capture
its original normal new-tab navigation target; the tab closes without reading
its body. A scoped BrowserContext request observer records only initial
main-frame navigation URLs and pairs them with the new page's Qingyan opener.
It does not intercept requests or read application traffic, headers or bodies.
Redirected destinations and browser error pages do not replace the original
citation, so failed external loads can still yield the actual cited URL. If
Playwright cannot supply the frame/opener association, extraction fails closed.
The
result deduplicates actual URLs after verifying card completeness. Missing
popups, ambiguous panels, contradictory zero counts or unusable URLs fail closed.
Popup capture is bounded to thirty seconds per card and the overall collection
deadline. A 150 ms event-delivery window checks both popup events and new pages
identified by navigation requests, rejecting multiple pages from the same click.
Abort closes opened source tabs. The adapter rechecks unchanged body,
origin, positive completion and blockers before returning the result.

The exported `readQingyanSources` helper can verify a historical completed
answer's sidebar without sending a question. Historical URLs are diagnostic
evidence only; never submit that historical answer as a new sample.

```bash
pnpm collector:probe
```

Require `zhipu_qingyan_probe` status `ready` before queue writes or leases.
Use `pnpm collector:run` with the approved external environment. A submitted
failure must not be queried again automatically or by clearing an attempt ledger.
The Qingyan attempt namespace is separate; Doubao and Ernie hashes remain
unchanged. Spool bindings refuse cross-platform replay. The outgoing question
is the original question alone (the shared prefix was removed on 2026-11-16);
task identity and approval-list comparisons use the same text.
New-conversation and approved-mode checks still apply to each question. Verify
observed source metadata separately.

## Current approved collection (2026-10-23)

The Jogeoai API task `.trellis/tasks/10-23-qingyan-markdown-collection` owns the
approved collection for 优联科技. Before creating its Run, read the site's
currently enabled questions and require exact equality with the approved list
(expected ten questions); report any change before proceeding. Use this Run's
dedicated node and encrypted spool. Before each lease, check the nonterminal
queue read-only and require the next task to belong to this Run with no prior
attempt. Bound each process to one explicit task with
`COLLECTOR_RETRY_TASK_ID`, `COLLECTOR_MAX_TASKS_PER_RUN=1` and
`COLLECTOR_RUN_ONCE=true`; leave at least thirty seconds between questions.

Send each task once in a new conversation. Preserve the attempt ledger on a
post-send failure and recover only from that task's original conversation after
matching its complete outgoing question and finished answer. Do not backfill
historical answers into this Run. Verify stored Markdown and sources through
the API, then correlate Run, Check, answer and analysis status before recording
live acceptance.

### Verified result

Run `84c9650d-0060-449b-bac5-7e074fa7f671` completed on 2026-10-23: 10/10 checks
and analyses succeeded, with 140 citation URLs across 98 domains. The stored
answers contain headings, paragraphs, lists and tables (2,447–3,883 Unicode
characters, 10–31 paragraph blocks); all 10 monitoring record IDs match the run.
No citation titles or search keywords were uploaded.

The ninth task stopped with `page_contract_changed`. Its original completed
conversation was captured before collecting the unattempted tenth question.
Recovery checked the original conversation ID, complete outgoing question,
stable plain text and Markdown hashes, then read 15 sources using the unchanged
source reader and rechecked the answer. A direct-link readiness failure required
navigation through site history; another source read stopped at the existing
structure guard before the final successful read. The precise cause of those
structure-check stops was not established, and no guard was weakened. Only then
was the expired ninth lease reacquired and the result submitted. That task has
control-plane attempt/lease counters of 2; the other nine have 1. The browser
ledger remains 10 distinct tasks/questions, one send each, and was unchanged by
recovery. No encrypted submissions remain pending, and profile/spool locks are
released. Non-sensitive evidence belongs to the Jogeoai task's
`live-verification.json` and `acceptance-report.md`; private conversation and
profile evidence remains external.

## Historical four-question smoke

`09-07-real-ai-search-e2e/qingyan-next.md` (a Trellis task in the sibling
`Ai-visiblility-api` repository) owns the earlier smoke's existing API/project and four unchanged questions: 管道内窥镜, 优联科技, ulinktek,
下水道内窥镜制造商. Its bound is at most four tasks. Run one controlled question
first and verify its accepted body through the API before the remaining three.
Disable that smoke's matrix immediately after scheduling its exact four tasks
and confirm all terminal results before restoring competing Collector/scheduler
processes. Its four-question scope does not define later authorized runs.
