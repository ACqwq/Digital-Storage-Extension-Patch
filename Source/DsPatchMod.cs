using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 补丁 mod 入口：持有本补丁自己的 ModSettings。
    /// </summary>
    public class DsPatchMod : Mod
    {
        public const string PackageId = "cagier.digitalstorage.patch";

        private static DsPatchSettings settings;
        private static bool resolving;

        public DsPatchMod(ModContentPack content) : base(content)
        {
            settings = GetSettings<DsPatchSettings>();
            settings.EnsureDefaults();
        }

        /// <summary>全局设置访问点；任何过早的调用都会走一次性自举，避免空引用与双份设置。</summary>
        public static DsPatchSettings Settings
        {
            get
            {
                if (settings == null)
                {
                    EnsureSettings();
                }
                return settings;
            }
        }

        /// <summary>
        /// 用于 [StaticConstructorOnStartup] 这类早于 Mod 构造的场合。
        /// 优先复用 RimWorld 已经为本 mod 建好的 ModSettings，避免出现两份互不同步的设置。
        /// </summary>
        public static void EnsureSettings()
        {
            if (settings != null || resolving)
            {
                return;
            }
            resolving = true;
            try
            {
                DsPatchMod self = FindSelf();
                if (self != null)
                {
                    settings = self.GetSettings<DsPatchSettings>();
                }
                else
                {
                    Log.Error("[数字存储补丁] 未能定位本补丁的 Mod 实例，将使用临时设置对象。"
                              + "这会导致设置界面与运行时读到不同的实例，请把此日志反馈给补丁作者。");
                    settings = new DsPatchSettings();
                }
                settings.EnsureDefaults();
            }
            finally
            {
                resolving = false;
            }
        }

        private static DsPatchMod FindSelf()
        {
            System.Collections.Generic.IEnumerable<Mod> mods = LoadedModManager.ModHandles;
            if (mods == null)
            {
                return null;
            }
            foreach (Mod m in mods)
            {
                DsPatchMod mine = m as DsPatchMod;
                if (mine != null)
                {
                    return mine;
                }
            }
            return null;
        }

        public override void DoSettingsWindowContents(UnityEngine.Rect inRect)
        {
            DsPatchSettingsUI.Draw(inRect, Settings);
            base.DoSettingsWindowContents(inRect);
        }

        public override string SettingsCategory()
        {
            return "DigitalStoragePatch_SettingsCategory".Translate();
        }
    }
}
