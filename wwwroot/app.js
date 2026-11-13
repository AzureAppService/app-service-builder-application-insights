const status = document.querySelector("#request-status");
const buttons = [...document.querySelectorAll("[data-scenario]")];
let operationId = "";

async function configure() {
  const environment = document.querySelector("#environment");
  try {
    const response = await fetch("/api/config");
    if (!response.ok) throw new Error("Sample configuration is unavailable.");
    const config = await response.json();
    environment.textContent =
      config.mode +
      (config.expiresAt
        ? ` Test window ends ${new Date(config.expiresAt).toLocaleString()}.`
        : "");
  } catch {
    environment.textContent =
      "Configuration unavailable. No ingestion claim can be made.";
  }
}

for (const button of buttons) {
  button.addEventListener("click", async () => {
    buttons.forEach((control) => {
      control.disabled = true;
    });
    status.textContent = "Sending one request...";
    document.querySelector("#result").hidden = true;
    document.querySelector("#empty").hidden = false;
    operationId = "";
    document.querySelector("#copy").disabled = true;
    const abort = new AbortController();
    const timeout = setTimeout(() => abort.abort(), 5000);
    try {
      const response = await fetch(`/api/demo/${button.dataset.scenario}`, {
        method: "POST",
        signal: abort.signal,
      });
      const body = await response.json();
      document.querySelector("#empty").hidden = true;
      document.querySelector("#result").hidden = false;
      document.querySelector("#http-status").textContent = String(
        response.status,
      );
      document.querySelector("#duration").textContent =
        typeof body.durationMs === "number" ? `${body.durationMs} ms` : "--";
      document.querySelector("#scenario").textContent =
        body.scenario ?? button.dataset.scenario;
      document.querySelector("#message").textContent = body.message;
      operationId = body.operationId ?? "";
      document.querySelector("#operation-id").textContent =
        operationId || "No demo operation ID";
      document.querySelector("#copy").disabled = !operationId;
      status.textContent =
        response.status === 500 && body.synthetic
          ? "Intentional 500 received. Verify its exception in Azure."
          : response.ok
            ? "Request completed. Ingestion is not yet verified."
            : `HTTP ${response.status}: ${body.message}`;
    } catch {
      status.textContent =
        "Request failed or timed out. No result was recorded; retry explicitly.";
    } finally {
      clearTimeout(timeout);
      buttons.forEach((control) => {
        control.disabled = false;
      });
      button.focus();
    }
  });
}

document.querySelector("#copy").addEventListener("click", async () => {
  try {
    await navigator.clipboard.writeText(operationId);
    status.textContent = "Operation ID copied.";
  } catch {
    status.textContent =
      "Copy unavailable. Select the operation ID and copy it manually.";
  }
});

configure();

async function setup() {
  const control = document.querySelector("#setup-check");
  const result = document.querySelector("#setup-status");
  control.disabled = true;
  const messages = {
    configuration_missing: "Azure export is off or its settings are missing. Create monitoring, connect identity and deploy the same app.",
    configuration_invalid: "Export configuration is invalid. Check the mode, connection string and UTC test deadline, then save and deploy.",
    demo_expired: "The approved demo window has ended. Do not renew it without the resource owner's approval.",
    identity_failed: "App identity could not authenticate. Check managed identity and the active deployment.",
    ingestion_denied: "Azure denied ingestion. Check Monitoring Metrics Publisher on the component and its access rules.",
    ingestion_limit: "Azure reported a usage limit. Review quota before an explicit retry.",
    ingestion_partial: "Azure accepted only part of an export. Check sanitized diagnostics and verify records in Logs.",
    ingestion_failed: "Azure rejected an export. Check the destination and service status.",
    export_warning: "The exporter reported a warning. Check identity, network and destination; no connectivity claim can be made.",
    ingestion_accepted: "Azure recently accepted an export. Delivery of your operation is still unverified: check its ID in workspace Logs.",
    not_checked: "Exporter configured, but delivery is not verified. Run one synthetic request, then find its operation ID in Logs."
  };
  try {
    const response = await fetch("/api/setup", { signal: AbortSignal.timeout(5000) });
    if (!response.ok) throw new Error();
    result.textContent = messages[(await response.json()).code] || "Service status unavailable. Follow setup and verify in Logs.";
  } catch {
    result.textContent = "Service status could not be read. Follow setup; Azure ingestion is not verified.";
  } finally {
    control.disabled = false;
  }
}
document.querySelector("#setup-check").addEventListener("click", setup);
setup();

document.querySelector("#health").addEventListener("click", async (event) => {
  const button = event.currentTarget;
  const result = document.querySelector("#health-status");
  button.disabled = true;
  result.textContent = "Checking /health...";
  const abort = new AbortController();
  const timeout = setTimeout(() => abort.abort(), 5000);
  try {
    const response = await fetch("/health", { signal: abort.signal });
    if (!response.ok) throw new Error("Health unavailable");
    result.textContent = `/health · HTTP ${response.status} · ${await response.text()}. Process ready; Azure ingestion is not verified.`;
  } catch {
    result.textContent = "App health could not be checked. Check your connection and retry.";
  } finally {
    clearTimeout(timeout);
    button.disabled = false;
  }
});
