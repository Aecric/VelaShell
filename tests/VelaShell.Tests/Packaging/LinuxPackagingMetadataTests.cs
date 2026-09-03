namespace VelaShell.Tests.Packaging;

[TestClass]
public class LinuxPackagingMetadataTests
{
    private static string RepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "VelaShell.slnx")))
            {
                return directory;
            }
            directory = Directory.GetParent(directory)?.FullName;
        }
        throw new InvalidOperationException("Could not find the VelaShell repository root.");
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    [TestMethod]
    public void DesktopEntry_UsesInstalledCommandAndStableApplicationId()
    {
        string desktop = Read("build", "linux", "io.github.joesdu.VelaShell.desktop");

        StringAssert.Contains(desktop, "Exec=velashell %u");
        StringAssert.Contains(desktop, "TryExec=velashell");
        StringAssert.Contains(desktop, "Icon=io.github.joesdu.VelaShell");
        StringAssert.Contains(desktop, "MimeType=x-scheme-handler/ssh;x-scheme-handler/sftp;");
    }

    [TestMethod]
    public void NfpmConfig_PreservesFullPublishTreeAndRequiredEntrypoints()
    {
        string config = Read("build", "linux", "nfpm.yaml");

        StringAssert.Contains(config, "src: ${VELASHELL_INPUT}/");
        StringAssert.Contains(config, "dst: /usr/lib/velashell");
        StringAssert.Contains(config, "type: tree");
        StringAssert.Contains(config, "src: /usr/lib/velashell/VelaShell");
        StringAssert.Contains(config, "dst: /usr/bin/velashell");
        StringAssert.Contains(config, "dst: /usr/share/applications/io.github.joesdu.VelaShell.desktop");
        StringAssert.Contains(config, "dst: /usr/share/icons/hicolor/1024x1024/apps/io.github.joesdu.VelaShell.png");
    }

    [TestMethod]
    public void ReleaseWorkflow_KeepsTarGzAsLinuxUpdateAsset()
    {
        string workflow = Read(".github", "workflows", "release.yml");

        StringAssert.Contains(workflow, "build/linux/build-linux.sh \"$version\" \"$rid\" out dist");
        StringAssert.Contains(workflow, "add_asset \"$rid\" \"VelaShell-$VERSION-$rid.tar.gz\"");
        Assert.DoesNotContain("add_asset \"$rid\" \"VelaShell-$VERSION-$rid.deb\"", workflow);
        Assert.DoesNotContain("add_asset \"$rid\" \"VelaShell-$VERSION-$rid.rpm\"", workflow);
    }

    [TestMethod]
    public void PackageCi_UsesSameBuildEntryPointAsRelease()
    {
        string workflow = Read(".github", "workflows", "linux-package-ci.yml");

        StringAssert.Contains(workflow, "build/linux/build-linux.sh \"$version\" \"$rid\" out dist");
        StringAssert.Contains(workflow, "VELASHELL_SIGN_ASSEMBLY=false");
    }

    [TestMethod]
    public void PackagingScript_PinsAndVerifiesNfpmDownload()
    {
        string script = Read("build", "linux", "package-linux.sh");

        StringAssert.Contains(script, "velashell_nfpm_version=2.47.0");
        StringAssert.Contains(script, "0660ca602b2d2d2ae4781a06c692b3eeb9d437ffea05b831d76e41f4a3188783");
        StringAssert.Contains(script, "1c0f5f2999b9a974bfb04fdb0cc3306096de530ac5dbb25d739cc5f5219c919c");
        StringAssert.Contains(script, "sha256sum --check --status");
        Assert.DoesNotContain("/latest/", script);
    }
}
