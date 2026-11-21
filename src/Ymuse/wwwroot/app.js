'use strict';

/* ---------------------------------------------------------------------------
   Ymuse — лёгкий клиент Яндекс Музыки.
   UI на чистом JS: без фреймворков, сборки и внешних ресурсов.
   Все сетевые запросы и работа с токеном — на стороне C# (см. MainWindow).
   ------------------------------------------------------------------------- */

const bridge = (window.chrome && window.chrome.webview) ? window.chrome.webview : null;
const audio = document.getElementById('audio');

const state = {
  view: 'wave',
  me: null,
  device: null,
  stations: [],
  groups: [],
  playlists: [],
  playlist: null,
  album: null,
  search: { q: '', res: null, loading: false },
  queue: [],
  index: -1,
  station: null,
  batchId: null,
  playing: false,
  volume: Number(localStorage.getItem('volume') || 0.8),
  history: [],
  fetching: false,
  guest: false,
  preview: false,
  playError: '',
  stationInfo: {},
  stationValues: JSON.parse(localStorage.getItem('stationValues') || '{}'),
};

let renderedList = null;

/* ------------------------------- RPC ------------------------------------ */

let rpcSeq = 0;
const pending = new Map();

function rpc(method, params) {
  return new Promise((resolve, reject) => {
    if (!bridge) return reject(new Error('Мост приложения недоступен'));
    const id = ++rpcSeq;
    pending.set(id, { resolve, reject });
    bridge.postMessage({ id, method, params: params || {} });
  });
}

if (bridge) {
  bridge.addEventListener('message', (ev) => {
    const msg = ev.data;
    if (!msg || !msg.id) return;
    const p = pending.get(msg.id);
    if (!p) return;
    pending.delete(msg.id);
    if (msg.ok) p.resolve(msg.data);
    else p.reject(new Error(msg.error || 'Неизвестная ошибка'));
  });
}

/* ------------------------------ helpers --------------------------------- */

const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => (
  { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]
));

function fmtTime(sec) {
  if (!isFinite(sec) || sec < 0) sec = 0;
  const m = Math.floor(sec / 60);
  const s = Math.floor(sec % 60);
  return m + ':' + (s < 10 ? '0' : '') + s;
}
const fmtDur = (ms) => fmtTime(Math.round((ms || 0) / 1000));
const plural = (n, one, few, many) => {
  const a = Math.abs(n) % 100;
  const b = a % 10;
  if (a > 10 && a < 20) return many;
  if (b > 1 && b < 5) return few;
  if (b === 1) return one;
  return many;
};
const tracksLabel = (n) => n + ' ' + plural(n, 'трек', 'трека', 'треков');

function toast(text) {
  const el = $('#toast');
  el.textContent = text;
  el.classList.add('show');
  clearTimeout(toast._t);
  toast._t = setTimeout(() => el.classList.remove('show'), 2600);
}

function cover(url, cls, fallback = '♪') {
  if (!url) return `<div class="${cls}">${fallback}</div>`;
  return `<div class="${cls}"><img src="${esc(url)}" alt="" loading="lazy" onerror="this.parentNode.innerHTML='${fallback}'"></div>`;
}

/* ------------------------------ navigation ------------------------------ */

const NAV_TITLES = {
  wave: 'Моя волна',
  radio: 'Поток',
  search: 'Поиск',
  playlists: 'Плейлисты',
  playlist: 'Плейлист',
  album: 'Альбом',
  station: 'Поток',
};

function navigate(view, params = {}, opts = {}) {
  if (!$('#drawer').classList.contains('hidden')) closeSettings();
  if (opts.push !== false) state.history.push({ view: state.view, params: state.viewParams || {} });
  state.view = view;
  state.viewParams = params;
  render();
}

function back() {
  const prev = state.history.pop();
  if (!prev) return go(state.me ? 'wave' : 'login');
  state.view = prev.view;
  state.viewParams = prev.params || {};
  render();
}

function go(view) {
  navigate(view, {}, { push: false });
}

/* ------------------------------ rendering ------------------------------- */

function render() {
  updateNav();
  const c = $('#content');
  const v = state.view;

  if (v === 'login') return renderLogin(c);

  if (v === 'wave') return renderStationPage(c, 'user:onyourwave', 'Моя волна');
  if (v === 'station') return renderStationPage(c, state.viewParams.id, state.viewParams.name);
  if (v === 'radio') return renderRadio(c);
  if (v === 'search') return renderSearch(c);
  if (v === 'playlists') return renderPlaylists(c);
  if (v === 'playlist') return renderPlaylistPage(c);
  if (v === 'album') return renderAlbumPage(c);

  go('wave');
}

function updateNav() {
  const map = { wave: 'wave', station: 'wave', radio: 'radio', search: 'search', playlists: 'playlists', playlist: 'playlists', album: 'playlists' };
  $$('.nav-item').forEach((b) => b.classList.toggle('active', b.dataset.nav === map[state.view]));
  $$('.side-link').forEach((b) => b.classList.toggle('active', state.station && b.dataset.id === state.station.id));
}

