using System.Reflection;
using Sed.Core.Updating;

namespace Sed.App;

public static class EditorUpdater
{
    public static readonly Uri PortfolioFeed = new("https://richard-pemberton-research.darkahn.chatgpt.site/updates/sed/releases.json");
    public static readonly Uri GitLabFeed = new("https://gitlab.com/codalilitari/SED-MotS/-/raw/main/updates/sed-releases.json");
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    public static string CurrentVersion => typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
    public static string DefaultChannel
    {
        get { try { return SedReleaseVersion.Parse(CurrentVersion).Preview is null ? "stable" : "preview"; } catch (InvalidDataException) { return "stable"; } }
    }
    public static bool Supported => OperatingSystem.IsWindows() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64;
    public static string UpdateRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SED", "updates");
    public static PortableUpdateService Create() => new(Http,
        new UpdateUrlPolicy([new("https://richard-pemberton-research.darkahn.chatgpt.site/updates/sed/"), GitLabFeed,
            new("https://gitlab.com/api/v4/projects/86965025/packages/generic/sed-mots/")]),
        [PortfolioFeed, GitLabFeed], Path.Combine(UpdateRoot, "check.json"));
}
