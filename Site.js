async (cmd, arg, account) => {
  'use strict';
  const origin = 'https://yundouxueyuan.com';
  const base = '/d/NOIPC/contest';
  const detailPattern = /^\/d\/NOIPC\/contest\/[0-9a-f]{24}$/i;
  if (location.origin !== origin || !location.pathname.startsWith('/d/NOIPC/')) {
    throw new Error('请回到本工具打开的云斗批量评测标签页。');
  }
  const norm = s => (s || '').normalize('NFKC').replace(/\s+/g, ' ').trim();
  const userId = doc => doc.querySelector('[data-dropdown-target="#menu-nav-user"] > a[href]')?.getAttribute('href')?.match(/^\/d\/NOIPC\/user\/([1-9][0-9]{0,15})$/)?.[1];
  if (!account || userId(document) !== account) throw new Error('云斗登录状态已改变，请重新开始。');
  async function request(path, wantCsv = false) {
    const url = new URL(path, origin);
    if (url.origin !== origin || !(url.pathname === base || /^\/d\/NOIPC\/contest\/[0-9a-f]{24}(?:\/scoreboard(?:\/csv)?)?$/i.test(url.pathname))) {
      throw new Error('成绩链接不在预期的云斗专区内。');
    }
    for (let i = 0; i < 2; ++i) {
      let response;
      try {
        response = await fetch(url, {credentials: 'same-origin', redirect: 'manual', cache: 'no-store', signal: AbortSignal.timeout(20000)});
      } catch (e) {
        if (!wantCsv && i === 0) { await new Promise(r => setTimeout(r, 500)); continue; }
        throw new Error('网络连接失败或超时。');
      }
      if (wantCsv && [403,429].includes(response.status)) {
        const doc = new DOMParser().parseFromString(await response.text(), 'text/html');
        const message = doc.querySelector('.error__text-container')?.textContent || '';
        const limit = message.match(/scoreboard_download\s*\(limit:\s*\d+\s+operations\s+in\s+(\d+)\s+seconds\)/i);
        const retry = response.headers.get('Retry-After');
        if (limit || response.status === 429) {
          const seconds = retry && /^\d+$/.test(retry) ? Number(retry) : limit ? Number(limit[1]) : 60;
          return {retrySeconds: Math.max(1, seconds + 1)};
        }
      }
      if (response.type === 'opaqueredirect' || [401,403].includes(response.status)) throw new Error('登录已失效、需要验证，或当前账号没有权限。');
      if (response.status === 429) throw new Error('云斗限制请求频率，请稍后再试。');
      if (!wantCsv && i === 0 && response.status >= 500) { await new Promise(r => setTimeout(r, 500)); continue; }
      if (!response.ok) throw new Error(`云斗返回 HTTP ${response.status}。`);
      const text = new TextDecoder('utf-8', {fatal: true}).decode(await response.arrayBuffer());
      if (wantCsv && !/^\s*</.test(text)) return text;
      const doc = new DOMParser().parseFromString(text, 'text/html');
      if (/用户登录|Cerberus Challenge/i.test(doc.title) || doc.querySelector('form[action$="/login"] input[name="password"]') && !doc.querySelector('a[href$="/logout"]')) {
        throw new Error('登录已失效或需要验证，请在云斗页面重新登录。');
      }
      if (wantCsv) throw new Error('云斗没有返回 CSV。');
      if (!doc.querySelector('.main') || /用户登录|系统错误|错误 -/.test(doc.title)) throw new Error('云斗返回的不是预期页面。');
      if (url.pathname === base && userId(doc) !== account) throw new Error('云斗账号已切换，请重新开始。');
      return doc;
    }
    throw new Error('请求失败。');
  }
  const provinces = 'BJ:北京:北京市;TJ:天津:天津市;HE:河北:河北省;SX:山西:山西省;NM:内蒙古:内蒙:内蒙古自治区;LN:辽宁:辽宁省;JL:吉林:吉林省;HL:黑龙江:黑龙江省;SH:上海:上海市;JS:江苏:江苏省;ZJ:浙江:浙江省;AH:安徽:安徽省;FJ:福建:福建省;JX:江西:江西省;SD:山东:山东省:魅力齐鲁大地;HA:河南:河南省;HB:湖北:湖北省;HN:湖南:湖南省;GD:广东:广东省;GX:广西:广西壮族自治区;HI:海南:海南省;CQ:重庆:重庆市;SC:四川:四川省;GZ:贵州:贵州省;YN:云南:云南省;XZ:西藏:西藏自治区;SN:陕西:陕西省;GS:甘肃:甘肃省;QH:青海:青海省;NX:宁夏:宁夏回族自治区;XJ:新疆:新疆维吾尔自治区;HK:香港:香港特别行政区;MO:澳门:澳门特别行政区;TW:台湾:台湾省'.split(';').map(s => s.split(':'));
  const type = title => /民间数据/.test(title) ? '民间' : /官方数据/.test(title) ? '官方' : '';
  function province(suffix) {
    const s = norm(suffix).replace(/^[\s·:：_—–-]+/, '').replace(/\s+(?:批量测试|批量评测|暂不公开).*$/, '').replace(/\s*[（(][A-Za-z]{2}[)）]\s*$/, '').trim();
    const found = provinces.find(p => p.some(alias => alias === s.toUpperCase()));
    return found ? found[1] : '';
  }
  async function limited(items, fn) {
    const out = Array(items.length);
    let next = 0, fatal = null;
    await Promise.all(Array.from({length: Math.min(4, items.length)}, async () => {
      while (next < items.length && !fatal) {
        const i = next++;
        try { out[i] = await fn(items[i]); } catch (e) { fatal = e; }
      }
    }));
    if (fatal) throw fatal;
    return out;
  }
  function issue(c, state, note) { return {...c, state, note, count: 0, started: c.started || 0}; }
  if (cmd === 'list') {
    const query = norm(arg.query);
    const matchQuery = query.toLowerCase();
    const lookup = query === '省选' || query.startsWith('省选 ') ? '联合' + query : query;
    const q = new URLSearchParams({q: lookup});
    const seenPages = new Set(), seenLinks = new Set(), candidates = [], ignored = [];
    let path = `${base}?${q}`;
    while (path) {
      if (seenPages.has(path) || seenPages.size >= 100) throw new Error('分页链接循环或过多，未导出不完整的结果。');
      seenPages.add(path);
      const doc = await request(path);
      if (!doc.querySelector('form input[name="q"]')) throw new Error('找不到云斗比赛列表搜索框。');
      for (const a of doc.querySelectorAll('h1.contest__title a[href]')) {
        const href = a.getAttribute('href');
        const title = norm(a.textContent);
        if (!detailPattern.test(href) || seenLinks.has(href)) continue;
        const index = title.toLowerCase().indexOf(matchQuery);
        if (index < 0 || /[A-Za-z0-9]/.test(title.charAt(index + matchQuery.length))) continue;
        seenLinks.add(href);
        const cat = type(title);
        if (cat && cat !== arg.kind) continue;
        const name = province(title.slice(index + matchQuery.length));
        const c = {title, path: href, province: name};
        if (/比赛模板|暂不公开/.test(title)) { ignored.push(issue(c,'跳过','比赛模板或标记暂不公开')); continue; }
        if (!cat || !name) { ignored.push(issue(c,'未识别',cat ? '标题无法匹配省份' : '未明确标记官方或民间数据')); continue; }
        candidates.push(c);
      }
      const next = [...doc.querySelectorAll('a[href*="page="]')].find(a => /^(?:下一页|Next)\b|^下一页/.test(norm(a.textContent)));
      const u = next ? new URL(next.getAttribute('href'), origin + base) : null;
      if (u && (u.origin !== origin || u.pathname !== base || u.searchParams.get('q') !== lookup)) throw new Error('赛事实例的分页链接已变化。');
      path = u ? u.pathname + u.search : '';
    }
    return {candidates, ignored};
  }
  if (cmd === 'details') return limited(arg, async c => {
    const doc = await request(c.path);
    const terms = Object.fromEntries([...doc.querySelectorAll('dt')].map(x => [norm(x.textContent), x.nextElementSibling]));
    const state = norm(terms['状态']?.textContent);
    if (!state) throw new Error(`${c.title}：找不到比赛状态。`);
    if (!/^(已结束|Done|Ended)$/i.test(state)) return issue(c, '跳过', '云斗比赛状态：' + state);
    if (norm(terms['参赛人数']?.textContent) === '0') return issue(c, '跳过', '无参赛者');
    const started = Number(terms['开始于']?.querySelector('[data-timestamp]')?.getAttribute('data-timestamp'));
    if (!Number.isSafeInteger(started) || started <= 0) throw new Error(`${c.title}：找不到开赛时间。`);
    if (![...doc.querySelectorAll('a[href]')].some(a => a.getAttribute('href') === c.path + '/scoreboard')) {
      throw new Error(`${c.title}：已结束，但没有成绩表链接或当前账号不可见。`);
    }
    return {...c, started, state: '待取成绩', note: '', count: 0};
  });
  if (cmd === 'boards') return limited(arg, async c => {
    const path = c.path + '/scoreboard';
    const doc = await request(path);
    if (!doc.querySelector('table .col--total_score')) throw new Error(`${c.title}：成绩表结构未识别。`);
    if (!doc.querySelector('tbody .col--problem a[href*="/record/"]')) return issue(c, '跳过', '成绩表尚无可见评测记录');
    const a = [...doc.querySelectorAll('a[href]')].find(e => e.getAttribute('href') === path + '/csv' && /导出为 CSV|Export as CSV/i.test(norm(e.textContent)));
    if (!a) throw new Error(`${c.title}：成绩表没有 CSV 导出按钮。`);
    return {...c, state: '可下载', download: a.getAttribute('href')};
  });
  if (cmd === 'downloads') {
    if (!Array.isArray(arg) || arg.length > 3) throw new Error('不能超过云斗每个下载窗口的限额。');
    return limited(arg, async c => {
      if (!detailPattern.test(c.path) || c.download !== c.path + '/scoreboard/csv') throw new Error('下载地址与成绩页不匹配。');
      try {
        const result = await request(c.download, true);
        if (typeof result === 'object') return {...c, retrySeconds: result.retrySeconds};
        return {...c, state: '已纳入', retrySeconds: 0, csv: result};
      } catch (e) {
        return {...c, retrySeconds: 0, downloadError: e.message || '下载失败。'};
      }
    });
  }
  throw new Error('未知操作。');
}
