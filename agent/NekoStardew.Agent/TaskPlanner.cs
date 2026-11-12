namespace NekoStardew.Agent;

public sealed class PlanResult
{
    public bool Ok { get; init; }
    public string Goal { get; init; } = "";
    public List<FarmAction> Actions { get; init; } = new();
    public string? Reason { get; init; }
}

/// <summary>
/// 把一句中文任务拆成动作原语序列。
///
/// 这里刻意不接大模型：任务文本由 N.E.K.O 的对话模型产出，通常已经是
/// "给所有没浇水的作物浇水" 这种明确目标，规则映射足够；真模糊的指令
/// 会返回 Ok=false 并把原因带回去，让模型细化，而不是瞎猜着执行。
/// 动作本身是语义化的（water_crops 而不是一组坐标），具体地块由 mod
/// 在游戏内按当前状态解析，避免坐标过期。
/// </summary>
public sealed class TaskPlanner
{
    private static readonly (string[] Keys, string Goal, FarmAction Action)[] Rules =
    {
        (new[] { "浇水", "浇一下", "浇灌", "补水" }, "给缺水的作物浇水", new FarmAction { Op = "water_crops" }),
        (new[] { "收获", "收了", "收割", "摘", "采收", "收菜", "收一下" }, "收成熟的作物", new FarmAction { Op = "harvest_crops" }),
        (new[] { "播种", "种下", "种菜", "种点" }, "给空地播种", new FarmAction { Op = "plant_seeds" }),
        (new[] { "喂食", "喂一下", "喂动物", "喂鸡", "喂牛", "饲料" }, "喂没吃过的动物", new FarmAction { Op = "feed_animals" }),
        (new[] { "摸", "撸", "爱抚", "亲亲动物", "提高好感" }, "和动物互动", new FarmAction { Op = "pet_animals" }),
        (new[] { "清理", "除草", "砍树", "清石头" }, "清理农场杂物", new FarmAction { Op = "clear_debris" }),
        (new[] { "买种子", "买点种子", "进货", "补种子", "买点菜种" }, "去买当季种子", new FarmAction { Op = "buy_seeds", Count = 300 }),
        (new[] { "卖", "出货", "卖掉" }, "把可出售的东西放进出货箱", new FarmAction { Op = "ship_items" }),
        (new[] { "睡觉", "回家睡", "休息", "结束这一天" }, "回家睡觉结束这天", new FarmAction { Op = "sleep" }),
    };

    private static readonly (string[] Keys, string Target)[] Locations =
    {
        (new[] { "鸡舍" }, "Coop"),
        (new[] { " barn", "牛棚", "畜棚" }, "Barn"),
        (new[] { "温室" }, "Greenhouse"),
        (new[] { "屋子", "回家", "房子" }, "FarmHouse"),
        (new[] { "矿洞", "下矿", "去矿", "挖矿" }, "Mine"),
        (new[] { "镇", "村子" }, "Town"),
        (new[] { "农场", "田", "地里" }, "Farm"),
    };

    public PlanResult Plan(string task, FarmState state)
    {
        var text = (task ?? "").Trim();
        if (text.Length == 0)
            return Fail("任务为空");

        var actions = new List<FarmAction>();
        var goals = new List<string>();

        // 位置意图先摘出来，作为最后一个动作，避免和农活抢顺序。
        string? location = null;
        foreach (var (keys, target) in Locations)
        {
            if (keys.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                location = target;
                break;
            }
        }

        foreach (var (keys, goal, action) in Rules)
        {
            if (keys.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                actions.Add(new FarmAction { Op = action.Op });
                goals.Add(goal);
            }
        }

        // "打理/照顾/看看/随便干点" 这类泛指令：按优先级挑一件真正该做的事。
        if (actions.Count == 0 && IsVague(text))
        {
            var auto = PickMostUrgent(state);
            if (auto is not null)
            {
                actions.Add(auto);
                goals.Add(Describe(auto.Op));
            }
        }

        if (location is not null && actions.Count > 0)
            actions.Add(new FarmAction { Op = "go_to", Target = location });
        else if (location is not null && actions.Count == 0)
        {
            // 纯"去某地"的指令只认农场范围内的地方。矿洞、镇子去了就回不来，
            // 人在那儿锄不动地也浇不了水，这一段托管等于白跑，还让当前地块数归零。
            if (location == "Mine" || location == "Town")
                return Fail("「" + location + "」要出农场，托管时不去，我先留在农场干活");

            actions.Add(new FarmAction { Op = "go_to", Target = location });
            goals.Add($"去{location}");
        }

        if (actions.Count == 0)
            return Fail($"没从「{Trim(text, 24)}」里认出可执行的动作");

        return new PlanResult
        {
            Ok = true,
            Goal = string.Join("，", goals),
            Actions = actions,
        };
    }

