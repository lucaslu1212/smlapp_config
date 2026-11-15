'use strict';

const $ = id => document.getElementById(id);

let me = null;
let classrooms = [];
let organizations = [];
let plans = [];

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

function stat(label, value) {
  return `<div class="stat"><div class="lbl">${esc(label)}</div><div class="num">${esc(value)}</div></div>`;
}

function kv(label, value) {
  return `<div class="k">${esc(label)}</div><div class="v">${value}</div>`;
}

/* ---------------- 登录 ---------------- */

function showLogin() {
  me = null;
  $('app').hidden = true;
  $('login').hidden = false;
}

async function boot() {
  try {
    me = await api('/api/auth/me');
    if (me.role !== 'admin' && me.role !== 'org_admin') {
      toast('该账号没有后台权限', true);
      showLogin();
      return;
    }

    const isOrgAdmin = me.role === 'org_admin';

    $('login').hidden = true;
    $('app').hidden = false;
    $('currentUser').textContent = `${me.displayName} · ${isOrgAdmin ? '组织管理员' : '全局管理员'}`;
    $('orgBadge').hidden = false;
    $('orgBadge').textContent = isOrgAdmin
      ? `${me.organizationName || '未分配组织'} · ${me.plan ? me.plan.name : '试用版'}`
      : '全局';

    if (isOrgAdmin) {
      const orgTab = document.querySelector('[data-tab="organizations"]');
      if (orgTab) orgTab.style.display = 'none';
      $('newClassroomOrgField').hidden = true;
      $('newUserOrgField').hidden = true;
      $('newUserRole').innerHTML = '<option value="teacher">教师（仅本组织设备）</option>';
      $('newUserRole').disabled = true;
    }

    $('auditDate').value = new Date().toISOString().slice(0, 10);

    if (!isOrgAdmin) await loadOrganizations();
    await loadPlans();
    await loadClassrooms();
    await loadOverview();
    await loadDevices();
    await loadUsers();
    await loadPlanTab();
    if (!isOrgAdmin) await loadKeys();
  } catch (error) {
    if (error.message !== 'AUTH') {
      console.error(error);
      toast(error.message, true);
    }
  }
}

/* ---------------- 总览 ---------------- */

async function loadOverview() {
  const data = await api('/api/admin/overview');

  const stats = [
    stat('在线设备', data.onlineDevices),
    stat('设备总数', data.totalDevices),
    stat('账号', data.users),
    stat('待审批', data.pendingApprovals),
    stat('可用一次性密码', data.availableOtp),
    stat('教室', data.classrooms),
  ];
  if (me && me.role === 'admin') stats.splice(2, 0, stat('组织', data.organizations));
  $('statGrid').innerHTML = stats.join('');

  $('serverBadge').className = 'badge on';
  $('serverBadge').textContent = `HTTPS ${data.httpsPort} · v${data.version}`;

  const cert = data.certificate;
  $('systemInfo').innerHTML = [
    kv('HTTPS 端口', esc(data.httpsPort)),
    kv('HTTP 端口', `${esc(data.httpPort)}（301 → HTTPS）`),
    kv('数据目录', esc(data.dataDir)),
    kv('服务启动时间', esc(fmtTime(new Date(data.startedAt).getTime()))),
    kv('证书主体', esc(cert.subject)),
    kv('证书到期', esc(new Date(cert.notAfter).toLocaleString('zh-CN'))),
    kv('证书来源', cert.selfSigned ? '<span class="badge warn">自签（浏览器会告警）</span>' : '<span class="badge on">已配置 PFX</span>'),
  ].join('');
}

/* ---------------- 组织 ---------------- */

