using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SoloStone;

static partial class Program
{
    // 2026-11-07, a real install: a player in the Administrators group (normal filtered token, not elevated) was refused by the isolation guard, and the log
    // said only "rejected". Unity's Mono answers IsInRole(Administrator) from the token's group list, deny-only groups included.
    sealed class GuardCalls { public int Isolated, Elevated, Group; public bool IsolatedResult=true, ElevatedResult, GroupResult; }
    static OfflineGuard.GuardVerdict Judge(bool release,bool deck,bool installed,GuardCalls c)
        => OfflineGuard.Judge(release,deck,installed,()=>{c.Isolated++;return c.IsolatedResult;},()=>{c.Elevated++;return c.ElevatedResult;},()=>{c.Group++;return c.GroupResult;});
    static string Why(string[] args,bool release,bool installerBuild,bool deck,OfflineGuard.GuardVerdict v) => OfflineGuard.RejectionReasons(args,release,installerBuild,deck,v);

    static void GuardDiagnosisRegressions() {
        string[] withRoot={"-launch","-hs-native-ui-run","SECRET-RUN","-hs-installer-root",@"C:\"+@"Users\player\AppData\Local\SoloStone","-hs-profile-relaunch-token","SECRET-TOKEN"};
        string[] bare={"-launch","-uid","SoloStone"};
        // The predicate. Public build: a member of Administrators at normal integrity (not elevated) is allowed and the group list is never read.
        var c=new GuardCalls { GroupResult=true };
        var v=Judge(true,false,true,c);
        Check(!v.Rejected && c.Group==0 && c.Isolated==0,"public guard: in the Administrators group, not elevated -> allowed");
        // Owner 2026-11-08: a public build runs elevated. With UAC off the token is always elevated, and refusing locked those players out.
        c=new GuardCalls { ElevatedResult=true, GroupResult=true };
        v=Judge(true,false,true,c);
        Check(!v.Rejected && !v.Elevated && c.Elevated==0 && c.Group==0,"public guard: elevated -> allowed, elevation never asked");
        Check(TokenElevation.Decide(true,1),"UAC off: the full token reads as elevated");
        c=new GuardCalls { ElevatedResult=TokenElevation.Decide(true,1) };
        Check(!Judge(true,false,true,c).Rejected,"public guard: UAC off (elevated administrator) -> allowed");
        // Hook builds launched by the installer (lab) still refuse elevation.
        c=new GuardCalls { ElevatedResult=true };
        Check(Judge(false,false,true,c).Rejected && Judge(false,false,true,c).Elevated,"hook build: elevated -> refused");
        c=new GuardCalls();
        Check(Judge(true,false,false,c).NotInstalled && Judge(true,false,false,c).Rejected,"public guard: no installer admission -> refused");
        c=new GuardCalls();
        Check(Judge(true,true,true,c).DeckInPublicBuild && c.Elevated==0 && c.Group==0 && c.Isolated==0,"public guard: Deck selector refused without any check");
        // Lab: a Deck launch never calls the elevation or group check (Wine tokens carry Administrators; Proton's Mono may throw).
        c=new GuardCalls { ElevatedResult=true, GroupResult=true };
        v=Judge(false,true,false,c);
        Check(!v.Rejected && c.Elevated==0 && c.Group==0 && c.Isolated==1,"lab Deck branch never calls the admin checks");
        c=new GuardCalls { IsolatedResult=false };
        Check(Judge(false,true,false,c).LabCheckFailed && c.Elevated==0 && c.Group==0,"lab Deck branch: failed isolation refused, still no admin checks");
        // Lab player launches keep the group check, and an installed launch skips the account check.
        c=new GuardCalls { GroupResult=true };
        v=Judge(false,false,false,c);
        Check(v.Rejected && v.InAdminGroup && !v.Elevated && c.Isolated==1,"lab guard: the Administrators group still refuses");
        c=new GuardCalls();
        Check(!Judge(false,false,true,c).Rejected && c.Isolated==0,"lab guard: an installed launch skips the account check");
        c=new GuardCalls { IsolatedResult=false };
        Check(Judge(false,false,false,c).LabCheckFailed,"lab guard: wrong account or path refused");
        // The log line comes from the same verdict.
        c=new GuardCalls(); string absent=Why(bare,true,true,false,Judge(true,false,false,c));
        Check(absent.Contains("-hs-installer-root is absent") && !absent.Contains("elevated") && !absent.Contains("Deck"),"guard log: selector absent");
        string nullResolve=Why(withRoot,true,true,false,Judge(true,false,false,new GuardCalls()));
        Check(nullResolve.Contains("returned null") && !nullResolve.Contains("absent"),"guard log: admission null with the selector present");
        Check(Why(withRoot,true,false,false,Judge(true,false,false,new GuardCalls())).Contains("no installer mode"),"guard log: plugin built without installer mode");
        Check(Why(withRoot,true,true,true,Judge(true,true,true,new GuardCalls())).Contains("Deck selector"),"guard log: Deck selector in a public build");
        string admin=Why(withRoot,false,true,false,Judge(false,false,true,new GuardCalls { ElevatedResult=true }));
        Check(admin.Contains("elevated") && !admin.Contains("absent") && !admin.Contains("returned null"),"guard log: elevated process (hook build)");
        string both=Why(bare,false,false,false,Judge(false,false,false,new GuardCalls { IsolatedResult=false, ElevatedResult=true }));
        Check(both.Contains("lab account/path check failed") && both.Contains("elevated") && both.Contains("; "),"guard log: several conditions are all listed");
        Check(Why(bare,false,false,false,Judge(false,false,false,new GuardCalls { IsolatedResult=false })).Contains("lab account/path check failed"),"guard log: lab check named");
        Check(Why(bare,false,true,false,Judge(false,false,false,new GuardCalls { GroupResult=true })).Contains("Administrators group"),"guard log: the group named in a lab build");
        Check(Why(bare,true,true,false,Judge(true,false,true,new GuardCalls()))=="no failed condition identified","guard log: nothing failing says so");
        // The arguments are logged, the run and relaunch tokens are not, whatever the case or form.
        string shown=OfflineGuard.RedactedArguments(withRoot);
        Check(shown.Contains("[-hs-installer-root]") && shown.Contains(@"[C:\"+@"Users\player\AppData\Local\SoloStone]"),"guard log shows the installer root argument");
        Check(!shown.Contains("SECRET-RUN") && !shown.Contains("SECRET-TOKEN") && shown.Contains("<redacted>"),"guard log hides run and relaunch tokens");
        string odd=OfflineGuard.RedactedArguments(new[]{"-HS-Native-UI-Run","SECRET-A","-hs-native-ui-run=SECRET-B","-Hs-Profile-Relaunch-Token=SECRET-C","-HS-PROFILE-PREVIOUS-RUN","SECRET-D","-uid","SoloStone"});
        Check(!odd.Contains("SECRET") && odd.Contains("[-hs-native-ui-run=<redacted>]") && odd.Contains("[SoloStone]"),"guard log hides tokens in any case and in -flag=value form");
        // Elevation fails closed; a filtered administrator (not elevated) passes, an elevated one and an unreadable token do not.
        Check(!TokenElevation.Decide(true,0),"a filtered administrator token is not elevated");
        Check(TokenElevation.Decide(false,0),"an unreadable token is treated as elevated");
        // The probe goes through the one predicate and logs its words.
        string probe=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"plugin-source","OfflineProbe.cs"));
        Check(probe.Contains("OfflineGuard.Judge(") && probe.Contains("verdict.Rejected") && probe.Contains("OfflineGuard.RejectionReasons(") && probe.Contains("OfflineGuard.RedactedArguments(args)"),"the guard decides through Judge and logs the failed condition and the arguments");
        Check(!probe.Contains("bool admin") && probe.IndexOf("IsInRole(",StringComparison.Ordinal)==probe.LastIndexOf("IsInRole(",StringComparison.Ordinal),"the group list is read in one place, a lab-only delegate");
    }

