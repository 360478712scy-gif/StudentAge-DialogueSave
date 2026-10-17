# 思思模组存档兼容性调查（2026-10-11）

已复现一个生产 0.2.13 保存拒绝：思思恋爱后的地点打招呼事件 1277156，首句 1277156001，携带 `MapSceneView.<ShowGreetingEvt>b__47_0`。该原生场景返回回调未列入适配表，拒绝原因是“此原生返回流程尚未适配”，而非作者普通对白整体不支持保存。用户未提供确切剧情位置，本轮不能断言此入口解释了用户遇到的所有失败。

## 当前生产修改

- `DialogueContinuation` 新增有限规则 `site-greeting`：保存场景 ID 和已核对的原生方法，恢复时绑定恢复后的场景实例。
- 读回后继续保持打招呼未结束时的关闭保护；自然结束才启用地点关闭按钮并刷新场景。
- 既有 `leave-site` 的派生离场事件防重放仍保留；未知回调、缺场景和错误方法仍拒绝。
- 版本 0.2.14；不更改原生存档格式、作者剧情、UP 或正式玩家配置。

## 输入和隔离

原版程序集 SHA256：`0d004b3a8c7fdd2ac41e4db099293db112d6b24dc2a14086e9c674283f1378ea`，与外置反编译当前版本一致。原生调用链为 MapSceneView.ShowGreetingEvt → LoveData.ShowGreetingEvt → CommonEvtMgr.ShowEvent → NewTalkView.callback；该回调显示关闭按钮、恢复右键关闭并 Refresh。

实际订阅思思 3664928586（回响思念的山谷-陈思思 V1.5-毕业旅行）、依赖 3673850497、UP 3782171338（1.0.23）。仅复制到隔离目录；原生 loader 合并作者配置，不替换作者对白或 CG。打招呼测试只在合成存档中设定待播事件 ID，绕过日历/恋爱资格，以真正的原生 ShowGreetingEvt 入口进入。正式游戏未启动。

## 已有证据

- `qa/sisi-save-20261011/greeting-reproduced-0.2.13`：旧版本实际拒绝首句捕获，包含原生回调名和原因。
- `default-archive-pass`：0.2.13 普通对白和两处 CG，各执行快存以及通过 EventSystem 点击 SAVE、手动槽、确认、读取槽；667 条断言（含指针事件断言），132 作者节点自然续行结束。旧音乐倍率失败档原始字节读回，未修改旧档。
- `sampled-events-pass`：0.2.13 对初遇、电话、告白、游乐园约会、泰坦尼克号、情感训练、回想室共七个事件取样；203 个稳定可捕获节点，七次真实快存/读回。最多取 40 节点/事件，不是这些事件或全部作者剧情完整验收。
- `greeting-adv-pass-0.2.14`：新回调通过实际 ADV 手动保存/读回；负向场景/方法校验、第三方回调拒绝、自然续行结束只执行一次原生完成回调、地点恢复可用，391 条断言。已目视保存和返回地点截图。
- 独立规则检查：存储 128、配置 26、历史 25、存档路由 81，以及知识库检查通过。

- `greeting-both-styles-pass-0.2.14`：488 条断言，ADV 手动槽和原版样式对话子页分别实际保存、读取同一作者首句；恢复时尚未结束的打招呼保持关闭保护，继续作者链自然结束后完成回调恰好一次，返回地点可用。旧 `leave-site` 恢复后再捕获也通过。三插件加载版本记录为 DialogueSave 0.2.14、QA 0.1.0、UP 1.0.23。已目视保存及返回地点截图。

## 本机安装与保护

生产 0.2.14 DLL SHA256：`8e99d184bf2603c22096ea6310a05991693157f5cb5f5cd26e0487b217bdb270`。源码哈希与 build-manifest 全部一致；安装前确认没有游戏进程，备份原 0.2.13，再同卷原子替换订阅目录的生产插件并核验哈希。未安装任何 QA 重写程序集。安装收据：`qa/sisi-save-20261011/install/install-receipt.json`。

初始与最终正式存档、正式 Managed/plugins、玩家配置注册表共 1865 项一致，清单未覆盖；49 个作者配置/UP 输入哈希一致。见 `live-before-verification.json`、`inputs-verification.json`。隔离测试的 mod 启用列表、UP 配置和先前作者副本已恢复；下一次运行须重新 prepare_qa。本轮不启动正式玩家游戏，未上传 Steam，工坊远端仍为上轮已发布 0.2.13。

## 中间测试失败

`initial-scope-fixture` 为 QA 遗留 Disabled 范围，与测试期待 ADV 页冲突；明确设置 QA DialogueOnly 后通过。`original-toolbar-fixture-timing`、`original-toolbar-canvas-depth`、`original-toolbar-transition-fixture` 分别记录原版样式连测时，工具栏重建未出现、Canvas 图形深度尚未注册、上一存档退出动画仍有输入护罩。`original-loading-fixture` 另记录原版页尚有原生 LoadingView 的点击阻挡。均未更改生产界面代码；测试等待真正的界面、原生加载和转场就绪后再按 EventSystem 点击，最终完整通过。

UP 日志另有作者自带 CustomMinigamecfg.json 解析错误，本轮未改作者资源，也不把它认定为此保存拒绝原因。

## 边界

CrossOver 隔离游戏已加载真实作者资源和 UP；不等同正式玩家的当前失败现场、Windows 原生、全模组全剧情或双设备云同步通过。只适配实际复现且核对过的原生回调，不无条件放开所有保存校验。
