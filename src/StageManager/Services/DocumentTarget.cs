using System;
using System.IO;
// Shared by the elevated stage and the ordinary-privilege companion. No executable or shell command targets.
internal static class DocumentTarget
{
    internal static bool Valid(string target) {
        if(String.IsNullOrWhiteSpace(target)||target.Length>2048||target.IndexOf('\n')>=0||target.IndexOf('\r')>=0)return false;
#if NET10_0_OR_GREATER
        Uri? uri;
#else
        Uri uri;
#endif
        if(Uri.TryCreate(target,UriKind.Absolute,out uri)&&(uri.Scheme=="http"||uri.Scheme=="https"))
            return uri.UserInfo.Length==0&&uri.Host.Length>0;
        try {
            if(!Path.IsPathRooted(target)||!String.Equals(Path.GetFullPath(target),target,StringComparison.OrdinalIgnoreCase))return false;
            string ext=Path.GetExtension(target).ToLowerInvariant();
            return Array.IndexOf(new string[]{".pdf",".doc",".docx",".xls",".xlsx",".ppt",".pptx",".txt",".md",".tex",".csv",".png",".jpg",".jpeg"},ext)>=0;
        }catch{return false;}
    }
}
