﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Azure.Templates.Analyzer.RuleEngines.JsonEngine;
using Microsoft.Azure.Templates.Analyzer.RuleEngines.PowerShellEngine;
using Microsoft.Azure.Templates.Analyzer.TemplateProcessor;
using Microsoft.Azure.Templates.Analyzer.Types;
using Microsoft.Azure.Templates.Analyzer.Utilities;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Microsoft.Azure.Templates.Analyzer.Core
{
    /// <summary>
    /// This class runs the TemplateAnalyzer logic given the template and parameters passed to it.
    /// </summary>
    public class TemplateAnalyzer
    {
        private JsonRuleEngine jsonRuleEngine { get; }

        /// <summary>
        /// Private constructor to enforce use of <see cref="TemplateAnalyzer.Create"/> for creating new instances.
        /// </summary>
        /// <param name="jsonRuleEngine">The <see cref="JsonRuleEngine"/> to use in analyzing templates.</param>
        private TemplateAnalyzer(JsonRuleEngine jsonRuleEngine)
        {
            this.jsonRuleEngine = jsonRuleEngine;
        }

        /// <summary>
        /// Creates a new <see cref="TemplateAnalyzer"/> instance with the default built-in rules.
        /// </summary>
        public static TemplateAnalyzer Create()
        {
            string rules;
            try
            {
                rules = LoadRules();
            }
            catch (Exception e)
            {
                throw new TemplateAnalyzerException("Failed to read rules.", e);
            }

            return new TemplateAnalyzer(
                JsonRuleEngine.Create(rules, templateContext => new JsonLineNumberResolver(templateContext)));
        }

        /// <summary>
        /// Runs the TemplateAnalyzer logic given the template and parameters passed to it.
        /// </summary>
        /// <param name="template">The ARM Template JSON</param>
        /// <param name="parameters">The parameters for the ARM Template JSON</param>
        /// <param name="templateFilePath">The ARM Template file path. (Needed to run arm-ttk checks.)</param>
        /// <param name="usePowerShell">Whether or not to use PowerShell rules to analyze the template.</param>
        /// <param name="logger">A logger to report errors and debug information</param>
        /// <returns>An enumerable of TemplateAnalyzer evaluations.</returns>
        public IEnumerable<IEvaluation> AnalyzeTemplate(string template, string parameters = null, string templateFilePath = null, bool usePowerShell = true, ILogger logger = null)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));

            JToken templatejObject;
            var armTemplateProcessor = new ArmTemplateProcessor(template, logger: logger);

            try
            {
                templatejObject = armTemplateProcessor.ProcessTemplate(parameters);
            }
            catch (Exception e)
            {
                throw new TemplateAnalyzerException("Error while processing template.", e);
            }

            var templateContext = new TemplateContext
            {
                OriginalTemplate = JObject.Parse(template),
                ExpandedTemplate = templatejObject,
                IsMainTemplate = true,
                ResourceMappings = armTemplateProcessor.ResourceMappings,
                TemplateIdentifier = templateFilePath
            };

            try
            {
                IEnumerable<IEvaluation> evaluations = jsonRuleEngine.AnalyzeTemplate(templateContext, logger);

                if (usePowerShell && templateContext.TemplateIdentifier != null)
                {
                    logger?.LogDebug("Running PowerShell rule engine");

                    var powerShellRuleEngine = new PowerShellRuleEngine();
                    evaluations = evaluations.Concat(powerShellRuleEngine.AnalyzeTemplate(templateContext, logger));
                }

                return evaluations;
            }
            catch (Exception e)
            {
                throw new TemplateAnalyzerException("Error while evaluating rules.", e);
            }
        }

        private static string LoadRules()
        {
            return File.ReadAllText(
                Path.Combine(
                    Path.GetDirectoryName(AppContext.BaseDirectory),
                    "Rules/BuiltInRules.json"));
        }

        /// <summary>
        /// Loads a configurations file. If no file was passed, checks the default directory for this file.
        /// </summary>
        /// <param name="configurationsFilePath">The path to a configuration file to read.</param>
        /// <returns>Configuration file path contents if a file exists.</returns>
        private string GetConfigurationFileContents(FileInfo configurationsFilePath)
        {
            try
            {
                string path;
                if (configurationsFilePath != null)
                {
                    path = configurationsFilePath.FullName;
                }
                else
                {
                    path = Path.Combine(Path.GetDirectoryName(AppContext.BaseDirectory),
                        "Configurations", "Configuration.json");
                    if (!File.Exists(path))
                    {
                        return null;
                    }
                }

                Console.WriteLine(Environment.NewLine + Environment.NewLine + $"Configuration File: {path}");
                return File.ReadAllText(path);
            }
            catch (Exception e)
            {
                throw new TemplateAnalyzerException("Failed to read configuration file.", e);
            }
        }

        /// <summary>
        /// Modifies the rules to run based on values defined in the configurations file.
        /// </summary>
        /// <param name="configurationsFilePath">The configuration specifying rule modifications.</param>
        public void FilterRules(FileInfo configurationsFilePath)
        {
            var configuration = GetConfigurationFileContents(configurationsFilePath);
            if (configuration != null)
                jsonRuleEngine.FilterRules(configuration);
        }
    }
}
