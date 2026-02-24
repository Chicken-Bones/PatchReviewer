using CodeChicken.DiffPatch;
using DynamicData;
using DynamicData.Binding;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;

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

			var changes = _results.Connect();
			changes
				.Sort(SortExpressionComparer<ResultViewModel>.Ascending(x => x.Start1), resort: changes.WhenPropertyChanged(x => x.Start1).Select(_ => Unit.Default))
				.Bind(out _sortedResults)
				.Subscribe(_ => RecalculateOffsets());

			changes
				.WhenPropertyChanged(x => x.ViewPatch)
				.Subscribe(_ => RecalculateOffsets());

			fp.results.AddRange(rejectsFile.patches.Select(MakeRejectedResult));
			UpdateResults();
		}

		private static Patcher.Result MakeRejectedResult(Patch p) => new() { patch = p, success = true, mode = Patcher.Mode.EXACT };

		private void UpdateResults() {
			_results.Clear();

			int i = 0;
			_results.AddRange(fp.results
				.OrderBy(r => r.patch.start1)
				.Select(r => new ResultViewModel(this, r, i++))
			);
		}

		public string Label { get; }
		public string Title => Label;

		private SourceList<ResultViewModel> _results = new SourceList<ResultViewModel>();

		private ReadOnlyObservableCollection<ResultViewModel> _sortedResults;
		public ReadOnlyObservableCollection<ResultViewModel> Results => _sortedResults;

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

		// Bound to collection changes (via Sort pipeline) and ViewPatch property changes on children
		public void RecalculateOffsets() {
			Patch lastApplied = null;
			int i = 0;
			foreach (var r in Results) {
				r.AppliedIndex = i++;

				if (r.ViewPatch != null) {
					r.UpdateOffset(lastApplied);
					lastApplied = r.ViewPatch;
				}
			}
		}
	}
}
