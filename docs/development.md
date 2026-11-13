# Local signal lab and diagnostics

Use the installed .NET 10 SDK. No Azure credentials are needed for local checks:

```bash
dotnet test tests/Sample.Tests.csproj --no-restore --verbosity minimal
dotnet publish BuilderApplicationInsightsSample.csproj --no-restore -c Release -o bin/sample-publish
TELEMETRY_MODE=off dotnet bin/sample-publish/BuilderApplicationInsightsSample.dll
```

The server binds port 8080 on all interfaces; use an isolated environment. Open `http://127.0.0.1:8080/`. The banner explicitly says export is off. Run each scenario once: 200 success, a fixed 600 ms delay, a handled synthetic 500, and a real HTTP call to the fixed loopback catalog. None verifies cloud ingestion.

For existing browser tests, point `PLAYWRIGHT_MODULE` to the installed Playwright module and `EDGE_EXECUTABLE` to installed Edge, then:

```bash
SAMPLE_URL=http://127.0.0.1:8080 node tests/ui.mjs
```

The browser tests check real local HTTP, copy-ID behavior, explicit failures, keyboard controls and responsive layout. They are not live provider tests.

## Privacy and bounds

The server accepts only four named synthetic scenarios, no query/body. It permits 12 attempts/minute and two concurrent requests, with a five-second browser timeout and two-second loopback dependency timeout. Retry is explicit.

Hosted export explicitly uses system-assigned `ManagedIdentityCredential`; there is no Azure CLI/environment-key fallback. The component disables local auth. Destination connection strings are not credentials and never go to the UI.

Safe processors remove caller URLs, query, host, headers, body, baggage, tracestate and unrelated tags. Request URLs use `sample.invalid`; route templates and fixed synthetic messages remain. Exceptions omit stacks. Static assets, health, setup/readiness and SDK identity/export traffic are excluded. Live Metrics, offline storage, optional SDK stats and runtime metrics collection are disabled.

`/api/diagnostics` exposes only fixed counters/categories, no SDK payloads or endpoints. Credential/HTTP failure categories support recovery, not verified delivery. `/api/setup` has a fixed allowlist; `/ready` reflects recent transport acceptance (five minutes) while `ingestionVerified` stays false. Only [workspace operation queries](../README.md#setup) verify actual records.

Missing settings/invalid mode/deadline leave the guide available with export off. Expired configured windows block demo POSTs with 410; they are not renewed automatically. A restart cannot extend an absolute deadline.

## Diagnosing missing signals

Check the active version first, not just desired variables. Verify the connection string belongs to your component, the same system identity has **Monitoring Metrics Publisher** there, local auth is disabled and the ingestion endpoint is reachable. Allow propagation/ingestion delay before an explicit retry.

A 403 can be an identity/RBAC/access-rule issue; token success alone does not prove ingestion. SDK warning event IDs omit raw details by design, so do not infer precise networking/RBAC causes where the SDK did not reliably identify them. Query permissions are separate from ingest permissions.

Legacy Application Insights Logs aliases are `requests`, `exceptions`, `traces`, `dependencies`, with `operation_Id`; the workspace query in the README uses `App*` tables and `OperationId`. Verify parent/correlation IDs as well as rows. A partial HTTP 206 is not complete delivery.

Historical hosted captures in `screenshots/hosted-*.png` show an earlier design and expired windows. They are not evidence for this UI or a current deployment; they were not renewed.

## Template boundaries

Use `infra/main.bicep` for **monitoring only** after app deployment. `infra/sample.bicep` is a separate new/source-less app template and explicitly cannot update an existing app; do not apply it to the recommended journey. No template/Portal provisioning integration or runtime picker was added.

The approved target authentication child is `/authConfigs/default`. The inspected local Builder CLI 0.0.2 bundle still contains `/authConfigs/current`; its auth writes need correction before use. The recommended journey does not change platform auth or copy either historical REST example. Visitor auth is separate from exporter identity.
