# 博宜练习室 · Boyi Studio

一个面向 Windows 的 C++ 练习桌面客户端，连接东方博宜 OJ，提供题面阅读、代码编辑、提交记录与复习工具。采用 WPF + WebView2，界面由原生 JavaScript 和 React 组件组成。

**公开发布版不内置参考答案库，不包含任何用户账号、密码、草稿、提交源码或壁纸文件。** 本项目独立开发，并非东方博宜官方产品。

## 下载与运行

从本仓库 **Releases** 下载 `BoyiStudio-1.4.5-win-x64-no-references.zip`，完整解压后运行 `博宜练习室.exe`。

- Windows 10/11，x64。
- 需要 [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)。
- 发布包自带 .NET 运行时。
- 本地运行 C++ 代码需要自行安装支持 C++11 的 `g++`，并在“外观与设置”中填写 `g++.exe` 路径。

## 功能

- 缓存题库与题面，使用抽屉式题库查找、筛选和打开题目。
- 代码编辑、自动保存、草稿恢复、独立保存待提交版本。
- 查看自己的官网提交；本地提交快照按账号保存，历史源码按官网权限读取。
- 题单连续练习、上一题/下一题、返回题单和继续上次进度。
- 小组浏览和题单入口；完整管理操作通过应用内官网窗口完成。
- 本地样例运行、输出差异、复习笔记和错题记录。
- 连续圆角、按压回弹、明暗主题和可选玻璃效果。
- 可选 Wallpaper Engine 联动：读取本机选定的视频/图片；场景及网页壁纸使用静态预览，视频为独立静音播放。

## 数据与边界

用户数据位于 `%LOCALAPPDATA%\BoyiStudio`。草稿、提交快照、复习笔记和补充题目缓存按账号保存；此目录不属于仓库，也不应上传。发布版首次启动不预设账号，不导入开发者的历史提交记录。

不内置答案不等于限制导入答案：如果同一台电脑已有本地参考答案，程序仍可能读取它们。卸载或更换发布包不会自动删除个人数据。

网站登录、题目可见性、源码查看及小组权限由东方博宜决定。公开样例通过不代表官网评测通过。程序对结果不确定的提交保留状态，不会自动重复发送。普通批量队列会跳过已通过题目，主动重交需在界面中明确选择。

## 从源码构建

需要 Node.js 22、.NET 10 SDK 和 Windows。

```powershell
pwsh -File tools/build-release.ps1
```

脚本会安装锁定的前端依赖、构建界面、发布 Windows 自包含程序、检查无参考答案载荷并生成 ZIP。结果位于 `dist/`。

单独构建前端：

```powershell
cd glass-frontend
npm ci
npm run build
```

运行项目：

```powershell
dotnet run --project glass-oj/GlassOJ.csproj -c Release
```

为防止影响自己的数据，可添加 `--test-mode --test-data-root=绝对路径` 使用隔离数据目录。`--test-scenario` 仅在同时指定这两个参数时执行随项目提供的无答案版冒烟检查。

## 目录

- `glass-oj/`：WPF 宿主、官网连接、本地数据及分发资源。
- `glass-frontend/`：React 组件和前端构建配置。
- `tools/`：构建、发布与载荷检查脚本。
- `.github/workflow-templates/`：可选 Windows 构建模板，默认不自动运行。若需 GitHub Actions，将模板复制到 `.github/workflows/`；推送工作流的凭据需要 `workflow` 权限。

## 许可证与第三方内容

本项目原创代码使用 [MIT 许可证](LICENSE)。第三方组件仍遵循各自许可证，详见 [第三方声明](glass-oj/THIRD-PARTY-NOTICES.txt) 和 `glass-oj/ui/vendor/` 中保留的许可证。

`glass-oj/seed/problems/`、`glass-oj/seed/assets/` 和题库元数据中的题面、插图等内容来自 [东方博宜 OJ](https://oj.czos.cn) 及原题作者，**不属于本项目 MIT 授权范围**。本项目不声明拥有这些内容的著作权。Wallpaper Engine 壁纸不随仓库或发布包分发。
