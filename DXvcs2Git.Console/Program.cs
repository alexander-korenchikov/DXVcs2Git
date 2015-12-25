﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using CommandLine;
using DXVcs2Git.Core;
using DXVcs2Git.Core.Serialization;
using DXVcs2Git.DXVcs;
using DXVcs2Git.Git;
using LibGit2Sharp;
using NGitLab.Models;
using Commit = LibGit2Sharp.Commit;
using User = DXVcs2Git.Core.User;

namespace DXVcs2Git.Console {
    internal class Program {
        const string repoPath = "repo";
        const string vcsServer = @"net.tcp://vcsservice.devexpress.devx:9091/DXVCSService";
        static void Main(string[] args) {
            var result = Parser.Default.ParseArguments<CommandLineOptions>(args);
            var exitCode = result.MapResult(clo => {
                return DoWork(clo);
            },
            errors => 1);
            Environment.Exit(exitCode);
        }

        static int DoWork(CommandLineOptions clo) {
            string localGitDir = clo.LocalFolder != null && Path.IsPathRooted(clo.LocalFolder) ? clo.LocalFolder : Path.Combine(Environment.CurrentDirectory, clo.LocalFolder ?? repoPath);
            EnsureGitDir(localGitDir);

            string gitRepoPath = clo.Repo;
            string username = clo.Login;
            string password = clo.Password;
            string gitlabauthtoken = clo.AuthToken;
            WorkMode workMode = clo.WorkMode;
            string branchName = clo.Branch;
            string trackerPath = clo.Tracker;
            string gitServer = clo.Server;

            TrackBranch branch = FindBranch(branchName, trackerPath);
            if (branch == null)
                return 1;
            DXVcsWrapper vcsWrapper = new DXVcsWrapper(vcsServer, username, password);

            string historyPath = GetVcsSyncHistory(vcsWrapper, branch.HistoryPath);
            if (historyPath == null)
                return 1;
            SyncHistory history = SyncHistory.Deserialize(historyPath);
            if (history == null)
                return 1;

            SyncHistoryWrapper syncHistory = new SyncHistoryWrapper(history, vcsWrapper, branch.HistoryPath, historyPath);
            var head = syncHistory.GetHead();
            do {
                if (head == null) {
                    Log.Error("Failed sync. Can`t find history item with success status.");
                    return 1;
                }
                if (head.Status == SyncHistoryStatus.Failed) {
                    Log.Error("Failed sync detected. Repair repo.");
                    return 1;
                }
                if (head.Status == SyncHistoryStatus.Success)
                    break;
                head = syncHistory.GetPrevious(head);
            }
            while (true);

            GitWrapper gitWrapper = CreateGitWrapper(gitRepoPath, localGitDir, branch, username, password);
            if (gitWrapper == null)
                return 1;

            GitLabWrapper gitLabWrapper = new GitLabWrapper(gitServer, gitlabauthtoken);
            RegisteredUsers registeredUsers = new RegisteredUsers(gitLabWrapper, vcsWrapper);
            User defaultUser = registeredUsers.GetUser(username);
            if (!defaultUser.IsRegistered) {
                Log.Error($"default user {username} is not registered in the active directory.");
                return 1;
            }

            if (workMode.HasFlag(WorkMode.history)) {
                ProcessHistoryResult result = ProcessHistory(vcsWrapper, gitWrapper, registeredUsers, defaultUser, gitRepoPath, localGitDir, branch, clo.CommitsCount, syncHistory, true);
                if (result == ProcessHistoryResult.NotEnough)
                    return 0;
                if (result == ProcessHistoryResult.Failed)
                    return 1;
            }
            if (workMode.HasFlag(WorkMode.mergerequests)) {
                int result = ProcessMergeRequests(vcsWrapper, gitWrapper, gitLabWrapper, registeredUsers, defaultUser, gitRepoPath, localGitDir, clo.Branch, clo.Tracker, syncHistory, username);
                if (result != 0)
                    return result;
            }
            return 0;
        }
        static string GetVcsSyncHistory(DXVcsWrapper vcsWrapper, string historyPath) {
            string local = Path.GetTempFileName();
            return vcsWrapper.GetFile(historyPath, local);
        }
        static void EnsureGitDir(string localGitDir) {
            if (Directory.Exists(localGitDir)) {
                KillProcess("tgitcache");
                DirectoryHelper.DeleteDirectory(localGitDir);
            }
        }
        static void KillProcess(string process) {
            Process[] procs = Process.GetProcessesByName(process);

            foreach (Process proc in procs) {
                proc.Kill();
                proc.WaitForExit();
            }
        }
        static GitWrapper CreateGitWrapper(string gitRepoPath, string localGitDir, TrackBranch branch, string username, string password) {
            GitWrapper gitWrapper = new GitWrapper(localGitDir, gitRepoPath, new UsernamePasswordCredentials() { Username = username, Password = password });
            if (gitWrapper.IsEmpty) {
                Log.Error($"Specified branch {branch.Name} in repo {gitRepoPath} is empty. Initialize repo properly.");
                return null;
            }

            gitWrapper.EnsureBranch(branch.Name, null);
            gitWrapper.CheckOut(branch.Name);
            gitWrapper.Fetch(updateTags: true);
            Log.Message($"Branch {branch.Name} initialized.");

            return gitWrapper;
        }
        static TrackBranch FindBranch(string branchName, string trackerPath) {
            string localPath = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            string configPath = Path.Combine(localPath, trackerPath);
            var branch = GetBranch(branchName, configPath);
            if (branch == null) {
                Log.Error($"Specified branch {branchName} not found in track file.");
                return null;
            }
            return branch;
        }
        static int ProcessMergeRequests(DXVcsWrapper vcsWrapper, GitWrapper gitWrapper, GitLabWrapper gitLabWrapper, RegisteredUsers users, User defaultUser, string gitRepoPath, string localGitDir, string branchName, string tracker, SyncHistoryWrapper syncHistory, string userName) {
            var project = gitLabWrapper.FindProject(gitRepoPath);
            TrackBranch branch = GetBranch(branchName, tracker);
            if (branch == null) {
                Log.Error($"Specified branch {branchName} not found in track file.");
                return 1;
            }
            var mergeRequests = gitLabWrapper.GetMergeRequests(project, x => x.TargetBranch == branchName).Where(x => x.Assignee?.Name == userName).ToList();
            if (!mergeRequests.Any()) {
                Log.Message("Zero registered merge requests.");
                return 0;
            }
            int result = 0;
            foreach (var mergeRequest in mergeRequests) {
                var mergeRequestResult = ProcessMergeRequest(vcsWrapper, gitWrapper, gitLabWrapper, users, defaultUser, localGitDir, branch, mergeRequest, syncHistory);
                if (mergeRequestResult == MergeRequestResult.Failed)
                    return 1;
                if (mergeRequestResult == MergeRequestResult.Conflicts) {
                    AssignBackConflictedMergeRequest(gitLabWrapper, users, mergeRequest);
                    result = 1;
                }
            }
            return result;
        }
        static void AssignBackConflictedMergeRequest(GitLabWrapper gitLabWrapper, RegisteredUsers users, MergeRequest mergeRequest) {
            User author = users.GetUser(mergeRequest.Author.Username);
            gitLabWrapper.UpdateMergeRequestAssignee(mergeRequest, author.UserName);
        }
        static bool ValidateMergeRequest(DXVcsWrapper vcsWrapper, RegisteredUsers users, TrackBranch branch, SyncHistoryItem previous, User defaultUser) {
            var history = vcsWrapper.GenerateHistory(branch, new DateTime(previous.VcsCommitTimeStamp)).Where(x => x.ActionDate.Ticks > previous.VcsCommitTimeStamp);
            if (history.Any(x => x.User != defaultUser.UserName))
                return false;
            return true;
        }
        static MergeRequestResult ProcessMergeRequest(DXVcsWrapper vcsWrapper, GitWrapper gitWrapper, GitLabWrapper gitLabWrapper, RegisteredUsers users, User defaultUser, string localGitDir, TrackBranch branch, MergeRequest mergeRequest, SyncHistoryWrapper syncHistory) {
            switch (mergeRequest.State) {
                case "reopened":
                case "opened":
                    return ProcessOpenedMergeRequest(vcsWrapper, gitWrapper, gitLabWrapper, users, defaultUser, localGitDir, branch, mergeRequest, syncHistory);
            }
            return MergeRequestResult.Conflicts;
        }
        static MergeRequestResult ProcessOpenedMergeRequest(DXVcsWrapper vcsWrapper, GitWrapper gitWrapper, GitLabWrapper gitLabWrapper, RegisteredUsers users, User defaultUser, string localGitDir, TrackBranch branch, MergeRequest mergeRequest, SyncHistoryWrapper syncHistory) {
            string autoSyncToken = syncHistory.CreateNewToken();
            var lastHistoryItem = syncHistory.GetHead();

            Log.Message($"Start merging mergerequest {mergeRequest.Title}");

            var changes = gitLabWrapper.GetMergeRequestChanges(mergeRequest).Where(x => branch.TrackItems.FirstOrDefault(track => x.OldPath.StartsWith(track.ProjectPath)) != null);
            var genericChange = changes.Select(x => ProcessMergeRequestChanges(mergeRequest, x, localGitDir, branch, autoSyncToken)).ToList();

            string sourceBranch = mergeRequest.SourceBranch;
            gitWrapper.EnsureBranch(sourceBranch, null);
            gitWrapper.Reset(sourceBranch);
            Log.Message($"Reset branch {sourceBranch} completed.");

            string targetBranch = mergeRequest.TargetBranch;
            gitWrapper.EnsureBranch(targetBranch, null);
            gitWrapper.Reset(targetBranch);
            Log.Message($"Reset branch {targetBranch} completed.");
            var result = gitWrapper.Merge(sourceBranch, defaultUser);
            if (result != MergeStatus.Conflicts) {
                Log.Message($"Merge attempt from {targetBranch} to {sourceBranch} completed without conflicts");
                CommentWrapper comment = CalcComment(mergeRequest, branch, autoSyncToken);
                if (!vcsWrapper.ProcessCheckout(genericChange)) {
                    Log.Message("Merging merge request failed.");
                    return MergeRequestResult.Conflicts;
                }
                gitWrapper.Reset(branch.Name);
                gitWrapper.Pull(defaultUser, branch.Name);

                mergeRequest = gitLabWrapper.ProcessMergeRequest(mergeRequest, comment.ToString());
                if (mergeRequest.State == "merged") {
                    gitWrapper.Reset(branch.Name);
                    gitWrapper.Pull(defaultUser, branch.Name);
                    var gitCommit = gitWrapper.FindCommit(branch.Name, x => CommentWrapper.Parse(x.Message).Token == autoSyncToken);
                    Log.Message("Merge request merged successfully.");

                    long timeStamp = lastHistoryItem.VcsCommitTimeStamp;
                    if (vcsWrapper.ProcessCheckIn(genericChange, comment.ToString())) {
                        var checkinHistory = vcsWrapper.GenerateHistory(branch, new DateTime(timeStamp)).Where(x => x.ActionDate.Ticks > timeStamp);
                        var lastCommit = checkinHistory.OrderBy(x => x.ActionDate).LastOrDefault();
                        long newTimeStamp = lastCommit?.ActionDate.Ticks ?? timeStamp;
                        var mergeRequestResult = MergeRequestResult.Success;
                        if (!ValidateMergeRequest(vcsWrapper, users, branch, lastHistoryItem, defaultUser))
                            mergeRequestResult = MergeRequestResult.Mixed;

                        syncHistory.Add(gitCommit.Sha, newTimeStamp, autoSyncToken, mergeRequestResult == MergeRequestResult.Success ? SyncHistoryStatus.Success : SyncHistoryStatus.Mixed);
                        syncHistory.Save();
                        Log.Message("Merge request checkin successfully.");
                        return MergeRequestResult.Mixed;
                    }
                    Log.Error("Merge request checkin failed.");
                    var failedHistory = vcsWrapper.GenerateHistory(branch, new DateTime(timeStamp));
                    var lastFailedCommit = failedHistory.OrderBy(x => x.ActionDate).LastOrDefault();
                    syncHistory.Add(gitCommit.Sha, lastFailedCommit?.ActionDate.Ticks ?? timeStamp, autoSyncToken, SyncHistoryStatus.Failed);
                    syncHistory.Save();

                    Log.Error("Revert merging completed.");

                    return MergeRequestResult.Failed;
                }

                Log.Message("Merge request merging failed");
                return MergeRequestResult.Conflicts;
            }
            Log.Message($"Merge request merging from {sourceBranch} to {targetBranch} failed due conflicts. Resolve conflicts manually.");
            return MergeRequestResult.Conflicts;
        }
        static SyncItem ProcessMergeRequestChanges(MergeRequest mergeRequest, MergeRequestFileData fileData, string localGitDir, TrackBranch branch, string token) {
            string vcsRoot = branch.RepoRoot;
            var syncItem = new SyncItem();
            if (fileData.IsNew) {
                syncItem.SyncAction = SyncAction.New;
                syncItem.LocalPath = CalcLocalPath(localGitDir, branch, fileData.OldPath);
                syncItem.VcsPath = CalcVcsPath(vcsRoot, branch, fileData.OldPath);
            }
            else if (fileData.IsDeleted) {
                syncItem.SyncAction = SyncAction.Delete;
                syncItem.LocalPath = CalcLocalPath(localGitDir, branch, fileData.OldPath);
                syncItem.VcsPath = CalcVcsPath(vcsRoot, branch, fileData.OldPath);
            }
            else if (fileData.IsRenamed) {
                syncItem.SyncAction = SyncAction.Move;
                syncItem.LocalPath = CalcLocalPath(localGitDir, branch, fileData.OldPath);
                syncItem.NewLocalPath = CalcLocalPath(localGitDir, branch, fileData.NewPath);
                syncItem.VcsPath = CalcVcsPath(vcsRoot, branch, fileData.OldPath);
                syncItem.NewVcsPath = CalcVcsPath(vcsRoot, branch, fileData.NewPath);
            }
            else {
                syncItem.SyncAction = SyncAction.Modify;
                syncItem.LocalPath = CalcLocalPath(localGitDir, branch, fileData.OldPath);
                syncItem.VcsPath = CalcVcsPath(vcsRoot, branch, fileData.OldPath);
            }
            syncItem.Comment = CalcComment(mergeRequest, branch, token);
            return syncItem;
        }
        static string CalcLocalPath(string localGitDir, TrackBranch branch, string path) {
            return Path.Combine(localGitDir, path);
        }
        static string CalcVcsPath(string vcsRoot, TrackBranch branch, string path) {
            var trackItem = branch.TrackItems.First(x => path.StartsWith(x.ProjectPath));
            string result = string.IsNullOrEmpty(trackItem.AdditionalOffset) ? Path.Combine(vcsRoot, path) : Path.Combine(vcsRoot, trackItem.AdditionalOffset, path);
            return result.Replace("\\", "/");
        }
        static ProcessHistoryResult ProcessHistory(DXVcsWrapper vcsWrapper, GitWrapper gitWrapper, RegisteredUsers users, User defaultUser, string gitRepoPath, string localGitDir, TrackBranch branch, int commitsCount, SyncHistoryWrapper syncHistory, bool mergeCommits) {
            DateTime lastCommit = CalcLastCommitDate(gitWrapper, defaultUser, branch, syncHistory);
            Log.Message($"Last commit has been performed at {lastCommit.ToLocalTime()}.");

            var history = vcsWrapper.GenerateHistory(branch, lastCommit).OrderBy(x => x.ActionDate).ToList();
            Log.Message($"History generated. {history.Count} history items obtained.");

            IList<CommitItem> commits = vcsWrapper.GenerateCommits(history).Where(x => x.TimeStamp > lastCommit && !IsLabel(x)).ToList();
            if (mergeCommits)
                commits = vcsWrapper.MergeCommits(commits);
            
            if (commits.Count > commitsCount) {
                Log.Message($"Commits generated. First {commitsCount} of {commits.Count} commits taken.");
                commits = commits.Take(commitsCount).ToList();
            }
            else {
                Log.Message($"Commits generated. {commits.Count} commits taken.");
            }
            if (commits.Count > 0)
                ProcessHistoryInternal(vcsWrapper, gitWrapper, users, defaultUser, localGitDir, branch, commits, syncHistory);
            Log.Message($"Importing history from vcs completed.");

            return commits.Count > commitsCount ? ProcessHistoryResult.NotEnough : ProcessHistoryResult.Success;
        }
        static DateTime CalcLastCommitDate(GitWrapper gitWrapper, User defaultUser, TrackBranch branch, SyncHistoryWrapper syncHistory) {
            var head = syncHistory.GetHead();
            do {
                if (head == null || head.Status == SyncHistoryStatus.Success)
                    break;
                head = syncHistory.GetPrevious(head);
            }
            while (true);
            if (head != null)
                return new DateTime(head.VcsCommitTimeStamp);
            return gitWrapper.GetLastCommitTimeStamp(branch.Name, defaultUser);
        }
        static void ProcessHistoryInternal(DXVcsWrapper vcsWrapper, GitWrapper gitWrapper, RegisteredUsers users, User defaultUser, string localGitDir, TrackBranch branch, IList<CommitItem> commits, SyncHistoryWrapper syncHistory) {
            ProjectExtractor extractor = new ProjectExtractor(commits, (item) => {
                var localCommits = vcsWrapper.GetCommits(item.TimeStamp, item.Items).Where(x => !IsLabel(x)).ToList();
                bool hasModifications = false;
                Commit last = null;
                string token = syncHistory.CreateNewToken();
                foreach(var localCommit in localCommits) {
                    string localProjectPath = Path.Combine(localGitDir, localCommit.Track.ProjectPath);
                    DirectoryHelper.DeleteDirectory(localProjectPath);
                    vcsWrapper.GetProject(vcsServer, localCommit.Track.Path, localProjectPath, item.TimeStamp);

                    gitWrapper.Fetch();
                    Log.Message($"git stage {localCommit.Track.ProjectPath}");
                    gitWrapper.Stage(localCommit.Track.ProjectPath);
                    try {
                        var comment = CalcComment(localCommit, token);
                        string author = CalcAuthor(localCommit, defaultUser);
                        User user = users.GetUser(author);
                        last = gitWrapper.Commit(comment.ToString(), user, user, localCommit.TimeStamp, false);
                        hasModifications = true;
                    }
                    catch (Exception) {
                        Log.Message($"Empty commit detected for {localCommit.Author} {localCommit.TimeStamp}.");
                    }
                }
                if (hasModifications) {
                    gitWrapper.Push(branch.Name);
                    syncHistory.Add(last.Sha, item.TimeStamp.Ticks, token);
                }
                else {
                    var head = syncHistory.GetHead();
                    syncHistory.Add(head.GitCommitSha, item.TimeStamp.Ticks, token);
                    string author = CalcAuthor(item, defaultUser);
                    Log.Message($"Push empty commits rejected for {author} {item.TimeStamp}.");
                }
                syncHistory.Save();
            });
            int i = 0;
            while (extractor.PerformExtraction())
                Log.Message($"{++i} from {commits.Count} push to branch {branch.Name} completed.");
        }
        static string CalcAuthor(CommitItem localCommit, User defaultUser) {
            string author = localCommit.Author;
            if (string.IsNullOrEmpty(author))
                author = localCommit.Items.FirstOrDefault(x => !string.IsNullOrEmpty(x.User))?.User;
            if (author != defaultUser.UserName)
                return author;
            var comment = localCommit.Items.FirstOrDefault(x => CommentWrapper.IsAutoSyncComment(x.Comment));
            if (comment == null)
                return author;
            var commentWrapper = CommentWrapper.Parse(comment.Comment);
            return commentWrapper.Author;
        }
        static TrackBranch GetBranch(string branchName, string configPath) {
            try {
                var branches = TrackBranch.Deserialize(configPath);
                return branches.First(x => x.Name == branchName);
            }
            catch (Exception ex) {
                Log.Error("Loading items for track failed", ex);
                return null;
            }
        }
        static bool IsLabel(CommitItem item) {
            return item.Items.Any(x => !string.IsNullOrEmpty(x.Label));
        }
        static CommentWrapper CalcComment(MergeRequest mergeRequest, TrackBranch branch, string autoSyncToken) {
            CommentWrapper comment = new CommentWrapper();
            comment.Author = mergeRequest.Author.Username;
            comment.Branch = branch.Name;
            comment.Token = autoSyncToken;
            comment.Comment = CalcCommentForMergeRequest(mergeRequest);
            return comment;
        }
        static string CalcCommentForMergeRequest(MergeRequest mergeRequest) {
            StringBuilder sb = new StringBuilder();
            if (!string.IsNullOrEmpty(mergeRequest.Title))
                sb.AppendLine(mergeRequest.Title);
            if (!string.IsNullOrEmpty(mergeRequest.Description)) {
                sb.Append(mergeRequest.Description);
                sb.AppendLine();
            }
            return sb.ToString();
        }
        static CommentWrapper CalcComment(CommitItem item, string token) {
            CommentWrapper comment = new CommentWrapper();
            comment.TimeStamp = item.TimeStamp.Ticks.ToString();
            comment.Author = item.Author;
            comment.Branch = item.Track.Branch;
            comment.Token = token;
            if (item.Items.Any(x => !string.IsNullOrEmpty(x.Comment) && CommentWrapper.IsAutoSyncComment(x.Comment))) {
                comment.Comment = item.Items.Select(x => CommentWrapper.Parse(x.Comment ?? x.Message).Comment).FirstOrDefault(x => !string.IsNullOrEmpty(x));
                comment.Author = comment.Comment != null ? CommentWrapper.Parse(comment.Comment).Author : comment.Author;
            }
            else
                comment.Comment = item.Items.FirstOrDefault(x => !string.IsNullOrEmpty(x.Comment))?.Comment;
            return comment;
        }
    }
    public enum MergeRequestResult {
        Success,
        Failed,
        Conflicts,
        Mixed,
    }
    public enum ProcessHistoryResult {
        Success,
        Failed,
        NotEnough,
    }
}