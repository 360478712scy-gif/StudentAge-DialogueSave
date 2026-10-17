using System;
using StudentAgeDialogueSave.UI;

class Program
{
    static int checks;
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);checks++;}
    static void Main()
    {
        // The expected entry matrix follows the three user-visible choices.
        foreach(bool dialogue in new[]{false,true})
        {
            Check(ArchiveUiPolicy.UsesAdv(AdvArchiveScope.Global,dialogue),"global entry");
            Check(ArchiveUiPolicy.UsesAdv(AdvArchiveScope.DialogueOnly,dialogue)==dialogue,"dialogue-only entry");
            Check(!ArchiveUiPolicy.UsesAdv(AdvArchiveScope.Disabled,dialogue),"disabled entry");
            foreach(bool title in new[]{false,true})
            {
                Check(ArchiveUiPolicy.AllowsNativeSave(dialogue,title)==(!dialogue&&!title),"native save only outside dialogue/title");
                for(int tab=0;tab<4;tab++)
                {
                    Check(ArchiveUiPolicy.UsesNativeSlots(dialogue,tab)==(!dialogue||tab==3),"native/dialogue data separation");
                    Check(ArchiveUiPolicy.AllowsReorder(dialogue,tab)==(dialogue||tab!=2),"quick files keep their native category");
                    foreach(bool occupied in new[]{false,true})
                    {
                        ArchiveSlotAction expected=tab==0&&!title?ArchiveSlotAction.Save:occupied?ArchiveSlotAction.Load:ArchiveSlotAction.Empty;
                        Check(ArchiveUiPolicy.Action(tab,title,occupied)==expected,"slot action matrix");
                    }
                }
            }
        }
        Check(!ArchiveUiPolicy.UsesAdv((AdvArchiveScope)99,false),"unknown range falls back to native");
        var date=new DateTime(2026,10,9,8,0,0);
        Check(ArchiveUiPolicy.TargetUnchanged(null,default,null,default),"empty target remains empty");
        Check(!ArchiveUiPolicy.TargetUnchanged(null,default,"new.save",date),"empty target filled during save");
        Check(ArchiveUiPolicy.TargetUnchanged("old.save",date,"old.save",date),"confirmed target unchanged");
        Check(!ArchiveUiPolicy.TargetUnchanged("old.save",date,"other.save",date),"target replaced during save");
        Check(!ArchiveUiPolicy.TargetUnchanged("old.save",date,"old.save",date.AddTicks(1)),"same filename changed during save");
        Check(!ArchiveUiPolicy.TargetUnchanged("old.save",date,null,default),"confirmed target removed during save");
        Console.WriteLine("ARCHIVE_POLICY_PASS "+checks);
    }
}
