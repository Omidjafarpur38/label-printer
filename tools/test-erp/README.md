# Isolated test ERP

Runs the real ERP backend (Fastify + PostgreSQL + Redis) from a local checkout of `jeans-production-erp`
on loopback, with a throw-away database and synthetic values, so that the agent's HTTP transport can be
tested against real ERP code. Nothing here touches a deployed ERP; the integration tests refuse any
non-loopback origin.

```bash
ERP_DIR=/path/to/jeans-production-erp tools/test-erp/setup.sh   # creates database lp_test_erp, builds, migrates
ERP_DIR=/path/to/jeans-production-erp tools/test-erp/start.sh   # serves http://127.0.0.1:4100
```

```bash
redis-cli -n 5 flushdb   # clears rate-limit counters between runs (/pair allows 5 per minute per IP)
LP_TEST_ERP_ORIGIN=http://127.0.0.1:4100 LP_TEST_ERP_PASSWORD=LocalTest-Admin-Pass-1 \
  dotnet run --project tests/LabelPrinter.Agent.Core.Tests -- RealErp
```

Notes:
- Requires a local PostgreSQL 16 and Redis, Node 22, and `su postgres` rights (a disposable container).
- Migrations are applied one file at a time in journal order. Two unrelated data-repair migrations
  (`0137`, `0143`) fail on an empty database, as the ERP's own notes describe; the label tables are unaffected.
- The feature flag `label.print.v2` is switched on in this database only.
- All passwords in these scripts are synthetic and exist only in the local test database.
- Without `LP_TEST_ERP_ORIGIN` the integration tests report as skipped. They do not run in CI: the ERP
  repository is private and is not available to this repository's workflows.
