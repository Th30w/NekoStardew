using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace NekoStardew
{
    /// <summary>
    /// 买种子。
    ///
    /// 不走"开门店菜单"那条路：Utility.TryOpenShopMenu 只是把界面挂起来，还得有人点选，
    /// 后台托管时没人点，界面会一直挂着把时间也挡住。
    /// 这里直接按"货架上有的当季种子"清单扣钱、进货，效果等价于她去皮埃尔店里买了一趟。
    ///
    /// 种子清单是运行时从 Game1.objectData 里筛出来的，没有写死任何 ID——
    /// 换季、换版本都不会拿错东西。判断"当季能不能种"用的是原版的
    /// HoeDirt.canPlantThisSeedHere，和播种时那套判定完全一致。
    /// </summary>
    public static class SeedShop
    {
        private const int SeedsCategory = -74;

        public sealed class SeedOffer
        {
            public string Id = "";
            public string Name = "";
            public int Price;
        }

        /// <summary>当前季节能种、且买得起的种子。按价格从低到高排，先买便宜的不容易把家底掏空。</summary>
        public static List<SeedOffer> SeasonSeeds()
        {
            var result = new List<SeedOffer>();

            var dict = ObjectDataDict();
            if (dict == null)
                return result;

            foreach (var key in dict.Keys)
            {
                var id = key as string;
                if (string.IsNullOrEmpty(id)) continue;

                StardewValley.Object obj;
                try
                {
                    obj = new StardewValley.Object(id, 1);
                }
                catch
                {
                    continue;
                }

                if (obj.Category != SeedsCategory) continue;
                if (obj.Price <= 0) continue;
                if (!CanPlantNow(id)) continue;

                result.Add(new SeedOffer { Id = id, Name = obj.DisplayName, Price = obj.Price });
            }

            result.Sort((a, b) => a.Price.CompareTo(b.Price));
            return result;
        }

        /// <summary>
        /// 这个种子现在能不能种。两路判断，能走哪路走哪路：
        /// ① 拿一块已经锄好的地当探针调 canPlantThisSeedHere，这条最准，但没锄过地就没探针；
        /// ② 查 cropData 里这季作物允许的季节，这条不依赖地图。
        /// 早年只写了 ①，"一格地都没锄"的时候所有种子都被判成不能种，买种子永远买不成。
        /// </summary>
        public static bool CanPlantNow(string seedId)
        {
            if (string.IsNullOrEmpty(seedId)) return false;

            var probe = FindEmptyDirt();
            if (probe != null)
            {
                try
                {
                    return probe.canPlantThisSeedHere(seedId, false);
                }
                catch
                {
                    // 探针这条路走不通，滑到下面按作物数据判断
                }
            }

            var crops = CropDataDict();
            if (crops != null && crops.Contains(seedId))
            {
                var data = crops[seedId];
                if (data == null) return false;

                try
                {
                    var p = data.GetType().GetProperty("Seasons");
                    var v = p?.GetValue(data);
                    if (v is IEnumerable seasons)
                    {
                        foreach (var s in seasons)
                        {
                            if (s != null && s.ToString() == Game1.season.ToString())
                                return true;
                        }
                        return false;
                    }
                }
                catch
                {
                    // 季节字段读不到就当不知道，下面乐观放行
                }
            }

            // 完全判断不出来时宁可放行：真种不下去时 plant_seeds 会自己回报，
            // 但这里卡死会让"买种子"这一步永远不成立。
            return true;
        }

        private static IDictionary? CropDataDict()
        {
            try
            {
                var f = typeof(Game1).GetField(
                    "cropData",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                if (f != null && f.GetValue(null) is IDictionary d1) return d1;

                var p = typeof(Game1).GetProperty(
                    "cropData",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                if (p != null && p.GetValue(null) is IDictionary d2) return d2;
            }
            catch
            {
                // 拿不到就当没有
            }
            return null;
        }

        /// <summary>
        /// 去买种子。budget 是这一轮最多花多少钱，maxPacks 是最多买几包。
        /// 每一包单独扣钱、单独进背包，中途背包满了就停手。
        /// </summary>
        public static IEnumerable<string> Buy(int budget, int maxPacks)
        {
            if (budget <= 0) budget = 300;
            if (maxPacks <= 0) maxPacks = 6;

            var offers = SeasonSeeds();
            if (offers.Count == 0)
            {
                yield return "货架上没有这个季节能种的种子";
                yield break;
            }

            var spent = 0;
            var packs = 0;

            foreach (var offer in offers)
            {
                if (packs >= maxPacks) break;
                if (spent + offer.Price > budget) break;
                if (Game1.player.Money < offer.Price)
                {
                    yield return "钱不够了，先不买了";
                    yield break;
                }

                // C# 不允许在 catch 块里 yield，所以异常先收进变量，出了 try 再报。
                StardewValley.Object? seed = null;
                string? ctorError = null;
                try
                {
                    seed = new StardewValley.Object(offer.Id, 1);
                }
                catch (Exception ex)
                {
                    ctorError = ex.Message;
                }

                if (seed == null)
                {
                    yield return offer.Name + " 造不出来: " + (ctorError ?? "未知原因");
                    continue;
                }

                var added = false;
                string? addError = null;
                try
                {
                    added = Game1.player.addItemToInventoryBool(seed);
                }
                catch (Exception ex)
                {
                    addError = ex.Message;
                }

                if (addError != null)
                {
                    yield return offer.Name + " 装不进背包: " + addError;
                    continue;
                }

                if (!added)
                {
                    // 背包只有 12 格，她身上那点家当一塞就满。买不进去先自己腾地方：
                    // 把能卖的材料卖掉换格子，卖的钱正好抵种子钱，不亏。
                    var freed = MakeRoom();
                    if (freed <= 0)
                    {
                        yield return "背包满了，也没有能腾地方的东西，先不买了";
                        yield break;
                    }

                    yield return "背包满了，先卖掉 " + freed + " 样材料腾出地方";

                    // 同样不能在 catch 里 yield，异常先收着
                    var retry = false;
                    string? retryError = null;
                    try
                    {
                        retry = Game1.player.addItemToInventoryBool(seed);
                    }
                    catch (Exception ex)
                    {
                        retryError = ex.Message;
                    }

                    if (retryError != null)
                    {
                        yield return offer.Name + " 还是装不进背包: " + retryError;
                        continue;
                    }

                    if (!retry)
                    {
                        yield return "腾了地方还是塞不进去，先不买了";
                        yield break;
                    }

                    added = true;
                }

                Game1.player.Money -= offer.Price;
                spent += offer.Price;
                packs++;

                yield return "买了 1 包 " + offer.Name + "，花 " + offer.Price + " 金";
            }

            if (packs == 0)
                yield return "什么都没买成，钱不够买最便宜的那包";

            yield return "这一趟买种子共花 " + spent + " 金，剩 " + Game1.player.Money + " 金";
        }

        /// <summary>
        /// 背包满了腾地方：卖掉能卖的材料（纤维、木材、树液、杂货），工具和种子一律留着。
        /// 一次只卖两样，够装种子就行，别把家底一次清空。返回卖掉的样数。
        /// </summary>
        private static int MakeRoom()
        {
            var player = Game1.player;
            if (player == null) return 0;
            var items = player.Items;
            if (items == null) return 0;

            var removed = 0;

            // 倒着走：删掉的索引不会影响还没遍历到的位置。
            // 这里用"索引置空"而不是 removeItemFromInventory(item)：后者在后台托管
            // （没有真实输入上下文）时不一定真的把物品拿掉，试过两次都返回 0 样。
            for (var i = items.Count - 1; i >= 0 && removed < 2; i--)
            {
                var obj = items[i] as StardewValley.Object;
                if (obj == null) continue;

                // 种子播完、材料用完之后，槽里会留一个 Stack=0 的空壳，但它还占着这一格。
                // 这种僵尸槽不清掉，背包永远显示"满"，买什么都塞不进来。
                // 先清它们，不占"卖两样"的名额。
                if (obj.Stack <= 0)
                {
                    items[i] = null;
                    removed++;
                    continue;
                }

                if (obj.Category == -999) continue;                    // 工具不卖
                if (obj.Category == StardewValley.Object.SeedsCategory) continue; // 种子留着

                try
                {
                    // 不看 salePrice 高低：这里的目的只是腾格子，卖不出好价的东西照样出手，
                    // 卖价算不出来（0 或抛异常）也得挪走，否则背包一满就再也买不进种子。
                    var gained = 0;
                    try { gained = obj.salePrice() * obj.Stack; } catch { gained = 0; }

                    items[i] = null;
                    if (gained > 0) player.Money += gained;
                    removed++;
                }
                catch
                {
                    // 这件挪不动就算了，换下一件
                }
            }

            return removed;
        }

        /// <summary>找一块已经锄过、还没种东西的湿地当"季节探针"。</summary>
        private static HoeDirt? FindEmptyDirt()
        {
            var locations = Game1.locations;
            if (locations == null) return null;

            foreach (var loc in locations)
            {
                if (loc == null) continue;
                // 有些地点（矿洞下层、还没初始化的区域）terrainFeatures 是 null，必须判
                var features = loc.terrainFeatures;
                if (features == null) continue;

                foreach (var pair in features.Pairs)
                {
                    if (pair.Value is HoeDirt dirt && dirt.crop == null)
                        return dirt;
                }
            }
            return null;
        }

        /// <summary>
        /// 反射拿 Game1.objectData。1.6 里它是 Dictionary&lt;string, ObjectData&gt;，
        /// 用 IDictionary 遍历就不用依赖 ObjectData 的具体类型名（版本之间会变）。
        /// </summary>
        private static IDictionary? ObjectDataDict()
        {
            try
            {
                var f = typeof(Game1).GetField(
                    "objectData",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                if (f != null)
                {
                    var v = f.GetValue(null);
                    if (v is IDictionary d) return d;
                }

                var p = typeof(Game1).GetProperty(
                    "objectData",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                if (p != null)
                {
                    var v = p.GetValue(null);
                    if (v is IDictionary d) return d;
                }
            }
            catch
            {
                // 拿不到就当没有数据源
            }
            return null;
        }
    }
}
