# 运行时启动与窗口生命周期

## 模块职责
- 定义应用从 `Main()` 到主窗口可用的完整启动链路。
- 管理单实例行为、依赖注入容器初始化、运行时异常处理与退出释放。
- 管理主窗口与设置窗口的基础生命周期行为（显示、隐藏、导航、释放）。

## 关键入口
- `STranslate/App.xaml.cs`
  - `Main()`：单实例判定、Velopack 回调、管理员模式启动判定。
  - `App()` 构造函数：读取三类设置并注册 DI。
  - `OnStartup()`：插件/服务/数据库初始化，必要时先显示首次向导，再创建主窗口并执行启动后延迟初始化。
  - `Register*Exception()`、`RegisterExitEvents()`、`Dispose()`：异常与退出收敛。
- `STranslate/Core/ISingleInstanceApp.cs`
  - `SingleInstance<TApplication>.InitializeAsFirstInstance()`：Mutex + NamedPipe 单实例通知。
- `STranslate/Views/MainWindow.xaml.cs`
  - `OnLoaded()`、`OnContentRendered()`、`OnDeactivated()`、`Dispose()`。
- `STranslate/Views/SettingsWindow.xaml.cs`
  - `Navigate()`：设置页导航入口与导航状态同步。
- `STranslate/Views/WelcomeSetupWindow.xaml.cs`
  - 首次欢迎/快速配置向导：仅当启动前 `Settings.json`、`HotkeySettings.json`、`ServiceSettings.json` 三个配置文件都不存在时自动打开；也可从关于页手动打开。窗口使用小尺寸纵向分页，第 1 页提供紧凑欢迎说明和语言选择。
- `STranslate/Core/AppMessageBox.cs`
  - 应用级 MessageBox 入口：优先使用当前活动窗口作为 owner，缺省时用主屏中心的临时透明 owner 承载 iNKORE 弹窗。
- `STranslate/Helpers/UACHelper.cs`
  - `Run()`：通过 Host 进程按普通/提权/计划任务模式拉起应用。
- `STranslate.Host/src/commands/start.rs`
  - `start` 命令：延迟启动、等待旧进程退出、必要时清理旧进程。

## 核心流程
### 从入口到结果：应用启动到主窗口可用
1. `App.Main()` 先调用 `SingleInstance<App>.InitializeAsFirstInstance()`。
2. 若是第二实例：通过命名管道通知首实例触发 `OnSecondAppStarted()` 并退出；首实例执行 `MainWindowViewModel.Show()`。
3. 首实例继续执行 Velopack `OnAfterUpdateFastCallback`，把临时配置目录回迁到便携数据目录。
4. 执行 `NeedAdmin()`：若启动模式要求提权，先清理单实例资源，再交给 `UACHelper` 重启进程后当前实例返回。
   - `UACHelper.Run(mode, waitPid: Environment.ProcessId, waitTimeoutSec: 6)` 会把当前进程 ID 传给 Host。
   - Host 启动前等待旧进程退出；超时后尝试 `taskkill /PID <pid> /T /F`，并输出 `handover ...` 报告。
5. 创建 `App` 实例：从 `AppStorage<T>` 读取 `Settings`、`HotkeySettings`、`ServiceSettings`。
6. 在 `App()` 内配置 DI：注册核心服务（插件/服务管理、翻译链、HTTP、窗口 VM、更新、外部调用、数据库等）。
7. `OnStartup()` 中顺序初始化：
   - `PluginManager.LoadPlugins()`
   - `ServiceManager.LoadServices()`
   - `SqlService.InitializeDB()`
   - 注册全局异常处理
8. 若 `App` 构造阶段记录到三类配置文件启动前都不存在，则在主窗口创建前打开欢迎向导；打开前只初始化语言资源，保证向导首页可切换语言。
9. 向导关闭、跳过或完成时调用现有 `Save()` 生成配置文件，后续启动按文件已存在不再自动弹出。
10. 向导结束后创建主窗口与主 VM；主窗口 `Loaded` 时执行延迟初始化：`Settings.LazyInitialize(initializeLanguage: false)`、`HotkeySettings.LazyInitialize()`、托盘提示、WebDav 后置备份上传，以及自动检查更新服务启动。
11. 非首次向导路径仍按原流程创建主窗口，并在 `Loaded` 时执行完整 `Settings.LazyInitialize()`。

### 从入口到结果：退出与资源回收
1. `ProcessExit`、`Application.Exit`、`SessionEnding` 任一事件触发时统一进入 `Dispose()`。
2. 在主线程释放资源：通知图标卸载、主窗口 VM 释放、主窗口释放、`PluginManager.Dispose()` 清理临时解压目录。
3. 需要重启生效的插件目录清理并不在这里直接删除，而由下次启动时的插件扫描阶段处理（`NeedDelete`/`NeedUpgrade` 标记）。

### 从入口到结果：管理员/后台启动交接
1. 主进程判断需要以其他启动模式重启时，调用 `SingleInstance<App>.Cleanup()` 释放 Mutex 和命名管道。
2. `UACHelper.Run()` 组装 Host `start` 命令参数：
   - `--mode direct/elevated/task`
   - `--delay <seconds>`
   - `--wait-pid <pid>`
   - `--wait-timeout <seconds>`
