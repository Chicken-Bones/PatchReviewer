using System.Collections.Generic;
using System.IO;
using DiffPatch;

namespace PatchReviewer
{
	public class PatchedFile
	{
		public string title;
		public string patchFilePath;
		public string rootDir;
		public PatchFile patchFile;
		public string[] original, patched;
		public List<Patcher.Result> results;
		
		public string BasePath => Path.Combine(rootDir, patchFile.basePath);
		public string PatchedPath => Path.Combine(rootDir, patchFile.patchedPath);

		internal bool userModified;
	}
}
