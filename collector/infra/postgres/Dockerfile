FROM postgres:18.0-bookworm@sha256:3f55f8895c4ed50603e2fbdfc72fffeeaba3173321fee5cb825bbbeb30d9d854

COPY --chmod=0755 infra/postgres/init-app-role.sh /docker-entrypoint-initdb.d/10-init-app-role.sh
