using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ClueDetail.Core;
using ClueDetail.Core.Dt;

namespace ClueDetail
{
    /// <summary>
    /// 入口，只做组装：打开自己的配置 → 装载功能补丁 → 落盘 → 报告。
    /// 不含任何玩法逻辑。
    /// </summary>
    [BepInPlugin(Guid, PluginName, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "ClueDetail";
        public const string PluginName = "ClueDetail";
        public const string Version = "0.1.0";

        public static Plugin Instance { get; private set; }

        /// <summary>本模块自己的 ConfigFile（BepInEx/config/ClueDetail.cfg）。</summary>
        public static ConfigFile Cfg { get; private set; }

        public static ManualLogSource Log { get; private set; }

        private void Awake()
        {
            Instance = this;
            Log = Logger;
        }

        private void Start()
        {
            string path = Path.Combine(Paths.ConfigPath, "ClueDetail.cfg");
            Cfg = new ConfigFile(path, true);

            // 启动期批量 Bind 不逐条落盘，全部 Bind 完成后统一 Save 一次。
            Cfg.SaveOnConfigSet = false;

            var result = FeatureLoader.Load(Cfg, Logger);

            Cfg.Save();
            Cfg.SaveOnConfigSet = true;

            // 与 DT_Tools 的可选集成：把上面这批条目镜像进它的 CONFIG 页。
            // 必须在**全部 Bind 之后**调用，否则会镜像进一批残缺条目。
            DtBridge.TryMirror(Cfg, Logger);

            Log.LogInfo(
                $"[ClueDetail] {Version} 加载完成：启用 {result.Enabled}，停用 {result.Disabled}，" +
                $"失败 {result.Failed}。配置：{path}。");

            if (result.Failed > 0)
            {
                Log.LogWarning(
                    "[ClueDetail] 有补丁没能挂上，对应功能会静默失效 —— 请把上面那几行错误发出来。");
            }
        }

        /// <summary>
        /// 主线程泵：把「待应用」的开关变更收掉（见 <see cref="FeatureLoader.ApplyPending"/>）。
        /// </summary>
        /// <remarks>
        /// 放在这里而不是 Harmony 补丁里，是因为 <c>BaseUnityPlugin</c> 本身就是 MonoBehaviour，
        /// Unity 会直接调这个方法 —— 不需要挂任何补丁，也就不会被「卸载自己」这件事反噬。
        /// <para>
        /// 游戏失焦 / 暂停时 Unity 不调 <c>Update</c>，开关变更会**延迟到恢复运行时**才生效
        /// （不是丢失）。这是刻意的：真正的挂 / 卸补丁只能在主线程做。
        /// </para>
        /// </remarks>
        private void Update()
        {
            FeatureLoader.ApplyPending();
        }
    }
}
