# 云斗成绩汇总 · 阅卷回执

从云斗 NOI 批量评测专区汇总各省成绩，按总分降序导出 Excel。第三方工具，与云斗学院、CCF 无隶属关系。

## 下载与使用

从 [GitHub Releases](../../releases/latest) 下载 `YundouScore-v2.exe`。双击打开本地工作台，输入赛事（如 `NOIP 2025`、`CSP-S 2026` 或 `联合省选 2026`），选择官方或民间数据，点击「开始汇总」。生成后点击「下载 Excel」。工作台的「退出程序」会关闭本地服务。

Windows 10/11 免安装，使用系统已有的 Edge 和 .NET Framework，不需要 Office、Python、Node.js 或 Playwright。工作台不开放局域网。

## 登录和续传

- 初次请在**专用 Edge 窗口**的云斗网页完成验证和登录，勾选「记住我」。后续复用 `%LOCALAPPDATA%\YundouScore\EdgeProfile`，会话有效时自动继续。云斗会话仍可能过期；程序不读取或导出账号密码、Cookie，也不使用原浏览器的登录档案。
- 已验证的省级成绩立即保存到 `%LOCALAPPDATA%\YundouScore\Scores\v1`，按账号和场次隔离。下次仍在线确认场次和权限，但只下载本地缺失或损坏的成绩。赛后补评时可勾选「重新下载成绩」；成功才替换旧缓存。
- 停止时会先尽可能保存正在下载的一批，再结束任务。不会生成不完整的 Excel。云斗限制每 60 秒最多下载 3 份 CSV；界面上的倒计时只适用于需要下载的省份。

## 命令行

```powershell
.\YundouScore-v2.exe "NOIP 2025" --out "成绩汇总.xlsx"
```

默认官方数据；用 `--data unofficial` 单独导出民间数据，`--refresh` 重新下载，`--web` 打开工作台。不会覆盖现有文件。

同省取最新已结束且有可见评测记录的场次；模板及「暂不公开」的场次不纳入。「已结束」不等于每人每题皆已评测，0 分也可能是未评到。成绩使用云斗原值降序，同分并列；来源页列明下载时间和是否复用缓存。结果不是 CCF 发布的官方全国榜。

可在 PowerShell 执行 `./build.ps1` 重新编译；`./YundouScore-v2.exe --self-test` 运行缓存与工作簿自检。
