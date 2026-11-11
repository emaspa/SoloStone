using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
#if !SOLOSTONE_PLUGIN
using System.Runtime.Serialization;
#endif
using System.Security.Cryptography;
using System.Text;

// Also compiled into the plugin (SOLOSTONE_PLUGIN), which runs on Unity's Mono. Its Managed folder has no working
// serializer and no COM binder, so nothing here may use them. Launcher-only code lives in CoreLauncher.cs.
namespace SoloStone
{
#if SOLOSTONE_PLUGIN
    // The plugin never serializes these types. The marks compile to nothing, so no stub assembly is touched.
    [Conditional("SOLOSTONE_NEVER")] internal sealed class DataContractAttribute : Attribute { }
    [Conditional("SOLOSTONE_NEVER")] internal sealed class DataMemberAttribute : Attribute { public string Name; }
#endif
    // What a player's disk shows (I2b). The lab's names (HSNativeUIProbe.dll, HearthstoneOffline) stay in lab builds only.
    internal static class PublicNames
    {
        internal const string PluginFile="SoloStone.Plugin.dll";
        internal const string PluginPayloadPath="BepInEx/plugins/"+PluginFile;
        // Inside the default install root, so the copy must stay out of it: the folder is reserved in Safety.RootFolders.
        internal const string SavesFolder="saves";
        internal static string SaveRoot(string localAppData) => Path.Combine(localAppData,"SoloStone",SavesFolder);
    }
    // Strict reader for install.json, on one code path for launcher and plugin. Objects, arrays, strings, whole numbers, booleans, null.
    public static class PlainJson
    {
        public const int MaxBytes=1024*1024;
        const int MaxDepth=16;
        sealed class Reader
        {
            readonly string text; int at;
            internal Reader(string text) { this.text=text; }
            static InvalidOperationException Bad() => new InvalidOperationException("Installation metadata is damaged. "+InstallRecord.DamagedStep);
            void Space() { while(at<text.Length && (text[at]==' ' || text[at]=='\t' || text[at]=='\r' || text[at]=='\n')) at++; }
            internal object Document() { object value=Value(0); Space(); if(at!=text.Length) throw Bad(); return value; }
            object Value(int depth) {
                if(depth>MaxDepth) throw Bad();
                Space();
                if(at>=text.Length) throw Bad();
                char first=text[at];
                if(first=='{') return Map(depth);
                if(first=='[') return Items(depth);
                if(first=='"') return Str();
                if(Word("true")) return true;
                if(Word("false")) return false;
                if(Word("null")) return null;
                return Number();
            }
            bool Word(string word) {
                if(string.CompareOrdinal(text,at,word,0,word.Length)!=0) return false;
                at+=word.Length; return true;
            }
            Dictionary<string,object> Map(int depth) {
                var map=new Dictionary<string,object>(StringComparer.Ordinal); at++; Space();
                if(at<text.Length && text[at]=='}') { at++; return map; }
                for(;;) {
                    Space();
                    if(at>=text.Length || text[at]!='"') throw Bad();
                    string key=Str(); Space();
                    if(at>=text.Length || text[at++]!=':' || map.ContainsKey(key)) throw Bad();
                    map[key]=Value(depth+1); Space();
                    if(at>=text.Length) throw Bad();
                    char next=text[at++];
                    if(next=='}') return map;
                    if(next!=',') throw Bad();
                }
            }
            List<object> Items(int depth) {
                var list=new List<object>(); at++; Space();
                if(at<text.Length && text[at]==']') { at++; return list; }
                for(;;) {
                    list.Add(Value(depth+1)); Space();
                    if(at>=text.Length) throw Bad();
                    char next=text[at++];
                    if(next==']') return list;
                    if(next!=',') throw Bad();
                }
            }
            string Str() {
                var result=new StringBuilder(); at++;
                for(;;) {
                    if(at>=text.Length) throw Bad();
                    char ch=text[at++];
                    if(ch=='"') return result.ToString();
                    if(ch<0x20) throw Bad();
                    if(ch!='\\') { result.Append(ch); continue; }
                    if(at>=text.Length) throw Bad();
                    char escape=text[at++];
                    switch(escape) {
                        case '"': case '\\': case '/': result.Append(escape); break;
                        case 'b': result.Append('\b'); break;
                        case 'f': result.Append('\f'); break;
                        case 'n': result.Append('\n'); break;
                        case 'r': result.Append('\r'); break;
                        case 't': result.Append('\t'); break;
                        case 'u':
                            int code;
                            if(at+4>text.Length || !int.TryParse(text.Substring(at,4),NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out code)) throw Bad();
                            result.Append((char)code); at+=4; break;
                        default: throw Bad();
                    }
                }
            }
            object Number() {
                int start=at;
                if(at<text.Length && text[at]=='-') at++;
                int digits=at;
                while(at<text.Length && text[at]>='0' && text[at]<='9') at++;
                long value;
                if(at==digits || at-digits>18 || (text[digits]=='0' && at-digits>1) || !long.TryParse(text.Substring(start,at-start),NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out value)) throw Bad();
                return value;
            }
        }
        // A byte order mark is refused here and in the launcher's JsonBytes: .NET Framework's reader rejects it, so one must never pass a test elsewhere.
        public static bool HasBom(byte[] bytes) => bytes.Length>=3 && bytes[0]==0xEF && bytes[1]==0xBB && bytes[2]==0xBF;
        public static object Parse(byte[] bytes) {
            if(bytes==null || bytes.Length>MaxBytes) throw new InvalidOperationException("Installation metadata is too large. "+InstallRecord.DamagedStep);
            if(HasBom(bytes)) throw new InvalidOperationException("Installation metadata starts with a byte order mark. "+InstallRecord.DamagedStep);
            string text;
            try { text=new UTF8Encoding(false,true).GetString(bytes); }
            catch(ArgumentException) { throw new InvalidOperationException("Installation metadata is damaged. "+InstallRecord.DamagedStep); }
            return new Reader(text).Document();
        }
        internal static string Str(Dictionary<string,object> map,string key) {
            object value;
            if(!map.TryGetValue(key,out value) || value==null) return null;
            string text=value as string;
            if(text==null) throw new InvalidOperationException("Installation metadata is damaged. "+InstallRecord.DamagedStep);
            return text;
        }
        internal static int Int(Dictionary<string,object> map,string key) {
            object value;
            if(!map.TryGetValue(key,out value) || !(value is long) || (long)value<int.MinValue || (long)value>int.MaxValue) throw new InvalidOperationException("Installation metadata is damaged. "+InstallRecord.DamagedStep);
            return (int)(long)value;
        }
        internal static Dictionary<string,string> Strings(Dictionary<string,object> map,string key) {
            object value;
            if(!map.TryGetValue(key,out value) || value==null) return null;
            var inner=value as Dictionary<string,object>;
            if(inner==null) throw new InvalidOperationException("Installation metadata is damaged. "+InstallRecord.DamagedStep);
            var result=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var row in inner) result[row.Key]=Str(inner,row.Key);
            return result;
        }
    }
    public static class Safety
    {
        public static bool Same(string a, string b) => string.Equals(a,b,StringComparison.OrdinalIgnoreCase);
        static string Slashes(string path) => path.Replace('/','\\').TrimEnd('\\');
        // Compares spellings only, so a resolved \\?\Volume{...} path works too. Either separator.
        public static bool Within(string path, string parent) {
            string inner=Slashes(path), outer=Slashes(parent);
            return Same(inner,outer) || inner.StartsWith(outer+"\\",StringComparison.OrdinalIgnoreCase);
        }
        public static string Canonical(string path) {
#if SOLOSTONE_TEST_HOOKS
            // Lab builds only. A player may keep Hearthstone anywhere, so a release has no such rule and no such path in it.
            if (LabGuard.IsProtected(path)) throw new InvalidOperationException("That protected lab location cannot be used by this installer.");
#endif
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || path.Any(char.IsControl)
                || path.IndexOf('"') >= 0 || path.StartsWith(@"\\") || path.StartsWith("//"))
                throw new InvalidOperationException("Choose a full local folder path, without links or special path syntax.");
            if (Path.DirectorySeparatorChar == '\\') {
                if (path.Length < 4 || path[1] != ':' || path[2] != '\\' || path.Substring(2).Contains(":"))
                    throw new InvalidOperationException("Choose a local drive folder.");
                PhysicalPaths.RequireLocalDrive(path.Substring(0,2));
                foreach (string part in path.Substring(3).Split('\\','/'))
                    if (part.EndsWith(".") || part.EndsWith(" ") || part.IndexOfAny(new[]{'*','?','<','>','|'})>=0)
                        throw new InvalidOperationException("Folder names cannot end in a dot or space or contain special characters.");
            }
            // Long names only from here on: every comparison, record and firewall rule then matches the image path Windows reports.
            string full = PhysicalPaths.Long(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar));
            if (full.Length <= (Path.GetPathRoot(full) ?? "").Length)
                throw new InvalidOperationException("A drive root cannot be an installation folder.");
            return full;
        }
        public static void NoLinks(string path) {
            for (string current=Path.GetFullPath(path); !string.IsNullOrEmpty(current); current=Path.GetDirectoryName(current)) {
#if NET
                // Test harness only: on Unix, GetAttributes asks the OS for group membership on every call.
                if (new FileInfo(current).LinkTarget!=null) throw new InvalidOperationException("Linked folders, junctions and linked files are not supported. Choose a regular folder.");
#else
                try { if ((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0) throw new InvalidOperationException("Linked folders, junctions and linked files are not supported. Choose a regular folder."); }
                catch(FileNotFoundException) {} catch(DirectoryNotFoundException) {}
#endif
            }
        }
        public static string Child(string root, string relative) {
            if (string.IsNullOrEmpty(relative) || relative.Contains(":") || relative.Contains("\\") || relative.Split('/').Any(p=>p==".." || p=="." || p.Length==0) || Path.IsPathRooted(relative))
                throw new InvalidOperationException("Installation contains an unsafe relative path.");
            string path=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
            if (!Within(path,root) || Same(path,root)) throw new InvalidOperationException("Installation path escapes its folder.");
            NoLinks(path); return path;
        }
        public static IEnumerable<string> Tree(string root) {
            NoLinks(root);
            var result = new List<string>();
            foreach (var entry in Directory.GetFileSystemEntries(root,"*")) {
                NoLinks(entry);
                if (Directory.Exists(entry)) result.AddRange(Tree(entry)); else result.Add(entry);
            }
            return result;
        }
        public static void Pair(string source, string root, IEnumerable<string> registered) {
            source=Canonical(source); root=Canonical(root); NoLinks(source); NoLinks(root);
            source=PhysicalPaths.Resolve(source);
            string physicalRoot=PhysicalPaths.Resolve(root);
            if (Within(source,physicalRoot) || Within(physicalRoot,source)) throw new InvalidOperationException("Source and SoloStone folders must be separate; neither may contain the other.");
            Destination(root,registered);
        }
        public static void Destination(string root, IEnumerable<string> registered) {
            root=Canonical(root); NoLinks(root);
            DestinationLocations(root,registered,PhysicalPaths.Resolve);
        }
        internal static void DestinationLocations(string root,IEnumerable<string> registered,Func<string,string> resolve) {
            string physicalRoot=resolve(root);
            foreach (string path in registered) {
                string other=null, physicalOther=null;
                try {
                    other=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
                    if (Within(root,other) || Within(other,root)) RetailDestination();
                    physicalOther=resolve(other);
                }
                catch(ArgumentException) { } catch(NotSupportedException) { } catch(IOException) { }
                catch(InvalidOperationException) when(other==null || !(Within(root,other) || Within(other,root))) { }
                if (physicalOther==null) {
                    // An uninstall entry for an unplugged drive can only overlap paths on that drive letter.
                    string drive=DriveOf(path);
                    if (drive!=null && Same(drive,DriveOf(root))) throw new InvalidOperationException("A game folder registered on "+drive+" can't be reached, so SoloStone can't confirm this folder is separate from it. Plug that drive back in, or use a folder on another drive.");
                    continue;
                }
                if (Within(physicalRoot,physicalOther) || Within(physicalOther,physicalRoot)) RetailDestination();
            }
        }
        // "E:" from a drive path, "/" from a Unix one, null from a network path.
        static string DriveOf(string path) {
            if (string.IsNullOrEmpty(path) || path.StartsWith(@"\\") || path.StartsWith("//")) return null;
            if (path.Length>=2 && path[1]==':') return path.Substring(0,2);
            return path[0]=='/'?"/":null;
        }
        public static readonly string[] RootFolders={"app","state","backups","updates","releases","reports",PublicNames.SavesFolder};
        // Pure location rules for a client copy, shared by the wizard's path check, setup and every record load.
        public static string ClientPathProblem(string client,string root,string source,string saveRoot) {
            try {
                string full=Canonical(client);
                if(!FirewallPlan.CmdSafe(full)) return "That folder name contains a character Windows Firewall commands cannot carry (& | < > ^ % or a quote). Choose another folder.";
                return OverlapProblem(full,Canonical(root),string.IsNullOrEmpty(source)?null:Canonical(source),string.IsNullOrEmpty(saveRoot)?null:Canonical(saveRoot));
            } catch(InvalidOperationException e) { return e.Message; }
        }
        // Containment only, with no path syntax rules and no disk access: it also takes resolved volume paths and a source on a removed drive.
        public static string OverlapProblem(string client,string root,string source,string saveRoot) {
            if(Within(root,client)) return "Choose a folder outside the SoloStone folder, or a new folder inside it.";
            foreach(string name in RootFolders)
                if(Within(client,Slashes(root)+"\\"+name)) return "That folder belongs to SoloStone's own files. Choose another folder.";
            if(!string.IsNullOrEmpty(source) && (Within(client,source) || Within(source,client)))
                return "The copy must be separate from your Hearthstone folder. Choose a folder that is neither inside it nor around it.";
            if(!string.IsNullOrEmpty(saveRoot) && (Within(client,saveRoot) || Within(saveRoot,client)))
                return "The copy must be separate from your saves folder. Choose another folder.";
            return null;
        }
        // Root and client copy both stay clear of every registered game folder.
        public static void Destinations(InstallRecord record,IEnumerable<string> registered) {
            var folders=registered.ToArray();
            Destination(record.Root,folders); Destination(record.Client,folders);
        }
        static void RetailDestination() {
            throw new InvalidOperationException("SoloStone cannot be installed in or around a registered game folder.");
        }
    }
    // Resolve directory identity, not drive-letter spelling. Missing children inherit their nearest existing ancestor.
    public static class PhysicalPaths
    {
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        static extern uint QueryDosDevice(string name,StringBuilder target,int size);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern uint GetDriveType(string root);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        static extern bool GetVolumeNameForVolumeMountPoint(string mount,StringBuilder volume,int size);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        static extern bool GetVolumePathNamesForVolumeName(string volume,[Out] char[] paths,uint size,out uint required);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,StringBuilder path,uint size,uint flags);
        internal static void CheckDriveTarget(uint type,string target,bool registeredMount) {
            const string prefix=@"\Device\HarddiskVolume";
            if(type!=3 || !registeredMount || !target.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)
                || target.Length==prefix.Length || !target.Substring(prefix.Length).All(char.IsDigit))
                throw new InvalidOperationException("SUBST, mapped drives and DOS-device aliases are not supported. Choose a physical local drive folder.");
        }
        internal static void RequireLocalDrive(string drive) {
            var target=new StringBuilder(32768);
            if(QueryDosDevice(drive,target,target.Capacity)==0)
                throw new InvalidOperationException("The local drive identity could not be verified. Choose a physical local drive folder.");
            var volume=new StringBuilder(128); var mounts=new char[32768]; uint required;
            bool registeredMount=GetVolumeNameForVolumeMountPoint(drive+"\\",volume,volume.Capacity)
                && GetVolumePathNamesForVolumeName(volume.ToString(),mounts,(uint)mounts.Length,out required)
                && new string(mounts).Split('\0').Any(mount=>Safety.Same(mount,drive+"\\"));
            // A direct DefineDosDevice alias can look like a disk; it must also be a real volume mount.
            CheckDriveTarget(GetDriveType(drive+"\\"),target.ToString(),registeredMount);
        }
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="GetLongPathNameW")]
        static extern uint GetLongPathName(string shortPath,StringBuilder longPath,uint size);
        // A short name always contains ~, so the common case never touches the disk.
        internal static Func<string,string> LongName=OsLongName;
        public static string Long(string path) => path!=null && path.IndexOf('~')>=0?LongName(path):path;
        public static bool SameFolder(string a,string b) => Safety.Same(Long(a).TrimEnd(Path.DirectorySeparatorChar),Long(b).TrimEnd(Path.DirectorySeparatorChar));
        // Missing children keep their spelling under their nearest existing ancestor's long name.
        static string OsLongName(string path) {
            if(Path.DirectorySeparatorChar!='\\') return path;
            string suffix="", current=path;
            for(;;) {
                var result=new StringBuilder(32768);
                uint length=GetLongPathName(current,result,(uint)result.Capacity);
                if(length>0 && length<result.Capacity) return result+suffix;
                string parent=Path.GetDirectoryName(current);
                if(string.IsNullOrEmpty(parent) || parent==current) return path;
                suffix=@"\"+Path.GetFileName(current)+suffix; current=parent;
            }
        }
        public static string Resolve(string path) {
            path=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            if(Path.DirectorySeparatorChar!='\\') return path; // Mac tests exercise the comparison logic without Windows calls.
            var suffix=new Stack<string>();
            string current=path;
#if SOLOSTONE_TEST_HOOKS
            // Never open the protected lab client or accepted account, even for directory metadata.
            foreach(string protectedRoot in LabGuard.Roots)
                if(Safety.Within(LabGuard.Normalize(path),protectedRoot)) {
                    while(Safety.Within(current,protectedRoot)) {
                        suffix.Push(Path.GetFileName(current)); current=Path.GetDirectoryName(current);
                    }
                    break;
                }
#endif
            for(;;) {
                using(var handle=CreateFile(current,0,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero)) {
                    if(!handle.IsInvalid) {
                        var final=new StringBuilder(32768);
                        // Volume GUIDs give one spelling for drive letters and short (8.3) names.
                        uint length=GetFinalPathNameByHandle(handle,final,(uint)final.Capacity,1);
                        if(length==0 || length>=final.Capacity) break;
                        string result=final.ToString().TrimEnd('\\');
                        foreach(string part in suffix) result=Path.Combine(result,part);
                        return result;
                    }
                    int error=Marshal.GetLastWin32Error();
                    if(error!=2 && error!=3) break;
                }
                string parent=Path.GetDirectoryName(current);
                if(string.IsNullOrEmpty(parent) || parent==current) break;
                suffix.Push(Path.GetFileName(current)); current=parent;
            }
            // A registered UNC/alias that cannot resolve to a local volume must never silently skip exclusion.
            throw new InvalidOperationException("A folder's physical location could not be verified (including registered game folders). Use local physical paths and correct stale game registrations before retrying.");
        }
    }
    public static class Hashes
    {
        public static string FileHash(string path) {
            Safety.NoLinks(path);
            using(var sha=SHA256.Create()) using(var file=File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");
        }
        public static void Verify(string root, IDictionary<string,string> files) {
            if (files==null || files.Count==0) throw new InvalidOperationException("No verified files in installation metadata.");
            foreach(var row in files) {
                string file=Safety.Child(root,row.Key);
                if (row.Value==null || row.Value.Length!=64 || !row.Value.All(Uri.IsHexDigit) || !File.Exists(file) || !Safety.Same(FileHash(file),row.Value))
                    throw new InvalidOperationException("File check failed: " + row.Key + ". Only the supported, unchanged client and SoloStone payload can be used.");
            }
        }
    }
    [DataContract] public sealed class Payload
    {
        [DataMember(Name="schema")] public int Schema;
        [DataMember(Name="bepInExVersion")] public string BepInExVersion;
        [DataMember(Name="doorstopVersion")] public string DoorstopVersion;
        [DataMember(Name="sourceRevision")] public string SourceRevision;
        [DataMember(Name="engineSha256")] public string EngineSha256;
        [DataMember(Name="pluginSha256")] public string PluginSha256;
        [DataMember(Name="files")] public Dictionary<string,string> Files;
        internal static Payload FromMap(Dictionary<string,object> map) => new Payload { Schema=PlainJson.Int(map,"schema"), BepInExVersion=PlainJson.Str(map,"bepInExVersion"),
            DoorstopVersion=PlainJson.Str(map,"doorstopVersion"), SourceRevision=PlainJson.Str(map,"sourceRevision"), EngineSha256=PlainJson.Str(map,"engineSha256"),
            PluginSha256=PlainJson.Str(map,"pluginSha256"), Files=PlainJson.Strings(map,"files") };
        public void Verify(string folder) {
            if (Schema!=1 || string.IsNullOrEmpty(DoorstopVersion) || string.IsNullOrEmpty(SourceRevision)
                || Files==null || !Files.ContainsKey("winhttp.dll") || !Files.ContainsKey("doorstop_config.ini")
                || !Files.ContainsKey("client.config") || !Files.ContainsKey("BepInEx/core/BepInEx.dll")
                || !Files.ContainsKey("BepInEx/core/BepInEx.Preloader.dll") || !Files.ContainsKey("BepInEx/unstripped_corlib/mscorlib.dll") || !Files.ContainsKey("BepInEx/core/0Harmony.dll")
                || !Files.ContainsKey(PublicNames.PluginPayloadPath) || !Files.ContainsKey("BepInEx/plugins/SabberStoneCore.dll")
                || Files[PublicNames.PluginPayloadPath]!=PluginSha256 || Files["BepInEx/plugins/SabberStoneCore.dll"]!=EngineSha256)
                throw new InvalidOperationException("Incomplete SoloStone payload. Rebuild the installer with the pinned loader and plugin.");
            Hashes.Verify(folder,Files);
        }
    }
    // install.json's paths are the game's spelling: the plugin reads them inside the game. The native Linux launcher works with Unix paths and
    // the game sees its Wine prefix's C: drive, so that launcher maps the paths here, where a record is read or written. Unchanged on Windows.
    public static class RecordPaths
    {
        public static Func<string,string> ToDisk=path=>path, FromDisk=path=>path;
        internal static string Map(string path,Func<string,string> map) => path==null?null:map(path);
    }
    [DataContract] public sealed class InstallRecord
    {
        public const string Marker=".solostone-client"; public const int CurrentSchema=2;
        [DataMember(Name="schema")] public int Schema;
        [DataMember(Name="installId")] public string InstallId;
        [DataMember(Name="root")] public string Root;
        [DataMember(Name="source")] public string Source;
        [DataMember(Name="clientPath")] public string ClientPath;
        [DataMember(Name="clientVersion")] public string ClientVersion;
        [DataMember(Name="releaseVersion")] public string ReleaseVersion;
        [DataMember(Name="files")] public Dictionary<string,string> Files;
        [DataMember(Name="payload")] public Payload Payload;
        // Hashes of the two data files setup patched (ClientIdentity). Not in Files: that is checked against the unpatched source too. Null on a copy from before the patch existed.
        [DataMember(Name="identityFiles")] public Dictionary<string,string> IdentityFiles;
        public string Client => ClientPath;
#if !SOLOSTONE_PLUGIN
        [OnSerializing] void ToDisk(StreamingContext context) { Root=RecordPaths.Map(Root,RecordPaths.ToDisk); Source=RecordPaths.Map(Source,RecordPaths.ToDisk); ClientPath=RecordPaths.Map(ClientPath,RecordPaths.ToDisk); }
        [OnSerialized] void Restore(StreamingContext context) { FromDisk(context); }
        [OnDeserialized] void FromDisk(StreamingContext context) { Root=RecordPaths.Map(Root,RecordPaths.FromDisk); Source=RecordPaths.Map(Source,RecordPaths.FromDisk); ClientPath=RecordPaths.Map(ClientPath,RecordPaths.FromDisk); }
#endif
        public string Stage => StageOf(ClientPath);
        public static string StageOf(string client) => client+".solostone-staging";
        // What a player can really do: setup refuses while an install record exists, so it is always uninstall first. Uninstall needs a readable install record.
        public const string StartOver="Uninstall SoloStone from Windows Settings, keeping your saves, then run setup again.";
        public const string DamagedStep=StartOver+" If it can't be uninstalled, close SoloStone, move its whole folder (your saves are in its saves folder: copy that folder out first, and back afterwards) to an unused sibling folder, and run setup again.";
        public static string RecoveryStep(string root) => "Run SoloStone.exe recover --root " + FirewallPlan.Quote(root) + ". If no ownership record exists, close SoloStone, move the entire root (your saves are in its saves folder: copy that folder out first, and back afterwards) to an unused sibling folder, then rerun the installer at the original root (see INSTALL-prototype.md).";
        public static InstallRecord Load(string root) {
            root=Safety.Canonical(root); Safety.NoLinks(root);
            var record=ReadRecord(root,"install.json");
            if (!OwnsCopy(record.Client,record.InstallId))
                throw new InvalidOperationException("SoloStone copy marker is missing or does not match. No files were removed or launched. " + RecoveryStep(root));
            return record;
        }
        public static bool OwnsCopy(string copy,string installId) {
            string marker=Path.Combine(copy,Marker); Safety.NoLinks(marker);
            return File.Exists(marker) && new FileInfo(marker).Length<=64 && File.ReadAllText(marker).Trim()==installId;
        }
        internal static InstallRecord FromBytes(byte[] bytes) {
            var map=PlainJson.Parse(bytes) as Dictionary<string,object>;
            if(map==null) throw new InvalidOperationException("SoloStone installation record is invalid. No files were removed or launched.");
            object payload;
            map.TryGetValue("payload",out payload);
            if(payload!=null && !(payload is Dictionary<string,object>)) throw new InvalidOperationException("SoloStone installation record is invalid. No files were removed or launched.");
            return new InstallRecord { Schema=PlainJson.Int(map,"schema"), InstallId=PlainJson.Str(map,"installId"), Root=RecordPaths.Map(PlainJson.Str(map,"root"),RecordPaths.FromDisk),
                Source=RecordPaths.Map(PlainJson.Str(map,"source"),RecordPaths.FromDisk), ClientPath=RecordPaths.Map(PlainJson.Str(map,"clientPath"),RecordPaths.FromDisk), ClientVersion=PlainJson.Str(map,"clientVersion"), ReleaseVersion=PlainJson.Str(map,"releaseVersion"),
                Files=PlainJson.Strings(map,"files"), IdentityFiles=PlainJson.Strings(map,"identityFiles"), Payload=payload==null?null:Payload.FromMap((Dictionary<string,object>)payload) };
        }
        // The copy is self-contained, so the source folder is compared by spelling only. Setup and recovery check that it really exists.
        internal static InstallRecord ReadRecord(string root,string name) {
            string path=Path.Combine(root,name);
            Safety.NoLinks(path);
            if (new FileInfo(path).Length>PlainJson.MaxBytes) throw new InvalidOperationException("Installation metadata is too large. "+InstallRecord.DamagedStep);
            var record=FromBytes(File.ReadAllBytes(path));
            Guid id;
            if (record.Schema!=CurrentSchema || !Guid.TryParseExact(record.InstallId,"N",out id) || !Safety.Same(root,record.Root) || string.IsNullOrEmpty(record.ClientPath) || record.Files==null || record.Files.Count==0)
                throw new InvalidOperationException("SoloStone installation record is invalid. No files were removed or launched.");
            string problem=Safety.ClientPathProblem(record.ClientPath,root,null,null)??Safety.OverlapProblem(record.ClientPath,root,record.Source,null);
            if (problem!=null) throw new InvalidOperationException("SoloStone installation record is invalid. "+problem);
            if (!Safety.Same(Safety.Canonical(record.ClientPath),record.ClientPath)) throw new InvalidOperationException("SoloStone installation record is invalid. No files were removed or launched.");
            return record;
        }
    }
    public static partial class FirewallPlan
    {
        public static string Quote(string value) {
            if(value.Any(char.IsControl) || value.Contains("\"")) throw new InvalidOperationException("Invalid command path.");
            return "\""+value+"\"";
        }
        public static string Name(InstallRecord record,string exe) {
            using(var sha=SHA256.Create()) return "SoloStone-"+record.InstallId+"-"+BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(exe.ToLowerInvariant()))).Replace("-","").Substring(0,16);
        }
        public static InstallRecord VerifiedRecord(string root,IEnumerable<string> registered,bool apply) {
            var record=InstallRecord.Load(root); Safety.Destinations(record,registered);
            if(apply) { Hashes.Verify(record.Client,record.Files); record.Payload.Verify(record.Client); }
            Programs(record); // Validate every path even for removal of a missing executable.
            return record;
        }
        public static string[] Programs(InstallRecord record) {
            if(record.Files==null || !record.Files.ContainsKey("Hearthstone.exe")) throw new InvalidOperationException("No recorded game executable. "+InstallRecord.DamagedStep);
            CmdQuote(record.Client);
            return record.Files.Keys.Where(path=>path.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))
                .Select(path=>{
                    CmdQuote(path);
                    string program=Safety.Canonical(Safety.Child(record.Client,path));
                    CmdQuote(program);
                    return program;
                }).OrderBy(path=>path,StringComparer.OrdinalIgnoreCase).ToArray();
        }
        public static void CheckOwnedRule(InstallRecord record,string name,string program,string group) {
            if(!Programs(record).Any(path=>Safety.Same(path,program)) || name!=Name(record,program)
                || (!string.IsNullOrEmpty(group) && group!="SoloStone"))
                throw new InvalidOperationException("A SoloStone rule has an unexpected target or name. Firewall changes were stopped.");
        }
        public static string[] Create(InstallRecord record,bool apply) {
            Guid id;
            if(!Guid.TryParseExact(record.InstallId,"N",out id)) throw new InvalidOperationException("Invalid firewall installation ID.");
            return Programs(record).Select(exe=>
                "advfirewall firewall "+(apply?"add":"delete")+" rule name="+CmdQuote(Name(record,exe))+" program="+CmdQuote(exe)
                    +(apply?" dir=out action=block enable=yes profile=any protocol=any localip=any remoteip=any interfacetype=any":"")
            ).ToArray();
        }
        public static bool CmdSafe(string value) => !string.IsNullOrEmpty(value) && !value.Any(char.IsControl) && value.IndexOfAny(new[]{'&','|','<','>','^','%','"'})<0;
        public static string CmdQuote(string value) {
            if(!CmdSafe(value))
                throw new InvalidOperationException("Firewall command values cannot contain shell characters (& | < > ^ % \" or control characters). Choose another installation path.");
            return "\""+value+"\"";
        }
    }

    // One launch's admission, written by the launcher (CoreLauncher.cs) and required by the plugin. Guards accidental direct launches.
    public static partial class LaunchGrant
    {
        public const string FileName="launch-grant.txt";
        public static string PathIn(string reports) => Path.Combine(reports,FileName);
        // Resolve runs twice per launch, so checking never consumes the grant. The launcher deletes it after the plugin marker.
        public static void Check(string reports,string runId,string clientVersion) {
            string path=PathIn(reports); Safety.NoLinks(path);
            if(!File.Exists(path) || new FileInfo(path).Length>1024) throw new InvalidOperationException("This game was not started by SoloStone. Start it from the SoloStone launcher.");
            string[] lines=File.ReadAllText(path).Split('\n');
            if(lines.Length!=5 || lines[4]!="" || lines[0]!="schema=1" || lines[1]!="runId="+runId
                || (lines[2]!="verdict=allow" && lines[2]!="verdict=warning") || lines[3]!="clientVersion="+clientVersion)
                throw new InvalidOperationException("This game's launch grant does not match. Start it from the SoloStone launcher.");
        }
    }
}
