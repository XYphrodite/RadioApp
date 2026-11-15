    const THEMES = {
      "Nightdrive FM": { freq: 104.5, icon: "🌃", color: "#ff2a85" },
      "Westside FM":   { freq: 88.7,  icon: "🌴", color: "#19e68c" },
      "Velvet 98.3":   { freq: 98.3,  icon: "🎷", color: "#f4b961" },
      "Thunder Rock":  { freq: 92.5,  icon: "⚡", color: "#ff5c33" },
      "Drift FM":      { freq: 89.1,  icon: "🏎️", color: "#b44cff" },
      "Golden Era FM": { freq: 90.3,  icon: "📼", color: "#ffd166" },
      "Static Radio":  { freq: 96.1,  icon: "🎸", color: "#9ef01a" },
      "Island Radio":  { freq: 91.2,  icon: "🏝️", color: "#00d4b2" },
      "Funk Zone":     { freq: 99.4,  icon: "🪩", color: "#ff70a6" },
      "Country Trail": { freq: 87.9,  icon: "🤠", color: "#e08e45" },
      "Soul Wave":     { freq: 103.9, icon: "💜", color: "#c77dff" }
    };

    const FREQ_MIN = 87.5, FREQ_MAX = 108.0;
    const saved = JSON.parse(localStorage.getItem("radiowork_web_state") || "{}");

    const state = {
      stations: [],
      activeName: saved.activeName || "Nightdrive FM",
      powerOn: false, // Browsers require 1 click to start audio cleanly
      staticFx: saved.staticFx ?? true,
      volume: saved.volume ?? 0.75,
      sortMode: saved.sortMode || "count",
      likedIds: new Set(saved.likedIds || []),
      bannedIds: new Set(saved.bannedIds || []),
      skipOffsets: saved.skipOffsets || {},
      failedDirectIds: new Set()
    };

    function persist() {
      localStorage.setItem("radiowork_web_state", JSON.stringify({
        activeName: state.activeName,
        staticFx: state.staticFx,
        volume: state.volume,
        sortMode: state.sortMode,
        likedIds: [...state.likedIds],
        bannedIds: [...state.bannedIds],
        skipOffsets: state.skipOffsets
      }));
    }

    function cleanTitle(filename, artist) {
      if (!filename) return "Unknown Track";
      let stem = filename.replace(/\.[^/.]+$/, "").replace(/_/g, " ").trim();
      stem = stem.replace(/^(?:(?:cd|disc)\s*\d+\s*[-.]?\s*)?(?:\d{1,2}-)?\d{1,3}(?:\s*[.-]\s*|\s+)/i, "").trim();
      const dash = stem.indexOf(" - ");
      if (dash > 0 && artist) {
        const na = artist.replace(/_/g, " ").trim().toLowerCase();
        const pref = stem.slice(0, dash).trim().toLowerCase();
        if (pref.startsWith(na) || na.startsWith(pref)) {
          stem = stem.slice(dash + 3).trim();
        }
      }
      stem = stem.replace(/\s*[\(\[](?:skysound\.cc|zaycev\.net|muzofon\.[a-z]+|mp3party\.[a-z]+|promodj\.com)[\)\]]\s*$/i, "").trim();
      return stem || filename;
    }

    function codecBadge(filename, album = "") {
      if (/\[24-|24\/96/i.test(album)) return "FLAC 24/96";
      if (/\[ALAC\]/i.test(album)) return "ALAC LOSSLESS";
      const ext = (filename.split(".").pop() || "").toLowerCase();
      return { flac: "FLAC LOSSLESS", m4a: "AAC / M4A", ogg: "OGG VORBIS", wav: "WAV PCM", mp3: "MP3" }[ext] || ext.toUpperCase();
    }

    function fnvHash(str) {
      let h = 2166136261 >>> 0;
      for (let i = 0; i < str.length; i++) {
        h ^= str.toLowerCase().charCodeAt(i);
        h = Math.imul(h, 16777619) >>> 0;
      }
      return h >>> 0;
    }

    function buildSchedule(st) {
      let pool = st.rawTracks.filter(t => !state.bannedIds.has(t.id));
      if (!pool.length) pool = st.rawTracks.slice();
      pool.sort((a, b) => a.id - b.id);

      // Deterministic shuffle
      let seed = (fnvHash(st.name) ^ 0x9e3779b9) >>> 0;
      for (let i = pool.length - 1; i > 0; i--) {
        seed = (Math.imul(seed ^ (seed >>> 15), 2246822519) ^ 0x85ebca6b) >>> 0;
        const j = seed % (i + 1);
        [pool[i], pool[j]] = [pool[j], pool[i]];
      }

      const liked = pool.filter(t => state.likedIds.has(t.id));
      if (liked.length > 0 && pool.length >= 4) {
        const stride = Math.max(2, Math.floor(pool.length / (liked.length + 1)));
        let pos = Math.floor(stride / 2);
        for (const lt of liked) {
          pool.splice(Math.min(pos, pool.length), 0, lt);
          pos = (pos + stride + 1) % (pool.length + 1);
        }
      }

      const cum = new Float64Array(pool.length);
      let sum = 0;
      for (let i = 0; i < pool.length; i++) {
        sum += pool[i].dur;
        cum[i] = sum;
      }
      st.slots = pool;
      st.cum = cum;
      st.totalCycle = Math.max(1, sum);
    }

    function locateLive(st, nowSec) {
      const off = state.skipOffsets[st.name] || 0;
      let pos = (nowSec + st.phase + off) % st.totalCycle;
      if (pos < 0) pos += st.totalCycle;

      let lo = 0, hi = st.cum.length - 1;
      while (lo < hi) {
        const mid = (lo + hi) >> 1;
        if (st.cum[mid] <= pos) lo = mid + 1;
        else hi = mid;
      }
      const start = lo === 0 ? 0 : st.cum[lo - 1];
      const tr = st.slots[lo];
      const elapsed = Math.max(0, Math.min(tr.dur, pos - start));

      const history = [];
      let back = elapsed;
      for (let h = 1; h <= Math.min(2, st.slots.length - 1); h++) {
        const prev = st.slots[(lo - h + st.slots.length) % st.slots.length];
        history.push({ track: prev, minsAgo: Math.max(1, Math.round(back / 60)) });
        back += prev.dur;
      }
      return { track: tr, slotIdx: lo, elapsed, history };
    }

    // Audio + Web Audio Spectrum + Squelch
    const audio = new Audio();
    audio.crossOrigin = "anonymous";
    audio.preload = "auto";

    let audioCtx = null, analyser = null, freqBins = null, sourceAttached = false;
    let syncedTrackId = -1, syncedStation = "", syncedTranscode = false, baseOffsetSec = 0, coverTrackId = -1;

    function ensureAudioGraph() {
      try {
        if (!audioCtx) audioCtx = new (window.AudioContext || window.webkitAudioContext)();
        if (audioCtx.state === "suspended") audioCtx.resume();
        if (audioCtx.state === "running" && !sourceAttached) {
          analyser = audioCtx.createAnalyser();
          analyser.fftSize = 256;
          analyser.smoothingTimeConstant = 0.78;
          freqBins = new Uint8Array(analyser.frequencyBinCount);
          const src = audioCtx.createMediaElementSource(audio);
          src.connect(analyser);
          analyser.connect(audioCtx.destination);
          sourceAttached = true;
        }
      } catch (_) {}
    }

    function playStaticBurst(sec = 0.16) {
      if (!state.staticFx || !state.powerOn) return;
      try {
        ensureAudioGraph();
        const len = Math.floor(audioCtx.sampleRate * sec);
        const buf = audioCtx.createBuffer(1, len, audioCtx.sampleRate);
        const d = buf.getChannelData(0);
        for (let i = 0; i < len; i++) d[i] = (Math.random() * 2 - 1) * Math.sin((i / len) * Math.PI) * 0.22 * state.volume;
        const n = audioCtx.createBufferSource();
        n.buffer = buf;
        const f = audioCtx.createBiquadFilter();
        f.type = "bandpass";
        f.frequency.value = 1900;
        f.Q.value = 0.9;
        n.connect(f);
        f.connect(audioCtx.destination);
        n.start();
      } catch (_) {}
    }

    audio.addEventListener("error", () => {
      if (syncedTrackId >= 0 && !syncedTranscode) {
        state.failedDirectIds.add(syncedTrackId);
        syncedTranscode = true;
        audio.src = `/api/stream/${syncedTrackId}?transcode=1&ss=${baseOffsetSec.toFixed(2)}`;
        if (state.powerOn) audio.play().catch(() => {});
      }
    });

    audio.addEventListener("ended", () => syncAudio(true));

    function syncAudio(force = false) {
      const st = state.stations.find(s => s.name === state.activeName);
      if (!st) return;
      const lv = locateLive(st, Date.now() / 1000);
      const tr = lv.track;

      audio.volume = state.volume;
      audio.muted = !state.powerOn;
      if (!state.powerOn) return;

      ensureAudioGraph();
      const needTranscode = tr.isAlac || state.failedDirectIds.has(tr.id);
      const changed = force || tr.id !== syncedTrackId || st.name !== syncedStation || needTranscode !== syncedTranscode;

      if (changed) {
        syncedTrackId = tr.id;
        syncedStation = st.name;
        syncedTranscode = needTranscode;
        if (needTranscode) {
          baseOffsetSec = lv.elapsed;
          audio.src = `/api/stream/${tr.id}?transcode=1&ss=${lv.elapsed.toFixed(2)}`;
        } else {
          baseOffsetSec = 0;
          audio.src = `/api/stream/${tr.id}`;
          const onMeta = () => {
            audio.removeEventListener("loadedmetadata", onMeta);
            try { audio.currentTime = lv.elapsed; } catch (_) {}
          };
          audio.addEventListener("loadedmetadata", onMeta);
        }
        audio.play().catch(() => {});
      } else if (!needTranscode && audio.readyState >= 1 && Math.abs((audio.currentTime || 0) - lv.elapsed) > 2.5) {
        try { audio.currentTime = lv.elapsed; } catch (_) {}
      }
    }

    function fmtTime(sec) {
      const s = Math.max(0, Math.floor(sec));
      return `${String(Math.floor(s / 60)).padStart(2, "0")}:${String(s % 60).padStart(2, "0")}`;
    }

    function freqToPct(f) { return ((f - FREQ_MIN) / (FREQ_MAX - FREQ_MIN)) * 100; }

    function buildTuner() {
      const el = document.getElementById("tunerTicks");
      el.innerHTML = "";
      for (let f = 88; f <= 108; f += 0.5) {
        const major = Number.isInteger(f) && f % 2 === 0;
        const t = document.createElement("div");
        t.className = "tick " + (major ? "major" : "minor");
        t.style.left = freqToPct(f) + "%";
        el.appendChild(t);
        if (major) {
          const l = document.createElement("div");
          l.className = "tick-label";
          l.style.left = freqToPct(f) + "%";
          l.textContent = f;
          el.appendChild(l);
        }
      }
      state.stations.forEach(st => {
        const pip = document.createElement("div");
        pip.className = "station-pip";
        pip.dataset.name = st.name;
        pip.style.left = freqToPct(st.freq) + "%";
        pip.style.background = st.color;
        pip.style.color = st.color;
        pip.title = `${st.freq.toFixed(1)} FM — ${st.name}`;
        pip.addEventListener("click", e => { e.stopPropagation(); tuneStation(st.name); });
        el.appendChild(pip);
      });
    }

    function tuneStation(name) {
      if (name === state.activeName) return;
      state.activeName = name;
      persist();
      playStaticBurst(0.16);
      const fl = document.getElementById("tuningFlash");
      fl.classList.add("flash");
      setTimeout(() => fl.classList.remove("flash"), 150);
      syncAudio(true);
      render();
    }

    function stepFrequency(dir) {
      if (!state.stations.length) return;
      const sorted = state.stations.slice().sort((a, b) => a.freq - b.freq);
      const idx = Math.max(0, sorted.findIndex(s => s.name === state.activeName));
      tuneStation(sorted[(idx + dir + sorted.length) % sorted.length].name);
    }

    function render() {
      if (!state.stations.length) return;
      const nowSec = Date.now() / 1000;
      const st = state.stations.find(s => s.name === state.activeName) || state.stations[0];
      const lv = locateLive(st, nowSec);
      const tr = lv.track;

      document.documentElement.style.setProperty("--accent", st.color);
      document.documentElement.style.setProperty("--accent-glow", st.color + "52");

      document.getElementById("tunerNeedle").style.left = freqToPct(st.freq) + "%";
      document.querySelectorAll(".station-pip").forEach(p => p.classList.toggle("active", p.dataset.name === st.name));

      document.getElementById("uiFreq").textContent = st.freq.toFixed(1);
      document.getElementById("uiStationName").textContent = st.name;
      document.getElementById("uiStationFormat").textContent = st.format;
      document.getElementById("uiStationCount").textContent = `· ${st.rawTracks.length.toLocaleString("ru-RU")} треков в ротации`;
      document.getElementById("vinylIcon").textContent = st.icon;

      if (tr.id !== coverTrackId) {
        coverTrackId = tr.id;
        const img = document.getElementById("vinylCover");
        const sp = document.getElementById("vinylSpindle");
        const ic = document.getElementById("vinylIcon");
        img.style.display = "none";
        sp.style.display = "none";
        ic.style.display = "flex";
        img.onload = () => { img.style.display = "block"; sp.style.display = "block"; ic.style.display = "none"; };
        img.onerror = () => { img.style.display = "none"; sp.style.display = "none"; ic.style.display = "flex"; };
        img.src = `/api/cover/${tr.id}`;
      }

      document.getElementById("uiGenre").textContent = tr.genre || st.format;
      document.getElementById("uiCodec").textContent = tr.codec;
      document.getElementById("uiBpm").textContent = `${tr.bpm} BPM`;
      document.getElementById("uiEnergy").textContent = `ENERGY ${tr.energy}%`;

      document.getElementById("uiTrackTitle").textContent = tr.title;
      document.getElementById("uiTrackArtist").textContent = tr.artist;
      document.getElementById("uiTrackAlbum").textContent = tr.album;

      document.getElementById("uiElapsed").textContent = fmtTime(lv.elapsed);
      document.getElementById("uiDuration").textContent = fmtTime(tr.dur);
      document.getElementById("uiProgressFill").style.width = ((lv.elapsed / tr.dur) * 100).toFixed(1) + "%";

      document.getElementById("btnLike").classList.toggle("liked", state.likedIds.has(tr.id));

      document.getElementById("uiHistory").innerHTML = lv.history
        .map(h => `<div class="history-item"><span class="history-time">${h.minsAgo} мин назад</span> <strong>${h.track.artist}</strong> — ${h.track.title}</div>`)
        .join("");

      const ordered = state.stations.slice().sort((a, b) =>
        state.sortMode === "freq" ? a.freq - b.freq : b.rawTracks.length - a.rawTracks.length);

      const listEl = document.getElementById("stationsList");
      listEl.innerHTML = ordered.map(s => {
        const slv = locateLive(s, nowSec);
        const pct = ((slv.elapsed / slv.track.dur) * 100).toFixed(1);
        return `
          <div class="st-card ${s.name === st.name ? "active" : ""}" style="--st-color: ${s.color}" data-name="${s.name}">
            <div class="st-row-1">
              <div class="st-title-wrap">
                <span class="st-freq">${s.freq.toFixed(1)}</span>
                <span class="st-name">${s.icon} ${s.name}</span>
              </div>
              <span class="st-count">${s.rawTracks.length.toLocaleString("ru-RU")} тр.</span>
            </div>
            <div class="st-row-2">▶ ${slv.track.artist} — ${slv.track.title}</div>
            <div class="st-mini-bar"><div class="st-mini-fill" style="width:${pct}%"></div></div>
          </div>`;
      }).join("");

      listEl.querySelectorAll(".st-card").forEach(c => c.addEventListener("click", () => tuneStation(c.dataset.name)));

      document.getElementById("whFreq").textContent = `${st.freq.toFixed(1)} FM`;
      document.getElementById("whFreq").style.color = st.color;
      document.getElementById("whName").textContent = st.name;
      document.getElementById("whTrack").textContent = `${tr.artist} — ${tr.title}`;
    }

    // Controls
    document.getElementById("btnPower").addEventListener("click", () => {
      state.powerOn = !state.powerOn;
      document.getElementById("btnPower").classList.toggle("off", !state.powerOn);
      document.getElementById("powerLabel").textContent = state.powerOn ? "В ЭФИРЕ" : "MUTE (ЭФИР ИДЁТ)";
      document.getElementById("vinylDisc").classList.toggle("paused", !state.powerOn);
      document.getElementById("liveDot").classList.toggle("muted", !state.powerOn);
      if (state.powerOn) syncAudio(true);
      else audio.muted = true;
    });

    document.getElementById("btnPrevSt").addEventListener("click", () => stepFrequency(-1));
    document.getElementById("btnNextSt").addEventListener("click", () => stepFrequency(1));

    document.getElementById("btnNextTrack").addEventListener("click", () => {
      const st = state.stations.find(s => s.name === state.activeName);
      if (!st) return;
      const lv = locateLive(st, Date.now() / 1000);
      const rem = Math.max(0.1, lv.track.dur - lv.elapsed + 0.1);
      state.skipOffsets[st.name] = ((state.skipOffsets[st.name] || 0) + rem) % st.totalCycle;
      persist();
      playStaticBurst(0.22);
      syncAudio(true);
      render();
    });

    document.getElementById("btnLike").addEventListener("click", () => {
      const st = state.stations.find(s => s.name === state.activeName);
      if (!st) return;
      const nowSec = Date.now() / 1000;
      const before = locateLive(st, nowSec);
      if (state.likedIds.has(before.track.id)) state.likedIds.delete(before.track.id);
      else state.likedIds.add(before.track.id);
      buildSchedule(st);
      const newIdx = st.slots.findIndex(t => t.id === before.track.id);
      if (newIdx >= 0) {
        const start = newIdx === 0 ? 0 : st.cum[newIdx - 1];
        state.skipOffsets[st.name] = ((start + before.elapsed - (nowSec + st.phase)) % st.totalCycle + st.totalCycle) % st.totalCycle;
      }
      persist();
      render();
    });

    document.getElementById("btnBan").addEventListener("click", () => {
      const st = state.stations.find(s => s.name === state.activeName);
      if (!st) return;
      const nowSec = Date.now() / 1000;
      const before = locateLive(st, nowSec);
      state.bannedIds.add(before.track.id);
      buildSchedule(st);
      persist();
      playStaticBurst(0.22);
      syncAudio(true);
      render();
    });

    document.getElementById("btnStaticToggle").addEventListener("click", e => {
      state.staticFx = !state.staticFx;
      e.currentTarget.textContent = state.staticFx ? "📻 Шум: ВКЛ" : "📻 Шум: ВЫКЛ";
      persist();
    });
    document.getElementById("btnStaticToggle").textContent = state.staticFx ? "📻 Шум: ВКЛ" : "📻 Шум: ВЫКЛ";

    const volSlider = document.getElementById("volSlider");
    volSlider.value = Math.round(state.volume * 100);
    document.getElementById("volVal").textContent = `${volSlider.value}%`;
    volSlider.addEventListener("input", e => {
      state.volume = Number(e.target.value) / 100;
      audio.volume = state.volume;
      document.getElementById("volVal").textContent = `${e.target.value}%`;
      persist();
    });

    document.querySelectorAll(".sort-tab").forEach(btn => {
      btn.classList.toggle("active", btn.dataset.sort === state.sortMode);
      btn.addEventListener("click", () => {
        document.querySelectorAll(".sort-tab").forEach(b => b.classList.remove("active"));
        btn.classList.add("active");
        state.sortMode = btn.dataset.sort;
        persist();
        render();
      });
    });

    // Tuner drag & wheel
    const scale = document.getElementById("tunerScale");
    let dragging = false;
    function dragAt(clientX) {
      if (!state.stations.length) return;
      const r = scale.getBoundingClientRect();
      const target = FREQ_MIN + Math.min(1, Math.max(0, (clientX - r.left) / r.width)) * (FREQ_MAX - FREQ_MIN);
      let best = state.stations[0], diff = Infinity;
      state.stations.forEach(s => { const d = Math.abs(s.freq - target); if (d < diff) { diff = d; best = s; } });
      tuneStation(best.name);
    }
    scale.addEventListener("pointerdown", e => { dragging = true; scale.setPointerCapture(e.pointerId); dragAt(e.clientX); });
    scale.addEventListener("pointermove", e => { if (dragging) dragAt(e.clientX); });
    scale.addEventListener("pointerup", () => { dragging = false; });
    scale.addEventListener("wheel", e => { e.preventDefault(); stepFrequency(e.deltaY > 0 ? 1 : -1); }, { passive: false });

    // Station Wheel
    function toggleWheel(force) {
      const bd = document.getElementById("wheelBackdrop");
      const open = typeof force === "boolean" ? force : !bd.classList.contains("open");
      if (open) {
        const cont = document.getElementById("wheelContainer");
        cont.querySelectorAll(".wheel-node").forEach(n => n.remove());
        const nowSec = Date.now() / 1000;
        state.stations.forEach((s, idx) => {
          const ang = (idx / state.stations.length) * Math.PI * 2 - Math.PI / 2;
          const node = document.createElement("div");
          node.className = "wheel-node" + (s.name === state.activeName ? " active" : "");
          node.style.left = (290 + Math.cos(ang) * 228) + "px";
          node.style.top = (290 + Math.sin(ang) * 228) + "px";
          node.style.setProperty("--node-color", s.color);
          node.innerHTML = `<div class="wheel-node-icon">${s.icon}</div><div class="wheel-node-freq">${s.freq.toFixed(1)}</div><div class="wheel-node-name">${s.name}</div>`;
          node.addEventListener("mouseenter", () => {
            const lv = locateLive(s, nowSec);
            document.getElementById("whFreq").textContent = `${s.freq.toFixed(1)} FM`;
            document.getElementById("whFreq").style.color = s.color;
            document.getElementById("whName").textContent = s.name;
            document.getElementById("whTrack").textContent = `${lv.track.artist} — ${lv.track.title}`;
          });
          node.addEventListener("click", () => { tuneStation(s.name); toggleWheel(false); });
          cont.appendChild(node);
        });
      }
      bd.classList.toggle("open", open);
    }

    document.getElementById("btnOpenWheel").addEventListener("click", () => toggleWheel(true));
    document.getElementById("wheelBackdrop").addEventListener("click", e => { if (e.target.id === "wheelBackdrop") toggleWheel(false); });

    window.addEventListener("keydown", e => {
      if (document.getElementById("appShell").classList.contains("player-mode") || /INPUT|SELECT|TEXTAREA/.test((document.activeElement||{}).tagName||"")) return;
      if (e.code === "KeyQ") toggleWheel();
      else if (e.code === "Escape") toggleWheel(false);
      else if (e.code === "ArrowRight") stepFrequency(1);
      else if (e.code === "ArrowLeft") stepFrequency(-1);
      else if (e.code === "Space") { e.preventDefault(); document.getElementById("btnPower").click(); }
    });

    // Spectrum canvas
    const canvas = document.getElementById("spectrumCanvas");
    const ctx = canvas.getContext("2d");
    const BARS = 48, barVals = new Array(BARS).fill(0.06);

    function drawSpectrum(ts) {
      const w = canvas.width, h = canvas.height;
      ctx.clearRect(0, 0, w, h);
      const st = state.stations.find(s => s.name === state.activeName);
      const color = st ? st.color : "#ff2a85";

      let liveFft = false;
      if (state.powerOn && analyser && freqBins) {
        analyser.getByteFrequencyData(freqBins);
        for (let k = 0; k < 20; k++) if (freqBins[k] > 3) { liveFft = true; break; }
      }

      const gap = 4, barW = (w - gap * (BARS - 1)) / BARS;
      for (let i = 0; i < BARS; i++) {
        let target = 0.05;
        if (state.powerOn && state.volume > 0) {
          if (liveFft) {
            const bi = Math.min(freqBins.length - 1, Math.floor((i / BARS) * (freqBins.length * 0.72)));
            target = Math.max(0.05, (freqBins[bi] / 255) * (0.55 + state.volume * 0.6));
          } else if (!audio.paused) {
            target = Math.min(1, (0.2 + Math.abs(Math.sin(ts * 0.006 + i * 0.45)) * 0.55) * state.volume);
          }
        }
        barVals[i] += (target - barVals[i]) * 0.28;
        const bh = Math.max(3, barVals[i] * h);
        const grad = ctx.createLinearGradient(0, h, 0, 0);
        grad.addColorStop(0, color + "55");
        grad.addColorStop(1, color);
        ctx.fillStyle = grad;
        ctx.beginPath();
        ctx.roundRect(i * (barW + gap), h - bh, barW, bh, 2);
        ctx.fill();
      }
      requestAnimationFrame(drawSpectrum);
    }

    async function boot() {
      const res = await fetch("/api/stations");
      const data = await res.json();
      const artists = new Set();
      let totalSec = 0, idx = 0;

      const entries = Object.entries(data.stations).sort((a, b) => a[0].localeCompare(b[0]));
      state.stations = [];
      for (const [key, entry] of entries) {
        if (!entry.tracks || !entry.tracks.length) continue;
        const name = entry.name || key;
        const th = THEMES[name] || { freq: parseFloat(entry.frequency) || 100.1, icon: "📻", color: "#00d4ff" };
        const rawTracks = entry.tracks.map(t => {
          if (t.artist) artists.add(t.artist.trim().toLowerCase());
          const dur = t.duration_sec >= 5 ? t.duration_sec : 180;
          totalSec += dur;
          return {
            id: t.id,
            title: cleanTitle(t.filename, t.artist),
            artist: t.artist || "Unknown Artist",
            album: t.album || "",
            genre: t.genre || entry.format,
            codec: codecBadge(t.filename, t.album),
            isAlac: /\.m4a$/i.test(t.filename) && /\[ALAC\]/i.test(t.album || ""),
            bpm: Math.round(t.bpm > 30 ? t.bpm : 112),
            energy: Math.round((t.energy ?? 0.65) * 100),
            dur
          };
        });
        const stObj = {
          name,
          format: entry.format,
          freq: th.freq,
          icon: th.icon,
          color: th.color,
          phase: (fnvHash(name) % 86400) + idx * 137,
          rawTracks
        };
        idx++;
        buildSchedule(stObj);
        state.stations.push(stObj);
      }

      document.getElementById("statsPill").textContent =
        `${data.tagged.toLocaleString("ru-RU")} треков · ${artists.size.toLocaleString("ru-RU")} артистов · ${Math.round(totalSec / 3600)} ч`;

      buildTuner();
      render();
      setInterval(() => {
        document.getElementById("clockPill").textContent = new Date().toLocaleTimeString("ru-RU");
        render();
        if (state.powerOn) syncAudio(false);
      }, 1000);
    }

    document.getElementById("clockPill").textContent = new Date().toLocaleTimeString("ru-RU");
    requestAnimationFrame(drawSpectrum);
    boot();
    window.radioStop = () => { if (state.powerOn) document.getElementById("btnPower").click(); };