async function loadOrganizations() {
  organizations = await api('/api/admin/organizations');

  const options = organizations.map(o => `<option value="${esc(o.id)}">${esc(o.name)}</option>`).join('');
  if ($('newClassroomOrg')) $('newClassroomOrg').innerHTML = options;
  if ($('newUserOrg')) $('newUserOrg').innerHTML = options;

  const planBadge = o => {
    const plan = o.plan;
    if (!plan || !plan.activated) return '<span class="badge warn">试用</span>';
    if (!plan.active) return '<span class="badge danger">已过期</span>';
    return `<span class="badge on">${esc(plan.name)}</span>`;
  };

  $('orgTable').querySelector('tbody').innerHTML = organizations.length
    ? organizations.map(o => `
      <tr>
        <td>${esc(o.name)}</td>
        <td>${planBadge(o)}</td>
        <td class="mono">${esc(o.id)}</td>
        <td>${o.classroomCount}</td>
        <td>${o.deviceCount}</td>
        <td>${o.onlineCount}</td>
        <td>${o.userCount}</td>
        <td class="nowrap">${fmtTime(o.createdAt)}</td>
        <td class="right nowrap">
          <button class="btn sm" data-activate-org="${esc(o.id)}">激活</button>
          <button class="btn sm danger" data-del-org="${esc(o.id)}">删除</button>
        </td>
      </tr>`).join('')
    : '<tr><td colspan="9" class="muted">暂无组织</td></tr>';

  $('orgTable').querySelectorAll('[data-activate-org]').forEach(button => {
    button.onclick = () => {
      const org = organizations.find(o => o.id === button.dataset.activateOrg);
      if (!org) return;
      const key = window.prompt(`为「${org.name}」输入激活码：\n（形如 SML-XXXX-XXXX-XXXX-XXXX）`);
      if (key) activateOrganization(org.id, key);
    };
  });

  $('orgTable').querySelectorAll('[data-del-org]').forEach(button => {
    button.onclick = async () => {
      if (!confirm('删除该组织？组织下还有教室或账号时会被拒绝。')) return;
      try {
        await api(`/api/admin/organizations/${encodeURIComponent(button.dataset.delOrg)}`, { method: 'DELETE' });
        toast('已删除');
        await loadOrganizations();
        await loadOverview();
      } catch (error) {
        toast(error.message, true);
      }
    };
  });
}

async function createOrganization() {
  const name = ($('newOrgName').value || '').trim();
  if (!name) { toast('请填写组织名称', true); return; }

  const adminUsername = ($('newOrgAdminUser').value || '').trim();
  const adminPassword = ($('newOrgAdminPass').value || '').trim();

  try {
    const result = await api('/api/admin/organizations', {
      method: 'POST',
      body: JSON.stringify({
        name,
        note: ($('newOrgNote').value || '').trim(),
        adminUsername,
        adminPassword,
      }),
    });

    $('newOrgResult').hidden = false;
    $('newOrgResult').innerHTML = `
      <div class="token-box">
        <div class="label">组织已创建，管理员账号（密码只显示这一次）：</div>
        用户名：${esc(result.admin.username)}<br>
        密码：${esc(result.admin.password)}
      </div>`;

    $('newOrgName').value = '';
    $('newOrgNote').value = '';
    $('newOrgAdminUser').value = '';
    $('newOrgAdminPass').value = '';

    await loadOrganizations();
    await loadClassrooms();
    await loadOverview();
    await loadUsers();
  } catch (error) {
    toast(error.message, true);
  }
}

/* ---------------- 套餐与激活码 ---------------- */

async function loadPlans() {
  plans = await api('/api/admin/plans');

  if ($('newKeyPlan')) {
    $('newKeyPlan').innerHTML = plans
      .filter(p => p.code !== 'trial')
      .map(p => `<option value="${esc(p.code)}">${esc(p.name)}（${p.maxDevices} 设备 / ${p.maxClassrooms} 教室）</option>`)
      .join('');
  }

  if ($('planTable')) {
    $('planTable').querySelector('tbody').innerHTML = plans.map(p => `
      <tr>
        <td>${esc(p.name)}</td>
        <td>${p.maxDevices}</td>
        <td>${p.maxClassrooms}</td>
        <td>${p.durationDays ? p.durationDays + ' 天' : '永久'}</td>
        <td>${esc(p.note || '')}</td>
      </tr>`).join('');
  }
}

