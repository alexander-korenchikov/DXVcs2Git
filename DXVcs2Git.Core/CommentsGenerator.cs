﻿using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DXVcs2Git.Core {
    public abstract class CommentsGenerator {
        const string DefaultStart = "dxvcs2gitservice ";
        const string sha = "sha:";
        const string branch = "branch:";
        const string timeStamp = "timestamp:";
        const string author = "author:";
        const string token = "token:";
        static readonly Regex ParseShaRegex = new Regex(@"(?<=sha:\s*)[0-9a-f]+", RegexOptions.Compiled);
        static readonly Regex ParseBranchRegex = new Regex(@"(?<=branch:\s*)\w+", RegexOptions.Compiled);
        static readonly Regex ParseTimeStampRegex = new Regex(@"(?<=timestamp:\s*)[0-9]+", RegexOptions.Compiled);
        static readonly Regex ParseAuthorRegex = new Regex(@"(?<=author:\s*)\w+", RegexOptions.Compiled);
        static readonly Regex ParseTokenRegex = new Regex(@"(?<=token:\s*)\S+", RegexOptions.Compiled);
        static CommentsGenerator() {

        }

        public CommentWrapper Parse(string commentString) {
            var comment = new CommentWrapper();
            ParseInternal(comment, commentString);
            return comment;
        }
        protected virtual void ParseInternal(CommentWrapper comment, string commentString) {
            if (string.IsNullOrEmpty(commentString))
                return;
            var chunks = commentString.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (!chunks.Any())
                return;
            foreach (var chunk in chunks)
                ParseChunk(comment, chunk);
        }
        void ParseChunk(CommentWrapper comment, string chunk) {
            if (!chunk.StartsWith(DefaultStart)) {
                if (!string.IsNullOrEmpty(chunk) && !string.IsNullOrWhiteSpace(chunk))
                    comment.Comment += chunk + Environment.NewLine;
                return;
            }
            var clean = chunk.Remove(0, DefaultStart.Length);
            if (clean.StartsWith(sha))
                comment.Sha = ParseSha(clean);
            else if (clean.StartsWith(branch))
                comment.Branch = ParseBranch(clean);
            else if (clean.StartsWith(timeStamp))
                comment.TimeStamp = ParseTimeStamp(clean);
            else if (clean.StartsWith(author))
                comment.Author = ParseAuthor(clean);
            else if (clean.StartsWith(token))
                comment.Token = ParseToken(clean);
        }
        string ParseAuthor(string value) {
            var match = ParseAuthorRegex.Match(value);
            return match.Value;
        }
        string ParseTimeStamp(string value) {
            var match = ParseTimeStampRegex.Match(value);
            return match.Value;
        }
        string ParseBranch(string value) {
            var match = ParseBranchRegex.Match(value);
            return match.Value;
        }
        string ParseSha(string value) {
            var match = ParseShaRegex.Match(value);
            return match.Value;
        }
        string ParseToken(string value) {
            var match = ParseTokenRegex.Match(value);
            return match.Value;
        }
        public string ConvertToString(CommentWrapper comment) {
            StringBuilder sb = new StringBuilder();
            ConvertToStringInternal(comment, sb);
            return sb.ToString();
        }
        protected virtual void ConvertToStringInternal(CommentWrapper comment, StringBuilder sb) {
        }
        protected static void WriteSha(CommentWrapper comment, StringBuilder sb) {
            if (!string.IsNullOrEmpty(comment.Sha))
                sb.AppendLine(DefaultStart + sha + " " + comment.Sha);
        }
        protected static void WriteTimestamp(CommentWrapper comment, StringBuilder sb) {
            if (!string.IsNullOrEmpty(comment.TimeStamp))
                sb.AppendLine(DefaultStart + timeStamp + " " + comment.TimeStamp);
        }
        protected static void WriteToken(CommentWrapper comment, StringBuilder sb) {
            sb.AppendLine(DefaultStart + token + " " + comment.Token);
        }
        protected static void WriteBranch(CommentWrapper comment, StringBuilder sb) {
            sb.AppendLine(DefaultStart + branch + " " + comment.Branch);
        }
        protected static void WriteAuthor(CommentWrapper comment, StringBuilder sb) {
            sb.AppendLine(DefaultStart + author + " " + comment.Author);
        }
    }
}
