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
