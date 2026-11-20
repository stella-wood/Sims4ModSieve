# 设计令牌与组件

界面上用到的颜色、字号、间距、圆角，都在这里有名字。写 XAML 时**只用这里的名字**，不直接写色值和随手的数字。

- 这份管「用什么值」。页面怎么布局、为什么这样设计，见 [UI 设计风格规范](docs/design/UI设计风格规范-v1.md)。
- 令牌名就是 XAML 资源的 `x:Key`。
- 标了「**待迁移**」的令牌，代码里还没有对应资源，页面里仍是写死的值；迁移时把写死的值换成令牌，见文末「迁移」。

## 颜色

### 中性色

| 令牌 | 值 | 用在 | 现在代码里 |
|---|---|---|---|
| `WindowBrush` | `#F6F5F2` | 页面底色 | 已有 |
| `SurfaceBrush` | `#FFFFFF` | 卡片表面 | 已有 |
| `SurfaceSunkenBrush` | `#F3F1ED` | 展开的文件区、卡片里凹下去的区域 | 待迁移（另有 `#F2F1EF` `#F0EEEB` `#F0F0F0` 近似值并入） |
| `SurfaceMutedBrush` | `#EAE7E2` | 当前 Mods 区等成块的浅灰底 | 待迁移（`#ECEAE7` `#E8E4DE` 并入） |
| `ControlBrush` | `#E4E0DB` | 次按钮底 | 在 `SecondaryButtonStyle` 里写死 |
| `ControlHoverBrush` | `#DAD5CF` | 次按钮悬停 | 同上（`#DED8D0` 并入） |
| `GhostHoverBrush` | `#E2DED8` | Ghost 按钮悬停底 | 在 `GhostButtonStyle` 里写死 |
| `BorderBrush` | `#D5D0CA` | 通用细边框 | 已有 |
| `DividerBrush` | `#D8D3CC` | 分组虚线（1 DIP，透明度 0.78，虚线 `2,4`） | 待迁移 |
| `BorderStrongBrush` | `#C8C1B8` | 需要更明显的边框 | 待迁移 |

### 文字色

| 令牌 | 值 | 用在 | 现在代码里 |
|---|---|---|---|
| `InkBrush` | `#2B2927` | 主文字 | 已有 |
| `MutedBrush` | `#706C67` | 次要文字、路径 | 已有 |
| `SubtleBrush` | `#948C84` | 更弱的说明、未分析状态 | 待迁移（`#A39E98` `#9C9C9C` 并入） |

### 强调色

| 令牌 | 值 | 用在 | 现在代码里 |
|---|---|---|---|
| `AccentBrush` | `#6F5A91` | 主要强调紫、主按钮 | 已有 |
| `AccentHoverBrush` | `#624D89` | 主按钮悬停 | 在 `PrimaryButtonStyle` 里写死 |
| `HoverTextBrush` | `#603B8C` | 文字悬停变色（导航、筛选标签等），新增同类效果都用它 | 已有 |
| `AccentSoftBrush` | `#E6E0ED` | 浅紫底：标签、首页图标底 | 已有（`#EEE9F3` 待确认是否并入） |
| `AccentLightBrush` | `#A892C5` | 「选择 / 更改」这类轻量路径操作。只用于这类辅助操作，不当正文、按钮或错误色 | 待迁移（12 处） |

### 状态色

评级的颜色全程序统一，界面和报告共用。

| 令牌 | 值 | 评级 | 现在代码里 |
|---|---|---|---|
| `AbnormalBrush` | `#D33C2E` | 异常 | `ConflictPage.xaml` 局部 `ConflictNeedReviewBrush` |
| `RiskBrush` | `#D99316` | 有隐患 | `ConflictPage.xaml` 局部 `ConflictCanOrganizeBrush` |
| `IncompleteBrush` | 同 `SubtleBrush` | 未分析 | `ConflictPage.xaml` 局部 `ConflictIncompleteBrush` |
| `NoEffectBrush` | 待定 | 不影响 | 界面上还没有绿色结果 |
| `WarningTextBrush` | `#9A6A32` | 「不可访问」这类提示文字 | 待迁移（9 处） |

`#F8EFED`、`#F8F3E8`、`#88774E`、`#FBF8FC` 各只出现一两次，迁移时逐个看是并入上面的令牌还是新增，新增要先写进本表。

## 字号

单位是 WPF DIP。只用下面这几档：

| 令牌 | 值 | 用在 |
|---|---|---|
| `FontSizeTiny` | 10 | 技术值、小标签、建议保留 |
| `FontSizeSmall` | 11 | 路径、组编号、统计数字 |
| `FontSizeBody` | 12 | 文件名、「选择 / 更改」、当前 Mods 标题 |
| `FontSizeBodyLarge` | 14 | 对话框正文、首页说明 |
| `FontSizeGroupTitle` | 15 | 组标题、导航 |
| `FontSizeSubtitle` | 16 | 首页副标题 |
| `FontSizeSectionTitle` | 18 | 工作区标题 |
| `FontSizeCardTitle` | 22 | 首页功能卡标题 |
| `FontSizeLarge` | 28 | 设置页、感谢名单的页面大标题 |
| `FontSizeDisplay` | 35 | 首页展示标题 |

