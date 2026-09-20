# 存储实现与独立架构审查记录

日期：2026-09-19。

> 本文后半部分保留阶段性历史审查。与当前实现、用户最新要求冲突时，**以下“本轮补充”优先**；旧测试数量、原先仅主界面回合对话的范围，不能代表当前完成度。最新要求是选项和各种对话状态均可保存，现存未适配流程属于需要继续补齐的实现缺口，不能据此宣称需求完成。

## 本轮补充：存储、九类返回流程与验证边界

存储作者测试现为 **`STORAGE_TESTS_OK 56`**，最新隔离合成数据为 `tests/StorageSandbox/c4f92dbfb4ac4286b0ebc08f1c23f4b1`。44 项之后新增 Wine 映射盘根边界与具体错误路径验证，以及完整祖先墓碑测试：A→B→C 删除 C 后，即使磁盘只出现 A 与墓碑，A 也不会成为可加载头；共享祖先的分叉 D 仍保留。删除前必须获得同一用户、周目、分类和槽位的完整已校验祖先闭包；缺失祖先且没有完整墓碑证明时，在创建备份或提交删除前拒绝。墓碑最多保存 8,192 个祖先 ID，普通检查点最多 16 个直接父版本；祖先集合参与整份校验。35 层历史、跨周目不能提供证明、拒绝删除零副作用均有测试。未发布 Schema 1 的墓碑语义现要求完整闭包，没有对外发布旧格式用户迁移问题。

`src/Game/DialogueContinuation.cs` 是本 agent 新增的实现，因此对该文件的核对属于作者验证，**不算独立审查**。它保存原主界面、大地图、地图场景、NPC 场景层级及参数，在世界载入后逐层重建，按代次谓词检查取消；不再把所有场景统一恢复成 MainView。结构校验、配置校验及独立世界对象引用校验在替换活动世界前执行。关闭的缓存窗口被跳过，正在 Loading/Loaded 的窗口仍需要等切换完成。

现有九类原生返回映射：

| 返回流程 | 保存与绑定方式 |
|---|---|
| 考试结果 | `StudyData.ShowExamResultComp` 绑定载入后的 `RoleModel.studyData` |
| 日常行动结算 | 保留行动配置、倍率及行动 ID，重绑当前 `ActionData` / `ActionSubData` |
| 社交小游戏开局 | 以游戏 ID 绑定 `FuncModel.miniGameData.games` 中的原生对象 |
| 社交小游戏结束 | 上述对象，加胜负与选择值；继续原生结算回调 |
| 谈判开局 | 保留谈判配置 ID；继续原生开局回调 |
| 电子游戏刷新 | 原生无实例状态的刷新回调 |
| 心愿失败刷新 | 原生无实例状态的刷新回调；不包含心愿成功奖励 |
| 离家后打开地图 | 原生 `MainView.OnClickMap` 闭包，继续 RecordMap 与打开大地图 |
| 离开场景后关闭场景 | 绑定重建后的同地图场景；按已核查的 `BaseView.CloseView` 等价调用 `UIMgr.CloseView`，避免误调用派生 override 而重播离场事件 |

白名单固定到已审查游戏模块 **`e0298a66-0f75-4b24-b030-dcc6a6778c63`**。存档中的方法 token 只用于身份核验，不用于任意方法解析执行。主 agent 报告最新只读反射契约 **115 项通过**，包括九个方法及其字段/token/MVID；这证明元数据存在，不证明九类返回已在游戏中通过。闭包的完整实例字段集合也会校验，新游戏模块或新增未知字段不能静默忽略。

收束时联合契约新增自动播放计时器 6 项字段检查，最新为 **121 项通过**，已直接读取 `qa/adapter-contract-latest.txt` 确认。115 项是九类回调集成阶段证据，最终数量以 121 为准。最新版适配器已出现 `playback.autoDelayRemaining` 及 `delayTime-passTime` 捕获逻辑；主 agent 报告最新版整体编译通过，计时恢复的游戏行为仍待运行验证。

