#!/bin/bash
# Throw-away local database for the isolated test ERP. Synthetic data only. Never point this at a real server.
set -u
: "${ERP_DIR:?set ERP_DIR to a jeans-production-erp checkout}"
cd "$ERP_DIR/backend" || exit 1
service postgresql start >/dev/null 2>&1; service redis-server start >/dev/null 2>&1; sleep 3
su postgres -c "psql -qAt -c \"ALTER ROLE postgres PASSWORD 'postgres'\""
su postgres -c "psql -qAt -c 'DROP DATABASE IF EXISTS lp_test_erp'"; su postgres -c "psql -qAt -c 'CREATE DATABASE lp_test_erp'"
npm ci --no-audit --no-fund >/dev/null 2>&1 || { echo "npm ci failed"; exit 1; }
npm run build >/dev/null 2>&1 || { echo "build failed"; exit 1; }
python3 -I - "$ERP_DIR/backend" <<'PY'
import json, subprocess, sys
root = sys.argv[1]
journal = json.load(open(root + '/migrations/meta/_journal.json'))
failed = []
for entry in journal['entries']:
    path = '%s/migrations/%s.sql' % (root, entry['tag'])
    r = subprocess.run(['su', 'postgres', '-c', 'psql -q -v ON_ERROR_STOP=1 -d lp_test_erp -f ' + path], capture_output=True, text=True)
    if r.returncode != 0: failed.append(entry['tag'])
print('migrations: %d applied, failed: %s' % (len(journal['entries']) - len(failed), failed))
PY
su postgres -c "psql -qAt -d lp_test_erp -c \"ALTER ROLE app_user PASSWORD 'app_test_pw' LOGIN; ALTER ROLE governance_admin PASSWORD 'gov_test_pw' LOGIN;\""
su postgres -c "psql -qAt -d lp_test_erp" <<'SQL'
insert into platform_settings("key","value","updatedAt") values ('feature.label.print.v2','{"mode":"on","tenantIds":[]}'::jsonb, 1)
  on conflict ("key") do update set "value" = excluded."value";
SQL
echo "label tables: $(su postgres -c "psql -qAt -d lp_test_erp -c \"select count(*) from information_schema.tables where table_name like 'label_%'\"")"
