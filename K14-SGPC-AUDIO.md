# Lenovo K14 Gen 1 display-audio dependency

## Evidence

Supplied package: `Z:/10.Device  Drive/01_联想驱动/K14GEN/intelVGA-r1vi509w.exe`.
Lenovo Authenticode signature valid. SHA256: `6063963DD357E36896E2C4B2F9F990ACFFBF0179336017A28BBC2AC69B4EA02D`.
Statically extracted with innoextract; vendor installer was not executed.

`Graphics/HdBusExt.inf` (31.0.101.5388) declares ExtensionId
`{69CB4EDD-BAFE-4F9A-9135-C1F284D6860D}` and matches `PCI\VEN_8086&DEV_A0C8`.
Its selected section writes `HKR,Settings,GfxSharedCodecAddress,REG_DWORD,2`.
`Graphics/MSHdaDac.inf` (31.0.101.5388) targets SGPC_FUNC_01 on current Windows,
and includes the Windows in-box `AcxHdAudioP.inf`.
The legacy `DisplayAudio/11.2/IntcDAud.inf` has an empty modern-Windows section;
`DisplayAudio/12.1/IntcDAud.inf` targets a different device (2819). Neither is a
valid way to force the currently reported FUNC_01 DEV_2812 node on build 26300.

Target diagnostic confirms legacy FUNC_01, Code 28, and Windows `Unable to find
any matching devices` for the two previously staged wrappers. This supports a
missing controller configuration dependency; actual SGPC re-enumeration and
successful binding/playback still need verification on the target.

## UDA change in 5.3.36

The generic-extension gate previously required SUBSYS targeting or matching OEM
provenance. The verified Intel HdBusExt lineage now has a narrow exception:
matching ExtensionId, Intel provider, and exclusively PCI VEN_8086/DEV-only targets.
Other unknown-OEM calibration extensions remain blocked. Installation signature
checks remain active. Legacy metadata recovers ExtensionId from the existing
archive without rebuilding the whole repository.

## Deployment

1. Upgrade server and regenerate/redistribute client and SCCM packages.
2. Import `K14-Audio-Repair/Intel-SGPC-DisplayAudio-Lenovo-31.0.101.5388.zip` through
   normal driver import, not machine-specific full-package mapping. It contains
   unmodified HdBusExt.inf/HdBusExt.cat and MSHdaDac.inf/AcxDAC.cat plus vendor license.
3. Scan and install the PCI audio controller extension. Follow a required restart;
   if the legacy child persists, restart before the next verification scan.
4. Rescan: verify whether SGPC_FUNC_01 appears and the audio wrapper binds. A
   package staged in Driver Store is not proof of binding. Verify playback separately.
5. SCCM: retain incomplete/failure reporting; if a restart is needed, rerun UDA
   after the task-sequence restart. Do not configure all failures as success.

No driver was installed or removed on the development computer.


## 5.3.39: SCCM PCI compatible-ID regression

The 2026-10-30 installation log contains SST, Realtek and other extensions, but
no HdBusExt installation. The earlier platform exception was incomplete: PCI
VEN/DEV is normally in the device CompatibleIds; HardwareIds carry SUBSYS/REV.
The server and client still required match level 4 (hardware-to-hardware), so
both could reject this required extension at match level 2. The original test
incorrectly placed bare VEN/DEV in HardwareIds and missed the defect.

5.3.39 shares the same target policy between recommendation and extracted-INF
revalidation: the known Intel SGPC ExtensionId with exclusively Intel PCI VEN/DEV
targets may match an identical compatible ID. All other extensions still require
hardware-to-hardware matching; OS, signature, identity and OEM gates remain.
The regression now uses subsystem-specific HardwareIds and VEN/DEV CompatibleIds,
and exercises download plus installation prevalidation through the fake installer.

Deploy both the server and newly generated SCCM client. Ensure the normal
repository contains the unchanged official repair component ZIP above. If Windows
requires a restart to activate the extension, retain the task-sequence restart
and subsequent UDA pass. Verify the resulting SGPC child and actual Windows
binding; a successful unit test cannot establish hardware activation or sound.
