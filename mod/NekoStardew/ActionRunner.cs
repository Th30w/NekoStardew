using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;

namespace NekoStardew
{
    /// <summary>
    /// 动作执行器。命令从网络线程进队列，每帧在主线程推进一小步，
    /// 绝不跨线程碰游戏对象。
    /// </summary>
    public class ActionRunner
    {
        private const int WateredState = 1;
        private const int SeedsCategory = -74;

        private readonly IMonitor _monitor;
        private readonly AgentLink _link;
        private readonly ConcurrentQueue<AgentCommand> _queue = new ConcurrentQueue<AgentCommand>();

        private IEnumerator<string>? _routine;
        private string? _taskId;
        private string _lastNote = "";
        private int _frameBudget;

        public ActionRunner(IMonitor monitor, AgentLink link)
        {
            _monitor = monitor;
            _link = link;
        }

        public void Enqueue(AgentCommand cmd)
        {
            if (cmd != null) _queue.Enqueue(cmd);
        }

        public void Tick()
        {
            if (!Context.IsWorldReady) return;

            if (_routine == null)
            {
                if (!_queue.TryDequeue(out var next) || next == null) return;

                _taskId = next.TaskId;
                _routine = BuildRoutine(next);
                _frameBudget = 600;
                _lastNote = "开始执行";
            }

            _frameBudget--;
            if (_frameBudget <= 0)
            {
                Finish("failed", "执行超时，已中止");
                return;
            }

            bool moved;
            try
            {
                moved = _routine.MoveNext();
            }
            catch (Exception ex)
            {
                Finish("failed", "动作执行出错: " + ex.Message);
                return;
            }

            if (moved)
            {
                _lastNote = _routine.Current ?? "执行中";
            }
            else
            {
                Finish("ok", _lastNote);
            }
        }

        private IEnumerator<string> BuildRoutine(AgentCommand cmd)
        {
            foreach (var action in cmd.Actions)
            {
                switch (action.Op)
                {
                    case "water_crops":
                        foreach (var note in WaterAll()) yield return note;
                        break;

                    case "harvest_crops":
                        foreach (var note in HarvestAll()) yield return note;
                        break;

                    case "feed_animals":
                        foreach (var note in FeedAll()) yield return note;
                        break;

                    case "pet_animals":
                        foreach (var note in PetAll()) yield return note;
                        break;

                    case "plant_seeds":
                        foreach (var note in PlantAll()) yield return note;
                        break;

                    case "sleep":
                        yield return TrySleep();
                        break;

                    case "go_to":
                        yield return GoTo(action.Target);
                        break;

                    case "till_soil":
                        foreach (var note in TillAll()) yield return note;
                        break;

                    case "clear_debris":
                        foreach (var note in ClearDebris()) yield return note;
                        break;

                    case "ship_items":
                        foreach (var note in ShipItems()) yield return note;
                        break;

                    case "buy_seeds":
                        foreach (var note in SeedShop.Buy(action.Count > 0 ? action.Count : 300, 6))
                            yield return note;
                        break;

                    default:
                        yield return "不认识的动作: " + action.Op;
                        break;
                }
            }
        }

        private IEnumerable<string> WaterAll()
        {
            var loc = Game1.currentLocation;
            var targets = new List<Vector2>();

            foreach (var pair in loc.terrainFeatures.Pairs)
            {
                if (pair.Value is HoeDirt dirt && dirt.crop != null && dirt.state.Value != WateredState)
                    targets.Add(pair.Key);
            }

            SortByDistance(targets);

            foreach (var tile in targets)
            {
                var dirt = loc.terrainFeatures[tile] as HoeDirt;
                if (dirt == null) continue;

                dirt.state.Value = WateredState;
                Game1.player.Stamina = Math.Max(0f, Game1.player.Stamina - 1f);
                yield return "给 (" + (int)tile.X + "," + (int)tile.Y + ") 浇了水";
            }
        }

