-- Synthetic pre-005 data for migration verification; use only in an isolated test database.
INSERT IGNORE INTO tenants (id, name, status, created_at, updated_at) VALUES
    (UUID_TO_BIN('22222222-2222-4222-8222-222222222222'), 'Legacy Test', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
INSERT IGNORE INTO sites (id, tenant_id, host, display_name, status, created_at, updated_at) VALUES
    (UUID_TO_BIN('33333333-3333-4333-8333-333333333333'), UUID_TO_BIN('22222222-2222-4222-8222-222222222222'),
     'legacy.example', 'Legacy', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
INSERT IGNORE INTO prompts (id, site_id, content, locale, enabled, created_at, updated_at) VALUES
    (UUID_TO_BIN('44444444-4444-4444-8444-444444444444'), UUID_TO_BIN('33333333-3333-4333-8333-333333333333'),
     'Legacy question', 'en-US', TRUE, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
INSERT IGNORE INTO visibility_runs (id, tenant_id, site_id, status, idempotency_key, requested_at, total_checks, created_at, updated_at) VALUES
    (UUID_TO_BIN('55555555-5555-4555-8555-555555555555'), UUID_TO_BIN('22222222-2222-4222-8222-222222222222'),
     UUID_TO_BIN('33333333-3333-4333-8333-333333333333'), 'queued', 'legacy-cloro-key', UTC_TIMESTAMP(6), 1, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)),
    (UUID_TO_BIN('66666666-6666-4666-8666-666666666666'), UUID_TO_BIN('22222222-2222-4222-8222-222222222222'),
     UUID_TO_BIN('33333333-3333-4333-8333-333333333333'), 'queued', 'legacy-domestic-key', UTC_TIMESTAMP(6), 1, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
INSERT IGNORE INTO run_engines (id, run_id, engine, provider, request_options, sort_order, created_at) VALUES
    (UUID_TO_BIN('77777777-7777-4777-8777-777777777777'), UUID_TO_BIN('55555555-5555-4555-8555-555555555555'),
     'chatgpt', 'cloro', JSON_OBJECT('country', 'US'), 0, UTC_TIMESTAMP(6)),
    (UUID_TO_BIN('88888888-8888-4888-8888-888888888888'), UUID_TO_BIN('66666666-6666-4666-8666-666666666666'),
     'doubao', 'domestic_collector', JSON_OBJECT(), 0, UTC_TIMESTAMP(6));
INSERT IGNORE INTO visibility_checks (id, tenant_id, site_id, run_id, prompt_id, engine, provider, status, prompt_snapshot,
    capture_mode, evidence_class, locale, country, created_at, updated_at) VALUES
    (UUID_TO_BIN('99999999-9999-4999-8999-999999999999'), UUID_TO_BIN('22222222-2222-4222-8222-222222222222'),
     UUID_TO_BIN('33333333-3333-4333-8333-333333333333'), UUID_TO_BIN('55555555-5555-4555-8555-555555555555'),
     UUID_TO_BIN('44444444-4444-4444-8444-444444444444'), 'chatgpt', 'cloro', 'pending', 'Legacy question',
     'scraped', 'observed_surface', 'en-US', 'US', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)),
    (UUID_TO_BIN('aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'), UUID_TO_BIN('22222222-2222-4222-8222-222222222222'),
     UUID_TO_BIN('33333333-3333-4333-8333-333333333333'), UUID_TO_BIN('66666666-6666-4666-8666-666666666666'),
     UUID_TO_BIN('44444444-4444-4444-8444-444444444444'), 'doubao', 'domestic_collector', 'pending', 'Legacy question',
     NULL, 'observed_surface', 'zh-CN', 'CN', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
INSERT IGNORE INTO provider_tasks (id, run_id, check_id, provider, operation, state, next_attempt_at, created_at, updated_at) VALUES
    (UUID_TO_BIN('bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'), UUID_TO_BIN('55555555-5555-4555-8555-555555555555'),
     UUID_TO_BIN('99999999-9999-4999-8999-999999999999'), 'cloro', 'submit_poll', 'pending', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
INSERT IGNORE INTO collector_tasks (id, tenant_id, run_id, check_id, platform_entry_id, platform_code, region, language,
    channel, question_snapshot, status, next_attempt_at, created_at, updated_at)
SELECT UUID_TO_BIN('cccccccc-cccc-4ccc-8ccc-cccccccccccc'), UUID_TO_BIN('22222222-2222-4222-8222-222222222222'),
       UUID_TO_BIN('66666666-6666-4666-8666-666666666666'), UUID_TO_BIN('aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'),
       e.id, 'doubao', 'cn', 'zh-CN', 'consumer_web', 'Legacy question', 'pending', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
FROM collector_platform_entries e WHERE e.platform_code = 'doubao' ORDER BY e.version DESC LIMIT 1;
