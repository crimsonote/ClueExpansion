using System;
using System.Collections;
using System.Reflection;
using System.Text;
using ClueDetail.Core;
using HarmonyLib;
using ServerDevice = Server.Game.Device;
using ServerGamePlayer = Server.Game.Player;
using ServerGameRoom = Server.Game.GameRoom;

namespace ClueDetail.Features.Both.CommandBridge
{
    internal partial class CommandBridgeFeature
    {
        private const string DtWebConsoleTypeName = "DT_Tools.WebConsole.WebConsole";
        private const string DtCommandsApiTypeName = "DT_Tools.WebConsole.Api.CommandsApi";
        private const string DtCommandResultTypeName = "DT_Tools.Commands.CommandResult";
        private const string DtLogTypeName = "DT_Tools.Log";

        /// <summary>本模块命令的前缀（用于快速判断"这条命令是不是我们的"）。</summary>
        private static bool IsOwnCommand(string name)
        {
            for (int i = 0; i < Commands.Length; i++)
            {
                if (string.Equals(Commands[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                string[] aliases = Commands[i].Aliases;
                if (aliases == null)
                {
                    continue;
                }

                for (int j = 0; j < aliases.Length; j++)
                {
                    if (string.Equals(aliases[j], name, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>拦截 DT_Tools 的命令执行口；不是本模块的命令一律原样放行。</summary>
        /// <remarks>
        /// <c>__result</c> 声明成 <c>object</c>：<c>CommandResult</c> 是本模块无法编译期引用的类型，
        /// Harmony 允许引用类型返回值用这种通用形式，反射构造后赋回即可（捉迷藏同款做法）。
        /// </remarks>
        [HarmonyPatch]
        internal static class ExecuteHook
        {
            private static MethodBase Target()
            {
                return AccessTools.Method(
                    AccessTools.TypeByName(DtWebConsoleTypeName),
                    "ExecuteCommandText",
                    new[] { typeof(string) });
            }

            private static bool Prepare()
            {
                if (Target() != null && AccessTools.TypeByName(DtCommandResultTypeName) != null)
                {
                    return true;
                }

                global::ClueDetail.Core.Log.Info(
                    "命令桥：未检测到 DT_Tools 的命令执行口（未装或结构变了）⇒ 本模块命令不接入，" +
                    "其余功能不受影响。");
                return false;
            }

            [HarmonyTargetMethod]
            private static MethodBase TargetMethod()
            {
                return Target();
            }

            [HarmonyPrefix]
            private static bool Prefix(string __0, ref object __result)
            {
                if (!FeatureLoader.Enabled<CommandBridgeFeature>()) return true;
                if (string.IsNullOrWhiteSpace(__0))
                {
                    return true;
                }

                string text = __0.Trim().TrimStart('/', '!').Trim();
                if (text.Length == 0)
                {
                    return true;
                }

                int space = text.IndexOf(' ');
                string name = space < 0 ? text : text.Substring(0, space);
                if (!IsOwnCommand(name))
                {
                    return true;                       // 不是本模块的命令 ⇒ 交还原版
                }

                string json;
                string display;
                try
                {
                    Execute(text, out json, out display);
                }
                catch (Exception ex)
                {
                    json = "{\"ok\":false,\"error\":" + Quote(ex.Message) + "}";
                    display = $"命令执行异常：{ex.Message}";
                }

                object result = MakeResult(json);
                if (result == null)
                {
                    // CommandResult 构造失败（上游结构又变了）⇒ 放行给原版：
                    // 它会按"未知命令"记一条警告，比让调用方拿到 null 安全。
                    global::ClueDetail.Core.Log.Warn(
                        "命令桥：构造 DT_Tools CommandResult 失败 ⇒ 本模块命令回包交给原版处理。");
                    return true;
                }

                __result = result;
                RelayToUpstreamLog(display);
                return false;
            }
        }

        // ══════════════════ 命令实现 ══════════════════

        /// <summary>解析并执行本模块的命令，产出「上游信封用的 JSON」与「给人看的一行文本」。</summary>
        private static void Execute(string text, out string json, out string display)
        {
            string[] parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            string commandName = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;
            if (commandName == "cd_impersonate")
            {
                ExecuteImpersonate(parts, out json, out display);
                return;
            }
            string target = parts.Length > 1 ? parts[1].ToLowerInvariant() : "status";
            string action = parts.Length > 2 ? parts[2].ToLowerInvariant() : "status";
            if (parts.Length <= 1)
            {
                action = "status";
            }

            if (target == "status" || action == "status")
            {
                json = "{\"ok\":true,\"message\":" + Quote(StatusText()) + "}";
                display = StatusText();
                return;
            }

            bool? on = action == "on" ? true : action == "off" ? false : (bool?)null;
            if (on == null)
            {
                json = "{\"ok\":false,\"error\":\"bad-arg\"}";
                display = $"无法识别的动作「{action}」，只能是 on / off / status。";
                return;
            }

            if (target != "clock" && target != "identity" && target != "both")
            {
                json = "{\"ok\":false,\"error\":\"bad-target\"}";
                display = $"无法识别的目标「{target}」，只能是 clock / identity / both / status。";
                return;
            }

            var sb = new StringBuilder();
            bool ok = true;
            if (target == "clock" || target == "both")
            {
                ok &= Apply(global::ClueDetail.Features.Host.ShadowSabotage.BreakerKind.Clock, on.Value, sb);
            }

            if (target == "identity" || target == "both")
            {
                ok &= Apply(global::ClueDetail.Features.Host.ShadowSabotage.BreakerKind.Identify, on.Value, sb);
            }

            sb.Append(StatusText());
            string message = sb.ToString();
            json = "{\"ok\":" + (ok ? "true" : "false") + ",\"message\":" + Quote(message) + "}";
            display = message;
        }

        /// <summary>
        /// 调试：让「源玩家」的操作记为「目标玩家」（<c>cd_impersonate</c>）。
        /// </summary>
        /// <remarks>
        /// 只替换**源玩家**，不是所有人 —— 其他人照常记在自己头上。
        /// </remarks>
        private static void ExecuteImpersonate(string[] parts, out string json, out string display)
        {
            string sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "status";

            if (sub == "off" || sub == "stop")
            {
                ImpersonateState.Stop();
                display = "已关闭冒名。";
                json = "{\"ok\":true,\"message\":" + Quote(display) + "}";
                return;
            }

            if (sub == "status")
            {
                display = ImpersonateState.Active
                    ? $"冒名中：#{ImpersonateState.SourceId} → #{ImpersonateState.TargetId}，" +
                      $"剩余 {ImpersonateState.RemainSeconds:F0} 秒。"
                    : "当前没有冒名。";
                json = "{\"ok\":true,\"message\":" + Quote(display) + "}";
                return;
            }

            if (parts.Length < 3
                || !int.TryParse(parts[1], out int sourceId)
                || !int.TryParse(parts[2], out int targetId)
                || sourceId <= 0 || targetId <= 0 || sourceId == targetId)
            {
                display = "用法：cd_impersonate <源玩家ID> <目标玩家ID> [秒] ｜ off ｜ status";
                json = "{\"ok\":false,\"error\":\"bad-arg\",\"message\":" + Quote(display) + "}";
                return;
            }

            float seconds = ImpersonateState.DefaultSeconds;
            if (parts.Length > 3 && float.TryParse(parts[3], out float s) && s > 0f)
            {
                seconds = s;
            }

            // ⚠️ 2026-10-20：目标必须**真实存在**，在这里就解析出来并持有引用。
            // 旧写法每次替换时现找，找不到静默跳过 —— 实机结果是「回执说开了、日志里
            // 一条 [2 / …] 都没有」，根本看不出是没开还是 ID 对不上。
            ServerGameRoom room = ServerGameRoom.Instance;
            ServerGamePlayer target = null;
            var available = new StringBuilder();
            if (room != null && room.Players != null)
            {
                for (int i = 0; i < room.Players.Count; i++)
                {
                    ServerGamePlayer p = room.Players[i];
                    if (p == null || p.PublicInfo == null)
                    {
                        continue;
                    }

                    if (available.Length > 0)
                    {
                        available.Append('、');
                    }

                    available.Append('#').Append(p.PublicInfo.PlayerId);

                    if (p.PublicInfo.PlayerId == targetId)
                    {
                        target = p;
                    }
                }
            }

            if (target == null)
            {
                display = $"房间里没有 #{targetId}，冒名未开启。现有玩家：{(available.Length > 0 ? available.ToString() : "无")}";
                json = "{\"ok\":false,\"error\":\"no-such-player\",\"message\":" + Quote(display) + "}";
                return;
            }

            ImpersonateState.Start(sourceId, targetId, seconds, target);
            display = $"已开启：接下来 {seconds:F0} 秒内，#{sourceId} 的操作会记为 #{targetId}。";
            json = "{\"ok\":true,\"message\":" + Quote(display) + "}";
        }

        private static bool Apply(
            global::ClueDetail.Features.Host.ShadowSabotage.BreakerKind kind, bool broken, StringBuilder sb)
        {
            string message;
            bool ok = global::ClueDetail.Features.Host.ShadowSabotage.ShadowSabotageFeature.ForceToggle(kind, broken, out message);
            sb.Append(ok ? "✔ " : "✘ ").Append(message).Append('\n');
            return ok;
        }

        /// <summary>当前两个破坏点的状态（只读）。</summary>
        private static string StatusText()
        {
            return global::ClueDetail.Features.Host.ShadowSabotage.ShadowSabotageFeature.StatusText();
        }

        // ══════════════════ 上游接线 ══════════════════

        /// <summary>反射构造上游的 <c>CommandResult</c>（<c>Success(object)</c>）。</summary>
        private static object MakeResult(string json)
        {
            Type type = AccessTools.TypeByName(DtCommandResultTypeName);
            if (type == null)
            {
                return null;
            }

            object data = TryParseJson(json);
            return AccessTools.Method(type, "Success", new[] { typeof(object) })?.Invoke(null, new[] { data });
        }

        /// <summary>
        /// 结果同时写进上游日志流（tag: ClueDetail），使 WebUI 的日志 / 终端区看得到 ——
        /// 只填 <c>ResultJson</c> 的话脚本拿得到、人看着像"什么都没返回"（捉迷藏踩过同一个坑）。
        /// </summary>
        private static void RelayToUpstreamLog(string text)
        {
            try
            {
                Type log = AccessTools.TypeByName(DtLogTypeName);
                MethodInfo info = log == null
                    ? null
                    : AccessTools.Method(log, "Info", new[] { typeof(string), typeof(string) });
                info?.Invoke(null, new object[] { "ClueDetail", text });
            }
            catch
            {
                // 日志写入失败不影响命令结果
            }
        }

        /// <summary>
        /// 重建上游命令表缓存：**上游自己的命令表 + 本模块命令**。
        /// </summary>
        /// <remarks>
        /// 上游 <c>CommandsApi.HandleList</c> 只在首次请求时生成 <c>_cachedJson</c>（进程内恒定），
        /// 我们的补丁比它晚挂 ⇒ 这里先逼它生成一次（传 null 上下文：它会先写缓存、
        /// 再在写响应时抛异常，异常被我们吞掉），再做**纯文本追加** ——
        /// 这样保留上游的原样形状，不需要 JSON 解析库。
        /// </remarks>
        internal static void RefreshCommandList()
        {
            try
            {
                if (!FeatureLoader.Enabled<CommandBridgeFeature>())
                {
                    global::ClueDetail.Core.Log.Info(
                        "命令桥未启用（[CommandBridge].Enabled = false）⇒ 本模块命令不接入 DT_Tools 控制台。");
                    return;
                }

                Type api = AccessTools.TypeByName(DtCommandsApiTypeName);
                FieldInfo field = api == null
                    ? null
                    : AccessTools.Field(api, "_cachedJson");
                if (field == null)
                {
                    global::ClueDetail.Core.Log.Warn(
                        "命令桥：找不到 DT_Tools 命令表缓存 CommandsApi._cachedJson，" +
                        "本模块命令不会出现在列表里（仍可执行）。");
                    return;
                }

                string current = field.GetValue(null) as string;
                if (string.IsNullOrEmpty(current))
                {
                    try
                    {
                        AccessTools.Method(api, "HandleList")?.Invoke(null, new object[] { null });
                    }
                    catch
                    {
                        // 上游写响应时必然抛（ctx 为 null）—— 缓存这时已经写好了
                    }

                    current = field.GetValue(null) as string;
                }

                if (string.IsNullOrEmpty(current))
                {
                    global::ClueDetail.Core.Log.Warn(
                        "命令桥：上游命令表缓存仍为空，跳过列表注入（本模块命令仍可执行）。");
                    return;
                }

                if (current.Contains("\"" + Commands[0].Name + "\""))
                {
                    return;                            // 已注入过（热重载 / 重复调用）
                }

                int end = current.LastIndexOf(']');
                if (end < 0)
                {
                    return;
                }

                var sb = new StringBuilder(current.Substring(0, end));
                bool first = current.TrimEnd().EndsWith("[", StringComparison.Ordinal);
                for (int i = 0; i < Commands.Length; i++)
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    sb.Append("{\"name\":").Append(Quote(Commands[i].Name)).Append(",\"aliases\":[");
                    string[] aliases = Commands[i].Aliases;
                    for (int j = 0; aliases != null && j < aliases.Length; j++)
                    {
                        if (j > 0)
                        {
                            sb.Append(',');
                        }

                        sb.Append(Quote(aliases[j]));
                    }

                    sb.Append("],\"usage\":").Append(Quote(Commands[i].Usage))
                      .Append(",\"description\":").Append(Quote(Commands[i].Description))
                      .Append(",\"author\":\"ClueDetail\"}");
                }

                sb.Append(']');
                field.SetValue(null, sb.ToString());
                global::ClueDetail.Core.Log.Info(
                    $"命令桥：已重建 DT_Tools 命令表缓存，{Commands.Length} 条本模块命令进入列表与补全。");
            }
            catch (Exception ex)
            {
                global::ClueDetail.Core.Log.Warn($"命令桥：重建命令列表失败 — {ex.Message}");
            }
        }

        /// <summary>优先用 Newtonsoft 解析成对象；取不到就交回原字符串（结果不丢）。</summary>
        private static object TryParseJson(string json)
        {
            try
            {
                Type token = AccessTools.TypeByName("Newtonsoft.Json.Linq.JToken");
                MethodInfo parse = token == null
                    ? null
                    : AccessTools.Method(token, "Parse", new[] { typeof(string) });
                if (parse != null)
                {
                    return parse.Invoke(null, new object[] { json });
                }
            }
            catch
            {
                // 解析失败走字符串回退
            }

            return json;
        }

        /// <summary>JSON 字符串字面量（只处理 <c>\</c> 与 <c>"</c>，够用且不引依赖）。</summary>
        private static string Quote(string value)
        {
            if (value == null)
            {
                return "\"\"";
            }

            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '"' || c == '\\')
                {
                    sb.Append('\\');
                }

                if (c == '\n')
                {
                    sb.Append("\\n");
                    continue;
                }

                if (c == '\r')
                {
                    continue;
                }

                sb.Append(c);
            }

            sb.Append('"');
            return sb.ToString();
        }
    }

        /// <summary>
        /// 调试（命令桥）：把**源玩家**的 <c>RecordLastUsingPlayer</c> 换成目标玩家。
        /// </summary>
        /// <remarks>
        /// ⚠️ 只对 <see cref="ImpersonateState.SourceId"/> 生效，其他人不受影响。
        /// 挂在所有「任务 / 设备使用」的唯一收口上，换掉 <c>player</c> 后整条链路
        /// （合并窗口、活动标记、污染标记、出站改写）都会当成目标玩家做的。
        /// </remarks>
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(ServerDevice), nameof(ServerDevice.RecordLastUsingPlayer))]
        internal static class ImpersonateHook
        {
            [HarmonyPrefix]
            private static void Prefix(ref ServerGamePlayer player)
            {
                if (!FeatureLoader.Enabled<CommandBridgeFeature>()) return;
                if (player == null || player.PublicInfo == null) return;
                if (!ImpersonateState.AppliesTo(player.PublicInfo.PlayerId)) return;

                // 直接用开启命令时解析好的引用（不再每次遍历 room.Players）。
                // Active 已经保证了引用有效；这里再兜一次，失效就让状态自动关闭。
                ServerGamePlayer target = ImpersonateState.Target;
                if (target == null || target.PublicInfo == null)
                {
                    ImpersonateState.Stop();
                    return;
                }

                player = target;
            }
        }
}