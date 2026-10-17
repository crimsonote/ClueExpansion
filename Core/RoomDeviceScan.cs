using System;
using System.Collections.Generic;
using Protocol;

namespace ClueDetail.Core
{
    /// <summary>
    /// 按「线索所在房间」反推该房间里有哪种设备活动 —— 房主未装本模块时的主力推断手段。
    /// </summary>
    /// <remarks>
    /// 数据全部来自客户端本就有的静态表，不需要任何网络交互：
    /// <list type="bullet">
    /// <item>线索的 <c>RoomId</c> → <c>Managers.Data.RoomDic[RoomId].Type</c>（<c>ERoomType</c>）；</item>
    /// <item>设备表 <c>Managers.Data.DeviceDic</c> 里带 <c>RoomType</c> 与 <c>WhatType</c>。</item>
    /// </list>
    /// 过滤口径与 0.1.18 服务端 <c>RecordLastUsingPlayer</c> 一致：只认 <c>WhatType != WNone</c>
    /// （这也是同文件 <c>ClueManager.InitBlackPropositionData</c> 判定"有设备的房间"时的口径）。
    /// <para>
    /// 结果是**去重后**的活动集合：房间里有三台终端机也只算一种活动，能确定；
    /// 若房间里有两种以上不同活动，就无法判明是哪一种 —— 调用方应退化为通用文案。
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
                if (data == null || data.RoomDic == null || data.DeviceDic == null)
                {
                    return result;
                }

                if (!data.RoomDic.TryGetValue(roomId, out var room) || room == null)
                {
                    return result;
                }

                ERoomType roomType = room.Type;
                foreach (var pair in data.DeviceDic)
                {
                    var device = pair.Value;
                    if (device == null || device.RoomType != roomType)
                    {
                        continue;
                    }

                    EWhatType what = device.WhatType;
                    if (what == EWhatType.WNone || result.Contains(what))
                    {
                        continue;
                    }

                    result.Add(what);
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "按房间推断设备活动失败");
            }

            return result;
        }
    }
}
