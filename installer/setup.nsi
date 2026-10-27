;==============================================================================
;  Modbus 从站模拟器 —— NSIS 安装脚本（NSIS 3+）
;  文件必须以 UTF-8 BOM 保存（含中文界面）
;  可由 GitHub Actions 通过 /D 传入：
;    /DAPP_VERSION=1.0.9800.12345      安装包版本号（与程序集版本一致）
;    /DPUBLISH_DIR=<dotnet publish 输出目录>
;    /DOUTFILE=<安装包输出路径>
;    /DICON_FILE=<图标 .ico 路径>      可选，缺省用 NSIS 默认图标
;    /DLICENSE_FILE=<许可协议 .txt>    可选，缺省不显示许可页面
; 升级安装时会自动备份并还原用户配置 Setting.json
;==============================================================================

Unicode true

;---------- 可覆盖的外部参数 ----------
!ifndef APP_VERSION
    !define APP_VERSION "1.0.0.0"
!endif
!ifndef PUBLISH_DIR
    !define PUBLISH_DIR "..\publish"
!endif
!ifndef OUTFILE
    !define OUTFILE "..\Modbus从站模拟器-Setup.exe"
!endif


;---------- 基本信息 ----------
!define APP_NAME        "Modbus从站模拟器"
!define APP_EXE         "通用模拟器.exe"
!define INSTALL_SUBDIR  "Modbus从站模拟器"
!define UNINST_KEY      "Software\Microsoft\Windows\CurrentVersion\Uninstall\Modbus从站模拟器"

Name        "${APP_NAME} ${APP_VERSION}"
OutFile     "${OUTFILE}"
InstallDir  "$PROGRAMFILES64\${INSTALL_SUBDIR}"     ; C:\Program Files\Modbus从站模拟器
InstallDirRegKey HKLM "${UNINST_KEY}" "InstallLocation"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
SetOverwrite on
ShowInstDetails   show
ShowUnInstDetails show
BrandingText "${APP_NAME} ${APP_VERSION}"

;---------- 安装包自身的版本信息（与应用程序版本一致） ----------
VIProductVersion  "${APP_VERSION}"
VIAddVersionKey   "ProductName"     "${APP_NAME}"
VIAddVersionKey   "ProductVersion"  "${APP_VERSION}"
VIAddVersionKey   "FileVersion"     "${APP_VERSION}"
VIAddVersionKey   "FileDescription" "${APP_NAME} 安装程序"
VIAddVersionKey   "LegalCopyright"  "Copyright (c) ${APP_NAME}"

;---------- 界面 ----------
!include "LogicLib.nsh"
!include "MUI2.nsh"
!include "FileFunc.nsh"

!ifdef ICON_FILE
    !define MUI_ICON   "${ICON_FILE}"
    !define MUI_UNICON "${ICON_FILE}"
!endif
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN            "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT       "运行 ${APP_NAME}"
!define MUI_FINISHPAGE_NOREBOOTSUPPORT

!ifdef LICENSE_FILE                                 ; 由 CI 传入许可协议文件（可选）
    !insertmacro MUI_PAGE_LICENSE "${LICENSE_FILE}"
!endif
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "SimpChinese"

;==============================================================================
; 安装前：检测旧版本 -> 静默卸载后升级（保留原安装目录）
;==============================================================================
Function .onInit
    SetRegView 64                                   ; 与 64 位安装目录对应，卸载信息写入 64 位注册表视图
    ReadRegStr $0 HKLM "${UNINST_KEY}" "InstallLocation"
    ReadRegStr $1 HKLM "${UNINST_KEY}" "DisplayVersion"

    StrCmp $0 "" done                               ; 从未安装过
    IfFileExists "$0\uninst.exe" 0 done             ; 旧安装目录已丢失
    StrCpy $INSTDIR $0                              ; 沿用旧版本的安装目录
    StrCmp $1 "${APP_VERSION}" done                 ; 同版本：直接覆盖安装

    IfSilent +3                                     ; 静默模式下不弹窗，直接升级
    MessageBox MB_YESNO|MB_ICONQUESTION "检测到已安装的 ${APP_NAME} $1。$\r$\n是否升级到 ${APP_VERSION}？" IDYES +2 IDNO +1
    Abort

    ; 升级前备份旧配置（旧版本存在程序目录），装完由程序迁移到 %LOCALAPPDATA%
    IfFileExists "$0\Setting.json" 0 +2
    CopyFiles "$0\Setting.json" "$TEMP\${APP_NAME}-Setting.json"

    DetailPrint "正在移除旧版本 $1 ..."
    ExecWait '"$0\uninst.exe" /S _?=$0' $2
    StrCmp $2 "0" +3                                ; 返回 0 表示卸载成功
    MessageBox MB_OK|MB_ICONSTOP "卸载旧版本失败（退出码 $2），安装已终止。"
    Abort
    DetailPrint "旧版本已移除。"

