#nullable enable
using System;

namespace OgsTsBridge.Sources
{
/// <summary>Creates a Windows .lnk through the shell's WScript.Shell
/// automation object (for patchers whose game needs a command-line switch).
/// Cold path; false where the shell object is unavailable.</summary>
internal static class ShellShortcut
{
    public static bool Create(string linkPath, string target, string arguments, string description)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type is null) return false;
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(type);
            var link = type.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { linkPath })!;
            var lt = link.GetType();
            void Set(string name, object value) =>
                lt.InvokeMember(name, System.Reflection.BindingFlags.SetProperty, null, link, new[] { value });
            Set("TargetPath", target);
            Set("Arguments", arguments);
            Set("Description", description);
            Set("WorkingDirectory", System.IO.Path.GetDirectoryName(target) ?? "");
            lt.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, link, null);
            return true;
        }
        catch (Exception ex) when (ex is System.Reflection.TargetInvocationException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
        finally
        {
            if (shell is not null && OperatingSystem.IsWindows())
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }
}
}
