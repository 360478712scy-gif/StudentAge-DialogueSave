using System;
using System.Collections.Generic;

namespace StudentAgeDialogueSave.UI
{
    public enum DialogueUiCategory { Manual = 0, Auto = 1, Quick = 2 }

    // A presentation DTO, deliberately independent of the game's SaveFileData.
    public sealed class DialogueUiRecord
    {
        public string RevisionId = "";
        public string RunId = "";
        public DialogueUiCategory Category;
        public int Slot;
        public string Speaker = "";
        public string Summary = "";
        public string Comment;
        public int BackgroundId,SpeakerId;
        public string PreviewImageUrl;
        public DateTime CreatedUtc;
        public string RoleName = "";
        public string YearLabel = "";
        public string SeasonLabel = "";
        public int? SeasonId;
        public string Location = "";
        public int Gender;
        public int GradeState;
        public bool CanLoad;
        public string StatusReason = "";
        public bool IsConflict;
    }

    public sealed class UiResult
    {
        public readonly bool Success;
        public readonly string Message;
        public readonly bool IsPending;
        public UiResult(bool success, string message, bool isPending = false)
        {
            Success = success && !isPending;
            Message = message ?? "";
            IsPending = isPending;
        }
    }

    public interface IDialogueUiService
    {
        bool IsDialogueContext { get; }
        // Scope support, independent of a transient save/animation busy state.
        bool IsSupportedDialogueContext { get; }
        bool IsListing { get; }
        bool IsPreparingSave { get; }
        string StatusMessage { get; }
        bool CanSave(out string reason);
        IReadOnlyList<DialogueUiRecord> List(DialogueUiCategory category);

        // The host freezes the dialogue and owns the stable snapshot until EndMenu.
        // A false return must leave the current dialogue untouched.
        bool BeginMenu(bool saving, out string reason);
        void EndMenu();
        // Callbacks must run on Unity's main thread; storage work belongs to the host.
        void Save(DialogueUiCategory category, int slot, string replacedRevisionId, Action<UiResult> done);
        void Load(string revisionId, Action<UiResult> done);
        void Delete(string revisionId, Action<UiResult> done);
        void QuickSave();
        void QuickLoad();
        void Notify(string message);
    }
}