done:
FunctionEnd

;==============================================================================
; 主安装节
;==============================================================================
Section "主程序" SEC_MAIN
    SectionIn RO
    SetOutPath "$INSTDIR"

    ; 发布产物（排除调试符号文件）
    File /r /x "*.pdb" "${PUBLISH_DIR}\*.*"

    ; 还原升级前备份的旧配置（新版配置在 %LOCALAPPDATA%，程序首次启动会自行迁移）
    IfFileExists "$INSTDIR\Setting.json" +3 0
    IfFileExists "$TEMP\${APP_NAME}-Setting.json" 0 +2
    CopyFiles "$TEMP\${APP_NAME}-Setting.json" "$INSTDIR\Setting.json"
    Delete "$TEMP\${APP_NAME}-Setting.json"

    ; 卸载程序
    WriteUninstaller "$INSTDIR\uninst.exe"

    ; 卸载/升级所需的注册表信息
    WriteRegStr   HKLM "${UNINST_KEY}" "DisplayName"           "${APP_NAME}"
    WriteRegStr   HKLM "${UNINST_KEY}" "DisplayVersion"        "${APP_VERSION}"
    WriteRegStr   HKLM "${UNINST_KEY}" "Publisher"             "${APP_NAME}"
    WriteRegStr   HKLM "${UNINST_KEY}" "InstallLocation"       "$INSTDIR"
    WriteRegStr   HKLM "${UNINST_KEY}" "UninstallString"       '"$INSTDIR\uninst.exe"'
    WriteRegStr   HKLM "${UNINST_KEY}" "QuietUninstallString"  '"$INSTDIR\uninst.exe" /S'
    WriteRegStr   HKLM "${UNINST_KEY}" "DisplayIcon"           "$INSTDIR\${APP_EXE},0"
    WriteRegDWORD HKLM "${UNINST_KEY}" "NoModify"              1
    WriteRegDWORD HKLM "${UNINST_KEY}" "NoRepair"              1
    ${GetSize} "$INSTDIR" "/S=0K" $3 $4 $5
    IntFmt $3 "0x%08X" $3
    WriteRegDWORD HKLM "${UNINST_KEY}" "EstimatedSize" "$3"

    ; 快捷方式（所有用户）
    SetShellVarContext all
    CreateShortCut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
    CreateDirectory "$SMPROGRAMS\${APP_NAME}"
    CreateShortCut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
    CreateShortCut "$SMPROGRAMS\${APP_NAME}\卸载 ${APP_NAME}.lnk" "$INSTDIR\uninst.exe"
SectionEnd

;==============================================================================
; 卸载前：安全校验（避免 $INSTDIR 异常时误删磁盘内容）
;==============================================================================
Function un.onInit
    SetRegView 64                                   ; 与安装时同一注册表视图，否则删不掉卸载项
    StrLen $0 "$INSTDIR"
    ${If} $0 < 4
        MessageBox MB_OK|MB_ICONSTOP "安装目录无效，卸载已终止。"
        Abort
    ${EndIf}
FunctionEnd

;==============================================================================
; 卸载节
;==============================================================================
Section "Uninstall"
    nsExec::Exec 'taskkill /F /T /IM "${APP_EXE}"'             ; 忽略返回值（未运行时会失败）
    Pop $0

    SetShellVarContext all
    Delete "$DESKTOP\${APP_NAME}.lnk"
    Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
    Delete "$SMPROGRAMS\${APP_NAME}\卸载 ${APP_NAME}.lnk"
    RMDir  "$SMPROGRAMS\${APP_NAME}"

    RMDir /r "$INSTDIR"
    DeleteRegKey HKLM "${UNINST_KEY}"
SectionEnd
