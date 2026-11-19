# Real ChatGPT consumer-Web smoke

This runbook uses the ChatGPT consumer website through a dedicated persistent
Playwright Profile and an authorized overseas egress. It does not use an OpenAI
API key and never places a password, Cookie, authorization header, proxy
credential, or full answer in Collector configuration, logs, or task payloads.

## 1. Review the authorized page

Prefer a user-managed Chrome session when OAuth rejects automated Chromium.
Fully quit Chrome, start a separate normal Chrome profile with a local-only CDP
endpoint, and complete login or verification in that visible browser:

```bash
/Applications/Google\ Chrome.app/Contents/MacOS/Google\ Chrome \
  --remote-debugging-port=9222 \
  --user-data-dir="$HOME/Library/Application Support/aivc/real-smoke/chatgpt-chrome"
```

Do not expose port 9222 beyond localhost. The Collector connects to this
browser and disconnects without closing it; it never copies the profile or
reads credentials.

```bash
export COLLECTOR_PROFILE_DIRECTORY=/absolute/path/aivc/real-smoke/chatgpt-overseas
export COLLECTOR_SUPPORTED_PLATFORMS=chatgpt
export COLLECTOR_BROWSER_CDP_URL=http://127.0.0.1:9222
pnpm collector:login
```

Inspect only non-secret DOM markers for authenticated state, new conversation,
composer, submit control, active conversation, assistant answer, completion,
and visible citations. Write an external JSON contract with:

```json
{
  "version": 2,
  "origin": "https://chatgpt.com",
  "samplingMode": "default_conversation",
  "reviewedAt": "<ISO timestamp>",
  "verifiedOnAuthorizedPage": true,
  "selectors": { "<each selector key>": "<reviewed selector>" }
}
```

The selector values are intentionally operator-reviewed and are not bundled in
the repository. Contracts expire after seven days and are rejected if their
origin or sampling mode changes.

## 2. Probe before leasing

```bash
export COLLECTOR_MODE=real
export COLLECTOR_API_URL=http://127.0.0.1:4314
export COLLECTOR_REGION=overseas
export COLLECTOR_EGRESS_COUNTRY=JP
export COLLECTOR_CAPACITY=1
export COLLECTOR_SUPPORTED_PLATFORMS=chatgpt
export COLLECTOR_EXPECTED_PROJECT_ID=<smoke-project-id>
export CHATGPT_PAGE_CONTRACT_PATH=/absolute/path/aivc/real-smoke/chatgpt-page-contract.json
export COLLECTOR_BROWSER_CDP_URL=http://127.0.0.1:9222
pnpm collector:probe
```

Only a `ready` probe permits leasing. Login, region, challenge, rate-limit,
origin, or contract failures stop the run without submitting a synthetic
answer. Confirm that the overseas egress is compliant independently; the
configured country is an assertion, not a geolocation measurement.

## 3. Run approved tasks

Keep the ChatGPT matrix disabled until the probe is ready, then schedule only
the intended project and date. The adapter submits the exact leased question and nothing else (the
[shared browser prefix](../../README.md#browser-question-prefix) was removed on 2026-11-16).
It waits for stable assistant output, filters internal/non-public links, and uses
the existing heartbeat and idempotent result protocol. If the DOM changes or
the site requests verification, stop and review a new external contract;
never bypass verification or fabricate citations.
