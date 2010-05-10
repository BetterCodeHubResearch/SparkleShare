//   SparkleShare, an instant update workflow to Git.
//   Copyright (C) 2010  Hylke Bons <hylkebons@gmail.com>
//
//   This program is free software: you can redistribute it and/or modify
//   it under the terms of the GNU General Public License as published by
//   the Free Software Foundation, either version 3 of the License, or
//   (at your option) any later version.
//
//   This program is distributed in the hope that it will be useful,
//   but WITHOUT ANY WARRANTY; without even the implied warranty of
//   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//   GNU General Public License for more details.
//
//   You should have received a copy of the GNU General Public License
//   along with this program.  If not, see <http://www.gnu.org/licenses/>.

using Gtk;
using SparkleShare;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Timers;

namespace SparkleShare {

	// SparkleRepo class holds repository information and timers
	public class SparkleRepo {

		private Process Process;
		private Timer FetchTimer;
		private Timer BufferTimer;
		private FileSystemWatcher Watcher;

		public string Name;
		public string Domain;
		public string LocalPath;
		public string RemoteOriginUrl;
		public string CurrentHash;

		public string UserEmail;
		public string UserName;
		public bool MonitorOnly;

		public SparkleRepo (string RepoPath) {

			MonitorOnly = false;

			Process = new Process();
			Process.EnableRaisingEvents = false; 
			Process.StartInfo.RedirectStandardOutput = true;
			Process.StartInfo.UseShellExecute = false;

			// Get the repository's path, example: "/home/user/SparkleShare/repo"
			LocalPath = RepoPath;
			Process.StartInfo.WorkingDirectory = LocalPath;

			// Get user.name, example: "User Name"
			UserName = "Anonymous";
			Process.StartInfo.FileName = "git";
			Process.StartInfo.Arguments = "config --get user.name";
			Process.Start();
			UserName = Process.StandardOutput.ReadToEnd().Trim ();

			// Get user.email, example: "user@github.com"
			UserEmail = "not.set@git-scm.com";
			Process.StartInfo.FileName = "git";
			Process.StartInfo.Arguments = "config --get user.email";
			Process.Start();
			UserEmail = Process.StandardOutput.ReadToEnd().Trim ();

			// Get remote.origin.url, example: "ssh://git@github.com/user/repo"
			Process.StartInfo.FileName = "git";
			Process.StartInfo.Arguments = "config --get remote.origin.url";
			Process.Start();
			RemoteOriginUrl = Process.StandardOutput.ReadToEnd().Trim ();

			// Get the repository name, example: "Project"
			Name = Path.GetFileName (LocalPath);

			// Get the domain, example: "github.com" 
			Domain = RemoteOriginUrl; 
			Domain = Domain.Substring (Domain.IndexOf ("@") + 1);
			if (Domain.IndexOf (":") > -1)
				Domain = Domain.Substring (0, Domain.IndexOf (":"));
			else
				Domain = Domain.Substring (0, Domain.IndexOf ("/"));

			// Get hash of the current commit
			Process.StartInfo.FileName = "git";
			Process.StartInfo.Arguments = "rev-list --max-count=1 HEAD";
			Process.Start();
			CurrentHash = Process.StandardOutput.ReadToEnd().Trim ();

			// Watch the repository's folder
			Watcher = new FileSystemWatcher (LocalPath);
			Watcher.IncludeSubdirectories = true;
			Watcher.EnableRaisingEvents = true;
			Watcher.Filter = "*";
			Watcher.Changed += new FileSystemEventHandler(OnFileActivity);
			Watcher.Created += new FileSystemEventHandler(OnFileActivity);
			Watcher.Deleted += new FileSystemEventHandler(OnFileActivity);

			// Fetch remote changes every 20 seconds
			FetchTimer = new Timer ();
			FetchTimer.Interval = 10000;
			FetchTimer.Elapsed += delegate { 
				Fetch ();
			};

			FetchTimer.Start();
			BufferTimer = new Timer ();

			// Add everything that changed 
			// since SparkleShare was stopped
			Add ();


		}

		// Starts a time buffer when something changes
		public void OnFileActivity (object o, FileSystemEventArgs args) {
		   WatcherChangeTypes wct = args.ChangeType;
			 if (!ShouldIgnore (args.Name) && !MonitorOnly) {
			  Console.WriteLine("[Event][" + Name + "] " + wct.ToString() + 
					              " '" + args.Name + "'");
				StartBufferTimer ();
			}
		}