现在代码里还零星用了 13、17、19、23、26，迁移时就近并到上面的档位。

设置页右侧分类标题共用 `SettingsTitleTextStyle`，字号使用 `FontSizeLarge`，使用随程序提供的 Noto Sans SC 600 字重标题字形。

字体和字重见 [UI 设计风格规范第 5 节](docs/design/UI设计风格规范-v1.md#5-字体与信息层级)，以已有的文字样式为准：`DisplayTextStyle`、`SectionTitleTextStyle`、`BodyTextStyle`、`DataTextStyle`、`FileNameTextStyle`、`BrandTextStyle`。

## 间距

一把尺子，以 4 为步长，另留 2 和 6 给特别紧凑的地方：

| 令牌 | 值 | 典型用法 |
|---|---|---|
| `Space2XS` | 2 | 文字和紧贴它的小标记 |
| `SpaceXS` | 4 | 行与行的上下间隙 |
| `Space6` | 6 | 小按钮、标签的内边距 |
| `SpaceS` | 8 | 图标和文字、按钮之间 |
| `SpaceM` | 12 | 卡片内的段落之间 |
| `SpaceL` | 16 | 卡片内边距 |
| `SpaceXL` | 20 | 区块之间 |
| `Space2XL` | 24 | 页面里大区块之间 |
| `Space3XL` | 28 | 页面左右边距、缩进一级 |

现在代码里的 Margin / Padding 用了二十多种数字（用得最多的是 8、4、10、12、20、6、18、28、26）。**现有页面的间距大多是 用户逐个看过、调过的，不能为了凑尺子自动改。** 这把尺子只约束新写的界面；老页面的间距要改，先列出位置和建议值给 用户过一遍，她点头的才改，改的 PR 附前后截图。

警告弹窗局部资源 `WarningDialogBodyMinWidth` = 300 DIP，用于正文区的最小宽度。

## 圆角

| 令牌 | 值 | 用在 |
|---|---|---|
| `RadiusS` | 2 | 小标签、细条 |
| `RadiusM` | 4 | 按钮、输入框，默认用这个 |
| `RadiusL` | 6 | 卡片、对话框 |
| `RadiusPill` | 24 | 胶囊形按钮、圆点外框 |

代码里的 3 并入 4。

## 扫描并发说明图标

设置页局部资源：`ScanConcurrencyInfoSize` = 14（矢量图标），`ScanConcurrencyInfoTargetSize` = 24（键盘和鼠标宿主），`ScanConcurrencyInfoGap` = 左侧 2。颜色使用 `MutedBrush`；图标与标题垂直居中，说明放入可通过键盘焦点显示的 Tooltip。

## 组件

现有的样式就是我们的组件库，都在 `App.xaml`。新页面先找这里有没有，没有再加，加了要写进这张表。

| 组件 | 样式 | 用在 |
|---|---|---|
| 卡片 | `CardStyle` | 首页功能卡、结果组 |
| 主按钮 | `PrimaryButtonStyle` | 每个页面最主要的那一个操作 |
| 次按钮 | `SecondaryButtonStyle` | 其余操作 |
| Ghost 按钮 | `GhostButtonStyle` | 不抢眼的操作 |
| 顶部返回 / 导航 | `HeaderBackButtonStyle`、`HeaderNavButtonStyle`、`ActiveHeaderNavButtonStyle` | 顶栏 |
| 纯图标按钮 | `BareIconButtonStyle` | 展开收起、复制等小图标 |
| 无框折叠 | `PlainExpanderStyle` | 结果组展开 |
| 滚动条 | `ResultVerticalScrollBarStyle`、`PageVerticalScrollBarStyle` | 结果区、整页 |

还没有、但页面里已经各自写了一份的，迁移时补成样式：

- 状态圆点 + 标签（冲突页筛选、组标题）
- 轻量路径操作（「选择 / 更改」）
- 文件操作确认窗（`MultiFileConfirmationWindow` 的按钮区）

## 迁移

把写死的值换成令牌，**不改布局、不改交互**。建议顺序：

1. 新建 `src/Sims4ModSieve.Desktop/Themes/Tokens.xaml`（颜色、字号、间距、圆角）和 `Themes/Controls.xaml`（组件样式），`App.xaml` 合并这两个文件，原有 `x:Key` 不变。
2. 颜色：按上表替换，值不变的直接换；并入近似值的会有细微色差，附截图。
3. 字号、圆角：按档位替换。
4. 间距：不在迁移范围里。见「间距」一节，要改先给用户过一遍。

迁移完后，页面 XAML 里不应再出现 `#` 开头的色值。
