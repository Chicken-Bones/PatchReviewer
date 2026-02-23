using CodeChicken.DiffPatch;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Data;

namespace PatchReviewer
{
	public class FilePatcherViewModel : INotifyPropertyChanged
	{
		private readonly FilePatcher fp;

		private readonly string rejectsFilePath;
		private readonly PatchFile rejectsFile;

		public FilePatcherViewModel(FilePatcher filePatcher, string commonBasePath) {
			fp = filePatcher;

			rejectsFilePath = Path.ChangeExtension(fp.patchFilePath, "rej");
			rejectsFile = File.Exists(rejectsFilePath) ? PatchFile.FromText(File.ReadAllText(rejectsFilePath), verifyHeaders: false) : new PatchFile();

			var label = fp.patchFile.basePath;
			if (commonBasePath != null && label.StartsWith(commonBasePath))
				label = label.Substring(commonBasePath.Length);
			Label = label;

			Children = (ListCollectionView)CollectionViewSource.GetDefaultView(_results);
			Children.SortDescriptions.Add(new SortDescription { PropertyName = nameof(ResultViewModel.Start1), Direction = ListSortDirection.Ascending });
			Children.IsLiveSorting = true;
			((INotifyCollectionChanged)Children).CollectionChanged += ResultsMoved;

			fp.results.AddRange(rejectsFile.patches.Select(MakeRejectedResult));
			UpdateResults();
		}

		private void ResultsMoved(object sender, NotifyCollectionChangedEventArgs e) {
			if (e.Action == NotifyCollectionChangedAction.Reset)
				return;

			var resultsList = ((System.Collections.IEnumerable)sender).Cast<ResultViewModel>();
			var newResults = e.NewItems?.Cast<ResultViewModel>();
			switch (e.Action) {
				case NotifyCollectionChangedAction.Add:
					int i = e.NewStartingIndex;
					foreach (var r in newResults)
						r.AppliedIndex = i++;
					foreach (var r in resultsList.Skip(i))
						r.AppliedIndex = i++;
					break;
				case NotifyCollectionChangedAction.Move:
					if (e.OldItems.Count != 1 || e.NewItems.Count != 1)
						throw new Exception("Multiple items moved simultaneously?");

					foreach (var r in resultsList.Skip(e.OldStartingIndex).Take(e.NewStartingIndex-e.OldStartingIndex))
						r.AppliedIndex--;
					foreach (var r in resultsList.Skip(e.NewStartingIndex+1).Take(e.OldStartingIndex-e.NewStartingIndex))
						r.AppliedIndex++;

					newResults.Single().AppliedIndex = e.NewStartingIndex;
					break;
				case NotifyCollectionChangedAction.Remove:
					int removedOrigIndex = e.OldItems.Cast<ResultViewModel>().Single().OrigIndex;
					foreach (var r in resultsList.Where(r => r.OrigIndex > removedOrigIndex))
						r.OrigIndex--;
					int j = e.OldStartingIndex;
					foreach (var r in resultsList.Skip(j))
						r.AppliedIndex = j++;
					break;
				default:
					throw new InvalidOperationException(e.Action.ToString());
			}
		}

		private static Patcher.Result MakeRejectedResult(Patch p) => new() { patch = p, success = true, mode = Patcher.Mode.EXACT };

		private void UpdateResults() {
			_results.Clear();

			int i = 0;
			foreach (var r in fp.results.OrderBy(r => r.patch.start1))
				_results.Add(new ResultViewModel(this, r, i++));

			RecalculateOffsets();
		}

		public string Label { get; }
		public string Title => Label;

		private ObservableCollection<ResultViewModel> _results = new ObservableCollection<ResultViewModel>();

		public ListCollectionView Children { get; }

		private static MethodInfo _restoreLiveShapingNow = typeof(ListCollectionView).GetMethod("RestoreLiveShaping", (BindingFlags)(-1));
		private static MethodInfo _isLiveShapingDirty = typeof(ListCollectionView).GetProperty("IsLiveShapingDirty", (BindingFlags)(-1)).GetGetMethod(nonPublic: true);
		public IEnumerable<ResultViewModel> Results {
			get {
				if ((bool)_isLiveShapingDirty.Invoke(Children, null))
					_restoreLiveShapingNow.Invoke(Children, null);

				return Children.Cast<ResultViewModel>();
			}
		}

		private bool _modified;
		public bool IsModified {
			get => _modified;
			private set {
				if (_modified == value) return;

				_modified = value;
				OnPropertyChanged();
				OnPropertyChanged(nameof(LabelWithModifiedIndicator));
			}
		}

		private void UpdateModified() {
			IsModified = ModifiedInEditor || ResultsModified;
		}

		private bool _modifiedInEditor;
		public bool ModifiedInEditor {
			get => _modifiedInEditor;
			set {
				_modifiedInEditor = value;
				UpdateModified();
			}
		}

		private bool _resultsModified;
		/// <summary>
		/// Results (or patched lines) modified
		/// </summary>
		public bool ResultsModified {
			get => _resultsModified;
			set {
				_resultsModified = value;
				UpdateModified();
				OnPropertyChanged(nameof(Status)); // child result statuses may have changed
			}
		}

		public string LabelWithModifiedIndicator => Label + (IsModified ? " *" : "");

		public string[] PatchedLines {
			get => fp.patchedLines;
			set {
				fp.patchedLines = value;
				ResultsModified = true;
				OnPropertyChanged();
			}
		}

		public string[] BaseLines => fp.baseLines;
		public string BasePath => fp.BasePath;

		public ResultStatus Status => _results.Count == 0 ? ResultStatus.EXACT : Results.Select(r => r.Status).Min();

		public bool ResultsAreValuable => Status < ResultStatus.REJECTED;

		public event PropertyChangedEventHandler PropertyChanged;

		protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

		private void Repatch(IEnumerable<Patch> patches, Patcher.Mode mode, IEnumerable<Patcher.Result> rejects) {
			var patcher = new Patcher(patches, fp.baseLines);
			patcher.Patch(mode);
			fp.results = patcher.Results.Concat(rejects).ToList();
			fp.patchedLines = patcher.ResultLines;

			UpdateResults();
			OnPropertyChanged(nameof(PatchedLines));
		}

		public void Repatch(IEnumerable<Patch> patches, Patcher.Mode mode) {
			var rejects = Results.Select(r => r.RejectedResult).Where(r => r != null).ToList();
			Repatch(patches, mode, rejects);
			ResultsModified = true;
		}

		/// <summary>
		/// Save the patch file and patched file. Update the patches list.
		/// The patches list always reflects the on disk state of the patch file.
		/// </summary>
		public void SaveApprovedPatches(bool autoHeaders) {
			if (!Results.Any()) { // only delete the patchFile if there are no rejects either
				File.Delete(fp.patchFilePath);
				File.Delete(rejectsFilePath);
				return;
			}

			// Results are sorted by AppliedPatch location. Since some patches may not have been approved, the ordering and offsets need recalculating
			fp.patchFile.patches = Results.Select(r => r.ApprovedPatch).Where(p => p != null).OrderBy(p => p.start1).ToList();

			int delta = 0;
			foreach (var p in fp.patchFile.patches) {
				p.start2 = p.start1 + delta;
				delta += p.length2 - p.length1;
			}

			File.WriteAllText(fp.patchFilePath, fp.patchFile.ToString(autoHeaders));
			fp.Save(); // saves the patched file


			rejectsFile.patches = Results.Where(r => r.ApprovedPatch == null).Select(r => r.OriginalPatch).OrderBy(p => p.start1).ToList();
			if (rejectsFile.patches.Any())
				File.WriteAllText(rejectsFilePath, rejectsFile.ToString(autoHeaders));
			else
				File.Delete(rejectsFilePath);


			// reloads results from the new patch list
			Repatch();
		}

		/// <summary>
		/// Remove a rejected patch entirely
		/// </summary>
		public void DeleteRejectedPatch(ResultViewModel result) {
			_results.Remove(result);
			ResultsModified = true;
		}

		/// <summary>
		/// Abandon all results and start again from the current (saved) patch file
		/// </summary>
		public void Repatch() {
			Repatch(fp.patchFile.patches, Patcher.Mode.FUZZY, rejectsFile.patches.Select(MakeRejectedResult));
			ResultsModified = false;
		}

		// Should be bound to a property change event on children length or position/reordering, but easier to get children to just tell us when they change
		public void RecalculateOffsets() {
			int delta = 0;
			foreach (var r in Results) {
				var p = r.ViewPatch;
				if (p != null) {
					r.MoveTo(p.start1 + delta);
					delta += p.length2 - p.length1;
				}
			}
		}
	}
}
