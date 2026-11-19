# Real DeepSeek consumer-web smoke

This runbook uses the DeepSeek consumer website through a dedicated persistent
Playwright Profile. It does not use the DeepSeek API and never places a key,
Cookie, or authorization header in Collector configuration or result payloads.

## 1. Review the authorized page

Open the DeepSeek page with the supported login command and complete login or
any platform verification manually:

```bash
export COLLECTOR_PROFILE_DIRECTORY=/absolute/path/aivc/real-smoke/deepseek-cn
export COLLECTOR_SUPPORTED_PLATFORMS=deepseek
pnpm collector:login
```

After login, inspect only non-secret DOM markers for the authenticated state,
new conversation control, composer, submit control, assistant answer,
completion state, and visible citations. Write a reviewed external contract
file with `origin: "https://chat.deepseek.com"`,
`samplingMode: "default_conversation"`, and
`verifiedOnAuthorizedPage: true`.

Set its path in `DEEPSEEK_PAGE_CONTRACT_PATH`. Contracts expire after seven
days and are rejected when the origin or sampling mode is changed.

## 2. Probe before leasing

```bash
export COLLECTOR_MODE=real
export COLLECTOR_API_URL=http://127.0.0.1:4314
export COLLECTOR_REGION=cn
export COLLECTOR_EGRESS_COUNTRY=CN
export COLLECTOR_CAPACITY=1
export COLLECTOR_SUPPORTED_PLATFORMS=deepseek
export COLLECTOR_EXPECTED_PROJECT_ID=<smoke-project-id>
export DEEPSEEK_PAGE_CONTRACT_PATH=/absolute/path/aivc/real-smoke/deepseek-page-contract.json
pnpm collector:probe
```

Only a `ready` probe permits the Collector to lease tasks. Login, region,
challenge, rate-limit, origin, and contract failures stop the run without
submitting synthetic answers.

## 3. Run approved tasks

Use the control-plane token and spool settings from the isolated smoke
environment. Keep the matrix disabled until the probe is ready, then schedule
only the intended DeepSeek project. The Collector sends the exact question text and nothing else (the
[shared browser prefix](../../README.md#browser-question-prefix) was removed on 2026-11-16).
It streams the visible assistant answer until stable completion, filters
internal/non-public links, and submits the result through the existing lease,
heartbeat, and idempotent result contract.

The website may change its DOM or require a fresh manual verification. In that
case update the external contract after review; do not add API calls, bypass
verification, or fabricate citations.

## 4. Smart search mode (`web_search_conversation`, reviewed 2026-10-22)

DeepSeek renamed its web toggle to 「智能搜索」. For runs that must return sources,
set it on and 「深度思考」 off in the Profile by hand, then use a contract with
`samplingMode: "web_search_conversation"`. The adapter never toggles modes: the
contract's `conversationActive` asserts the state, e.g.

```
div:has(> .ds-toggle-button--selected:has-text("智能搜索")):not(:has(> .ds-toggle-button--selected:has-text("深度思考")))
```

so a changed mode stops readiness before any prompt is sent.

- `answerBody`: `.ds-assistant-message-main-content` (the thinking/search trace is
  a separate `.ds-think-content` and is never part of the answer).
- Inline citation chips are `span[data-citation-group] > a[href] > span.ds-markdown-cite`;
  use `citations: ".ds-assistant-message-main-content [data-citation-group] a[href]"` and
  `answerIgnore: "[data-citation-group]"` so the Markdown answer carries no chips or empty links.
- `sourcesToggle` matches the footer count, e.g. `.f93f59e4 :text-matches("^\\d+ 个网页$")`.
  The adapter clicks it, waits for the 「搜索结果」 side panel to list exactly that many
  `a[href]:has(.search-view-card__title)` cards, submits their URLs with titles, then closes
  the panel. A count mismatch, a non-public card, or a panel already open fails closed;
  more than 100 sources is rejected (the Jogeoai intake limit) rather than truncated.
  An answer that did not search (no footer count and no inline citations) submits no sources.
- Blocker selectors are full-text anchored (`text=/^服务器繁忙，请稍后再试。?$/`) so they do not
  fire on answer paragraphs; `isVisible()` requires at most one match.
- Run one task per process with `COLLECTOR_RUN_ONCE=true`. Do not lower
  `COLLECTOR_MAX_TASKS_PER_RUN` for this: in real mode it is also the lifetime attempt-ledger cap
  per binding, and a value at or below the ledger size stops before leasing (`task_not_approved`).
