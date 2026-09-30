# AyuTranslate

[![构建发布](https://github.com/zhengwuji/AyuGram-fanyi/actions/workflows/build-release.yml/badge.svg)](https://github.com/zhengwuji/AyuGram-fanyi/actions/workflows/build-release.yml)

给 **AyuGram / Telegram Desktop** 用的实时翻译工具，**支持任意自定义 AI 接口**。

提供两种工作方式，共用同一套翻译后端配置，可以同时启用：

| 方式 | 效果 | 原理 |
|---|---|---|
| **① 原生翻译接管**（推荐） | 和 AyuGram 自带翻译**完全一致**：译文原地替换原文，可点「显示原文」切换 | 把 AyuGram 内置 Google 翻译的接口地址改写成本地代理，代理转发到你的 AI |
| **② 覆盖层翻译** | 在原文位置画出译文框（可采样气泡配色，接近原生观感） | 截图 → Windows OCR 识别 → 翻译 → 画回去 |

---

## 目录

- [方式一：原生翻译接管（推荐）](#方式一原生翻译接管推荐)
- [方式二：覆盖层翻译](#方式二覆盖层翻译)
- [配置自定义 AI 地址](#配置自定义-ai-地址)
- [常用场景配置](#常用场景配置)
- [界面与快捷键](#界面与快捷键)
- [配置文件](#配置文件)
- [自检与排障](#自检与排障)
- [原理与限制](#原理与限制)
- [从源码构建](#从源码构建)
- [目录结构](#目录结构)

---

## 方式一：原生翻译接管（推荐）

这是 AyuGram 自带的翻译界面 —— 译文直接替换消息原文、显示原文/译文可切换、
排版完全原生。本程序让这个界面使用**你自己的 AI**，而不是 AyuGram 的服务器。

### 原理

AyuGram 的翻译后端有四个（见其源码 `ayu/ayu_settings.h`）：

```cpp
enum class TranslationProvider {
    Telegram = 0,   // 走 MTProto，无法接管
    Google   = 1,   // https://translate-pa.googleapis.com/v1/translateHtml  ← 明文 HTTP，可接管
    Yandex   = 2,
    Native   = 3,   // translate.ayugram.one
};
```

其中 Google 这一路的接口地址是硬编码在 `implementations/google.cpp` 里的字符串常量，
且**在二进制中唯一存在**（偏移 `0x64B9C70`，52 字节），因此可以原地改写成本地地址：

```
https://translate-pa.googleapis.com/v1/translateHtml
                    ↓ 改写
http://127.0.0.1:8766/v1/translateHtml
```

本地代理实现同一个协议（`Content-Type: application/json+protobuf`），
把翻译转发到你配置的 AI。AyuGram 的 UI 完全不知道这件事，
因此渲染、排版、交互全部保持原生。

### 三步启用

**最快方式：一键安装**

先**完全退出 AyuGram**（含托盘图标），然后：

```powershell
AyuTranslate.exe --setup-native --port 8766
```

它会一次做完两件事：改写二进制接口地址 + 把 `tdata/ayu_settings.json` 里的
`translationProvider` 设为 `google`。之后只要运行 `AyuTranslate.exe`（代理会按配置自动启动）。

卸载同样一条命令：`AyuTranslate.exe --revert-native`

---

**手动方式（对应设置界面里的按钮）**

**第 1 步：打补丁**

设置 → **原生翻译接管** → 「打补丁」。

> ⚠️ 必须先**完全退出 AyuGram**（包括托盘图标）。程序会自动备份为
> `AyuGram.exe.ayutranslate.bak`，随时可点「还原为原版」逐字节恢复。

**第 2 步：启动代理**

同一页点「启动代理」，再点「测试代理」确认能翻出中文。
勾上「打开本程序时自动启动代理」以后就不用管了。

**第 3 步：在 AyuGram 里选 Google**

AyuGram → 设置 → AyuGram 选项 → 翻译服务 → 选 **Google**。

之后在任意消息上点「翻译」，译文就由你的 AI 生成，界面完全原生。

### 验证补丁是否生效

```powershell
AyuTranslate.exe --patch-status
```

输出会显示当前接口地址。若显示 `http://127.0.0.1:8766/v1/translateHtml` 即为已接管。

代理运行时会打印每一条翻译：

```
14:32:07  auto→zh-CN  Someone tested the New ver of moody?  ⇒  有人测试过 moody 的新版本吗？
```

### 命令行等价操作

```powershell
# 一键安装 / 卸载
AyuTranslate.exe --setup-native --port 8766
AyuTranslate.exe --revert-native

# 只要二进制补丁
AyuTranslate.exe --patch --exe "C:\Program Files\AyuGram\AyuGram.exe" --port 8766
AyuTranslate.exe --unpatch

# 查看状态
AyuTranslate.exe --patch-status

# 前台运行代理（保持运行）
AyuTranslate.exe --proxy --port 8766
```

### 注意事项

- **AyuGram 更新后会覆盖补丁**，需要重新打一次。更新后 URL 位置可能变化，
  程序会先校验该字符串是否仍唯一，不唯一就拒绝修改，不会乱改。
- 补丁只改动 `.rdata` 里那 52 字节的 URL 字符串，不改任何代码逻辑；
  改动前后其余字节完全一致（可用 `--unpatch` 后比对 SHA256 验证）。
- 代理只监听 `127.0.0.1`，不对外开放。
- 需要保持 AyuTranslate 运行（最小化到托盘即可）。
- 想换回官方翻译：点「还原为原版」，或在 AyuGram 里把翻译服务改回 Telegram。
- **AI 失效自动兜底**（默认开启）：你的 AI 报错 / 超时 / 未配置时，代理会把请求
  自动转发给 AyuGram 原本的免费谷歌翻译接口，聊天始终能出译文。
  需要网络能访问 `translate-pa.googleapis.com`（走系统代理也算）；
  在设置 → 原生翻译接管里可关闭（「AI 翻译失效时自动回退到免费谷歌翻译」）。

---

## 方式二：覆盖层翻译

不修改 AyuGram 的任何文件，也不注入进程：

```
识别 AyuGram 窗口 → 抓取聊天区画面 → Windows OCR 本地识别文字
      → 调用你指定的 AI 接口翻译 → 把译文以「覆盖层」画回原文位置
```

因为走的是「读屏幕 + 画覆盖层」，所以对任何 Qt 版 Telegram 客户端都通用。

### 特性

| 能力 | 说明 |
|---|---|
| 自动翻译对话框全部内容 | 定时抓取并整屏翻译，识别到多少条就翻多少条 |
| 原生配色 | 可采样原文气泡的背景色与文字色去覆盖，视觉上接近原地替换 |
| 本地 OCR | 用 Windows 自带的 OCR 引擎，**识别过程不联网**，只有翻译文本会发出去 |
| 批量请求 | 一句话一次请求太浪费；程序会把多条消息打包成一次请求 |
| 多层去重 | 多语言 OCR 产生的重复块会**先去重再翻译**，不浪费额度 |
| 智能跳过 | 已经是目标语言的消息不翻译；纯数字 / 时间戳 / 系统提示不翻译 |
| 帧跳过 | 抓到的画面与上一帧一致（采样差异 < 1%）就不再跑 OCR / 翻译，静止画面几乎零开销 |
| 缓存 + 位置复用 | 相同文本不重复请求；OCR 抖动导致文本微变时沿用上一帧译文 |
| 焦点感知 | AyuGram 切到后台时自动收起覆盖层 |
| 全局热键 | 立即翻译 / 显示隐藏覆盖层 / 开关自动翻译 / 打开设置 |
| 写回输入框 | 可把译文填进输入框（默认只填不发送） |

### 支持的后端

OpenAI 兼容（DeepSeek / OpenAI / Ollama / LM Studio / vLLM / OneAPI / NewAPI…）、
任意自定义 HTTP 接口、DeepL、LibreTranslate、Google 非官方接口。

---

## 快速开始

### 1. 确认环境

- Windows 10 1809+ / Windows 11
- 已安装 **.NET 8 桌面运行时**（若用 `-FrameworkDependent:$false` 构建的版本则不需要）
- 若用覆盖层模式：系统已安装所需语言的 **OCR 语言包**（见下方「OCR 语言」）
- AyuGram 已安装；用覆盖层模式时需要它正在运行

### 2. 运行

```powershell
.\dist\AyuTranslate.exe
```

启动后：

1. 系统托盘出现图标
2. 程序自动找到 AyuGram 主窗口
3. 若开启覆盖层，它会就位并开始自动翻译
4. 双击托盘图标 或 按 `Ctrl+Alt+S` 打开设置

### 3. 先选一条路线

- **想要原生体验** → 走 [方式一](#方式一原生翻译接管推荐)，
  设置里点「打补丁」「启动代理」，然后在 AyuGram 里把翻译服务选成 Google
- **不想改动客户端** → 走 [方式二](#方式二覆盖层翻译)，
  先到设置 → 识别与区域 → 勾选「**启用覆盖层翻译**」（默认关闭以节省 CPU），
  再开自动翻译即可

无论哪条路线，都建议先跑一次下面的自检确认 AI 接口通。

### 4. 先跑一次自检（强烈建议）

```powershell
# 只测翻译接口，不需要 AyuGram 窗口（最快定位配置问题）
.\dist\AyuTranslate.exe --translate-test

# 抓图 + OCR + 分块，不联网翻译，并输出一张预览图（覆盖层模式用）
.\dist\AyuTranslate.exe --selftest --provider offline --dump
```

`--translate-test` 输出示例（会分别测单条和 8 条批量）：

```
接口类型  : OpenAI 兼容 (127.0.0.1/v1/chat/completions)
模型      : deepseek/deepseek-v4-flash

---- 单条翻译 ----
[ OK ] 5598 ms
       译文：早上好！服务器正在维护中，请稍后再试。

---- 批量翻译（8 条一次性发出）----
  ✔ Hello, how are you today?
    → 你好，你今天怎么样？
  ...
       [ OK ] 批量耗时 4482 ms，成功 8/8
       平均每条 560 ms
```

`--selftest` 输出示例：

```
[ OK ] 目标窗口：pid=20076 hwnd=0x20804 client=1003x1133 title="Human nature"
[ OK ] 抓图 995x1133 用时 30 ms
[ OK ] 聊天区域（客户区坐标）：{X=341,Y=0,Width=662,Height=1023}
[ OK ] 系统 OCR 语言：en-US, zh-Hans-CN
[ OK ] OCR 原始行数 = 96，用时 262 ms
[ OK ] 合并后文本块 = 58
  [  160,  100    84x  15] PT-Depiler 官，
  ...

[ OK ] 覆盖层预览已保存：selftest-preview.png
```

如果 `合并后文本块 = 0`，说明区域或 OCR 语言有问题，按提示调整。

---

## 配置自定义 AI 地址

打开设置 → **翻译服务** 标签页。

### 方式一：OpenAI 兼容接口（推荐）

绝大多数自建 / 云端服务都兼容这个协议。只填两样：

| 字段 | 填法 |
|---|---|
| **API 地址** | 填 **base url**，程序会自动补 `/chat/completions` |
| **模型名称** | 服务商的模型名 |
| **API 密钥** | 云端服务填；本地服务留空 |

常见例子：

| 服务 | API 地址 | 模型 |
|---|---|---|
| DeepSeek | `https://api.deepseek.com/v1` | `deepseek-chat` |
| OpenAI | `https://api.openai.com/v1` | `gpt-4o-mini` |
| 智谱 | `https://open.bigmodel.cn/api/paas/v4` | `glm-4-flash` |
| 硅基流动 | `https://api.siliconflow.cn/v1` | `Qwen/Qwen2.5-7B-Instruct` |
| **Ollama**（本地） | `http://127.0.0.1:11434/v1` | `qwen2.5:7b` |
| **LM Studio**（本地） | `http://127.0.0.1:1234/v1` | 你加载的模型名 |
| **vLLM**（本地） | `http://127.0.0.1:8000/v1` | 你部署的模型名 |
| OneAPI / NewAPI 网关 | `http://网关地址:3000/v1` | 网关里的模型名 |

> 本地 Ollama 记得先拉模型：`ollama pull qwen2.5:7b`
> 也记得让 Ollama 监听：`set OLLAMA_HOST=0.0.0.0:11434`

填完点 **「获取模型列表」** 可以直接从服务商拉取真实可用的模型名（避免手填错导致
`403 MODEL_NOT_IN_PLAN` / `Model not recognized`），再点 **「测试翻译接口」** 确认能翻。

命令行等价验证（不需要 AyuGram 窗口）：

```powershell
AyuTranslate.exe --translate-test --provider OpenAICompatible `
    --url https://api.deepseek.com/v1 --model deepseek-chat --key sk-xxx
```

它会分别测单条和 8 条批量，并报告平均每条耗时 —— 这个数字直接决定覆盖层的刷新体验。

### 方式二：任意自定义 HTTP 接口

接口不是 OpenAI 格式时选 **自定义 HTTP**，然后自定义：

**请求体模板** — 可用占位符：

| 占位符 | 含义 |
|---|---|
| `{text}` | 原文（自动做 JSON 转义，**推荐**） |
| `{text_raw}` | 原文（不转义，用于表单/纯文本接口） |
| `{target}` | 目标语言代码，如 `zh-CN` |
| `{source}` | 源语言代码，`auto` 表示自动 |
| `{target_name}` | 目标语言英文名，如 `Simplified Chinese` |

**响应取值路径** — 用点号表示层级，数组用下标：

```
data.translation
choices.0.message.content
translations.0.text
result.output
```

**把 `{text}` 写进接口地址** 就会改用 GET 请求：

```
https://api.example.com/translate?q={q}&target={target}
```

例子（一个假想的自建接口）：

```
接口地址：      https://translate.myserver.local/api/v1
请求体模板：    {"content":"{text}","to":"{target}","format":"text"}
响应取值路径：  data.result.text
```

### 方式三：DeepL / LibreTranslate / Google

| 接口类型 | 接口地址 | 备注 |
|---|---|---|
| DeepL | `https://api-free.deepl.com/v2/translate`（免费版） | 密钥以 `:fx` 结尾；程序会自动判断域名 |
| LibreTranslate | `http://127.0.0.1:5000/translate` | 自建实例 |
| Google(非官方) | `https://translate.googleapis.com/translate_a/single` | 免密钥，但国内网络通常不可用 |

---

## 常用场景配置

### 场景 A：只翻译聊天内容，不要翻译会话列表

设置 → **识别与区域**：

- 保持 **自动推断聊天区域** 勾选
- **不要**勾选「把左侧会话列表也纳入翻译范围」

程序会自动按窗口宽度估算出左侧边栏宽度并裁掉。默认布局下应该刚好从聊天区开始。

### 场景 B：区域识别不准

1. 先跑 `AyuTranslate.exe --selftest --dump`，看输出的「聊天区域」和文本块
2. 记下正确的 X / Y / 宽 / 高
3. 设置 → 识别与区域 → **取消勾选「自动推断」**，填入数值
4. 点 **测试识别(OCR)** 验证

区域坐标是**相对于 AyuGram 窗口客户区**的像素，不是屏幕坐标。

### 场景 C：本地模型，不想让文本出门

- 用 Ollama / LM Studio，地址填 `http://127.0.0.1:xxxx/v1`
- OCR 本来就是本地的
- 这样整条链路完全不经过外网

### 场景 D：只想手动翻译，不要一直请求

- 按 `Ctrl+Alt+A` 关闭自动翻译
- 需要时按 `Ctrl+Alt+T` 手动翻一次

### 场景 E：字体太小 / 盖不住原文

设置 → **覆盖层外观**：

- **字号**：调大
- **外扩像素**：默认 4，盖不住就调到 6~8（OCR 行框通常比实际文字紧）
- **上下留白**：默认 3，行高不够就加大
- **背景色**：改成不透明度更高的，例如 `#F2000000`

### 场景 F：不想让它挡住别的程序

保持 **「目标窗口失去前台时收起覆盖层」** 勾选即可。
关闭「仅在 AyuGram 处于前台时自动翻译」会让它在后台也持续翻译（更耗接口额度）。

### OCR 语言

程序使用 Windows 自带 OCR。查看本机可用语言：

```powershell
# 自检输出里会列出
.\dist\AyuTranslate.exe --selftest --dump
# 报告 [ OK ] 系统 OCR 语言：en-US, zh-Hans-CN
```

设置里「识别语言」填逗号分隔的标签，例如：

```
zh-Hans-CN, en-US, ja-JP, ko-KR, ru-RU
```

某语言不可用会自动跳过。要装新语言：

> 设置 → 时间和语言 → 语言和区域 → （某语言）三个点 → 语言选项 → 下载「光学字符识别」

---

## 界面与快捷键

### 全局热键（默认）

| 热键 | 作用 |
|---|---|
| `Ctrl+Alt+T` | 立即翻译一次 |
| `Ctrl+Alt+H` | 显示 / 隐藏覆盖层 |
| `Ctrl+Alt+A` | 自动翻译 开 / 关 |
| `Ctrl+Alt+S` | 打开设置 |

都可以在设置 → 运行与输入里改。格式：`Ctrl+Alt+T`、`Ctrl+Shift+F9`、`Alt+Q`。

> 某个热键注册失败会在日志里提示，且不影响其他热键。

### 托盘菜单

双击托盘图标 = 打开设置。右键菜单可以：

- 看当前状态与统计（抓图次数 / 文本块数 / 翻译成功失败数 / 缓存命中）
- 开关自动翻译、显示隐藏覆盖层
- 立即翻译一次
- 重新连接 AyuGram 窗口
- 打开日志 / 打开配置 / 退出

### 写回输入框

设置 → 运行与输入：

- **写入方式**：`剪贴板 + Ctrl+V`（推荐，对 Qt 最稳）或 `直接发送字符消息`（不动剪贴板）
- **写入后**：`仅填入输入框`（默认，最安全）或 `填入后自动按回车发送`

自动回车会真的把消息发出去，请谨慎开启。

---

## 配置文件

位置：`%APPDATA%\AyuTranslate\config.json`

程序会监控这个文件，**改完保存即生效**（热重载，不用重启）。

常用项：

```jsonc
{
  // 目标窗口（留空则按 AyuGram / Telegram 进程名自动查找）
  "TargetExecutable": "",

  // 翻译服务
  "Provider": "OpenAICompatible",
  "ApiBaseUrl": "https://api.deepseek.com/v1",
  "ApiEndpoint": "",              // 留空则按 ApiBaseUrl 自动拼
  "ApiKey": "",                   // 保存时自动用 DPAPI 加密（enc:v1:…）；旧明文配置兼容
  "Model": "deepseek-chat",
  "TimeoutSeconds": 60,
  "MaxRetries": 2,
  "MaxBatchItems": 12,            // 批量翻译一次最多打包多少条
  "MaxBatchChars": 1800,          // 单次批量请求的最大字符数
  "Temperature": 0.2,

  // 语言
  "TargetLanguage": "zh-CN",
  "SourceLanguage": "auto",       // auto = 自动检测
  "SkipTargetLanguage": true,     // 已是目标语言则跳过

  // 提示词（{target} 会被替换成目标语言名）
  "SystemPrompt": "You are a professional real-time chat translator...",

  // 覆盖层外观
  "OverlayBackground": "#D9000000",   // #AARRGGBB
  "OverlayForeground": "#FFFFFFFF",
  "OverlayFontSize": 15,
  "OverlayFontFamily": "Microsoft YaHei UI",
  "OverlayBleed": 4,                  // 向外扩张像素，用来盖住原文
  "OverlayPaddingY": 3,
  "OverlayOpacity": 1.0,
  "OverlayClickThrough": true,        // 鼠标穿透
  "OverlayMaxLines": 6,

  // 识别
  "OcrLanguages": ["zh-Hans-CN", "en-US"],
  "OcrScale": 2.0,                    // 放大倍数，提识别率
  "AdaptiveOcrScale": false,          // 文字够大时跳过放大，省一次插值
  "LineMergeGap": 10,                 // 同一消息内行合并的最大间距
  "MinTextLength": 2,

  // 区域（Auto=true 时自动推断）
  "ChatRegion":      { "Auto": true,  "X": 0, "Y": 0, "Width": 0, "Height": 0 },
  "IncludeSidebar": false,

  // 运行
  "OverlayEnabled": false,            // 覆盖层翻译总开关（截图+OCR 链路），只用原生接管时保持关闭
  "PollIntervalMs": 700,              // 抓取间隔
  "SkipUnchangedFrames": true,        // 画面没变就不重跑 OCR / 翻译
  "MinRequestIntervalMs": 350,        // 请求节流，防限流
  "OnlyWhenTargetFocused": true,
  "HideOverlayWhenUnfocused": true,
  "CacheEnabled": true,
  "CacheMaxEntries": 4000,
  "ReuseSimilarity": 0.82,            // 位置复用阈值，1.0 = 关闭

  // 原生翻译接管（代理）
  "ProxyEnabled": false,              // 打开主界面时自动启动代理
  "ProxyPort": 8766,                  // 代理端口（改动需重新打补丁并重启 AyuGram）
  "ProxyAutoStart": false,
  "ProxyFallbackToGoogle": true,      // AI 失效时自动回退到免费谷歌翻译

  "DebugLog": false
}
```

日志：`%APPDATA%\AyuTranslate\logs\ayutranslate-YYYYMMDD.log`

---

## 自检与排障

程序内置无界面自检模式，用来分离定位问题所在。

```powershell
# 完整链路（定位→抓图→OCR→分块→翻译→生成预览图）
AyuTranslate.exe --selftest --dump

# 只测翻译接口，不需要 AyuGram 窗口
AyuTranslate.exe --translate-test

# 只验证识别，不联网（用离线占位翻译器）
AyuTranslate.exe --selftest --provider offline --dump

# 指定接口测试
AyuTranslate.exe --selftest --provider OpenAICompatible --url http://127.0.0.1:11434/v1 --model qwen2.5:7b

# 用自定义 HTTP 接口测试
AyuTranslate.exe --selftest --provider CustomHttp --url https://api.example.com/translate `
    --template '{"text":"{text}","to":"{target}"}' --response-path 'data.result'

# 端到端：真的显示覆盖层并截图（约 2 秒）
AyuTranslate.exe --selftest --overlaytest --out preview.png
```

参数一览：

| 参数 | 说明 |
|---|---|
| `--selftest` | 完整链路自检 |
| `--translate-test` | 只测翻译接口（不需要窗口），报告单条/批量耗时 |
| `--overlaytest` | 真正显示覆盖层并截图验证 |
| `--help` | 显示用法 |
| `--provider <名字>` | `OpenAICompatible` / `CustomHttp` / `DeepL` / `LibreTranslate` / `GoogleUnofficial` / `Offline` |
| `--url <地址>` | API 地址 |
| `--model <名字>` | 模型名 |
| `--key <密钥>` | API 密钥 |
| `--target <代码>` | 目标语言，如 `zh-CN` |
| `--exe <路径>` | AyuGram.exe 路径 |
| `--template <模板>` | 自定义请求体模板 |
| `--response-path <路径>` | 自定义响应取值路径 |
| `--headers <头>` | 额外请求头，多条用 `\|` 分隔 |
| `--sidebar` | 把会话列表也纳入识别范围 |
| `--dump` | 输出区域截图和全部文本块 |
| `--out <文件>` | 预览图输出路径 |
| `--config <文件>` | 用指定配置文件 |
| `--report <文件>` | 自检报告输出路径 |

### 常见问题

**「未找到 AyuGram 窗口」**
- 确认 AyuGram 在运行且窗口没有最小化
- 检查设置里的 `AyuGram.exe 路径`；如果进程名不是 AyuGram，程序会按 `AyuGram` / `Telegram` 关键字兜底查找
- 托盘菜单 → 重新连接 AyuGram 窗口

**「窗口不在当前虚拟桌面上（被系统屏蔽）」**
- 这是 Windows 虚拟桌面造成的：AyuGram 在另一个桌面（或「任务视图」里），窗口虽然 `IsWindowVisible=true`，但被 DWM 标记为 *cloaked*，抓图必然全白
- 解决：切换回 AyuGram 所在的虚拟桌面
- 程序会明确报出这个原因，不会让你误以为是 OCR 或区域配置问题

**抓图全黑 / 全白**
- 目标窗口被隐藏或最小化时，Windows 不给渲染内容
- AyuGram 刚启动时可能还没渲染完，等几秒再试
- 自检模式会自动尝试恢复窗口；仍不行就手动把 AyuGram 显示出来
- 程序内置了空白检测：抓到全白/全黑会直接报告，不会静默当成「没识别到文字」

**接口返回 403 / MODEL_NOT_IN_PLAN / Model not recognized**
- 模型名写错了，或你的账号套餐不包含该模型
- 点设置里的 **「获取模型列表」** 从 `/models` 拉取真实可用的模型名（需要服务商支持该端点）
- 命令行验证：`AyuTranslate.exe --translate-test`

**接口能用但很慢（每条好几秒）**
- 先跑 `--translate-test` 看「平均每条」耗时
- 如果单条正常但批量很慢，多半是模型在「思考」：推理型模型（DeepSeek 系、部分 Qwen）会把思考过程写在 `reasoning_content` 里，白耗 token
- 程序已内置应对：请求里带 `reasoning_effort: none` / `enable_thinking: false` / `thinking.type: disabled`，并按输入规模放大 `max_tokens`（否则推理会吃光额度导致正文为空）
- 如果服务商不支持这些字段，**换一个非推理模型**通常立竿见影。实测同一网关下 8 条批量：
  | 模型 | 耗时 | completion tokens |
  |---|---|---|
  | `moonshotai/Kimi-K2.7-Code-Highspeed` | 5.1 s | 661 |
  | `z-ai/glm-5.3-flashx` | 9.7 s | 863 |
  | `deepseek/deepseek-v4-flash-fast` | 12.8 s | 2561 |
  | `deepseek/deepseek-v4-flash` | 4.5 s（带防思考参数后） | — |
- 也可以加大配置里的 `MaxBatchItems` / `MaxBatchChars`，一次多翻几条摊薄延迟

**OCR 识别不到文字**
- 聊天窗口没打开会话（右侧是空白背景）
- 区域设置不正确 → 跑一次 `--selftest --dump` 看区域，或用 `--sidebar` 对比
- OCR 语言与消息语言不匹配 → 在设置里加上对应语言标签
- 见 [场景 B](#场景-b区域识别不准)

**覆盖层位置不对 / 盖不住原文**
- 加大「外扩像素」和「上下留白」
- 确认 DPI 缩放不是非整数（150% 缩放时定位精度会下降）

**翻译报错**
- 点设置里的「测试翻译接口」看具体错误，或用 `--translate-test`
- `HTTP 401` → 密钥不对
- `HTTP 404` → 地址不对，注意要不要带 `/v1`
- `HTTP 429` → 触发限流，加大「请求最小间隔」
- `响应结构无法识别` / `取值失败` → 用自定义 HTTP 时「响应取值路径」写错了
- 日志里能看到完整的请求与响应片段（开 `DebugLog`）

**同一句话反复请求接口**
- 打开 `DebugLog` 看是不是 OCR 抖动导致文本微变（例如末尾多一个 `：`）
- 程序已有宽松缓存键 + 位置复用两层兜底；还是频繁的话把 `ReuseSimilarity` 调低（如 `0.7`）

**热键无效**
- 组合被别的程序占用，日志会写明是哪一个
- 换一个组合并保存

---

## 原理与限制

### 两条路线各自的原理

**原生翻译接管**（方式一）

AyuGram 基于 Telegram Desktop，自带翻译功能，其后端在
`ayu/features/translator/implementations/` 下实现。其中 Google 后端向固定
地址发 `application/json+protobuf` 请求：

```
POST https://translate-pa.googleapis.com/v1/translateHtml
X-Goog-Api-Key: AIzaSyATBXajvzQLTDHEQbcpq0Ihe0vWDHmO520
[[["<原文>"],"auto","zh-CN"],"wt_lib"]
```

该地址在二进制里是唯一的字符串常量，且请求是明文 HTTP，所以可以：
改写地址 → 本地代理实现同协议 → 转发到自定义 AI。
AyuGram 的界面完全不知情，渲染保持原生。

**覆盖层翻译**（方式二）

实测 AyuGram 主窗口：

- **没有 MSAA / UIA 无障碍树**（`AutomationElement.FromHandle` 返回 0 个子节点，
  `AccessibleObjectFromWindow(OBJID_CLIENT)` 也拿不到 `IAccessible`）
- 因此**无法从外部逐条读取消息控件里的文本**

所以退而求其次：截图 + OCR 读出文本，再把译文画回原位置。

### 抓图方式

主用 `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)`：

- 窗口在后台、被遮挡时也能拿到内容
- 不要求 AyuGram 在最前面

程序会检测两种会导致抓图失败的状态：

- 窗口被隐藏/最小化 → Windows 不渲染，返回全黑
- 窗口被 DWM **cloaked**（在另一个虚拟桌面上）→ 返回全白

两种都会明确报出原因，不会静默当作「没识别到文字」。

### 覆盖层

一个置顶的 WPF 透明窗口，精确对齐到 AyuGram 客户区：

- `WS_EX_TOOLWINDOW` — 不显示在任务栏 / Alt+Tab
- `WS_EX_NOACTIVATE` — 不抢焦点
- `WS_EX_TRANSPARENT` — 鼠标穿透，点击直接落到 AyuGram（可关）
- 用 `SetWindowPos` 以物理像素定位，保证不同 DPI 下都对齐
- 原生样式下采样气泡背景色与文字色去覆盖，视觉上接近原地替换

### 已知限制

| 限制 | 说明 |
|---|---|
| **原生接管**：AyuGram 更新会覆盖补丁 | 重新打一次即可；程序会先校验字符串唯一性，不唯一就拒绝修改 |
| **原生接管**：只能接管 Google 后端 | Telegram 走后 MTProto、Yandex/Native 走各自协议，无法安全改写 |
| **原生接管**：需要保持代理运行 | 代理退出后 AyuGram 翻译会失败，还原补丁即可恢复 |
| **覆盖层**：依赖 OCR 精度 | 小字号、特殊字体、低对比度会识别错；程序提供放大倍数与字号下限来缓解 |
| **覆盖层**：图片内文字 | 会一起被识别翻译，这是特性也是干扰 |
| **覆盖层**：不改变布局 | 译文比原文长时会在框内截断（可用「最多显示行数」与行内省略号控制），不会撑开聊天区 |
| **覆盖层**：需要窗口可见 | 最小化 / 隐藏 / 在别的虚拟桌面时无法抓图 |
| **覆盖层**：长文本 | 很长的消息会被截断显示，但翻译请求仍是完整文本 |
| 滚动时 | 覆盖层跟随刷新，间隔取决于「抓取间隔」 |
| DPI 非整数缩放 | 150% 之类缩放下定位有几像素误差 |
| 切到后台 | 默认自动收起覆盖层（可关） |

---

## 从源码构建

### 依赖

- .NET 8 SDK
- Windows 10 SDK（`Windows.Media.Ocr` 的 WinRT 投影，`net8.0-windows10.0.19041.0` 目标会自动引用）

### 构建

```powershell
# 编译 + 发布单文件 exe 到 dist\（依赖已安装的 .NET 8 运行时）
pwsh -File build.ps1

# 顺带跑一次自检
pwsh -File build.ps1 -SelfTest

# 自带运行时（体积大，但目标机器不需要装 .NET）
pwsh -File build.ps1 -FrameworkDependent:$false

# 额外打包 zip
pwsh -File build.ps1 -Zip
```

也可以直接：

```powershell
dotnet build   src\AyuTranslate.csproj -c Release
dotnet publish src\AyuTranslate.csproj -c Release -r win-x64 --self-contained:false `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

### 用 mock 服务验证翻译链路

仓库带了一个极小的 OpenAI 兼容 mock 服务，用来在没有真实额度的情况下验证整条链路：

```powershell
# 终端 1
python tools\mock_openai.py 8765

# 终端 2
dist\AyuTranslate.exe --selftest --provider OpenAICompatible `
    --url http://127.0.0.1:8765/v1 --model mock-model --sidebar
```

它支持批量协议（`###<n>###<译文>`），会打印收到的原始请求，方便排查请求格式。

---

## 目录结构

```
TG-fanyi/
├─ build.ps1                     构建 / 发布脚本
├─ build.cmd                     双击入口（调用 build.ps1）
├─ .github/workflows/            CI：推送即自动编译并发布 Release
├─ tools/
│  └─ mock_openai.py             本地 mock 翻译服务
└─ src/
   ├─ AyuTranslate.csproj
   ├─ app.manifest               DPI 感知声明
   ├─ Program.cs                 入口（分发自检 / GUI 两条路径）
   ├─ SelfTest.cs                无界面模式：自检 / 接口测试 / 补丁 / 代理
   ├─ Compositor.cs              自检用：把覆盖条目合成到截图上
   ├─ Core/
   │  ├─ Native.cs               user32 / gdi32 / dwmapi 互操作（含 cloaked 检测）
   │  ├─ AppConfig.cs            配置模型与读写
   │  ├─ Log.cs                  日志
   │  ├─ TextUtil.cs             文本清洗、语言判定、相似度、缓存键
   │  ├─ LanguageMap.cs          语言代码 / 字符集 / 各服务商映射
   │  ├─ ColorSampler.cs         采样气泡背景色/文字色（原生样式覆盖用）
   │  ├─ WindowLocator.cs        定位 AyuGram 主窗口（含可用性诊断）
   │  ├─ ScreenCapture.cs        PrintWindow / BitBlt 抓图
   │  ├─ OcrService.cs           Windows OCR 封装（多语言并行）
   │  ├─ BlockDetector.cs        OCR 行 → 消息块；去重；区域推断
   │  ├─ TranslationPipeline.cs  抓图→OCR→分块→翻译→覆盖条目（含空白检测）
   │  ├─ ProxyManager.cs         原生接管：代理生命周期 + 补丁状态
   │  ├─ InputInjector.cs        写回输入框
   │  ├─ HotkeyManager.cs        全局热键
   │  └─ AppController.cs        总控
   ├─ Proxy/
   │  ├─ TranslateProxyServer.cs 冒充 AyuGram 的 Google 翻译接口
   │  └─ BinaryPatcher.cs        改写/还原 AyuGram.exe 里的接口地址
   ├─ Translate/
   │  ├─ ITranslator.cs          后端接口
   │  ├─ Http.cs                 共享 HttpClient + 重试
   │  ├─ OpenAiCompatibleTranslator.cs   (含批量协议 + 防思考参数)
   │  ├─ CustomHttpTranslator.cs
   │  ├─ DeepLTranslator.cs
   │  ├─ LibreTranslateTranslator.cs
   │  ├─ GoogleTranslator.cs
   │  ├─ OfflineTranslator.cs
   │  ├─ ModelListService.cs     从 /models 拉取可用模型
   │  ├─ TranslationCache.cs     LRU 缓存
   │  └─ TranslatorFactory.cs
   └─ UI/
      ├─ OverlayWindow.cs        透明置顶穿透窗口
      ├─ OverlayRenderer.cs      覆盖层绘制
      ├─ SettingsWindow.xaml(.cs) 设置界面
      ├─ SettingsHost.cs         单实例宿主
      └─ TrayIcon.cs             托盘图标与菜单
```

---

## 更新日志

### v1.1.1（2026-09-30）

- **覆盖层翻译默认关闭**：新增总开关「启用覆盖层翻译」（设置 → 识别与区域，配置项
  `OverlayEnabled`，默认 `false`）。只用「原生翻译接管」时不再周期性抓图 / OCR，
  CPU 占用显著下降。关闭后自动翻译、手动翻译热键（`Ctrl+Alt+T`）、覆盖层显隐全部停用；
  需要覆盖层时勾选开启即可。自检命令（`--selftest` / `--overlaytest`）不受影响。

### v1.1.0（2026-09-30）

**新增**

- **AI 失效自动兜底**：你的 AI 报错 / 超时 / 未配置时，代理自动把请求转发给
  AyuGram 原本的免费谷歌翻译接口，聊天始终能出译文。设置 → 原生翻译接管里可关闭。
  兜底译文同样进缓存，翻译日志会标注「（谷歌兜底）」。
- 代理层翻译缓存：重复翻译同一段文本直接秒回，不再重复请求接口
- 代理并发限流（最多 3 路同时请求 AI）+ 单次调用时间预算（AyuGram 15 秒超时之内完成，
  开兜底时 AI 10s / 谷歌 4s，不开兜底 12s）
- 配置热更新实时同步到运行中的代理：换模型 / 密钥 / 地址立即生效，无需重启
- API 密钥改用 **DPAPI 加密存储**（`enc:v1:` 前缀）；旧明文配置完全兼容，
  下次保存时自动转密文
- 新配置项：`SkipUnchangedFrames`（帧跳过开关）、`AdaptiveOcrScale`（文字够大时跳过
  放大插值）、`MaxBatchItems` / `MaxBatchChars`（批量翻译参数）、`ProxyFallbackToGoogle`

**修复**

- 修复「修改 API 地址不生效」：按 base url 派生的完整地址不再持久化到 `ApiEndpoint`，
  旧配置里的派生值加载时自动迁移清空
- 翻译失败时**如实透传 HTTP 错误**（401 / 404 / 429 / 超时…），
  不再误报为「模型未返回正文」
- HTTP 层失败不再盲目重试 `max_tokens`；批量请求失败不再退化为逐条风暴
- 二进制补丁 / 还原改为**原子写回**（临时文件 + Replace），中途断电不会再损坏 AyuGram.exe

**性能**

- **帧跳过**：画面与上一帧一致（采样差异 < 1%）时不再跑 OCR / 翻译，
  静止画面几乎零开销；手动翻译绕过帧跳过，保证能重试
- 图像链路优化：SoftwareBitmap 直接从像素缓冲构造（省一次 BMP 编解码）、
  多 OCR 引擎共享同一份位图、抓图转换与裁剪合并为一次拷贝、调试快照仅 DebugLog 开启时生成
- 日志改为常开句柄写入，跨午夜自动滚动新文件

**其他**

- `TargetExecutable` 不再写死默认路径，留空按进程名自动查找
- HTTP 重试判断改为结构化状态码（429 / 408 可重试，其余 4xx 不重试）

### v1.0.0

首个公开版本：

- 方式一「原生翻译接管」：改写 AyuGram 内置 Google 翻译地址 → 本地代理 → 你的 AI，
  界面完全原生（一键安装 / 一键还原，改动前强制备份）
- 方式二「覆盖层翻译」：截图 → Windows OCR 本地识别 → AI 翻译 → 译文按气泡配色画回原位
- 后端：OpenAI 兼容（DeepSeek / Ollama / vLLM / LM Studio / 网关…）、自定义 HTTP、
  DeepL、LibreTranslate、Google 非官方
- 批量翻译协议（多段一次请求）、多语言 OCR 并行识别与去重、
  缓存 + 位置复用、全局热键、写回输入框、无界面自检（`--selftest` / `--translate-test`）

---

## 许可与合规

- **方式二（覆盖层）** 不修改、不注入 AyuGram，只读取屏幕像素并绘制自己的窗口。
- **方式一（原生接管）** 会改写 `AyuGram.exe` 中 **52 字节**的接口地址字符串，
  不涉及任何代码逻辑；程序强制保留原文件备份，可一键逐字节还原。
  AyuGram 是 GPLv3 开源项目，本地修改自用没有问题；请遵守其许可证。
- OCR 在本机完成，不上传图像。
- 只有**识别出的文本**会发送到你配置的翻译接口。请自行确认该服务的数据处理条款。
- 翻译内容请遵守你所使用服务的条款与当地法律法规。

