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
      color-scheme: light dark;
      --hub-surface: color-mix(in srgb, var(--background-color-default, #ffffff) 96%, var(--text-color-default, #1f2328) 4%);
      --hub-control: color-mix(in srgb, var(--background-color-default, #ffffff) 90%, var(--text-color-default, #1f2328) 10%);
      --hub-code: color-mix(in srgb, var(--background-color-default, #ffffff) 92%, var(--true-color-blue, #0969da) 8%);
      --hub-shadow: rgb(0 0 0 / 18%);
      --hub-backdrop: rgb(0 0 0 / 55%);
    }
    :root[data-color-mode="light"], body[data-color-mode="light"] { color-scheme: light; }
    :root[data-color-mode="dark"], body[data-color-mode="dark"] {
      color-scheme: dark;
      --hub-shadow: rgb(0 0 0 / 45%);
      --hub-backdrop: rgb(0 0 0 / 70%);
    }
    * { box-sizing: border-box; }
    body {
      margin: 0; background: var(--background-color-default, #0d1117);
      color: var(--text-color-default, #f0f6fc);
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
    .muted { color: var(--text-color-muted, #8b949e); }
    .refresh {
      border: 1px solid var(--border-color-default, #30363d); border-radius: 8px;
      background: transparent; color: inherit; padding: 8px 12px;
    }
    .summary { display: grid; grid-template-columns: repeat(4, 1fr); gap: 10px; margin-bottom: 18px; }
    .metric, .panel {
      border: 1px solid var(--border-color-default, #30363d); border-radius: 12px;
      background: var(--hub-surface);
    }
    .metric { padding: 14px; min-height: 82px; }
    .metric-link { color: inherit; text-decoration: none; }
    .metric-link:hover { border-color: var(--true-color-blue, #0969da); background: var(--hub-control); }
    .metric-label { color: var(--text-color-muted, #8b949e); font-size: 12px; }
    .metric-value { font-size: 18px; font-weight: 650; margin-top: 5px; overflow-wrap: anywhere; }
    .tabs { display: flex; gap: 4px; border-bottom: 1px solid var(--border-color-default, #30363d); margin-bottom: 18px; }
    .tab {
      border: 0; border-bottom: 2px solid transparent; background: transparent; color: var(--text-color-muted, #8b949e);
      padding: 10px 15px; font-weight: 650;
    }
    .tab[aria-selected="true"] { color: inherit; border-bottom-color: var(--true-color-blue, #58a6ff); }
    .grid { display: grid; grid-template-columns: minmax(0, 1.45fr) minmax(300px, .8fr); gap: 16px; }
    .panel { padding: 18px; }
    .panel h2 { margin: 0 0 4px; font-size: 17px; }
    .panel-head { display: flex; align-items: flex-start; justify-content: space-between; gap: 10px; margin-bottom: 14px; }
    .badge { display: inline-flex; align-items: center; gap: 6px; padding: 3px 8px; border-radius: 999px; font-size: 12px; font-weight: 650; }
    .good { background: var(--true-color-green-muted, #1b4721); color: var(--true-color-green, #7ee787); }
    .warn { background: var(--true-color-yellow-muted, #4d3b05); color: var(--true-color-yellow, #f2cc60); }
    .bad { background: var(--true-color-red-muted, #4c1c1c); color: var(--true-color-red, #ff7b72); }
    .steps { display: grid; gap: 8px; }
    .step { display: grid; grid-template-columns: 26px minmax(0, 1fr) auto; gap: 10px; align-items: center; padding: 10px; border: 1px solid var(--border-color-default, #30363d); border-radius: 9px; }
    .step-num { width: 24px; height: 24px; display: grid; place-items: center; border-radius: 50%; background: var(--hub-control); font-size: 12px; font-weight: 700; }
    .step-title { font-weight: 650; }
    .step-detail { color: var(--text-color-muted, #8b949e); font-size: 12px; margin-top: 1px; }
    .action {
      border: 1px solid var(--border-color-default, #30363d); border-radius: 7px;
      display: inline-flex; align-items: center; background: var(--hub-control); color: inherit;
      padding: 6px 10px; text-decoration: none; white-space: nowrap;
    }
    .action.primary { background: var(--true-color-blue, #0969da); border-color: transparent; color: var(--color-white, #fff); }
    .action.danger { color: var(--true-color-red, #ff7b72); }
    .action:disabled { cursor: not-allowed; opacity: .5; }
    .version-row { display: flex; gap: 8px; margin: 12px 0; }
    input {
      width: 100%; border: 1px solid var(--border-color-default, #30363d); border-radius: 7px;
      background: var(--background-color-default, #0d1117); color: inherit; padding: 7px 9px;
    }
    .notes-source, .markdown-preview {
      width: 100%; min-height: 380px; margin: 0; padding: 14px; overflow: auto; white-space: pre-wrap;
      border: 1px solid var(--border-color-default, #30363d); border-radius: 9px;
      background: var(--background-color-default, #0d1117); color: inherit;
    }
    .notes-source {
      font-family: var(--font-mono, Consolas, monospace); font-size: 12px; line-height: 1.55;
    }
    .markdown-preview { white-space: normal; line-height: 1.6; }
    .markdown-preview > :first-child { margin-top: 0; }
    .markdown-preview > :last-child { margin-bottom: 0; }
    .markdown-preview h2 { margin: 24px 0 8px; padding-bottom: 6px; border-bottom: 1px solid var(--border-color-default, #30363d); font-size: 18px; }
    .markdown-preview h3 { margin: 20px 0 7px; font-size: 15px; }
    .markdown-preview p { margin: 8px 0; }
    .markdown-preview ul { margin: 8px 0; padding-left: 24px; }
    .markdown-preview li + li { margin-top: 6px; }
    .markdown-preview code, .confirm-code {
      border-radius: 5px; background: var(--hub-code);
      font-family: var(--font-mono, Consolas, monospace); font-size: var(--text-code-inline, 12px);
    }
    .markdown-preview code { padding: 2px 5px; }
    .markdown-preview pre { overflow: auto; margin: 12px 0; padding: 12px; border: 1px solid var(--border-color-default, #30363d); border-radius: 8px; background: var(--hub-code); }
    .markdown-preview pre code { padding: 0; background: transparent; }
    .markdown-preview a { color: var(--true-color-blue, #0969da); }
    .inline-actions { display: flex; gap: 7px; flex-wrap: wrap; }
    .view-switch { display: inline-flex; padding: 2px; border: 1px solid var(--border-color-default, #30363d); border-radius: 8px; background: var(--background-color-default, #0d1117); }
    .view-switch .action { border: 0; background: transparent; }
    .view-switch .action[aria-pressed="true"] { background: var(--hub-control); }
    .workflow { margin-top: 14px; padding-top: 14px; border-top: 1px solid var(--border-color-default, #30363d); }
    .workflow-head { display: flex; align-items: center; justify-content: space-between; gap: 10px; }
    .workflow-row { display: flex; justify-content: space-between; gap: 10px; margin-top: 7px; }
    .workflow-link { margin-inline: -7px; padding: 6px 7px; border-radius: 7px; color: inherit; text-decoration: none; }
    .workflow-link:hover { background: var(--hub-control); }
    .destination-link { color: var(--true-color-blue, #0969da); font-weight: 650; text-decoration: none; }
    .destination-link:hover { text-decoration: underline; }
    .history-panel { display: grid; gap: 14px; margin-top: 12px; padding-top: 12px; border-top: 1px solid var(--border-color-default, #30363d); }
    .history-label { margin-bottom: 6px; color: var(--text-color-muted, #8b949e); font-size: 12px; font-weight: 650; text-transform: uppercase; letter-spacing: .04em; }
    .history-list { display: grid; gap: 6px; }
    .history-item { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 12px; align-items: center; padding: 8px; border: 1px solid var(--border-color-default, #30363d); border-radius: 8px; color: inherit; text-decoration: none; }
    .history-item:hover { background: var(--hub-control); }
    .history-title { overflow: hidden; font-weight: 600; text-overflow: ellipsis; white-space: nowrap; }
    .history-meta { color: var(--text-color-muted, #8b949e); font-size: 12px; }
    .history-side { display: flex; align-items: center; gap: 8px; }
    dialog {
      width: min(480px, calc(100vw - 32px)); border: 1px solid var(--border-color-default, #30363d);
      border-radius: 12px; background: var(--background-color-default, #0d1117); color: inherit; padding: 20px;
      box-shadow: 0 16px 50px var(--hub-shadow);
    }
    dialog::backdrop { background: var(--hub-backdrop); }
    dialog h2 { margin: 0 0 8px; }
    .confirm-code { display: block; margin: 10px 0; padding: 9px; }
    .dialog-actions { display: flex; justify-content: flex-end; gap: 8px; margin-top: 15px; }
    .toast {
      position: fixed; right: 20px; bottom: 20px; max-width: min(520px, calc(100vw - 40px));
      padding: 11px 14px; border: 1px solid var(--border-color-default, #30363d); border-radius: 9px;
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
      width: 18px; height: 18px; border: 2px solid var(--border-color-default, #30363d);
      border-top-color: var(--true-color-blue, #0969da); border-radius: 50%;
      animation: operation-spin .8s linear infinite;
    }
    .operation-progress[data-state="success"] .operation-spinner,
    .operation-progress[data-state="error"] .operation-spinner { animation: none; border-width: 5px; }
    .operation-progress[data-state="success"] .operation-spinner { border-color: var(--true-color-green, #1a7f37); }
    .operation-progress[data-state="error"] .operation-spinner { border-color: var(--true-color-red, #cf222e); }
    .operation-title { font-weight: 650; }
    .operation-detail { margin-top: 2px; color: var(--text-color-muted, #8b949e); font-size: 12px; }
    .operation-link { color: var(--true-color-blue, #0969da); font-weight: 650; text-decoration: none; white-space: nowrap; }
    @keyframes operation-spin { to { transform: rotate(360deg); } }
    .loading { opacity: .65; pointer-events: none; }
    @media (max-width: 800px) {
      .summary { grid-template-columns: repeat(2, 1fr); }
      .grid { grid-template-columns: 1fr; }
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
    <div class="tabs" role="tablist" aria-label="Release platform">
      <button class="tab" id="tab-mac" role="tab" aria-controls="dashboard" aria-selected="true" data-platform="mac">macOS</button>
      <button class="tab" id="tab-windows" role="tab" aria-controls="dashboard" aria-selected="false" data-platform="windows">Windows</button>
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
      toast.style.borderColor = isError ? "var(--true-color-red, #ff7b72)" : "var(--border-color-default, #30363d)";
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
      const label = winget.status === "current" ? "Current" : "Behind release";
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

    function step(number, title, detail, action, label, disabled = false, extraClass = "") {
      return '<div class="step"><div class="step-num">' + number + '</div><div><div class="step-title">' +
        escapeHtml(title) + '</div><div class="step-detail">' + escapeHtml(detail) +
        '</div></div><button class="action ' + extraClass + '" data-action="' + action + '" type="button"' +
        (disabled ? " disabled" : "") + '>' + escapeHtml(label) + '</button></div>';
    }

    function renderDashboard() {
      const data = snapshot.platforms[platform];
      const version = releaseTags[platform] || data.pendingTag || data.suggestedTag;
      releaseTags[platform] = version;
      const notes = currentNotes();
      const notesView = notesViews[platform];
      const preparedLocally = data.pendingTag === version;
      const tagPushed = data.remoteTags?.includes(version);
      const releasePublished = data.latestRelease?.tagName === version;
      const readinessBadge = preparedLocally
        ? '<span class="badge good">Prepared locally</span>'
        : (!snapshot.git.atMainTip
          ? '<span class="badge warn">Latest main required</span>'
          : (snapshot.git.clean
            ? '<span class="badge good">Ready to prepare</span>'
            : '<span class="badge warn">Commit changes first</span>'));
      const prepareDetail = preparedLocally
        ? "Prepared locally. Undo removes only this unpushed tag and its release commit."
        : (!snapshot.git.atMainTip
          ? "Start from the latest origin/main with no additional commits."
          : "Moves Unreleased notes into a dated section, commits, and creates an annotated tag.");
      const pushDetail = tagPushed
        ? "This tag is already on origin."
        : (preparedLocally
          ? "Fast-forwards main with the release commit, then pushes the tag to start the workflow."
          : "Prepare the release tag first.");
      const workflowDetail = tagPushed
        ? "The tag push starts this automatically; use this only to recover or re-run it."
        : "Push the prepared tag before manually dispatching this workflow.";
      const wingetStep = platform === "windows"
        ? step(4, "Submit to winget", "Runs only after the GitHub release is published.", "submit_winget", "Submit", !releasePublished)
        : step(4, "Publish update metadata", "Sparkle appcast and Homebrew cask update in the macOS workflow.", "view_workflow", "In workflow", true);

      dashboard.innerHTML =
        '<article class="panel"><div class="panel-head"><div><h2>' + escapeHtml(data.label) +
        ' release path</h2><div class="muted">' + escapeHtml(data.unreleasedCount) +
        ' unreleased changelog items</div></div>' +
        readinessBadge + '</div>' +
        '<label for="version">Release tag</label><div class="version-row"><input id="version" value="' +
        escapeHtml(version) + '" spellcheck="false" /><button class="action" data-action="generate_notes" type="button">Generate notes</button>' +
        (tagPushed ? '<a class="action" href="' + escapeHtml(repositoryUrl("/tree/" + encodeURIComponent(version))) +
          '" target="_blank" rel="noreferrer">Open tag</a>' : '') +
        (releasePublished ? '<a class="action" href="' + escapeHtml(repositoryUrl("/releases/tag/" + encodeURIComponent(version))) +
          '" target="_blank" rel="noreferrer">Open release</a>' : '') +
        '</div>' +
        '<div class="steps">' +
        step(
          1,
          "Prepare release",
          prepareDetail,
          preparedLocally ? "undo_prepare" : "prepare_release",
          tagPushed ? "Released" : (preparedLocally ? "Undo" : "Prepare"),
          tagPushed || (!preparedLocally && !snapshot.git.canPrepareRelease),
          preparedLocally ? "danger" : "primary"
        ) +
        step(2, "Push release tag", pushDetail, "push_release", tagPushed ? "Pushed" : "Push", !snapshot.git.canPushRelease || !preparedLocally || tagPushed) +
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
      document.getElementById("confirm-description").textContent = description + " Type this exact phrase:";
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
      return "Release operation completed.";
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
        openConfirmation(action, { tag }, "SUBMIT " + tag,
          "This submits the published Windows release to microsoft/winget-pkgs.");
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
        renderSummary(); renderDashboard();
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
        if (result.tag) {
          releaseTags[platform] = result.tag;
        }
        renderSummary(); renderDashboard();
        if (result.tracking) {
          setOperationProgress(
            "Waiting for GitHub Actions",
            "The operation completed locally; waiting for the workflow run to appear."
          );
          trackWorkflow(result.tracking.platform, result.tracking.kind, result.tag, startedAt);
        } else {
          setOperationProgress(
            result.action === "undo_prepare" ? "Preparation undone" : "Release prepared",
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
