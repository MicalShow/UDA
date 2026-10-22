# 部署：局域网在线版驱动服务 / Deploy: LAN Online Driver Service

5.3.0 前置要求：先在现有服务端用独立数据库迁移工具完成 MySQL 迁移，再运行新版安装器。文件模式或数据库校验失败将拒绝升级且不停止原服务。不要手工删除 packages；安装器在验证 ZIP 后清理仓库散装副本。原始外部导入源保留。全新部署先用 5.2.12 建立仓库并迁移数据库，再安装本版本。

第三阶段局域网在线版由三部分组成：离线驱动仓库（服务端索引源）、自托管驱动服务（`LanDriverService.exe`，部署于 Windows Server）、客户端（`UniversalDriverAssistant.exe --lan`）。全部基于 .NET Framework，无第三方依赖。

## 1. 准备离线驱动仓库

服务端需要一个离线驱动仓库作为索引源。用 `OfflineRepoGenerator.exe`（位于 `bin\Lan\` 或 `bin\Offline\`）从已收集的驱动包目录生成：

```powershell
.\OfflineRepoGenerator.exe --repo \\fileserver\DriverRepo `
  --source \\fileserver\Drivers\ModelA --source \\fileserver\Drivers\Common `
  --os amd64 --osver 10 --create
.\OfflineRepoGenerator.exe --repo \\fileserver\DriverRepo --verify
```

仓库包含驱动包、`repo.manifest.json`（信任锚）和 `repo.udadb`（索引）。`--update` 增量更新，`--verify` 完整性校验。驱动收集是另一阶段的工作，本阶段不下载或捆绑任何第三方驱动。

> 仓库目录需要对运行 `LanDriverService` 的服务账户可读；导入（import）用的源目录需单独配置 `--import-allow` 白名单。

## 2. 部署服务端（Windows Server）

推荐运行 `Build.ps1` 后分发 `release-v5.3.0\Packages\UDA-Server-Setup-v5.3.0.exe`。在 Windows Server 上双击运行，可直接选择包含 INF 的普通驱动目录，也可选择已有索引仓库；自动仓库位于驱动源同一磁盘的同级目录，建库进度会实时显示。设置端口、客户端访问地址与管理员账号密码，再点击“安装并启动”，安装程序会自动建库、请求管理员权限、复制服务文件、生成机器 Token/管理员凭据、注册并启动服务、开放防火墙，成功后直接打开管理后台，不需要 PowerShell。升级已有服务时，先在停止旧服务前验证 MySQL 迁移和权限；随后并行核验 ZIP、迁入其余业务数据并清理仓库冗余副本，保留协议 2 客户端接口；每轮开始时检测逻辑处理器数，并按检测到的数量并行处理，128 线程主机可使用 128 个工作线程。独立预打包命令可通过 `--package-workers N` 调整。

`bin\Packages\UDA-Server-Setup.zip` 和下述 PowerShell 命令仅作为静默部署、排障及高级运维备用。安装脚本默认使用 `--bind +` 接受局域网连接；如只允许本机访问可传 `-BindAddress localhost`。

将 `bin\Lan\` 整个目录复制到服务器（例如 `C:\Program Files\UdaLanService\`）。

### 2.1 Dry-run（推荐先验证，不注册服务、不开防火墙）

```powershell
.\Install-LanService.ps1 -Port 8731 -RepoDir "\\fileserver\DriverRepo" -Token "<long-random-token>"
```

脚本会打印将执行的 `sc.exe` / `netsh` 命令，不真正执行。

### 2.2 真正部署

```powershell
.\Install-LanService.ps1 -Port 8731 -RepoDir "\\fileserver\DriverRepo" -Token "<long-random-token>" -Apply
```

脚本执行：`sc.exe create UdaLanDriverService binPath= "...\LanDriverService.exe --service --port 8731 --repo ... --token ..." start= auto`、`netsh advfirewall firewall add rule ... localport=8731`、`sc.exe start UdaLanDriverService`。

`LanDriverService.exe --service` 以 `System.ServiceProcess.ServiceBase` 运行，`OnStart` 启动 `HttpListener`，`OnStop` 停止。服务以 LocalSystem 运行时，仓库 UNC 路径需对计算机账户或专用域账户可读。

### 2.3 控制台调试模式

不注册服务，前台运行查看日志：

```powershell
.\LanDriverService.exe --port 8731 --repo "\\fileserver\DriverRepo" --token "<token>" --bind localhost
```

健康检查：`http://localhost:8731/api/health`。

### 2.4 卸载

```powershell
.\Install-LanService.ps1 -Uninstall -Apply
```

### 2.5 防火墙与绑定

默认 `--bind localhost`（仅本机，无需 netsh，适合调试）。生产部署到其他主机时，`Install-LanService.ps1 -Apply` 会用 `netsh` 开放入站 TCP 规则；`HttpListener` 前缀为 `http://localhost:PORT/`，仅监听本机。若需监听所有网卡，请改用 `--bind +` 并预先用 `netsh http add urlacl` 授权（需要管理员），并评估仅限内网/VPN 的访问范围。

## 3. Token 鉴权（可选但强烈建议）

