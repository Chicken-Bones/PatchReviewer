using CodeChicken.DiffPatch;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PatchReviewer
{
	public class ResultViewModel : INotifyPropertyChanged
	{
		public FilePatcherViewModel File { get; }
		private Patcher.Result Result { get; set; }
		public int OrigIndex { get; set; }

		public bool IsSplit { get; set; }

		public ResultViewModel(FilePatcherViewModel file, Patcher.Result result, int origIndex) {
			File = file;
			Result = result;
			OrigIndex = origIndex;
		}

		private Patch _editingPatch;
		public Patch EditingPatch {
			get => _editingPatch;
			set {
				var start1 = Start1;
				_editingPatch = value;
				EditingRepatchResult = null;

				if (start1 != Start1)
					OnPropertyChanged(nameof(Start1));

				OnPropertyChanged(nameof(ViewPatch));
			}
		}

		private Patcher.Result _editingRepatchResult;
		public Patcher.Result EditingRepatchResult {
			get => _editingRepatchResult;
			set {
				if (_editingRepatchResult == value)
					return;

				if (value != null) {
					if (!value.success)
						throw new ArgumentException($"{nameof(EditingRepatchResult)} must be success");
					if (!string.Join("", value.appliedPatch.diffs).Equals(string.Join("", EditingPatch.diffs)))
						throw new ArgumentException($"{nameof(EditingRepatchResult)} must be an application of the editing patch");
				}

				_editingRepatchResult = value;
				OnLabelPropertiesChanged();
			}
		}


		public Patch ViewPatch => EditingPatch ?? AppliedPatch;

		public int Start1 => ViewPatch?.start1 ?? (OriginalPatch.start1 + SearchOffset);
		public int Start2 => ViewPatch.start2;
		public int End1 => Start1 + (ViewPatch ?? OriginalPatch).length1;
		public int End2 => Start2 + ViewPatch.length2;
		public LineRange Range1 => new LineRange { start = Start1, end = End1 };
		public LineRange Range2 => new LineRange { start = Start2, end = End2 };
		public int SearchOffset => Result.searchOffset;

		// should be a 'friend method' of FilePatcherViewModel
		public void UpdateOffset(Patch prevPatch) {
			if (AppliedPatch is not { } p)
				return;

			var offset = prevPatch == null ? 0 : prevPatch.start2 - prevPatch.start1 + prevPatch.length2 - prevPatch.length1;
			p.start2 = p.start1 + offset;
		}

		public bool IsRejected => Result.success && AppliedPatch == null;

		private Patcher.Result StatusResult => EditingRepatchResult ?? Result;
		public ResultStatus Status {
			get {
				var r = StatusResult;
				if (!r.success)
					return ResultStatus.FAILED;
				if (IsRejected)
					return ResultStatus.REJECTED;
				if (r.mode == Patcher.Mode.FUZZY && r.fuzzyQuality < 0.5f)
					return ResultStatus.BAD;
				if (r.offsetWarning || r.mode == Patcher.Mode.FUZZY && r.fuzzyQuality < 0.85f)
					return ResultStatus.WARNING;
				if (r.mode == Patcher.Mode.FUZZY)
					return ResultStatus.GOOD;
				if (r.mode == Patcher.Mode.OFFSET)
					return ResultStatus.OFFSET;
				return ResultStatus.EXACT;
			}
		}

		public Patch OriginalPatch => Result.patch;
		public Patch AppliedPatch {
			get => Result.appliedPatch;
			set {
				Result.appliedPatch = value;
				OnPropertyChanged(nameof(ViewPatch));
			}
		}
		public Patch ApprovedPatch => Status >= ResultStatus.REJECTED ? AppliedPatch : OriginalPatch;

		private bool _modifiedInEditor;
		public bool ModifiedInEditor {
			get => _modifiedInEditor;
			set {
				if (_modifiedInEditor == value) return;

				_modifiedInEditor = value;
				OnPropertyChanged();
				OnPropertyChanged(nameof(LabelWithModifiedIndicator));
			}
		}

		public string LabelWithModifiedIndicator => Label + (ModifiedInEditor ? " *" : "");

		public string Label {
			get {
				var label = IsRejected ? $"REJECTED: {OriginalPatch.Header}" : StatusResult.Summary();
				return IsSplit ? $"(Split) {label}" : label;
			}
		}
		public string Title => $"{File.Label} {Label}";

		public string MovedPatchCountText { get; private set; } = "";

		private int _appliedIndex = -1;
		public int AppliedIndex {
			get => _appliedIndex;
			set {
				if (_appliedIndex == value)
					return;

				_appliedIndex = value;

				int moved = AppliedIndex - OrigIndex;
				MovedPatchCountText = moved > 0 ? $"▼{moved}" : moved < 0 ? $"▲{-moved}" : "";
				OnPropertyChanged(nameof(MovedPatchCountText));
			}
		}

		public event PropertyChangedEventHandler PropertyChanged;

		protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

		// If EditingPatch is null, this will change it to rejected
		internal void Approve() {
			Result.success = true;
			Result.mode = Patcher.Mode.EXACT;
			Result.offsetWarning = false;
			AppliedPatch = EditingPatch;

			ModifiedInEditor = false;
			OnPropertyChanged(nameof(Start1)); // trigger reordering in the collection view
			OnLabelPropertiesChanged();
			File.ResultsModified = true;
		}

		internal void ReplaceWith(Patcher.Result result) {
			Result = result;
			EditingPatch = null;

			OnPropertyChanged(nameof(Start1));
			OnLabelPropertiesChanged();
		}

		internal void ConvertRejectedToFailed() {
			if (Result.mode != Patcher.Mode.EXACT || AppliedPatch != null || !Result.success)
				throw new Exception("Rejected result invariants failed: " + Label);

			Result.success = false; //convert to FAILED
			OnLabelPropertiesChanged();
			File.ResultsModified = true;
		}

		private void OnLabelPropertiesChanged()
		{
			OnPropertyChanged(nameof(Status));
			OnPropertyChanged(nameof(Label));
			OnPropertyChanged(nameof(LabelWithModifiedIndicator));
			OnPropertyChanged(nameof(Title));
		}

		public Patcher.Result RejectedResult => IsRejected ? Result : null;
	}
}
