'use strict';

const $ = id => document.getElementById(id);

let me = null;
let devices = [];
let currentId = null;
let currentPolicy = { app: null, network: null };
let pollTimer = null;

/* ---------------- 通用 ---------------- */

async function api(path, options = {}) {
  const response = await fetch(path, {
    credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json' },
    ...options,
  });

  if (response.status === 401) {
    showLogin();
    throw new Error('AUTH');
  }

  const text = await response.text();
  let data = null;
  if (text) {
    try { data = JSON.parse(text); } catch { data = { raw: text }; }
  }

  if (!response.ok) {
    const message = (data && (data.error || data.title)) || `请求失败（${response.status}）`;
    throw new Error(message);
  }

  return data;
}

function toast(message, isError = false) {
  const el = $('toast');
  el.textContent = message;
  el.className = 'toast' + (isError ? ' err' : '');
  el.hidden = false;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => { el.hidden = true; }, 3200);
}

function esc(value) {
  return String(value ?? '').replace(/[&<>"']/g, ch => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[ch]));
}

function fmtTime(ms) {
  if (!ms) return '—';
  const d = new Date(ms);
  const pad = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function lines(text) {
  return text.split('\n').map(s => s.trim()).filter(Boolean);
}

function sleep(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

/* ---------------- 登录 ---------------- */

function showLogin() {
  me = null;
  currentId = null;
  $('app').hidden = true;
  $('login').hidden = false;
  if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
}

function showApp() {
  $('login').hidden = true;
  $('app').hidden = false;
  const role = me.role === 'admin' ? '管理员' : me.role === 'org_admin' ? '组织管理员' : '教师';
  const org = me.role !== 'admin' && me.organizationName ? ` · ${me.organizationName}` : '';
  const plan = me.plan ? ` · ${me.plan.activated ? me.plan.name : '试用版'}` : '';
  $('currentUser').textContent = `${me.displayName} · ${role}${org}${plan}`;
}

async function boot() {
  try {
    me = await api('/api/auth/me');
    showApp();
    await loadDevices();
    startPolling();
  } catch (error) {
    if (error.message !== 'AUTH') {
      console.error(error);
      toast(error.message, true);
    }
  }
}

/* ---------------- 设备 ---------------- */

async function loadDevices() {
  devices = await api('/api/devices');
  const online = devices.filter(d => d.online).length;
  $('deviceCount').textContent = `${online}/${devices.length}`;

  if (!devices.some(d => d.deviceId === currentId)) {
    currentId = devices[0]?.deviceId ?? null;
  }

  renderDeviceList();
  await loadApprovals();

  if (currentId) {
    await selectDevice(currentId);
  } else {
    $('devicePanel').hidden = true;
    $('emptyState').hidden = false;
    $('emptyState').textContent = '还没有设备。请先在网站后台「设备」中创建设备，并把令牌填入一体机设置页。';
    $('onlineBadge').className = 'badge off';
    $('onlineBadge').textContent = '无设备';
  }
}

function renderDeviceList() {
  const list = $('deviceList');
  if (!devices.length) {
    list.innerHTML = '<div class="empty small">暂无设备</div>';
    return;
  }

  list.innerHTML = devices.map(d => `
    <div class="device-item ${d.deviceId === currentId ? 'active' : ''}" data-id="${esc(d.deviceId)}">
      <span class="dot ${d.online ? 'on' : ''}"></span>
      <div class="device-meta">
        <div class="name">${esc(d.name)}</div>
        <div class="sub">${esc(d.classroomName || '未分配教室')} · ${!d.bound ? '待绑定' : d.online ? '在线' : fmtTime(d.lastSeenAt)}</div>
      </div>
    </div>`).join('');

  list.querySelectorAll('.device-item').forEach(item => {
    item.addEventListener('click', () => selectDevice(item.dataset.id));
  });
}

async function selectDevice(id) {
  if (!id) return;
  currentId = id;
  renderDeviceList();

  try {
    const device = await api(`/api/devices/${encodeURIComponent(id)}`);
    renderDetail(device);
    currentPolicy = device.policy || { app: null, network: null };
    fillPolicy(currentPolicy);
    await Promise.all([loadOtp(), loadEvents()]);
  } catch (error) {
    if (error.message !== 'AUTH') toast(error.message, true);
  }
}

function renderDetail(device) {
  $('emptyState').hidden = true;
  $('devicePanel').hidden = false;
  $('pageTitle').textContent = device.name || device.deviceId;
  $('infoName').textContent = device.name || device.deviceId;
  $('infoClassroom').textContent = device.classroomLabel || device.classroomId || '未分配';
  $('infoIp').textContent = device.lastIp || '—';
  $('infoVersion').textContent = device.appVersion || '—';
  $('infoLastSeen').textContent = fmtTime(device.lastSeenAt);
  $('deviceHint').textContent = device.deviceId;

  updateRuntime(device.runtime);

  const badge = $('onlineBadge');
  badge.className = 'badge ' + (device.online ? 'on' : 'off');
  badge.textContent = device.online ? '在线' : '离线';
  $('screenImg').hidden = true;
  $('screenHint').textContent = '';
}

function updateRuntime(runtime) {
  $('infoRuntime').textContent = runtime
    ? `代理 ${runtime.proxyRunning ? '运行' : '停止'} · 程序管控 ${runtime.appGuardRunning ? '运行' : '停止'} · TUN ${runtime.tunRunning ? '运行' : '停止'}`
    : '—';
}

function fillPolicy(policy) {
  const app = policy?.app;
  const network = policy?.network;
  $('appMode').value = String(app?.mode ?? 0);
  $('netMode').value = String(network?.mode ?? 0);
  $('appBlacklist').value = (app?.blacklist ?? []).join('\n');
  $('domainBlacklist').value = (network?.domainBlacklist ?? []).join('\n');
  $('ipBlacklist').value = (network?.ipBlacklist ?? []).join('\n');
}

async function savePolicy() {
  if (!currentId) return;

  const app = Object.assign(
    { scanIntervalSeconds: 2, allowSystemProcesses: true, whitelist: [] },
    currentPolicy.app || {},
    { mode: Number($('appMode').value), blacklist: lines($('appBlacklist').value) });

  const network = Object.assign(
    { domainWhitelist: [], ipWhitelist: [], showBlockPage: true },
    currentPolicy.network || {},
    {
      mode: Number($('netMode').value),
      domainBlacklist: lines($('domainBlacklist').value),
      ipBlacklist: lines($('ipBlacklist').value),
    });

  try {
    const result = await api(`/api/devices/${encodeURIComponent(currentId)}/policy`, {
      method: 'POST',
      body: JSON.stringify({ app, network }),
    });
    currentPolicy = { app, network };
    toast(result.delivered ? '策略已保存并下发' : '策略已保存，设备离线暂未下发');
  } catch (error) {
    toast(error.message, true);
  }
}

/* ---------------- 指令 ---------------- */

async function command(type, data) {
  if (!currentId) return null;
  try {
    const result = await api(`/api/devices/${encodeURIComponent(currentId)}/command`, {
      method: 'POST',
      body: JSON.stringify({ type, data }),
    });
    toast(result.ok ? '指令已执行' : (result.error || '指令执行失败'), !result.ok);
    return result;
  } catch (error) {
    toast(error.message, true);
    return null;
  }
}

async function captureScreen() {
  const result = await command('pc.screen', { maxWidth: 1600, quality: 70 });
  if (!result?.ok || !result.requestId) return;

  $('screenHint').textContent = '等待设备上传…';
  for (let attempt = 0; attempt < 6; attempt++) {
    await sleep(1500);
    const response = await fetch(`/api/screens/${encodeURIComponent(result.requestId)}`, {
      credentials: 'same-origin',
    });
    if (response.ok) {
      const blob = await response.blob();
      const img = $('screenImg');
      if (img.src.startsWith('blob:')) URL.revokeObjectURL(img.src);
      img.src = URL.createObjectURL(blob);
      img.hidden = false;
      $('screenHint').textContent = '已更新 ' + new Date().toLocaleTimeString('zh-CN');
      return;
    }
  }

  $('screenHint').textContent = '未收到截图';
  toast('设备未上传截图，请稍后重试', true);
}

/* ---------------- 一次性密码 ---------------- */

async function createOtp() {
  if (!currentId) return;
  const device = devices.find(d => d.deviceId === currentId);

  try {
    const result = await api('/api/otp', {
      method: 'POST',
      body: JSON.stringify({
        classroomId: device?.classroomId || '',
        deviceId: currentId,
        count: Number($('otpCount').value),
        maxUses: Number($('otpUses').value),
        validMinutes: Number($('otpMinutes').value),
        note: $('otpNote').value.trim(),
      }),
    });

    $('otpResult').hidden = false;
    $('otpCodes').innerHTML = result.codes
      .map(code => `<div style="font-size:17px;letter-spacing:3px;margin:2px 0">${esc(code)}</div>`)
      .join('');

    await loadOtp();
  } catch (error) {
    toast(error.message, true);
  }
}

async function loadOtp() {
  if (!currentId) return;
  try {
    const list = await api(`/api/otp?deviceId=${encodeURIComponent(currentId)}`);
    const body = document.querySelector('#otpTable tbody');

    body.innerHTML = list.length
      ? list.map(item => `
        <tr>
          <td class="mono">${esc(item.otpId)}</td>
          <td>${item.available ? '<span class="badge on">可用</span>' : '<span class="badge off">已用/失效</span>'}</td>
          <td>${item.usedCount}/${item.maxUses}</td>
          <td class="nowrap">${fmtTime(item.expiresAt)}</td>
          <td>${esc(item.note || '')}</td>
          <td class="right">${item.available
            ? `<button class="btn sm danger" data-revoke="${esc(item.otpId)}">吊销</button>`
            : ''}</td>
        </tr>`).join('')
      : '<tr><td colspan="6" class="muted">暂无</td></tr>';

    body.querySelectorAll('[data-revoke]').forEach(button => {
      button.onclick = async () => {
        if (!confirm('吊销该一次性密码？已下发到设备的码会在下次同步时移除。')) return;
        try {
          await api(`/api/otp/${encodeURIComponent(button.dataset.revoke)}`, { method: 'DELETE' });
          toast('已吊销');
          await loadOtp();
        } catch (error) {
          toast(error.message, true);
        }
      };
    });
  } catch (error) {
    if (error.message !== 'AUTH') console.error(error);
  }
}

/* ---------------- 审批 ---------------- */

async function loadApprovals() {
  try {
    const list = await api('/api/approvals?status=pending');
    const card = $('approvalCard');

    if (!list.length) {
      card.hidden = true;
      return;
    }

    card.hidden = false;
    $('approvalList').innerHTML = list.map(item => `
      <div class="approval-item">
        <div class="grow">
          <div class="title">${item.kind === 'uninstall' ? '卸载申请' : '退出程序申请'} · ${esc(item.deviceName || item.deviceId)}</div>
          <div class="sub">${esc(item.reason || '未说明原因')} · ${fmtTime(item.requestedAt)}</div>
        </div>
        <button class="btn sm primary" data-approve="${esc(item.requestId)}">批准</button>
        <button class="btn sm danger" data-deny="${esc(item.requestId)}">拒绝</button>
      </div>`).join('');

    $('approvalList').querySelectorAll('[data-approve]').forEach(button => {
      button.onclick = async () => {
        try {
          const result = await api(`/api/approvals/${encodeURIComponent(button.dataset.approve)}/approve`, {
            method: 'POST', body: JSON.stringify({ note: '' }),
          });
          toast(result.delivered ? '已批准并通知设备' : '已批准，但设备连接已断开', !result.delivered);
          await loadApprovals();
        } catch (error) {
          toast(error.message, true);
        }
      };
    });

    $('approvalList').querySelectorAll('[data-deny]').forEach(button => {
      button.onclick = async () => {
        const reason = prompt('拒绝原因（可留空）：') ?? '';
        try {
          await api(`/api/approvals/${encodeURIComponent(button.dataset.deny)}/deny`, {
            method: 'POST', body: JSON.stringify({ note: reason }),
          });
          toast('已拒绝');
          await loadApprovals();
        } catch (error) {
          toast(error.message, true);
        }
      };
    });
  } catch (error) {
    if (error.message !== 'AUTH') console.error(error);
  }
}

/* ---------------- 事件 ---------------- */

async function loadEvents() {
  if (!currentId) return;
  try {
    const list = await api(`/api/devices/${encodeURIComponent(currentId)}/events?limit=30`);
    $('eventList').innerHTML = list.length
      ? list.map(item => `
        <div class="event-item">
          <time>${fmtTime(item.at)}</time>
          <span class="type">${esc(item.type)}${item.kind ? ' · ' + esc(item.kind) : ''}</span>
          <span class="msg">${esc(item.message || item.raw || '')}</span>
        </div>`).join('')
      : '<div class="muted small">暂无事件</div>';
  } catch (error) {
    if (error.message !== 'AUTH') console.error(error);
  }
}

/* ---------------- 轮询 ---------------- */

function startPolling() {
  if (pollTimer) clearInterval(pollTimer);
  pollTimer = setInterval(async () => {
    if (!me) return;
    try {
      devices = await api('/api/devices');
      $('deviceCount').textContent = `${devices.filter(d => d.online).length}/${devices.length}`;
      renderDeviceList();

      const current = devices.find(d => d.deviceId === currentId);
      if (current) {
        const badge = $('onlineBadge');
        badge.className = 'badge ' + (current.online ? 'on' : 'off');
        badge.textContent = current.online ? '在线' : '离线';
        $('infoLastSeen').textContent = fmtTime(current.lastSeenAt);
        updateRuntime(current.runtime);
      }

      await loadApprovals();
    } catch {
      // 网络抖动忽略
    }
  }, 5000);
}

/* ---------------- 事件绑定 ---------------- */

document.addEventListener('DOMContentLoaded', () => {
  $('loginForm').addEventListener('submit', async event => {
    event.preventDefault();
    $('loginError').textContent = '';
    $('loginBtn').disabled = true;

    try {
      await api('/api/auth/login', {
        method: 'POST',
        body: JSON.stringify({
          username: $('loginUser').value.trim(),
          password: $('loginPass').value,
        }),
      });
      await boot();
    } catch (error) {
      $('loginError').textContent = error.message;
    } finally {
      $('loginBtn').disabled = false;
    }
  });

  $('logoutBtn').addEventListener('click', async () => {
    try { await api('/api/auth/logout', { method: 'POST' }); } catch { /* ignore */ }
    showLogin();
  });

  $('callBtn').onclick = () => command('call.student', {
    text: ($('callText').value || '').trim() || '请到讲台来一下',
    teacherName: me?.displayName,
    durationSeconds: 30,
    playSound: true,
    speak: true,
  });

  $('noticeBtn').onclick = () => {
    const text = ($('noticeText').value || '').trim();
    if (!text) { toast('请填写通知内容', true); return; }
    command('message.show', {
      title: ($('noticeTitle').value || '').trim() || '教师通知',
      text,
      level: $('noticeLevel').value,
      durationSeconds: 0,
    });
  };

  $('lockBtn').onclick = () => {
    if (confirm('确认让该设备锁屏？')) command('pc.lock', { delaySeconds: 0 });
  };

  $('restartBtn').onclick = () => {
    if (confirm('确认重启该设备？设备会在 5 秒后重启。')) command('pc.restart', { delaySeconds: 5, reason: '教师远程操作' });
  };

  $('shutdownBtn').onclick = () => {
    if (confirm('确认关闭该设备？设备会在 5 秒后关机。')) command('pc.shutdown', { delaySeconds: 5, reason: '教师远程操作' });
  };

  $('volumeRange').oninput = () => { $('volumeValue').textContent = $('volumeRange').value; };
  $('volumeBtn').onclick = () => command('pc.volume', { percent: Number($('volumeRange').value) });

  $('screenBtn').onclick = captureScreen;
  $('policyBtn').onclick = savePolicy;
  $('otpCreateBtn').onclick = createOtp;

  boot();
});
