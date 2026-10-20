export function renderHtml(nonce) {
    return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <meta name="pulse-nonce" content="${nonce}">
  <title>TinyClips Project Pulse</title>
  <script>
    (() => {
      const param = new URLSearchParams(window.location.search).get("scoutTheme");
      const theme =
        param || (window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
      document.documentElement.setAttribute("data-theme", theme);
    })();
  </script>
  <link rel="stylesheet" href="/styles.css">
  <script type="module" src="/app.mjs"></script>
</head>
<body>
  <a id="skip-to-view" class="skip" href="#content">Skip to current view</a>
  <main class="shell">
    <header class="topbar">
      <div><h1>TinyClips<span class="title-dot">.</span> <span class="light-title">Project Pulse</span></h1>
      <p class="subtitle">The team, the work, the road to 2.0.</p></div>
      <div class="refresh-group">
        <span id="freshness">Connecting to GitHub...</span>
        <div class="refresh-controls">
          <label><span class="sr-only">Automatic refresh interval</span><select id="refresh-interval"><option value="5">Every 5 min</option><option value="10">Every 10 min</option><option value="15">Every 15 min</option><option value="20">Every 20 min</option><option value="30">Every 30 min</option><option value="0">Never</option></select></label>
          <button id="refresh" class="button primary">Refresh now</button>
        </div>
      </div>
    </header>
    <div id="notice" class="notice" role="alert" hidden></div>
    <p id="announcement" class="sr-only" role="status" aria-live="polite"></p>
    <section class="team-section" aria-labelledby="team-heading">
      <div class="section-heading"><h2 id="team-heading">A small team. A lot in motion.</h2><button id="clear" class="text-button">Clear filters</button></div>
      <div id="roster" class="roster" aria-label="Filter by team member"><p class="muted">Loading the team...</p></div>
    </section>
    <div class="toolbar">
      <nav class="view-nav" aria-label="Project views">
        <button data-view="overview" aria-current="page">Overview</button><button data-view="activity">Activity</button>
        <button data-view="prs">Pull requests</button><button data-view="team">Team</button><button data-view="momentum">Momentum</button>
      </nav>
      <div class="filters">
        <label><span class="sr-only">Repository</span><select id="repository"><option value="all">All repositories</option></select></label>
        <label class="launch-toggle"><input id="launch" type="checkbox"> 2.0 Launch only</label>
        <label><span class="sr-only">Platform</span><select id="platform"><option value="all">All platforms</option><option value="mac">macOS + shared</option><option value="windows">Windows + shared</option><option value="shared">Shared / mixed</option><option value="unknown">Unclassified</option></select></label>
        <label id="activity-window"><span class="sr-only">Recent activity window</span><select id="days"><option value="7">Past 7 days</option><option value="30">Past 30 days</option></select></label>
      </div>
    </div>
    <div id="filter-context" class="filter-context"></div>
    <section id="launch-summary" class="launch-summary" aria-label="Launch progress"><p>Loading launch progress...</p></section>
    <section id="momentum-view" class="momentum-section" aria-labelledby="momentum-heading" tabindex="-1" hidden>
      <div class="section-heading">
        <div><h2 id="momentum-heading">Momentum over time</h2><p class="muted">See the rhythm of commits, pull requests, and issues opened or completed.</p></div>
        <button id="load-momentum" class="button" aria-expanded="false" aria-controls="momentum-content">Explore momentum</button>
      </div>
      <div id="momentum-content" hidden>
        <div class="momentum-toolbar">
          <label for="momentum-days">History <select id="momentum-days"><option value="7">7 days</option><option value="30" selected>30 days</option><option value="90">90 days</option></select></label>
          <button id="replay-momentum" class="button" disabled>Replay timeline</button>
          <span id="momentum-status" class="muted" role="status"></span>
        </div>
        <p id="momentum-error" class="notice" role="alert" hidden></p>
        <p id="momentum-scope" class="muted"></p>
        <div id="momentum-chart"></div>
      </div>
    </section>
    <div id="content" tabindex="-1" aria-busy="true"><p class="loading">Gathering the latest from GitHub. The first load includes recent history.</p></div>
    <footer><p id="coverage">Choose an automatic refresh interval or refresh manually anytime. No presence or session tracking.</p><a id="repo-link" href="https://github.com/jamesmontemagno/tiny-clips" target="_blank" rel="noreferrer">Open repository</a></footer>
  </main>
  <div id="celebration" class="confetti" aria-hidden="true" hidden></div>
  <aside id="updates" class="updates-toast" aria-label="Changes since refresh" hidden>
    <p id="updates-message" role="status" aria-live="polite"></p><button id="dismiss-updates" class="text-button">Dismiss</button>
  </aside>
  <dialog id="detail" aria-labelledby="detail-heading">
    <div class="dialog-header"><h2 id="detail-heading">Work item</h2><button id="close-detail" class="button">Close</button></div>
    <div id="detail-content"></div>
    <p id="detail-notice" class="notice" role="alert" hidden></p>
    <p id="detail-status" role="status"></p>
  </dialog>
</body>
</html>`;
}
