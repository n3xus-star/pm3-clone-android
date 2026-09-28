// Version shown in the window title and in the .exe's Properties -> Details.
// Bump AppInfo.Version (and the attributes below) for every release, and note it in CHANGELOG.md.

using System.Reflection;

[assembly: AssemblyTitle("ALM Image Uploader")]
[assembly: AssemblyProduct("ALM Image Uploader")]
[assembly: AssemblyDescription("Assign screenshots to HP / OpenText ALM Test Lab test cases and upload them")]
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]
[assembly: AssemblyInformationalVersion("2.0.0")]

namespace AlmImageUploader
{
    public static class AppInfo
    {
        public const string Version = "2.0.0";
    }
}