function setCrumbs(title, sub, withBack) {
  $('#crumbs').innerHTML =
    (withBack ? '<button class="back" data-act="back">←</button>' : '') +
    `<h1>${esc(title || '')}</h1>` +
    (sub ? `<span class="sub">${esc(sub)}</span>` : '');
}

function renderLogin(c) {
  $('#player').classList.add('hidden');
  setCrumbs('Вход', '', false);
  $('#account').textContent = '';
  c.innerHTML = `
    <div class="login-wrap">
      <div class="login-card">
        <h2>Вход в Яндекс Музыку</h2>
        <p>Ymuse работает с вашим аккаунтом напрямую: токен хранится только на этом компьютере.</p>
        <div id="loginBody"></div>
        <div class="divider">или</div>
        <button class="btn small" data-act="login-device" style="width:100%;justify-content:center">Войти по коду на другом устройстве</button>
        <div class="divider">или</div>
        <div class="field">
          <div class="field-label">Токен вручную</div>
          <div style="display:flex;gap:8px">
            <input id="tokenInput" type="password" placeholder="OAuth-токен" style="flex:1">
            <button class="btn small" data-act="login-token">Войти</button>
          </div>
        </div>
        <div class="login-error" id="loginError"></div>
        <div class="divider">без аккаунта</div>
        <p>Каталог станций, настройки потока и поиск работают и без входа. Аккаунт нужен для прослушивания и своих плейлистов.</p>
        <button class="btn" data-act="guest" style="width:100%;justify-content:center">Продолжить без входа</button>
      </div>
    </div>`;

  renderSideGroup($('#sideMood'), null);
  renderSideGroup($('#sideActivity'), null);

  const body = $('#loginBody');
  if (state.device) {
    showDeviceCode(body, state.device);
  } else {
    body.innerHTML = defaultLoginBody();
  }
}

function defaultLoginBody() {
  return `<button class="btn primary" data-act="login-browser" style="width:100%;justify-content:center">Войти через Яндекс</button>
    <p style="margin:8px 0 0;font-size:11.5px;color:var(--muted)">Откроется окно Яндекса: пароль вводится на сайте Яндекса, приложение получает только токен.</p>`;
}

function showDeviceCode(body, d) {
  body.innerHTML = `
    <p>Откройте страницу Яндекса и введите код:</p>
    <div class="code-box">${esc(d.userCode)}</div>
    <div style="display:flex;gap:8px;margin-top:8px">
      <button class="btn small" data-act="open-url" data-url="${esc(d.verificationUrl)}" style="flex:1;justify-content:center">Открыть страницу</button>
      <button class="btn small" data-act="login-device" style="flex:1;justify-content:center"><span class="spinner"></span> Ждём…</button>
    </div>`;
}

/* --------------------------- station / wave ----------------------------- */

function renderStationPage(c, id, name) {
  const isWave = id === 'user:onyourwave';
  const active = state.station && state.station.id === id;
  const list = active ? state.queue : [];
  const meta = state.stations.find((s) => s.id === id);

  setCrumbs(name || (state.station ? state.station.name : 'Поток'), active && list.length ? tracksLabel(list.length) : '', true);

  let html = `
    <div class="hero">
      ${cover(meta && meta.icon ? meta.icon : null, 'hero-cover', '≋')}
      <div class="hero-body">
        <div class="hero-kicker">${isWave ? 'Персональный поток' : 'Радиостанция'}</div>
        <div class="hero-title">${esc(name || 'Поток')}</div>
        <div class="hero-meta">${active && list.length ? tracksLabel(list.length) + ' в очереди' : 'Настройте характер потока под себя'}</div>
        <div class="hero-actions">
          <button class="btn primary" data-act="station-start" data-id="${esc(id)}" data-name="${esc(name || '')}">▶ Включить</button>
          <button class="btn" data-act="station-more" data-id="${esc(id)}" data-name="${esc(name || '')}">Ещё треки</button>
          <button class="btn" data-act="settings">⚙ Настройки потока</button>
        </div>
      </div>
    </div>`;

  if (!active || !list.length) {
    if (!state.me) {
      const why = state.playError || 'Очередь треков для станции сервер собирает только под аккаунт.';
      const whyText = /[.!?]$/.test(why) ? why : why + '.';
      html += `<div class="notice">
        <div class="notice-title">${isWave ? '«Моя волна» собирается под аккаунт' : 'Прослушивание потока требует входа'}</div>
        <div class="notice-text">${esc(whyText)} Каталог станций и их настройки доступны и без входа — вход нужен для прослушивания.</div>
        <div class="notice-actions">
          <button class="btn primary" data-act="login">Войти по коду</button>
          <button class="btn" data-act="nav" data-nav="radio">Каталог станций</button>
        </div>
      </div>`;
    } else {
      html += `<div class="empty"><div class="big">Поток не запущен</div><div>Нажмите «Включить» — треки подберутся под ваш вкус.</div></div>`;
    }
  } else {
    html += `<div class="tracks" id="trackList">${trackRows(list)}</div>`;
  }
  c.innerHTML = html;
  renderedList = active ? state.queue : null;
  syncPlaying();
}

