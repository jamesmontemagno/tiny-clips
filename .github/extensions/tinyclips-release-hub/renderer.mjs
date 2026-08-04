function escapeInline(value) {
    return String(value)
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;")
        .replaceAll("'", "&#39;");
}

export function renderHtml({ instanceId, token }) {
    return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>TinyClips Release Hub</title>
  <style>
    :root {
      color-scheme: light;
      --hub-background: var(--background-color-default, #ffffff);
      --hub-text: var(--text-color-default, #1f2328);
      --hub-muted: var(--text-color-muted, #57606a);
      --hub-border: var(--border-color-default, #d0d7de);
      --hub-surface: color-mix(in srgb, var(--hub-background) 96%, var(--hub-text) 4%);
      --hub-control: color-mix(in srgb, var(--hub-background) 90%, var(--hub-text) 10%);
      --hub-code: color-mix(in srgb, var(--hub-background) 92%, var(--true-color-blue, #0969da) 8%);
      --hub-good: var(--true-color-green, #1a7f37);
      --hub-warn: var(--true-color-yellow, #9a6700);
      --hub-bad: var(--true-color-red, #cf222e);
      --hub-good-bg: color-mix(in srgb, var(--hub-background) 86%, var(--hub-good) 14%);
      --hub-warn-bg: color-mix(in srgb, var(--hub-background) 86%, var(--hub-warn) 14%);
      --hub-bad-bg: color-mix(in srgb, var(--hub-background) 86%, var(--hub-bad) 14%);
      --hub-shadow: rgb(0 0 0 / 18%);
      --hub-backdrop: rgb(0 0 0 / 55%);
    }
    :root[data-color-mode="light"], body[data-color-mode="light"] { color-scheme: light; }
    :root[data-color-mode="dark"], body[data-color-mode="dark"] {
      color-scheme: dark;
      --hub-background: var(--background-color-default, #0d1117);
      --hub-text: var(--text-color-default, #f0f6fc);
      --hub-muted: var(--text-color-muted, #8b949e);
      --hub-border: var(--border-color-default, #30363d);
      --hub-shadow: rgb(0 0 0 / 45%);
      --hub-backdrop: rgb(0 0 0 / 70%);
    }
    * { box-sizing: border-box; }
    body {
      margin: 0; background: var(--hub-background);
      color: var(--hub-text);
      font-family: var(--font-sans, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif);
      font-size: var(--text-body-medium, 14px); line-height: var(--leading-body-medium, 20px);
    }
    button, input { font: inherit; }
    button { cursor: pointer; }
    button:hover:not(:disabled) { filter: brightness(1.08); }
    button:focus-visible, input:focus-visible, [role="tab"]:focus-visible {
      outline: 2px solid var(--color-focus-outline, #2f81f7); outline-offset: 2px;
    }
    .shell { max-width: 1180px; margin: 0 auto; padding: 24px; }
    .topbar { display: flex; align-items: flex-start; justify-content: space-between; gap: 16px; margin-bottom: 22px; }
    .eyebrow { color: var(--true-color-blue, #58a6ff); font-size: 12px; font-weight: 700; letter-spacing: .08em; text-transform: uppercase; }
    h1 { margin: 3px 0 4px; font-size: var(--text-title-large, 28px); line-height: 1.2; }
    .muted { color: var(--hub-muted); }
    .refresh {
      border: 1px solid var(--hub-border); border-radius: 8px;
      background: transparent; color: inherit; padding: 8px 12px;
    }
    .summary { display: grid; grid-template-columns: repeat(4, 1fr); gap: 10px; margin-bottom: 18px; }
    .metric, .panel {
      border: 1px solid var(--hub-border); border-radius: 12px;
      background: var(--hub-surface);
    }
    .metric { padding: 14px; min-height: 82px; }
    .metric-link { color: inherit; text-decoration: none; }
    .metric-link:hover { border-color: var(--true-color-blue, #0969da); background: var(--hub-control); }
    .metric-label { color: var(--hub-muted); font-size: 12px; }
    .metric-value { font-size: 18px; font-weight: 650; margin-top: 5px; overflow-wrap: anywhere; }
    .tabs { display: flex; gap: 4px; border-bottom: 1px solid var(--hub-border); margin-bottom: 18px; }
    .tab {
      border: 0; border-bottom: 2px solid transparent; background: transparent; color: var(--hub-muted);
      padding: 10px 15px; font-weight: 650;
    }
    .tab[aria-selected="true"] { color: inherit; border-bottom-color: var(--true-color-blue, #58a6ff); }
    .grid { display: grid; grid-template-columns: minmax(0, 1.45fr) minmax(300px, .8fr); gap: 16px; }
    .panel { padding: 18px; }
    .panel h2 { margin: 0 0 4px; font-size: 17px; }
    .panel-head { display: flex; align-items: flex-start; justify-content: space-between; gap: 10px; margin-bottom: 14px; }
    .badge { display: inline-flex; align-items: center; gap: 6px; padding: 3px 8px; border-radius: 999px; font-size: 12px; font-weight: 650; }
    .good { background: var(--hub-good-bg); color: var(--hub-good); }
    .warn { background: var(--hub-warn-bg); color: var(--hub-warn); }
    .bad { background: var(--hub-bad-bg); color: var(--hub-bad); }
    .steps { display: grid; gap: 8px; }
    .step { display: grid; grid-template-columns: 26px minmax(0, 1fr) auto; gap: 10px; align-items: center; padding: 10px; border: 1px solid var(--hub-border); border-radius: 9px; }
    .step-num { width: 24px; height: 24px; display: grid; place-items: center; border-radius: 50%; background: var(--hub-control); font-size: 12px; font-weight: 700; }
    .step-title { font-weight: 650; }
    .step-detail { color: var(--hub-muted); font-size: 12px; margin-top: 1px; }
    .action {
      border: 1px solid var(--hub-border); border-radius: 7px;
      display: inline-flex; align-items: center; background: var(--hub-control); color: inherit;
      padding: 6px 10px; text-decoration: none; white-space: nowrap;
    }
    .action.primary { background: var(--true-color-blue, #0969da); border-color: transparent; color: var(--color-white, #fff); }
    .action.danger { color: var(--true-color-red, #ff7b72); }
    .action:disabled { cursor: not-allowed; opacity: .5; }
    .version-row { display: flex; gap: 8px; margin: 12px 0; }
    input {
      width: 100%; border: 1px solid var(--hub-border); border-radius: 7px;
      background: var(--hub-background); color: inherit; padding: 7px 9px;
    }
    .notes-source, .markdown-preview {
      width: 100%; min-height: 380px; margin: 0; padding: 14px; overflow: auto; white-space: pre-wrap;
      border: 1px solid var(--hub-border); border-radius: 9px;
      background: var(--hub-background); color: inherit;
    }
    .notes-source {
      font-family: var(--font-mono, Consolas, monospace); font-size: 12px; line-height: 1.55;
    }
    .markdown-preview { white-space: normal; line-height: 1.6; }
    .markdown-preview > :first-child { margin-top: 0; }
    .markdown-preview > :last-child { margin-bottom: 0; }
    .markdown-preview h2 { margin: 24px 0 8px; padding-bottom: 6px; border-bottom: 1px solid var(--hub-border); font-size: 18px; }
    .markdown-preview h3 { margin: 20px 0 7px; font-size: 15px; }
    .markdown-preview p { margin: 8px 0; }
    .markdown-preview ul { margin: 8px 0; padding-left: 24px; }
    .markdown-preview li + li { margin-top: 6px; }
    .markdown-preview code, .confirm-code {
      border-radius: 5px; background: var(--hub-code);
      font-family: var(--font-mono, Consolas, monospace); font-size: var(--text-code-inline, 12px);
    }
    .markdown-preview code { padding: 2px 5px; }
    .markdown-preview pre { overflow: auto; margin: 12px 0; padding: 12px; border: 1px solid var(--hub-border); border-radius: 8px; background: var(--hub-code); }
    .markdown-preview pre code { padding: 0; background: transparent; }
    .markdown-preview a { color: var(--true-color-blue, #0969da); }
    .inline-actions { display: flex; gap: 7px; flex-wrap: wrap; }
    .view-switch { display: inline-flex; padding: 2px; border: 1px solid var(--hub-border); border-radius: 8px; background: var(--hub-background); }
    .view-switch .action { border: 0; background: transparent; }
    .view-switch .action[aria-pressed="true"] { background: var(--hub-control); }
    .workflow { margin-top: 14px; padding-top: 14px; border-top: 1px solid var(--hub-border); }
    .workflow-head { display: flex; align-items: center; justify-content: space-between; gap: 10px; }
    .workflow-row { display: flex; justify-content: space-between; gap: 10px; margin-top: 7px; }
    .workflow-link { margin-inline: -7px; padding: 6px 7px; border-radius: 7px; color: inherit; text-decoration: none; }
    .workflow-link:hover { background: var(--hub-control); }
    .destination-link { color: var(--true-color-blue, #0969da); font-weight: 650; text-decoration: none; }
    .destination-link:hover { text-decoration: underline; }
    .history-panel { display: grid; gap: 14px; margin-top: 12px; padding-top: 12px; border-top: 1px solid var(--hub-border); }
    .history-label { margin-bottom: 6px; color: var(--hub-muted); font-size: 12px; font-weight: 650; text-transform: uppercase; letter-spacing: .04em; }
    .history-list { display: grid; gap: 6px; }
    .history-item { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 12px; align-items: center; padding: 8px; border: 1px solid var(--hub-border); border-radius: 8px; color: inherit; text-decoration: none; }
    .history-item:hover { background: var(--hub-control); }
    .history-title { overflow: hidden; font-weight: 600; text-overflow: ellipsis; white-space: nowrap; }
    .history-meta { color: var(--hub-muted); font-size: 12px; }
    .history-side { display: flex; align-items: center; gap: 8px; }
    .actions-list, .actions-runs { display: grid; gap: 8px; }
    .actions-group + .actions-group { margin-top: 18px; }
    .actions-group-title { margin: 0 0 7px; color: var(--hub-muted); font-size: 12px; font-weight: 650; letter-spacing: .04em; text-transform: uppercase; }
    .actions-workflow, .actions-run {
      padding: 11px 0; border-bottom: 1px solid var(--hub-border);
    }
    .actions-workflow:first-child, .actions-run:first-child { padding-top: 0; }
    .actions-workflow:last-child, .actions-run:last-child { padding-bottom: 0; border-bottom: 0; }
    .actions-workflow-head, .actions-run-head { display: flex; justify-content: space-between; gap: 12px; align-items: flex-start; }
    .actions-workflow-name { font-weight: 650; }
    .actions-meta { display: flex; flex-wrap: wrap; gap: 5px 10px; margin-top: 4px; color: var(--hub-muted); font-size: 12px; }
    .actions-meta a { color: inherit; }
    .actions-empty { padding: 18px 0; color: var(--hub-muted); }
    .actions-runner { display: grid; gap: 12px; }
    .actions-runner-head { padding-bottom: 12px; border-bottom: 1px solid var(--hub-border); }
    .actions-runner-head h2 { margin-bottom: 3px; }
    .workflow-form { display: grid; gap: 10px; }
    .workflow-field { display: grid; gap: 4px; }
    .workflow-field-label { font-size: 12px; font-weight: 650; }
    .workflow-field-detail { color: var(--hub-muted); font-size: 12px; }
    .workflow-field select {
      width: 100%; border: 1px solid var(--hub-border); border-radius: 7px;
      background: var(--hub-background); color: inherit; padding: 7px 9px;
    }
    .actions-recent { margin-top: 16px; padding-top: 14px; border-top: 1px solid var(--hub-border); }
    .demo-status {
      display: none; margin-bottom: 18px; padding: 11px 14px; border: 1px solid var(--true-color-yellow, #9a6700);
      border-radius: 10px; background: var(--hub-warn-bg); color: var(--hub-text);
    }
    .demo-status[data-active="true"] { display: block; }
    .settings-stack { display: grid; gap: 18px; }
    .setting-row { display: flex; align-items: flex-start; justify-content: space-between; gap: 16px; padding: 12px 0; border-bottom: 1px solid var(--hub-border); }
    .setting-row:last-child { padding-bottom: 0; border-bottom: 0; }
    .setting-title { font-weight: 650; }
    .setting-toggle { display: inline-flex; align-items: center; gap: 8px; white-space: nowrap; font-weight: 650; }
    .setting-toggle input { width: 18px; height: 18px; accent-color: var(--true-color-blue, #0969da); }
    dialog {
      width: min(480px, calc(100vw - 32px)); border: 1px solid var(--hub-border);
      border-radius: 12px; background: var(--hub-background); color: inherit; padding: 20px;
      box-shadow: 0 16px 50px var(--hub-shadow);
    }
    dialog::backdrop { background: var(--hub-backdrop); }
    dialog h2 { margin: 0 0 8px; }
    .confirm-code { display: block; margin: 10px 0; padding: 9px; }
    .dialog-actions { display: flex; justify-content: flex-end; gap: 8px; margin-top: 15px; }
    .toast {
      position: fixed; right: 20px; bottom: 20px; max-width: min(520px, calc(100vw - 40px));
      padding: 11px 14px; border: 1px solid var(--hub-border); border-radius: 9px;
      background: var(--hub-surface); box-shadow: 0 8px 30px var(--hub-shadow); display: none; white-space: pre-wrap;
    }
    .operation-progress {
      display: grid; grid-template-columns: auto minmax(0, 1fr) auto; gap: 12px; align-items: center;
      margin-bottom: 18px; padding: 12px 14px; border: 1px solid var(--true-color-blue, #0969da);
      border-radius: 10px; background: var(--hub-surface);
    }
    .operation-progress[hidden] { display: none; }
    .operation-progress[data-state="success"] { border-color: var(--true-color-green, #1a7f37); }
    .operation-progress[data-state="error"] { border-color: var(--true-color-red, #cf222e); }
    .operation-spinner {
      width: 18px; height: 18px; border: 2px solid var(--hub-border);
      border-top-color: var(--true-color-blue, #0969da); border-radius: 50%;
      animation: operation-spin .8s linear infinite;
    }
    .operation-progress[data-state="success"] .operation-spinner,
    .operation-progress[data-state="error"] .operation-spinner { animation: none; border-width: 5px; }
    .operation-progress[data-state="success"] .operation-spinner { border-color: var(--true-color-green, #1a7f37); }
    .operation-progress[data-state="error"] .operation-spinner { border-color: var(--true-color-red, #cf222e); }
    .operation-title { font-weight: 650; }
    .operation-detail { margin-top: 2px; color: var(--hub-muted); font-size: 12px; }
    .operation-link { color: var(--true-color-blue, #0969da); font-weight: 650; text-decoration: none; white-space: nowrap; }
    @keyframes operation-spin { to { transform: rotate(360deg); } }
    .loading { opacity: .65; pointer-events: none; }
    @media (max-width: 800px) {
      .summary { grid-template-columns: repeat(2, 1fr); }
      .grid { grid-template-columns: 1fr; }
      .actions-workflow-head, .actions-run-head { display: grid; }
      .shell { padding: 16px; }
      .step { grid-template-columns: 26px minmax(0, 1fr); }
      .step .action { grid-column: 2; justify-self: start; }
    }
  </style>
</head>
<body>
  <main class="shell" id="app">
    <header class="topbar">
      <div>
        <div class="eyebrow">TinyClips delivery</div>
        <h1>Release Hub</h1>
        <div class="muted">Tags, release notes, GitHub Actions, Homebrew, Sparkle, MSIX, and winget.</div>
      </div>
      <button class="refresh" id="refresh" type="button">Refresh</button>
    </header>
    <section class="operation-progress" id="operation-progress" role="status" aria-live="polite" data-state="running" hidden>
      <span class="operation-spinner" aria-hidden="true"></span>
      <div>
        <div class="operation-title" id="operation-title"></div>
        <div class="operation-detail" id="operation-detail"></div>
      </div>
      <a class="operation-link" id="operation-link" target="_blank" rel="noreferrer" hidden>Open run</a>
    </section>
    <section class="summary" id="summary" aria-label="Repository release summary"></section>
    <section class="demo-status" id="demo-status" role="status" aria-live="polite" data-active="false"></section>
    <div class="tabs" role="tablist" aria-label="Release and automation">
      <button class="tab" id="tab-mac" role="tab" aria-controls="dashboard" aria-selected="true" data-platform="mac">macOS</button>
      <button class="tab" id="tab-windows" role="tab" aria-controls="dashboard" aria-selected="false" data-platform="windows">Windows</button>
      <button class="tab" id="tab-actions" role="tab" aria-controls="dashboard" aria-selected="false" data-platform="actions">Actions</button>
      <button class="tab" id="tab-settings" role="tab" aria-controls="dashboard" aria-selected="false" data-platform="settings">Settings</button>
    </div>
    <section class="grid" id="dashboard" role="tabpanel" aria-live="polite"></section>
  </main>
  <dialog id="confirm-dialog" aria-labelledby="confirm-title">
    <h2 id="confirm-title">Confirm release operation</h2>
    <p id="confirm-description" class="muted"></p>
    <span class="confirm-code" id="confirm-code"></span>
    <input id="confirm-input" autocomplete="off" aria-label="Type confirmation text" />
    <div class="dialog-actions">
      <button class="action" id="confirm-cancel" type="button">Cancel</button>
      <button class="action danger" id="confirm-run" type="button">Run operation</button>
    </div>
  </dialog>
  <div class="toast" id="toast" role="status" aria-live="polite"></div>
  <script>
    const TOKEN = ${JSON.stringify(token)};
    const INSTANCE_ID = ${JSON.stringify(escapeInline(instanceId))};
    let snapshot;
    let platform = "mac";
    let generatedNotes = {};
    let notesViews = { mac: "preview", windows: "preview" };
    let historyViews = { mac: false, windows: false };
    let releaseTags = { mac: null, windows: null };
    let activeActionsWorkflow = null;
    let demoProgress = {};
    let pendingAction;
    let workflowPollToken = 0;
    let workflowTracking = false;
    let progressHideTimer;

    const app = document.getElementById("app");
    const dashboard = document.getElementById("dashboard");
    const dialog = document.getElementById("confirm-dialog");
    const confirmInput = document.getElementById("confirm-input");
    const operationProgress = document.getElementById("operation-progress");
    const operationTitle = document.getElementById("operation-title");
    const operationDetail = document.getElementById("operation-detail");
    const operationLink = document.getElementById("operation-link");

    const escapeHtml = (value) => String(value ?? "")
      .replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;")
      .replaceAll('"', "&quot;").replaceAll("'", "&#39;");

    function renderInlineMarkdown(value) {
      return escapeHtml(value)
        .replace(/\\[([^\\]]+)\\]\\((https?:\\/\\/[^\\s)]+)\\)/g, '<a href="$2" target="_blank" rel="noreferrer">$1</a>')
        .replace(/\\*\\*([^*]+)\\*\\*/g, "<strong>$1</strong>")
        .replace(/\`([^\`]+)\`/g, "<code>$1</code>");
    }

    function renderMarkdown(markdown) {
      const lines = String(markdown ?? "").split(/\\r?\\n/);
      const output = [];
      let inCode = false;
      let codeLines = [];
      let inList = false;

      const closeList = () => {
        if (inList) {
          output.push("</ul>");
          inList = false;
        }
      };

      for (const line of lines) {
        if (line.trim().startsWith("\`\`\`")) {
          closeList();
          if (inCode) {
            output.push("<pre><code>" + escapeHtml(codeLines.join("\\n")) + "</code></pre>");
            codeLines = [];
          }
          inCode = !inCode;
          continue;
        }
        if (inCode) {
          codeLines.push(line);
          continue;
        }
        if (!line.trim()) {
          closeList();
          continue;
        }
        const heading = line.match(/^(#{2,3})\\s+(.+)$/);
        if (heading) {
          closeList();
          const level = heading[1].length;
          output.push("<h" + level + ">" + renderInlineMarkdown(heading[2]) + "</h" + level + ">");
          continue;
        }
        if (line.startsWith("- ")) {
          if (!inList) {
            output.push("<ul>");
            inList = true;
          }
          output.push("<li>" + renderInlineMarkdown(line.slice(2)) + "</li>");
          continue;
        }
        closeList();
        output.push("<p>" + renderInlineMarkdown(line) + "</p>");
      }
      closeList();
      if (inCode) {
        output.push("<pre><code>" + escapeHtml(codeLines.join("\\n")) + "</code></pre>");
      }
      return output.join("");
    }

    function currentNotes() {
      const data = snapshot.platforms[platform];
      return generatedNotes[platform]?.notes || data.unreleasedNotes || "No unreleased notes yet.";
    }

    function showToast(message, isError = false) {
      const toast = document.getElementById("toast");
      toast.textContent = message;
      toast.style.display = "block";
      toast.style.borderColor = isError ? "var(--true-color-red, #ff7b72)" : "var(--hub-border)";
      window.clearTimeout(showToast.timer);
      showToast.timer = window.setTimeout(() => { toast.style.display = "none"; }, 7000);
    }

    function setOperationProgress(title, detail, state = "running", url = null) {
      window.clearTimeout(progressHideTimer);
      operationProgress.hidden = false;
      operationProgress.dataset.state = state;
      operationTitle.textContent = title;
      operationDetail.textContent = detail;
      operationLink.hidden = !url;
      if (url) operationLink.href = url;
    }

    function hideOperationProgress(delay = 0) {
      window.clearTimeout(progressHideTimer);
      progressHideTimer = window.setTimeout(() => {
        if (!workflowTracking) operationProgress.hidden = true;
      }, delay);
    }

    function actionProgress(action, tag) {
      if (action === "prepare_release") {
        return ["Preparing " + tag, "Updating the changelog, committing it, and creating the annotated tag."];
      }
      if (action === "undo_prepare") {
        return ["Undoing " + tag, "Deleting the local tag and restoring this session to origin/main."];
      }
      if (action === "push_release") {
        return ["Publishing " + tag, "Fast-forwarding main and pushing the release tag."];
      }
      if (action === "run_release_workflow") {
        return ["Dispatching release workflow", "Requesting a GitHub Actions run for " + tag + "."];
      }
      if (action === "dispatch_workflow") {
        return ["Dispatching workflow", "Requesting a manual GitHub Actions run."];
      }
      return ["Dispatching winget submission", "Requesting the winget workflow for " + tag + "."];
    }

    const sleep = (milliseconds) => new Promise((resolve) => window.setTimeout(resolve, milliseconds));

    async function trackWorkflow(targetPlatform, kind, tag, since) {
      const token = ++workflowPollToken;
      workflowTracking = true;
      const workflowLabel = kind === "winget"
        ? "winget submission"
        : (targetPlatform === "mac" ? "macOS release" : "Windows release");
      let failures = 0;
      setOperationProgress("Waiting for " + workflowLabel, "GitHub Actions has not queued the run yet.");

      while (token === workflowPollToken) {
        try {
          const result = await api("workflow_status", {
            platform: targetPlatform, kind, tag, since
          }, false);
          failures = 0;
          const run = result.run;
          if (!run) {
            setOperationProgress("Waiting for " + workflowLabel, "GitHub Actions has not queued the run yet.");
          } else if (run.status !== "completed") {
            const status = run.status === "in_progress" ? "In progress" : "Queued";
            setOperationProgress(
              workflowLabel.charAt(0).toUpperCase() + workflowLabel.slice(1) + " " + status.toLowerCase(),
              run.displayTitle || status,
              "running",
              run.url
            );
          } else {
            workflowTracking = false;
            const succeeded = run.conclusion === "success";
            setOperationProgress(
              succeeded ? workflowLabel.charAt(0).toUpperCase() + workflowLabel.slice(1) + " succeeded"
                : workflowLabel.charAt(0).toUpperCase() + workflowLabel.slice(1) + " failed",
              run.displayTitle || ("Conclusion: " + run.conclusion),
              succeeded ? "success" : "error",
              run.url
            );
            await refresh(false);
            if (succeeded) hideOperationProgress(15000);
            return;
          }
        } catch (error) {
          failures += 1;
          setOperationProgress(
            "Checking " + workflowLabel,
            failures < 3 ? "Status query failed; retrying." : error.message,
            failures < 3 ? "running" : "error"
          );
        }
        await sleep(5000);
      }
    }

    function resumeActiveWorkflow() {
      if (workflowTracking) return true;
      const candidates = [
        { platform: "mac", kind: "release", run: snapshot.platforms.mac.workflow?.run },
        { platform: "windows", kind: "release", run: snapshot.platforms.windows.workflow?.run },
        { platform: "windows", kind: "winget", run: snapshot.platforms.windows.wingetWorkflow?.run },
      ].filter((candidate) => candidate.run && candidate.run.status !== "completed")
        .sort((left, right) => Date.parse(right.run.createdAt) - Date.parse(left.run.createdAt));
      const active = candidates[0];
      if (!active) return false;
      const tag = active.run.headBranch?.startsWith("v") ? active.run.headBranch : null;
      trackWorkflow(active.platform, active.kind, tag, active.run.createdAt);
      return true;
    }

    function workflowLabel(workflow) {
      if (!workflow?.available) return "Unavailable";
      if (!workflow.run) return "No runs";
      return workflow.run.conclusion || workflow.run.status || "Unknown";
    }

    function workflowBadge(workflow) {
      const label = workflowLabel(workflow);
      const kind = label === "success" ? "good" : (label === "failure" ? "bad" : "warn");
      return '<span class="badge ' + kind + '">' + escapeHtml(label) + '</span>';
    }

    function workflowRow(label, workflow) {
      const content = '<span>' + escapeHtml(label) + '</span>' + workflowBadge(workflow);
      return workflow?.run?.url
        ? '<a class="workflow-row workflow-link" href="' + escapeHtml(workflow.run.url) +
          '" target="_blank" rel="noreferrer" title="Open latest GitHub Actions run">' + content + '</a>'
        : '<div class="workflow-row">' + content + '</div>';
    }

    function formatRunDate(value) {
      if (!value) return "Unknown date";
      return new Date(value).toLocaleString([], {
        month: "short", day: "numeric", hour: "numeric", minute: "2-digit"
      });
    }

    function workflowHistorySection(label, workflow) {
      const runs = workflow?.runs || [];
      if (!runs.length) {
        return '<section><div class="history-label">' + escapeHtml(label) +
          '</div><div class="muted">No recent runs found.</div></section>';
      }
      return '<section><div class="history-label">' + escapeHtml(label) + '</div><div class="history-list">' +
        runs.map((run) =>
          '<a class="history-item" href="' + escapeHtml(run.url) + '" target="_blank" rel="noreferrer">' +
          '<div><div class="history-title">' + escapeHtml(run.displayTitle || label) +
          '</div><div class="history-meta">' + escapeHtml(formatRunDate(run.createdAt)) + '</div></div>' +
          '<div class="history-side">' + workflowBadge({ available: true, run }) + '<span aria-hidden="true">↗</span></div></a>'
        ).join("") + '</div></section>';
    }

    function renderWorkflowHistory(data) {
      if (!historyViews[platform]) return "";
      return '<div class="history-panel">' +
        workflowHistorySection(data.label + " release", data.workflow) +
        (platform === "windows" ? workflowHistorySection("winget submission", data.wingetWorkflow) : "") +
        '</div>';
    }

    function wingetVersionStatus(winget) {
      if (!winget?.available) {
        return '<span class="badge warn" title="' + escapeHtml(winget?.error || "winget version unavailable") + '">Unavailable</span>';
      }
      if (!winget.version) {
        return '<span class="badge warn">Not published</span>';
      }
      const kind = winget.status === "current" ? "good" : "warn";
      const label = winget.status === "current"
        ? "Current"
        : (winget.status === "missing"
          ? "Not published"
          : (winget.status === "behind" ? "Behind release" : "Newer than release"));
      return '<span class="badge ' + kind + '">' + escapeHtml(label) + '</span>';
    }

    function repositoryUrl(path) {
      return snapshot.repositoryUrl ? snapshot.repositoryUrl + path : null;
    }

    function linkedValue(value, url, className = "destination-link") {
      return url
        ? '<a class="' + className + '" href="' + escapeHtml(url) +
          '" target="_blank" rel="noreferrer">' + escapeHtml(value) + '</a>'
        : escapeHtml(value);
    }

    function renderSummary() {
      const mac = snapshot.platforms.mac;
      const windows = snapshot.platforms.windows;
      document.getElementById("summary").innerHTML = [
        { label: "Branch", value: snapshot.git.branch },
        { label: "Working tree", value: snapshot.git.clean ? "Clean" : snapshot.git.changeCount + " changes" },
        {
          label: "Latest macOS release",
          value: mac.latestRelease?.tagName || "No release",
          url: mac.latestRelease ? repositoryUrl("/releases/tag/" + encodeURIComponent(mac.latestRelease.tagName)) : null
        },
        {
          label: "Latest Windows release",
          value: windows.latestRelease?.tagName || "No release",
          url: windows.latestRelease ? repositoryUrl("/releases/tag/" + encodeURIComponent(windows.latestRelease.tagName)) : null
        },
      ].map((item) =>
        (item.url ? '<a class="metric metric-link" href="' + escapeHtml(item.url) + '" target="_blank" rel="noreferrer">'
          : '<div class="metric">') +
        '<div class="metric-label">' + escapeHtml(item.label) +
        '</div><div class="metric-value">' + escapeHtml(item.value) + '</div>' +
        (item.url ? '</a>' : '</div>')
      ).join("");
    }

    function renderDemoStatus() {
      const status = document.getElementById("demo-status");
      const enabled = snapshot?.settings?.demoMode === true;
      status.dataset.active = String(enabled);
      status.innerHTML = enabled
        ? '<strong>Demo Mode is on.</strong> Release actions, workflow dispatches, and Winget submissions are simulated; no Git or GitHub changes are made.'
        : "";
    }

    function renderSettingsDashboard() {
      const enabled = snapshot.settings?.demoMode === true;
      dashboard.innerHTML = '<article class="panel"><div class="panel-head"><div><h2>Settings</h2><div class="muted">Release Hub preferences are saved for this Copilot user.</div></div></div>' +
        '<div class="settings-stack"><div class="setting-row"><div><div class="setting-title">Demo Mode</div><div class="muted">Simulate release preparation, Git pushes, release workflows, Winget submissions, and Actions dispatches without making external changes.</div></div>' +
        '<label class="setting-toggle"><input id="demo-mode-toggle" type="checkbox" role="switch" aria-label="Enable Demo Mode"' +
        (enabled ? " checked" : "") + ' />' + (enabled ? "On" : "Off") + '</label></div>' +
        '<div class="setting-row"><div><div class="setting-title">Demo sequence</div><div class="muted">The simulated release sequence is kept only in this open Release Hub panel.</div></div>' +
        '<button class="action" id="reset-demo-progress" type="button"' + (Object.keys(demoProgress).length ? "" : " disabled") + '>Reset sequence</button></div></div></article>';
      document.getElementById("demo-mode-toggle").addEventListener("change", (event) => updateDemoMode(event.target.checked));
      document.getElementById("reset-demo-progress").addEventListener("click", () => {
        demoProgress = {};
        renderSettingsDashboard();
        showToast("Demo sequence reset.");
      });
    }

    function actionRunBadge(run) {
      return workflowBadge({ available: true, run });
    }

    function renderActionRun(run) {
      return '<div class="actions-run"><div class="actions-run-head"><div><div class="actions-workflow-name">' +
        escapeHtml(run.workflowName || run.name || "Workflow run") + '</div><div class="actions-meta"><span>' +
        escapeHtml(run.displayTitle || "No title") + '</span><span>' + escapeHtml(run.event || "unknown event") +
        '</span><span>' + escapeHtml(run.headBranch || "no ref") + '</span><span>' +
        escapeHtml(formatRunDate(run.createdAt)) + '</span></div></div><div class="history-side">' +
        actionRunBadge(run) + (run.url ? '<a class="action" href="' + escapeHtml(run.url) +
        '" target="_blank" rel="noreferrer">Open</a>' : '') + '</div></div></div>';
    }

    function workflowGroup(workflow) {
      const source = (workflow.name + " " + workflow.path).toLowerCase();
      if (source.includes("release") || source.includes("winget")) return "Releases & distribution";
      if (source.includes("build") || source.includes("test")) return "Builds & validation";
      if (source.includes("copilot")) return "Copilot";
      if (source.includes("report") || source.includes("parity") || source.includes("weekly")) return "Automation & reports";
      return "Other workflows";
    }

    function runGroup(run) {
      if (run.status !== "completed") return "In progress";
      const created = new Date(run.createdAt);
      const now = new Date();
      const startOfToday = new Date(now.getFullYear(), now.getMonth(), now.getDate());
      const startOfYesterday = new Date(startOfToday);
      startOfYesterday.setDate(startOfYesterday.getDate() - 1);
      if (created >= startOfToday) return "Today";
      if (created >= startOfYesterday) return "Yesterday";
      return created.toLocaleDateString([], { month: "long", day: "numeric", year: "numeric" });
    }

    function groupBy(items, keyForItem) {
      const groups = new Map();
      items.forEach((item) => {
        const key = keyForItem(item);
        if (!groups.has(key)) groups.set(key, []);
        groups.get(key).push(item);
      });
      return groups;
    }

    function renderActionsRunner() {
      if (!activeActionsWorkflow) {
        return '<div class="actions-runner"><div class="actions-runner-head"><h2>Run a workflow</h2>' +
          '<div class="muted">Choose a workflow to inspect its manual-dispatch inputs and recent runs.</div></div></div>';
      }
      const details = activeActionsWorkflow;
      const workflow = details.workflow;
      const dispatch = details.dispatch;
      const inputFields = dispatch.inputs.map((input) => {
        const required = input.required ? " required" : "";
        const detail = input.description
          ? '<div class="workflow-field-detail">' + escapeHtml(input.description) + '</div>'
          : "";
        if ((input.type === "choice" || input.options?.length) && input.options.length) {
          return '<label class="workflow-field"><span class="workflow-field-label">' + escapeHtml(input.name + required) +
            '</span><select data-workflow-input="' + escapeHtml(input.name) + '"><option value=""></option>' +
            input.options.map((option) => '<option value="' + escapeHtml(option) + '"' +
              (option === input.default ? " selected" : "") + '>' + escapeHtml(option) + '</option>').join("") +
            '</select>' + detail + '</label>';
        }
        if (input.type === "boolean") {
          return '<label class="workflow-field"><span class="workflow-field-label">' + escapeHtml(input.name + required) +
            '</span><select data-workflow-input="' + escapeHtml(input.name) + '"><option value="true"' +
            (input.default === "true" ? " selected" : "") + '>true</option><option value="false"' +
            (input.default === "false" ? " selected" : "") + '>false</option></select>' + detail + '</label>';
        }
        return '<label class="workflow-field"><span class="workflow-field-label">' + escapeHtml(input.name + required) +
          '</span><input data-workflow-input="' + escapeHtml(input.name) + '" value="' +
          escapeHtml(input.default || "") + '" />' + detail + '</label>';
      }).join("");
      const recentRuns = details.recentRuns.length
        ? details.recentRuns.map(renderActionRun).join("")
        : '<div class="actions-empty">No recent runs for this workflow.</div>';
      const dispatchMessage = dispatch.supported
        ? (dispatch.inputs.length ? "Provide the declared inputs, then confirm the dispatch." : "This workflow can be dispatched without inputs.")
        : "This workflow does not declare workflow_dispatch and cannot be started manually.";
      return '<div class="actions-runner"><div class="actions-runner-head"><h2>' + escapeHtml(workflow.name) +
        '</h2><div class="actions-meta"><span>' + escapeHtml(workflow.path) + '</span><span>' +
        escapeHtml(dispatchMessage) + '</span></div></div>' +
        (dispatch.supported
          ? '<form class="workflow-form" id="workflow-dispatch-form"><label class="workflow-field"><span class="workflow-field-label">Ref</span>' +
            '<input id="workflow-ref" value="main" aria-label="Workflow ref" /></label>' + inputFields +
            '<button class="action primary" type="submit">Run workflow</button></form>'
          : '') +
        '<div class="actions-recent"><div class="history-label">Recent runs for this workflow</div><div class="actions-runs">' +
        recentRuns + '</div></div></div>';
    }

    function renderActionsDashboard() {
      const actions = snapshot.actions;
      if (!actions?.available) {
        dashboard.innerHTML = '<article class="panel"><h2>GitHub Actions unavailable</h2><div class="actions-empty">' +
          escapeHtml(actions?.error || "Could not query repository workflows.") + '</div></article>';
        return;
      }
      const workflowList = actions.workflows.length
        ? [...groupBy(actions.workflows, workflowGroup)].map(([group, workflows]) => {
          const rows = workflows.map((workflow) => {
          const latest = workflow.latestRun;
          return '<div class="actions-workflow"><div class="actions-workflow-head"><div><div class="actions-workflow-name">' +
            escapeHtml(workflow.name) + '</div><div class="actions-meta"><span>' + escapeHtml(workflow.path) +
            '</span><span>' + escapeHtml(workflow.state) + '</span>' +
            (latest ? '<span>Latest: ' + escapeHtml(formatRunDate(latest.createdAt)) + '</span>' : '<span>No recent runs</span>') +
            '</div></div><div class="history-side">' + (latest ? actionRunBadge(latest) : "") +
            '<button class="action" data-actions-workflow="' + escapeHtml(workflow.id) + '" type="button">View details</button></div></div></div>';
          }).join("");
          return '<section class="actions-group"><h3 class="actions-group-title">' + escapeHtml(group) + '</h3>' + rows + '</section>';
        }).join("")
        : '<div class="actions-empty">No repository workflows found.</div>';
      const runs = actions.runs.length
        ? [...groupBy(actions.runs, runGroup)].map(([group, groupRuns]) =>
          '<section class="actions-group"><h3 class="actions-group-title">' + escapeHtml(group) + '</h3>' +
          groupRuns.map(renderActionRun).join("") + '</section>'
        ).join("")
        : '<div class="actions-empty">No recent workflow runs found.</div>';
      dashboard.innerHTML = '<article class="panel"><div class="panel-head"><div><h2>Workflows</h2><div class="muted">All enabled and disabled workflows in this repository.</div></div>' +
        '<span class="badge good">' + escapeHtml(actions.workflows.length) + ' workflows</span></div><div class="actions-list">' +
        workflowList + '</div></article><article class="panel">' + renderActionsRunner() +
        '<div class="actions-recent"><div class="panel-head"><div><h2>Recent activity</h2><div class="muted">The latest 50 runs across all workflows.</div></div></div>' +
        '<div class="actions-runs">' + runs + '</div></div></article>';
      dashboard.querySelectorAll("[data-actions-workflow]").forEach((button) => {
        button.addEventListener("click", () => loadActionsWorkflow(button.dataset.actionsWorkflow));
      });
      document.getElementById("workflow-dispatch-form")?.addEventListener("submit", submitActionsWorkflow);
    }

    function step(number, title, detail, action, label, disabled = false, extraClass = "") {
      return '<div class="step"><div class="step-num">' + number + '</div><div><div class="step-title">' +
        escapeHtml(title) + '</div><div class="step-detail">' + escapeHtml(detail) +
        '</div></div><button class="action ' + extraClass + '" data-action="' + action + '" type="button"' +
        (disabled ? " disabled" : "") + '>' + escapeHtml(label) + '</button></div>';
    }

    function renderDashboard() {
      if (platform === "settings") {
        renderSettingsDashboard();
        return;
      }
      if (platform === "actions") {
        renderActionsDashboard();
        return;
      }
      const data = snapshot.platforms[platform];
      const version = releaseTags[platform] || data.pendingTag || data.suggestedTag;
      releaseTags[platform] = version;
      const notes = currentNotes();
      const notesView = notesViews[platform];
      const demoMode = snapshot.settings?.demoMode === true;
      const demo = demoMode ? demoProgress[platform] : null;
      const preparedLocally = data.pendingTag === version || (demo?.tag === version && demo.prepared);
      const actualTagPushed = data.remoteTags?.includes(version);
      const actualReleasePublished = data.latestRelease?.tagName === version;
      const tagPushed = actualTagPushed || (demo?.tag === version && demo.pushed);
      const releasePublished = actualReleasePublished || (demo?.tag === version && demo.published);
      const canPrepareRelease = demoMode || snapshot.git.canPrepareRelease;
      const canPushRelease = demoMode || snapshot.git.canPushRelease;
      const readinessBadge = preparedLocally
        ? '<span class="badge good">' + (demo?.prepared ? "Prepared in demo" : "Prepared locally") + '</span>'
        : (demoMode
          ? '<span class="badge warn">Demo Mode</span>'
          : (!snapshot.git.atMainTip
          ? '<span class="badge warn">Latest main required</span>'
          : (snapshot.git.clean
            ? '<span class="badge good">Ready to prepare</span>'
            : '<span class="badge warn">Commit changes first</span>')));
      const prepareDetail = preparedLocally
        ? "Prepared locally. Undo removes only this unpushed tag and its release commit."
        : (demoMode
          ? "Simulates the changelog commit and release tag without changing Git."
          : (!snapshot.git.atMainTip
          ? "Start from the latest origin/main with no additional commits."
          : "Moves Unreleased notes into a dated section, commits, and creates an annotated tag."));
      const pushDetail = demoMode
        ? (tagPushed ? "Push simulated. No branch or tag was sent to origin." : "Simulates publishing the prepared release tag.")
        : (tagPushed
          ? "This tag is already on origin."
          : (preparedLocally
            ? "Fast-forwards main with the release commit, then pushes the tag to start the workflow."
            : "Prepare the release tag first."));
      const workflowDetail = demoMode
        ? "Simulates the platform release workflow without dispatching GitHub Actions."
        : (tagPushed
          ? "The tag push starts this automatically; use this only to recover or re-run it."
          : "Push the prepared tag before manually dispatching this workflow.");
      const wingetSubmissionTag = demo?.published && !demo.wingetSubmitted ? demo.tag : data.wingetSubmissionTag;
      const wingetStep = platform === "windows"
        ? step(
          4,
          "Submit to winget",
          wingetSubmissionTag
            ? (demoMode ? "Simulates a Winget submission without dispatching a workflow." : "Winget is behind the latest published Windows release.")
            : "Available when a published Windows release is newer than winget.",
          "submit_winget",
          wingetSubmissionTag ? "Submit " + wingetSubmissionTag : "Up to date",
          !wingetSubmissionTag,
        )
        : step(4, "Publish update metadata", "Sparkle appcast and Homebrew cask update in the macOS workflow.", "view_workflow", "In workflow", true);

      dashboard.innerHTML =
        '<article class="panel"><div class="panel-head"><div><h2>' + escapeHtml(data.label) +
        ' release path</h2><div class="muted">' + escapeHtml(data.unreleasedCount) +
        ' unreleased changelog items</div></div>' +
        readinessBadge + '</div>' +
        '<label for="version">Release tag</label><div class="version-row"><input id="version" value="' +
        escapeHtml(version) + '" spellcheck="false" /><button class="action" data-action="generate_notes" type="button">Generate notes</button>' +
        (actualTagPushed ? '<a class="action" href="' + escapeHtml(repositoryUrl("/tree/" + encodeURIComponent(version))) +
          '" target="_blank" rel="noreferrer">Open tag</a>' : '') +
        (actualReleasePublished ? '<a class="action" href="' + escapeHtml(repositoryUrl("/releases/tag/" + encodeURIComponent(version))) +
          '" target="_blank" rel="noreferrer">Open release</a>' : '') +
        '</div>' +
        '<div class="steps">' +
        step(
          1,
          "Prepare release",
          prepareDetail,
          preparedLocally ? "undo_prepare" : "prepare_release",
          tagPushed ? "Released" : (preparedLocally ? "Undo" : "Prepare"),
          tagPushed || (!preparedLocally && !canPrepareRelease),
          preparedLocally ? "danger" : "primary"
        ) +
        step(2, "Push release tag", pushDetail, "push_release", tagPushed ? "Pushed" : "Push", !canPushRelease || !preparedLocally || tagPushed) +
        step(3, "Run release workflow", workflowDetail, "run_release_workflow", "Re-run workflow", !tagPushed) +
        wingetStep + '</div>' +
        '<div class="workflow"><div class="workflow-head"><h2>Automation status</h2>' +
        '<button class="action" data-action="toggle_history" type="button" aria-expanded="' +
        historyViews[platform] + '">' + (historyViews[platform] ? "Hide history" : "Show history") + '</button></div>' +
        workflowRow(data.label + " release", data.workflow) +
        (platform === "windows" ? workflowRow("winget submission", data.wingetWorkflow) : '') +
        '<div class="workflow-row"><span>Latest remote tag</span><strong>' +
        linkedValue(
          data.latestRemoteTag || "Not found",
          data.latestRemoteTag ? repositoryUrl("/tree/" + encodeURIComponent(data.latestRemoteTag)) : null
        ) + '</strong></div>' +
        '<div class="workflow-row"><span>Latest GitHub release</span><strong>' +
        linkedValue(
          data.latestRelease?.tagName || "Not found",
          data.latestRelease ? repositoryUrl("/releases/tag/" + encodeURIComponent(data.latestRelease.tagName)) : null
        ) + '</strong></div>' +
        (platform === "windows"
          ? '<div class="workflow-row"><span>Published on winget</span><span><strong>' +
            linkedValue(data.wingetPublished?.version || "Not found", data.wingetPublished?.url) + '</strong> ' +
            wingetVersionStatus(data.wingetPublished) + '</span></div>'
          : '') +
        renderWorkflowHistory(data) +
        '</div></article>' +
        '<article class="panel"><div class="panel-head"><div><h2>Release notes</h2><div class="muted">' +
        escapeHtml(generatedNotes[platform]?.source || data.changelog) + '</div></div><div class="inline-actions">' +
        '<div class="view-switch" role="group" aria-label="Release notes view">' +
        '<button class="action" data-action="show_preview" type="button" aria-pressed="' + (notesView === "preview") + '">Preview</button>' +
        '<button class="action" data-action="show_markdown" type="button" aria-pressed="' + (notesView === "markdown") + '">Markdown</button></div>' +
        '<button class="action" data-action="copy_notes" type="button">Copy Markdown</button></div></div>' +
        (notesView === "preview"
          ? '<div class="markdown-preview" id="notes-preview">' + renderMarkdown(notes) + '</div>'
          : '<pre class="notes-source" id="notes-source">' + escapeHtml(notes) + '</pre>') +
        '</article>';

      dashboard.querySelectorAll("[data-action]").forEach((button) => {
        button.addEventListener("click", () => handleButton(button.dataset.action));
      });
      document.getElementById("version")?.addEventListener("input", (event) => {
        releaseTags[platform] = event.target.value;
      });
    }

    async function updateDemoMode(enabled) {
      setOperationProgress(enabled ? "Enabling Demo Mode" : "Disabling Demo Mode", "Saving the Release Hub preference.");
      try {
        await api("update_settings", { demoMode: enabled });
        if (!enabled) demoProgress = {};
        renderSummary(); renderDemoStatus(); renderDashboard();
        setOperationProgress(
          enabled ? "Demo Mode enabled" : "Demo Mode disabled",
          enabled ? "All state-changing operations will now be simulated." : "Release operations will run normally again.",
          "success",
        );
        hideOperationProgress(3000);
      } catch (error) {
        setOperationProgress("Could not update Demo Mode", error.message, "error");
        showToast(error.message, true);
      }
    }

    async function loadActionsWorkflow(workflowId) {
      setOperationProgress("Loading workflow", "Reading its manual-dispatch inputs and recent runs.");
      try {
        activeActionsWorkflow = await api("workflow_details", { workflowId: Number(workflowId) });
        renderDashboard();
        setOperationProgress("Workflow ready", "Review the inputs before starting a manual run.", "success");
        hideOperationProgress(3000);
      } catch (error) {
        setOperationProgress("Could not load workflow", error.message, "error");
        showToast(error.message, true);
      }
    }

    function submitActionsWorkflow(event) {
      event.preventDefault();
      const details = activeActionsWorkflow;
      if (!details?.dispatch?.supported) return;
      const inputs = {};
      const missingInputs = [];
      details.dispatch.inputs.forEach((input) => {
        const element = event.currentTarget.querySelector('[data-workflow-input="' + input.name + '"]');
        const value = String(element?.value || "").trim();
        if (input.required && !value) missingInputs.push(input.name);
        if (value) inputs[input.name] = value;
      });
      if (missingInputs.length) {
        showToast("Required workflow inputs: " + missingInputs.join(", ") + ".", true);
        return;
      }
      const ref = event.currentTarget.querySelector("#workflow-ref")?.value.trim() || "main";
      openConfirmation(
        "dispatch_workflow",
        { workflowId: details.workflow.id, ref, inputs },
        "RUN " + details.workflow.id,
        'This starts "' + details.workflow.name + '" on ' + ref + ".",
      );
    }

    async function api(action, body = {}, blocking = true) {
      if (blocking) app.classList.add("loading");
      try {
        const response = await fetch("/api/action", {
          method: "POST",
          headers: { "Content-Type": "application/json", "X-Release-Hub-Token": TOKEN },
          body: JSON.stringify({ action, ...body }),
        });
        const payload = await response.json();
        if (!response.ok || payload.error) throw new Error(payload.error || "Release operation failed.");
        if (payload.snapshot) snapshot = payload.snapshot;
        return payload.result;
      } finally {
        if (blocking) app.classList.remove("loading");
      }
    }

    function openConfirmation(action, body, phrase, description) {
      pendingAction = { action, body, phrase };
      const demoSuffix = snapshot.settings?.demoMode ? " Demo Mode will simulate this operation." : "";
      document.getElementById("confirm-description").textContent = description + demoSuffix + " Type this exact phrase:";
      document.getElementById("confirm-code").textContent = phrase;
      confirmInput.value = "";
      dialog.showModal();
      confirmInput.focus();
    }

    function currentVersion() {
      return document.getElementById("version")?.value.trim() || releaseTags[platform] ||
        snapshot.platforms[platform].suggestedTag;
    }

    function operationSuccessMessage(result) {
      if (result.demo) {
        const target = result.workflow?.name || result.tag || "the operation";
        return "Demo Mode simulated " + target + ". No GitHub or Git changes were made.";
      }
      if (result.action === "prepare_release") {
        return "Prepared " + result.tag + " locally. Continue with Push release tag.";
      }
      if (result.action === "undo_prepare") {
        return "Undid local preparation for " + result.tag + ". The release is ready to prepare again.";
      }
      if (result.action === "push_release") {
        return "Pushed " + result.tag + ". The release workflow should start automatically.";
      }
      if (result.action === "run_release_workflow") {
        return "Dispatched the release workflow for " + result.tag + ".";
      }
      if (result.action === "submit_winget") {
        return "Dispatched winget submission for " + result.tag + ".";
      }
      if (result.action === "dispatch_workflow") {
        return "Dispatched " + result.workflow.name + " on " + result.ref + ".";
      }
      return "Release operation completed.";
    }

    function recordDemoAction(result, body) {
      if (!result.demo) return;
      const targetPlatform = body.platform || (result.tag?.endsWith("-windows") ? "windows" : "mac");
      if (result.action === "dispatch_workflow") return;
      if (result.action === "prepare_release") {
        demoProgress[targetPlatform] = { tag: result.tag, prepared: true };
      } else if (result.action === "undo_prepare") {
        delete demoProgress[targetPlatform];
      } else {
        const progress = demoProgress[targetPlatform] || { tag: result.tag, prepared: true };
        progress.tag = result.tag;
        if (result.action === "push_release") progress.pushed = true;
        if (result.action === "run_release_workflow") progress.published = true;
        if (result.action === "submit_winget") progress.wingetSubmitted = true;
        demoProgress[targetPlatform] = progress;
      }
    }

    async function handleButton(action) {
      const tag = currentVersion();
      if (action === "generate_notes") {
        try {
          setOperationProgress("Generating release notes", "Reading and formatting the platform changelog.");
          generatedNotes[platform] = await api("generate_notes", { platform, version: tag });
          renderSummary(); renderDashboard();
          setOperationProgress("Release notes ready", "Generated from " + generatedNotes[platform].source + ".", "success");
          hideOperationProgress(3000);
          showToast("Release notes generated from " + generatedNotes[platform].source + ".");
        } catch (error) {
          setOperationProgress("Release notes failed", error.message, "error");
          showToast(error.message, true);
        }
        return;
      }
      if (action === "copy_notes") {
        await navigator.clipboard.writeText(currentNotes());
        showToast("Release notes copied as Markdown.");
        return;
      }
      if (action === "show_preview" || action === "show_markdown") {
        notesViews[platform] = action === "show_preview" ? "preview" : "markdown";
        renderDashboard();
        return;
      }
      if (action === "toggle_history") {
        historyViews[platform] = !historyViews[platform];
        renderDashboard();
        return;
      }
      if (action === "prepare_release") {
        openConfirmation(action, { platform, version: tag }, "PREPARE " + tag,
          "This creates a changelog commit and annotated tag locally.");
      } else if (action === "undo_prepare") {
        openConfirmation(action, { platform, tag }, "UNDO " + tag,
          "This deletes the unpushed local tag and permanently removes its single release commit from this session.");
      } else if (action === "push_release") {
        openConfirmation(action, { tag }, "PUSH " + tag,
          "This fast-forwards main with the prepared release commit, then pushes the tag to start the release workflow.");
      } else if (action === "run_release_workflow") {
        openConfirmation(action, { platform, tag }, "RUN " + tag,
          "This manually dispatches the platform release workflow.");
      } else if (action === "submit_winget") {
        const demo = snapshot.settings?.demoMode ? demoProgress.windows : null;
        const wingetSubmissionTag = demo?.published && !demo.wingetSubmitted
          ? demo.tag
          : snapshot.platforms.windows.wingetSubmissionTag;
        if (!wingetSubmissionTag) {
          showToast("Winget is already current or no published Windows release is available.", true);
          return;
        }
        openConfirmation(action, { tag: wingetSubmissionTag }, "SUBMIT " + wingetSubmissionTag,
          "This submits the latest published Windows release to microsoft/winget-pkgs.");
      }
    }

    async function refresh(showProgress = true) {
      if (showProgress && !workflowTracking) {
        setOperationProgress("Refreshing release status", "Querying tags, releases, workflows, and winget.");
      }
      app.classList.add("loading");
      try {
        const response = await fetch("/api/status");
        const payload = await response.json();
        if (!response.ok || payload.error) throw new Error(payload.error || "Refresh failed.");
        snapshot = payload;
        renderSummary(); renderDemoStatus(); renderDashboard();
        const resumed = resumeActiveWorkflow();
        if (showProgress && !resumed) {
          setOperationProgress("Release status refreshed", "Repository and distribution status are up to date.", "success");
          hideOperationProgress(2500);
        }
      } catch (error) {
        setOperationProgress("Status refresh failed", error.message, "error");
        showToast(error.message, true);
      }
      finally { app.classList.remove("loading"); }
    }

    document.querySelectorAll("[role=tab]").forEach((tab) => {
      tab.addEventListener("click", () => {
        platform = tab.dataset.platform;
        document.querySelectorAll("[role=tab]").forEach((item) =>
          item.setAttribute("aria-selected", String(item === tab)));
        renderDashboard();
      });
    });
    document.getElementById("refresh").addEventListener("click", refresh);
    document.getElementById("confirm-cancel").addEventListener("click", () => dialog.close());
    document.getElementById("confirm-run").addEventListener("click", async () => {
      if (!pendingAction || confirmInput.value !== pendingAction.phrase) {
        showToast("Confirmation text does not match.", true);
        return;
      }
      const active = pendingAction;
      dialog.close();
      const startedAt = new Date().toISOString();
      const progress = actionProgress(active.action, active.body.tag || active.body.version);
      setOperationProgress(progress[0], progress[1]);
      try {
        const result = await api(active.action, { ...active.body, confirmation: active.phrase });
        recordDemoAction(result, active.body);
        if (result.tag) {
          releaseTags[platform] = result.tag;
        }
        renderSummary(); renderDemoStatus(); renderDashboard();
        if (result.tracking) {
          setOperationProgress(
            "Waiting for GitHub Actions",
            "The operation completed locally; waiting for the workflow run to appear."
          );
          trackWorkflow(result.tracking.platform, result.tracking.kind, result.tag, startedAt);
        } else {
          setOperationProgress(
            result.action === "undo_prepare"
              ? "Preparation undone"
              : (result.action === "dispatch_workflow" ? "Workflow dispatched" : "Release prepared"),
            operationSuccessMessage(result),
            "success"
          );
          hideOperationProgress(6000);
        }
        showToast(operationSuccessMessage(result));
      } catch (error) {
        setOperationProgress("Release operation failed", error.message, "error");
        showToast(error.message, true);
      }
    });

    refresh();
  </script>
</body>
</html>`;
}
