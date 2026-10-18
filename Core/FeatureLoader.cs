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

    /// <summary>一个功能的运行期句柄。</summary>
    internal sealed class FeatureRuntime
    {
        public Type Type;

        public PatchFeatureAttribute Attr;

        public ConfigEntry<bool> Toggle;

        /// <summary>上一次已知的启用状态，只用来判断切换方向（**不是**判断依据）。</summary>
        /// <remarks>
        /// 判断依据永远是 <see cref="FeatureLoader.Enabled(Type)"/> —— 它每次读配置值。
        /// 这个字段只回答「刚才关着吗」，好在翻转时调一次 <c>OnEnabled</c> / <c>OnDisabled</c>。
        /// </remarks>
        public bool Active;
    }

    /// <summary>
    /// 反射发现带 <see cref="PatchFeatureAttribute"/> 的类并装载。逐类：
    /// ① 绑段开关 <c>[Section] Enabled</c>；② 有 <c>static void Bind(ConfigFile, string)</c> 就调用它绑子项；
    /// ③ 有 <c>static void OnLoaded()</c> 就调用它做一次性初始化；④ **无条件 <c>PatchAll</c>**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>空转式</b>（2026-10-18 定，与 DT_Tools 的 <c>Engine.Enabled&lt;T&gt;()</c> 同款）：
    /// **补丁一律挂着**，开关只决定每个补丁入口那行检查放不放行。
    /// </para>
    /// <list type="bullet">
    /// <item><b>热</b>：<see cref="Enabled{T}"/> 每次都读 <c>ConfigEntry.Value</c>，
    /// 所以改 <c>.cfg</c> 立刻生效、不用重启，切换时也不碰任何 IL；</item>
    /// <item><b>入口检查是硬要求</b>：每个 <c>Prefix</c> / <c>Postfix</c> 的第一行必须是
    /// <c>if (!FeatureLoader.Enabled&lt;XxxFeature&gt;()) return;</c>
    /// （返回 bool 的 Prefix 写 <c>return true;</c> —— 放行原方法，**绝不能写 false**）。
    /// 漏一处就是「关了还生效」；</item>
    /// <item><b>切换回调</b>：<c>OnEnabled</c> / <c>OnDisabled</c> 由主线程的
    /// <see cref="ApplyPending"/> 调（<c>Plugin.Update</c>），只用来做状态清理与重建 ——
    /// 它们跟「补丁在不在」无关。</item>
    /// </list>
    /// <para>
    /// <c>SettingChanged</c> 只置一个待处理标记：它可能在任意线程触发
    /// （DT_Tools 的 CONFIG 页走 HTTP 线程写配置），而 <c>OnDisabled</c> 普遍要碰 Unity API，
    /// 必须在主线程跑（DT_Tools 的 <c>WireLifecycle</c> 用 <c>CoroutineHost.Post</c> 做同一件事）。
    /// </para>
    /// <para>
    /// 生命周期约定（全部可选、全部 <c>static</c> 无参）：<c>Bind</c> / <c>OnLoaded</c> 在启动时
    /// 各跑一次（**与开关状态无关**，这样热开时配置已经就位）；<c>OnEnabled</c> 在「关 → 开」翻转时跑；
    /// <c>OnDisabled</c> 在「开 → 关」翻转时跑 —— **有状态副作用的必须在它里面收拾干净**。
    /// </para>
    /// </remarks>
    internal static class FeatureLoader
    {
        /// <summary>总开关所在的段：<c>[ClueDetail] Enabled</c>。</summary>
        public const string MasterSection = "ClueDetail";

        private static readonly Type[] BindSignature = { typeof(ConfigFile), typeof(string) };

        private static readonly List<FeatureRuntime> Features = new List<FeatureRuntime>();

        private static readonly Dictionary<Type, FeatureRuntime> ByType = new Dictionary<Type, FeatureRuntime>();

        private static ConfigEntry<bool> _master;
        private static ManualLogSource _log;

        private static readonly object Gate = new object();

        /// <summary>是否有待处理的状态翻转（可能来自任意线程）。</summary>
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
                "总开关（热更新）：关掉后本模块的所有功能立即停用，不用重启游戏。\n" +
                "补丁仍然挂着，但每个入口的检查会全部放行原版行为 —— 与 DT_Tools 的空转式一致。");
            _master.SettingChanged += OnAnySettingChanged;

            // 单实例：空转式不卸载补丁，所以不需要按功能分开。
            var harmony = new Harmony(Plugin.Guid);

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
                };
                Features.Add(runtime);
                ByType[type] = runtime;

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

                // ── 补丁**总是挂**（空转式）；开关只在入口检查里生效 ──
                try
                {
                    harmony.PatchAll(type);
                }
                catch (Exception ex)
                {
                    failed++;
                    log.LogError($"[ClueDetail] 功能 {type.Name} 补丁挂载失败：{ex}");
                    continue;
                }

                runtime.Active = IsOn(runtime);
                if (runtime.Active)
                {
                    enabled++;
                }
                else
                {
                    disabled++;
                    log.LogInfo(
                        $"[ClueDetail] 功能 {type.Name} 未启用" +
                        $"（[{(runtime.Toggle.Value ? MasterSection : attr.Section)}].Enabled = false）" +
                        $"—— 补丁已挂载但会空转。");
                }
            }

            return new LoadResult(enabled, disabled, failed);
        }

        /// <summary>
        /// 某个功能此刻是否启用。**每次读配置值** —— 这就是「热」的全部来源。
        /// </summary>
        /// <remarks>
        /// 每个补丁方法的第一行都要用它，例如：
        /// <code>if (!FeatureLoader.Enabled&lt;RecordDetailFeature&gt;()) return;</code>
        /// 返回 bool 的 Prefix 要写 <c>return true;</c>（放行原方法），**不能**写 <c>return false;</c>。
        /// </remarks>
        public static bool Enabled<T>()
        {
            return Enabled(typeof(T));
        }

        /// <inheritdoc cref="Enabled{T}"/>
        public static bool Enabled(Type type)
        {
            FeatureRuntime runtime;
            return ByType.TryGetValue(type, out runtime) && IsOn(runtime);
        }

        /// <summary>
        /// 把待处理的状态翻转收掉：逐功能比较「现在该开吗」与上一次的状态，
        /// 翻转的调一次生命周期回调并记日志。
        /// </summary>
        /// <remarks>
        /// **必须在 Unity 主线程调用**（<c>Plugin.Update</c> 每帧一次，没有待办时只是一次 bool 判断）。
        /// 注意它**不碰任何补丁** —— 开关生效靠的是入口检查，这里只负责清理 / 重建。
        /// </remarks>
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

            for (int i = 0; i < Features.Count; i++)
            {
                var feature = Features[i];
                bool want = IsOn(feature);

                if (want == feature.Active)
                {
                    continue;
                }

                feature.Active = want;

                if (want)
                {
                    Invoke(feature, "OnEnabled");
                    _log?.LogInfo($"[ClueDetail] 已启用功能 {feature.Type.Name}（热更新）。");
                }
                else
                {
                    Invoke(feature, "OnDisabled");
                    _log?.LogInfo($"[ClueDetail] 已停用功能 {feature.Type.Name}（热更新）。");
                }
            }

            return true;
        }

        // ══════════════════ 内部 ══════════════════

        private static bool IsOn(FeatureRuntime feature)
        {
            if (_master != null && !_master.Value)
            {
                return false;
            }

            return feature.Toggle != null && feature.Toggle.Value;
        }

        /// <summary>
        /// <c>SettingChanged</c> 的处理器：**只标记**。
        /// </summary>
        /// <remarks>
        /// 这个回调可能在任意线程触发（DT_Tools 的 CONFIG 页走 HTTP 线程写配置），
        /// 而生命周期回调普遍要碰 Unity API ⇒ 一律投递到主线程。
        /// </remarks>
        private static void OnAnySettingChanged(object sender, EventArgs e)
        {
            lock (Gate)
            {
                _pending = true;
            }
        }

        private static void Invoke(FeatureRuntime feature, string name)
        {
            try
            {
                MethodInfo method = FindStatic(feature.Type, name, Type.EmptyTypes);
                if (method != null)
                {
                    method.Invoke(null, null);
                }
            }
            catch (Exception ex)
            {
                _log?.LogError($"[ClueDetail] 功能 {feature.Type.Name} 的 {name} 失败：{ex}");
            }
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
    }
}