		// A buffer that will fetch changes after 
		// file activity has settles down
		public void StartBufferTimer () {

			int Interval = 2000;
			if (!BufferTimer.Enabled) {	

				// Delay for a few seconds to see if more files change
				BufferTimer.Interval = Interval; 
				BufferTimer.Elapsed += delegate (object o, ElapsedEventArgs args) {
					Console.WriteLine ("[Buffer][" + Name + "] Done waiting.");
					Add ();
				};
				Console.WriteLine ("[Buffer][" + Name + "] " + 
					               "Waiting for more changes...");

				BufferTimer.Start();
			} else {

				// Extend the delay when something changes
				BufferTimer.Close ();
				BufferTimer = new Timer ();
				BufferTimer.Interval = Interval;
				BufferTimer.Elapsed += delegate (object o, ElapsedEventArgs args) {
					Console.WriteLine ("[Buffer][" + Name + "] Done waiting.");
					Add ();
				};

				BufferTimer.Start();
				Console.WriteLine ("[Buffer][" + Name + "] " + 
					               "Waiting for more changes...");

			}

		}

		// Clones a remote repo
		public void Clone () {
			Process.StartInfo.Arguments = "clone " + RemoteOriginUrl;
			Process.Start();

			// Add a gitignore file
		  TextWriter Writer = new StreamWriter(LocalPath + ".gitignore");
		  Writer.WriteLine("*~"); // Ignore gedit swap files
		  Writer.WriteLine(".*.sw?"); // Ignore vi swap files
		  Writer.Close();
		}

		// Stages the made changes
		public void Add () {
			BufferTimer.Stop ();
			Console.WriteLine ("[Git][" + Name + "] Staging changes...");
			Process.StartInfo.Arguments = "add --all";
			Process.Start();
//			SparkleUI.NotificationIcon.SetSyncingState ();
			string Message = FormatCommitMessage ();
			if (!Message.Equals ("")) {
				Commit (Message);
				Push ();
				Fetch ();
				// Push again in case of a conflict
				Push ();
			}
		}

		// Commits the made changes
		public void Commit (string Message) {
			Console.WriteLine ("[Commit][" + Name + "] " + Message);
			Console.WriteLine ("[Git][" + Name + "] Commiting changes...");
			Process.StartInfo.Arguments = "commit -m \"" + Message + "\"";
			Process.Start();
			ShowEventBubble (UserName + " " + Message, 
				              SparkleHelpers.GetAvatar (UserEmail, 48),
				              true);
		}

		// Fetches changes from the remote repo	
		public void Fetch () {
			FetchTimer.Stop ();
			SparkleUI.NotificationIcon.SetSyncingState ();
			Console.WriteLine ("[Git][" + Name + "] Fetching changes... ");
			Process.StartInfo.Arguments = "fetch";
			Process.Start();
			Process.WaitForExit ();
			Console.WriteLine ("[Git][" + Name + "] Done fetching changes ");
			Merge ();
			SparkleUI.NotificationIcon.SetIdleState ();
			FetchTimer.Start ();
		}

		// Merges the fetched changes
		public void Merge () {
			Watcher.EnableRaisingEvents = false;
			Console.WriteLine ("[Git][" + Name + "] Merging fetched changes... ");
			Process.StartInfo.Arguments = "merge origin/master";
			Process.Start();
			Process.WaitForExit ();
			Console.WriteLine ("[Git][" + Name + "] Done merging changes ");
			string Output = Process.StandardOutput.ReadToEnd().Trim ();
			// Show notification if there are updates
			if (!Output.Equals ("Already up-to-date.")) {

				// Get the last commit message
				Process.StartInfo.Arguments = "log --format=\"%ae\" -1";
				Process.Start();
				string LastCommitEmail = Process.StandardOutput.ReadToEnd().Trim ();

				// Get the last commit message
				Process.StartInfo.Arguments = "log --format=\"%s\" -1";
				Process.Start();
				string LastCommitMessage = Process.StandardOutput.ReadToEnd().Trim ();

				// Get the last commiter
				Process.StartInfo.Arguments = "log --format=\"%an\" -1";
				Process.Start();
				string LastCommitUserName = Process.StandardOutput.ReadToEnd().Trim ();

				ShowEventBubble (LastCommitUserName + " " + LastCommitMessage, 
				                 SparkleHelpers.GetAvatar (LastCommitEmail, 48),
				                 true);

			}

			Watcher.EnableRaisingEvents = true;

		}

		// Pushes the changes to the remote repo
		public void Push () {
			// TODO: What happens when network disconnects during a push
			Console.WriteLine ("[Git][" + Name + "] Pushing changes...");
			Process.StartInfo.Arguments = "push";
			Process.Start();
			Process.WaitForExit ();
//			SparkleUI.NotificationIcon.SetIdleState ();
		}

