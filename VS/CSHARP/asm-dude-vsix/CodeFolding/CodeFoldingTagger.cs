﻿// The MIT License (MIT)
//
// Copyright (c) 2017 Henk-Jan Lebbink
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:

// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.

// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Text;
using System.Windows.Controls;
using AsmDude.Tools;
using AsmDude.SyntaxHighlighting;
using System.Threading;
using System.Text;
using Microsoft.VisualStudio.Shell;
using AsmTools;
using Amib.Threading;

namespace AsmDude.CodeFolding
{

    class PartialRegion
    {
        public int StartLine { get; set; }
        public int StartOffset { get; set; }
        public int StartOffsetHoverText { get; set; }
        public int Level { get; set; }
        public PartialRegion PartialParent { get; set; }
    }

    class Region : PartialRegion
    {
        public int EndLine { get; set; }
    }

    internal sealed class CodeFoldingTagger : ITagger<IOutliningRegionTag>
    {

        #region Private Fields
        private string startRegionTag = Settings.Default.CodeFolding_BeginTag;  //the characters that start the outlining region
        private string endRegionTag = Settings.Default.CodeFolding_EndTag;      //the characters that end the outlining region

        private readonly ITextBuffer _buffer;
        private readonly ITagAggregator<AsmTokenTag> _aggregator;
        private readonly ErrorListProvider _errorListProvider;
        private ITextSnapshot _snapshot;
        private IList<Region> _regions;
        private object _updateLock = new object();
        private bool _enabled;

        private bool _busy;
        private bool _waiting;
        private bool _scheduled;


        #endregion Private Fields

        /// <summary>Constructor</summary>
        public CodeFoldingTagger(
            ITextBuffer buffer,
            ITagAggregator<AsmTokenTag> aggregator,
            ErrorListProvider errorListProvider)
        {
            //AsmDudeToolsStatic.Output_INFO("CodeFoldingTagger: constructor");
            this._buffer = buffer;
            this._aggregator = aggregator;
            this._errorListProvider = errorListProvider;

            this._snapshot = buffer.CurrentSnapshot;
            this._regions = new List<Region>();
            this._enabled = true;
            this._busy = false;
            this._waiting = false;
            this._scheduled = false;

            Parse_Delayed();
            this._buffer.ChangedLowPriority += this.Buffer_Changed;
        }

