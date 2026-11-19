-- 046_packy_yuanbao_provider.sql
-- 为 answer_provider_settings（025）新增经 PackyAPI 接入的元宝（腾讯混元 hy4，hunyuan-officially 分组）默认行：
--   packy_yuanbao  元宝  OpenAI Responses（POST /v1/responses，流式）+ web_search + max_tool_calls（默认 3）
-- 2026-11-08 实测：只有 Responses + web_search 真正检索（chat/completions 的混元增强参数被网关丢弃，Messages 不执行服务端搜索）；
-- 该渠道不返回检索结果与 url_citation，来源只有模型打开过的网页（visit 动作）；思考关不掉，限 3 次搜索单题仍约 5 分钟。
-- 依赖 025（表）；与 036 互不依赖。手动执行，API / Worker 启动不会执行本文件。
-- 只插入种子，不改表结构，可重复执行。执行时必须使用 --default-character-set=utf8mb4。
-- 种子未启用、无凭据：系统管理员通过 PUT /api/v1/providers/packy/yuanbao/settings 保存 Key 并启用，
-- 或按本机约定直接写库；凭据不进入本文件。
-- 回滚（先在品牌目标中停用 packy_yuanbao，否则每日时隙整轮 TARGET_UNAVAILABLE 跳过）：
--   DELETE FROM answer_provider_settings WHERE provider = 'packy_yuanbao';
-- 回滚后目录中的元宝 API Target 为 unavailable；已入库的 Check 保留，其他 Provider 不受影响。

-- ON DUPLICATE KEY UPDATE provider = provider 保证重放不会覆盖管理员已经保存的地址、模型、凭据或开关。
INSERT INTO answer_provider_settings
    (provider, api_url, model, credential, credential_hint, enabled, timeout_seconds, options, created_at, updated_at)
VALUES
    ('packy_yuanbao', 'https://www.packyapi.ai/v1/responses', 'hy4',
     NULL, NULL, 0, 540, '{"requestMode":"web_search","sources":[],"maxToolCalls":3,"thinking":"disabled"}',
     UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE provider = provider;
