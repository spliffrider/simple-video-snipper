#ifndef Profile
  #define Profile "SimpleVideoSnipper"
#endif
#ifndef DisplayName
  #define DisplayName "Simple Video Snipper"
#endif
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={#Profile}
AppName={#DisplayName}
AppVersion={#AppVersion}
AppPublisher=Simple Video Snipper
AppPublisherURL=https://github.com/spliffrider/simple-video-snipper
DefaultDirName={localappdata}\Programs\{#Profile}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19045
AppMutex=Local\{#Profile}.Running
SetupMutex=Local\{#Profile}.Setup
CloseApplications=no
UninstallDisplayIcon={app}\SimpleVideoSnipper.exe
OutputBaseFilename={#Profile}-{#AppVersion}-win-x64-setup
VersionInfoProductName={#DisplayName}
VersionInfoDescription={#DisplayName} Setup
VersionInfoProductVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs; Excludes: "*.pdb"
Source: "{#MediaDir}\*"; DestDir: "{app}\ffmpeg"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{userprograms}\{#DisplayName}"; Filename: "{app}\SimpleVideoSnipper.exe"

[Registry]
Root: HKCU; Subkey: "Software\{#Profile}"; ValueType: string; ValueName: "TemporaryRecordingsFolder"; ValueData: "{code:GetRecordingsFolder}"; Check: Not HasStorageFolder

[Run]
Filename: "{app}\SimpleVideoSnipper.exe"; Description: "Open {#DisplayName}"; Flags: nowait postinstall skipifsilent

[Code]
var
  StoragePage: TInputDirWizardPage;
  ExistingFolder: String;
  SelectedFolder: String;

function GetDriveType(Root: String): LongWord;
  external 'GetDriveTypeW@kernel32.dll stdcall';
function GetTempFileName(Directory, Prefix: String; Unique: LongWord; FileName: String): LongWord;
  external 'GetTempFileNameW@kernel32.dll stdcall';

function DefaultRecordingsFolder: String;
begin
  Result := ExpandConstant('{localappdata}\{#Profile}\Recordings');
end;

function HasStorageFolder: Boolean;
begin
  Result := ExistingFolder <> '';
end;

procedure InitializeWizard;
var
  InitialFolder: String;
begin
  RegQueryStringValue(HKCU, 'Software\{#Profile}', 'TemporaryRecordingsFolder', ExistingFolder);
  ExistingFolder := Trim(ExistingFolder);
  InitialFolder := Trim(ExpandConstant('{param:RECORDINGSFOLDER|}'));
  if InitialFolder = '' then
    InitialFolder := DefaultRecordingsFolder;
  StoragePage := CreateInputDirPage(wpSelectDir, 'Temporary recordings folder',
    'Choose where the app holds recordings before saving',
    'This is where recordings are held until you save or discard them.', False, '');
  StoragePage.Add('');
  StoragePage.Values[0] := InitialFolder;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = StoragePage.ID) and HasStorageFolder;
end;

function GetRecordingsFolder(Param: String): String;
begin
  Result := SelectedFolder;
end;

function ValidateStorage: String;
var
  Folder, Pending, Drive, Probe: String;
  DriveType: LongWord;
  Free, Total: Int64;
  Wrote, Removed: Boolean;
begin
  Folder := Trim(StoragePage.Values[0]);
  Drive := ExtractFileDrive(Folder);
  Result := 'Choose a full folder path on an available local drive.';
  if (Length(Drive) <> 2) or (Copy(Folder, 3, 1) <> '\') then
    Exit;
  DriveType := GetDriveType(Drive + '\');
  if (DriveType <> 2) and (DriveType <> 3) and (DriveType <> 6) then
    Exit;
  Folder := RemoveBackslashUnlessRoot(ExpandFileName(Folder));
  if CompareText(Folder, DefaultRecordingsFolder) = 0 then
    Pending := AddBackslash(Folder) + 'Pending'
  else
    Pending := AddBackslash(Folder) + 'Simple Video Snipper Pending';

  Result := 'Could not create the temporary recordings folder. Choose another folder.';
  if not ForceDirectories(Pending) then
    Exit;
  Result := 'The temporary recordings folder needs at least 10 MB of free space.';
  if not GetSpaceOnDisk64(Pending, Free, Total) or (Free < 10485760) then
    Exit;
  Result := 'Could not write to the temporary recordings folder and remove the test file. Choose another folder.';
  SetLength(Probe, 260);
  if GetTempFileName(Pending, 'svs', 0, Probe) = 0 then
    Exit;
  Probe := Copy(Probe, 1, Pos(#0, Probe) - 1);
  Wrote := SaveStringToFile(Probe, 'Simple Video Snipper storage check', False);
  Removed := DeleteFile(Probe);
  if not Wrote or not Removed then
    Exit;

  SelectedFolder := Folder;
  Result := '';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Failure: String;
begin
  Result := True;
  if (CurPageID <> StoragePage.ID) or HasStorageFolder or WizardSilent then
    Exit;
  Failure := ValidateStorage;
  Result := Failure = '';
  if not Result then
    SuppressibleMsgBox(Failure, mbError, MB_OK, IDOK);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not HasStorageFolder then
    Result := ValidateStorage;
  if Result <> '' then
    Log(Result);
end;