行动结算闭包六个字段已逐项与 IL 核对：`cfg`、`rate`、`_data`、`<>4__this`、`close`、`<>9__2`。结算 `b__0` 会懒创建后续奖励 `b__2`；`b__2` 仅读取 `cfg`、`<>4__this`、`_data`，运行经验奖励、替换行动或发送刷新，不依赖 `close`，也不继续构造下级委托。只有另一条小游戏包装 `b__1` 调用 `close.Invoke`。因此保留四个有效值/引用，允许重建两个辅助委托字段：若捕获时字段非空，`close` 必须是同目标原生 `b__0`，`<>9__2` 必须是同目标原生 `b__2`；发现其他代码替换则不能省略。此结论来自当前 DLL 直接 IL 阅读，不是只检查 `b__0` 的直接字段访问。

给适配器作者报告并由其处理的地图重建副作用包括 CheckGuide、PhoneData.AddPhoto、NPC 招呼语音、NPC 自动离场，以及重建后还原 `recordMapId/recordBgId/recordNpcId`。另报告自动播放计时缺口：原先恢复自动播放时立即 Next，丢失 TimerMgr 中的剩余等待时间；应保存 `delayTime-passTime` / 待添加计时器并按剩余时间继续。以上需以对应作者最新代码和运行证据确认，不能将报告或代码修正视作实机验收。

**明确未覆盖、仍须补齐：** 已消耗道具的 `ItemData/useEffector` 返回、心愿成功奖励 Effector、恋爱行动复合闭包、其他 UI/小游戏对象持有的回调、第三方回调，以及四类底层场景以外的窗口上下文。特殊演出、过渡中状态是否已覆盖，以适配器逐项验收为准。当前代码对无法可靠重建的情况会给出失败，不能静默丢弃 callback，也不能把这种限制当作满足用户“所有对话可保存”的最终产品方案。

本 agent 没有运行游戏或操作正式安装。本轮九类扩展及最新 UI 修改尚缺重新运行和截图证据；主 agent 报告另一任务正在使用测试游戏进程，未终止该进程。跨进程完整继续、全部原生效果恰好一次、失败回滚、各地图来回、与其他插件组合和 Steam 跨设备同步均须独立标注运行验收状态。

## 身份与证据边界

本记录的 agent 是 `src/Storage/*.cs`、`tests/StorageTests.cs`，以及后续 `src/Game/DialogueContinuation.cs`、`tests/RuntimeSemanticQA.cs` 的作者，因此这些文件的验证**不属于独立审查**。其他游戏适配器文件、`src/DialogueSaveService.cs`、`src/Plugin.cs`、`src/UI/*.cs` 由其他 agent 实现；对这些文件的阅读与问题报告属于独立静态审查。

本 agent 没有启动游戏、安装插件、改动其他插件，没有解析用户真实存档正文。原版行为证据来自 `research/current/` 中当前游戏反编译结果及只读反编译输出。隔离测试只写本项目 `tests/`，使用合成世界字节与对话数据。

## 存储实现及 39 → 44 项验证

接口：`StudentAgeDialogueSave.Storage.Repository` 的 `Publish`、`Scan`、`Load`、`Delete`、`FindHeads`。世界数据为独立 `byte[]`，对话状态为 `JObject`；存储层不假设这些数据足以恢复游戏，恢复语义由适配器验收。

已实现：

- 单文件、完整、版本化检查点，`dialogue_<revision>.dsav` 平铺；写临时文件、flush、复读校验后无覆盖原子发布，不使用跨卷复制回退。
- 主体和头部摘要都参与 SHA-256；严格 DTO、禁 `$type`、禁重复 key、文件及 payload 大小/JSON 深度限制。
- SteamID、run、分类、逻辑槽彼此隔离；唯一 revision 与 parent 图识别分叉，不依赖修改时间裁决。
- 高版本只展示已知头部，不加载、不覆盖；没有可变共享索引。
- 显式删除先本地备份、再发布墓碑，旧版本不会因删除头版本或离线文件重现而自动复活。没有卸载清理存档的代码。
- 默认总量限制为所有平铺 `.dsav` 合计 2 GiB / 4,000 文件。未知、高版本文件也计入；墓碑额外预留 16 MiB / 256 文件预算。提交前复查，不自动 GC。这是本地容量门禁，不是 Steam 剩余配额检测或跨设备同步保证。
- 可选头部 `SeasonId: int?` 用于原版季节渐变，旧文件缺字段时为 null。严格 DTO 已通过 `SaveHeader` 类型定义接纳该字段，未来版本头部提取的字段白名单也由该 DTO 自动生成；无第二份需手工同步的名字数组。整份头部校验覆盖此字段。

