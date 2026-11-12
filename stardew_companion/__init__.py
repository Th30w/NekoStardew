"""星露谷猫娘插件入口。

职责只有三件事：生命周期接线、把 stardew_task 注册成 LLM 工具、暴露状态查询入口。
真正的协议与状态逻辑在 client.py / service.py，业务判断由模型和 C# agent 负责。
"""
from __future__ import annotations

import asyncio
import os
from typing import Any, Dict

from plugin.sdk.plugin import (
    NekoPluginBase,
    lifecycle,
    llm_tool,
    neko_plugin,
    plugin_entry,
)

from . import prompts
from .service import StardewService

STARDEW_TASK_SCHEMA: Dict[str, Any] = {
    "type": "object",
    "properties": {
        "task": {
            "type": "string",
            "description": (
                "一句自然语言指令，描述你想让农场里的自己去做的事。"
                "例如「给所有没浇水的作物浇水」「把成熟的防风草收了」「去鸡舍喂鸡」。"
                "必须是具体的动作目标，不要写坐标和内部命令名。"
            ),
        },
        "overwrite": {
            "type": "boolean",
            "description": (
                "true 表示打断当前正在做的事，立刻换成这个新目标。"
                "只有用户明确让你停下/改主意时才用；默认 false。"
            ),
        },
    },
    "required": ["task"],
}

STARDEW_TASK_DESCRIPTION = (
    "给星露谷里的自己派一个具体任务，她会自己去执行。\n\n"
    "这是动作派发工具，不是状态查询工具。状态、截图、日志、完成提示都只是背景信息，"
    "本身永远不构成一个任务。派发前先想清楚：她现在有没有事在做？如果还在做，"
    "默认返回 busy，不要重复派发同一个目标。\n\n"
    "任务来源：只用用户最近一条消息里明确提出的新指令，或者根据当前农场状态做出的一个"
    "确实明显的新动作。绝不把旧指令、旧工具调用、日志、状态提示原样回放成任务。\n\n"
    "当用户明确说「停下」「别浇了」「先收菜」这类纠正时，用 overwrite=true 立刻切过去。"
)


