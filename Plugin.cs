using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ClueDetail.Core;
using HarmonyLib;

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

            var result = FeatureLoader.Load(new Harmony(Guid), Cfg, Logger);

            Cfg.Save();
            Cfg.SaveOnConfigSet = true;

            Log.LogInfo(
                $"[ClueDetail] {Version} 加载完成：启用 {result.Enabled}，跳过 {result.Skipped}，失败 {result.Failed}。" +
                $"配置：{path}。");
        }
    }
}
