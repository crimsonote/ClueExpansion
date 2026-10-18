using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace ClueDetail.Core
{
    /// <summary>装载结果（只用于启动日志）。</summary>
    internal readonly struct LoadResult
    {
        public readonly int Enabled;
        public readonly int Disabled;
        public readonly int Failed;

        public LoadResult(int enabled, int disabled, int failed)
        {
            Enabled = enabled;
            Disabled = disabled;
            Failed = failed;
        }
    }

    /// <summary>一个功能的运行期句柄：配置项、它自己的 Harmony 实例、当前是否挂着补丁。</summary>
    internal sealed class FeatureRuntime
    {
        public Type Type;

        public PatchFeatureAttribute Attr;

        public ConfigEntry<bool> Toggle;

        /// <summary>
        /// 这个功能**专属**的 Harmony 实例（id = <c>ClueDetail:&lt;段名&gt;</c>）。
        /// </summary>
        /// <remarks>
        /// 每个功能一个实例是热更新的前提：<c>UnpatchSelf()</c> 卸载的是「这个实例挂的全部补丁」，
        /// 共用一个实例的话就没法按功能卸载（只能全卸）。
        /// </remarks>
        public Harmony Harmony;

        /// <summary>补丁当前是否挂载着。</summary>
        public bool Active;
    }

    /// <summary>
    /// 反射发现带 <see cref="PatchFeatureAttribute"/> 的类并装载。逐类：
    /// ① 绑段开关 <c>[Section] Enabled</c>；② 有 <c>static void Bind(ConfigFile, string)</c> 就调用它绑子项；
    /// ③ 有 <c>static void OnLoaded()</c> 就调用它做一次性初始化；④ 启用则 <c>PatchAll</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>全部开关都是热的</b>（2026-10-18 起）。改 <c>.cfg</c> 里的段开关或总开关
    /// **不需要重启游戏**：
    /// </para>
    /// <list type="number">
    /// <item><c>SettingChanged</c> 只把「待应用」标记置起来（它可能在 HTTP 线程触发 ——
    /// DT_Tools 的 CONFIG 页就是走那里的，绝不能在那种线程里动 Harmony）；</item>
    /// <item><see cref="ApplyPending"/> 在 Unity 主线程（<c>Plugin.Update</c>）真正挂 / 卸补丁。</item>
    /// </list>
    /// <para>
    /// 生命周期约定（全部可选、全部 <c>static</c> 无参）：
    /// <c>Bind</c> / <c>OnLoaded</c> 在插件启动时各跑一次（**与开关状态无关**，这样热开时配置已经就位）；
    /// <c>OnEnabled</c> 在补丁挂上之后跑；<c>OnDisabled</c> 在补丁卸下之前跑 ——
    /// **有状态副作用的必须在 <c>OnDisabled</c> 里收拾干净**，否则关掉再开就带残血。
    /// </para>
    /// </remarks>
    internal static class FeatureLoader
    {
        /// <summary>总开关所在的段：<c>[ClueDetail] Enabled</c>。</summary>
        public const string MasterSection = "ClueDetail";

        private static readonly Type[] BindSignature = { typeof(ConfigFile), typeof(string) };

        private static readonly List<FeatureRuntime> Features = new List<FeatureRuntime>();

        private static ConfigEntry<bool> _master;
        private static ManualLogSource _log;

        private static readonly object Gate = new object();

        /// <summary>是否有待应用的开关变更（可能来自任意线程）。</summary>
        private static bool _pending;

        /// <summary>总开关当前值（Bind 之前的兜底是 true）。</summary>
        public static bool MasterOn
        {
            get { return _master == null || _master.Value; }
        }

        public static LoadResult Load(ConfigFile config, ManualLogSource log)
        {
            _log = log;

            // 总开关：**框架层**手写 Bind（功能代码才禁止手写；段开关本来也在这里手写）。
            _master = config.Bind(
                MasterSection,
                "Enabled",
                true,
                "总开关（热更新）：关掉后本模块的所有补丁立即卸载，不用重启游戏。\n" +
                "开启 / 关闭都会在日志里留下「已启用 / 已停用」记录。");
            _master.SettingChanged += OnAnySettingChanged;

            int enabled = 0, disabled = 0, failed = 0;

            foreach (Type type in typeof(FeatureLoader).Assembly.GetTypes())
            {
                var attr = type.GetCustomAttribute<PatchFeatureAttribute>();
                if (attr == null)
                {
                    continue;
                }

                var runtime = new FeatureRuntime
                {
                    Type = type,
                    Attr = attr,
                    Toggle = config.Bind(attr.Section, "Enabled", attr.DefaultEnabled, attr.Description),
                    Harmony = new Harmony(Plugin.Guid + ":" + attr.Section),
                };
                Features.Add(runtime);

                runtime.Toggle.SettingChanged += OnAnySettingChanged;

                // ── 子配置绑定：**无论开关状态都绑**，这样热开时配置已经就位 ──
                MethodInfo bind = FindStatic(type, "Bind", BindSignature);
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

                // ── 一次性初始化：同样与开关状态无关（启动时就跑） ──
                MethodInfo onLoaded = FindStatic(type, "OnLoaded", Type.EmptyTypes);
                if (onLoaded != null)
                {
                    try
                    {
                        onLoaded.Invoke(null, null);
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        log.LogError($"[ClueDetail] 功能 {type.Name} 的 OnLoaded 失败：{ex}");
                        continue;
                    }
                }

                if (_master.Value && runtime.Toggle.Value)
                {
                    if (Patch(runtime))
                    {
                        enabled++;
                    }
                    else
                    {
                        failed++;
                    }
                }
                else
                {
                    disabled++;
                    log.LogInfo(
                        $"[ClueDetail] 功能 {type.Name} 未启用" +
                        $"（[{(runtime.Toggle.Value ? MasterSection : attr.Section)}].Enabled = false）");
                }
            }

            return new LoadResult(enabled, disabled, failed);
        }

        /// <summary>
        /// 应用待处理的开关变更。**必须在 Unity 主线程调用**（<c>Plugin.Update</c> 每帧调一次，
        /// 没有待办时只是一次 bool 判断）。返回本次是否真的动了补丁。
        /// </summary>
        public static bool ApplyPending()
        {
            lock (Gate)
            {
                if (!_pending)
                {
                    return false;
                }

                _pending = false;
            }

            ApplyAll();
            return true;
        }

        /// <summary>某个功能的补丁此刻是否挂着（供功能之间做运行时判断）。</summary>
        public static bool IsActive<T>()
        {
            return IsActive(typeof(T));
        }

        /// <inheritdoc cref="IsActive{T}"/>
        public static bool IsActive(Type type)
        {
            for (int i = 0; i < Features.Count; i++)
            {
                if (Features[i].Type == type)
                {
                    return Features[i].Active;
                }
            }

            return false;
        }

        // ══════════════════ 内部 ══════════════════

        /// <summary>
        /// <c>SettingChanged</c> 的处理器：**只标记，不动手**。
        /// </summary>
        /// <remarks>
        /// 这个回调可能在**任意线程**触发 —— DT_Tools 的 CONFIG 页（<c>/api/config/update</c>）
        /// 就是走 HTTP 线程写配置的。在那个线程里 <c>PatchAll</c> / <c>UnpatchSelf</c>
        /// 会去改方法的 IL，不能这么干。所以这里只置一个标记，等主线程来收。
        /// </remarks>
        private static void OnAnySettingChanged(object sender, EventArgs e)
        {
            lock (Gate)
            {
                _pending = true;
            }
        }

        private static void ApplyAll()
        {
            bool master = MasterOn;

            for (int i = 0; i < Features.Count; i++)
            {
                var feature = Features[i];
                bool want = master && feature.Toggle != null && feature.Toggle.Value;

                if (want == feature.Active)
                {
                    continue;
                }

                if (want)
                {
                    if (Patch(feature))
                    {
                        _log?.LogInfo($"[ClueDetail] 已启用功能 {feature.Type.Name}（热更新）。");
                    }
                }
                else
                {
                    Unpatch(feature);
                    _log?.LogInfo($"[ClueDetail] 已停用功能 {feature.Type.Name}（热更新）。");
                }
            }
        }

        private static bool Patch(FeatureRuntime feature)
        {
            try
            {
                feature.Harmony.PatchAll(feature.Type);
                feature.Active = true;
                Invoke(feature.Type, "OnEnabled");
                return true;
            }
            catch (Exception ex)
            {
                feature.Active = false;
                _log?.LogError($"[ClueDetail] 功能 {feature.Type.Name} 补丁挂载失败：{ex}");
                return false;
            }
        }

        /// <summary>
        /// 卸载一个功能的补丁。
        /// </summary>
        /// <remarks>
        /// 顺序是**先 <c>OnDisabled</c> 再 <c>UnpatchSelf</c>**：清理动作（撤包、移除设备、发广播）
        /// 用的都是原版 API，与自己的补丁无关；把自己的补丁留到清理完再卸，
        /// 可以避免"清理过程中自己挂的钩子因为已经不在而缺行为"这种难查的差异。
        /// </remarks>
        private static void Unpatch(FeatureRuntime feature)
        {
            try
            {
                Invoke(feature.Type, "OnDisabled");
            }
            catch (Exception ex)
            {
                _log?.LogError($"[ClueDetail] 功能 {feature.Type.Name} 的 OnDisabled 失败：{ex}");
            }

            try
            {
                feature.Harmony.UnpatchSelf();
            }
            catch (Exception ex)
            {
                _log?.LogError($"[ClueDetail] 功能 {feature.Type.Name} 补丁卸载失败：{ex}");
            }

            feature.Active = false;
        }

        private static MethodInfo FindStatic(Type type, string name, Type[] signature)
        {
            return type.GetMethod(
                name,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                signature,
                null);
        }

        private static void Invoke(Type type, string name)
        {
            MethodInfo method = FindStatic(type, name, Type.EmptyTypes);
            if (method != null)
            {
                method.Invoke(null, null);
            }
        }
    }
}
