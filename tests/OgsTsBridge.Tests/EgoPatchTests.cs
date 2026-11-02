#nullable enable
using System.Collections.Generic;
using System.IO;
using OgsTsBridge.Sources;
using Xunit;

namespace OgsTsBridge.Tests
{
public class EgoPatchTests
{
    static string WriteConfig(string root, string rel, string xml)
    {
        var path = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, xml);
        return path;
    }

    [Fact]
    public void PatchEnablesUdpInExistingConfig()
    {
        var root = Path.Combine(Path.GetTempPath(), "bridgepatch-" + Path.GetRandomFileName());
        WriteConfig(root, Path.Combine("My Games", "DiRT Rally 2.0", "hardwaresettings", "hardware_settings_config.xml"),
            """
            <hardware_settings_config version="2">
              <motion_platform>
                <dbox enabled="false" />
                <udp enabled="false" extradata="0" ip="0.0.0.0" port="11111" delay="0" />
              </motion_platform>
            </hardware_settings_config>
            """);

        var patched = EgoSource.PatchEgoFiles(root, 20777);
        Assert.Single(patched);

        var doc = new System.Xml.XmlDocument();
        doc.Load(patched[0]);
        var udp = doc.DocumentElement!.SelectSingleNode("//motion_platform/udp")!;
        Assert.Equal("true", udp.Attributes!["enabled"]!.Value);
        Assert.Equal("127.0.0.1", udp.Attributes!["ip"]!.Value);
        Assert.Equal("20777", udp.Attributes!["port"]!.Value);
        Assert.Equal("1", udp.Attributes!["delay"]!.Value);
        var dbox = doc.DocumentElement.SelectSingleNode("//motion_platform/dbox")!;
        Assert.Equal("true", dbox.Attributes!["enabled"]!.Value);
        Assert.True(File.Exists(patched[0] + ".bak-ogs-ts-bridge"), "backup saved");
        Directory.Delete(root, true);
    }

    [Fact]
    public void PatchCreatesMissingSectionsAndSkipsMissingFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "bridgepatch-" + Path.GetRandomFileName());
        WriteConfig(root, Path.Combine("My Games", "DiRT 4", "hardwaresettings", "hardware_settings_config.xml"),
            "<root><something_else /></root>");

        var patched = EgoSource.PatchEgoFiles(root, 20777);
        Assert.Single(patched); // only the DiRT 4 file exists

        var doc = new System.Xml.XmlDocument();
        doc.Load(patched[0]);
        var udp = doc.DocumentElement!.SelectSingleNode("//motion_platform/udp")!;
        Assert.Equal("true", udp.Attributes!["enabled"]!.Value);
        Assert.Equal("20777", udp.Attributes!["port"]!.Value);
        Directory.Delete(root, true);
    }


    [Fact]
    public void SolarCrown_Patch_EnablesTelemetry()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bridgetdu-" + System.IO.Path.GetRandomFileName());
        var dir = System.IO.Path.Combine(root, "My Games", "Test Drive Unlimited Solar Crown");
        System.IO.Directory.CreateDirectory(dir);
        var cfg = System.IO.Path.Combine(dir, "UserSettings.cfg");
        System.IO.File.WriteAllText(cfg, "Rc2.Graphics.Quality = 3\nRc2.Telemetry.EnableTelemetry = false\n");
        try
        {
            Assert.Single(EgoSource.PatchSolarCrownSettings(root, 20777));
            var text = System.IO.File.ReadAllText(cfg);
            Assert.Contains("Rc2.Telemetry.EnableTelemetry = true", text);
            Assert.Contains("Rc2.Telemetry.TelemetryPort = 20777", text);
            Assert.Contains("Rc2.Graphics.Quality = 3", text);
            Assert.Empty(EgoSource.PatchSolarCrownSettings(root, 20777)); // already set
        }
        finally { System.IO.Directory.Delete(root, true); }
    }
}
}
