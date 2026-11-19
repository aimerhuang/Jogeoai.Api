# Real Qwen consumer-web collection

Use `qwen` with a dedicated authorized persistent Profile and the local
Jogeoai control plane. This path collects the consumer website at
`https://www.qianwen.com/`; it uses the Profile's existing login.

## Configuration and page review

Keep the real-mode environment, approved question list, spool key, and page
contract outside the repository. Use owner-only file permissions. Configure
one platform, capacity one, the intended tenant/site, and a separate encrypted
spool. Set `QWEN_PAGE_CONTRACT_PATH` to the reviewed version-2 contract with
origin `https://www.qianwen.com`, sampling mode `default_conversation`, and
`verifiedOnAuthorizedPage: true`. Contracts expire after seven days.

The 2026-10-22 page review found these behaviors:

- The sidebar's `[data-session-switch-target="new-chat"]` can remain in the
  DOM outside the viewport when the sidebar collapses. Select the visible
  toolbar `button:has(> [data-icon-type="qwpcicon-newDialogue"]):visible`
  when present; otherwise select the sidebar control. Review both layouts
  before leasing. Clicking a hidden or offscreen control is unnecessary.
- The quick-mode measurement clone can pass ordinary visibility checks.
  Scope the active mode to `[data-role="visible-capsule"]`.
- The empty Slate composer can expose `\uFEFF\n\n向千问提问` through
  `innerText`. Only the reviewed placeholder with surrounding whitespace is
  accepted; other draft content stops collection before sending.
- Assistant messages use `[class*="message-select-wrapper-answer"]`.
  Read visible `.qk-markdown` bodies and require a stable answer with
  `.qk-markdown-complete` and no generating marker.
- A visible `iframe#baxia-dialog-content` is a verification blocker. Inspect
  only the iframe element's presence and visibility. The operator must
  complete the platform verification manually.

Run `pnpm collector:probe` with the same external configuration. Only a
`ready` probe permits new leases. Synthetic fixtures prove adapter behavior;
they do not replace this authorized live-page review.

## Sources and result submission

The answer-local `.reference-wrap-iEjeb3` toggle declares `N篇来源`.
Its cards are in a separate `.deep-think-source-tyxrYL` panel. Read URL and
optional title from each `.source-item-mqZd08` element's `data-click-extra`.
Validate the raw visible-card count before canonical URL deduplication and
align titles with the final URL list. Duplicate/hidden toggles, stale visible
cards, invalid URLs, and incomplete or excess cards stop collection.

Titles are optional and bounded to 512 UTF-16 units. An unreadable title
does not discard a valid URL. Answers are plain text. Search keywords are
not reliably exposed and are omitted; this does not prove search was absent.

Use the normal `pnpm collector:run` entry point. Preserve the exact approved
question text in the task, approvals and attempt ledger. Browser messages are the original question alone (the
[shared prefix](../../README.md#browser-question-prefix) was removed on 2026-11-16).
Preserve the lease heartbeat, encrypted spool, and result
idempotency key. Retain the spool until the control plane accepts the result.

## Recovering an interrupted question

Before sending again, establish whether the question was already sent.
For an existing answer, inspect the matching history conversation, require
an exact user-message match (original question alone; full prefix plus original
question for rounds sent between 2026-10-23 and 2026-11-16), stable completed
answer, and the corresponding
source cards. Save that existing result under the valid lease. Do not call
`collect()`, lease again, or reserve another attempt to inspect an answer.

For a failure proven to have occurred before sending, fix the observed page
rule and resume the existing reserved task under its still-valid lease.
Login, verification, rate limits, or expired ownership require a stop; never
reset the attempt ledger to retry.

If ownership expires during manual verification, capture and validate the
existing completed answer before changing conversations. Reacquire its task
through the normal lease API and validate its exact task, question, tenant,
site, and new lease version before submitting the saved answer. Queue order
may require finishing the remaining untouched tasks first. This recovery
requires no additional browser prompt or attempt-ledger reservation; the
control-plane attempt count still increases for the new lease. Do not alter
task ordering or lease columns directly in the database.

## Targeted verification

From the repository root, using Node.js 24:

```bash
node node_modules/vitest/vitest.mjs run --config vitest.config.ts apps/collector/src/adapters/qwen.test.ts
node node_modules/typescript/bin/tsc --noEmit
node node_modules/eslint/bin/eslint.js apps/collector/src/adapters/qwen.ts apps/collector/src/adapters/qwen.test.ts
node node_modules/prettier/bin/prettier.cjs --check apps/collector/src/adapters/qwen.ts apps/collector/src/adapters/qwen.test.ts apps/collector/src/adapters/fixtures/qwen-synthetic.html
```

These checks passed on 2026-10-22: 22 Qwen tests, type checking, scoped lint,
and formatting. Runtime acceptance additionally requires the intended Run's
task/check counts, attempt counts, API monitoring records, citations/titles,
and completed analyses to agree. The Jogeoai consumer-web path is manually
started and is not included in its brand daily schedule.

## Authorized live acceptance: 2026-10-22

Jogeoai run `f021f463-d56e-494e-9cd8-689bb3b550c8` completed all 10 approved
Ulinktek questions against API port 5260. All tasks/checks succeeded; all
10 monitoring records matched the check IDs and had completed analyses.
The 145 persisted citation URLs all have titles and cover 47 domains.

The operator completed platform verification manually. Questions 7 and 9
were recovered from their existing completed conversations. Question 7's
lease expired during that wait; its saved answer was submitted under lease
version 2 after normal queue reacquisition. Nine tasks have one control-plane
attempt and question 7 has two. The browser attempt ledger still contains
10 distinct tasks and questions. Final pending encrypted spool count is zero;
the dedicated browser was closed and both process locks were released.
