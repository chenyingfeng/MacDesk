using System;
using System.IO;
internal static class TaskbarToggleQueue
{
    static string Folder(string root){return Path.Combine(root,"taskbar-toggles");}
    internal static void Request(string root) {
        string token=Guid.NewGuid().ToString("N"),folder=Folder(root);Directory.CreateDirectory(folder);
        string path=Path.Combine(folder,token+".toggle");File.WriteAllText(path+".pending",token);File.Move(path+".pending",path);
    }
    internal static void Consume(string root,Action toggle,bool discard) {
        string folder=Folder(root);if(!Directory.Exists(folder))return;
        foreach(string path in Directory.GetFiles(folder,"*.toggle")) {
            Guid id;if(!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path),"N",out id))continue;
            try {bool fresh=DateTime.UtcNow-File.GetLastWriteTimeUtc(path)<TimeSpan.FromSeconds(5);
                string value=File.ReadAllText(path);File.Delete(path);
                if(!discard&&fresh&&value==id.ToString("N"))toggle();
            }catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }
}
