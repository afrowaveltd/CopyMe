using Afrowave.DiskMigrator.Core.Abstractions;
using Afrowave.DiskMigrator.Core.Planning;

namespace Afrowave.DiskMigrator.Platform.Windows;

/// <summary>
/// Makes the freshly written target disk bootable. Everything here delegates to Microsoft's own
/// tools: bcdboot writes a correct BCD for the new partition layout, bootsect restores the BIOS
/// boot code, reagentc re-registers the recovery environment.
/// </summary>
internal sealed class WindowsBootConfigurator : IBootConfigurator
{
    private readonly IToolRunner _runner;
    private readonly WindowsVolumeService _volumes;

    public WindowsBootConfigurator(IToolRunner runner, WindowsVolumeService volumes)
    {
        _runner = runner;
        _volumes = volumes;
    }

    public bool IsAvailable => _runner.ResolveTool("bcdboot.exe") is not null;

    public string? UnavailableReason => IsAvailable ? null : "bcdboot.exe nebyl nalezen";

    public BootConfigurationResult Configure(BootConfigurationRequest request, CancellationToken cancellationToken = default)
    {
        var actions = new List<string>();
        var errors = new List<string>();

        if (request.WindowsPartitionStartBytes is null)
        {
            errors.Add("Nebyl určen oddíl s instalací Windows, zavádění nelze obnovit.");
            return new BootConfigurationResult(false, actions, errors);
        }

        if (request.SystemPartitionStartBytes is null)
        {
            errors.Add("Nebyl určen systémový oddíl (ESP / aktivní oddíl), zavádění nelze obnovit.");
            return new BootConfigurationResult(false, actions, errors);
        }

        char? windowsLetter = null;
        char? systemLetter = null;
        char? recoveryLetter = null;

        try
        {
            windowsLetter = _volumes.MountVolume(request.TargetDiskNumber, request.WindowsPartitionStartBytes.Value, readOnly: false);
            systemLetter = _volumes.MountVolume(request.TargetDiskNumber, request.SystemPartitionStartBytes.Value, readOnly: false);

            string bcdboot = _runner.ResolveTool("bcdboot.exe")!;
            string firmware = request.Mode == BootMode.Uefi ? "UEFI" : "BIOS";

            var result = _runner.Run(bcdboot, [
                $@"{windowsLetter}:\Windows",
                "/s", $"{systemLetter}:",
                "/f", firmware,
                "/v",
            ], TimeSpan.FromMinutes(15), null, cancellationToken);

            if (!result.Success)
            {
                errors.Add($"bcdboot selhal: {Summarize(result)}");
                return new BootConfigurationResult(false, actions, errors);
            }

            actions.Add($"Zaváděcí soubory zapsány na oddíl {systemLetter}: (režim {firmware}).");

            if (request.RunBootsect)
            {
                string? bootsect = _runner.ResolveTool("bootsect.exe");
                if (bootsect is null)
                {
                    errors.Add("bootsect.exe nebyl nalezen, BIOS zaváděcí kód nebyl obnoven.");
                }
                else
                {
                    var bootsectResult = _runner.Run(bootsect, [
                        "/nt60", $"{systemLetter}:", "/force", "/mbr",
                    ], TimeSpan.FromMinutes(5), null, cancellationToken);

                    if (bootsectResult.Success)
                    {
                        actions.Add("Zaváděcí kód MBR byl obnoven (bootsect /nt60 /mbr).");
                    }
                    else
                    {
                        errors.Add($"bootsect selhal: {Summarize(bootsectResult)}");
                    }
                }
            }

            if (request.RegisterWinRe && request.RecoveryPartitionStartBytes is not null)
            {
                string? reagentc = _runner.ResolveTool("reagentc.exe");
                if (reagentc is null)
                {
                    errors.Add("reagentc.exe nebyl nalezen, prostředí pro obnovení nebylo zaregistrováno.");
                }
                else
                {
                    recoveryLetter = _volumes.MountVolume(
                        request.TargetDiskNumber, request.RecoveryPartitionStartBytes.Value, readOnly: false);

                    string winRePath = $@"{recoveryLetter}:\Recovery\WindowsRE";
                    if (!Directory.Exists(winRePath))
                    {
                        errors.Add($"Na oddíle pro obnovení chybí složka {winRePath}.");
                    }
                    else
                    {
                        var reagentcResult = _runner.Run(reagentc, [
                            "/setreimage",
                            "/path", winRePath,
                            "/target", $@"{windowsLetter}:\Windows",
                        ], TimeSpan.FromMinutes(10), null, cancellationToken);

                        if (reagentcResult.Success)
                        {
                            actions.Add("Prostředí pro obnovení (WinRE) bylo znovu zaregistrováno.");
                        }
                        else
                        {
                            errors.Add($"reagentc selhal: {Summarize(reagentcResult)}");
                        }
                    }
                }
            }

            actions.Add("Zavádění systému bylo znovu vytvořeno – funkčnost na cílovém hardwaru nebyla otestována.");

            return new BootConfigurationResult(errors.Count == 0, actions, errors);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            errors.Add(ex.Message);
            return new BootConfigurationResult(false, actions, errors);
        }
        finally
        {
            if (recoveryLetter is { } r) _volumes.DismountVolume(r);
            if (systemLetter is { } s) _volumes.DismountVolume(s);
            if (windowsLetter is { } w) _volumes.DismountVolume(w);
        }
    }

    private static string Summarize(ToolResult result)
    {
        string text = result.CombinedOutput.Trim();
        if (text.Length == 0) return $"návratový kód {result.ExitCode}";

        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" / ", lines.Take(3));
    }
}
