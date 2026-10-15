/* ============================================================================
   数据库运维看板前端逻辑
   - 零依赖：无框架、无 CDN、无构建步骤，直接用原生 DOM API 渲染。
   - 安全：所有来自服务端的数据一律经 textContent 写入，绝不拼接 innerHTML，
     因为库名、说明、错误信息与 SQL 预览都源自数据库与日志文件，必须视为不可信内容。
   - 口径：审计时间戳是 UTC，页面统一转换为浏览器本地时区显示，并保留 UTC 原值。
   ========================================================================= */
(function () {
  'use strict';

  /** 轮询间隔（毫秒）：探测较重，审计较轻，分别设定。 */
  const POLL_OVERVIEW_MS = 60000;
  const POLL_AUDIT_MS = 30000;

  /** 手动刷新的节流窗口，避免连点造成请求风暴。 */
  const MANUAL_THROTTLE_MS = 800;

  /** 审计统计窗口（天）与最近记录条数。 */
  const SUMMARY_DAYS = 7;
  const RECENT_LIMIT = 50;

  const COLORS = {
    write: '#22d3ee',
    guardRejected: '#f59e0b',
    writeFailed: '#ef4444',
    commit: '#10b981',
    dryRun: '#f59e0b',
    unknown: '#64748b'
  };

  const EVENT_LABELS = {
    write: '写操作',
    guard_rejected: '守卫拒绝',
    write_failed: '执行失败'
  };

  const EVENT_COLORS = {
    write: COLORS.write,
    guard_rejected: COLORS.guardRejected,
    write_failed: COLORS.writeFailed
  };

  const MODE_LABELS = {
    commit: '已提交',
    'dry-run': '演练回滚'
  };

  const MODE_COLORS = {
    commit: COLORS.commit,
    'dry-run': COLORS.dryRun
  };

  const REDUCED_MOTION = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  /** 缓存最近一次成功的数据，供只读重绘（如切换自动刷新）使用。 */
  const state = {
    overview: null,
    summary: null,
    recent: null,
    lastLoadedAt: null,
    overviewTimer: null,
    auditTimer: null,
    busy: false,
    lastManualAt: 0
  };

  const dom = {};
  ['chip-transport', 'chip-write', 'chip-apikey', 'updated-at', 'auto-refresh', 'refresh-btn',
    'progress', 'kpi-grid', 'db-grid', 'db-note', 'audit-note', 'donut-events', 'legend-events',
    'donut-modes', 'legend-modes', 'bars-trend', 'dist-database', 'recent-note', 'recent-body',
    'footer-config', 'toast', 'toast-title', 'toast-text', 'toast-retry'
  ].forEach(function (id) {
    dom[id] = document.getElementById(id);
  });

  // ---------------------------------------------------------------- 通用工具

  /** GET 一个 JSON 端点；非 2xx 或非 JSON 都抛出可读错误。 */
  async function getJson(path) {
    const response = await fetch(path, {
      method: 'GET',
      cache: 'no-store',
      headers: { Accept: 'application/json' }
    });

    if (!response.ok) {
      throw new Error('接口 ' + path + ' 返回 HTTP ' + response.status);
    }

    try {
      return await response.json();
    } catch (error) {
      throw new Error('接口 ' + path + ' 返回的内容不是合法 JSON');
    }
  }

  /** 创建元素并安全写入文本。 */
  function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) {
      node.className = className;
    }
    if (text !== undefined && text !== null) {
      node.textContent = String(text);
    }
    return node;
  }

  function clear(node) {
    while (node.firstChild) {
      node.removeChild(node.firstChild);
    }
  }

  /** 数字格式化：千分位 + 缺省值。 */
  function num(value, fallback) {
    if (value === null || value === undefined || value === '') {
      return fallback === undefined ? '0' : fallback;
    }
    const parsed = Number(value);
    if (!Number.isFinite(parsed)) {
      return fallback === undefined ? '—' : fallback;
    }
    return parsed.toLocaleString('zh-CN');
  }

  /** UTC ISO-8601 → { local, utc } 两种展示形态。 */
  function formatTimestamp(iso) {
    if (!iso) {
      return { local: '—', utc: '' };
    }

    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) {
      return { local: String(iso), utc: '' };
    }

    const pad = function (value) { return String(value).padStart(2, '0'); };
    const localText = date.getFullYear() + '-' + pad(date.getMonth() + 1) + '-' + pad(date.getDate()) +
      ' ' + pad(date.getHours()) + ':' + pad(date.getMinutes()) + ':' + pad(date.getSeconds());

    return { local: localText, utc: iso.replace('T', ' ').replace(/\.\d+/, '').replace('Z', ' UTC') };
  }

  /** 数值入场动画（尊重系统减弱动效偏好）。 */
  function animateNumber(node, target) {
    if (REDUCED_MOTION || !Number.isFinite(target)) {
      node.textContent = num(target);
      return;
    }

    const from = Number(node.dataset.value || 0);
    node.dataset.value = String(target);

    if (from === target) {
      node.textContent = num(target);
      return;
    }

    const duration = 420;
    const startedAt = performance.now();

    function step(now) {
      const progress = Math.min((now - startedAt) / duration, 1);
      const eased = 1 - Math.pow(1 - progress, 3);
      node.textContent = num(Math.round(from + (target - from) * eased));
      if (progress < 1) {
        requestAnimationFrame(step);
      }
    }

    requestAnimationFrame(step);
  }

  /** 空态占位（含可选提示）。 */
  function emptyNode(text, hint) {
    const wrapper = el('div', 'empty', text);
    if (hint) {
      wrapper.appendChild(el('span', 'empty__hint', hint));
    }
    return wrapper;
  }

  // -------------------------------------------------------------- 顶部工具条

  function renderChips(overview) {
    const config = (overview && overview.config) || {};

    dom['chip-transport'].textContent = '传输 ' + (config.transport || '—') + ' · 端口 ' + num(config.port, '—');
    dom['chip-transport'].className = 'chip ' + (config.transport === 'http' ? 'chip--ok' : 'chip--muted');

    dom['chip-write'].textContent = '写操作 ' + (config.allow_write ? '已启用' : '已禁用');
    dom['chip-write'].className = 'chip ' + (config.allow_write ? 'chip--warn' : 'chip--ok');

    dom['chip-apikey'].textContent = '/mcp 鉴权 ' + (config.api_key_configured ? '已配置' : '未配置（拒绝所有请求）');
    dom['chip-apikey'].className = 'chip ' + (config.api_key_configured ? 'chip--ok' : 'chip--bad');
  }

  // ---------------------------------------------------------------- KPI 指标卡

  function kpiCard(label, value, subText, tone) {
    const card = el('div', 'kpi' + (tone ? ' kpi--' + tone : ''));
    card.appendChild(el('span', 'kpi__label', label));

    const valueNode = el('span', 'kpi__value');
    valueNode.dataset.value = '0';
    card.appendChild(valueNode);
    animateNumber(valueNode, value);

    if (subText) {
      card.appendChild(el('span', 'kpi__sub', subText));
    }

    return card;
  }

  function renderKpi(overview, summary) {
    const grid = dom['kpi-grid'];
    clear(grid);

    const summaryBlock = (overview && overview.summary) || {};
    const total = Number(summaryBlock.database_count || 0);
    const connected = Number(summaryBlock.connected_count || 0);
    const failed = Number(summaryBlock.failed_count || 0);

    const byEvent = (summary && summary.by_event) || {};
    const byMode = (summary && summary.by_mode) || {};
    const writes = Number(byEvent.write || 0);
    const rejects = Number(byEvent.guard_rejected || 0);
    const affectedRows = summary ? Number(summary.affected_rows_total || 0) : 0;
    const skipped = summary ? Number(summary.skipped_lines || 0) : 0;

    grid.appendChild(kpiCard('授权数据库', total, '白名单内可访问', 'info'));
    grid.appendChild(kpiCard(
      '连通正常',
      connected,
      failed > 0 ? '异常 ' + num(failed) + ' 个' : '全部可达',
      failed > 0 ? 'bad' : 'ok'
    ));
    grid.appendChild(kpiCard(
      '写操作 · 近 ' + SUMMARY_DAYS + ' 日',
      writes,
      '已提交 ' + num(byMode.commit) + ' / 演练 ' + num(byMode['dry-run']),
      writes > 0 ? 'warn' : ''
    ));
    grid.appendChild(kpiCard('守卫拒绝', rejects, '被安全规则拦下的语句', rejects > 0 ? 'warn' : 'ok'));
    grid.appendChild(kpiCard('提交影响行数', affectedRows, '仅统计已提交的写操作', 'info'));
    grid.appendChild(kpiCard(
      '审计损坏行',
      skipped,
      skipped > 0 ? '存在无法解析的日志行' : '日志解析正常',
      skipped > 0 ? 'bad' : 'ok'
    ));

    // 最后更新时间与提示
    const generatedAt = overview && overview.generated_at;
    const stamp = formatTimestamp(generatedAt);
    dom['updated-at'].textContent = '更新于 ' + stamp.local;
  }

  // -------------------------------------------------------------- 健康矩阵

  function renderDatabases(overview) {
    const grid = dom['db-grid'];
    clear(grid);

    const databases = (overview && overview.databases) || [];
    if (databases.length === 0) {
      grid.appendChild(emptyNode('尚未配置任何授权数据库', '请在 appsettings.json 的 SqlServer:Databases 节中添加。'));
      dom['db-note'].textContent = '';
      return;
    }

    databases.forEach(function (item) {
      const card = el('div', 'db-card' + (item.connected ? '' : ' db-card--down'));

      const head = el('div', 'db-card__head');
      head.appendChild(el('span', 'db-card__name', item.database));

      const typeBadge = el('span', 'db-card__type', item.display_name || item.type);
      typeBadge.dataset.type = item.type || '';
      head.appendChild(typeBadge);
      card.appendChild(head);

      if (item.description) {
        card.appendChild(el('p', 'db-card__desc', item.description));
      }

      const stateRow = el('div', 'db-card__state');
      stateRow.appendChild(el('span', 'db-card__dot db-card__dot--' + (item.connected ? 'ok' : 'down')));

      if (item.connected) {
        stateRow.appendChild(el('span', 'db-card__state-text', '连通正常'));
        const latency = el('span', 'db-card__latency', num(item.latency_ms));
        latency.appendChild(el('span', null, 'ms'));
        stateRow.appendChild(latency);
      } else {
        stateRow.appendChild(el('span', 'db-card__state-text', '不可达'));
      }
      card.appendChild(stateRow);

      const meta = el('div', 'db-card__meta');
      if (item.connected) {
        meta.appendChild(el('span', 'tag tag--ok', '版本 ' + (item.server_version || '未知')));
        if (item.current_database) {
          meta.appendChild(el('span', 'tag', '当前库 ' + item.current_database));
        }
      }
      meta.appendChild(el(
        'span',
        'tag ' + (item.ddl_rehearsal ? 'tag--ok' : 'tag--warn'),
        item.ddl_rehearsal ? '支持 DDL 演练' : '不支持 DDL 演练'
      ));
      meta.appendChild(el(
        'span',
        'tag ' + (item.supports_stored_procedures ? '' : 'tag--warn'),
        item.supports_stored_procedures ? '支持存储过程' : '无存储过程'
      ));
      card.appendChild(meta);

      if (!item.connected) {
        card.appendChild(el('p', 'db-card__error', item.type_error || item.error || '探测失败'));
      } else if (item.type_error) {
        card.appendChild(el('p', 'db-card__error', item.type_error));
      }

      grid.appendChild(card);
    });

    const cache = (overview && overview.cache) || {};
    dom['db-note'].textContent = cache.from_cache
      ? '探测结果来自缓存 · ' + formatTimestamp(cache.probed_at).local
      : '本次为实时探测 · ' + formatTimestamp(cache.probed_at).local;
  }

  // ------------------------------------------------------------------ 图表

  /**
   * 渲染圆环图：SVG 半径固定，多段用 stroke-dasharray 顺序拼接。
   * segments 中 value 为 0 的段跳过（不渲染零长弧），但图例仍完整列出。
   */
  function renderDonut(container, legendNode, segments, caption) {
    clear(container);
    clear(legendNode);

    const total = segments.reduce(function (sum, item) { return sum + Number(item.value || 0); }, 0);

    const radius = 62;
    const circumference = 2 * Math.PI * radius;
    const svgNs = 'http://www.w3.org/2000/svg';

    const svg = document.createElementNS(svgNs, 'svg');
    svg.setAttribute('viewBox', '0 0 148 148');
    svg.setAttribute('role', 'img');
    svg.setAttribute('aria-label', caption + '，总计 ' + total);

    const track = document.createElementNS(svgNs, 'circle');
    track.setAttribute('class', 'donut__track');
    track.setAttribute('cx', '74');
    track.setAttribute('cy', '74');
    track.setAttribute('r', String(radius));
    track.setAttribute('stroke-width', '14');
    svg.appendChild(track);

    let offset = 0;
    segments.forEach(function (segment) {
      const value = Number(segment.value || 0);
      if (value <= 0 || total <= 0) {
        return;
      }

      const length = (value / total) * circumference;
      const arc = document.createElementNS(svgNs, 'circle');
      arc.setAttribute('class', 'donut__arc');
      arc.setAttribute('cx', '74');
      arc.setAttribute('cy', '74');
      arc.setAttribute('r', String(radius));
      arc.setAttribute('stroke-width', '14');
      arc.setAttribute('stroke', segment.color);
      arc.setAttribute('stroke-dasharray', length + ' ' + (circumference - length));
      arc.setAttribute('stroke-dashoffset', String(-offset));
      svg.appendChild(arc);

      offset += length;
    });

    container.appendChild(svg);

    const center = el('div', 'donut__center');
    center.appendChild(el('span', 'donut__total', num(total)));
    center.appendChild(el('span', 'donut__caption', caption));
    container.appendChild(center);

    segments.forEach(function (segment) {
      const value = Number(segment.value || 0);
      const percent = total > 0 ? (value / total) * 100 : 0;

      const row = el('li', 'legend__item');
      const swatch = el('span', 'legend__swatch');
      swatch.style.background = segment.color;
      row.appendChild(swatch);
      row.appendChild(el('span', null, segment.label));
      row.appendChild(el('span', 'legend__pct', total > 0 ? percent.toFixed(1) + '%' : '—'));
      row.appendChild(el('span', 'legend__value', num(value)));
      legendNode.appendChild(row);
    });
  }

  /** 近 N 日堆叠柱状：每列一根柱，段高按当日最大值归一化。 */
  function renderTrend(days) {
    const container = dom['bars-trend'];
    clear(container);

    if (!days || days.length === 0) {
      container.appendChild(emptyNode('所选窗口内没有逐日数据'));
      return;
    }

    const maxTotal = days.reduce(function (max, day) {
      const total = Number(day.write || 0) + Number(day.guard_rejected || 0) + Number(day.write_failed || 0);
      return Math.max(max, total);
    }, 0);

    days.forEach(function (day) {
      const write = Number(day.write || 0);
      const rejected = Number(day.guard_rejected || 0);
      const failed = Number(day.write_failed || 0);
      const total = write + rejected + failed;

      const column = el('div', 'bar');
      column.title = day.date + '：写操作 ' + write + ' / 守卫拒绝 ' + rejected + ' / 执行失败 ' + failed;

      const totalNode = el('span', 'bar__total', total > 0 ? num(total) : '');
      column.appendChild(totalNode);

      const stack = el('div', 'bar__stack');
      const scale = maxTotal > 0 ? Math.max(total, 1) / maxTotal : 0;

      // 零值也渲染为极小段，保持列高一致，避免空日看起来像缺柱
      [['write', write, 'bar__seg--write'], ['reject', rejected, 'bar__seg--reject'], ['failed', failed, 'bar__seg--failed']]
        .forEach(function (entry) {
          const value = entry[1];
          if (value <= 0) {
            return;
          }
          const seg = el('div', 'bar__seg ' + entry[2]);
          seg.style.height = (maxTotal > 0 ? (value / maxTotal) * 100 : 0) + '%';
          seg.title = entry[0] + ' ' + value;
          stack.appendChild(seg);
        });

      // 全零的一天：给一个 2% 高的灰色底座，保留"这一天存在但无记录"的视觉线索
      if (total === 0) {
        const placeholder = el('div', 'bar__seg');
        placeholder.style.height = '2%';
        placeholder.style.background = 'rgba(148, 163, 184, 0.22)';
        placeholder.title = '无记录';
        stack.appendChild(placeholder);
      }

      stack.style.opacity = maxTotal > 0 ? String(0.55 + scale * 0.45) : '1';
      column.appendChild(stack);

      const label = day.date ? day.date.slice(5) : '';
      column.appendChild(el('span', 'bar__label', label));

      container.appendChild(column);
    });
  }

  /** 按数据库分布：横向条 + 计数。 */
  function renderDistribution(buckets) {
    const list = dom['dist-database'];
    clear(list);

    if (!buckets || buckets.length === 0) {
      list.appendChild(el('li', 'empty empty--inline', '窗口内没有审计记录'));
      return;
    }

    const max = buckets.reduce(function (current, item) { return Math.max(current, Number(item.count || 0)); }, 0);

    buckets.forEach(function (bucket) {
      const row = el('li', 'dist__row');
      row.appendChild(el('span', 'dist__name', bucket.name));

      const track = el('span', 'dist__track');
      const fill = el('span', 'dist__fill');
      fill.style.width = (max > 0 ? (Number(bucket.count || 0) / max) * 100 : 0) + '%';
      track.appendChild(fill);
      row.appendChild(track);

      row.appendChild(el('span', 'dist__value', num(bucket.count)));
      list.appendChild(row);
    });
  }

  function renderCharts(summary) {
    clear(dom['donut-events']);
    clear(dom['legend-events']);
    clear(dom['donut-modes']);
    clear(dom['legend-modes']);

    if (!summary) {
      dom['donut-events'].appendChild(emptyNode('审计数据不可用'));
      dom['donut-modes'].appendChild(emptyNode('审计数据不可用'));
      dom['bars-trend'].appendChild(emptyNode('审计数据不可用'));
      dom['dist-database'].appendChild(el('li', 'empty empty--inline', '审计数据不可用'));
      return;
    }

    const byEvent = summary.by_event || {};
    renderDonut(
      dom['donut-events'],
      dom['legend-events'],
      Object.keys(byEvent).map(function (key) {
        return {
          label: EVENT_LABELS[key] || key,
          value: Number(byEvent[key] || 0),
          color: EVENT_COLORS[key] || COLORS.unknown
        };
      }),
      '事件'
    );

    const byMode = summary.by_mode || {};
    renderDonut(
      dom['donut-modes'],
      dom['legend-modes'],
      Object.keys(byMode).map(function (key) {
        return {
          label: MODE_LABELS[key] || key,
          value: Number(byMode[key] || 0),
          color: MODE_COLORS[key] || COLORS.unknown
        };
      }),
      '写操作'
    );

    renderTrend(summary.by_day || []);
    renderDistribution(summary.by_database || []);

    const range = summary.range || {};
    const note = '窗口 ' + (range.start || '—') + ' ~ ' + (range.end || '—') +
      '（' + (range.days || SUMMARY_DAYS) + ' 天，' + (range.timezone || 'UTC') + ' 存储）';
    dom['audit-note'].textContent = note;
  }

  // -------------------------------------------------------------- 最近记录

  function eventBadge(event) {
    const map = {
      write: 'badge--write',
      guard_rejected: 'badge--reject',
      write_failed: 'badge--failed'
    };
    return el('span', 'badge ' + (map[event] || 'badge--unknown'), EVENT_LABELS[event] || event || '未知');
  }

  function modeBadge(mode) {
    if (!mode) {
      return el('span', null, '—');
    }
    const cls = mode === 'commit' ? 'badge--commit' : (mode === 'dry-run' ? 'badge--dryrun' : 'badge--unknown');
    return el('span', 'badge ' + cls, MODE_LABELS[mode] || mode);
  }

  function summaryText(entry) {
    if (entry.reason) {
      return '拒绝原因：' + entry.reason;
    }
    if (entry.error) {
      return '错误：' + entry.error;
    }
    if (entry.sql_preview) {
      return entry.sql_preview;
    }
    return '（无附加信息）';
  }

  function renderRecent(recent) {
    const body = dom['recent-body'];
    clear(body);

    const entries = (recent && recent.entries) || [];
    if (entries.length === 0) {
      const row = el('tr');
      const cell = el('td', 'empty', '最近 ' + ((recent && recent.scanned_days) || 0) + ' 天内没有审计记录');
      cell.colSpan = 7;
      row.appendChild(cell);
      body.appendChild(row);
      dom['recent-note'].textContent = '';
      return;
    }

    entries.forEach(function (entry) {
      const row = el('tr');
      const stamp = formatTimestamp(entry.ts);

      const timeCell = el('td', 'col-time', stamp.local);
      if (stamp.utc) {
        timeCell.appendChild(el('small', null, stamp.utc));
      }
      row.appendChild(timeCell);

      const eventCell = el('td');
      eventCell.appendChild(eventBadge(entry.event));
      row.appendChild(eventCell);

      row.appendChild(el('td', 'col-db', entry.database || '—'));
      row.appendChild(el('td', 'col-tool', entry.tool || '—'));

      const modeCell = el('td');
      modeCell.appendChild(modeBadge(entry.mode));
      row.appendChild(modeCell);

      const rowsCell = el('td', 'col-rows num');
      rowsCell.textContent = entry.affected_rows === undefined || entry.affected_rows === null
        ? '—'
        : num(entry.affected_rows);
      row.appendChild(rowsCell);

      const summaryCell = el('td', 'col-summary');
      const clampNode = el('span', 'clamp', summaryText(entry));
      const fullText = (entry.sql_preview ? entry.sql_preview + '\n' : '') +
        'sql_sha256: ' + (entry.sql_sha256 || '（未记录）');
      clampNode.title = fullText;
      summaryCell.appendChild(clampNode);
      row.appendChild(summaryCell);

      body.appendChild(row);
    });

    dom['recent-note'].textContent = '显示最新 ' + entries.length + ' 条 · 时间已换算为本地时区';
  }

  // ------------------------------------------------------------------ 页脚

  function renderFooter(overview) {
    const container = dom['footer-config'];
    clear(container);

    if (!overview || !overview.config) {
      container.appendChild(el('span', 'footer__item', '配置不可用'));
      return;
    }

    const config = overview.config;
    const items = [
      ['传输', config.transport || '—'],
      ['端口', num(config.port, '—')],
      ['写操作', config.allow_write ? '已启用' : '已禁用'],
      ['命令超时', num(config.command_timeout_seconds, '—') + ' 秒'],
      ['响应字符预算', num(config.max_response_chars, '—')],
      ['审计保留', num(config.audit_retention_days, '—') + ' 天'],
      ['危险关键词', num(config.dangerous_keyword_count, '—') + ' 条'],
      ['启动连通性预检', config.startup_connectivity_check ? '已开启' : '未开启']
    ];

    items.forEach(function (pair) {
      const node = el('span', 'footer__item');
      node.appendChild(document.createTextNode(pair[0] + ' '));
      node.appendChild(el('b', null, pair[1]));
      container.appendChild(node);
    });
  }

  // -------------------------------------------------------------- 加载与调度

  function setBusy(busy) {
    state.busy = busy;
    dom['refresh-btn'].disabled = busy;
    dom['refresh-btn'].classList.toggle('is-busy', busy);
    dom['progress'].classList.toggle('is-active', busy);
  }

  function showError(message) {
    dom['toast-title'].textContent = '数据加载失败';
    dom['toast-text'].textContent = message + '（服务可能未运行，或本页非 http 模式产物）';
    dom.toast.hidden = false;
  }

  function hideError() {
    dom.toast.hidden = true;
  }

  async function loadOverview() {
    const overview = await getJson('api/overview');
    state.overview = overview;

    renderChips(overview);
    renderDatabases(overview);
    renderFooter(overview);
    if (state.summary) {
      renderKpi(state.overview, state.summary);
    } else {
      renderKpi(overview, null);
    }
  }

  async function loadAudit() {
    const results = await Promise.all([
      getJson('api/audit/summary?days=' + SUMMARY_DAYS),
      getJson('api/audit/recent?limit=' + RECENT_LIMIT)
    ]);

    state.summary = results[0];
    state.recent = results[1];

    renderCharts(state.summary);
    renderRecent(state.recent);
    renderKpi(state.overview, state.summary);
  }

  /**
   * 刷新：两个数据源各自独立成功/失败——
   * 探测慢或不可达不会阻塞审计区块，反之亦然（单点失败不造成整页空白）。
   */
  async function refresh(manual) {
    if (manual) {
      const now = Date.now();
      if (now - state.lastManualAt < MANUAL_THROTTLE_MS) {
        return;
      }
      state.lastManualAt = now;
    }

    setBusy(true);
    const failures = [];

    const overviewTask = loadOverview().catch(function (error) {
      failures.push('概览：' + error.message);
    });
    const auditTask = loadAudit().catch(function (error) {
      failures.push('审计：' + error.message);
    });

    await Promise.all([overviewTask, auditTask]);

    setBusy(false);

    if (failures.length > 0) {
      showError(failures.join('；'));
    } else {
      hideError();
      state.lastLoadedAt = new Date();
    }
  }

  function scheduleTimers(enabled) {
    if (state.overviewTimer) {
      clearInterval(state.overviewTimer);
      state.overviewTimer = null;
    }
    if (state.auditTimer) {
      clearInterval(state.auditTimer);
      state.auditTimer = null;
    }

    if (!enabled) {
      return;
    }

    state.overviewTimer = setInterval(function () {
      loadOverview().catch(function () { /* 静默：轮询失败由手动刷新或下一次轮询体现 */ });
    }, POLL_OVERVIEW_MS);

    state.auditTimer = setInterval(function () {
      loadAudit().catch(function () { /* 同上 */ });
    }, POLL_AUDIT_MS);
  }

  // ------------------------------------------------------------------ 启动

  function init() {
    dom['refresh-btn'].addEventListener('click', function () { refresh(true); });
    dom['toast-retry'].addEventListener('click', function () { refresh(true); });

    dom['auto-refresh'].addEventListener('change', function (event) {
      scheduleTimers(event.target.checked);
    });

    // 标签页切回前台时立即刷新一次（长时间挂后台的页面数据可能已过期）
    document.addEventListener('visibilitychange', function () {
      if (!document.hidden && dom['auto-refresh'].checked) {
        refresh(false);
      }
    });

    scheduleTimers(dom['auto-refresh'].checked);
    refresh(false);
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
