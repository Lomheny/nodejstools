﻿/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the Apache License, Version 2.0, please send an email to 
 * vspython@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.NodejsTools.Classifier;
using Microsoft.NodejsTools.Project;
using Microsoft.NodejsTools.Repl;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudioTools;
using Microsoft.VisualStudioTools.Project;

namespace Microsoft.NodejsTools.Intellisense {
    sealed partial class CompletionSource : ICompletionSource {
        public const string NodejsRequireCompletionSetMoniker = "Node.js require";

        private readonly ITextBuffer _textBuffer;
        private readonly NodejsClassifier _classifier;
        private readonly IServiceProvider _serviceProvider;
        private readonly IGlyphService _glyphService;

        private static string[] _allowRequireTokens = new[] { "!", "!=", "!==", "%", "%=", "&", "&&", "&=", "(", ")", 
            "*", "*=", "+", "++", "+=", ",", "-", "--", "-=",  "..", "...", "/", "/=", ":", ";", "<", "<<", "<<=", 
            "<=", "=", "==", "===", ">", ">=", ">>", ">>=", ">>>", ">>>=", "?", "[", "^", "^=", "{", "|", "|=", "||", 
            "}", "~", "in", "case", "new", "return", "throw", "typeof"
        };

        private static string[] _keywords = new[] {
            "break", "case", "catch", "class", "const", "continue", "default", "delete", "do", "else", "eval", "extends", 
            "false", "field", "final", "finally", "for", "function", "if", "import", "in", "instanceof", "new", "null", 
            "package", "private", "protected", "public", "return", "super", "switch", "this", "throw", "true", "try", 
            "typeof", "var", "while", "with",
            "abstract", "debugger", "enum", "export", "goto", "implements", "native", "static", "synchronized", "throws",
            "transient", "volatile"
        };

        public CompletionSource(ITextBuffer textBuffer, NodejsClassifier classifier, IServiceProvider serviceProvider, IGlyphService glyphService) {
            _textBuffer = textBuffer;
            _classifier = classifier;
            _serviceProvider = serviceProvider;
            _glyphService = glyphService;
        }

        #region ICompletionSource Members

        public void AugmentCompletionSession(ICompletionSession session, IList<CompletionSet> completionSets) {
            var buffer = _textBuffer;
            var snapshot = buffer.CurrentSnapshot;
            var triggerPoint = session.GetTriggerPoint(buffer).GetPoint(snapshot);

            // Disable completions if user is editing a special command (e.g. ".cls") in the REPL.
            if (snapshot.TextBuffer.Properties.ContainsProperty(typeof(IReplEvaluator)) && snapshot.Length != 0 && snapshot[0] == '.') {
                return;
            }

            if (ShouldTriggerRequireIntellisense(triggerPoint, _classifier, true, true)) {
                AugmentCompletionSessionForRequire(triggerPoint, session, completionSets);
                return;
            }

            var textBuffer = _textBuffer;
            var span = GetApplicableSpan(session, textBuffer);
            var provider = VsProjectAnalyzer.GetCompletions(
                _textBuffer.CurrentSnapshot,
                span,
                session.GetTriggerPoint(buffer)
            );

            var completions = provider.GetCompletions(_glyphService);
            if (completions != null) {
                completionSets.Add(completions);
            }
        }
        private void AugmentCompletionSessionForRequire(SnapshotPoint triggerPoint, ICompletionSession session, IList<CompletionSet> completionSets) {
            var classifications = EnumerateClassificationsInReverse(_classifier, triggerPoint);
            bool? doubleQuote = null;
            int length = 0;

            // check which one of these we're doing:
            // require(         inserting 'module' at trigger point
            // require('        inserting module' at trigger point
            // requre('ht')     ctrl space at ht, inserting http' at trigger point - 2
            // requre('addo')   ctrl space at add, inserting addons' at trigger point - 3

            // Therefore we have no quotes or quotes.  In no quotes we insert both
            // leading and trailing quotes.  In quotes we leave the leading quote in
            // place and replace any other quotes value that was already there.

            if (classifications.MoveNext()) {
                var curText = classifications.Current.Span.GetText();
                if (curText.StartsWith("'") || curText.StartsWith("\"")) {
                    // we're in the quotes case, figure out the existing string,
                    // and use that at the applicable span.
                    var fullSpan = _classifier.GetClassificationSpans(
                        new SnapshotSpan(
                            classifications.Current.Span.Start,
                            classifications.Current.Span.End.GetContainingLine().End
                        )
                    ).First();

                    doubleQuote = curText[0] == '"';
                    triggerPoint -= (curText.Length - 1);
                    length = fullSpan.Span.Length - 1;
                }
                // else it's require(
            }

            var completions = GenerateBuiltinCompletions(doubleQuote);
            completions.AddRange(GetProjectCompletions(doubleQuote));
            completions.Sort(CompletionSorter);

            completionSets.Add(
                new CompletionSet(
                    NodejsRequireCompletionSetMoniker,
                    "Node.js require",
                    _textBuffer.CurrentSnapshot.CreateTrackingSpan(
                        triggerPoint,
                        length,
                        SpanTrackingMode.EdgeInclusive
                    ),
                    completions,
                    null
                )
            );
        }

