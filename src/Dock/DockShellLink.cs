using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

// Native UTF-16 Shell links avoid WScript's locale-dependent Automation path.
// Interface order/signatures: Microsoft's shobjidl_core.h IShellLinkW;
// persistence: https://learn.microsoft.com/windows/win32/shell/links
internal static class DockShellLink {
    [ComImport,Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkObject { }

    [ComImport,Guid("000214F9-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW {
        [PreserveSig] int GetPath([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder path,int capacity,IntPtr findData,uint flags);
        [PreserveSig] int GetIDList(out IntPtr idList);
        [PreserveSig] int SetIDList(IntPtr idList);
        [PreserveSig] int GetDescription([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder value,int capacity);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int GetWorkingDirectory([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder value,int capacity);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int GetArguments([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder value,int capacity);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int GetHotkey(out ushort hotkey);
        [PreserveSig] int SetHotkey(ushort hotkey);
        [PreserveSig] int GetShowCmd(out int showCommand);
        [PreserveSig] int SetShowCmd(int showCommand);
        [PreserveSig] int GetIconLocation([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder value,int capacity,out int index);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string value,int index);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string value,uint reserved);
        [PreserveSig] int Resolve(IntPtr window,uint flags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string value);
    }

    internal static string ReadTarget(string path) {
        object link=null;
        try {
            link=new ShellLinkObject();
            ((IPersistFile)link).Load(Path.GetFullPath(path),0); // STGM_READ; no Save or Resolve.
            var buffer=new StringBuilder(32768);
            int result=((IShellLinkW)link).GetPath(buffer,buffer.Capacity,IntPtr.Zero,0);
            if(result<0)Marshal.ThrowExceptionForHR(result);
            // S_FALSE represents a non-filesystem/empty target. Reject a filled
            // buffer as possibly truncated rather than using an ambiguous identity.
            return result==0&&buffer.Length<buffer.Capacity-1?buffer.ToString():"";
        } finally {if(link!=null&&Marshal.IsComObject(link))Marshal.ReleaseComObject(link);}
    }

    // Used only by owned command-line fixtures. Runtime importing never writes
    // shortcuts, resolves their targets, or launches anything through this helper.
    internal static void WriteFixture(string path,string target) {
        if(!Path.IsPathRooted(path)||!Path.IsPathRooted(target)||!Path.GetExtension(path).Equals(".lnk",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Fixture requires absolute shortcut and target paths.");
        object link=null;
        try {
            link=new ShellLinkObject();
            int result=((IShellLinkW)link).SetPath(Path.GetFullPath(target));
            if(result<0)Marshal.ThrowExceptionForHR(result);
            ((IPersistFile)link).Save(Path.GetFullPath(path),true);
        } finally {if(link!=null&&Marshal.IsComObject(link))Marshal.ReleaseComObject(link);}
    }
}
