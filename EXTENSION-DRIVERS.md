# 配套扩展驱动（5.3.32）

## 通用安装行为

通用仓库现在独立匹配 Extension INF，不要求为每个电脑型号配置机型整包。基础驱动与扩展不再竞争同一候选：基础驱动保留正常版本/排名策略；扩展按当前设备硬件 ID、系统与架构检查适用性，再按 ExtensionId 分组，以驱动日期、版本选择同一组中的一个版本。其他 ExtensionId 的适用组件分别保留。

当前自动扩展通道要求精确硬件 ID 命中，并且命中 ID 含 SUBSYS，或来源明确属于当前电脑品牌；排除明确标注为其他电脑品牌的来源，不使用仅兼容 ID 命中的扩展。无有效 ExtensionId、不适用系统/架构以及已确认不受信任的扩展不会下发。需要 CHID 才能进一步区分的通用扩展不属于本次自动匹配范围。

客户端先下载并校验候选，再先安装基础驱动，后安装适用扩展。扩展使用标准 `pnputil /add-driver <INF> /install`，不将扩展强制绑定为父设备的基础驱动。安装前再次检查真实 INF 的哈希、类别、ExtensionId、系统及硬件 ID；仍执行签名验证和管理员检查。扩展安装时要求基础设备已有正常驱动。扩展创建的新软件组件由后续闭环硬件采集与常规候选匹配继续安装。

扩展的版本与父设备驱动版本没有比较关系。例如父设备基础驱动为 100.x，缺失的功放扩展为 13.x，也应显示“安装配套扩展”。

## 安装状态与重复扫描

通过 Windows SDK 定义的 `DEVPKEY_Device_ExtendedConfigurationIds`（`540b947e-8b40-45bc-a8a2-6a0b894cbda2`, PID 15）读取目标设备实际应用的扩展 INF，再检查该 INF 的 ExtensionId、硬件/系统适用性、哈希或日期/版本。同一或更高优先级扩展已经应用后，不再重复推荐。仅在 Driver Store 中存在文件不视为已应用。

安装命令成功但设备尚未报告应用扩展时，记为未核验，不记为安装完成；如果系统要求重启，重启后重新扫描复核。驱动组件安装状态不能代替扬声器实际播放验收。

## 升级与仓库要求

更新服务端后，必须重新生成并分发完整客户端，SCCM 客户端包也需同步替换。新客户端声明扩展通道能力；未声明能力的旧客户端保持既有候选协议，避免旧客户端把扩展作为基础驱动安装。

现有数据库、索引和归档可以继续使用。旧索引缺少 ExtensionId 时，只读取命中 INF 的既有归档并校验哈希，按内容哈希缓存结果，不触发整库重建。

通用仓库必须已经导入扩展 INF、其文件及软件组件 INF。只有基础 INF 的仓库无法自动生成缺失的 OEM 配套内容。可以把完整官方驱动目录或正常电脑导出的完整驱动集合通过普通“驱动导入”入库一次，保留全部子目录，无需逐机型建立整包对应关系。服务端不会在线下载未授权的厂商驱动，也不会执行第三方安装脚本。

## HP 830 G9 案例

正常安装官方音频包和完整驱动导入后声音恢复，说明应重点修复 UDA 的组件选择/安装覆盖。旧通用流程只提供基础驱动，并将 Extension INF 排除；本次增加独立扩展通道。

对提供的 `sp174139` 静态实包与之前诊断中的声卡/功放硬件 ID 进行复核，六个适用 Extension INF 均有有效 ExtensionId 且精确命中目标硬件，包括 `csaudioext.inf` 对 `ACPI\VEN_CSC&DEV_3551&SUBSYS_103C8971` 的 `CONF_0202`，以及 Realtek/Sonitude 配套扩展。这是静态匹配验证，尚未在故障机上运行新版并完成扬声器播放验收。

参考：
- https://learn.microsoft.com/en-us/windows-hardware/drivers/install/using-an-extension-inf-file
- https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/submit-dashboard-extension-inf-files
- https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax
