using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

// Launcher only: this needs the COM binder, which Unity's Mono does not have. The plugin uses FirewallRegistry.
namespace SoloStone
{
    public static partial class FirewallProtection
    {
        public static void Verify(InstallRecord record,string[] executables) {
            object policy=null;
            try {
                policy=Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2",true));
                VerifyPolicy(record,executables,policy);
            } catch(InvalidOperationException) { throw; }
            catch(Exception error) { throw new InvalidOperationException("Windows Firewall protection could not be verified. SoloStone cannot start. Run SoloStone firewall --apply.",error); }
            finally { if(policy!=null && Marshal.IsComObject(policy)) Marshal.FinalReleaseComObject(policy); }
        }
        // Tests supply API-shaped snapshots, registry contents for adapter names, and what Group Policy sets (FirewallRegistry reads it on a real machine).
        internal static void VerifyPolicy(InstallRecord record,string[] executables,dynamic policy,NetworkAdapters adapters=null,
            Func<int,bool> policyBlocksAllInbound=null,Func<bool> policyDropsLocalRules=null) {
            int active=(int)policy.CurrentProfileTypes;
            if(active==0 || (active & ~7)!=0)
                throw new InvalidOperationException("Windows Firewall active profiles could not be verified. SoloStone cannot start.");
            // 0: local rules apply. 2: "Block all incoming connections" is on locally; that only stops inbound rules, and SoloStone's are
            // outbound blocks, so they still apply. 1: Group Policy overrides the current profile, and on Windows 11 it reports that in two
            // cases: the policy sets "Apply local firewall rules: No", which drops SoloStone's rules, or the policy itself blocks all
            // incoming connections, which leaves them working. So 1 passes only when policy blocks all inbound traffic on every active
            // profile and drops local rules on none; any other cause still refuses. FirewallRegistry, which the launcher runs after this,
            // also refuses dropped local rules.
            int modify=(int)policy.LocalPolicyModifyState;
            if(modify==1 && (policyBlocksAllInbound??FirewallRegistry.PolicyBlocksAllInbound)(active)
                && !(policyDropsLocalRules??FirewallRegistry.PolicyDropsLocalRules)()) modify=2;
            if(modify!=0 && modify!=2)
                throw new InvalidOperationException("Windows policy prevents SoloStone from verifying its local firewall rules. Contact the computer administrator.");
            // Exclusions count on every profile, as in FirewallRegistry: Windows can move a network to another profile while the game runs.
            var excluded=new List<ExcludedAdapter>();
            foreach(int profile in new[]{1,2,4}) {
                if(!(bool)policy.FirewallEnabled[profile])
                    throw new InvalidOperationException("Windows Firewall must be enabled for all network profiles before SoloStone can start.");
                object ids=policy.ExcludedInterfaces[profile];
                ExcludedAdapter.Add(excluded,profile==1?"Domain":profile==2?"Private":"Public",ExcludedAdapter.Ids(ids),adapters??NetworkAdapters.Machine);
            }
            if(excluded.Count>0) throw new InvalidOperationException(ExcludedAdapter.Message(excluded));
            foreach(string exe in executables) {
                var matches=new List<object>();
                foreach(dynamic rule in policy.Rules)
                    if((string)rule.Name==FirewallPlan.Name(record,exe)) matches.Add(rule);
                if(matches.Count!=1) throw new InvalidOperationException("SoloStone firewall rules are missing or duplicated. Run SoloStone firewall --apply.");
                dynamic r=matches[0];
                // netsh-created rules have no group; retain compatibility with the previous SoloStone group.
                string group=(string)r.Grouping;
                if(!(bool)r.Enabled || (int)r.Direction!=2 || (int)r.Action!=0 || (int)r.Profiles!=int.MaxValue
                    || (!string.IsNullOrEmpty(group) && group!="SoloStone")
                    || !Safety.Same((string)r.ApplicationName,exe) || (int)r.Protocol!=256
                    || (string)r.LocalAddresses!="*" || (string)r.RemoteAddresses!="*" || (string)r.InterfaceTypes!="All"
                    || !string.IsNullOrEmpty((string)r.ServiceName) || HasInterfaces(r.Interfaces))
                    throw new InvalidOperationException("SoloStone firewall protection is incomplete. Run SoloStone firewall --apply.");
            }
        }
        static bool HasInterfaces(object value) => value!=null && (!(value is Array) || ((Array)value).Length!=0);
    }
}
