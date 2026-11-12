"""跨回调的状态层：农场快照缓存、任务占位、主动陪伴循环、消息推送。"""
from __future__ import annotations

import asyncio
import base64
import time
import uuid
from collections import deque
from dataclasses import dataclass, field
from typing import Any, Deque, Dict, List, Optional

from . import prompts
from .client import StardewClient


SEASON_ZH = {
    "spring": "春", "summer": "夏", "fall": "秋", "winter": "冬",
}
WEATHER_ZH = {
    "sunny": "晴", "rain": "雨", "storm": "雷雨", "snow": "雪",
    "windy": "大风", "fog": "雾", "green_rain": "绿雨",
}


@dataclass
class PendingTask:
    """一次已认领、正在等 agent 回执的任务。"""
    task_id: str
    text: str
    future: asyncio.Future = field(default_factory=lambda: asyncio.get_event_loop().create_future())
    started_at: float = field(default_factory=time.monotonic)


class StardewService:
    def __init__(
        self,
        *,
        logger: Any,
        push_message_fn: Any,
        lang: str = prompts.DEFAULT_LANG,
    ):
        self.logger = logger
        self._push_message = push_message_fn
        self._lang = lang

        self._client: Optional[StardewClient] = None
        self._tasks: List[asyncio.Task] = []
        self._running = False

        self._cfg: Dict[str, Any] = {}
        self._state: Dict[str, Any] = {}
        self._state_at: float = 0.0
        self._logs: Deque[str] = deque(maxlen=12)
        self._shots: Deque[bytes] = deque(maxlen=3)

        self._pending: Optional[PendingTask] = None
        self._pending_lock = asyncio.Lock()
        self._last_nudge_at: float = 0.0
        self._last_shot_push_at: float = 0.0

    # ------------------------------------------------------------------
    # 生命周期
    # ------------------------------------------------------------------

    async def start(self) -> None:
        if self._running:
            return
        self._running = True
        uri = str(self._cfg.get("ws_url") or "ws://localhost:48921")
        reconnect = float(self._cfg.get("reconnect_interval_seconds") or 5.0)

        self._client = StardewClient(
            uri,
            on_log=self._on_log,
            on_screenshot=self._on_screenshot,
            on_task_finished=self._on_task_finished,
            on_state=self._on_state,
            on_event=self._on_event,
            reconnect_interval=reconnect,
            logger=self.logger,
        )
        self._tasks.append(asyncio.create_task(self._client.start(), name="stardew.client"))
        self._tasks.append(asyncio.create_task(self._nudge_loop(), name="stardew.nudge"))
        self._log_info("service started, ws={}", uri)

    async def stop(self) -> None:
        self._running = False
        if self._pending is not None and not self._pending.future.done():
            self._pending.future.set_result({"status": "interrupted", "text": "插件停止"})
        self._pending = None
        if self._client is not None:
            await self._client.stop()
        for task in self._tasks:
            task.cancel()
        if self._tasks:
            await asyncio.gather(*self._tasks, return_exceptions=True)
        self._tasks.clear()
        self._log_info("service stopped")

    @property
    def client(self) -> Optional[StardewClient]:
        return self._client

    def apply_config(self, cfg: Dict[str, Any]) -> None:
        self._cfg = dict(cfg or {})

    def set_lang(self, lang: str) -> None:
        self._lang = lang or prompts.DEFAULT_LANG

    # ------------------------------------------------------------------
    # 任务占位
    # ------------------------------------------------------------------

    async def try_claim_pending(self, text: str, *, overwrite: bool = False) -> Optional[PendingTask]:
        async with self._pending_lock:
            if self._pending is not None:
                if not overwrite:
                    return None
                if not self._pending.future.done():
                    self._pending.future.set_result(
                        {"status": "interrupted", "text": "被新的指令替换"}
                    )
                self._pending = None
            pending = PendingTask(task_id=str(uuid.uuid4()), text=text)
            self._pending = pending
            return pending

    def current_task_text(self) -> str:
        return self._pending.text if self._pending is not None else ""

    async def run_claimed_task(self, pending: PendingTask) -> Dict[str, Any]:
        """把已认领的任务发给 agent，等回执或超时。"""
        timeout = float(self._cfg.get("task_timeout_seconds") or 180.0)
        client = self._client
        if client is None or not client.is_connected:
            self._release(pending)
            return {"status": "failed", "text": "agent 未连接"}
        ok = await client.send_task(pending.text, task_id=pending.task_id)
        if not ok:
            self._release(pending)
            return {"status": "failed", "text": "任务发送失败"}
        try:
            result = await asyncio.wait_for(pending.future, timeout=timeout)
        except asyncio.TimeoutError:
            result = {"status": "timeout", "text": f"超过 {int(timeout)} 秒没有回执"}
        except asyncio.CancelledError:
            self._release(pending)
            raise
        finally:
            self._release(pending)
        return result if isinstance(result, dict) else {"status": "ok", "text": ""}

    def _release(self, pending: PendingTask) -> None:
        if self._pending is pending:
            self._pending = None

    # ------------------------------------------------------------------
    # 入站回调
    # ------------------------------------------------------------------

    async def _on_log(self, text: str) -> None:
        if not text:
            return
        self._logs.append(text)
        self._push_text(text, coalesce="stardew_log", priority=3)

    async def _on_screenshot(self, payload: str, encoding: str) -> None:
        try:
            raw = base64.b64decode(payload)
        except Exception:
            return
        if not raw:
            return
        self._shots.append(raw)
        if not self._cfg.get("stream_screenshots_to_llm", True):
            return
        min_interval = float(self._cfg.get("screenshot_stream_min_interval_seconds") or 8.0)
        now = time.monotonic()
        if now - self._last_shot_push_at < min_interval:
            return
        self._last_shot_push_at = now
        mime = "image/png" if encoding == "png" else "image/jpeg"
        try:
            self._push_message(
                source="stardew_companion",
                visibility=[],
                ai_behavior="read",
                parts=[{"type": "image", "data": raw, "mime": mime}],
                priority=3,
                coalesce_key="stardew_screenshot",
            )
        except Exception as exc:
            self._log_warn("push screenshot failed: {}: {}", type(exc).__name__, exc)

    async def _on_state(self, data: Dict[str, Any]) -> None:
        self._state = data
        self._state_at = time.monotonic()

    async def _on_event(self, data: Dict[str, Any]) -> None:
        text = str(data.get("text") or "")
        if not text:
            return
        body = prompts.t("EVENT", lang=self._lang, text=text)
        self._push_text(body, coalesce="stardew_event", priority=8, respond=True)

    async def _on_task_finished(self, data: Dict[str, Any]) -> None:
        task_id = str(data.get("task_id") or "")
        status = str(data.get("status") or "ok")
        text = str(data.get("text") or "")
        pending = self._pending
        # 显式 ID 匹配优先；没有 ID 时按 FIFO 认这一个 pending。
        if pending is not None and task_id and task_id != pending.task_id:
            return
        if pending is not None and not pending.future.done():
            pending.future.set_result({"status": status, "text": text})

    # ------------------------------------------------------------------
    # 主动陪伴
    # ------------------------------------------------------------------

    async def _nudge_loop(self) -> None:
        while self._running:
            try:
                interval = float(self._cfg.get("system_prompt_interval_seconds") or 20.0)
                await asyncio.sleep(max(5.0, interval))
                if not self._running:
                    break
                if self._cfg.get("skip_system_prompt_if_busy", True) and self._pending is not None:
                    continue
                if not self._state:
                    continue
                if time.monotonic() - self._last_nudge_at < interval:
                    continue
                self._last_nudge_at = time.monotonic()
                body = self._build_system_prompt()
                parts: List[Dict[str, Any]] = [{"type": "text", "text": body}]
                for shot in self._shots:
                    parts.append({"type": "image", "data": shot, "mime": "image/jpeg"})
                self._push_message(
                    source="stardew_companion",
                    visibility=[],
                    ai_behavior="respond",
                    parts=parts,
                    priority=6,
                    coalesce_key="stardew_nudge",
                )
            except asyncio.CancelledError:
                break
            except Exception as exc:
                self._log_warn("nudge loop error: {}: {}", type(exc).__name__, exc)
                await asyncio.sleep(5.0)

    def _build_system_prompt(self) -> str:
        state_line = self._describe_state()
        current = self.current_task_text()
        activity = (
            prompts.t("ACTIVITY_RUNNING", lang=self._lang, current=current)
            if current else prompts.t("ACTIVITY_IDLE", lang=self._lang)
        )
        return prompts.t(
            "GAME_SYSTEM",
            lang=self._lang,
            MASTER_NAME="{MASTER_NAME}",
            state=state_line,
            activity=activity,
        )

    def _describe_state(self) -> str:
        s = self._state or {}
        season = SEASON_ZH.get(str(s.get("season") or ""), str(s.get("season") or "?"))
        weather = WEATHER_ZH.get(str(s.get("weather") or ""), str(s.get("weather") or "?"))
        head = prompts.t(
            "STATUS_HEADER",
            lang=self._lang,
            season=season,
            day=s.get("day", "?"),
            time=s.get("time", "?"),
            weather=weather,
            money=s.get("money", "?"),
            energy=s.get("energy", "?"),
            max_energy=s.get("maxEnergy", "?"),
        )
        lines = [head]

        crops = s.get("crops") or []
        if crops:
            parts = []
            for c in crops[:8]:
                name = c.get("name", "?")
                stage = c.get("stage", "?")
                extra = []
                if c.get("daysLeft") is not None:
                    extra.append(f"还有{c.get('daysLeft')}天")
                if c.get("watered") is False:
                    extra.append("没浇水")
                parts.append(f"{name}({stage}{('，' + '，'.join(extra)) if extra else ''})")
            lines.append(prompts.t("STATUS_CROPS", lang=self._lang, crops="、".join(parts)))
        else:
            lines.append(prompts.t("STATUS_NONE_CROPS", lang=self._lang))

        animals = s.get("animals") or []
        if animals:
            parts = []
            for a in animals[:8]:
                name = a.get("name", "?")
                fed = "已喂" if a.get("fed") else "没喂"
                parts.append(f"{name}({fed})")
            lines.append(prompts.t("STATUS_ANIMALS", lang=self._lang, animals="、".join(parts)))
        else:
            lines.append(prompts.t("STATUS_NONE_ANIMALS", lang=self._lang))

        if s.get("notes"):
            lines.append(str(s["notes"]))
        return "\n".join(lines)

    # ------------------------------------------------------------------
    # 推送辅助
    # ------------------------------------------------------------------

    def _push_text(self, text: str, *, coalesce: str, priority: int, respond: bool = False) -> None:
        try:
            self._push_message(
                source="stardew_companion",
                visibility=[],
                ai_behavior="respond" if respond else "read",
                parts=[{"type": "text", "text": text}],
                priority=priority,
                coalesce_key=coalesce,
            )
        except Exception as exc:
            self._log_warn("push text failed: {}: {}", type(exc).__name__, exc)

    def build_status_summary(self) -> Dict[str, Any]:
        """给 plugin entry 用的可读状态。"""
        client = self._client
        return {
            "connected": bool(client is not None and client.is_connected),
            "ws_url": self._cfg.get("ws_url"),
            "has_state": bool(self._state),
            "state_age_seconds": int(time.monotonic() - self._state_at) if self._state_at else None,
            "current_task": self.current_task_text(),
            "cached_screenshots": len(self._shots),
            "cached_logs": len(self._logs),
            "state": self._state,
        }

    # ------------------------------------------------------------------
    # 日志
    # ------------------------------------------------------------------

    def _emit(self, level: str, msg: str, *args: Any) -> None:
        if self.logger is None:
            return
        try:
            getattr(self.logger, level)("[StardewService] " + msg, *args)
        except Exception:
            pass

    def _log_info(self, msg: str, *args: Any) -> None:
        self._emit("info", msg, *args)

    def _log_warn(self, msg: str, *args: Any) -> None:
        self._emit("warning", msg, *args)
