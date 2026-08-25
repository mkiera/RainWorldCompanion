; RainWorld Companion installer.
;
; Wraps a self-contained .NET publish, which is a folder of roughly 250 files rather than one
; exe, so a folder is what has to be delivered. Self-contained because the alternative asks
; someone who wants to back up their Rain World saves to install a .NET runtime first, and that
; is where most of them would stop.
;
; Per-user by design: PrivilegesRequired=lowest means Windows never shows a UAC prompt. That is
; not only politeness. It is the reason the app can update itself silently later, because a
; silent installer has nobody present to answer an elevation dialog.
;
; Compile with (both defines optional, see the fallbacks below):
;   iscc /DAppVersion=1.1.0-beta.1 /DVersionNumeric=1.1.0.0 installer.iss
; The output is dist_installer\RainWorldCompanion-Setup.exe.

#define AppName "RainWorld Companion"
#define AppPublisher "mkiera"
#define AppURL "https://github.com/mkiera/RainWorldCompanion"
#define AppExeName "RainWorldCompanion.exe"

; Where `dotnet publish -o publish` left the self-contained output. Relative paths resolve
; against this script's directory.
#ifndef AppSourceDir
  #define AppSourceDir "publish"
#endif

; A release passes both of these in from the git tag. The fallbacks exist so a hand-run iscc
; still produces something rather than failing; 0.0.0.0 is Inno Setup's own default.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef VersionNumeric
  #define VersionNumeric "0.0.0.0"
#endif