        /// <summary>
        /// Returns the span to use for the provided intellisense session.
        /// </summary>
        /// <returns>A tracking span. The span may be of length zero if there
        /// is no suitable token at the trigger point.</returns>
        internal static ITrackingSpan GetApplicableSpan(IIntellisenseSession session, ITextBuffer buffer) {
            var snapshot = buffer.CurrentSnapshot;
            var triggerPoint = session.GetTriggerPoint(buffer);

            var span = GetApplicableSpan(snapshot, triggerPoint);
            if (span != null) {
                return span;
            }
            return snapshot.CreateTrackingSpan(triggerPoint.GetPosition(snapshot), 0, SpanTrackingMode.EdgeInclusive);
        }

        /// <summary>
        /// Returns the applicable span at the provided position.
        /// </summary>
        /// <returns>A tracking span, or null if there is no token at the
        /// provided position.</returns>
        internal static ITrackingSpan GetApplicableSpan(ITextSnapshot snapshot, ITrackingPoint point) {
            return GetApplicableSpan(snapshot, point.GetPosition(snapshot));
        }

        /// <summary>
        /// Returns the applicable span at the provided position.
        /// </summary>
        /// <returns>A tracking span, or null if there is no token at the
        /// provided position.</returns>
        internal static ITrackingSpan GetApplicableSpan(ITextSnapshot snapshot, int position) {
            var classifier = snapshot.TextBuffer.GetNodejsClassifier();
            var line = snapshot.GetLineFromPosition(position);
            if (classifier == null || line == null) {
                return null;
            }

            var spanLength = position - line.Start.Position;
            // Increase position by one to include 'fob' in: "abc.|fob"
            if (spanLength < line.Length) {
                spanLength += 1;
            }

            var classifications = classifier.GetClassificationSpans(new SnapshotSpan(line.Start, spanLength));
            // Handle "|"
            if (classifications == null || classifications.Count == 0) {
                return null;
            }

            var lastToken = classifications[classifications.Count - 1];
            // Handle "fob |"
            if (lastToken == null || position > lastToken.Span.End) {
                return null;
            }

            if (position > lastToken.Span.Start) {
                if (lastToken.CanComplete()) {
                    // Handle "fo|o"
                    return snapshot.CreateTrackingSpan(lastToken.Span, SpanTrackingMode.EdgeInclusive);
                } else {
                    // Handle "<|="
                    return null;
                }
            }

            var secondLastToken = classifications.Count >= 2 ? classifications[classifications.Count - 2] : null;
            if (lastToken.Span.Start == position && lastToken.CanComplete() &&
                (secondLastToken == null ||             // Handle "|fob"
                 position > secondLastToken.Span.End || // Handle "if |fob"
                 !secondLastToken.CanComplete())) {     // Handle "abc.|fob"
                return snapshot.CreateTrackingSpan(lastToken.Span, SpanTrackingMode.EdgeInclusive);
            }

            // Handle "abc|."
            // ("ab|c." would have been treated as "ab|c")
            if (secondLastToken != null && secondLastToken.Span.End == position && secondLastToken.CanComplete()) {
                return snapshot.CreateTrackingSpan(secondLastToken.Span, SpanTrackingMode.EdgeInclusive);
            }

            return null;
        }

        private int CompletionSorter(Completion x, Completion y) {
            if (x.DisplayText.StartsWith(".")) {
                if (y.DisplayText.StartsWith(".")) {
                    return String.Compare(x.DisplayText, y.DisplayText);
                }
                return 1;
            } else if (y.DisplayText.StartsWith(".")) {
                return -1;
            }
            return String.Compare(x.DisplayText, y.DisplayText);
        }

