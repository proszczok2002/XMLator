using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;

namespace Schneegans.Unattend;

public interface ITargetDiskSettings;

public record class InteractiveTargetDiskSettings : ITargetDiskSettings;

public record class ScriptTargetDiskSettings(
  string Script
) : ITargetDiskSettings;

public record class FixedTargetDiskSettings(
  int Index
) : ITargetDiskSettings;

public class GeneratedTargetDiskSettings : ITargetDiskSettings
{
  public GeneratedTargetDiskSettings(
    int? minSizeGiB = Constants.TargetDiskMinSizeGiB,
    int? maxSizeGiB = Constants.TargetDiskMaxSizeGiB,
    int? index = 0,
    bool assertNoPartitions = true,
    bool assertInterfaceType = false,
    bool assertMediaType = false
  )
  {
    if (minSizeGiB != null && maxSizeGiB != null && minSizeGiB > maxSizeGiB)
    {
      throw new ConfigurationException($"Value of '{nameof(minSizeGiB)}' ({minSizeGiB}) must be less than or equal to value of '{nameof(maxSizeGiB)}' ({maxSizeGiB}).");
    }

    MinSizeGiB = minSizeGiB;
    MaxSizeGiB = maxSizeGiB;
    Index = index;
    AssertNoPartitions = assertNoPartitions;
    AssertInterfaceType = assertInterfaceType;
    AssertMediaType = assertMediaType;
  }

  public int? MinSizeGiB { get; }

  public int? MaxSizeGiB { get; }

  public int? Index { get; }

  public bool AssertNoPartitions { get; }

  public bool AssertInterfaceType { get; }

  public bool AssertMediaType { get; }
}

public interface IPartitionSettings;

public record class CustomPartitionSettings(
  string Script
) : IPartitionSettings;

public record class UnattendedPartitionSettings(
  ITargetDiskSettings TargetDiskSettings,
  PartitionLayout PartitionLayout,
  RecoveryMode RecoveryMode,
  int SystemSize = Constants.SystemPartitionSize,
  int RecoverySize = Constants.RecoveryPartitionSize,
  int WindowsSize = 0,
  bool SinglePartition = false
) : IPartitionSettings;

public class InteractivePartitionSettings : IPartitionSettings;

public interface IInstallFromSettings;

public record class EditionInstallFromSettings(
  WindowsEdition Edition
) : IInstallFromSettings;

public record class EditionMetadataInstallFromSettings(
  string EditionId,
  string Architecture,
  int MinimumBuild,
  int MaximumBuild
) : IInstallFromSettings;

public record class IndexInstallFromSettings(
  int Index
) : IInstallFromSettings;

public record class NameInstallFromSettings(
  string Name
) : IInstallFromSettings;

public class InteractiveInstallFromSettings : IInstallFromSettings;

public interface IPESettings;

public record class DefaultPESettings(
  IEditionSettings EditionSettings,
  bool BypassRequirementsCheck,
  bool UseConfigurationSet
) : IPESettings;

public interface ICmdPESettings : IPESettings;

public record class GeneratePESettings(
  IPartitionSettings PartitionSettings,
  IInstallFromSettings InstallFromSettings,
  IPagingFileSettings PagingFileSettings,
  bool DisableDefender,
  bool Disable8Dot3Names,
  bool PauseBeforeFormatting,
  bool PauseBeforeReboot,
  bool CompactOs,
  bool SkipIntegrityCheck
) : ICmdPESettings;

public record class ScriptPESetttings(
  string Script
) : ICmdPESettings;

public interface IPagingFileSettings;

public class AutomaticPagingFileSettings : IPagingFileSettings;

public record class CustomPagingFileSettings(
  int InitialSizeMiB,
  int MaxSizeMiB
) : IPagingFileSettings;

public class NoPagingFileSettings : IPagingFileSettings;

static class Paths
{
  internal const string PEScript = @"X:\pe.cmd";
  internal const string DiskpartScript = @"X:\diskpart.txt";
  internal const string TargetDiskScript = @"X:\target.vbs";
  internal const string TargetDiskOutput = @"X:\target.out";
}

static class DriveLetters
{
  internal const char System = 'S';
  internal const char Windows = 'W';
  internal const char Recovery = 'R';
  internal const char Data = 'V';
}

/// <summary>
/// A file that is created from within the pe.cmd script.
/// </summary>
record class EmbeddedScript(
  string Path,
  IEnumerable<string> Lines,
  bool Escape
);

