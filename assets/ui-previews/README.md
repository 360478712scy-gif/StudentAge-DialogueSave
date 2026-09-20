# 阅读界面选择页预览

两个 960×540 JPEG 来自本项目隔离运行的真实游戏截图，仅等比缩小；没有生成或模拟界面。

- original.jpg：qa/runtime/results-012858/controls-01-dialogue-toolbar.png，原版对话模式及插件存读入口。
- adv.jpg：本轮 qa/runtime/results/adv-preview-ready.png，ADV 对话模式，已恢复左右收起箭头。

构建时内嵌 DLL，选择页打开时才解码，关闭时释放纹理。更新 UI 外观后应重新捕获对应截图。素材包含游戏本身的背景，仅用于本地游戏插件中的模式对照；外部分发前另外确认游戏素材授权。
