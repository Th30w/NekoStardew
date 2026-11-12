"""stardew-agent 的 WebSocket 客户端。

协议（单连接，JSON 帧）：

  插件 → agent
    {"type": "task",    "task": "<目标>", "task_id": "<uuid>"}
    {"type": "command", "action": "status"}

  agent → 插件
    {"type": "log",           "text": "..."}
    {"type": "screenshot",    "image": "<base64>", "encoding": "jpeg"|"png"}
    {"type": "task_finished", "status": "ok|failed|interrupted|superseded|timeout",
                              "text": "...", "task_id": "<uuid>"}
    {"type": "state",         "data": {...}}   # 结构化农场快照
    {"type": "event",         "kind": "...", "text": "..."}
    {"type": "agent_status",  ...}             # 仅信息，忽略

设计要点：截图可能 1Hz 以上，HTTP 每帧握手会压死延迟，所以用长连接 + 自动重连。
"""
from __future__ import annotations

import asyncio
import json
from typing import Any, Awaitable, Callable, Dict, Optional

import websockets
from websockets import exceptions as ws_exceptions

OnLog = Callable[[str], Awaitable[None]]
OnScreenshot = Callable[[str, str], Awaitable[None]]
OnTaskFinished = Callable[[Dict[str, Any]], Awaitable[None]]
OnState = Callable[[Dict[str, Any]], Awaitable[None]]
OnEvent = Callable[[Dict[str, Any]], Awaitable[None]]


class StardewClient:
    """对 stardew-agent 的持久连接。start() 阻塞到 stop()，自带重连。"""

    def __init__(
        self,
        uri: str,
        *,
        on_log: Optional[OnLog] = None,
        on_screenshot: Optional[OnScreenshot] = None,
        on_task_finished: Optional[OnTaskFinished] = None,
        on_state: Optional[OnState] = None,
        on_event: Optional[OnEvent] = None,
        reconnect_interval: float = 5.0,
        logger: Any = None,
    ):
        self.uri = uri
        self.on_log = on_log
        self.on_screenshot = on_screenshot
        self.on_task_finished = on_task_finished
        self.on_state = on_state
        self.on_event = on_event
        self.reconnect_interval = reconnect_interval
        self.logger = logger

        self._ws: Optional[Any] = None
        self._running = False
        self.is_connected: bool = False
        self._background: set[asyncio.Task] = set()

    # ---------------- lifecycle ----------------

    async def start(self) -> None:
        self._running = True
        while self._running:
            try:
                self._ws = await websockets.connect(
                    self.uri,
                    ping_interval=20,
                    ping_timeout=20,
                    close_timeout=3,
                    max_size=None,  # 截图帧可能几百 KB
                )
                self.is_connected = True
                self._log("connected to {}", self.uri)
                await self._listen()
                self.is_connected = False
                if self._running:
                    try:
                        await asyncio.sleep(self.reconnect_interval)
                    except asyncio.CancelledError:
                        break
            except asyncio.CancelledError:
                break
            except Exception as exc:
                self.is_connected = False
                if self._running:
                    self._warn(
                        "connection failed ({}: {}), retry in {:.1f}s",
                        type(exc).__name__, exc, self.reconnect_interval,
                    )
                    try:
                        await asyncio.sleep(self.reconnect_interval)
                    except asyncio.CancelledError:
                        break

    async def stop(self) -> None:
        self._running = False
        self.is_connected = False
        ws = self._ws
        if ws is not None:
            try:
                await ws.close()
            except Exception:
                pass
        for task in tuple(self._background):
            task.cancel()
        if self._background:
            await asyncio.gather(*self._background, return_exceptions=True)
        self._background.clear()
        self._log("stopped")

    # ---------------- outbound ----------------

    async def send_task(self, task: str, *, task_id: Optional[str] = None) -> bool:
        ws = self._ws
        if ws is None or not self.is_connected:
            self._warn("not connected; drop task: {}", task[:80])
            return False
        payload: Dict[str, Any] = {"type": "task", "task": task}
        if task_id is not None:
            payload["task_id"] = task_id
        try:
            await ws.send(json.dumps(payload))
            self._log("sent task: {}", task[:120])
            return True
        except Exception as exc:
            self._error("send task failed: {}: {}", type(exc).__name__, exc)
            return False

    async def request_status(self) -> bool:
        ws = self._ws
        if ws is None or not self.is_connected:
            return False
        try:
            await ws.send(json.dumps({"type": "command", "action": "status"}))
            return True
        except Exception as exc:
            self._error("status request failed: {}: {}", type(exc).__name__, exc)
            return False

    async def set_mode(self, mode: str) -> bool:
        """mode: idle | auto | manual —— 控制她那边是否自主经营。"""
        ws = self._ws
        if ws is None or not self.is_connected:
            return False
        try:
            await ws.send(json.dumps({"type": "command", "action": "set_mode", "mode": mode}))
            return True
        except Exception as exc:
            self._error("set_mode failed: {}: {}", type(exc).__name__, exc)
            return False

    # ---------------- inbound ----------------

    async def _listen(self) -> None:
        ws = self._ws
        if ws is None:
            return
        try:
            async for raw in ws:
                try:
                    data = json.loads(raw)
                except (json.JSONDecodeError, TypeError):
                    continue
                if not isinstance(data, dict):
                    continue
                msg_type = data.get("type", "")
                try:
                    if msg_type == "log" and self.on_log is not None:
                        text = str(data.get("text") or data.get("message") or "")
                        if text:
                            await self.on_log(text)

                    elif msg_type == "screenshot" and self.on_screenshot is not None:
                        payload = data.get("image") or data.get("data") or ""
                        encoding = str(data.get("encoding") or "").lower()
                        if isinstance(payload, str) and payload:
                            await self.on_screenshot(payload, encoding)

                    elif msg_type == "task_finished" and self.on_task_finished is not None:
                        await self.on_task_finished(data)

                    elif msg_type == "state" and self.on_state is not None:
                        payload = data.get("data")
                        if isinstance(payload, dict):
                            await self.on_state(payload)

                    elif msg_type == "event" and self.on_event is not None:
                        if data.get("text"):
                            await self.on_event(data)

                    elif msg_type == "agent_status":
                        self._debug("agent_status: {}", data)

                except Exception as cb_err:
                    self._error(
                        "callback error type={}: {}: {}",
                        msg_type, type(cb_err).__name__, cb_err,
                    )
        except ws_exceptions.ConnectionClosed:
            self.is_connected = False
            self._log("connection closed")
        except Exception as exc:
            self.is_connected = False
            self._error("listen error: {}: {}", type(exc).__name__, exc)

    # ---------------- logging ----------------
    # SDK 注入的是 loguru 风格 logger；没有就静默，避免日志本身把调用方打断。

    def _emit(self, level: str, msg: str, *args: Any) -> None:
        if self.logger is None:
            return
        try:
            getattr(self.logger, level)("[Stardew] " + msg, *args)
        except Exception:
            pass

    def _log(self, msg: str, *args: Any) -> None:
        self._emit("info", msg, *args)

    def _warn(self, msg: str, *args: Any) -> None:
        self._emit("warning", msg, *args)

    def _error(self, msg: str, *args: Any) -> None:
        self._emit("error", msg, *args)

    def _debug(self, msg: str, *args: Any) -> None:
        self._emit("debug", msg, *args)


__all__ = ["StardewClient", "OnLog", "OnScreenshot", "OnTaskFinished", "OnState", "OnEvent"]
