window.radioAudio = (() => {
  const FREQ_MIN = 87.5;
  const FREQ_MAX = 108.0;
  const BARS = 48;

  let dotNetRef = null;
  let audioEl = null;
  let audioCtx = null;
  let analyser = null;
  let mediaSourceAttached = false;
  let freqBins = null;
  let rafId = null;

  let currentTrackId = -1;
  let currentTranscodeMode = false;
  let currentTranscodeBaseUrl = "";
  let streamBaseOffsetSec = 0;

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

  function ensureAudioElement() {
    if (audioEl) return audioEl;
    audioEl = new Audio();
    audioEl.crossOrigin = "anonymous";
    audioEl.preload = "auto";

    audioEl.addEventListener("ended", () => {
      if (dotNetRef) {
        dotNetRef.invokeMethodAsync("OnAudioTrackEndedAsync").catch(() => {});
      }
    });

    audioEl.addEventListener("error", () => {
      // Automatic fallback to server-side ffmpeg lossless FLAC stream if codec (e.g. ALAC) isn't decoded natively
      if (currentTrackId >= 0 && !currentTranscodeMode && currentTranscodeBaseUrl) {
        failedDirectIds.add(currentTrackId);
        currentTranscodeMode = true;
        const ss = Math.max(0, streamBaseOffsetSec);
        audioEl.src = `${currentTranscodeBaseUrl}&ss=${ss.toFixed(2)}`;
        if (vizState.powerOn) {
          audioEl.play().catch(() => {});
        }
      }
    });

    return audioEl;
  }

  function ensureAudioContext() {
    try {
      if (!audioCtx) {
        const Ctx = window.AudioContext || window.webkitAudioContext;
        if (Ctx) audioCtx = new Ctx();
      }
      if (audioCtx && audioCtx.state === "suspended") {
        audioCtx.resume().catch(() => {});
      }
      if (audioCtx && audioCtx.state === "running" && audioEl && !mediaSourceAttached) {
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
      ensureAudioContext();
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
    ensureAudioContext();

    audio.volume = vizState.volume;
    audio.muted = !vizState.powerOn;

    const trackId = Number(opts.trackId);
    if (trackId < 0 || !opts.directUrl) {
      audio.pause();
      return;
    }

    const targetElapsed = Math.max(0, Number(opts.targetElapsedSec || 0));
    const useTranscode = !!opts.isTranscoded || failedDirectIds.has(trackId);
    currentTranscodeBaseUrl = opts.transcodeBaseUrl || "";

    const trackChanged = trackId !== currentTrackId || useTranscode !== currentTranscodeMode;

    if (trackChanged) {
      currentTrackId = trackId;
      currentTranscodeMode = useTranscode;

      if (useTranscode) {
        streamBaseOffsetSec = targetElapsed;
        audio.src = `${currentTranscodeBaseUrl}&ss=${targetElapsed.toFixed(2)}`;
      } else {
        streamBaseOffsetSec = 0;
        audio.src = opts.directUrl;
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
      return;
    }

    // Same track already loaded — keep live sync tight (especially when unmuting)
    if (vizState.powerOn) {
      if (useTranscode) {
        const effectivePos = streamBaseOffsetSec + (audio.currentTime || 0);
        if (Math.abs(effectivePos - targetElapsed) > 3.2 || audio.paused) {
          streamBaseOffsetSec = targetElapsed;
          audio.src = `${currentTranscodeBaseUrl}&ss=${targetElapsed.toFixed(2)}`;
          audio.play().catch(() => {});
        }
      } else {
        if (audio.readyState >= 1 && Math.abs((audio.currentTime || 0) - targetElapsed) > 2.2) {
          try { audio.currentTime = targetElapsed; } catch (_) {}
        }
        if (audio.paused) {
          audio.play().catch(() => {});
        }
      }
    }
  }

  function setVolumeOnly(vol, powerOn, staticFx) {
    vizState.volume = Math.max(0, Math.min(1, Number(vol)));
    vizState.powerOn = !!powerOn;
    vizState.staticFx = !!staticFx;
    if (audioEl) {
      audioEl.volume = vizState.volume;
      audioEl.muted = !vizState.powerOn;
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

  function init(ref) {
    dotNetRef = ref;
    ensureAudioElement();

    if (!rafId) {
      rafId = requestAnimationFrame(drawSpectrum);
    }

    // Unlock Web Audio graph on first interaction if platform started it suspended
    const unlock = () => {
      ensureAudioContext();
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
        const rect = scale.getBoundingClientRect();
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
    setVolumeOnly,
    playStaticBurst
  };
})();
