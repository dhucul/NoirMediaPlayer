#define AppName "NOIR Media Player"
#define AppVersion "1.0.10"
#define AppPublisher "NOIR"
#define AppExeName "NoirMediaPlayer.exe"
#ifndef PublishDir
  #define PublishDir "..\..\artifacts\publish\win-x64"
#endif
#if !FileExists(PublishDir + "\\" + AppExeName)
  #error "Publish NOIR before compiling the installer, and pass /DPublishDir=<fresh publish directory>."
#endif
#if GetVersionNumbersString(PublishDir + "\\" + AppExeName) != AppVersion + ".0"
  #error "Published NOIR version does not match the installer. Publish the current release into a fresh directory."
#endif

[Setup]
AppId={{EB62B4CB-2081-4D5A-89C9-5748A10263ED}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Installer
VersionInfoProductName={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\..\Installer
OutputBaseFilename=NOIR-Media-Player-Setup-{#AppVersion}-x64
SetupIconFile=..\..\src\NoirMediaPlayer\Assets\NoirMediaPlayer.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "libvlc\win-arm64\*,libvlc\win-x86\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Replace the complete VLC runtime as a unit so older modules and data cannot
; survive an upgrade. Setup closes NOIR first; settings and AACS keys live outside
; this runtime directory. The previous tested runtime is backed up separately.
Type: filesandordirs; Name: "{app}\libvlc\win-x64"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
