window.radioAudio = (() => {
  const FREQ_MIN = 87.5;
  const FREQ_MAX = 108.0;
  const BARS = 48;

  let dotNetRef = null;
  let audioEl = null;
  let preloadEl = null;
  let preloadTrackId = -1;
  let audioCtx = null;
  let analyser = null;
  let mediaSourceAttached = false;
  let freqBins = null;
  let rafId = null;

  let platformPreferOpus = false;
  let skipWebAudioCapture = false;

  let currentTrackId = -1;
  let currentStreamMode = "none"; // "opus" | "transcode" | "direct"
  let currentDirectUrl = "";
  let currentTranscodeBaseUrl = "";
  let currentOpusBaseUrl = "";
  let streamBaseOffsetSec = 0;
  let streamLoadStartedAt = 0;
  let lastSwitchAt = 0;
  let tuneDebounceTimer = null;
  let artworkTimer = null;

  let vizState = {
    powerOn: true,
    volume: 0.75,
    accentHex: "#ff2a85",
    bpm: 120,
    energy: 75,
    staticFx: true
  };

  const barVals = new Array(BARS).fill(0.08);
  const failedDirectIds = new Set();
  const failedOpusIds = new Set();
  const retriedNoCorsIds = new Set();

  function updateMediaSession(opts) {
    if (!("mediaSession" in navigator) || typeof MediaMetadata === "undefined") return;
    if (artworkTimer) {
      clearTimeout(artworkTimer);
      artworkTimer = null;
    }

    const applyMeta = (withArtwork) => {
      try {
        const artwork = withArtwork && opts.coverUrl
          ? [{ src: opts.coverUrl, sizes: "360x360", type: "image/jpeg" }]
          : [];
        navigator.mediaSession.metadata = new MediaMetadata({
          title: opts.trackTitle || "Прямой эфир",
          artist: opts.trackArtist || "RadioWork FM",
          album: opts.stationLabel || "RadioWork // FM",
          artwork
        });
        navigator.mediaSession.playbackState = opts.powerOn ? "playing" : "paused";
      } catch (_) {}
    };

    if (skipWebAudioCapture && opts.coverUrl) {
      // Update text immediately, defer cover fetch by 250ms so audio stream wins bandwidth
      applyMeta(false);
      artworkTimer = setTimeout(() => {
        artworkTimer = null;
        if (currentTrackId === Number(opts.trackId)) {
          applyMeta(true);
        }
      }, 250);
    } else {
      applyMeta(true);
    }
  }

  function createAudioElement(forPreload = false) {
    const el = new Audio();
    if (!skipWebAudioCapture && !forPreload) {
      el.crossOrigin = "anonymous";
    }
    el.preload = "auto";
    bindAudioEvents(el);
    return el;
  }

  function bindAudioEvents(el) {
    el.addEventListener("ended", () => {
      if (el !== audioEl) return;
      if (dotNetRef) {
        dotNetRef.invokeMethodAsync("OnAudioTrackEndedAsync").catch(() => {});
      }
    });

    el.addEventListener("error", () => {
      if (el !== audioEl || currentTrackId < 0) return;

      // 1) If Opus transcode failed (e.g. server without ffmpeg), fall back to direct file stream
      if (currentStreamMode === "opus" && currentDirectUrl) {
        failedOpusIds.add(currentTrackId);
        currentStreamMode = "direct";
        const target = Math.max(0, streamBaseOffsetSec);
        streamBaseOffsetSec = 0;
        streamLoadStartedAt = performance.now();
        el.src = currentDirectUrl;
        const onMeta = () => {
          el.removeEventListener("loadedmetadata", onMeta);
          if (el.duration && target < el.duration - 0.5) {
            try { el.currentTime = target; } catch (_) {}
          }
        };
        el.addEventListener("loadedmetadata", onMeta);
        if (vizState.powerOn) {
          el.play().catch(() => {});
        }
        return;
      }

      // 2) If anonymous CORS was blocked by an intermediate proxy on desktop, retry without crossOrigin
      if (el.crossOrigin && !mediaSourceAttached && !retriedNoCorsIds.has(currentTrackId)) {
        retriedNoCorsIds.add(currentTrackId);
        const currentSrc = el.src;
        el.removeAttribute("crossorigin");
        el.crossOrigin = null;
        streamLoadStartedAt = performance.now();
        el.src = currentSrc;
        if (vizState.powerOn) {
          el.play().catch(() => {});
        }
        return;
      }

      // 3) Automatic fallback to server-side ffmpeg lossless FLAC stream if codec (e.g. ALAC) isn't decoded natively
      if (currentStreamMode === "direct" && currentTranscodeBaseUrl) {
        failedDirectIds.add(currentTrackId);
        currentStreamMode = "transcode";
        const ss = Math.max(0, streamBaseOffsetSec);
        streamLoadStartedAt = performance.now();
        el.src = `${currentTranscodeBaseUrl}&ss=${ss.toFixed(2)}`;
        if (vizState.powerOn) {
          el.play().catch(() => {});
        }
      }
    });
  }

  function ensureAudioElement() {
    if (audioEl) return audioEl;
    audioEl = createAudioElement(false);
    return audioEl;
  }

  function cancelPreload() {
    preloadTrackId = -1;
    if (!preloadEl) return;
    try {
      preloadEl.pause();
      preloadEl.removeAttribute("src");
      preloadEl.load();
    } catch (_) {}
  }

  function preloadNext(opts) {
    if (!opts || !vizState.powerOn || mediaSourceAttached) return;
    const nextId = Number(opts.trackId);
    if (nextId < 0 || nextId === currentTrackId || nextId === preloadTrackId || !opts.streamUrl) return;

    cancelPreload();
    if (!preloadEl) {
      preloadEl = createAudioElement(true);
    }
    preloadTrackId = nextId;
    preloadEl.muted = true;
    preloadEl.volume = 0;
    preloadEl.src = opts.streamUrl;
    try { preloadEl.load(); } catch (_) {}
  }

  function ensureAudioContext(forStaticNoise = false) {
    if (skipWebAudioCapture && !forStaticNoise) return;
    try {
      if (!audioCtx) {
        const Ctx = window.AudioContext || window.webkitAudioContext;
        if (Ctx) audioCtx = new Ctx();
      }
      if (audioCtx && audioCtx.state === "suspended") {
        audioCtx.resume().catch(() => {});
      }
      if (!skipWebAudioCapture && audioCtx && audioCtx.state === "running" && audioEl && !mediaSourceAttached) {
        analyser = audioCtx.createAnalyser();
        analyser.fftSize = 256;
        analyser.smoothingTimeConstant = 0.78;
        freqBins = new Uint8Array(analyser.frequencyBinCount);

        const src = audioCtx.createMediaElementSource(audioEl);
        src.connect(analyser);
        analyser.connect(audioCtx.destination);
        mediaSourceAttached = true;
      }
    } catch (_) {
      // Fallback to BPM/Energy spectrum animation if Web Audio graph cannot attach
    }
  }

  function playStaticBurst(durationSec = 0.16) {
    if (!vizState.staticFx || !vizState.powerOn || durationSec <= 0) return;
    try {
      ensureAudioContext(true);
      if (!audioCtx) return;
      const sr = audioCtx.sampleRate;
      const len = Math.max(1, Math.floor(sr * durationSec));
      const buffer = audioCtx.createBuffer(1, len, sr);
      const data = buffer.getChannelData(0);
      for (let i = 0; i < len; i++) {
        const env = Math.sin((i / len) * Math.PI);
        data[i] = (Math.random() * 2 - 1) * env * 0.22 * vizState.volume;
      }
      const noise = audioCtx.createBufferSource();
      noise.buffer = buffer;
      const filter = audioCtx.createBiquadFilter();
      filter.type = "bandpass";
      filter.frequency.value = 1900;
      filter.Q.value = 0.9;
      noise.connect(filter);
      filter.connect(audioCtx.destination);
      noise.start();
    } catch (_) {}
  }

  function applyTheme(accentHex) {
    if (!accentHex) return;
    vizState.accentHex = accentHex;
    document.documentElement.style.setProperty("--accent", accentHex);
    document.documentElement.style.setProperty("--accent-glow", accentHex + "52");
  }

  function startTrackStream(opts, trackId, desiredMode, targetElapsed) {
    currentTrackId = trackId;
    currentStreamMode = desiredMode;
    currentDirectUrl = opts.directUrl || "";
    currentTranscodeBaseUrl = opts.transcodeBaseUrl || "";
    currentOpusBaseUrl = opts.opusBaseUrl || "";

    updateMediaSession(opts);

    // Promote preloaded standby element for zero-gap transitions on mobile
    if (!mediaSourceAttached &&
        preloadEl &&
        preloadTrackId === trackId &&
        preloadEl.readyState >= 2 &&
        !preloadEl.error &&
        targetElapsed < 4.5) {
      const oldEl = audioEl;
      audioEl = preloadEl;
      preloadEl = oldEl;
      preloadTrackId = -1;
      cancelPreload();

      streamBaseOffsetSec = 0;
      streamLoadStartedAt = performance.now();
      if (targetElapsed > 0.25) {
        try { audioEl.currentTime = targetElapsed; } catch (_) {}
      }
      audioEl.volume = vizState.volume;
      audioEl.muted = !vizState.powerOn;
      if (vizState.powerOn) {
        audioEl.play().catch(() => {});
      }
      return;
    }

    cancelPreload();
    const audio = ensureAudioElement();
    audio.volume = vizState.volume;
    audio.muted = !vizState.powerOn;
    streamLoadStartedAt = performance.now();

    if (desiredMode === "opus") {
      streamBaseOffsetSec = targetElapsed;
      audio.src = `${currentOpusBaseUrl}&ss=${targetElapsed.toFixed(2)}`;
    } else if (desiredMode === "transcode") {
      streamBaseOffsetSec = targetElapsed;
      audio.src = `${currentTranscodeBaseUrl}&ss=${targetElapsed.toFixed(2)}`;
    } else {
      streamBaseOffsetSec = 0;
      audio.src = currentDirectUrl;
      const onLoadedMeta = () => {
        audio.removeEventListener("loadedmetadata", onLoadedMeta);
        if (audio.duration && targetElapsed < audio.duration - 0.5) {
          try { audio.currentTime = targetElapsed; } catch (_) {}
        }
      };
      audio.addEventListener("loadedmetadata", onLoadedMeta);
    }

    if (vizState.powerOn) {
      audio.play().catch(() => {});
    }
  }

  function syncBroadcast(opts) {
    if (!opts) return;

    vizState.powerOn = !!opts.powerOn;
    vizState.volume = Math.max(0, Math.min(1, Number(opts.volume ?? 0.75)));
    vizState.staticFx = !!opts.staticFx;
    vizState.bpm = Number(opts.bpm || 115);
    vizState.energy = Number(opts.energy || 70);
    applyTheme(opts.accentHex);

    if (opts.staticBurstSec && opts.staticBurstSec > 0) {
      playStaticBurst(opts.staticBurstSec);
      const flash = document.getElementById("tuningFlash");
      if (flash) {
        flash.classList.add("flash");
        setTimeout(() => flash.classList.remove("flash"), 155);
      }
    }

    const audio = ensureAudioElement();
    ensureAudioContext(false);

    audio.volume = vizState.volume;
    audio.muted = !vizState.powerOn;

    const trackId = Number(opts.trackId);
    if (trackId < 0 || !opts.directUrl) {
      cancelPreload();
      audio.pause();
      return;
    }

    const targetElapsed = Math.max(0, Number(opts.targetElapsedSec || 0));
    const useOpus = (!!opts.preferOpus || platformPreferOpus) && !!opts.opusBaseUrl && !failedOpusIds.has(trackId);
    const useTranscode = !useOpus && (!!opts.isTranscoded || failedDirectIds.has(trackId));
    const desiredMode = useOpus ? "opus" : (useTranscode ? "transcode" : "direct");

    const trackChanged = trackId !== currentTrackId || desiredMode !== currentStreamMode;

    if (trackChanged) {
      if (tuneDebounceTimer) {
        clearTimeout(tuneDebounceTimer);
        tuneDebounceTimer = null;
      }

      const now = performance.now();
      const rapidSweep = (now - lastSwitchAt) < 240 && opts.staticBurstSec > 0;
      lastSwitchAt = now;

      if (rapidSweep) {
        // Listener is dragging the FM dial or rapidly stepping stations — avoid spawning intermediate streams
        audio.pause();
        const switchedAt = now;
        tuneDebounceTimer = setTimeout(() => {
          tuneDebounceTimer = null;
          const extraSec = Math.max(0, (performance.now() - switchedAt) / 1000);
          startTrackStream(opts, trackId, desiredMode, targetElapsed + extraSec);
        }, 125);
      } else {
        startTrackStream(opts, trackId, desiredMode, targetElapsed);
      }
      return;
    }

    // Same track already loaded — keep live sync tight without interrupting an in-flight buffer
    if (vizState.powerOn && !tuneDebounceTimer) {
      const loadingMs = performance.now() - streamLoadStartedAt;
      const isBuffering = audio.readyState < 3 && loadingMs < 8000;

      if (desiredMode === "opus" || desiredMode === "transcode") {
        const baseUrl = desiredMode === "opus" ? currentOpusBaseUrl : currentTranscodeBaseUrl;
        const effectivePos = streamBaseOffsetSec + (audio.currentTime || 0);
        if (audio.paused && loadingMs > 600) {
          if (Math.abs(effectivePos - targetElapsed) > 4.5) {
            streamBaseOffsetSec = targetElapsed;
            streamLoadStartedAt = performance.now();
            audio.src = `${baseUrl}&ss=${targetElapsed.toFixed(2)}`;
          }
          audio.play().catch(() => {});
        } else if (!isBuffering && audio.readyState >= 3 && Math.abs(effectivePos - targetElapsed) > 5.5) {
          streamBaseOffsetSec = targetElapsed;
          streamLoadStartedAt = performance.now();
          audio.src = `${baseUrl}&ss=${targetElapsed.toFixed(2)}`;
          audio.play().catch(() => {});
        }
      } else {
        if (!isBuffering && audio.readyState >= 2 && Math.abs((audio.currentTime || 0) - targetElapsed) > 2.5) {
          try { audio.currentTime = targetElapsed; } catch (_) {}
        }
        if (audio.paused && loadingMs > 400) {
          audio.play().catch(() => {});
        }
      }
    }
  }

  function setVolumeOnly(vol, powerOn, staticFx) {
    vizState.volume = Math.max(0, Math.min(1, Number(vol)));
    vizState.powerOn = !!powerOn;
    vizState.staticFx = !!staticFx;
    if (!vizState.powerOn) {
      cancelPreload();
    }
    if (audioEl) {
      audioEl.volume = vizState.volume;
      audioEl.muted = !vizState.powerOn;
    }
    if ("mediaSession" in navigator) {
      try { navigator.mediaSession.playbackState = vizState.powerOn ? "playing" : "paused"; } catch (_) {}
    }
  }

  function drawSpectrum(ts) {
    const canvas = document.getElementById("spectrumCanvas");
    if (canvas) {
      const ctx = canvas.getContext("2d");
      const w = canvas.width;
      const h = canvas.height;
      ctx.clearRect(0, 0, w, h);

      let hasLiveFft = false;
      if (vizState.powerOn && vizState.volume > 0 && analyser && freqBins) {
        analyser.getByteFrequencyData(freqBins);
        for (let k = 0; k < 24; k++) {
          if (freqBins[k] > 3) {
            hasLiveFft = true;
            break;
          }
        }
      }

      const beatFreq = (vizState.bpm / 60) * Math.PI * 2;
      const beatPulse = 0.65 + 0.35 * Math.abs(Math.sin((ts / 1000) * beatFreq * 0.5));
      const energyScale = (vizState.energy / 100) * vizState.volume;

      const gap = 4;
      const barW = (w - gap * (BARS - 1)) / BARS;

      for (let i = 0; i < BARS; i++) {
        let target = 0.05;
        if (vizState.powerOn && vizState.volume > 0) {
          if (hasLiveFft) {
            const binIdx = Math.min(freqBins.length - 1, Math.floor((i / BARS) * (freqBins.length * 0.72)));
            target = Math.max(0.05, (freqBins[binIdx] / 255) * Math.min(1.15, 0.55 + vizState.volume * 0.6));
          } else if (audioEl && !audioEl.paused) {
            const centerWeight = 1 - Math.abs(i - BARS * 0.32) / BARS;
            const wave = Math.sin(ts * 0.006 + i * 0.45) * 0.5 + 0.5;
            target = Math.min(1, (0.18 + wave * 0.65 * centerWeight) * beatPulse * energyScale + Math.random() * 0.12 * energyScale);
          }
        }

        barVals[i] += (target - barVals[i]) * 0.28;
        const bh = Math.max(3, Math.min(h, barVals[i] * h));
        const x = i * (barW + gap);
        const y = h - bh;

        const grad = ctx.createLinearGradient(0, h, 0, 0);
        grad.addColorStop(0, vizState.accentHex + "55");
        grad.addColorStop(1, vizState.accentHex);
        ctx.fillStyle = grad;
        ctx.beginPath();
        if (ctx.roundRect) {
          ctx.roundRect(x, y, barW, bh, 2);
        } else {
          ctx.rect(x, y, barW, bh);
        }
        ctx.fill();
      }
    }

    rafId = requestAnimationFrame(drawSpectrum);
  }

  function init(ref, config) {
    dotNetRef = ref;
    if (config) {
      platformPreferOpus = !!config.preferOpus;
      skipWebAudioCapture = !!config.skipWebAudioCapture;
    }
    ensureAudioElement();

    if ("mediaSession" in navigator && !window.__radioMediaSessionBound) {
      window.__radioMediaSessionBound = true;
      try {
        navigator.mediaSession.setActionHandler("play", () => {
          if (dotNetRef) dotNetRef.invokeMethodAsync("OnMediaPowerAsync", true).catch(() => {});
        });
        navigator.mediaSession.setActionHandler("pause", () => {
          if (dotNetRef) dotNetRef.invokeMethodAsync("OnMediaPowerAsync", false).catch(() => {});
        });
        navigator.mediaSession.setActionHandler("previoustrack", () => {
          if (dotNetRef) dotNetRef.invokeMethodAsync("OnStepFrequencyAsync", -1).catch(() => {});
        });
        navigator.mediaSession.setActionHandler("nexttrack", () => {
          if (dotNetRef) dotNetRef.invokeMethodAsync("OnStepFrequencyAsync", 1).catch(() => {});
        });
      } catch (_) {}
    }

    if (!rafId) {
      rafId = requestAnimationFrame(drawSpectrum);
    }

    // Unlock Web Audio graph on first interaction if platform started it suspended
    const unlock = () => {
      ensureAudioContext(false);
      if (audioEl && vizState.powerOn && audioEl.paused && audioEl.src) {
        audioEl.play().catch(() => {});
      }
    };
    window.addEventListener("pointerdown", unlock, { passive: true });
    window.addEventListener("keydown", unlock, { passive: true });

    // Bind FM tuner drag & wheel
    const scale = document.getElementById("tunerScale");
    if (scale && !scale.dataset.bound) {
      scale.dataset.bound = "1";
      let dragging = false;
      let lastMhzSent = -1;

      const sendFreqAt = (clientX) => {
        const inner = scale.querySelector(".tuner-ticks") || scale;
        const rect = inner.getBoundingClientRect();
        if (rect.width <= 0) return;
        const ratio = Math.min(1, Math.max(0, (clientX - rect.left) / rect.width));
        const mhz = FREQ_MIN + ratio * (FREQ_MAX - FREQ_MIN);
        if (Math.abs(mhz - lastMhzSent) > 0.12 && dotNetRef) {
          lastMhzSent = mhz;
          dotNetRef.invokeMethodAsync("OnTunerDragFrequencyAsync", mhz).catch(() => {});
        }
      };

      scale.addEventListener("pointerdown", (e) => {
        if (e.target.classList.contains("station-pip")) return;
        dragging = true;
        try { scale.setPointerCapture(e.pointerId); } catch (_) {}
        sendFreqAt(e.clientX);
      });

      scale.addEventListener("pointermove", (e) => {
        if (dragging) sendFreqAt(e.clientX);
      });

      scale.addEventListener("pointerup", () => { dragging = false; });
      scale.addEventListener("pointercancel", () => { dragging = false; });

      scale.addEventListener("wheel", (e) => {
        e.preventDefault();
        if (dotNetRef) {
          dotNetRef.invokeMethodAsync("OnStepFrequencyAsync", e.deltaY > 0 ? 1 : -1).catch(() => {});
        }
      }, { passive: false });
    }

    // Bind global hotkeys (ignoring text inputs)
    if (!window.__radioKeysBound) {
      window.__radioKeysBound = true;
      window.addEventListener("keydown", (e) => {
        const tag = (e.target && e.target.tagName) ? e.target.tagName.toUpperCase() : "";
        if (tag === "INPUT" || tag === "TEXTAREA") {
          if (e.code === "Escape") e.target.blur();
          return;
        }
        if (!dotNetRef) return;

        if (e.code === "KeyQ") {
          e.preventDefault();
          dotNetRef.invokeMethodAsync("OnHotkeyAsync", "wheel").catch(() => {});
        } else if (e.code === "Escape") {
          dotNetRef.invokeMethodAsync("OnHotkeyAsync", "escape").catch(() => {});
        } else if (e.code === "ArrowRight") {
          e.preventDefault();
          dotNetRef.invokeMethodAsync("OnStepFrequencyAsync", 1).catch(() => {});
        } else if (e.code === "ArrowLeft") {
          e.preventDefault();
          dotNetRef.invokeMethodAsync("OnStepFrequencyAsync", -1).catch(() => {});
        } else if (e.code === "Space") {
          e.preventDefault();
          dotNetRef.invokeMethodAsync("OnHotkeyAsync", "power").catch(() => {});
        }
      });
    }
  }

  return {
    init,
    syncBroadcast,
    preloadNext,
    setVolumeOnly,
    playStaticBurst
  };
})();
