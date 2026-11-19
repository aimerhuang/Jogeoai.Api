-- 036_packy_answer_providers.sql
-- 为 answer_provider_settings（025）新增经 PackyAPI 接入的三个 API 回答 Provider 的默认行：
--   packy_qwen  千问（bailian 分组）           OpenAI Responses（POST /v1/responses，流式）+ web_search
--   packy_glm   智谱清言（zai-officially 分组） OpenAI Responses（POST /v1/responses，流式）+ web_search
--   packy_kimi  Kimi（kimi-officially 分组）    Anthropic Messages（POST /v1/messages）+ web_search_20250305，max_uses 必须为 1
-- PackyAPI 的 key 按分组发放、不能互用，因此每个 Provider 一行、一把凭据。
-- 依赖 025（answer_provider_settings 表必须已存在）；手动执行于 001–035 之后，API / Worker 启动不会执行本文件。
-- 只插入种子，不改表结构，可重复执行。
-- 执行时必须使用 --default-character-set=utf8mb4，否则非 ASCII 注释会写成乱码。
-- 超时：千问 540 秒（完整回答实测 2–4 分钟以上，搜索可达十余次），GLM / Kimi 240 秒；Worker 租约默认 600 秒。
-- 种子未启用、无凭据：系统管理员通过 PUT /api/v1/providers/packy/{qwen|glm|kimi}/settings 保存 Key 并启用，
-- 或按本机约定用 getpass 脚本直接写库；凭据不进入本文件。
-- options：{"requestMode":"web_search","sources":[],"maxToolCalls":1-10,"thinking":"disabled"|"enabled"}；
-- 千问 / GLM 的 maxToolCalls 只做记录不下发，Kimi 必须为 1；默认关闭思考（千问开着思考时不会发起搜索）。
-- 回滚：DELETE FROM answer_provider_settings WHERE provider IN ('packy_qwen', 'packy_glm', 'packy_kimi');
-- 回滚后目录中的三个 Target 为 unavailable，手动 Run 返回 503 PACKY_NOT_CONFIGURED，
-- 每日时隙以 TARGET_UNAVAILABLE 跳过；已入库的 Check 保留，Cloro、豆包与 DeepSeek 链路不受影响。

-- ON DUPLICATE KEY UPDATE provider = provider 保证重放不会覆盖管理员已经保存的地址、模型、凭据或开关。
INSERT INTO answer_provider_settings
    (provider, api_url, model, credential, credential_hint, enabled, timeout_seconds, options, created_at, updated_at)
VALUES
    ('packy_qwen', 'https://www.packyapi.ai/v1/responses', 'qwen3.8-flash',
     NULL, NULL, 0, 540, '{"requestMode":"web_search","sources":[],"maxToolCalls":5,"thinking":"disabled"}',
     UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)),
    ('packy_glm', 'https://www.packyapi.ai/v1/responses', 'glm-5.2',
     NULL, NULL, 0, 240, '{"requestMode":"web_search","sources":[],"maxToolCalls":5,"thinking":"disabled"}',
     UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)),
    ('packy_kimi', 'https://www.packyapi.ai/v1/messages', 'kimi-k3',
     NULL, NULL, 0, 240, '{"requestMode":"web_search","sources":[],"maxToolCalls":1,"thinking":"disabled"}',
     UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE provider = provider;
