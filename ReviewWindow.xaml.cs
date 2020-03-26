using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using DiffPatch;

namespace PatchReviewer
{
	public partial class ReviewWindow
	{
		#region Styling Properties

		public static readonly DependencyProperty OffsetPatchBrushProperty = DependencyProperty.Register(
			nameof(OffsetPatchBrush), typeof(Brush), typeof(ReviewWindow));

		public Brush OffsetPatchBrush {
			get => (Brush) GetValue(OffsetPatchBrushProperty);
			set => SetValue(OffsetPatchBrushProperty, value);
		}

		public static readonly DependencyProperty GoodPatchBrushProperty = DependencyProperty.Register(
			nameof(GoodPatchBrush), typeof(Brush), typeof(ReviewWindow));

		public Brush GoodPatchBrush {
			get => (Brush) GetValue(GoodPatchBrushProperty);
			set => SetValue(GoodPatchBrushProperty, value);
		}

		public static readonly DependencyProperty WarningPatchBrushProperty = DependencyProperty.Register(
			nameof(WarningPatchBrush), typeof(Brush), typeof(ReviewWindow));

		public Brush WarningPatchBrush {
			get => (Brush) GetValue(WarningPatchBrushProperty);
			set => SetValue(WarningPatchBrushProperty, value);
		}

		public static readonly DependencyProperty BadPatchBrushProperty = DependencyProperty.Register(
			nameof(BadPatchBrush), typeof(Brush), typeof(ReviewWindow));

		public Brush BadPatchBrush {
			get => (Brush) GetValue(BadPatchBrushProperty);
			set => SetValue(BadPatchBrushProperty, value);
		}

		public static readonly DependencyProperty FailPatchBrushProperty = DependencyProperty.Register(
			nameof(FailPatchBrush), typeof(Brush), typeof(ReviewWindow));

		public Brush FailPatchBrush {
			get => (Brush) GetValue(FailPatchBrushProperty);
			set => SetValue(FailPatchBrushProperty, value);
		}

		#endregion

		public bool AutoHeaders { get; set; }

		private readonly List<FilePatcher> files;
		private readonly string commonBasePath; //trim this from the start of PatchFile.basePath for cleaner names in tree view

		private HashSet<FilePatcher> modifiedFiles = new HashSet<FilePatcher>();

		private FilePatcher file;
		private Patcher.Result result;
		private LineRange leftEditRange, rightEditRange;

		private bool editorsInSync;
		private Patch userPatch;

		private TreeViewItem currentItem;

		public ReviewWindow(IEnumerable<FilePatcher> files, string commonBasePath = null) {
			InitializeComponent();

			this.commonBasePath = commonBasePath;
			this.files = files.OrderBy(GetFileLabel).ToList();

			PopulateTreeView();
			SetupEditors();
			
			Select((TreeViewItem) treeView.Items[0]);
			if (NextReviewItem(1) != null)
				Select(NextReviewItem(1));
		}

		private void SetupEditors() {
			filePanel.SetTitles("Original File", "Patched File");

			patchPanel.SetTitles("Original Patch", "Applied Patch");
			patchPanel.SyntaxHighlighting = LoadHighlighting(Properties.Resources.Patch_Mode);
		}

		private IHighlightingDefinition LoadHighlighting(byte[] resource) {
			using (Stream s = new MemoryStream(resource))
			using (XmlTextReader reader = new XmlTextReader(s))
				return HighlightingLoader.Load(reader, HighlightingManager.Instance);
		}

		#region TreeView Items

		private void PopulateTreeView() {
			foreach (var f in files)
				treeView.Items.Add(UpdateItem(new TreeViewItem(), f, true));
		}

		public string GetFileLabel(FilePatcher f) {
			var label = f.patchFile.basePath;
			if (commonBasePath != null && label.StartsWith(commonBasePath))
				return label.Substring(commonBasePath.Length);

			return label;
		}

		private TreeViewItem UpdateItem(TreeViewItem item, FilePatcher f, bool regenChildren) {
			item.Header = GetFileLabel(f);
			item.Background = StatusBrush(GetStatus(f));
			item.Tag = f;
			SetItemModifiedStyle(item, false);

			if (regenChildren) {
				item.Items.Clear();
				foreach (var r in f.results)
					item.Items.Add(UpdateItem(new TreeViewItem(), r));
			}

			return item;
		}

