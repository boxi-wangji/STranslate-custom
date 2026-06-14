# OCR 与图片翻译链路

## 模块职责
- 提供截图 OCR、OCR 窗口、图片翻译窗口三条图像文本处理链路。
- 管理 OCR 服务与图片翻译专用 OCR/翻译服务绑定。
- 在图片翻译中完成版面分析、文本块合并、翻译回写与结果图生成。
- 将截图翻译和静默 OCR 结果接入统一取词文本后处理。

## 关键入口
- `STranslate/ViewModels/MainWindowViewModel.cs`
  - `ScreenshotTranslateAsync()` / `ScreenshotTranslateHandlerAsync()`
  - `OcrAsync()` / `OcrHandlerAsync()`
  - `ImageTranslateAsync()` / `ImageTranslateHandlerAsync()`
- `STranslate/ViewModels/OcrWindowViewModel.cs`
  - `ExecuteAsync(Bitmap)`：OCR 窗口主执行命令。
- `STranslate/ViewModels/ImageTranslateWindowViewModel.cs`
  - `ExecuteAsync(Bitmap)`：图片翻译窗口主执行命令。
  - `ApplyLayoutAnalysis(OcrResult)`：调用版面分析器重组 OCR 文本块。
- `STranslate/Core/OcrLayoutAnalyzer.cs`
  - `AnalyzeBlocks(OcrResult, LayoutAnalysisMode)`：图片翻译版面分析入口，输出带来源、置信度、段落框和行框的内部布局块。
  - `Apply(OcrResult, LayoutAnalysisMode)`：兼容入口，将布局块投影回段落级 `OcrContent`。
- `STranslate/Core/Screenshot.cs`
  - `GetScreenshotAsync()`：截图前隐藏主窗口，调用 `ScreenGrabber`。
- `STranslate/Services/OcrService.cs`
  - `ImageTranslateOcrService`：图片翻译专用 OCR 服务选择与持久化。

## 核心流程
### 从入口到结果：主窗口截图翻译
1. `MainWindowViewModel.ScreenshotTranslateAsync()` 先取可用 OCR 服务。
2. 通过 `IScreenshot.GetScreenshotAsync()` 获取截图位图（主窗口可见且非置顶时先折叠，避免截到自身）。
3. `ScreenshotTranslateHandlerAsync()` 调 OCR `RecognizeAsync()`。
4. OCR 成功后：按设置可复制识别文本，再通过 `HandleCapturedText(text, TextSeparatorHandleScope.ScreenshotTranslate)` 处理换行与可选分隔符。
5. 处理后的文本调用 `ExecuteTranslate()` 进入主翻译链路。
6. `Settings.FocusInputAfterScreenshotTranslate` 控制截图翻译完成后是否显示主窗口并聚焦输入框；关闭且主窗口置顶时只更新结果不抢焦点，非置顶时仍会恢复主窗口。

### 从入口到结果：静默 OCR
1. `SilentOcrAsync()` 截图后调用当前 OCR 服务识别文本。
2. 识别成功后写入剪贴板。
3. 若启用了 `TextSeparatorHandleScope.SilentOcr`，写入前会复用 `HandleCapturedText()` 处理换行与 `_` / `-` 分隔符。

### 从入口到结果：OCR 窗口执行
1. `OcrWindowViewModel.ExecuteAsync(bitmap)` 设置执行态并清理旧结果。
2. 调用当前启用的 OCR 服务 `RecognizeAsync(new OcrRequest(data, Settings.OcrLanguage, bitmap.Width, bitmap.Height))`。
3. OCR 返回后调用 `Utilities.NormalizeOcrCoordinates()`，把 `Normalized` 坐标统一换算为图片像素坐标；缺少图片尺寸时 normalized 坐标安全退化为无坐标。
4. 生成两类展示数据：
   - 原图/标注图（边框）
   - `OcrWords` 与 `Result` 文本
5. 根据 `Settings.IsOcrShowingAnnotated` 决定显示原图还是标注图。