class DiskModifier(ModifierContext context) : Modifier(context)
{
  public override void Process()
  {
    if (Configuration.PESettings is DefaultPESettings d && d.UseConfigurationSet)
    {
      Document.SelectSingleNodeOrThrow("//u:UseConfigurationSet", NamespaceManager).InnerText = "true";
    }

    if (Configuration.PESettings is ICmdPESettings)
    {
      {
        string forbidden = Configuration.Components.Where(c => c.Key.Pass == Pass.windowsPE).Select(c => $"‘{c.Key.Component}’").JoinString(", ");
        if (forbidden.Length != 0)
        {
          throw new ConfigurationException($"Cannot create .cmd script with custom components ({forbidden}) for the ‘windowsPE’ pass. To load drivers in the PE stage, add them to the ‘$WinPEDriver$’ folder or use a custom script to run the ‘drvload.exe’ command.");
        }
      }

      foreach (var node in Document.SelectNodesOrEmpty($"/u:unattend/u:settings[@pass='{Pass.windowsPE}']/*", NamespaceManager))
      {
        node.RemoveSelf();
      }

      if (Configuration.UseNarrator)
      {
        GetAppender(CommandConfig.WindowsPE).Append(
          CommandBuilder.ShellCommand(@"start X:\Windows\System32\Narrator.exe")
        );
      }
    }

    List<string> lines = Configuration.PESettings switch
    {
      ScriptPESetttings script => Util.SplitLines(script.Script),
      GeneratePESettings generate => GetPEScript(Configuration, generate, Generator),
      DefaultPESettings => [],
      _ => throw new NotSupportedException(),
    };
    var copy = (XmlElement)Document.SelectSingleNodeOrThrow("//s:PEScriptCopy", NamespaceManager);
    if (lines.Count > 0)
    {
      WritePeScript(lines);
      copy.InsertTextWithIndentation(lines.JoinLines());
    }
    else
    {
      copy.RemoveSelf();
    }
  }

  /// <summary>
  /// Creates the .cmd script that handles the PE stage of Windows Setup instead of setup.exe.
  /// </summary>
  private void WritePeScript(IEnumerable<string> lines)
  {
    CommandAppender appender = GetAppender(CommandConfig.WindowsPE);
    appender.Append([
      ..CommandBuilder.WriteToFilePE(Paths.PEScript, lines),
      CommandBuilder.ShellCommand(Paths.PEScript)
    ]);
  }

  internal static List<string> GetTargetDiskScript(GeneratedTargetDiskSettings g)
  {
    StringWriter writer = new();
    writer.WriteLine($"""
      Function Fail(message)
        WScript.Echo message
        WScript.Quit 1
      End Function

      On Error Resume Next
      Set wmi = GetObject("winmgmts:\\.\root\cimv2")
      Set drives = wmi.InstancesOf("Win32_DiskDrive")
      If Err.Number <> 0 Then
        Fail "Could not enumerate disks: " & Err.Description
      End If
      Set accepted = CreateObject("Scripting.Dictionary")

      For Each drive In drives
        accept = True

      """
    );
    if (g.AssertInterfaceType)
    {
      writer.WriteLine($"""
          actual = drive.InterfaceType
          If actual <> "IDE" And actual <> "SCSI" Then
            accept = False
          End If

        """);
    }
    if (g.AssertMediaType)
    {
      writer.WriteLine($"""
          actual = drive.MediaType
          If actual <> "Fixed hard disk media" Then
            accept = False
          End If

        """);
    }
    if (g.MinSizeGiB != null)
    {
      writer.WriteLine($"""
          actual = CInt(drive.Size / 1024 / 1024 / 1024)
          expected = {g.MinSizeGiB}
          If actual < expected Then
            accept = False
          End If

        """);
    }
    if (g.MaxSizeGiB != null)
    {
      writer.WriteLine($"""
          actual = CInt(drive.Size / 1024 / 1024 / 1024)
          expected = {g.MaxSizeGiB}
          If actual > expected Then
            accept = False
          End If

        """);
    }
    if (g.Index != null)
    {
      writer.WriteLine($"""
          actual = drive.Index
          expected = {g.Index}
          If actual <> expected Then
            accept = False
          End If

        """);
    }
    if (g.AssertNoPartitions)
    {
      writer.WriteLine($"""
          actual = drive.Partitions
          If actual > 0 Then
            accept = False
          End If

        """);
    }
    writer.WriteLine("""
        If accept Then
          accepted.Add drive.Index, ""
        End If
      Next

      If accepted.Count = 0 Then
        Fail "No disk satisfied the given criteria."
      ElseIf accepted.Count > 1 Then
        Fail "Several disks (" & Join(accepted.Keys, ", ") & ") satisfied the given criteria."
      Else
        WScript.Echo Join(accepted.Keys)
        WScript.Quit 0
      End If
      """);

    return Util.SplitLines(writer.ToString());
  }

