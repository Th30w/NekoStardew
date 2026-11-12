using System.Text.Json.Serialization;

namespace NekoStardew.Agent;

// 字段命名规则：mod 发的是 "机器视角"（stamina / max_stamina / tile / phase / x / y / fullness / petted），
// 插件和面板读的是 "语义视角"（energy / maxEnergy / playerTile / stage / fed / hearts）。
// 两边都保留，做成别名属性，任何一个来源的 JSON 都能吃下，序列化出去也能被任何消费方读。

/// <summary>农场快照。</summary>
public sealed class FarmState
{
    [JsonPropertyName("season")] public string Season { get; set; } = "";
    [JsonPropertyName("day")] public int Day { get; set; }
    [JsonPropertyName("year")] public int Year { get; set; }
    [JsonPropertyName("time")] public string Time { get; set; } = "";
    [JsonPropertyName("weather")] public string Weather { get; set; } = "";
    [JsonPropertyName("money")] public int Money { get; set; }

    [JsonPropertyName("energy")] public float Energy { get; set; }

    [JsonPropertyName("maxEnergy")] public float MaxEnergy { get; set; }

    [JsonPropertyName("location")] public string Location { get; set; } = "";

    [JsonPropertyName("playerTile")] public int[]? PlayerTile { get; set; }

    [JsonPropertyName("mode")] public string Mode { get; set; } = "auto";

    [JsonPropertyName("crops")] public List<CropInfo> Crops { get; set; } = new();

    /// <summary>已锄好、还空着的地块数（mod 侧统计）。</summary>
    [JsonPropertyName("tilled")] public int Tilled { get; set; }

    /// <summary>背包里当季真能种下去的种子堆数（季节不对的种子不算）。</summary>
    [JsonPropertyName("plantable_seeds")] public int PlantableSeeds { get; set; }

    [JsonPropertyName("animals")] public List<AnimalInfo> Animals { get; set; } = new();

    [JsonPropertyName("inventory")] public List<ItemInfo> Inventory { get; set; } = new();

    [JsonPropertyName("notes")] public string? Notes { get; set; }

    // —— mod 侧的旧字段名，只为兼容 ——

    [JsonPropertyName("stamina")] public float Stamina { get => Energy; set => Energy = value; }

    [JsonPropertyName("max_stamina")] public float MaxStamina { get => MaxEnergy; set => MaxEnergy = value; }

    [JsonPropertyName("tile")] public int[]? Tile { get => PlayerTile; set => PlayerTile = value; }
}

public sealed class CropInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    [JsonPropertyName("x")] public int X { get; set; }

    [JsonPropertyName("y")] public int Y { get; set; }

    [JsonPropertyName("phase")] public int Phase { get; set; }

    [JsonPropertyName("daysLeft")] public int? DaysLeft { get; set; }

    [JsonPropertyName("watered")] public bool Watered { get; set; }

    [JsonPropertyName("harvestable")] public bool Harvestable { get; set; }

    [JsonPropertyName("dead")] public bool Dead { get; set; }

    /// <summary>地块坐标，由 x/y 派生；外部传 tile 数组也能反推 x/y。</summary>
    [JsonPropertyName("tile")]
    public int[] Tile
    {
        get => new[] { X, Y };
        set
        {
            if (value is { Length: >= 2 })
            {
                X = value[0];
                Y = value[1];
            }
        }
    }

    /// <summary>生长阶段文案，由 phase / harvestable / dead 派生。</summary>
    [JsonPropertyName("stage")]
    public string Stage => Dead ? "dead" : Harvestable ? "ready" : Phase <= 0 ? "seed" : "growing";
}

public sealed class AnimalInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    [JsonPropertyName("type")] public string Kind { get; set; } = "";

    [JsonPropertyName("house")] public string Building { get; set; } = "";

    [JsonPropertyName("fullness")] public int Fullness { get; set; }

    [JsonPropertyName("petted")] public bool Petted { get; set; }

    [JsonPropertyName("friendship")] public int Friendship { get; set; }

    [JsonPropertyName("happiness")] public int Happiness { get; set; }

    [JsonPropertyName("count")] public int Count { get; set; }

    /// <summary>语义镜像：喂饱没（fullness > 0）。</summary>
    [JsonPropertyName("fed")] public bool Fed { get => Fullness > 0; set => Fullness = value ? 1 : 0; }

    /// <summary>语义镜像：好感心数（好感值 / 100）。</summary>
    [JsonPropertyName("hearts")] public int Hearts { get => Friendship / 100; set => Friendship = value * 100; }

    [JsonPropertyName("wasPet")] public bool WasPet { get => Petted; set => Petted = value; }

    [JsonPropertyName("building")] public string BuildingAlias { get => Building; set => Building = value; }
}

public sealed class ItemInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    [JsonPropertyName("id")] public string Id { get; set; } = "";

    [JsonPropertyName("stack")] public int Stack { get; set; } = 1;

    [JsonPropertyName("count")] public int Count { get => Stack; set => Stack = value; }
}

/// <summary>下发给 mod 的动作原语。op 是有限的枚举，mod 侧按它分派。</summary>
public sealed class FarmAction
{
    [JsonPropertyName("op")] public string Op { get; set; } = "";
    [JsonPropertyName("x")] public int? X { get; set; }
    [JsonPropertyName("y")] public int? Y { get; set; }
    [JsonPropertyName("target")] public string? Target { get; set; }
    [JsonPropertyName("count")] public int? Count { get; set; }
}

public sealed class AgentCommand
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("task_id")] public string? TaskId { get; set; }
    [JsonPropertyName("goal")] public string? Goal { get; set; }
    [JsonPropertyName("actions")] public List<FarmAction>? Actions { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
}

public sealed class InboundFrame
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("task")] public string? Task { get; set; }
    [JsonPropertyName("task_id")] public string? TaskId { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("encoding")] public string? Encoding { get; set; }

    /// <meta>mod 发的农场快照放在 "state" 里。</meta>
    [JsonPropertyName("state")] public FarmState? State { get; set; }

    /// <meta>插件或其它来源可能放在 "data" 里。</meta>
    [JsonPropertyName("data")] public FarmState? Data { get; set; }

    /// <summary>无论哪一路来的快照都从这里取。</summary>
    [JsonIgnore] public FarmState? Snapshot => State ?? Data;
}

public sealed class OutboundFrame
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("task_id")] public string? TaskId { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("encoding")] public string? Encoding { get; set; }
    [JsonPropertyName("data")] public FarmState? Data { get; set; }

    public static OutboundFrame Make(string type, string text) => new() { Type = type, Text = text };
}
