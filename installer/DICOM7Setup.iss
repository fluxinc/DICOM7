; DICOM2ORM Inno Setup Script

#define MyAppName "DICOM7"
#define MyAppVersion "1.0.5"
#define MyAppPublisher "Flux Inc"
#define MyAppURL "https://fluxinc.co/"
#define MyAppExeName "DICOM2ORM.exe"
#define MyAppDataDir "{commonappdata}\Flux Inc\DICOM7"

[Setup]
AppId={{E2399B1C-9D29-4F45-BB2B-4B33F1281D99}
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
OutputBaseFilename=DICOM7Setup-x64-{#MyAppVersion}
DisableProgramGroupPage=true
DisableDirPage=false
; SetupIconFile=Input\DICOM2ORM_icon.ico
Compression=lzma
SolidCompression=yes
Uninstallable=yes
DefaultDirName={commonpf64}\{#MyAppPublisher}\{#MyAppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\DICOM2ORM\bin\Release\*.*"; DestDir: "{app}"; Flags: ignoreversion;
Source: "..\DICOM2ORU\bin\Release\*.*"; DestDir: "{app}"; Flags: ignoreversion;

; Source: "Input\DICOM2ORM_icon.ico"; DestDir: "{app}"; Flags: ignoreversion;
Source: "Input\WinSW-x64.exe"; DestDir: "{app}"; Flags: ignoreversion;
Source: "Input\DICOM2ORM.WinSW.xml"; DestDir: "{app}"; Flags: ignoreversion;
Source: "..\DICOM2ORM\config.yaml"; DestDir: "{#MyAppDataDir}\DICOM2ORM"; DestName: config.yaml; Flags: onlyifdoesntexist confirmoverwrite;

Source: "Input\DICOM2ORU.WinSW.xml"; DestDir: "{app}"; Flags: ignoreversion;
Source: "..\DICOM2ORU\config.yaml"; DestDir: "{#MyAppDataDir}\DICOM2ORU"; DestName: config.yaml; Flags: onlyifdoesntexist confirmoverwrite;



[Run]
Filename: "{sys}\sc.exe"; Parameters: "stop DICOM2ORM"; Description: "Stopping existing service"; WorkingDir: {app}; Check: IsServiceRunning('DICOM2ORM');
Filename: "{sys}\sc.exe"; Parameters: "delete DICOM2ORM"; Description: "Removing existing service"; WorkingDir: {app}; Flags: runhidden;
Filename: "{app}\WinSW-x64.exe"; Parameters: "install ""{app}\DICOM2ORM.WinSW.xml"""; Description: "Installing DICOM2ORM service"; WorkingDir: {app}; Flags: runhidden;

Filename: "{sys}\sc.exe"; Parameters: "stop DICOM2ORU"; Description: "Stopping existing service"; WorkingDir: {app}; Check: IsServiceRunning('DICOM2ORU');
Filename: "{sys}\sc.exe"; Parameters: "delete DICOM2ORU"; Description: "Removing existing service"; WorkingDir: {app}; Flags: runhidden;
Filename: "{app}\WinSW-x64.exe"; Parameters: "install ""{app}\DICOM2ORU.WinSW.xml"""; Description: "Installing DICOM2ORU service"; WorkingDir: {app}; Flags: runhidden;

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop DICOM2ORM"; Check: IsServiceRunning('DICOM2ORM');
Filename: "{sys}\sc.exe"; Parameters: "delete DICOM2ORM"; Flags: runhidden;

Filename: "{sys}\sc.exe"; Parameters: "stop DICOM2ORU"; Check: IsServiceRunning('DICOM2ORU');
Filename: "{sys}\sc.exe"; Parameters: "delete DICOM2ORU"; Flags: runhidden;

[Code]

#include "Include\Services.pas"
