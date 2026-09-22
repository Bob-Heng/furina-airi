# 芙宁娜 · AIRI 桌面伙伴（furina）

住在桌面上的芙宁娜：以 [AIRI](https://github.com/moeru-ai/airi) 桌面宠物为载体，
大模型负责对话与人格（角色卡约束），GPT-SoVITS 负责她的声音，`furina.exe` 负责把这一切一键拉起来并看门。

本仓库**只包含代码与角色卡文本**，开箱可配置，无需 AI 协助：
下载后双击 `launcher\furina.exe`，在图形界面里浏览选择你本机的 AIRI 程序和语音服务脚本即可。
（项目作者的语音方案——GPT-SoVITS + 自研 OpenAI 兼容适配器——随仓库提供为默认配置。）

## 组成

| 部分 | 说明 |
|---|---|
| `launcher/` | 整合启动器（C# WinForms 单文件，.NET Framework 自带编译器即可构建）：GUI 浏览配置路径、一键启停、五组件状态灯、统一探活看门狗、内置四章教程 |
| `voice/openai_tts_adapter.py` | 自研语音适配器（仅标准库）：OpenAI TTS 协议 ↔ GPT-SoVITS api_v2 协议翻译、ACT 情绪同源选音、预合成缓存、断点续播、文本归一化、长句二次拆分 |
| `voice/ref_pool.json` | 参考音池定义（语气控制核心；音频文件需自备，见下） |
| `llm_guard.py` | LLM 守卫（仅标准库）：输出长度前置检查（超限打回重生成）、ACT 情绪分段解析并共享给适配器 |
| `start-voice.bat` | 语音服务启动器（探活→拉起，幂等；路径全部相对，仓库可放任意目录） |
| `assets/character-card/` | 芙宁娜角色卡（CCv3）源文件 + AIRI manifest |
| `docs/` | 交接文档、角色卡迭代史、状态机图、依赖锁定快照 |

## 快速开始

1. **装 AIRI**：从 [moeru-ai/airi](https://github.com/moeru-ai/airi) 发行版安装。
2. **准备语音（二选一）**：
   - 用默认方案：按《项目文档》§4 部署 GPT-SoVITS（芙宁娜权重需自行训练，语料请确保有权使用）；
   - 或接任意 OpenAI 兼容 TTS：把你自己的服务地址填进启动器界面的「语音适配器地址」。
3. **双击 `launcher\furina.exe`**：
   - 「组件路径」→ 浏览选择 **AIRI 程序**、**语音服务脚本**（默认已指向本仓库的 bat）；
   - **NewAPI 留空 = 不使用网关**（适配器与 AIRI 直接对接你自己的端点）；需要网关则浏览选择 `new-api.exe` 与其数据目录；
   - 点「保存配置」→「▶ 启动」。全部就绪后 AIRI 出现，退出按「■ 停止」自动回收全部进程。
4. **AIRI 内配置**：意识模块填你的 LLM 端点与密钥，TTS 选 OpenAI 兼容并指向适配器地址，导入 `assets\character-card\芙宁娜·AIRI角色卡.zip` 与 MMD 模型。
   激活 LLM 守卫（输出长度前置检查 + 情绪同源）：意识模块地址指向守卫（默认 `http://127.0.0.1:3001/v1`）。

## 改卡

编辑 `assets\character-card\card.json` → 把 card.json 与 manifest.json 打包为 zip → AIRI 里重新导入并**新开会话**生效。
角色卡迭代史与验证方法见 `docs/角色卡·设计说明.md`。

## 构建

改 `launcher\furina.cs` / `furina_gui.cs` / `tutorial_text.cs` 后运行 `launcher\build.bat`（仅需 Windows 自带 .NET Framework csc）。
命令行模式：`furina.exe --console`（按 Q 退出），`furina.exe --exit-after=N`（冒烟测试）。

## 版权与免责

- 《原神》及芙宁娜相关素材（模型、语音、设定）版权归米哈游所有，**本仓库不含任何游戏资产**；
  参考音、模型、语音权重请自备，且仅限个人非营利使用，请勿二次配布。
- 角色卡文本为粉丝创作，按 CC BY-NC-SA 4.0 许可；代码部分按 MIT 许可。
- 本项目与米哈游、AIRI 官方均无隶属关系。
