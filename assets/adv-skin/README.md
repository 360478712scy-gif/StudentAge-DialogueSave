# ADV 素材入口

当前组件规则只维护在 [项目知识库](../../docs/knowledge/README.md)，不要从历史素材说明推断新样式：

| 要改的内容 | 查询主题 | 素材/来源入口 |
| --- | --- | --- |
| 顶部原作三态、白笔刷 | settings.tabs | settings-source-controls.png、settings-tab-brush-prompt.txt |
| 票券艺术字、说明条 | settings.footer | settings-lettering.png、ticket-lettering-prompts.md、settings-help-prompt.md |
| 中间卡片、选项、滑轨 | settings.controls | settings-source-frame.png、settings-source-controls.png、settings-tracks.png |
| 原作是/否确认框、蓝色头部 | ui.confirmations | confirm-provenance.json、confirm-*.png |
| 剧情选项、文具剪影 | ui.choices | choice-provenance.json、choice-stationery-prompt.md |
| 黑色单边条件提示 | ui.conditions | choice-condition-provenance.json |
| 半透明日志、DATE | ui.backlog | backlog-provenance.json、backlog-prompts.md |
| 实际中文字体 | ui.fonts | chinese-font-provenance.json；不内置苹方/微软雅黑文件 |
| 悬停/点击/切换声音 | ui.audio | source-ui-audio.json、hover.wav、click.wav、toggle.wav |

- 本机参考库 `/Users/yugonglian/Projects/limelight-ui-extraction`，按 sprites/、layouts/named/、original/unencrypted/、work/extracted/ 定向查找。
- quickmenu/window 原图与 layout.json 保留原作坐标和 alpha；原作资源复用不改变业务操作顺序或回调。logo-mask.png 为新生成学生时代标志，黑底在导入时转透明，不是背景底板。
- 图像只在加载时做色键、覆盖率 mask、裁切、九切和调色；真实 RGBA 优先，禁止逐帧解码。含黑色/棋盘背景的 RGB 文件不当作透明图。
- 顶部普通字加白笔刷，无模糊；票券一律艺术字；设置中间用普通字。旧顶部艺术字/模糊/无箭头说明已经失效。
- 更新素材同步保留来源/提示词，检查实际游戏效果。资源在仓库、构建或本机安装，不代表已发布工坊或通过全部剧情验收。

- 新存档页：`archive-background.png` 校园背景、`archive-lettering.png` 六行艺术字由内置图像工具生成；原作卡片/详情/悬停资源来源和运行时裁切见 `archive-provenance.json`，提示词见 `archive-prompts.md`。读取蓝、保存粉、快读绿、原版黄。
