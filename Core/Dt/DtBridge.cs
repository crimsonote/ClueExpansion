using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace ClueDetail.Core.Dt
{
    /// <summary>
    /// 与 DT_Tools 的**可选**集成：把本模块的配置条目镜像进它的 ConfigFile，
    /// 使设置照旧出现在 DT 的 CONFIG 页里。
    /// </summary>
    /// <remarks>
    /// DT_Tools 是**可选依赖**：不装它，本模块全部功能照常工作，这里只记一行提示。
    /// <para>
    /// 权威来源永远是本模块自己的 <c>BepInEx/config/ClueDetail.cfg</c>；
    /// 镜像进 DT 的只是**同一批 ConfigEntry 对象**，所以值只有一份 ——
    /// 从 DT CONFIG 页改，会直接生效并落到本模块的 cfg 里。
    /// </para>
    /// <para>
    /// ⚠️ 与 HideAndSeek 的差异（2026-10-18 实测 DT_Tools **2.0.0.4**）：
    /// <list type="bullet">
    /// <item>配置入口仍在 <c>DT_Tools.Core.Engine.Config</c>（public static 属性）⇒ 镜像可行；</item>
    /// <item>但它文档里记录的命令桥挂点 <c>WebConsole.ExecuteCommandText(string)</c> **已不存在**
    /// （2.0 的 <c>DT_Tools.WebConsole.WebConsole</c> 只剩 <c>Post</c>/<c>RunOnMain</c>/<c>Init</c>）；</item>
    /// <item><c>SectionCategories</c> / <c>category</c> 字段也已删除，配置页改由**标签体系**归组 ——
    /// 所以 HideAndSeek 那套「给上游补登记分类」的做法在 2.0 下没有对应物，本模块不做。</item>
    /// </list>
    /// </para>
    /// </remarks>
    internal static class DtBridge
    {
        private const string EngineTypeName = "DT_Tools.Core.Engine";

        /// <summary>DT_Tools 是否已加载（只探类型，不依赖它的 Awake 是否跑过）。</summary>
        public static bool HasDtTools => AccessTools.TypeByName(EngineTypeName) != null;

        /// <summary>取 DT_Tools 的 ConfigFile；拿不到返回 null（未装或入口改名）。</summary>
        public static ConfigFile TryGetDtConfig()
        {
            try
            {
                System.Type type = AccessTools.TypeByName(EngineTypeName);
                PropertyInfo property = type?.GetProperty("Config", BindingFlags.Public | BindingFlags.Static);
                return property?.GetValue(null) as ConfigFile;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 把本模块的条目镜像进 DT_Tools 的 CONFIG 页。
        /// </summary>
        /// <remarks>
        /// 必须在**全部 Bind 之后**调用：早一步会镜像进一批残缺条目。
        /// 逐键检查是否已被占用，撞车就跳过 —— 覆盖上游条目远比"少显示几项"严重。
        /// </remarks>
        public static void TryMirror(ConfigFile own, ManualLogSource log)
        {
            if (own == null)
            {
                return;
            }

            ConfigFile dtConfig = TryGetDtConfig();
            if (dtConfig == null)
            {
                log.LogInfo("[ClueDetail] 未检测到可用的 DT_Tools 配置入口，本模块独立运行（配置见 ClueDetail.cfg）。");
                return;
            }

            Dictionary<ConfigDefinition, ConfigEntryBase> ownEntries = ConfigEntries.Of(own);
            Dictionary<ConfigDefinition, ConfigEntryBase> dtEntries = ConfigEntries.Of(dtConfig);
            if (ownEntries == null || dtEntries == null)
            {
                log.LogWarning("[ClueDetail] 拿不到配置条目表（BepInEx 内部结构可能变了），跳过镜像；本模块功能不受影响。");
                return;
            }

            int added = 0;
            int skipped = 0;

            foreach (var pair in ownEntries)
            {
                if (dtEntries.ContainsKey(pair.Key))
                {
                    skipped++;
                    log.LogWarning($"[ClueDetail] 配置键 [{pair.Key.Section}] {pair.Key.Key} 与 DT_Tools 撞名，跳过镜像。");
                    continue;
                }

                dtEntries[pair.Key] = pair.Value;
                added++;
            }

            log.LogInfo($"[ClueDetail] 已把 {added} 条配置镜像进 DT_Tools 的 CONFIG 页" +
                        (skipped > 0 ? $"（跳过 {skipped} 条与上游撞名的键）" : "。"));
        }
    }
}