        private IEnumerable<string> HarvestAll()
        {
            var loc = Game1.currentLocation;
            var targets = new List<Vector2>();

            foreach (var pair in loc.terrainFeatures.Pairs)
            {
                if (pair.Value is HoeDirt dirt && dirt.readyForHarvest())
                    targets.Add(pair.Key);
            }

            SortByDistance(targets);

            foreach (var tile in targets)
            {
                var dirt = loc.terrainFeatures[tile] as HoeDirt;
                if (dirt == null || dirt.crop == null) continue;

                var note = "";
                try
                {
                    dirt.crop.harvest((int)tile.X, (int)tile.Y, dirt, null, false);
                    note = "收了一茬 (" + (int)tile.X + "," + (int)tile.Y + ")";
                }
                catch (Exception ex)
                {
                    note = "这块收不了: " + ex.Message;
                }
                yield return note;
            }
        }

        private IEnumerable<string> FeedAll()
        {
            foreach (var loc in Game1.locations)
            {
                if (!(loc is AnimalHouse house)) continue;

                var hungry = 0;
                foreach (var pair in house.animals.Pairs)
                {
                    if (pair.Value.fullness.Value < 100) hungry++;
                }

                if (hungry == 0) continue;

                var note = "";
                try
                {
                    house.feedAllAnimals();
                    note = "给 " + house.Name + " 添了草料";
                }
                catch (Exception ex)
                {
                    note = "喂食失败: " + ex.Message;
                }
                yield return note;
            }
        }

        private IEnumerable<string> PetAll()
        {
            foreach (var loc in Game1.locations)
            {
                if (!(loc is AnimalHouse house)) continue;

                foreach (var pair in house.animals.Pairs)
                {
                    var animal = pair.Value;
                    if (animal.wasPet.Value) continue;

                    animal.wasPet.Value = true;
                    animal.friendshipTowardFarmer.Value =
                        Math.Min(1000, animal.friendshipTowardFarmer.Value + 5);

                    yield return "摸了摸 " + animal.Name;
                }
            }
        }

        private IEnumerable<string> PlantAll()
        {
            var farmer = Game1.player;
            var loc = Game1.currentLocation;

            var seeds = new List<StardewValley.Object>();
            foreach (var item in farmer.Items)
            {
                if (item is StardewValley.Object obj && obj.Category == SeedsCategory)
                    seeds.Add(obj);
            }

            if (seeds.Count == 0)
            {
                yield return "背包里没有种子";
                yield break;
            }

            var empty = new List<Vector2>();
            foreach (var pair in loc.terrainFeatures.Pairs)
            {
                if (pair.Value is HoeDirt dirt && dirt.crop == null)
                    empty.Add(pair.Key);
            }

            if (empty.Count == 0)
            {
                yield return "没有空着的地块";
                yield break;
            }

            SortByDistance(empty);

            foreach (var tile in empty)
            {
                var dirt = loc.terrainFeatures[tile] as HoeDirt;
                if (dirt == null) continue;

                // 当季种不了的种子（夏天种防风草）canPlantThisSeedHere 直接为 false，跳过换一种
                var seed = seeds.FirstOrDefault(s => s.Stack > 0 && dirt.canPlantThisSeedHere(s.ItemId, false));
                if (seed == null)
                {
                    yield return "没有当季能种的种子";
                    yield break;
                }

                var planted = false;
                string? failNote = null;

                try
                {
                    planted = dirt.plant(seed.ItemId, farmer, false);
                }
                catch (Exception ex)
                {
                    failNote = "播种失败: " + ex.Message;
                }

                if (failNote != null)
                {
                    yield return failNote;
                    yield break;
                }

                if (planted)
                {
                    var name = seed.DisplayName;
                    seed.Stack--;
                    if (seed.Stack <= 0)
                    {
                        // 播完最后一颗，槽里会留一个 Stack=0 的空壳还占着格子。
                        // 不清掉它，背包就永远显示"满"，之后买什么都塞不进来——
                        // 这是"她一直买不成种子"的最后一道卡点。
                        ClearEmptySlot(farmer, seed);
                        seeds.Remove(seed);
                    }
                    yield return "在 (" + (int)tile.X + "," + (int)tile.Y + ") 播了 " + name;
                }
                else
                {
                    yield return "(" + (int)tile.X + "," + (int)tile.Y + ") 播不下去";
                }
            }
        }

