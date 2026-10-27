# 円香桌宠

Windows 独立悬浮桌宠。人物采用你提供的樋口円香 Q 版造型，透明底，仅保留人物、帽子和衣服。没有底座、文字或装饰背景。使用 Windows 自带的 .NET Framework / WPF，不需要 Python、Node.js 或 API Key。

## 启动与操作

双击 **MadokaPet.exe**，或双击 **Start.cmd**。

- 单击人物：打开 / 收起额度面板。
- 按住人物并拖动：移动位置。
- 右键人物：查看额度、换台词、暂停随机台词、调整大小、切换置顶、移回右下角、退出。
- 面板的“刷新额度”：手动刷新。后台每 60 秒自动刷新。
- 面板的“说点什么”：随机显示一句台词。
- Esc 或面板右上角 ×：收起面板。
- 系统托盘菜单也能显示桌宠和退出。

程序会在旁边的 `state.json` 中记住位置、大小、置顶和台词开关。不添加开机启动。

## 用量从哪里来

使用官方 Codex app-server 的只读 `account/rateLimits/read` 接口，通过本机已安装的 `codex.exe` 和已经登录的 ChatGPT 账号查询。桌宠不读取、复制或记录你的登录令牌，不请求生成回答、不消耗模型推理额度，也不兑换重置或购买点数。

显示接口实际返回的各个额度周期（例如 5 小时、每周），包括已用百分比、剩余百分比、恢复时间和服务端提供的额外点数余额。剩余比例按 `max(0, 100 - usedPercent)` 计算；进度条表示已用比例。界面不会根据百分比推算 token 数或还能发几条消息。

账号以本机 Codex CLI 的登录状态为准。如果桌面应用与 CLI 使用不同账号或工作区，两者显示的额度可能不同。API Key 登录不提供 ChatGPT 订阅额度。显示的是该接口返回的 Codex / Work 额度视图，不保证包含所有账户方案中的月度预算或组织控制项。

数据超过 3 分钟、到达返回的恢复时间、或刷新失败时，会明确标记旧数据。连接失败且没有历史数据时显示“暂时无法获取额度”，不显示虚构的 0% 或 100%。这些数据仅保存在内存中，退出后清空。

如果找不到 Codex：先安装并用 ChatGPT 账号登录 Codex。程序会查找 PATH、Codex 桌面应用的本地安装目录和常见的 npm 安装目录。自定义安装位置可通过 `MADOKA_CODEX_PATH` 环境变量指向实际的 `codex.exe`。不需要在桌宠里输入账号密码。

官方协议说明：[Codex App Server](https://learn.chatgpt.com/docs/app-server)。

## 台词与人物素材

`dialogues.json` 保存中文同人原创台词，按待机、点击、低额度、额度耗尽、深夜、连接失败等场景分类。台词保持简短、略带疏离和吐槽，关心表达比较含蓄。**不是游戏原文或官方台词。**

随机台词每 55–110 秒出现一次，停留约 9 秒，避免连续重复。深夜台词按 Windows 本地时间切换。额度相关台词只在有新鲜、可用的数据时出现。暂停后不弹出待机气泡，仍可在面板里手动换台词。

人物使用内置 ImageGen 根据你提供的图片重新整理，图像位于 `assets/madoka.png`，使用真实透明背景。生成要求保存在 `assets/generation-prompt.txt`。桌宠有轻微的呼吸浮动；遵守 Windows 关闭界面动画的设置。此版本没有生成完整的逐帧动作动画。

这是非官方同人小工具，与万代南梦宫或 OpenAI 没有隶属关系。

## 修改与重建

源码在 `src/MadokaPet.cs`。修改台词或 PNG 后运行 `build.ps1`，会将人物图片和台词一起嵌入程序。关闭正在运行的桌宠后再重建。可执行文件可以单独复制到自己的可写文件夹使用。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

开发检查：

```powershell
.\MadokaPet.exe --self-test .\qa\self-test.txt
.\MadokaPet.exe --preview .\qa
.\MadokaPet.exe --probe .\qa\live-usage.json
.\MadokaPet.exe --smoke .\qa
```

`--preview` 只渲染布局，预览数字明确标成演示数据。`--probe` 做真实的只读额度查询，仅保存额度字段，不保存账号标识或凭证。`--smoke` 短暂显示窗口、检查面板和设置、查询额度、保存结果后退出。PowerShell 可能异步启动 GUI 程序；自动化检查请用 `Start-Process -Wait -WindowStyle Hidden` 等待完成。

`qa` 包含自检、布局预览和实际运行检查结果。真实额度和界面截图属于本机检查产物，不应公开上传。