  internal static List<string> GetDiskpartScript(UnattendedPartitionSettings settings)
  {
    string IfRecovery(string line)
    {
      return settings.RecoveryMode == RecoveryMode.Partition ? line : "";
    }

    string targetDisk = settings.TargetDiskSettings switch
    {
      FixedTargetDiskSettings f => f.Index.ToString(),
      GeneratedTargetDiskSettings or ScriptTargetDiskSettings or InteractiveTargetDiskSettings => "%TARGET_DISK%",
      _ => throw new NotSupportedException(),
    };

    if (settings.SinglePartition)
    {
      return
      [
        $"SELECT DISK={targetDisk}",
        "CLEAN",
        "CONVERT MBR",
        "CREATE PARTITION PRIMARY",
        @"FORMAT QUICK FS=NTFS LABEL=""Windows""",
        $"ASSIGN LETTER={DriveLetters.Windows}",
        "ACTIVE"
      ];
    }

    string windows = settings.WindowsSize > 0 ? $"CREATE PARTITION PRIMARY SIZE={settings.WindowsSize}" : "CREATE PARTITION PRIMARY";
    string recovery = settings.WindowsSize > 0 ? $"CREATE PARTITION PRIMARY SIZE={settings.RecoverySize}" : "CREATE PARTITION PRIMARY";
    return settings.PartitionLayout switch
    {
      PartitionLayout.MBR =>
      [
        $"SELECT DISK={targetDisk}",
        "CLEAN",
        $"CREATE PARTITION PRIMARY SIZE={settings.SystemSize}",
        @"FORMAT QUICK FS=NTFS LABEL=""System""",
        $"ASSIGN LETTER={DriveLetters.System}",
        "ACTIVE",
        "CREATE PARTITION PRIMARY",
        (IfRecovery($"SHRINK MINIMUM={settings.RecoverySize}")),
        @"FORMAT QUICK FS=NTFS LABEL=""Windows""",
        $"ASSIGN LETTER={DriveLetters.Windows}",
        (IfRecovery("CREATE PARTITION PRIMARY")),
        (IfRecovery(@"FORMAT QUICK FS=NTFS LABEL=""Recovery""")),
        (IfRecovery($"ASSIGN LETTER={DriveLetters.Recovery}")),
        (IfRecovery("SET ID=27"))
      ],
      PartitionLayout.GPT =>
      [
        $"SELECT DISK={targetDisk}",
        "CLEAN",
        "CONVERT GPT",
        $"CREATE PARTITION EFI SIZE={settings.SystemSize}",
        @"FORMAT QUICK FS=FAT32 LABEL=""System""",
        $"ASSIGN LETTER={DriveLetters.System}",
        "CREATE PARTITION MSR SIZE=16",
        windows,
        @"FORMAT QUICK FS=NTFS LABEL=""Windows""",
        $"ASSIGN LETTER={DriveLetters.Windows}",
        (IfRecovery(settings.WindowsSize == 0 ? $"SHRINK DESIRED={settings.RecoverySize} MINIMUM={settings.RecoverySize}" : "")),
        (IfRecovery(recovery)),
        (IfRecovery(@"FORMAT QUICK FS=NTFS LABEL=""Recovery""")),
        (IfRecovery($"ASSIGN LETTER={DriveLetters.Recovery}")),
        (IfRecovery(@"SET ID=""de94bba4-06d1-4d40-a16a-bfd50179d6ac""")),
        (IfRecovery("GPT ATTRIBUTES=0x8000000000000001")),
        (settings.WindowsSize > 0 ? "CREATE PARTITION PRIMARY" : ""),
        (settings.WindowsSize > 0 ? @"FORMAT QUICK FS=NTFS LABEL=""Data""" : ""),
        (settings.WindowsSize > 0 ? $"ASSIGN LETTER={DriveLetters.Data}" : "")
      ],
      _ => throw new NotSupportedException()
    };
  }

