#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
教室一体机管家 · 服务端（Linux / Python 3.9+，仅标准库）

功能与 windows/SmlServer 对齐：
- WSS /ws/agent：设备令牌鉴权、首次接入自动绑定、心跳/在线表、指令下发与 ack
- 教师端 / 与后台 /admin 两个网页（static/ 内为同一套前端）
- /api：登录（Cookie）、设备/指令/策略、一次性密码、卸载/退出审批、后台管理、审计
- HTTPS(443) + HTTP(80) 301 重定向，证书可配置（PEM），缺失时用 openssl 生成自签

数据格式与 Windows 版一致（data/*.json），可直接迁移。

用法：
    python3 sml_server.py            # 读取 config.json（默认 443/80）
    python3 sml_server.py --dev      # 本地调试：8443/8080，数据在 data-dev/
"""

import asyncio
import base64
import hashlib
import hmac
import json
import os
import re
import secrets
import ssl
import struct
import subprocess
import sys
import time
from datetime import datetime
from urllib.parse import urlparse, parse_qs

ROOT = os.path.dirname(os.path.abspath(__file__))
CONFIG_PATH = os.path.join(ROOT, "config.json")
STATIC_DIR = os.path.join(ROOT, "static")

WS_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
MAX_BODY = 4 * 1024 * 1024
MAX_WS_MESSAGE = 4 * 1024 * 1024
COOKIE_NAME = "sml.auth"
PBKDF2_ITERATIONS = 100_000

ROLE_TEACHER = "teacher"
ROLE_ORG_ADMIN = "org_admin"
ROLE_ADMIN = "admin"

DOWNSTREAM = {
    "call.student", "message.show", "pc.lock", "pc.shutdown", "pc.restart",
    "pc.volume", "pc.screen", "policy.update", "client.kick", "ping",
    "otp.sync", "uninstall.grant", "uninstall.deny", "exit.grant", "exit.deny",
}

# ============================================================ 工具

def now_ms():
    return int(time.time() * 1000)


def sha256_hex(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def new_token():
    return base64.urlsafe_b64encode(secrets.token_bytes(32)).decode().rstrip("=")


def hash_password(password):
    salt = secrets.token_bytes(16)
    digest = hashlib.pbkdf2_hmac("sha256", password.encode("utf-8"), salt, PBKDF2_ITERATIONS, 32)
    return base64.b64encode(digest).decode(), base64.b64encode(salt).decode()


def verify_password(password, hash_b64, salt_b64):
    try:
        salt = base64.b64decode(salt_b64)
        expected = base64.b64decode(hash_b64)
        actual = hashlib.pbkdf2_hmac("sha256", password.encode("utf-8"), salt, PBKDF2_ITERATIONS, len(expected))
        return hmac.compare_digest(actual, expected)
    except Exception:
        return False


def token_hash(token):
    return sha256_hex(token)


def token_matches(token, expected_hex):
    return hmac.compare_digest(sha256_hex(token), (expected_hex or "").lower())


def normalize_otp_code(value):
    return "".join(ch for ch in (value or "") if not ch.isspace() and ch != "-").upper()


def hash_otp_code(normalized):
    return sha256_hex(normalized)


def new_otp_plain():
    alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ"
    return "".join(secrets.choice(alphabet) for _ in range(8))


def read_json(path, default):
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except FileNotFoundError:
        return default
    except Exception as exc:
        print(f"[store] 读取 {path} 失败，按空处理: {exc}")
        return default


def write_json_atomic(path, value):
    os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
    temp = path + ".tmp"
    with open(temp, "w", encoding="utf-8") as f:
        json.dump(value, f, ensure_ascii=False, separators=(",", ":"))
    os.replace(temp, path)


def dumps(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def envelope(type_, data=None, envelope_id=None, reply_to=None):
    msg = {"type": type_, "id": envelope_id or secrets.token_hex(6), "ts": now_ms()}
    if reply_to:
        msg["replyTo"] = reply_to
    if data is not None:
        msg["data"] = data
    return msg


def parse_cookies(header):
    result = {}
    for part in (header or "").split(";"):
        if "=" in part:
            key, value = part.split("=", 1)
            result[key.strip()] = value.strip()
    return result


# ============================================================ 存储

class Store:
    FILES = {
        "organizations": "organizations.json",
        "users": "users.json",
        "classrooms": "classrooms.json",
        "devices": "devices.json",
        "otps": "otps.json",
        "otp_usages": "otp-usages.json",
        "approvals": "approvals.json",
    }

    def __init__(self, data_dir, bootstrap):
        self.dir = data_dir
        self.bootstrap = bootstrap
        self.organizations = []
        self.users = []
        self.classrooms = []
        self.devices = []
        self.otps = []
        self.otp_usages = []
        self.approvals = []

    def load(self):
        os.makedirs(self.dir, exist_ok=True)
        for attr, name in self.FILES.items():
            setattr(self, attr, read_json(os.path.join(self.dir, name), []))

        changed = False
        created_admin = False

        if not self.users:
            digest, salt = hash_password(self.bootstrap.get("adminPassword", "Admin@12345"))
            self.users.append({
                "id": secrets.token_hex(6),
                "username": self.bootstrap.get("adminUsername", "admin"),
                "displayName": "系统管理员",
                "passwordHash": digest,
                "passwordSalt": salt,
                "role": ROLE_ADMIN,
                "organizationId": "",
                "classroomIds": [],
                "enabled": True,
                "createdAt": now_ms(),
                "lastLoginAt": 0,
            })
            created_admin = True
            changed = True

        if not self.organizations:
            self.organizations.append({
                "id": "org-default",
                "name": "默认组织",
                "note": "首次启动自动创建，可在后台改名",
                "createdAt": now_ms(),
            })
            changed = True

        # 迁移：历史教室没有组织归属时，归入默认组织
        for classroom in self.classrooms:
            if not classroom.get("organizationId"):
                classroom["organizationId"] = "org-default"
                changed = True

        if not self.classrooms:
            self.classrooms.append({
                "id": "cls-default",
                "name": "默认教室",
                "note": "首次启动自动创建，可在后台改名",
                "organizationId": "org-default",
                "createdAt": now_ms(),
            })
            changed = True

        # 迁移：教师账号按第一个教室补组织归属
        for user in self.users:
            if user.get("role") == ROLE_TEACHER and not user.get("organizationId"):
                first = next((c for c in self.classrooms if c["id"] in (user.get("classroomIds") or [])), None)
                if first and first.get("organizationId"):
                    user["organizationId"] = first["organizationId"]
                    changed = True

        # 迁移：设备按所属教室补组织归属
        for device in self.devices:
            if not device.get("organizationId"):
                classroom = next((c for c in self.classrooms if c["id"] == device.get("classroomId")), None)
                if classroom and classroom.get("organizationId"):
                    device["organizationId"] = classroom["organizationId"]
                    changed = True

        if changed:
            self.save()
        return created_admin

    def save(self):
        for attr, name in self.FILES.items():
            write_json_atomic(os.path.join(self.dir, name), getattr(self, attr))

    # ---- 查询 ----
    def user(self, user_id):
        return next((u for u in self.users if u["id"] == user_id), None)

    def user_by_name(self, username):
        return next((u for u in self.users if u["username"].lower() == username.lower()), None)

    def device(self, device_id):
        return next((d for d in self.devices if d["deviceId"] == device_id), None)

    def organization(self, organization_id):
        return next((o for o in self.organizations if o["id"] == organization_id), None)

    def organization_name(self, organization_id):
        org = self.organization(organization_id)
        return org["name"] if org else organization_id

    def classroom(self, classroom_id):
        return next((c for c in self.classrooms if c["id"] == classroom_id), None)

    def classroom_name(self, classroom_id):
        classroom = self.classroom(classroom_id)
        return classroom["name"] if classroom else classroom_id

    def otp(self, otp_id):
        return next((o for o in self.otps if o["otpId"] == otp_id), None)


def otp_available(record, now=None):
    now = now or now_ms()
    return (not record.get("revoked")) \
        and record.get("usedCount", 0) < max(1, record.get("maxUses", 1)) \
        and (record.get("expiresAt", 0) <= 0 or record.get("expiresAt", 0) > now)


# ============================================================ 审计与事件

class Audit:
    def __init__(self, data_dir):
        self.dir = os.path.join(data_dir, "audit")
        os.makedirs(self.dir, exist_ok=True)

    def write(self, actor, role, action, target, detail, ip):
        entry = {
            "at": now_ms(), "actor": actor, "role": role,
            "action": action, "target": target, "detail": detail or "", "ip": ip or "",
        }
        try:
            path = os.path.join(self.dir, f"audit-{datetime.now():%Y%m%d}.jsonl")
            with open(path, "a", encoding="utf-8") as f:
                f.write(json.dumps(entry, ensure_ascii=False) + "\n")
        except Exception as exc:
            print(f"[audit] 写入失败: {exc}")

    def read(self, day, limit):
        path = os.path.join(self.dir, f"audit-{day:%Y%m%d}.jsonl")
        if not os.path.exists(path):
            return []
        entries = []
        with open(path, "r", encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    entries.append(json.loads(line))
                except Exception:
                    continue
        return list(reversed(entries))[:limit]


class Events:
    def __init__(self, data_dir):
        self.dir = os.path.join(data_dir, "events")
        os.makedirs(self.dir, exist_ok=True)
        self.buffer = []

    def add(self, device_id, type_, kind, message, raw):
        entry = {"at": now_ms(), "deviceId": device_id, "type": type_,
                 "kind": kind or "", "message": message or "", "raw": raw or ""}
        self.buffer.append(entry)
        if len(self.buffer) > 2000:
            del self.buffer[: len(self.buffer) - 2000]
        try:
            path = os.path.join(self.dir, f"events-{datetime.now():%Y%m%d}.jsonl")
            with open(path, "a", encoding="utf-8") as f:
                f.write(json.dumps(entry, ensure_ascii=False) + "\n")
        except Exception:
            pass

    def recent(self, device_id, limit):
        items = [e for e in self.buffer if not device_id or e["deviceId"] == device_id]
        return list(reversed(items))[:limit]


# ============================================================ 连接

class Connection:
    def __init__(self, device_id, reader, writer):
        self.id = secrets.token_hex(4)
        self.device_id = device_id
        self.reader = reader
        self.writer = writer
        self.send_lock = asyncio.Lock()
        self.connected_at = now_ms()
        self.last_seen = now_ms()
        self.runtime = None
        self.approval_request_id = None
        self.pending = {}
        self.closed = False

    async def send_frame(self, opcode, payload):
        if self.closed:
            raise ConnectionError("closed")
        header = bytearray([0x80 | opcode])
        length = len(payload)
        if length < 126:
            header.append(length)
        elif length < 65536:
            header.append(126)
            header += struct.pack("!H", length)
        else:
            header.append(127)
            header += struct.pack("!Q", length)
        async with self.send_lock:
            self.writer.write(bytes(header) + payload)
            await self.writer.drain()

    async def send(self, message):
        await self.send_frame(1, dumps(message))


class Connections:
    def __init__(self):
        self.by_device = {}

    def register(self, conn):
        self.by_device.setdefault(conn.device_id, {})[conn.id] = conn

    def unregister(self, conn):
        group = self.by_device.get(conn.device_id)
        if group:
            group.pop(conn.id, None)
            if not group:
                self.by_device.pop(conn.device_id, None)
        for future in conn.pending.values():
            if not future.done():
                future.set_result({"ok": False, "error": "连接已断开"})
        conn.pending.clear()

    def group(self, device_id):
        return list(self.by_device.get(device_id, {}).values())

    def primary(self, device_id):
        items = [c for c in self.group(device_id) if c.approval_request_id is None and not c.closed]
        if items:
            return max(items, key=lambda c: c.connected_at)
        items = [c for c in self.group(device_id) if not c.closed]
        return max(items, key=lambda c: c.connected_at) if items else None

    def by_request(self, request_id):
        for group in self.by_device.values():
            for conn in group.values():
                if conn.approval_request_id == request_id and not conn.closed:
                    return conn
        return None

    def online(self, device_id):
        return any(not c.closed for c in self.group(device_id))

    def online_count(self):
        return sum(1 for g in self.by_device.values() if any(not c.closed for c in g.values()))

    def all(self):
        for group in self.by_device.values():
            for conn in group.values():
                yield conn

    async def send_command(self, device_id, message, timeout):
        conn = self.primary(device_id)
        if conn is None:
            return {"ok": False, "error": "设备不在线"}

        future = asyncio.get_running_loop().create_future()
        conn.pending[message["id"]] = future
        try:
            await conn.send(message)
            ack = await asyncio.wait_for(future, timeout)
            return {"ok": ack.get("ok", True), "error": ack.get("error"), "note": ack.get("note")}
        except asyncio.TimeoutError:
            return {"ok": False, "error": "等待设备回执超时"}
        except Exception as exc:
            return {"ok": False, "error": str(exc)}
        finally:
            conn.pending.pop(message["id"], None)

    async def send_to_approval(self, request_id, device_id, message):
        conn = self.by_request(request_id) or self.primary(device_id)
        if conn is None:
            return False
        try:
            await conn.send(message)
            return True
        except Exception:
            return False


# ============================================================ WebSocket

async def ws_handshake(reader, writer, headers):
    key = headers.get("sec-websocket-key", "")
    accept = base64.b64encode(
        hashlib.sha1((key + WS_GUID).encode("ascii")).digest()
    ).decode("ascii")
    response = (
        "HTTP/1.1 101 Switching Protocols\r\n"
        "Upgrade: websocket\r\n"
        "Connection: Upgrade\r\n"
        f"Sec-WebSocket-Accept: {accept}\r\n\r\n"
    )
    writer.write(response.encode("ascii"))
    await writer.drain()


async def ws_read_frame(reader):
    head = await reader.readexactly(2)
    b1, b2 = head[0], head[1]
    fin = bool(b1 & 0x80)
    opcode = b1 & 0x0F
    masked = bool(b2 & 0x80)
    length = b2 & 0x7F

    if length == 126:
        length = struct.unpack("!H", await reader.readexactly(2))[0]
    elif length == 127:
        length = struct.unpack("!Q", await reader.readexactly(8))[0]

    if length > MAX_WS_MESSAGE:
        raise ValueError("frame too large")

    mask = await reader.readexactly(4) if masked else None
    payload = await reader.readexactly(length) if length else b""

    if mask and payload:
        n = len(payload)
        repeated = (mask * ((n + 3) // 4))[:n]
        payload = bytes(a ^ b for a, b in zip(payload, repeated))

    return fin, opcode, payload


def ws_frame(opcode, payload):
    header = bytearray([0x80 | opcode])
    length = len(payload)
    if length < 126:
        header.append(length)
    elif length < 65536:
        header.append(126)
        header += struct.pack("!H", length)
    else:
        header.append(127)
        header += struct.pack("!Q", length)
    return bytes(header) + payload


# ============================================================ 设备授权

def authorize_device(store, device_id, token, remote_ip):
    device = store.device(device_id)
    if device and device.get("enabled", True) and token_matches(token, device.get("tokenHash", "")):
        return device

    # 首次接入：后台创建的是注册令牌，用设备自己的 deviceId 完成绑定
    candidate = next(
        (d for d in store.devices
         if not d.get("bound", False) and d.get("enabled", True)
         and token_matches(token, d.get("tokenHash", ""))),
        None,
    )
    if candidate is None:
        return None

    store.devices = [d for d in store.devices if d is candidate or d["deviceId"] != device_id]
    candidate["deviceId"] = device_id
    candidate["bound"] = True
    candidate["lastSeenAt"] = now_ms()
    candidate["lastIp"] = remote_ip or ""
    store.save()
    print(f"[agent] 设备首次接入并绑定: {device_id} ({candidate.get('name')})")
    return candidate


# ============================================================ 设备消息

async def handle_agent_message(message, conn, store, ctx):
    type_ = message.get("type", "")
    data = message.get("data") or {}
    conn.last_seen = now_ms()

    if type_ == "hello":
        device = store.device(conn.device_id)
        if device:
            if data.get("deviceName"):
                device["name"] = str(data["deviceName"]).strip()
            if data.get("classroom"):
                device["classroomLabel"] = str(data["classroom"]).strip()
            device["appVersion"] = data.get("appVersion", "")
            device["osVersion"] = data.get("osVersion", "")
            device["machineUser"] = data.get("userName", "")
            device["lastSeenAt"] = now_ms()
            store.save()

            policy = device.get("policy") or {}
            if policy.get("app") or policy.get("network"):
                await conn.send(envelope("policy.update", {
                    "app": policy.get("app"), "network": policy.get("network"),
                }))

    elif type_ == "heartbeat":
        conn.runtime = {
            "uptimeSeconds": data.get("uptimeSeconds", 0),
            "proxyRunning": bool(data.get("proxyRunning")),
            "appGuardRunning": bool(data.get("appGuardRunning")),
            "tunRunning": bool(data.get("tunRunning")),
            "at": now_ms(),
        }

    elif type_ == "ack":
        reply_to = message.get("replyTo")
        future = conn.pending.get(reply_to) if reply_to else None
        if future and not future.done():
            future.set_result({"ok": data.get("ok", True), "error": data.get("error"), "note": data.get("note")})

    elif type_ == "otp.pull":
        device = store.device(conn.device_id) or {}
        await conn.send(envelope("otp.sync", build_otp_sync(store, conn.device_id, device.get("classroomId", ""))))

    elif type_ == "otp.report":
        apply_otp_report(store, ctx, conn.device_id, data.get("usages", []))

    elif type_ in ("uninstall.request", "exit.request"):
        request_id = message.get("id")
        if not request_id:
            return
        kind = "uninstall" if type_.startswith("uninstall") else "exit"
        conn.approval_request_id = request_id
        store.approvals.append({
            "requestId": request_id,
            "kind": kind,
            "deviceId": conn.device_id,
            "deviceName": data.get("deviceName") or conn.device_id,
            "reason": data.get("reason") or "",
            "requestedAt": now_ms(),
            "expiresAt": now_ms() + 10 * 60 * 1000,
            "status": "pending",
            "decidedBy": "",
            "decidedAt": 0,
            "decisionNote": "",
        })
        store.save()
        ctx.audit.write("device", "device", kind + ".request", conn.device_id,
                        f"requestId={request_id}", "")
        print(f"[agent] {kind} 申请: {conn.device_id} requestId={request_id}")

    elif type_ == "screen.result":
        save_screenshot(ctx, data)

    elif type_ in ("event", "ai.verdict", "student.call.teacher", "message.student",
                   "device.control.request", "policy.state"):
        ctx.events.add(conn.device_id, type_,
                       data.get("kind"), data.get("message") or data.get("reason") or data.get("domain"),
                       json.dumps(data, ensure_ascii=False))


def build_otp_sync(store, device_id, classroom_id, replace=False):
    now = now_ms()
    codes = []
    for record in store.otps:
        if not otp_available(record, now):
            continue
        if record.get("deviceId") and record["deviceId"] != device_id:
            continue
        if record.get("classroomId") and record["classroomId"] != classroom_id:
            continue
        codes.append({
            "otpId": record["otpId"],
            "hash": record["codeHash"],
            "expiresAt": record.get("expiresAt", 0),
            "issuedBy": record.get("issuedBy", ""),
            "note": record.get("note"),
            "maxUses": record.get("maxUses", 1),
        })
    return {"replace": replace, "codes": codes}


def apply_otp_report(store, ctx, device_id, usages):
    if not isinstance(usages, list):
        return
    applied = 0
    for usage in usages:
        usage_id = usage.get("usageId")
        if not usage_id:
            continue
        if any(u["usageId"] == usage_id for u in store.otp_usages):
            continue
        store.otp_usages.append({
            "usageId": usage_id,
            "otpId": usage.get("otpId", ""),
            "deviceId": device_id,
            "action": usage.get("action", ""),
            "result": usage.get("result", "success"),
            "attemptHash": usage.get("attemptHash"),
            "at": usage.get("usedAt") or now_ms(),
        })
        if usage.get("result") == "success" and usage.get("otpId"):
            record = store.otp(usage["otpId"])
            if record:
                record["usedCount"] = record.get("usedCount", 0) + 1
                if record["usedCount"] >= max(1, record.get("maxUses", 1)):
                    record["revoked"] = True
                    record["revokedAt"] = now_ms()
        applied += 1
    if applied:
        store.save()
        ctx.audit.write("device", "device", "otp.report", device_id, f"上报 {applied} 条", "")


def save_screenshot(ctx, data):
    image = data.get("imageBase64")
    if not image:
        return
    try:
        request_id = re.sub(r"[^A-Za-z0-9_-]", "", str(data.get("requestId") or "unknown"))
        path = os.path.join(ctx.store.dir, "screens", (request_id or "unknown") + ".jpg")
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as f:
            f.write(base64.b64decode(image))
        print(f"[agent] 截图已保存 {path}")
    except Exception as exc:
        print(f"[agent] 截图保存失败: {exc}")


async def handle_agent(reader, writer, headers, store, ctx):
    device_id = headers.get("x-device-id", "").strip()
    authorization = headers.get("authorization", "")
    token = authorization[7:].strip() if authorization.lower().startswith("bearer ") else ""

    if not device_id or not token:
        await write_http(writer, 401, "text/plain; charset=utf-8", b"missing credentials")
        return

    remote_ip = writer.get_extra_info("peername", ("", 0))[0]
    device = authorize_device(store, device_id, token, remote_ip)
    if device is None or not device.get("enabled", True):
        print(f"[agent] 鉴权失败 deviceId={device_id} ip={remote_ip}")
        await write_http(writer, 401, "text/plain; charset=utf-8", b"invalid token")
        return

    await ws_handshake(reader, writer, headers)
    conn = Connection(device_id, reader, writer)
    ctx.connections.register(conn)
    if store.device(device_id):
        store.device(device_id)["lastSeenAt"] = now_ms()
        store.save()
    print(f"[agent] 设备上线 {device_id} ({device.get('name')}) ip={remote_ip} conn={conn.id}")

    frag = bytearray()
    try:
        while True:
            fin, opcode, payload = await ws_read_frame(reader)

            if opcode == 0x8:          # close
                break
            if opcode == 0x9:          # ping → pong
                await conn.send_frame(0xA, payload)
                continue
            if opcode == 0xA:
                continue
            if opcode in (0x1, 0x2):
                frag = bytearray(payload)
            elif opcode == 0x0:
                frag.extend(payload)
            else:
                continue

            if not fin:
                continue

            text = frag.decode("utf-8", "replace")
            frag = bytearray()
            try:
                message = json.loads(text)
            except Exception:
                continue

            await handle_agent_message(message, conn, store, ctx)
    except (asyncio.IncompleteReadError, ConnectionResetError, ConnectionError):
        pass
    except Exception as exc:
        print(f"[agent] 连接异常 {device_id}: {exc}")
    finally:
        conn.closed = True
        ctx.connections.unregister(conn)
        current = store.device(device_id)
        if current:
            current["lastSeenAt"] = now_ms()
            store.save()
        try:
            writer.close()
            await writer.wait_closed()
        except Exception:
            pass
        print(f"[agent] 设备下线 {device_id} conn={conn.id}")


# ============================================================ HTTP

async def read_request(reader):
    head = await asyncio.wait_for(reader.readuntil(b"\r\n\r\n"), timeout=15)
    lines = head.decode("utf-8", "replace").split("\r\n")
    parts = lines[0].split(" ")
    if len(parts) < 3:
        raise ValueError("bad request line")
    method, target = parts[0].upper(), parts[1]

    headers = {}
    for line in lines[1:]:
        if ":" in line:
            key, value = line.split(":", 1)
            headers[key.strip().lower()] = value.strip()

    body = b""
    if "content-length" in headers:
        length = int(headers["content-length"])
        if length > MAX_BODY:
            raise ValueError("body too large")
        body = await reader.readexactly(length)

    return method, target, headers, body


async def write_http(writer, status, content_type, payload, extra_headers=None, head_only=False):
    reasons = {200: "OK", 301: "Moved Permanently", 400: "Bad Request", 401: "Unauthorized",
               403: "Forbidden", 404: "Not Found", 500: "Internal Server Error"}
    headers = [
        f"HTTP/1.1 {status} {reasons.get(status, 'OK')}",
        f"Content-Type: {content_type}",
        f"Content-Length: {len(payload)}",
        "Connection: close",
        "Cache-Control: no-store" if content_type.startswith("application/json") else "Cache-Control: no-cache",
    ]
    for key, value in (extra_headers or []):
        headers.append(f"{key}: {value}")
    writer.write(("\r\n".join(headers) + "\r\n\r\n").encode("utf-8"))
    if not head_only:
        writer.write(payload)
    await writer.drain()


def json_response(value, status=200):
    return status, "application/json; charset=utf-8", dumps(value), []


def error_response(message, status=400):
    return json_response({"error": message}, status)


STATIC_TYPES = {
    ".html": "text/html; charset=utf-8",
    ".css": "text/css; charset=utf-8",
    ".js": "application/javascript; charset=utf-8",
    ".png": "image/png",
    ".jpg": "image/jpeg",
    ".svg": "image/svg+xml",
    ".ico": "image/x-icon",
}


def serve_static(path):
    name = "index.html" if path in ("/", "") else path.lstrip("/")
    if name == "admin":
        name = "admin.html"
    full = os.path.realpath(os.path.join(STATIC_DIR, name))
    if not full.startswith(os.path.realpath(STATIC_DIR) + os.sep) or not os.path.isfile(full):
        return error_response("not found", 404)
    ext = os.path.splitext(full)[1].lower()
    with open(full, "rb") as f:
        data = f.read()
    return 200, STATIC_TYPES.get(ext, "application/octet-stream"), data, []


# ============================================================ 认证与权限

def current_user(ctx, headers):
    # 同时支持：Web 的 Cookie，以及小程序/客户端的 Authorization: Bearer <token>
    token = None
    authorization = headers.get("authorization", "")
    if authorization.lower().startswith("bearer "):
        token = authorization[7:].strip()

    if not token:
        token = parse_cookies(headers.get("cookie", "")).get(COOKIE_NAME)

    if not token:
        return None
    user_id = ctx.sessions.get(token)
    if not user_id:
        return None
    user = ctx.store.user(user_id)
    if user is None or not user.get("enabled", True):
        return None
    return user


def is_admin(user):
    return user and user.get("role") == ROLE_ADMIN


def is_org_admin(user):
    return user and user.get("role") == ROLE_ORG_ADMIN


def can_access(user, device):
    if is_admin(user):
        return True
    if is_org_admin(user):
        return bool(user.get("organizationId")) and device.get("organizationId") == user["organizationId"]
    return bool(device.get("classroomId")) and device["classroomId"] in (user.get("classroomIds") or [])


def can_see_otp(user, record, store):
    if is_admin(user):
        return True

    classroom_id = record.get("classroomId") or ""
    if is_org_admin(user):
        if not classroom_id:
            return False
        classroom = store.classroom(classroom_id)
        return bool(classroom) and classroom.get("organizationId") == user.get("organizationId")

    return not classroom_id or classroom_id in (user.get("classroomIds") or [])


def can_manage_classroom(actor, classroom, store):
    if is_admin(actor):
        return True
    if not is_org_admin(actor):
        return False
    return bool(classroom) and classroom.get("organizationId") == actor.get("organizationId")


def can_manage_user(actor, target, store):
    if is_admin(actor):
        return True
    if not is_org_admin(actor):
        return False

    org = actor.get("organizationId") or ""
    if not org:
        return False

    if target.get("organizationId") == org:
        return True

    org_rooms = {c["id"] for c in store.classrooms if c.get("organizationId") == org}
    return bool(org_rooms & set(target.get("classroomIds") or []))


def resolve_scope(user, classroom_id, store=None):
    cid = (classroom_id or "").strip()

    if is_admin(user):
        return cid, None

    if is_org_admin(user):
        org = user.get("organizationId") or ""
        if not org:
            return cid, "账号未分配组织"
        if cid:
            classroom = store.classroom(cid) if store else None
            if not classroom or classroom.get("organizationId") != org:
                return cid, "无权操作该教室"
            return cid, None

        rooms = [c for c in store.classrooms if c.get("organizationId") == org] if store else []
        if len(rooms) == 1:
            return rooms[0]["id"], None
        return cid, "请指定教室"

    if cid:
        return (cid, None) if cid in (user.get("classroomIds") or []) else (cid, "无权操作该教室")
    if len(user.get("classroomIds") or []) == 1:
        return user["classroomIds"][0], None
    return cid, "请指定教室"


def public_user(user, store=None):
    organization_id = user.get("organizationId") or ""
    return {
        "id": user["id"],
        "username": user["username"],
        "displayName": user.get("displayName") or user["username"],
        "role": user.get("role"),
        "organizationId": organization_id,
        "organizationName": store.organization_name(organization_id) if (store and organization_id) else "",
        "classroomIds": user.get("classroomIds") or [],
        "lastLoginAt": user.get("lastLoginAt", 0),
    }


# ============================================================ 业务辅助

def device_dto(store, ctx, device, online_override=None):
    conn = ctx.connections.primary(device["deviceId"])
    online = ctx.connections.online(device["deviceId"]) if online_override is None else online_override
    return {
        "deviceId": device["deviceId"],
        "name": device.get("name") or device["deviceId"],
        "classroomId": device.get("classroomId", ""),
        "classroomName": store.classroom_name(device.get("classroomId", "")),
        "classroomLabel": device.get("classroomLabel", ""),
        "enabled": device.get("enabled", True),
        "bound": device.get("bound", False),
        "online": online,
        "lastSeenAt": conn.last_seen if online and conn else device.get("lastSeenAt", 0),
        "lastIp": (conn.writer.get_extra_info("peername", ("", 0))[0] if online and conn else device.get("lastIp", "")),
        "appVersion": device.get("appVersion", ""),
        "machineUser": device.get("machineUser", ""),
        "runtime": (conn.runtime if online and conn else device.get("runtime")),
    }


def normalize_list(items):
    result = []
    seen = set()
    for item in items or []:
        value = str(item).strip().lower()
        if value and value not in seen:
            seen.add(value)
            result.append(value)
    return result


def normalize_policy(payload):
    app = payload.get("app")
    network = payload.get("network")
    if isinstance(app, dict):
        app["blacklist"] = normalize_list(app.get("blacklist"))
        app["whitelist"] = normalize_list(app.get("whitelist"))
        app["scanIntervalSeconds"] = max(1, min(60, int(app.get("scanIntervalSeconds", 2) or 2)))
        app.setdefault("allowSystemProcesses", True)
    if isinstance(network, dict):
        for key in ("domainBlacklist", "domainWhitelist", "ipBlacklist", "ipWhitelist"):
            network[key] = normalize_list(network.get(key))
        network.setdefault("showBlockPage", True)
    return {"app": app, "network": network}


def build_command(payload):
    type_ = payload.get("type")
    data = payload.get("data") or {}
    if type_ == "call.student":
        body = {
            "text": str(data.get("text") or "请到讲台来一下")[:60],
            "teacherName": data.get("teacherName"),
            "durationSeconds": min(600, max(1, int(data.get("durationSeconds", 30) or 30))),
            "playSound": bool(data.get("playSound", True)),
            "speak": bool(data.get("speak", True)),
        }
        return envelope("call.student", body), None, None
    if type_ == "message.show":
        text = str(data.get("text") or "").strip()
        if not text:
            return None, None, "通知内容不能为空"
        body = {
            "title": str(data.get("title") or "教师通知")[:40],
            "text": text[:300],
            "level": data.get("level") if data.get("level") in ("info", "warning", "urgent") else "info",
            "durationSeconds": min(300, max(0, int(data.get("durationSeconds", 0) or 0))),
        }
        return envelope("message.show", body), None, None
    if type_ in ("pc.lock", "pc.shutdown", "pc.restart"):
        body = {
            "delaySeconds": min(300, max(0, int(data.get("delaySeconds", 0) or 0))),
            "reason": data.get("reason"),
        }
        return envelope(type_, body), None, None
    if type_ == "pc.volume":
        body = {"percent": min(100, max(0, int(data.get("percent", 50) or 0)))}
        return envelope("pc.volume", body), None, None
    if type_ == "pc.screen":
        request_id = secrets.token_hex(6)
        body = {
            "requestId": request_id,
            "maxWidth": min(3840, max(320, int(data.get("maxWidth", 1600) or 1600))),
            "quality": min(95, max(30, int(data.get("quality", 70) or 70))),
        }
        return envelope("pc.screen", body), request_id, None
    return None, None, "不支持的指令类型"


# ============================================================ 路由

async def route(method, target, headers, body, ctx):
    parsed = urlparse(target)
    path = parsed.path
    query = {k: v[0] for k, v in parse_qs(parsed.query).items()}

    # ---- 健康检查与静态页 ----
    if method == "GET" and path == "/api/health":
        return json_response({"ok": True, "at": now_ms(), "version": "1.0.0"})

    if method == "GET" and not path.startswith("/api/"):
        return serve_static(path)

    # ---- 认证 ----
    if path == "/api/auth/login" and method == "POST":
        return handle_login(body, ctx, headers)

    if path == "/api/auth/logout" and method == "POST":
        token = parse_cookies(headers.get("cookie", "")).get(COOKIE_NAME)
        ctx.sessions.pop(token, None)
        return 200, "application/json; charset=utf-8", dumps({"ok": True}), [
            (f"Set-Cookie", f"{COOKIE_NAME}=; Path=/; HttpOnly; Secure; SameSite=Lax; Max-Age=0")
        ]

    user = current_user(ctx, headers)
    if path.startswith("/api/") and user is None:
        return error_response("未登录", 401)

    if path == "/api/auth/me" and method == "GET":
        return json_response(public_user(user, store))

    store = ctx.store

    # ---- 教师端：教室与设备 ----
    if path == "/api/classrooms" and method == "GET":
        if is_admin(user):
            items = store.classrooms
        elif is_org_admin(user):
            items = [c for c in store.classrooms if c.get("organizationId") == user.get("organizationId")]
        else:
            items = [c for c in store.classrooms if c["id"] in (user.get("classroomIds") or [])]
        return json_response(items)

    if path == "/api/devices" and method == "GET":
        items = [d for d in store.devices if can_access(user, d)]
        items.sort(key=lambda d: (d.get("name") or "").lower())
        return json_response([device_dto(store, ctx, d) for d in items])

    match = re.fullmatch(r"/api/devices/([^/]+)", path)
    if match and method == "GET":
        device = store.device(match.group(1))
        if not device or not can_access(user, device):
            return error_response("设备不存在", 404)
        dto = device_dto(store, ctx, device)
        dto["policy"] = device.get("policy") or {}
        dto["tokenHint"] = device.get("tokenHint", "")
        dto["osVersion"] = device.get("osVersion", "")
        return json_response(dto)

    match = re.fullmatch(r"/api/devices/([^/]+)/events", path)
    if match and method == "GET":
        device = store.device(match.group(1))
        if not device or not can_access(user, device):
            return error_response("设备不存在", 404)
        limit = int(query.get("limit", "50"))
        return json_response(ctx.events.recent(device["deviceId"], max(1, min(500, limit))))

    match = re.fullmatch(r"/api/devices/([^/]+)/command", path)
    if match and method == "POST":
        return await handle_command(match.group(1), body, user, ctx)

    match = re.fullmatch(r"/api/devices/([^/]+)/policy", path)
    if match and method == "POST":
        return await handle_policy(match.group(1), body, user, ctx)

    match = re.fullmatch(r"/api/screens/([^/]+)", path)
    if match and method == "GET":
        request_id = re.sub(r"[^A-Za-z0-9_-]", "", match.group(1))
        file_path = os.path.join(store.dir, "screens", (request_id or "unknown") + ".jpg")
        if not os.path.isfile(file_path):
            return error_response("截图不存在或尚未上传", 404)
        with open(file_path, "rb") as f:
            return 200, "image/jpeg", f.read(), []

    # ---- 一次性密码 ----
    if path == "/api/otp" and method == "GET":
        scope, error = resolve_scope(user, query.get("classroomId"), store)
        if error:
            return error_response(error)
        device_id = query.get("deviceId", "")
        active_only = query.get("activeOnly", "false").lower() == "true"
        items = []
        for record in store.otps:
            if device_id and record.get("deviceId") != device_id:
                continue
            if scope and record.get("classroomId") and record["classroomId"] != scope:
                continue
            if not can_see_otp(user, record, store):
                continue
            if active_only and not otp_available(record):
                continue
            items.append(otp_dto(record))
        items.sort(key=lambda o: o["issuedAt"], reverse=True)
        return json_response(items[:200])

    if path == "/api/otp" and method == "POST":
        return await handle_create_otp(body, user, ctx)

    match = re.fullmatch(r"/api/otp/([^/]+)", path)
    if match and method == "DELETE":
        record = store.otp(match.group(1))
        if not record:
            return error_response("不存在", 404)
        if not can_see_otp(user, record, store):
            return error_response("无权操作", 403)
        record["revoked"] = True
        record["revokedAt"] = now_ms()
        store.save()
        ctx.audit.write(user["username"], user.get("role"), "otp.revoke", record["otpId"], "吊销", "")
        return json_response({"ok": True})

    if path == "/api/otp/usages" and method == "GET":
        device_id = query.get("deviceId", "")
        limit = max(1, min(500, int(query.get("limit", "100"))))
        items = [u for u in store.otp_usages if not device_id or u["deviceId"] == device_id]
        if not is_admin(user):
            allowed = {d["deviceId"] for d in store.devices if can_access(user, d)}
            items = [u for u in items if u["deviceId"] in allowed]
        items.sort(key=lambda u: u["at"], reverse=True)
        return json_response(items[:limit])

    # ---- 审批 ----
    if path == "/api/approvals" and method == "GET":
        status = query.get("status", "")
        device_id = query.get("deviceId", "")
        for record in store.approvals:
            if record["status"] == "pending" and record.get("expiresAt", 0) and record["expiresAt"] <= now_ms():
                record["status"] = "expired"
                record["decidedAt"] = now_ms()
        allowed = {d["deviceId"] for d in store.devices if can_access(user, d)}
        items = [
            a for a in store.approvals
            if (not status or a["status"] == status)
            and (not device_id or a["deviceId"] == device_id)
            and (is_admin(user) or a["deviceId"] in allowed)
        ]
        items.sort(key=lambda a: a["requestedAt"], reverse=True)
        return json_response(items[:100])

    match = re.fullmatch(r"/api/approvals/([^/]+)/(approve|deny)", path)
    if match and method == "POST":
        return await handle_approval(match.group(1), match.group(2) == "approve", body, user, ctx)

    # ---- 后台 ----
    if path.startswith("/api/admin/") and not (is_admin(user) or is_org_admin(user)):
        return error_response("需要管理员权限", 403)

    # ---- 组织（仅全局 admin） ----
    if path == "/api/admin/organizations" and method == "GET":
        if not is_admin(user):
            return error_response("需要全局管理员权限", 403)

        items = []
        for org in store.organizations:
            rooms = [c for c in store.classrooms if c.get("organizationId") == org["id"]]
            devices = [d for d in store.devices if d.get("organizationId") == org["id"]]
            members = [u for u in store.users if u.get("organizationId") == org["id"]]
            items.append({
                **org,
                "classroomCount": len(rooms),
                "deviceCount": len(devices),
                "onlineCount": sum(1 for d in devices if ctx.connections.online(d["deviceId"])),
                "userCount": len(members),
            })
        return json_response(items)

    if path == "/api/admin/organizations" and method == "POST":
        if not is_admin(user):
            return error_response("需要全局管理员权限", 403)

        payload = parse_body(body)
        name = str(payload.get("name") or "").strip()
        if not name:
            return error_response("请填写组织名称")

        admin_username = str(payload.get("adminUsername") or "").strip()
        admin_password = str(payload.get("adminPassword") or "")

        if admin_password and len(admin_password) < 8:
            return error_response("管理员密码至少 8 位")
        if admin_username and store.user_by_name(admin_username):
            return error_response("管理员用户名已存在")

        org_id = "org-" + secrets.token_hex(3)

        if not admin_username:
            base = "org_" + org_id.split("-")[-1]
            admin_username = base
            suffix = 1
            while store.user_by_name(admin_username):
                suffix += 1
                admin_username = f"{base}{suffix}"

        if not admin_password:
            admin_password = secrets.token_urlsafe(9)[:10]

        digest, salt = hash_password(admin_password)
        account = {
            "id": secrets.token_hex(6),
            "username": admin_username,
            "displayName": name + "管理员",
            "passwordHash": digest,
            "passwordSalt": salt,
            "role": ROLE_ORG_ADMIN,
            "organizationId": org_id,
            "classroomIds": [],
            "enabled": True,
            "createdAt": now_ms(),
            "lastLoginAt": 0,
        }

        organization = {
            "id": org_id,
            "name": name,
            "note": payload.get("note") or "",
            "createdAt": now_ms(),
        }

        store.organizations.append(organization)
        store.users.append(account)
        store.save()

        ctx.audit.write(user["username"], user.get("role"), "organization.create", org_id,
                        f"创建组织 {name}，管理员 {admin_username}", "")

        return json_response({
            "organization": organization,
            "admin": {"username": admin_username, "password": admin_password},
            "message": "管理员密码只显示这一次，请转告并尽快修改",
        })

    match = re.fullmatch(r"/api/admin/organizations/([^/]+)", path)
    if match and method == "PATCH":
        if not is_admin(user):
            return error_response("需要全局管理员权限", 403)
        payload = parse_body(body)
        org = store.organization(match.group(1))
        if not org:
            return error_response("组织不存在", 404)
        if payload.get("name"):
            org["name"] = str(payload["name"]).strip()
        if "note" in payload:
            org["note"] = payload.get("note") or ""
        store.save()
        return json_response({"ok": True})

    if match and method == "DELETE":
        if not is_admin(user):
            return error_response("需要全局管理员权限", 403)
        org_id = match.group(1)
        org = store.organization(org_id)
        if not org:
            return error_response("组织不存在", 404)
        if any(c.get("organizationId") == org_id for c in store.classrooms):
            return error_response("该组织下还有教室，请先删除教室")
        if any(u.get("organizationId") == org_id for u in store.users):
            return error_response("该组织下还有账号，请先删除账号")
        store.organizations = [o for o in store.organizations if o is not org]
        store.save()
        return json_response({"ok": True})

    # ---- 总览 ----
    if path == "/api/admin/overview" and method == "GET":
        if is_admin(user):
            scope_devices = store.devices
            scope_classrooms = store.classrooms
            scope_users = store.users
        else:
            scope_devices = [d for d in store.devices if d.get("organizationId") == user.get("organizationId")]
            scope_classrooms = [c for c in store.classrooms if c.get("organizationId") == user.get("organizationId")]
            scope_users = [u for u in store.users if u.get("organizationId") == user.get("organizationId")]

        scope_device_ids = {d["deviceId"] for d in scope_devices}
        available = sum(1 for o in store.otps if otp_available(o) and can_see_otp(user, o, store))
        pending = sum(1 for a in store.approvals
                      if a["status"] == "pending" and a["deviceId"] in scope_device_ids)

        return json_response({
            "onlineDevices": sum(1 for d in scope_devices if ctx.connections.online(d["deviceId"])),
            "totalDevices": len(scope_devices),
            "users": len(scope_users),
            "classrooms": len(scope_classrooms),
            "organizations": len(store.organizations) if is_admin(user) else 1,
            "pendingApprovals": pending,
            "availableOtp": available,
            "httpsPort": ctx.config["httpsPort"],
            "httpPort": ctx.config["httpPort"],
            "dataDir": store.dir,
            "startedAt": ctx.started_at,
            "version": "1.0.0",
            "certificate": {
                "subject": ctx.cert_subject,
                "thumbprint": "-",
                "notAfter": ctx.cert_not_after,
                "selfSigned": ctx.cert_self_signed,
                "source": ctx.cert_source,
            },
        })

    if path == "/api/admin/devices" and method == "GET":
        items = [d for d in store.devices
                 if is_admin(user) or d.get("organizationId") == user.get("organizationId")]
        items.sort(key=lambda d: (d.get("name") or "").lower())
        return json_response([{
            "deviceId": d["deviceId"],
            "name": d.get("name", ""),
            "organizationId": d.get("organizationId", ""),
            "organizationName": store.organization_name(d.get("organizationId", "")) if d.get("organizationId") else "",
            "classroomId": d.get("classroomId", ""),
            "classroomName": store.classroom_name(d.get("classroomId", "")),
            "enabled": d.get("enabled", True),
            "bound": d.get("bound", False),
            "online": ctx.connections.online(d["deviceId"]),
            "tokenHint": d.get("tokenHint", ""),
            "appVersion": d.get("appVersion", ""),
            "lastSeenAt": d.get("lastSeenAt", 0),
            "lastIp": d.get("lastIp", ""),
            "createdAt": d.get("createdAt", 0),
        } for d in items])

    if path == "/api/admin/devices" and method == "POST":
        return handle_create_device(body, user, ctx)

    match = re.fullmatch(r"/api/admin/devices/([^/]+)/token", path)
    if match and method == "POST":
        device = store.device(match.group(1))
        if not device or not can_access(user, device):
            return error_response("设备不存在", 404)
        token = new_token()
        device["tokenHash"] = token_hash(token)
        device["tokenHint"] = token[:6] + "…"
        device["bound"] = False
        store.save()
        ctx.audit.write(user["username"], user.get("role"), "device.token.reset", device["deviceId"], "重置令牌", "")
        return json_response({"token": token, "message": "旧令牌已失效，请更新一体机配置"})

    match = re.fullmatch(r"/api/admin/devices/([^/]+)/kick", path)
    if match and method == "POST":
        device = store.device(match.group(1))
        if not device or not can_access(user, device):
            return error_response("设备不存在", 404)
        payload = parse_body(body)
        reason = payload.get("reason") or "管理员下线"
        delivered = False
        for conn in ctx.connections.group(device["deviceId"]):
            try:
                await conn.send(envelope("client.kick", {"reason": reason}))
                delivered = True
            except Exception:
                pass
        ctx.audit.write(user["username"], user.get("role"), "device.kick", device["deviceId"], reason, "")
        return json_response({"ok": True, "delivered": delivered})

    match = re.fullmatch(r"/api/admin/devices/([^/]+)", path)
    if match and method == "PATCH":
        payload = parse_body(body)
        device = store.device(match.group(1))
        if not device or not can_access(user, device):
            return error_response("设备不存在", 404)
        if payload.get("name"):
            device["name"] = str(payload["name"]).strip()
        if payload.get("classroomId"):
            cid = str(payload["classroomId"]).strip()
            classroom = store.classroom(cid)
            if not classroom:
                return error_response("教室不存在")
            if not can_manage_classroom(user, classroom, store):
                return error_response("无权操作该教室", 403)
            device["classroomId"] = cid
            device["organizationId"] = classroom.get("organizationId", "")
        if "enabled" in payload:
            device["enabled"] = bool(payload["enabled"])
        store.save()
        return json_response({"ok": True})

    if match and method == "DELETE":
        device = store.device(match.group(1))
        if not device or not can_access(user, device):
            return error_response("设备不存在", 404)
        store.devices = [d for d in store.devices if d is not device]
        store.save()
        return json_response({"ok": True})

    if path == "/api/admin/classrooms" and method == "GET":
        if is_admin(user):
            scope_rooms = store.classrooms
        else:
            scope_rooms = [c for c in store.classrooms if c.get("organizationId") == user.get("organizationId")]

        items = []
        for c in scope_rooms:
            devices = [d for d in store.devices if d.get("classroomId") == c["id"]]
            items.append({
                **c,
                "organizationName": store.organization_name(c.get("organizationId", "")) if c.get("organizationId") else "",
                "deviceCount": len(devices),
                "onlineCount": sum(1 for d in devices if ctx.connections.online(d["deviceId"])),
            })
        return json_response(items)

    if path == "/api/admin/classrooms" and method == "POST":
        payload = parse_body(body)
        name = str(payload.get("name") or "").strip()
        if not name:
            return error_response("请填写教室名称")

        organization_id = str(payload.get("organizationId") or "").strip()
        if is_org_admin(user):
            if organization_id and organization_id != user.get("organizationId"):
                return error_response("无权操作该组织", 403)
            organization_id = user.get("organizationId") or ""
        if not organization_id:
            organization_id = store.organizations[0]["id"] if store.organizations else ""

        if not store.organization(organization_id):
            return error_response("组织不存在")
        if not is_admin(user) and organization_id != user.get("organizationId"):
            return error_response("无权操作该组织", 403)

        record = {
            "id": "cls-" + secrets.token_hex(3),
            "name": name,
            "note": payload.get("note") or "",
            "organizationId": organization_id,
            "createdAt": now_ms(),
        }
        store.classrooms.append(record)
        store.save()
        return json_response(record)

    match = re.fullmatch(r"/api/admin/classrooms/([^/]+)", path)
    if match and method == "DELETE":
        cid = match.group(1)
        classroom = store.classroom(cid)
        if not classroom or not can_manage_classroom(user, classroom, store):
            return error_response("教室不存在", 404)
        if any(d.get("classroomId") == cid for d in store.devices):
            return error_response("该教室下还有设备，请先迁移或删除设备")
        store.classrooms = [c for c in store.classrooms if c is not classroom]
        store.save()
        return json_response({"ok": True})

    if path == "/api/admin/users" and method == "GET":
        items = [u for u in store.users if can_manage_user(user, u, store)]
        return json_response([{
            "id": u["id"], "username": u["username"], "displayName": u.get("displayName", ""),
            "role": u.get("role"),
            "organizationId": u.get("organizationId", ""),
            "organizationName": store.organization_name(u.get("organizationId", "")) if u.get("organizationId") else "",
            "classroomIds": u.get("classroomIds") or [],
            "enabled": u.get("enabled", True), "createdAt": u.get("createdAt", 0),
            "lastLoginAt": u.get("lastLoginAt", 0),
        } for u in items])

    if path == "/api/admin/users" and method == "POST":
        payload = parse_body(body)
        username = str(payload.get("username") or "").strip()
        password = str(payload.get("password") or "")

        if is_admin(user):
            requested = payload.get("role")
            role = requested if requested in (ROLE_ADMIN, ROLE_ORG_ADMIN, ROLE_TEACHER) else ROLE_TEACHER
        else:
            role = ROLE_TEACHER

        if len(username) < 3:
            return error_response("用户名至少 3 位")
        if len(password) < 8:
            return error_response("密码至少 8 位")
        if store.user_by_name(username):
            return error_response("用户名已存在")

        organization_id = str(payload.get("organizationId") or "").strip()
        if is_org_admin(user):
            if organization_id and organization_id != user.get("organizationId"):
                return error_response("无权操作该组织", 403)
            organization_id = user.get("organizationId") or ""
        if role == ROLE_ADMIN:
            organization_id = ""
        if role == ROLE_ORG_ADMIN and not organization_id:
            return error_response("组织管理员必须归属一个组织")
        if organization_id and not store.organization(organization_id):
            return error_response("组织不存在")
        if not is_admin(user) and organization_id != user.get("organizationId"):
            return error_response("无权操作该组织", 403)

        classroom_ids = list(dict.fromkeys(payload.get("classroomIds") or []))
        if role == ROLE_ADMIN:
            classroom_ids = []
        if classroom_ids:
            if is_org_admin(user):
                allowed_rooms = {c["id"] for c in store.classrooms if c.get("organizationId") == organization_id}
                if not set(classroom_ids).issubset(allowed_rooms):
                    return error_response("只能选择本组织的教室", 403)
            elif not all(store.classroom(cid) for cid in classroom_ids):
                return error_response("教室不存在")

        digest, salt = hash_password(password)
        record = {
            "id": secrets.token_hex(6), "username": username,
            "displayName": str(payload.get("displayName") or username).strip(),
            "passwordHash": digest, "passwordSalt": salt, "role": role,
            "organizationId": organization_id,
            "classroomIds": classroom_ids,
            "enabled": True, "createdAt": now_ms(), "lastLoginAt": 0,
        }
        store.users.append(record)
        store.save()
        return json_response({k: record[k] for k in ("id", "username", "displayName", "role", "organizationId")})

    match = re.fullmatch(r"/api/admin/users/([^/]+)/password", path)
    if match and method == "POST":
        payload = parse_body(body)
        password = str(payload.get("password") or "")
        if len(password) < 8:
            return error_response("密码至少 8 位")
        target = store.user(match.group(1))
        if not target or not can_manage_user(user, target, store):
            return error_response("账号不存在", 404)
        if is_org_admin(user) and target.get("role") != ROLE_TEACHER:
            return error_response("组织管理员只能维护教师账号", 403)
        digest, salt = hash_password(password)
        target["passwordHash"] = digest
        target["passwordSalt"] = salt
        store.save()
        return json_response({"ok": True})

    match = re.fullmatch(r"/api/admin/users/([^/]+)", path)
    if match and method == "PATCH":
        payload = parse_body(body)
        target = store.user(match.group(1))
        if not target or not can_manage_user(user, target, store):
            return error_response("账号不存在", 404)
        if is_org_admin(user) and target.get("role") != ROLE_TEACHER:
            return error_response("组织管理员只能维护教师账号", 403)

        if payload.get("displayName"):
            target["displayName"] = str(payload["displayName"]).strip()

        if payload.get("role") in (ROLE_ADMIN, ROLE_ORG_ADMIN, ROLE_TEACHER):
            if is_org_admin(user):
                return error_response("无权修改角色", 403)
            target["role"] = payload["role"]

        if payload.get("organizationId") is not None and is_admin(user):
            org_id = str(payload.get("organizationId") or "").strip()
            if org_id and not store.organization(org_id):
                return error_response("组织不存在")
            target["organizationId"] = org_id

        if isinstance(payload.get("classroomIds"), list):
            classroom_ids = list(dict.fromkeys(payload["classroomIds"]))
            if is_org_admin(user):
                allowed_rooms = {c["id"] for c in store.classrooms
                                 if c.get("organizationId") == user.get("organizationId")}
                if not set(classroom_ids).issubset(allowed_rooms):
                    return error_response("只能选择本组织的教室", 403)
            elif not all(store.classroom(cid) for cid in classroom_ids):
                return error_response("教室不存在")
            target["classroomIds"] = classroom_ids

        if "enabled" in payload:
            target["enabled"] = bool(payload["enabled"])

        store.save()
        return json_response({"ok": True})

    if match and method == "DELETE":
        if match.group(1) == user["id"]:
            return error_response("不能删除当前登录账号")
        target = store.user(match.group(1))
        if not target or not can_manage_user(user, target, store):
            return error_response("账号不存在", 404)
        if is_org_admin(user) and target.get("role") != ROLE_TEACHER:
            return error_response("组织管理员只能维护教师账号", 403)
        store.users = [u for u in store.users if u is not target]
        store.save()
        return json_response({"ok": True})

    if path == "/api/admin/audit" and method == "GET":
        day = datetime.now()
        raw = query.get("date", "")
        if raw:
            try:
                day = datetime.strptime(raw.replace("-", ""), "%Y%m%d")
            except ValueError:
                pass
        limit = max(1, min(1000, int(query.get("limit", "200"))))
        entries = ctx.audit.read(day, limit)
        if not is_admin(user):
            allowed_names = {u["username"] for u in store.users
                             if u.get("organizationId") == user.get("organizationId")}
            entries = [e for e in entries if e.get("actor") in allowed_names]
        return json_response(entries)

    return error_response("not found", 404)


def parse_body(body):
    if not body:
        return {}
    try:
        value = json.loads(body.decode("utf-8"))
        return value if isinstance(value, dict) else {}
    except Exception:
        return {}


def handle_login(body, ctx, headers):
    payload = parse_body(body)
    username = str(payload.get("username") or "").strip()
    password = str(payload.get("password") or "")
    ip = headers.get("x-forwarded-for") or ""

    if not username or not password:
        return error_response("请输入用户名和密码")

    user = ctx.store.user_by_name(username)
    if not user or not user.get("enabled", True) or not verify_password(password, user["passwordHash"], user["passwordSalt"]):
        ctx.audit.write(username, "-", "auth.login.failed", username, "用户名或密码错误", ip)
        return error_response("用户名或密码不正确", 401)

    token = secrets.token_urlsafe(32)
    ctx.sessions[token] = user["id"]
    user["lastLoginAt"] = now_ms()
    ctx.store.save()
    ctx.audit.write(user["username"], user.get("role"), "auth.login", user["username"], None, ip)

    return 200, "application/json; charset=utf-8", dumps({**public_user(user, ctx.store), "token": token}), [
        ("Set-Cookie", f"{COOKIE_NAME}={token}; Path=/; HttpOnly; Secure; SameSite=Lax; Max-Age=604800")
    ]


async def handle_command(device_id, body, user, ctx):
    device = ctx.store.device(device_id)
    if not device or not can_access(user, device):
        return error_response("设备不存在", 404)
    if not device.get("enabled", True):
        return error_response("设备已禁用")

    payload = parse_body(body)
    message, request_id, error = build_command(payload)
    if error:
        return error_response(error)

    timeout = 35 if payload.get("type") == "pc.screen" else 15
    result = await ctx.connections.send_command(device_id, message, timeout)
    ctx.audit.write(user["username"], user.get("role"), "device.command", device_id,
                    f"{payload.get('type')}: {'成功' if result['ok'] else '失败'} {result.get('error') or ''}", "")
    return json_response({
        "ok": result["ok"], "error": result.get("error"),
        "note": result.get("note"), "requestId": request_id,
    })


async def handle_policy(device_id, body, user, ctx):
    device = ctx.store.device(device_id)
    if not device or not can_access(user, device):
        return error_response("设备不存在", 404)

    policy = normalize_policy(parse_body(body))
    device["policy"] = policy
    ctx.store.save()

    delivered = False
    conn = ctx.connections.primary(device_id)
    if conn:
        try:
            await conn.send(envelope("policy.update", {
                "app": policy.get("app"), "network": policy.get("network"),
            }))
            delivered = True
        except Exception:
            pass

    ctx.audit.write(user["username"], user.get("role"), "policy.update", device_id,
                    "策略已保存" + ("并下发" if delivered else "，设备离线未下发"), "")
    return json_response({"ok": True, "delivered": delivered})


def otp_dto(record):
    return {
        "otpId": record["otpId"],
        "classroomId": record.get("classroomId", ""),
        "deviceId": record.get("deviceId", ""),
        "maxUses": record.get("maxUses", 1),
        "usedCount": record.get("usedCount", 0),
        "expiresAt": record.get("expiresAt", 0),
        "note": record.get("note"),
        "issuedBy": record.get("issuedBy", ""),
        "issuedAt": record.get("issuedAt", 0),
        "revoked": record.get("revoked", False),
        "available": otp_available(record),
    }


async def handle_create_otp(body, user, ctx):
    payload = parse_body(body)
    scope, error = resolve_scope(user, payload.get("classroomId"), ctx.store)
    if error:
        return error_response(error)

    device_id = str(payload.get("deviceId") or "").strip()
    if device_id:
        device = ctx.store.device(device_id)
        if not device or not can_access(user, device):
            return error_response("设备不存在或无权操作")

    count = max(1, min(10, int(payload.get("count", 1) or 1)))
    max_uses = max(1, min(10, int(payload.get("maxUses", 1) or 1)))
    valid_minutes = max(5, min(43200, int(payload.get("validMinutes", 4320) or 4320)))
    expires_at = now_ms() + valid_minutes * 60 * 1000
    issuer = user.get("displayName") or user["username"]

    plain_codes = []
    records = []
    for _ in range(count):
        code = new_otp_plain()
        record = {
            "otpId": "otp_" + secrets.token_hex(5),
            "codeHash": hash_otp_code(normalize_otp_code(code)),
            "classroomId": scope,
            "deviceId": device_id,
            "maxUses": max_uses,
            "usedCount": 0,
            "expiresAt": expires_at,
            "note": payload.get("note"),
            "issuedBy": issuer,
            "issuedAt": now_ms(),
            "revoked": False,
            "revokedAt": 0,
        }
        ctx.store.otps.append(record)
        plain_codes.append(code)
        records.append(record)
    ctx.store.save()

    # 推送给受影响的在线设备
    affected = [d for d in ctx.store.devices
                if (not scope or d.get("classroomId") == scope)
                and (not device_id or d["deviceId"] == device_id)]
    for device in affected:
        conn = ctx.connections.primary(device["deviceId"])
        if conn:
            try:
                await conn.send(envelope("otp.sync", build_otp_sync(ctx.store, device["deviceId"], device.get("classroomId", ""))))
            except Exception:
                pass

    ctx.audit.write(user["username"], user.get("role"), "otp.generate", scope or "*",
                    f"生成 {count} 个，每个可用 {max_uses} 次", "")
    return json_response({
        "codes": plain_codes,
        "otpIds": [r["otpId"] for r in records],
        "expiresAt": expires_at,
        "maxUses": max_uses,
    })


async def handle_approval(request_id, approve, body, user, ctx):
    record = next((a for a in ctx.store.approvals if a["requestId"] == request_id), None)
    if not record:
        return error_response("审批请求不存在", 404)

    device = ctx.store.device(record["deviceId"])
    if device and not can_access(user, device):
        return error_response("无权处理", 403)

    if record["status"] != "pending":
        return error_response("该申请已处理或已过期")
    if record.get("expiresAt", 0) and record["expiresAt"] <= now_ms():
        record["status"] = "expired"
        record["decidedAt"] = now_ms()
        ctx.store.save()
        return error_response("该申请已过期")

    payload = parse_body(body)
    note = payload.get("note") or ""
    teacher = user.get("displayName") or user["username"]

    record["status"] = "approved" if approve else "denied"
    record["decidedBy"] = teacher
    record["decidedAt"] = now_ms()
    record["decisionNote"] = note
    ctx.store.save()

    expires_at = now_ms() + 90 * 1000
    if record["kind"] == "uninstall":
        message = envelope("uninstall.grant" if approve else "uninstall.deny",
                           {"requestId": request_id, "teacherId": user["id"], "teacherName": teacher,
                            "expiresAt": expires_at} if approve else {"requestId": request_id, "reason": note})
    else:
        message = envelope("exit.grant" if approve else "exit.deny",
                           {"requestId": request_id, "teacherId": user["id"], "teacherName": teacher,
                            "expiresAt": expires_at} if approve else {"requestId": request_id, "reason": note})

    delivered = await ctx.connections.send_to_approval(request_id, record["deviceId"], message)
    ctx.audit.write(user["username"], user.get("role"),
                    record["kind"] + (".approve" if approve else ".deny"),
                    record["deviceId"], f"requestId={request_id}", "")
    return json_response({"ok": True, "delivered": delivered})


def handle_create_device(body, user, ctx):
    payload = parse_body(body)
    name = str(payload.get("name") or "").strip()
    classroom_id = str(payload.get("classroomId") or "").strip()
    if not name:
        return error_response("请填写设备名称")
    if not classroom_id:
        return error_response("请选择教室")

    classroom = ctx.store.classroom(classroom_id)
    if not classroom:
        return error_response("教室不存在")
    if not can_manage_classroom(user, classroom, ctx.store):
        return error_response("无权操作该教室", 403)

    device_id = secrets.token_hex(16)
    token = new_token()
    record = {
        "deviceId": device_id,
        "name": name,
        "organizationId": classroom.get("organizationId", ""),
        "classroomId": classroom_id,
        "tokenHash": token_hash(token),
        "tokenHint": token[:6] + "…",
        "enabled": True,
        "bound": False,
        "createdAt": now_ms(),
        "lastSeenAt": 0,
        "lastIp": "",
        "appVersion": "",
        "osVersion": "",
        "machineUser": "",
        "classroomLabel": "",
        "runtime": None,
        "policy": {"app": None, "network": None},
    }
    ctx.store.devices.append(record)
    ctx.store.save()
    ctx.audit.write(user["username"], user.get("role"), "device.create", device_id, f"创建设备 {name}", "")
    return json_response({
        "deviceId": device_id,
        "token": token,
        "tokenHint": record["tokenHint"],
        "message": "令牌只显示这一次，请填入一体机设置页",
    })


# ============================================================ 连接处理

async def handle_https(reader, writer, ctx):
    try:
        method, target, headers, body = await read_request(reader)

        if target.startswith("/ws/agent") and headers.get("upgrade", "").lower() == "websocket":
            await handle_agent(reader, writer, headers, ctx.store, ctx)
            return

        status, content_type, payload, extra = await route(method, target, headers, body, ctx)
        await write_http(writer, status, content_type, payload, extra)
    except asyncio.TimeoutError:
        pass
    except (asyncio.IncompleteReadError, ConnectionResetError):
        pass
    except Exception as exc:
        print(f"[http] 处理失败: {exc}")
        try:
            await write_http(writer, 500, "application/json; charset=utf-8", dumps({"error": "服务器内部错误"}))
        except Exception:
            pass
    finally:
        try:
            writer.close()
            await writer.wait_closed()
        except Exception:
            pass


async def handle_http_redirect(reader, writer, ctx):
    try:
        line = await reader.readline()
        host = ""
        while True:
            header = await reader.readline()
            if header in (b"\r\n", b"\n", b""):
                break
            text = header.decode("latin1")
            if text.lower().startswith("host:"):
                host = text.split(":", 1)[1].strip()
        parts = line.decode("latin1").split()
        target = parts[1] if len(parts) > 1 else "/"
        port = ctx.config["httpsPort"]
        authority = host.split(":")[0] + ("" if port == 443 else f":{port}")
        location = f"https://{authority}{target}"
        await write_http(writer, 301, "text/plain; charset=utf-8", b"",
                         [("Location", location)])
    except Exception:
        pass
    finally:
        try:
            writer.close()
            await writer.wait_closed()
        except Exception:
            pass


async def janitor(ctx):
    while True:
        await asyncio.sleep(15)
        try:
            deadline = now_ms() - ctx.config["heartbeatTimeoutSeconds"] * 1000
            for conn in list(ctx.connections.all()):
                if conn.closed:
                    continue
                if conn.last_seen >= deadline:
                    continue
                print(f"[agent] 心跳超时，断开 {conn.device_id} conn={conn.id}")
                conn.closed = True
                try:
                    conn.writer.close()
                except Exception:
                    pass
                ctx.connections.unregister(conn)
        except Exception as exc:
            print(f"[janitor] 巡检异常: {exc}")


# ============================================================ 配置与证书

def load_config(dev):
    default = {
        "httpsPort": 443, "httpPort": 80, "dataDir": "data",
        "certFile": "certs/server.crt", "keyFile": "certs/server.key",
        "heartbeatTimeoutSeconds": 90,
        "bootstrap": {"adminUsername": "admin", "adminPassword": "Admin@12345"},
    }
    config = dict(default)
    if os.path.exists(CONFIG_PATH):
        try:
            with open(CONFIG_PATH, "r", encoding="utf-8") as f:
                config.update(json.load(f))
        except Exception as exc:
            print(f"[config] 读取失败，使用默认值: {exc}")

    if dev:
        config["httpsPort"] = 8443
        config["httpPort"] = 8080
        config["dataDir"] = "data-dev"

    config["bootstrap"] = {**default["bootstrap"], **(config.get("bootstrap") or {})}
    return config


def ensure_cert(config):
    cert_path = os.path.join(ROOT, config["certFile"])
    key_path = os.path.join(ROOT, config["keyFile"])

    if os.path.exists(cert_path) and os.path.exists(key_path):
        return cert_path, key_path, False

    os.makedirs(os.path.dirname(cert_path), exist_ok=True)
    print(f"[cert] 未找到证书，尝试用 openssl 生成自签证书: {cert_path}")
    command = [
        "openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1095",
        "-keyout", key_path, "-out", cert_path, "-subj", "/CN=SmlApp Server",
        "-addext", "subjectAltName=DNS:localhost,IP:127.0.0.1",
    ]
    try:
        subprocess.run(command, check=True, capture_output=True)
        return cert_path, key_path, True
    except Exception as first_error:
        try:
            command = [c for c in command if c not in ("-addext", "subjectAltName=DNS:localhost,IP:127.0.0.1")]
            subprocess.run(command, check=True, capture_output=True)
            return cert_path, key_path, True
        except Exception as second_error:
            print(f"[cert] 生成失败: {second_error}。请手动放置 PEM 证书并配置 certFile/keyFile。")
            raise SystemExit(1)


# ============================================================ 启动

class Context:
    pass


async def main():
    dev = "--dev" in sys.argv
    config = load_config(dev)
    data_dir = os.path.join(ROOT, config["dataDir"])
    os.makedirs(data_dir, exist_ok=True)

    store = Store(data_dir, config["bootstrap"])
    created_admin = store.load()

    ctx = Context()
    ctx.config = config
    ctx.store = store
    ctx.audit = Audit(data_dir)
    ctx.events = Events(data_dir)
    ctx.connections = Connections()
    ctx.sessions = {}
    ctx.started_at = now_ms()
    ctx.cert_source = "self-signed"
    ctx.cert_self_signed = True
    ctx.cert_subject = "CN=SmlApp Server"
    ctx.cert_not_after = now_ms() + 3 * 365 * 24 * 3600 * 1000

    cert_path, key_path, generated = ensure_cert(config)
    if not generated:
        ctx.cert_source = "pem"
        ctx.cert_self_signed = False

    ssl_context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ssl_context.load_cert_chain(cert_path, key_path)

    https_server = await asyncio.start_server(
        lambda r, w: handle_https(r, w, ctx),
        host="0.0.0.0", port=config["httpsPort"], ssl=ssl_context, limit=64 * 1024,
    )

    http_server = None
    if config["httpPort"] and config["httpPort"] != config["httpsPort"]:
        http_server = await asyncio.start_server(
            lambda r, w: handle_http_redirect(r, w, ctx),
            host="0.0.0.0", port=config["httpPort"], limit=64 * 1024,
        )

    print("=" * 60)
    print("教室一体机管家 · 服务端（Linux / Python）")
    print(f"HTTPS : 0.0.0.0:{config['httpsPort']}")
    print(f"HTTP  : 0.0.0.0:{config['httpPort']}（301 → HTTPS）")
    print(f"教师端: https://<服务器地址>:{config['httpsPort']}/")
    print(f"后  台: https://<服务器地址>:{config['httpsPort']}/admin")
    print(f"设  备: wss://<服务器地址>:{config['httpsPort']}/ws/agent")
    print(f"数据目录: {data_dir}")
    print(f"证书: {cert_path}（{'自签' if ctx.cert_self_signed else 'PEM'}）")
    if created_admin:
        print(f"首次启动已创建管理员: {config['bootstrap']['adminUsername']} / {config['bootstrap']['adminPassword']}")
        print("请登录后台后立即修改密码。")
    print("=" * 60)

    asyncio.create_task(janitor(ctx))

    try:
        await asyncio.gather(https_server.serve_forever(),
                             http_server.serve_forever() if http_server else asyncio.sleep(0))
    except asyncio.CancelledError:
        pass
    finally:
        https_server.close()
        if http_server:
            http_server.close()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\n已停止")
    except PermissionError:
        print("监听 443/80 需要 root 权限（或用 CAP_NET_BIND_SERVICE），调试请用 --dev")
        sys.exit(1)