### 从入口到结果：图片翻译窗口执行
1. `ImageTranslateWindowViewModel.ExecuteAsync(bitmap)` 获取图片翻译专用 OCR 服务。候选 OCR 必须声明同时支持 `ImageTranslation` 与 `BoundingBox`，普通旧 OCR 插件不会进入图片翻译服务列表。
2. OCR 请求传入真实图片尺寸，OCR 返回后先统一坐标单位；如果服务商返回 `Regions -> Paragraphs -> Lines`，会保留结构化布局并投影出兼容的 `OcrContents`。
3. OCR 后执行 `ApplyLayoutAnalysis()`：按当前版面分析模式生成 `OcrLayoutBlock`，再投影回段落级 `OcrResult.OcrContents`。
   - `Auto`：默认策略。有服务商结构化 `Regions` 时优先使用 Provider 段落；缺失时回退当前 Smart 智能分段。
   - `Provider`：只使用服务商结构化段落；没有结构化布局时退化为 `NoMerge`，不再自行猜段落。
   - `Smart`：本地智能分段，先按坐标恢复视觉行，再划分区域/列，最后只在同一区域内合并段落，避免跨栏链式吞并；短 UI 文本、表格/按钮类文本会更保守地保持边界，英文断词续行会合并。
   - `NoMerge`：保留 OCR 原始块。
   - 无坐标 OCR：跳过版面分析，保留 OCR 返回文本。
4. 获取 `TranslateService.ImageTranslateService`（必须是 `ITranslatePlugin`，词典服务不支持）。
5. 并发翻译每个 `OcrLayoutBlock.Text`；翻译粒度是段落/布局块，渲染时仍保留原始行框列表。
6. 生成两类图：
   - `_annotatedImage`：合并后边框图
   - `_resultImage`：在原图覆盖译文；擦除原文时优先使用布局块内的多行行框，减少一个大外接矩形覆盖过多背景
7. `Settings.IsImTranShowingAnnotated` 控制最终显示哪种图。

### 图片翻译专用服务绑定
- OCR 专用绑定：`OcrService.ImageTranslateOcrService`，由 `OnSelectedOcrEngineChanged` 写入 `ServiceSettings.ImageTranslateOcrSvcID`。
- OCR 服务筛选：`OcrService.GetImageTranslateOcrServices()` 仅返回实现 `IOcrCapabilityProvider` 且声明 `ImageTranslation + BoundingBox` 的服务；`GetImageTranslateOcrServiceOrDefault()` 也只会返回合格服务。
- 翻译专用绑定：`TranslateService.ImageTranslateService`，由 `OnSelectedTranslateEngineChanged` 写入 `ServiceSettings.ImageTranslateSvcID`。

### 托盘入口
- 主窗口托盘菜单可通过 `Settings.ShowImageTranslateItemInNotifyIconMenu` 控制是否显示“图片翻译”入口。
- 托盘右键触发截图类命令后，需要注意上下文菜单残留；截图入口应在执行前确保菜单状态已收敛。

## 错误处理与通知策略

### 服务未配置（阻断性错误）
当 OCR / TTS 等核心服务未配置或全部禁用时，使用 `Helper.PromptConfigureService` 弹出 MessageBox（OK/Cancel）。弹窗底层统一走 `AppMessageBox`，活动窗口优先、没有活动窗口时通过主屏中心的临时透明 owner 显示：
- 用户点击 **确定** → 自动打开设置窗口并定位到对应配置页。
- 用户点击 **取消** → 仅关闭弹窗，不跳转。

具体映射：
| 功能 | 未配置服务 | 跳转页面 | 涉及 ViewModel |
|---|---|---|---|
| 截图翻译 | OCR | `OcrPage` | `MainWindowViewModel` |
| 图片翻译窗口 | 图片翻译专用 OCR | `OcrPage` | `ImageTranslateWindowViewModel` |
| OCR 窗口 | OCR | `OcrPage` | `OcrWindowViewModel` |
| 朗读（主窗口/OCR窗口/图片翻译窗口） | TTS | `TtsPage` | `MainWindowViewModel` / `OcrWindowViewModel` / `ImageTranslateWindowViewModel` |

