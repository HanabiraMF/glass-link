'use strict';

(function () {
  const {
    GlassLinkClient, RemoteCursor, clamp, tokenFromUrl, videoContentRect,
    requestFullscreen, exitFullscreen, fullscreenElement,
    storageGet, storageSet, installNoContextMenu
  } = window.GlassLink;

  installNoContextMenu();

  const CONTROL_STORAGE_KEY = 'glasslink.game.controls.v3';
  const FULLSCREEN_STORAGE_KEY = 'glasslink.mobile.fullscreen';
  const MAX_TOUCHES = 20;

  const KEY_OPTIONS = [
    ['mouse:left', '鼠标左键'],
    ['mouse:right', '鼠标右键'],
    ['mouse:middle', '鼠标中键'],
    ['Space', '空格 / 跳跃'],
    ['ShiftLeft', '左 Shift'],
    ['ControlLeft', '左 Ctrl'],
    ['KeyW', 'W'],
    ['KeyA', 'A'],
    ['KeyS', 'S'],
    ['KeyD', 'D'],
    ['KeyE', 'E'],
    ['KeyQ', 'Q'],
    ['KeyR', 'R'],
    ['KeyF', 'F'],
    ['KeyG', 'G'],
    ['KeyC', 'C'],
    ['KeyV', 'V'],
    ['KeyX', 'X'],
    ['KeyZ', 'Z'],
    ['Digit1', '数字 1'],
    ['Digit2', '数字 2'],
    ['Digit3', '数字 3'],
    ['Digit4', '数字 4'],
    ['Digit5', '数字 5'],
    ['ArrowUp', '方向键 ↑'],
    ['ArrowDown', '方向键 ↓'],
    ['ArrowLeft', '方向键 ←'],
    ['ArrowRight', '方向键 →'],
    ['Enter', '回车'],
    ['Escape', 'Esc'],
    ['Tab', 'Tab']
  ];

  const DEFAULT_CONTROLS = [
    { id: 'l3', type: 'joystick', label: 'L3', x: 0.15, y: 0.72, size: 118, shape: 'circle', opacity: 0.5, color: '#9fb4cc', preset: 'wasd', sensitivity: 1.2, key: 'KeyW', mouseButton: '' },
    { id: 'r3', type: 'look', label: 'R3', x: 0.84, y: 0.62, size: 210, shape: 'circle', opacity: 0.16, color: '#9fb4cc', preset: 'wasd', sensitivity: 1.2, key: '', mouseButton: '' },
    { id: 'l1', type: 'button', label: 'L1', x: 0.22, y: 0.14, size: 52, shape: 'rounded', opacity: 0.62, color: '#9fb4cc', preset: 'wasd', sensitivity: 1.2, key: 'KeyQ', mouseButton: '' },
    { id: 'l2', type: 'button', label: 'L2', x: 0.09, y: 0.22, size: 52, shape: 'rounded', opacity: 0.62, color: '#9fb4cc', preset: 'wasd', sensitivity: 1.2, key: 'KeyE', mouseButton: '' },
    { id: 'r1', type: 'button', label: 'R1', x: 0.78, y: 0.14, size: 52, shape: 'rounded', opacity: 0.62, color: '#9fb4cc', preset: 'wasd', sensitivity: 1.2, key: 'KeyR', mouseButton: '' },
    { id: 'r2', type: 'button', label: 'R2', x: 0.91, y: 0.22, size: 52, shape: 'rounded', opacity: 0.62, color: '#9fb4cc', preset: 'wasd', sensitivity: 1.2, key: 'KeyF', mouseButton: '' },
    { id: 'a', type: 'button', label: 'A', x: 0.91, y: 0.78, size: 52, shape: 'circle', opacity: 0.72, color: '#7ec8a8', preset: 'wasd', sensitivity: 1.2, key: 'Space', mouseButton: '' },
    { id: 'b', type: 'button', label: 'B', x: 0.82, y: 0.84, size: 52, shape: 'circle', opacity: 0.72, color: '#d49a9a', preset: 'wasd', sensitivity: 1.2, key: 'KeyB', mouseButton: '' },
    { id: 'x', type: 'button', label: 'X', x: 0.73, y: 0.78, size: 52, shape: 'circle', opacity: 0.72, color: '#8ea8d8', preset: 'wasd', sensitivity: 1.2, key: 'KeyX', mouseButton: '' },
    { id: 'y', type: 'button', label: 'Y', x: 0.82, y: 0.72, size: 52, shape: 'circle', opacity: 0.72, color: '#d8c48e', preset: 'wasd', sensitivity: 1.2, key: 'KeyY', mouseButton: '' },
    { id: 'select', type: 'button', label: 'SELECT', x: 0.42, y: 0.10, size: 46, shape: 'rounded', opacity: 0.55, color: '#9fb4cc', preset: 'wasd', sensitivity: 1.2, key: 'Tab', mouseButton: '' },
    { id: 'start', type: 'button', label: 'START', x: 0.55, y: 0.10, size: 46, shape: 'rounded', opacity: 0.55, color: '#9fb4cc', preset: 'wasd', sensitivity: 1.2, key: 'Escape', mouseButton: '' }
  ];

  const el = {
    app: document.getElementById('app'),
    canvas: document.getElementById('videoCanvas'),
    normalLayer: document.getElementById('normalLayer'),
    touchSurface: document.getElementById('normalTouchSurface'),
    waiting: document.getElementById('waitingOverlay'),
    waitingText: document.getElementById('waitingText'),
    requestStart: document.getElementById('requestStartBtn'),
    remoteCursor: document.getElementById('remoteCursor'),
    topHud: document.getElementById('topHud'),
    modeLabel: document.getElementById('modeLabel'),
    connChip: document.getElementById('mobileConnChip'),
    streamChip: document.getElementById('mobileStreamChip'),
    fpsChip: document.getElementById('mobileFpsChip'),
    configBtn: document.getElementById('configBtn'),
    keyboardBtn: document.getElementById('keyboardBtn'),
    settingsBtn: document.getElementById('settingsBtn'),
    normalKeyboardBtn: document.getElementById('normalKeyboardBtn'),
    exitGameBtn: document.getElementById('exitGameBtn'),
    fsIconBtn: document.getElementById('fsIconBtn'),
    ring: document.getElementById('longPressRing'),
    settingsSheet: document.getElementById('settingsSheet'),
    closeSettingsBtn: document.getElementById('closeSettingsBtn'),
    mobileQualitySelect: document.getElementById('mobileQualitySelect'),
    mobileFpsSelect: document.getElementById('mobileFpsSelect'),
    mobileLayerInfo: document.getElementById('mobileLayerInfo'),
    mobileFullscreenInfo: document.getElementById('mobileFullscreenInfo'),
    mobileEncoderInfo: document.getElementById('mobileEncoderInfo'),
    mobileDriverHint: document.getElementById('mobileDriverHint'),
    menuGameBtn: document.getElementById('menuGameBtn'),
    mobileDriverBtn: document.getElementById('mobileDriverBtn'),
    keyboardSheet: document.getElementById('keyboardSheet'),
    closeKeyboardBtn: document.getElementById('closeKeyboardBtn'),
    virtualKeyboardInput: document.getElementById('virtualKeyboardInput'),
    sendKeyboardTextBtn: document.getElementById('sendKeyboardTextBtn'),
    fullscreenChoice: document.getElementById('fullscreenChoice'),
    fullscreenChoiceText: document.getElementById('fullscreenChoiceText'),
    enterGameBtn: document.getElementById('enterGameBtn'),
    gameLayer: document.getElementById('gameLayer'),
    controlCanvas: document.getElementById('controlCanvas'),
    gameHint: document.getElementById('gameHint'),
    editorSheet: document.getElementById('editorSheet'),
    closeEditorBtn: document.getElementById('closeEditorBtn'),
    addButton: document.getElementById('addButton'),
    addJoystick: document.getElementById('addJoystick'),
    addLook: document.getElementById('addLook'),
    resetConfigBtn: document.getElementById('resetConfigBtn'),
    editorEmpty: document.getElementById('editorEmpty'),
    editorForm: document.getElementById('editorForm'),
    editLabel: document.getElementById('editLabel'),
    editShape: document.getElementById('editShape'),
    editKey: document.getElementById('editKey'),
    editSize: document.getElementById('editSize'),
    editOpacity: document.getElementById('editOpacity'),
    editSensitivity: document.getElementById('editSensitivity'),
    editPreset: document.getElementById('editPreset'),
    editColor: document.getElementById('editColor'),
    sizeValue: document.getElementById('sizeValue'),
    opacityValue: document.getElementById('opacityValue'),
    sensitivityValue: document.getElementById('sensitivityValue'),
    duplicateControlBtn: document.getElementById('duplicateControlBtn'),
    deleteControlBtn: document.getElementById('deleteControlBtn'),
    transition: document.getElementById('transitionOverlay'),
    fatal: document.getElementById('fatalOverlay'),
    fatalText: document.getElementById('fatalText'),
    fatalRetry: document.getElementById('fatalRetryBtn'),
    toast: document.getElementById('toast')
  };

  const client = new GlassLinkClient({
    role: 'mobile',
    clientName: detectClientName(),
    videoCanvas: el.canvas,
    token: tokenFromUrl()
  });

  let controls = loadControls();
  let selectedId = null;
  let gameMode = false;
  let editorMode = false;
  let transitioning = false;
  let connectedOnce = false;
  let fullscreenPreferred = storageGet(FULLSCREEN_STORAGE_KEY, false);
  let toastTimer = null;
  let gameHintTimer = null;
  let wakeLock = null;

  const remoteCursor = new RemoteCursor(el.remoteCursor, el.canvas, el.app);
  const gameCtx = el.controlCanvas.getContext('2d');
  const activePointers = new Map();
  const gamepadState = { buttons: new Set(), leftX: 0, leftY: 0, rightX: 0, rightY: 0, leftTrigger: 0, rightTrigger: 0 };
  let rightStickReset = 0;

  function gamepadButtonName(label) {
    const n = String(label || '').toUpperCase();
    return ['A','B','X','Y','L1','L2','R1','R2','L3','R3','START','SELECT'].includes(n) ? n : null;
  }

  function sendGamepad() {
    if (!client.isOpen) return;
    client.sendInput({ type: 'gamepad', gamepad: {
      buttons: [...gamepadState.buttons],
      leftX: gamepadState.leftX, leftY: gamepadState.leftY,
      rightX: gamepadState.rightX, rightY: gamepadState.rightY,
      leftTrigger: gamepadState.leftTrigger, rightTrigger: gamepadState.rightTrigger
    }, source: 'mobile-gamepad' });
  }
  const heldKeyCounts = new Map();
  const heldMouseCounts = new Map();
  let dragState = null;
  let drawQueued = false;

  const normalTouches = new Map();
  let normalTouchSeq = 0;

  init();

  function init() {
    document.addEventListener('pointerdown', () => client.enableAudio(), { passive: true });
    buildKeyOptions();
    updateFullscreenUi();
    bindEvents();
    resizeControlCanvas();
    window.addEventListener('resize', () => {
      resizeControlCanvas();
      requestDraw();
    });
    window.addEventListener('orientationchange', () => setTimeout(() => { resizeControlCanvas(); requestDraw(); }, 220));
    document.addEventListener('fullscreenchange', updateFullscreenUi);
    document.addEventListener('webkitfullscreenchange', updateFullscreenUi);
    document.addEventListener('visibilitychange', () => {
      if (document.hidden) {
        releaseAllGameInputs();
        cancelAllNormalTouches();
      } else if (gameMode) {
        requestWakeLock();
      }
    });
    window.addEventListener('blur', () => {
      releaseAllGameInputs();
      cancelAllNormalTouches();
    });
    window.addEventListener('beforeunload', () => {
      releaseAllGameInputs();
      cancelAllNormalTouches();
    });

    client.addEventListener('connectionState', onConnectionState);
    client.addEventListener('status', event => applyStatus(event.detail.status));
    client.addEventListener('streamState', event => applyStreamState(event.detail));
    client.addEventListener('videoConfig', event => applyMobileVideo(event.detail.video));
    client.addEventListener('videoStats', event => {
      el.fpsChip.classList.remove('hidden');
      el.fpsChip.textContent = `${Math.round(event.detail.stats.fps || 0)} FPS`;
    });
    client.addEventListener('cursor', event => remoteCursor.set(event.detail.cursor));
    client.addEventListener('decodeFatal', event => showFatal(event.detail.message));
    client.addEventListener('decoderReady', () => el.fatal.classList.add('hidden'));
    client.addEventListener('decodeError', event => showToast('解码恢复中：' + event.detail.message));
    client.addEventListener('serverError', event => showToast(event.detail.message, true));
    client.addEventListener('file', event => {
      const banner = document.getElementById('fileBanner');
      if (!banner) return;
      banner.href = event.detail.url;
      banner.download = event.detail.name;
      banner.textContent = '下载：' + event.detail.name;
      banner.classList.remove('hidden');
      clearTimeout(banner._hideTimer);
      banner._hideTimer = setTimeout(() => banner.classList.add('hidden'), 30000);
    });

    client.connect();
  }

  function detectClientName() {
    const ua = navigator.userAgent || '';
    if (/iPhone/i.test(ua)) return 'iPhone 浏览器';
    if (/iPad/i.test(ua)) return 'iPad 浏览器';
    if (/Android/i.test(ua)) return 'Android 手机';
    return '手机浏览器';
  }

  function bindEvents() {
    el.requestStart.addEventListener('click', () => {
      el.requestStart.disabled = true;
      el.requestStart.textContent = '正在请求…';
      client.startStream();
      setTimeout(() => {
        el.requestStart.disabled = false;
        el.requestStart.textContent = '请求电脑开始串流';
      }, 1800);
    });

    el.fullscreenChoice.addEventListener('click', toggleFullscreenPreference);
    el.fsIconBtn.addEventListener('click', toggleFullscreenPreference);
    el.enterGameBtn.addEventListener('click', enterGameMode);
    el.exitGameBtn.addEventListener('click', exitGameMode);
    el.configBtn.addEventListener('click', toggleEditor);

    el.settingsBtn.addEventListener('click', openSettings);
    el.closeSettingsBtn.addEventListener('click', closeSettings);
    el.menuGameBtn.addEventListener('click', () => { closeSettings(); enterGameMode(); });
    el.mobileQualitySelect.addEventListener('change', applyMobileVideoSettings);
    el.mobileFpsSelect.addEventListener('change', applyMobileVideoSettings);
    el.mobileDriverBtn.addEventListener('click', openDriverInstallerOnPc);

    el.keyboardBtn.addEventListener('click', openVirtualKeyboard);
    el.normalKeyboardBtn.addEventListener('click', openVirtualKeyboard);
    el.closeKeyboardBtn.addEventListener('click', closeVirtualKeyboard);
    el.sendKeyboardTextBtn.addEventListener('click', sendVirtualKeyboardText);
    document.querySelectorAll('#keyboardSheet [data-special]').forEach(button => {
      button.addEventListener('click', () => sendSpecialKey(button.dataset.special));
    });
    el.virtualKeyboardInput.addEventListener('keydown', event => {
      if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
        event.preventDefault();
        sendVirtualKeyboardText();
      }
    });

    el.touchSurface.addEventListener('pointerdown', onNormalPointerDown);
    el.touchSurface.addEventListener('pointermove', onNormalPointerMove);
    el.touchSurface.addEventListener('pointerup', onNormalPointerUp);
    el.touchSurface.addEventListener('pointercancel', onNormalPointerUp);

    el.controlCanvas.addEventListener('pointerdown', onGamePointerDown);
    el.controlCanvas.addEventListener('pointermove', onGamePointerMove);
    el.controlCanvas.addEventListener('pointerup', onGamePointerUp);
    el.controlCanvas.addEventListener('pointercancel', onGamePointerUp);
    el.controlCanvas.addEventListener('contextmenu', event => event.preventDefault());

    el.closeEditorBtn.addEventListener('click', closeEditor);
    el.addButton.addEventListener('click', () => addControl('button'));
    el.addJoystick.addEventListener('click', () => addControl('joystick'));
    el.addLook.addEventListener('click', () => addControl('look'));
    el.resetConfigBtn.addEventListener('click', resetControls);
    el.deleteControlBtn.addEventListener('click', deleteSelectedControl);
    el.duplicateControlBtn.addEventListener('click', duplicateSelectedControl);

    el.editLabel.addEventListener('input', () => updateSelected({ label: el.editLabel.value.trim().slice(0, 12) || '控件' }));
    el.editShape.addEventListener('change', () => updateSelected({ shape: el.editShape.value }));
    el.editKey.addEventListener('change', () => {
      const value = el.editKey.value;
      if (value.startsWith('mouse:')) updateSelected({ mouseButton: value.slice(6), key: '' });
      else updateSelected({ key: value, mouseButton: '' });
    });
    el.editSize.addEventListener('input', () => {
      el.sizeValue.textContent = el.editSize.value;
      updateSelected({ size: Number(el.editSize.value) });
    });
    el.editOpacity.addEventListener('input', () => {
      el.opacityValue.textContent = el.editOpacity.value + '%';
      updateSelected({ opacity: Number(el.editOpacity.value) / 100 });
    });
    el.editSensitivity.addEventListener('input', () => {
      el.sensitivityValue.textContent = Number(el.editSensitivity.value).toFixed(1);
      updateSelected({ sensitivity: Number(el.editSensitivity.value) });
    });
    el.editPreset.addEventListener('change', () => updateSelected({ preset: el.editPreset.value }));
    el.editColor.addEventListener('input', () => updateSelected({ color: el.editColor.value }));
    el.fatalRetry.addEventListener('click', () => {
      el.fatal.classList.add('hidden');
      if (!client.isOpen) client.connect();
      client.requestKeyframe(true);
    });

    document.addEventListener('keydown', event => {
      if (!gameMode || editorMode) return;
      if (event.key === 'Escape' && !fullscreenElement()) {
        // Leave browser-level controls alone.
      }
    });
  }

  function onConnectionState(event) {
    const state = event.detail.state;
    if (state === 'connected') {
      connectedOnce = true;
      setChip(el.connChip, 'live', '已连接');
      updateWaiting();
    } else if (state === 'connecting' || state === 'reconnecting') {
      setChip(el.connChip, 'warn', state === 'connecting' ? '连接中' : '重连中');
      updateWaiting();
    } else {
      setChip(el.connChip, state === 'error' ? 'bad' : '', '已断开');
      client.requestStatus();
      updateWaiting();
    }
  }

  function applyStatus(status) {
    if (!status) return;
    updateWaiting(status);
    const state = status.streamState || 'idle';
    applyStreamState({ state, error: status.streamError });
    if (status.controller) {
      const controller = status.controller;
      el.modeLabel.textContent = (gameMode ? '游戏模式' : '普通模式') +
        ' · ' + (controller.active === 'hardware' ? '驱动注入' : '系统注入');
      el.mobileLayerInfo.textContent = controller.active === 'hardware' ? '驱动注入' : '系统注入';
      el.mobileDriverHint.classList.toggle('hidden',
        controller.active === 'hardware' || controller.hardwareAvailable || controller.requested === 'software');
      if (controller.fallbackReason) el.mobileDriverHint.querySelector('p').textContent = controller.fallbackReason;
    }
    if (status.video) applyMobileVideo(status.video);
  }

  function applyMobileVideo(video) {
    if (!video) return;
    el.mobileEncoderInfo.textContent = `${video.width}×${video.height} · ${video.hardwareAccelerated ? 'GPU' : '软件'}`;
    if (document.activeElement !== el.mobileFpsSelect)
      el.mobileFpsSelect.value = String(video.fps);
    if (document.activeElement !== el.mobileQualitySelect) {
      const options = Array.from(el.mobileQualitySelect.options);
      const exact = options.find(option => Number(option.value) === video.bitrate);
      const closest = exact || options.sort((a, b) =>
        Math.abs(Number(a.value) - video.bitrate) - Math.abs(Number(b.value) - video.bitrate))[0];
      if (closest) el.mobileQualitySelect.value = closest.value;
    }
  }

  function applyMobileVideoSettings() {
    if (!client.isOpen) {
      showToast('尚未连接电脑。', true);
      return;
    }
    const fps = Number(el.mobileFpsSelect.value) || 60;
    const bitrate = Number(el.mobileQualitySelect.value) || 16000000;
    if (client.setVideoSettings(fps, bitrate))
      showToast('画质设置已应用，视频不会中断。');
    else
      showToast('设置发送失败。', true);
  }

  async function openDriverInstallerOnPc() {
    const token = client.token ? `?token=${encodeURIComponent(client.token)}` : '';
    try {
      const response = await fetch(`/api/drivers/open-installer${token}`, { method: 'POST' });
      if (!response.ok) throw new Error(await response.text() || `HTTP ${response.status}`);
      showToast('已在电脑上打开驱动安装目录。');
    } catch (error) {
      showToast('电脑端未部署驱动安装目录，请查看 docs/DRIVERS.md。', true);
    }
  }

  function openSettings() {
    closeKeyboardIfOpen();
    if (editorMode) closeEditor();
    releaseAllGameInputs();
    cancelAllNormalTouches();
    el.settingsSheet.classList.remove('hidden');
    updateFullscreenUi();
  }

  function closeSettings() {
    el.settingsSheet.classList.add('hidden');
  }

  function openVirtualKeyboard() {
    closeSettings();
    releaseAllGameInputs();
    cancelAllNormalTouches();
    el.keyboardSheet.classList.remove('hidden');
    setTimeout(() => el.virtualKeyboardInput.focus({ preventScroll: true }), 120);
  }

  function closeKeyboardIfOpen() {
    if (!el.keyboardSheet.classList.contains('hidden')) closeVirtualKeyboard();
  }

  function closeVirtualKeyboard() {
    el.keyboardSheet.classList.add('hidden');
    el.virtualKeyboardInput.blur();
  }

  function sendVirtualKeyboardText() {
    if (!client.isOpen) {
      showToast('尚未连接电脑。', true);
      return;
    }
    const text = el.virtualKeyboardInput.value;
    if (!text) return;
    client.sendInput({ type: 'text', key: text, source: 'mobile-keyboard' });
    el.virtualKeyboardInput.value = '';
    showToast('文本已发送。');
  }

  function sendSpecialKey(code) {
    if (!client.isOpen) return;
    client.sendInput({ type: 'keyboard', action: 'down', code, source: 'mobile-keyboard' });
    setTimeout(() => {
      client.sendInput({ type: 'keyboard', action: 'up', code, source: 'mobile-keyboard' });
    }, 40);
  }

  function applyStreamState(state) {
    const streaming = ['starting', 'streaming', 'recovering'].includes(state.state);
    if (state.state === 'streaming') {
      setChip(el.streamChip, 'live', '串流中');
      maybeHideGameHint();
    } else if (state.state === 'starting') {
      setChip(el.streamChip, 'warn', '启动中');
    } else if (state.state === 'recovering') {
      setChip(el.streamChip, 'warn', '恢复中');
    } else if (state.state === 'error') {
      setChip(el.streamChip, 'bad', '串流错误');
      showToast(state.error || '串流发生错误。', true);
    } else {
      setChip(el.streamChip, '', '未串流');
    }
    updateWaiting();
    if (!streaming) {
      releaseAllGameInputs();
      cancelAllNormalTouches();
    }
  }

  function updateWaiting(status) {
    status = status || client.status;
    const state = status ? (status.streamState || 'idle') : 'idle';
    const active = ['starting', 'streaming', 'recovering'].includes(state);
    el.waiting.classList.toggle('hidden', active);
    if (active) {
      el.waitingText.textContent = '正在接收电脑画面…';
      el.requestStart.classList.add('hidden');
      return;
    }
    if (!client.isOpen) {
      el.waitingText.textContent = '正在连接 GlassLink 服务…';
      el.requestStart.classList.add('hidden');
    } else if (state === 'error') {
      el.waitingText.textContent = status.streamError || '串流启动失败，请查看电脑端日志。';
      el.requestStart.classList.remove('hidden');
    } else {
      el.waitingText.textContent = '电脑尚未开始串流。请在电脑端网页点击「开始串流」，或点击下方按钮发起请求。';
      el.requestStart.classList.remove('hidden');
    }
  }

  function setChip(element, cssClass, text) {
    element.className = 'm-chip ' + (cssClass || '');
    if (element === el.connChip) {
      element.innerHTML = `<i></i>${escapeHtml(text)}`;
    } else {
      element.textContent = text;
    }
  }

  function escapeHtml(value) {
    return String(value).replace(/[&<>"']/g, s => ({
      '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    }[s]));
  }

  /* ================= fullscreen ================= */
  function toggleFullscreenPreference() {
    if (fullscreenElement()) {
      exitFullscreen().finally(updateFullscreenUi);
    } else {
      requestFullscreen(document.documentElement).then(ok => {
        if (!ok) showToast('浏览器未允许自动全屏，请再点一次或使用菜单中的全屏。', true);
        else {
          fullscreenPreferred = true;
          storageSet(FULLSCREEN_STORAGE_KEY, true);
          tryLockLandscape();
        }
        updateFullscreenUi();
      });
    }
  }

  function updateFullscreenUi() {
    const active = !!fullscreenElement();
    el.mobileFullscreenInfo.textContent = active ? '开' : '关';
    el.fullscreenChoice.classList.toggle('on', active);
    el.fullscreenChoiceText.textContent = `全屏显示：${active ? '开' : '关'}`;
    el.fsIconBtn.textContent = active ? '⤢' : '⛶';
    if (!active) {
      fullscreenPreferred = false;
      storageSet(FULLSCREEN_STORAGE_KEY, false);
    }
  }

  function tryLockLandscape() {
    try {
      if (screen.orientation && screen.orientation.lock) {
        screen.orientation.lock('landscape').catch(() => { });
      }
    } catch (e) { }
  }

  /* ================= normal mode: relative touchpad ================= */
  const padPointers = new Map();
  let padPrimary = null, padMode = 'single';
  let padLastX = 0, padLastY = 0, padMoved = 0, padStartedAt = 0;
  let padLongPressTimer = null, padRingTimer = null, padRightClicked = false;
  let padMidX = 0, padMidY = 0;
  let lastTapAt = 0, lastTapX = 0, lastTapY = 0;
  let scrollStartedAt = 0, scrollMoved = 0;

  function onNormalPointerDown(event) {
    if (gameMode || transitioning) return;
    if (padPointers.size >= 2) return;
    if (!isStreamActive()) { showToast('串流尚未开始。请先请求电脑开始串流。'); return; }
    event.preventDefault();
    el.touchSurface.setPointerCapture?.(event.pointerId);
    padPointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
    if (!fullscreenElement()) requestFullscreen(document.documentElement).catch(() => {});

    if (padPointers.size === 1) {
      padPrimary = event.pointerId;
      padLastX = event.clientX; padLastY = event.clientY;
      padMoved = 0; padStartedAt = performance.now(); padRightClicked = false;
      const now = performance.now();
      const nearLast = Math.hypot(event.clientX - lastTapX, event.clientY - lastTapY) < 40;
      if (now - lastTapAt < 320 && nearLast) {
        padMode = 'drag';
        clearPressTimer();
        if (padRingTimer) { clearTimeout(padRingTimer); padRingTimer = null; }
        hideLongPressRing();
        client.sendInput({ type: 'mouseButton', button: 'left', action: 'down', source: 'mobile-touchpad' });
      } else {
        padMode = 'single';
        padRingTimer = setTimeout(() => showLongPressRing(event.clientX, event.clientY), 200);
        padLongPressTimer = setTimeout(() => {
          padRightClicked = true;
          el.ring.classList.add('ready');
          haptic(35);
          rightClick();
        }, 650);
      }
    } else if (padPointers.size === 2) {
      padMode = 'scroll';
      clearPressTimer(); hideLongPressRing();
      const [a, b] = [...padPointers.values()];
      padMidX = (a.x + b.x) / 2; padMidY = (a.y + b.y) / 2;
      scrollStartedAt = performance.now(); scrollMoved = 0;
    }
  }

  function onNormalPointerMove(event) {
    const pointer = padPointers.get(event.pointerId);
    if (!pointer) return;
    event.preventDefault();
    pointer.x = event.clientX; pointer.y = event.clientY;

    if (padMode === 'scroll' && padPointers.size >= 2) {
      const [a, b] = [...padPointers.values()];
      const midY = (a.y + b.y) / 2;
      const dy = midY - padMidY;
      padMidY = midY;
      scrollMoved += Math.abs(dy);
      if (Math.abs(dy) >= 0.5)
        client.sendInput({ type: 'mouseWheel', wheelDelta: Math.round(dy * 1.5), source: 'mobile-touchpad' });
      return;
    }

    if (event.pointerId !== padPrimary) return;
    const dx = (event.clientX - padLastX) * 1.6;
    const dy = (event.clientY - padLastY) * 1.6;
    padLastX = event.clientX; padLastY = event.clientY;
    padMoved += Math.abs(dx) + Math.abs(dy);
    if (padMoved > 12) {
      clearPressTimer();
      if (padRingTimer) { clearTimeout(padRingTimer); padRingTimer = null; }
      hideLongPressRing();
    }
    if (Math.abs(dx) >= 0.5 || Math.abs(dy) >= 0.5)
      client.sendInput({ type: 'mouseMove', mode: 'relative', dx, dy, source: 'mobile-touchpad' });
  }

  function onNormalPointerUp(event) {
    if (!padPointers.has(event.pointerId)) return;
    padPointers.delete(event.pointerId);

    if (padMode === 'scroll') {
      if (padPointers.size === 0) {
        if (event.type !== 'pointercancel' && performance.now() - scrollStartedAt < 300 && scrollMoved < 10)
          rightClick();
        padPrimary = null; padMode = 'single';
      }
      return;
    }
    if (event.pointerId !== padPrimary) return;

    clearPressTimer();
    if (padRingTimer) { clearTimeout(padRingTimer); padRingTimer = null; }
    hideLongPressRing();
    if (padMode === 'drag') {
      client.sendInput({ type: 'mouseButton', button: 'left', action: 'up', source: 'mobile-touchpad' });
    } else {
      const tap = event.type !== 'pointercancel' && !padRightClicked &&
        padMoved < 12 && performance.now() - padStartedAt < 500;
      if (tap) {
        client.sendInput({ type: 'mouseButton', button: 'left', action: 'down', source: 'mobile-touchpad' });
        setTimeout(() => client.sendInput({ type: 'mouseButton', button: 'left', action: 'up', source: 'mobile-touchpad' }), 35);
        lastTapAt = performance.now(); lastTapX = event.clientX; lastTapY = event.clientY;
      }
    }
    padPrimary = null; padMode = 'single';
    if (padPointers.size === 0) padPointers.clear();
  }

  function clearPressTimer() {
    if (padLongPressTimer) { clearTimeout(padLongPressTimer); padLongPressTimer = null; }
  }

  function rightClick() {
    haptic(30);
    client.sendInput({ type: 'mouseButton', button: 'right', action: 'down', source: 'mobile-touchpad' });
    setTimeout(() => client.sendInput({ type: 'mouseButton', button: 'right', action: 'up', source: 'mobile-touchpad' }), 40);
  }

  function showLongPressRing(x, y) {
    if (!el.ring) return;
    // The ring belongs to the remote pointer, not to the finger. Follow the
    // host cursor reported by the server so the charge indicator stays aligned.
    const c = client.cursor;
    if (c && c.visible) {
      const rect = videoContentRect(el.canvas, el.app);
      if (rect.width && rect.height) {
        x = rect.left + c.x * rect.width;
        y = rect.top + c.y * rect.height;
      }
    }
    el.ring.style.left = x + 'px'; el.ring.style.top = y + 'px';
    el.ring.classList.remove('hidden', 'ready');
    el.ring.style.animation = 'none'; void el.ring.offsetWidth; el.ring.style.animation = '';
  }

  function hideLongPressRing() {
    el.ring?.classList.add('hidden'); el.ring?.classList.remove('ready');
  }

  function cancelAllNormalTouches() {
    clearPressTimer();
    if (padRingTimer) { clearTimeout(padRingTimer); padRingTimer = null; }
    hideLongPressRing();
    padPointers.clear(); padPrimary = null; padMode = 'single';
  }

  /* ================= game mode ================= */
  function enterGameMode() {
    if (gameMode || transitioning) return;
    transitioning = true;
    cancelAllNormalTouches();
    releaseAllGameInputs();
    el.transition.classList.add('active');

    // Must be requested in the user gesture stack.
    requestFullscreen(document.documentElement).then(ok => {
      if (ok) {
        fullscreenPreferred = true;
        storageSet(FULLSCREEN_STORAGE_KEY, true);
        tryLockLandscape();
      }
      updateFullscreenUi();
    });

    setTimeout(() => {
      gameMode = true;
      transitioning = false;
      el.normalLayer.classList.add('hidden');
      el.gameLayer.classList.remove('hidden');
      el.configBtn.classList.remove('hidden');
      el.exitGameBtn.classList.remove('hidden');
      el.fsIconBtn.classList.add('hidden');
      el.modeLabel.textContent = '游戏模式';
      remoteCursor.setEnabled(false);
      resizeControlCanvas();
      requestDraw();
      requestWakeLock();
      showHintTemporarily();
      setTimeout(() => el.transition.classList.remove('active'), 500);
    }, 680);
  }

  function exitGameMode() {
    if (!gameMode) return;
    closeEditor();
    releaseAllGameInputs();
    gameMode = false;
    el.gameLayer.classList.add('hidden');
    el.normalLayer.classList.remove('hidden');
    el.configBtn.classList.add('hidden');
    el.exitGameBtn.classList.add('hidden');
    el.fsIconBtn.classList.remove('hidden');
    el.modeLabel.textContent = '普通模式';
    selectedId = null;
    remoteCursor.setEnabled(true);
    releaseWakeLock();
    if (fullscreenElement()) exitFullscreen().finally(updateFullscreenUi);
    else updateFullscreenUi();
    requestDraw();
  }

  function showHintTemporarily() {
    el.gameHint.classList.remove('fade');
    clearTimeout(gameHintTimer);
    gameHintTimer = setTimeout(() => el.gameHint.classList.add('fade'), 3800);
  }

  function maybeHideGameHint() {
    if (gameMode) showHintTemporarily();
  }

  async function requestWakeLock() {
    try {
      if ('wakeLock' in navigator && !wakeLock && !document.hidden) {
        wakeLock = await navigator.wakeLock.request('screen');
        wakeLock.addEventListener('release', () => { wakeLock = null; });
      }
    } catch (e) { }
  }

  function releaseWakeLock() {
    try { wakeLock && wakeLock.release(); } catch (e) { }
    wakeLock = null;
  }

  function resizeControlCanvas() {
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const width = Math.max(1, window.innerWidth);
    const height = Math.max(1, window.innerHeight);
    el.controlCanvas.width = Math.round(width * dpr);
    el.controlCanvas.height = Math.round(height * dpr);
    el.controlCanvas.style.width = width + 'px';
    el.controlCanvas.style.height = height + 'px';
    gameCtx.setTransform(dpr, 0, 0, dpr, 0, 0);
    gameCtx.imageSmoothingEnabled = true;
    requestDraw();
  }

  function canvasSize() {
    return {
      width: window.innerWidth,
      height: window.innerHeight
    };
  }

  /* ================= game input routing ================= */
  function hitTest(x, y) {
    const { width, height } = canvasSize();
    // Buttons and joysticks take precedence over the large look area so the
    // transparent view-rotation zone can never block a real control.
    for (let i = controls.length - 1; i >= 0; i--) {
      const control = controls[i];
      if (control.type === 'look') continue;
      const cx = control.x * width;
      const cy = control.y * height;
      const r = Math.max(24, control.size / 2);
      if (Math.hypot(x - cx, y - cy) <= r) return control;
    }
    for (let i = controls.length - 1; i >= 0; i--) {
      const control = controls[i];
      if (control.type !== 'look') continue;
      const cx = control.x * width;
      const cy = control.y * height;
      const r = Math.max(36, control.size / 2);
      if (Math.hypot(x - cx, y - cy) <= r) return control;
    }
    return null;
  }

  function onGamePointerDown(event) {
    if (!gameMode || transitioning) return;
    const x = event.clientX;
    const y = event.clientY;
    const hit = hitTest(x, y);

    if (editorMode) {
      if (hit) {
        selectControl(hit.id);
        const { width, height } = canvasSize();
        dragState = {
          pointerId: event.pointerId,
          controlId: hit.id,
          offsetX: x - hit.x * width,
          offsetY: y - hit.y * height
        };
        el.controlCanvas.setPointerCapture?.(event.pointerId);
        requestDraw();
      } else {
        selectedId = null;
        syncEditorForm();
      }
      event.preventDefault();
      return;
    }

    if (!hit) return;
    haptic(18);
    if (!isStreamActive()) {
      showToast('串流尚未开始。', false);
      return;
    }
    event.preventDefault();
    el.controlCanvas.setPointerCapture?.(event.pointerId);
    showHintTemporarily();

    const active = {
      pointerId: event.pointerId,
      controlId: hit.id,
      type: hit.type,
      pressedKeys: new Set(),
      mouseButton: null,
      lastX: x,
      lastY: y,
      vectorX: 0,
      vectorY: 0
    };
    activePointers.set(event.pointerId, active);

    if (hit.type === 'button') {
      activateButton(hit, active);
    } else if (hit.type === 'joystick') {
      updateJoystick(hit, active, x, y);
    } else if (hit.type === 'look') {
      active.lastX = x;
      active.lastY = y;
    }
    requestDraw();
  }

  function onGamePointerMove(event) {
    if (!gameMode) return;
    if (editorMode) {
      if (!dragState || dragState.pointerId !== event.pointerId) return;
      const control = controls.find(item => item.id === dragState.controlId);
      if (!control) return;
      const { width, height } = canvasSize();
      control.x = clamp((event.clientX - dragState.offsetX) / width, 0.06, 0.94);
      control.y = clamp((event.clientY - dragState.offsetY) / height, 0.08, 0.92);
      requestDraw();
      return;
    }

    const active = activePointers.get(event.pointerId);
    if (!active) return;
    const control = controls.find(item => item.id === active.controlId);
    if (!control) return;

    if (active.type === 'joystick') {
      updateJoystick(control, active, event.clientX, event.clientY);
      requestDraw();
    } else if (active.type === 'look') {
      const dx = event.clientX - active.lastX;
      const dy = event.clientY - active.lastY;
      active.lastX = event.clientX;
      active.lastY = event.clientY;
      const sensitivity = clamp(Number(control.sensitivity) || 1.2, 0.2, 4);
      if (control.label === 'R3' || control.id === 'r3') {
        gamepadState.rightX = clamp(dx / 35, -1, 1);
        gamepadState.rightY = clamp(dy / 35, -1, 1);
        sendGamepad();
        const token = ++rightStickReset;
        setTimeout(() => {
          if (token !== rightStickReset) return;
          gamepadState.rightX = 0; gamepadState.rightY = 0; sendGamepad();
        }, 90);
      } else if (Math.abs(dx) + Math.abs(dy) >= 0.5) {
        client.sendInput({
          type: 'mouseMove',
          mode: 'relative',
          dx: dx * sensitivity,
          dy: dy * sensitivity,
          source: 'mobile-game'
        });
      }
    }
  }

  function onGamePointerUp(event) {
    if (!gameMode) return;
    if (editorMode) {
      if (dragState && dragState.pointerId === event.pointerId) {
        dragState = null;
        saveControls();
      }
      return;
    }

    const active = activePointers.get(event.pointerId);
    if (!active) return;
    activePointers.delete(event.pointerId);
    const control = controls.find(item => item.id === active.controlId);
    if (!control) return;

    if (active.type === 'button') {
      releaseButtonControl(control, active);
    } else if (active.type === 'joystick') {
      for (const code of active.pressedKeys) releaseVirtualKey(code);
      active.pressedKeys.clear();
      active.vectorX = 0;
      active.vectorY = 0;
      if (control.label === 'L3' || control.id === 'l3') {
        gamepadState.leftX = 0; gamepadState.leftY = 0; sendGamepad();
      }
    }
    requestDraw();
  }

  function activateButton(control, active) {
    const pad = gamepadButtonName(control.label);
    if (pad) {
      active.gamepadButton = pad;
      gamepadState.buttons.add(pad);
      if (pad === 'L2') gamepadState.leftTrigger = 1;
      if (pad === 'R2') gamepadState.rightTrigger = 1;
      sendGamepad();
      return;
    }
    if (control.mouseButton) {
      active.mouseButton = control.mouseButton;
      mouseButtonDown(control.mouseButton);
    } else if (control.key) {
      keyDown(control.key);
      active.pressedKeys.add(control.key);
    }
  }

  function releaseButtonControl(control, active) {
    if (active.gamepadButton) {
      gamepadState.buttons.delete(active.gamepadButton);
      if (active.gamepadButton === 'L2') gamepadState.leftTrigger = 0;
      if (active.gamepadButton === 'R2') gamepadState.rightTrigger = 0;
      sendGamepad();
    }
    if (active.mouseButton) mouseButtonUp(active.mouseButton);
    for (const code of active.pressedKeys) releaseVirtualKey(code);
    active.pressedKeys.clear();
  }

  function joystickKeys(preset) {
    return preset === 'arrows'
      ? { up: 'ArrowUp', down: 'ArrowDown', left: 'ArrowLeft', right: 'ArrowRight' }
      : { up: 'KeyW', down: 'KeyS', left: 'KeyA', right: 'KeyD' };
  }

  function updateJoystick(control, active, px, py) {
    const { width, height } = canvasSize();
    const cx = control.x * width;
    const cy = control.y * height;
    const radius = Math.max(30, control.size / 2);
    let vx = (px - cx) / radius;
    let vy = (py - cy) / radius;
    const magnitude = Math.hypot(vx, vy);
    if (magnitude > 1) {
      vx /= magnitude;
      vy /= magnitude;
    }
    active.vectorX = vx;
    active.vectorY = vy;

    if (control.label === 'L3' || control.id === 'l3') {
      gamepadState.leftX = vx;
      gamepadState.leftY = vy;
      sendGamepad();
      return;
    }

    const keys = joystickKeys(control.preset || 'wasd');
    const threshold = 0.24;
    const desired = new Set();
    if (vy < -threshold) desired.add(keys.up);
    if (vy > threshold) desired.add(keys.down);
    if (vx < -threshold) desired.add(keys.left);
    if (vx > threshold) desired.add(keys.right);

    for (const code of Array.from(active.pressedKeys)) {
      if (!desired.has(code)) {
        releaseVirtualKey(code);
        active.pressedKeys.delete(code);
      }
    }
    for (const code of desired) {
      if (!active.pressedKeys.has(code)) {
        keyDown(code);
        active.pressedKeys.add(code);
      }
    }
  }

  const KEYS_BY_CODE = {
    Space: ' ',
    ShiftLeft: 'Shift',
    ControlLeft: 'Control',
    Enter: 'Enter',
    Escape: 'Escape',
    Tab: 'Tab',
    ArrowUp: 'ArrowUp',
    ArrowDown: 'ArrowDown',
    ArrowLeft: 'ArrowLeft',
    ArrowRight: 'ArrowRight'
  };

  function keyDown(code) {
    const count = heldKeyCounts.get(code) || 0;
    heldKeyCounts.set(code, count + 1);
    if (count === 0) {
      client.sendInput({
        type: 'keyboard',
        action: 'down',
        code,
        key: KEYS_BY_CODE[code] || (code.startsWith('Digit') ? code.slice(5) : code.replace(/^Key/, '')),
        source: 'mobile-game'
      });
    }
  }

  function releaseVirtualKey(code) {
    const count = heldKeyCounts.get(code) || 0;
    if (count <= 0) return;
    if (count === 1) {
      heldKeyCounts.delete(code);
      client.sendInput({
        type: 'keyboard',
        action: 'up',
        code,
        key: KEYS_BY_CODE[code] || (code.startsWith('Digit') ? code.slice(5) : code.replace(/^Key/, '')),
        source: 'mobile-game'
      });
    } else {
      heldKeyCounts.set(code, count - 1);
    }
  }

  function mouseButtonDown(button) {
    const count = heldMouseCounts.get(button) || 0;
    heldMouseCounts.set(button, count + 1);
    if (count === 0) {
      client.sendInput({ type: 'mouseButton', button, action: 'down', source: 'mobile-game' });
    }
  }

  function mouseButtonUp(button) {
    const count = heldMouseCounts.get(button) || 0;
    if (count <= 0) return;
    if (count === 1) {
      heldMouseCounts.delete(button);
      client.sendInput({ type: 'mouseButton', button, action: 'up', source: 'mobile-game' });
    } else {
      heldMouseCounts.set(button, count - 1);
    }
  }

  function releaseAllGameInputs() {
    for (const code of Array.from(heldKeyCounts.keys())) {
      const key = KEYS_BY_CODE[code] || (code.startsWith('Digit') ? code.slice(5) : code.replace(/^Key/, ''));
      client.sendInput({ type: 'keyboard', action: 'up', code, key, source: 'mobile-game' });
    }
    heldKeyCounts.clear();
    for (const button of Array.from(heldMouseCounts.keys())) {
      client.sendInput({ type: 'mouseButton', button, action: 'up', source: 'mobile-game' });
    }
    heldMouseCounts.clear();
    for (const active of activePointers.values()) {
      for (const code of active.pressedKeys) {
        // Counts are reset above; this is only local state cleanup.
      }
    }
    activePointers.clear();
    if (gameMode) requestDraw();
  }

  function haptic(ms) {
    try { navigator.vibrate?.(ms || 18); } catch (e) { }
  }

  function isStreamActive() {
    return !!client.isOpen && !!client.status && ['starting', 'streaming', 'recovering'].includes(client.status.streamState);
  }

  /* ================= control drawing ================= */
  function requestDraw() {
    if (drawQueued) return;
    drawQueued = true;
    requestAnimationFrame(() => {
      drawQueued = false;
      if (gameMode) drawControls();
    });
  }

  function drawControls() {
    const { width, height } = canvasSize();
    gameCtx.clearRect(0, 0, width, height);

    for (const control of controls) {
      if (control.type === 'look') drawLook(control);
      else if (control.type === 'joystick') drawJoystick(control);
      else drawButton(control);
    }

    if (editorMode && selectedId) {
      const control = controls.find(c => c.id === selectedId);
      if (control) {
        const cx = control.x * width;
        const cy = control.y * height;
        const r = Math.max(24, control.size / 2) + 9;
        gameCtx.save();
        gameCtx.setLineDash([5, 4]);
        gameCtx.strokeStyle = 'rgba(114, 241, 209, .8)';
        gameCtx.lineWidth = 1.5;
        gameCtx.beginPath();
        gameCtx.arc(cx, cy, r, 0, Math.PI * 2);
        gameCtx.stroke();
        gameCtx.fillStyle = 'rgba(114, 241, 209, .9)';
        gameCtx.beginPath();
        gameCtx.arc(cx, cy + r + 12, 3.5, 0, Math.PI * 2);
        gameCtx.fill();
        gameCtx.restore();
      }
    }
  }

  function drawButton(control) {
    const { width, height } = canvasSize();
    const x = control.x * width;
    const y = control.y * height;
    const r = Math.max(22, control.size / 2);
    const active = Array.from(activePointers.values()).some(a => a.controlId === control.id && a.type === 'button');
    const scale = active ? 0.92 : 1;
    const alpha = clamp(control.opacity, 0.18, 1);
    const color = control.color || '#72f1d1';
    const fill = hexToRgba(color, active ? Math.min(0.92, alpha + 0.18) : alpha * 0.65);
    const stroke = hexToRgba(color, active ? 0.95 : 0.72);

    gameCtx.save();
    gameCtx.translate(x, y);
    gameCtx.scale(scale, scale);
    gameCtx.beginPath();
    shapePath(gameCtx, 0, 0, r, control.shape);
    gameCtx.fillStyle = fill;
    gameCtx.fill();
    gameCtx.strokeStyle = stroke;
    gameCtx.lineWidth = active ? 2.2 : 1.3;
    gameCtx.stroke();

    gameCtx.fillStyle = active ? '#05110d' : 'rgba(255,255,255,.9)';
    gameCtx.font = `600 ${Math.max(10, Math.min(15, r * 0.38))}px ${getComputedStyle(document.body).fontFamily}`;
    gameCtx.textAlign = 'center';
    gameCtx.textBaseline = 'middle';
    gameCtx.fillText(control.label || '按键', 0, 0);
    gameCtx.restore();
  }

  function drawJoystick(control) {
    const { width, height } = canvasSize();
    const x = control.x * width;
    const y = control.y * height;
    const r = Math.max(34, control.size / 2);
    const active = Array.from(activePointers.values()).find(a => a.controlId === control.id && a.type === 'joystick');
    const alpha = clamp(control.opacity, 0.15, 1);
    const color = control.color || '#72f1d1';

    gameCtx.save();
    gameCtx.beginPath();
    gameCtx.arc(x, y, r, 0, Math.PI * 2);
    gameCtx.fillStyle = hexToRgba(color, alpha * 0.22);
    gameCtx.fill();
    gameCtx.strokeStyle = hexToRgba(color, alpha * 0.85);
    gameCtx.lineWidth = 1.4;
    gameCtx.stroke();

    gameCtx.beginPath();
    gameCtx.arc(x, y, r * 0.68, 0, Math.PI * 2);
    gameCtx.strokeStyle = 'rgba(255,255,255,.08)';
    gameCtx.lineWidth = 1;
    gameCtx.stroke();

    const knobR = Math.max(20, r * 0.34);
    const ox = (active ? active.vectorX : 0) * (r - knobR) * 0.82;
    const oy = (active ? active.vectorY : 0) * (r - knobR) * 0.82;
    gameCtx.beginPath();
    gameCtx.arc(x + ox, y + oy, knobR, 0, Math.PI * 2);
    gameCtx.fillStyle = hexToRgba(color, active ? 0.78 : 0.48);
    gameCtx.fill();
    gameCtx.strokeStyle = hexToRgba(color, 0.9);
    gameCtx.lineWidth = 1.4;
    gameCtx.stroke();

    gameCtx.fillStyle = 'rgba(255,255,255,.55)';
    gameCtx.font = '600 10px ' + getComputedStyle(document.body).fontFamily;
    gameCtx.textAlign = 'center';
    gameCtx.textBaseline = 'middle';
    gameCtx.fillText(control.label || '移动', x, y + r + 13);
    gameCtx.restore();
  }

  function drawLook(control) {
    const { width, height } = canvasSize();
    const x = control.x * width;
    const y = control.y * height;
    const r = Math.max(36, control.size / 2);
    const active = Array.from(activePointers.values()).some(a => a.controlId === control.id && a.type === 'look');
    const alpha = clamp(control.opacity, 0.06, 0.5);
    const color = control.color || '#78a5ff';

    gameCtx.save();
    gameCtx.setLineDash([6, 7]);
    gameCtx.beginPath();
    gameCtx.arc(x, y, r, 0, Math.PI * 2);
    gameCtx.fillStyle = hexToRgba(color, active ? alpha + 0.08 : alpha * 0.35);
    gameCtx.fill();
    gameCtx.strokeStyle = hexToRgba(color, active ? 0.7 : 0.35);
    gameCtx.lineWidth = active ? 1.8 : 1;
    gameCtx.stroke();
    gameCtx.setLineDash([]);

    gameCtx.fillStyle = active ? 'rgba(220,236,255,.9)' : 'rgba(190,208,236,.34)';
    gameCtx.font = '600 10px ' + getComputedStyle(document.body).fontFamily;
    gameCtx.textAlign = 'center';
    gameCtx.textBaseline = 'middle';
    gameCtx.fillText(control.label || '视角', x, y - 6);
    gameCtx.font = '9px ' + getComputedStyle(document.body).fontFamily;
    gameCtx.fillText('拖动旋转', x, y + 9);
    gameCtx.restore();
  }

  function shapePath(ctx, x, y, r, shape) {
    switch (shape) {
      case 'square':
        ctx.rect(x - r, y - r, r * 2, r * 2);
        break;
      case 'rounded': {
        const rad = r * 0.42;
        if (ctx.roundRect) ctx.roundRect(x - r, y - r, r * 2, r * 2, rad);
        else ctx.rect(x - r, y - r, r * 2, r * 2);
        break;
      }
      case 'diamond':
        ctx.moveTo(x, y - r);
        ctx.lineTo(x + r, y);
        ctx.lineTo(x, y + r);
        ctx.lineTo(x - r, y);
        ctx.closePath();
        break;
      default:
        ctx.arc(x, y, r, 0, Math.PI * 2);
        break;
    }
  }

  function hexToRgba(hex, alpha) {
    const value = String(hex || '#72f1d1').replace('#', '');
    const full = value.length === 3 ? value.split('').map(ch => ch + ch).join('') : value.padEnd(6, '0').slice(0, 6);
    const int = parseInt(full, 16);
    const r = (int >> 16) & 255;
    const g = (int >> 8) & 255;
    const b = int & 255;
    return `rgba(${r}, ${g}, ${b}, ${clamp(alpha, 0, 1)})`;
  }

  /* ================= editor ================= */
  function buildKeyOptions() {
    for (const [value, label] of KEY_OPTIONS) {
      const option = document.createElement('option');
      option.value = value;
      option.textContent = label;
      el.editKey.appendChild(option);
    }
  }

  function toggleEditor() {
    if (editorMode) closeEditor();
    else openEditor();
  }

  function openEditor() {
    if (!gameMode) return;
    releaseAllGameInputs();
    editorMode = true;
    el.editorSheet.classList.remove('hidden');
    el.configBtn.textContent = '✓';
    selectedId = selectedId || (controls[0] ? controls[0].id : null);
    syncEditorForm();
    requestDraw();
  }

  function closeEditor() {
    if (!editorMode) return;
    editorMode = false;
    el.editorSheet.classList.add('hidden');
    el.configBtn.textContent = '⚙';
    selectedId = null;
    saveControls();
    requestDraw();
  }

  function selectControl(id) {
    selectedId = id;
    syncEditorForm();
    requestDraw();
  }

  function syncEditorForm() {
    const control = controls.find(item => item.id === selectedId);
    if (!control) {
      el.editorEmpty.classList.remove('hidden');
      el.editorForm.classList.add('hidden');
      return;
    }
    el.editorEmpty.classList.add('hidden');
    el.editorForm.classList.remove('hidden');
    el.editLabel.value = control.label;
    el.editShape.value = control.shape || 'circle';
    el.editSize.value = control.size;
    el.editOpacity.value = Math.round(control.opacity * 100);
    el.editSensitivity.value = control.sensitivity || 1.2;
    el.editPreset.value = control.preset || 'wasd';
    el.editColor.value = control.color || '#72f1d1';
    el.sizeValue.textContent = control.size;
    el.opacityValue.textContent = Math.round(control.opacity * 100) + '%';
    el.sensitivityValue.textContent = Number(control.sensitivity || 1.2).toFixed(1);

    const keyValue = control.mouseButton ? 'mouse:' + control.mouseButton : (control.key || 'Space');
    el.editKey.value = keyValue;
    if (el.editKey.value !== keyValue) el.editKey.value = 'Space';

    el.editKey.closest('.form-row').classList.toggle('hidden', control.type !== 'button');
    el.editPreset.closest('.form-row').classList.toggle('hidden', control.type !== 'joystick');
    el.editSensitivity.closest('.form-row').classList.toggle('hidden', control.type !== 'look');
    el.deleteControlBtn.disabled = controls.length <= 1;
    el.editorSubtitle.textContent = control.type === 'button'
      ? '拖动改变位置 · 当前为按键'
      : control.type === 'joystick'
        ? '拖动改变位置 · 当前为摇杆'
        : '拖动改变位置 · 当前为视角区';
  }

  function updateSelected(patch) {
    const control = controls.find(item => item.id === selectedId);
    if (!control) return;
    Object.assign(control, patch);
    saveControls();
    requestDraw();
  }

  function addControl(type) {
    if (!gameMode) return;
    const id = type + '-' + Date.now().toString(36) + Math.random().toString(36).slice(2, 5);
    const base = type === 'joystick'
      ? { x: 0.24, y: 0.7, size: 116, shape: 'circle', label: '移动', color: '#72f1d1', opacity: 0.48, preset: 'wasd' }
      : type === 'look'
        ? { x: 0.8, y: 0.46, size: 230, shape: 'circle', label: '视角', color: '#78a5ff', opacity: 0.16, sensitivity: 1.2 }
        : { x: 0.5, y: 0.5, size: 60, shape: 'circle', label: '按键', color: '#72f1d1', opacity: 0.6, key: 'Space', mouseButton: '' };
    const control = {
      id, type, ...base, x: clamp(base.x + (Math.random() - 0.5) * 0.04, 0.08, 0.92),
      y: clamp(base.y + (Math.random() - 0.5) * 0.04, 0.1, 0.9),
      preset: base.preset || 'wasd', sensitivity: base.sensitivity || 1.2,
      mouseButton: base.mouseButton || '', key: base.key || ''
    };
    controls.push(control);
    selectedId = id;
    if (!editorMode) openEditor();
    syncEditorForm();
    saveControls();
    requestDraw();
  }

  function deleteSelectedControl() {
    if (!selectedId || controls.length <= 1) return;
    controls = controls.filter(item => item.id !== selectedId);
    selectedId = controls[0]?.id || null;
    syncEditorForm();
    saveControls();
    requestDraw();
  }

  function duplicateSelectedControl() {
    const control = controls.find(item => item.id === selectedId);
    if (!control) return;
    const copy = JSON.parse(JSON.stringify(control));
    copy.id = control.type + '-' + Date.now().toString(36) + Math.random().toString(36).slice(2, 5);
    copy.x = clamp(control.x + 0.07, 0.08, 0.92);
    copy.y = clamp(control.y + 0.07, 0.1, 0.9);
    controls.push(copy);
    selectedId = copy.id;
    syncEditorForm();
    saveControls();
    requestDraw();
  }

  function resetControls() {
    if (!confirm('恢复默认虚拟按键布局？当前配置将被覆盖。')) return;
    controls = JSON.parse(JSON.stringify(DEFAULT_CONTROLS));
    selectedId = controls[0].id;
    saveControls();
    syncEditorForm();
    requestDraw();
  }

  /* ================= persistence ================= */
  function loadControls() {
    const stored = storageGet(CONTROL_STORAGE_KEY, null);
    if (!Array.isArray(stored) || stored.length === 0) return JSON.parse(JSON.stringify(DEFAULT_CONTROLS));
    return stored.map(sanitizeControl).filter(Boolean);
  }

  function sanitizeControl(raw, index) {
    if (!raw || typeof raw !== 'object') return null;
    const type = ['button', 'joystick', 'look'].includes(raw.type) ? raw.type : 'button';
    return {
      id: typeof raw.id === 'string' && raw.id ? raw.id.slice(0, 80) : 'control-' + index,
      type,
      label: String(raw.label || (type === 'button' ? '按键' : type === 'joystick' ? '移动' : '视角')).slice(0, 12),
      x: clamp(Number(raw.x) || 0.5, 0.06, 0.94),
      y: clamp(Number(raw.y) || 0.5, 0.08, 0.92),
      size: clamp(Number(raw.size) || 60, 44, 180),
      shape: ['circle', 'rounded', 'square', 'diamond'].includes(raw.shape) ? raw.shape : 'circle',
      opacity: clamp(Number(raw.opacity) || 0.6, 0.18, 1),
      color: /^#[0-9a-fA-F]{6}$/.test(raw.color || '') ? raw.color : '#72f1d1',
      key: typeof raw.key === 'string' ? raw.key.slice(0, 32) : '',
      mouseButton: ['left', 'right', 'middle'].includes(raw.mouseButton) ? raw.mouseButton : '',
      preset: raw.preset === 'arrows' ? 'arrows' : 'wasd',
      sensitivity: clamp(Number(raw.sensitivity) || 1.2, 0.2, 4)
    };
  }

  function saveControls() {
    storageSet(CONTROL_STORAGE_KEY, controls);
  }

  /* ================= toast / fatal ================= */
  function showToast(message, error) {
    el.toast.textContent = message;
    el.toast.classList.toggle('error', !!error);
    el.toast.classList.add('show');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => el.toast.classList.remove('show'), 3200);
  }

  function showFatal(message) {
    el.fatalText.textContent = message || '未知错误';
    el.fatal.classList.remove('hidden');
  }
})();
