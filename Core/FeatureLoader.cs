using System;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace ClueDetail.Core
{
    internal readonly struct LoadResult
    {
        public readonly int Enabled;
        public readonly int Skipped;
        public readonly int Failed;

        public LoadResult(int enabled, int skipped, int failed)
        {
            Enabled = enabled;
            Skipped = skipped;
            Failed = failed;
        }
    }

    /// <summary>
    /// 反射发现带 <see cref="PatchFeatureAttribute"/> 的类，逐类：
    /// ① 绑定段开关 <c>[Section] Enabled</c>；② 若功能类有 <c>static void Bind(ConfigFile, string)</c> 就调用它绑子项；
    /// ③ 启用则 <c>PatchAll</c>，关闭则整类跳过。
    /// </summary>
    /// <remarks>
    /// 一个类失败只影响它自己；失败计数必须看日志 —— 补丁挂不上不会报错，只会静默失效。
    /// </remarks>
    internal static class FeatureLoader
    {
        private static readonly Type[] BindSignature = { typeof(ConfigFile), typeof(string) };

        public static LoadResult Load(Harmony harmony, ConfigFile config, ManualLogSource log)
        {
            int enabled = 0, skipped = 0, failed = 0;

            foreach (Type type in typeof(FeatureLoader).Assembly.GetTypes())
            {
                var attr = type.GetCustomAttribute<PatchFeatureAttribute>();
                if (attr == null)
                {
                    continue;
                }

                ConfigEntry<bool> toggle = config.Bind(attr.Section, "Enabled", attr.DefaultEnabled, attr.Description);

                MethodInfo bind = type.GetMethod("Bind", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, BindSignature, null);
                if (bind != null)
                {
                    try
                    {
                        bind.Invoke(null, new object[] { config, attr.Section });
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        log.LogError($"[ClueDetail] 功能 {type.Name} 配置绑定失败：{ex}");
                        continue;
                    }
                }

                if (!toggle.Value)
                {
                    skipped++;
                    log.LogInfo($"[ClueDetail] 已跳过功能：{type.Name}（[{attr.Section}].Enabled = false）");
                    continue;
                }

                try
                {
                    harmony.PatchAll(type);
                    enabled++;
                }
                catch (Exception ex)
                {
                    failed++;
                    log.LogError($"[ClueDetail] 功能 {type.Name} 补丁挂载失败：{ex}");
                }
            }

            return new LoadResult(enabled, skipped, failed);
        }
    }
}