        /// <summary>
        /// 睡觉 = 结束这一天。星露谷只在睡觉时存档，作物也只有过夜才长。
        /// 之前这里是空的，所以每次重启都回到 06:00、当日锄地和播种全丢。
        /// </summary>
        private string TrySleep()
        {
            if (Game1.timeOfDay < 1800)
                return "现在才 " + ClockText(Game1.timeOfDay) + "，还没到收工的点";

            if (Game1.eventUp || Game1.CurrentEvent != null)
            {
                // 后台托管没有真人推进剧情，这个 event 会一直挂着把睡觉挡在门外，
                // 结果时间一路跑到 26:00 强制晕倒（还要扣钱）。这里自己再清一次。
                ClearEventForSleep();

                if (Game1.eventUp || Game1.CurrentEvent != null)
                    return "有剧情卡着，这时候睡会出问题，先不睡";
            }

            // 1.6 里没有 endDay()：实测 StardewValley.Game1 上只有 newDayAfterFade(Action)、
            // OnDayStarted()、_newDayAfterFade() 这些。原版睡觉走的就是 newDayAfterFade：
            // 它内部做"存档 → 换天"，作物过夜长、当日进度落盘都在里面。
            var flags = System.Reflection.BindingFlags.Static |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic;

            var m = typeof(Game1).GetMethod(
                "newDayAfterFade", flags, null, new[] { typeof(Action) }, null);

            if (m == null)
                return "这一版游戏里找不到结束一天的方法";

            try
            {
                Game1.newDay = true;
                m.Invoke(null, new object[] { null });
                return "睡下了，这一天结束";
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                return "睡觉失败: " + inner.Message;
            }
        }

        private static string ClockText(int t)
        {
            var h = t / 100;
            var m = t % 100;
            return (h < 10 ? "0" : "") + h + ":" + (m < 10 ? "0" : "") + m;
        }

        /// <summary>
        /// 清农场杂物：杂草、石头、树枝、木头。
        /// 直接把这些物体从地点上移除，同时扣体力，效果和用镰刀/镐子清掉一样。
        /// </summary>
        private IEnumerable<string> ClearDebris()
        {
            var loc = Game1.currentLocation;
            if (loc == null)
            {
                yield return "当前地点读不到";
                yield break;
            }

            var targets = new List<Vector2>();
            foreach (var pair in loc.Objects.Pairs)
            {
                if (pair.Value != null && IsDebris(pair.Value))
                    targets.Add(pair.Key);
            }

            if (targets.Count == 0)
            {
                yield return "这块地上没有要清的杂物";
                yield break;
            }

            SortByDistance(targets);

            var removed = 0;
            foreach (var tile in targets)
            {
                if (removed >= 10) break;
                if (Game1.player.Stamina < 2f)
                {
                    yield return "体力不够了，先停";
                    yield break;
                }

                var obj = loc.Objects[tile];
                if (obj == null) continue;

                var name = obj.DisplayName;
                string note;
                try
                {
                    loc.Objects.Remove(tile);
                    Game1.player.Stamina = Math.Max(0f, Game1.player.Stamina - 2f);
                    removed++;
                    note = "清掉 (" + (int)tile.X + "," + (int)tile.Y + ") 的" + name;
                }
                catch (Exception ex)
                {
                    note = "这一格清不动: " + ex.Message;
                }

                yield return note;
            }

            if (removed == 0)
                yield return "一格都没清动";
        }