		private TreeViewItem UpdateItem(TreeViewItem item, Patcher.Result r) =>
			UpdateItem(item, r, IsRemoved(r));

		private TreeViewItem UpdateItem(TreeViewItem item, Patcher.Result r, bool removed) {
			item.Header = r.Summary();
			if (removed)
				item.Header = $"REMOVED: {r.patch.Header}";

			item.Background = StatusBrush(GetStatus(r));
			item.Tag = r;
			SetItemModifiedStyle(item, false);

			return item;
		}

		private static void SetItemModifiedStyle(TreeViewItem item, bool modified) {
			item.FontWeight = modified ? FontWeights.Bold : FontWeights.Normal;
			var title = ((string) item.Header).TrimEnd(' ', '*');
			if (modified)
				title += " *";
			item.Header = title;
		}

		private Brush StatusBrush(ResultStatus status) {
			switch (status) {
				case ResultStatus.EXACT:
					return Brushes.Transparent;
				case ResultStatus.OFFSET:
					return OffsetPatchBrush;
				case ResultStatus.GOOD:
					return GoodPatchBrush;
				case ResultStatus.WARNING:
					return WarningPatchBrush;
				case ResultStatus.BAD:
					return BadPatchBrush;
				case ResultStatus.FAILED:
					return FailPatchBrush;
				default:
					throw new ArgumentException("ResultStatus: " + status);
			}
		}

		//this is a mess
		private static T WalkTree<T>(T item, int dir, bool drill = true) where T : ItemsControl {
			if (drill && dir > 0 && item.Items.Count > 0 && item.Items[0] is T child)
				return child;

			if (!(item.Parent is ItemsControl parent))
				return null;

			int i = parent.Items.IndexOf(item) + dir;
			if (i < 0 || i >= parent.Items.Count) { //go up a level
				if (!(parent is T))
					return null;

				var next = WalkTree((T) parent, dir, false);
				if (drill && dir < 0 && next != null)
					while (next.Items.Count > 0 && next.Items[next.Items.Count - 1] is T t)
						next = t;

				return next;
			}

			return (T) parent.Items[i];
		}

		private TreeViewItem FindItem(FilePatcher file, Patcher.Result result = null) {
			var item = treeView.Items.Cast<TreeViewItem>().Single(t => t.Tag == file);
			if (result != null)
				item = item.Items.Cast<TreeViewItem>().Single(t => t.Tag == result);

			return item;
		}

		private TreeViewItem NextReviewItem(int dir) {
			if (currentItem == null)
				return null;

			var node = currentItem;
			do {
				node = WalkTree(node, dir);
			} while (node != null && (!(node.Tag is Patcher.Result r) || GetStatus(r) >= ResultStatus.OFFSET));

			return node;
		}

		private static void Select(TreeViewItem item) {
			var n = item;
			while (n.Parent is TreeViewItem parent)
				(n = parent).IsExpanded = true;

			item.IsSelected = true;
			item.BringIntoView();
		}

		#endregion

		private void SelectTab(TabItem tabItem) => Dispatcher.BeginInvoke(new Action(() => tabItem.IsSelected = true));

		private void TreeView_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) {
			if (e.NewValue == currentItem)
				return;

			if (e.OldValue == currentItem && CanRevert) {
				if (ApproveOnExit() == MessageBoxResult.Cancel) {
					Dispatcher.BeginInvoke(new Action(() => Select(currentItem)));
					return;
				}

				//incase something happens during approval that overrides this
				if (treeView.SelectedItem != e.NewValue)
					return;
			}

			currentItem = (TreeViewItem) e.NewValue;
			if (currentItem.Tag is FilePatcher patchedFile)
				ReloadPanes(patchedFile, null);
			else
				ReloadPanes((FilePatcher) ((TreeViewItem) currentItem.Parent).Tag, (Patcher.Result) currentItem.Tag);
		}

		private void TabControl_OnSelectionChanged(object sender, SelectionChangedEventArgs e) {
			if (e.RemovedItems.Count == 0)
				return;

			if (e.RemovedItems[0] == fileTab && filePanel.CanReDiff)
				RediffFileEditor();
			else if (e.RemovedItems[0] == patchTab && patchPanel.CanReDiff)
				RediffPatchEditor();
		}