[Setup]
; This GUID is what identifies the installed product to Windows. It must NEVER change: a new
; AppId makes Windows treat the next release as a different program, so it installs alongside
; the old one instead of replacing it, and the stale entry sits in Add/Remove Programs forever.
AppId={{EE2030A6-26E6-4E19-AF9F-A579A3A145B3}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases

; Per-user install, no elevation. {localappdata}\Programs is where a non-administrative install
; belongs, and writing there needs no UAC prompt.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\RainWorldCompanion
DefaultGroupName={#AppName}

; Every page that is not asking a real question is off. What is left is the single "additional
; tasks" page holding the desktop-shortcut checkbox, and its button reads Install.
DisableStartupPrompt=yes
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableFinishedPage=yes

; Both are Inno Setup defaults, spelled out because the in-app updater depends on them and
; deliberately passes neither /DIR nor /TASKS. Turning either off would move a silently updated
; app to the default directory and reset the user's desktop-shortcut choice.
UsePreviousAppDir=yes
UsePreviousTasks=yes

; Close a running copy before overwriting it. The updater exits first and passes
; /CLOSEAPPLICATIONS as well, but losing that race would leave files locked. The default filter
; (*.exe, *.dll) already covers a running .NET app, which holds its own exe and every loaded
; assembly mapped, so there is nothing to add to it here.
CloseApplications=yes
; Relaunching the app is the [Run] entry's job and only its job. Letting the Restart Manager put
; it back too would start a second copy, which this app's single-instance check would then refuse
; with a message about it already running, mid-update. The updater passes
; /NORESTARTAPPLICATIONS for the same reason; this makes it true for interactive installs too.
RestartApplications=no

Uninstallable=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\app\{#AppExeName}

OutputDir=dist_installer
OutputBaseFilename=RainWorldCompanion-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; No icon exists in the repo yet. Named conditionally rather than left out, so dropping the file
; in is the whole of adding one: a setup exe with the generic icon is one more thing for an
; antivirus heuristic to notice on top of being unsigned.
#if FileExists("src\RainWorldCompanion\app.ico")
SetupIconFile=src\RainWorldCompanion\app.ico
#endif

; The setup exe is the file people download, so it carries a version resource of its own for the
; same reason the app exe does: an executable with no company, product or description reads as
; suspicious to antivirus heuristics.
VersionInfoVersion={#VersionNumeric}
VersionInfoProductVersion={#VersionNumeric}
VersionInfoTextVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
VersionInfoCompany={#AppPublisher}
VersionInfoCopyright=Copyright (C) 2026 mkiera

; No ArchitecturesAllowed or ArchitecturesInstallIn64BitMode on purpose: nothing lands under
; Program Files, so there is no WOW64 redirection to opt out of, and the accepted spelling of the
; 64-bit value changed in Inno Setup 6.3, so pinning one would break whichever version the build
; machine happens to have.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

; No [InstallDelete] section on purpose. Clearing the previous build is still necessary, but
; those entries do it at a moment that cannot be undone. See [Code] below.

[Files]
; The whole publish, into {app}\app rather than {app} itself. One level down means the entire
; payload is a single directory that [Code] can park with one rename instead of moving 250 files,
; and it keeps unins000.exe out of the way of that swap. ignoreversion because every file here is
; ours and a shared runtime assembly's version says nothing about which build it arrived with.
Source: "{#AppSourceDir}\*"; DestDir: "{app}\app"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\app\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\app\{#AppExeName}"; Tasks: desktopicon

[Run]
; Deliberately neither "postinstall" nor "skipifsilent". A postinstall entry is a checkbox on the
; Setup Completed page, which is disabled here, and skipifsilent would leave a silent update with
; no app running at all: the updater exits before Setup starts and expects Setup to be what
; brings the app back.
Filename: "{app}\app\{#AppExeName}"; StatusMsg: "Starting {#AppName}..."; Flags: nowait

[UninstallDelete]
; Files, named one at a time, and never the folder holding them.
;
; %LOCALAPPDATA%\RainWorldCompanion is where the backups and the save library live by default,
; and those are the reason this program exists. They are also irreplaceable: a Rain World save
; that has been overwritten is gone, and a backup of one is the only copy left. So nothing here
; names that folder, and nothing here asks the user whether to delete it either, because a
; misread question at uninstall time destroys the same data as a wrong answer.
;
; The user can also point either root somewhere else entirely from the settings dialog, which
; means an uninstaller cannot reliably find them anyway. Reading settings.json to hunt for them
; would only turn "leaves data behind" into "deletes a folder the user chose", which is worse.
;
; What goes is what this program made for itself and nothing else: its own settings, the
; half-written temp file a crash during a save can leave beside them, and the cache of downloaded
; installers. An empty folder left behind is the safe direction to be wrong in.
Type: filesandordirs; Name: "{localappdata}\RainWorldCompanion\updates"
Type: files; Name: "{localappdata}\RainWorldCompanion\settings.json"
Type: files; Name: "{localappdata}\RainWorldCompanion\settings.json.tmp"
; Exists only when an upgrade died midway and [Code] could not put it back. It is ours, we named
; it, and removing it is also what lets {app} itself disappear.
Type: filesandordirs; Name: "{app}\app.old"

[Code]
// Comments in here are // rather than { }, because a brace comment ends at the first closing
// brace and half the things worth naming below are constants like {app}. One of those in a
// comment would silently truncate it.
//
// Upgrades: move the old payload aside instead of deleting it.
//
// A self-contained .NET publish is a tree of roughly 250 files, and which files are in it changes
// between releases as the runtime and the dependencies move. An upgrade that copies over the top
// therefore leaves the previous build's orphans behind. Clearing the tree first is right, but the
// [InstallDelete] entry that would do it is the wrong tool: those entries are processed as the
// first step of installation, before a single new file has been copied. Setup does undo a failed
// or cancelled install, rolling back through the same log the uninstaller uses, but a rollback
// can only remove what Setup added. Nothing brings back what the delete already destroyed. So a
// copy that died halfway, from a full disk or from antivirus quarantining a file, would leave the
// user with no working app at all, just a shortcut pointing at nothing.
//
// A rename costs nothing and can be reversed, so the old tree is parked under another name before
// the copy, deleted only once the install is past the point where it can fail, and moved back if
// it never got there. The one real cost is that both trees exist at once, so an upgrade briefly
// wants roughly 275 MB free rather than 136 MB. For a manager of a game whose own install is
// larger than that, it is a fair price for never destroying a working install to save a rename.

var
  // Set while an old payload is parked under another name, and cleared again once the install can
  // no longer fail. Still set when Setup terminates therefore means the install never completed.
  StalePayloadDir: String;
  // Where the tree belongs, remembered so the restore below never has to expand {app} again at a
  // point where the wizard is already tearing itself down.
  LivePayloadDir: String;

procedure CurStepChanged(CurStep: TSetupStep);
var
  BackupDir: String;
begin
  if CurStep = ssInstall then begin
    // ssInstall runs before any file is copied and after CloseApplications has already shut the
    // running app down, so nothing of ours should still hold the tree open.
    LivePayloadDir := ExpandConstant('{app}\app');
    BackupDir := ExpandConstant('{app}\app.old');
    // A backup already sitting here is from an earlier attempt that died before it could tidy up,
    // and it is 136 MB of it. Whatever it holds is older than anything this run will produce, so
    // it goes either way, including when the live tree is missing entirely: that is the case
    // where the earlier attempt could not even put it back and this run is the repair.
    if DirExists(BackupDir) then
      DelTree(BackupDir, True, True, True);
    if DirExists(LivePayloadDir) then begin
      if RenameFile(LivePayloadDir, BackupDir) then
        StalePayloadDir := BackupDir
      else
        // Locked anyway, or {app} somehow straddles a volume boundary. Fall through and let the
        // file copy overwrite in place: that can leave orphans, which is much better than
        // refusing to install at all.
        Log('Could not move the previous payload aside; installing over it.');
    end;
  end else if CurStep = ssPostInstall then begin
    // Setup can no longer fail or be cancelled once it reaches here, so the old tree is finally
    // safe to drop. Clearing the variable is also the signal to DeinitializeSetup that there is
    // nothing left to undo.
    if StalePayloadDir <> '' then begin
      DelTree(StalePayloadDir, True, True, True);
      StalePayloadDir := '';
    end;
  end;
end;

procedure DeinitializeSetup();
begin
  // Arriving here with the variable still set means ssPostInstall never ran, so the install
  // failed or was cancelled. Setup's own rollback finished long before this point and took the
  // half-copied files with it, so moving the old tree back leaves the user on the version they
  // started with rather than on nothing at all.
  if StalePayloadDir <> '' then begin
    Log('Install did not complete; restoring the previous payload.');
    DelTree(LivePayloadDir, True, True, True);
    if not RenameFile(StalePayloadDir, LivePayloadDir) then
      Log('Could not restore the payload; it is still on disk as app.old.');
  end;
end;
