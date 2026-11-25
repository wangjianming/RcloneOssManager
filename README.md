# RcloneOssManager

基于 [rclone](https://rclone.org/) 的 Windows 桌面 OSS / S3 / MinIO 连接管理器。

用图形界面管理 rclone 远程连接，把对象存储桶挂载成本地盘符，并直接浏览、下载、编辑桶权限与 Bucket Policy，无需记忆 rclone 命令行。

## 功能

- **连接管理**：新增、编辑、测试、排序 S3 / MinIO / 阿里云 OSS 等 rclone 远程连接。
- **盘符挂载**：一键把连接挂载为本地盘符，可设置卷标；开机静默恢复上次挂载。
- **桶与文件操作**：浏览桶、目录和对象，下载、上传、拖拽导出到资源管理器。
- **权限与策略**：可视化查看和编辑桶 ACL 与 Bucket Policy，支持私有、公共只读和自定义三档。
- **单文件发行**：构建时把 rclone 引擎压缩内嵌进 EXE，运行时按 SHA-256 校验后释放，不捆绑任何连接配置或认证信息。

## 环境要求

- Windows 10 / 11（64 位）
- .NET Framework 4.x（调用系统自带的 `csc.exe` 编译，无需安装 .NET SDK）
- 可选：`rclone.exe`（构建内嵌引擎版本时需要）

## 构建

```cmd
build.cmd
```

默认输出到 `build\RcloneOssManager.exe`。

内嵌 rclone 的单文件构建，需要把 `rclone.exe` 放在仓库根目录，或显式指定：

```cmd
build.cmd -RcloneExecutable "C:\path\to\rclone.exe"
```

不内嵌引擎：

```cmd
build.cmd -NoEmbeddedRclone
```

## 运行

双击 `RcloneOssManager.exe`。程序把界面设置与缓存保存在 `%LOCALAPPDATA%\RcloneOssManager` 下；连接认证仍由 rclone 配置文件独立管理，不写入 EXE。

仓库还附带两个辅助脚本：

- `open-rclone-oss-manager.vbs`：从脚本所在目录启动管理器。
- `mount-rclone-remotes-hidden.vbs`：开机时按 `rclone-mounts.txt` 静默恢复挂载，日志写入 `%LOCALAPPDATA%\RcloneOssManager`。

## 配置

程序读取 `RcloneOssManager.settings.json`（优先程序目录旁，其次 `%LOCALAPPDATA%\RcloneOssManager\settings.json`）：

```json
{
  "RcloneExecutable": "C:\\path\\to\\rclone.exe",
  "RcloneConfiguration": "C:\\path\\to\\rclone.conf",
  "ServerModtimeRemotes": ["my-minio"]
}
```

- `RcloneExecutable` / `RcloneConfiguration`：自定义 rclone 程序与配置文件路径，留空时使用程序目录或内嵌引擎。
- `ServerModtimeRemotes`：需要追加 `--use-server-modtime` 的连接标识列表，默认为空。某些 S3 兼容服务（如部分 MinIO 部署）在浏览目录时依赖服务器修改时间，可在此按连接名配置。

## 目录结构

```
src/Core      路径、配置、rclone 进程调用、日志等基础能力
src/Files     桶、对象、策略与 ACL 的协议实现
src/Shell     Windows Shell 拖拽与文件管理器集成
src/Transfers 下载与传输
src/UI        WinForms 界面
tests         源码级与界面级验收程序
```

## 许可

本项目使用 [MIT License](LICENSE)。

rclone 本身同样遵循 MIT License，见 <https://github.com/rclone/rclone>。
