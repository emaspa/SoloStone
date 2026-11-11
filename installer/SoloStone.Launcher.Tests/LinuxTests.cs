using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SoloStone;

static partial class Program
{
    const string NetDevHeader="Inter-|   Receive                                                |  Transmit\n"
        +" face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed\n";
    static void LinuxTests(string temp) {
        // The isolation the Linux launcher gives the game: /proc/net/dev lists loopback only. (The no-route canary is the plugin's, in OfflineProbe.)
        string loopback=NetDevHeader+"    lo:       0       0    0    0    0     0          0         0        0       0    0    0    0     0       0          0\n";
        string networked=loopback+"enp13s0: 21758956135 15679490    0  208    0     0          0     45313 5235610294 1811851    0    0    0     0       0          0\n";
        NetworkIsolation.Check(loopback); NetworkIsolation.Check(loopback.Replace("\n","\r\n"));
        Check(string.Join(",",NetworkIsolation.Interfaces(networked))=="lo,enp13s0","interface names read from /proc/net/dev");
        RejectsBecause(()=>NetworkIsolation.Check(networked),"enp13s0","an interface besides loopback is refused and named");
        foreach(string junk in new[]{null,"","lo: 0 0\n",NetDevHeader,NetDevHeader+"  eth0: 1 2\n",NetDevHeader+"no colon here\n"})
            RejectsBecause(()=>NetworkIsolation.Check(junk),"could not read","unreadable or loopback-less /proc/net/dev refused: "+(junk??"null").Replace("\n","|"));
        Check(!NetworkIsolation.UnderWine,"the test host is not Wine");
        // Off Wine there is no /proc to read through Wine's paths, so the Wine protection refuses: it can never pass by accident.
        RejectsBecause(NetworkIsolation.Verify,"could not read","isolation check off Wine refuses");

        // install.json keeps the game's spelling; the Linux launcher maps it to the prefix's folders and back.
        var prefix=new WinePrefix("/x/prefix","/usr/bin/wine");
        Check(prefix.ToWine("/x/prefix/drive_c/users/a/AppData/Local/SoloStone")==@"C:\users\a\AppData\Local\SoloStone","prefix path is the C: drive");
        Check(prefix.ToWine("/x/prefix/drive_c")==@"C:\","drive_c itself is C:\\");
        Check(prefix.ToWine("/home/a/games/Hearthstone")==@"Z:\home\a\games\Hearthstone","any other path is Z:");
        Check(prefix.ToWine("/x/prefix/drive_cx/y")==@"Z:\x\prefix\drive_cx\y","a sibling of drive_c is not C:");
        Check(prefix.FromWine(@"C:\users\a\AppData\Local\SoloStone")=="/x/prefix/drive_c/users/a/AppData/Local/SoloStone","C: maps back into the prefix");
        Check(prefix.FromWine(@"c:\users")=="/x/prefix/drive_c/users","drive letters in any case");
        Check(prefix.FromWine(@"Z:\home\a\games\Hearthstone")=="/home/a/games/Hearthstone","Z: maps back to /");
        foreach(string bad in new[]{null,"",@"D:\games",@"C:\users\..\..\..\etc",@"C:\.\x","relative",@"\\server\share",@"C:/users/a",@"C:"})
            Refuses(()=>prefix.FromWine(bad),"record path the Linux launcher can't map: "+(bad??"null"));

        string root=Path.Combine(temp,"linux-record","drive_c","users","a","AppData","Local","SoloStone");
        var mapped=new WinePrefix(Path.Combine(temp,"linux-record"),"/usr/bin/wine");
        Directory.CreateDirectory(root);
        var record=new InstallRecord { Schema=InstallRecord.CurrentSchema, InstallId=Guid.NewGuid().ToString("N"), Root=root, Source="/home/a/games/Hearthstone",
            ClientPath=Path.Combine(root,"client"), ClientVersion="37.0.0", Files=new Dictionary<string,string>{{"Hearthstone.exe",new string('A',64)}} };
        try {
            RecordPaths.ToDisk=mapped.ToWine; RecordPaths.FromDisk=mapped.FromWine;
            string file=Path.Combine(root,"install.json");
            Json.Save(file,record);
            string text=Encoding.UTF8.GetString(File.ReadAllBytes(file));
            Check(text.Contains(@"C:\\users\\a\\AppData\\Local\\SoloStone\\client") && text.Contains(@"Z:\\home\\a\\games\\Hearthstone") && !text.Contains(temp),"install.json holds the game's spelling");
            Check(record.Root==root && record.Source=="/home/a/games/Hearthstone","saving leaves the launcher's own record in host paths");
            var read=InstallRecord.FromBytes(File.ReadAllBytes(file));
            Check(read.Root==root && read.ClientPath==Path.Combine(root,"client") && read.Source=="/home/a/games/Hearthstone","the plain reader maps back");
            var parsed=Json.Read<InstallRecord>(file);
            Check(parsed.Root==root && parsed.ClientPath==Path.Combine(root,"client"),"the data contract reader maps back (update journal)");
        } finally { RecordPaths.ToDisk=path=>path; RecordPaths.FromDisk=path=>path; }
        // DXVK goes into the prefix only, and is switched on by the DLL overrides of the game's launch.
        Check(prefix.Variables(true,true)["WINEDLLOVERRIDES"].EndsWith(";winhttp=n,b;d3d11,dxgi=n,b") && !prefix.Variables(true)["WINEDLLOVERRIDES"].Contains("d3d11"),"DXVK overrides only when DXVK is on");
        Check(!prefix.Variables(false)["WINEDLLOVERRIDES"].Contains("winhttp") && prefix.Variables(false)["WINEDLLOVERRIDES"].Contains("winemenubuilder.exe=d"),"no loader outside the game; no menu entries from Wine");
        string dxvkBundle=Path.Combine(temp,"linux-bundle"), dxvkPrefix=Path.Combine(temp,"linux-prefix");
        Directory.CreateDirectory(Path.Combine(dxvkBundle,"dxvk")); Directory.CreateDirectory(Path.Combine(dxvkPrefix,"drive_c","windows","system32"));
        var dxvkHost=new WinePrefix(dxvkPrefix,"/usr/bin/wine");
        Check(!dxvkHost.InstallDxvk(dxvkBundle),"no DXVK in the download: Wine's own Direct3D");
        foreach(string name in WinePrefix.DxvkFiles) File.WriteAllText(Path.Combine(dxvkBundle,"dxvk",name),"dxvk "+name);
        File.WriteAllText(Path.Combine(dxvkPrefix,"drive_c","windows","system32","dxgi.dll"),"wine's own");
        Check(dxvkHost.InstallDxvk(dxvkBundle) && WinePrefix.DxvkFiles.All(name=>File.ReadAllText(Path.Combine(dxvkPrefix,"drive_c","windows","system32",name))=="dxvk "+name),"DXVK replaces Wine's DLLs in the prefix");
        Check(dxvkHost.InstallDxvk(dxvkBundle),"installing again is a no-op that still says DXVK is on");
        // The IMGUI font goes into the prefix's Fonts folder, which a new prefix may not have yet.
        string prefixFont=Path.Combine(dxvkPrefix,"drive_c","windows","Fonts",WinePrefix.ImguiFont);
        Check(!dxvkHost.InstallImguiFont(dxvkBundle) && !File.Exists(prefixFont),"no font in the download: nothing installed");
        Directory.CreateDirectory(Path.Combine(dxvkBundle,"fonts")); File.WriteAllText(Path.Combine(dxvkBundle,"fonts",WinePrefix.ImguiFont),"font v1");
        Check(dxvkHost.InstallImguiFont(dxvkBundle) && File.ReadAllText(prefixFont)=="font v1","the font goes into the prefix's Fonts folder");
        File.WriteAllText(Path.Combine(dxvkBundle,"fonts",WinePrefix.ImguiFont),"font v2");
        Check(dxvkHost.InstallImguiFont(dxvkBundle) && File.ReadAllText(prefixFont)=="font v2","a newer download replaces it");

        // A new prefix runs "wine wineboot --init" with Wine's own Direct3D: with two params overloads, "wineboot" once became the DXVK log.
        try {
            Sandbox.Command=new[]{"sandbox","--"};
            var boot=prefix.CreateStart();
            Check(boot.FileName=="sandbox" && boot.ArgumentList.SequenceEqual(new[]{"--","/usr/bin/wine","wineboot","--init"}) && (!boot.Environment.TryGetValue("DXVK_LOG_PATH",out string bootLog) || bootLog!="wineboot")
                && !boot.Environment["WINEDLLOVERRIDES"].Contains("d3d11"),"prefix creation runs wineboot --init without DXVK");
            var game=prefix.Start("/x/client",true,@"C:\reports",new[]{"Hearthstone.exe","-x"});
            Check(game.ArgumentList.SequenceEqual(new[]{"--","/usr/bin/wine","Hearthstone.exe","-x"}) && game.Environment["DXVK_LOG_PATH"]==@"C:\reports" && game.Environment["WINEDLLOVERRIDES"].Contains("d3d11"),"the game's start takes the DXVK log folder");
            var plain=prefix.Start("/x",false,"winecfg","/v");
            Check(plain.ArgumentList.SequenceEqual(new[]{"--","/usr/bin/wine","winecfg","/v"}) && !plain.Environment["WINEDLLOVERRIDES"].Contains("d3d11"),"string arguments never bind to the DXVK overload");
        } finally { Sandbox.Command=null; }

        // The shared install code names the Windows launcher's commands; the Linux launcher shows its own.
        string step=LinuxMessages.Adapt(InstallRecord.RecoveryStep("/x/prefix/drive_c/users/a/AppData/Local/SoloStone"));
        Check(step.StartsWith("Run solostone recover. ") && step.Contains("run solostone setup again") && !step.Contains("SoloStone.exe") && !step.Contains("--root"),"recovery advice names solostone recover");
        Check(LinuxMessages.Adapt("Committed client integrity failed. No files were changed. Run SoloStone.exe uninstall --root \"/r\"; saves stay. Then rerun the installer.")
            =="Committed client integrity failed. No files were changed. Run solostone uninstall; saves stay. Then run solostone setup again.","uninstall advice names solostone uninstall");
        Check(LinuxMessages.Adapt("Recovery complete. Run SoloStone.exe firewall --apply --root \"/r\", then launch; or run uninstall with the same --root.").EndsWith("solostone play"),"no firewall step on Linux");
        Check(!LinuxMessages.Adapt(Messages.Friendly(new UnauthorizedAccessException())).Contains("Windows") && LinuxMessages.Adapt("Copying files…")=="Copying files…","other messages pass through");

        // The game's console output goes to a log, not the launcher's terminal; arguments, environment and exit code pass through.
        if(File.Exists("/bin/sh")) {
            string log=Path.Combine(temp,"wine output.log"); File.WriteAllText(log,"earlier\n");
            var inner=new System.Diagnostics.ProcessStartInfo("/bin/sh") { UseShellExecute=false, WorkingDirectory=temp };
            foreach(string part in new[]{"-c","echo \"$1|$2|$SOLO_TEST|$(pwd)\"; echo err >&2; exit 46","sh","a b","$HOME \"q\""}) inner.ArgumentList.Add(part);
            inner.Environment["SOLO_TEST"]="set";
            var wrapped=WinePrefix.OutputTo(inner,log); wrapped.RedirectStandardOutput=true; wrapped.RedirectStandardError=true;
            using(var run=System.Diagnostics.Process.Start(wrapped)) {
                string shown=run.StandardOutput.ReadToEnd()+run.StandardError.ReadToEnd(); run.WaitForExit();
                Check(run.ExitCode==46 && shown=="","exit code passes through; nothing reaches the terminal");
            }
            Check(File.ReadAllText(log)=="earlier\na b|$HOME \"q\"|set|"+temp+"\nerr\n","output and errors are appended to the log, arguments and environment intact");
        }

        // The applications-menu entry starts this launcher wherever the player unpacked it, quoted as the Desktop Entry spec asks.
        Check(DesktopEntry.Quote("/home/a b/SoloStone/solostone")=="\"/home/a b/SoloStone/solostone\"","a path with a space is quoted");
        // Quoting escapes come after the key file's string escapes, so the file holds two backslashes per quoting backslash.
        Check(DesktopEntry.Quote("/x/$HOME`\"\\y")==@"""/x/\\$HOME\\`\\""\\\\y""","$ ` \" and \\ are escaped for both layers");
        RejectsBecause(()=>DesktopEntry.Quote("/games/100%f/solostone"),"holds %","a path with % is refused: GLib and KDE can't start it, %% or not");
        string entry=DesktopEntry.Text("/opt/solo stone/solostone","/opt/solo stone/solostone.png");
        Check(entry.StartsWith("[Desktop Entry]\n") && entry.Contains("\nExec=\"/opt/solo stone/solostone\" play --desktop\n") && entry.Contains("\nIcon=/opt/solo stone/solostone.png\n") && entry.Contains("\nTerminal=false\n"),"desktop entry runs play --desktop with no terminal");

        // Without a terminal, errors and client warnings go to kdialog on KDE and zenity elsewhere, with the message as one argument.
        string tricky="Path <C:\\a & b> \"q\" $HOME";
        var kde=Desktop.Dialogs("error",tricky,true); var gnome=Desktop.Dialogs("confirm",tricky,false);
        Check(kde[0][0]=="kdialog" && kde[1][0]=="zenity" && gnome[0][0]=="zenity" && gnome[1][0]=="kdialog","the desktop's own dialog program first, the other as fallback");
        Check(kde[0].SequenceEqual(new[]{"kdialog","--title","SoloStone","--error",tricky}) && gnome[1].Contains("--warningcontinuecancel"),"kdialog error and continue/cancel dialogs");
        Check(kde[1].Contains("--no-markup") && kde[1].Contains("--text="+tricky) && gnome[0].Contains("--question") && gnome[0].Contains("--no-markup"),"zenity shows < and & as text, not markup");
        string oldState=Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        try {
            Environment.SetEnvironmentVariable("XDG_STATE_HOME",Path.Combine(temp,"state"));
            Check(Desktop.LogPath==Path.Combine(temp,"state","solostone","launcher.log"),"the launcher log follows XDG_STATE_HOME");
            Directory.CreateDirectory(Path.GetDirectoryName(Desktop.LogPath)); File.WriteAllText(Desktop.LogPath,"last start\n");
            var log=Desktop.OpenLog(); log("Starting SoloStone…");
            string logged=File.ReadAllText(Desktop.LogPath);
            Check(logged.StartsWith("last start\n") && logged.EndsWith(" Starting SoloStone…\n"),"a second start appends, so it can't wipe the running game's log; lines are time-stamped");
            File.WriteAllText(Desktop.LogPath,new string('x',(int)Desktop.LogLimit+1));
            Desktop.OpenLog()("fresh");
            Check(new FileInfo(Desktop.LogPath+".old").Length==Desktop.LogLimit+1 && File.ReadAllText(Desktop.LogPath).EndsWith(" fresh\n") && new FileInfo(Desktop.LogPath).Length<100,"past the limit the log moves to launcher.log.old");
            Environment.SetEnvironmentVariable("XDG_STATE_HOME","relative/state");
            Check(Desktop.LogPath.EndsWith("/.local/state/solostone/launcher.log"),"a relative XDG_STATE_HOME is ignored, as the spec says");
        } finally { Environment.SetEnvironmentVariable("XDG_STATE_HOME",oldState); }
        Check(!DesktopEntry.Text("/x/solostone",null).Contains("Icon="),"no icon line without an icon");
        Check(InstallRecord.FromBytes(Encoding.UTF8.GetBytes("{\"schema\":2,\"root\":\"/r\",\"clientPath\":\"/r/client\"}")).Root=="/r","unmapped by default");
    }
}
