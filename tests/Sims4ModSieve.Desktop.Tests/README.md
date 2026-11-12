# Desktop.Tests 窗口

普通布局、渲染和控件命中测试通过 `WpfTestHost.ShowOffScreen` 显示窗口：手动定位到 `(-10000, -10000)`，设置 `ShowActivated = false`、`ShowInTaskbar = false`。最大化时将窗口设为透明，避免系统把窗口移回显示器时遮住桌面，恢复普通状态时恢复不透明度；客户区仍可通过 `RenderTargetBitmap` 保存供检查。

## 真实输入与前台窗口例外

下列测试保留屏内窗口。真实鼠标坐标必须落在显示器内；键盘和前台测试需要验证实际焦点或激活状态。共用的 `WpfTestHost.PlaceOnScreen` 保留置顶和激活，并恢复窗口不透明度，不显示任务栏按钮；重复组标题的真实鼠标测试保留原有屏内位置。

| 测试类 | 测试方法 | 原因 |
| --- | --- | --- |
| ConflictInteractionTests | `OpenFilterPopupAllowsNativeWheelScrollingAndOutsideClick`、`ResultScrollbarsSupportNativeThumbDraggingAndTrackPaging` | 原生滚轮、外部点击、滑块拖动和轨道翻页 |
| ConflictMultiSelectTests | `MultiSelectRevealsCheckboxesActionsAndLiveCount`、`GroupCheckboxSelectsAllFilesAndSyncsPartialState`、`MultiSelectContextMenuNamesSelectionActions` | 原生点击、悬停与右键菜单 |
| ConflictPageRenderTests | `FileContextMenuTargetsRowAndCopiesFullPathWithoutIcons` | 原生右键菜单、悬停及点击 |
| ConflictPageRenderTests | `ResourceAnnotationHoverShowsCountsWithoutOpeningFileRelationships`、`CollectionExplanationAndAnnotationsDoNotOpenEmptyTooltips` | 验证真实鼠标悬停是否打开提示 |
| ConflictPageRenderTests | `AllConflictFilterTracksSelectionAndSupportsHoverAndKeyboard` | 真实悬停与键盘空格切换 |
| ConflictRelationshipsDialogTests | `GroupHeadingAndFileTextAreasRouteToTheirActions`、`FileActionsHaveRectangularHitAreasAndTechnicalTableStaysInline` | 验证按钮获得键盘焦点及空格操作 |
| ConflictRelationshipsDialogTests | `RelationshipBadgeNativeHoverShowsDetailsAndAllowsDialogClose` | 原生悬停打开提示、点击关闭弹层 |
| ConflictTechnicalDetailsTests | `ContinuousNativeWheelMovesContentByRequestedDistanceWithoutRebound`、`NativeVerticalThumbDragCancelsWheelEasingWithoutRebound`、`FloatingBarKeepsScrollTransparencyAndTracksHoverAndDrag`、`NativeMouseWheelOverTableTextWhitespaceAndTooltipScrollsResults` | 原生滚轮、悬停与滑块拖动 |
| DuplicateGroupHeaderTests | `HeaderMouseTargetsToggleExpansionWithoutChangingSelection` | 原生点击标题、统计、箭头及留白，同时验证键盘空格 |
| LinkTargetScanTests | `RealJunctionButtonAddsOrEnablesTargetOnlyOnCurrentPage` | 实际按钮焦点和键盘操作 |
| ModDisableViewModelTests | `DisabledSectionButtonsRenderAndInvokeAtMinimumWindowSize` | 原生点击刷新与文件操作按钮 |
| ScopeScrollbarTests | `SourcesScrollbarsKeepNativeScrollingAndSourceActions` | 原生来源列表滚动与点击 |
| StartupSplashScreenTests | `SecondLaunchActivatesExistingWindowAndRestoresMinimizedState` | 先激活辅助窗口，再验证第二实例将主窗口或对话框切回前台 |
| UpdateCheckTests | `HomeIconPopupAndDownloadWork` | 原生点击、悬停，以及更新弹层的位置 |
| UpdateDownloadTests | `CancelButtonAndWindowCloseCancelDownload` | 原生点击更新入口及取消下载 |
| VerticalScrollbarTests | `NativeInputWindowFitsWorkAreaAndKeepsEdgeControlClickable`、`OuterPageScrollbarsSupportNativeInputAndIdleVisibility`、`SettingsInnerScrollbarStaysVisibleAndScrollsWithoutWakingOuter` | 工作区边缘点击、原生滚轮、悬停及拖动 |

`StartupSplashScreenTests.StartupClosesSplashBeforeErrorsAndSkipsItForSecondInstance` 还会运行真实启动子进程，观察启动画面、主窗口和启动失败提示的显示顺序，因此保留子进程界面。

感谢名单的人工录制入口也保留屏内窗口：`CreditsTests.BurstsAccumulateUntilTwentyFiveAndResumeWithoutClearingParticles` 和 `HeartsStayAtFourAndOldestFadesBeforeReplacement` 仅在 `SIMS4_CREDITS_ANIMATION_REVIEW=1` 时显示；`CreditsClicksTriggerEffectsWithoutOverlayBlockingAndLeavingResets` 仅在 `SIMS4_CREDITS_NATIVE_REVIEW=1` 时使用屏内真实鼠标。这些入口默认关闭。

## 屏外窗口中的键盘焦点例外

以下测试仍调用控件的 `Focus()`，但不显式激活或移动窗口到屏内。原有焦点断言和键盘事件保留，用于验证键盘可用性、按下状态或关闭弹层后恢复焦点。

| 测试类 | 测试方法 | 验证内容 |
| --- | --- | --- |
| ConflictFileActionTests | `FileChangesPreserveVisibleRowPositionAfterLayoutAndUndo` | 操作前定位文件按钮 |
| ConflictInteractionTests | `GroupLinksExpandTargetAndKeepOneHeadingAbove` | 跳转链接的键盘焦点 |
| ConflictPageRenderTests | `DuplicateExplanationLinkStaysInlineAndNavigates`、`ResourceCardsAndFilterPopupSupportKeyboardAndNarrowWindows` | 链接、资源卡和筛选按钮的焦点与空格操作 |
| ConflictRelationshipsDialogTests | `DialogShowsDirectRelationsCopiesAndRestoresFocusAfterEscape` | Escape 关闭关系弹层后恢复按钮焦点 |
| CreditsTests | `ConsecutiveBurstsIncreaseLaunchSpeedUpToThirdAndResetAfterRest`、`PetalsStayUprightWhileRibbonsKeepFlipping`、`ConfettiButtonCompressesReboundsAndSettlesWithoutChangingHitArea`、`WholePageClicksCreateHeartsExceptFloatingConfettiButton`、`BurstFollowsReferenceMotionWithinPageAndFallsNaturally`、`CreditsClicksTriggerEffectsWithoutOverlayBlockingAndLeavingResets` | 键盘触发感谢名单效果与按钮按下状态 |
| DuplicateGroupHeaderTests | `HeadersShowKeepFileNameAndCountAndSupportSelectionAndExpansion` | 组选择、展开与收起的键盘空格操作 |
| SettingsPageTests | `AboutDocumentsToggleSwitchAndScrollWithKeyboardAndClick`、`SettingsControlsSupportKeyboardAndRectangularHitTargets` | 设置导航、文档开合与控件键盘操作 |
