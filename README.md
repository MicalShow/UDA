# UDA — 万能驱动助手

Universal Driver Assistant 是面向 Windows 桌面运维、系统部署和 SCCM 任务序列的驱动管理工具，提供局域网服务端、在线客户端与离线工具。

当前源码版本：**5.3.45**。许可证：[MIT](LICENSE)。第三方依赖保留各自的许可，见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 主要功能

- 按硬件 ID、操作系统版本、架构及 Windows 原生驱动排名筛选候选，安装后核对实际绑定。
- 服务端 Web 控制台管理驱动目录、稳定版/最新版策略、设备安装记录和管理员审计。
- 按厂商、机型、系统和架构配置专用驱动整包；未配置适用机型包时回退通用驱动。
- 机型整包按包下载、解压和递归导入，避免逐设备重复导入同一整包。
- 服务端使用 MySQL/MariaDB 保存业务元数据、匹配索引及日志；驱动 ZIP 保留在本地。
- 上传增量追加只处理本次输入，复用未变归档及机型包；保留内容核验。
- SCCM 静默部署、安装结果上报和日志故障补传。

## 构建

在 Windows 上准备 Windows PowerShell 5.1、.NET Framework 4.6.2 或更高版本，以及 Python 3。无需将项目转换为 .NET SDK 工程。

```powershell
python Tools\fetch_mysql_provider.py
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1 -OutputDirectory .\bin
```

构建脚本通过系统 C# 编译器生成 `bin\Lan`、`bin\Offline` 和服务端安装包。依赖恢复会从 NuGet 下载 .NET Framework 兼容的程序集，并保存依赖清单和许可证。

本仓库仅提供源码、文档和构建/验证脚本，不包含第三方驱动载荷、生产配置、数据库或私有仓库提交历史。自行准备的驱动包须具备相应使用和分发权限。

## 部署

1. 新建 MySQL/MariaDB 数据库，按安装/迁移工具配置连接和管理员认证。
2. 构建并运行服务端安装程序，选择驱动仓库目录、网卡地址与端口。
3. 在控制台导入驱动目录或机型整包，并生成带服务端地址的客户端。
4. 目标 Windows 客户端须有基础网络连接；SCCM 中在完整 Windows 阶段运行生成的客户端：

```text
UDA-Client.exe --sccm
```

已有服务升级前，按 [迁移准备说明](MIGRATION-PREFLIGHT.md) 完成数据库迁移与检查。更新含客户端行为变更的版本后，需要重新生成和分发客户端。

## 文档

- [构建与仓库说明](REPOSITORY.md)
- [局域网部署](DEPLOY-LAN.md)
- [数据库迁移](DATABASE-MIGRATION.md)
- [机型专用整包](MACHINE-PACKAGES.md)
- [增量导入](INCREMENTAL-IMPORT.md)
- [客户端扫描性能](SCAN-PERFORMANCE.md)
- [SCCM 任务序列](SCCM-AUTODEPLOY.md)
- [归档核验](ARCHIVE-VERIFICATION.md)
- [服务停止与升级](UPGRADE-SERVICE-STOP.md)

## 验证

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Run-LanTests.ps1 -OutputDirectory .\validation-lan
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Run-OfflineTests.ps1 -OutputDirectory .\validation-offline
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Run-DatabaseMigrationTests.ps1 -OutputDirectory .\validation-migration
```

核心协议测试使用合成驱动和模拟安装执行器，不会向真实硬件安装驱动。真实 SCCM、厂商驱动和网络部署仍应在隔离测试环境验收。其他验证脚本可能启动临时数据库、产生文件或需要外部工具，请先阅读对应脚本。

## 5.3.45 更新

完善原生硬件清单、扩展驱动与 Models 适用性匹配；SCCM 同次运行持续复扫安装并最终核验，不增加额外重启流程。增加持续诊断日志、跨机数据库连接恢复、安装错误收集与缺失归档补齐脚本。

LAN/GUI 完整回归在 5.3.43 为 666/172 项断言；5.3.45 安装和数据库测试 67 项通过。实际机型 SCCM 部署及音频功能仍需现场验收。
