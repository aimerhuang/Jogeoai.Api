# Real Gemini consumer-Web smoke

This runbook uses the Gemini consumer website through a dedicated persistent
browser Profile and an authorized overseas egress. It does not use a Gemini API
key and never places a password, Cookie, authorization header, proxy credential,
or full answer in Collector configuration or logs.

## 1. Open and review the authorized page

Use a dedicated profile. Complete Google login and any verification manually;
the Collector does not read credentials, automate challenges, or bypass access
controls.

```bash
export COLLECTOR_PROFILE_DIRECTORY=/absolute/path/aivc/real-smoke/gemini-overseas
export COLLECTOR_SUPPORTED_PLATFORMS=gemini
pnpm collector:login
```

If login requires a normal user-managed Chrome session, start a dedicated
Chrome profile with a local-only CDP endpoint, log in visibly, then set:

```bash
export COLLECTOR_BROWSER_CDP_URL=http://127.0.0.1:9222
```

Review only non-secret DOM markers for authenticated state, new conversation,
composer, submit control, active conversation, assistant answer, generation,
completion, and answer-associated citations. Store the selectors outside Git:

```json
{
  "version": 2,
  "origin": "https://gemini.google.com",
  "samplingMode": "default_conversation",
  "reviewedAt": "<ISO timestamp>",
  "verifiedOnAuthorizedPage": true,
  "selectors": { "<each selector key>": "<reviewed selector>" }
}
```

No bundled selector is treated as live evidence. Contracts expire after seven
days and fail closed when their origin or sampling mode changes. Default
conversation mode does not prove that Gemini used Google Search grounding.

## 2. Probe before leasing

```bash
export COLLECTOR_MODE=real
export COLLECTOR_API_URL=http://127.0.0.1:4314
export COLLECTOR_REGION=overseas
export COLLECTOR_EGRESS_COUNTRY=<ISO-3166-1-alpha-2-country>
export COLLECTOR_CAPACITY=1
export COLLECTOR_SUPPORTED_PLATFORMS=gemini
export COLLECTOR_EXPECTED_PROJECT_ID=<smoke-project-id>
export GEMINI_PAGE_CONTRACT_PATH=/absolute/path/aivc/real-smoke/gemini-page-contract.json
pnpm collector:probe
```

Only `ready` permits leasing. Login, region, challenge, rate-limit, origin, or
contract failures stop the run without submitting a synthetic answer. Verify
the overseas egress independently; the configured country is an operator
assertion, not a geolocation measurement.

## 3. Run only the approved questions

Keep the Gemini matrix disabled until the probe is ready. Enable and schedule
only the intended project, questions, sample slots, and date. Keep
`COLLECTOR_MAX_TASKS_PER_RUN` at the exact approved query count, then run:

```bash
pnpm collector:run
```

The adapter sends the unchanged leased question and nothing else (the
[shared browser prefix](../../README.md#browser-question-prefix) was removed on 2026-11-16). It renews the lease while waiting, records stable assistant
output and public answer-associated URLs, and submits
through the existing idempotent result endpoint. Read the resulting sample via
the control-plane GEO prompt and answer-detail routes. Never treat fixture tests
as live evidence, fabricate citations, or retry an uncertain external submission
without explicit approval.
