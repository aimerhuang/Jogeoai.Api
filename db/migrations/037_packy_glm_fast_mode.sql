-- 037_packy_glm_fast_mode.sql
-- 让 PackyAPI 智谱清言（packy_glm）贴近网页端默认：模型 glm-5.3-flash，思考强度“快速”。
-- glm-5.3-flash 在 zai-officially 分组里不认 thinking:{type:"disabled"}（2026-10-29 实测仍有约 4.6k 推理 token、186 秒），
-- 改为 Responses 的 reasoning:{effort:"low"}（实测 43 秒、87 个推理 token、3 次搜索、22 个来源 URL、正文完整）。
-- options 新增 "reasoningEffort":"low"；设置后适配器发送 reasoning.effort，不再发送 thinking。
-- 依赖 036；手动执行，API / Worker 启动不会执行本文件。必须使用 --default-character-set=utf8mb4。
-- 只更新仍是 036 种子模型 glm-5.2 的行，管理员改过的模型不覆盖；凭据、地址、开关与超时保持不变；可重复执行。
-- 回滚：UPDATE answer_provider_settings SET model = 'glm-5.2',
--   options = '{"requestMode":"web_search","sources":[],"maxToolCalls":5,"reasoningEffort":null,"thinking":"disabled"}',
--   updated_at = UTC_TIMESTAMP(6) WHERE provider = 'packy_glm' AND model = 'glm-5.3-flash';
-- 回滚只换回模型与选项，已入库的 Check 保留各自的 model_version。

UPDATE answer_provider_settings
SET model = 'glm-5.3-flash',
    options = '{"requestMode":"web_search","sources":[],"maxToolCalls":5,"reasoningEffort":"low","thinking":"disabled"}',
    updated_at = UTC_TIMESTAMP(6)
WHERE provider = 'packy_glm' AND model = 'glm-5.2';
