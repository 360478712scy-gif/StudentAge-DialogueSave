using System;

namespace StudentAgeDialogueSave.UI
{
    internal enum AdvArchiveScope { Global, DialogueOnly, Disabled }
    internal enum ArchiveSlotAction { Empty, Save, Load }

    // One policy for entry routing, animation and native-save permissions.
    internal static class ArchiveUiPolicy
    {
        internal static bool UsesAdv(AdvArchiveScope scope, bool dialogue) =>
            scope == AdvArchiveScope.Global || scope == AdvArchiveScope.DialogueOnly && dialogue;
        internal static bool UsesNativeSlots(bool dialogue, int tab) => !dialogue || tab == 3;
        internal static bool Saves(int tab, bool title) => !title && tab == 0;
        internal static ArchiveSlotAction Action(int tab, bool title, bool occupied) =>
            Saves(tab,title) ? ArchiveSlotAction.Save : occupied ? ArchiveSlotAction.Load : ArchiveSlotAction.Empty;
        internal static bool AllowsNativeSave(bool dialogue, bool title) => !dialogue && !title;
        internal static bool AllowsReorder(bool dialogue, int tab) => dialogue || tab != 2;
        internal static bool TargetUnchanged(string expectedName, DateTime expectedDate, string currentName, DateTime currentDate) =>
            string.Equals(expectedName,currentName,StringComparison.Ordinal) && (expectedName == null || expectedDate == currentDate);
    }
}
