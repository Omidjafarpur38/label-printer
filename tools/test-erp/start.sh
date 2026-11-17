#!/bin/bash
# Starts the isolated test ERP on loopback only. Synthetic, local-only values.
set -u
: "${ERP_DIR:?set ERP_DIR to a jeans-production-erp checkout}"
cd "$ERP_DIR/backend" || exit 1
export DATABASE_URL=postgresql://app_user:app_test_pw@127.0.0.1:5432/lp_test_erp
export GOVERNANCE_DATABASE_URL=postgresql://governance_admin:gov_test_pw@127.0.0.1:5432/lp_test_erp
export REDIS_URL=redis://127.0.0.1:6379/5
export JWT_SECRET=local-test-only-jwt-secret-0123456789abcdef0123456789
export ADMIN_PASSWORD=LocalTest-Admin-Pass-1
export PORT=4100 HOST=127.0.0.1 NODE_ENV=development LOG_LEVEL=warn BACKGROUND_JOBS=off PUBLIC_APP_URL=http://127.0.0.1:4100
unset MIGRATION_DATABASE_URL HTTPS_PROXY HTTP_PROXY https_proxy http_proxy
exec node dist/server.js
