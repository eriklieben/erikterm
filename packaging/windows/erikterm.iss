; A per-user installer: everything goes in the user's profile and HKCU, so it needs no administrator rights.
; Built in CI:  iscc /DAppVersion=0.1.0 /DSourceDir=out\erikterm /DOutputDir=. packaging\windows\erikterm.iss

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\out\erikterm"
#endif
#ifndef OutputDir
  #define OutputDir "..\.."
#endif

[Setup]
AppId={{7E1B4C52-3A9D-4F7E-9C21-6D0A5B8E4F13}
AppName=Erik's Terminal
AppVersion={#AppVersion}
AppPublisher=Erik Lieben
DefaultDirName={localappdata}\Programs\erikterm
DefaultGroupName=Erik's Terminal
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
ChangesEnvironment=yes
OutputDir={#OutputDir}
OutputBaseFilename=erikterm-win-x64-setup
SetupIconFile=..\..\assets\icon\erikterm.ico
UninstallDisplayIcon={app}\erikterm.exe
UninstallDisplayName=Erik's Terminal
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Erik's Terminal"; Filename: "{app}\erikterm.exe"
Name: "{autodesktop}\Erik's Terminal"; Filename: "{app}\erikterm.exe"; Tasks: desktopicon

[Registry]
; The per-user PATH, so `erikterm` works from any new shell.
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Check: NeedsPathEntry(ExpandConstant('{app}'))

[Run]
Filename: "{app}\erikterm.exe"; Description: "Open Erik's Terminal"; Flags: nowait postinstall skipifsilent

[Code]
function PathHasEntry(const Path, Entry: string): Boolean;
begin
  Result := Pos(';' + Uppercase(Entry) + ';', ';' + Uppercase(Path) + ';') > 0;
end;

function NeedsPathEntry(Entry: string): Boolean;
var
  Path: string;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Path) then
    Path := '';
  Result := not PathHasEntry(Path, Entry);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Path, Entry: string;
  Index: Integer;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Path) then
    Exit;
  Entry := ExpandConstant('{app}');
  Index := Pos(';' + Uppercase(Entry) + ';', ';' + Uppercase(Path) + ';');
  if Index = 0 then
    Exit;
  Delete(Path, Index, Length(Entry) + 1);
  if (Length(Path) > 0) and (Path[Length(Path)] = ';') then
    Delete(Path, Length(Path), 1);
  RegWriteExpandStringValue(HKCU, 'Environment', 'Path', Path);
end;
