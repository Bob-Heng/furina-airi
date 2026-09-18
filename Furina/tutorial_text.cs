// -*- coding: utf-8 -*-
/*
 * tutorial_text.cs - 教程文本（Markdown 格式）+ 极简 Markdown 渲染器
 *
 * 支持的语法（刻意保持极简，零依赖）：
 *   # 章标题      大号加粗深蓝
 *   ## 节标题     中号加粗
 *   其余          正文（空行分段）
 * 改教程只需编辑 Contents，build.bat 重编译后生效。
 */
using System.Text;

static class TutorialText
{
    public static readonly string[] Titles = {
        "第一章 · AIRI 配置",
        "第二章 · 语音服务配置",
        "第三章 · 语音适配器配置",
        "第四章 · NewAPI 网关配置（可选）",
    };

    // 四章按顺序连贯渲染为一份可滚动的文档
    public static readonly string FullMarkdown =
@"# 第一章 · AIRI 配置
AIRI 是桌面宠物本体：显示模型、播放动作、提供聊天界面。

## 1.1 模型
1. 浏览器打开 modao.cc，搜索“芙宁娜”，下载 MMD 模型包（.zip）。
2. AIRI → 设置 → Models → Import → 选择该 zip。

## 1.2 角色卡
1. 从本仓库「角色卡源文件」下载 card.json 与 manifest.json。
2. 全选两个文件 → 右键 → 发送到 → 压缩文件夹，得到 zip。
3. AIRI → 设置 → 角色卡 → 导入该 zip → 新开一个会话生效。

## 1.3 动作
1. 模之屋下载 .vmd 动作文件。
2. AIRI → 动作管理 → 导入。
3. 在「情绪动作映射」中绑定到情绪；数量宁少勿多。

## 1.4 大模型 API
1. AIRI → 意识模块 → 选择 OpenAI 兼容。
2. 不用网关：地址 https://api.deepseek.com/v1，密钥填 deepseek.com 后台创建的 sk- 密钥，模型 deepseek-chat。
3. 使用网关（见第四章）：地址 http://127.0.0.1:3000/v1，密钥填网关令牌。

## 1.5 语音
TTS 选择 OpenAI 兼容：地址 http://127.0.0.1:9881/v1，模型 gpt-sovits-tts，密钥任意填写。

# 第二章 · 语音服务配置
语音服务负责把文字合成为语音。默认方案为 GPT-SoVITS。

## 2.1 语音获取
1. 准备 3~10 秒单人干声：环境安静、无背景音乐、无混响，句与句之间留 1 秒静音。
2. 来源：自己录制，或已取得明确授权的素材；建议一次准备 20~40 条，覆盖不同情绪。
3. 录音建议：找一位声线接近角色的朋友（清亮、略带戏剧感的女声），
   准备能体现不同情绪色彩的示例文本——平静的日常闲谈、开心的分享、
   低落时的独白、俏皮地打趣——每种情绪读 5~8 条，
   用接近角色的说话方式朗读即可，不必刻意背诵台词。
4. 在 语音\ref_pool.json 中登记每条参考音：路径、逐字台词、情绪标签（soft/calm/bright）。台词必须逐字准确，否则明显拉低合成质量。

## 2.2 模型选取与安装
1. 下载 GPT-SoVITS 整合包（建议 v2ProPlus），解压到项目目录下的 GPT-SoVITS-main 文件夹。
2. 按整合包说明安装 Python 环境；依赖版本快照见 requirements-frozen.txt。

## 2.3 训练与启动
1. 把语音切成 3~10 秒小段，放入切片目录。
2. 打开 GPT-SoVITS 网页界面，依次训练 GPT 模型（.ckpt）与 SoVITS 模型（.pth）。
3. 用 api_v2.py 在 127.0.0.1:9880 启动推理服务；参照仓库根目录的「启动芙宁娜语音服务.bat」。
4. 语气优化：训练时提高角色日常对话语料的权重，压低演讲/念白类语料的权重。

# 第三章 · 语音适配器配置
适配器负责协议翻译：AIRI 使用 OpenAI 语音协议，语音服务使用自有协议；同时按情绪选择参考音、处理故障兜底。

## 3.1 默认用法（无需改动）
1. 本仓库自带的 openai_tts_adapter.py 监听 127.0.0.1:9881，随启动脚本自动拉起。
2. AIRI 语音设置按 1.5 节填写即可。

## 3.2 换用自有语音服务
1. 「组件路径」中：语音服务脚本改为你的启动脚本，两个地址指向你的服务。
2. 保存配置，重启启动器。

## 3.3 语气调节（进阶）
1. 情绪判定词表在适配器源码开头的 BRIGHT_WORDS / SOFT_WORDS。
2. 改动后重启启动器生效。
3. 排查看 语音\adapter.log：每句记录了情绪判定、参考音与合成用时。

# 第四章 · NewAPI 网关配置（可选）
网关统一托管密钥并转发大模型请求。不需要可跳过整章：启动器中 NewAPI 程序留空，AIRI 直连 DeepSeek。

## 4.1 安装
1. 下载 new-api 单文件版，解压到任意目录。
2. 「组件路径」中选择 new-api.exe 与数据目录；密钥留空，首次启动自动生成。

## 4.2 后台配置
1. 浏览器打开 http://127.0.0.1:3000。
2. 新建渠道：选择 DeepSeek，录入 sk- 密钥。
3. 新建令牌；AIRI 意识模块填写 http://127.0.0.1:3000/v1 + 该令牌。

## 4.3 排障
1. 无回复先查 http://127.0.0.1:3000/api/status。
2. 重建网关后需重做：建用户 → 建渠道 → 建令牌 → AIRI 重新填写。
";
}

// ---------------------------------------------------------------
// 极简 Markdown → RTF 渲染器（零依赖）
// ---------------------------------------------------------------
static class MiniMd
{
    public static string ToRtf(string md)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{\\rtf1\\ansi\\deff0");
        sb.Append("{\\fonttbl{\\f0\\fnil\\fcharset134 Microsoft YaHei UI;}}");
        sb.Append("{\\colortbl ;\\red25\\green90\\blue205;\\red40\\green60\\blue90;\\red30\\green30\\blue30;}");
        sb.Append("\\viewkind4\\uc1\\pard\\f0\\fs21 ");
        foreach (string raw in md.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("# "))
                EmitLine(sb, raw.Substring(2), 34, true, 1, 320, 140);
            else if (raw.StartsWith("## "))
                EmitLine(sb, raw.Substring(3), 24, true, 2, 220, 80);
            else if (raw.Trim().Length == 0)
                sb.Append("\\par\\sa20 ");
            else
                EmitLine(sb, raw, 21, false, 3, 60, 60);
        }
        sb.Append("}");
        return sb.ToString();
    }

    static void EmitLine(StringBuilder sb, string text, int fs, bool bold, int colorIdx, int before, int after)
    {
        sb.Append("\\par\\sb").Append(before).Append("\\sa").Append(after)
          .Append("\\fs").Append(fs).Append("\\cf").Append(colorIdx);
        if (bold) sb.Append("\\b ");
        AppendEscaped(sb, text);
        if (bold) sb.Append("\\b0 ");
        sb.Append(' ');
    }

    static void AppendEscaped(StringBuilder sb, string s)
    {
        foreach (char c in s)
        {
            if (c == '\\' || c == '{' || c == '}') sb.Append('\\').Append(c);
            else if (c < 128) sb.Append(c);
            else sb.Append("\\u").Append((short)c).Append('?');
        }
    }
}
