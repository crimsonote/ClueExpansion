using System;
using System.Collections.Generic;
using Protocol;

namespace ClueDetail.Core
{
    /// <summary>
    /// 按「线索所在房间」反推该房间里有哪种设备活动 —— 房主未装本模块时的最后一级推断手段。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 数据全部来自客户端本就有的静态表与本局设备实例，不需要任何网络交互：
    /// <list type="bullet">
    /// <item>线索的 <c>RoomId</c> → <c>Managers.Data.RoomDic[RoomId].Type</c>（<c>ERoomType</c>）；</item>
    /// <item>候选设备优先取**本局实际生成**的设备（<c>Managers.Device.Cache</c>，0.1.21 :27621），
    /// 用 <c>DeviceBase.Data.RoomType</c> 与房间类型比对；</item>
    /// <item>设备实例表还没建立（进图前 / 自检早期）时，退回全局静态表 <c>Managers.Data.DeviceDic</c>。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 过滤口径与 0.1.18 服务端 <c>RecordLastUsingPlayer</c> 一致：只认 <c>WhatType != WNone</c>
    /// （这也是同文件 <c>ClueManager.InitBlackPropositionData</c> 判定"有设备的房间"时的口径）。
    /// </para>
    /// <para>
    /// 结果是**去重后**的活动集合：房间里有三台终端机也只算一种活动，能确定；
    /// 若房间里有两种以上不同活动，就无法判明是哪一种 —— 调用方应退化为通用文案。
    /// ⚠️ 这是**信息维度**上的上限，不是本文件能修好的：房主没装本模块时协议里没有设备号，
    /// 房间里有几台设备、分别是什么活动，客户端只能从这一层拿到「这个房间类型下有哪些活动」。
    /// </para>
    /// <para>
    /// <b>2026-11-14 改</b>：原来只遍历全局 <c>DeviceDic</c>（整份数据表，含本局没生成的设备、
    /// 以及同类型的所有房间实例），候选偏多；现在优先用本局实际设备实例。实测数据下
    /// 单房间仍有 2~6 种活动（例如 100004 一个房间 5 种），所以真正提高命中率要靠
    /// 「线索 → 设备」关联（<see cref="ActivityMap"/>）而不是这里。
    /// </para>
    /// </remarks>
    internal static class RoomDeviceScan
    {
        public static List<EWhatType> CandidatesOf(int roomId)
        {
            var result = new List<EWhatType>();
            if (roomId == 0)
            {
                return result;
            }

            try
            {
                var data = Managers.Data;
                if (data == null || data.RoomDic == null)
                {
                    return result;
                }

                if (!data.RoomDic.TryGetValue(roomId, out var room) || room == null)
                {
                    return result;
                }

                ERoomType roomType = room.Type;

                // 优先：本局实际生成的设备实例（Managers.Device 是客户端 DeviceManager，
                // Cache 是 public Dictionary<int, DeviceBase>，0.1.21 :27619-27621）。
                var cache = Managers.Device != null ? Managers.Device.Cache : null;
                if (cache != null && cache.Count > 0)
                {
                    foreach (var pair in cache)
                    {
                        var device = pair.Value;
                        var deviceData = device != null ? device.Data : null;
                        if (deviceData == null || deviceData.RoomType != roomType)
                        {
                            continue;
                        }

                        Add(result, deviceData.WhatType);
                    }

                    if (result.Count > 0)
                    {
                        return result;
                    }
                }

                // 兜底：静态设备表（设备实例还没建立时，或者本局设备表里该房间类型为空）。
                if (data.DeviceDic == null)
                {
                    return result;
                }

                foreach (var pair in data.DeviceDic)
                {
                    var device = pair.Value;
                    if (device == null || device.RoomType != roomType)
                    {
                        continue;
                    }

                    Add(result, device.WhatType);
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "按房间推断设备活动失败");
            }

            return result;
        }

        /// <summary>
        /// 加入一种活动；排除 <c>WNone</c> 与 <c>WFusebox</c>。
        /// </summary>
        /// <remarks>
        /// ⚠️ 排除 <c>WFusebox</c> 沿用旧版构「房间活动候选」的口径（0.1.17a :27539 的条件是
        /// <c>value.WhatType != WNone &amp;&amp; value.WhatType != WFusebox</c>）。
        /// 理由：电闸那个活动是角色级动作（切断了电力），不该被当成房间里某台设备的活动贴到线索上。
        /// </remarks>
        private static void Add(List<EWhatType> result, EWhatType what)
        {
            if (what == EWhatType.WNone || what == EWhatType.WFusebox || result.Contains(what))
            {
                return;
            }

            result.Add(what);
        }
    }
}
