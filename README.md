# 芙宁娜 · AIRI 桌面伙伴（furina）

住在桌面上的芙宁娜：以 [AIRI](https://github.com/moeru-ai/airi) 桌面宠物为载体，
大模型负责对话与人格（角色卡约束），GPT-SoVITS 负责她的声音，`furina.exe` 负责把这一切一键拉起来并看门。

本仓库**只包含代码与角色卡文本**，开箱可配置，无需 AI 协助：
下载后双击 `Furina\furina.exe`，在图形界面里浏览选择你本机的 AIRI 程序和 TTS 启动脚本即可。
（项目作者的 TTS 方案——GPT-SoVITS + 自研 OpenAI 兼容适配器——随仓库提供为默认配置。）

## 组成

| 部分 | 说明 |
|---|---|
| `Furina/` | 整合启动器（C# WinForms 单文件，.NET Framework 自带编译器即可构建）；GUI 配置路径、一键启停、角色卡打包与验收测试 |
| `语音/openai_tts_adapter.py` | 自研 TTS 适配器（仅标准库）：OpenAI TTS 协议 ↔ GPT-SoVITS api_v2 协议翻译、情绪路由、故障降级 |
| `语音/ref_pool.json` | 参考音池定义（语气控制核心；音频文件需自备，见下） |
| `启动芙宁娜语音服务.bat` | 语音服务启动器（探活→清残留→拉起，幂等；路径全部相对，仓库可放任意目录） |
| `角色卡源文件/` | 芙宁娜角色卡（CCv3）源文件 + AIRI manifest |
| `项目文档.md` / `角色卡·设计说明.md` | 交接文档与角色卡迭代史 |

## 快速开始

1. **装 AIRI**：从 [moeru-ai/airi](https://github.com/moeru-ai/airi) 发行版安装。
2. **准备语音（二选一）**：
   - 用默认方案：按《项目文档》§4 部署 GPT-SoVITS（芙宁娜权重需自行训练，语料请确保有权使用）；
   - 或接任意 OpenAI 兼容 TTS：把你自己的服务地址填进启动器界面的「适配器探活地址」。
3. **双击 `Furina\furina.exe`**：
   - 「启动器」页 → 浏览选择 **AIRI 程序**、**TTS 启动脚本**（默认已指向本仓库的 bat）；
   - **NewAPI 留空 = 不使用网关**（适配器与 AIRI 直接对接你自己的端点）；需要网关则浏览选择 `new-api.exe` 与其数据目录；
   - 点「保存配置」→「▶ 启动」。全部就绪后 AIRI 出现，退出按「■ 停止」自动回收全部进程。
4. **AIRI 内配置**：意识模块填你的 LLM 端点与密钥，TTS 选 OpenAI 兼容并指向适配器地址，导入 `芙宁娜·AIRI角色卡.zip`（用下方角色卡工具打包）与 MMD 模型。

## 改卡与验收

`furina.exe`「角色卡工具」页：编辑 `角色卡源文件\card.json` →「打包」（自动备份旧包）→
到 AIRI 里重新导入并**新开会话**。「运行验收」对当前卡一键跑内置题库
（元泄漏/知识防火墙/节奏/反大模型腔），人工核对观察点。题库文件：`Furina\configs\card-tests.json`，可自由增删。

## 构建

改 `Furina\furina.cs` / `furina_gui.cs` 后运行 `Furina\build.bat`（仅需 Windows 自带 .NET Framework csc）。
命令行模式：`furina.exe --console`（按 Q 退出），`furina.exe --exit-after=N`（冒烟测试）。

## 版权与免责

- 《原神》及芙宁娜相关素材（模型、语音、设定）版权归米哈游所有，**本仓库不含任何游戏资产**；
  参考音、模型、语音权重请自备，且仅限个人非营利使用，请勿二次配布。
- 角色卡文本为粉丝创作，按 CC BY-NC-SA 4.0 许可；代码部分按 MIT 许可。
- 本项目与米哈游、AIRI 官方均无隶属关系。
