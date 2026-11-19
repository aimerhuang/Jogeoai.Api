-- 048_team_monitoring_defaults.sql
-- 团队默认监控配置与品牌跟随标记（Trellis 11-11-team-monitoring-config-api，design.md §2）。
-- 只做加法：
--   新表 tenant_monitoring_defaults（每个团队一行，带 config_version）与 tenant_monitoring_default_cells
--     （单元格 = provider × engine × 回答模式 × 采集频率；answer_mode 空串 = 该 provider 的默认模式，与 site_targets.answer_mode IS NULL 对应）；
--   site_brand_settings 新增 monitoring_follows_default TINYINT(1) NOT NULL DEFAULT 1（1 = 跟随团队默认，0 = 单独配置）。
-- 本迁移不对 site_targets 做任何写入：所有品牌当前的采集行为不变。归档品牌与国内网页采集目标不受影响。
-- 依赖 005（sites / site_brand_settings / site_targets）与 047（site_targets.answer_mode / frequency）；
-- 手动执行于 001–047 之后，须用 root，mysql 客户端须加 --default-character-set=utf8mb4；API / Worker 启动不会执行本文件。
-- 发布顺序：先执行本迁移，再发布 API（Worker 不读新表与新列）。未执行时团队接口返回 503 TEAM_MONITORING_SCHEMA_NOT_MIGRATED。
--
-- 存量归类（父任务 D3 / D4；每个团队独立计算，只处理「存在标记为 NULL 的品牌」且「还没有默认配置行」的团队）：
--   「已配置品牌」= sites.status = 'active' 且至少有 1 行启用的、可参与每日监控的 site_targets。
--   可参与每日监控的 Provider = 已注册的 API 回答 Provider（AnswerProviderDefinition.All / IsRegistered 的当前集合）：
--     cloro、doubao_api、deepseek_api、packy_qwen、packy_glm、packy_kimi、packy_yuanbao（即除 domestic_collector 外的全部现有 Provider）。
--   品牌签名 = 启用的可监控行按 provider|engine|answer_mode(NULL→'')|frequency 去重后排序拼接，忽略国家 / 语言
--     （同一模型多个地区、频率相同的行折叠为一个单元格）。
--   同一 provider + engine + 模式出现两种频率的签名无法表示为团队默认配置（每个单元格只有一个频率），这类签名不参与竞选默认配置，
--     其品牌一律标为单独配置。
--   0 个已配置品牌：不写默认配置行（读取时为虚拟基线），启用品牌全部标 1。
--   恰好 1 个：默认配置 = 该品牌的单元格，该品牌标 1（签名无法表示时默认 = 基线、该品牌标 0）。
--   ≥2 个：按签名分组；成员最多的可表示组（并列时取组内最早 sites.created_at 最早的组）成员 ≥2 时，默认 = 该组配置、组内标 1、
--     其余已配置品牌标 0；否则默认 = 基线（cloro | chatgpt | 默认模式 | daily），已配置品牌全部标 0。
--   启用但未配置的品牌标 1；归档品牌标 0。写入的默认配置 config_version = 1、updated_by = NULL。
-- 可重放：表用 IF NOT EXISTS；列按 information_schema 判断后再 ALTER；归类只处理仍为 NULL 的品牌；列已 NOT NULL 时跳过最后一步。
-- 跟随标记分三步加，任一步失败后重跑可继续：1) 加可空列；2) 一个事务内归类回填；3) 剩余 NULL 置 1 后改为 NOT NULL DEFAULT 1。
-- 回滚：见 db/migrations/rollback/048_team_monitoring_defaults_rollback.sql（只删除本迁移新增的两张表和一列，site_targets 不变）。

