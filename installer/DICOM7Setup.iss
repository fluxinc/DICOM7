; DICOM7 Inno Setup Script

#define MyAppName "DICOM7"
#define MyAppVersion "2.0.2"
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
InfoBeforeFile="Input\readme.txt"

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Components]
Name: "core"; Description: "Core Files (Required)"; Types: full custom; Flags: fixed
Name: "dicom2orm"; Description: "DICOM2ORM - DICOM Worklist to HL7 ORM (Order) messages"; Types: full custom
Name: "dicom2oru"; Description: "DICOM2ORU - DICOM C-Store to HL7 ORU (Result) messages"; Types: full custom
Name: "orm2dicom"; Description: "ORM2DICOM - HL7 ORMs (Orders) to DICOM Modality Worklist"; Types: full custom
Name: "oru2dicom"; Description: "ORU2DICOM - HL7 ORU (Result) messages to DICOM C-STORE"; Types: full custom

[Types]
Name: "full"; Description: "Full installation (all components)"
Name: "custom"; Description: "Custom installation"; Flags: iscustom

[Dirs]
Name: "{#MyAppDataDir}"; Flags: uninsalwaysuninstall

[Files]
; Core files for all installations
Source: "Input\WinSW-x64.exe"; DestDir: "{app}"; Flags: ignoreversion; Components: core
Source: "..\Shared\bin\Release\net462\*.*"; DestDir: "{app}"; Flags: ignoreversion; Components: core

; DICOM2ORM component
Source: "..\DICOM2ORM\bin\Release\*.*"; DestDir: "{app}"; Flags: ignoreversion; Components: dicom2orm
Source: "Input\DICOM2ORM.WinSW.xml"; DestDir: "{app}"; Flags: ignoreversion; Components: dicom2orm
Source: "..\DICOM2ORM\config.yaml"; DestDir: "{#MyAppDataDir}\DICOM2ORM"; DestName: config.yaml; Flags: onlyifdoesntexist confirmoverwrite; Components: dicom2orm

; DICOM2ORU component
Source: "..\DICOM2ORU\bin\Release\*.*"; DestDir: "{app}"; Flags: ignoreversion; Components: dicom2oru
Source: "Input\DICOM2ORU.WinSW.xml"; DestDir: "{app}"; Flags: ignoreversion; Components: dicom2oru
Source: "..\DICOM2ORU\config.yaml"; DestDir: "{#MyAppDataDir}\DICOM2ORU"; DestName: config.yaml; Flags: onlyifdoesntexist confirmoverwrite; Components: dicom2oru

; ORM2DICOM component
Source: "..\ORM2DICOM\bin\Release\*.*"; DestDir: "{app}"; Flags: ignoreversion; Components: orm2dicom
Source: "Input\ORM2DICOM.WinSW.xml"; DestDir: "{app}"; Flags: ignoreversion; Components: orm2dicom
Source: "..\ORM2DICOM\config.yaml"; DestDir: "{#MyAppDataDir}\ORM2DICOM"; DestName: config.yaml; Flags: onlyifdoesntexist confirmoverwrite; Components: orm2dicom

; ORU2DICOM component
Source: "..\ORU2DICOM\bin\Release\*.*"; DestDir: "{app}"; Flags: ignoreversion; Components: oru2dicom
Source: "Input\ORU2DICOM.WinSW.xml"; DestDir: "{app}"; Flags: ignoreversion; Components: oru2dicom
Source: "..\ORU2DICOM\config.yaml"; DestDir: "{#MyAppDataDir}\ORU2DICOM"; DestName: config.yaml; Flags: onlyifdoesntexist confirmoverwrite; Components: oru2dicom

[Run]
; DICOM2ORM Service Install
Filename: "{sys}\sc.exe"; Parameters: "stop DICOM7_DICOM2ORM"; Description: "Stopping existing DICOM2ORM service"; WorkingDir: {app}; Flags: runhidden;
Filename: "{sys}\sc.exe"; Parameters: "stop DICOM7_DICOM2ORU"; Description: "Stopping existing DICOM2ORU service"; WorkingDir: {app}; Flags: runhidden;
Filename: "{sys}\sc.exe"; Parameters: "stop DICOM7_ORM2DICOM"; Description: "Stopping existing ORM2DICOM service"; WorkingDir: {app}; Flags: runhidden;
Filename: "{sys}\sc.exe"; Parameters: "stop DICOM7_ORU2DICOM"; Description: "Stopping existing ORU2DICOM service"; WorkingDir: {app}; Flags: runhidden;

