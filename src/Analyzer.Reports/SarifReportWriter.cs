﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Azure.Templates.Analyzer.Types;
using Microsoft.CodeAnalysis.Sarif;
using Microsoft.CodeAnalysis.Sarif.Writers;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;

namespace Microsoft.Azure.Templates.Analyzer.Reports
{
    /// <summary>
    /// Class to export analysis result to SARIF report
    /// </summary>
    public class SarifReportWriter : IReportWriter
    {
        // may define the const values in common place
        internal const string ToolName = "ARM BPA";
        internal const string ToolFullName = "ARM Template Best Practice Analyzer";
        internal const string ToolVersion = "0.0.2-alpha";
        internal const string Organization = "Microsoft";
        internal const string InformationUri = "https://github.com/Azure/template-analyzer";
        internal const string UriBaseIdString = "ROOTPATH";

        private IFileInfo reportFile;
        private Run sarifRun;
        private IList<Result> sarifResults;
        private IDictionary<string, ReportingDescriptor> rulesDictionary;
        private string rootPath;

        /// <summary>
        /// constructor of SarifReportWriter class
        /// </summary>
        /// <param name="reportFile">report file</param>
        /// <param name="targetPath">the directory analyzer targets</param>
        public SarifReportWriter(IFileInfo reportFile, string targetPath = null)
        {
            this.reportFile = reportFile ?? throw new ArgumentException(nameof(reportFile));
            this.InitRun();
            this.rulesDictionary = new ConcurrentDictionary<string, ReportingDescriptor>();
            this.sarifResults = new List<Result>();
            this.rootPath = targetPath;
        }

        /// <inheritdoc/>
        public void WriteResults(IFileInfo templateFile, IEnumerable<IEvaluation> evaluations)
        {
            this.rootPath ??= templateFile.DirectoryName;
            foreach (var evaluation in evaluations.Where(eva => !eva.Passed))
            {
                // get rule definition from first level evaluation
                ReportingDescriptor rule = this.ExtractRule(evaluation);
                this.ExtractResult(evaluation, evaluation, templateFile.FullName); ;
            }
        }

        internal Run SarifRun => this.sarifRun;

        private void InitRun()
        {
            this.sarifRun = new Run
            {
                Tool = new Tool
                {
                    Driver = new ToolComponent
                    {
                        Name = ToolName,
                        FullName = ToolFullName,
                        Version = ToolVersion,
                        InformationUri = new Uri(InformationUri),
                        Organization = Organization,
                    }
                }
            };
        }

        private ReportingDescriptor ExtractRule(IEvaluation evaluation)
        {
            if (!rulesDictionary.TryGetValue(evaluation.RuleId, out _))
            {
                var hasUri = Uri.TryCreate(evaluation.HelpUri, UriKind.RelativeOrAbsolute, out Uri uri);
                rulesDictionary.Add(
                    evaluation.RuleId,
                    new ReportingDescriptor
                    {
                        Id = evaluation.RuleId,
                        // Name = evaluation.RuleId, TBD
                        FullDescription = new MultiformatMessageString { Text = evaluation.RuleDescription },
                        Help = new MultiformatMessageString { Text = evaluation.Recommendation },
                        HelpUri = hasUri ? uri : null,
                        DefaultConfiguration = new ReportingConfiguration { Level = GetLevelFromEvaluation(evaluation) }
                    });
            }
            return rulesDictionary[evaluation.RuleId];
        }

        private void ExtractResult(IEvaluation rootEvaluation, IEvaluation evaluation, string filePath)
        {
            foreach (var result in evaluation.Results.Where(r => !r.Passed))
            {
                this.sarifResults.Add(new Result
                {
                    RuleId = rootEvaluation.RuleId,
                    Level = GetLevelFromEvaluation(rootEvaluation),
                    Message = new Message { Text = rootEvaluation.RuleDescription },
                    Locations = new[]
                    {
                        new Location
                        {
                            PhysicalLocation = new PhysicalLocation
                            {
                                ArtifactLocation = new ArtifactLocation
                                {
                                    Uri = new Uri(
                                    UriHelper.MakeValidUri(
                                        filePath.Replace(this.rootPath, string.Empty, StringComparison.OrdinalIgnoreCase)),
                                    UriKind.Relative),
                                    UriBaseId = UriBaseIdString,
                                },
                                Region = new Region { StartLine = result.LineNumber },
                            },
                        },
                    }
                });
            }

            foreach (var eval in evaluation.Evaluations.Where(e => !e.Passed))
            {
                this.ExtractResult(rootEvaluation, eval, filePath);
            }
        }

        private FailureLevel GetLevelFromEvaluation(IEvaluation evaluation)
        {
            //rule severity in designing https://github.com/Azure/template-analyzer/issues/177
            return evaluation.Passed ? FailureLevel.Note : FailureLevel.Error;
        }

        private void PersistReport()
        {
            using Stream outputTextStream = this.reportFile.Create();
            using var outputTextWriter = new StreamWriter(outputTextStream);
            using var outputJsonWriter = new JsonTextWriter(outputTextWriter) { Formatting = Formatting.Indented };
            using var output = new ResultLogJsonWriter(outputJsonWriter);

            output.Initialize(this.sarifRun);

            this.sarifRun.Tool.Driver.Rules = this.rulesDictionary.Select(r => r.Value).ToList();
            this.sarifRun.Results = this.sarifResults;
            this.sarifRun.OriginalUriBaseIds = new Dictionary<string, ArtifactLocation>
            {
                [UriBaseIdString] = new ArtifactLocation { Uri = new Uri(UriHelper.MakeValidUri(this.rootPath), UriKind.RelativeOrAbsolute) },
            };

            if (sarifRun.Results != null)
            {
                output.OpenResults();
                output.WriteResults(sarifRun.Results);
                output.CloseResults();
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Disposes resources owned by this instance.
        /// </summary>
        /// <param name="disposing"></param>
        public void Dispose(bool disposing)
        {
            this.PersistReport();
        }
    }
}