服务端配置 `--token` 后，`/api/report`、`/api/download`、`/api/admin/import` 均要求 `X-UDA-Token` 头匹配（常量时间比较，不写入日志）。`/api/health` 不需要 token，便于探活。客户端用 `--token` 传入同一 token。token 应为足够长的随机串，仅在内网/VPN 内分发。

## 4. 部署客户端

也可登录 `http://server:8731/admin`，在“客户端分发”中填写客户端可访问的服务器地址并下载由服务端生成的 `UDA-Client.zip`。压缩包已写入服务器地址和机器 Token；用户仍可在客户端“服务器”页面解锁、修改并保存地址。

将 `bin\Lan\`（或仅 `UniversalDriverAssistant.exe` 与 `DriverConfig.json`）分发到目标机。客户端需要一个本地 NIC bootstrap 仓库（含常见网卡驱动的小型离线仓库），以便在缺网卡驱动时先联网：

```powershell
.\UniversalDriverAssistant.exe --lan http://server:8731 --token "<token>" --bootstrap D:\NicBootstrapRepo --deployment
.\UniversalDriverAssistant.exe --lan http://server:8731 --whatif           # 仅模拟
```

客户端流程：检测缺驱动网卡 → 用 bootstrap 仓库离线安装网卡驱动 → 退避重试探活服务端 → 上报硬件清单 → 下载匹配包到本地暂存目录 → 校验 SHA-256 与签名 → pnputil 安装。同内容哈希本次运行只装一次。

客户端主程序 manifest 为 `requireAdministrator`（pnputil 安装需要管理员）。

## 5. 双节点 HTTPS 集群与真实链路验收

两台主机先分别部署同一版本服务端，并使用同一测试仓库快照、同一个客户端 Token；仓库清单哈希必须一致。`LanDriverService` 当前使用 HTTP `HttpListener`，不直接终止 TLS。每台主机需在服务前放置 TLS 反向代理，使用节点间和客户端均可访问、且受客户端信任的 HTTPS 证书；代理应转发 `/api/` 和 `/admin`、保留 `X-UDA-*` 请求头，并对 ZIP 响应启用流式转发/关闭响应缓冲。服务端以 `--advertise https://node-a.example`（另一台用自己的 URL）公布代理地址；集群节点地址必须是 HTTPS 根路径，不能使用 URL 子路径。代理再转发到本机 HTTP 服务端口；防火墙仅允许受信内网访问服务端口。随后在管理后台“集群”页互相添加对方的 HTTPS 代理地址和共用 Token。待两端节点状态都显示“可用”后，在客户端测试机运行：

```powershell
.\Verify-PhysicalClusterClient.ps1 -RouterUrl "https://node-a.example:8731" `
  -HardwareId "PCI\VEN_XXXX&DEV_YYYY" -Clients 40
```

把硬件 ID 换成测试仓库中确实有驱动的 ID。脚本先并发度为 1 地发送 40 个唯一设备上报，验证候选是否分布到两个节点，再从返回的节点地址同时下载并解压驱动 ZIP；输出节点分配数、客户端吞吐、首字节 P95、解包 P95 和总吞吐。它不会安装驱动。客户端 Token 以安全输入框读取，临时 ZIP 与解包目录在完成后清理。请仅对隔离的测试节点和测试仓库运行。

如只需测单机 1Gbps 下载而不验证分流，可使用 `Verify-PhysicalNetworkClient.ps1`。两项实测都应在有线 1Gbps 网卡的第二台主机上运行，报告中保留主机网卡协商速率、服务端地址、测试时间、客户端数及输出指标；本机回环测试不能代替物理链路验收。

## 6. 安全要点

- 服务端**不接受任意客户端路径**：`/api/download` 的 `path` 必须解析到仓库根下，`/api/admin/import` 的 `src` 必须落在 `--import-allow` 白名单下，`..`/绝对/UNC 越界一律 400。
- 服务端**不执行命令、不安装驱动**，只服务索引过的文件。
- 客户端传输协议为版本 2（`X-UDA-Protocol: 2`）；请求大小上限默认 8 MB，具备超时与 `RequestId` 幂等。管理控制台的既有 API 保持协议 1，服务端直接处理的仓库变更路由使用协议 2。
- token 常量时间比较且不入日志；日志中硬件 ID/机型等仅信息级记录，不含 token。
- 下载的包在客户端校验 SHA-256（与 manifest 一致）和签名后才安装；哈希不匹配则拒绝。

## 7. 排错

- 服务起不来：查看 `%ProgramData%\UniversalDriverAssistant\Logs` 与 `sc.exe query UdaLanDriverService`；`--service` 模式下控制台不可见，可先用控制台模式（去掉 `--service`）调试。
- 端口被占：换 `-Port`，或检查 `netstat -ano | findstr :8731`。
- 客户端连不上：先 `curl http://server:8731/api/health`；确认防火墙规则、token、绑定地址。
- 匹配为空：用 `OfflineRepoGenerator.exe --scan --repo ...` 在仓库本地核对 INF 与硬件 ID；确认 OS/架构装饰串匹配。
- 测试：`.\Run-LanTests.ps1` 使用本机回环临时端口与合成 INF 端到端验证协议与安全保证，不注册服务、不开防火墙、不安装真实驱动。

