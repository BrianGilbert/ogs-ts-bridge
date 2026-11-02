#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using OgsTsBridge.Sources;
using Ogs.Telemetry;
using Xunit;
using static OgsTsBridge.Tests.SourceTestKit;

namespace OgsTsBridge.Tests
{
/// <summary>Forged-input translation tests: DCS export script, parser and patcher.</summary>
public class DcsSourceTests
{

    static string DcsLine(Func<int, string>? field = null)
    {
        var core = "OGS2;12.5;1.5;0.1;-0.2;0.01;0.02;0.03;0.1;1.0;-0.05;1000;500;-2000";
        var rest = new StringBuilder();
        for (int i = 0; i < DcsSource.Fields.Length; i++) rest.Append(';').Append(field?.Invoke(i) ?? "");
        return core + rest + "\n";
    }

    static int DcsIndex(string channel) => Array.FindIndex(DcsSource.Fields, f => f.Name == channel);

    [Fact]
    public void Dcs_Parses_ExportLine()
    {
        var src = new DcsSource();
        var (cap, session) = Start(src);
        using var _ = session;

        int ias = DcsIndex("air.indicatedAirspeed"), lat = DcsIndex("position.lat"),
            warn = DcsIndex("warning.masterWarning"), gear = DcsIndex("gear.position");
        var line = DcsLine(i => i == ias ? "120" : i == lat ? "45" : i == warn ? "1" : i == gear ? "" : "");
        src.Translate(Encoding.ASCII.GetBytes(line), session, Now());
        var m = cap.LastMotion();
        var obj = m.Objects[0];
        AssertQuat(OgsMath.EulerToQuaternion(1.5, 0.1, -0.2), m);
        AssertEx.ApproxEqual(-2000f, obj.Position.X, 1e-3f);   // DCS z (east)
        AssertEx.ApproxEqual(1000f, obj.Position.Z, 1e-3f);    // DCS x (north)
        AssertEx.ApproxEqual(-0.05f * 9.80665f, obj.BodyDynamics.SpecificForce.X, 1e-4f);
        AssertEx.ApproxEqual(1.0f * 9.80665f, obj.BodyDynamics.SpecificForce.Y, 1e-4f);
        AssertEx.ApproxEqual(0.1f * 9.80665f, obj.BodyDynamics.SpecificForce.Z, 1e-4f);
        AssertEx.ApproxEqual(0.03f, obj.BodyDynamics.AngularVelocity.X, 1e-6f);
        AssertEx.ApproxEqual(-0.02f, obj.BodyDynamics.AngularVelocity.Y, 1e-6f);
        AssertEx.ApproxEqual(0.01f, obj.BodyDynamics.AngularVelocity.Z, 1e-6f);
        AssertEx.ApproxEqual(120f, cap.Channel(m, "air.indicatedAirspeed"), 1e-4f);
        AssertEx.ApproxEqual((float)(Math.PI / 4), cap.Channel(m, "position.lat"), 1e-5f);
        Assert.Throws<ArgumentException>(() => cap.Channel(m, "gear.position")); // empty = not provided

        src.Translate(Encoding.ASCII.GetBytes("OGSI;Test Pilot;F-16C_50;blue\n"), session, Now());
        src.Translate(Encoding.ASCII.GetBytes(line), session, Now() + 1.0);
        Assert.Equal("F-16C_50", cap.Discovery().Objects[0].TypeLabel);
        Assert.Equal("Test Pilot", cap.SessionText("driver.name"));

        int before = cap.Motions;
        src.Translate(Encoding.ASCII.GetBytes("OGS2;1;2;3\n"), session, Now() + 2.0);   // short core
        src.Translate(Encoding.ASCII.GetBytes(line.Replace("OGS2;12.5", "OGS2;x")), session, Now() + 3.0);
        Assert.Equal(before, cap.Motions);
    }

    [Fact]
    public void Dcs_Script_Emits_Every_Field_In_Order()
    {
        var script = DcsSource.ExportScript(42424);
        int last = -1;
        foreach (var f in DcsSource.Fields)
        {
            int at = script.IndexOf("n(" + f.Lua + ")", last + 1, StringComparison.Ordinal);
            Assert.True(at > last, $"{f.Name} missing or out of order in the export script");
            last = at;
        }
    }

    [Fact]
    public void Dcs_Patch_InstallsScript_And_HooksExportOnce()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bridgedcs-" + System.IO.Path.GetRandomFileName());
        var scripts = System.IO.Path.Combine(root, "DCS.openbeta", "Scripts");
        System.IO.Directory.CreateDirectory(scripts);
        var export = System.IO.Path.Combine(scripts, "Export.lua");
        System.IO.File.WriteAllText(export, "dofile('other-tool.lua')");
        try
        {
            var patched = DcsSource.PatchSavedGames(root, 42424);
            Assert.Equal(2, patched.Count);
            var script = System.IO.File.ReadAllText(System.IO.Path.Combine(scripts, DcsSource.ScriptName));
            Assert.Contains("port = 42424", script);
            var text = System.IO.File.ReadAllText(export);
            Assert.StartsWith("dofile('other-tool.lua')", text);
            Assert.Contains(DcsSource.ScriptName, text);
            Assert.True(System.IO.File.Exists(export + ".bak-ogs-ts-bridge"));

            DcsSource.PatchSavedGames(root, 42424);  // idempotent hook
            var again = System.IO.File.ReadAllText(export);
            Assert.Equal(text, again);
        }
        finally { System.IO.Directory.Delete(root, true); }
    }
}
}
