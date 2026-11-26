"use strict";

const byId = (id) => document.getElementById(id);
const form = byId("query-form");
const fields = byId("query-fields");
const query = byId("query");
const start = byId("start");
const stop = byId("stop");
const exit = byId("exit");
const download = byId("download");
const phase = byId("phase");
const message = byId("message");
const actionMessage = byId("action-message");
const countdownRow = byId("countdown-row");
const countdown = byId("countdown");
const provinces = byId("provinces");
const resultMessage = byId("result-message");
const token = document.querySelector('meta[name="yundou-token"]').content;

const phases = new Map([
  ["idle", ["待查询", "neutral", "输入赛事名称，开始汇总。"]],
  ["working", ["汇总中", "active", "正在查找场次并汇总各省成绩。"]],
  ["login", ["等待登录", "active", "需要在云斗页面登录或验证。"]],
  ["waiting", ["等待下载", "active", "按云斗的下载限额分批获取成绩。"]],
  ["done", ["汇总结束", "success", "成绩汇总已结束。"]],
  ["failed", ["汇总失败", "error", "未能完成汇总，请重试。"]],
  ["stopped", ["已停止", "neutral", "本次汇总已停止。"]]
]);
const activePhases = new Set(["working", "login", "waiting"]);
const number = new Intl.NumberFormat("zh-CN");
let status = null;
let connected = false;
let exited = false;
let busy = "";
let pollTimer;
let currentPoll;
let renderedRows;

class RequestError extends Error {}

function setText(node, text) {
  if (node.textContent !== text) node.textContent = text;
}

function setPhase(label, tone, detail) {
  setText(phase, label);
  phase.dataset.tone = tone;
  setText(message, detail);
}

function renderControls() {
  const active = activePhases.has(status?.phase);
  const locked = !connected || exited || Boolean(busy);
  fields.disabled = locked || active;
  start.disabled = fields.disabled;
  setText(start, busy === "start" ? "正在启动…" : "开始汇总");
  stop.hidden = !active || exited;
  stop.disabled = locked || !active;
  setText(stop, busy === "stop" ? "正在停止…" : "停止汇总");
  exit.disabled = locked;
  setText(exit, busy === "exit" ? "正在退出…" : "退出程序");
  const ready = !exited && status?.canDownload === true;
  download.disabled = locked || !ready;
  setText(download, busy === "download" ? "正在准备…" : "下载 Excel");
  resultMessage.classList.toggle("result-ready", ready);
  setText(resultMessage, exited ? "程序已退出。" : ready ? "工作簿已生成。" : "工作簿生成后即可下载。");
}

function renderCountdown() {
  const wakeAt = status?.wakeAt;
  const show = connected && !exited && status?.phase === "waiting" && Number.isFinite(wakeAt) && wakeAt > 0;
  countdownRow.hidden = !show;
  if (!show) return;
  const seconds = Math.max(0, Math.ceil((wakeAt - Date.now()) / 1000));
  const minutes = Math.floor(seconds / 60);
  const rest = seconds % 60;
  setText(countdown, seconds === 0 ? "即将开始" : minutes > 0 ? `${minutes} 分 ${String(rest).padStart(2, "0")} 秒后` : `${rest} 秒后`);
}

function renderProvinces(rows, phaseName) {
  const emptyText = phaseName === "idle"
    ? "开始汇总后，这里会显示各省进度。"
    : activePhases.has(phaseName)
      ? "正在等待省份记录…"
      : "本次没有省份记录。";
  const key = rows.length ? JSON.stringify(rows) : emptyText;
  if (key === renderedRows) return;
  renderedRows = key;
  const fragment = document.createDocumentFragment();
  if (!rows.length) {
    const row = document.createElement("tr");
    const cell = row.insertCell();
    cell.colSpan = 4;
    cell.className = "empty";
    cell.textContent = emptyText;
    fragment.append(row);
  } else {
    for (const province of rows) {
      const row = document.createElement("tr");
      const name = document.createElement("th");
      name.scope = "row";
      name.textContent = province.name || "—";
      row.append(name);
      const progress = row.insertCell();
      progress.textContent = province.status || "—";
      if (province.status === "已保存" || province.status === "已复用") progress.className = "positive";
      if (province.status === "下载失败") progress.className = "negative";
      const source = row.insertCell();
      source.className = "source";
      source.textContent = province.source || "—";
      const count = province.count;
      const hasCount = Number.isSafeInteger(count) && count >= 0 &&
        !(["待下载", "下载失败"].includes(province.status) && count === 0);
      row.insertCell().textContent = hasCount ? number.format(count) : "—";
      fragment.append(row);
    }
  }
  provinces.replaceChildren(fragment);
}

function renderStatus() {
  const [label, tone, fallback] = phases.get(status.phase);
  setPhase(label, tone, status.message || fallback);
  byId("login-help").hidden = status.phase !== "login";
  byId("account-row").hidden = !status.account;
  setText(byId("account"), status.account || "");
  renderProvinces(status.provinces, status.phase);
  renderCountdown();
  renderControls();
}

