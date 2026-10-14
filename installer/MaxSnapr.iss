; Instalador de MaxSnapr (Inno Setup 6).
; Compilar: iscc /DAppVersion=1.0.0 /DSourceExe=..\artifacts\publish\MaxSnapr.exe installer\MaxSnapr.iss
; Instala para el usuario actual, sin permisos de administrador, y al desinstalar no toca las capturas.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\artifacts\publish\MaxSnapr.exe"
#endif

[Setup]
AppId={{84020F9C-C54C-48DE-AEB5-908C6D568017}
AppName=MaxSnapr
AppVersion={#AppVersion}
AppVerName=MaxSnapr {#AppVersion}
AppPublisher=danfigi
AppPublisherURL=https://github.com/danfigi/MaxSnapr
AppSupportURL=https://github.com/danfigi/MaxSnapr/issues
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Instalador de MaxSnapr
DefaultDirName={autopf}\MaxSnapr
DefaultGroupName=MaxSnapr
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\artifacts
OutputBaseFilename=MaxSnapr-Setup-{#AppVersion}
SetupIconFile=..\src\MaxSnapr\Assets\MaxSnapr.ico
UninstallDisplayIcon={app}\MaxSnapr.exe
UninstallDisplayName=MaxSnapr
WizardStyle=modern
; Licence of use shown before installing (the user has to accept it).
LicenseFile=..\LICENCIA.txt
Compression=lzma2/max
SolidCompression=yes
; Ask to close a running MaxSnapr before replacing it.
AppMutex=MaxSnapr.SingleInstance
CloseApplications=yes

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENCIA.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\TERCEROS.md"; DestDir: "{app}\licencias"; Flags: ignoreversion
Source: "..\src\MaxSnapr\Assets\Models\LICENSE-pphumanseg.txt"; DestDir: "{app}\licencias"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\MaxSnapr"; Filename: "{app}\MaxSnapr.exe"
Name: "{autodesktop}\MaxSnapr"; Filename: "{app}\MaxSnapr.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MaxSnapr.exe"; Description: "{cm:LaunchProgram,MaxSnapr}"; Flags: nowait postinstall skipifsilent

[Code]
// The app adds these per-user entries only when the user turns the options on, so the installer
// never sees them: remove them explicitly. Settings, captures and recordings are kept.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Prog: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MaxSnapr');
    if RegQueryStringValue(HKCU, 'Software\Classes\.snapr', '', Prog) and (Prog = 'MaxSnapr.Document') then
      RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\.snapr');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\MaxSnapr.Document');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\SystemFileAssociations\image\shell\MaxSnaprEdit');
  end;
end;
