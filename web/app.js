"use strict";

const byId = (id) => document.getElementById(id);
const form = byId("query-form");
const fields = byId("query-fields");
const query = byId("query");
const start = byId("start");
const stop = byId("stop");
const exit = byId("exit");
const download = byId("download");
const openResult = byId("open-result");
const resultPath = byId("result-path");
const mode = byId("mode");
const provinceOptions = byId("province-options");
const provinceChoices = byId("province-choices");
const selectedCount = byId("selected-count");
const codeOptions = byId("code-options");
const groupOptions = byId("group-options");
const group = byId("group");
const cutoffOptions = byId("cutoff-options");
const cutoffFields = byId("cutoff-fields");
const cutoffMessage = byId("cutoff-message");
const phase = byId("phase");
const message = byId("message");
const actionMessage = byId("action-message");
const countdownRow = byId("countdown-row");
const countdown = byId("countdown");
const provinces = byId("provinces");
const resultMessage = byId("result-message");
const token = document.querySelector('meta[name="yundou-token"]').content;

// Canonical Chinese names in the same order as Site.js.
const provinceNames = "北京 天津 河北 山西 内蒙古 辽宁 吉林 黑龙江 上海 江苏 浙江 安徽 福建 江西 山东 河南 湖北 湖南 广东 广西 海南 重庆 四川 贵州 云南 西藏 陕西 甘肃 青海 宁夏 新疆 香港 澳门 台湾".split(" ");
const startLabels = { all: "开始汇总", province: "导出省份成绩", code: "导出选手代码" };
const maxCutoff = 9999999;
const cutoffDrafts = new Map();
const savedCutoffs = new Map();
let displayedCutoffQuery = "";