async function loadPlanTab() {
  if (!me) return;

  const isOrgAdmin = me.role === 'org_admin';
  $('keyAdmin').hidden = !(me.role === 'admin');
  $('activateCard').hidden = !isOrgAdmin;

  if (!isOrgAdmin) {
    $('planCurrentCard').hidden = false;
    $('planOrgName').textContent = '全局管理员';
    $('planStats').innerHTML = '';
    $('planInfo').innerHTML =
      '<div class="k">说明</div><div class="v">全局管理员不受套餐限制；套餐用于各组织，可在「组织」中为组织激活。</div>';
    return;
  }

  const plan = me.plan;
  $('planOrgName').textContent = me.organizationName || '';

  $('planStats').innerHTML = plan ? [
    stat('套餐', plan.activated ? plan.name : '试用版'),
    stat('设备用量', `${plan.usedDevices}/${plan.maxDevices}`),
    stat('教室用量', `${plan.usedClassrooms}/${plan.maxClassrooms}`),
  ].join('') : '';

  let statusText;
  if (!plan || !plan.activated) {
    statusText = '<span class="badge warn">未激活（试用版）</span>';
  } else if (!plan.active) {
    statusText = '<span class="badge danger">已过期</span>';
  } else {
    statusText = '<span class="badge on">生效中</span>';
  }

  $('planInfo').innerHTML = [
    kv('状态', statusText),
    kv('到期时间', plan && plan.expiresAt ? fmtTime(plan.expiresAt) : '—'),
    kv('剩余天数', plan && plan.expiresAt ? plan.remainingDays + ' 天' : '—'),
    kv('设备上限', plan ? plan.maxDevices : '—'),
    kv('教室上限', plan ? plan.maxClassrooms : '—'),
  ].join('');
}

async function loadKeys() {
  if (!me || me.role !== 'admin') return;

  const list = await api('/api/admin/keys');
  const badge = status => ({
    unused: '<span class="badge on">未使用</span>',
    used: '<span class="badge">已使用</span>',
    revoked: '<span class="badge danger">已吊销</span>',
    expired: '<span class="badge warn">已过期</span>',
  }[status] || esc(status));

  $('keyTable').querySelector('tbody').innerHTML = list.length
    ? list.map(k => `
      <tr>
        <td class="mono">${esc(k.keyHint)}</td>
        <td>${esc(k.planName)}</td>
        <td class="nowrap">${k.maxDevices} 设备 / ${k.maxClassrooms} 教室<br><span class="muted">${k.durationDays ? k.durationDays + ' 天' : '永久'}</span></td>
        <td>${badge(k.status)}</td>
        <td class="nowrap">${fmtTime(k.createdAt)}</td>
        <td class="nowrap">${k.usedAt ? fmtTime(k.usedAt) + '<br><span class="muted">' + esc(k.organizationName || k.usedBy) + '</span>' : '—'}</td>
        <td class="nowrap">${k.expiresAt ? fmtTime(k.expiresAt) : '永久'}</td>
        <td class="right">${k.status === 'unused' ? `<button class="btn sm danger" data-revoke-key="${esc(k.keyId)}">吊销</button>` : ''}</td>
      </tr>`).join('')
    : '<tr><td colspan="8" class="muted">暂无激活码</td></tr>';

  $('keyTable').querySelectorAll('[data-revoke-key]').forEach(button => {
    button.onclick = async () => {
      if (!confirm('吊销该激活码？吊销后无法再用于激活。')) return;
      try {
        await api(`/api/admin/keys/${encodeURIComponent(button.dataset.revokeKey)}/revoke`, { method: 'POST' });
        toast('已吊销');
        await loadKeys();
      } catch (error) {
        toast(error.message, true);
      }
    };
  });
}

