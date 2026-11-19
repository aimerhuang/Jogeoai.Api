-- 030_deepseek_answer_provider.sql
-- 为 answer_provider_settings（025）新增 DeepSeek 官方 API Provider 的默认行：deepseek_api，
-- Anthropic 兼容 Messages API（POST /anthropic/v1/messages）+ 服务端 web_search 工具。
-- 依赖 025（answer_provider_settings 表必须已存在）；手动执行于 001–029 之后，API / Worker 启动不会执行本文件。
-- 只插入种子，不改表结构，可重复执行。
-- 执行时必须使用 --default-character-set=utf8mb4，否则非 ASCII 注释会写成乱码。
-- 种子未启用、无凭据：系统管理员通过 PUT /api/v1/providers/deepseek/settings 保存 Key 并启用；凭据不进入本文件。
-- options 为 DeepSeek 专属 JSON：{"requestMode":"web_search","sources":[],"maxToolCalls":1-10,"thinking":"disabled"|"enabled"}；
-- 默认 deepseek-flash + 关闭思考，贴近网页端「智能搜索开、深度思考关」。
-- 回滚：DELETE FROM answer_provider_settings WHERE provider = 'deepseek_api';
-- 回滚后目录中的 deepseek_api Target 为 unavailable，手动 Run 返回 503 DEEPSEEK_NOT_CONFIGURED，
-- 每日时隙以 TARGET_UNAVAILABLE 跳过；已入库的 Check 保留，豆包与 Cloro 链路不受影响。

-- ON DUPLICATE KEY UPDATE provider = provider 保证重放不会覆盖管理员已经保存的地址、模型、凭据或开关。
INSERT INTO answer_provider_settings
    (provider, api_url, model, credential, credential_hint, enabled, timeout_seconds, options, created_at, updated_at)
VALUES
    ('deepseek_api', 'https://api.deepseek.com/anthropic/v1/messages', 'deepseek-flash',
     NULL, NULL, 0, 240, '{"requestMode":"web_search","sources":[],"maxToolCalls":5,"thinking":"disabled"}',
     UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE provider = provider;