### 运行时失败
OCR / 图片翻译执行过程中抛出异常或返回失败结果时，使用当前窗口内的 **Snackbar** 提示：
- `MainWindowViewModel.ScreenshotTranslateHandlerAsync` catch：先 `Show()` 主窗口，再 `_snackbar.ShowError`。
- `MainWindowViewModel.SilentOcrHandlerAsync` catch：先 `Show()` 主窗口，再 `_snackbar.ShowError`（静默场景下没有可见窗口，必须先让窗口出现）。
- `OcrWindowViewModel.ExecuteAsync` catch：`_snackbar.ShowError`。
- `ImageTranslateWindowViewModel.ExecuteAsync` catch：`_snackbar.ShowError`。
- OCR 识别成功但结果为空：`OcrWindowViewModel` / `ImageTranslateWindowViewModel` 使用 `_snackbar.ShowWarning`。

### 其他 Snackbar 提示
- 图片翻译服务未配置（`ImageTranslateService` 不可用）：`_snackbar.ShowWarning("NoTranslateService")`。
- 图片翻译语言检测失败：`_snackbar.ShowWarning("LanguageDetectionFailed")`。

## 关键数据结构/配置
- `OcrResult` / `OcrRegion` / `OcrParagraph` / `OcrContent` / `BoxPoint`：OCR 原始与结构化文本块。
  - `OcrResult.OcrContents` 继续作为旧插件和旧调用链的扁平结果。
  - `OcrResult.Regions` 可承载服务商结构化布局，层级为 `Region -> Paragraph -> Line`。
  - `OcrContent.CoordinateUnit` 支持 `Pixel` 与 `Normalized`；宿主在 OCR 返回后统一归一到图片像素坐标。
- `OcrLayoutBlock`：图片翻译内部布局块，包含段落文本、段落外接框、原始行框列表、布局来源和置信度；不对插件 SDK 暴露。
- `IOcrCapabilityProvider` / `OcrCapabilities`：OCR 插件可选能力声明。未实现能力声明的旧插件仍可用于普通 OCR，但不会被图片翻译 OCR 选择器选中。
- 版面分析参数（`Settings`）：
  - `LayoutAnalysisMode`：默认 `Auto`；可选 `Auto`、`Provider`、`Smart`、`NoMerge`。旧 `"smart"` 仍读取为 `Smart`，旧未知模式归一为 `Auto`，`NoMerge` 保持不变。
- 图像展示设置：
  - `IsOcrShowingAnnotated`
  - `IsImTranShowingAnnotated`
  - `IsImTranShowingTextControl`
  - `ImageQuality`
  - `ShowImageTranslateItemInNotifyIconMenu`
- OCR 语言设置：`OcrLanguage`
- 截图翻译焦点设置：`FocusInputAfterScreenshotTranslate`
- 取词后处理设置：`TextSeparatorHandleType`、`TextSeparatorHandleScopes`

## 关键文件
- `STranslate/ViewModels/MainWindowViewModel.cs`
- `STranslate/ViewModels/OcrWindowViewModel.cs`
- `STranslate/ViewModels/ImageTranslateWindowViewModel.cs`
- `STranslate/Core/Screenshot.cs`
- `STranslate/Services/OcrService.cs`
- `STranslate/Services/TranslateService.cs`
- `STranslate.Plugin/IOcrPlugin.cs`

## 常见改动任务
- 调整版面合并效果：优先改 `OcrLayoutAnalyzer`，不要只改渲染层。
- 接入服务商结构化 OCR：优先填充 `OcrResult.Regions`，并声明 `StructuredLayout`；仍可同时填充 `OcrContents` 作为兼容扁平结果。
- 新增图片翻译后处理：应在翻译回写 `OcrLayoutBlock.Text` 后、`GenerateTranslatedImage` 前插入。
- 截图行为改造：在 `Screenshot.GetScreenshotAsync()` 处理窗口折叠与等待时机。
- OCR 服务优先级策略调整：修改 `OcrService.GetImageTranslateOcrServiceOrDefault()` 与对应 VM 的选中逻辑。
- OCR 结果进入翻译或剪贴板前的文本清洗：优先复用 `MainWindowViewModel.HandleCapturedText()`，避免截图翻译和静默 OCR 行为分叉。
