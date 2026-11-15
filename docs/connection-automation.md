# Configure telemetry with CLI or ARM

Run from your fork's root with explicit subscription and a dedicated DEV/TEST group.
Review workspace/component charges, caps and an owner-approved expiry first.

## Provision a new source-less app with Bicep

Set `SUBSCRIPTION`, `RG`, unique new `APP`, `WORKSPACE`, `INSIGHTS` and future UTC `EXPIRES_AT`.

```bash
az deployment group create --subscription "$SUBSCRIPTION" -g "$RG" \
  --name telemetry-sample --template-file infra/sample.bicep \
  --parameters appName="$APP" workspaceName="$WORKSPACE" \
    applicationInsightsName="$INSIGHTS" expiresAt="$EXPIRES_AT"
az rest --method get --url "https://management.azure.com/subscriptions/$SUBSCRIPTION/resourceGroups/$RG/providers/Microsoft.Web/builderApps/$APP?api-version=2026-09-01-preview" \
  --query '{identity:identity,identityUpdate:properties.identityUpdate}'
```

This reuses `infra/main.bicep` for monitoring and creates the SAMI app, 0–1 scaling, expiry/telemetry controls and component-scoped Monitoring Metrics Publisher.
It disables local ingestion auth. Do not apply it over the existing app from the Portal README journey: full PUT can replace desired source/settings.
Wait for committed identity/no pending operation (`builder app status`) and role propagation.
With ARM-only tooling, GET the app ARM URL below and inspect `identity` and `properties.identityUpdate` instead.
Copy root `connection.json` into private ignored `.local/connection.json` and fill the component resource ID and its non-secret routing connection string.
Prepare it with `umask 077; mkdir -p .local; cp connection.json .local/connection.json`.
Keep `APPLICATIONINSIGHTS_CONNECTION_STRING` in Connections, not also in Variables.

## Add with native Builder CLI

Requires a release listing `app connections` in help; otherwise use ARM below.

```bash
builder login
builder app connections list --name "$APP" -s "$SUBSCRIPTION" -g "$RG" --json
builder app connections add --name "$APP" -s "$SUBSCRIPTION" -g "$RG" --connection-json '@.local/connection.json'
builder app connections status --name "$APP" -s "$SUBSCRIPTION" -g "$RG" --connection-name monitoring
```

No duplicate grant is needed. `update --connection-json` accepts one complete replacement; `remove --connection-name monitoring` changes pending configuration only.
The app's explicit credential-aware OpenTelemetry exporter still authenticates and sends telemetry.

## Add with ARM instead

Use Bash/`jq`; keep resource identifiers/routing metadata private in ignored `.local/`.

```bash
APP_ARM="https://management.azure.com/subscriptions/$SUBSCRIPTION/resourceGroups/$RG/providers/Microsoft.Web/builderApps/$APP"
APP_URL="$APP_ARM?api-version=2026-09-01-preview"
az rest --method get --url "$APP_URL" > .local/app.json
ETAG="$(jq -er .etag .local/app.json)"
jq --slurpfile next .local/connection.json '
  {properties:{versionConfiguration:{connections:
    ((.properties.versionConfiguration.connections // [] |
      map(select((.name|ascii_downcase) != ($next[0].name|ascii_downcase)))) + $next)}}}
' .local/app.json > .local/update.json
az rest --method patch --url "$APP_URL" --headers "If-Match=$ETAG" --body @.local/update.json
az rest --method get --url "$APP_URL" --query properties.versionConfiguration.connections
```

The complete list preserves unrelated connections; omitted manual values/source/scaling remain unchanged.
On 412 refresh/reconcile; do not remove the guard. Existing apps must remove conflicting manual routing without an intermediate deployment.

## Deploy and verify correlated ingestion

```bash
git archive --format=zip --output=../telemetry-source.zip HEAD
builder app deploy --name "$APP" -s "$SUBSCRIPTION" -g "$RG" \
  --source-archive ../telemetry-source.zip --manifest ./builder.yaml --json
```

Wait for terminal success. Run one bounded synthetic scenario and use README KQL to find its exact correlation ID across request/trace/exception/dependency.
Transport acceptance, health and an Active diagram badge do not prove ingestion.
Do not renew expired windows or widen permissions to make telemetry appear.
