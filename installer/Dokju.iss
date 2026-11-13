#ifndef BuildNumber
  #define BuildNumber "0"
#endif

[Setup]
AppId={{027FBFD8-323E-4C51-AC23-64E1EF9D4389}
AppName=Dokju
AppVersion=1.0.{#BuildNumber}
AppPublisher=Dokju
AppPublisherURL=https://github.com/tayco00/Dokju
AppUpdatesURL=https://github.com/tayco00/Dokju/releases/latest
DefaultDirName={localappdata}\Programs\Dokju
DefaultGroupName=Dokju
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
AppMutex=Local\Dokju-Desktop,Local\Dokju-Launcher
OutputDir=..
OutputBaseFilename=Dokju-Setup
SetupIconFile=..\desktop\Dokju.ico
UninstallDisplayIcon={app}\Dokju.exe
WizardStyle=modern
Compression=lzma2
SolidCompression=yes

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\Dokju.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\Dokju.exe"; DestDir: "{app}\bin"; Flags: ignoreversion
Source: "..\bin\Dokju.Start.exe"; DestDir: "{app}\bin"; Flags: ignoreversion
Source: "..\bin\release.json"; DestDir: "{app}\bin"; Flags: ignoreversion
Source: "..\bin\node.exe"; DestDir: "{app}\bin"; Flags: ignoreversion
Source: "..\bin\NODE-LICENSE.txt"; DestDir: "{app}\bin"; Flags: ignoreversion
Source: "..\bin\backend\*.mjs"; DestDir: "{app}\bin\backend"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\Dokju"; Filename: "{app}\Dokju.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\Dokju"; Filename: "{app}\Dokju.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Dokju.exe"; Description: "{cm:LaunchProgram,Dokju}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Dokju\App"