async function createKeys() {
  const count = Math.max(1, Math.min(50, parseInt($('newKeyCount').value, 10) || 1));
  const planCode = $('newKeyPlan').value;
  const maxDevices = parseInt($('newKeyDevices').value, 10);
  const maxClassrooms = parseInt($('newKeyRooms').value, 10);
  const durationDays = parseInt($('newKeyDuration').value, 10);
  const validDays = parseInt($('newKeyValid').value, 10);
  const note = ($('newKeyNote').value || '').trim();

  try {
    const result = await api('/api/admin/keys', {
      method: 'POST',
      body: JSON.stringify({
        count,
        planCode,
        maxDevices: Number.isFinite(maxDevices) ? maxDevices : undefined,
        maxClassrooms: Number.isFinite(maxClassrooms) ? maxClassrooms : undefined,
        durationDays: Number.isFinite(durationDays) ? durationDays : undefined,
        validDays: Number.isFinite(validDays) ? validDays : undefined,
        note,
      }),
    });

    $('newKeyResult').hidden = false;
    $('newKeyResult').innerHTML = `
      <div class="token-box">
        <div class="label">激活码（只显示这一次，请立即复制保存）：</div>
        ${result.keys.map(k => esc(k.key)).join('<br>')}
      </div>`;

    $('newKeyNote').value = '';
    await loadKeys();
  } catch (error) {
    toast(error.message, true);
  }
}

async function activateOrganization(orgId, keyOverride) {
  const key = (keyOverride || $('activateKey').value || '').trim();
  if (!key) { toast('请输入激活码', true); return; }

  try {
    const result = await api(`/api/admin/organizations/${encodeURIComponent(orgId)}/activate`, {
      method: 'POST',
      body: JSON.stringify({ key }),
    });

    toast(`已激活：${result.plan.name}（剩余 ${result.plan.remainingDays} 天）`);
    $('activateKey').value = '';
    me = await api('/api/auth/me');
    await loadPlanTab();
    await loadOverview();
    if (me.role === 'admin') {
      await loadOrganizations();
      await loadKeys();
    }
  } catch (error) {
    toast(error.message, true);
  }
}

/* ---------------- 教室 ---------------- */

async function loadClassrooms() {
  classrooms = await api('/api/admin/classrooms');

  const labelOf = c => (c.organizationName ? c.organizationName + ' / ' : '') + c.name;
  const options = classrooms.map(c => `<option value="${esc(c.id)}">${esc(labelOf(c))}</option>`).join('');
  $('newDeviceClassroom').innerHTML = options;
  $('newUserClassrooms').innerHTML = options;

  $('classroomTable').querySelector('tbody').innerHTML = classrooms.length
    ? classrooms.map(c => `
      <tr>
        <td>${esc(c.name)}</td>
        <td>${esc(c.organizationName || '—')}</td>
        <td class="mono">${esc(c.id)}</td>
        <td>${c.deviceCount}</td>
        <td>${c.onlineCount}</td>
        <td>${esc(c.note || '')}</td>
        <td class="right"><button class="btn sm danger" data-del-classroom="${esc(c.id)}">删除</button></td>
      </tr>`).join('')
    : '<tr><td colspan="7" class="muted">暂无教室</td></tr>';

  $('classroomTable').querySelectorAll('[data-del-classroom]').forEach(button => {
    button.onclick = async () => {
      if (!confirm('删除该教室？教室下有设备时会被拒绝。')) return;
      try {
        await api(`/api/admin/classrooms/${encodeURIComponent(button.dataset.delClassroom)}`, { method: 'DELETE' });
        toast('已删除');
        await loadClassrooms();
        await loadOverview();
      } catch (error) {
        toast(error.message, true);
      }
    };
  });
}

