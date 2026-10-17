using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Protocol;

namespace ClueDetail.Core
{
    /// <summary>
    /// 在 <see cref="PropositionInfo"/> 的 protobuf 未知字段区读写「详细活动」（EWhatType）。
    /// </summary>
    /// <remarks>
    /// 背景：0.1.18 删除了 <c>PropositionInfo.WhatType</c>（原字段号 4）且**未复用**该号。
    /// protobuf 的 wire 规则要求解析方原样保留未定义字段，所以：
    /// <list type="bullet">
    /// <item>服务端把 EWhatType 写进字段 4 → 序列化后随包下发；</item>
    /// <item>0.1.18 原版客户端解析时把它丢进自己的 _unknownFields 并忽略 —— **不会报错、不改变任何原版行为**；</item>
    /// <item>装了本模块的客户端用这里的 GetWhat 读回。</item>
    /// </list>
    /// 这条通道不需要新增 opcode，也不污染任何原版字段（RoomId / PlayerId / Time 保持原值）。
    /// <para>
    /// 实现约束：<c>Google.Protobuf.UnknownFieldSet</c> 是 public，但它返回的
    /// <c>UnknownField</c> 是 internal，因此只能用反射链（GetOrAddField → AddVarint）。
    /// 已用 0.1.18 的 Mono.Cecil/反射核对过成员名：_unknownFields / fields / GetOrAddField / AddVarint / varintList。
    /// </para>
    /// </remarks>
    internal static class UnknownFields
    {
        public const int WhatFieldNumber = 4;

        private static readonly FieldInfo SetField;
        private static readonly Type SetType;
        private static readonly FieldInfo FieldsField;
        private static readonly MethodInfo GetOrAddField;

        static UnknownFields()
        {
            try
            {
                SetField = typeof(PropositionInfo).GetField("_unknownFields", BindingFlags.Instance | BindingFlags.NonPublic);
                if (SetField == null)
                {
                    return;
                }

                SetType = SetField.FieldType;
                FieldsField = SetType.GetField("fields", BindingFlags.Instance | BindingFlags.NonPublic);
                GetOrAddField = SetType.GetMethod("GetOrAddField", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "反射定位 protobuf 未知字段失败");
            }
        }

        public static bool Available => SetField != null && SetType != null && FieldsField != null && GetOrAddField != null;

        /// <summary>把详细活动写进未知字段（对原版客户端无副作用）。</summary>
        public static bool SetWhat(PropositionInfo info, int what)
        {
            if (info == null || what == 0 || !Available)
            {
                return false;
            }

            // 幂等：未知字段的 varintList 是追加语义，重复写会变成两个值。
            if (GetWhat(info) != 0)
            {
                return false;
            }

            try
            {
                object set = SetField.GetValue(info) ?? Activator.CreateInstance(SetType, true);
                object field = GetOrAddField.Invoke(set, new object[] { WhatFieldNumber });
                if (field == null)
                {
                    return false;
                }

                MethodInfo addVarint = field.GetType().GetMethod("AddVarint", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (addVarint == null)
                {
                    return false;
                }

                addVarint.Invoke(field, new object[] { (ulong)what });
                SetField.SetValue(info, set);
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "写入线索详细活动失败");
                return false;
            }
        }

        /// <summary>读回详细活动；没有则返回 0（EWhatType.WNone）。</summary>
        public static int GetWhat(PropositionInfo info)
        {
            if (info == null || !Available)
            {
                return 0;
            }

            try
            {
                object set = SetField.GetValue(info);
                if (set == null)
                {
                    return 0;
                }

                if (!(FieldsField.GetValue(set) is IDictionary fields) || !fields.Contains(WhatFieldNumber))
                {
                    return 0;
                }

                object field = fields[WhatFieldNumber];
                FieldInfo listField = field.GetType().GetField("varintList", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!(listField?.GetValue(field) is IList values) || values.Count == 0)
                {
                    return 0;
                }

                return (int)Convert.ToUInt64(values[0]);
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "读取线索详细活动失败");
                return 0;
            }
        }
    }
}
