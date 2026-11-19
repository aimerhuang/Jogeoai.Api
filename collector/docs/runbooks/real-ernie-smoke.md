# Bounded Ernie consumer Web smoke

This is the Ernie follow-up to the four-question Doubao slice, using stable
`ernie` / `ernie.consumer_web`. It is not evidence that the ten-platform program
is complete. The API task `09-07-real-ai-search-e2e/ernie-next.md` (a Trellis
task in the sibling `Ai-visiblility-api` repository) owns live acceptance. Use its current API/project and inspect the shared queue first;
do not recreate the historical disposable Doubao database.

Use Node.js 24 and an external environment file with the approved API URL,
tenant/project IDs, CN service token and normal real Collector limits. Set
`COLLECTOR_SUPPORTED_PLATFORMS=ernie`, capacity 1, CN region/egress, at most four
tasks, and a dedicated Ernie node, profile and encrypted spool. Profiles must
be absolute, owner-only directories outside Git with `/aivc/` in their path.
Never export an in-app browser's cookies or personal browser profile.

```bash
export PATH=/opt/homebrew/opt/node@24/bin:$PATH
export COLLECTOR_SUPPORTED_PLATFORMS=ernie
export COLLECTOR_PROFILE_DIRECTORY=/absolute/external/aivc/ernie-cn
pnpm collector:login
```

Login opens `https://wenxin.baidu.com/`. Complete login manually and close the
browser before probing. Login needs no API credentials or page contract.
Retain the reviewed quick-conversation mode; its button may display 快速 or 对话
depending on the UI version. It does not prove network search occurred.

Provide `ERNIE_PAGE_CONTRACT_PATH` to a reviewed external JSON manifest. Its
strict schema requires version 2, origin `https://wenxin.baidu.com`, samplingMode
`quick_conversation`, a reviewedAt timestamp no older than seven days, and
verifiedOnAuthorizedPage=true. These are operator assertions: synthetic tests
are never evidence for setting that flag. The selectors have the same required
names as the Doubao manifest; `conversationActive` must identify the reviewed
visible quick-conversation mode, `assistantMessage` must distinguish assistant from user messages,
and `answerBody` may match multiple visible Markdown chunks in DOM order.
Keep citations and source snippets outside those body selectors. `completed`
requires a positive completion marker, in addition to absence of `generating`.
Ernie reference selectors identify elements carrying `data-long-press-ext-info`;
only the public URL in the parsed JSON `link` field is extracted. Malformed
reference metadata fails closed. No selectors are bundled as validated defaults.

```bash
pnpm collector:probe
```

Require `ernie_probe` status `ready` before scheduling/leasing. Then configure
only the exact four approved tasks for 管道内窥镜, 优联科技, ulinktek and
下水道内窥镜制造商 and run `pnpm collector:run` with the external real environment.
Do not delete attempt ledgers to retry: a reserved attempt survives crashes.
Ernie has a separate attempt namespace; existing Doubao ledger hashes are
unchanged. Spool bindings reject cross-platform reuse; retain old spool data
and use a separate directory. Verify accepted answers and actual citations
through the control plane, then disable the bounded matrix.

## Authorized-page review: 2026-10-23

The current default mode button displays `对话`. Opening its normal mode menu
shows `data-chat-mode="fast"` with `aria-checked="true"`; the separate work-task
mode is unselected. The older `快速` caption therefore fails readiness even
with a valid login. Review the selected menu state before updating the external
`conversationActive` selector to the current visible `对话` button. Keep
`samplingMode: "quick_conversation"`; the label change does not establish a
specific underlying model version or prove network search for every answer.

A repeated operator-approved question set can use fresh control-plane tasks,
with each exact new task ID set as `COLLECTOR_RETRY_TASK_ID` and
`COLLECTOR_MAX_TASKS_PER_RUN=1`. Preserve the Profile attempt ledger and validate
that the next queued task belongs to the intended new Run before each lease.
