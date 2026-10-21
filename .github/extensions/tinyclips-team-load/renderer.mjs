export function renderHtml(nonce, theme = null) {
    const themeAttr = theme === "light" || theme === "dark" ? ` data-theme="${theme}" data-theme-locked` : "";
    return `<!doctype html>
<html lang="en"${themeAttr}>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="team-load-nonce" content="${nonce}">
<meta name="referrer" content="no-referrer">
<title>Team load</title>
<link rel="icon" href="data:,">
<link rel="stylesheet" href="/styles.css">
<script type="module" src="/app.mjs"></script>
</head>
<body>
<div class="app" id="home">
  <header class="topbar">
    <div class="title">
      <h1>Team load</h1>
      <p class="meta"><button class="scope-btn" type="button" id="scope-btn" aria-haspopup="dialog" aria-expanded="false" aria-controls="scope">Tracking…</button><span class="dim" id="updated"></span></p>
    </div>
    <div class="toolbar">
      <button class="btn-icon" type="button" id="theme-toggle" aria-label="Switch theme"></button>
      <button class="btn-icon" type="button" id="refresh" aria-label="Refresh" data-icon="sync"></button>
    </div>
    <div class="scope-backdrop" id="scope-backdrop" hidden></div>
    <div class="scope-panel" id="scope" role="dialog" aria-labelledby="scope-h" hidden>
      <div class="sp-head"><h2 id="scope-h">Tracking</h2><button class="btn-icon" type="button" id="scope-close" aria-label="Close" data-icon="x"></button></div>
      <div class="seg" role="tablist" aria-label="Scope">
        <button type="button" role="tab" id="tab-people" data-tab="people"></button>
        <button type="button" role="tab" id="tab-repos" data-tab="repos"></button>
      </div>
      <div class="sp-search"><span data-icon-inline="search"></span><input id="scope-q" type="text" autocomplete="off" spellcheck="false"></div>
      <ul class="sp-list" id="scope-list" role="listbox" aria-multiselectable="true"></ul>
      <p class="sp-status" id="scope-status" role="status" aria-live="polite"></p>
    </div>
  </header>
  <div class="notice" id="notice" role="alert" hidden></div>
  <div class="grid">
    <section class="box roster-box" aria-labelledby="roster-h">
      <div class="box-head">
        <h2 id="roster-h">Who’s on what</h2>
        <p class="insight" id="insight"></p>
      </div>
      <ul class="roster" id="roster" aria-busy="true"></ul>
      <p class="move-help" id="move-help">Expand a person, then drag an issue onto another teammate. <span class="sr-only">Or focus an issue and press Space to pick it up, Tab to a teammate and press Enter to move it. Escape cancels.</span></p>
      <p class="sr-only" id="move-status" role="status" aria-live="polite"></p>
    </section>
    <div class="side-col">
      <section class="box triage-box" aria-labelledby="triage-h">
        <div class="box-head">
          <h2 id="triage-h">Needs an owner <span class="Counter" id="triage-count"></span></h2>
        </div>
        <ul class="tickets" id="tickets" aria-busy="true"></ul>
      </section>
      <section class="box momentum-box" id="momentum"></section>
    </div>
  </div>
</div>
<div class="app detail-view" id="detail" hidden></div>
<div class="app detail-view person-view" id="person" hidden></div>
<div class="toast" id="toast" role="status" aria-live="polite"></div>
</body>
</html>`;
}
