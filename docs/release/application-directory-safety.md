# 程序文件保护

程序位置以当前进程的 `AppContext.BaseDirectory` 为准。路径先转成规范化绝对路径，再按目录边界判断；例如 `Sieve-backup` 不属于 `Sieve`。保护的目的是防止程序放进扫描范围后，其自身文件被当作待整理内容误删或禁用。

重复、Package 和 Script 扫描共用文件发现入口，只跳过以下程序内容，不跳过整个程序目录：

- 程序顶层的 `Sims4ModSieve.exe`、`createdump.exe`、DLL、PDB、`*.runtimeconfig.json`、`*.deps.json`、`BUILD-INFO.txt`、`SHA256SUMS.txt`、`LICENSE`、`README.md` 和 `THIRD-PARTY-NOTICES.md`。
- 程序顶层由 Inno Setup 生成的 `uninsNNN.exe`、`uninsNNN.dat`、`uninsNNN.msg`（`NNN` 为三位数字）。
- 程序顶层的 `Assets/`、`licenses/` 和语言资源目录（`cs`、`de`、`en`、`en-US`、`es`、`fr`、`it`、`ja`、`ko`、`pl`、`pt-BR`、`ru`、`tr`、`zh-Hans`、`zh-Hant`）及其内容。

程序旁边以及其他普通子目录中的 `.package` 和 `.ts4script` 正常参与扫描。主动排除程序内容不会产生读取失败记录；来源可读取但没有 Mod 时，两种检测显示「这个文件夹里没有找到 mod 文件」。来源或文件确实读取失败时保留具体原因。

删除预检及提交前复核拒绝上面的程序内容；撤回和历史恢复拒绝恢复到这些程序文件路径。禁用、启用及已禁用文件的回收站恢复同样核对；程序文件被加上 `.off` 后仍受保护。同目录的用户 Mod 按既有身份、时间及覆盖检查规则操作。拒绝时保留文件和未完成恢复记录，并向本机 `application-error.log` 追加拒绝路径，查看或复制诊断时沿用路径脱敏。

已设置或自动发现的 Mods / Tray，或任一已勾选扫描来源与程序目录相同时，窗口顶部提示「建议把 Sims4ModSieve 放进单独的文件夹，避免与 Mod 混放」。提示随设置和来源启停更新，不阻止扫描。程序在独立子文件夹中时，不因父目录被扫描而提示混放。历史便携版 ZIP 内保留一层与包名一致的文件夹；安装器的路径规则见 [安装与更新方案](安装与更新方案.md#首次安装)。