function trackRows(list) {
  return list.map((t, i) => `
    <div class="track ${t.available === false ? 'unavailable' : ''}" data-act="track" data-i="${i}" data-id="${esc(t.id)}">
      <div class="track-num" data-num>${i + 1}</div>
      ${cover(t.cover, 'track-cover')}
      <div class="track-main">
        <div class="track-title">${esc(t.title)}</div>
        <div class="track-artist">${esc(t.artist)}</div>
      </div>
      <div class="track-album">${esc(t.album || '')}</div>
      <div class="track-dur">${fmtDur(t.durationMs)}</div>
    </div>`).join('');
}

function syncPlaying() {
  $$('#content .track').forEach((row) => {
    const i = Number(row.dataset.i);
    const on = renderedList === state.queue && i === state.index;
    row.classList.toggle('playing', on);
    const num = row.querySelector('[data-num]');
    if (num) num.textContent = on ? '▶' : i + 1;
  });
}

/* ------------------------------- radio ---------------------------------- */

const GROUP_TITLES = {
  user: 'Для вас',
  mood: 'Настроение',
  activity: 'Занятие',
  genre: 'Жанры',
  epoch: 'Эпохи',
  editorial: 'Подборки',
  tempo: 'Темп',
};

function renderRadio(c) {
  setCrumbs('Поток', 'Выберите настроение, занятие или жанр', true);

  if (!state.groups.length) {
    c.innerHTML = `<div class="empty"><div class="big">Станции не загрузились</div><div>Проверьте подключение к интернету.</div></div>`;
    return;
  }

  let html = `<div class="hero"><div class="hero-body">
      <div class="hero-kicker">Все настройки потока</div>
      <div class="hero-title">Поток под ситуацию</div>
      <div class="hero-meta">Настроение, занятие, жанр, эпоха и язык — Яндекс соберёт очередь под выбор.</div>
      <div class="hero-actions"><button class="btn" data-act="settings">⚙ Настройки текущего потока</button></div>
    </div></div>`;

  if (!state.me) {
    html += `<div class="notice">
      <div class="notice-title">Гостевой режим</div>
      <div class="notice-text">Станции загружены напрямую из Яндекс Музыки. Настройки открываются для каждой станции, но включить поток можно после входа в аккаунт.</div>
      <div class="notice-actions"><button class="btn primary" data-act="login">Войти по коду</button></div>
    </div>`;
  }

  for (const g of state.groups) {
    html += `<div class="section-title">${esc(g.title)}<span class="muted">${g.stations.length}</span></div>
      <div class="field-opts" style="margin-bottom:18px">` +
      g.stations.map((s) => `<button class="chip ${state.station && state.station.id === s.id ? 'active' : ''}" data-act="station-start" data-id="${esc(s.id)}" data-name="${esc(s.name)}" data-icon="${esc(s.icon || '')}">${esc(s.name)}</button>`).join('') +
      `</div>`;
  }
  c.innerHTML = html;
  renderedList = null;
}

/* ------------------------------- search --------------------------------- */

function renderSearch(c) {
  setCrumbs('Поиск', '', true);
  const s = state.search;
  let html = `<div class="searchbar">
      <input id="searchInput" type="text" placeholder="Треки, альбомы, плейлисты…" value="${esc(s.q)}" autocomplete="off" spellcheck="false">
    </div><div id="searchResults">`;

  if (s.loading) {
    html += `<div class="empty"><span class="spinner"></span></div>`;
  } else if (!s.q.trim()) {
    html += `<div class="empty"><div class="big">Что послушаем?</div><div>Введите название трека, исполнителя или плейлиста.</div></div>`;
  } else if (!s.res) {
    html += `<div class="empty">Начните вводить запрос</div>`;
  } else {
    const r = s.res;
    if (r.tracks && r.tracks.length) {
      html += `<div class="section-title">Треки</div><div class="tracks">${trackRows(r.tracks)}</div>`;
    }
    if (r.albums && r.albums.length) {
      html += `<div class="section-title" style="margin-top:20px">Альбомы</div><div class="grid">` +
        r.albums.map((a) => `<div class="card" data-act="album" data-id="${esc(a.id)}">
          ${cover(a.cover, 'card-cover', '◍')}
          <div class="card-title">${esc(a.title)}</div>
          <div class="card-sub">${esc(a.artist || '')}</div></div>`).join('') + `</div>`;
    }
    if (r.playlists && r.playlists.length) {
      html += `<div class="section-title" style="margin-top:20px">Плейлисты</div><div class="grid">` +
        r.playlists.map((p) => `<div class="card" data-act="playlist" data-kind="${esc(p.kind)}" data-uid="${esc(p.uid)}">
          ${cover(p.cover, 'card-cover', '≡')}
          <div class="card-title">${esc(p.title)}</div>
          <div class="card-sub">${tracksLabel(p.trackCount || 0)}</div></div>`).join('') + `</div>`;
    }
    if (!(r.tracks || []).length && !(r.albums || []).length && !(r.playlists || []).length) {
      html += `<div class="empty"><div class="big">Ничего не найдено</div></div>`;
    }
  }
  html += `</div>`;
  c.innerHTML = html;
  renderedList = s.res ? s.res.tracks : null;

  const input = $('#searchInput');
  if (input) {
    input.focus();
    input.selectionStart = input.value.length;
  }
}

