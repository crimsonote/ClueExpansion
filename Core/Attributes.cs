using System;

namespace ClueDetail.Core
{
    /// <summary>补丁实际生效的机器角色（仅用于文档与界面，没有运行时语义）。</summary>
    internal enum FeatureSide
    {
        Client,
        Host,
        Both,
    }

    /// <summary>
    /// 标记一个功能类。类所在命名空间下（含嵌套类型）的全部 Harmony 补丁由
    /// <see cref="FeatureLoader"/> 统一挂载 / 跳过。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    internal sealed class PatchFeatureAttribute : Attribute
    {
        public readonly string Section;
        public readonly string Description;
        public readonly bool DefaultEnabled;
        public readonly FeatureSide Side;

        public PatchFeatureAttribute(string section, string description, bool defaultEnabled = true, FeatureSide side = FeatureSide.Both)
        {
            Section = section;
            Description = description;
            DefaultEnabled = defaultEnabled;
            Side = side;
        }
    }
}