39 项阶段测试包括完整读回、调用方快照不被修改、原版存档忽略/字节哨兵不变、线性与分叉/合并、run/分类/槽隔离、非法路径/父版本、缺文件、符号链接、损坏/头部篡改、重复 key、`$type`、深度/大小限制、断写与暂存失败不发布、高版本保留、删除备份和防复活、无索引重建。

新增 5 项配额测试后为 **44 项**：

1. 恰好达到字节预算允许发布。
2. 再发布一个版本被拒绝。
3. 配额失败没有新增目标文件、没有暂存残留、原档字节未变。
4. 文件个数上限阻止新版本。
5. 数据预算已满仍可提交删除墓碑。

最后一次在加入 `SeasonId` 后重跑，结果 `STORAGE_TESTS_OK 44`。完整读回断言同时检查 `SeasonId == 2`。最新合成数据：

`tests/StorageSandbox/7af3af841b2e452eadae2fe8c399cf89`

此前 39 项证据：`tests/StorageSandbox/29279d8ff03f44d4861fc72cc6657a40`。

首次配额 44 项证据：`tests/StorageSandbox/e6dcb85e098847e49ec10bad849fbf4f`。

复跑方式（`GameManaged` 指向只读游戏 Managed 目录）：

```sh
dotnet run --project tests/StorageTests.csproj -p:GameManaged="<游戏 Managed 目录>"
```

测试工程使用 net9.0 与本机游戏 Newtonsoft.Json；没有分发游戏 DLL。以上没有验证 Windows/CrossOver 的 `MoveFileEx` 分支或实际断电耐久性，也没有验证 Steam 云上传。

## 独立审查：发现与修正状态

| 问题 | 原版/实现证据与影响 | 本次复核状态 |
|---|---|---|
| 稳定选项页永远不能保存 | 原版 `NewTalkView.DoTextEnd` 在 `ShowOption(); return` 前设置 `waitFrame`，跳过归零；适配器原先无条件要求归零 | 已看到按 `Option` 与 `AnimEnd` 分阶段判断。待自然选项页运行验证 |
| 读回后整个操作栏消失 | `UIMgr.CloseAllView` 销毁全部视图，原 `RestoreCore` 只重开 Top/Main/NewTalk，未开 Hotkey | 已看到重开并等待 `HotkeyView`。待读回后四按钮截图、点击与按键验证 |
| 缺 Mod 先切换世界、后异步警告 | 原 `ProfileMgr.LoadEnd.ValidateModList` 仅异步显示缺 Mod 警告；其他 `LoadEnd/CheckWrong` 可能清理数据 | 已看到 `ValidateMods` 在破坏当前世界前核对 `activeMods` 和存档 `ProfileModel.modList`，并加入插件指纹。待缺 Mod/版本变化拒读和回滚验证 |
| 超时后旧资源回调干扰新事务 | 原版 `BaseView.LoadComp` 不检查视图是否已销毁；旧 NewTalk 异步回调可能消费回滚的全局 pending 状态 | 已看到所有旧视图登记 retired，并在 `BaseView.LoadComp` 拦截迟到回调。待人为延迟资源 → 超时 → 回滚 → 旧请求晚到测试 |
| 未提交的引导记录丢失 | `GuideData.addGuides` 带 `[IgnoreMember]`；捕获期间拦 `SaveGlobalGuide` 后，这些记录不在 MessagePack 世界快照内 | 已看到 `pendingGuides` 单独捕获/恢复/校验，以及拒绝 `showingGuide != 0`。待完整读回及后续普通保存验证 |
| 季节配色没有沿用原版 | 原 `SaveView.OnSaveRender` 用 `SeasonCfg.colors` 设置年份/季节渐变，插件卡片原先仅填文字 | 已补存储 `SeasonId`；适配器、服务、UI 映射由对应作者集成，未在本文声称视觉通过 |

