'use strict';

(function () {
  const {
    GlassLinkClient, RemoteCursor, clamp, tokenFromUrl, videoContentRect,
    requestFullscreen, exitFullscreen, fullscreenElement, installNoContextMenu
  } = window.GlassLink;

  installNoContextMenu();

  const elements = {
    stage: document.getElementById('stage'),
    canvas: document.getElementById('videoCanvas'),
    idle: document.getElementById('idleOverlay'),
    bigStart: document.getElementById('bigStartBtn'),
    streamBtn: document.getElementById('streamBtn'),
    quickStartBtn: document.getElementById('quickStartBtn'),
    fullscreenScreenBtn: document.getElementById('fullscreenScreenBtn'),
    captureToggle: document.getElementById('captureToggle'),
    qualitySelect: document.getElementById('qualitySelect'),
    fpsSelect: document.getElementById('fpsSelect'),
    videoSettingsState: document.getElementById('videoSettingsState'),
    driverNotice: document.getElementById('driverNotice'),
    openDriverInstallerBtn: document.getElementById('openDriverInstallerBtn'),
    recheckHardwareBtn: document.getElementById('recheckHardwareBtn'),
    controllerSelect: document.getElementById('controllerSelect'),
    connectionChip: document.getElementById('connectionChip'),
    streamChip: document.getElementById('streamChip'),
    layerChip: document.getElementById('layerChip'),
    panelState: document.getElementById('panelState'),
    layerHealth: document.getElementById('layerHealth'),
    diagnostics: document.getElementById('controllerDiagnostics'),
    fallback: document.getElementById('fallbackBanner'),
    clientsBadge: document.getElementById('clientsBadge'),
    kvStreamState: document.getElementById('kvStreamState'),
    kvEncoder: document.getElementById('kvEncoder'),
    kvCodec: document.getElementById('kvCodec'),
    kvLatency: document.getElementById('kvLatency'),
    kvClients: document.getElementById('kvClients'),
    kvSecure: document.getElementById('kvSecure'),
    hudResolution: document.getElementById('hudResolution'),
    hudFps: document.getElementById('hudFps'),
    hudBitrate: document.getElementById('hudBitrate'),
    hudLatency: document.getElementById('hudLatency'),
    videoError: document.getElementById('videoError'),
    videoErrorText: document.getElementById('videoErrorText'),
    retryVideo: document.getElementById('retryVideoBtn'),
    pointerHint: document.getElementById('pointerHint'),
    toast: document.getElementById('toast'),
    fullscreenBtn: document.getElementById('fullscreenBtn'),
    remoteCursor: document.getElementById('remoteCursor'),
    fullscreenControls: document.getElementById('fullscreenControls'),
    exitFullscreenBtn: document.getElementById('exitFullscreenBtn'),
    fullscreenKeyHint: document.getElementById('fullscreenKeyHint')
  };

  const remoteCursor = new RemoteCursor(elements.remoteCursor, elements.canvas, elements.stage);

  const client = new GlassLinkClient({
    role: 'desktop',
    clientName: '电脑浏览器',
    videoCanvas: elements.canvas,
    token: tokenFromUrl()
  });
  document.addEventListener('pointerdown', () => client.enableAudio(), { passive: true });

  let controlActive = false;
  let pointerInside = false;
  let lastPointer = null;
  let toastTimer = null;
  const heldKeys = new Set();
  const heldMouseButtons = new Set();
  let lastStatus = null;
  let lastVideo = null;
  let controlBeforeFullscreen = false;
  let keyboardLocked = false;
  let keyboardLockPending = false;
  let keyboardLockToastShown = false;

  client.addEventListener('connectionState', event => {
    const state = event.detail.state;
    if (state === 'connected') {
      setChip(elements.connectionChip, 'live', '已连接');
    } else if (state === 'connecting' || state === 'reconnecting') {
      setChip(elements.connectionChip, 'warn', state === 'connecting' ? '连接中' : '重连中');
      releaseAll('连接波动');
    } else if (state === 'error') {
      setChip(elements.connectionChip, 'bad', '连接错误');
    } else {
      setChip(elements.connectionChip, '', '已断开');
      remoteCursor.set({ visible: false });
      releaseAll('连接断开');
    }
  });

  client.addEventListener('cursor', event => remoteCursor.set(event.detail.cursor));

  client.addEventListener('status', event => {
    lastStatus = event.detail.status;
    applyStatus(lastStatus);
  });

  client.addEventListener('streamState', event => {
    const state = event.detail.state;
    if (state === 'error' && event.detail.error) {
      showToast('串流错误：' + event.detail.error, true);
      setControlActive(false);
    }
    if (state === 'streaming') {
      elements.idle.classList.add('hidden');
      if (controlActive) showHint(true);
    }
    updateStreamVisuals(state);
  });

  client.addEventListener('controller', event => applyController(event.detail.controller));
  client.addEventListener('videoConfig', event => {
    lastVideo = event.detail.video;
    applyVideo(event.detail.video);
    elements.videoError.classList.add('hidden');
  });
  client.addEventListener('videoStats', event => applyStats(event.detail.stats));
  client.addEventListener('latency', event => {
    const value = event.detail.latencyMs;
    elements.hudLatency.textContent = String(value);
    elements.kvLatency.textContent = `${value} ms`;
  });
  client.addEventListener('decodeFatal', event => {
    elements.videoError.classList.remove('hidden');
    elements.videoErrorText.textContent = event.detail.message;
    showToast(event.detail.message, true);
  });
  client.addEventListener('decodeError', event => {
    showToast('解码器恢复中：' + event.detail.message, false);
  });
  client.addEventListener('decoderReady', () => {
    elements.videoError.classList.add('hidden');
  });
  client.addEventListener('serverError', event => showToast(event.detail.message, true));

  applyStoredCapturePreference();
  client.connect();
  client.requestStatus();

  elements.streamBtn.addEventListener('click', toggleStream);
  elements.bigStart.addEventListener('click', toggleStream);
  elements.quickStartBtn.addEventListener('click', toggleStream);
  elements.qualitySelect.addEventListener('change', applyVideoSettingsFromUi);
  elements.fpsSelect.addEventListener('change', applyVideoSettingsFromUi);
  elements.openDriverInstallerBtn.addEventListener('click', openDriverInstaller);
  elements.recheckHardwareBtn.addEventListener('click', () => {
    elements.layerHealth.textContent = '检测中';
    elements.layerHealth.className = 'mini-badge';
    client.setController('hardware');
    showToast('正在重新检测输入驱动…');
  });
  elements.captureToggle.addEventListener('change', () => {
    setControlActive(elements.captureToggle.checked);
  });
  elements.controllerSelect.addEventListener('change', () => {
    const layer = elements.controllerSelect.value;
    elements.layerHealth.textContent = '切换中';
    elements.layerHealth.className = 'mini-badge warn';
    client.setController(layer);
    showToast('正在切换输入控制器层，视频流不会中断。');
  });
  elements.retryVideo.addEventListener('click', () => {
    elements.videoError.classList.add('hidden');
    client.requestKeyframe(true);
  });
  elements.fullscreenBtn.addEventListener('click', toggleScreenFullscreen);
  elements.fullscreenScreenBtn.addEventListener('click', toggleScreenFullscreen);
  elements.exitFullscreenBtn.addEventListener('click', () => exitFullscreen());
  document.addEventListener('fullscreenchange', onFullscreenChange);
  document.addEventListener('webkitfullscreenchange', onFullscreenChange);

  document.addEventListener('keydown', onKeyDown, true);
  document.addEventListener('keyup', onKeyUp, true);
  window.addEventListener('blur', () => releaseAll('窗口失焦'));
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) releaseAll('页面隐藏');
  });
  window.addEventListener('beforeunload', () => releaseAll('页面关闭'));

  elements.stage.addEventListener('pointerenter', () => { pointerInside = true; });
  elements.stage.addEventListener('pointerleave', () => {
    pointerInside = false;
    lastPointer = null;
  });
  elements.stage.addEventListener('pointermove', onPointerMove);
  elements.stage.addEventListener('pointerdown', onPointerDown);
  elements.stage.addEventListener('pointerup', onPointerUp);
  elements.stage.addEventListener('pointercancel', onPointerUp);
  elements.stage.addEventListener('wheel', onWheel, { passive: false });
  window.addEventListener('pointerup', onGlobalPointerUp);

  window.addEventListener('resize', () => {
    if (lastVideo) applyVideo(lastVideo);
  });

  function applyStoredCapturePreference() {
    try {
      const stored = localStorage.getItem('glasslink.capture');
      elements.captureToggle.checked = stored !== 'false';
    } catch (e) { }
    elements.captureToggle.addEventListener('change', () => {
      try { localStorage.setItem('glasslink.capture', elements.captureToggle.checked ? 'true' : 'false'); } catch (e) { }
    });
  }

  async function toggleStream() {
    if (lastStatus && lastStatus.streaming) {
      setControlActive(false);
      client.stopStream();
      showToast('已请求停止串流。');
      return;
    }

    setControlActive(true);
    elements.streamBtn.disabled = true;
    elements.bigStart.disabled = true;
    elements.streamBtn.textContent = '正在启动…';
    elements.bigStart.textContent = '正在启动…';
    client.startStream();
    showToast('正在初始化 GPU 捕获与硬件编码器…');
    setTimeout(() => {
      elements.streamBtn.disabled = false;
      elements.bigStart.disabled = false;
    }, 1800);
  }

  async function toggleScreenFullscreen() {
    if (fullscreenElement()) {
      await exitFullscreen();
      return;
    }

    controlBeforeFullscreen = controlActive;
    const ok = await requestFullscreen(elements.stage);
    if (!ok) {
      showToast('浏览器未允许全屏显示。请再次点击，或在浏览器菜单中启用全屏。', true);
      return;
    }
    if (fullscreenElement() === elements.stage) {
      await engageKeyboardLock();
    }
  }

  async function onFullscreenChange() {
    const stageFullscreen = fullscreenElement() === elements.stage;
    elements.fullscreenControls.classList.toggle('hidden', !stageFullscreen);

    if (stageFullscreen) {
      setControlActive(true);
      elements.stage.focus({ preventScroll: true });
      await engageKeyboardLock();
      showToast('已进入电脑画面全屏。Windows/Alt/Tab 等可截获系统按键会被转发；点击右上角按钮退出。');
    } else {
      releaseKeyboardLock();
      elements.fullscreenControls.classList.add('hidden');
      releaseAll('退出全屏');
      setControlActive(controlBeforeFullscreen);
      showHint(false);
      keyboardLockToastShown = false;
    }
  }

  async function engageKeyboardLock() {
    if (keyboardLocked || keyboardLockPending) return;
    if (fullscreenElement() !== elements.stage) return;
    keyboardLockPending = true;
    if (!('keyboard' in navigator) || typeof navigator.keyboard.lock !== 'function') {
      elements.fullscreenKeyHint.textContent = '当前浏览器不支持键盘锁定';
      keyboardLockPending = false;
      if (!keyboardLockToastShown) {
        keyboardLockToastShown = true;
        showToast('当前浏览器不支持 Keyboard Lock，Win 键等系统按键可能仍由本机处理。建议使用 Chrome/Edge 全屏。');
      }
      return;
    }

    try {
      await navigator.keyboard.lock();
      if (fullscreenElement() !== elements.stage) {
        try { navigator.keyboard.unlock(); } catch (e) { }
        return;
      }
      keyboardLocked = true;
      elements.fullscreenKeyHint.textContent = '系统按键已接管';
    } catch (firstError) {
      try {
        await navigator.keyboard.lock(['MetaLeft', 'MetaRight', 'AltLeft', 'AltRight', 'Tab', 'Escape']);
        if (fullscreenElement() !== elements.stage) {
          try { navigator.keyboard.unlock(); } catch (e) { }
          return;
        }
        keyboardLocked = true;
        elements.fullscreenKeyHint.textContent = '系统按键已接管';
      } catch (secondError) {
        elements.fullscreenKeyHint.textContent = '键盘锁定未生效';
        if (!keyboardLockToastShown) {
          keyboardLockToastShown = true;
          showToast('浏览器拒绝了 Keyboard Lock；Win/Alt 等系统按键可能无法转发。', true);
        }
      }
    } finally {
      keyboardLockPending = false;
    }
  }

  function releaseKeyboardLock() {
    keyboardLockPending = false;
    if (!keyboardLocked) return;
    keyboardLocked = false;
    try {
      if (navigator.keyboard && typeof navigator.keyboard.unlock === 'function') navigator.keyboard.unlock();
    } catch (e) { }
    elements.fullscreenKeyHint.textContent = '系统按键接管中';
  }

  function setControlActive(active) {
    if (active && !(lastStatus && lastStatus.streaming)) {
      // Allow enabling while the encoder is starting; input is harmless until then.
    }
    controlActive = active;
    elements.captureToggle.checked = active;
    document.body.classList.toggle('control-active', active);
    if (!active) {
      releaseAll('停止控制');
      showHint(false);
    } else {
      showHint(!!(lastStatus && lastStatus.streaming));
      elements.stage.focus();
    }
  }

  function applyVideoSettingsFromUi() {
    if (!client.isOpen) {
      showToast('尚未连接服务器。', true);
      return;
    }
    const fps = Number(elements.fpsSelect.value) || 60;
    const bitrate = Number(elements.qualitySelect.value) || 16000000;
    elements.videoSettingsState.textContent = '应用中';
    elements.videoSettingsState.className = 'mini-badge warn';
    if (!client.setVideoSettings(fps, bitrate)) {
      showToast('画质设置发送失败。', true);
      return;
    }
    setTimeout(() => {
      elements.videoSettingsState.textContent = '实时生效';
      elements.videoSettingsState.className = 'mini-badge ok';
    }, 700);
    showToast(`画质已切换：${elements.qualitySelect.selectedOptions[0]?.textContent || bitrate} / ${fps} FPS`);
  }

  async function openDriverInstaller() {
    const token = client.token ? `?token=${encodeURIComponent(client.token)}` : '';
    try {
      const response = await fetch(`/api/drivers/open-installer${token}`, { method: 'POST' });
      if (!response.ok) throw new Error(await response.text() || `HTTP ${response.status}`);
      showToast('已打开驱动安装目录。请以管理员身份运行安装脚本，并重启电脑。');
    } catch (error) {
      showToast('安装目录未随程序部署，请重新运行安装程序并选择驱动安装。', true);
    }
  }

  function applyStatus(status) {
    if (!status) return;
    lastStatus = status;
    const streaming = !!status.streaming;
    const state = String(status.streamState || 'idle');
    updateStreamVisuals(state);
    elements.idle.classList.toggle('hidden', streaming || state === 'starting' || state === 'recovering');

    if (status.video) {
      lastVideo = status.video;
      applyVideo(status.video);
    }
    applyStats(status.stats);
    applyController(status.controller);
    elements.kvClients.textContent = String(status.clients || 0);
    elements.kvSecure.textContent = status.secureContextAvailable ? 'HTTPS 可用' : '仅 HTTP';
    elements.kvSecure.style.color = status.secureContextAvailable ? 'var(--green)' : 'var(--amber)';
    elements.clientsBadge.textContent = `${status.clients || 0} 个客户端`;
    elements.kvEncoder.textContent = status.video ? status.video.encoderName : '--';
    elements.kvCodec.textContent = status.video ? `${status.video.codec} · ${status.video.format}` : 'H.264 Annex-B';

    if (streaming && !lastStatus.streamError) {
      elements.pointerHint.classList.toggle('hidden', !controlActive);
    } else {
      elements.pointerHint.classList.add('hidden');
    }

    if (state === 'error' && status.streamError) {
      elements.panelState.textContent = '错误';
      elements.panelState.className = 'mini-badge bad';
      elements.streamBtn.textContent = '重新开始串流';
      elements.bigStart.textContent = '重新开始串流';
      elements.videoError.classList.remove('hidden');
      elements.videoErrorText.textContent = status.streamError;
    }
  }

  function updateStreamVisuals(state) {
    const streaming = ['starting', 'streaming', 'recovering'].includes(state);
    elements.streamBtn.textContent = streaming ? '停止串流' : (state === 'error' ? '重新开始串流' : '开始串流');
    elements.bigStart.textContent = streaming ? '停止串流' : (state === 'error' ? '重新开始串流' : '开始串流');
    elements.quickStartBtn.textContent = streaming ? '停止串流' : '一键开始';
    elements.streamBtn.disabled = false;
    elements.bigStart.disabled = false;
    elements.panelState.textContent = stateLabel(state);
    elements.panelState.className = 'mini-badge ' + stateClass(state);
    elements.kvStreamState.textContent = stateLabel(state);

    if (state === 'streaming') {
      setChip(elements.streamChip, 'live', '串流中');
    } else if (state === 'starting') {
      setChip(elements.streamChip, 'warn', '启动中');
    } else if (state === 'recovering') {
      setChip(elements.streamChip, 'warn', '恢复中');
    } else if (state === 'error') {
      setChip(elements.streamChip, 'bad', '错误');
    } else if (state === 'stopping') {
      setChip(elements.streamChip, 'warn', '停止中');
    } else {
      setChip(elements.streamChip, '', '未串流');
      if (!streaming) elements.pointerHint.classList.add('hidden');
    }
  }

  function stateLabel(state) {
    const labels = {
      idle: '待机', starting: '启动中', streaming: '串流中', recovering: '恢复中',
      stopping: '停止中', error: '错误'
    };
    return labels[state] || state;
  }

  function stateClass(state) {
    if (state === 'streaming') return 'ok';
    if (state === 'error') return 'bad';
    if (state === 'starting' || state === 'recovering' || state === 'stopping') return 'warn';
    return '';
  }

  function applyController(controller) {
    if (!controller) return;
    elements.controllerSelect.value = controller.requested || 'auto';
    const active = controller.active;
    elements.layerChip.textContent = `输入：${active === 'hardware' ? '驱动注入' : '系统注入'}`;
    elements.layerChip.className = 'chip ' + (controller.hardwareAvailable ? 'live' : 'warn');
    elements.kvLayer = null;
    elements.layerHealth.textContent = controller.hardwareAvailable ? '硬件层可用' : '软件层运行中';
    elements.layerHealth.className = 'mini-badge ' + (controller.hardwareAvailable ? 'ok' : 'warn');

    const lines = [];
    if (controller.fallbackReason) {
      elements.fallback.classList.remove('hidden');
      elements.fallback.textContent = '已回退到软件层：' + controller.fallbackReason;
    } else {
      elements.fallback.classList.add('hidden');
    }
    const needsDrivers = controller.active !== 'hardware' && controller.requested !== 'software' && !controller.hardwareAvailable;
    elements.driverNotice.classList.toggle('hidden', !needsDrivers);
    for (const line of (controller.diagnostics || [])) {
      const div = document.createElement('div');
      div.className = 'diag-line' + (line.includes('Interception') || line.includes('vmulti') ? ' highlight' : '');
      div.textContent = line;
      lines.push(div);
    }
    elements.diagnostics.replaceChildren(...lines);
  }

  function applyVideo(video) {
    if (!video) return;
    elements.hudResolution.textContent = `${video.width}×${video.height}`;
    elements.kvEncoder.textContent = `${video.encoderName}${video.hardwareAccelerated ? ' · GPU' : ' · 软件'}`;
    elements.kvCodec.textContent = `${video.codec} · Annex-B · ${video.fps}fps`;
    if (elements.canvas.width !== video.width) {
      elements.canvas.width = video.width;
      elements.canvas.height = video.height;
    }
    if (document.activeElement !== elements.fpsSelect)
      elements.fpsSelect.value = String(video.fps);
    if (document.activeElement !== elements.qualitySelect) {
      const exact = Array.from(elements.qualitySelect.options).find(option => Number(option.value) === video.bitrate);
      if (exact) {
        elements.qualitySelect.value = exact.value;
      } else {
        const closest = Array.from(elements.qualitySelect.options)
          .sort((a, b) => Math.abs(Number(a.value) - video.bitrate) - Math.abs(Number(b.value) - video.bitrate))[0];
        if (closest) elements.qualitySelect.value = closest.value;
      }
    }
  }

  function applyStats(stats) {
    if (!stats) return;
    elements.hudFps.textContent = Number(stats.fps || 0).toFixed(0);
    elements.hudBitrate.textContent = Number(stats.megabitsPerSecond || 0).toFixed(1);
  }

  function setChip(element, cssClass, text) {
    element.className = 'chip ' + (cssClass || 'chip-neutral');
    const dot = document.createElement('span');
    dot.className = 'dot';
    const label = document.createElement('span');
    label.className = 'chip-text';
    label.textContent = text;
    element.replaceChildren(dot, label);
  }

  function showHint(show) {
    elements.pointerHint.classList.toggle('hidden', !show);
    elements.pointerHint.textContent = '键鼠输入已接管 · 移动鼠标即控制被控机 · 本机光标已隐藏';
  }

  function showToast(message, error) {
    elements.toast.textContent = message;
    elements.toast.classList.toggle('error', !!error);
    elements.toast.classList.add('show');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => elements.toast.classList.remove('show'), 4200);
  }

  function isEditableTarget(target) {
    if (!(target instanceof Element)) return false;
    if (target.closest('[data-no-input]')) {
      const active = document.activeElement;
      return active && (active.matches('input, select, textarea, button') || active.isContentEditable);
    }
    return !!target.closest('input, textarea, select, [contenteditable="true"]');
  }

  function onKeyDown(event) {
    if (!controlActive || !client.isOpen) return;
    if (isEditableTarget(event.target)) return;
    if (event.repeat) {
      event.preventDefault();
      return;
    }
    if (heldKeys.has(event.code)) return;
    heldKeys.add(event.code);
    event.preventDefault();
    event.stopPropagation();
    client.sendInput({
      type: 'keyboard',
      action: 'down',
      code: event.code,
      key: event.key,
      virtualKey: event.keyCode || undefined
    });
  }

  function onKeyUp(event) {
    if (!controlActive || !client.isOpen) return;
    if (isEditableTarget(event.target)) return;
    const wasHeld = heldKeys.delete(event.code);
    if (!wasHeld) return;
    event.preventDefault();
    event.stopPropagation();
    client.sendInput({
      type: 'keyboard',
      action: 'up',
      code: event.code,
      key: event.key,
      virtualKey: event.keyCode || undefined
    });
  }

  function videoRect() {
    return videoContentRect(elements.canvas, elements.stage);
  }

  function pointerToNormalized(event) {
    const rect = videoRect();
    if (!rect.width || !rect.height) return null;
    const x = (event.clientX - rect.left) / rect.width;
    const y = (event.clientY - rect.top) / rect.height;
    if (x < 0 || x > 1 || y < 0 || y > 1) return null;
    return { x: clamp(x, 0, 1), y: clamp(y, 0, 1) };
  }

  function onPointerMove(event) {
    if (!controlActive || !client.isOpen) return;
    const point = pointerToNormalized(event);
    if (!point) return;
    if (document.activeElement !== elements.stage) elements.stage.focus({ preventScroll: true });
    const previous = lastPointer;
    lastPointer = point;
    if (previous && Math.abs(previous.x - point.x) < 0.00005 && Math.abs(previous.y - point.y) < 0.00005) return;
    client.sendInput({ type: 'mouseMove', mode: 'absolute', x: point.x, y: point.y });
  }

  function onPointerDown(event) {
    if (!controlActive || !client.isOpen) return;
    if (event.button < 0 || event.button > 2) return;
    const point = pointerToNormalized(event);
    if (!point) return;
    event.preventDefault();
    const button = ['left', 'middle', 'right'][event.button];
    heldMouseButtons.add(button);
    client.sendInput({ type: 'mouseButton', button, action: 'down' });
    elements.stage.setPointerCapture?.(event.pointerId);
  }

  function onPointerUp(event) {
    if (event.button < 0 || event.button > 2) return;
    const button = ['left', 'middle', 'right'][event.button];
    if (!heldMouseButtons.has(button)) return;
    heldMouseButtons.delete(button);
    client.sendInput({ type: 'mouseButton', button, action: 'up' });
  }

  function onGlobalPointerUp(event) {
    if (event.button < 0 || event.button > 2) return;
    const button = ['left', 'middle', 'right'][event.button];
    if (!heldMouseButtons.has(button)) return;
    heldMouseButtons.delete(button);
    client.sendInput({ type: 'mouseButton', button, action: 'up' });
  }

  function onWheel(event) {
    if (!controlActive || !client.isOpen) return;
    if (!pointerInside) return;
    event.preventDefault();
    client.sendInput({
      type: 'mouseWheel',
      wheelDelta: -Math.round(event.deltaY),
      wheelDeltaX: -Math.round(event.deltaX)
    });
  }

  function releaseAll(reason) {
    if (heldKeys.size > 0 && client.isOpen) {
      for (const code of heldKeys) {
        client.sendInput({ type: 'keyboard', action: 'up', code });
      }
    }
    if (heldMouseButtons.size > 0 && client.isOpen) {
      for (const button of heldMouseButtons) {
        client.sendInput({ type: 'mouseButton', button, action: 'up' });
      }
    }
    heldKeys.clear();
    heldMouseButtons.clear();
    if (reason) showHint(false);
  }
})();