3. Host 先等待旧 PID 退出，避免新实例注册单实例、热键或托盘资源时撞上旧进程残留。
4. 等待超时才尝试清理旧进程；启动结果和清理结果会写入标准输出/错误，方便排查后台拉起失败。

### 窗口生命周期要点
- `MainWindow`：
  - `OnLoaded()` 会按 `HideOnStartup` 计算窗口位置并挂接窗口过程钩子。
  - `OnContentRendered()` 决定首次显示或隐藏。
  - `OnDeactivated()` 可按 `HideWhenDeactivated` 自动隐藏，避免 Alt-Tab 残留。
- `SettingsWindow`：
  - `Navigate(tag)` 根据页面类型从 DI 取页实例并注入到 `RootFrame.Content`。
  - `Ctrl+F` 由 `OnKeyDown` 路由到当前页面的搜索框。
- `WelcomeSetupWindow`：
  - 自动显示只由启动前配置文件是否存在决定，不新增单独的完成标记。
  - 首次自动显示会阻塞主窗口创建，避免新用户先看到主窗口再看到向导；关于页手动打开向导不影响当前主窗口显隐。
  - 向导为 5 页纵向流程：欢迎和语言选择、翻译服务、OCR/TTS 服务、图片翻译/替换翻译专用服务、关键快捷键。
  - 上一页/下一页仅切换中间内容区；内容使用 XamlFlair 的轻量入场动画，顶部品牌区、底部按钮和进度文本保持静止。
  - 第 1 页不使用整块强调色背景，只保留紧凑品牌块和语言选择，避免大面积空白。
  - 向导复用现有服务、插件配置 UI 和 `HotkeyControl`；快捷键页只显示打开窗口、输入翻译、划词翻译、截图翻译、OCR、图片翻译、替换翻译。
  - 首次自动显示时主窗口尚未创建，快捷键编辑 `ContentDialog` 必须显式绑定当前向导窗口作为 owner，避免无参 `ShowAsync()` 等待后续应用激活才显示。
  - 关于页中的“向导”按钮可手动重新打开。
- `AppMessageBox`：
  - 项目内 MessageBox 必须统一走 `AppMessageBox.Show()`，不要直接调用 iNKORE `MessageBox.Show()`。
  - 如果存在活动且可见的应用窗口，优先用该窗口作为 owner，保持 UI 点击触发弹窗的窗口上下文。
  - 如果没有活动窗口，才创建一个 1x1、透明、置顶、不进任务栏的临时 owner，位置固定在主显示器正中心。
  - 弹窗显示前会切回 UI Dispatcher；临时 owner 会被激活并在弹窗关闭后立即清理。
  - 该策略兼顾窗口内点击弹窗和后台/热键触发弹窗，避免主窗口隐藏、设置窗口在后台或没有可见应用窗口时，模态弹窗创建成功但不可见。

## 关键数据结构/配置
- `Settings`：主行为配置（窗口、主题、热键策略、网络、OCR/图像翻译参数等）。
- `StartMode`：普通启动、提权启动、计划任务启动。
- `HotkeySettings`：全局热键、软件内热键、增量翻译键、Ctrl+CC 配置。
- `ServiceSettings`：服务实例列表与特殊服务 ID（替换翻译、图片翻译）。
- `DataLocation`：便携/漫游目录选择、日志/缓存/配置路径、`InfoFilePath` 与 `BackupFilePath`。

## 关键文件
- `STranslate/App.xaml.cs`
- `STranslate/Core/ISingleInstanceApp.cs`
- `STranslate/Views/MainWindow.xaml.cs`
- `STranslate/Views/SettingsWindow.xaml.cs`
- `STranslate/Core/AppMessageBox.cs`
- `STranslate/Core/DataLocation.cs`
- `STranslate/Helpers/UACHelper.cs`
- `STranslate.Host/src/commands/start.rs`

## 常见改动任务
- 新增启动期服务：在 `App()` 的 `ConfigureServices` 注册，并在 `OnStartup()` 明确初始化顺序。
- 增加全局异常策略：优先放到 `RegisterDispatcherUnhandledException` / `RegisterTaskSchedulerUnhandledException`。
- 调整窗口初始行为：优先改 `MainWindow.OnContentRendered()` 与 `MainWindowViewModel.UpdatePosition()` 配合逻辑。
- 调整首次向导：优先改 `App` 中启动前配置文件存在性判断、主窗口创建时机和 `WelcomeSetupWindow`，避免用额外设置项控制是否已完成。
- 设置页新增导航项：同步修改 `SettingsWindow.xaml` 菜单与 `SettingsWindow.xaml.cs` 的 `Navigate` 映射。
- 新增阻断性弹窗：统一调用 `AppMessageBox.Show()`，保持活动窗口优先、透明 owner 兜底的策略。
- 调整管理员或后台启动：同步修改 `UACHelper.Run()` 与 Host `start` 命令参数，确保主进程和 Host 协议一致。