let searchTimer = null;
function onSearchInput(e) {
  const q = e.target.value;
  state.search.q = q;
  clearTimeout(searchTimer);
  if (!q.trim()) {
    state.search.res = null;
    state.search.loading = false;
    renderSearch($('#content'));
    $('#searchInput').focus();
    return;
  }
  searchTimer = setTimeout(runSearch, 350);
}

async function runSearch() {
  const q = state.search.q.trim();
  if (!q) return;
  state.search.loading = true;
  const input = $('#searchInput');
  const scroll = $('#content').scrollTop;
  if (input) $('#searchResults') && ($('#searchResults').innerHTML = '<div class="empty"><span class="spinner"></span></div>');
  try {
    const res = await rpc('search', { text: q });
    if (state.search.q.trim() !== q) return;
    state.search.res = res;
  } catch (err) {
    toast(err.message);
    state.search.res = { tracks: [], albums: [], playlists: [] };
  } finally {
    state.search.loading = false;
    if (state.view === 'search') {
      renderSearch($('#content'));
      const again = $('#searchInput');
      if (again) { again.focus(); again.selectionStart = again.value.length; }
      $('#content').scrollTop = $('#searchInput') ? scroll : 0;
    }
  }
}

/* ------------------------------ playlists ------------------------------- */

function renderPlaylists(c) {
  setCrumbs('Плейлисты', state.me ? `${state.playlists.length}` : 'гостевой режим', true);
  if (!state.me) {
    c.innerHTML = `<div class="notice">
      <div class="notice-title">Плейлисты привязаны к аккаунту</div>
      <div class="notice-text">Без входа сервер не отдаёт личные плейлисты и «Мне нравится». Войдите по коду — это безопасно и занимает полминуты.</div>
      <div class="notice-actions">
        <button class="btn primary" data-act="login">Войти по коду</button>
        <button class="btn" data-act="nav" data-nav="search">Искать плейлисты</button>
      </div>
    </div>`;
    renderedList = null;
    return;
  }
  if (!state.playlists.length) {
    c.innerHTML = `<div class="empty"><div class="big">Плейлистов нет</div><div>Создайте плейлист в Яндекс Музыке — он появится здесь.</div></div>`;
    renderedList = null;
    return;
  }
  c.innerHTML = `<div class="grid">` + state.playlists.map((p) => `
    <div class="card" data-act="playlist" data-kind="${esc(p.kind)}" data-uid="${esc(p.uid)}">
      ${cover(p.cover, 'card-cover', '≡')}
      <div class="card-title">${esc(p.title)}</div>
      <div class="card-sub">${tracksLabel(p.trackCount || 0)}</div>
    </div>`).join('') + `</div>`;
  renderedList = null;
}

function renderPlaylistPage(c) {
  const p = state.playlist;
  if (!p) return go('playlists');
  setCrumbs(p.title, tracksLabel((p.tracks || []).length), true);
  c.innerHTML = `
    <div class="hero">
      ${cover(p.cover, 'hero-cover', '≡')}
      <div class="hero-body">
        <div class="hero-kicker">${esc(p.owner || 'Плейлист')}</div>
        <div class="hero-title">${esc(p.title)}</div>
        <div class="hero-meta">${tracksLabel((p.tracks || []).length)}${p.description ? ' · ' + esc(p.description) : ''}</div>
        <div class="hero-actions"><button class="btn primary" data-act="play-here" data-i="0">▶ Слушать</button></div>
      </div>
    </div>
    <div class="tracks">${trackRows(p.tracks || [])}</div>`;
  renderedList = p.tracks || [];
  syncPlaying();
}

function renderAlbumPage(c) {
  const a = state.album;
  if (!a) return go('playlists');
  setCrumbs(a.title, a.artist || '', true);
  c.innerHTML = `
    <div class="hero">
      ${cover(a.cover, 'hero-cover', '◍')}
      <div class="hero-body">
        <div class="hero-kicker">Альбом${a.year ? ' · ' + a.year : ''}</div>
        <div class="hero-title">${esc(a.title)}</div>
        <div class="hero-meta">${esc(a.artist || '')} · ${tracksLabel((a.tracks || []).length)}</div>
        <div class="hero-actions"><button class="btn primary" data-act="play-here" data-i="0">▶ Слушать</button></div>
      </div>
    </div>
    <div class="tracks">${trackRows(a.tracks || [])}</div>`;
  renderedList = a.tracks || [];
  syncPlaying();
}

/* ------------------------------- player --------------------------------- */

