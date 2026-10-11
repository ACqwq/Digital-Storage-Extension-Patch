using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 读档 / 开新局时清空投影核心注册表。
    ///
    /// ── 为什么需要 ──
    /// <see cref="ProjectionRegistry"/> 是**静态**列表，而 mod 程序集在游戏进程里只加载一次。
    /// 玩家「存档 A → 主菜单 → 存档 B」时，静态内容不会自动重置。
    /// 若上一局结束时表里还有登记项（典型：某张地图被整块移除，其上建筑走不到 DeSpawn），
    /// 它们就会被带进下一局：
    ///   - 轻则：读 <c>.Map</c> 抛 ArgumentOutOfRangeException（地图下标已失效）；
    ///   - 重则：旧实例的 mapIndex 在新局里恰好有效 ⇒ 被当成"这张图上的投影"，
    ///     变成统计 / 交易 / 自动收取里数不干净的**幽灵**。
    ///
    /// 读档时各地图会重新 Spawn 投影核心，它们会在 SpawnSetup 里自行登记 —— 所以清空是安全的。
    ///
    /// ── 降级 ──
    /// 找不到目标方法就只记一条 Warning：注册表本身还有"遍历时自愈"兜底，不会因此失效。
    /// </summary>
    internal static class Patch_RegistryReset
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.registryreset";

        public static void Install()
        {
            Harmony harmony = new Harmony(HarmonyId);
            int ok = 0;

            // Game.LoadGame()：注意有无参 / 带参两个重载，这里明确取无参那个。
            MethodInfo loadGame = FindNoArgMethod(typeof(Game), "LoadGame");
            if (loadGame != null)
            {
                harmony.Patch(loadGame, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(Patch_RegistryReset), nameof(Prefix))));
                ok++;
            }

            MethodInfo initNew = FindNoArgMethod(typeof(Game), "InitNewGame");
            if (initNew != null)
            {
                harmony.Patch(initNew, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(Patch_RegistryReset), nameof(Prefix))));
                ok++;
            }

            if (ok == 2)
            {
                Log.Message("[数字存储补丁] 投影核心注册表重置已挂载：读档 / 新开局时清空（防跨存档残留）。");
            }
            else
            {
                Log.Warning("[数字存储补丁] 投影核心注册表重置只挂上 " + ok + "/2 —— "
                            + "跨存档残留改由遍历时自愈清理（功能不受影响）。");
            }
        }

        /// <summary>
        /// 找 type 上指定名字的**无参**方法。
        /// 自己遍历而不直接用 AccessTools.FirstMethod，是为了不依赖某个 Harmony 版本的重载签名。
        /// </summary>
        private static MethodInfo FindNoArgMethod(Type type, string name)
        {
            MethodInfo[] methods = type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.Name == name && m.GetParameters().Length == 0)
                {
                    return m;
                }
            }
            return null;
        }

        public static void Prefix()
        {
            try
            {
                ProjectionRegistry.Reset();
            }
            catch (Exception)
            {
                // 清空注册表失败不该阻断读档。
            }
        }
    }
}
