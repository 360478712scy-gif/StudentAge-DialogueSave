# 开发与验证

## 基础环境

- Python 3，使用标准库构建和检查。
- .NET 9 SDK；构建插件也可使用更高 SDK，独立测试目标为 net9.0。
- 完整插件编译需要本机《学生时代》的 `StudentAge_Data/Managed/` 和 `BepInEx/core/`。脚本通过已安装 SDK 的 C# 编译器引用游戏实际程序集，不将其上传或复制进源码仓库。
- 独立测试的 Newtonsoft.Json 13.0.3 由 NuGet 恢复，不要求复制游戏 DLL。

## 一次完成修改，再统一验证

```sh
python3 tools/check.py
python3 tools/build.py --game "/path/to/StudentAge"
```

前者运行三组独立测试；后者仅编译插件。本轮源码整理不涉及启动游戏。涉及 UI 的修改集中完成后做一次对应画面检查；涉及存读档的修改集中做一次相关功能回归。只有失败后的必要复测才重新启动游戏。

反射契约检查只读取程序集元数据：

```sh
dotnet run --project tests/AdapterContractProbe -- "/path/to/StudentAge/StudentAge_Data/Managed"
```

`tests/ConfigSchemaCheck` 需要通过 `-p:GameManaged=...` 指定同一目录；它依赖实际游戏配置结构，不纳入无游戏资源的 CI。

## 隔离游戏测试（macOS / CrossOver）

这些是开发者专用脚本。不要把运行驱动装进正式游戏，也不要将真实玩家档案用作 fixture。

1. 完成上面的插件构建，准备专用测试周目 fixture。此文件不随仓库分发；部分测试还依赖旧轮隔离产物，不能把缺少 fixture 的新环境当作完整验收环境。
2. 指定以下本机参数：

| 变量 | 用途 |
| --- | --- |
| `STUDENTAGE_GAME_DIR` | 安装了 BepInEx 5 的游戏路径，只读来源 |
| `STUDENTAGE_QA_FIXTURE` | 专用隔离测试 fixture 路径；已有 `qa/runtime/fixture.save` 可复用 |
| `STUDENTAGE_QA_PYTHON` | 安装了 UnityPy 的 Python；用于修改隔离副本的原生偏好命名空间 |
| `STUDENTAGE_CROSSOVER_WINE` | CrossOver 的 wine 可执行文件；未设置时搜索本机 Applications |
| `STUDENTAGE_BOTTLE` | 启动用 bottle 名称，默认 Steam |
| `STUDENTAGE_BOTTLE_DIR` | 只读基线工具使用的 bottle 目录 |

3. 用 `tools/qa_baseline.py record` 建立本轮首次基线。已存在基线时不要覆盖它来掩盖差异。
4. 运行 `python3 tools/prepare_qa.py`，仅在 `qa/runtime/` 创建副本，重写托管数据路径和原生偏好身份。此工具使用 macOS 克隆复制，不能在 Windows 直接运行。
5. 确认隔离证明已生成，再运行 `python3 tools/run_qa.py --adv-only --preview`。`--preview` 会在检查完成后保留隔离游戏，供目视核对。
6. 检查 `qa/runtime/results/success.txt` 或 `failed.txt`，并核对截图、后台日志与只读基线差异。

## 验证边界

CI 检查存储和纯数据行为，不能验证 Unity 画面或全部剧情。真实游戏测试、Windows 原生环境、CrossOver、云同步分别记录，不能互相代替。历史实测与尚未完成项见 [STATUS.md](STATUS.md)。

`dist/` 仅为本地构建与 Steam 测试包产物；不上传游戏依赖或存档。`tools/package_workshop.py` 核对源码和构建 DLL 一致后组装原生 `plugins/` 布局，不调用 Steam 上传。

本轮定向验证：`python3 tools/run_qa.py --adv-only --hotfix`，覆盖跨版本实际恢复、真实旧档、卡片提示页终止、漫画复用和退出存档成功/失败。
