using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace SoloStone
{
    // SoloStone's own Wine prefix. The copy and the install root sit where Windows puts them, %LOCALAPPDATA%\SoloStone on the prefix's C: drive,
    // because the plugin checks every path with Windows rules inside the game and Wine's C: drive passes them (Z: does not).
    // Every Wine process for this prefix runs in a network namespace whose only interface is loopback (Sandbox). wineserver creates the sockets,
    // so it must start inside that namespace too: a server already running for the prefix is refused (ServerRunning).
    internal sealed class WinePrefix
    {
        internal readonly string Path, Wine;
        internal WinePrefix(string path,string wine) { Path=Unix(path); Wine=wine; }
        internal string DriveC => System.IO.Path.Combine(Path,"drive_c");
        internal bool Exists => File.Exists(System.IO.Path.Combine(Path,"system.reg"));
        // The prefix's Windows user, e.g. drive_c/users/<name>. Wine names it after the Unix account, Proton "steamuser".
        internal string User {
            get {
                string users=System.IO.Path.Combine(DriveC,"users");
                var names=Directory.Exists(users) ? Directory.GetDirectories(users).Select(System.IO.Path.GetFileName).Where(n=>n!="Public").ToArray() : new string[0];
                if(names.Contains(Environment.UserName)) return Environment.UserName;
                if(names.Length==1) return names[0];
                throw new InvalidOperationException("SoloStone's Wine prefix has "+(names.Length==0?"no user folder":"several user folders")+" in "+users+". Use a new prefix.");
            }
        }
        internal string LocalData => System.IO.Path.Combine(DriveC,"users",User,"AppData","Local");
        internal string LocalLow => System.IO.Path.Combine(DriveC,"users",User,"AppData","LocalLow");
        internal string Root => System.IO.Path.Combine(LocalData,"SoloStone");
        internal string SaveRoot => PublicNames.SaveRoot(LocalData);
        internal string Server => Sibling("wineserver");
        string Sibling(string name) {
            string dir=System.IO.Path.GetDirectoryName(Wine);
            string beside=string.IsNullOrEmpty(dir)?null:System.IO.Path.Combine(dir,name);
            return beside!=null && File.Exists(beside) ? beside : name;
        }
        static string Unix(string path) => System.IO.Path.GetFullPath(path).TrimEnd('/');

        // The game's spelling of a host path, and back. Only C: (drive_c) and Z: (/) are used, and their links are checked first (CheckDrives).
        internal string ToWine(string unix) {
            string full=Unix(unix);
            if(full==DriveC) return @"C:\";
            if(full.StartsWith(DriveC+"/",StringComparison.Ordinal)) return @"C:\"+full.Substring(DriveC.Length+1).Replace('/','\\');
            return "Z:"+full.Replace('/','\\');
        }
        internal string FromWine(string wine) {
            if(wine==null || wine.Length<3 || wine[1]!=':' || wine[2]!='\\' || wine.IndexOf('/')>=0) throw new InvalidOperationException("SoloStone installation record holds a path it can't read: "+wine);
            string rest=wine.Substring(3).Replace('\\','/');
            if(rest.Split('/').Any(part=>part==".." || part==".")) throw new InvalidOperationException("SoloStone installation record holds a path it can't read: "+wine);
            char drive=char.ToUpperInvariant(wine[0]);
            if(drive=='C') return rest.Length==0?DriveC:System.IO.Path.Combine(DriveC,rest);
            if(drive=='Z') return "/"+rest;
            throw new InvalidOperationException("SoloStone installation record holds a path on drive "+drive+": which the Linux launcher doesn't map.");
        }
        internal void CheckDrives() {
            foreach(var drive in new[]{("c:","../drive_c"),("z:","/")}) {
                string link=System.IO.Path.Combine(Path,"dosdevices",drive.Item1);
                string target=new FileInfo(link).LinkTarget;
                if(target!=drive.Item2) throw new InvalidOperationException("SoloStone's Wine prefix maps "+drive.Item1.ToUpperInvariant()+" to "+(target??"nothing")+", not "+drive.Item2+". Use a new prefix.");
            }
        }

        // DXVK runs the game's Direct3D 11 on Vulkan. Without it Wine's own translation (wined3d) is far slower. The Linux download ships it in dxvk/,
        // and it goes into this prefix's system32 like any Windows DLL; the game copy is never touched. SOLOSTONE_DXVK=0 leaves Wine's own for a test.
        internal static readonly string[] DxvkFiles={"d3d11.dll","dxgi.dll"};
        internal static bool DxvkWanted => Environment.GetEnvironmentVariable("SOLOSTONE_DXVK")!="0";
        internal bool InstallDxvk(string bundle) {
            string from=System.IO.Path.Combine(bundle,"dxvk");
            if(!DxvkWanted || !DxvkFiles.All(name=>File.Exists(System.IO.Path.Combine(from,name)))) return false;
            string system32=System.IO.Path.Combine(DriveC,"windows","system32");
            foreach(string name in DxvkFiles) Place(System.IO.Path.Combine(from,name),System.IO.Path.Combine(system32,name));
            return true;
        }
        // The mod's labels (brand label, FPS counter, ladder) use Unity's built-in font, which Unity loads by family name,
        // LegacyRuntime or else Arial, from C:\windows\Fonts. Windows has Arial; a Wine prefix has neither, so the labels draw their boxes but no
        // text. The Linux download ships Liberation Sans renamed LegacyRuntime (fonts/, made by package_linux.py) and it goes into this prefix's
        // Fonts folder. The game copy is never touched.
        internal const string ImguiFont="LegacyRuntime.ttf";
        internal bool InstallImguiFont(string bundle) {
            string source=System.IO.Path.Combine(bundle,"fonts",ImguiFont);
            if(!File.Exists(source)) return false;
            string fonts=System.IO.Path.Combine(DriveC,"windows","Fonts");
            Directory.CreateDirectory(fonts);
            Place(source,System.IO.Path.Combine(fonts,ImguiFont));
            return true;
        }
        // Copies a file into the prefix unless the same bytes are there already; the old file is replaced in one step.
        static void Place(string source,string target) {
            Safety.NoLinks(target);
            if(File.Exists(target) && Hashes.FileHash(target)==Hashes.FileHash(source)) return;
            File.Copy(source,target+".solostone-tmp",true); File.Move(target+".solostone-tmp",target,true);
        }
        internal Dictionary<string,string> Variables(bool game,bool dxvk=false) {
            var env=new Dictionary<string,string> {
                ["WINEPREFIX"]=Path,
                // winemenubuilder would add launcher entries that start the game outside the namespace. Mono and Gecko are not needed:
                // the game brings its own Mono, and without these overrides a new prefix asks to download them.
                ["WINEDLLOVERRIDES"]="winemenubuilder.exe=d;mscoree=;mshtml="+(game?";winhttp=n,b":"")+(dxvk?";d3d11,dxgi=n,b":""), // winhttp: Doorstop, which loads BepInEx
            };
            if(Environment.GetEnvironmentVariable("WINEDEBUG")==null) env["WINEDEBUG"]="-all";
            return env;
        }
        internal ProcessStartInfo Start(string workingDirectory,bool game,params string[] arguments) => Start(workingDirectory,game,null,arguments);
        // dxvkLog: the game's spelling of the folder for DXVK's log, which also turns DXVK on. Null keeps Wine's own Direct3D.
        // Not params: with both overloads params, Start(path,false,"wineboot","--init") binds here and Wine loses "wineboot".
        internal ProcessStartInfo Start(string workingDirectory,bool game,string dxvkLog,string[] arguments) {
            var start=new ProcessStartInfo(Sandbox.Command[0]) { UseShellExecute=false, WorkingDirectory=workingDirectory };
            foreach(string part in Sandbox.Command.Skip(1)) start.ArgumentList.Add(part);
            start.ArgumentList.Add(Wine);
            foreach(string part in arguments) start.ArgumentList.Add(part);
            foreach(var row in Variables(game,dxvkLog!=null)) start.Environment[row.Key]=row.Value;
            if(dxvkLog!=null) start.Environment["DXVK_LOG_PATH"]=dxvkLog;
            return start;
        }
        // Appends the program's console output to log. The shell opens the file and execs, so the program keeps its process id,
        // and its output still has a place to go if the launcher closes first.
        internal static ProcessStartInfo OutputTo(ProcessStartInfo start,string log) {
            var wrapped=new ProcessStartInfo("/bin/sh") { UseShellExecute=false, WorkingDirectory=start.WorkingDirectory };
            foreach(string part in new[]{"-c","exec \"$@\" >>\"$0\" 2>&1",log,start.FileName}.Concat(start.ArgumentList)) wrapped.ArgumentList.Add(part);
            wrapped.Environment.Clear();
            foreach(var row in start.Environment) wrapped.Environment[row.Key]=row.Value;
            return wrapped;
        }
        internal ProcessStartInfo CreateStart() => Start(Path,false,"wineboot","--init");
        // A new prefix is made inside the namespace too, so no wineserver for it ever had network.
        internal void Create(Action<string> progress) {
            if(ServerRunning()) throw new InvalidOperationException(ServerRunningMessage);
            Directory.CreateDirectory(Path);
            progress("Creating SoloStone's Wine prefix in "+Path+"…");
            int exit;
            using(var boot=Process.Start(CreateStart())) { boot.WaitForExit(); exit=boot.ExitCode; }
            WaitServer(); // The registry files are written when the server exits.
            if(exit!=0 || !Exists) throw new InvalidOperationException("Wine could not create SoloStone's prefix (exit "+exit+").");
        }
        // Waits for the prefix's wineserver to exit, so the next start makes a new one inside a new namespace.
        internal void WaitServer() {
            if(!ServerRunning()) return;
            var start=new ProcessStartInfo(Server,"-w") { UseShellExecute=false };
            start.Environment["WINEPREFIX"]=Path;
            try { using(var wait=Process.Start(start)) wait.WaitForExit(60000); } catch(System.ComponentModel.Win32Exception) { }
        }
        internal void KillServer() {
            var start=new ProcessStartInfo(Server,"-k") { UseShellExecute=false };
            start.Environment["WINEPREFIX"]=Path;
            try { using(var kill=Process.Start(start)) kill.WaitForExit(30000); } catch(System.ComponentModel.Win32Exception) { }
        }

        internal string ServerRunningMessage => "A program is still running in SoloStone's Wine prefix. Close it, or run: WINEPREFIX=\""+Path+"\" \""+Server+"\" -k";
        // wineserver listens on /tmp/.wine-<uid>/server-<dev>-<inode>/socket, named after the prefix folder. A socket that accepts means a server is up.
        internal bool ServerRunning() {
            if(!Directory.Exists(Path)) return false;
            string id=Identity(Path);
            string socket=System.IO.Path.Combine("/tmp",".wine-"+getuid(),"server-"+id,"socket");
            if(!File.Exists(socket)) return false;
            try {
                using(var client=new Socket(AddressFamily.Unix,SocketType.Stream,ProtocolType.Unspecified)) { client.Connect(new UnixDomainSocketEndPoint(socket)); return true; }
            } catch(SocketException) { return false; }
        }
        // st_dev and st_ino in hex, as wineserver formats them.
        static string Identity(string path) {
            var start=new ProcessStartInfo("stat",new[]{"-L","-c","%d %i",path}) { UseShellExecute=false, RedirectStandardOutput=true };
            using(var stat=Process.Start(start)) {
                string[] parts=stat.StandardOutput.ReadToEnd().Trim().Split(' ');
                stat.WaitForExit();
                ulong dev, ino;
                if(stat.ExitCode!=0 || parts.Length!=2 || !ulong.TryParse(parts[0],out dev) || !ulong.TryParse(parts[1],out ino))
                    throw new InvalidOperationException("SoloStone could not read its Wine prefix folder "+path+".");
                return dev.ToString("x")+"-"+ino.ToString("x");
            }
        }
        [DllImport("libc")] static extern uint getuid();
    }

    // The network namespace. unshare (util-linux) first; bwrap where unshare may not create user namespaces (Ubuntu's AppArmor allows bwrap).
    internal static class Sandbox
    {
        static readonly string[][] Choices={
            new[]{"unshare","--user","--map-current-user","--net","--"},
            new[]{"bwrap","--dev-bind","/","/","--unshare-net","--die-with-parent","--"},
        };
        static string[] chosen;
        // Tests set it, so building a command needs no namespace on the test host; null probes again.
        internal static string[] Command { get {
            if(chosen!=null) return chosen;
            foreach(var choice in Choices) if(Works(choice)) return chosen=choice;
            throw new InvalidOperationException("SoloStone could not create a network namespace, so it can't start the game without network. Install util-linux (unshare) or bubblewrap (bwrap), and allow unprivileged user namespaces.");
        } set => chosen=value; }
        static bool Works(string[] choice) {
            var start=new ProcessStartInfo(choice[0]) { UseShellExecute=false, RedirectStandardError=true, RedirectStandardOutput=true };
            foreach(string part in choice.Skip(1)) start.ArgumentList.Add(part);
            // Inside, only loopback may be listed: the same rule the game applies.
            start.ArgumentList.Add("cat"); start.ArgumentList.Add("/proc/net/dev");
            try {
                using(var probe=Process.Start(start)) {
                    string text=probe.StandardOutput.ReadToEnd(); probe.StandardError.ReadToEnd();
                    if(!probe.WaitForExit(10000)) { probe.Kill(); return false; }
                    if(probe.ExitCode!=0) return false;
                    NetworkIsolation.Check(text);
                    return true;
                }
            } catch(Exception e) when(e is System.ComponentModel.Win32Exception || e is InvalidOperationException) { return false; }
        }
    }
}