    static void ElevationSourceRegressions() {
        string source=Path.Combine(AppContext.BaseDirectory,"launcher-source");
        string program=File.ReadAllText(Path.Combine(source,"Program.cs"));
        string host=File.ReadAllText(Path.Combine(source,"WindowsHost.cs"));
        string core=File.ReadAllText(Path.Combine(source,"Core.cs"))+File.ReadAllText(Path.Combine(source,"CoreLauncher.cs"));
        string inno=File.ReadAllText(Path.Combine(source,"SoloStone.iss"));
        Check(inno.Contains("DisableWelcomePage=yes") && inno.Contains("PageID = wpReady") && inno.Contains("SetupIconFile=assets\\installer.ico"),"Inno skips ready and uses installer icon");
        Check(!program.Contains("--elevated") && !host.Contains("elevatedChild"),"no elevated launcher option or child path remains");
        Check(!host.Contains("MainModule.FileName") && !host.Contains("RunNetsh"),"no executable self-relaunch or separate mutation path remains");
        Check(!host.Contains("Verb=\"runas\"") && core.Contains("Verb=\"runas\""),"elevation uses only the shared System32 command builder");
        Check(!host.Contains("rule.Grouping=") && !host.Contains("PowerShell") && !core.Contains("powershell.exe"),"no elevated COM writer or PowerShell startup path");
        Check(program.Contains("WindowsHost.Firewall(record,!o.Remove,o.DryRun?progress:null)"),"dry-run uses the same command plan as apply/remove");
        Check(host.Contains("dryRun(FirewallPlan.CommandLine(start)); return;")
            && host.IndexOf("if(dryRun!=null)")<host.IndexOf("Process.Start(start)"),"dry-run prints exact elevated command without starting it");
        Check(host.IndexOf("child.WaitForExit()")<host.IndexOf("if(apply) VerifyFirewall(record,executables)")
            && host.Contains("else if(HasRules(record))"),"unelevated launcher reads back apply/removal after system tools exit");
        Check(inno.Contains("PrivilegesRequired=lowest") && !inno.Contains("RequireStandardUser") && !inno.Contains("IsAdmin"),
            "Inno installs per user and lets an elevated user (UAC off) run setup and uninstall");
        Check(!program.Contains("WindowsHost.Elevated"),"the launcher no longer refuses an elevated process");
    }
    static void RejectsBecause(Action action,string message,string name) {
        try { action(); }
        catch(InvalidOperationException error) {
            Check(error.Message.Contains(message),name+": unexpected refusal: "+error.Message);
            return;
        }
        throw new Exception("Did not refuse: "+name);
    }
    static void PhysicalPathRegressions(string temp) {
        PhysicalPaths.CheckDriveTarget(3,@"\Device\HarddiskVolume3",true);
        Check(true,"physical fixed volume accepted");
        RejectsBecause(()=>PhysicalPaths.CheckDriveTarget(3,@"\Device\HarddiskVolume3",false),"DOS-device aliases","direct disk alias absent from volume mount manager refused");
        foreach(string alias in new[]{@"\??\C:\scratch",@"\??\Volume{fake}",@"\Device\LanmanRedirector\server\share",@"\Device\HarddiskVolume3\alias",""})
            RejectsBecause(()=>PhysicalPaths.CheckDriveTarget(3,alias,true),"DOS-device aliases","SUBST or DOS-device target refused");
        RejectsBecause(()=>PhysicalPaths.CheckDriveTarget(4,@"\Device\HarddiskVolume3",true),"mapped drives","mapped drive type refused");
        foreach(string path in new[]{@"\\server\share\SoloStone",@"\\?\C:\SoloStone",@"\\.\C:\SoloStone","//server/share/SoloStone"})
            Refuses(()=>Safety.Canonical(path),"UNC/device destination refused");
        string root=Path.Combine(temp,"physical-root"), retail=Path.Combine(temp,"registered-alias");
        string physical=Path.Combine(temp,"volume","retail");
        foreach(string physicalRoot in new[]{physical,Path.Combine(physical,"SoloStone"),Path.GetDirectoryName(physical)})
            RejectsBecause(()=>Safety.DestinationLocations(root,new[]{retail},path=>path==root?physicalRoot:physical),
                "registered game folder","physical registered overlap despite distinct spellings");
        Safety.DestinationLocations(root,new[]{retail},path=>path==root?Path.Combine(temp,"volume","separate"):physical);
        Check(true,"distinct physical destinations accepted");
        Refuses(()=>Safety.DestinationLocations(root,new[]{retail},path=>path==root?root:throw new InvalidOperationException("unresolved registration")),
            "unresolved registered alias fails closed");
    }
    static void FirewallRegressions(InstallRecord record,string source) {
        FirewallCommandRegressions(record);
        string exe=Path.Combine(record.Client,"Hearthstone.exe");
        FirewallPlan.CheckOwnedRule(record,FirewallPlan.Name(record,exe),exe,"SoloStone");
        Check(true,"record-derived firewall target and name accepted");
        foreach(string program in new[]{Path.Combine(source,"Hearthstone.exe"),Path.Combine(record.Client,"Unrecorded.exe")})
            Refuses(()=>FirewallPlan.CheckOwnedRule(record,FirewallPlan.Name(record,program),program,"SoloStone"),"caller/policy-selected target refused");
        Refuses(()=>FirewallPlan.CheckOwnedRule(record,"attacker-chosen-rule",exe,"SoloStone"),"caller/policy-selected name refused");
        foreach(string path in new[]{"../outside.exe","/outside.exe","C:/outside.exe","nested/../../outside.exe","%TARGET%.exe"}) {
            record.Files[path]=new string('A',64);
            Refuses(()=>FirewallPlan.Create(record,false),"unsafe recorded firewall path refused");
            record.Files.Remove(path);
        }
        Refuses(()=>FirewallPlan.CheckOwnedRule(record,FirewallPlan.Name(record,exe),exe,"Unrelated"),"unowned firewall group refused");
        string marker=Path.Combine(record.Client,InstallRecord.Marker);
        File.WriteAllText(marker,"wrong");
        Refuses(()=>FirewallPlan.VerifiedRecord(record.Root,new string[0],false),"unelevated planner reloads copy ownership before removal");
        File.WriteAllText(marker,record.InstallId);
        Check(FirewallPlan.VerifiedRecord(record.Root,new string[0],false).InstallId==record.InstallId,"owned removal record reloaded");
    }
    static void FirewallCommandRegressions(InstallRecord record) {
        string system=Path.Combine(Path.GetDirectoryName(record.Root),"Windows tools","System32");
        string cmd=Path.Combine(system,"cmd.exe"), netsh=Path.Combine(system,"netsh.exe");
        string exe=Path.Combine(record.Client,"Hearthstone.exe"), name=FirewallPlan.Name(record,exe);
        string delete="\""+netsh+"\" advfirewall firewall delete rule name=\""+name+"\" program=\""+exe+"\"";
        string add="\""+netsh+"\" advfirewall firewall add rule name=\""+name+"\" program=\""+exe+"\" dir=out action=block enable=yes profile=any protocol=any localip=any remoteip=any interfacetype=any";
        var apply=FirewallPlan.Elevation(record,true,system);
        Check(apply.FileName==cmd && apply.WorkingDirectory==system && apply.UseShellExecute && apply.Verb=="runas",
            "elevate only absolute System32 cmd with System32 working directory");
        Check(apply.Arguments=="/d /v:off /s /c \""+delete+" & "+add+"\"","exact quoted apply chain disables AutoRun and delayed expansion");
        Check(FirewallPlan.CommandLine(apply)=="\""+cmd+"\" "+apply.Arguments,"dry-run prints exact executable and arguments, including deletion prelude");
        var remove=FirewallPlan.Elevation(record,false,system);
        Check(FirewallPlan.CommandLine(remove)=="\""+cmd+"\" /d /v:off /s /c \""+delete+" & exit /b 0\"",
            "exact removal dry-run tolerates absent rules before authoritative read-back");
        Check(FirewallPlan.CmdQuote("space ! (å)")=="\"space ! (å)\"","quoted Unicode, spaces and literal exclamation/parentheses accepted");
        foreach(char bad in new[]{'&','|','<','>','^','%','"','\r','\n','\t','\0'}) {
            Refuses(()=>FirewallPlan.CmdQuote("before"+bad+"after"),"cmd metacharacter/control refused");
            string relative="Helper"+bad+".exe";
            record.Files[relative]=new string('A',64);
            Refuses(()=>FirewallPlan.Elevation(record,true,system),"unsafe recorded executable refused before elevation");
            record.Files.Remove(relative);
            string client=record.ClientPath;
            try {
                record.ClientPath=client+bad+"injected";
                Refuses(()=>FirewallPlan.Elevation(record,false,system),"unsafe absolute program parent refused on removal");
            } finally { record.ClientPath=client; }
            Refuses(()=>FirewallPlan.Elevation(record,true,system+bad+"/System32"),"unsafe system tool path refused");
        }
        Refuses(()=>FirewallPlan.CmdQuote(null),"null cmd value refused");
        Refuses(()=>FirewallPlan.CmdQuote(""),"empty cmd value refused");
        Refuses(()=>FirewallPlan.Elevation(record,true,"System32"),"relative system tool directory refused");
        Refuses(()=>FirewallPlan.Elevation(record,true,record.Root),"non-System32 tool directory refused");
        string id=record.InstallId;
        try {
            record.InstallId="attacker-chosen-rule";
            Refuses(()=>FirewallPlan.Elevation(record,false,system),"rule names require fixed GUID inventory");
        } finally { record.InstallId=id; }
        const string helper="Helper tool! (å).exe";
        record.Files[helper]=new string('A',64);
        var multiple=FirewallPlan.Elevation(record,true,system);
        Check(multiple.Arguments.Contains("program=\""+Path.Combine(record.Client,helper)+"\""),"helper path stays quoted without expansion");
        Check(multiple.Arguments.Split(new[]{" delete rule "},StringSplitOptions.None).Length==3
            && multiple.Arguments.Split(new[]{" add rule "},StringSplitOptions.None).Length==3
            && multiple.Arguments.Contains(" && \""+netsh+"\" advfirewall firewall add"),"every fixed EXE deleted then added, with add failure stopping chain");
        record.Files.Remove(helper);
        for(int i=0;i<100;i++) record.Files["Helper"+i+".exe"]=new string('A',64);
        Refuses(()=>FirewallPlan.Elevation(record,true,system),"overlong command refused rather than truncated");
        for(int i=0;i<100;i++) record.Files.Remove("Helper"+i+".exe");
    }
    static void CopyFixture(string source,string destination) {
        foreach(string file in Safety.Tree(source)) {
            string target=Path.Combine(destination,file.Substring(source.Length+1));
            Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(file,target);
        }
    }
    static void RecoveryRegressions(string temp,string source,string bundle,TestedClient build,Payload payload,string saves) {
        string helper=Path.Combine(source,"Helper.exe"); File.WriteAllText(helper,"test helper");
        // These are durable filesystem states at each setup interruption, not mocked recovery results.
        foreach(string point in new[]{"extraction","pending-temp","metadata","unmarked-stage","copy","payload","verified-stage","renamed-client","published-record","app-marker","firewall"}) {
            string root=Path.Combine(temp,"recover-"+point), app=Path.Combine(root,"app");
            CopyFixture(bundle,app);
            var record=new InstallRecord {Schema=InstallRecord.CurrentSchema,InstallId=Guid.NewGuid().ToString("N"),Root=root,Source=source,
                ClientPath=Installer.DefaultClient(root),ClientVersion="37.0.0",Files=new Dictionary<string,string>(build.Files),Payload=payload};
            string pending=Path.Combine(root,"install.pending.json"), installed=Path.Combine(root,"install.json");
            string stage=record.Stage, copy=point=="renamed-client" || point=="published-record" || point=="app-marker" || point=="firewall"?record.Client:stage;
            bool manual=point=="extraction" || point=="pending-temp" || point=="unmarked-stage";
            if(point=="pending-temp") Json.Save(pending+".tmp",record);
            else if(point!="extraction") Json.Save(point=="published-record" || point=="app-marker" || point=="firewall"?installed:pending,record);
            if(point!="extraction" && point!="pending-temp" && point!="metadata") {
                Directory.CreateDirectory(copy);
                if(point!="unmarked-stage") File.WriteAllText(Path.Combine(copy,InstallRecord.Marker),record.InstallId);
                File.WriteAllText(Path.Combine(copy,"interrupted-bytes"),"preserve");
                if(point!="copy" && point!="unmarked-stage") {
                    CopyFixture(source,copy);
                    if(point!="payload") CopyFixture(Path.Combine(bundle,"payload"),copy);
                }
            }
            // A whole copy carries the patch and its recorded hashes, as Complete leaves it. An interrupted one never got that far in its record.
            if(point=="verified-stage" || point=="renamed-client" || point=="published-record" || point=="app-marker" || point=="firewall") {
                record.IdentityFiles=ClientIdentity.Apply(copy);
                Json.Save(point=="published-record" || point=="app-marker" || point=="firewall"?installed:pending,record);
            }
            if(point=="app-marker" || point=="firewall") File.WriteAllText(Path.Combine(app,".solostone-app"),record.InstallId);
            if(manual) {
                RejectsBecause(()=>Installer.Recover(root,app,new string[0],saves,_=>{}),"move the entire root","unowned interruption gives exact preservation step: "+point);
                if(Directory.Exists(copy)) Check(File.ReadAllText(Path.Combine(copy,"interrupted-bytes"))=="preserve","unowned bytes kept: "+point);
                string backup=root+"-preserved"; Directory.Move(root,backup);
                Installer.Preflight(root,app,new string[0],false);
                Check(Directory.Exists(backup) && !Directory.Exists(root),"documented whole-root move permits fresh wizard: "+point);
                continue;
            }
            var messages=new List<string>();
            var recovered=Installer.Recover(root,app,new string[0],saves,step=>messages.Add(step.Message));
            Check(InstallRecord.Load(root).InstallId==record.InstallId,"recovered record and copy ownership: "+point);
            Installer.Preflight(root,app,new string[0],true);
            Check(File.ReadAllText(Path.Combine(app,".solostone-app"))==record.InstallId,"recovery passes app uninstall ownership: "+point);
            recovered.Payload.Verify(recovered.Client); Hashes.Verify(recovered.Client,recovered.Files); ClientIdentity.Verify(recovered.Client,recovered.IdentityFiles);
            Check(messages.Last().Contains("firewall --apply --root") && messages.Last().Contains("uninstall"),"exact recovery next commands: "+point);
            if(point=="copy" || point=="payload") {
                Check(!File.Exists(Path.Combine(recovered.Client,"interrupted-bytes")) && !Directory.Exists(record.Stage),"owned partial stage deleted and recopied: "+point);
            }
            Check(FirewallPlan.VerifiedRecord(root,new string[0],true).InstallId==record.InstallId,"firewall apply requires verified files and ownership: "+point);
            Check(recovered.Files.ContainsKey("Helper.exe"),"recovery records every EXE, including legacy manifests: "+point);
            string game=Path.Combine(recovered.Client,"Hearthstone.exe");
            File.WriteAllText(game,"changed");
            Refuses(()=>FirewallPlan.VerifiedRecord(root,new string[0],true),"unelevated apply planner rejects corrupt recorded executable");
            // Removal remains possible with damaged/deleted EXEs, but only for fixed recorded paths.
            Check(FirewallPlan.Create(FirewallPlan.VerifiedRecord(root,new string[0],false),false).Length==2,"damaged executable removal retains fixed plan");
            File.Delete(game);
            Check(FirewallPlan.Create(FirewallPlan.VerifiedRecord(root,new string[0],false),false).Length==2,"deleted executable removal retains fixed plan");
        }
        string broken=Path.Combine(temp,"recovery-refusals"), brokenApp=Path.Combine(broken,"app");
        CopyFixture(bundle,brokenApp);
        var owner=new InstallRecord {Schema=InstallRecord.CurrentSchema,InstallId=Guid.NewGuid().ToString("N"),Root=broken,Source=source,ClientPath=Installer.DefaultClient(broken),ClientVersion="37.0.0",Files=new Dictionary<string,string>(build.Files),Payload=payload};
        Json.Save(Path.Combine(broken,"install.pending.json"),owner);
        Directory.CreateDirectory(owner.Client); File.WriteAllText(Path.Combine(owner.Client,InstallRecord.Marker),"foreign");
        RejectsBecause(()=>Installer.Recover(broken,brokenApp,new string[0],saves,_=>{}),"Copy ownership","recovery refuses foreign marker");
        Check(File.ReadAllText(Path.Combine(owner.Client,InstallRecord.Marker))=="foreign","foreign marker preserved");
        Json.Save(Path.Combine(broken,"install.json"),owner);
        RejectsBecause(()=>Installer.Recover(broken,brokenApp,new string[0],saves,_=>{}),"Conflicting","recovery refuses conflicting records");
        File.Delete(Path.Combine(broken,"install.json"));
        File.WriteAllText(Path.Combine(owner.Client,InstallRecord.Marker),owner.InstallId);
        CopyFixture(source,owner.Client); CopyFixture(Path.Combine(bundle,"payload"),owner.Client);
        File.WriteAllText(Path.Combine(brokenApp,"payload","winhttp.dll"),"unowned app payload");
        Refuses(()=>Installer.Recover(broken,brokenApp,new string[0],saves,_=>{}),"recovery refuses app with different payload");
        Check(!File.Exists(Path.Combine(brokenApp,".solostone-app")),"recovery never marks mismatched app");
        File.Delete(helper);
    }
    static void AppOnlyPreflight(string temp) {
        string root=Path.Combine(temp,"app-only"), app=Path.Combine(root,"app"), payloadDir=Path.Combine(app,"payload");
        Directory.CreateDirectory(payloadDir);
        var payload=new Payload { Schema=1, DoorstopVersion="fake", SourceRevision="test", Files=new Dictionary<string,string>() };
        foreach(string relative in new[]{"winhttp.dll","doorstop_config.ini","client.config","BepInEx/unstripped_corlib/mscorlib.dll","BepInEx/core/BepInEx.dll","BepInEx/core/BepInEx.Preloader.dll","BepInEx/core/0Harmony.dll","BepInEx/plugins/SoloStone.Plugin.dll","BepInEx/plugins/SabberStoneCore.dll"}) {
            string file=Path.Combine(payloadDir,relative); Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllText(file,relative); payload.Files[relative]=Hashes.FileHash(file);
        }
        payload.PluginSha256=payload.Files["BepInEx/plugins/SoloStone.Plugin.dll"]; payload.EngineSha256=payload.Files["BepInEx/plugins/SabberStoneCore.dll"];
        Json.Save(Path.Combine(app,"payload.json"),payload); File.WriteAllText(Path.Combine(app,"SoloStone.exe"),"launcher");
        Installer.Preflight(root,app,new string[0],false); Check(true,"cancelled wizard can rerun setup");
        File.WriteAllText(Path.Combine(payloadDir,"winhttp.dll"),"changed");
        Refuses(()=>Installer.Preflight(root,app,new string[0],false),"unverified app cannot be overwritten");
    }
    static void PreflightRegressions(string temp) {
        string crash=Path.Combine(temp,"pending-tmp"), crashApp=Path.Combine(crash,"app");
        Directory.CreateDirectory(crash); File.WriteAllText(Path.Combine(crash,"install.pending.json.tmp"),"partial");
        try { Installer.Preflight(crash,crashApp,new string[0],false); throw new Exception("pending temp accepted"); }
        catch(SetupOccupiedException) { Check(true,"pending temp gets short occupied path"); }
        string root=Path.Combine(temp,"preflight"), app=Path.Combine(root,"app"), outside=Path.Combine(temp,"retail");
        Directory.CreateDirectory(outside);
        string sentinel=Path.Combine(outside,"keep.txt"); File.WriteAllText(sentinel,"retail bytes");
        Installer.Preflight(root,app,new string[0],false);
        Check(!Directory.Exists(root),"preflight no destination writes");
        Directory.CreateDirectory(root);
        LinkDirectory(app,outside);
        RejectsBecause(()=>Installer.Preflight(root,app,new[]{outside},false),"Linked folders","actual app junction before extraction");
        RejectsBecause(()=>Installer.Preflight(root,app,new string[0],true),"Linked folders","actual app junction before uninstall");
        Directory.Delete(app);
        string alias=Path.Combine(temp,"preflight-ancestor"); LinkDirectory(alias,root);
        RejectsBecause(()=>Installer.Preflight(alias,Path.Combine(alias,"app"),new string[0],false),"Linked folders","app ancestor link");
        Directory.Delete(alias);
        Directory.CreateDirectory(app);
        Installer.Preflight(root,app,new string[0],false);
        LinkDirectory(Path.Combine(app,"nested"),outside);
        RejectsBecause(()=>Installer.Preflight(root,app,new string[0],false),"Linked folders","app descendant junction before extraction");
        RejectsBecause(()=>Installer.Preflight(root,app,new string[0],true),"Linked folders","app descendant junction before uninstall");
        Directory.Delete(Path.Combine(app,"nested"));
        File.CreateSymbolicLink(Path.Combine(app,"linked.txt"),sentinel);
        RejectsBecause(()=>Installer.Preflight(root,app,new string[0],false),"Linked folders","app linked file before extraction");
        File.Delete(Path.Combine(app,"linked.txt"));
        File.WriteAllText(Path.Combine(app,"foreign.txt"),"unowned");
        RejectsBecause(()=>Installer.Preflight(root,app,new string[0],false),"already contains","unowned app content before extraction");
        Check(File.ReadAllText(Path.Combine(app,"foreign.txt"))=="unowned","unowned app preserved");
        File.Delete(Path.Combine(app,"foreign.txt"));
        Directory.CreateDirectory(Path.Combine(app,"empty-foreign"));
        RejectsBecause(()=>Installer.Preflight(root,app,new string[0],false),"already contains","unowned empty app descendant");
        Directory.Delete(Path.Combine(app,"empty-foreign"));
        RejectsBecause(()=>Installer.Preflight(root,outside,new string[0],false),"actual app destination","actual app must match root");
        RejectsBecause(()=>Installer.Preflight(root,app,new[]{root},false),"registered game folder","registered app destination");
        var record=new InstallRecord {Schema=InstallRecord.CurrentSchema,InstallId=Guid.NewGuid().ToString("N"),Root=root,Source=outside,ClientPath=Installer.DefaultClient(root),ClientVersion="37.0.0",
            Files=new Dictionary<string,string>{{"Hearthstone.exe",new string('A',64)}}};
        Directory.CreateDirectory(record.Client);
        File.WriteAllText(Path.Combine(record.Client,InstallRecord.Marker),record.InstallId);
        Json.Save(Path.Combine(root,"install.json"),record);
        RejectsBecause(()=>Installer.Preflight(root,app,new string[0],true),"App ownership","unowned uninstall refused");
        string marker=Path.Combine(app,".solostone-app");
        File.WriteAllText(marker,"wrong");
        RejectsBecause(()=>Installer.Preflight(root,app,new string[0],true),"App ownership","mismatched uninstall ownership");
        File.WriteAllText(marker,record.InstallId);
        Installer.Preflight(root,app,new string[0],true);
        Check(File.ReadAllText(sentinel)=="retail bytes","all preflights preserve outside bytes");
    }
#if SOLOSTONE_INSTALLER
    static void RuntimeAdmission(InstallRecord installed,string root,string source) {
        string exe=Path.Combine(installed.Client,"Hearthstone.exe"), data=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string run=Guid.NewGuid().ToString("D"), reports=Path.Combine(root,"reports");
        string[] args={"-hs-installer-root",root,"-hs-native-ui-run",run};
        Directory.CreateDirectory(reports);
        LaunchGrant.Write(reports,run,"allow",installed.ClientVersion);
        Action<InstallRecord> quiet=record=>FirewallProtection.VerifyPolicy(record,FirewallProtection.Executables(record),HealthyPolicy(record));
        Func<string[],Func<string>,string> resolve=(a,cosmetic)=>InstallerRuntime.Resolve(exe,data,a,false,false,()=>new string[0],quiet,cosmetic);
        Func<string> client=()=>"37.0";
        Check(resolve(args,client)==root,"plugin admits a granted launch");
        Check(resolve(args,client)==root,"second Resolve in the same launch still passes (grant not consumed)");
        Refuses(()=>InstallerRuntime.Resolve(Path.Combine(source,"Hearthstone.exe"),data,args,false,false,()=>new string[0],_=>{},client),"plugin source exe refused");
        Refuses(()=>InstallerRuntime.Resolve(exe,data,args,true,false),"plugin candidate refused");
        // Under Wine (Linux) the plugin admits a launch only through the network isolation check, which stands where the firewall check does on Windows.
        Check(InstallerRuntime.Resolve(exe,data,args,false,true,()=>new string[0],quiet,client)==root,"plugin admits a Wine launch that passes its protection check");
        Check(new Func<int>(()=>{ try { InstallerRuntime.Resolve(exe,data,args,false,true,()=>new string[0],_=>throw new InvalidOperationException("network"),client); return 0; } catch(InstallerRefusal e) { return e.ExitCode; } })()==InstallerRuntime.ExitFirewallRefused,"a Wine launch that fails isolation is refused as a protection failure");
        RejectsBecause(()=>InstallerRuntime.Protection(true)(installed),"could not read","the Wine protection is the isolation check, which refuses off Wine");
        Refuses(()=>resolve(args.Concat(new[]{"-hs-native-ui-lane","1"}).ToArray(),client),"plugin lane refused");
        Refuses(()=>resolve(args.Concat(new[]{"-hs-installer-root",root}).ToArray(),client),"plugin duplicate root refused");
        Check(InstallerRuntime.Resolve(exe,data,new string[0],false,false)==null,"plugin lab mode unchanged");
        // Grant: direct launches and foreign runs refuse.
        RejectsBecause(()=>resolve(new[]{"-hs-installer-root",root},client),"not started by SoloStone","run id required");
        RejectsBecause(()=>resolve(new[]{"-hs-installer-root",root,"-hs-native-ui-run",Guid.NewGuid().ToString("D")},client),"grant does not match","grant from another run refused");
        LaunchGrant.Remove(reports);
        RejectsBecause(()=>resolve(args,client),"not started by SoloStone","direct launch without grant refused");
        File.WriteAllText(LaunchGrant.PathIn(reports),"schema=1\nrunId="+run+"\nverdict=block\nclientVersion="+installed.ClientVersion+"\n");
        RejectsBecause(()=>resolve(args,client),"grant does not match","blocked verdict grant refused");
        Refuses(()=>LaunchGrant.Write(reports,run,"block",installed.ClientVersion),"launcher cannot grant a block");
        LaunchGrant.Write(reports,run,"warning",installed.ClientVersion);
        Check(resolve(args,client)==root,"accepted warning grant admits");
        LaunchGrant.Write(reports,run,"allow","37.0.9");
        RejectsBecause(()=>resolve(args,client),"grant does not match","grant for another client version refused");
        LaunchGrant.Write(reports,run,"allow",installed.ClientVersion);
        // Runtime version: reflection result, not an inlined constant.
        RejectsBecause(()=>resolve(args,()=>"38.0"),"does not run on Hearthstone 38","other major refused at runtime");
        RejectsBecause(()=>resolve(args,()=>"37.2"),"differs from the copy","runtime version differs from install record");
        foreach(string junk in new[]{null,"","37","37.0.0","x.y"}) RejectsBecause(()=>resolve(args,()=>junk),"could not be read","unreadable runtime version refused");
        Check(InstallerRuntime.CompiledMajor==ClientVersion.Major(TestedClient.Compiled().Version),"plugin compiled major matches tested-client.json");
        RuntimeRegressions(installed,data,args,source,reports,run);
        LaunchGrant.Remove(reports);
    }
    static void RuntimeRegressions(InstallRecord installed,string data,string[] args,string source,string reports,string run) {
        string exe=Path.Combine(installed.Client,"Hearthstone.exe");
        Func<string> resolve=()=>InstallerRuntime.Resolve(exe,data,args,false,false,()=>new string[0],
            record=>FirewallProtection.VerifyPolicy(record,FirewallProtection.Executables(record),HealthyPolicy(record)),()=>"37.0");
        var policy=HealthyPolicy(installed);
        Action admit=()=>InstallerRuntime.Resolve(exe,data,args,false,false,()=>new string[0],
            record=>FirewallProtection.VerifyPolicy(record,FirewallProtection.Executables(record),policy),()=>"37.0");
        FirewallProtection.VerifyPolicy(installed,FirewallProtection.Executables(installed),policy);
        Check(true,"complete firewall API snapshot accepted");
        foreach(var rule in policy.Rules) rule.Grouping="";
        FirewallProtection.VerifyPolicy(installed,FirewallProtection.Executables(installed),policy);
        Check(true,"ungrouped netsh rules accepted with complete protection checks");
        policy.FirewallEnabled[4]=false;
        RejectsBecause(admit,"must be enabled","direct client rejects disabled active firewall");
        policy=HealthyPolicy(installed); policy.Rules.Clear();
        RejectsBecause(admit,"missing or duplicated","direct client rejects missing firewall after cancelled UAC");
        string helper=Path.Combine(installed.Client,"Helper.exe"); File.WriteAllText(helper,"helper");
        policy=HealthyPolicy(installed); policy.Rules.RemoveAll(rule=>rule.ApplicationName==helper);
        RejectsBecause(admit,"missing or duplicated","direct client requires every executable block");
        File.Delete(helper);
        foreach(var corrupt in new Action<TestRule>[] {
            rule=>rule.Enabled=false, rule=>rule.Direction=1, rule=>rule.Action=1,
            rule=>rule.Profiles=4, rule=>rule.ApplicationName=source, rule=>rule.Protocol=6,
            rule=>rule.RemoteAddresses="203.0.113.1", rule=>rule.LocalAddresses="127.0.0.1",
            rule=>rule.InterfaceTypes="Wireless", rule=>rule.Grouping="Other",
            rule=>rule.ServiceName="SomeService", rule=>rule.Interfaces=new[]{"Ethernet"}
        }) {
            policy=HealthyPolicy(installed); corrupt(policy.Rules[0]);
            RejectsBecause(admit,"protection is incomplete","direct client rejects narrowed firewall rule");
        }
        policy=HealthyPolicy(installed); policy.Rules.Add(policy.Rules[0]);
        RejectsBecause(admit,"missing or duplicated","duplicate firewall rules rejected");
        policy=HealthyPolicy(installed); policy.ExcludedInterfaces[4]=new[]{"Ethernet"};
        RejectsBecause(admit,"switched off for the network adapter \"Ethernet\" in the Public profile","excluded firewall interface rejected");
        // As the registry check: an exclusion on a profile not in use refuses too. COM gives a disabled adapter's GUID.
        policy=HealthyPolicy(installed); policy.ExcludedInterfaces[2]=new object[]{HamachiGuid}; policy.ExcludedInterfaces[4]=new object[]{HamachiGuid};
        RejectsBecause(()=>FirewallProtection.VerifyPolicy(installed,FirewallProtection.Executables(installed),policy,SampleAdapters()),
            "adapter \"Hamachi\" (LogMeIn Hamachi Virtual Ethernet Adapter, disabled) in the Private and Public profiles","exclusion on an inactive profile refused, adapter named");
        policy=HealthyPolicy(installed); policy.ExcludedInterfaces[1]=new object[0]; policy.ExcludedInterfaces[2]=new object[]{""};
        FirewallProtection.VerifyPolicy(installed,FirewallProtection.Executables(installed),policy);
        Check(true,"empty exclusion lists accepted");
        policy=HealthyPolicy(installed); policy.LocalPolicyModifyState=1;
        RejectsBecause(admit,"Windows policy prevents","ineffective local policy rejected");
        // State 1 from a Group Policy that blocks all incoming connections: SoloStone's outbound blocks still apply (issue #18).
        int blockedProfiles=0;
        Action<bool> policyState1=dropsLocalRules=>FirewallProtection.VerifyPolicy(installed,FirewallProtection.Executables(installed),policy,
            null,active=>(active & ~blockedProfiles)==0,()=>dropsLocalRules);
        policy=HealthyPolicy(installed); policy.LocalPolicyModifyState=1; blockedProfiles=4;
        policyState1(false); Check(true,"state 1 accepted when policy blocks all inbound and keeps local rules");
        RejectsBecause(()=>policyState1(true),"Windows policy prevents","state 1 refused when policy drops local rules, even with inbound blocked");
        blockedProfiles=0;
        RejectsBecause(()=>policyState1(false),"Windows policy prevents","state 1 with inbound allowed refused: its cause is not the inbound block");
        policy.CurrentProfileTypes=6; blockedProfiles=4;
        RejectsBecause(()=>policyState1(false),"Windows policy prevents","state 1 refused unless policy blocks inbound on every active profile");
        blockedProfiles=6; policyState1(false); Check(true,"state 1 accepted when both active profiles block inbound");
        policy=HealthyPolicy(installed); policy.LocalPolicyModifyState=3; blockedProfiles=7;
        RejectsBecause(()=>policyState1(false),"Windows policy prevents","an unknown state stays refused with inbound blocked");
        Check(FirewallRegistry.PolicyProfiles.Contains("PrivateProfile") && FirewallRegistry.PolicyProfiles.Contains("StandardProfile")
            && FirewallRegistry.PolicyProfiles.Contains("DomainProfile") && FirewallRegistry.PolicyProfiles.Contains("PublicProfile"),"Group Policy is read under both names of the Private profile");
        policy=HealthyPolicy(installed); policy.LocalPolicyModifyState=3;
        RejectsBecause(admit,"Windows policy prevents","unknown local policy state rejected");
        // "Block all incoming connections" reports 2, and outbound block rules still apply.
        policy=HealthyPolicy(installed); policy.LocalPolicyModifyState=2;
        FirewallProtection.VerifyPolicy(installed,FirewallProtection.Executables(installed),policy);
        Check(true,"inbound-blocked profile accepted");
        policy=HealthyPolicy(installed); policy.CurrentProfileTypes=0;
        RejectsBecause(admit,"active profiles could not be verified","unknown active profiles rejected");
        // A changed client file must match its install record even though the compiled pin is gone.
        string old=File.ReadAllText(exe); File.WriteAllText(exe,"changed executable");
        RejectsBecause(()=>resolve(),"File check failed: Hearthstone.exe","modified exe rejected against the install record");
        File.WriteAllText(exe,old);
        Check(resolve()==installed.Root,"restored exe admitted");
        foreach(string retail in new[]{installed.Root,installed.Client,Path.Combine(installed.Client,"retail"),Path.GetDirectoryName(installed.Root)})
            RejectsBecause(()=>InstallerRuntime.Resolve(exe,data,args,false,false,()=>new[]{retail},_=>{},()=>"37.0"),"registered game folder","runtime registered retail exclusion");
        // The plugin's own firewall path: the registry read-back, no COM.
        Action viaRegistry=()=>InstallerRuntime.Resolve(exe,data,args,false,false,()=>new string[0],
            record=>FirewallRegistry.Check(record,FirewallProtection.Executables(record),RegistrySnapshot(record)),()=>"37.0");
        viaRegistry(); Check(true,"plugin admits through the registry read-back");
        // Reports stay a real folder even when the grant lives there, so move it aside for link checks.
        string grant=File.ReadAllText(LaunchGrant.PathIn(reports));
        Directory.Delete(reports,true);
        LinkDirectory(reports,source);
        RejectsBecause(()=>resolve(),"Linked folders","direct client linked reports directory rejected");
        Directory.Delete(reports); Directory.CreateDirectory(reports); File.WriteAllText(LaunchGrant.PathIn(reports),grant);
        foreach(string name in new[]{"plugin-loaded.txt","save-bound.txt.tmp","debug-scenario","pet-xp-pacing"}) {
            string file=Path.Combine(reports,name); File.CreateSymbolicLink(file,Path.Combine(source,"Hearthstone.exe"));
            RejectsBecause(()=>resolve(),"Linked folders","direct client linked report file rejected: "+name);
            File.Delete(file);
        }
        string control=Path.Combine(reports,"control"); LinkDirectory(control,source);
        RejectsBecause(()=>resolve(),"Linked folders","nested report directory rejected"); Directory.Delete(control);
        string clientReports=Path.Combine(installed.Client,"reports"); LinkDirectory(clientReports,source);
        RejectsBecause(()=>resolve(),"Linked folders","client report write path rejected"); Directory.Delete(clientReports);
        string fixture=Path.Combine(installed.Root,"fixture.xml"); File.CreateSymbolicLink(fixture,Path.Combine(source,"Hearthstone.exe"));
        RejectsBecause(()=>resolve(),"Linked folders","other runtime read/write path rejected"); File.Delete(fixture);
        Check(File.Exists(Path.Combine(source,"Hearthstone.exe")),"runtime admission preserves link target");
    }
    static FirewallSnapshot RegistrySnapshot(InstallRecord record) => new FirewallSnapshot {
        Rules=FirewallProtection.Executables(record).Select(exe=>"v2.33|Action=Block|Active=TRUE|Dir=Out|App="+exe+"|Name="+FirewallPlan.Name(record,exe)+"|").ToList() };
    static TestPolicy HealthyPolicy(InstallRecord record) => new TestPolicy {
        Rules=FirewallProtection.Executables(record).Select(exe=>new TestRule {Name=FirewallPlan.Name(record,exe),ApplicationName=exe}).ToList()
    };
#endif
}
// Public for the dynamic COM-shaped verifier; these contain no platform calls.
public sealed class TestPolicy
{
    public int LocalPolicyModifyState {get;set;}
    public int CurrentProfileTypes {get;set;}=4;
    public Dictionary<int,bool> FirewallEnabled {get;}=new Dictionary<int,bool>{{1,true},{2,true},{4,true}};
    public Dictionary<int,object> ExcludedInterfaces {get;}=new Dictionary<int,object>{{1,null},{2,null},{4,null}};
    public List<TestRule> Rules {get;set;}=new List<TestRule>();
}
public sealed class TestRule
{
    public string Name {get;set;}
    public string ApplicationName {get;set;}
    public bool Enabled {get;set;}=true;
    public int Direction {get;set;}=2;
    public int Action {get;set;}
    public int Profiles {get;set;}=int.MaxValue;
    public string Grouping {get;set;}="SoloStone";
    public int Protocol {get;set;}=256;
    public string LocalAddresses {get;set;}="*";
    public string RemoteAddresses {get;set;}="*";
    public string InterfaceTypes {get;set;}="All";
    public string ServiceName {get;set;}="";
    public object Interfaces {get;set;}
}