    /// <summary>她自主经营时用的决策：按"会毁掉收益的程度"排序，一次只做一件。</summary>
    public FarmAction? PickMostUrgent(FarmState state)
    {
        TryParseTime(state.Time, out var minutes);

        // 该收工先收工。睡觉是唯一会把进度存下来的一步，排在所有农活前面，
        // 否则她会被"还有活要干"一直拖到天黑又清零。
        if (minutes >= 22 * 60)
            return new FarmAction { Op = "sleep" };

        // 傍晚先把能卖的卖掉，换出来的钱才是第二天买种子的本钱。
        if (minutes >= 18 * 60 && HasSellable(state))
            return new FarmAction { Op = "ship_items" };

        if (state.Crops.Any(c => c.Harvestable))
            return new FarmAction { Op = "harvest_crops" };
        if (state.Crops.Any(c => !c.Watered && !c.Harvestable))
            return new FarmAction { Op = "water_crops" };

        // 手里有"这一季真能种"的种子：人不在田里就先去田里，否则在屋里播种无处落地。
        // 注意不能用"有没有叫什么什么种子的东西"判断 —— 夏天拿着防风草种子一定种不下去。
        var hasSeed = state.PlantableSeeds > 0;
        if (hasSeed && !IsOnFarm(state.Location))
            return new FarmAction { Op = "go_to", Target = "Farm" };

        // 到了田里但地还没锄开，先整地
        if (hasSeed && state.Tilled == 0)
            return new FarmAction { Op = "till_soil" };

        // 有锄好的地就播种
        if (hasSeed)
            return new FarmAction { Op = "plant_seeds" };

        // 种子没了但兜里有钱：先锄出至少一格地，再去买。
        // 买种子要靠"这块地当季能不能种"判断货架上有什么，一格地都没锄过就没有判断依据，
        // 直接去买只会得到"货架上没有这个季节能种的种子"，然后卡在那里空转。
        // 背包塞满时不去：买回来也装不进，addItem 会直接失败，白跑一趟还刷日志。
        if (!hasSeed && state.Money >= 60 && HasRoom(state))
        {
            if (!IsOnFarm(state.Location))
                return new FarmAction { Op = "go_to", Target = "Farm" };
            if (state.Tilled < 1)
                return new FarmAction { Op = "till_soil" };
            return new FarmAction { Op = "buy_seeds", Count = 300 };
        }

        if (state.Animals.Any(a => !a.Fed))
            return new FarmAction { Op = "feed_animals" };
        if (state.Animals.Any(a => a.Hearts < 10))
            return new FarmAction { Op = "pet_animals" };

        // 人不在农场：先回去。till_soil 在矿洞、镇上这些地方根本锄不动，
        // 不先回来就会一直派任务、一直回"人不在农场"，空转刷屏。
        if (!IsOnFarm(state.Location))
            return new FarmAction { Op = "go_to", Target = "Farm" };

        // 没有当季能种的种子：把地整到够用就行，太多了只是白花体力。
        // 阈值压小是因为农舍门口那块可耕地有限，整到 18 格左右就会一直"没锄动"空转。
        // 现在 mod 侧也开了 clear_debris，锄满之后不会再原地打转。
        if (state.Tilled < 18)
            return new FarmAction { Op = "till_soil" };

        // 既没种子也没什么活：天天清一遍农场杂物，总比站着不动像。
        return new FarmAction { Op = "clear_debris" };
    }

    /// <summary>背包里还有没有空格子。买种子、收菜都靠它，满了就必须先想办法腾地方。</summary>
    private static bool HasRoom(FarmState state) =>
        state.Inventory.Any(i => i.Stack <= 0) || state.Inventory.Count < 12;

    /// <summary>背包里有没有值得卖的东西：工具和当季种子留着，其余能出手的都算出货。</summary>
    private static bool HasSellable(FarmState state) =>
        state.Inventory.Any(i =>
            i.Stack > 0 &&
            !IsTool(i.Id) &&
            !IsSeed(i.Name));

    /// <summary>种子名可能是中文（防风草种子）也可能是英文（Parsnip Seeds），两种都认。</summary>
    private static bool IsSeed(string name) =>
        name.Contains("种子", StringComparison.Ordinal) ||
        name.Contains("Seed", StringComparison.OrdinalIgnoreCase);

    /// <summary>这几个 id 是她的家当，出货时一律留着。</summary>
    private static bool IsTool(string id) =>
        id.Equals("Axe", StringComparison.OrdinalIgnoreCase) ||
        id.Equals("Hoe", StringComparison.OrdinalIgnoreCase) ||
        id.Equals("Pickaxe", StringComparison.OrdinalIgnoreCase) ||
        id.Equals("WateringCan", StringComparison.OrdinalIgnoreCase) ||
        id.Equals("BambooPole", StringComparison.OrdinalIgnoreCase) ||
        id.Equals("47", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否站在农场露天地块上。FarmHouse 也带 Farm 前缀，必须排掉。</summary>
    private static bool IsOnFarm(string? location)
    {
        if (string.IsNullOrEmpty(location)) return false;
        if (location.Contains("House", StringComparison.OrdinalIgnoreCase)) return false;
        if (location.Contains("Greenhouse", StringComparison.OrdinalIgnoreCase)) return false;
        return location.StartsWith("Farm", StringComparison.OrdinalIgnoreCase);
    }

    public static string Describe(string op) => op switch
    {
        "water_crops" => "给缺水的作物浇水",
        "harvest_crops" => "收成熟的作物",
        "plant_seeds" => "给空地播种",
        "till_soil" => "把地锄开",
        "feed_animals" => "喂没吃过的动物",
        "pet_animals" => "和动物互动",
        "clear_debris" => "清理农场杂物",
        "ship_items" => "把可出售的东西放进出货箱",
        "buy_seeds" => "去买当季种子",
        "sleep" => "回家睡觉",
        "go_to" => "走过去",
        _ => op,
    };

    private static bool IsVague(string text) =>
        new[] { "打理", "照顾", "看看", "随便", "干点", "安排", "处理一下", "干点活" }
            .Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static bool TryParseTime(string? time, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(time)) return false;
        var parts = time.Split(':');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return false;
        minutes = h * 60 + m;
        return true;
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private static PlanResult Fail(string reason) => new() { Ok = false, Reason = reason };
}
