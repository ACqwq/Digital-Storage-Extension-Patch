using System;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 「僵尸 Thing」安全读取工具。
    ///
    /// ── 背景：为什么必须有它 ──
    /// <c>Verse.Thing.Map</c> 的实现是拿 <c>mapIndex</c> 直接索引地图列表：
    /// 形如 <c>mapIndex &lt; 0 ? null : Find.Maps[mapIndex]</c>。
    /// 一旦**地图集合发生变动**（重力飞船起飞/落地、地图被移除、跨图传送），
    /// 某些 Thing 的 <c>mapIndex</c> 会残留一个**已经不存在的下标**。
    /// 此时读 <c>thing.Map</c> 得到的不是 null，而是
    /// <c>ArgumentOutOfRangeException: Index was out of range</c>。
    ///
    /// 本补丁的投影核心注册表是**静态**列表，跨地图生命周期持有实例，
    /// 于是正好踩中这个陷阱：残留一条「僵尸」登记后，每次遍历都抛一次异常，
    /// 表现为每秒刷屏的红字、绑定菜单打不开、连卸载都报错。
    ///
    /// ── 用法 ──
    /// 凡**不确定目标是否还在有效地图上**的读取（尤其：遍历静态集合、
    /// 描述一个可能在外图的对象），一律用 <see cref="MapOf"/> 代替 <c>thing.Map</c>。
    /// 正常路径零成本（只是多一层极薄的方法调用），只有真遇到僵尸才会走 catch。
    /// </summary>
    internal static class DsSafe
    {
        /// <summary>
        /// 读取 thing 所在地图；目标为空 / 已销毁 / 地图下标已失效时返回 null —— **绝不抛异常**。
        /// </summary>
        public static Map MapOf(Thing t)
        {
            if (t == null)
            {
                return null;
            }
            try
            {
                if (t.Destroyed)
                {
                    return null;
                }
                return t.Map;
            }
            catch (Exception)
            {
                // mapIndex 指向一张已被移除的地图 ⇒ 视作"不在任何地图上"。
                return null;
            }
        }

        /// <summary>两者是否在同一张**有效**地图上（任一方失效都算"不同图"）。</summary>
        public static bool SameMap(Thing a, Thing b)
        {
            Map ma = MapOf(a);
            Map mb = MapOf(b);
            return ma != null && mb != null && ReferenceEquals(ma, mb);
        }

        /// <summary>thing 是否"真的还在场上"：未销毁且所在地图有效。</summary>
        public static bool IsAliveOnMap(Thing t)
        {
            return MapOf(t) != null;
        }
    }
}