		#region Result Status
		
		private enum ResultStatus
		{
			FAILED,
			BAD,
			WARNING,
			GOOD,
			OFFSET,
			EXACT,
		}

		private static ResultStatus GetStatus(Patcher.Result r) {
			if (!r.success)
				return ResultStatus.FAILED;
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

		private static ResultStatus GetStatus(FilePatcher f) =>
			f.results.Count == 0 ? ResultStatus.EXACT :
			f.results.Select(GetStatus).Min();

		private static bool IsRemoved(Patcher.Result r) =>
			r.success && r.appliedPatch == null;

		#endregion

		#region Result and Patch Functions

		private bool CanSave => file != null && !CanRevert && modifiedFiles.Contains(file);

		private bool CanRevert => file != null && filePanel.IsModified || result != null && (patchPanel.IsModified || IsRemoved(result));

		private bool CanRediff => (fileTab.IsSelected ? filePanel : patchPanel).CanReDiff || !editorsInSync;
		
		private bool UserPatchRemoved => result != null && result.success && userPatch == null;

		private static bool ResultsAreValuable(FilePatcher f) => GetStatus(f) < ResultStatus.OFFSET;

		private void ReloadPanes(FilePatcher f, Patcher.Result r, bool force = false) {
			bool newFile = file != f;
			file = f;
			result = r;

			if (result == null) {
				titleLabel.Content = GetFileLabel(file);
				titleLabel.Background = StatusBrush(GetStatus(file));
			}
			else {
				titleLabel.Content = GetFileLabel(file) + " " + result.Summary();
				titleLabel.Background = StatusBrush(GetStatus(result));
			}

			if (newFile || force) {
				filePanel.LoadDiff(file.baseLines, file.patchedLines);
				filePanel.SyntaxHighlighting =
					HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(file.patchFile.basePath));
			}

			if (result == null) {
				filePanel.ClearRangeMarkers();
				AddEditWarning();
				SelectTab(fileTab);
				patchTab.Visibility = Visibility.Hidden;
				return;
			}

			patchTab.Visibility = Visibility.Visible;

			userPatch = result.appliedPatch;
			ReloadUserPatch();
			ReCalculateEditRange();
			filePanel.ScrollToMarked();
		}

		private void AddEditWarning() {
			if (ResultsAreValuable(file)) {
				var textArea = filePanel.right.editor.TextArea;
				textArea.ReadOnlySectionProvider = new EditWarningReadOnlySectionProvider(textArea);
			}
		}

		private void ReCalculateEditRange() {
			leftEditRange = new LineRange { start = 0, end = file.baseLines.Length};
			rightEditRange = new LineRange { start = 0, end = file.patchedLines.Length};

			int i = file.results.IndexOf(result);
			var prev = file.results.Take(i).LastOrDefault(r => r.appliedPatch != null);
			if (prev != null) {
				leftEditRange.start = prev.appliedPatch.start1 + prev.appliedPatch.length1;
				rightEditRange.start = prev.appliedPatch.start2 + prev.appliedPatch.length2;
			}

			var next = file.results.Skip(i + 1).FirstOrDefault(r => r.appliedPatch != null);
			if (next != null) {
				leftEditRange.end = next.appliedPatch.start1;
				rightEditRange.end = next.appliedPatch.start2;
			}

			filePanel.SetEditableRange(rightEditRange);

			if (result.appliedPatch != null && !(leftEditRange.Contains(result.appliedPatch.Range1) && rightEditRange.Contains(result.appliedPatch.Range2))) {
				var choice = new CustomMessageBox {
					Title = "Patch out of order",
					Message = "Patch does not fit between neighbours. Either patches overlap, or have different order to original file.",
					Image = MessageBoxImage.Error
				}.ShowDialogOk();
			}
		}

