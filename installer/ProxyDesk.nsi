Unicode True
!include "MUI2.nsh"
!include "x64.nsh"
!include "LogicLib.nsh"
Name "ProxyDesk"
!ifdef DRIVERLESS_PREVIEW
OutFile "${OUTPUT_DIR}\ProxyDesk-Setup-0.1.2-x64-preview.exe"
!else
OutFile "${OUTPUT_DIR}\ProxyDesk-Setup-0.1.2-x64.exe"
!endif
InstallDir "$PROGRAMFILES64\ProxyDesk"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
ShowInstDetails show
ShowUninstDetails show
!define MUI_ABORTWARNING
!define MUI_ICON "..\src\ProxyDesk.Desktop\Assets\ProxyDesk.ico"
!define MUI_UNICON "..\src\ProxyDesk.Desktop\Assets\ProxyDesk.ico"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"
Var OwnerSid
Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "ProxyDesk 需要 Windows x64。"
    Abort
  ${EndIf}
!ifdef DRIVERLESS_PREVIEW
  MessageBox MB_OKCANCEL|MB_ICONINFORMATION "此构建不内置 WinDivert 驱动。安装后可在仪表盘点击“下载驱动并启用”，完成官方驱动校验和 Windows 管理员授权。驱动就绪前可管理配置及测试上游代理。安装过程会创建后台服务。" IDOK +2
  Abort
!endif
FunctionEnd
Section "ProxyDesk"
  SetRegView 64
  SetOutPath "$INSTDIR"
  nsExec::ExecToStack '"$SYSDIR\sc.exe" stop ProxyDesk'
  Pop $0
  Pop $1
  Sleep 1500
  File /r "${PUBLISH_DIR}\*.*"
  nsExec::ExecToStack '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -Command "[Security.Principal.WindowsIdentity]::GetCurrent().User.Value"'
  Pop $0
  Pop $OwnerSid
  ${If} $0 != 0
    MessageBox MB_ICONSTOP "无法获取安装用户身份。"
    Abort
  ${EndIf}
  nsExec::ExecToStack '"$INSTDIR\ProxyDesk.Service.exe" --initialize-owner $OwnerSid'
  Pop $0
  Pop $1
  ${If} $0 != 0
    MessageBox MB_ICONSTOP "无法初始化服务数据目录：$1"
    Abort
  ${EndIf}
  nsExec::ExecToStack '"$SYSDIR\sc.exe" start ProxyDesk'
  Pop $0
  Pop $1
  ${If} $0 != 0
    MessageBox MB_ICONSTOP "后台服务启动失败：$1"
    Abort
  ${EndIf}
  CreateDirectory "$SMPROGRAMS\ProxyDesk"
  CreateShortcut "$SMPROGRAMS\ProxyDesk\ProxyDesk.lnk" "$INSTDIR\ProxyDesk.Desktop.exe"
  CreateShortcut "$DESKTOP\ProxyDesk.lnk" "$INSTDIR\ProxyDesk.Desktop.exe"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\ProxyDesk" "DisplayName" "ProxyDesk"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\ProxyDesk" "DisplayVersion" "0.1.2"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\ProxyDesk" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
SectionEnd
Section "Uninstall"
  SetRegView 64
  nsExec::ExecToStack '"$SYSDIR\sc.exe" stop ProxyDesk'
  Pop $0
  Pop $1
  Sleep 2000
  nsExec::ExecToStack '"$SYSDIR\sc.exe" delete ProxyDesk'
  Pop $0
  Pop $1
  nsExec::ExecToStack '"$SYSDIR\netsh.exe" advfirewall firewall delete rule name=all program=$\"$INSTDIR\ProxyDesk.Service.exe$\"'
  Pop $0
  Pop $1
  Delete "$DESKTOP\ProxyDesk.lnk"
  Delete "$SMPROGRAMS\ProxyDesk\ProxyDesk.lnk"
  RMDir "$SMPROGRAMS\ProxyDesk"
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\ProxyDesk"
  !include "${OUTPUT_DIR}\uninstall-files.nsh"
  Delete /REBOOTOK "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
SectionEnd
