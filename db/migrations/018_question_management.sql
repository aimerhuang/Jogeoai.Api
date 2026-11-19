-- 018_question_management.sql
-- 提问管理模块：支持分组管理的提问库

-- 提问分组表
CREATE TABLE IF NOT EXISTS question_groups (
    id BINARY(16) NOT NULL PRIMARY KEY COMMENT '分组ID',
    site_id BINARY(16) NOT NULL COMMENT '品牌站点ID',
    name VARCHAR(200) NOT NULL COMMENT '分组名称',
    description TEXT COMMENT '分组描述',
    display_order INT NOT NULL DEFAULT 0 COMMENT '显示顺序',
    is_active BOOLEAN NOT NULL DEFAULT TRUE COMMENT '是否启用',
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) COMMENT '创建时间',
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6) COMMENT '更新时间',

    INDEX idx_site_active (site_id, is_active, display_order),

    CONSTRAINT fk_question_groups_site
        FOREIGN KEY (site_id) REFERENCES sites(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
COMMENT='提问分组表';

-- 提问表
CREATE TABLE IF NOT EXISTS questions (
    id BINARY(16) NOT NULL PRIMARY KEY COMMENT '提问ID',
    group_id BINARY(16) NOT NULL COMMENT '所属分组ID',
    site_id BINARY(16) NOT NULL COMMENT '品牌站点ID',
    question_text TEXT NOT NULL COMMENT '提问内容',

    -- 监控配置
    target_platforms JSON COMMENT '目标平台列表，如 ["ChatGPT", "Perplexity"]',
    priority INT NOT NULL DEFAULT 0 COMMENT '优先级/排名',
    expected_reach_rate DECIMAL(5,2) COMMENT '预期投及率（百分比）',

    -- 状态
    is_enabled BOOLEAN NOT NULL DEFAULT TRUE COMMENT '是否启用',
    display_order INT NOT NULL DEFAULT 0 COMMENT '分组内显示顺序',

    -- 统计字段（运行时更新）
    actual_reach_rate DECIMAL(5,2) COMMENT '实际投及率（百分比）',
    trend_direction ENUM('up', 'down', 'stable', 'unknown') DEFAULT 'unknown' COMMENT '趋势方向',
    last_monitored_at DATETIME(6) COMMENT '最后监控时间',

    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) COMMENT '创建时间',
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6) COMMENT '更新时间',

    INDEX idx_group (group_id, display_order),
    INDEX idx_site_enabled (site_id, is_enabled),
    INDEX idx_priority (site_id, priority),

    CONSTRAINT fk_questions_group
        FOREIGN KEY (group_id) REFERENCES question_groups(id) ON DELETE CASCADE,
    CONSTRAINT fk_questions_site
        FOREIGN KEY (site_id) REFERENCES sites(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
COMMENT='提问表';