async function request(path, options = {}) {
  return fetch(path, {
    ...options,
    headers: { ...options.headers, "X-Yundou-Token": token },
    mode: "same-origin",
    redirect: "error",
    cache: "no-store"
  });
}

async function requestError(response, fallback) {
  const data = await response.json().catch(() => null);
  return new RequestError(typeof data?.error === "string" && data.error ? data.error : fallback);
}

async function poll() {
  const controller = new AbortController();
  currentPoll = controller;
  const timeout = setTimeout(() => controller.abort(), 6000);
  try {
    const response = await request("/api/status", { signal: controller.signal });
    if (!response.ok) throw new Error("Status unavailable");
    const data = await response.json();
    if (!phases.has(data?.phase) || !Array.isArray(data.provinces)) throw new Error("Invalid status");
    if (currentPoll !== controller) return;
    connected = true;
    status = data;
    renderStatus();
  } catch {
    if (currentPoll !== controller) return;
    connected = false;
    setPhase("连接中断", "error", "暂时无法连接本机程序，正在重试。若程序已关闭，请重新打开。");
    byId("login-help").hidden = true;
    renderCountdown();
    renderControls();
  } finally {
    clearTimeout(timeout);
    if (currentPoll === controller) {
      currentPoll = undefined;
      pollTimer = setTimeout(poll, connected && activePhases.has(status?.phase) ? 1000 : 2000);
    }
  }
}

async function perform(action, run, fallback) {
  if (!connected || exited || busy) return;
  busy = action;
  clearTimeout(pollTimer);
  const pending = currentPoll;
  currentPoll = undefined;
  pending?.abort();
  setText(actionMessage, "");
  renderControls();
  try {
    await run();
  } catch (error) {
    setText(actionMessage, error instanceof RequestError ? error.message : fallback);
  } finally {
    if (!exited) await poll();
    busy = "";
    renderControls();
  }
}

async function post(action, fallback, body) {
  const options = { method: "POST", signal: AbortSignal.timeout(12000) };
  if (body) {
    options.headers = { "Content-Type": "application/x-www-form-urlencoded;charset=UTF-8" };
    options.body = body;
  }
  const response = await request(`/api/${action}`, options);
  if (!response.ok) throw await requestError(response, fallback);
  const data = await response.json();
  if (data?.ok !== true) throw new RequestError(typeof data?.error === "string" && data.error ? data.error : fallback);
}

query.addEventListener("input", () => query.setCustomValidity(""));
form.addEventListener("submit", (event) => {
  event.preventDefault();
  if (fields.disabled) return;
  const name = query.value.trim();
  if (!name) {
    query.setCustomValidity("请输入赛事名称。");
    query.reportValidity();
    return;
  }
  const values = new FormData(form);
  const body = new URLSearchParams({ query: name, kind: values.get("kind"), refresh: values.has("refresh") ? "1" : "0" });
  const fallback = "未能开始汇总，请重试。";
  void perform("start", async () => {
    await post("start", fallback, body);
    byId("receipt-title").focus();
  }, fallback);
});

stop.addEventListener("click", () => {
  const fallback = "未能停止汇总，请重试。";
  void perform("stop", () => post("stop", fallback), fallback);
});

exit.addEventListener("click", () => {
  const fallback = "未能退出程序，请重试。";
  void perform("exit", async () => {
    await post("exit", fallback);
    exited = true;
    connected = false;
    setPhase("已退出", "neutral", "本机程序已退出，可以关闭此页。");
    byId("login-help").hidden = true;
    byId("account-row").hidden = true;
    setText(byId("account"), "");
    renderCountdown();
  }, fallback);
});

function resultFilename(disposition) {
  const fallback = "云斗成绩汇总.xlsx";
  let filename = "";
  if (disposition) {
    const extended = /(?:^|;)\s*filename\*\s*=\s*UTF-8'[^']*'([^;]+)/i.exec(disposition);
    if (extended) {
      try { filename = decodeURIComponent(extended[1].trim()); } catch { /* Try filename instead. */ }
    }
    if (!filename) {
      const plain = /(?:^|;)\s*filename\s*=\s*(?:"((?:\\.|[^"\\])*)"|([^;]*))/i.exec(disposition);
      if (plain) filename = plain[1] === undefined ? plain[2].trim() : plain[1].replace(/\\(["\\])/g, "$1");
    }
  }
  filename = filename.split(/[\\/]/).pop().replace(/[\u0000-\u001f\u007f<>:"|?*]/g, "_").trim().replace(/[. ]+$/, "");
  return filename || fallback;
}

download.addEventListener("click", () => {
  if (!status?.canDownload) return;
  const fallback = "未能下载工作簿，请重试。";
  void perform("download", async () => {
    const response = await request("/api/result", { signal: AbortSignal.timeout(60000) });
    if (!response.ok) throw await requestError(response, fallback);
    const blob = await response.blob();
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = resultFilename(response.headers.get("Content-Disposition"));
    document.body.append(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 60000);
  }, fallback);
});

if (token && token !== "__SESSION_TOKEN__") {
  void poll();
  setInterval(renderCountdown, 1000);
} else {
  setPhase("未连接", "error", "请从云斗成绩汇总程序打开此页。");
}
