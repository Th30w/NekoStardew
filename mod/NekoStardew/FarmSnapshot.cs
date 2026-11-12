using System;
using System.Collections.Generic;
using System.Text.Json;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace NekoStardew
{
    /// <summary>
    /// 把农场真实状态拍成一个 JSON 快照。所有读取都包了兜底，
    /// 单个字段取不到不会让整帧丢掉。
    /// </summary>
    public static class FarmSnapshot
    {
        public static string Envelope()
        {
            var msg = new Dictionary<string, object?>
            {
                ["type"] = "state",
                ["state"] = Build(),
            };

            return JsonSerializer.Serialize(msg);
        }

        private static Dictionary<string, object?> Build()
        {
            return new Dictionary<string, object?>
            {
                ["season"] = Safe(() => Game1.currentSeason, "unknown"),
                ["day"] = Safe(() => Game1.dayOfMonth, 0),
                ["year"] = Safe(() => Game1.year, 0),
                ["time"] = Safe(() => FormatTime(Game1.timeOfDay), "00:00"),
                ["weather"] = Safe(Weather, "sunny"),
                ["money"] = Safe(() => Game1.player.Money, 0),
                ["stamina"] = Safe(() => (double)Game1.player.Stamina, 0.0),
                ["max_stamina"] = Safe(() => Game1.player.MaxStamina, 0),
                ["location"] = Safe(() => Game1.currentLocation.Name, "?"),
                ["tile"] = Safe(() => new[] { (int)Game1.player.Tile.X, (int)Game1.player.Tile.Y }, new[] { 0, 0 }),
                ["tilled"] = Safe(() => CountTilled(Game1.currentLocation), 0),
                ["plantable_seeds"] = Safe(() => CountPlantableSeeds(Game1.player), 0),
                ["crops"] = Safe(() => CollectCrops(Game1.currentLocation), new List<object>()),
                ["animals"] = Safe(CollectAnimals, new List<object>()),
                ["inventory"] = Safe(() => CollectInventory(Game1.player), new List<object>()),
            };
        }

        private static string FormatTime(int timeOfDay)
        {
            var h = timeOfDay / 100;
            var m = timeOfDay % 100;
            return (h < 10 ? "0" + h : h.ToString()) + ":" + (m < 10 ? "0" + m : m.ToString());
        }

        /// <summary>已经锄好但还没种的地块数。她据此决定"该锄地"还是"该播种"。</summary>
        private static int CountTilled(GameLocation loc)
        {
            if (loc == null || loc.terrainFeatures == null) return 0;

            var n = 0;
            foreach (var pair in loc.terrainFeatures.Pairs)
            {
                if (pair.Value is HoeDirt dirt && dirt.crop == null) n++;
            }
            return n;
        }

        /// <summary>
        /// 背包里"这一季真能种下去"的种子总堆数。防风草是春季作物，
        /// 夏天拿在手里也不能种，只按物品名判断"有种子"会让她反复派播种任务。
        /// 季节判断交给 SeedShop.CanPlantNow：它先用锄好的地当探针，没探针时按作物数据判断，
        /// 不再"一格地都没锄就认定所有种子都不能种"——那会让她一直卡在买种子那一步。
        /// </summary>
        private static int CountPlantableSeeds(Farmer who)
        {
            if (who == null) return 0;

            var total = 0;
            foreach (var item in who.Items)
            {
                if (item is StardewValley.Object obj &&
                    obj.Category == StardewValley.Object.SeedsCategory &&
                    SeedShop.CanPlantNow(obj.ItemId))
                    total += obj.Stack;
            }
            return total;
        }

        private static string Weather()
        {
            if (Game1.isLightning) return "storm";
            if (Game1.isSnowing) return "snow";
            if (Game1.isRaining) return "rain";
            return "sunny";
        }

        private static List<object> CollectCrops(GameLocation loc)
        {
            var list = new List<object>();
            if (loc == null || loc.terrainFeatures == null) return list;

            foreach (var pair in loc.terrainFeatures.Pairs)
            {
                if (!(pair.Value is HoeDirt dirt)) continue;
                if (dirt.crop == null) continue;

                var crop = dirt.crop;

                list.Add(new Dictionary<string, object?>
                {
                    ["x"] = (int)pair.Key.X,
                    ["y"] = (int)pair.Key.Y,
                    ["name"] = CropName(crop),
                    ["watered"] = Safe(() => dirt.state.Value == 1, false),
                    ["harvestable"] = Safe(() => dirt.readyForHarvest(), false),
                    ["dead"] = Safe(() => crop.dead.Value, false),
                    ["phase"] = Safe(() => crop.currentPhase.Value, 0),
                });
            }

            return list;
        }

        private static string CropName(Crop crop)
        {
            try
            {
                var id = crop.indexOfHarvest.Value;
                if (string.IsNullOrEmpty(id)) return "unknown";

                var data = ItemRegistry.GetDataOrErrorItem(id);
                var name = data.DisplayName;
                return string.IsNullOrEmpty(name) ? id : name;
            }
            catch
            {
                return "unknown";
            }
        }

        private static List<object> CollectAnimals()
        {
            var list = new List<object>();

            foreach (var loc in Game1.locations)
            {
                if (!(loc is AnimalHouse house)) continue;
                if (house.animals == null) continue;

                foreach (var pair in house.animals.Pairs)
                {
                    var animal = pair.Value;

                    list.Add(new Dictionary<string, object?>
                    {
                        ["id"] = Safe(() => pair.Key.ToString(), "?"),
                        ["type"] = Safe(() => animal.type.Value, "?"),
                        ["name"] = Safe(() => animal.Name, "?"),
                        ["fullness"] = Safe(() => animal.fullness.Value, 0),
                        ["petted"] = Safe(() => animal.wasPet.Value, false),
                        ["friendship"] = Safe(() => animal.friendshipTowardFarmer.Value, 0),
                        ["happiness"] = Safe(() => animal.happiness.Value, 0),
                        ["house"] = Safe(() => house.Name, "?"),
                    });
                }
            }

            return list;
        }

        private static List<object> CollectInventory(Farmer farmer)
        {
            var list = new List<object>();
            if (farmer == null || farmer.Items == null) return list;

            foreach (var item in farmer.Items)
            {
                if (item == null) continue;

                list.Add(new Dictionary<string, object?>
                {
                    ["name"] = Safe(() => item.DisplayName, "?"),
                    ["id"] = Safe(() => item.ItemId, "?"),
                    ["stack"] = Safe(() => item.Stack, 1),
                });
            }

            return list;
        }

        private static T Safe<T>(Func<T> getter, T fallback)
        {
            try
            {
                return getter();
            }
            catch
            {
                return fallback;
            }
        }
    }
}