  internal static List<string> GetPEScript(Configuration configuration, GeneratePESettings pe, UnattendGenerator generator)
  {
    StringWriter writer = new();
    bool singlePartition = pe.PartitionSettings is UnattendedPartitionSettings { SinglePartition: true };
    bool hasDataPartition = pe.PartitionSettings is UnattendedPartitionSettings { WindowsSize: > 0 };
    char bootVolume = singlePartition ? DriveLetters.Windows : DriveLetters.System;

    char[] letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray();
    char[] skippedDrives = ['A', 'B'];

    bool Include(EmbeddedScript script)
    {
      if (script.Lines.Any())
      {
        writer.WriteLine($">{script.Path} (");
        foreach (string line in EchoProcessor.Process(script.Lines, script.Escape))
        {
          writer.WriteLine($"    {line}");
        }
        writer.WriteLine(")");
        writer.WriteLine();
        return true;
      }
      else
      {
        return false;
      }
    }

    writer.WriteLine("""
      @echo off

      """);

    {
      if (configuration.LanguageSettings is UnattendedLanguageSettings settings)
      {
        var pair = settings.LocaleAndKeyboard;
        writer.WriteLine($"""
          call :print "Setting keyboard layout for PE session"
          wpeutil.exe SetKeyboardLayout {pair.Locale.LCID}:{pair.Keyboard.Id}

          """);
      }
    }

    writer.WriteLine($"""
      for %%d in ({letters.Except([.. skippedDrives, DriveLetters.System, DriveLetters.Windows, DriveLetters.Recovery]).JoinString(' ')}) do (
          if exist %%d:\sources\install.wim set "IMAGE_FILE=%%d:\sources\install.wim"
          if exist %%d:\sources\install.esd set "IMAGE_FILE=%%d:\sources\install.esd"
          if exist %%d:\sources\install.swm set "IMAGE_FILE=%%d:\sources\install.swm" & set "SWM_PARAM=/SWMFile:%%d:\sources\install*.swm"
          if exist %%d:\autounattend.xml set "XML_FILE=%%d:\autounattend.xml"
          if exist %%d:\$OEM$ set "OEM_FOLDER=%%d:\$OEM$"
          if exist %%d:\$WinPEDriver$ set "PEDRIVERS_FOLDER=%%d:\$WinPEDriver$"
          if exist %%d:\$WinPEPackage$ set "MSU_FOLDER=%%d:\$WinPEPackage$"
      """);
    if (configuration.VirtIoGuestTools)
    {
      writer.WriteLine("""
        if exist %%d:\virtio-win-guest-tools.exe set "VIRTIO_DRIVE=%%d:"
        """);
    }
    writer.WriteLine("""
      )
      for /f "tokens=3" %%t in ('reg.exe query HKLM\System\Setup /v UnattendFile 2^>nul') do ( if exist %%t set "XML_FILE=%%t" )
      if not defined IMAGE_FILE call :fail "Could not locate install.wim, install.esd or install.swm."
      if not defined XML_FILE call :fail "Could not locate autounattend.xml."
  
      """);

    writer.WriteLine("""
      set "OS_VERSION=11"
      for /f "tokens=3 delims=." %%v in ('ver') do (
          if %%v LSS 20000 set "OS_VERSION=10"
      )

      """);

    writer.WriteLine("""
      if defined PEDRIVERS_FOLDER (
          call :print "Loading drivers from %PEDRIVERS_FOLDER%"
          for /R %PEDRIVERS_FOLDER% %%f IN (*.inf) do drvload.exe "%%f"
      )

      """);

    if (configuration.VirtIoGuestTools)
    {
      writer.WriteLine("""
        if defined VIRTIO_DRIVE (
            call :print "Loading VirtIO drivers from %VIRTIO_DRIVE%"
            drvload.exe "%VIRTIO_DRIVE%\vioscsi\w%OS_VERSION%\%PROCESSOR_ARCHITECTURE%\vioscsi.inf"
            drvload.exe "%VIRTIO_DRIVE%\viostor\w%OS_VERSION%\%PROCESSOR_ARCHITECTURE%\viostor.inf"
            drvload.exe "%VIRTIO_DRIVE%\NetKVM\w%OS_VERSION%\%PROCESSOR_ARCHITECTURE%\netkvm.inf"
        )

        """);
    }

    if (pe.InstallFromSettings is EditionMetadataInstallFromSettings edition)
    {
      writer.WriteLine($$"""
        call :print "Looking for edition {{edition.EditionId}} in the installation image"
        dism.exe /English /Get-WimInfo /WimFile:"%IMAGE_FILE%" > X:\xmlator-catalog.txt || call :fail "Could not read the image catalog."
        set "XMLATOR_INDEX="
        set "XMLATOR_DUPLICATE="
        for /f "tokens=3" %%i in ('findstr.exe /b /r /c:"Index : [0-9][0-9]*$" X:\xmlator-catalog.txt') do (
            dism.exe /English /Get-WimInfo /WimFile:"%IMAGE_FILE%" /Index:%%i > X:\xmlator-image.txt || call :fail "Could not read image %%i."
            findstr.exe /i /x /c:"Edition : {{edition.EditionId}}" X:\xmlator-image.txt >nul && (
                findstr.exe /i /x /c:"Architecture : {{edition.Architecture}}" X:\xmlator-image.txt >nul && (
                    for /f "tokens=5 delims=. " %%b in ('findstr.exe /b /c:"Version : 10.0." X:\xmlator-image.txt') do (
                        if %%b GEQ {{edition.MinimumBuild}} if %%b LEQ {{edition.MaximumBuild}} (
                            if defined XMLATOR_INDEX set "XMLATOR_DUPLICATE=1"
                            set "XMLATOR_INDEX=%%i"
                        )
                    )
                )
            )
        )
        if defined XMLATOR_DUPLICATE call :fail "Several images match edition {{edition.EditionId}}. Specify an index or exact image name."
        if not defined XMLATOR_INDEX call :fail "No image matches edition {{edition.EditionId}} for this Windows version and architecture."
        set "IMG_PARAM=/Index:%XMLATOR_INDEX%"

        """);
    }

    {
      void RunTargetDiskScript(IEnumerable<string> lines)
      {
        Include(new EmbeddedScript(Paths.TargetDiskScript, lines, Escape: true));
        writer.WriteLine($"""
          call :print "Determining target disk"
          (cscript.exe //E:vbscript "{Paths.TargetDiskScript}" //Nologo >{Paths.TargetDiskOutput}) || (type {Paths.TargetDiskOutput} & call :fail "Could not determine target disk. Windows Setup will halt to avoid potential data loss.")
          for /f %%t in ({Paths.TargetDiskOutput}) do set "TARGET_DISK=%%t"

          """);
      }

      if (pe.PartitionSettings is UnattendedPartitionSettings ups)
      {
        switch (ups.TargetDiskSettings)
        {
          case InteractiveTargetDiskSettings:
            writer.WriteLine("""
              echo list disk | diskpart.exe
              echo:
              :choice
              set /p "CHOICE=Enter index of the disk you want to install Windows to: " || goto :choice
              set "TARGET_DISK=%CHOICE%"

              """);
            break;

          case ScriptTargetDiskSettings stds:
            RunTargetDiskScript(Util.SplitLines(stds.Script));
            break;

          case GeneratedTargetDiskSettings gtds:
            RunTargetDiskScript(GetTargetDiskScript(gtds));
            break;

          case FixedTargetDiskSettings:
            break;

          default:
            throw new NotSupportedException();
        }
      }
    }

    {
      void CheckDriveLetterAssignments(IEnumerable<string> lines)
      {
        void CheckDriveLetterAssignment(char letter, string purpose)
        {
          Regex regex = new(@$"^\s*ASSIGN\s+LETTER((\s+)|(\s*=\s*))(({letter})|(""{letter}""))\s*$", RegexOptions.IgnoreCase);
          if (!lines.Any(regex.IsMatch))
          {
            throw new ConfigurationException($"Your diskpart script must contain a line such as ‘ASSIGN LETTER={letter}’ to assign the drive letter ‘{letter}:’ to the {purpose} partition.");
          }
        }
        CheckDriveLetterAssignment(DriveLetters.Windows, "Windows");
        if (!singlePartition)
        {
          CheckDriveLetterAssignment(DriveLetters.System, "system");
        }
      }

      void IncludeDiskpartScript(EmbeddedScript script)
      {
        CheckDriveLetterAssignments(script.Lines);
        Include(script);
      }

      void Execute(string path, string message)
      {
        writer.WriteLine($"""
          call :print "{message}"
          """);
        if (pe.PauseBeforeFormatting)
        {
          writer.WriteLine("pause");
        }
        writer.WriteLine($"""
          diskpart.exe /s {path} || call :fail "diskpart.exe encountered an error."
      
          """);
      }

      writer.WriteLine("""
        set "LAYOUT="
        wpeutil.exe UpdateBootInfo || call :fail "Unable to read the boot mode from Windows PE."
        for /f "tokens=3" %%t in ('reg.exe query HKLM\System\CurrentControlSet\Control /v PEFirmwareType') do (
            if %%t == 0x1 (
                set "LAYOUT=MBR"
                set "FIRMWARE=BIOS"
            ) else if %%t == 0x2 (
                set "LAYOUT=GPT"
                set "FIRMWARE=UEFI"
            ) else (
                call :fail "Unexpected PEFirmwareType value %%t."
            )
        )
        if not defined LAYOUT call :fail "Windows PE did not report a boot mode."
        call :print "The computer is booted in %FIRMWARE% mode, hence the target disk must be configured with the %LAYOUT% partition layout"
        """);

      switch (pe.PartitionSettings)
      {
        case CustomPartitionSettings settings:
          {
            IncludeDiskpartScript(new EmbeddedScript(Paths.DiskpartScript, Util.SplitLines(settings.Script), Escape: true));
            Execute(Paths.DiskpartScript, "diskpart will now execute your script");
            break;
          }

        case UnattendedPartitionSettings settings:
          string message = "diskpart will now wipe, partition and format disk %TARGET_DISK%";
          if (hasDataPartition)
          {
            writer.WriteLine($"""
              if exist {DriveLetters.Data}:\ call :fail "{DriveLetters.Data}: must be free in Windows PE for the data partition."
              """);
          }
          if (settings.PartitionLayout == PartitionLayout.Automatic)
          {
            foreach (PartitionLayout layout in new PartitionLayout[] { PartitionLayout.GPT, PartitionLayout.MBR })
            {
              IncludeDiskpartScript(new EmbeddedScript($@"X:\{layout}.txt", GetDiskpartScript(settings with { PartitionLayout = layout }), Escape: false));
            }
            Execute(@"X:\%LAYOUT%.txt", message);
          }
          else
          {
            writer.WriteLine($"""
              if /i not "%LAYOUT%"=="{settings.PartitionLayout}" call :fail "Restart the installation media in the boot mode required by the {settings.PartitionLayout} layout."
              """);
            IncludeDiskpartScript(new EmbeddedScript(Paths.DiskpartScript, GetDiskpartScript(settings), Escape: false));
            Execute(Paths.DiskpartScript, message);
          }
          break;

        case InteractivePartitionSettings settings:
          {
            writer.WriteLine($"""
              call :print ^"Press Shift+F10 to open a new console window, then use diskpart to partition and format the disk manually. Make sure to assign the drive letters {DriveLetters.Windows} and {DriveLetters.System} ^
              to the Windows and system partitions, respectively. When finished, continue with Windows Setup in this window.^"
              pause
              """);
            break;
          }

        default:
          throw new NotSupportedException();
      }
    }

    switch (pe.InstallFromSettings)
    {
      case EditionMetadataInstallFromSettings:
        // The image is resolved before the partitioning commands.
        break;

      case IndexInstallFromSettings indexSettings:
        writer.WriteLine($"""
          set "IMG_PARAM=/Index:{indexSettings.Index}"
          """);
        break;

      case NameInstallFromSettings nameSettings:
        writer.WriteLine($"""
          set "IMG_PARAM=/Name:"{nameSettings.Name}""
          """);
        break;

      case InteractiveInstallFromSettings:
        writer.WriteLine("""
          dism.exe /Get-WimInfo /WimFile:"%IMAGE_FILE%"
          echo:
          :choice
          set /p "CHOICE=Enter index of the image you want to install: " || goto :choice
          set "IMG_PARAM=/Index:%CHOICE%"
          """);
        break;

      case EditionInstallFromSettings editionSettings:
        writer.WriteLine($"""
          set "IMG_PARAM=/Name:"Windows %OS_VERSION% {editionSettings.Edition.DisplayName}""
          """);
        break;
      default:
        throw new NotSupportedException();
    }

    writer.WriteLine($$"""
      call :print "Applying Windows image to target disk"
      dism.exe /Apply-Image /ImageFile:%IMAGE_FILE% %SWM_PARAM% %IMG_PARAM% /ApplyDir:{{DriveLetters.Windows}}:\{{(pe.CompactOs ? " /Compact" : "")}}{{(pe.SkipIntegrityCheck ? "" : " /CheckIntegrity /Verify")}} || call :fail "dism.exe encountered an error."

      call :print "Making system partition bootable"
      bcdboot.exe {{DriveLetters.Windows}}:\Windows /s {{bootVolume}}: || call :fail "bcdboot.exe encountered an error."
      if %LAYOUT% == GPT (
          bcdedit.exe /set {fwbootmgr} bootsequence {bootmgr} || call :fail "bcdedit.exe encountered an error."
      )

      """);

    if (hasDataPartition)
    {
      // Copy the new volume's stable mount-manager identity into the fresh image before Windows assigns other letters.
      writer.WriteLine($"""
        call :print "Assigning the data partition to D: in the installed system"
        set "DATA_VOLUME="
        for /f "tokens=3" %%t in ('reg.exe query HKLM\SYSTEM\MountedDevices /v "\DosDevices\{DriveLetters.Data}:"') do set "DATA_VOLUME=%%t"
        if not defined DATA_VOLUME call :fail "Unable to identify the new data volume."
        reg.exe load HKLM\XMLatorTargetSystem "{DriveLetters.Windows}:\Windows\System32\config\SYSTEM" || call :fail "Unable to load the installed SYSTEM hive."
        reg.exe add HKLM\XMLatorTargetSystem\MountedDevices /v "\DosDevices\D:" /t REG_BINARY /d "%DATA_VOLUME%" /f || (
            reg.exe unload HKLM\XMLatorTargetSystem
            call :fail "Unable to assign D: to the data partition."
        )
        reg.exe unload HKLM\XMLatorTargetSystem || call :fail "Unable to unload the installed SYSTEM hive."

        """);
    }

    writer.WriteLine($"""
      if defined MSU_FOLDER (
          call :print "Adding packages from %MSU_FOLDER% to new installation"
          dism.exe /Add-Package /Image:{DriveLetters.Windows}:\ /PackagePath:%MSU_FOLDER% /ScratchDir:{DriveLetters.Windows}:\Windows\Temp || call :fail "dism.exe encountered an error."
      )

      """);

    {
      void DeleteWinRE()
      {
        writer.WriteLine($"""
        call :print "Deleting winre.wim file to avoid creation of recovery partition"
        del {DriveLetters.Windows}:\Windows\System32\Recovery\winre.wim
        
        """);
      }

      switch (pe.PartitionSettings)
      {
        case UnattendedPartitionSettings settings:
          switch (settings.RecoveryMode)
          {
            case RecoveryMode.None:
              if (!settings.SinglePartition)
              {
                DeleteWinRE();
              }
              break;
            case RecoveryMode.Partition:
              // Nothing to do – Windows will automatically install RE on the recovery partition
              break;
            default:
              throw new NotSupportedException();
          }
          break;
        case CustomPartitionSettings settings:
          string[] keywords = [
            "SET ID=27",
            @"LABEL=""Recovery""",
            @"SET ID=""de94bba4-06d1-4d40-a16a-bfd50179d6ac"""
            ];
          if (!keywords.Any(keyword => settings.Script.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
          {
            DeleteWinRE();
          }
          break;
      }
    }

    writer.WriteLine($"""
      call :print "Copying answer file to target disk"
      mkdir {DriveLetters.Windows}:\Windows\Panther
      copy %XML_FILE% {DriveLetters.Windows}:\Windows\Panther\unattend.xml
    
      """);

    writer.WriteLine($"""
      if defined PEDRIVERS_FOLDER (
          call :print "Adding drivers from %PEDRIVERS_FOLDER% to new installation"
          dism.exe /Add-Driver /Image:{DriveLetters.Windows}:\ /Driver:"%PEDRIVERS_FOLDER%" /Recurse
      )

      """);

    if (configuration.VirtIoGuestTools)
    {
      writer.WriteLine($"""
        if defined VIRTIO_DRIVE (
            call :print "Adding VirtIO drivers from %VIRTIO_DRIVE% to new installation"
            dism.exe /Add-Driver /Image:{DriveLetters.Windows}:\ /Driver:"%VIRTIO_DRIVE%\vioscsi\w%OS_VERSION%\%PROCESSOR_ARCHITECTURE%\vioscsi.inf"
            dism.exe /Add-Driver /Image:{DriveLetters.Windows}:\ /Driver:"%VIRTIO_DRIVE%\viostor\w%OS_VERSION%\%PROCESSOR_ARCHITECTURE%\viostor.inf"
            dism.exe /Add-Driver /Image:{DriveLetters.Windows}:\ /Driver:"%VIRTIO_DRIVE%\NetKVM\w%OS_VERSION%\%PROCESSOR_ARCHITECTURE%\netkvm.inf"
        )

        """);
    }

    {
      if (configuration.TimeZoneSettings is ExplicitTimeZoneSettings settings)
      {
        writer.WriteLine($"""
          call :print "Setting time zone"
          dism.exe /Image:{DriveLetters.Windows}:\ /Set-TimeZone:"{settings.TimeZone.Id}"

          """);
      }
    }

    if (pe.Disable8Dot3Names)
    {
      writer.WriteLine($"""
        call :print "Disabling 8.3 file names"
        fsutil.exe 8dot3name set {DriveLetters.Windows}: 1
        fsutil.exe 8dot3name strip /s /f {DriveLetters.Windows}:\
        reg.exe LOAD HKLM\mount {DriveLetters.Windows}:\Windows\System32\config\SYSTEM
        reg.exe ADD HKLM\mount\ControlSet001\Control\FileSystem /v NtfsDisable8dot3NameCreation /t REG_DWORD /d 1 /f
        reg.exe UNLOAD HKLM\mount

        """);
    }

    if (pe.DisableDefender)
    {
      writer.WriteLine($"""
        call :print "Disabling Windows Defender"
        reg.exe LOAD HKLM\mount {DriveLetters.Windows}:\Windows\System32\config\SYSTEM
        for %%s in (Sense WdBoot WdFilter WdNisDrv WdNisSvc WinDefend) do reg.exe ADD HKLM\mount\ControlSet001\Services\%%s /v Start /t REG_DWORD /d 4 /f
        reg.exe UNLOAD HKLM\mount

        """);
    }

    if (configuration.DisableWpbt)
    {
      writer.WriteLine($"""
        call :print "Disabling WPBT"
        reg.exe LOAD HKLM\mount {DriveLetters.Windows}:\Windows\System32\config\SYSTEM
        reg.exe add "HKLM\mount\ControlSet001\Control\Session Manager" /v DisableWpbtExecution /t REG_DWORD /d 1 /f
        reg.exe UNLOAD HKLM\mount

        """);
    }
    {
      void ConfigurePagingFile(string data)
      {
        writer.WriteLine($"""
          call :print "Configuring paging file"
          reg.exe LOAD HKLM\mount {DriveLetters.Windows}:\Windows\System32\config\SYSTEM
          reg.exe add "HKLM\mount\ControlSet001\Control\Session Manager\Memory Management" /v PagingFiles /t REG_MULTI_SZ /f {data}
          reg.exe UNLOAD HKLM\mount

          """);
      }

      switch (pe.PagingFileSettings)
      {
        case AutomaticPagingFileSettings:
          break;
        case NoPagingFileSettings:
          ConfigurePagingFile("");
          break;
        case CustomPagingFileSettings pfs:
          ConfigurePagingFile(@$"/d ""C:\pagefile.sys {pfs.InitialSizeMiB} {pfs.MaxSizeMiB}""");
          break;
      }
    }
    {
      if (configuration.LanguageSettings is UnattendedLanguageSettings settings)
      {
        GeoLocation location = settings.GeoLocation;
        writer.WriteLine($"""
          call :print "Setting device setup region to {location.DisplayName} (GeoID {location.Id})"
          reg.exe LOAD HKLM\mount {DriveLetters.Windows}:\Windows\System32\config\SOFTWARE
          reg.exe ADD "HKLM\mount\Microsoft\Windows\CurrentVersion\Control Panel\DeviceRegion" /v DeviceRegion /t REG_DWORD /d {location.Id} /f
          reg.exe UNLOAD HKLM\mount

          """);
      }
    }

    {
      writer.WriteLine($"""
        set "ROBOCOPY_ARGS=/E /XX /COPY:DAT /DCOPY:DAT /R:0"
        if defined OEM_FOLDER (
            call :print "Copying contents of %OEM_FOLDER%"
            if exist "%OEM_FOLDER%\$$" robocopy.exe "%OEM_FOLDER%\$$" {DriveLetters.Windows}:\Windows %ROBOCOPY_ARGS%
            if exist "%OEM_FOLDER%\$1" robocopy.exe "%OEM_FOLDER%\$1" {DriveLetters.Windows}:\ %ROBOCOPY_ARGS%
            for %%d in ({letters.Except(skippedDrives).JoinString(' ')}) do (
                if exist "%OEM_FOLDER%\%%d" robocopy.exe "%OEM_FOLDER%\%%d" %%d:\ %ROBOCOPY_ARGS%
            )
        )

        """);
    }

    writer.WriteLine("""
      call :print "Computer will now reboot"
      """);
    if (pe.PauseBeforeReboot)
    {
      writer.WriteLine("pause");
    }
    writer.WriteLine("""
      wpeutil.exe reboot
      goto :eof

      :fail
      echo:
      echo:Fatal error: %~1
      echo:
      pause
      exit 1

      :print
      echo:
      echo:*** %~1 ***
      echo:
      goto :eof
      """);

    return Util.SplitLines(writer.ToString());
  }
}
