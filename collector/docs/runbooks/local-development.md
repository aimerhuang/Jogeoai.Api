# Local Development Runbook

## Region configuration

`collector-cn` clears proxy variables and only supports the seven mainland platform codes. `collector-overseas` supports only ChatGPT, Claude, and Gemini and receives an optional, externally injected HTTPS proxy URL. The control plane still validates task region during both lease and result submission.

The two collectors use distinct service tokens. `COLLECTOR_CN_SERVICE_TOKEN` can only operate on CN leases, while `COLLECTOR_OVERSEAS_SERVICE_TOKEN` can only operate on overseas leases. Never reuse one value for both settings; API startup rejects an ambiguous configuration.

Collector configuration validates the configured country code in both Mock and `real` mode (`CN` for the mainland node and a non-`CN` value for the overseas node; a mismatch is rejected as `EGRESS_REGION_MISMATCH`). This is a configuration assertion, not a measurement: no outbound IP country probe exists, including for the browser adapters. Verify the actual egress independently.

## Resetting local fixture data

The PostgreSQL volume is named `ai-visibility-collector-postgres-data`. Do not remove it during normal application upgrades. For an intentional local reset only:

```bash
docker compose --env-file .env down
docker volume rm ai-visibility-collector-postgres-data
```

This deletes local database data and is not a production recovery procedure.

## Collector spool

Each collector has a separate persistent spool volume. Failed in-flight submissions are encrypted with AES-256-GCM using `COLLECTOR_SPOOL_KEY`. After API connectivity returns, the collector replays the submission; the central lease version decides whether it is accepted or retained only as a stale audit submission.
