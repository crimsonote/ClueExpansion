﻿﻿﻿using System.Collections.Generic;
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
    /// ⚠️ **2026-10-19 用 Cecil 重核过部署版（游戏里是 DT_Tools 2.0.0.6）**，
    /// 更正两条早前的错误结论（都属于"查类型用全名精确匹配 + 过滤可见性"造成的假阴性）：
    /// <list type="bullet">
    /// <item>配置入口仍在 <c>DT_Tools.Core.Engine.Config</c>（public static 属性）⇒ 镜像可行；</item>
    /// <item><c>WebConsole.ExecuteCommandText(string)</c> **仍然存在**
    /// （<c>internal static CommandResult ExecuteCommandText(string)</c>）⇒ 命令桥可行，
    /// 见 <c>Features/Both/CommandBridge/</c>；</item>
    /// <item><c>Engine.SectionCategories</c>（旧版的"分类"）**确实已删除**，现在归组走
    /// <c>Engine.SectionTags : Dictionary&lt;string, FeatureTag&gt;</c> +
    /// <c>Engine.SectionGroups : Dictionary&lt;string, string&gt;</c>，
    /// 标签的显示信息来自 <c>FeatureTagCatalog.Entries</c>（<c>ConfigApi.HandleTags</c> 直接投影它）
    /// ⇒ 外来模块要自己**追加一个标签**并登记段，见 <see cref="TryRegisterTag"/>。</item>
    /// </list>
    /// </para>
    /// </remarks>
    internal static class DtBridge
    {
        private const string EngineTypeName = "DT_Tools.Core.Engine";

        private const string CatalogTypeName = "DT_Tools.Core.Attributes.FeatureTagCatalog";
        private const string TagEnumTypeName = "DT_Tools.Core.Attributes.FeatureTag";

        /// <summary>
        /// 本模块占用的标签位：<c>FeatureTag</c> 是 <c>[Flags]</c>，上游已定义到
        /// <c>Peripheral = 1 &lt;&lt; 38</c>，<c>1 &lt;&lt; 39</c> 空着。
        /// </summary>
        private const ulong OwnTagBit = 1UL << 39;

        /// <summary>新标签的键（ASCII，前端按它归类）。</summary>
        public const string OwnTagKey = "ClueDetail";

        /// <summary>新标签的显示名（配置页里的文件夹名）。</summary>
        public const string OwnTagLabel = "线索拓展";

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

            TryRegisterTag(ownEntries, log);
        }

        /// <summary>
        /// 往 DT 的标签体系里**追加一个「线索拓展」标签**，并把本模块的配置段都挂上去。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 上游的标签是编译期 <c>[Flags]</c> 枚举 + 装载期填的三张表，外来的段不在表里 ⇒
        /// 不补登记，本模块的段会全落在 CONFIG 页的「其他」里。这里做三件事（全部反射，不改上游一个字节）：
        /// </para>
        /// <list type="number">
        /// <item>给 <c>FeatureTagCatalog.Entries</c> 追加一条 <c>Entry</c>
        /// （<c>{Tag, Key, Group, Label, Description}</c>）—— 前端 <c>/api/config/tags</c> 直接投影它；</item>
        /// <item>把占用的位并进 <c>FeatureTagCatalog.AllBits</c>（上游用它判"是否是已定义标签"）；</item>
        /// <item>写 <c>Engine.SectionTags[段] = 本标签</c> 与 <c>Engine.SectionGroups[段] = 「线索拓展」</c>。</item>
        /// </list>
        /// <para>
        /// ⚠️ 用**未使用的高位**而不是自造枚举名：枚举成员无法在运行期新增，
        /// 但 <c>[Flags]</c> 值的"名字"是查 <c>Entries</c> 得到的 ⇒ 追加目录条目就等于新增了标签。
        /// </para>
        /// </remarks>
        private static void TryRegisterTag(
            Dictionary<ConfigDefinition, ConfigEntryBase> ownEntries, ManualLogSource log)
        {
            try
            {
                System.Type engine = AccessTools.TypeByName(EngineTypeName);
                System.Type catalog = AccessTools.TypeByName(CatalogTypeName);
                System.Type tagEnum = AccessTools.TypeByName(TagEnumTypeName);
                System.Type entryType = catalog?.GetNestedType("Entry");
                if (engine == null || catalog == null || tagEnum == null || entryType == null)
                {
                    log.LogWarning("[ClueDetail] DT_Tools 的标签体系结构与预期不符，跳过标签登记（配置仍可在「其他」里改）。");
                    return;
                }

                object ownTag = System.Enum.ToObject(tagEnum, (long)OwnTagBit);
                var sections = new HashSet<string>();
                if (ownEntries != null)
                {
                    foreach (var pair in ownEntries)
                    {
                        sections.Add(pair.Key.Section);
                    }
                }

                if (sections.Count == 0)
                {
                    return;
                }

                // ① 标签目录 + AllBits
                //
                // ⚠️ Unity Mono 的 `FieldInfo.GetValue` **不会**触发 `beforefieldinit` 类型的静态构造：
                // 直接读 `Entries` 可能得到 null，随后「读不到旧值 → 只追加自己 → 写回」
                // 就会把上游的全部条目覆盖成我们这一条（HideAndSeek 用 `PrimeTagCatalog` 规避）。
                // 所以这里先手动跑一次静态构造，再读；仍然拿不到就**放弃登记**，绝不写入。
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(catalog.TypeHandle);

                FieldInfo entriesField = catalog.GetField("Entries", BindingFlags.Public | BindingFlags.Static);
                object oldEntries = entriesField?.GetValue(null);
                if (entriesField == null || oldEntries == null)
                {
                    log.LogWarning("[ClueDetail] DT 标签目录未初始化（Entries 为 null），跳过标签登记以免覆盖上游条目；" +
                                   "配置仍可在 CONFIG 页的「其他」里修改。");
                    return;
                }

                if (!ContainsTag(oldEntries, ownTag))
                {
                    object entry = System.Activator.CreateInstance(entryType);
                    SetMember(entryType, entry, "Tag", ownTag);
                    SetMember(entryType, entry, "Key", OwnTagKey);
                    SetMember(entryType, entry, "Group", OwnTagLabel);
                    SetMember(entryType, entry, "Label", OwnTagLabel);
                    SetMember(entryType, entry, "Description",
                        "ClueDetail 模块：详细活动还原、线索卡片、隐蔽破坏点与线索污染。");

                    var list = (System.Collections.IList)System.Activator.CreateInstance(
                        typeof(List<>).MakeGenericType(entryType));
                    if (oldEntries is System.Collections.IEnumerable old)
                    {
                        foreach (object item in old)
                        {
                            list.Add(item);
                        }
                    }

                    list.Add(entry);
                    entriesField.SetValue(null, list);

                    FieldInfo allBits = catalog.GetField("AllBits", BindingFlags.Public | BindingFlags.Static);
                    if (allBits != null && allBits.GetValue(null) is ulong bits)
                    {
                        allBits.SetValue(null, bits | OwnTagBit);
                    }
                }

                // ② 段 → 标签 / 分组
                int tagged = WriteSectionMap(engine, "SectionTags", sections, ownTag);
                WriteSectionMap(engine, "SectionGroups", sections, OwnTagLabel);
                log.LogInfo($"[ClueDetail] 已登记 DT 标签「{OwnTagLabel}」（{OwnTagKey}），" +
                            $"本模块 {tagged} 个配置段归入该标签。");
            }
            catch (System.Exception ex)
            {
                log.LogWarning($"[ClueDetail] 标签登记失败（{ex.GetType().Name}: {ex.Message}）；" +
                               "配置仍可在 CONFIG 页的「其他」里修改。");
            }
        }

        /// <summary>目录里是否已经有这个标签（热重载时避免重复追加）。</summary>
        private static bool ContainsTag(object entries, object ownTag)
        {
            if (entries is System.Collections.IEnumerable list && ownTag != null)
            {
                foreach (object item in list)
                {
                    if (item == null)
                    {
                        continue;
                    }

                    FieldInfo tag = item.GetType().GetField("Tag");
                    if (tag != null && Equals(tag.GetValue(item), ownTag))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static void SetMember(System.Type type, object target, string name, object value)
        {
            FieldInfo field = type.GetField(name);
            if (field != null)
            {
                field.SetValue(target, value);
                return;
            }

            PropertyInfo property = type.GetProperty(name);
            property?.SetValue(target, value);
        }

        /// <summary>写 <c>Engine</c> 里的私有静态字典（<c>Dictionary&lt;string, X&gt;</c>）。</summary>
        private static int WriteSectionMap(System.Type engine, string fieldName, HashSet<string> sections, object value)
        {
            FieldInfo field = engine.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            if (!(field?.GetValue(null) is System.Collections.IDictionary map))
            {
                return 0;
            }

            int count = 0;
            foreach (string section in sections)
            {
                map[section] = value;
                count++;
            }

            return count;
        }
    }
}
