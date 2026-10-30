# SCCM 同次运行闭环安装（v5.3.43）

在完整 Windows 阶段添加运行命令行，以 LocalSystem 执行：

```text
UDA-Client.exe --sccm
```

每次安装后重新采集 Windows 原生及 WMI 设备清单，重新匹配并安装，直到连续复扫确认没有待安装的适用候选。不会为复扫增加重启步骤。驱动被 Windows 拒绝或连续安装仍无法绑定时返回失败并列出未完成设备，不把添加进 Driver Store 当成安装成功。

更新服务端后重新生成客户端，替换 SCCM 软件包并更新分发点；仅更新服务端不会改变已有客户端代码。不要启用“发生错误时继续”。0 表示最终核验通过；1 表示未完成；2 表示执行错误；3010 仅保留驱动安装自身请求重启的含义，程序不会主动重启。

## 运行日志

- 客户端启动后即订阅日志，健康网络下通常每两秒异步上传；扫描、等待、下载、安装期间都上传。
- 包含客户端/服务器版本、运行身份、系统目标、已知 SCCM 步骤上下文、型号/SKU、异常设备全部 H/K、候选及拒绝原因、轮次/依赖变化、重启标志、最终结果和异常堆栈。
- API Key、Token、密码等字段脱敏；诊断分片不增加安装数或使用电脑统计。每个事件有唯一 ID，失败后本地持久化重传，不重复计数。
- 网络不可用时无法即时送达，队列保存在 `%ProgramData%\UniversalDriverAssistant\TelemetryQueue`，完整本地日志在 `%ProgramData%\UniversalDriverAssistant\Logs`。队列上限 100 MiB，队列不足时原始本地日志继续保留。
- SCCM 结构化结果在 `%ProgramData%\UniversalDriverAssistant\Reports`。保留的客户端含连接凭据，排障时不要将该 EXE、defaults 或整个目录公开发送。
