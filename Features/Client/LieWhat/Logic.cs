using System.Collections.Generic;
using ClueDetail.Core;
using Data;
using Protocol;
using TMPro;
using UnityEngine;

namespace ClueDetail.Features.Client.LieWhat
{
    /// <summary>
    /// 伪证页那个「详细活动」下拉的建立与刷新。
    /// </summary>
    /// <remarks>
    /// 控件是**克隆**界面上已有的下拉（who / hour / minute 三者之一），不自己拼 prefab；
    /// 克隆后必须 <c>RemoveAllListeners()</c>，否则会连带触发原版下拉的回调（AGENTS.md 铁律 8）。
    /// </remarks>
    internal static class LieWhatDropdown
    {
        private const string ObjectName = "ClueDetailWhatDropdown";

        private static readonly List<EWhatType> Options = new List<EWhatType>();

        /// <summary>下拉是否已经建出来了（自检用）。</summary>
        public static bool IsBuilt => _dropdown != null;

        private static UI_GameTablet _tablet;
        private static TMP_Dropdown _dropdown;
        private static bool _failedLogged;

        public static void Build(UI_GameTablet tablet)
        {
            if (tablet == null || _dropdown != null)
            {
                return;
            }

            TMP_Dropdown template = FindTemplate(tablet);
            if (template == null)
            {
                WarnOnce("界面上找不到可作模板的下拉");
                return;
            }

            _tablet = tablet;
            _dropdown = Object.Instantiate(template, template.transform.parent);
            _dropdown.name = ObjectName;
            _dropdown.gameObject.SetActive(true);
            _dropdown.onValueChanged.RemoveAllListeners();
            _dropdown.onValueChanged.AddListener(OnChanged);

            RefreshOptions(null);
            global::ClueDetail.Core.Log.Info("伪证界面已加入「详细活动」下拉。");
        }

        /// <summary>玩家在地图上点了某个区域（选房间）后刷新选项。</summary>
        public static void Refresh(UI_GameTablet tablet, RoomData room)
        {
            if (_dropdown == null)
            {
                Build(tablet);
                if (_dropdown == null)
                {
                    return;
                }
            }

            RefreshOptions(room);
        }

        private static void RefreshOptions(RoomData room)
        {
            if (_dropdown == null)
            {
                return;
            }

            Options.Clear();
            var labels = new List<string>();

            if (room != null)
            {
                foreach (EWhatType what in RoomDeviceScan.CandidatesOf(room.DataId))
                {
                    string text = ClueWhat.TextOf(what);
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    labels.Add(text);
                    Options.Add(what);
                }
            }

            if (labels.Count == 0)
            {
                // 与旧版一致：没有可选活动时给一个占位项，选中它等于不指定活动。
                labels.Add("-");
                Options.Add(EWhatType.WNone);
            }

            _dropdown.ClearOptions();
            _dropdown.AddOptions(labels);
            Apply(0);
        }

        private static void OnChanged(int index)
        {
            Apply(index);
        }

        private static void Apply(int index)
        {
            if (index < 0 || index >= Options.Count)
            {
                return;
            }

            EWhatType what = Options[index];
            if (what == EWhatType.WNone)
            {
                return;
            }

            // 直接写进伪证线索的未知字段：提交时原版会 Clone.Info 发出去，
            // 而 Clone 会一并复制未知字段，所以服务端与其它客户端都收得到。
            var info = _tablet?.LieClue?.Clue?.Info;
            if (info == null)
            {
                return;
            }

            UnknownFields.ReplaceWhat(info, (int)what);
        }

        private static TMP_Dropdown FindTemplate(UI_GameTablet tablet)
        {
            TMP_Dropdown[] all = tablet.GetComponentsInChildren<TMP_Dropdown>(true);
            for (int i = 0; i < all.Length; i++)
            {
                TMP_Dropdown candidate = all[i];
                if (candidate != null && candidate.name != ObjectName)
                {
                    return candidate;
                }
            }

            return null;
        }

        private static void WarnOnce(string message)
        {
            if (_failedLogged)
            {
                return;
            }

            _failedLogged = true;
            global::ClueDetail.Core.Log.Warn("伪证下拉未能建立：" + message + "（不影响其它功能）。");
        }
    }
}
