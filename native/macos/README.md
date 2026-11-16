# 原生 Liquid Glass 灵动岛

`main.swift` 管理非激活悬浮面板、协议和输入；`LyricRenderer.swift` 只处理文字排版与透明的歌词图像；
`IslandMotion.swift` 提供播放时钟、有限时长布局动画和换行曲线；`IslandInteraction.swift` 管理暂停保留、快捷按钮状态和滚轮手势门控。
辅助进程不加载 Avalonia 或 .NET，最低运行版本为 macOS 26；主应用在 macOS 12–15 继续使用 Avalonia 兼容渲染。

视觉遵循 Apple 的 [Materials](https://developer.apple.com/design/human-interface-guidelines/materials)
与 [Motion](https://developer.apple.com/design/human-interface-guidelines/motion) 指南，并参考 Apple Music 的歌词聚焦表现。
当前参数是针对紧凑浮岛的设计选择，不是 Apple 公开的逐像素规范。

- 只有一个 `.regular` 的 `NSGlassEffectView`，不使用换行背景脉冲、额外材质、黑色遮罩或自绘反光。
- 主歌词使用 26pt 系统 semibold，翻译使用 16pt medium；暂停不缩放字形。
- CoreText 保留字形塑形和字体回退，歌词升起保持/波浪回落与泛光五档参考 Linux 的时序。
  下一句预先排版，逐字高亮使用连续渐变，避免跨字形时突然变亮；减少动态效果同时关闭逐字升降。
- AI 译文显示按字形实际边界居中的 `AI` 徽标，计入长译文滚动范围。
  两侧复制链接操作按当前歌曲和公开链接能力启用，过期歌曲的动作由主程序拒绝。
- 正常换行在 340ms 内完成：旧句在 200ms 内上移、淡出并逐渐失焦，新句从下方进入且在 160ms 内恢复清晰。
  模糊使用公开的 `CIGaussianBlur`，只处理带透明边距的文字图像；第一次使用前预热 Core Image。
- 旧句及中断中的过渡先冻结成文字图像，保留高亮、滚动位置和排版；新句的文字视口一次确定。
  同一帧只采样一次时间和歌词索引，玻璃布局关闭隐式动画，并在 340ms 后精确停止。
- 小于 600ms 的播放锚点误差以有限速率校正，避免播放中倒退；曲目切换、暂停、恢复和明确跳转立即建立新锚点。
- 系统“减少动态效果”关闭文字模糊和位移，使用 120ms 交叉淡入；玻璃和文字颜色遵循系统外观与辅助功能。
- 布局不叠加 NSWindow 隐式动画；屏幕变化后重新约束位置与尺寸，刷新率不超过显示器刷新率及用户设置。
- 延迟单击、快捷按钮和菜单偏移操作绑定触发时的歌曲，换歌后不再操作新歌；触控板一次手势只切歌一次，忽略惯性阶段。
- 主进程使用有界异步队列写入，写入超时、协议不兼容或异常退出均回退到兼容岛；非法辅助进程消息只记录日志。

## 验证

在仓库根目录运行时钟和动画回归测试：

```sh
bash packaging/test-macos-glass-island.sh /tmp/kashishu-glass-tests
```

采集深色、浅色与减少动态效果的原生窗口连续帧，并检查歌词边界、背景亮度与暂停尺寸：

```sh
bash packaging/test-macos-glass-island.sh /tmp/kashishu-glass-tests --visual
```

视觉测试通过单独的 `.app` 启动，每种模式运行约 8 秒，输出逐帧 PNG、JSON、接触表与绘制耗时。
快照和测试场景仅编入 Debug，Release helper 不包含这些入口。

构建应用后，可运行隔离配置下的 v2 协议、新设置转发、菜单入口、异常消息与进程回退测试，
以及真实设置页、AI 连接和窗口状态自检（需要 Command Line Tools 提供的 Python 3）：

```sh
bash packaging/test-macos-integration.sh dist/Kashishu.app /tmp/kashishu-macos-integration
```

测试只使用演示播放，不控制真实播放器。自动化覆盖不能替代真实 Spotify / Music 账号、首次自动化权限弹窗、
多显示器拔插和全屏 Spaces 的人工验证。Intel 可以交叉编译检查，但仍需 Intel 真机验证运行行为。
