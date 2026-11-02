#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using OgsTsBridge.Sources;
using Xunit;
using static OgsTsBridge.Tests.SourceTestKit;

namespace OgsTsBridge.Tests
{
/// <summary>Session identity across a redeclare.</summary>
public class SessionLabelTests
{
    [Fact]
    public void Runtime_Type_Label_Survives_A_Redeclare()
    {
        var sent = new List<(Ogs.Telemetry.FrameType, byte[])>();
        using var session = new OgsSession((t, d) => { lock (sent) sent.Add((t, d)); }, "label");
        session.DeclareObject("car", Ogs.Telemetry.Schema.ObjectType.Vehicle, "default label");
        session.Start(Now());
        session.SetTypeLabel("Ginetta G55"u8);
        session.BeginRedeclare();
        session.DeclareObject("car", Ogs.Telemetry.Schema.ObjectType.Vehicle, "default label");
        session.CommitRedeclare(Now());
        session.SetTypeLabel("Ginetta G55"u8);   // unchanged: a no-op that must not lose the label
        session.Tick(Now());
        Assert.Equal("Ginetta G55", SentFrames.Page0(sent).Objects[0].TypeLabel);
    }
}
}