/* ---------------- 设备 ---------------- */

async function loadDevices() {
  const list = await api('/api/admin/devices');
  $('deviceSummary').textContent = `${list.filter(d => d.online).length}/${list.length} 在线`;

  const body = $('deviceTable').querySelector('tbody');
  body.innerHTML = list.length
    ? list.map(d => `
      <tr>
        <td>${esc(d.name)}</td>
        <td>${esc(d.classroomName)}</td>
        <td>${!d.enabled
          ? '<span class="badge danger">已禁用</span>'
          : !d.bound
            ? '<span class="badge warn">待绑定</span>'
            : d.online ? '<span class="badge on">在线</span>' : '<span class="badge off">离线</span>'}</td>
        <td class="mono">${esc(d.tokenHint || '—')}</td>
        <td class="nowrap">${fmtTime(d.lastSeenAt)}</td>
        <td>${esc(d.appVersion || '—')}</td>
        <td class="right nowrap">
          <button class="btn sm" data-token="${esc(d.deviceId)}">重置令牌</button>
          <button class="btn sm" data-toggle="${esc(d.deviceId)}" data-enabled="${d.enabled}">${d.enabled ? '禁用' : '启用'}</button>
          <button class="btn sm" data-kick="${esc(d.deviceId)}">下线</button>
          <button class="btn sm danger" data-del="${esc(d.deviceId)}">删除</button>
        </td>
      </tr>`).join('')
    : '<tr><td colspan="7" class="muted">暂无设备，请在上方创建</td></tr>';

  body.querySelectorAll('[data-token]').forEach(button => {
    button.onclick = async () => {
      if (!confirm('重置令牌后旧令牌立即失效，需要在一体机上重新填写。继续？')) return;
      try {
        const result = await api(`/api/admin/devices/${encodeURIComponent(button.dataset.token)}/token`, { method: 'POST' });
        $('newDeviceResult').hidden = false;
        $('newDeviceResult').innerHTML = `
          <div class="token-box">
            <div class="label">新令牌（只显示这一次）：</div>
            ${esc(result.token)}
          </div>`;
        await loadDevices();
      } catch (error) {
        toast(error.message, true);
      }
    };
  });

  body.querySelectorAll('[data-toggle]').forEach(button => {
    button.onclick = async () => {
      const enabled = button.dataset.enabled === 'true';
      try {
        await api(`/api/admin/devices/${encodeURIComponent(button.dataset.toggle)}`, {
          method: 'PATCH',
          body: JSON.stringify({ enabled: !enabled }),
        });
        toast(enabled ? '已禁用' : '已启用');
        await loadDevices();
        await loadOverview();
      } catch (error) {
        toast(error.message, true);
      }
    };
  });

  body.querySelectorAll('[data-kick]').forEach(button => {
    button.onclick = async () => {
      if (!confirm('让该设备立即下线？设备会自动重连。')) return;
      try {
        const result = await api(`/api/admin/devices/${encodeURIComponent(button.dataset.kick)}/kick`, {
          method: 'POST', body: JSON.stringify({ reason: '管理员下线' }),
        });
        toast(result.delivered ? '已通知下线' : '设备不在线', !result.delivered);
        await loadDevices();
      } catch (error) {
        toast(error.message, true);
      }
    };
  });

  body.querySelectorAll('[data-del]').forEach(button => {
    button.onclick = async () => {
      if (!confirm('删除该设备？删除后需重新注册并分发令牌。')) return;
      try {
        await api(`/api/admin/devices/${encodeURIComponent(button.dataset.del)}`, { method: 'DELETE' });
        toast('已删除');
        await loadDevices();
        await loadOverview();
      } catch (error) {
        toast(error.message, true);
      }
    };
  });
}

