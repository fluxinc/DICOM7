; OrderORM Inno Setup Script

#define MyAppName "OrderORM"
#define MyAppVersion "1.0.3"
#define MyAppPublisher "Flux Inc"
#define MyAppURL "https://fluxinc.co/"
#define MyAppExeName "OrderORM.exe"
#define MyAppDataDir "{commonappdata}\Flux Inc\OrderORM"

[Setup]
AppId={{F8BFE0BB-D200-4954-B45E-8D0E72E19188}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
ArchitecturesAllowed=x64
DefaultGroupName={#MyAppPublisher}\{#MyAppName}
CreateAppDir=yes
OutputBaseFilename=OrderORMSetup-x64-{#MyAppVersion}
DisableProgramGroupPage=true
DisableDirPage=false
; SetupIconFile=Input\OrderORM_icon.ico
Compression=lzma
SolidCompression=yes
Uninstallable=yes
DefaultDirName={commonpf64}\{#MyAppPublisher}\{#MyAppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\bin\Release\*.*"; DestDir: "{app}"; Flags: ignoreversion;
; Source: "Input\OrderORM_icon.ico"; DestDir: "{app}"; Flags: ignoreversion;
Source: "Input\WinSW-x64.exe"; DestDir: "{app}"; Flags: ignoreversion;
Source: "Input\OrderORM.WinSW.xml"; DestDir: "{app}"; Flags: ignoreversion;
Source: "Input\config.yaml"; DestDir: "{#MyAppDataDir}"; Flags: onlyifdoesntexist confirmoverwrite;


[Run]
Filename: "{sys}\sc.exe"; Parameters: "stop OrderORM"; Description: "Stopping existing service"; WorkingDir: {app}; Check: IsServiceRunning('OrderORMService');
Filename: "{sys}\sc.exe"; Parameters: "delete OrderORM"; Description: "Removing existing service"; WorkingDir: {app}; Flags: runhidden;

Filename: "{app}\WinSW-x64.exe"; Parameters: "install ""{app}\OrderORM.WinSW.xml"""; Description: "Installing OrderORM service"; WorkingDir: {app}; Flags: runhidden;

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop OrderORM"; Check: IsServiceRunning('OrderORM');
Filename: "{sys}\sc.exe"; Parameters: "delete OrderORM"; Flags: runhidden;

[Code]

#include "Include\Services.pas"