		private void ReloadUserPatch(bool soft = false) {
			//mark patch ranges
			if (userPatch != null)
				filePanel.MarkRange(
					new LineRange { start = userPatch.start1, length = userPatch.length1},
					new LineRange { start = userPatch.start2, length = userPatch.length2});
			else if (result.patch.start1 + result.patch.length1 <= file.baseLines.Length)
				filePanel.MarkRange(
					new LineRange { start = result.patch.start1, length = result.patch.length1});
			else
				filePanel.ClearRangeMarkers();

			var editPatch = userPatch;
			if (editPatch == null) { //removed or failed
				editPatch = new Patch(result.patch);
				editPatch.start2 += result.searchOffset;
			}

			if (soft)
				patchPanel.ReplaceEditedLines(editPatch.ToString().GetLines());
			else
				patchPanel.LoadDiff(
					result.patch.ToString().GetLines(),
					editPatch.ToString().GetLines());

			string patchEffect = !result.success ? "Failed" : userPatch == null ? "Removed" : "Applied";
			patchPanel.right.Title = patchEffect + " Patch";
			UpdateItem(FindItem(file, result), result, UserPatchRemoved);

			editorsInSync = true;
		}

		private void Repatch(IEnumerable<Patch> patches, Patcher.Mode mode) {
			var patcher = new Patcher(patches, file.baseLines);
			patcher.Patch(mode);
			file.results = patcher.Results.ToList();
			file.patchedLines = patcher.ResultLines;

			UpdateItem(FindItem(file), file, true);
			ReloadPanes(file, null, true);
		}

		private void RediffFile() {
			Repatch(filePanel.Diff(), Patcher.Mode.OFFSET);
			modifiedFiles.Add(file);
		}

		private void RediffFileEditor() {
			filePanel.ReDiff();
			if (result == null)
				return;

			//recalculate user patch
			try {
				userPatch = filePanel.DiffEditableRange();
			}
			catch (InvalidOperationException e) {
				MessageBox.Show(this, e.Message, "Rediff Failed", MessageBoxButton.OK, MessageBoxImage.Error);
				return;
			}

			//Can't remember what this was for, but it currently ruins the range marking
			/*if (userPatch != null)
				userPatch.start1 -= result.searchOffset;*/

			ReloadUserPatch();
		}

		private IEnumerable<string> PatchedLinesExcludingCurrentResult =>
			file.patchedLines.Slice(new LineRange { start = 0, end = rightEditRange.start})
				.Concat(file.baseLines.Slice(leftEditRange))
				.Concat(file.patchedLines.Slice(new LineRange { start = rightEditRange.end, end = file.patchedLines.Length}));

		private void RediffPatchEditor() {
			patchPanel.ReDiff();
			editorsInSync = false;

			var p = FormatAssistEditingPatch();
			//offset given other patches are already applied
			p.start1 += rightEditRange.start - leftEditRange.start;
			
			var patcher = new Patcher(new[] {p}, PatchedLinesExcludingCurrentResult);
			patcher.Patch(Patcher.Mode.FUZZY);

			var r = patcher.Results.Single();
			if (!r.success) {
				new CustomMessageBox {
					Title = "Patch Failed",
					Message = "Patch could not be applied, please ensure that the context is correct",
					Image = MessageBoxImage.Error
				}.ShowDialogOk();

				return;
			}

			var trimmed = new Patch(r.appliedPatch);
			trimmed.Trim(0);
			if (r.mode == Patcher.Mode.FUZZY) {
				var choice = new CustomMessageBox {
					Title = "Fuzzy Patch",
					Message = $"Fuzzy patch mode was required to make the patch succeed. " +
					          $"Load assisted patch? (Quality {(int) (r.fuzzyQuality * 100)})",
					Image = MessageBoxImage.Question
				}.ShowDialogOkCancel("Load", "Undo");
				if (choice == MessageBoxResult.Cancel)
					return;
			}

			if (!new LineRange { start = rightEditRange.start, length = leftEditRange.length}.Contains(trimmed.Range1)) {
				new CustomMessageBox {
					Title = "Patch Failed",
					Message = $"Patch applied ({r.mode}) with modifications {trimmed.Range1} outside the editable range {leftEditRange}",
					Image = MessageBoxImage.Error
				}.ShowDialogOk("Ignore");
				
				return;
			}

			editorsInSync = true;
			userPatch = r.appliedPatch;
			userPatch.start1 -= rightEditRange.start - leftEditRange.start;
			
			filePanel.LoadDiff(file.baseLines, patcher.ResultLines, true, false);
			filePanel.SetEditableRange(new LineRange { start = rightEditRange.start, 
				length = leftEditRange.length + userPatch.length2 - userPatch.length1});

			ReloadUserPatch(r.mode != Patcher.Mode.FUZZY);
		}

