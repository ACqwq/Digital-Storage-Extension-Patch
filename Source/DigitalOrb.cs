using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 数字核心同款的「一束光 + 悬浮球体」共享渲染器。
    /// 材质与绘制参数照抄原 mod `Building_StorageCore.DrawAt` 的实现，
    /// 由**数字电池**与**投影核心**共用（`MaterialPool` 本来就按路径缓存，
    /// 这里主要是把两份重复代码收成一份）。
    /// </summary>
    internal static class DigitalOrb
    {
        private static Material lightMat;
        private static Material orbMat;
        private static bool initialized;

        private static void EnsureMaterials()
        {
            if (initialized)
            {
                return;
            }
            // 复用原 mod 的资源（原 mod 是硬依赖且先于本补丁加载）。
            lightMat = MaterialPool.MatFrom("2.0/一束光", ShaderDatabase.MoteGlow);
            orbMat = MaterialPool.MatFrom("2.0/一个球", ShaderDatabase.Cutout);
            initialized = true;
        }

        /// <summary>在 drawLoc（建筑 DrawAt 的入参）之上绘制光效与浮动球体。</summary>
        internal static void DrawAt(Vector3 drawLoc)
        {
            EnsureMaterials();
            if (lightMat == null || orbMat == null)
            {
                return;
            }

            Vector3 lightLoc = drawLoc;
            lightLoc.y = AltitudeLayer.BuildingOnTop.AltitudeFor();
            Graphics.DrawMesh(
                MeshPool.plane10,
                Matrix4x4.TRS(lightLoc, Quaternion.identity, new Vector3(3f, 10f, 3f)),
                lightMat, 0);

            float bob = Mathf.Sin(Time.realtimeSinceStartup * 2f) * 0.15f;
            Vector3 orbLoc = drawLoc;
            orbLoc.y = AltitudeLayer.BuildingOnTop.AltitudeFor() + 0.01f;
            orbLoc.z += bob;
            Graphics.DrawMesh(
                MeshPool.plane10,
                Matrix4x4.TRS(orbLoc, Quaternion.identity, new Vector3(3f, 10f, 3f)),
                orbMat, 0);
        }
    }
}
