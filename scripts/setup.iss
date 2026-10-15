[Setup]
AppId={{F2431F11-C7B6-4E8C-80FC-C273231D908E}
AppName=NetEaseNEXT
AppVersion=0.2.0
AppPublisher=Yizuka17
DefaultDirName={localappdata}\Programs\NetEaseNEXT
DefaultGroupName=NetEaseNEXT
DisableDirPage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename=NetEaseNEXT-0.2.0-Setup-x64
SetupIconFile=..\NetEaseNEXT\Assets\AppIcon.ico
UninstallDisplayIcon={app}\NetEaseNEXT.exe
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
MinVersion=10.0.19041

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "..\artifacts\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{autoprograms}\NetEaseNEXT\NetEaseNEXT"; Filename: "{app}\NetEaseNEXT.exe"; AppUserModelID: "NetEaseNEXT.Desktop"
Name: "{autodesktop}\NetEaseNEXT"; Filename: "{app}\NetEaseNEXT.exe"; Tasks: desktopicon; AppUserModelID: "NetEaseNEXT.Desktop"

[Run]
Filename: "{app}\NetEaseNEXT.exe"; Description: "Launch NetEaseNEXT"; Flags: nowait postinstall skipifsilent
