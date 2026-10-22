# 5.3.3：安装前复用归档核验结果

安装时先读取 MySQL 的 repo.archive-verification.json 与 repo.archive-hashes.json，检查每个独立 ZIP 的 Windows 文件标识、卷标识、长度、创建/修改/变更时间、属性、核验规则版本，以及该 ZIP 对应的 INF 路径/哈希/包根目录/包哈希。全部一致且没有待核对的散装 packages 目录时，直接复用已核验结果，不重新读取 ZIP 内容、不解压逐项校验。

只有 ZIP 的归档内容指纹仍与已入库指纹一致才可重新登记，避免只改动 SYS 等负载却沿用旧 INF 的归档被重新认可。

记录缺失/不完整、文件新增或替换、清单内容变化、无法取得可靠文件状态，都会重新核验；有待清理的散装文件时必须与 ZIP 比对后才能删除。已修改 ZIP 即使恢复文件长度与最后修改时间，也会因原生变更时间或文件标识不同而失效。记录只在整轮归档校验全部成功后写入，失败不会发布不完整的新记录。

机型整包的多个 INF 共享的基础归档只核验一次；派生机型 ZIP 也记录文件状态，未变化时保留，不再每次安装删除后重压缩。原始驱动仍由既有核验与数据库读回门槛保护，压缩包仍留在本地。

首次安装本版本时，旧版本只有归档 SHA256，没有可靠文件状态记录，因此仍需要核验一次以建立记录；之后未变化的归档自动跳过。文件系统或系统 API 不支持可靠文件标识/变更时间时会回到完整核验。NTFS 与可提供 FileIdInfo 的 Windows 文件系统支持快速预检。

安装进度显示独立归档数、跳过数量和重新核验数量。本改动不会跳过数据库连接、权限、目录完整性与业务数据迁移检查。

实现依据：Windows FILE_BASIC_INFO 的 ChangeTime 与文件标识接口，见 https://learn.microsoft.com/windows/win32/api/winbase/ns-winbase-file_basic_info 与 https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-getfileinformationbyhandleex 。