        /// <summary>
        /// Checks if we are at a require statement where we can offer completions.
        /// 
        /// The bool flags are used to control when we are checking if we should provide
        /// the completions before updating the buffer the characters the user just typed.
        /// </summary>
        /// <param name="triggerPoint">The point where the completion session is being triggered</param>
        /// <param name="classifier">A classifier for getting the tokens</param>
        /// <param name="eatOpenParen">True if the open paren has been inserted and we should expect it</param>
        /// <param name="allowQuote">True if we will parse the require(' or require(" forms.</param>
        /// <returns></returns>
        internal static bool ShouldTriggerRequireIntellisense(SnapshotPoint triggerPoint, IClassifier classifier, bool eatOpenParen, bool allowQuote = false) {
            var classifications = EnumerateClassificationsInReverse(classifier, triggerPoint);
            bool atRequire = false;

            if (allowQuote && classifications.MoveNext()) {
                var curText = classifications.Current.Span.GetText();
                if (!curText.StartsWith("'") && !curText.StartsWith("\"")) {
                    // no leading quote, reset back to original classifications.
                    classifications = EnumerateClassificationsInReverse(classifier, triggerPoint);
                }
            }

            if ((!eatOpenParen || EatToken(classifications, "(")) && EatToken(classifications, "require")) {
                // start of a file or previous token to require is followed by an expression
                if (!classifications.MoveNext()) {
                    // require at beginning of the file
                    atRequire = true;
                } else {
                    var tokenText = classifications.Current.Span.GetText();

                    atRequire =
                        classifications.Current.Span.Start.GetContainingLine().LineNumber != triggerPoint.GetContainingLine().LineNumber ||
                        tokenText.EndsWith(";") || // f(x); has ); displayed as a single token
                        _allowRequireTokens.Contains(tokenText) || // require after a token which starts an expression
                        (tokenText.All(IsIdentifierChar) && !_keywords.Contains(tokenText));    // require after anything else that isn't a statement like keyword 
                                                                                                //      (including identifiers which are on the previous line)
                }
            }
            
            return atRequire;
        }

