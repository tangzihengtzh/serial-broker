# Connection History and Tray Startup Implementation Plan

> 按用户授权在当前会话执行；完成修改和测试后暂不提交、不推送。此项局部维护沿用现有实现，不引入额外开发框架。

> 2026-11-10追加授权：将清除历史连接合并到设置菜单，统一版本2.0.1，README简短新增功能说明，验证后提交本地Git并标记V2.0.1。本授权取代上面的暂不提交要求。

**Goal:** 为V2.0.0增加可确认、可持久化的清除历史连接及可选开机自启动到托盘功能。

**Architecture:** WorkspaceWindow统一清理Preferences.History，先保存再更新各SessionView的端口候选；保存失败恢复原历史。确认框默认取消。刷新端口只更新候选和串口显示，不回填配置或停止原生连接。

**Tech Stack:** .NET 8、WPF/ModernWpf、现有命名管道模拟设备测试、MSVC/CMake。

**追加需求（2026-11-10）：** 在顶部设置菜单增加可勾选的“开机自启动到托盘”。使用当前用户Windows Run注册项，无需管理员；登记当前EXE、独立数据目录及--start-in-tray参数。取消勾选删除注册项，读写失败显示错误并恢复勾选状态；普通启动仍显示窗口。测试使用隔离注册表子键，不改用户真实启动项，不执行真实注销/登录。

---

### Task 1: 增加入口及持久化操作

文件：ui/SerialBroker.Gui/WorkspaceWindow.xaml、WorkspaceWindow.xaml.cs；函数：ClearHistoryClick、ClearConnectionHistory、SavePreferences、UpdateSummary。

- [x] 顶部增加“清除历史连接”按钮，历史为空时禁用。
- [x] 点击显示带历史数量的确认框，默认取消；测试可注入确认结果。
- [x] ClearConnectionHistory保存空历史；SavePreferences返回保存是否成功，失败时恢复原列表并保留错误提示。
- [x] ui/SerialBroker.Gui/SessionView.xaml.cs的RefreshPorts改为internal，清除后刷新所有标签；不重载连接配置。

### Task 2: 回归验证

文件：tests/SerialBroker.Tests/Program.cs；函数：Ui。

- [x] 通过实际按钮事件验证取消、确认、空历史禁用、立即持久化和多标签下拉刷新。
- [x] 使用现有模拟设备验证当前连接、转发、监视和草稿参数不受清除影响。
- [x] 验证重启不恢复已清除历史，新连接可以重新记录，主题保存不恢复旧历史。
- [x] 用偏好目录不可写场景验证清除失败保留原历史及错误提示。
- [x] 运行 scripts/test.ps1，要求构建、功能测试及正式DLL边界测试全部通过；不打开物理COM。

### Task 3: 文档和交付

- [x] 更新README.md、README.zh-CN.md、docs/CHANGELOG.md、docs/DESIGN.md及docs/V2-USER-GUIDE.md，说明入口、确认、保存和保留当前连接的行为。
- [x] 运行 python scripts/render-help.py 更新离线HTML帮助。
- [x] 在docs/VALIDATION.md追加此次验证结果，检查git diff --check及git status；保留未提交修改。

### Task 4: 可选自启动到托盘

文件：ui/SerialBroker.Gui/StartupRegistration.cs（新增）、App.xaml.cs、WorkspaceWindow.xaml、WorkspaceWindow.xaml.cs、tests/SerialBroker.Tests/Program.cs。

- [x] StartupRegistration封装读取/写入/移除当前用户Run值；状态以注册表为准，路径带引号并保留独立数据目录。
- [x] 顶部设置菜单提供可勾选项，初始化及打开菜单时读取实际注册状态；保存失败恢复原状态并提示错误。
- [x] App解析--start-in-tray和--data-directory，托盘启动不Show主窗口；通过ShowFromTray恢复正常窗口且不自动连接设备。
- [x] 在临时HKCU子键验证带空格/中文路径、启用、重新读取、禁用、失败恢复；验证隐藏启动、托盘恢复、普通启动和参数校验。
- [x] 更新用户说明和验证记录，重新运行scripts/test.ps1；不提交、不推送。

### Task 5: V2.0.1

- [x] 将WorkspaceWindow.xaml的清除按钮改为设置ContextMenu中的ClearHistoryMenuItem，同步UpdateSummary及实际菜单事件测试。
- [x] 更新CMake、原生资源、CLI、.NET元数据、打包脚本和版本断言为2.0.1。
- [x] README仅新增两条简短功能说明；同步帮助文档及CHANGELOG中的菜单入口。
- [x] 执行scripts/test.ps1并打包artifacts/V2.0.1，验证GUI/原生DLL/CLI版本。
- [x] 检查diff后提交本地Git并创建V2.0.1标签。