		private MessageBoxResult ApproveUserPatch(bool remove = false) {
			if (!editorsInSync) {
				new CustomMessageBox {
					Title = "Cannot Approve",
					Message = $"Editors not in sync.",
					Image = MessageBoxImage.Error
				}.ShowDialogOk();
				return MessageBoxResult.Cancel;
			}

			if (remove) {
				userPatch = null;
			}
			else if (userPatch == null) {
				var choice = new CustomMessageBox {
					Title = "Remove Empty Patch?",
					Message = "The approved patch is empty. Remove it?",
					Image = MessageBoxImage.Question
				}.ShowDialogOkCancel("Remove");

				if (choice == MessageBoxResult.Cancel)
					return MessageBoxResult.Cancel;
			}

			int prevDelta = (result.appliedPatch?.length2 - result.appliedPatch?.length1) ?? 0;
			int newDelta = (userPatch?.length2 - userPatch?.length1) ?? 0;
			int delta = newDelta - prevDelta;

			result.success = true;
			result.mode = Patcher.Mode.EXACT;
			result.offsetWarning = false;
			result.appliedPatch = userPatch;
			file.patchedLines = (userPatch != null ? filePanel.EditedLines : PatchedLinesExcludingCurrentResult).ToArray();
			modifiedFiles.Add(file);

			for (int i = file.results.IndexOf(result) + 1; i < file.results.Count; i++) {
				var r = file.results[i];
				if (r.appliedPatch != null)
					r.appliedPatch.start2 += delta;
				//TODO maybe searchoffset
			}

			UpdateItem(FindItem(file), file, false);
			ReloadPanes(file, result, true);

			if (CanSave && !ResultsAreValuable(file)) {
				var save = new CustomMessageBox {
					Title = "Save?",
					Message = "All items requiring user review have been approved. Save patch and results?",
					Image = MessageBoxImage.Question
				}.ShowDialogOkCancel("Save");
				if (save == MessageBoxResult.OK)
					SaveFile();
			}

			return MessageBoxResult.OK;
		}

		private Patch FormatAssistEditingPatch() {
			var lines = patchPanel.EditedLines.ToArray();
			for (int i = 1; i < lines.Length; i++) {
				var l = lines[i];
				if (l.Length == 0 || l[0] != ' ' && l[0] != '-' && l[0] != '+')
					lines[i] = ' ' + l;
			}

			Patch p;
			try {
				p = PatchFile.FromLines(lines, false).patches[0];
			}
			catch (Exception e) {
				MessageBox.Show(this, e.Message, "Invalid Patch", MessageBoxButton.OK, MessageBoxImage.Error);
				return null;
			}

			p.RecalculateLength();
			patchPanel.ReplaceEditedLines(p.ToString().GetLines());
			return p;
		}

		private MessageBoxResult ApproveOnExit() {
			MessageBoxResult choice;
			Action yesAction;

			if (result == null) {
				choice = new CustomMessageBox {
					Title = "Unapplied Changes",
					Message = "Rediff file to convert edits to patches?",
					Image = MessageBoxImage.Question
				}.ShowDialogYesNoCancel("Rediff", "Revert");

				yesAction = RediffFile;
			}
			else {
				if (CanRediff) {
					choice = new CustomMessageBox {
						Title = "Unapproved Changes",
						Message = "Modifications would be lost. Rediff and approve?",
						Image = MessageBoxImage.Question
					}.ShowDialogYesNoCancel("Rediff", "Revert");
					
					if (choice == MessageBoxResult.Cancel)
						return MessageBoxResult.Cancel;

					if (choice == MessageBoxResult.Yes) {
						ExecuteRediff(null, null);
						if (CanRediff)//failed to sync
							return MessageBoxResult.Cancel;
					}
					
					yesAction = () => ApproveUserPatch();
				}
				else if (userPatch == null) {
					choice = new CustomMessageBox {
						Title = "Unapproved Changes",
						Message = "Remove patch?",
						Image = MessageBoxImage.Question
					}.ShowDialogYesNoCancel("Remove", "Revert");
					
					yesAction = () => ApproveUserPatch(true);
				}
				else {
					choice = new CustomMessageBox {
						Title = "Unapproved Changes",
						Message = "Approve changes to patch?",
						Image = MessageBoxImage.Question
					}.ShowDialogYesNoCancel("Approve", "Revert");
					
					yesAction = () => ApproveUserPatch();
				}
			}

			if (choice == MessageBoxResult.Yes)
				yesAction();
			else if (choice == MessageBoxResult.No)
				ExecuteRevert(null, null);

			return choice;
		}