async function createDevice() {
  const name = ($('newDeviceName').value || '').trim();
  const classroomId = $('newDeviceClassroom').value;

  if (!name) { toast('请填写设备名称', true); return; }
  if (!classroomId) { toast('请先创建教室', true); return; }

  try {
    const result = await api('/api/admin/devices', {
      method: 'POST',
      body: JSON.stringify({ name, classroomId }),
    });

    $('newDeviceResult').hidden = false;
    $('newDeviceResult').innerHTML = `
      <div class="token-box">
        <div class="label">设备 ID（记录用）：${esc(result.deviceId)}<br>
        接入令牌（只显示这一次，填入一体机后首次连接会自动绑定）：</div>
        ${esc(result.token)}
      </div>`;

    $('newDeviceName').value = '';
    await loadDevices();
    await loadOverview();
  } catch (error) {
    toast(error.message, true);
  }
}

/* ---------------- 账号 ---------------- */

async function loadUsers() {
  const list = await api('/api/admin/users');
  const classroomMap = Object.fromEntries(classrooms.map(c => [c.id, c.name]));

  const roleBadge = role => {
    if (role === 'admin') return '<span class="badge on">全局管理员</span>';
    if (role === 'org_admin') return '<span class="badge warn">组织管理员</span>';
    return '<span class="badge">教师</span>';
  };

  const body = $('userTable').querySelector('tbody');
  body.innerHTML = list.length
    ? list.map(u => `
      <tr>
        <td>${esc(u.username)}</td>
        <td>${esc(u.displayName || '—')}</td>
        <td>${roleBadge(u.role)}</td>
        <td>${esc(u.organizationName || '—')}</td>
        <td>${esc((u.classroomIds || []).map(id => classroomMap[id] || id).join('、') || '—')}</td>
        <td>${u.enabled ? '正常' : '<span class="badge danger">已禁用</span>'}</td>
        <td class="nowrap">${fmtTime(u.lastLoginAt)}</td>
        <td class="right nowrap">
          <button class="btn sm" data-pass="${esc(u.id)}">重置密码</button>
          <button class="btn sm" data-user-toggle="${esc(u.id)}" data-enabled="${u.enabled}">${u.enabled ? '禁用' : '启用'}</button>
          <button class="btn sm danger" data-user-del="${esc(u.id)}">删除</button>
        </td>
      </tr>`).join('')
    : '<tr><td colspan="8" class="muted">暂无账号</td></tr>';

  body.querySelectorAll('[data-pass]').forEach(button => {
    button.onclick = async () => {
      const password = prompt('输入新密码（至少 8 位）：');
      if (!password) return;
      try {
        await api(`/api/admin/users/${encodeURIComponent(button.dataset.pass)}/password`, {
          method: 'POST', body: JSON.stringify({ password }),
        });
        toast('密码已重置');
      } catch (error) {
        toast(error.message, true);
      }
    };
  });

  body.querySelectorAll('[data-user-toggle]').forEach(button => {
    button.onclick = async () => {
      const enabled = button.dataset.enabled === 'true';
      try {
        await api(`/api/admin/users/${encodeURIComponent(button.dataset.userToggle)}`, {
          method: 'PATCH', body: JSON.stringify({ enabled: !enabled }),
        });
        toast(enabled ? '已禁用' : '已启用');
        await loadUsers();
      } catch (error) {
        toast(error.message, true);
      }
    };
  });

  body.querySelectorAll('[data-user-del]').forEach(button => {
    button.onclick = async () => {
      if (!confirm('删除该账号？')) return;
      try {
        await api(`/api/admin/users/${encodeURIComponent(button.dataset.userDel)}`, { method: 'DELETE' });
        toast('已删除');
        await loadUsers();
        await loadOverview();
      } catch (error) {
        toast(error.message, true);
      }
    };
  });
}

