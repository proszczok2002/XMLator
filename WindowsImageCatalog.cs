using System.Buffers.Binary;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace XMLator;

internal sealed record WindowsImage(int Index, string Name, string Edition, string Architecture, int Build,
    string InstallationType, string ProductType);

internal static class WindowsImageCatalog
{
    // The XML resource descriptor starts at byte 72 of a WIM image header.
    private const int HeaderSize = 208;
    private const ulong ResourceSizeMask = 0x00ff_ffff_ffff_ffff;
    private const int MaxXmlSize = 16 * 1024 * 1024;

    public static WindowsImage[] Read(Stream image)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        image.ReadExactly(header);
        if (!header[..8].SequenceEqual("MSWIM\0\0\0"u8) || BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) < HeaderSize)
            throw new InvalidDataException("Invalid WIM header.");
        ulong sizeAndFlags = BinaryPrimitives.ReadUInt64LittleEndian(header[72..]);
        ulong size = sizeAndFlags & ResourceSizeMask;
        ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(header[80..]);
        ulong uncompressedSize = BinaryPrimitives.ReadUInt64LittleEndian(header[88..]);
        if (size is 0 or > MaxXmlSize || size != uncompressedSize || (sizeAndFlags >> 56 & 4) != 0 ||
            offset < HeaderSize || offset > (ulong)image.Length || size > (ulong)image.Length - offset)
            throw new InvalidDataException("Invalid WIM XML resource.");

        image.Position = (long)offset;
        byte[] data = new byte[(int)size];
        image.ReadExactly(data);
        using var stream = new MemoryStream(data);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            MaxCharactersInDocument = MaxXmlSize
        });
        var root = XElement.Load(reader);
        if (root.Name != "WIM")
            throw new InvalidDataException("Invalid WIM image catalog.");
        var images = root.Elements("IMAGE").Select(entry =>
        {
            XElement? windows = entry.Element("WINDOWS");
            string edition = (string?)windows?.Element("EDITIONID") ?? (string?)entry.Element("FLAGS") ?? "";
            return new WindowsImage((int?)entry.Attribute("INDEX") ?? 0, (string?)entry.Element("NAME") ?? "", edition,
                (string?)windows?.Element("ARCH") switch
                {
                    "0" => "x86", "6" => "ia64", "9" => "amd64", "12" => "arm64", _ => ""
                }, (int?)windows?.Element("VERSION")?.Element("BUILD") ?? 0,
                (string?)windows?.Element("INSTALLATIONTYPE") ?? "", (string?)windows?.Element("PRODUCTTYPE") ?? "");
        }).ToArray();
        if (images.Length == 0 || images.Any(image => image.Index < 1) || images.Select(image => image.Index).Distinct().Count() != images.Length)
            throw new InvalidDataException("Invalid WIM image indexes.");
        return images;
    }

    public static void Validate(SetupProfile profile, IReadOnlyList<WindowsImage> images)
    {
        var architectures = profile.ExtraArchitectures.Prepend(profile.Architecture).Distinct().ToArray();
        var target = profile.Target;
        var version = images.Where(image => MatchesWindows(target, image)).ToArray();
        if (version.Length == 0)
            throw SetupException.Format(
                $"ISO nie zawiera systemu {target.Name}. Obrazy na nośniku: {Describe(images)}.",
                $"The ISO does not contain {target.Name}. Images on the media: {Describe(images)}.");
        var compatible = version.Where(image => architectures.Contains(image.Architecture)).ToArray();
        if (compatible.Length == 0)
        {
            string expected = string.Join(", ", architectures.Select(ArchitectureLabel));
            throw SetupException.Format(
                $"ISO dla {target.Name} nie zawiera obrazu o architekturze {expected}. Obrazy na nośniku: {Describe(version)}.",
                $"The ISO for {target.Name} has no image for the {expected} architecture. Images on the media: {Describe(version)}.");
        }

        string method = target.ModernClient ? profile.Option("PE", "Normal") : "Normal";
        if (method == "Custom" || profile.ImageMode == ImageMode.Interactive)
            return;
        if (profile.ImageMode == ImageMode.Index && compatible.Any(image => image.Index == profile.ImageIndex) ||
            profile.ImageMode == ImageMode.ExactName && compatible.Any(image => Same(image.Name, profile.ImageName.Trim())))
            return;
        if (profile.ImageMode == ImageMode.Edition)
        {
            var matches = compatible.Where(image =>
                EditionCatalog.For(target, image.Architecture).FirstOrDefault(edition => edition.Id == profile.PresetEdition) is { } edition &&
                (!target.ModernClient || Same(image.Edition, edition.Id)) &&
                (method == "Generated" || Same(image.Name, edition.ImageName))).ToArray();
            if (matches.Length > 0 && (method != "Generated" || matches.GroupBy(image => image.Architecture).Any(group => group.Count() == 1)))
                return;
            if (matches.Length > 1)
                throw SetupException.Format(
                    $"ISO zawiera kilka pasujących obrazów edycji {profile.SelectedEdition?.Label}. Wybierz indeks lub dokładną nazwę. Obrazy: {Describe(matches)}.",
                    $"The ISO contains multiple images matching the {profile.SelectedEdition?.Label} edition. Choose an index or exact image name. Images: {Describe(matches)}.");
        }

        string selected = profile.ImageMode switch
        {
            ImageMode.Index => profile.ImageIndex.ToString(CultureInfo.InvariantCulture),
            ImageMode.ExactName => profile.ImageName.Trim(),
            _ => profile.SelectedEdition?.ImageName ?? profile.PresetEdition
        };
        throw SetupException.Format(
            $"ISO nie zawiera zgodnego obrazu „{selected}”. Obrazy dostępne dla wybranego systemu i architektury: {Describe(compatible)}.",
            $"The ISO has no compatible image matching “{selected}”. Images for the selected Windows version and architecture: {Describe(compatible)}.");
    }

    private static bool MatchesWindows(Target target, WindowsImage image)
    {
        if (image.Build == 0 || Same(image.InstallationType, "WindowsPE"))
            return false;
        bool server = image.InstallationType.StartsWith("Server", StringComparison.OrdinalIgnoreCase) ||
            Same(image.ProductType, "ServerNT") || Same(image.ProductType, "LanmanNT");
        bool thin = Same(image.Edition, "WinThinPC") || image.Name.Contains("Thin PC", StringComparison.OrdinalIgnoreCase);
        bool hyperV = image.Edition.StartsWith("ServerHyper", StringComparison.OrdinalIgnoreCase) ||
            image.Name.Contains("Hyper-V Server", StringComparison.OrdinalIgnoreCase);
        if (target.HyperV ? !hyperV : hyperV || target.Server != server)
            return false;
        if (thin != (target.Name == "Windows Thin PC"))
            return false;

        return target.Name switch
        {
            "Windows Vista" => image.Build == 6000,
            "Windows Vista SP1" or "Windows Server 2008" => image.Build == 6001,
            "Microsoft Hyper-V Server 2008" => image.Build is 6001 or 6002,
            "Windows Vista SP2" or "Windows Server 2008 SP2" => image.Build == 6002,
            "Windows 7" or "Windows Thin PC" or "Windows Server 2008 R2" or "Microsoft Hyper-V Server 2008 R2"
                => image.Build is 7600 or 7601,
            "Windows 8" or "Windows Server 2012" or "Microsoft Hyper-V Server 2012" => image.Build == 9200,
            "Windows 8.1" or "Windows Server 2012 R2" or "Microsoft Hyper-V Server 2012 R2" => image.Build == 9600,
            "Windows 10" => image.Build is >= 10240 and < 22000,
            "Windows 11" => image.Build >= 22000,
            "Windows Server 2016" or "Microsoft Hyper-V Server 2016" => image.Build == 14393,
            "Windows Server, version 1709" => image.Build == 16299,
            "Windows Server, version 1803" => image.Build == 17134,
            "Windows Server 2019" or "Windows Server, version 1809" or "Microsoft Hyper-V Server 2019" => image.Build == 17763,
            "Windows Server, version 1903" => image.Build == 18362,
            "Windows Server, version 1909" => image.Build == 18363,
            "Windows Server, version 2004" => image.Build == 19041,
            "Windows Server, version 20H2" => image.Build == 19042,
            "Windows Server 2022" => image.Build == 20348,
            "Windows Server, version 23H2" => image.Build == 25398,
            "Windows Server 2025" => image.Build == 26100,
            _ => false
        };
    }

    private static bool Same(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static string ArchitectureLabel(string architecture) => architecture == "amd64" ? "x64" : architecture;

    private static string Describe(IReadOnlyList<WindowsImage> images)
    {
        string entries = string.Join(", ", images.Take(8).Select(image =>
            $"{image.Index}. {(image.Name.Length > 100 ? image.Name[..100] + "…" : image.Name)} ({ArchitectureLabel(image.Architecture)}, {image.Build})"));
        return images.Count > 8 ? entries + ", …" : entries;
    }
}