		private void RepatchFile() {
			var patches = file.results.Where(r => !IsRemoved(r)).Select(r => r.success ? r.appliedPatch : r.patch);
			Repatch(patches, Patcher.Mode.OFFSET);
			modifiedFiles.Add(file);
		}

		private void SaveFile()
		{
			bool onlySavePatches = ResultsAreValuable(file);
			if (onlySavePatches) {
				var choice = new CustomMessageBox {
					Title = "Unapproved patches",
					Message = "Any failed or fuzzy patches will be saved in their original state. Only the patch file will be saved",
					Image = MessageBoxImage.Warning
				}.ShowDialogOkCancel("Save");

				if (choice == MessageBoxResult.Cancel)
					return;
				
				file.patchFile.patches = file.results
					.Where(r => !IsRemoved(r))
					.Select(r => r.success && r.mode != Patcher.Mode.FUZZY ? r.appliedPatch : r.patch)
					.ToList();
				
				//recalculate offsets
				int delta = 0;
				foreach (var p in file.patchFile.patches)
				{
					p.start2 = p.start1 + delta;
					delta += p.length2 - p.length1;
				}
			}
			else
			{
				RepatchFile();
				if (ResultsAreValuable(file)) {
					new CustomMessageBox {
						Title = "Save Failed",
						Message = "Some patches did not apply perfectly. Cannot save in this state.",
						Image = MessageBoxImage.Error
					}.ShowDialogOk();
					return;
				}

				file.patchFile.patches = file.results.Select(r => r.appliedPatch).ToList();
			}

			if (file.patchFile.patches.Count == 0) {
				new CustomMessageBox {
					Title = "Patch File Deleted",
					Message = "Patch file was deleted as all patches were removed.",
					Image = MessageBoxImage.Information
				}.ShowDialogOk();

				File.Delete(file.patchFilePath);
			}
			else {
				File.WriteAllText(file.patchFilePath, file.patchFile.ToString(AutoHeaders));
			}

			ReloadFile();

			if (!onlySavePatches)
				file.Save();
		}

		private void ReloadFile() {
			Repatch(file.patchFile.patches, Patcher.Mode.FUZZY);
			modifiedFiles.Remove(file);
		}

		#endregion

		#region CommandBindings

		private void CanExecuteSave(object sender, CanExecuteRoutedEventArgs e) {
			e.CanExecute = CanSave;
		}

		private void ExecuteSave(object sender, ExecutedRoutedEventArgs e) {
			SaveFile();
		}

		private void CanExecuteReloadFile(object sender, CanExecuteRoutedEventArgs e) {
			if (file == null)
				return;

			e.CanExecute = modifiedFiles.Contains(file) || filePanel.IsModified;
			SetItemModifiedStyle(FindItem(file), e.CanExecute);
		}

		private void ExecuteReloadFile(object sender, ExecutedRoutedEventArgs e) {
			var choice = new CustomMessageBox {
				Title = "Abandon Changes?",
				Message = "Are you sure you want to revert all user changes?"
			}.ShowDialogOkCancel("Reload");

			if (choice == MessageBoxResult.Cancel)
				return;

			ReloadFile();
		}

		private void CanExecuteNextReviewItem(object sender, CanExecuteRoutedEventArgs e) {
			e.CanExecute = NextReviewItem(1) != null;
		}

		private void ExecuteNextReviewItem(object sender, ExecutedRoutedEventArgs e) {
			Select(NextReviewItem(1));
		}

		private void CanExecutePrevReviewItem(object sender, CanExecuteRoutedEventArgs e) {
			e.CanExecute = NextReviewItem(-1) != null;
		}

