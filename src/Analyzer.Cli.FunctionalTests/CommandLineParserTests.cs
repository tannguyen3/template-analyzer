﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Linq;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Azure.Templates.Analyzer.Cli;
using Microsoft.Azure.Templates.Analyzer.Types;
using Newtonsoft.Json.Linq;

namespace Analyzer.Cli.FunctionalTests
{
    [TestClass]
    public class CommandLineParserTests
    {
        private CommandLineParser _commandLineParser;

        [TestInitialize]
        public void TestInit()
        {
            _commandLineParser = new CommandLineParser();
        }

        [DataTestMethod]
        [DataRow("Path does not exist", 2, DisplayName = "Invalid template file path provided")]
        [DataRow("Configuration.json", 4, DisplayName = "Path exists, not an ARM template.")]
        [DataRow("Configuration.json", 3, "--report-format", "Sarif", DisplayName = "Path exists, Report-format flag set, --output-file-path flag not included.")]
        [DataRow("Configuration.json", 1, "--parameters-file-path", DisplayName = "Path exists, Parameters-file-path flag included, but no value provided.")]
        [DataRow("AppServicesLogs-Failures.json", 5, DisplayName = "Violations found in the template")]
        [DataRow("AppServicesLogs-Passes.json", 0, DisplayName = "Success")]
        public void AnalyzeTemplate_ValidInputValues_ReturnExpectedExitCode(string relativeTemplatePath, int expectedExitCode, params string[] additionalCliOptions)
        {
            var args = new string[] { "analyze-template" , GetFilePath(relativeTemplatePath)}; 
            args = args.Concat(additionalCliOptions).ToArray();
            var result = _commandLineParser.InvokeCommandLineAPIAsync(args);

            Assert.AreEqual(expectedExitCode, result.Result);
        }

        [DataTestMethod]
        [DataRow("Configuration.json", 1, DisplayName = "Provided parameters file is not a parameters file")]
        [DataRow("Parameters.json", 5, DisplayName = "Provided parameters file correct, issues in template")]
        public void AnalyzeTemplate_ParameterFileParamUsed_ReturnExpectedExitCode(string relativeParametersFilePath, int expectedExitCode)
        {
            var templatePath = GetFilePath("AppServicesLogs-Failures.json");
            var parametersFilePath = GetFilePath(relativeParametersFilePath);
            var args = new string[] { "analyze-template", templatePath, "--parameters-file-path", parametersFilePath };
            var result = _commandLineParser.InvokeCommandLineAPIAsync(args);

            Assert.AreEqual(expectedExitCode, result.Result);
        }

        [TestMethod]
        public void AnalyzeTemplate_UseConfigurationFileOption_ReturnExpectedExitCodeUsingOption()
        {
            var templatePath = GetFilePath("AppServicesLogs-Failures.json");
            var configurationPath = GetFilePath("Configuration.json");
            var args = new string[] { "analyze-template", templatePath, "--config-file-path", configurationPath};
            var result = _commandLineParser.InvokeCommandLineAPIAsync(args);

            Assert.AreEqual(5, result.Result);
        }

        [TestMethod]
        public void AnalyzeTemplate_ReportFormatAsSarif_ReturnExpectedExitCodeUsingOption()
        {
            var templatePath = GetFilePath("AppServicesLogs-Failures.json");
            var outputFilePath = Path.Combine(Directory.GetCurrentDirectory(), "OutputFile.sarif");
            var args = new string[] { "analyze-template", templatePath, "--report-format", "Sarif", "--output-file-path", outputFilePath };
            var result = _commandLineParser.InvokeCommandLineAPIAsync(args);

            Assert.AreEqual(5, result.Result);
            
            File.Delete(outputFilePath);
        }