function updateNowPlaying(t) {
  const img = $('#npCover');
  const fb = $('#npCoverFallback');
  if (t.cover) {
    img.src = t.cover;
    img.hidden = false;
    fb.hidden = true;
  } else {
    img.hidden = true;
    img.removeAttribute('src');
    fb.hidden = false;
  }
  $('#npTitle').textContent = t.title || '—';
  $('#npArtist').textContent = t.artist || '';
  $('#tDur').textContent = fmtDur(t.durationMs);
  $('#player').classList.remove('hidden');
  document.title = (t.title ? t.title + ' — ' : '') + 'Ymuse';
}

function current() {
  return state.queue[state.index] || null;
}

function sendFeedback(type, trackId) {
  if (!state.station) return;
  rpc('rotor.feedback', {
    station: state.station.id,
    type,
    trackId: trackId || null,
    batchId: state.batchId,
    playedSeconds: Math.round(audio.currentTime || 0),
  }).catch(() => {});
}

async function startCurrent(depth = 0) {
  const t = current();
  if (!t) return;
  state.preview = false;
  updateNowPlaying(t);
  syncPlaying();

  if (t.available === false) {
    toast('Трек недоступен: ' + t.title);
    if (depth < 3) return advance(true, depth + 1);
    return;
  }

  try {
    const r = await rpc('track.stream', { id: t.id });
    if (!r || !r.url) throw new Error('Нет ссылки на поток');
    state.preview = !!r.preview;
    if (state.preview) $('#npArtist').textContent = (t.artist || '') + ' · 30-секундное превью';
    audio.src = r.url;
    audio.volume = state.volume;
    audio.muted = state.muted || false;
    await audio.play();
    if (state.station) {
      sendFeedback('trackStarted', t.id);
      ensureMore();
    }
  } catch (err) {
    toast(err.message || 'Не удалось воспроизвести трек');
  }
}

async function playIndex(i) {
  if (i < 0 || i >= state.queue.length) return;
  state.index = i;
  await startCurrent();
  syncPlaying();
}

function advance(auto, depth = 0) {
  if (state.index + 1 < state.queue.length) {
    state.index++;
    return startCurrent(depth);
  }
  if (state.station) {
    return ensureMore(true).then(() => {
      if (state.index + 1 < state.queue.length) {
        state.index++;
        startCurrent(depth);
      } else {
        toast('Поток закончился');
      }
    });
  }
  state.playing = false;
  updatePlayButton();
}

function nextTrack() {
  const t = current();
  if (state.station && t && audio.currentTime > 0 && audio.currentTime < (audio.duration || Infinity) * 0.9) {
    sendFeedback('skip', t.id);
  }
  advance(true);
}

function prevTrack() {
  if (audio.currentTime > 4) {
    audio.currentTime = 0;
    return;
  }
  if (state.index > 0) {
    state.index--;
    startCurrent();
  } else {
    audio.currentTime = 0;
  }
}

function updatePlayButton() {
  $('#btnPlay').textContent = audio.paused ? '▶' : '❚❚';
}

async function ensureMore(force) {
  if (!state.station) return;
  if (!force && state.queue.length - state.index > 2) return;
  if (state.fetching) return;
  state.fetching = true;
  try {
    const last = state.queue[state.queue.length - 1];
          const r = await rpc('rotor.tracks', {
        station: state.station.id,
        queue: last ? last.id : null,
      });
    if (r.batchId) state.batchId = r.batchId;
    const seen = new Set(state.queue.map((t) => t.id));
    let added = 0;
    for (const t of r.tracks || []) {
      if (seen.has(t.id)) continue;
      seen.add(t.id);
      state.queue.push(t);
      added++;
    }
    if (added && (state.view === 'station' || state.view === 'wave')) render();
  } catch (err) {
    toast(err.message);
  } finally {
    state.fetching = false;
  }
}

async function startStation(id, name, autoplay = true) {
  state.playError = '';
  state.station = { id, name: name || '' };
  state.queue = [];
  state.index = -1;
  state.batchId = null;
  navigate('station', { id, name });
  try {
    const r = await rpc('rotor.tracks', { station: id, queue: null });
    state.batchId = r.batchId || null;
    state.queue = r.tracks || [];
    if (!state.queue.length) {
      toast('Станция не вернула треков');
      render();
      return;
    }
    render();
    if (autoplay) {
      state.index = 0;
      sendFeedback('radioStarted', state.queue[0].id);
      await startCurrent();
      syncPlaying();
      render(); // обновить счётчик в заголовке
    }
  } catch (err) {
    state.playError = err.message;
    toast(err.message);
    render();
  }
}

/* --------------------------- station settings --------------------------- */

const VALUE_LABELS = {
  fun: 'Весело', active: 'Бодро', calm: 'Спокойно', sad: 'Грустно', all: 'Всё',
  default: 'Любое', favorite: 'Любимое', discover: 'Незнакомое', popular: 'Популярное', diverse: 'Редкие',
  any: 'Любой', russian: 'Русский', 'not-russian': 'Иностранный', 'without-words': 'Без слов',
};
const labelOf = (v) => VALUE_LABELS[v] || v;