const phases = new Map([
  ["idle", ["待开始", "neutral", "输入赛事名称并选择任务模式。"]],
  ["working", ["处理中", "active", "正在查找场次并处理本次任务。"]],
  ["login", ["等待登录", "active", "需要在云斗页面登录或验证。"]],
  ["waiting", ["等待下载", "active", "按云斗的下载限额分批获取数据。"]],
  ["done", ["任务完成", "success", "本次任务已完成。"]],
  ["failed", ["任务失败", "error", "未能完成任务，请重试。"]],
  ["stopped", ["已停止", "neutral", "本次任务已停止。"]]
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

function setSection(section, visible) {
  section.hidden = !visible;
  section.disabled = !visible;
}

const provinceInputs = provinceNames.map((name) => {
  const label = document.createElement("label");
  label.className = "option";
  const input = document.createElement("input");
  input.type = "checkbox";
  input.name = "provinces";
  input.value = name;
  label.append(input, name);
  provinceChoices.append(label);
  return input;
});

function selectedProvinces() {
  return provinceInputs.filter((input) => input.checked).map((input) => input.value);
}

function noipQuery() {
  const name = query.value.trim();
  return /^NOIP\s+\d{4}$/i.test(name) ? name.toUpperCase().replace(/\s+/g, " ") : "";
}

let cutoffInputs = new Map();

function renderOptions() {
  const currentMode = mode.value;
  const selected = selectedProvinces();
  const noip = currentMode === "province" ? noipQuery() : "";
  const cutoffQuery = group.value !== "full" && selected.length > 0 ? noip : "";
  setSection(provinceOptions, currentMode !== "all");
  setSection(codeOptions, currentMode === "code");
  setSection(groupOptions, Boolean(noip));
  setSection(cutoffOptions, Boolean(cutoffQuery));
  setText(selectedCount, `已选择 ${selected.length} 个省份`);
  if (!cutoffQuery) return;

  if (cutoffQuery !== displayedCutoffQuery || selected.some((name) => !cutoffInputs.has(name)) || selected.length !== cutoffInputs.size) {
    displayedCutoffQuery = cutoffQuery;
    cutoffInputs = new Map();
    const fragment = document.createDocumentFragment();
    for (const name of selected) {
      const label = document.createElement("label");
      label.className = "cutoff-row";
      const input = document.createElement("input");
      input.className = "field-control";
      input.type = "number";
      input.inputMode = "numeric";
      input.min = "0";
      input.max = String(maxCutoff);
      input.step = "1";
      input.required = true;
      input.setAttribute("aria-label", `${name}高中实际参赛人数`);
      input.addEventListener("input", () => {
        if (!cutoffDrafts.has(cutoffQuery)) cutoffDrafts.set(cutoffQuery, new Map());
        cutoffDrafts.get(cutoffQuery).set(name, input.value);
      });
      cutoffInputs.set(name, input);
      label.append(name, input);
      fragment.append(label);
    }
    cutoffFields.replaceChildren(fragment);
    fillCutoffs();
  }
  if (connected && !savedCutoffs.has(cutoffQuery)) void loadCutoffs(cutoffQuery);
}

function fillCutoffs() {
  if (cutoffOptions.hidden) return;
  const entry = savedCutoffs.get(displayedCutoffQuery);
  const drafts = cutoffDrafts.get(displayedCutoffQuery);
  for (const [name, input] of cutoffInputs) {
    // A cleared field is also an edit; never replace it with the saved value.
    if (!drafts?.has(name)) input.value = entry?.values?.get(name) ?? "";
    else if (input.value !== drafts.get(name)) input.value = drafts.get(name);
  }
  setText(cutoffMessage, entry?.message || "");
}

async function loadCutoffs(name) {
  const entry = { message: "正在读取上次填写的人数…" };
  savedCutoffs.set(name, entry);
  fillCutoffs();
  try {
    const params = new URLSearchParams({ query: name });
    const response = await request(`/api/cutoffs?${params}`, { signal: AbortSignal.timeout(6000) });
    if (!response.ok) throw new Error("Cutoffs unavailable");
    const data = await response.json();
    if (!data || typeof data !== "object" || Array.isArray(data)) throw new Error("Invalid cutoffs");
    entry.values = new Map(Object.entries(data).filter(([province, count]) =>
      provinceNames.includes(province) && Number.isInteger(count) && count >= 0 && count <= maxCutoff));
    entry.message = "";
  } catch {
    entry.message = "未能读取上次填写的人数，请手动填写。";
  }
  if (displayedCutoffQuery === name) fillCutoffs();
}

function renderControls() {
  const active = activePhases.has(status?.phase);
  const locked = !connected || exited || Boolean(busy);
  fields.disabled = locked || active;
  start.disabled = fields.disabled;
  setText(start, busy === "start" ? "正在启动…" : startLabels[mode.value]);
  stop.hidden = !active || exited;
  stop.disabled = locked || !active;
  setText(stop, busy === "stop" ? "正在停止…" : "停止任务");
  exit.disabled = locked;
  setText(exit, busy === "exit" ? "正在退出…" : "退出程序");

  const hasTask = !exited && status && status.phase !== "idle";
  const folderMode = status?.mode === "province" || status?.mode === "code";
  const canDownload = hasTask && !folderMode && status.canDownload === true;
  const canOpen = hasTask && folderMode && status.canOpen === true;
  download.hidden = !hasTask || folderMode;
  openResult.hidden = !hasTask || !folderMode;
  download.disabled = locked || !canDownload;
  openResult.disabled = locked || !canOpen;
  setText(download, busy === "download" ? "正在准备…" : "下载 Excel");
  setText(openResult, busy === "open" ? "正在打开…" : "打开结果文件夹");

  const path = hasTask && folderMode && typeof status.resultDirectory === "string" ? status.resultDirectory : "";
  byId("result-path-row").hidden = !path;
  setText(resultPath, path);
  resultMessage.classList.toggle("result-ready", Boolean(canDownload || canOpen));
  let resultText = "任务完成后，这里会显示结果。";
  if (exited) resultText = "程序已退出。";
  else if (canDownload) resultText = "工作簿已生成。";
  else if (canOpen) resultText = "文件已保存到本机。";
  else if (status?.phase === "failed" || status?.phase === "stopped" || status?.phase === "done") {
    resultText = path ? "结果目录如下。" : "本次没有可打开或下载的结果。";
  }
  setText(resultMessage, resultText);
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

function renderProvinces(rows, phaseName, taskMode) {
  const emptyText = phaseName === "idle"
    ? "启动任务后，这里会显示省份进度。"
    : activePhases.has(phaseName)
      ? "正在等待省份进度…"
      : "本次没有省份回执。";
  const showFiles = taskMode === "code";
  setText(byId("count-heading"), showFiles ? "文件数" : "记录数");
  const key = rows.length ? JSON.stringify([taskMode, rows]) : emptyText;
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
      const count = showFiles ? (province.fileCount ?? province.count) : (province.rowCount ?? province.count);
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
  renderProvinces(status.provinces, status.phase, status.mode);
  renderCountdown();
  renderOptions();
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

async function perform(action, run, fallback, feedback = actionMessage) {
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
    setText(feedback, error instanceof RequestError ? error.message : fallback);
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

const formMessage = byId("form-message");
query.addEventListener("input", () => {
  query.setCustomValidity("");
  renderOptions();
});
form.addEventListener("input", () => setText(formMessage, ""));
form.addEventListener("change", (event) => {
  if (event.target === mode || event.target === group || provinceChoices.contains(event.target)) {
    renderOptions();
    renderControls();
  }
});
form.addEventListener("submit", (event) => {
  event.preventDefault();
  if (fields.disabled) return;
  const name = query.value.trim();
  if (!name) {
    query.setCustomValidity("请输入赛事名称。");
    query.reportValidity();
    return;
  }
  const taskMode = mode.value;
  const selected = taskMode === "all" ? [] : selectedProvinces();
  if (taskMode !== "all" && !selected.length) {
    setText(formMessage, "请至少选择一个省份。");
    provinceInputs[0].focus();
    return;
  }
  const taskGroup = taskMode === "province" && noipQuery() ? group.value : "full";
  const cutoffs = {};
  if (taskGroup !== "full") {
    for (const province of selected) {
      const input = cutoffInputs.get(province);
      const count = input.valueAsNumber;
      if (!Number.isInteger(count) || count < 0 || count > maxCutoff) {
        input.reportValidity();
        return;
      }
      cutoffs[province] = count;
    }
  }
  const values = new FormData(form);
  const body = new URLSearchParams({
    query: name,
    kind: values.get("kind"),
    refresh: values.has("refresh") ? "1" : "0",
    mode: taskMode,
    provinces: selected.join(","),
    compress: values.has("compress") ? "1" : "0",
    group: taskGroup,
    cutoffs: JSON.stringify(cutoffs)
  });
  setText(formMessage, "");
  const fallback = "未能启动任务，请重试。";
  void perform("start", async () => {
    await post("start", fallback, body);
    byId("receipt-title").focus();
  }, fallback, formMessage);
});

stop.addEventListener("click", () => {
  const fallback = "未能停止任务，请重试。";
  void perform("stop", () => post("stop", fallback), fallback);
});

openResult.addEventListener("click", () => {
  if (!status?.canOpen || (status.mode !== "province" && status.mode !== "code")) return;
  const fallback = "未能打开结果文件夹，请重试。";
  void perform("open", () => post("open", fallback), fallback);
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
