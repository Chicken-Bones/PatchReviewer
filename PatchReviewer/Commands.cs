using System.Windows.Input;

namespace PatchReviewer
{
	public static class Commands
	{
		public static readonly RoutedUICommand Reload = new RoutedUICommand("Reload File (Revert All)", "Reload", typeof(ReviewWindow));

		public static readonly RoutedUICommand RediffFile = new RoutedUICommand("Reapply Diffs", "RediffFile", typeof(ReviewWindow));

		public static readonly RoutedUICommand NextDiff = new RoutedUICommand("Next Diff", "NextDiff", typeof(ReviewWindow),
				new InputGestureCollection {
					new KeyGesture(Key.Down, ModifierKeys.Alt)
				});

		public static readonly RoutedUICommand PrevDiff = new RoutedUICommand("Previous Diff", "PrevDiff", typeof(ReviewWindow),
				new InputGestureCollection {
					new KeyGesture(Key.Up, ModifierKeys.Alt)
				});

		public static readonly RoutedUICommand RefocusPatch = new RoutedUICommand("Refocus Patch", "RefocusPatch", typeof(ReviewWindow),
			new InputGestureCollection {
				new KeyGesture(Key.Left, ModifierKeys.Alt)
			});

		public static readonly RoutedUICommand ApprovePatch = new RoutedUICommand("Approve Patch", "ApprovePatch", typeof(ReviewWindow),
			new InputGestureCollection {
				new KeyGesture(Key.F2)
			});
		public static readonly RoutedUICommand RejectPatch = new RoutedUICommand("Reject Patch", "RejectPatch", typeof(ReviewWindow),
			new InputGestureCollection {
				new KeyGesture(Key.F3)
			});

		public static readonly RoutedUICommand RetryOriginalPatch = new RoutedUICommand("Retry Original Patch", "RetryOriginalPatch", typeof(ReviewWindow),
			new InputGestureCollection {
				new KeyGesture(Key.F4)
			});
		public static readonly RoutedUICommand Rediff = new RoutedUICommand("Recalculate Diff", "Rediff", typeof(ReviewWindow),
			new InputGestureCollection {
				new KeyGesture(Key.F5)
			});
		public static readonly RoutedUICommand Revert = new RoutedUICommand("Revert", "Revert", typeof(ReviewWindow),
			new InputGestureCollection {
				new KeyGesture(Key.R, ModifierKeys.Control)
			});
	}
}
