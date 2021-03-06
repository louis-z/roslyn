﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Formatting;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editor.Implementation.AutomaticCompletion;
using Microsoft.CodeAnalysis.Editor.Implementation.AutomaticCompletion.Sessions;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Options;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Formatting.Rules;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.BraceCompletion;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Editor.CSharp.AutomaticCompletion.Sessions
{
    internal class CurlyBraceCompletionSession : AbstractTokenBraceCompletionSession
    {
        private readonly ISmartIndentationService _smartIndentationService;
        private readonly ITextBufferUndoManagerProvider _undoManager;

        public CurlyBraceCompletionSession(ISyntaxFactsService syntaxFactsService, ISmartIndentationService smartIndentationService, ITextBufferUndoManagerProvider undoManager)
            : base(syntaxFactsService, (int)SyntaxKind.OpenBraceToken, (int)SyntaxKind.CloseBraceToken)
        {
            _smartIndentationService = smartIndentationService;
            _undoManager = undoManager;
        }

        public override void AfterStart(IBraceCompletionSession session, CancellationToken cancellationToken)
        {
            FormatTrackingSpan(session, shouldHonorAutoFormattingOnCloseBraceOption: true);

            session.TextView.TryMoveCaretToAndEnsureVisible(session.ClosingPoint.GetPoint(session.SubjectBuffer.CurrentSnapshot).Subtract(1));
        }

        private ITextUndoHistory GetUndoHistory(ITextView textView)
            => _undoManager.GetTextBufferUndoManager(textView.TextBuffer).TextBufferUndoHistory;

        public override void AfterReturn(IBraceCompletionSession session, CancellationToken cancellationToken)
        {
            // check whether shape of the braces are what we support
            // shape must be either "{|}" or "{ }". | is where caret is. otherwise, we don't do any special behavior
            if (!ContainsOnlyWhitespace(session))
            {
                return;
            }

            // alright, it is in right shape.
            var undoHistory = GetUndoHistory(session.TextView);
            using var transaction = undoHistory.CreateTransaction(EditorFeaturesResources.Brace_Completion);

            var document = session.SubjectBuffer.CurrentSnapshot.GetOpenDocumentInCurrentContextWithChanges();
            if (document != null)
            {
                document.InsertText(session.ClosingPoint.GetPosition(session.SubjectBuffer.CurrentSnapshot) - 1, Environment.NewLine, cancellationToken);
                FormatTrackingSpan(session, shouldHonorAutoFormattingOnCloseBraceOption: false, rules: GetFormattingRules(document));

                // put caret at right indentation
                PutCaretOnLine(session, session.OpeningPoint.GetPoint(session.SubjectBuffer.CurrentSnapshot).GetContainingLineNumber() + 1);

                transaction.Complete();
            }
        }

        private static bool ContainsOnlyWhitespace(IBraceCompletionSession session)
        {
            var span = session.GetSessionSpan();

            var snapshot = span.Snapshot;

            var start = span.Start.Position;
            start = snapshot[start] == session.OpeningBrace ? start + 1 : start;

            var end = span.End.Position - 1;
            end = snapshot[end] == session.ClosingBrace ? end - 1 : end;

            if (!start.PositionInSnapshot(snapshot) ||
                !end.PositionInSnapshot(snapshot))
            {
                return false;
            }

            for (var i = start; i <= end; i++)
            {
                if (!char.IsWhiteSpace(snapshot[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static IEnumerable<AbstractFormattingRule> GetFormattingRules(Document document)
        {
            var indentStyle = document.GetOptionsAsync(CancellationToken.None).WaitAndGetResult_CanCallOnBackground(CancellationToken.None).GetOption(FormattingOptions.SmartIndent);
            return SpecializedCollections.SingletonEnumerable<AbstractFormattingRule>(BraceCompletionFormattingRule.ForIndentStyle(indentStyle)).Concat(Formatter.GetDefaultFormattingRules(document));
        }

        private static void FormatTrackingSpan(IBraceCompletionSession session, bool shouldHonorAutoFormattingOnCloseBraceOption, IEnumerable<AbstractFormattingRule> rules = null)
        {
            if (!session.SubjectBuffer.GetFeatureOnOffOption(FeatureOnOffOptions.AutoFormattingOnCloseBrace) && shouldHonorAutoFormattingOnCloseBraceOption)
            {
                return;
            }

            var snapshot = session.SubjectBuffer.CurrentSnapshot;
            var startPoint = session.OpeningPoint.GetPoint(snapshot);
            var endPoint = session.ClosingPoint.GetPoint(snapshot);
            var startPosition = startPoint.Position;
            var endPosition = endPoint.Position;

            // Do not format within the braces if they're on the same line for array/collection/object initializer expressions.
            // This is a heuristic to prevent brace completion from breaking user expectation/muscle memory in common scenarios.
            // see bug Devdiv:823958
            if (startPoint.GetContainingLineNumber() == endPoint.GetContainingLineNumber())
            {
                // Brace completion is not cancellable
                var startToken = snapshot.FindToken(startPosition, CancellationToken.None);
                if (startToken.IsKind(SyntaxKind.OpenBraceToken) &&
                    (startToken.Parent.IsInitializerForArrayOrCollectionCreationExpression() ||
                     startToken.Parent is AnonymousObjectCreationExpressionSyntax))
                {
                    // format everything but the brace pair.
                    var endToken = snapshot.FindToken(endPosition, CancellationToken.None);
                    if (endToken.IsKind(SyntaxKind.CloseBraceToken))
                    {
                        endPosition -= (endToken.Span.Length + startToken.Span.Length);
                    }
                }
            }

            var document = snapshot.GetOpenDocumentInCurrentContextWithChanges();
            var style = document != null ? document.GetOptionsAsync(CancellationToken.None).WaitAndGetResult(CancellationToken.None).GetOption(FormattingOptions.SmartIndent)
                                         : FormattingOptions.SmartIndent.DefaultValue;

            if (style == FormattingOptions.IndentStyle.Smart)
            {
                // skip whitespace
                while (startPosition >= 0 && char.IsWhiteSpace(snapshot[startPosition]))
                {
                    startPosition--;
                }

                // skip token
                startPosition--;
                while (startPosition >= 0 && !char.IsWhiteSpace(snapshot[startPosition]))
                {
                    startPosition--;
                }
            }

            session.SubjectBuffer.Format(TextSpan.FromBounds(Math.Max(startPosition, 0), endPosition), rules);
        }

        private void PutCaretOnLine(IBraceCompletionSession session, int lineNumber)
        {
            var lineOnSubjectBuffer = session.SubjectBuffer.CurrentSnapshot.GetLineFromLineNumber(lineNumber);

            var indentation = GetDesiredIndentation(session, lineOnSubjectBuffer);

            session.TextView.TryMoveCaretToAndEnsureVisible(new VirtualSnapshotPoint(lineOnSubjectBuffer, indentation));
        }

        private int GetDesiredIndentation(IBraceCompletionSession session, ITextSnapshotLine lineOnSubjectBuffer)
        {
            // first try VS's smart indentation service
            var indentation = session.TextView.GetDesiredIndentation(_smartIndentationService, lineOnSubjectBuffer);
            if (indentation.HasValue)
            {
                return indentation.Value;
            }

            // do poor man's indentation
            var openingPoint = session.OpeningPoint.GetPoint(lineOnSubjectBuffer.Snapshot);
            var openingSpanLine = openingPoint.GetContainingLine();

            return openingPoint - openingSpanLine.Start;
        }

        private sealed class BraceCompletionFormattingRule : BaseFormattingRule
        {
            private static readonly Predicate<SuppressOperation> s_predicate = o => o == null || o.Option.IsOn(SuppressOption.NoWrapping);

            private static readonly ImmutableArray<BraceCompletionFormattingRule> s_instances = ImmutableArray.Create(
                new BraceCompletionFormattingRule(FormattingOptions.IndentStyle.None),
                new BraceCompletionFormattingRule(FormattingOptions.IndentStyle.Block),
                new BraceCompletionFormattingRule(FormattingOptions.IndentStyle.Smart));

            private readonly FormattingOptions.IndentStyle _indentStyle;
            private readonly CachedOptions _options;

            public BraceCompletionFormattingRule(FormattingOptions.IndentStyle indentStyle)
                : this(indentStyle, new CachedOptions(null))
            {
            }

            private BraceCompletionFormattingRule(FormattingOptions.IndentStyle indentStyle, CachedOptions options)
            {
                _indentStyle = indentStyle;
                _options = options;
            }

            public static AbstractFormattingRule ForIndentStyle(FormattingOptions.IndentStyle indentStyle)
            {
                Debug.Assert(s_instances[(int)indentStyle]._indentStyle == indentStyle);
                return s_instances[(int)indentStyle];
            }

            public override AbstractFormattingRule WithOptions(AnalyzerConfigOptions options)
            {
                var cachedOptions = new CachedOptions(options);

                if (cachedOptions == _options)
                {
                    return this;
                }

                return new BraceCompletionFormattingRule(_indentStyle, cachedOptions);
            }

            public override AdjustNewLinesOperation GetAdjustNewLinesOperation(in SyntaxToken previousToken, in SyntaxToken currentToken, in NextGetAdjustNewLinesOperation nextOperation)
            {
                // Eg Cases -
                // new MyObject {
                // new List<int> {
                // int[] arr = {
                //           = new[] {
                //           = new int[] {
                if (currentToken.IsKind(SyntaxKind.OpenBraceToken) && currentToken.Parent != null &&
                (currentToken.Parent.Kind() == SyntaxKind.ObjectInitializerExpression ||
                currentToken.Parent.Kind() == SyntaxKind.CollectionInitializerExpression ||
                currentToken.Parent.Kind() == SyntaxKind.ArrayInitializerExpression ||
                currentToken.Parent.Kind() == SyntaxKind.ImplicitArrayCreationExpression))
                {
                    if (_options.NewLinesForBracesInObjectCollectionArrayInitializers)
                    {
                        return CreateAdjustNewLinesOperation(1, AdjustNewLinesOption.PreserveLines);
                    }
                    else
                    {
                        return null;
                    }
                }

                return base.GetAdjustNewLinesOperation(in previousToken, in currentToken, in nextOperation);
            }

            public override void AddAlignTokensOperations(List<AlignTokensOperation> list, SyntaxNode node, in NextAlignTokensOperationAction nextOperation)
            {
                base.AddAlignTokensOperations(list, node, in nextOperation);
                if (_indentStyle == FormattingOptions.IndentStyle.Block)
                {
                    var bracePair = node.GetBracePair();
                    if (bracePair.IsValidBracePair())
                    {
                        AddAlignIndentationOfTokensToBaseTokenOperation(list, node, bracePair.Item1, SpecializedCollections.SingletonEnumerable(bracePair.Item2), AlignTokensOption.AlignIndentationOfTokensToFirstTokenOfBaseTokenLine);
                    }
                }
            }

            public override void AddSuppressOperations(List<SuppressOperation> list, SyntaxNode node, in NextSuppressOperationAction nextOperation)
            {
                base.AddSuppressOperations(list, node, in nextOperation);

                // remove suppression rules for array and collection initializer
                if (node.IsInitializerForArrayOrCollectionCreationExpression())
                {
                    // remove any suppression operation
                    list.RemoveAll(s_predicate);
                }
            }

            private readonly struct CachedOptions : IEquatable<CachedOptions>
            {
                public readonly bool NewLinesForBracesInObjectCollectionArrayInitializers;

                public CachedOptions(AnalyzerConfigOptions options)
                {
                    NewLinesForBracesInObjectCollectionArrayInitializers = GetOptionOrDefault(options, CSharpFormattingOptions2.NewLinesForBracesInObjectCollectionArrayInitializers);
                }

                public static bool operator ==(CachedOptions left, CachedOptions right)
                    => left.Equals(right);

                public static bool operator !=(CachedOptions left, CachedOptions right)
                    => !(left == right);

                private static T GetOptionOrDefault<T>(AnalyzerConfigOptions options, Option2<T> option)
                {
                    if (options is null)
                        return option.DefaultValue;

                    return options.GetOption(option);
                }

                public override bool Equals(object obj)
                    => obj is CachedOptions options && Equals(options);

                public bool Equals(CachedOptions other)
                {
                    return NewLinesForBracesInObjectCollectionArrayInitializers == other.NewLinesForBracesInObjectCollectionArrayInitializers;
                }

                public override int GetHashCode()
                {
                    var hashCode = 0;
                    hashCode = (hashCode << 1) + (NewLinesForBracesInObjectCollectionArrayInitializers ? 1 : 0);
                    return hashCode;
                }
            }
        }
    }
}
