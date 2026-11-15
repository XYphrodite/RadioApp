// RadioWork Web Player mode (v0.3.17)
(() => {
  const $ = id => document.getElementById(id);
  const esc = s => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const fmt = sec => { const s = Math.max(0, Math.floor(sec || 0)); return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`; };

  const RU = { а:"a",б:"b",в:"v",г:"g",д:"d",е:"e",ё:"e",ж:"zh",з:"z",и:"i",й:"y",к:"k",л:"l",м:"m",н:"n",о:"o",п:"p",р:"r",с:"s",т:"t",у:"u",ф:"f",х:"h",ц:"ts",ч:"ch",ш:"sh",щ:"sch",ъ:"",ы:"y",ь:"",э:"e",ю:"yu",я:"ya" };
  const norm = s => String(s || "").toLowerCase().replace(/ё/g, "е").replace(/[_\-.,()\[\]'"!?&/]+/g, " ").replace(/\s+/g, " ").trim();
  const translit = s => s.replace(/[а-я]/g, c => RU[c] ?? c);
  const skel = s => s.replace(/oo|ou/g, "u").replace(/ee|ea/g, "i").replace(/y/g, "i").replace(/ph/g, "f").replace(/ck|q/g, "k").replace(/w/g, "v").replace(/x/g, "ks").replace(/c(?!h)/g, "k").replace(/(.)\1+/g, "$1");
  const ALIASES = {
    "рок": ["rock"], "рэп": ["hip hop", "rap", "gangsta"], "реп": ["hip hop", "rap", "gangsta"], "хип": ["hip hop"], "хоп": ["hip hop"],
    "хипхоп": ["hip hop", "rap"], "фонк": ["drift fm", "phonk", "memphis", "cloud rap", "horrorcore"], "синтвейв": ["synthwave", "synth pop"],
    "ретровейв": ["synthwave"], "электроника": ["electronic"], "джаз": ["jazz"], "блюз": ["blues"], "соул": ["soul", "rnb"], "фанк": ["funk"],
    "метал": ["metal"], "металл": ["metal"], "панк": ["punk"], "поп": ["pop"], "регги": ["reggae", "ragga", "dub"], "классика": ["classical"],
    "эмбиент": ["ambient"], "кантри": ["country", "folk"], "техно": ["techno"], "хаус": ["house"], "транс": ["trance"], "даб": ["dub"], "инди": ["indie", "alternative rock"]
  };
  const GENRES = [
    { key: "hiphop", icon: "🎤", label: "Hip-Hop", kw: ["hip hop", "hip-hop", "hiphop", "rap", "gangsta", "boom bap", "g-funk", "crunk", "grime"] },
    { key: "synthwave", icon: "🌃", label: "Synthwave", kw: ["synthwave", "synth-pop", "new beat", "chiptune"] },
    { key: "rock", icon: "🎸", label: "Rock", kw: ["rock"] },
    { key: "phonk", icon: "🏎️", label: "Phonk", kw: ["phonk", "memphis", "horrorcore", "cloud rap"], station: "drift fm" },
    { key: "jazz", icon: "🎷", label: "Jazz / Soul", kw: ["jazz", "soul", "funk", "rnb", "r&b", "disco", "blues"] },
    { key: "electronic", icon: "⚡", label: "Electronic", kw: ["electronic"] },
    { key: "ambient", icon: "🌊", label: "Ambient / Chill", kw: ["ambient", "downtempo", "chill", "new age", "lounge", "classical", "soundtrack"] },
    { key: "metal", icon: "🤘", label: "Metal / Punk", kw: ["metal", "punk", "hardcore", "goregrind", "grunge"] },
    { key: "pop", icon: "🌟", label: "Pop", kw: ["pop"] },
    { key: "reggae", icon: "🏝️", label: "Reggae / Folk", kw: ["reggae", "dub", "ragga", "dancehall", "latin", "salsa", "country", "folk"] }
  ];
  const genreMatch = (t, g) => (g.station && t.stLow === g.station) || g.kw.some(k => t.gLow.includes(k));

  const saved = JSON.parse(localStorage.getItem("radiowork_web_player") || "{}");
  const P = {
    tracks: [], byId: new Map(), artists: [],
    mode: saved.mode || "radio", view: saved.view || "tracks", query: "", station: saved.station || "", artist: saved.artist || "",
    genre: saved.genre || "", sort: saved.sort || "", likes: new Set(saved.likes || []), shuffle: !!saved.shuffle, repeat: !!saved.repeat,
    volume: saved.volume ?? 0.8, limit: 120, filtered: [], queue: [], cur: null, base: 0, playing: false, transcode: false, seeking: false
  };
  const persist = () => localStorage.setItem("radiowork_web_player", JSON.stringify({
    mode: P.mode, view: P.view, station: P.station, artist: P.artist, genre: P.genre, sort: P.sort,
    likes: [...P.likes], shuffle: P.shuffle, repeat: P.repeat, volume: P.volume, lastId: P.cur?.id ?? saved.lastId
  }));

  const audio = new Audio();
  audio.preload = "auto";
  audio.volume = P.volume;

  // ---------- data ----------
  function loadData() {
    const all = [];
    const artistCount = new Map();
    for (const st of state.stations) {
      for (const t of st.rawTracks) {
        const tr = Object.assign({}, t, { station: st.name, stIcon: st.icon });
        tr.gLow = (tr.genre || "").toLowerCase();
        tr.stLow = st.name.toLowerCase();
        const h = norm(`${tr.title} ${tr.artist} ${tr.album} ${tr.genre} ${st.name}`);
        tr.hay = h + " " + translit(h) + " " + skel(translit(h));
        tr.gHay = tr.gLow + " " + tr.stLow;
        all.push(tr);
        artistCount.set(tr.artist, (artistCount.get(tr.artist) || 0) + 1);
      }
    }
    P.tracks = all;
    all.forEach(t => P.byId.set(t.id, t));
    P.artists = [...artistCount.entries()].map(([name, count]) => ({ name, count, hay: norm(name) + " " + translit(norm(name)) + " " + skel(translit(norm(name))) }))
      .sort((a, b) => a.name.localeCompare(b.name, "ru"));
    const sel = $("pStation");
    sel.innerHTML = `<option value="">📻 Все станции (${all.length.toLocaleString("ru-RU")})</option>` +
      state.stations.map(s => `<option value="${esc(s.name)}">${s.icon} ${esc(s.name)} (${s.rawTracks.length})</option>`).join("");
    sel.value = P.station;
    if (saved.lastId != null && P.byId.has(saved.lastId)) { P.cur = P.byId.get(saved.lastId); }
  }

  function matchQuery(t, tokens) {
    for (const tk of tokens) {
      const al = ALIASES[tk];
      if (al) { if (!al.some(a => t.gHay.includes(a))) return false; continue; }
      if (!t.hay.includes(tk) && !t.hay.includes(translit(tk)) && !t.hay.includes(skel(translit(tk)))) return false;
    }
    return true;
  }

  function baseFiltered(ignoreGenre) {
    const tokens = norm(P.query).split(" ").filter(Boolean);
    const g = !ignoreGenre && P.genre ? GENRES.find(x => x.key === P.genre) : null;
    return P.tracks.filter(t =>
      (P.view !== "fav" || P.likes.has(t.id)) &&
      (!P.station || t.station === P.station) &&
      (!P.artist || t.artist === P.artist) &&
      (!g || genreMatch(t, g)) &&
      (!tokens.length || matchQuery(t, tokens)));
  }

  function applySort(list) {
    const c = (a, b) => String(a || "").localeCompare(String(b || ""), "ru", { sensitivity: "base" });
    if (P.sort === "artist") list.sort((a, b) => c(a.artist, b.artist) || c(a.album, b.album) || c(a.title, b.title));
    else if (P.sort === "album") list.sort((a, b) => c(a.album, b.album) || c(a.title, b.title));
    else if (P.sort === "title") list.sort((a, b) => c(a.title, b.title));
    return list;
  }

  // ---------- render ----------
  function renderGenres(pre) {
    const counts = {};
    GENRES.forEach(g => counts[g.key] = 0);
    for (const t of pre) for (const g of GENRES) if (genreMatch(t, g)) counts[g.key]++;
    $("pGenres").innerHTML = `<button class="player-genre-chip ${P.genre ? "" : "active"}" data-genre="">Все <span class="pgc-count">${pre.length}</span></button>` +
      GENRES.map(g => `<button class="player-genre-chip ${P.genre === g.key ? "active" : ""} ${counts[g.key] ? "" : "empty"}" data-genre="${g.key}"><span class="pgc-icon">${g.icon}</span>${g.label} <span class="pgc-count">${counts[g.key]}</span></button>`).join("");
  }

  function trackRow(t, i) {
    const active = P.cur && P.cur.id === t.id;
    const liked = P.likes.has(t.id);
    const idx = active ? `<span class="ptr-eq ${P.playing ? "playing" : "paused"}"><span></span><span></span><span></span></span>` : `<span class="ptr-play-hint">${i + 1}</span>`;
    return `<div class="player-track-row ${active ? "active" : ""}" data-id="${t.id}">
      <div class="ptr-idx">${idx}</div>
      <div class="ptr-info"><div class="ptr-title ${P.sort === "title" ? "sort-highlight" : ""}">${esc(t.title)}</div>
        <div class="ptr-sub"><span class="ptr-artist ${P.sort === "artist" ? "sort-highlight" : ""}" data-artist="${esc(t.artist)}">${esc(t.artist)}</span>${t.album ? `<span class="ptr-sep">·</span><span class="ptr-album ${P.sort === "album" ? "sort-highlight" : ""}">${esc(t.album)}</span>` : ""}</div></div>
      <div class="ptr-meta-right"><span class="ptr-genre hide-xs">${esc(t.genre || "")}</span><span class="ptr-codec hide-mobile">${esc(t.codec)}</span><span class="ptr-time">${fmt(t.dur)}</span>
        <button class="ptr-like-btn ${liked ? "is-liked" : ""}" data-like="${t.id}" title="Любимое">${liked ? "❤️" : "🤍"}</button></div></div>`;
  }

  function render() {
    if (!P.tracks.length) return;
    document.querySelectorAll(".player-view-tab").forEach(b => b.classList.toggle("active", b.dataset.view === P.view));
    document.querySelectorAll("[data-psort]").forEach(b => b.classList.toggle("active", b.dataset.psort === P.sort));
    $("pSortReset").classList.toggle("hidden", !P.sort);
    $("pArtistText").innerHTML = P.artist ? `<span class="pab-name">${esc(P.artist)}</span>` : "Все артисты";
    $("pArtistBtn").classList.toggle("has-selection", !!P.artist);
    $("pArtistClear").classList.toggle("hidden", !P.artist);
    const list = $("pList");

    if (P.view === "artists") {
      const pre = baseFiltered(false);
      const m = new Map();
      pre.forEach(t => m.set(t.artist, (m.get(t.artist) || 0) + 1));
      const arr = [...m.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0], "ru"));
      $("pGenres").classList.add("hidden");
      $("pCount").textContent = `${arr.length.toLocaleString("ru-RU")} артистов`;
      list.className = "player-artist-catalog";
      list.innerHTML = arr.slice(0, P.limit).map(([name, n]) => `<div class="player-artist-card ${P.artist === name ? "active" : ""}" data-open-artist="${esc(name)}">
        <div class="pac-avatar">🎤</div><div class="pac-info"><div class="pac-name">${esc(name)}</div><div class="pac-sub">${n} треков</div></div>
        <div class="pac-action"><button class="pac-play-btn" data-play-artist="${esc(name)}">▶ Играть</button></div></div>`).join("") +
        (arr.length > P.limit ? `<button class="btn-ghost player-more" data-more="1">Ещё +120</button>` : "") +
        (arr.length ? "" : `<div class="player-empty">Ничего не найдено</div>`);
      return;
    }

    $("pGenres").classList.remove("hidden");
    renderGenres(baseFiltered(true));
    P.filtered = applySort(baseFiltered(false));
    $("pCount").textContent = `${P.filtered.length.toLocaleString("ru-RU")} треков`;
    list.className = "player-tracks-list";
    if (!P.filtered.length) {
      list.innerHTML = `<div class="player-empty player-empty-box">${P.view === "fav" && !P.likes.size ? "Пока нет любимых — жми 🤍 у трека" : "Ничего не найдено"}
        <button class="btn-ghost player-reset-filters-btn" data-reset="1">Сбросить фильтры</button></div>`;
      return;
    }
    list.innerHTML = P.filtered.slice(0, P.limit).map(trackRow).join("") +
      (P.filtered.length > P.limit ? `<button class="btn-ghost player-more" data-more="1">Ещё +120 (из ${P.filtered.length - P.limit})</button>` : "");
  }

  // ---------- playback ----------
  function srcFor(t, ss) {
    return P.transcode ? `/api/stream/${t.id}?transcode=1&ss=${(ss || 0).toFixed(2)}` : `/api/stream/${t.id}`;
  }

  function playTrack(t, fromList) {
    if (!t) return;
    if (typeof window.radioStop === "function") window.radioStop();
    if (fromList) P.queue = (P.filtered.length ? P.filtered : P.tracks).slice();
    P.cur = t;
    P.base = 0;
    P.transcode = !!t.isAlac;
    audio.src = srcFor(t, 0);
    audio.play().catch(() => {});
    persist();
    updateNow();
    render();
  }

  function position() { return P.base + (audio.currentTime || 0); }
  function duration() { return Math.max(P.cur?.dur || 0, Number.isFinite(audio.duration) ? audio.duration + P.base : 0); }

  function seekTo(sec) {
    if (!P.cur) return;
    sec = Math.max(0, Math.min(duration() - 0.5, sec));
    if (P.transcode) {
      P.base = sec;
      audio.src = srcFor(P.cur, sec);
      audio.play().catch(() => {});
    } else {
      try { audio.currentTime = sec; } catch (_) {}
    }
  }

  function step(dir) {
    const q = P.queue.length ? P.queue : (P.filtered.length ? P.filtered : P.tracks);
    if (!q.length) return;
    if (dir < 0 && position() > 4) { seekTo(0); return; }
    if (P.shuffle && dir > 0) { playTrack(q[Math.floor(Math.random() * q.length)]); return; }
    const i = P.cur ? q.findIndex(t => t.id === P.cur.id) : -1;
    playTrack(q[(i + dir + q.length) % q.length]);
  }

  function togglePlay() {
    if (!P.cur) { const q = P.filtered.length ? P.filtered : P.tracks; playTrack(q[0], true); return; }
    if (!audio.src) { P.transcode = !!P.cur.isAlac; audio.src = srcFor(P.cur, 0); }
    if (audio.paused) { if (typeof window.radioStop === "function") window.radioStop(); audio.play().catch(() => {}); }
    else audio.pause();
  }

  function toggleLike(id) {
    if (P.likes.has(id)) P.likes.delete(id); else P.likes.add(id);
    persist();
    updateNow();
    render();
  }

  audio.addEventListener("error", () => {
    if (P.cur && !P.transcode) {
      const at = audio.currentTime || 0;
      P.transcode = true;
      P.base = at;
      audio.src = srcFor(P.cur, at);
      audio.play().catch(() => {});
    }
  });
  audio.addEventListener("ended", () => { if (P.repeat) seekTo(0) || audio.play().catch(() => {}); else step(1); });
  audio.addEventListener("play", () => { P.playing = true; updateNow(); render(); });
  audio.addEventListener("pause", () => { P.playing = false; updateNow(); render(); });

  function setCover(img, fallback, t) {
    img.classList.add("hidden");
    fallback.classList.remove("hidden");
    img.onload = () => { img.classList.remove("hidden"); fallback.classList.add("hidden"); };
    img.onerror = () => { img.classList.add("hidden"); fallback.classList.remove("hidden"); };
    img.src = `/api/cover/${t.id}`;
  }

  let coverId = -1;
  function updateNow() {
    const t = P.cur;
    const playing = P.playing;
    ["dPlay", "npPlay"].forEach(id => { $(id).textContent = playing ? "⏸" : "▶"; $(id).classList.toggle("paused", !playing); });
    ["dShuffle", "npShuffle"].forEach(id => $(id).classList.toggle(id[0] === "d" ? "liked" : "active", P.shuffle));
    ["dRepeat", "npRepeat"].forEach(id => $(id).classList.toggle(id[0] === "d" ? "liked" : "active", P.repeat));
    $("dDisc").classList.toggle("spinning", !!t);
    $("dDisc").classList.toggle("paused", !playing);
    $("npDisc").classList.toggle("spinning", !!t);
    $("npDisc").classList.toggle("paused", !playing);
    if (!t) return;
    const liked = P.likes.has(t.id);
    $("dTitle").textContent = t.title;
    $("dSub").innerHTML = `<span class="dock-artist">${esc(t.artist)}</span>${t.album ? `<span class="dock-sep">·</span><span class="dock-album">${esc(t.album)}</span>` : ""}`;
    $("dLike").textContent = liked ? "❤️" : "♡";
    $("dLike").classList.toggle("liked", liked);
    $("npHeart").textContent = liked ? "❤️" : "♡";
    $("npHeart").classList.toggle("is-liked", liked);
    $("npTitle").textContent = t.title;
    $("npArtist").textContent = t.artist;
    $("npAlbum").textContent = t.album || "";
    $("npStation").textContent = `${t.stIcon} ${t.station}`;
    const lossless = /FLAC|ALAC|WAV/.test(t.codec);
    $("npBadges").innerHTML = `<span class="np-badge np-badge-codec">${esc(t.codec)}</span>${lossless ? `<span class="np-badge np-badge-lossless">LOSSLESS</span>` : ""}${t.genre ? `<span class="np-badge np-badge-genre">${esc(t.genre)}</span>` : ""}`;
    if (coverId !== t.id) {
      coverId = t.id;
      setCover($("dCover"), $("dIcon"), t);
      setCover($("npImg"), $("npFallback"), t);
    }
    document.title = `${playing ? "▶ " : ""}${t.artist} — ${t.title} · RadioWork`;
    if ("mediaSession" in navigator) {
      try {
        navigator.mediaSession.metadata = new MediaMetadata({ title: t.title, artist: t.artist, album: t.album, artwork: [{ src: `/api/cover/${t.id}`, sizes: "512x512" }] });
        navigator.mediaSession.playbackState = playing ? "playing" : "paused";
      } catch (_) {}
    }
  }

  function tick() {
    if (P.mode === "player" && P.cur && !P.seeking) {
      const pos = position(), dur = duration();
      const v = dur ? Math.round((pos / dur) * 1000) : 0;
      for (const [s, c, d] of [["dSeek", "dCur", "dTot"], ["npSeek", "npCur", "npTot"]]) {
        $(s).value = v;
        $(s).style.setProperty("--progress", (v / 10) + "%");
        $(s).classList.toggle("paused", !P.playing);
        $(c).textContent = fmt(pos);
        $(d).textContent = fmt(dur);
      }
    }
    requestAnimationFrame(tick);
  }

  // ---------- artist picker ----------
  function renderPicker() {
    const tokens = norm($("apSearch").value).split(" ").filter(Boolean);
    const arr = P.artists.filter(a => tokens.every(tk => a.hay.includes(tk) || a.hay.includes(translit(tk)) || a.hay.includes(skel(translit(tk)))));
    $("apList").innerHTML = `<button class="artist-picker-item ${P.artist ? "" : "is-selected"}" data-pick=""><span class="api-item-main"><span class="api-item-bullet">◎</span><span class="api-item-name">Все артисты</span></span><span class="api-item-badge">${P.tracks.length}</span></button>` +
      arr.slice(0, 400).map(a => `<button class="artist-picker-item ${P.artist === a.name ? "is-selected" : ""}" data-pick="${esc(a.name)}"><span class="api-item-main"><span class="api-item-bullet">●</span><span class="api-item-name">${esc(a.name)}</span></span><span class="api-item-badge">${a.count}</span></button>`).join("") +
      (arr.length ? "" : `<div class="artist-picker-empty">Артист не найден</div>`);
  }
  function openPicker(open) {
    $("apBackdrop").classList.toggle("hidden", !open);
    if (open) { $("apSearch").value = ""; renderPicker(); setTimeout(() => $("apSearch").focus(), 30); }
  }
  function setArtist(name) { P.artist = name; P.limit = 120; if (name && P.view === "artists") P.view = "tracks"; persist(); render(); $("pList").scrollTop = 0; }

  // ---------- mode ----------
  function setMode(mode) {
    P.mode = mode;
    $("appShell").classList.toggle("player-mode", mode === "player");
    document.querySelectorAll("#modeSwitch [data-mode]").forEach(b => b.classList.toggle("active", b.dataset.mode === mode));
    if (mode === "radio") { audio.pause(); $("playerShell").classList.remove("is-fullscreen"); }
    persist();
    render();
  }

  // ---------- events ----------
  document.querySelectorAll("#modeSwitch [data-mode]").forEach(b => b.addEventListener("click", () => setMode(b.dataset.mode)));
  document.querySelectorAll(".player-view-tab").forEach(b => b.addEventListener("click", () => { P.view = b.dataset.view; P.limit = 120; persist(); render(); $("pList").scrollTop = 0; }));
  document.querySelectorAll("[data-psort]").forEach(b => b.addEventListener("click", () => { P.sort = P.sort === b.dataset.psort ? "" : b.dataset.psort; persist(); render(); }));
  $("pSortReset").addEventListener("click", () => { P.sort = ""; persist(); render(); });
  let qTimer = 0;
  $("pSearch").addEventListener("input", e => { clearTimeout(qTimer); qTimer = setTimeout(() => { P.query = e.target.value; P.limit = 120; render(); $("pList").scrollTop = 0; }, 90); });
  $("pStation").addEventListener("change", e => { P.station = e.target.value; P.limit = 120; persist(); render(); });
  $("pArtistBtn").addEventListener("click", () => openPicker(true));
  $("pArtistClear").addEventListener("click", () => setArtist(""));
  $("pFull").addEventListener("click", () => $("playerShell").classList.toggle("is-fullscreen"));
  $("pGenres").addEventListener("click", e => { const b = e.target.closest("[data-genre]"); if (!b) return; P.genre = P.genre === b.dataset.genre ? "" : b.dataset.genre; P.limit = 120; persist(); render(); });
  $("pList").addEventListener("click", e => {
    const like = e.target.closest("[data-like]");
    if (like) { e.stopPropagation(); toggleLike(Number(like.dataset.like)); return; }
    if (e.target.closest("[data-more]")) { P.limit += 120; render(); return; }
    if (e.target.closest("[data-reset]")) { P.query = ""; $("pSearch").value = ""; P.station = ""; $("pStation").value = ""; P.artist = ""; P.genre = ""; persist(); render(); return; }
    const pa = e.target.closest("[data-play-artist]");
    if (pa) { e.stopPropagation(); setArtist(pa.dataset.playArtist); playTrack(P.filtered[0], true); return; }
    const oa = e.target.closest("[data-open-artist]");
    if (oa) { setArtist(oa.dataset.openArtist); return; }
    const ar = e.target.closest("[data-artist]");
    if (ar) { e.stopPropagation(); setArtist(ar.dataset.artist); return; }
    const row = e.target.closest("[data-id]");
    if (row) { const t = P.byId.get(Number(row.dataset.id)); if (P.cur && t && P.cur.id === t.id) togglePlay(); else playTrack(t, true); }
  });
  $("apClose").addEventListener("click", () => openPicker(false));
  $("apBackdrop").addEventListener("click", e => { if (e.target.id === "apBackdrop") openPicker(false); });
  $("apSearch").addEventListener("input", renderPicker);
  $("apList").addEventListener("click", e => { const b = e.target.closest("[data-pick]"); if (!b) return; openPicker(false); setArtist(b.dataset.pick); });

  const openNp = open => $("npBackdrop").classList.toggle("hidden", !open);
  $("dSummary").addEventListener("click", () => { if (P.cur) openNp(true); });
  $("npClose").addEventListener("click", () => openNp(false));
  $("npDrag").addEventListener("click", () => openNp(false));
  $("npBackdrop").addEventListener("click", e => { if (e.target.id === "npBackdrop") openNp(false); });
  $("npArtist").addEventListener("click", () => { if (P.cur) { openNp(false); setArtist(P.cur.artist); } });

  for (const [a, b] of [["dPlay", "npPlay"], ["dPrev", "npPrev"], ["dNext", "npNext"], ["dShuffle", "npShuffle"], ["dRepeat", "npRepeat"], ["dLike", "npHeart"]]) {
    const fn = {
      dPlay: togglePlay, dPrev: () => step(-1), dNext: () => step(1),
      dShuffle: () => { P.shuffle = !P.shuffle; persist(); updateNow(); },
      dRepeat: () => { P.repeat = !P.repeat; persist(); updateNow(); },
      dLike: () => { if (P.cur) toggleLike(P.cur.id); }
    }[a];
    $(a).addEventListener("click", fn);
    $(b).addEventListener("click", fn);
  }
  for (const id of ["dSeek", "npSeek"]) {
    const el = $(id);
    el.addEventListener("input", () => { P.seeking = true; el.style.setProperty("--progress", (el.value / 10) + "%"); });
    el.addEventListener("change", () => { P.seeking = false; seekTo((el.value / 1000) * duration()); });
  }
  $("dVol").value = Math.round(P.volume * 100);
  $("dVol").addEventListener("input", e => { P.volume = e.target.value / 100; audio.volume = P.volume; persist(); });

  if ("mediaSession" in navigator) {
    try {
      navigator.mediaSession.setActionHandler("play", togglePlay);
      navigator.mediaSession.setActionHandler("pause", togglePlay);
      navigator.mediaSession.setActionHandler("previoustrack", () => step(-1));
      navigator.mediaSession.setActionHandler("nexttrack", () => step(1));
    } catch (_) {}
  }

  window.addEventListener("keydown", e => {
    if (e.code === "Escape") { openPicker(false); openNp(false); $("playerShell").classList.remove("is-fullscreen"); }
    if (P.mode !== "player" || /INPUT|SELECT|TEXTAREA/.test((document.activeElement || {}).tagName || "")) return;
    if (e.code === "Space") { e.preventDefault(); togglePlay(); }
    else if (e.code === "ArrowRight") step(1);
    else if (e.code === "ArrowLeft") step(-1);
  });

  // stop player when radio power is switched on
  $("btnPower").addEventListener("click", () => { if (!audio.paused) audio.pause(); }, true);

  // ---------- init ----------
  const wait = setInterval(() => {
    if (!state.stations.length) return;
    clearInterval(wait);
    loadData();
    setMode(P.mode);
    updateNow();
    requestAnimationFrame(tick);
  }, 150);
  window.radioPlayer = P;
})();
