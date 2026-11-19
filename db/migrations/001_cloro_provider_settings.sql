CREATE TABLE IF NOT EXISTS cloro_provider_settings (
    id TINYINT UNSIGNED NOT NULL PRIMARY KEY,
    api_key TEXT NOT NULL,
    api_url VARCHAR(2048) NOT NULL,
    enabled BOOLEAN NOT NULL DEFAULT TRUE,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    CONSTRAINT ck_cloro_provider_settings_singleton CHECK (id = 1)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