async function createUser() {
  const username = ($('newUserName').value || '').trim();
  const password = $('newUserPass').value || '';
  const displayName = ($('newUserDisplay').value || '').trim();
  const role = $('newUserRole').value;
  const classroomIds = Array.from($('newUserClassrooms').selectedOptions).map(o => o.value);
  const organizationId = $('newUserOrg') ? $('newUserOrg').value : '';

  if (username.length < 3) { toast('用户名至少 3 位', true); return; }
  if (password.length < 8) { toast('密码至少 8 位', true); return; }
  if (role === 'org_admin' && !organizationId) { toast('组织管理员必须选择所属组织', true); return; }

  try {
    await api('/api/admin/users', {
      method: 'POST',
      body: JSON.stringify({ username, password, displayName, role, classroomIds, organizationId }),
    });
    toast('账号已创建');
    $('newUserName').value = '';
    $('newUserPass').value = '';
    $('newUserDisplay').value = '';
    await loadUsers();
    await loadOverview();
  } catch (error) {
    toast(error.message, true);
  }
}

/* ---------------- 审计 ---------------- */

async function loadAudit() {
  const date = $('auditDate').value || new Date().toISOString().slice(0, 10);
  const list = await api(`/api/admin/audit?date=${encodeURIComponent(date)}&limit=300`);

  $('auditTable').querySelector('tbody').innerHTML = list.length
    ? list.map(item => `
      <tr>
        <td class="nowrap">${fmtTime(item.at)}</td>
        <td>${esc(item.actor)}</td>
        <td>${esc(item.role)}</td>
        <td class="mono">${esc(item.action)}</td>
        <td>${esc(item.target)}</td>
        <td>${esc(item.detail || '')}</td>
        <td class="mono">${esc(item.ip || '')}</td>
      </tr>`).join('')
    : '<tr><td colspan="7" class="muted">当天暂无记录</td></tr>';
}

/* ---------------- 绑定 ---------------- */

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

  document.querySelectorAll('.tab').forEach(tab => {
    tab.addEventListener('click', async () => {
      document.querySelectorAll('.tab').forEach(t => t.classList.toggle('active', t === tab));
      const name = tab.dataset.tab;
      document.querySelectorAll('section[id^="tab-"]').forEach(section => {
        section.hidden = section.id !== 'tab-' + name;
      });

      try {
        if (name === 'overview') await loadOverview();
        if (name === 'devices') await loadDevices();
        if (name === 'organizations') await loadOrganizations();
        if (name === 'plan') {
          await loadPlanTab();
          if (me.role === 'admin') await loadKeys();
        }
        if (name === 'classrooms') await loadClassrooms();
        if (name === 'users') { await loadClassrooms(); await loadUsers(); }
        if (name === 'audit') await loadAudit();
      } catch (error) {
        if (error.message !== 'AUTH') toast(error.message, true);
      }
    });
  });

  $('createDeviceBtn').onclick = createDevice;
  $('createOrgBtn').onclick = createOrganization;
  $('createKeyBtn').onclick = createKeys;
  $('activateBtn').onclick = () => {
    if (me && me.role === 'org_admin' && me.organizationId) {
      activateOrganization(me.organizationId);
    } else {
      toast('请在组织列表中为组织激活', true);
    }
  };
  $('createClassroomBtn').onclick = async () => {
    const name = ($('newClassroomName').value || '').trim();
    if (!name) { toast('请填写教室名称', true); return; }
    const organizationId = $('newClassroomOrg') ? $('newClassroomOrg').value : '';
    try {
      await api('/api/admin/classrooms', {
        method: 'POST',
        body: JSON.stringify({
          name,
          note: ($('newClassroomNote').value || '').trim(),
          organizationId,
        }),
      });
      toast('教室已创建');
      $('newClassroomName').value = '';
      $('newClassroomNote').value = '';
      await loadClassrooms();
      await loadOverview();
    } catch (error) {
      toast(error.message, true);
    }
  };

  $('createUserBtn').onclick = createUser;
  $('auditRefreshBtn').onclick = () => loadAudit().catch(error => toast(error.message, true));

  boot();
});
