﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Azure.Templates.Analyzer.Types;

using Powershell = System.Management.Automation.PowerShell; // There's a conflict between this class name and a namespace

namespace Microsoft.Azure.Templates.Analyzer.RuleEngines.PowerShellEngine
{
    /// <summary>
    /// Executes template analysis encoded in PowerShell
    /// </summary>
    public class PowerShellRuleEngine
    {
        /// <summary>
        /// Execution environment for PowerShell
        /// </summary>
        private readonly Powershell powerShell;

        /// <summary>
        /// Regex that matches a string like: " on line: aNumber"
        /// </summary>
        private readonly Regex lineNumberRegex = new(@"\son\sline:\s\d+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Creates a new instance of a PowerShellRuleEngine
        /// </summary>
        public PowerShellRuleEngine()
        {
            this.powerShell = Powershell.Create();

            powerShell.Commands.AddCommand("Import-Module")
                .AddParameter("Name", Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + @"\TTK\arm-ttk.psd1"); // arm-ttk is added to the needed project's bins directories in build time
            powerShell.AddStatement();

            powerShell.Invoke();
        }

        /// <summary>
        /// Evaluates template against the rules encoded in PowerShell, and outputs the results to the console
        /// </summary>
        /// <param name="templateFilePath">The file path of the template under analysis.</param>
        public IEnumerable<IEvaluation> EvaluateRules(string templateFilePath)
        {
            this.powerShell.Commands.AddCommand("Test-AzTemplate")
                .AddParameter("Test", "deploymentTemplate")
                .AddParameter("TemplatePath", templateFilePath);

            var executionResults = this.powerShell.Invoke();

            var evaluations = new List<PowerShellRuleEvaluation>();

            foreach (dynamic executionResult in executionResults)
            {
                var uniqueErrors = new Dictionary<string, SortedSet<int>>(); // Maps error messages to a sorted set of line numbers
                var ruleIDs = new Dictionary<string, string>(); // Maps error messages to rule IDs

                foreach (dynamic error in executionResult.Errors)
                {
                    AddErrorToDictionaries(error, ref uniqueErrors, ref ruleIDs);
                }

                foreach (KeyValuePair<string, SortedSet<int>> uniqueError in uniqueErrors)
                {
                    var evaluationResults = new List<PowerShellRuleResult>();
                    foreach (int lineNumber in uniqueError.Value)
                    {
                        evaluationResults.Add(new PowerShellRuleResult(false, lineNumber));
                    }
                    var ruleDescription = executionResult.Name + ". " + uniqueError.Key;
                    evaluations.Add(new PowerShellRuleEvaluation(ruleIDs[uniqueError.Key], ruleDescription, false, evaluationResults));
                }
            }

            return evaluations;
        }

        private void AddErrorToDictionaries(dynamic error, ref Dictionary<string, SortedSet<int>> uniqueErrors, ref Dictionary<string, string> ruleIDs)
        {
            var lineNumber = 0;
            var ruleID = "";

            Type errorType = error.GetType();
            IEnumerable<PropertyInfo> errorProperties = errorType.GetRuntimeProperties();
            if (errorProperties.Where(prop => prop.Name == "TargetObject").Any())
            {
                if (error.TargetObject is PSObject targetObject)
                {
                    if (targetObject.Properties["lineNumber"] != null)
                    {
                        lineNumber = error.TargetObject.lineNumber;
                    }

                    if (targetObject.Properties["ruleID"] != null)
                    {
                        ruleID = "TA-" + error.TargetObject.ruleID;
                    }
                }
            }

            var errorMessage = lineNumberRegex.Replace(error.ToString(), string.Empty); 

            if (!uniqueErrors.TryAdd(errorMessage, new SortedSet<int> { lineNumber }))
            {
                // errorMessage was already added to the dictionary
                uniqueErrors[errorMessage].Add(lineNumber);
            }

            ruleIDs[errorMessage] = ruleID;
        }
    }
}