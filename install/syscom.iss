; SYSCOM installer (Inno Setup 7, https://jrsoftware.org/isinfo.php). Built by build\package.ps1:
;   ISCC.exe /DAppVersion=0.18.0 /DPackageDir=<staged package folder> /O<output folder> install\syscom.iss
;
; Does what install.ps1 does, with a normal Windows setup wizard: copies SYSCOM (with its private .NET runtime) to
; Program Files, installs or updates the PawnIO driver when the bundled one is newer, registers the "SYSCOM" service
; (starts at boot, restarts on failure), allows it through the firewall on private networks, starts the tray app at
; every logon (with administrator rights, to manage updates) and adds Start menu entries. Running a newer setup over
; an installed SYSCOM (by this setup or by install.ps1) upgrades it in place; settings and dashboards are kept.
; Uninstall from Settings > Apps > Installed apps.

#ifndef AppVersion
  #error Pass the version: ISCC /DAppVersion=x.y.z
#endif
#ifndef PackageDir
  #error Pass the staged package folder: ISCC /DPackageDir=...
#endif

#define PawnIOVersionFile PackageDir + "\drivers\PawnIO.version"
#if FileExists(PawnIOVersionFile)
  #define PawnIOFile FileOpen(PawnIOVersionFile)
  #define PawnIOVersion Trim(FileRead(PawnIOFile))
  #expr FileClose(PawnIOFile)
#else
  #define PawnIOVersion ""
#endif

[Setup]
AppId={{6B0E9B0C-5E1F-4C1D-9D57-6A3C2F1E8B47}
AppName=SYSCOM
AppVersion={#AppVersion}
AppVerName=SYSCOM {#AppVersion}
AppPublisher=SYSCOM contributors
AppPublisherURL=https://github.com/rotate4all/syscom
AppSupportURL=https://github.com/rotate4all/syscom/issues
AppUpdatesURL=https://github.com/rotate4all/syscom/releases
AppComments=Hardware monitor for your network: live sensors, dashboards on any phone or PC.
VersionInfoVersion={#AppVersion}
VersionInfoDescription=SYSCOM setup
DefaultDirName={autopf}\SYSCOM
DisableDirPage=auto
DisableProgramGroupPage=yes
DisableReadyPage=no
PrivilegesRequired=admin
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
WizardSizePercent=110
SetupIconFile=..\src\Syscom.Tray\syscom.ico
UninstallDisplayIcon={app}\syscom-tray.exe
UninstallDisplayName=SYSCOM
OutputBaseFilename=SYSCOM-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
; SYSCOM's own processes are stopped by the code below (the service needs a proper stop, not a close request).
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Messages]
WelcomeLabel2=This installs SYSCOM {#AppVersion} on your computer.%n%nSYSCOM reads this PC's hardware sensors (temperatures, load, clocks, fans, frame rate…) and shows them on a dashboard you open from any phone, tablet or PC on your network.%n%nIt runs as a Windows service, with a tray icon to manage it.

[Tasks]
Name: pawnio; Description: "Install or update the PawnIO driver ({#PawnIOVersion}) for CPU temperatures and motherboard sensors"; Check: PawnIONeeded
Name: tray; Description: "Start the SYSCOM tray icon when I sign in (health, updates, phone setup)"

[Files]
Source: "{#PackageDir}\*"; DestDir: "{app}"; Excludes: "\drivers\*,\install.ps1,\uninstall.ps1,\README.txt"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDir}\drivers\*"; DestDir: "{tmp}\drivers"; Flags: ignoreversion deleteafterinstall; Tasks: pawnio

[InstallDelete]
; Left behind by install.ps1 installs (this setup's uninstaller replaces it) and by in-app updates.
Type: files; Name: "{app}\uninstall.ps1"
Type: files; Name: "{app}\*.syscom-old"
Type: files; Name: "{app}\runtime\*.syscom-old"

[UninstallDelete]
; In-app updates add and replace files this setup didn't install; remove the whole folder.
Type: filesandordirs; Name: "{app}"

[Icons]
Name: "{autoprograms}\SYSCOM\SYSCOM"; Filename: "{app}\syscom-tray.exe"; Parameters: "--open"; WorkingDir: "{app}"; Comment: "Manage SYSCOM: health, updates and phone setup"
Name: "{autoprograms}\SYSCOM\SYSCOM dashboard"; Filename: "http://localhost:47810/"; IconFilename: "{app}\syscom-tray.exe"

[Run]
; The tray app requires administrator rights (its manifest), and post-install entries run without them unless told
; otherwise ("runasoriginaluser" is their default), which fails with error 740. Setup is elevated: run it as that.
Filename: "{app}\syscom-tray.exe"; Parameters: "--open"; Description: "Open SYSCOM (dashboard address, password, phone setup)"; Flags: postinstall nowait skipifsilent runascurrentuser
Filename: "http://localhost:47810/"; Description: "Open the dashboard in the browser"; Flags: postinstall shellexec nowait skipifsilent unchecked runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM syscom-tray.exe"; Flags: runhidden waituntilterminated; RunOnceId: "StopTray"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""SYSCOM Tray"" /F"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteTask"
Filename: "{sys}\net.exe"; Parameters: "stop SYSCOM"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM syscom.exe"; Flags: runhidden waituntilterminated; RunOnceId: "KillServer"
Filename: "{sys}\sc.exe"; Parameters: "delete SYSCOM"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""SYSCOM dashboard"""; Flags: runhidden waituntilterminated; RunOnceId: "Firewall1"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""SYSCOM dashboard (HTTPS)"""; Flags: runhidden waituntilterminated; RunOnceId: "Firewall2"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""SYSCOM discovery"""; Flags: runhidden waituntilterminated; RunOnceId: "Firewall3"

[Code]
const
  ServiceName = 'SYSCOM';
  TaskName = 'SYSCOM Tray';

{ Runs a program hidden and waits; logs the exit code, and its output when it failed (setup log, /LOG). }
function Run(const Exe, Params: String): Integer;
var
  Code, I: Integer;
  Output: TExecOutput;
begin
  if not ExecAndCaptureOutput(Exe, Params, '', SW_HIDE, ewWaitUntilTerminated, Code, Output) then Code := -1;
  Log(Format('%s %s -> %d', [Exe, Params, Code]));
  if Code <> 0 then
  begin
    for I := 0 to GetArrayLength(Output.StdOut) - 1 do
      if Trim(Output.StdOut[I]) <> '' then Log('    ' + Output.StdOut[I]);
    for I := 0 to GetArrayLength(Output.StdErr) - 1 do
      if Trim(Output.StdErr[I]) <> '' then Log('    ' + Output.StdErr[I]);
  end;
  Result := Code;
end;

function Sys(const Name: String): String;
begin
  Result := ExpandConstant('{sys}\' + Name);
end;

{ Whether a program with that file name is running. }
function Running(const Image: String): Boolean;
begin
  Result := Run(ExpandConstant('{cmd}'), '/c ' + Sys('tasklist.exe') + ' /NH /FI "IMAGENAME eq ' + Image + '" | ' + Sys('find.exe') + ' /I "' + Image + '" >nul') = 0;
end;

function ServiceExists: Boolean;
begin
  Result := Run(Sys('sc.exe'), 'query ' + ServiceName) <> 1060;   { 1060: no such service }
end;

{ The bundled PawnIO is offered when PawnIO is missing or older. }
function PawnIONeeded: Boolean;
var
  Installed: String;
  Have, Bundled: Int64;
begin
  Result := False;
  if '{#PawnIOVersion}' = '' then Exit;
  if not RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO', 'DisplayVersion', Installed) then
    Result := True
  else if StrToVersion(Installed, Have) and StrToVersion('{#PawnIOVersion}', Bundled) then
    Result := ComparePackedVersion(Bundled, Have) > 0;
end;

{ Stops the tray app, the service and anything still running from the install folder, so every file can be replaced. }
procedure StopSyscom;
var
  I: Integer;
begin
  Run(Sys('taskkill.exe'), '/F /IM syscom-tray.exe');
  if ServiceExists then Run(Sys('net.exe'), 'stop ' + ServiceName);
  { "Stopped" comes before the process is gone (a crashing one lingers while Windows writes its dump), and until
    then its files can't be replaced. }
  for I := 1 to 30 do
  begin
    if not Running('syscom.exe') then Break;
    Sleep(1000);
  end;
  Run(Sys('taskkill.exe'), '/F /IM syscom.exe');
  Run(Sys('taskkill.exe'), '/F /IM syscom-updater.exe');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopSyscom;
  Result := '';
end;

function XmlEscape(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '&', '&amp;', True);
  StringChangeEx(Result, '<', '&lt;', True);
  StringChangeEx(Result, '>', '&gt;', True);
end;

{ The tray app at every logon of an administrator, elevated, on battery too, never stopped: the same task install.ps1
  registers, made the same way (PowerShell's ScheduledTasks module, with the Administrators group by its SID so it
  works in every Windows language). If PowerShell is blocked, schtasks with the equivalent task XML. }
procedure RegisterTrayTask;
var
  Tray, Script, Path: String;
  Xml: TArrayOfString;
begin
  Tray := ExpandConstant('{app}\syscom-tray.exe');
  StringChangeEx(Tray, '''', '''''', True);
  Script :=
    '$ErrorActionPreference = ''Stop''; ' +
    '$admins = (New-Object Security.Principal.SecurityIdentifier ''S-1-5-32-544'').Translate([Security.Principal.NTAccount]).Value; ' +
    '$action = New-ScheduledTaskAction -Execute ''' + Tray + '''; ' +
    '$trigger = New-ScheduledTaskTrigger -AtLogOn; ' +
    '$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit 0; ' +
    '$principal = New-ScheduledTaskPrincipal -GroupId $admins -RunLevel Highest; ' +
    'Register-ScheduledTask -TaskName ''' + TaskName + ''' -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null';
  if Run(Sys('WindowsPowerShell\v1.0\powershell.exe'), '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + Script + '"') = 0 then Exit;

  Path := ExpandConstant('{tmp}\syscom-tray-task.xml');
  SetArrayLength(Xml, 1);
  Xml[0] :=
    '<?xml version="1.0" encoding="UTF-8"?>' + #13#10 +
    '<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">' +
    '<Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>' +
    '<Principals><Principal id="Author"><GroupId>S-1-5-32-544</GroupId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>' +
    '<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>' +
    '<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit></Settings>' +
    '<Actions Context="Author"><Exec><Command>' + XmlEscape(ExpandConstant('{app}\syscom-tray.exe')) + '</Command></Exec></Actions>' +
    '</Task>';
  SaveStringsToUTF8FileWithoutBOM(Path, Xml, False);
  if Run(Sys('schtasks.exe'), '/Create /TN "' + TaskName + '" /XML "' + Path + '" /F') <> 0 then
    Log('Could not register the tray task; the tray app can still be opened from the Start menu.');
end;

procedure SetUpService;
var
  Exe: String;
begin
  Exe := ExpandConstant('{app}\syscom.exe');
  if not ServiceExists then
    Run(Sys('sc.exe'), 'create ' + ServiceName + ' binPath= "\"' + Exe + '\"" start= auto DisplayName= "SYSCOM System Companion"')
  else
    Run(Sys('sc.exe'), 'config ' + ServiceName + ' binPath= "\"' + Exe + '\"" start= auto');
  Run(Sys('sc.exe'), 'description ' + ServiceName + ' "Streams this PC''s hardware sensors to the SYSCOM web dashboard on your network."');
  { Restart automatically if it ever crashes. }
  Run(Sys('sc.exe'), 'failure ' + ServiceName + ' reset= 86400 actions= restart/5000/restart/10000/restart/30000');
end;

{ Lets SYSCOM's dashboard (TCP) and LAN discovery (UDP) through, on private networks only. }
procedure SetUpFirewall;
var
  Exe: String;
begin
  Exe := ExpandConstant('{app}\syscom.exe');
  { Older installs made one rule per port, and a run from source may have added its own. }
  Run(Sys('netsh.exe'), 'advfirewall firewall delete rule name="SYSCOM dashboard"');
  Run(Sys('netsh.exe'), 'advfirewall firewall delete rule name="SYSCOM dashboard (HTTPS)"');
  Run(Sys('netsh.exe'), 'advfirewall firewall delete rule name="SYSCOM discovery"');
  Run(Sys('netsh.exe'), 'advfirewall firewall delete rule name="SYSCOM dev"');
  Run(Sys('netsh.exe'), 'advfirewall firewall delete rule name="SYSCOM dev discovery"');
  Run(Sys('netsh.exe'), 'advfirewall firewall add rule name="SYSCOM dashboard" dir=in action=allow protocol=TCP profile=private program="' + Exe + '" enable=yes');
  Run(Sys('netsh.exe'), 'advfirewall firewall add rule name="SYSCOM discovery" dir=in action=allow protocol=UDP profile=private program="' + Exe + '" enable=yes');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    { The driver goes in before the service starts: the sensors library looks for it once, at start. }
    if WizardIsTaskSelected('pawnio') then
    begin
      WizardForm.StatusLabel.Caption := 'Installing the PawnIO driver…';
      if Run(ExpandConstant('{tmp}\drivers\PawnIO_setup.exe'), '-install -silent') <> 0 then
        Log('PawnIO setup failed; CPU temperatures may be missing. It can be updated later from the tray app.');
    end;
    WizardForm.StatusLabel.Caption := 'Setting up the SYSCOM service…';
    SetUpService;
    SetUpFirewall;
    if WizardIsTaskSelected('tray') then RegisterTrayTask
    else Run(Sys('schtasks.exe'), '/Delete /TN "' + TaskName + '" /F');
    WizardForm.StatusLabel.Caption := 'Starting SYSCOM…';
    Run(Sys('net.exe'), 'start ' + ServiceName);
    if WizardIsTaskSelected('tray') then Run(Sys('schtasks.exe'), '/Run /TN "' + TaskName + '"');
  end;
end;

{ Settings, dashboards, API keys and history stay unless asked (a reinstall picks them up again). }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Data := ExpandConstant('{commonappdata}\SYSCOM');
    if DirExists(Data) and not UninstallSilent and
       (MsgBox('Also delete SYSCOM''s settings, dashboards, API keys, password and sensor history?' + #13#10 + #13#10 +
               'Keep them to pick up where you left off if you install SYSCOM again.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      DelTree(Data, True, True, True);
  end;
end;
