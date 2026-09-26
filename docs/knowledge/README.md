# 精简项目知识库

`project.json` 是唯一知识数据库：当前 22 个主题，保持 30 KB 内，无数据库服务、向量模型或额外依赖。AGENTS.md 是 agent 自动读取的工作入口，不再另建大小写不同的 agent.md。

```sh
python3 tools/knowledge.py                       # 只列主题
python3 tools/knowledge.py "条件 提示框"          # 默认最多3条
python3 tools/knowledge.py "字体" --limit 1
python3 tools/knowledge.py --id settings.footer # 精确获取
python3 tools/knowledge.py --check              # 检查结构及仓库文件引用
```

命令可从任何工作目录调用（脚本路径需正确）；只读取、打印，不执行条目中的验证命令。中文别名用于减少反复全文搜索，未命中再在相关目录用 rg。

## 阅读顺序

AGENTS.md → 相关主题 → 所指源码/检查 → 必要时 STATUS 最新段或证据。

- `files`：可核对的仓库入口，校验要求文件存在。
- `facts`：当前有效规则与已确认经验，每主题最多5条。
- `check`：按改动选择的验证入口，运行游戏前仍须构建及隔离证明。
- `evidence`：观察时点的本地证据，qa/ 不提交，其他机器可能没有；不能当作当前安装或完整验收证明。

## 维护预算

优先原位更新已有主题，总量不超过24个、30 KB（检查会拦截超限）。不录入完整日志、聊天、图片、反编译全文或每次构建哈希；这些留在原文件，数据库只放路径。用户纠正应删除被覆盖事实，不追加多套互相矛盾的“最新要求”。

源码/路径变更后运行 `--check`；常规 tools/check.py 也会首先执行这项检查。新增术语补到既有 keywords。状态或安装变更在 STATUS/收据记录，只有长期决策或路由变化才更新知识库。大任务结束统一维护一次；文档整理不启动游戏。
