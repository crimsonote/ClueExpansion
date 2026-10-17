using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;

namespace ClueDetail.Core.Dt
{
    /// <summary>
    /// 读写 <see cref="ConfigFile"/> 的条目表 —— 把本模块的配置**镜像**进 DT_Tools 的 ConfigFile，
    /// 使它们出现在 DT 的 CONFIG 页（见 <see cref="DtBridge.TryMirror"/>）。
    /// </summary>
    /// <remarks>
    /// 关于反射：BepInEx 5.4 的 <c>Entries</c> 是 <c>protected</c> 属性（编译期不可见），
    /// 底层是私有只读字段 <c>&lt;Entries&gt;k__BackingField</c>。这里只取**该字典实例**、
    /// 只增删内容、不替换引用。实测口径（沿用 HideAndSeek 在真 BepInEx 5.4.23.5 上的结论）：
    /// <list type="bullet">
    /// <item><c>ConfigFile.Keys</c> 与索引器都读这个字典 ⇒ 注入即被枚举到；</item>
    /// <item>注入的是**同一批 ConfigEntry 对象** ⇒ 两侧读写的是同一个值，不需要任何同步代码；</item>
    /// <item>⚠️ 代价：DT_Tools 的 <c>Save()</c> 会把这些条目一并写进**它自己的** cfg。</item>
    /// </list>
    /// <para>字段名若在某天变动，<see cref="Of"/> 返回 null，调用方只降级记 WARNING ——
    /// 本模块独立运行的能力不依赖这里。</para>
    /// </remarks>
    internal static class ConfigEntries
    {
        private static readonly FieldInfo EntriesField = typeof(ConfigFile)
            .GetField("<Entries>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>该配置文件的条目表；取不到时返回 null（BepInEx 内部结构变动）。</summary>
        public static Dictionary<ConfigDefinition, ConfigEntryBase> Of(ConfigFile file)
        {
            if (file == null || EntriesField == null)
            {
                return null;
            }

            return EntriesField.GetValue(file) as Dictionary<ConfigDefinition, ConfigEntryBase>;
        }
    }
}
