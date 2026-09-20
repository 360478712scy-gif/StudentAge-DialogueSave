# 仓库布局

| 目录 | 内容 | 是否提交 |
| --- | --- | --- |
| `src/` | `Game/` 原生适配与恢复、`Storage/` 原子存储、`UI/` 界面、插件入口与事务服务 | 是 |
| `assets/ui-previews/` | DLL 内嵌的两张模式预览 JPEG 与来源说明 | 是 |
| `tests/StorageTests.csproj` | 不依赖游戏的仓库测试 | 是 |
| `tests/ConfigFingerprintCheck/` | 配置摘要兼容和深层变更比较 | 是 |
| `tests/HistoryTrailCheck/` | 历史检查点编码、兼容及非法格式 | 是 |
| `tests/ConfigSchemaCheck/`、`tests/AdapterContractProbe/` | 需要本机游戏程序集的结构检查 | 是 |
| `tests/Runtime*QA.cs`、`tests/QaIsolation.cs` | 隔离玩家中的实际交互测试与路径重定向 | 是 |
| `tools/` | 构建、统一测试、隔离克隆与只读基线工具 | 是 |
| `docs/reviews/` | 早期专项审查记录；当前状态以 STATUS 为准 | 是 |
| `qa/` | 游戏副本、fixture、截图、日志、基线与临时环境 | 否 |
| `research/` | 本地反编译研究资料 | 否 |
| `dist/`、`**/bin/`、`**/obj/` | 构建输出及缓存 | 否 |

不移动或清理用户的本地存档来整理 Git 仓库。所有产物由 `.gitignore` 排除；首次上传不包含游戏 DLL、真实存档、Steam 用户目录或反编译游戏源码。