        [DataTestMethod]
        [DataRow(false, 2, DisplayName = "Invalid directory path provided")]
        [DataRow(true, 3, "--report-format", "Sarif", DisplayName = "Directory exists, Report-format flag set, --output-file-path flag not included.")]
        [DataRow(true, 1, "--report-format", "Console", "--output-file-path", DisplayName = "Path exists, Report-format flag set, --output-file-path flag included, but no value provided.")]
        [DataRow(true, 6, DisplayName = "Error + Violation: Scan has both errors and violations")]
        public void AnalyzeDirectory_ValidInputValues_ReturnExpectedExitCode(bool useTestDirectoryPath, int expectedExitCode, params string[] additionalCliOptions)
        {
            var args = new string[] { "analyze-directory", useTestDirectoryPath ? Directory.GetCurrentDirectory() : "Directory does not exist" };

            args = args.Concat(additionalCliOptions).ToArray();
            var result = _commandLineParser.InvokeCommandLineAPIAsync(args);

            Assert.AreEqual(expectedExitCode, result.Result);
        }

        [TestMethod]
        public void AnalyzeDirectory_DirectoryWithInvalidTemplates_LogsExpectedErrorInSarif()
        {
            var outputFilePath = Path.Combine(Directory.GetCurrentDirectory(), "Output.sarif");
            var directoryToAnalyze = GetFilePath("ToTestSarifNotifications");
            
            var args = new string[] { "analyze-directory", directoryToAnalyze, "--report-format", "Sarif", "--output-file-path", outputFilePath };

            var result = _commandLineParser.InvokeCommandLineAPIAsync(args);

            Assert.AreEqual(1, result.Result);

            var sarifOutput = JObject.Parse(File.ReadAllText(outputFilePath));
            var toolNotifications = sarifOutput["runs"][0]["invocations"][0]["toolExecutionNotifications"];

            var templateErrorMessage = "An exception occurred while analyzing a template";
            Assert.AreEqual(templateErrorMessage, toolNotifications[0]["message"]["text"]);
            Assert.AreEqual(templateErrorMessage, toolNotifications[1]["message"]["text"]);

            var nonJsonFilePath1 = Path.Combine(directoryToAnalyze, "AnInvalidTemplate.json");
            var nonJsonFilePath2 = Path.Combine(directoryToAnalyze, "AnotherInvalidTemplate.json");
            var thirdNotificationMessageText = toolNotifications[2]["message"]["text"].ToString();
            // Both orders have to be considered for Windows and Linux:
            Assert.IsTrue($"Unable to analyze 2 files: {nonJsonFilePath1}, {nonJsonFilePath2}" == thirdNotificationMessageText ||
                $"Unable to analyze 2 files: {nonJsonFilePath2}, {nonJsonFilePath1}" == thirdNotificationMessageText);
            
            Assert.AreEqual("error", toolNotifications[0]["level"]);
            Assert.AreEqual("error", toolNotifications[1]["level"]);
            Assert.AreEqual("error", toolNotifications[2]["level"]);

            Assert.AreNotEqual(null, toolNotifications[0]["exception"]);
            Assert.AreNotEqual(null, toolNotifications[1]["exception"]);
            Assert.AreEqual(null, toolNotifications[2]["exception"]);
        }