        internal static bool IsIdentifierChar(char ch) {
            return ch == '_' || (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '$';
        }

        // TODO: require completions should move into the analyzer
        private IEnumerable<Completion> GetProjectCompletions(bool? doubleQuote) {
            var filePath = _textBuffer.GetFilePath();

            var rdt = _serviceProvider.GetService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
            IVsHierarchy hierarchy;
            uint itemId;
            IntPtr punk = IntPtr.Zero;
            uint cookie;
            int hr;
            CompletionInfo[] res = null;
            try {
                hr = rdt.FindAndLockDocument((uint)_VSRDTFLAGS.RDT_NoLock, filePath, out hierarchy, out itemId, out punk, out cookie);
                if (ErrorHandler.Succeeded(hr) && hierarchy != null) {
                    var dteProj = hierarchy.GetProject();
                    if (dteProj != null) {
                        var nodeProj = dteProj.GetNodeProject();
                        if (nodeProj != null) {
                            // file is open in our project, we can provide completions...
                            var node = nodeProj.FindNodeByFullPath(filePath) as FileNode;
                            Debug.Assert(node != null);

                            if (!nodeProj._requireCompletionCache.TryGetCompletions(node, out res)) {
                                List<CompletionInfo> completions = new List<CompletionInfo>();

                                GetParentNodeModules(nodeProj, node.Parent, completions);
                                GetPeerAndChildModules(nodeProj, node, completions);

                                nodeProj._requireCompletionCache.CacheCompletions(node, res = completions.ToArray());
                            }
                        }
                    }
                }
            } finally {
                if (punk != IntPtr.Zero) {
                    Marshal.Release(punk);
                }
            }
            if (res != null) {
                return res.Select(x => x.ToCompletion(doubleQuote));
            }
            return Enumerable.Empty<Completion>();
        }

        private void GetParentNodeModules(NodejsProjectNode nodeProj, HierarchyNode parent, List<CompletionInfo> projectCompletions) {
            do {
                var modulesFolder = parent.FindImmediateChildByName(NodejsConstants.NodeModulesFolder);
                if (modulesFolder != null) {
                    GetParentNodeModules(nodeProj, modulesFolder, parent, projectCompletions);
                }
                parent = parent.Parent;
            } while (parent != null);
        }

        private void GetParentNodeModules(NodejsProjectNode nodeProj, HierarchyNode modulesFolder, HierarchyNode fromFolder, List<CompletionInfo> projectCompletions) {
            for (HierarchyNode n = modulesFolder.FirstChild; n != null; n = n.NextSibling) {
                FileNode file = n as FileNode;
                if (file != null &&
                    NodejsConstants.FileExtension.Equals(
                        Path.GetExtension(file.Url),
                        StringComparison.OrdinalIgnoreCase
                    )) {
                    projectCompletions.Add(                        
                        MakeCompletion(
                            nodeProj,
                            file,
                            MakeNodePath(fromFolder, file).Substring(NodejsConstants.NodeModulesFolder.Length + 1)
                        )
                    );
                }

                FolderNode folder = n as FolderNode;
                if (folder != null) {
                    if (folder.FindImmediateChildByName(NodejsConstants.PackageJsonFile) != null || 
                        folder.FindImmediateChildByName(NodejsConstants.DefaultPackageMainFile) != null) {
                        projectCompletions.Add(
                            MakeCompletion(
                                nodeProj,
                                folder,
                                MakeNodePath(fromFolder, folder).Substring(NodejsConstants.NodeModulesFolder.Length + 1)
                            )
                        );
                        // we don't recurse here - you can pull out a random .js file from a package, 
                        // but we don't include those in the available completions.
                    } else if (!NodejsConstants.NodeModulesFolder.Equals(Path.GetFileName(folder.Url), StringComparison.OrdinalIgnoreCase)) {
                        // recurse into folder and make available members...
                        GetParentNodeModules(nodeProj, folder, fromFolder, projectCompletions);
                    }
                }
            }
        }

        private static string MakeNodePath(HierarchyNode relativeTo, HierarchyNode node) {
            return CommonUtils.CreateFriendlyFilePath(relativeTo.FullPathToChildren, CommonUtils.TrimEndSeparator(node.Url)).Replace("\\", "/");
        }

        private CompletionInfo MakeCompletion(NodejsProjectNode nodeProj, HierarchyNode node, string displayText) {
            return new CompletionInfo(
                displayText,
                CommonUtils.CreateFriendlyFilePath(nodeProj.ProjectHome, node.Url) + " (in project)",
                _glyphService.GetGlyph(StandardGlyphGroup.GlyphGroupModule, StandardGlyphItem.GlyphItemProtected)
            );
        }

        /// <summary>
        /// Finds available modules which are children of the folder where we're doing require
        /// completions from.
        /// </summary>
        private void GetPeerAndChildModules(NodejsProjectNode nodeProj, HierarchyNode node, List<CompletionInfo> projectCompletions) {
            var folder = node.Parent;
            foreach (HierarchyNode child in EnumCodeFilesExcludingNodeModules(folder)) {
                if (child == node) {
                    // you can require yourself, but we don't show the completion
                    continue;
                }

                projectCompletions.Add(
                    MakeCompletion(
                        nodeProj,
                        child,
                        "./" + MakeNodePath(folder, child)
                    )
                );
            }
        }

        /// <summary>
        /// Enumerates the available code files excluding node modules whih we handle specially.
        /// </summary>
        internal IEnumerable<HierarchyNode> EnumCodeFilesExcludingNodeModules(HierarchyNode node) {
            for (HierarchyNode n = node.FirstChild; n != null; n = n.NextSibling) {
                var fileNode = n as NodejsFileNode;
                if (fileNode != null) {
                    yield return fileNode;
                }

                var folder = n as FolderNode;
                // exclude node_modules, you can do require('./node_modules/foo.js'), but no
                // one does.
                if (folder != null) {
                    var folderName = Path.GetFileName(CommonUtils.TrimEndSeparator(folder.Url));
                    if (!folderName.Equals(NodejsConstants.NodeModulesFolder, StringComparison.OrdinalIgnoreCase)) {
                        if (folder.FindImmediateChildByName(NodejsConstants.PackageJsonFile) != null ||
                            folder.FindImmediateChildByName(NodejsConstants.DefaultPackageMainFile) != null) {
                            yield return folder;
                        } 

                        foreach (var childNode in EnumCodeFilesExcludingNodeModules(n)) {
                            yield return childNode;
                        }
                    }
                }
            }
        }

        private static bool EatToken(IEnumerator<ClassificationSpan> classifications, string tokenText) {
            return classifications.MoveNext() && classifications.Current.Span.GetText() == tokenText;
        }

        /// <summary>
        /// Enumerates all of the classifications in reverse starting at start to the beginning of the file.
        /// </summary>
        private static IEnumerator<ClassificationSpan> EnumerateClassificationsInReverse(IClassifier classifier, SnapshotPoint start) {
            var curLine = start.GetContainingLine();
            var spanEnd = start;

            for (; ; ) {
                var classifications = classifier.GetClassificationSpans(new SnapshotSpan(curLine.Start, spanEnd));
                for (int i = classifications.Count - 1; i >= 0; i--) {
                    yield return classifications[i];
                }

                if (curLine.LineNumber == 0) {
                    break;
                }

                curLine = start.Snapshot.GetLineFromLineNumber(curLine.LineNumber - 1);
                spanEnd = curLine.End;
            }
        }

        #endregion

        #region IDisposable Members

        public void Dispose() {
        }

        #endregion

        private List<Completion> GenerateBuiltinCompletions(bool? doubleQuote) {
            var modules = _nodejsModules.Keys.ToArray();

            List<Completion> res = new List<Completion>();
            foreach (var module in modules) {
                res.Add(
                    new Completion(
                        module,
                        CompletionInfo.GetInsertionQuote(doubleQuote, module),
                        _nodejsModules[module],
                        _glyphService.GetGlyph(StandardGlyphGroup.GlyphGroupModule, StandardGlyphItem.GlyphItemPublic),
                        null
                    )
                );
            }
            return res;
        }
    }
}
