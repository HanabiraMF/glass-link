/* GlassLink shared browser client: WebSocket control + WebCodecs H.264 receiver. */
'use strict';

(function () {
  const VideoMagic = [0x47, 0x4c, 0x56, 0x31]; // GLV1
  const HeaderSize = 24;

  class GlassLinkClient extends EventTarget {
    constructor(options) {
      super();
      this.role = options.role || 'desktop';
      this.clientName = options.clientName || (this.role === 'mobile' ? '手机浏览器' : '电脑浏览器');
      this.videoCanvas = options.videoCanvas || null;
      this.token = options.token || new URLSearchParams(location.search).get('token') || '';
      this._socket = null;
      this._manualClose = false;
      this._reconnectTimer = null;
      this._reconnectAttempts = 0;
      this._decoder = null;
      this._decoderConfigured = false;
      this._configuring = false;
      this._videoConfig = null;
      this._lastKeyframe = null;
      this._pendingFrame = null;
      this._drawScheduled = false;
      this._ctx = this.videoCanvas ? this.videoCanvas.getContext('2d', { alpha: false, desynchronized: true }) : null;
      this._pingTimer = null;
      this._lastPingAt = 0;
      this._pendingPackets = [];
      this._keyframeRequestAt = 0;
      this._lastDecodeErrorAt = 0;
      this.status = null;
      this.video = null;
      this.controller = null;
      this.stats = null;
      this.latencyMs = null;
      this._audioContext = null;
      this._audioNextTime = 0;
      this._audioEnabled = false;
    }

    enableAudio() {
      this._audioEnabled = true;
      try {
        if (!this._audioContext) {
          const AudioCtx = window.AudioContext || window.webkitAudioContext;
          if (!AudioCtx) return;
          this._audioContext = new AudioCtx();
        }
        if (this._audioContext.state === 'suspended') this._audioContext.resume().catch(() => {});
      } catch (e) { }
    }

    _handleAudio(buffer) {
      if (!this._audioEnabled || !this._audioContext) return;
      const ctx = this._audioContext;
      if (ctx.state !== 'running') return;
      const view = new DataView(buffer);
      const sampleRate = view.getUint32(4, true);
      const channels = view.getUint16(8, true);
      const bits = view.getUint16(10, true);
      if (bits !== 16 || channels < 1 || channels > 2 || sampleRate < 8000) return;
      const payload = new Int16Array(buffer, 16);
      const frames = Math.floor(payload.length / channels);
      if (!frames) return;
      const audioBuffer = ctx.createBuffer(channels, frames, sampleRate);
      for (let ch = 0; ch < channels; ch++) {
        const out = audioBuffer.getChannelData(ch);
        for (let i = 0; i < frames; i++) out[i] = payload[i * channels + ch] / 32768;
      }
      const source = ctx.createBufferSource();
      source.buffer = audioBuffer;
      source.connect(ctx.destination);
      const now = ctx.currentTime;
      if (this._audioNextTime < now + 0.02 || this._audioNextTime > now + 0.5) this._audioNextTime = now + 0.05;
      source.start(this._audioNextTime);
      this._audioNextTime += audioBuffer.duration;
    }

    get isOpen() {
      return !!this._socket && this._socket.readyState === WebSocket.OPEN;
    }

    get videoInfo() {
      return this.video;
    }

    connect() {
      this._manualClose = false;
      clearTimeout(this._reconnectTimer);
      this._disconnectSocketOnly();

      const secure = location.protocol === 'https:';
      const wsProtocol = secure ? 'wss:' : 'ws:';
      const token = this.token ? `&token=${encodeURIComponent(this.token)}` : '';
      const url = `${wsProtocol}//${location.host}/ws?client=${encodeURIComponent(this.role)}${token}`;
      this._emit('connectionState', { state: 'connecting' });

      let socket;
      try {
        socket = new WebSocket(url);
      } catch (error) {
        this._emit('error', { message: '无法创建 WebSocket：' + error.message });
        this._scheduleReconnect();
        return;
      }

      this._socket = socket;
      socket.binaryType = 'arraybuffer';
      socket.onopen = () => {
        this._reconnectAttempts = 0;
        this._emit('connectionState', { state: 'connected' });
        this._sendRaw({ type: 'hello', role: this.role, clientName: this.clientName });
        this._startHeartbeat();
        this._flushPendingPackets();
        this.requestKeyframe(true);
      };
      socket.onmessage = event => {
        if (typeof event.data === 'string') {
          this._handleText(event.data);
        } else {
          this._handleBinary(event.data);
        }
      };
      socket.onerror = () => {
        this._emit('connectionState', { state: 'error' });
      };
      socket.onclose = () => {
        this._stopHeartbeat();
        this._emit('connectionState', { state: 'disconnected' });
        if (!this._manualClose) this._scheduleReconnect();
      };
    }

    _disconnectSocketOnly() {
      if (this._socket) {
        this._socket.onopen = null;
        this._socket.onmessage = null;
        this._socket.onerror = null;
        this._socket.onclose = null;
        try { this._socket.close(); } catch (e) { }
        this._socket = null;
      }
    }

    _scheduleReconnect() {
      if (this._manualClose || this._reconnectTimer) return;
      const delay = Math.min(5000, 700 + this._reconnectAttempts * 700);
      this._reconnectAttempts++;
      this._emit('connectionState', { state: 'reconnecting', delay });
      this._reconnectTimer = setTimeout(() => {
        this._reconnectTimer = null;
        this.connect();
      }, delay);
    }

    _startHeartbeat() {
      this._stopHeartbeat();
      this._pingTimer = setInterval(() => {
        if (!this.isOpen) return;
        this._lastPingAt = performance.now();
        this._sendRaw({ type: 'ping', t: Date.now() });
      }, 10000);
    }

    _stopHeartbeat() {
      if (this._pingTimer) clearInterval(this._pingTimer);
      this._pingTimer = null;
    }

    close() {
      this._manualClose = true;
      this._stopHeartbeat();
      clearTimeout(this._reconnectTimer);
      this._reconnectTimer = null;
      this._disposeDecoder();
      this._disconnectSocketOnly();
    }

    _sendRaw(message) {
      if (!this.isOpen) return false;
      try {
        this._socket.send(JSON.stringify(message));
        return true;
      } catch (error) {
        this._emit('error', { message: '发送失败：' + error.message });
        return false;
      }
    }

    startStream() {
      return this._sendRaw({ type: 'stream.start' });
    }

    stopStream() {
      return this._sendRaw({ type: 'stream.stop' });
    }

    setController(layer) {
      return this._sendRaw({ type: 'controller.set', layer });
    }

    setVideoSettings(fps, bitrate) {
      return this._sendRaw({ type: 'video.settings', fps, bitrate });
    }

    requestKeyframe(force) {
      const now = performance.now();
      if (!force && now - this._keyframeRequestAt < 800) return false;
      this._keyframeRequestAt = now;
      return this._sendRaw({ type: 'keyframe' });
    }

    sendInput(event) {
      return this._sendRaw({ type: 'input', event });
    }

    requestStatus() {
      return this._sendRaw({ type: 'status' });
    }

    _handleText(text) {
      let message;
      try { message = JSON.parse(text); }
      catch (error) { this._emit('error', { message: '服务器返回了无效 JSON。' }); return; }

      switch (message.type) {
        case 'hello':
        case 'status':
          if (message.status) {
            const previous = this.status;
            this.status = message.status;
            this.controller = message.status.controller || this.controller;
            this.stats = message.status.stats || this.stats;
            this._emit('status', { status: this.status, previous });
          }
          if (message.clientId) this.clientId = message.clientId;
          if (message.serverVersion) this.serverVersion = message.serverVersion;
          if (message.role) this.role = message.role;
          break;
        case 'streamState': {
          if (this.status) {
            this.status.streaming = ['starting', 'streaming', 'recovering'].includes(message.state);
            this.status.streamState = message.state;
            this.status.streamError = message.error || null;
          }
          this._emit('streamState', message);
          break;
        }
        case 'controllerStatus':
          this.controller = message.controller;
          if (this.status) this.status.controller = message.controller;
          this._emit('controller', { controller: this.controller });
          break;
        case 'videoConfig':
          this._applyVideoConfig(message.video);
          break;
        case 'videoStats':
          this.stats = message.stats;
          this._emit('videoStats', { stats: this.stats });
          break;
        case 'cursor':
          this.cursor = message.cursor || { visible: false, x: 0, y: 0 };
          this._emit('cursor', { cursor: this.cursor });
          break;
        case 'pong':
          if (this._lastPingAt) {
            this.latencyMs = Math.max(0, Math.round(performance.now() - this._lastPingAt));
            this._lastPingAt = 0;
            this._emit('latency', { latencyMs: this.latencyMs });
          }
          break;
        case 'file':
          this._emit('file', { name: message.name, url: message.url });
          break;
        case 'error':
          this._emit('serverError', { message: message.message });
          break;
        default:
          break;
      }
    }

    async _applyVideoConfig(video) {
      if (!video) return;
      const same = this._videoConfig &&
        this._videoConfig.codec === video.codec &&
        this._videoConfig.width === video.width &&
        this._videoConfig.height === video.height;
      this.video = video;
      if (same && this._decoderConfigured) {
        this._emit('videoConfig', { video });
        return;
      }

      this._disposeDecoder();
      this._videoConfig = video;
      this._emit('videoConfig', { video });

      if (!('VideoDecoder' in window)) {
        this._emit('decodeFatal', {
          message: '此浏览器不支持 WebCodecs VideoDecoder。请使用最新的 Chrome/Edge，并通过 HTTPS 访问。'
        });
        return;
      }
      if (!window.isSecureContext) {
        this._emit('decodeFatal', {
          message: '当前页面不是安全上下文，WebCodecs 被浏览器禁用。请使用 HTTPS 地址访问（先安装 GlassLink 根证书）。'
        });
        return;
      }

      this._configuring = true;
      try {
        const base = {
          codedWidth: Math.max(2, video.width),
          codedHeight: Math.max(2, video.height),
          optimizeForLatency: true,
          avc: { format: 'annexb' }
        };
        // Low-end devices often reject prefer-hardware with "requested output
        // format is not supported". Let the browser choose, then try software,
        // then common H.264 codec strings used by baseline decoders.
        const candidates = [
          { ...base, codec: video.codec },
          { ...base, codec: video.codec, hardwareAcceleration: 'prefer-software' },
          { ...base, codec: 'avc1.42E01E', hardwareAcceleration: 'prefer-software' },
          { ...base, codec: 'avc1.4D401E', hardwareAcceleration: 'prefer-software' },
          { ...base, codec: 'avc1.42E01E' }
        ];
        let config = null;
        if (typeof VideoDecoder.isConfigSupported === 'function') {
          for (const candidate of candidates) {
            try {
              const support = await VideoDecoder.isConfigSupported(candidate);
              if (support.supported) { config = candidate; break; }
            } catch (e) { }
          }
        } else {
          config = candidates[0];
        }
        if (!config) {
          throw new Error(`此设备不支持 H.264 解码（${video.codec} @ ${video.width}x${video.height}）。请降低分辨率/使用支持 WebCodecs 的浏览器。`);
        }
        if (this._videoConfig !== video) return;

        this._decoder = new VideoDecoder({
          output: frame => this._onDecodedFrame(frame),
          error: error => this._onDecoderError(error)
        });
        this._decoder.configure(config);
        this._decoderConfigured = true;

        if (this.videoCanvas) {
          this.videoCanvas.width = video.width;
          this.videoCanvas.height = video.height;
        }
        this._emit('decoderReady', { video });
        this._flushPendingPackets();
        this.requestKeyframe(true);
      } catch (error) {
        this._decoderConfigured = false;
        this._emit('decodeFatal', { message: error && error.message ? error.message : String(error) });
      } finally {
        this._configuring = false;
      }
    }

    _handleBinary(buffer) {
      if (!(buffer instanceof ArrayBuffer) || buffer.byteLength < HeaderSize) return;
      const bytes = new Uint8Array(buffer);
      if (bytes[0] === 0x47 && bytes[1] === 0x4c && bytes[2] === 0x41 && bytes[3] === 0x31) {
        this._handleAudio(buffer);
        return;
      }
      if (bytes[0] !== VideoMagic[0] || bytes[1] !== VideoMagic[1] || bytes[2] !== VideoMagic[2] || bytes[3] !== VideoMagic[3]) return;

      const view = new DataView(buffer);
      const flags = view.getUint8(4);
      const codec = view.getUint8(5);
      if (codec !== 1) return;
      const timestamp = Number(view.getBigInt64(8, true));
      const duration = view.getUint32(16, true);
      const length = view.getUint32(20, true);
      if (length <= 0 || HeaderSize + length > buffer.byteLength) return;
      const data = new Uint8Array(buffer, HeaderSize, length);
      const isKeyframe = (flags & 1) !== 0;
      const packet = { data, isKeyframe, timestamp, duration };

      if (isKeyframe) this._lastKeyframe = packet;
      if (!this._decoderConfigured || this._configuring) {
        if (isKeyframe) this._pendingPackets = [packet];
        else if (this._pendingPackets.length < 8) this._pendingPackets.push(packet);
        if (!this._configuring && this._videoConfig) this.requestKeyframe();
        return;
      }

      this._decodePacket(packet);
    }

    _flushPendingPackets() {
      if (!this._decoderConfigured || !this._decoder) return;
      const packets = this._pendingPackets;
      this._pendingPackets = [];
      for (const packet of packets) this._decodePacket(packet);
    }

    _decodePacket(packet) {
      if (!this._decoder || !this._decoderConfigured || this._decoder.state !== 'configured') return;
      if (this._decoder.decodeQueueSize > 10 && !packet.isKeyframe) {
        this.requestKeyframe();
        return;
      }
      try {
        const chunk = new EncodedVideoChunk({
          type: packet.isKeyframe ? 'key' : 'delta',
          timestamp: packet.timestamp,
          duration: packet.duration,
          data: packet.data
        });
        this._decoder.decode(chunk);
      } catch (error) {
        this._onDecoderError(error);
      }
    }

    _onDecodedFrame(frame) {
      if (!this.videoCanvas || !this._ctx) {
        frame.close();
        return;
      }
      if (this._pendingFrame) this._pendingFrame.close();
      this._pendingFrame = frame;
      if (this._drawScheduled) return;
      this._drawScheduled = true;
      requestAnimationFrame(() => {
        this._drawScheduled = false;
        const next = this._pendingFrame;
        this._pendingFrame = null;
        if (!next) return;
        try {
          if (this.videoCanvas.width !== next.displayWidth || this.videoCanvas.height !== next.displayHeight) {
            this.videoCanvas.width = next.displayWidth;
            this.videoCanvas.height = next.displayHeight;
          }
          this._ctx.drawImage(next, 0, 0, this.videoCanvas.width, this.videoCanvas.height);
          this._emit('frameRendered', { timestamp: next.timestamp });
        } finally {
          next.close();
        }
      });
    }

    _onDecoderError(error) {
      const now = performance.now();
      if (now - this._lastDecodeErrorAt > 1500) {
        this._lastDecodeErrorAt = now;
        this._emit('decodeError', { message: error && error.message ? error.message : String(error) });
      }
      this._disposeDecoder();
      if (this._videoConfig) {
        const config = this._videoConfig;
        this._videoConfig = null;
        this._applyVideoConfig(config);
      }
      this.requestKeyframe(true);
    }

    _disposeDecoder() {
      this._decoderConfigured = false;
      if (this._pendingFrame) {
        try { this._pendingFrame.close(); } catch (e) { }
        this._pendingFrame = null;
      }
      if (this._decoder) {
        try { this._decoder.close(); } catch (e) { }
        this._decoder = null;
      }
      this._pendingPackets = [];
    }

    _emit(type, detail) {
      this.dispatchEvent(new CustomEvent(type, { detail }));
    }
  }

  function normalizeKeyEvent(event) {
    return {
      type: 'keyboard',
      action: event.type === 'keyup' ? 'up' : 'down',
      code: event.code,
      key: event.key,
      virtualKey: event.keyCode || undefined
    };
  }

  function fullscreenElement() {
    return document.fullscreenElement || document.webkitFullscreenElement || null;
  }

  async function requestFullscreen(element) {
    const target = element || document.documentElement;
    const method = target.requestFullscreen || target.webkitRequestFullscreen;
    if (!method) return false;
    try {
      await method.call(target, { navigationUI: 'hide' });
      return true;
    } catch (error) {
      return false;
    }
  }

  async function exitFullscreen() {
    if (!fullscreenElement()) return;
    const method = document.exitFullscreen || document.webkitExitFullscreen;
    if (!method) return;
    try { await method.call(document); } catch (e) { }
  }

  function toggleFullscreen(element) {
    if (fullscreenElement()) return exitFullscreen();
    return requestFullscreen(element);
  }

  function clamp(value, min, max) {
    return Math.min(max, Math.max(min, value));
  }

  /**
   * Positions the host-cursor overlay on top of the letterboxed video area.
   * The browser's own pointer is hidden on the computer page while controlling.
   */
  class RemoteCursor {
    constructor(element, canvas, container) {
      this.element = element;
      this.canvas = canvas;
      this.container = container;
      this.cursor = { visible: false, x: 0, y: 0 };
      this.enabled = true;
      window.addEventListener('resize', () => this.render());
      document.addEventListener('fullscreenchange', () => setTimeout(() => this.render(), 30));
    }

    setEnabled(enabled) {
      this.enabled = !!enabled;
      this.render();
    }

    set(cursor) {
      this.cursor = cursor || { visible: false, x: 0, y: 0 };
      this.render();
    }

    render() {
      if (!this.element) return;
      if (!this.enabled || !this.cursor || !this.cursor.visible || this.canvas.width < 2) {
        this.element.classList.add('hidden');
        return;
      }
      const rect = videoContentRect(this.canvas, this.container);
      if (!rect.width || !rect.height) {
        this.element.classList.add('hidden');
        return;
      }
      this.element.classList.remove('hidden');
      this.element.style.transform =
        `translate3d(${rect.left + this.cursor.x * rect.width}px, ${rect.top + this.cursor.y * rect.height}px, 0)`;
    }
  }

  function storageGet(key, fallback) {
    try {
      const raw = localStorage.getItem(key);
      return raw ? JSON.parse(raw) : fallback;
    } catch (e) {
      return fallback;
    }
  }

  function storageSet(key, value) {
    try { localStorage.setItem(key, JSON.stringify(value)); } catch (e) { }
  }

  function formatBitrate(mbps) {
    return `${Number(mbps || 0).toFixed(1)} Mbps`;
  }

  function tokenFromUrl() {
    return new URLSearchParams(location.search).get('token') || '';
  }

  /**
   * Rect of the actual letterboxed video inside a container whose canvas uses
   * object-fit: contain. Used to map pointer/touch coordinates to the host screen.
   */
  function videoContentRect(canvas, container) {
    const outer = (container || canvas).getBoundingClientRect();
    const vw = canvas.width || 16;
    const vh = canvas.height || 9;
    const scale = Math.min(outer.width / vw, outer.height / vh);
    const width = vw * scale;
    const height = vh * scale;
    return {
      left: outer.left + (outer.width - width) / 2,
      top: outer.top + (outer.height - height) / 2,
      width,
      height,
      right: outer.left + (outer.width + width) / 2,
      bottom: outer.top + (outer.height + height) / 2
    };
  }

  function installNoContextMenu() {
    document.addEventListener('contextmenu', event => {
      if (event.target.closest('.allow-context-menu')) return;
      event.preventDefault();
    });
  }

  window.GlassLink = {
    GlassLinkClient,
    RemoteCursor,
    normalizeKeyEvent,
    requestFullscreen,
    exitFullscreen,
    toggleFullscreen,
    fullscreenElement,
    clamp,
    storageGet,
    storageSet,
    formatBitrate,
    tokenFromUrl,
    videoContentRect,
    installNoContextMenu
  };
})();