function settingFields() {
  const info = state.stationInfo[state.station ? state.station.id : 'user:onyourwave'];
  const defaults = {
    moodEnergy: ['fun', 'active', 'calm', 'sad', 'all'],
    diversity: ['favorite', 'discover', 'popular', 'default'],
    language: ['any', 'russian', 'not-russian'],
  };
  const result = [];
  for (const key of ['moodEnergy', 'diversity', 'language']) {
    const opt = info && info.options && info.options[key];
    const values = (opt && opt.length ? opt.map((o) => o.value) : defaults[key]);
    const labels = {};
    if (opt) opt.forEach((o) => { labels[o.value] = o.label || labelOf(o.value); });
    result.push({ key, values, labels, current: currentSettingValue(key) });
  }
  return result;
}

function currentSettingValue(key) {
  const id = state.station ? state.station.id : 'user:onyourwave';
  const saved = (state.stationValues[id] || {})[key];
  const info = state.stationInfo[id];
  if (saved) return saved;
  if (info && info.values && info.values[key]) return info.values[key];
  return null;
}

async function openSettings() {
  const id = state.station ? state.station.id : 'user:onyourwave';
  const name = state.station ? state.station.name : 'Моя волна';
  $('#drawerStation').textContent = name;
  $('#drawerBody').innerHTML = '<div class="empty"><span class="spinner"></span></div>';
  $('#drawer').classList.remove('hidden');
  $('#scrim').classList.remove('hidden');
  document.body.classList.add('drawer-open');

  if (!state.stationInfo[id]) {
    try {
      state.stationInfo[id] = await rpc('rotor.info', { station: id });
    } catch (e) {
      state.stationInfo[id] = { options: {}, values: {} };
    }
  }
  renderDrawer();
}

function renderDrawer() {
  const fields = settingFields();
  $('#drawerBody').innerHTML = fields.map((f) => `
    <div class="field">
      <div class="field-label">${{ moodEnergy: 'Характер (энергия)', diversity: 'Разнообразие', language: 'Язык' }[f.key]}</div>
      <div class="field-opts">
        ${f.values.map((v) => `<button class="chip ${f.current === v ? 'active' : ''}" data-act="station-setting" data-key="${f.key}" data-value="${esc(v)}">${esc(f.labels[v] || labelOf(v))}</button>`).join('')}
      </div>
    </div>`).join('') + `
    <div style="font-size:11.5px;color:var(--muted);line-height:1.5">
      ${state.me
        ? 'Настройки применяются к текущей станции и сохраняются. Новые треки подстроятся под выбор.'
        : 'Значения загружены из Яндекс Музыки. Применить их к станции можно после входа в аккаунт.'}
    </div>
    ${state.me
      ? '<button class="btn" data-act="station-apply">Применить и обновить поток</button>'
      : '<button class="btn primary" data-act="login">Войти, чтобы применить</button>'}`;


  // сохраняем выбранные значения
  $('#drawerBody').dataset.fields = JSON.stringify(fields.map((f) => ({ key: f.key, values: f.values })));
}

async function applyStationSetting(key, value) {
  const id = state.station ? state.station.id : 'user:onyourwave';
  state.stationValues[id] = Object.assign({}, state.stationValues[id] || {}, { [key]: value });
  localStorage.setItem('stationValues', JSON.stringify(state.stationValues));
  const fields = JSON.parse($('#drawerBody').dataset.fields || '[]');
  const payload = {};
  for (const f of fields) {
    payload[f.key] = (state.stationValues[id] || {})[f.key] || null;
  }
  payload[key] = value;
  try {
    await rpc('rotor.settings', {
      station: id,
      moodEnergy: payload.moodEnergy || null,
      diversity: payload.diversity || null,
      language: payload.language || null,
    });
    toast('Настройка применена');
  } catch (err) {
    toast(err.message);
  }
  renderDrawer();
}

function closeSettings() {
  $('#drawer').classList.add('hidden');
  $('#scrim').classList.add('hidden');
  document.body.classList.remove('drawer-open');
}

/* -------------------------------- events -------------------------------- */