		// Ignores Repos, dotfiles, swap files and the like.
		public bool ShouldIgnore (string FileName) {
			if (FileName.Substring (0, 1).Equals (".") ||
				 FileName.Contains (".lock") ||
				 FileName.Contains (".git") ||
				 FileName.Contains ("/.") ||
				 Directory.Exists (LocalPath + FileName))
				return true; // Yes, ignore it.
			else if (FileName.Length > 3 &&
				     FileName.Substring (FileName.Length - 4).Equals (".swp"))
				return true;
			else return false;
		}

		// Creates a pretty commit message based on what has changed
		public string FormatCommitMessage () {

			bool DoneAddCommit = false;
			bool DoneEditCommit = false;
			bool DoneRenameCommit = false;
			bool DoneDeleteCommit = false;
			int FilesAdded = 0;
			int FilesEdited = 0;
			int FilesRenamed = 0;
			int FilesDeleted = 0;

			Process.StartInfo.Arguments = "status";
			Process.Start();
			string Output = Process.StandardOutput.ReadToEnd();

			foreach (string Line in Regex.Split (Output, "\n")) {
				if (Line.IndexOf ("new file:") > -1)
					FilesAdded++;
				if (Line.IndexOf ("modified:") > -1)
					FilesEdited++;
				if (Line.IndexOf ("renamed:") > -1)
					FilesRenamed++;
				if (Line.IndexOf ("deleted:") > -1)
					FilesDeleted++;
			}

			foreach (string Line in Regex.Split (Output, "\n")) {

				// Format message for when files are added,
				// example: "added 'file' and 3 more."
				if (Line.IndexOf ("new file:") > -1 && !DoneAddCommit) {
					DoneAddCommit = true;
					if (FilesAdded > 1)
						return "added ‘" + 
								  Line.Replace ("#\tnew file:", "").Trim () + 
							    "’ and " + (FilesAdded - 1) + " more.";
					else
						return "added ‘" + 
								  Line.Replace ("#\tnew file:", "").Trim () + "’.";
				}

				// Format message for when files are edited,
				// example: "edited 'file'."
				if (Line.IndexOf ("modified:") > -1 && !DoneEditCommit) {
					DoneEditCommit = true;
					if (FilesEdited > 1)
						return "edited ‘" + 
								  Line.Replace ("#\tmodified:", "").Trim () + 
							    "’ and " + (FilesEdited - 1) + " more.";
					else
						return "edited ‘" + 
								  Line.Replace ("#\tmodified:", "").Trim () + "’.";
				}

				// Format message for when files are edited,
				// example: "deleted 'file'."
				if (Line.IndexOf ("deleted:") > -1 && !DoneDeleteCommit) {
					DoneDeleteCommit = true;
					if (FilesDeleted > 1)
						return "deleted ‘" + 
								  Line.Replace ("#\tdeleted:", "").Trim () + 
							    "’ and " + (FilesDeleted - 1) + " more.";
					else
						return "deleted ‘" + 
								  Line.Replace ("#\tdeleted:", "").Trim () + "’.";
				}

				// Format message for when files are renamed,
				// example: "renamed 'file' to 'new name'."
				if (Line.IndexOf ("renamed:") > -1 && !DoneRenameCommit) {
					DoneDeleteCommit = true;
					if (FilesRenamed > 1)
						return "renamed ‘" + 
								  Line.Replace ("#\trenamed:", "").Trim ().Replace
							    (" -> ", "’ to ‘") + "’ and " + (FilesDeleted - 1) + 
							    " more.";
					else
						return "renamed ‘" + 
								  Line.Replace ("#\trenamed:", "").Trim ().Replace
							    (" -> ", "’ to ‘") + "’.";
				}

			}

			// Nothing happened:
			return "";

		}

		// Shows a notification with text and image
		public void ShowEventBubble (string Title, 
			                          Gdk.Pixbuf Avatar, 
			                          bool ShowButtons) {

			SparkleBubble StuffChangedBubble = new SparkleBubble (Title, "");
			StuffChangedBubble.Icon = Avatar;

			// Add a button to open the folder where the changed file is
			if (ShowButtons)
				StuffChangedBubble.AddAction ("", "Open Folder", 
						                   delegate {
							                  Process.StartInfo.FileName = "xdg-open";
				  	                     	Process.StartInfo.Arguments = LocalPath;
					 	                   	Process.Start();
				  	                     	Process.StartInfo.FileName = "git";
							                } );
		}

	}

}
