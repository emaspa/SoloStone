using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace SoloStone
{
    // solostone [command] [options]. The same lifecycle as the Windows launcher's command line, on the shared install and update code.
    internal static class LinuxProgram
    {
        const string Usage=@"Usage: solostone [command] [options]

Commands:
  play                     Start SoloStone (the default).
  setup --source <folder>  Copy your Hearthstone folder into SoloStone's Wine prefix and add the mod.
  desktop-entry            Add SoloStone to your applications menu again, e.g. after moving this folder.
  find-clients             List Hearthstone folders found in the usual Wine, Lutris, Bottles and Steam places.
  status                   Show the installed version and whether this client can play.
  check-client [--source <folder>]
  recover                  Finish or remove a copy an interrupted setup left.
  restore-backup --id <id> Restore a save backup.
  uninstall [--delete-saves]

Options:
  --prefix <folder>  The Wine prefix (setup only; default ~/.local/share/SoloStone/prefix).
  --wine <program>   The wine program that runs the game (setup only; default: wine on PATH).
  --accept-warning   Continue past a client warning (setup, play).
  --json             Machine-readable output (status, check-client).
  --desktop          No terminal (play only; the applications-menu entry uses it): a notification when the game
                     starts, dialogs for errors and client warnings, and the messages in ~/.local/state/solostone/launcher.log.";

        static int Main(string[] args) {
            string command="play", source=null, prefixPath=null, wine=null, backupId=null; bool json=false, acceptWarning=false, deleteSaves=false, desktop=false;
            Action<string> progress=line=>Console.WriteLine(LinuxMessages.Adapt(line));
            try {
                int i=0;
                Func<string,string> value=name=>{ if(++i>=args.Length) throw new InvalidOperationException(name+" needs a value."); return args[i]; };
                if(args.Length>0 && !args[0].StartsWith("-")) { command=args[0]; i=1; }
                for(;i<args.Length;i++) switch(args[i]) {
                    case "--source": source=value("--source"); break;
                    case "--prefix": prefixPath=value("--prefix"); break;
                    case "--wine": wine=value("--wine"); break;
                    case "--id": backupId=value("--id"); break;
                    case "--json": json=true; break;
                    case "--accept-warning": acceptWarning=true; break;
                    case "--delete-saves": deleteSaves=true; break;
                    case "--desktop": desktop=true; break;
                    case "-h": case "--help": Console.WriteLine(Usage); return 0;
                    default: throw new InvalidOperationException("Unknown option: "+args[i]+"\n\n"+Usage);
                }
                if(command=="launch") command="play";
                if(!new[]{"play","setup","desktop-entry","find-clients","status","check-client","recover","restore-backup","uninstall"}.Contains(command)) throw new InvalidOperationException("Unknown command: "+command+"\n\n"+Usage);
                if((prefixPath!=null || wine!=null) && command!="setup") throw new InvalidOperationException("--prefix and --wine are only valid with setup.");
                if(source!=null && command!="setup" && command!="check-client") throw new InvalidOperationException("--source is only valid with setup or check-client.");
                if(command=="restore-backup" && backupId==null) throw new InvalidOperationException("Use restore-backup --id <backup id>.");
                if(desktop && command!="play") throw new InvalidOperationException("--desktop is only valid with play.");
                if(desktop) { var log=Desktop.OpenLog(); progress=line=>log(LinuxMessages.Adapt(line)); }
                return Run(command,source,prefixPath,wine,backupId,json,acceptWarning,deleteSaves,progress,desktop);
            } catch(Exception e) {
                string message=LinuxMessages.Adapt(Messages.Friendly(e));
                if(desktop) { progress(message); Desktop.Error(message,Icon); }
                else Console.Error.WriteLine(message);
                return e is IOException || e is UnauthorizedAccessException ? 5 : 1;
            }
        }

        static string Icon => Path.Combine(AppContext.BaseDirectory,"solostone.png");

        static int Run(string command,string source,string prefixPath,string wine,string backupId,bool json,bool acceptWarning,bool deleteSaves,Action<string> progress,bool desktop) {
            if(command=="desktop-entry") { DesktopEntry.Write(Environment.ProcessPath,Icon); progress("Added SoloStone to your applications menu: "+DesktopEntry.FilePath); return 0; }
            if(command=="find-clients") { foreach(string found in LinuxHost.FindClients()) Console.WriteLine(found); return 0; }
            string bundle=AppContext.BaseDirectory.TrimEnd('/');
            var config=LinuxConfig.Load();
            if(command=="setup") {
                if(config!=null && File.Exists(Path.Combine(new WinePrefix(config.Prefix,config.Wine).Root,"install.json")))
                    throw new InvalidOperationException("SoloStone is already set up in "+config.Prefix+". Uninstall it first to set it up again.");
                config=new LinuxConfig { Prefix=Path.GetFullPath(prefixPath??config?.Prefix??LinuxConfig.DefaultPrefix), Wine=LinuxConfig.FindWine(wine??config?.Wine??"wine") };
            }
            else if(config==null) throw new InvalidOperationException("SoloStone is not set up yet. Run: solostone setup --source <your Hearthstone folder>");
            var prefix=new WinePrefix(config.Prefix,config.Wine);
            if(command=="setup" && !prefix.Exists) { _=Sandbox.Command; prefix.Create(progress); }
            if(!prefix.Exists) throw new InvalidOperationException("SoloStone's Wine prefix is missing: "+prefix.Path+". Uninstall, then set SoloStone up again.");
            prefix.CheckDrives();
            // install.json keeps the game's spelling of its paths (C:\users\...), which the plugin reads inside the game.
            RecordPaths.ToDisk=prefix.ToWine; RecordPaths.FromDisk=prefix.FromWine;
            string root=prefix.Root;
            if(command=="check-client") {
                var verdict=CheckClient.Run(root,source,TestedClient.Compiled(),UpdateConfig.Compiled(),new NoBindingManifest(),source!=null?LinuxHost.IdleFailures(prefix):new string[0]);
                progress(json?verdict.ToJson():verdict.Message); return 0;
            }
            Directory.CreateDirectory(root);
            using(Lease.Open(root,command=="play"?"SoloStone is already running.":null)) {
                var engine=new UpdateEngine(root,new LinuxUpdateHost(prefix),progress);
                if(command=="status") {
                    var report=StatusReport.Build(root,engine,TestedClient.Compiled(),UpdateConfig.Compiled(),new NoBindingManifest(),LinuxHost.IdleFailures(prefix));
                    progress(json?report.ToJson():(report.Installed?"Installed: "+report.ReleaseVersion+". Client: "+report.ClientVersion+". "+report.Verdict?.Message:"Not installed. "+report.InstallProblem));
                    if(!json) progress("Wine prefix: "+prefix.Path+"\nWine: "+prefix.Wine);
                    return 0;
                }
                if(command=="setup") {
                    LinuxHost.RequireIdle(prefix);
                    if(source==null) {
                        string[] found=LinuxHost.FindClients();
                        if(found.Length!=1) throw new InvalidOperationException(found.Length==0
                            ? "No Hearthstone folder was found. Pass it with --source <folder> (the folder that holds Hearthstone.exe)."
                            : "Several Hearthstone folders were found; choose one with --source:\n  "+string.Join("\n  ",found));
                        source=found[0];
                    }
                    // Saved before the first write, so recover and uninstall can find a copy this setup leaves half done.
                    config.Save();
                    progress("Preparing your offline copy of "+source+"…");
                    string last=null;
                    Installer.Setup(new SetupOptions { Source=source, Root=root, Bundle=bundle, SaveRoot=prefix.SaveRoot, Registered=LinuxHost.Registered,
                        Check=facts=>ClientGate.Evaluate(facts,TestedClient.Compiled(),new ManifestStore(root,UpdateConfig.Compiled()).Current(),new NoBindingManifest(),LinuxHost.IdleFailures(prefix)),
                        AcceptedWarning=acceptWarning, Progress=step=>{ if(step.Message!=last) progress(last=step.Message); } });
                    if(prefix.InstallDxvk(bundle)) progress("Installed DXVK in SoloStone's Wine prefix.");
                    prefix.InstallImguiFont(bundle);
                    try { DesktopEntry.Write(Environment.ProcessPath,Icon); progress("Added SoloStone to your applications menu."); }
                    catch(Exception e) when(e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException) { progress("SoloStone could not add itself to your applications menu: "+e.Message); }
                    progress("Setup complete. Start SoloStone from your applications menu, or with: solostone play");
                    return 0;
                }
                if(command=="recover") { LinuxHost.RequireIdle(prefix); Installer.Recover(root,bundle,LinuxHost.Registered,prefix.SaveRoot,step=>progress(step.Message)); return 0; }
                if(command=="uninstall") {
                    LinuxHost.RequireIdle(prefix);
                    if(File.Exists(Path.Combine(root,"install.json"))) {
                        // The prefix's registry holds the copy's settings; deleting the prefix removes them. LocalLow is a folder like any other.
                        Installer.Uninstall(root,LinuxHost.Registered,prefix.LocalData,null,prefix.LocalLow);
                    }
                    if(deleteSaves) Installer.DeleteSaves(root,prefix.SaveRoot);
                    DesktopEntry.Remove();
                    progress("Copied client removed. "+(deleteSaves?"Saves removed.":"Saves kept in "+prefix.SaveRoot+".")+" The Wine prefix stays in "+prefix.Path+".");
                    return 0;
                }
                if(engine.Resume()==ResumeOutcome.HandedOff) { progress(Messages.UpdateFinishing); return 0; }
                if(!File.Exists(Path.Combine(root,"install.json"))) throw new InvalidOperationException("Installation is incomplete. Run: solostone recover");
                var record=InstallRecord.Load(root);
                Safety.Destinations(record,LinuxHost.Registered);
                if(command=="restore-backup") { progress(engine.RestoreBackup(backupId).Message); return 0; }
                // Only now, holding the lease, is this the one launcher starting the game.
                if(desktop) Desktop.Notify("Checking your copy and starting the game…",Icon);
                return LinuxHost.Launch(prefix,record,bundle,engine,acceptWarning,progress,desktop?Desktop.Confirm:(Func<string,bool>)null);
            }
        }
    }
}