        [DataTestMethod]
        [DataRow(false, DisplayName = "Outputs a recommendation for the verbose mode")]
        [DataRow(true, DisplayName = "Does not recommend the verbose mode")]
        [DataRow(false, true, DisplayName = "Outputs a recommendation for the verbose mode and uses plural form for 'errors'")]
        public void AnalyzeDirectory_ExecutionWithErrorAndWarning_PrintsExpectedLogSummary(bool usesVerboseMode, bool multipleErrors = false)
        {
            var directoryToAnalyze = GetFilePath("ToTestSummaryLogger");

            var expectedLogSummary = $"{(multipleErrors ? "2 errors" : "1 error")} and 1 warning were found during the execution, please refer to the original messages above";

            if (!usesVerboseMode)
            {
                expectedLogSummary += $"{Environment.NewLine}The verbose mode (option -v or --verbose) can be used to obtain even more information about the execution";
            }
            
            expectedLogSummary += ($"{Environment.NewLine}Summary of the errors:" +
                $"{Environment.NewLine}\t{(multipleErrors ? "2 instances" : "1 instance")} of: An exception occurred while analyzing a template" +
                $"{Environment.NewLine}Summary of the warnings:" +
                $"{Environment.NewLine}\t1 instance of: An exception occurred when processing the template language expressions{Environment.NewLine}");

            var args = new string[] { "analyze-directory", directoryToAnalyze };

            if (usesVerboseMode)
            {
                args = args.Append("--verbose").ToArray();
            }

            using StringWriter outputWriter = new();
            Console.SetOut(outputWriter);

            // Copy template producing an error to get multiple errors in run
            string secondErrorTemplate = Path.Combine(directoryToAnalyze, "ReportsError2.json");
            if (multipleErrors)
            {
                File.Copy(Path.Combine(directoryToAnalyze, "ReportsError.json"), secondErrorTemplate);
            }

            try
            {
                var result = _commandLineParser.InvokeCommandLineAPIAsync(args);

                var cliConsoleOutput = outputWriter.ToString();
                var expectedLogMessageStart = multipleErrors ? "2 errors and 1 warning" : "1 error and 1 warning";
                var indexOfLogSummary = cliConsoleOutput.IndexOf(expectedLogMessageStart);
                Assert.IsTrue(indexOfLogSummary >= 0, $"Expected log message \"{expectedLogMessageStart}\" not found in CLI output.  Found:{Environment.NewLine}{cliConsoleOutput}");
                var logSummary = cliConsoleOutput[indexOfLogSummary..];

                Assert.AreEqual(expectedLogSummary, logSummary);
            }
            finally
            {
                File.Delete(secondErrorTemplate);
            }
        }

        [TestMethod]
        public void FilterRules_ValidConfig_RulesFiltered()
        {
            var templatePath = GetFilePath("AppServicesLogs-Failures.json");
            var args = new string[] { "analyze-template", templatePath };

            // Analyze template without filtering rules to verify there is a failure.
            var result = _commandLineParser.InvokeCommandLineAPIAsync(args);
            Assert.AreEqual(5, result.Result);

            // Run again with rule filtered out, verify it passes.
            var tempConfig = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tempConfig,
                    JObject.FromObject(
                        new ConfigurationDefinition
                        {
                            InclusionsConfigurationDefinition = new()
                            {
                                // Use a non-existent rule id so all rules are filtered out.
                                Ids = new() { "NonRuleId" }
                            }
                        })
                    .ToString());
                result = _commandLineParser.InvokeCommandLineAPIAsync(args.Concat(new[] { "--config-file-path", tempConfig }).ToArray());
                Assert.AreEqual(0, result.Result);
            }
            finally
            {
                File.Delete(tempConfig);
            }
        }

        [TestMethod]
        public void FilterRules_ConfigurationPathIsInvalid_ReturnsGenericError()
        {
            var templatePath = GetFilePath("AppServicesLogs-Passes.json");
            var args = new string[] { "analyze-template", templatePath, "--config-file-path", "NonExistentFile.json" };

            var result = _commandLineParser.InvokeCommandLineAPIAsync(args);
            Assert.AreEqual(1, result.Result);
        }

        [TestMethod]
        public void FilterRules_EmptyConfigurationFile_ReturnsGenericError()
        {
            var templatePath = GetFilePath("AppServicesLogs-Passes.json");
            var tempConfig = Path.GetTempFileName();
            var args = new string[] { "analyze-template", templatePath, "--config-file-path", tempConfig };

            try
            {
                var result = _commandLineParser.InvokeCommandLineAPIAsync(args);
                Assert.AreEqual(1, result.Result);
            }
            finally
            {
                File.Delete(tempConfig);
            }
        }

        [TestMethod]
        public void FilterRules_MalformedConfigurationFile_ReturnsGenericError()
        {
            var templatePath = GetFilePath("AppServicesLogs-Passes.json");
            var tempConfig = Path.GetTempFileName();
            File.WriteAllText(tempConfig, "Invalid JSON");
            var args = new string[] { "analyze-template", templatePath, "--config-file-path", tempConfig };

            try
            {
                var result = _commandLineParser.InvokeCommandLineAPIAsync(args);
                Assert.AreEqual(1, result.Result);
            }
            finally
            {
                File.Delete(tempConfig);
            }
        }

        private static string GetFilePath(string testFileName)
        {
            return Path.Combine(Directory.GetCurrentDirectory(), "Tests", testFileName);
        }
    }
}