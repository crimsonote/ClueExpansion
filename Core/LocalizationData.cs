using System.Collections.Generic;

namespace ClueDetail.Core
{
    /// <summary>
    /// 0.1.18 从语言表整体移除的设备活动词条（**全量 49 条**）。
    /// </summary>
    /// <remarks>
    /// `EWhatType` 共 50 个成员（0–49）；`WNone`(0) 不需要文案 ⇒ 需要 49 条。
    /// 前 46 条（0–46）由 .tmps/gamedata/TextData.json 里 DataId 匹配 ^W[A-Z] 的行生成；
    /// 后 3 条（`WMorse` / `WFishing` / `WChat`）是 0.1.18 新增的设备类型，旧版基线里没有，
    /// 见文件末尾的注释（文案依据是对应的设备交互词条）。
    /// </remarks>
    internal static class LocalizationData
    {
        public static readonly Dictionary<string, Dictionary<string, string>> Entries =
            new Dictionary<string, Dictionary<string, string>>
            {
                { "WBroadcast", new Dictionary<string, string> { { "KOR", "방송장비를 사용했" }, { "ENG", "used the broadcast system" }, { "JPN", "放送装置を使った" }, { "CHS", "使用了广播设备" }, { "CHT", "使用了廣播設備" } } },
                { "WComputer", new Dictionary<string, string> { { "KOR", "단말기를 사용했" }, { "ENG", "used the terminal" }, { "JPN", "端末を使った" }, { "CHS", "使用了终端机" }, { "CHT", "使用了終端機" } } },
                { "WArmory", new Dictionary<string, string> { { "KOR", "흉기를 획득했" }, { "ENG", "obtained the weapon" }, { "JPN", "凶器を入手した" }, { "CHS", "获得了凶器" }, { "CHT", "獲得了凶器" } } },
                { "WBattery", new Dictionary<string, string> { { "KOR", "전력을 공급했" }, { "ENG", "supplied power" }, { "JPN", "電力を供給した" }, { "CHS", "供应了电力" }, { "CHT", "供應了電力" } } },
                { "WSurgery", new Dictionary<string, string> { { "KOR", "수술을 진행했" }, { "ENG", "performed surgery" }, { "JPN", "手術を行った" }, { "CHS", "进行了手术" }, { "CHT", "進行了手術" } } },
                { "WAlchemist", new Dictionary<string, string> { { "KOR", "솥을 사용했" }, { "ENG", "used the cauldron" }, { "JPN", "大釜を使った" }, { "CHS", "使用了坩埚" }, { "CHT", "使用了坩堝" } } },
                { "WCharger", new Dictionary<string, string> { { "KOR", "배터리를 충전했" }, { "ENG", "charged the battery" }, { "JPN", "バッテリーを充電した" }, { "CHS", "给电池充了电" }, { "CHT", "給電池充了電" } } },
                { "WDrink", new Dictionary<string, string> { { "KOR", "음료를 제조했" }, { "ENG", "brewed a drink" }, { "JPN", "飲料を製造した" }, { "CHS", "制作了饮料" }, { "CHT", "製作了飲料" } } },
                { "WManikin", new Dictionary<string, string> { { "KOR", "인체모형을 가져갔" }, { "ENG", "took the mannequin" }, { "JPN", "人体模型を持ち去った" }, { "CHS", "拿走了人体模型" }, { "CHT", "拿走了人體模型" } } },
                { "WShelf", new Dictionary<string, string> { { "KOR", "선반을 사용했" }, { "ENG", "used shelf" }, { "JPN", "棚を使った" }, { "CHS", "使用了架子" }, { "CHT", "使用了架子" } } },
                { "WFlower", new Dictionary<string, string> { { "KOR", "화분을 사용했" }, { "ENG", "used the flowerpot" }, { "JPN", "鉢植えを使った" }, { "CHS", "使用了花盆" }, { "CHT", "使用了花盆" } } },
                { "WOperatingDoor", new Dictionary<string, string> { { "KOR", "문을 작동시켰" }, { "ENG", "operated the door" }, { "JPN", "ドアを操作した" }, { "CHS", "开启了门" }, { "CHT", "開啟了門" } } },
                { "WMiner", new Dictionary<string, string> { { "KOR", "채광기를 조작했" }, { "ENG", "operated the light core" }, { "JPN", "ライトコアを操作した" }, { "CHS", "操作了采矿机" }, { "CHT", "操作了採礦機" } } },
                { "WScanner", new Dictionary<string, string> { { "KOR", "꽃을 스캔했" }, { "ENG", "scanned a flower" }, { "JPN", "花をスキャンした" }, { "CHS", "扫描了花朵" }, { "CHT", "掃描了花朵" } } },
                { "WCompounder", new Dictionary<string, string> { { "KOR", "배합기를 사용했" }, { "ENG", "used the compounder" }, { "JPN", "調合器を使った" }, { "CHS", "使用了配合机" }, { "CHT", "使用了配合機" } } },
                { "WCancer", new Dictionary<string, string> { { "KOR", "종양을 제거했" }, { "ENG", "removed the tumor" }, { "JPN", "腫瘍を除去した" }, { "CHS", "切除了肿瘤" }, { "CHT", "切除了腫瘤" } } },
                { "WBoiler", new Dictionary<string, string> { { "KOR", "온도를 조절했" }, { "ENG", "adjusted the temperature" }, { "JPN", "温度を調節した" }, { "CHS", "调节了温度" }, { "CHT", "調節了溫度" } } },
                { "WCollector", new Dictionary<string, string> { { "KOR", "광석을 배치했" }, { "ENG", "placed the ore" }, { "JPN", "鉱石を配置した" }, { "CHS", "放置了矿石" }, { "CHT", "放置了礦石" } } },
                { "WCraft", new Dictionary<string, string> { { "KOR", "광석을 가공했" }, { "ENG", "carved the ore" }, { "JPN", "鉱石を架空した" }, { "CHS", "加工了矿石" }, { "CHT", "加工了礦石" } } },
                { "WMineral", new Dictionary<string, string> { { "KOR", "광석을 채굴했" }, { "ENG", "mined the ore" }, { "JPN", "鉱石を採掘した" }, { "CHS", "采掘了矿石" }, { "CHT", "採掘了礦石" } } },
                { "WMushroom", new Dictionary<string, string> { { "KOR", "버섯을 배양했" }, { "ENG", "incubated the mushroom" }, { "JPN", "キノコを培養した" }, { "CHS", "培养了蘑菇" }, { "CHT", "培養了蘑菇" } } },
                { "WFire", new Dictionary<string, string> { { "KOR", "화로에 불을 붙였" }, { "ENG", "lit the flame brazier" }, { "JPN", "火鉢に火をつけた" }, { "CHS", "点燃了火盆" }, { "CHT", "點燃了火盆" } } },
                { "WCandle", new Dictionary<string, string> { { "KOR", "양초에 불을 붙였" }, { "ENG", "lit the candle" }, { "JPN", "ロウソクに火をつけた" }, { "CHS", "点燃了蜡烛" }, { "CHT", "點燃了蠟燭" } } },
                { "WBookcase", new Dictionary<string, string> { { "KOR", "책을 꺼냈" }, { "ENG", "took out a book" }, { "JPN", "本を引き出した" }, { "CHS", "取出了书" }, { "CHT", "取出了書" } } },
                { "WRune", new Dictionary<string, string> { { "KOR", "마법진을 활성화시켰" }, { "ENG", "activated the rune" }, { "JPN", "魔法陣を活性化させた" }, { "CHS", "激活了魔法阵" }, { "CHT", "激活了魔法陣" } } },
                { "WSeparator", new Dictionary<string, string> { { "KOR", "정수를 추출했" }, { "ENG", "extracted the essence" }, { "JPN", "エッセンスを抽出した" }, { "CHS", "提取了精华" }, { "CHT", "提取了精華" } } },
                { "WMicroscope", new Dictionary<string, string> { { "KOR", "표본을 확인했" }, { "ENG", "examined the sample" }, { "JPN", "サンプルを確認した" }, { "CHS", "确认了样本" }, { "CHT", "確認了樣本" } } },
                { "WWarp", new Dictionary<string, string> { { "KOR", "블랙홀을 가동시켰" }, { "ENG", "activated the black hole" }, { "JPN", "ブラックホールを稼働させた" }, { "CHS", "启动了黑洞" }, { "CHT", "啟動了黑洞" } } },
                { "WStatue", new Dictionary<string, string> { { "KOR", "음료를 제공했" }, { "ENG", "offered a drink" }, { "JPN", "ドリンクを提供した" }, { "CHS", "提供了饮料" }, { "CHT", "提供了飲料" } } },
                { "WPotion", new Dictionary<string, string> { { "KOR", "플라스크를 가져갔" }, { "ENG", "took the flask" }, { "JPN", "フラスコを持ち去った" }, { "CHS", "拿走了烧瓶" }, { "CHT", "拿走了燒瓶" } } },
                { "WColor", new Dictionary<string, string> { { "KOR", "플라스크를 넣었" }, { "ENG", "loaded the flask" }, { "JPN", "フラスコを入れた" }, { "CHS", "投入了烧瓶" }, { "CHT", "投入了燒瓶" } } },
                { "WTeleport", new Dictionary<string, string> { { "KOR", "포탈을 작동시켰" }, { "ENG", "activated the portal" }, { "JPN", "ポータルを作動させた" }, { "CHS", "开启了传送门" }, { "CHT", "開啟了傳送門" } } },
                { "WAutomatic", new Dictionary<string, string> { { "KOR", "자동문을 열었" }, { "ENG", "activated the door" }, { "JPN", "自動扉を開いた" }, { "CHS", "开启了自动门" }, { "CHT", "開啟了自動門" } } },
                { "WStone", new Dictionary<string, string> { { "KOR", "비약을 사용했" }, { "ENG", "used the elixir" }, { "JPN", "秘薬を使用した" }, { "CHS", "使用了秘药" }, { "CHT", "使用了秘藥" } } },
                { "WIce", new Dictionary<string, string> { { "KOR", "냉각수를 제조했" }, { "ENG", "generated coolant" }, { "JPN", "冷却水を生成した" }, { "CHS", "制造了冷却水" }, { "CHT", "製造了冷卻水" } } },
                { "WBio", new Dictionary<string, string> { { "KOR", "전시관을 관리했" }, { "ENG", "managed the display case" }, { "JPN", "展示館を管理した" }, { "CHS", "管理了展示馆" }, { "CHT", "管理了展示館" } } },
                { "WVote", new Dictionary<string, string> { { "KOR", "투표를 진행했" }, { "ENG", "cast a vote" }, { "JPN", "投票を行った" }, { "CHS", "进行了投票" }, { "CHT", "進行了投票" } } },
                { "WHope", new Dictionary<string, string> { { "KOR", "잠식을 억제시켰" }, { "ENG", "suppressed the corruption" }, { "JPN", "侵食を抑制した" }, { "CHS", "抑制了侵蚀" }, { "CHT", "抑制了侵蝕" } } },
                { "WFusebox", new Dictionary<string, string> { { "KOR", "전력을 차단했" }, { "ENG", "cut the power" }, { "JPN", "電力を遮断した" }, { "CHS", "切断了电力" }, { "CHT", "切斷了電力" } } },
                { "WNintendo", new Dictionary<string, string> { { "KOR", "미니게임을 수행했" }, { "ENG", "played a minigame" }, { "JPN", "ミニゲームをプレイした" }, { "CHS", "玩了小游戏" }, { "CHT", "玩了小遊戲" } } },
                { "WShelfBattery", new Dictionary<string, string> { { "KOR", "배터리를 가져갔" }, { "ENG", "took the battery" }, { "JPN", "バッテリーを持っていった" }, { "CHS", "拿走了电池" }, { "CHT", "拿走了電池" } } },
                { "WShelfAxe", new Dictionary<string, string> { { "KOR", "곡괭이를 가져갔" }, { "ENG", "took the pickaxe" }, { "JPN", "ツルハシを持っていった" }, { "CHS", "拿走了镐头" }, { "CHT", "拿走了鎬頭" } } },
                { "WMirror", new Dictionary<string, string> { { "KOR", "식별 스크린에 마지막으로 인식됐" }, { "ENG", "was last detected on the identification screen" }, { "JPN", "識別スクリーンで最後に認識した" }, { "CHS", "最后被识别于身份验证屏幕" }, { "CHT", "最後被識別於身份驗證屏幕" } } },
                { "WCabinet", new Dictionary<string, string> { { "KOR", "캐비닛에 들어갔" }, { "ENG", "got into the locker" }, { "JPN", "キャビネットに入った" }, { "CHS", "进入了储物柜" }, { "CHT", "進入了儲物櫃" } } },
                { "WShelfSpray", new Dictionary<string, string> { { "KOR", "스프레이를 가져갔" }, { "ENG", "took the spray" }, { "JPN", "スプレーを持っていった" }, { "CHS", "拿走了喷雾" }, { "CHT", "拿走了噴霧" } } },
                { "WShelfMushroom", new Dictionary<string, string> { { "KOR", "버섯을 가져갔" }, { "ENG", "took the mushroom" }, { "JPN", "キノコを持っていった" }, { "CHS", "拿走了蘑菇" }, { "CHT", "拿走了蘑菇" } } },

                // ── 2026-10-19 补：0.1.18 新增的三条（旧版 46 条只覆盖 EWhatType 的 0–46）──
                // 全量口径：EWhatType 共 50 个成员（0–49），除 WNone(0) 外每个都要有文案 ⇒ 46 + 3 = 49 条。
                // 文案取自设备自己的交互词条：
                //   WMorse  ← MorseCodeInteract「发送信号」
                //   WFishing← FishingInteract「钓鱼」
                //   WChat   ← ChatDeviceInteract「发送消息」（ChatDevice 的 WhatType = 49 = WChat）
                { "WMorse", new Dictionary<string, string> { { "KOR", "모스 신호를 보냈" }, { "ENG", "sent a Morse signal" }, { "JPN", "モールス信号を送った" }, { "CHS", "发送了摩斯信号" }, { "CHT", "發送了摩斯信號" } } },
                { "WFishing", new Dictionary<string, string> { { "KOR", "낚싯대를 사용했" }, { "ENG", "used the fishing rod" }, { "JPN", "釣り竿を使った" }, { "CHS", "使用了钓竿" }, { "CHT", "使用了釣竿" } } },
                { "WChat", new Dictionary<string, string> { { "KOR", "메시지를 남겼" }, { "ENG", "left a message" }, { "JPN", "メッセージを残した" }, { "CHS", "留下了消息" }, { "CHT", "留下了訊息" } } },
            };
    }
}