Filename: "{sys}\sc.exe"; Parameters: "delete DICOM7_DICOM2ORM"; Description: "Removing existing DICOM2ORM service"; WorkingDir: {app}; Flags: runhidden; Components: dicom2orm;
Filename: "{app}\WinSW-x64.exe"; Parameters: "install ""{app}\DICOM2ORM.WinSW.xml"""; Description: "Installing DICOM2ORM service"; WorkingDir: {app}; Flags: runhidden; Components: dicom2orm;
Filename: "{sys}\sc.exe"; Parameters: "start DICOM7_DICOM2ORM"; Description: "Starting DICOM2ORM service"; Flags: runhidden; WorkingDir: {app}; Components: dicom2orm;

; DICOM2ORU Service Install
Filename: "{sys}\sc.exe"; Parameters: "delete DICOM7_DICOM2ORU"; Description: "Removing existing DICOM2ORU service"; WorkingDir: {app}; Flags: runhidden; Components: dicom2oru;
Filename: "{app}\WinSW-x64.exe"; Parameters: "install ""{app}\DICOM2ORU.WinSW.xml"""; Description: "Installing DICOM2ORU service"; WorkingDir: {app}; Flags: runhidden; Components: dicom2oru;
Filename: "{sys}\sc.exe"; Parameters: "start DICOM7_DICOM2ORU"; Description: "Starting DICOM2ORU service"; Flags: runhidden; WorkingDir: {app}; Components: dicom2oru;

; ORM2DICOM Service Install
Filename: "{sys}\sc.exe"; Parameters: "delete DICOM7_ORM2DICOM"; Description: "Removing existing ORM2DICOM service"; WorkingDir: {app}; Flags: runhidden; Components: orm2dicom;
Filename: "{app}\WinSW-x64.exe"; Parameters: "install ""{app}\ORM2DICOM.WinSW.xml"""; Description: "Installing ORM2DICOM service"; WorkingDir: {app}; Flags: runhidden; Components: orm2dicom;
Filename: "{sys}\sc.exe"; Parameters: "start DICOM7_ORM2DICOM"; Description: "Starting ORM2DICOM service"; Flags: runhidden; WorkingDir: {app}; Components: orm2dicom;

; ORU2DICOM Service Install
Filename: "{sys}\sc.exe"; Parameters: "delete DICOM7_ORU2DICOM"; Description: "Removing existing ORU2DICOM service"; WorkingDir: {app}; Flags: runhidden; Components: oru2dicom;
Filename: "{app}\WinSW-x64.exe"; Parameters: "install ""{app}\ORU2DICOM.WinSW.xml"""; Description: "Installing ORU2DICOM service"; WorkingDir: {app}; Flags: runhidden; Components: oru2dicom;
Filename: "{sys}\sc.exe"; Parameters: "start DICOM7_ORU2DICOM"; Description: "Starting ORU2DICOM service"; Flags: runhidden; WorkingDir: {app}; Components: oru2dicom;

[UninstallRun]
; Only try to uninstall services if they exist
Filename: "{sys}\sc.exe"; Parameters: "stop DICOM7_DICOM2ORM";  Flags: runhidden;
Filename: "{sys}\sc.exe"; Parameters: "delete DICOM7_DICOM2ORM"; Flags: runhidden;

Filename: "{sys}\sc.exe"; Parameters: "stop DICOM7_DICOM2ORU"; Flags: runhidden;
Filename: "{sys}\sc.exe"; Parameters: "delete DICOM7_DICOM2ORU"; Flags: runhidden;

Filename: "{sys}\sc.exe"; Parameters: "stop DICOM7_ORM2DICOM"; Flags: runhidden;
Filename: "{sys}\sc.exe"; Parameters: "delete DICOM7_ORM2DICOM"; Flags: runhidden;

Filename: "{sys}\sc.exe"; Parameters: "stop DICOM7_ORU2DICOM"; Flags: runhidden;
Filename: "{sys}\sc.exe"; Parameters: "delete DICOM7_ORU2DICOM"; Flags: runhidden;

[Code]

#include "Include\Services.pas"

procedure InitializeWizard;
begin
  // Add custom initialization code if needed
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
end;
