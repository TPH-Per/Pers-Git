/**
 * Per's Git (Gitea.Net) — Production Vanilla ES6 Frontend + PersMotion (GSAP-Grade MIT Animation Engine)
 * - 100% MIT Licensed, Zero External Non-MIT Libraries.
 * - Includes `PersMotion`: hardware-accelerated Web Animations API (WAAPI) & SVG stroke-dashoffset choreography
 *   (SVG path draw-in, staggered list cascades, modal spring physics, and animated number counters).
 */
(() => {
  "use strict";

  // ==========================================================================
  // 0. PERSMOTION — 100% MIT-LICENSED GSAP-GRADE CHOREOGRAPHY ENGINE
  // ==========================================================================
  const PersMotion = {
    prefersReducedMotion() {
      return window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    },

    animateLogoDraw() {
      if (this.prefersReducedMotion()) return;
      const paths = document.querySelectorAll(".svg-draw-path");
      paths.forEach((p, idx) => {
        if (typeof p.getTotalLength === "function") {
          const len = p.getTotalLength();
          p.style.strokeDasharray = `${len}`;
          p.style.strokeDashoffset = `${len}`;
          p.animate(
            [{ strokeDashoffset: len }, { strokeDashoffset: 0 }],
            {
              duration: 750,
              delay: idx * 140,
              easing: "cubic-bezier(0.22, 1, 0.36, 1)",
              fill: "forwards"
            }
          );
        }
      });

      const nodes = document.querySelectorAll(".svg-pulse-node");
      nodes.forEach((node, idx) => {
        node.animate(
          [
            { transform: "scale(0.3)", opacity: 0, transformOrigin: "center" },
            { transform: "scale(1.15)", opacity: 1, transformOrigin: "center" },
            { transform: "scale(1)", opacity: 1, transformOrigin: "center" }
          ],
          {
            duration: 520,
            delay: 240 + idx * 90,
            easing: "cubic-bezier(0.34, 1.56, 0.64, 1)",
            fill: "forwards"
          }
        );
      });
    },

    staggerIn(elements, staggerMs = 32) {
      if (this.prefersReducedMotion() || !elements) return;
      Array.from(elements).forEach((el, idx) => {
        if (typeof el.animate === "function") {
          el.animate(
            [
              { opacity: 0, transform: "translateY(8px)" },
              { opacity: 1, transform: "translateY(0)" }
            ],
            {
              duration: 340,
              delay: Math.min(idx * staggerMs, 320),
              easing: "cubic-bezier(0.22, 1, 0.36, 1)",
              fill: "both"
            }
          );
        }
      });
    },

    springIn(element) {
      if (this.prefersReducedMotion() || !element || typeof element.animate !== "function") return;
      element.animate(
        [
          { opacity: 0, transform: "translateY(10px) scale(0.97)" },
          { opacity: 1, transform: "translateY(0) scale(1)" }
        ],
        {
          duration: 260,
          easing: "cubic-bezier(0.22, 1, 0.36, 1)",
          fill: "both"
        }
      );
    }
  };

  // ==========================================================================
  // 1. PRODUCTION SNACKBAR / TOAST NOTIFICATION ENGINE
  // ==========================================================================
  const GiteaSnackbar = {
    show(title, message, type = "info", durationMs = 4200) {
      const viewport = document.getElementById("snackbarViewport");
      if (!viewport) return;

      const item = document.createElement("div");
      item.className = `snackbar snackbar-${type}`;

      const content = document.createElement("div");
      const titleEl = document.createElement("div");
      titleEl.className = "snackbar-title";
      titleEl.textContent = title;

      const msgEl = document.createElement("div");
      msgEl.className = "snackbar-message";
      msgEl.textContent = message;

      content.appendChild(titleEl);
      content.appendChild(msgEl);

      const closeBtn = document.createElement("button");
      closeBtn.className = "btn btn-sm";
      closeBtn.textContent = "✕";
      closeBtn.setAttribute("aria-label", "Dismiss notification");

      const progress = document.createElement("div");
      progress.className = "snackbar-progress";
      progress.style.animationDuration = `${durationMs}ms`;

      item.appendChild(content);
      item.appendChild(closeBtn);
      item.appendChild(progress);
      viewport.appendChild(item);

      PersMotion.springIn(item);

      const remove = () => {
        if (item.parentNode) item.parentNode.removeChild(item);
      };

      const timer = setTimeout(remove, durationMs);
      closeBtn.addEventListener("click", () => {
        clearTimeout(timer);
        remove();
      });
    },
    success(title, msg) { this.show(title, msg, "success"); },
    error(title, msg)   { this.show(title, msg, "error", 6000); },
    warning(title, msg) { this.show(title, msg, "warning", 5000); }
  };

  // ==========================================================================
  // 2. PRODUCTION DIALOG MODAL ENGINE (<dialog>)
  // ==========================================================================
  const GiteaDialog = {
    open(id) {
      const dlg = document.getElementById(id);
      if (dlg && typeof dlg.showModal === "function") {
        dlg.showModal();
        PersMotion.springIn(dlg);
      }
    },
    close(id) {
      const dlg = document.getElementById(id);
      if (dlg && dlg.open) {
        dlg.close();
      }
    },
    confirm(title, message) {
      return new Promise((resolve) => {
        const dlg = document.getElementById("confirmActionDialog");
        document.getElementById("confirmDialogTitle").textContent = title;
        document.getElementById("confirmDialogMessage").textContent = message;

        const okBtn = document.getElementById("confirmDialogOkBtn");
        const cancelBtn = document.getElementById("confirmDialogCancelBtn");

        const cleanup = (result) => {
          okBtn.removeEventListener("click", onOk);
          cancelBtn.removeEventListener("click", onCancel);
          dlg.close();
          resolve(result);
        };

        const onOk = () => cleanup(true);
        const onCancel = () => cleanup(false);

        okBtn.addEventListener("click", onOk);
        cancelBtn.addEventListener("click", onCancel);
        dlg.showModal();
        PersMotion.springIn(dlg);
      });
    }
  };

  document.querySelectorAll("[data-close-dialog]").forEach((btn) => {
    btn.addEventListener("click", () => GiteaDialog.close(btn.getAttribute("data-close-dialog")));
  });

  // ==========================================================================
  // 3. APPLICATION STATE & COOKIE-AUTHENTICATED API CLIENT
  // ==========================================================================
  const state = {
    currentUser: null,
    repositories: [],
    activeOwner: "gitea-admin",
    activeRepo: "gitea-dotnet-core",
    activeBranch: "main",
    activeTreePath: "",
    activeIssue: null,
    activePr: null,
    activeCiRun: null
  };

  async function apiFetch(path, options = {}) {
    const headers = Object.assign({
      "Content-Type": "application/json",
      "X-Gitea-Csrf": "1"
    }, options.headers || {});

    const response = await fetch(path, Object.assign({
      credentials: "same-origin"
    }, options, { headers }));

    const contentType = response.headers.get("content-type") || "";
    const isJson = contentType.includes("json");
    const payload = isJson ? await response.json() : await response.text();

    if (!response.ok) {
      const errMsg = (payload && (payload.error || payload.detail || payload.title))
        ? (payload.error || payload.detail || payload.title)
        : `HTTP ${response.status}`;
      throw new Error(errMsg);
    }
    return payload;
  }

  function unwrapItems(res) {
    if (Array.isArray(res)) return res;
    if (res && Array.isArray(res.items)) return res.items;
    return [];
  }

  // ==========================================================================
  // 4. DATA LOADING & RENDERING
  // ==========================================================================
  async function loadCurrentUser() {
    try {
      const user = await apiFetch("/api/v1/auth/me");
      state.currentUser = user;
      document.getElementById("activeActorLabel").textContent = user.username;
      document.getElementById("logoutBtn").style.display = "inline-flex";
    } catch {
      state.currentUser = null;
      document.getElementById("activeActorLabel").textContent = "Anonymous";
      document.getElementById("logoutBtn").style.display = "none";
    }
  }

  async function loadSystemHealth() {
    try {
      const health = await apiFetch("/api/v1/health");
      document.getElementById("dbProviderBadge").textContent = health.provider;
      document.getElementById("gitBinaryStatus").textContent = health.gitBinary;
    } catch (err) {
      GiteaSnackbar.error("Health Check Error", err.message);
    }
  }

  async function loadAuditLogs() {
    const container = document.getElementById("auditLogContainer");
    container.innerHTML = "";
    if (!state.currentUser || !state.currentUser.isAdmin) {
      const msg = document.createElement("div");
      msg.style.color = "var(--text-muted)";
      msg.textContent = "Sign in as an Administrator to view the security audit stream.";
      container.appendChild(msg);
      return;
    }
    try {
      const res = await apiFetch("/api/v1/audit-logs?page=1&pageSize=25");
      const logs = unwrapItems(res);
      logs.forEach((log) => {
        const row = document.createElement("div");
        row.style.padding = "0.35rem 0";
        row.style.borderBottom = "1px solid var(--border-subtle)";
        const actorStrong = document.createElement("strong");
        actorStrong.textContent = `@${log.actorUsername}: `;
        const summarySpan = document.createElement("span");
        summarySpan.textContent = log.summary;
        row.appendChild(actorStrong);
        row.appendChild(summarySpan);
        container.appendChild(row);
      });
      PersMotion.staggerIn(container.children, 20);
    } catch (err) {
      console.error(err);
    }
  }

  async function loadRepositories(searchQuery = "") {
    try {
      const qParam = searchQuery ? `?q=${encodeURIComponent(searchQuery)}` : "";
      const res = await apiFetch(`/api/v1/repos${qParam}`);
      const repos = unwrapItems(res);
      state.repositories = repos;
      document.getElementById("repoCountBadge").textContent = String(res.totalCount ?? repos.length);

      const listEl = document.getElementById("repoListContainer");
      listEl.innerHTML = "";

      repos.forEach((r) => {
        const item = document.createElement("div");
        item.className = "repo-item" + (r.ownerUsername === state.activeOwner && r.name === state.activeRepo ? " active" : "");

        const nameSpan = document.createElement("span");
        nameSpan.className = "repo-name";
        nameSpan.textContent = `${r.ownerUsername}/${r.name}` + (r.isPrivate ? " 🔒" : "");

        const metaSpan = document.createElement("span");
        metaSpan.className = "badge";
        metaSpan.textContent = `★ ${r.starsCount}`;

        item.appendChild(nameSpan);
        item.appendChild(metaSpan);

        item.addEventListener("click", () => {
          state.activeOwner = r.ownerUsername;
          state.activeRepo = r.name;
          state.activeTreePath = "";
          selectRepository(r.ownerUsername, r.name);
        });

        listEl.appendChild(item);
      });
      PersMotion.staggerIn(listEl.children, 30);
    } catch (err) {
      GiteaSnackbar.error("Failed to load repositories", err.message);
    }
  }

  async function selectRepository(owner, repo) {
    try {
      const data = await apiFetch(`/api/v1/repos/${owner}/${repo}`);
      const r = data.repository;
      const branches = data.branches || [];

      state.activeOwner = r.ownerUsername;
      state.activeRepo = r.name;
      state.activeBranch = branches.includes(r.defaultBranch) ? r.defaultBranch : (branches[0] || "main");

      document.getElementById("activeRepoTitle").textContent = `${r.ownerUsername} / ${r.name}`;
      document.getElementById("activeRepoVisibility").textContent = r.isPrivate ? "Private" : "Public";
      document.getElementById("activeRepoDesc").textContent = r.description;
      document.getElementById("starCountLabel").textContent = String(r.starsCount);
      document.getElementById("tabIssuesCount").textContent = String(r.openIssuesCount);
      document.getElementById("tabPullsCount").textContent = String(r.openPrsCount);

      const cloneUrl = `${window.location.origin}/${r.ownerUsername}/${r.name}.git`;
      document.getElementById("cloneUrlBox").textContent = cloneUrl;

      const branchSel = document.getElementById("branchSelector");
      branchSel.innerHTML = "";
      branches.forEach((b) => {
        const opt = document.createElement("option");
        opt.value = b;
        opt.textContent = b;
        if (b === state.activeBranch) opt.selected = true;
        branchSel.appendChild(opt);
      });

      await Promise.all([
        loadFileTree(),
        loadCommits(),
        loadIssues(),
        loadPullRequests(),
        loadCiWorkflowRuns(),
        loadCollaborators(),
        loadWebhooks()
      ]);
    } catch (err) {
      GiteaSnackbar.error("Repository Not Found", err.message);
    }
  }

  async function loadFileTree() {
    const tbody = document.getElementById("fileTreeTableBody");
    tbody.innerHTML = "";
    document.getElementById("blobViewerCard").style.display = "none";

    const pathLabel = document.getElementById("currentTreePathLabel");
    pathLabel.textContent = state.activeTreePath ? `/${state.activeTreePath}` : "Root Directory (/)";
    document.getElementById("treeUpBtn").style.display = state.activeTreePath ? "inline-flex" : "none";

    try {
      const url = `/api/v1/repos/${state.activeOwner}/${state.activeRepo}/tree?ref=${encodeURIComponent(state.activeBranch)}&path=${encodeURIComponent(state.activeTreePath)}`;
      const res = await apiFetch(url);
      (res.entries || []).forEach((entry) => {
        const tr = document.createElement("tr");
        tr.style.cursor = "pointer";

        const tdName = document.createElement("td");
        tdName.style.fontWeight = entry.type === "tree" ? "600" : "500";
        tdName.style.color = entry.type === "tree" ? "var(--brand-primary)" : "var(--text-primary)";
        tdName.textContent = (entry.type === "tree" ? "📁 " : "📄 ") + entry.name;

        const tdType = document.createElement("td");
        tdType.textContent = entry.type;

        const tdSha = document.createElement("td");
        tdSha.style.fontFamily = "var(--font-mono)";
        tdSha.textContent = entry.objectSha.slice(0, 8);

        const tdSize = document.createElement("td");
        tdSize.style.textAlign = "right";
        tdSize.textContent = entry.type === "tree" ? "—" : `${entry.size} B`;

        tr.appendChild(tdName);
        tr.appendChild(tdType);
        tr.appendChild(tdSha);
        tr.appendChild(tdSize);

        tr.addEventListener("click", () => {
          if (entry.type === "tree") {
            state.activeTreePath = entry.path;
            loadFileTree();
          } else {
            loadBlobContent(entry.path);
          }
        });

        tbody.appendChild(tr);
      });
      PersMotion.staggerIn(tbody.children, 24);
    } catch (err) {
      GiteaSnackbar.error("Tree Error", err.message);
    }
  }

  async function loadBlobContent(filePath) {
    try {
      const url = `/api/v1/repos/${state.activeOwner}/${state.activeRepo}/blob?ref=${encodeURIComponent(state.activeBranch)}&path=${encodeURIComponent(filePath)}`;
      const res = await apiFetch(url);
      document.getElementById("blobFileNameLabel").textContent = `${filePath} (@${state.activeBranch}, ${res.size} bytes)`;
      if (res.isBinary) {
        document.getElementById("blobContentPre").textContent = `[Binary file detected — ${res.size} bytes. Preview omitted to prevent UTF-8 corruption.]`;
      } else {
        document.getElementById("blobContentPre").textContent = res.content + (res.isTruncated ? "\n\n... [Truncated at 1 MB preview limit]" : "");
      }
      const card = document.getElementById("blobViewerCard");
      card.style.display = "block";
      PersMotion.springIn(card);
    } catch (err) {
      GiteaSnackbar.error("File Load Error", err.message);
    }
  }

  async function loadCommits() {
    const container = document.getElementById("commitsListContainer");
    container.innerHTML = "";
    try {
      const commits = await apiFetch(`/api/v1/repos/${state.activeOwner}/${state.activeRepo}/commits?ref=${encodeURIComponent(state.activeBranch)}`);
      commits.forEach((c) => {
        const row = document.createElement("div");
        row.className = "repo-item";
        row.style.cursor = "default";

        const left = document.createElement("div");
        const subj = document.createElement("div");
        subj.style.fontWeight = "600";
        subj.textContent = c.subject;

        const meta = document.createElement("div");
        meta.style.fontSize = "0.78rem";
        meta.style.color = "var(--text-muted)";
        meta.textContent = `${c.authorName} <${c.authorEmail}> committed on ${new Date(c.committedAt).toLocaleString()}`;

        left.appendChild(subj);
        left.appendChild(meta);

        const shaBadge = document.createElement("code");
        shaBadge.className = "badge";
        shaBadge.textContent = c.shortSha;

        row.appendChild(left);
        row.appendChild(shaBadge);
        container.appendChild(row);
      });
    } catch (err) {
      console.error(err);
    }
  }

  async function loadIssues() {
    const container = document.getElementById("issuesListContainer");
    container.innerHTML = "";
    try {
      const res = await apiFetch(`/api/v1/repos/${state.activeOwner}/${state.activeRepo}/issues`);
      const issues = unwrapItems(res);
      issues.forEach((iss) => {
        const row = document.createElement("div");
        row.className = "repo-item";

        const left = document.createElement("div");
        const title = document.createElement("div");
        title.style.fontWeight = "600";
        title.textContent = `#${iss.issueIndex} ${iss.title}`;

        const sub = document.createElement("div");
        sub.style.fontSize = "0.78rem";
        sub.style.color = "var(--text-muted)";
        sub.textContent = `Opened by @${iss.authorUsername} • ${iss.commentsCount} comments • Labels: ${iss.labelsCsv || "none"}`;

        left.appendChild(title);
        left.appendChild(sub);

        const stateBadge = document.createElement("span");
        stateBadge.className = `badge badge-${iss.state}`;
        stateBadge.textContent = iss.state.toUpperCase();

        row.appendChild(left);
        row.appendChild(stateBadge);
        row.addEventListener("click", () => openIssueDetail(iss.issueIndex));
        container.appendChild(row);
      });
    } catch (err) {
      console.error(err);
    }
  }

  async function openIssueDetail(issueIndex) {
    try {
      const bundle = await apiFetch(`/api/v1/repos/${state.activeOwner}/${state.activeRepo}/issues/${issueIndex}`);
      state.activeIssue = bundle.issue;

      const card = document.getElementById("issueDetailCard");
      card.style.display = "block";
      PersMotion.springIn(card);
      document.getElementById("issueDetailTitle").textContent = `#${bundle.issue.issueIndex} ${bundle.issue.title} [${bundle.issue.state.toUpperCase()}]`;
      document.getElementById("issueDetailBody").textContent = bundle.issue.body;
      document.getElementById("toggleIssueStateBtn").textContent = bundle.issue.state === "open" ? "Close Issue" : "Reopen Issue";

      const commentsEl = document.getElementById("issueCommentsContainer");
      commentsEl.innerHTML = "";
      (bundle.comments || []).forEach((c) => {
        const item = document.createElement("div");
        item.style.marginBottom = "0.75rem";
        item.style.padding = "0.65rem";
        item.style.background = "var(--bg-elevated)";
        item.style.borderRadius = "var(--radius-sm)";

        const head = document.createElement("div");
        head.style.fontSize = "0.78rem";
        head.style.fontWeight = "700";
        head.textContent = `@${c.authorUsername} • ${new Date(c.createdAt).toLocaleString()}`;

        const body = document.createElement("div");
        body.style.marginTop = "0.25rem";
        body.textContent = c.body;

        item.appendChild(head);
        item.appendChild(body);
        commentsEl.appendChild(item);
      });
    } catch (err) {
      GiteaSnackbar.error("Failed to load issue", err.message);
    }
  }

  async function loadPullRequests() {
    const container = document.getElementById("pullsListContainer");
    container.innerHTML = "";
    try {
      const res = await apiFetch(`/api/v1/repos/${state.activeOwner}/${state.activeRepo}/pulls`);
      const pulls = unwrapItems(res);
      pulls.forEach((pr) => {
        const row = document.createElement("div");
        row.className = "repo-item";

        const left = document.createElement("div");
        const title = document.createElement("div");
        title.style.fontWeight = "600";
        title.textContent = `#${pr.pullIndex} ${pr.title}`;

        const sub = document.createElement("div");
        sub.style.fontSize = "0.78rem";
        sub.style.color = "var(--text-muted)";
        sub.textContent = `${pr.sourceBranch} → ${pr.targetBranch} • by @${pr.authorUsername}`;

        left.appendChild(title);
        left.appendChild(sub);

        const badge = document.createElement("span");
        badge.className = `badge badge-${pr.state}`;
        badge.textContent = pr.state.toUpperCase();

        row.appendChild(left);
        row.appendChild(badge);
        row.addEventListener("click", () => openPullRequestDetail(pr.pullIndex));
        container.appendChild(row);
      });
    } catch (err) {
      console.error(err);
    }
  }

  async function openPullRequestDetail(pullIndex) {
    try {
      const data = await apiFetch(`/api/v1/repos/${state.activeOwner}/${state.activeRepo}/pulls/${pullIndex}`);
      const pr = data.pullRequest;
      const diff = data.diff;
      state.activePr = pr;

      const card = document.getElementById("prDetailCard");
      card.style.display = "block";
      PersMotion.springIn(card);
      document.getElementById("prDetailTitle").textContent = `PR #${pr.pullIndex}: ${pr.title} [${pr.state.toUpperCase()}]`;
      document.getElementById("prDetailMeta").textContent =
        `Branch ${pr.sourceBranch} into ${pr.targetBranch} • ${diff.filesChanged} file(s) changed` +
        (pr.mergeCommitSha ? ` • Merged commit ${pr.mergeCommitSha.slice(0, 8)}` : "");
      document.getElementById("prDetailBody").textContent = pr.body;

      document.getElementById("mergePrBtn").style.display = pr.state === "open" ? "inline-flex" : "none";
      const togglePrBtn = document.getElementById("togglePrStateBtn");
      togglePrBtn.style.display = pr.isMerged ? "none" : "inline-flex";
      togglePrBtn.textContent = pr.state === "open" ? "Close PR" : "Reopen PR";

      const diffViewer = document.getElementById("prDiffViewer");
      diffViewer.innerHTML = "";
      const rawDiff = diff.unifiedDiff || "No diff changes.";
      rawDiff.split("\n").forEach((line) => {
        const span = document.createElement("span");
        if (line.startsWith("+") && !line.startsWith("+++")) {
          span.className = "diff-line-add";
        } else if (line.startsWith("-") && !line.startsWith("---")) {
          span.className = "diff-line-del";
        }
        span.textContent = line + "\n";
        diffViewer.appendChild(span);
      });
    } catch (err) {
      GiteaSnackbar.error("Failed to load PR diff", err.message);
    }
  }

  async function loadCiWorkflowRuns() {
    const container = document.getElementById("ciRunsListContainer");
    if (!container) return;
    container.innerHTML = "";
    try {
      const res = await apiFetch(`/api/v1/repos/${state.activeOwner}/${state.activeRepo}/actions/runs`);
      const runs = unwrapItems(res);
      const countEl = document.getElementById("tabActionsCount");
      if (countEl) countEl.textContent = String(res.totalCount ?? runs.length);

      if (runs.length === 0) {
        const empty = document.createElement("div");
        empty.className = "card-body";
        empty.style.color = "var(--text-muted)";
        empty.textContent = "No CI/CD workflow runs recorded yet. Click '▶ Run CI/CD Pipeline' or push a commit.";
        container.appendChild(empty);
        return;
      }

      runs.forEach((run) => {
        const row = document.createElement("div");
        row.className = "repo-item";

        const left = document.createElement("div");
        const title = document.createElement("div");
        title.style.fontWeight = "600";
        title.textContent = `Run #${run.runIndex} — ${run.workflowName}`;

        const sub = document.createElement("div");
        sub.style.fontSize = "0.78rem";
        sub.style.color = "var(--text-muted)";
        sub.textContent = `[${run.eventType}] branch ${run.branch} (${(run.commitSha || "").slice(0, 8)}) • by @${run.triggeredByUsername} • ${run.durationMs.toFixed(1)}ms`;

        left.appendChild(title);
        left.appendChild(sub);

        const badge = document.createElement("span");
        badge.className = run.status === "success"
          ? "badge badge-open"
          : (run.status === "failed" ? "badge badge-closed" : "badge badge-merged");
        badge.textContent = run.status.toUpperCase();

        row.appendChild(left);
        row.appendChild(badge);
        row.addEventListener("click", () => openCiRunDetail(run.runIndex));
        container.appendChild(row);
      });
    } catch (err) {
      console.error(err);
    }
  }

  async function openCiRunDetail(runIndex) {
    try {
      const run = await apiFetch(`/api/v1/repos/${state.activeOwner}/${state.activeRepo}/actions/runs/${runIndex}`);
      state.activeCiRun = run;

      const card = document.getElementById("ciRunDetailCard");
      if (!card) return;
      card.style.display = "block";
      PersMotion.springIn(card);

      document.getElementById("ciRunDetailTitle").textContent = `CI/CD Run #${run.runIndex}: ${run.workflowName}`;
      const badge = document.getElementById("ciRunStatusBadge");
      badge.className = run.status === "success"
        ? "badge badge-open"
        : (run.status === "failed" ? "badge badge-closed" : "badge badge-merged");
      badge.textContent = run.status.toUpperCase();

      document.getElementById("ciRunDetailMeta").textContent =
        `Event: ${run.eventType} • Branch: ${run.branch} • Commit: ${(run.commitSha || "").slice(0, 12)} • Triggered by @${run.triggeredByUsername} • Duration: ${run.durationMs.toFixed(1)} ms`;
      document.getElementById("ciRunLogsViewer").textContent = run.logsText || "Pipeline queued...";
    } catch (err) {
      GiteaSnackbar.error("Failed to load CI/CD run logs", err.message);
    }
  }

  async function loadCollaborators() {
    const container = document.getElementById("collaboratorsListContainer");
    container.innerHTML = "";
    try {
      const collabs = await apiFetch(`/api/v1/repos/${state.activeOwner}/${state.activeRepo}/collaborators`);
      if (collabs.length === 0) {
        const empty = document.createElement("div");
        empty.className = "card-body";
        empty.style.color = "var(--text-muted)";
        empty.textContent = "No external collaborators added.";
        container.appendChild(empty);
        return;
      }
      collabs.forEach((c) => {
        const row = document.createElement("div");
        row.className = "repo-item";
        row.style.cursor = "default";

        const left = document.createElement("div");
        left.style.fontWeight = "600";
        left.textContent = `@${c.username}`;

        const right = document.createElement("span");
        right.className = "badge badge-open";
        right.textContent = c.permission.toUpperCase();

        row.appendChild(left);
        row.appendChild(right);
        container.appendChild(row);
      });
    } catch {
    }
  }

  async function loadWebhooks() {
    const container = document.getElementById("webhooksListContainer");
    const delivContainer = document.getElementById("webhookDeliveriesContainer");
    container.innerHTML = "";
    if (delivContainer) delivContainer.innerHTML = "";

    if (!state.currentUser) {
      const msg = document.createElement("div");
      msg.className = "card-body";
      msg.style.color = "var(--text-muted)";
      msg.textContent = "Sign in as Repository Owner or Admin to view webhooks and delivery logs.";
      container.appendChild(msg);
      return;
    }
    try {
      const hooks = await apiFetch(`/api/v1/repos/${state.activeOwner}/${state.activeRepo}/webhooks`);
      if (hooks.length === 0) {
        const empty = document.createElement("div");
        empty.className = "card-body";
        empty.style.color = "var(--text-muted)";
        empty.textContent = "No webhooks configured for this repository.";
        container.appendChild(empty);
      } else {
        hooks.forEach((h) => {
          const row = document.createElement("div");
          row.className = "repo-item";
          row.style.cursor = "default";

          const left = document.createElement("div");
          left.style.fontWeight = "600";
          left.textContent = `${h.targetUrl} ${h.hasSecret ? "(HMAC-SHA256 Secret Active)" : ""}`;

          const right = document.createElement("span");
          right.className = "badge badge-open";
          right.textContent = h.eventsCsv;

          row.appendChild(left);
          row.appendChild(right);
          container.appendChild(row);
        });
      }

      if (delivContainer) {
        const deliveries = await apiFetch(`/api/v1/repos/${state.activeOwner}/${state.activeRepo}/webhooks/deliveries`);
        if (deliveries.length === 0) {
          const emptyDeliv = document.createElement("div");
          emptyDeliv.className = "card-body";
          emptyDeliv.style.color = "var(--text-muted)";
          emptyDeliv.textContent = "No webhook deliveries recorded yet.";
          delivContainer.appendChild(emptyDeliv);
        } else {
          deliveries.forEach((d) => {
            const row = document.createElement("div");
            row.className = "repo-item";
            row.style.cursor = "default";

            const left = document.createElement("div");
            const title = document.createElement("div");
            title.style.fontWeight = "600";
            title.textContent = `[${d.eventName}] → ${d.targetUrl} (Attempt #${d.attemptCount}, ${d.durationMs.toFixed(1)}ms)`;
            const sig = document.createElement("div");
            sig.style.fontSize = "0.75rem";
            sig.style.fontFamily = "var(--font-mono)";
            sig.style.color = "var(--text-muted)";
            sig.textContent = `X-Gitea-Signature-256: ${d.signatureHeader.slice(0, 32)}...`;
            left.appendChild(title);
            left.appendChild(sig);

            const badge = document.createElement("span");
            badge.className = d.status === "delivered" ? "badge badge-open" : "badge badge-closed";
            badge.textContent = `${d.status.toUpperCase()} (${d.statusCode || "ERR"})`;

            row.appendChild(left);
            row.appendChild(badge);
            delivContainer.appendChild(row);
          });
        }
      }
    } catch {
      const msg = document.createElement("div");
      msg.className = "card-body";
      msg.style.color = "var(--text-muted)";
      msg.textContent = "Requires Repository Admin permission to inspect webhooks.";
      container.appendChild(msg);
    }
  }

  async function loadPatTokens() {
    const container = document.getElementById("patListContainer");
    container.innerHTML = "";
    if (!state.currentUser) {
      container.textContent = "Sign in to manage Personal Access Tokens.";
      return;
    }
    try {
      const tokens = await apiFetch("/api/v1/auth/tokens");
      if (tokens.length === 0) {
        container.textContent = "No Personal Access Tokens generated.";
        return;
      }
      tokens.forEach((t) => {
        const row = document.createElement("div");
        row.style.display = "flex";
        row.style.justifyContent = "space-between";
        row.style.alignItems = "center";
        row.style.padding = "0.45rem 0";
        row.style.borderBottom = "1px solid var(--border-subtle)";

        const info = document.createElement("div");
        info.textContent = `${t.name} (${t.tokenPrefix}...) — Scopes: [${t.scopes}]`;

        const revokeBtn = document.createElement("button");
        revokeBtn.type = "button";
        revokeBtn.className = "btn btn-danger btn-sm";
        revokeBtn.textContent = "Revoke";
        revokeBtn.addEventListener("click", async () => {
          try {
            await apiFetch(`/api/v1/auth/tokens/${t.id}`, { method: "DELETE" });
            GiteaSnackbar.success("Token Revoked", `Revoked PAT '${t.name}'`);
            await loadPatTokens();
          } catch (err) {
            GiteaSnackbar.error("Revoke Error", err.message);
          }
        });

        row.appendChild(info);
        row.appendChild(revokeBtn);
        container.appendChild(row);
      });
    } catch (err) {
      container.textContent = err.message;
    }
  }

  // Tabs switching with PersMotion spring transition
  document.querySelectorAll(".tab-btn").forEach((btn) => {
    btn.addEventListener("click", () => {
      document.querySelectorAll(".tab-btn").forEach((b) => b.classList.remove("active"));
      document.querySelectorAll(".tab-panel").forEach((p) => (p.style.display = "none"));
      btn.classList.add("active");
      const target = document.getElementById(`tabPanel-${btn.getAttribute("data-tab")}`);
      if (target) {
        target.style.display = "block";
        PersMotion.springIn(target);
      }
    });
  });

  // Replay SVG Logo draw-in choreography on click
  document.getElementById("navHomeBtn").addEventListener("click", () => PersMotion.animateLogoDraw());
  const replayLogoBtn = document.getElementById("replayLogoAnimBtn");
  if (replayLogoBtn) {
    replayLogoBtn.addEventListener("click", () => {
      PersMotion.animateLogoDraw();
      GiteaSnackbar.show("Per’s Git Emblem", "Replayed SVG branch-and-merge vector animation.");
    });
  }

  // Hot Backup button
  const hotBackupBtn = document.getElementById("triggerHotBackupBtn");
  if (hotBackupBtn) {
    hotBackupBtn.addEventListener("click", async () => {
      try {
        const res = await apiFetch("/api/v1/admin/backup", { method: "POST" });
        GiteaSnackbar.success("SQLite Hot Backup Completed", `Snapshot saved: ${res.fileName}`);
        await loadAuditLogs();
      } catch (err) {
        GiteaSnackbar.error("Hot Backup Requires Admin", err.message);
      }
    });
  }

  // CI/CD Actions buttons
  const refreshCiBtn = document.getElementById("refreshCiRunsBtn");
  if (refreshCiBtn) {
    refreshCiBtn.addEventListener("click", async () => {
      await loadCiWorkflowRuns();
      GiteaSnackbar.show("CI/CD Actions", "Refreshed workflow run list.");
    });
  }

  const triggerCiBtn = document.getElementById("triggerCiWorkflowBtn");
  if (triggerCiBtn) {
    triggerCiBtn.addEventListener("click", async () => {
      try {
        const run = await apiFetch(`/api/v1/repos/${state.activeOwner}/${state.activeRepo}/actions/runs`, {
          method: "POST",
          body: JSON.stringify({
            branch: state.activeBranch,
            workflowName: "Per's Git Production CI/CD Pipeline"
          })
        });
        GiteaSnackbar.success("CI/CD Pipeline Completed", `Run #${run.runIndex} finished with ${run.status.toUpperCase()} (${run.durationMs.toFixed(1)}ms)`);
        await loadCiWorkflowRuns();
        await openCiRunDetail(run.runIndex);
        await loadAuditLogs();
      } catch (err) {
        GiteaSnackbar.error("CI/CD Trigger Failed", err.message);
      }
    });
  }

  // Test Webhook Delivery button
  const testWhBtn = document.getElementById("testWebhookDeliveryBtn");
  if (testWhBtn) {
    testWhBtn.addEventListener("click", async () => {
      try {
        await apiFetch(`/api/v1/repos/${state.activeOwner}/${state.activeRepo}/webhooks/test`, { method: "POST" });
        GiteaSnackbar.success("Webhook Event Enqueued", "Dispatched test push payload to background outbox.");
        setTimeout(() => loadWebhooks(), 600);
      } catch (err) {
        GiteaSnackbar.error("Webhook Test Failed", err.message);
      }
    });
  }

  document.getElementById("toggleThemeBtn").addEventListener("click", () => {
    const root = document.documentElement;
    const next = root.getAttribute("data-theme") === "light" ? "dark" : "light";
    root.setAttribute("data-theme", next);
    GiteaSnackbar.show("Theme Updated", `Switched interface to Claude ${next} palette.`);
  });

  document.getElementById("repoSearchInput").addEventListener("input", (e) => {
    loadRepositories(e.target.value);
  });

  document.getElementById("branchSelector").addEventListener("change", (e) => {
    state.activeBranch = e.target.value;
    state.activeTreePath = "";
    loadFileTree();
    loadCommits();
    GiteaSnackbar.show("Branch Switched", `Viewing branch '${state.activeBranch}'`);
  });

  document.getElementById("treeUpBtn").addEventListener("click", () => {
    const parts = state.activeTreePath.split("/").filter(Boolean);
    parts.pop();
    state.activeTreePath = parts.join("/");
    loadFileTree();
  });

  document.getElementById("closeBlobViewerBtn").addEventListener("click", () => {
    document.getElementById("blobViewerCard").style.display = "none";
  });

  document.getElementById("copyCloneUrlBtn").addEventListener("click", async () => {
    const url = document.getElementById("cloneUrlBox").textContent;
    if (navigator.clipboard) {
      await navigator.clipboard.writeText(url);
    }
    GiteaSnackbar.success("Copied Git Smart HTTP URL", url);
  });

  document.getElementById("starRepoBtn").addEventListener("click", async () => {
    try {
      const res = await apiFetch(`/api/v1/repos/${state.activeOwner}/${state.activeRepo}/star`, { method: "POST" });
      document.getElementById("starCountLabel").textContent = String(res.starsCount);
      await loadRepositories();
      GiteaSnackbar.success(res.starred ? "Repository Starred" : "Star Removed", `Total stars: ${res.starsCount}`);
    } catch (err) {
      GiteaSnackbar.error("Authentication Required", err.message);
    }
  });

  document.getElementById("openCreateRepoDialogBtn").addEventListener("click", () => GiteaDialog.open("createRepoDialog"));
  document.getElementById("openCommitFileDialogBtn").addEventListener("click", () => {
    document.getElementById("commitBranchInput").value = state.activeBranch;
    GiteaDialog.open("commitFileDialog");
  });
  document.getElementById("openCreateBranchDialogBtn").addEventListener("click", () => {
    document.getElementById("fromBranchRefInput").value = state.activeBranch;
    GiteaDialog.open("createBranchDialog");
  });
  document.getElementById("openCreateIssueDialogBtn").addEventListener("click", () => GiteaDialog.open("createIssueDialog"));
  document.getElementById("openCreatePrDialogBtn").addEventListener("click", () => GiteaDialog.open("createPrDialog"));
  document.getElementById("openAddCollabDialogBtn").addEventListener("click", () => GiteaDialog.open("addCollabDialog"));
  document.getElementById("openCreateWebhookDialogBtn").addEventListener("click", () => GiteaDialog.open("createWebhookDialog"));
  document.getElementById("openPatDialogBtn").addEventListener("click", async () => {
    document.getElementById("generatedPatBox").style.display = "none";
    await loadPatTokens();
    GiteaDialog.open("patDialog");
  });
  document.getElementById("openAuthDialogBtn").addEventListener("click", () => GiteaDialog.open("authDialog"));

  document.getElementById("logoutBtn").addEventListener("click", async () => {
    try {
      await apiFetch("/api/v1/auth/logout", { method: "POST" });
      await loadCurrentUser();
      await loadRepositories();
      await loadAuditLogs();
      GiteaSnackbar.success("Signed Out", "Your session cookie and token have been revoked.");
    } catch (err) {
      GiteaSnackbar.error("Sign Out Error", err.message);
    }
  });

  document.getElementById("authLoginForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    try {
      const payload = {
        username: document.getElementById("loginUsername").value.trim(),
        password: document.getElementById("loginPassword").value
      };
      const res = await apiFetch("/api/v1/auth/login", {
        method: "POST",
        body: JSON.stringify(payload)
      });
      await loadCurrentUser();
      await loadRepositories();
      await selectRepository(state.activeOwner, state.activeRepo);
      await loadAuditLogs();
      GiteaDialog.close("authDialog");
      GiteaSnackbar.success("Signed In", `Authenticated as @${res.user.username}`);
    } catch (err) {
      GiteaSnackbar.error("Authentication Failed", err.message);
    }
  });

  document.getElementById("authRegisterForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    try {
      const payload = {
        username: document.getElementById("regUsername").value.trim(),
        email: document.getElementById("regEmail").value.trim(),
        fullName: document.getElementById("regFullName").value.trim(),
        password: document.getElementById("regPassword").value
      };
      const res = await apiFetch("/api/v1/auth/register", {
        method: "POST",
        body: JSON.stringify(payload)
      });
      await loadCurrentUser();
      await loadRepositories();
      GiteaDialog.close("authDialog");
      GiteaSnackbar.success("Account Registered", `Welcome, @${res.user.username}!`);
    } catch (err) {
      GiteaSnackbar.error("Registration Failed", err.message);
    }
  });

  document.getElementById("createPatForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    try {
      const payload = {
        name: document.getElementById("patNameInput").value.trim(),
        scopes: document.getElementById("patScopesInput").value.trim(),
        expiresInDays: 30
      };
      const res = await apiFetch("/api/v1/auth/tokens", {
        method: "POST",
        body: JSON.stringify(payload)
      });
      document.getElementById("generatedPatValue").textContent = res.rawToken;
      document.getElementById("generatedPatBox").style.display = "block";
      await loadPatTokens();
      GiteaSnackbar.success("Personal Access Token Created", `Prefix: ${res.tokenPrefix}...`);
    } catch (err) {
      GiteaSnackbar.error("PAT Creation Failed", err.message);
    }
  });

  document.getElementById("createRepoForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    try {
      const payload = {
        name: document.getElementById("repoNameInput").value.trim(),
        description: document.getElementById("repoDescInput").value.trim(),
        isPrivate: document.getElementById("repoIsPrivateInput").checked,
        defaultBranch: document.getElementById("repoDefaultBranchInput").value.trim() || "main",
        initializeReadme: document.getElementById("repoInitReadmeInput").checked
      };
      const created = await apiFetch("/api/v1/repos", {
        method: "POST",
        body: JSON.stringify(payload)
      });
      GiteaDialog.close("createRepoDialog");
      e.target.reset();
      GiteaSnackbar.success("Repository Created", `Initialized ${created.ownerUsername}/${created.name}`);
      await loadRepositories();
      await selectRepository(created.ownerUsername, created.name);
      await loadAuditLogs();
    } catch (err) {
      GiteaSnackbar.error("Repository Creation Failed", err.message);
    }
  });

  document.getElementById("commitFileForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    try {
      const payload = {
        branch: document.getElementById("commitBranchInput").value.trim(),
        filePath: document.getElementById("commitPathInput").value.trim(),
        content: document.getElementById("commitContentInput").value,
        commitMessage: document.getElementById("commitMessageInput").value.trim()
      };
      const res = await apiFetch(`/api/v1/repos/${state.activeOwner}/${state.activeRepo}/commits`, {
        method: "POST",
        body: JSON.stringify(payload)
      });
      GiteaDialog.close("commitFileDialog");
      GiteaSnackbar.success("Committed via Plumbing", `Commit ${res.sha.slice(0, 8)} pushed to ${res.branch} (CI Run #${res.ciRunIndex || 1})`);
      await selectRepository(state.activeOwner, state.activeRepo);
      await loadAuditLogs();
    } catch (err) {
      GiteaSnackbar.error("Git Commit Failed", err.message);
    }
  });

  document.getElementById("createBranchForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    try {
      const payload = {
        newBranchName: document.getElementById("newBranchNameInput").value.trim(),
        fromRef: document.getElementById("fromBranchRefInput").value.trim()
      };
      await apiFetch(`/api/v1/repos/${state.activeOwner}/${state.activeRepo}/branches`, {
        method: "POST",
        body: JSON.stringify(payload)
      });
      GiteaDialog.close("createBranchDialog");
      GiteaSnackbar.success("Branch Created", `Created branch '${payload.newBranchName}'`);
      await selectRepository(state.activeOwner, state.activeRepo);
      await loadAuditLogs();
    } catch (err) {
      GiteaSnackbar.error("Create Branch Failed", err.message);
    }
  });

  document.getElementById("createIssueForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    try {
      const payload = {
        title: document.getElementById("issueTitleInput").value.trim(),
        body: document.getElementById("issueBodyInput").value.trim(),
        labelsCsv: document.getElementById("issueLabelsInput").value.trim()
      };
      const issue = await apiFetch(`/api/v1/repos/${state.activeOwner}/${state.activeRepo}/issues`, {
        method: "POST",
        body: JSON.stringify(payload)
      });
      GiteaDialog.close("createIssueDialog");
      e.target.reset();
      GiteaSnackbar.success("Issue Opened", `Created Issue #${issue.issueIndex}`);
      await selectRepository(state.activeOwner, state.activeRepo);
      await openIssueDetail(issue.issueIndex);
      await loadAuditLogs();
    } catch (err) {
      GiteaSnackbar.error("Create Issue Failed", err.message);
    }
  });

  document.getElementById("toggleIssueStateBtn").addEventListener("click", async () => {
    if (!state.activeIssue) return;
    const nextState = state.activeIssue.state === "open" ? "closed" : "open";
    const confirmed = await GiteaDialog.confirm(
      "Confirm Issue State Change",
      `Are you sure you want to mark Issue #${state.activeIssue.issueIndex} as ${nextState.toUpperCase()}?`
    );
    if (!confirmed) return;

    try {
      await apiFetch(`/api/v1/repos/${state.activeOwner}/${state.activeRepo}/issues/${state.activeIssue.issueIndex}/state`, {
        method: "PATCH",
        body: JSON.stringify({
          state: nextState,
          expectedRowVersion: state.activeIssue.rowVersion
        })
      });
      GiteaSnackbar.success("Issue Updated", `Issue #${state.activeIssue.issueIndex} is now ${nextState}`);
      await selectRepository(state.activeOwner, state.activeRepo);
      await openIssueDetail(state.activeIssue.issueIndex);
      await loadAuditLogs();
    } catch (err) {
      GiteaSnackbar.error("Concurrency / Authorization Error", err.message);
    }
  });

  document.getElementById("addCommentForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    if (!state.activeIssue) return;
    try {
      const body = document.getElementById("newCommentBody").value.trim();
      await apiFetch(`/api/v1/repos/${state.activeOwner}/${state.activeRepo}/issues/${state.activeIssue.issueIndex}/comments`, {
        method: "POST",
        body: JSON.stringify({ body })
      });
      document.getElementById("newCommentBody").value = "";
      GiteaSnackbar.success("Comment Added", `Posted comment on Issue #${state.activeIssue.issueIndex}`);
      await openIssueDetail(state.activeIssue.issueIndex);
      await loadIssues();
    } catch (err) {
      GiteaSnackbar.error("Comment Error", err.message);
    }
  });

  document.getElementById("createPrForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    try {
      const payload = {
        title: document.getElementById("prTitleInput").value.trim(),
        sourceBranch: document.getElementById("prSourceBranchInput").value.trim(),
        targetBranch: document.getElementById("prTargetBranchInput").value.trim(),
        body: document.getElementById("prBodyInput").value.trim()
      };
      const pr = await apiFetch(`/api/v1/repos/${state.activeOwner}/${state.activeRepo}/pulls`, {
        method: "POST",
        body: JSON.stringify(payload)
      });
      GiteaDialog.close("createPrDialog");
      GiteaSnackbar.success("Pull Request Opened", `Created PR #${pr.pullIndex} & triggered CI/CD`);
      await selectRepository(state.activeOwner, state.activeRepo);
      await openPullRequestDetail(pr.pullIndex);
      await loadAuditLogs();
    } catch (err) {
      GiteaSnackbar.error("Pull Request Error", err.message);
    }
  });

  document.getElementById("togglePrStateBtn").addEventListener("click", async () => {
    if (!state.activePr) return;
    const nextState = state.activePr.state === "open" ? "closed" : "open";
    try {
      await apiFetch(`/api/v1/repos/${state.activeOwner}/${state.activeRepo}/pulls/${state.activePr.pullIndex}/state`, {
        method: "PATCH",
        body: JSON.stringify({
          state: nextState,
          expectedRowVersion: state.activePr.rowVersion
        })
      });
      GiteaSnackbar.success("Pull Request Updated", `PR #${state.activePr.pullIndex} is now ${nextState}`);
      await selectRepository(state.activeOwner, state.activeRepo);
      await openPullRequestDetail(state.activePr.pullIndex);
    } catch (err) {
      GiteaSnackbar.error("Update PR Error", err.message);
    }
  });

  document.getElementById("mergePrBtn").addEventListener("click", async () => {
    if (!state.activePr) return;
    const strategy = document.getElementById("mergeStrategySelect").value;
    const confirmed = await GiteaDialog.confirm(
      "Merge Pull Request Atomically",
      `Merge '${state.activePr.sourceBranch}' into '${state.activePr.targetBranch}' using strategy '${strategy}' (3-phase DB CAS + git merge-tree)?`
    );
    if (!confirmed) return;

    try {
      const merged = await apiFetch(`/api/v1/repos/${state.activeOwner}/${state.activeRepo}/pulls/${state.activePr.pullIndex}/merge`, {
        method: "POST",
        body: JSON.stringify({
          mergeStrategy: strategy,
          customCommitMessage: `Merge PR #${state.activePr.pullIndex}: ${state.activePr.title}`,
          expectedRowVersion: state.activePr.rowVersion
        })
      });
      GiteaSnackbar.success("Pull Request Merged!", `Commit ${merged.mergeCommitSha.slice(0, 8)} merged into ${merged.targetBranch}`);
      await selectRepository(state.activeOwner, state.activeRepo);
      await openPullRequestDetail(state.activePr.pullIndex);
      await loadAuditLogs();
    } catch (err) {
      GiteaSnackbar.error("Merge Conflict / Concurrency Rejection", err.message);
    }
  });

  document.getElementById("addCollabForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    try {
      const payload = {
        username: document.getElementById("collabUsernameInput").value.trim(),
        permission: document.getElementById("collabPermissionSelect").value
      };
      await apiFetch(`/api/v1/repos/${state.activeOwner}/${state.activeRepo}/collaborators`, {
        method: "POST",
        body: JSON.stringify(payload)
      });
      GiteaDialog.close("addCollabDialog");
      GiteaSnackbar.success("Collaborator Updated", `Granted ${payload.permission} to @${payload.username}`);
      await loadCollaborators();
      await loadAuditLogs();
    } catch (err) {
      GiteaSnackbar.error("Collaborator Error", err.message);
    }
  });

  document.getElementById("createWebhookForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    try {
      const payload = {
        targetUrl: document.getElementById("webhookUrlInput").value.trim(),
        secret: document.getElementById("webhookSecretInput").value.trim(),
        eventsCsv: document.getElementById("webhookEventsInput").value.trim()
      };
      await apiFetch(`/api/v1/repos/${state.activeOwner}/${state.activeRepo}/webhooks`, {
        method: "POST",
        body: JSON.stringify(payload)
      });
      GiteaDialog.close("createWebhookDialog");
      GiteaSnackbar.success("Webhook Added", payload.targetUrl);
      await loadWebhooks();
      await loadAuditLogs();
    } catch (err) {
      GiteaSnackbar.error("Webhook Validation / SSRF Error", err.message);
    }
  });

  (async function init() {
    PersMotion.animateLogoDraw();
    await loadCurrentUser();
    await loadSystemHealth();
    await loadRepositories();
    await selectRepository(state.activeOwner, state.activeRepo);
    await loadAuditLogs();
  })();
})();