CREATE TABLE IF NOT EXISTS tenant_monitoring_defaults (
  tenant_id      BINARY(16)   NOT NULL,
  config_version INT UNSIGNED NOT NULL DEFAULT 1,
  updated_by     BINARY(16)   NULL,
  created_at     DATETIME(6)  NOT NULL,
  updated_at     DATETIME(6)  NOT NULL,
  PRIMARY KEY (tenant_id),
  CONSTRAINT fk_tmd_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS tenant_monitoring_default_cells (
  tenant_id   BINARY(16)  NOT NULL,
  provider    VARCHAR(64) NOT NULL,
  engine      VARCHAR(64) NOT NULL,
  answer_mode VARCHAR(16) NOT NULL DEFAULT '' COMMENT '空串=provider 默认模式',
  frequency   VARCHAR(16) NOT NULL DEFAULT 'daily',
  PRIMARY KEY (tenant_id, provider, engine, answer_mode),
  CONSTRAINT fk_tmdc_default FOREIGN KEY (tenant_id) REFERENCES tenant_monitoring_defaults (tenant_id) ON DELETE CASCADE,
  CONSTRAINT ck_tmdc_answer_mode CHECK (answer_mode IN ('', 'fast', 'deep')),
  CONSTRAINT ck_tmdc_frequency CHECK (frequency IN ('twice_daily','daily','every_other_day','weekly'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_048_team_monitoring_defaults//
CREATE PROCEDURE jogeoai_048_team_monitoring_defaults()
BEGIN
    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        ROLLBACK;
        DROP TEMPORARY TABLE IF EXISTS tmp048_eligible;
        DROP TEMPORARY TABLE IF EXISTS tmp048_sig;
        DROP TEMPORARY TABLE IF EXISTS tmp048_choice;
        RESIGNAL;
    END;

    IF DATABASE() IS NULL
        OR NOT EXISTS (SELECT 1 FROM information_schema.tables
            WHERE table_schema = DATABASE() AND table_name = 'site_brand_settings')
        OR NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
            AND table_name = 'site_targets' AND column_name = 'frequency')
        OR NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
            AND table_name = 'site_targets' AND column_name = 'answer_mode') THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '048 requires a selected database and migrations 005 and 047';
    END IF;

    -- 1. 跟随标记先以可空列加入（NULL = 尚未归类）。
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'site_brand_settings' AND column_name = 'monitoring_follows_default') THEN
        ALTER TABLE site_brand_settings ADD COLUMN monitoring_follows_default TINYINT(1) NULL
            COMMENT '1=跟随团队默认监控配置，0=单独配置';
    END IF;

    -- 2. 归类回填（一个事务）。签名最多 20 个单元格，调大会话的 GROUP_CONCAT 上限以免截断。
    SET SESSION group_concat_max_len = 65536;
    START TRANSACTION;

    DROP TEMPORARY TABLE IF EXISTS tmp048_eligible;
    CREATE TEMPORARY TABLE tmp048_eligible (tenant_id BINARY(16) NOT NULL PRIMARY KEY) ENGINE=InnoDB;
    INSERT INTO tmp048_eligible (tenant_id)
    SELECT DISTINCT b.tenant_id FROM site_brand_settings b
    WHERE b.monitoring_follows_default IS NULL
      AND NOT EXISTS (SELECT 1 FROM tenant_monitoring_defaults d WHERE d.tenant_id = b.tenant_id);

    -- 已配置品牌及其签名；valid = 每个 provider + engine + 模式只有一个频率（可表示为团队默认单元格）。
    DROP TEMPORARY TABLE IF EXISTS tmp048_sig;
    CREATE TEMPORARY TABLE tmp048_sig (
        tenant_id  BINARY(16)  NOT NULL,
        site_id    BINARY(16)  NOT NULL PRIMARY KEY,
        created_at DATETIME(6) NOT NULL,
        sig        TEXT        NOT NULL,
        valid      TINYINT(1)  NOT NULL
    ) ENGINE=InnoDB;
    INSERT INTO tmp048_sig (tenant_id, site_id, created_at, sig, valid)
    SELECT s.tenant_id, s.id, s.created_at,
           GROUP_CONCAT(DISTINCT CONCAT_WS('|', t.provider, t.engine, COALESCE(t.answer_mode, ''), t.frequency)
               ORDER BY CONCAT_WS('|', t.provider, t.engine, COALESCE(t.answer_mode, ''), t.frequency) SEPARATOR ','),
           COUNT(DISTINCT CONCAT_WS('|', t.provider, t.engine, COALESCE(t.answer_mode, '')))
               = COUNT(DISTINCT CONCAT_WS('|', t.provider, t.engine, COALESCE(t.answer_mode, ''), t.frequency))
    FROM sites s
    JOIN tmp048_eligible e ON e.tenant_id = s.tenant_id
    JOIN site_targets t ON t.tenant_id = s.tenant_id AND t.site_id = s.id
    WHERE s.status = 'active' AND t.enabled = TRUE
      AND t.provider IN ('cloro', 'doubao_api', 'deepseek_api', 'packy_qwen', 'packy_glm', 'packy_kimi', 'packy_yuanbao')
    GROUP BY s.tenant_id, s.id, s.created_at;

    -- 每个有已配置品牌的团队选出默认配置：rep_site 为被选中组的代表品牌；为 NULL 表示默认 = 基线。
    DROP TEMPORARY TABLE IF EXISTS tmp048_choice;
    CREATE TEMPORARY TABLE tmp048_choice (
        tenant_id BINARY(16) NOT NULL PRIMARY KEY,
        rep_site  BINARY(16) NULL,
        sig       TEXT       NULL
    ) ENGINE=InnoDB;
    INSERT INTO tmp048_choice (tenant_id, rep_site, sig)
    SELECT r.tenant_id,
           CASE WHEN r.valid = 1 AND (r.configured = 1 OR r.members >= 2) THEN r.rep_site END,
           CASE WHEN r.valid = 1 AND (r.configured = 1 OR r.members >= 2) THEN r.sig END
    FROM (
        SELECT g.tenant_id, g.sig, g.valid, g.members, g.rep_site,
               SUM(g.members) OVER (PARTITION BY g.tenant_id) AS configured,
               ROW_NUMBER() OVER (PARTITION BY g.tenant_id
                   ORDER BY g.valid DESC, g.members DESC, g.first_created, g.rep_site) AS rn
        FROM (
            SELECT tenant_id, sig, MIN(valid) AS valid, COUNT(*) AS members,
                   MIN(created_at) AS first_created, MIN(site_id) AS rep_site
            FROM tmp048_sig GROUP BY tenant_id, sig
        ) g
    ) r
    WHERE r.rn = 1;

    INSERT INTO tenant_monitoring_defaults (tenant_id, config_version, updated_by, created_at, updated_at)
    SELECT c.tenant_id, 1, NULL, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6) FROM tmp048_choice c;

    -- 选中组的单元格取自代表品牌的启用可监控行（同一模型多地区时 DISTINCT 折叠）。
    INSERT INTO tenant_monitoring_default_cells (tenant_id, provider, engine, answer_mode, frequency)
    SELECT DISTINCT c.tenant_id, t.provider, t.engine, COALESCE(t.answer_mode, ''), t.frequency
    FROM tmp048_choice c
    JOIN site_targets t ON t.site_id = c.rep_site
    WHERE c.rep_site IS NOT NULL AND t.enabled = TRUE
      AND t.provider IN ('cloro', 'doubao_api', 'deepseek_api', 'packy_qwen', 'packy_glm', 'packy_kimi', 'packy_yuanbao');

    -- 没有多数派（或唯一已配置品牌的签名无法表示）：默认 = 基线，与 PromptMonitoringDefaults.Target 选的模型一致。
    INSERT INTO tenant_monitoring_default_cells (tenant_id, provider, engine, answer_mode, frequency)
    SELECT c.tenant_id, 'cloro', 'chatgpt', '', 'daily' FROM tmp048_choice c WHERE c.rep_site IS NULL;

    UPDATE site_brand_settings b
    JOIN tmp048_eligible e ON e.tenant_id = b.tenant_id
    JOIN sites s ON s.tenant_id = b.tenant_id AND s.id = b.site_id
    LEFT JOIN tmp048_sig g ON g.site_id = b.site_id
    LEFT JOIN tmp048_choice c ON c.tenant_id = b.tenant_id
    SET b.monitoring_follows_default = CASE
            WHEN s.status <> 'active' THEN 0
            WHEN g.site_id IS NULL THEN 1
            WHEN c.sig IS NOT NULL AND g.sig = c.sig THEN 1
            ELSE 0
        END
    WHERE b.monitoring_follows_default IS NULL;

    COMMIT;
    DROP TEMPORARY TABLE IF EXISTS tmp048_eligible;
    DROP TEMPORARY TABLE IF EXISTS tmp048_sig;
    DROP TEMPORARY TABLE IF EXISTS tmp048_choice;

    -- 3. 剩余 NULL（第 2 步之后才加入、且团队已有默认配置的品牌）置 1，再改为 NOT NULL DEFAULT 1；新品牌由列默认值得到 1。
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'site_brand_settings' AND column_name = 'monitoring_follows_default' AND is_nullable = 'YES') THEN
        UPDATE site_brand_settings SET monitoring_follows_default = 1 WHERE monitoring_follows_default IS NULL;
        ALTER TABLE site_brand_settings MODIFY COLUMN monitoring_follows_default TINYINT(1) NOT NULL DEFAULT 1
            COMMENT '1=跟随团队默认监控配置，0=单独配置';
    END IF;
END//
DELIMITER ;
CALL jogeoai_048_team_monitoring_defaults();
DROP PROCEDURE jogeoai_048_team_monitoring_defaults;