        public IEnumerable<ITagSpan<IOutliningRegionTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0)
            {
                yield break;
            }
            if (Settings.Default.CodeFolding_On && this._enabled)
            {
                //AsmDudeToolsStatic.Output_INFO("CodeFoldingTagger:GetTags:entering: IsDefaultCollapsed= " + Settings.Default.CodeFolding_IsDefaultCollapsed);

                lock (this._updateLock)
                {
                    SnapshotSpan entire = new SnapshotSpan(spans[0].Start, spans[spans.Count - 1].End).TranslateTo(this._snapshot, SpanTrackingMode.EdgeExclusive);
                    int startLineNumber = entire.Start.GetContainingLine().LineNumber;
                    int endLineNumber = entire.End.GetContainingLine().LineNumber;

                    //foreach (Region region in this._regions.ToArray()) //TODO expensive and ugly ToArray here to prevent a modification exception
                    foreach (Region region in this._regions)
                    {
                        if ((region.StartLine <= endLineNumber) && (region.EndLine >= startLineNumber))
                        {
                            ITextSnapshotLine startLine = this._snapshot.GetLineFromLineNumber(region.StartLine);
                            ITextSnapshotLine endLine = this._snapshot.GetLineFromLineNumber(region.EndLine);

                            var replacement = Get_Region_Description(startLine.GetText(), region.StartOffsetHoverText);
                            var hover = Get_Hover_Text(region.StartLine, region.EndLine, this._snapshot);

                            yield return new TagSpan<IOutliningRegionTag>(
                                new SnapshotSpan(startLine.Start + region.StartOffset, endLine.End),
                                new OutliningRegionTag(Settings.Default.CodeFolding_IsDefaultCollapsed, true, replacement, hover));
                        }
                    }
                }
            }
        }
        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public bool Is_Enabled { get { return this._enabled; } }

        #region Private Methods

        /// <summary>
        /// Get the description of the region that starts at the provided line content
        /// </summary>
        private string Get_Region_Description(string line, int startPos)
        {
            string description = "";
            //AsmDudeToolsStatic.Output_INFO("getRegionDescription: startPos=" + startPos + "; line=" + line);
            if (startPos < 0)
            {
                description = line;
            }
            else if (startPos < line.Length)
            {
                description = line.Substring(startPos).Trim();
            }
            return (description.Length > 0) ? description : "...";
        }

        /// <summary>
        /// Get the text to be displayed when hovering over a closed region
        /// </summary>
        private TextBlock Get_Hover_Text(int beginLineNumber, int endLineNumber, ITextSnapshot snapshot)
        {
            TextBlock description = new TextBlock();
            StringBuilder str = new StringBuilder();

            int numberOfLines = Math.Min((endLineNumber + 1) - beginLineNumber, 40); // do not show more than 40 lines 
            for (int i = 0; i < numberOfLines; ++i)
            {
                str.AppendLine(snapshot.GetLineFromLineNumber(beginLineNumber + i).GetText());
            }

            //TODO provide syntax highlighting for the next run
            System.Windows.Documents.Run run = new System.Windows.Documents.Run(str.ToString().TrimEnd()); // TrimEnd to get rid of the last new line
            run.FontSize -= 1;
            description.Inlines.Add(run);
            return description;
        }

        private void Buffer_Changed(object sender, TextContentChangedEventArgs e)
        {
            // If this isn't the most up-to-date version of the buffer, then ignore it for now (we'll eventually get another change event).
            if (e.After != this._buffer.CurrentSnapshot)
            {
                return;
            }
            Parse_Delayed();
        }

        /// <summary>
        /// Return start positions of the provided line content. Tuple has: 1) start of the folding position; 2) start of the description position.
        /// </summary>
        private Tuple<int, int> Is_Start_Keyword(string lineContent, int lineNumber)
        {
            var tup = Is_Start_Directive_Keyword(lineContent);
            if (tup.Item1 != -1)
            {
                return tup;
            }
            else
            {
                AssemblerEnum usedAssember = AsmDudeToolsStatic.Used_Assembler;
                if (usedAssember.HasFlag(AssemblerEnum.MASM))
                {
                    return Is_Start_Masm_Keyword(lineContent, lineNumber);
                }
                else if (usedAssember.HasFlag(AssemblerEnum.NASM))
                {
                    return Is_Start_Nasm_Keyword(lineContent, lineNumber);
                }
                else
                {
                    return new Tuple<int, int>(-1, -1);
                }
            }
        }

        /// <summary>
        /// Return start positions of the provided line content. Tuple has: 1) start of the folding position; 2) start of the description position.
        /// </summary>
        private Tuple<int, int> Is_Start_Directive_Keyword(string lineContent)
        {
            int i1 = lineContent.IndexOf(this.startRegionTag, StringComparison.OrdinalIgnoreCase);
            if (i1 == -1)
            {
                return new Tuple<int, int>(-1, -1);
            }
            else
            {
                return new Tuple<int, int>(i1, i1 + this.startRegionTag.Length);
            }
        }

        /// <summary>
        /// Return start positions of the provided line content. Tuple has: 1) start of the folding position; 2) start of the description position.
        /// </summary>
        private Tuple<int, int> Is_Start_Masm_Keyword(string lineContent, int lineNumber)
        {
            ITextSnapshotLine line = this._buffer.CurrentSnapshot.GetLineFromLineNumber(lineNumber);
            IEnumerable<IMappingTagSpan<AsmTokenTag>> tags = this._aggregator.GetTags(line.Extent);
            foreach (IMappingTagSpan<AsmTokenTag> asmTokenSpan in tags)
            {
                if (asmTokenSpan.Tag.Type == AsmTokenType.Directive)
                {
                    string tokenStr = asmTokenSpan.Span.GetSpans(this._buffer)[0].GetText().ToUpper();
                    //AsmDudeToolsStatic.Output_INFO("CodeFoldingTagger:IsStartMasmKeyword: tokenStr=" + tokenStr);
                    switch (tokenStr)
                    {
                        case "SEGMENT":
                        case "MACRO":
                        case "STRUCT":
                        case ".IF":
                        case ".WHILE":
                        case "PROC":
                            {
                                return new Tuple<int, int>(lineContent.Length, lineContent.Length);
                            }
                        case "EXTERN":
                        case "EXTRN": // no start region on a line with EXTERN keyword
                            {
                                return new Tuple<int, int>(-1, -1);
                            }
                        default: break;
                    }
                }
            }
            return new Tuple<int, int>(-1, -1);
        }

        /// <summary>
        /// Return start positions of the provided line content. Tuple has: 1) start of the folding position; 2) start of the description position.
        /// </summary>
        private Tuple<int, int> Is_Start_Nasm_Keyword(string lineContent, int lineNumber)
        {
            ITextSnapshotLine line = this._buffer.CurrentSnapshot.GetLineFromLineNumber(lineNumber);
            IEnumerable<IMappingTagSpan<AsmTokenTag>> tags = this._aggregator.GetTags(line.Extent);
            foreach (IMappingTagSpan<AsmTokenTag> asmTokenSpan in tags)
            {
                if (asmTokenSpan.Tag.Type == AsmTokenType.Directive)
                {
                    string tokenStr = asmTokenSpan.Span.GetSpans(this._buffer)[0].GetText().ToUpper();
                    //AsmDudeToolsStatic.Output_INFO("CodeFoldingTagger:IsStartMasmKeyword: tokenStr=" + tokenStr);
                    switch (tokenStr)
                    {
                        case "STRUC":
                        case "ISTRUC":
                        case "%MACRO":
                            {
                                return new Tuple<int, int>(lineContent.Length, lineContent.Length);
                            }
                        default: break;
                    }
                }
            }
            return new Tuple<int, int>(-1, -1);
        }

        private int Is_End_Keyword(string lineContent, int lineNumber)
        {
            int i1 = Is_End_Directive_Keyword(lineContent);
            if (i1 != -1)
            {
                return i1;
            }
            else
            {
                AssemblerEnum usedAssember = AsmDudeToolsStatic.Used_Assembler;
                if (usedAssember.HasFlag(AssemblerEnum.MASM))
                {
                    return Is_End_Masm_Keyword(lineContent, lineNumber);
                }
                else if (usedAssember.HasFlag(AssemblerEnum.NASM))
                {
                    return Is_End_Nasm_Keyword(lineContent, lineNumber);
                }
                else
                {
                    return -1;
                }
            }
        }

        private int Is_End_Directive_Keyword(string lineContent)
        {
            return lineContent.IndexOf(this.endRegionTag, StringComparison.OrdinalIgnoreCase);
        }

        private int Is_End_Masm_Keyword(string lineContent, int lineNumber)
        {
            IEnumerable<IMappingTagSpan<AsmTokenTag>> tags = this._aggregator.GetTags(this._buffer.CurrentSnapshot.GetLineFromLineNumber(lineNumber).Extent);
            foreach (IMappingTagSpan<AsmTokenTag> asmTokenSpan in tags)
            {
                if (asmTokenSpan.Tag.Type == AsmTokenType.Directive)
                {
                    string tokenStr = asmTokenSpan.Span.GetSpans(this._buffer)[0].GetText().ToUpper();
                    switch (tokenStr)
                    {
                        case "ENDS": // end token for SEGMENT
                        case "ENDP": // end token for PROC
                        case "ENDM": // end token for MACRO
                        //case "ENDS": // end token for STRUCT
                        case ".ENDIF": // end token for .IF
                        case ".ENDW": // end token for .WHILE
                            {
                                return lineContent.IndexOf(tokenStr, StringComparison.OrdinalIgnoreCase);
                            }
                        default: break;
                    }
                }
            }
            return -1;
        }

        private int Is_End_Nasm_Keyword(string lineContent, int lineNumber)
        {
            IEnumerable<IMappingTagSpan<AsmTokenTag>> tags = this._aggregator.GetTags(this._buffer.CurrentSnapshot.GetLineFromLineNumber(lineNumber).Extent);
            foreach (IMappingTagSpan<AsmTokenTag> asmTokenSpan in tags)
            {
                if (asmTokenSpan.Tag.Type == AsmTokenType.Directive)
                {
                    string tokenStr = asmTokenSpan.Span.GetSpans(this._buffer)[0].GetText().ToUpper();
                    switch (tokenStr)
                    {
                        case "ENDSTRUC": // end token for STRUC
                        case "IEND":    // end token for ISTRUC
                        case "%ENDMACRO": // end token for %MACRO
                            {
                                return lineContent.IndexOf(tokenStr, StringComparison.OrdinalIgnoreCase);
                            }
                        default: break;
                    }
                }
            }
            return -1;
        }

        /// <summary>
        /// execute the parse method with a delay.
        /// </summary>
        private void Parse_Delayed()
        {
            if (this._waiting)
            {
                AsmDudeToolsStatic.Output_INFO("CodeFoldingTagger:Parse_Delayed: already waiting for execution. Skipping this call.");
                return;
            }
            if (this._busy)
            {
                AsmDudeToolsStatic.Output_INFO("CodeFoldingTagger:Parse_Delayed: busy; scheduling this call.");
                this._scheduled = true;
            }
            else
            {
                AsmDudeToolsStatic.Output_INFO("CodeFoldingTagger:Parse_Delayed: going to execute this call.");
                if (true)
                {
                    AsmDudeTools.Instance.Thread_Pool.QueueWorkItem(this.Parse2);
                }
                else
                {
                    ThreadPool.QueueUserWorkItem(this.Parse);
                }
            }
        }
        private void Parse2()
        {
            Parse(null);
        }
        private void Parse(object threadContext)
        {
            if (!this._enabled) return;

            this._waiting = true;
            Thread.Sleep(AsmDudePackage.msSleepBeforeAsyncExecution);
            this._busy = true;
            this._waiting = false;

            #region Payload
            lock (this._updateLock)
            {
                DateTime time1 = DateTime.Now;

                ITextSnapshot newSnapshot = this._buffer.CurrentSnapshot;
                IList<Region> newRegions = new List<Region>();

                // keep the current (deepest) partial region, which will have
                // references to any parent partial regions.
                PartialRegion currentRegion = null;

                IEnumerator<ITextSnapshotLine> enumerator = newSnapshot.Lines.GetEnumerator();

                ITextSnapshotLine line = null;
                bool hasNext = enumerator.MoveNext();
                bool already_advanced = true;
                if (hasNext) line = enumerator.Current;

                while (hasNext)
                {
                    already_advanced = false;

                    #region Parse Line
                    if (line.Length > 0)
                    {
                        string lineContent = line.GetText();
                        int lineNumber = line.LineNumber;

                        Tuple<int, int> tup = Is_Start_Keyword(lineContent, lineNumber);
                        int regionStart = tup.Item1;
                        int regionStartHoverText = tup.Item2;

                        if (regionStart != -1)
                        {
                            Add_Start_Region(lineContent, regionStart, lineNumber, regionStartHoverText, ref currentRegion, newRegions);
                        }
                        else
                        {
                            int regionEnd = Is_End_Keyword(lineContent, lineNumber);
                            if (regionEnd != -1)
                            {
                                Add_End_Region(lineContent, regionEnd, lineNumber, ref currentRegion, newRegions);
                            }
                            else
                            {
                                #region Search for multi-line Remark
                                if (AsmSourceTools.IsRemarkOnly(lineContent))
                                {
                                    int lineNumber2 = -1;
                                    string lineContent2 = null;

                                    while (enumerator.MoveNext())
                                    {
                                        line = enumerator.Current;
                                        string lineContent3 = line.GetText();
                                        if (AsmSourceTools.IsRemarkOnly(lineContent3) &&
                                                (Is_Start_Directive_Keyword(lineContent3).Item1 == -1) &&
                                                (Is_End_Directive_Keyword(lineContent3) == -1))
                                        {
                                            lineNumber2 = line.LineNumber;
                                            lineContent2 = lineContent3;
                                            already_advanced = false;
                                        }
                                        else
                                        {
                                            already_advanced = true;
                                            break;
                                        }
                                    }
                                    if (lineNumber2 != -1)
                                    {
                                        int regionStartPos = AsmSourceTools.GetRemarkCharPosition(lineContent);
                                        Add_Start_Region(lineContent, regionStartPos, lineNumber, regionStartPos, ref currentRegion, newRegions);
                                        //this.updateChangedSpans(newSnapshot, newRegions);
                                        Add_End_Region(lineContent2, 0, lineNumber2, ref currentRegion, newRegions);
                                    }
                                }
                                #endregion
                            }
                        }
                    }
                    #endregion Parse Line

                    #region Update Changed Spans
                    Update_Changed_Spans(newSnapshot, newRegions);
                    #endregion

                    #region Advance to next line
                    if (!already_advanced)
                    {
                        hasNext = enumerator.MoveNext();
                        if (hasNext) line = enumerator.Current;
                    }
                    #endregion
                }
                AsmDudeToolsStatic.Print_Speed_Warning(time1, "CodeFoldingTagger");

                double elapsedSec = (double)(DateTime.Now.Ticks - time1.Ticks) / 10000000;
                if (elapsedSec > AsmDudePackage.slowShutdownThresholdSec)
                {
#                   if DEBUG
                    AsmDudeToolsStatic.Output_WARNING("CodeFoldingTagger: Parse: disabled CodeFolding had I been in Release mode");
#                   else
                    Disable();
#                   endif
                }
            }
            #endregion Payload

            this._busy = false;
            if (this._scheduled)
            {
                this._scheduled = false;
                Parse_Delayed();
            }
        }

        private void Add_Start_Region(
            string lineContent,
            int regionStart,
            int lineNumber,
            int regionStartHoverText,
            ref PartialRegion currentRegion,
            IList<Region> newRegions)
        {
            //AsmDudeToolsStatic.Output_INFO("CodeFoldingTagger: addStartRegion");
#pragma warning disable IDE0030 // Use null propagation
            int currentLevel = (currentRegion != null) ? currentRegion.Level : 1;
#pragma warning restore IDE0030 // Use null propagation
            int newLevel = currentLevel + 1;

            //levels are the same and we have an existing region;
            //end the current region and start the next
            if ((currentLevel == newLevel) && (currentRegion != null))
            {
                newRegions.Add(new Region()
                {
                    Level = currentRegion.Level,
                    StartLine = currentRegion.StartLine,
                    StartOffset = currentRegion.StartOffset,
                    StartOffsetHoverText = regionStartHoverText,
                    EndLine = lineNumber
                });

                currentRegion = new PartialRegion()
                {
                    Level = newLevel,
                    StartLine = lineNumber,
                    StartOffset = regionStart,
                    StartOffsetHoverText = regionStartHoverText,
                    PartialParent = currentRegion.PartialParent
                };
            }
            //this is a new (sub)region
            else
            {
                currentRegion = new PartialRegion()
                {
                    Level = newLevel,
                    StartLine = lineNumber,
                    StartOffset = regionStart,
                    StartOffsetHoverText = regionStartHoverText,
                    PartialParent = currentRegion
                };
            }
        }

        private void Add_End_Region(
            string lineContent,
            int regionEnd,
            int lineNumber,
            ref PartialRegion currentRegion,
            IList<Region> newRegions)
        {
            //AsmDudeToolsStatic.Output_INFO("CodeFoldingTagger: addEndRegion: lineContent=" + lineContent + "; regionEnd=" + regionEnd + "; lineNumber=" + lineNumber);
            if (currentRegion != null)
            {
                newRegions.Add(new Region()
                {
                    Level = currentRegion.Level,
                    StartLine = currentRegion.StartLine,
                    StartOffset = currentRegion.StartOffset,
                    StartOffsetHoverText = currentRegion.StartOffsetHoverText,
                    EndLine = lineNumber
                });
                currentRegion = currentRegion.PartialParent;
            }
        }

        private void Update_Changed_Spans(ITextSnapshot newSnapshot, IList<Region> newRegions)
        {
            //determine the changed span, and send a changed event with the new spans
            IList<Span> oldSpans =
                    new List<Span>(this._regions.Select(r => CodeFoldingTagger.As_Snapshot_Span(r, this._snapshot)
                        .TranslateTo(newSnapshot, SpanTrackingMode.EdgeExclusive)
                        .Span));
            IList<Span> newSpans = new List<Span>(newRegions.Select(r => CodeFoldingTagger.As_Snapshot_Span(r, newSnapshot).Span));

            NormalizedSpanCollection oldSpanCollection = new NormalizedSpanCollection(oldSpans);
            NormalizedSpanCollection newSpanCollection = new NormalizedSpanCollection(newSpans);

            //the changed regions are regions that appear in one set or the other, but not both.
            NormalizedSpanCollection removed = NormalizedSpanCollection.Difference(oldSpanCollection, newSpanCollection);

            int changeStart = int.MaxValue;
            int changeEnd = -1;

            if (removed.Count > 0)
            {
                changeStart = removed[0].Start;
                changeEnd = removed[removed.Count - 1].End;
            }
            if (newSpans.Count > 0)
            {
                changeStart = Math.Min(changeStart, newSpans[0].Start);
                changeEnd = Math.Max(changeEnd, newSpans[newSpans.Count - 1].End);
            }

            this._snapshot = newSnapshot;
            this._regions = newRegions;
            if (changeStart <= changeEnd)
            {
                if (TagsChanged != null)
                {
                    TagsChanged(this, new SnapshotSpanEventArgs(new SnapshotSpan(this._snapshot, Span.FromBounds(changeStart, changeEnd))));
                }
                else
                {
                    AsmDudeToolsStatic.Output_WARNING("CodeFoldingTagger:updateChangedSpans: TagsChanged is null");
                }
            }
        }

        private static SnapshotSpan As_Snapshot_Span(Region region, ITextSnapshot snapshot)
        {
            var startLine = snapshot.GetLineFromLineNumber(region.StartLine);
            var endLine = (region.StartLine == region.EndLine) ? startLine : snapshot.GetLineFromLineNumber(region.EndLine);
            return new SnapshotSpan(startLine.Start + region.StartOffset, endLine.End);
        }

        private void Disable()
        {
            string filename = AsmDudeToolsStatic.GetFileName(this._buffer);
            string msg = string.Format("Performance of CodeFoldingTagger is horrible: disabling folding for {0}.", filename);
            AsmDudeToolsStatic.Output_WARNING(msg);

            this._enabled = false;
            lock (this._updateLock)
            {
                this._buffer.ChangedLowPriority -= this.Buffer_Changed;
                this._regions.Clear();
            }
            AsmDudeToolsStatic.Disable_Message(msg, filename, this._errorListProvider);
        }

        #endregion Private Methods
    }
}