document.addEventListener('click', async (e) => {
  const el = e.target.closest('[data-act]');
  if (!el) return;
  const act = el.dataset.act;

  switch (act) {
    case 'nav':
      return go(el.dataset.nav);
    case 'back':
      return back();

    case 'open-url':
      return rpc('app.openUrl', { url: el.dataset.url }).catch(() => {});

    case 'guest':
      return enterGuest();

    case 'login':
      state.guest = false;
      state.playError = '';
      return go('login');

    case 'login-browser': {
      const body = $('#loginBody');
      const previous = body.innerHTML;
      body.innerHTML = '<div class="empty"><span class="spinner"></span> Ожидаем вход в окне Яндекса…</div>';
      $('#loginError').textContent = '';
      try {
        const r = await rpc('auth.browserLogin');
        if (r && r.status === 'ok') {
          await afterLogin();
          return;
        }
        body.innerHTML = previous;
      } catch (err) {
        $('#loginError').textContent = err.message;
        body.innerHTML = previous;
      }
      return;
    }

    case 'login-device': {
      if (state.device) return pollDevice(state.device);
      const body = $('#loginBody');
      body.innerHTML = '<div class="empty"><span class="spinner"></span> Получаем код…</div>';
      try {
        const d = await rpc('auth.deviceStart');
        state.device = d;
        showDeviceCode(body, d);
        pollDevice(d);
      } catch (err) {
        $('#loginError').textContent = err.message;
        body.innerHTML = defaultLoginBody();
      }
      return;
    }

    case 'login-token': {
      const val = $('#tokenInput').value.trim();
      if (!val) { $('#loginError').textContent = 'Вставьте токен'; return; }
      $('#loginError').textContent = '';
      try {
        await rpc('auth.setToken', { token: val });
        await afterLogin();
      } catch (err) {
        $('#loginError').textContent = err.message;
      }
      return;
    }

    case 'logout':
      await rpc('auth.logout').catch(() => {});
      state.me = null;
      state.guest = false;
      state.queue = [];
      state.station = null;
      audio.pause();
      audio.removeAttribute('src');
      $('#player').classList.add('hidden');
      state.history = [];
      return go('login');

    case 'station-start':
      return startStation(el.dataset.id, el.dataset.name, true);

    case 'station-more': {
      const id = el.dataset.id;
      if (!state.station || state.station.id !== id) return startStation(id, el.dataset.name, false).then(() => ensureMore(true));
      return ensureMore(true);
    }

    case 'track': {
      const i = Number(el.dataset.i);
      if (renderedList === state.queue) return playIndex(i);
      const list = renderedList || state.queue;
      state.queue = list.slice();
      state.station = state.station && state.view === 'station' ? state.station : null;
      return playIndex(i);
    }

    case 'play-here': {
      const i = Number(el.dataset.i || 0);
      state.station = null;
      if (renderedList) {
        const list = renderedList;
        state.queue = list.slice();
      }
      return playIndex(i);
    }

    case 'playlist':
      return openPlaylist(el.dataset.uid, el.dataset.kind);
    case 'album':
      return openAlbum(el.dataset.id);

    case 'settings':
      return openSettings();
    case 'station-apply': {
      const id = state.station ? state.station.id : 'user:onyourwave';
      closeSettings();
      if (state.station) {
        await startStation(state.station.id, state.station.name, true);
      } else {
        await startStation(id, 'Моя волна', true);
      }
      return;
    }
    case 'station-setting':
      return applyStationSetting(el.dataset.key, el.dataset.value);
    case 'drawer-close':
      return closeSettings();
  }
});

document.addEventListener('input', (e) => {
  if (e.target.id === 'searchInput') onSearchInput(e);
  if (e.target.id === 'vol') {
    state.volume = Number(e.target.value) / 100;
    audio.volume = state.volume;
    localStorage.setItem('volume', String(state.volume));
  }
  if (e.target.id === 'seek' && audio.duration) {
    audio.currentTime = (Number(e.target.value) / 1000) * audio.duration;
  }
});

document.addEventListener('keydown', (e) => {
  if (e.target.matches('input, textarea')) return;
  if (e.code === 'Space') {
    e.preventDefault();
    if (audio.paused) audio.play().catch(() => {}); else audio.pause();
  }
});

$('#btnPlay').addEventListener('click', () => {
  if (!current()) { toast('Ничего не выбрано'); return; }
  if (!audio.src) startCurrent();
  else if (audio.paused) audio.play().catch(() => {});
  else audio.pause();
});
$('#btnNext').addEventListener('click', nextTrack);
$('#btnPrev').addEventListener('click', prevTrack);
$('#btnSettings').addEventListener('click', openSettings);
$('#drawerClose').addEventListener('click', closeSettings);
$('#scrim').addEventListener('click', closeSettings);
$('#btnMute').addEventListener('click', () => {
  state.muted = !state.muted;
  audio.muted = state.muted;
  $('#btnMute').textContent = state.muted ? '🔇' : '🔊';
});

audio.addEventListener('play', updatePlayButton);
audio.addEventListener('pause', updatePlayButton);
audio.addEventListener('ended', () => {
  const t = current();
  if (state.station && t) sendFeedback('trackFinished', t.id);
  advance(true);
});
audio.addEventListener('error', () => {
  if (!audio.src) return;
  toast('Ошибка воспроизведения');
});
audio.addEventListener('timeupdate', () => {
  if (!audio.duration) return;
  $('#tCur').textContent = fmtTime(audio.currentTime);
  $('#tDur').textContent = fmtTime(audio.duration);
  const seek = $('#seek');
  if (seek && !seek.matches(':active')) seek.value = String(Math.round((audio.currentTime / audio.duration) * 1000));
});

/* -------------------------------- boot ---------------------------------- */

