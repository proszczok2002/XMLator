namespace XMLator;

internal sealed record MediaEdition(string Id, string Label, string ImageName);

internal static class EditionCatalog
{
    public static MediaEdition[] For(Target target, string? architecture = null)
    {
        string prefix = target.Vista ? "Windows Vista" : target.Name == "Windows Server 2008 SP2" ? "Windows Server 2008" : target.Name;
        if (architecture == "ia64")
            return [new("Itanium", "For Itanium-Based Systems", $"{prefix} for Itanium-Based Systems")];
        if (target.HyperV || target.Name == "Windows Thin PC")
            return [new("Default", target.Name, target.Name)];
        if (target.ServerChannel)
            return
            [
                new("StandardCore", "Standard (Server Core)", "Windows Server Standard"),
                new("DatacenterCore", "Datacenter (Server Core)", "Windows Server Datacenter")
            ];
        if (target.Vista)
            return
            [
                Edition("HomeBasic", "Home Basic", "HOMEBASIC"), Edition("HomePremium", "Home Premium", "HOMEPREMIUM"),
                Edition("Business", "Business", "BUSINESS"), Edition("Enterprise", "Enterprise", "ENTERPRISE"),
                Edition("Ultimate", "Ultimate", "ULTIMATE"), Edition("Starter", "Starter", "STARTER"),
                Edition("HomeBasicN", "Home Basic N", "HOMEBASICN"), Edition("BusinessN", "Business N", "BUSINESSN")
            ];
        if (target.ModernClient)
        {
            return
            [
                Edition("Core", "Home"), Edition("Professional", "Pro"), Edition("Enterprise", "Enterprise"),
                Edition("Education", "Education"), Edition("CoreSingleLanguage", "Home Single Language"),
                Edition("ProfessionalWorkstation", "Pro for Workstations"), Edition("ProfessionalEducation", "Pro Education"),
                Edition("EnterpriseS", "Enterprise LTSC"), Edition("IoTEnterprise", "IoT Enterprise"),
                Edition("IoTEnterpriseS", "IoT Enterprise LTSC"), Edition("CoreN", "Home N"),
                Edition("ProfessionalN", "Pro N"), Edition("EnterpriseN", "Enterprise N"), Edition("EducationN", "Education N"),
                Edition("EnterpriseSN", "Enterprise N LTSC"), Edition("EnterpriseEval", "Enterprise Evaluation")
            ];
        }
        if (target.Name == "Windows 7")
        {
            return
            [
                Edition("HomePremium", "Home Premium", "HOMEPREMIUM"),
                Edition("Professional", "Professional", "PROFESSIONAL"),
                Edition("Enterprise", "Enterprise", "ENTERPRISE"),
                Edition("Ultimate", "Ultimate", "ULTIMATE"),
                Edition("HomeBasic", "Home Basic", "HOMEBASIC"), Edition("Starter", "Starter", "STARTER")
            ];
        }
        if (!target.Server)
        {
            return
            [
                new("Core", target.Name, target.Name),
                Edition("Professional", "Pro"), Edition("Enterprise", "Enterprise"),
                Edition("CoreSingleLanguage", "Single Language"), Edition("CoreN", $"{target.Name} N", "N"),
                Edition("ProfessionalN", "Pro N"), Edition("EnterpriseN", "Enterprise N")
            ];
        }
        if (target.Name == "Windows Server 2025")
            return
            [
                Edition("StandardDesktop", "Standard (GUI)", "Standard (Desktop Experience)"),
                Edition("StandardCore", "Standard (Server Core)", "Standard"),
                Edition("DatacenterDesktop", "Datacenter (GUI)", "Datacenter (Desktop Experience)"),
                Edition("DatacenterCore", "Datacenter (Server Core)", "Datacenter"),
                Edition("StandardDesktopEval", "Standard Evaluation (GUI)", "SERVERSTANDARD"),
                Edition("StandardCoreEval", "Standard Evaluation (Server Core)", "SERVERSTANDARDCORE"),
                Edition("DatacenterDesktopEval", "Datacenter Evaluation (GUI)", "SERVERDATACENTER"),
                Edition("DatacenterCoreEval", "Datacenter Evaluation (Server Core)", "SERVERDATACENTERCORE")
            ];
        var server = new List<MediaEdition>
        {
            Edition("StandardDesktop", "Standard (GUI)", "SERVERSTANDARD"),
            Edition("StandardCore", "Standard (Server Core)", "SERVERSTANDARDCORE"),
            Edition("DatacenterDesktop", "Datacenter (GUI)", "SERVERDATACENTER"),
            Edition("DatacenterCore", "Datacenter (Server Core)", "SERVERDATACENTERCORE")
        };
        if (target.Server2008)
        {
            server.AddRange([Edition("EnterpriseDesktop", "Enterprise (GUI)", "SERVERENTERPRISE"),
                Edition("EnterpriseCore", "Enterprise (Server Core)", "SERVERENTERPRISECORE"),
                Edition("Web", "Web Server", "SERVERWEB")]);
        }
        else
        {
            server.AddRange([Edition("StandardDesktopEval", "Standard Evaluation (GUI)", "SERVERSTANDARDEVAL"),
                Edition("StandardCoreEval", "Standard Evaluation (Server Core)", "SERVERSTANDARDCOREEVAL"),
                Edition("DatacenterDesktopEval", "Datacenter Evaluation (GUI)", "SERVERDATACENTEREVAL"),
                Edition("DatacenterCoreEval", "Datacenter Evaluation (Server Core)", "SERVERDATACENTERCOREEVAL")]);
        }
        if (target.Name is "Windows Server 2012" or "Windows Server 2012 R2" or "Windows Server 2016" or "Windows Server 2019")
            server.Add(Edition("Essentials", "Essentials", "SERVERESSENTIALS"));
        if (target.Name is "Windows Server 2012" or "Windows Server 2012 R2")
            server.Add(Edition("Foundation", "Foundation", "SERVERFOUNDATION"));
        return server.ToArray();

        MediaEdition Edition(string id, string label, string? image = null) => new(id, label, $"{prefix} {image ?? label}");
    }
}
