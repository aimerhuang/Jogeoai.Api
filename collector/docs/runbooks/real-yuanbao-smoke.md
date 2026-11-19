# Real Tencent Yuanbao consumer-web smoke

This runbook uses the Tencent Yuanbao consumer website through a dedicated
persistent Playwright Profile. It does not use Tencent Hunyuan API credentials
and never places a key, Cookie, or authorization header in Collector
configuration or result payloads.

## 1. Review the authorized page

Open the Tencent Yuanbao page with the supported login command and complete
login or any platform verification manually:

```bash
export COLLECTOR_PROFILE_DIRECTORY=/absolute/path/aivc/real-smoke/yuanbao-cn
export COLLECTOR_SUPPORTED_PLATFORMS=tencent_yuanbao
pnpm collector:login
```

After login, inspect only non-secret DOM markers for the authenticated state,
new conversation control, composer, submit control, assistant answer,
completion state, and visible citations. Write a reviewed external contract
file with `origin: "https://yuanbao.tencent.com"`,
`samplingMode: "default_conversation"`, and
`verifiedOnAuthorizedPage: true`.

Set its path in `YUANBAO_PAGE_CONTRACT_PATH`. Contracts expire after seven
days and are rejected when the origin or sampling mode is changed. Synthetic
fixtures in the repository are test-only and are not evidence for a live
contract.

## 2. Probe before leasing

```bash
export COLLECTOR_MODE=real
export COLLECTOR_API_URL=http://127.0.0.1:4314
export COLLECTOR_REGION=cn
export COLLECTOR_EGRESS_COUNTRY=CN
export COLLECTOR_CAPACITY=1
export COLLECTOR_SUPPORTED_PLATFORMS=tencent_yuanbao
export COLLECTOR_EXPECTED_PROJECT_ID=<smoke-project-id>
export YUANBAO_PAGE_CONTRACT_PATH=/absolute/path/aivc/real-smoke/yuanbao-page-contract.json
pnpm collector:probe
```

Only a `ready` probe permits the Collector to lease tasks. Login, region,
challenge, rate-limit, origin, and contract failures stop the run without
submitting synthetic answers.

## 3. Run approved tasks

Use the control-plane token and spool settings from the isolated smoke
environment. Keep the matrix disabled until the probe is ready, then schedule
only the intended Tencent Yuanbao project. The Collector sends the exact question text and nothing else (the
[shared browser prefix](../../README.md#browser-question-prefix) was removed on 2026-11-16).
It streams the visible assistant answer until stable completion,
filters internal/non-public links, and submits the result through the existing
lease, heartbeat, and idempotent result contract.

The website may change its DOM or require a fresh manual verification. In that
case update the external contract after review; do not add API calls, bypass
verification, export personal browser cookies, or fabricate citations.

## 4. Authorized-page review: 2026-10-22

The current quick-answer UI uses `.agent-chat__list__item--ai` as the
assistant scope, `.hyc-common-markdown` for the answer, and the answer-local
`[aria-label="重新生成"]` control as positive completion evidence. Wait for the
body to appear after the assistant placeholder, then require stable text and
absence of the generating control. The external contract also checks that
`button[aria-label="切换模型"]` displays `快速回答` before sending.

The `源` control has an accessible label such as `引用52篇资料作为参考`.
Its source drawer is outside the assistant scope. The Yuanbao-specific reader
opens that answer's control, requires the declared number of DOM source cards,
reads public URLs from `.hyc-common-markdown__ref_card[data-url]`, and aligns
optional titles from `.hyc-common-markdown__ref_card-title`. Duplicate URLs
are deduplicated after validating the raw card count. A missing/incomplete
source drawer, stale open drawer, or non-public card stops collection.

Close the drawer through `.agent-dialogue-references__close button`.
The surrounding `.agent-dialogue-references__close` div is wider than the
button, and clicking the div's center does not close the real drawer. The
synthetic fixture preserves this distinction and declares UTF-8 explicitly;
otherwise routed HTML can misdecode the Chinese accessible labels and skip
the source reader in tests.

Answers are plain text. Search queries are not reliably exposed and are not
submitted. Titles are best effort; the result contract drops unreadable,
invalid, or over-512-character titles while retaining their URLs. Never
report such omissions as absent sources.

If a prompt has already been sent when a DOM check fails, inspect the matching
history conversation and save the existing completed answer under the still
valid lease. Do not call `collect()` or reserve another attempt for inspection.
Preserve encrypted spool files until the control plane accepts the result.
