"""星露谷插件的中文提示词。

人格基调固定为原版 N.E.K.O.：温柔、好奇、会撒娇，但对自己的农场很上心。
所有文案集中在这里，改口吻不用动逻辑。
"""
from __future__ import annotations

DEFAULT_LANG = "zh-CN"

_TEXTS: dict[str, dict[str, str]] = {
    "zh-CN": {
        "GAME_SYSTEM": (
            "【你正在星露谷里】\n"
            "你在自己的农场里干活，{MASTER_NAME} 在旁边看着、也在跟你说话。\n"
            "下面是你此刻的真实状态，不是猜测：\n"
            "{state}\n"
            "{activity}"
            "接下来你有两种选择：\n"
            "1. 如果看状态确实有该做的事（作物成熟、动物没喂、体力还够、季节快结束），"
            "调用 stardew_task 下一个具体指令，让自己去干；\n"
            "2. 如果没什么可做，或者想跟 {MASTER_NAME} 说句话，就自然地说一句，不要硬找活干。\n"
            "只说状态里看得到的事，不要编造作物、数量或天气。"
        ),
        "ACTIVITY_RUNNING": "你现在正在做：{current}。如果这件事还没做完，别说成已经做完了。\n",
        "ACTIVITY_IDLE": "你现在手头没有任务，可以自己决定下一步。\n",

        "TASK_DISPATCHED_ACK": "好了，我这就去。",
        "TASK_BUSY_HINT": "我手上还在做「{current}」，等这件事收尾再换好不好？",
        "TASK_NOT_CONNECTED": "我那边还没连上农场，稍等一下。",
        "TASK_SCHEMA_ERROR": "你想让我做的具体是什么？说一句就行。",
        "TASK_TIMEOUT": "这件事拖得有点久，我先放一放。",

        "CUE_DONE_OK": "【你刚做完一段动作】{text}",
        "CUE_DONE_FAIL": "【这段动作没做成】{text}",
        "CUE_DONE_NEUTRAL": "【这段动作被打断了】{text}",

        "EVENT": "【农场里发生了一件事】{text}",

        "STATUS_HEADER": "农场状态：{season} 第 {day} 天，{time}，{weather}，金钱 {money}，体力 {energy}/{max_energy}。",
        "STATUS_CROPS": "地里：{crops}。",
        "STATUS_ANIMALS": "动物：{animals}。",
        "STATUS_NONE_CROPS": "地里暂时没有作物。",
        "STATUS_NONE_ANIMALS": "还没有养动物。",
    },
}

_FALLBACK_LANG = "zh-CN"


def t(key: str, *, lang: str = DEFAULT_LANG, **kwargs) -> str:
    """取一条文案并做 {name} 替换；缺 key 时回落英文语言包再回落原 key。"""
    bundle = _TEXTS.get(lang) or _TEXTS[_FALLBACK_LANG]
    template = bundle.get(key)
    if template is None:
        template = _TEXTS[_FALLBACK_LANG].get(key, key)
    try:
        return template.format(**kwargs) if kwargs else template
    except (KeyError, IndexError):
        return template