		private void ExecutePrevReviewItem(object sender, ExecutedRoutedEventArgs e) {
			Select(NextReviewItem(-1));
		}

		private void CanExecuteRediffFile(object sender, CanExecuteRoutedEventArgs e) {
			e.CanExecute = true;
		}

		private void ExecuteRediffFile(object sender, ExecutedRoutedEventArgs e) {
			if (ResultsAreValuable(file)) {
				var choice = new CustomMessageBox {
					Title = "Unapproved Patch Results",
					Message = "This file still has unapproved patch results. " +
					          "Editing in file mode will replace the results list, " +
					          "effectively approving the current patched file."
				}.ShowDialogOkCancel("Approve All");

				if (choice == MessageBoxResult.Cancel)
					return;
			}

			RediffFile();
		}

		private void CanExecuteRepatchFile(object sender, CanExecuteRoutedEventArgs e) {
			e.CanExecute = true;
		}

		private void ExecuteRepatchFile(object sender, ExecutedRoutedEventArgs e) {
			if (ResultsAreValuable(file)) {
				var choice = new CustomMessageBox {
					Title = "Unapproved Patch Results",
					Message = "Are you sure you want to approve all patch results and repatch the file?"
				}.ShowDialogOkCancel("Approve All");

				if (choice == MessageBoxResult.Cancel)
					return;
			}

			RepatchFile();
		}

		private void CanExecuteRediff(object sender, CanExecuteRoutedEventArgs e) {
			if (fileTab == null)
				return;

			e.CanExecute = CanRediff;
		}

		private void ExecuteRediff(object sender, ExecutedRoutedEventArgs e) {
			if (fileTab.IsSelected)
				RediffFileEditor();
			else
				RediffPatchEditor();
		}

		private void CanExecuteRevert(object sender, CanExecuteRoutedEventArgs e) {
			if (fileTab == null)
				return;

			e.CanExecute = CanRevert;
			SetItemModifiedStyle(FindItem(file, result), CanRevert);
		}

		private void ExecuteRevert(object sender, ExecutedRoutedEventArgs e) {
			if (IsRemoved(result)) {
				result.success = false; //convert to FAILED
				UpdateItem(FindItem(file), file, false);
			}

			ReloadPanes(file, result, true);
		}

		private void CanExecuteApprove(object sender, CanExecuteRoutedEventArgs e) {
			if (result == null)
				return;
			

			e.CanExecute = !CanRediff && (CanRevert || result.mode != Patcher.Mode.EXACT);
		}

		private void ExecuteApprove(object sender, ExecutedRoutedEventArgs e) {
			if (filePanel.CanReDiff)
				RediffFileEditor();

			ApproveUserPatch();
		}

		private void CanExecuteRemove(object sender, CanExecuteRoutedEventArgs e) {
			e.CanExecute = result != null && !IsRemoved(result);
		}

		private void ExecuteRemove(object sender, ExecutedRoutedEventArgs e) {
			ApproveUserPatch(true);
		}

		#endregion

		private class EditWarningReadOnlySectionProvider : IReadOnlySectionProvider
		{
			private readonly TextArea textArea;
			private DateTime lastWarning;

			public EditWarningReadOnlySectionProvider(TextArea textArea) {
				this.textArea = textArea;
			}

			private bool Warning() {
				if ((DateTime.Now - lastWarning).TotalMilliseconds < 200)
					return false;

				var choice = new CustomMessageBox {
					Title = "Unapproved Patch Results",
					Message = "This file still has unapproved patch results. " +
					          "Editing in file mode will replace the results list, " +
					          "effectively approving the current patched file.",
					Image = MessageBoxImage.Warning
				}.ShowDialogOkCancel("Edit Anyway");

				if (choice == MessageBoxResult.OK) {
					textArea.ReadOnlySectionProvider = Util.FullyEditable();
					return true;
				}

				lastWarning = DateTime.Now;
				return false;
			}

			public bool CanInsert(int offset) {
				//ugly hack
				if (new StackFrame(1).GetMethod().DeclaringType?.Name != "ImeSupport")
					return Warning();

				return false;
			}

			public IEnumerable<ISegment> GetDeletableSegments(ISegment segment) {
				if (Warning())
					yield return segment;
			}
		}
	}
}