        private static bool IsDebris(StardewValley.Object obj)
        {
            if (obj.IsWeeds()) return true;

            // 原版的石头 / 树枝 / 木头：ParentSheetIndex 分别是 32 / 294 / 343
            switch (obj.ParentSheetIndex)
            {
                case 32:
                case 294:
                case 343:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 出货：把背包里能卖的东西按当日售价换成金币。
        ///
        /// 1.6 的出货箱不是独立类型，`Farm.getShippingBin` 返回的是 IInventory（没有 addItem），
        /// 直接往箱子里塞东西这条路走不通，所以这里按"卖断"处理：算出售价、加钱、从背包移除。
        /// 绿叶、种子、工具、任务物品都留着，别把家当卖了。
        /// </summary>
        private IEnumerable<string> ShipItems()
        {
            var sellable = new List<StardewValley.Object>();
            foreach (var item in Game1.player.Items)
            {
                if (item is StardewValley.Object obj && obj.Stack > 0 &&
                    obj.Category != SeedsCategory && obj.Category != -999 &&
                    obj.salePrice() > 0)
                {
                    sellable.Add(obj);
                }
            }

            if (sellable.Count == 0)
            {
                yield return "背包里没有能卖的东西";
                yield break;
            }

            var total = 0;
            foreach (var obj in sellable)
            {
                var name = obj.DisplayName;
                var stack = obj.Stack;
                var price = obj.salePrice() * stack;

                string note;
                try
                {
                    Game1.player.removeItemFromInventory(obj);
                    Game1.player.Money += price;
                    total += price;
                    note = "卖掉 " + name + "×" + stack + "，进账 " + price + " 金";
                }
                catch (Exception ex)
                {
                    note = name + " 卖不掉: " + ex.Message;
                }

                yield return note;
            }

            yield return "这一轮出货共进账 " + total + " 金";
        }

        /// <summary>走到目标地点。用原版 warp，地图切换、镜头、碰撞都按正常流程走。</summary>
        private string GoTo(string? target)
        {
            if (string.IsNullOrWhiteSpace(target)) return "没说要去哪";

            var name = target.Trim();

            if (Game1.currentLocation != null &&
                string.Equals(Game1.currentLocation.Name, name, StringComparison.OrdinalIgnoreCase))
                return "已经在" + name + "了";

            var dest = Game1.getLocationFromName(name);
            if (dest == null) return "找不到地点 " + name;

            int x = 64;
            int y = 15;

            if (!name.Equals("Farm", StringComparison.OrdinalIgnoreCase))
            {
                var warp = dest.warps != null && dest.warps.Count > 0 ? dest.warps[0] : null;
                if (warp != null)
                {
                    x = warp.TargetX;
                    y = warp.TargetY;
                }
                else
                {
                    x = 10;
                    y = 10;
                }
            }

            try
            {
                Game1.warpFarmer(name, x, y, false);
                return "传送到 " + name + " (" + x + "," + y + ")";
            }
            catch (Exception ex)
            {
                return "传送失败: " + ex.Message;
            }
        }

        /// <summary>
        /// 锄地。调原版锄头的 DoFunction，让它自己判定能不能耕、扣体力、生成 HoeDirt，
        /// 比手写 new HoeDirt 稳（不会把水塘或建筑地板锄掉）。
        /// </summary>
        private IEnumerable<string> TillAll()
        {
            var loc = Game1.getLocationFromName("Farm");
            if (loc == null)
            {
                yield return "找不到农场";
                yield break;
            }

            if (Game1.currentLocation != loc)
            {
                yield return "人不在农场，得先走过去";
                yield break;
            }

            int hoeIndex = -1;
            for (int i = 0; i < Game1.player.Items.Count; i++)
            {
                if (Game1.player.Items[i] is Hoe)
                {
                    hoeIndex = i;
                    break;
                }
            }

            if (hoeIndex < 0)
            {
                yield return "背包里没有锄头";
                yield break;
            }

            var oldIndex = Game1.player.CurrentToolIndex;
            Game1.player.CurrentToolIndex = hoeIndex;

            var made = 0;
            var tried = 0;

            try
            {
                for (int y = 8; y <= 22 && made < 12; y++)
                {
                    for (int x = 62; x <= 82 && made < 12; x++)
                    {
                        var tile = new Vector2(x, y);

                        if (loc.terrainFeatures.ContainsKey(tile)) continue;
                        if (loc.objects.ContainsKey(tile)) continue;
                        // doesTileHaveProperty 返回的是属性值字符串（不是 bool），空就是没有这个属性
                        if (string.IsNullOrEmpty(loc.doesTileHaveProperty(x, y, "Diggable", "Back"))) continue;

                        if (Game1.player.Stamina < 2f)
                        {
                            yield return "体力不够了，先停";
                            yield break;
                        }

                        tried++;

                        var ok = false;
                        try
                        {
                            Game1.player.CurrentTool?.DoFunction(loc, x * 64, y * 64, 0, Game1.player);
                            ok = loc.terrainFeatures.ContainsKey(tile);
                        }
                        catch
                        {
                            ok = false;
                        }

                        if (ok)
                        {
                            made++;
                            yield return "锄开 (" + x + "," + y + ")";
                        }
                    }
                }
            }
            finally
            {
                Game1.player.CurrentToolIndex = oldIndex;
            }

            if (made == 0)
                yield return "这块没锄动（试了 " + tried + " 格）";
        }

        /// <summary>
        /// 把卡住的剧情事件摘掉。字段名按"类型是 Event、名字里带 CurrentEvent"去找，
        /// 不写死具体名，1.6 换版本字段改名也不会失效。
        /// </summary>
        private static void ClearEventForSleep()
        {
            Game1.eventUp = false;

            // 1.6 的 Game1.CurrentEvent 其实挂在当前地点上，只清 Game1 自己的静态字段不够：
            // 实测 eventUp 已经是 False，CurrentEvent 还一直是个 Event 对象。
            var loc = Game1.currentLocation;
            if (loc != null)
            {
                try
                {
                    foreach (var f in loc.GetType().GetFields(
                                 System.Reflection.BindingFlags.Instance |
                                 System.Reflection.BindingFlags.Public |
                                 System.Reflection.BindingFlags.NonPublic))
                    {
                        if (f.FieldType != typeof(StardewValley.Event)) continue;
                        if (f.Name.IndexOf("event", StringComparison.OrdinalIgnoreCase) < 0) continue;

                        f.SetValue(loc, null);
                    }
                }
                catch
                {
                    // 清不掉就按原样判断，下面还会挡一次
                }
            }

            try
            {
                foreach (var f in typeof(Game1).GetFields(
                             System.Reflection.BindingFlags.Static |
                             System.Reflection.BindingFlags.Public |
                             System.Reflection.BindingFlags.NonPublic))
                {
                    if (f.FieldType != typeof(StardewValley.Event)) continue;
                    if (f.Name.IndexOf("CurrentEvent", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    f.SetValue(null, null);
                }
            }
            catch
            {
                // 同上
            }
        }

        /// <summary>
        /// 把背包里堆数归零的那个槽清空。星露谷不会自动回收这种空壳槽，
        /// 留着它这一格就一直被占，12 格的背包很快会被几个空壳占满。
        /// </summary>
        private static void ClearEmptySlot(Farmer farmer, Item item)
        {
            if (farmer == null || item == null) return;

            var items = farmer.Items;
            if (items == null) return;

            for (var i = 0; i < items.Count; i++)
            {
                if (items[i] == item)
                {
                    items[i] = null;
                    return;
                }
            }
        }

        private void SortByDistance(List<Vector2> tiles)
        {
            var here = Game1.player.Tile;
            tiles.Sort((a, b) =>
                Vector2.DistanceSquared(a, here).CompareTo(Vector2.DistanceSquared(b, here)));
        }

        private void Finish(string status, string note)
        {
            var payload = new
            {
                type = "task_finished",
                task_id = _taskId,
                status,
                text = note,
            };

            _link.Send(JsonSerializer.Serialize(payload));
            _monitor.Log("[任务 " + (_taskId ?? "-") + "] " + status + ": " + note, LogLevel.Info);

            _routine = null;
            _taskId = null;
            _lastNote = "";
        }
    }
}
