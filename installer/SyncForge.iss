#define MyAppName "SyncForge"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "SyncForge"
#define MyAppExeName "SyncForge.Desktop.exe"

[Setup]
AppId={{E96F5CA8-7E61-4D16-89C4-5BBE5D63742E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\SyncForge
DefaultGroupName=SyncForge
OutputDir=..\output\installer
OutputBaseFilename=SyncForge-Setup
Compression=lzma
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible

[Files]
Source: "..\publish\desktop\*"; DestDir: "{app}\desktop"; Flags: recursesubdirs ignoreversion
Source: "..\publish\worker\*"; DestDir: "{app}\worker"; Flags: recursesubdirs ignoreversion

[Dirs]
Name: "{commonappdata}\SyncForge"; Permissions: admins-full system-full

[Icons]
Name: "{group}\SyncForge"; Filename: "{app}\desktop\{#MyAppExeName}"
Name: "{autodesktop}\SyncForge"; Filename: "{app}\desktop\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop icon"; GroupDescription: "Additional icons:"

[Run]
Filename: "sc.exe"; Parameters: "create \"SyncForge Worker\" binPath= \"\"\"{app}\worker\SyncForge.Worker.exe\"\"\" start= auto"; Flags: runhidden
Filename: "sc.exe"; Parameters: "failure \"SyncForge Worker\" reset= 86400 actions= restart/5000/restart/5000/restart/5000"; Flags: runhidden
Filename: "sc.exe"; Parameters: "start \"SyncForge Worker\""; Flags: runhidden

[UninstallRun]
Filename: "sc.exe"; Parameters: "stop \"SyncForge Worker\""; Flags: runhidden
Filename: "sc.exe"; Parameters: "delete \"SyncForge Worker\""; Flags: runhidden
