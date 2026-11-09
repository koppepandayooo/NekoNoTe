; NekoNoTe のインストーラー(Inno Setup 6)
; ビルド: ISCC.exe /DAppVersion=0.2.0 installer\NekoNoTe.iss
; 先に dotnet publish NekoNoTe.csproj -c Release -o dist を実行しておく

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6F0F3C52-8E1B-4B7A-9D0E-5B1C7A2E4D11}
AppName=NekoNoTe
AppVersion={#AppVersion}
AppPublisher=koppepandayooo
AppPublisherURL=https://github.com/koppepandayooo/NekoNoTe
DefaultDirName={localappdata}\Programs\NekoNoTe
DefaultGroupName=NekoNoTe
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
LicenseFile=..\LICENSE
SetupIconFile=..\Resources\app.ico
UninstallDisplayIcon={app}\NekoNoTe.exe
OutputDir=..\dist-installer
OutputBaseFilename=NekoNoTe-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=force
RestartApplications=no

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "desktopicon"; Description: "デスクトップにショートカットを作る"

[Files]
Source: "..\dist\NekoNoTe.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\NekoNoTe"; Filename: "{app}\NekoNoTe.exe"
Name: "{userdesktop}\NekoNoTe"; Filename: "{app}\NekoNoTe.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\NekoNoTe.exe"; Description: "NekoNoTe を起動する"; Flags: postinstall nowait