function renderSideGroup(host, group) {
  const block = host.closest('.side-block');
  if (!group || !group.stations.length) {
    host.innerHTML = '';
    if (block) block.style.display = 'none';
    return;
  }
  if (block) block.style.display = '';
  host.innerHTML = group.stations.slice(0, 9).map((s) =>
    `<button class="side-link" data-act="station-start" data-id="${esc(s.id)}" data-name="${esc(s.name)}" title="${esc(s.name)}">${esc(s.name)}</button>`
  ).join('');
}

function fillSidebar() {
  renderSideGroup($('#sideMood'), state.groups.find((g) => g.key === 'mood'));
  renderSideGroup($('#sideActivity'), state.groups.find((g) => g.key === 'activity'));
}

function updateAccount() {
  const me = state.me;
  if (!me) {
    $('#account').textContent = state.guest ? 'Гость' : '';
    $('#sideFoot').innerHTML = state.guest
      ? '<b>Гостевой режим</b><span>Станции и поиск — без входа</span><button data-act="login">Войти по коду</button>'
      : '';
    return;
  }
  $('#account').textContent = me.name || me.login || '';
  $('#sideFoot').innerHTML =
    `<b>${esc(me.name || me.login || 'Аккаунт')}</b>` +
    (me.plus ? '<span>Плюс активен</span>' : '<span style="color:#b58a00">Без Плюса</span>') +
    '<button data-act="logout">Выйти</button>';
}

async function loadStations() {
  const r = await rpc('rotor.stations');
  state.groups = (r.groups || []).filter((g) => g.stations.length);
  state.stations = state.groups.flatMap((g) => g.stations);
  fillSidebar();
}

async function loadPlaylists() {
  try {
    state.playlists = await rpc('library.playlists');
  } catch (e) {
    state.playlists = [];
    toast(e.message);
  }
}

async function openPlaylist(uid, kind) {
  try {
    const p = await rpc('library.playlist', { uid, kind });
    state.playlist = p;
    state.history.push({ view: state.view, params: state.viewParams || {} });
    state.view = 'playlist';
    render();
  } catch (err) {
    toast(err.message);
  }
}

async function openAlbum(id) {
  try {
    const a = await rpc('library.album', { id });
    state.album = a;
    state.history.push({ view: state.view, params: state.viewParams || {} });
    state.view = 'album';
    render();
  } catch (err) {
    toast(err.message);
  }
}

async function pollDevice(d) {
  const deadline = Date.now() + (d.expiresIn || 300) * 1000;
  const interval = Math.max(2, d.interval || 3) * 1000;
  while (Date.now() < deadline) {
    await sleep(interval);
    if (state.device !== d) return;
    try {
      const r = await rpc('auth.devicePoll', { deviceCode: d.code });
      if (r && r.status === 'ok') {
        state.device = null;
        await afterLogin();
        return;
      }
    } catch (err) {
      // 400 authorization_pending приходит как pending, прочие ошибки показываем
      if (!/pending|authorization/i.test(err.message)) {
        toast(err.message);
      }
    }
  }
  state.device = null;
  if (state.view === 'login') {
    renderLogin($('#content'));
    $('#loginError').textContent = 'Код истёк, попробуйте снова';
  }
}

async function loadAppInfo() {
  try {
    const info = await rpc('app.info');
    state.info = info;
    const el = $('#sideVersion');
    if (!el) return;
    let text = 'Ymuse ' + info.version;
    if (info.pending) text += ' · обн. ' + info.pending + ' при выходе';
    else if (info.autoUpdate) text += ' · автообновление';
    el.textContent = text;
    el.title = info.autoUpdate
      ? 'Канал обновлений: ' + (info.channel || '')
      : 'Канал обновлений не найден: нужен git-remote, channel.json рядом с exe или updateUrl';
  } catch {}
}

async function afterLogin() {
  state.me = await rpc('auth.status');
  if (!state.me.loggedIn) {
    state.me = null;
    go('login');
    return;
  }
  updateAccount();
  loadAppInfo();
  state.history = [];
  go('wave');
  await Promise.all([loadStations(), loadPlaylists()]);
  if (state.view === 'wave') render();
}

async function enterGuest() {
  state.guest = true;
  state.me = null;
  state.playError = '';
  state.playlists = [];
  updateAccount();
  go('radio');
  try {
    await loadStations();
  } catch (err) {
    toast(err.message);
  }
  if (state.view === 'radio') render();
}

async function boot() {
  audio.volume = state.volume;
  $('#vol').value = String(Math.round(state.volume * 100));

  if (!bridge) {
    $('#content').innerHTML = '<div class="empty"><div class="big">Откройте приложение Ymuse.exe</div><div>Страница работает только внутри приложения.</div></div>';
    return;
  }

  loadAppInfo();

  try {
    const st = await rpc('auth.status');
    if (!st.loggedIn) return go('login');
    state.me = st;
    updateAccount();
    go('wave');
    await Promise.all([loadStations(), loadPlaylists()]);
  } catch (err) {
    toast(err.message);
    go('login');
  }
}

boot();
