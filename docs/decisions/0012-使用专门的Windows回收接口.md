# 0012 使用专门的 Windows 回收接口

状态：已采纳
日期：2026-11-21

## 决定

Windows 适配器逐项调用 `ITransferSource::RecycleItem`，把返回的回收站条目标识写入历史。适配器先核对用户禁止回收的策略、文件所在卷的禁用设置和已配置的容量。恢复继续通过 Windows Shell 移回原路径。专用接口将回收和永久移除定义为两个独立操作。 [Microsoft 回收接口](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-itransfersource-recycleitem)

## 比过的方案

在 `IFileOperation::PreDeleteItem` 回调里只检查 `TSF_DELETE_RECYCLE_IF_POSSIBLE`，仍无法根据该标志确认回收。回调拒绝还会取消同批等待的操作。改用专用接口后，每个失败可以单独报告，下一项照常尝试。 [删除前回调](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperationprogresssink-predeleteitem) · [回收标志](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/ne-shobjidl_core-_transfer_source_flags)

## 代价

每项回收多读取卷设置和文件大小。永久删除可能让玩家失去唯一一份 Mod，严重度 5、发生度 2、难挽回 4，乘积 40。