@neko_plugin
class StardewCompanionPlugin(NekoPluginBase):
    def __init__(self, ctx):
        super().__init__(ctx)
        level = (os.environ.get("NEKO_LOG_LEVEL") or "INFO").strip().upper()
        try:
            self.file_logger = self.enable_file_logging(log_level=level)
        except ValueError:
            self.file_logger = self.enable_file_logging(log_level="INFO")
        self.logger = self.file_logger
        self._cfg: Dict[str, Any] = {}
        self._lang: str = prompts.DEFAULT_LANG
        self._service = StardewService(
            logger=self.logger,
            push_message_fn=self.push_message,
            lang=self._lang,
        )
        # SDK 用一次性的 asyncio.run 调 startup，返回即关 loop，所有 create_task 会被取消。
        # 因此真正的启动延后到第一个 entry 调用时，跑在宿主的长驻循环上。
        self._service_lazy_started = False
        self._start_lock = asyncio.Lock()

    async def _ensure_service_started(self, *, wait_s: float = 3.0) -> bool:
        async with self._start_lock:
            if not self._service_lazy_started:
                try:
                    self._service.apply_config(self._cfg.get("stardew") or {})
                    await self._service.start()
                    self._service_lazy_started = True
                    self.logger.info("[lazy-start] stardew service started")
                except Exception as exc:
                    self.logger.warning(
                        "[lazy-start] start failed, will retry: {}: {}",
                        type(exc).__name__, exc,
                    )
                    return False

            import time
            deadline = time.monotonic() + wait_s
            client = self._service.client
            while time.monotonic() < deadline:
                if client is not None and client.is_connected:
                    return True
                await asyncio.sleep(0.05)
            return bool(client is not None and client.is_connected)

    # ------------------------------------------------------------------

    @lifecycle(id="startup")
    async def startup(self, **_):
        try:
            cfg = await self.config.dump(timeout=5.0)
        except Exception:
            cfg = {}
        self._cfg = cfg if isinstance(cfg, dict) else {}
        stardew_cfg = self._cfg.get("stardew") or {}
        self._service.apply_config(stardew_cfg)
        self.logger.info("[startup] config loaded: {}", stardew_cfg)

    @lifecycle(id="shutdown")
    async def shutdown(self, **_):
        if self._service_lazy_started:
            try:
                await self._service.stop()
            except Exception as exc:
                self.logger.warning("[shutdown] stop failed: {}: {}", type(exc).__name__, exc)

    # ------------------------------------------------------------------

    @llm_tool(
        name="stardew_task",
        description=STARDEW_TASK_DESCRIPTION,
        parameters=STARDEW_TASK_SCHEMA,
        timeout=300.0,
    )
    async def stardew_task(self, *, task: Any = None, overwrite: Any = False, **_):
        if not isinstance(task, str) or not task.strip():
            return {"summary": prompts.t("TASK_SCHEMA_ERROR", lang=self._lang)}
        task_text = task.strip()
        overwrite_flag = overwrite is True

        connected = await self._ensure_service_started()
        if not connected:
            return {"summary": prompts.t("TASK_NOT_CONNECTED", lang=self._lang)}

        claimed = await self._service.try_claim_pending(task_text, overwrite=overwrite_flag)
        if claimed is None:
            current = self._service.current_task_text() or "别的事"
            return {
                "summary": prompts.t(
                    "TASK_BUSY_HINT", lang=self._lang, current=current[:60]
                )
            }

        detached = asyncio.create_task(
            self._service.run_claimed_task(claimed),
            name=f"stardew_companion.task:{task_text[:30]}",
        )
        detached.add_done_callback(self._on_task_done)
        return {"summary": prompts.t("TASK_DISPATCHED_ACK", lang=self._lang)}

    def _on_task_done(self, task: asyncio.Task) -> None:
        try:
            result = task.result()
        except asyncio.CancelledError:
            return
        except Exception as exc:
            self.logger.warning("[task] crashed: {}: {}", type(exc).__name__, exc)
            return
        if not isinstance(result, dict):
            return
        status = str(result.get("status") or "")
        text = str(result.get("text") or "")
        if status == "ok":
            body = prompts.t("CUE_DONE_OK", lang=self._lang, text=text)
            behavior = "respond"
        elif status in ("interrupted", "superseded"):
            body = prompts.t("CUE_DONE_NEUTRAL", lang=self._lang, text=text)
            behavior = "read"
        else:
            body = prompts.t("CUE_DONE_FAIL", lang=self._lang, text=text)
            behavior = "respond"
        try:
            self.push_message(
                source="stardew_companion",
                visibility=[],
                ai_behavior=behavior,
                parts=[{"type": "text", "text": body}],
                priority=7,
                coalesce_key="stardew_cue",
            )
        except Exception as exc:
            self.logger.warning("[task] push cue failed: {}: {}", type(exc).__name__, exc)

    # ------------------------------------------------------------------

    @plugin_entry(
        id="stardew_status",
        name="查询星露谷状态",
        description=(
            "查询她在星露谷里的连接状态、当前任务和最近一次农场快照。"
            "当用户问「她那边在干嘛」「农场怎么样」「连上了吗」时调用。"
        ),
    )
    async def stardew_status(self, **_):
        await self._ensure_service_started(wait_s=1.0)
        summary = self._service.build_status_summary()
        return {"state": summary}

    @plugin_entry(
        id="stardew_mode",
        name="切换她的农场节奏",
        description="idle 只陪同不说话；auto 让她自主经营；manual 只等你派任务。",
    )
    async def stardew_mode(self, *, mode: str = "auto", **_):
        mode = str(mode or "auto").strip().lower()
        if mode not in ("idle", "auto", "manual"):
            return {"error": "mode 只能是 idle / auto / manual"}
        connected = await self._ensure_service_started(wait_s=1.0)
        client = self._service.client
        ok = bool(client is not None and await client.set_mode(mode))
        return {"mode": mode, "applied": ok, "connected": connected}


__all__ = ["StardewCompanionPlugin"]