上表“已看到”只表示重新阅读了修正代码，不等于运行测试已通过。尤其不能用编译、日志初始化成功或合成世界字节读回来替代 exact-once 游戏验收。

## UI 与生命周期静态审查

- 克隆器为克隆对象重建 Button/Toggle 事件，避免继承原版存档删除、改备注等监听；未发现插件卡片调用原版文件写操作。
- 对话页的九宫格与分类控件独立克隆，原版 tabgroup/卡片只隐藏并恢复；原版三个分类的数据和监听不被重新绑定。
- 新快存/快读复用原按键路径，未新增第二套全局键盘监听；挂在 `NewTalkView.OnHotKeyInput` 的消费逻辑需与实际点击和输入流共同测试。
- 引导对象与持久宿主已分开；`BepInEx_Manager` 销毁不触发主运行时清理。主宿主退出清理分项隔离异常，只撤销自身 Harmony ID。
- 多分辨率、九宫格文本截断、克隆异步资源、切页/关闭重复打开、点击是否推进底层对白、恢复后操作栏是否完整，仍需真实截图和操作验收。

## 性能与尚未证明的边界

`ConfigDigest` 当前每次将多张有效配置表转成 JSON；插件指纹现场读取全部 DLL 并 SHA。这些调用位于 Unity 主线程捕获流程。应测大型剧情 Mod 的捕获耗时，若优化则明确按配置变化/F9 重载使依赖缓存失效，不把仍指向活动世界对象的数据放进后台序列化。

`Scan` 完整验证全部版本；`Load` 重新检查删除标记。服务应继续在后台运行这些 IO，并在主线程更新 UI。历史版本及墓碑没有自动垃圾回收，达到预算时只拒绝新保存；安全历史清理需要另行设计，不能直接删除会影响版本图的文件。

本阶段历史审查当时只覆盖主界面回合普通对话，不能证明全部剧情入口受支持。按照用户最新要求，未知回调、特殊演出与未适配场景是需要补齐的实现缺口；实现过程中不能静默改成 callback=null，更不能因保留拒绝逻辑就声称已经完成“所有对话可保存”。当前扩展范围及未覆盖项见本文顶部补充。

## QA 隔离要求

只复制游戏目录或只替换 `SAVE_PATH` 不足以隔离全部写入。开始游戏 QA 前应证明以下边界：

- `PathDefine.SAVE_PATH` 与插件基于 `Application.persistentDataPath` 的暂存、备份、设备 ID 目录均映射至项目 `qa/`。
- 拦截 `Main.FixSaveProblem`，避免启动时从真实 `persistentDataPath/Saves` 搬迁旧目录。
- `SaveMgr.SetPref`、`GetPref`、**`DelPref`** 使用隔离的键值存储；原生 `DelPref(null)` 会调用 `PlayerPrefs.DeleteAll`。此前消息把该名称写成 DeletePref，应以此处核实后的 `DelPref` 为准。
- 对 `SaveMgrEx.Save`、`SaveAsync`、`SaveGlobal`、`SaveGlobalAsync` 记录调用并验证写入目标；监控 `GuideData.SaveGlobalGuide` / GlobalMgr 的全局状态变化。
- Steam QA 阻断写入：`SteamPlatform.SetStat` 两重载、`StoreStat`、`SetAchievement`、`IndicateAchievementProgress`、`ResetAllStat`、`SetRichPresence`。`MainView.Refresh` 会主动更新 Rich Presence，不能只监控成就。
- 若 QA 可能触及工坊入口，还需阻断下载更新、取消订阅、上传、投票入口。Steam 认证保持正常，不以隔离为名绕开认证。
- 正常存档文件、`LatestSaveKey`、真实全局档案均作为“不被插件操作改变”的独立验收对象。跨设备云同步需要两设备真实证据，单机本地提交不等于云同步。
