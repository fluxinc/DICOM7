using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using FellowOakDicom;
using Serilog;

namespace DICOM7.DICOM2ORU
{
    /// <summary>
    /// Processes SR-specific template syntax for generating HL7 ORU messages
    /// </summary>
    public class SRTemplateProcessor(DicomDataset dataset)
    {
      private readonly Dictionary<string, int> _sequenceNumbers = new();
        private readonly Dictionary<string, DicomDataset> _loopVariables = new();
        private readonly Stack<bool> _conditionalStack = new();

        private static readonly Regex SrPathRegex = new(@"#\{SR\((.*?)\.(.*?)\)\}", RegexOptions.Compiled);
        private static readonly Regex ForEachRegex = new(@"#\{ForEach\((.*?) as (.*?)\)\}", RegexOptions.Compiled);
        private static readonly Regex EndForEachRegex = new(@"#\{EndForEach\}", RegexOptions.Compiled);
        private static readonly Regex LoopVarPathRegex = new(@"#\{(.*?)/(.*?)\.(.*?)\}", RegexOptions.Compiled);
        private static readonly Regex IfRegex = new(@"#\{If\((.*?)\)\}", RegexOptions.Compiled);
        private static readonly Regex ElseRegex = new(@"#\{Else\}", RegexOptions.Compiled);
        private static readonly Regex EndIfRegex = new(@"#\{EndIf\}", RegexOptions.Compiled);
        private static readonly Regex SequenceNumberRegex = new(@"#\{HL7SequenceNumber\((.*?)\)\}", RegexOptions.Compiled);
        private static readonly Regex CommentRegex = new(@"//.*?$", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex CodeSelectorRegex = new(@"Code\((.*?):(.*?)\)", RegexOptions.Compiled);
        private static readonly Regex VariableRegex = new(@"#\{([^{}]+)\}", RegexOptions.Compiled);

        /// <summary>
        /// Process all SR-related placeholders in the template
        /// </summary>
        public string ProcessTemplate(string template)
        {
            if (dataset == null || string.IsNullOrEmpty(template))
            {
                Log.Debug("SRTemplateProcessor: Empty dataset or template, skipping processing");
                return template;
            }

            Log.Debug("SRTemplateProcessor: Starting template processing");

            // First clean the template of comments and empty lines
            template = CleanTemplate(template);
            Log.Debug("SRTemplateProcessor: Template cleaned of comments and empty lines");

            // Process the template in blocks, handling nested ForEach and If blocks
            string processedTemplate = ProcessTemplateBlocks(template);
            Log.Debug("SRTemplateProcessor: Template blocks processed");

            // Process any remaining simple SR paths
            processedTemplate = ProcessSrPaths(processedTemplate);
            Log.Debug("SRTemplateProcessor: SR paths processed");

            // Process any sequence numbers
            processedTemplate = ProcessSequenceNumbers(processedTemplate);
            Log.Debug("SRTemplateProcessor: Sequence numbers processed");

            Log.Debug("SRTemplateProcessor: Template processing completed");
            return processedTemplate;
        }

        /// <summary>
        /// Clean the template by removing comments and extra empty lines
        /// </summary>
        private string CleanTemplate(string template)
        {
            // Normalize line endings first
            string cleaned = Regex.Replace(template, @"\r\n|\n\r|\n|\r", "\r\n");

            // Process line by line to handle indentation and comments properly
            List<string> cleanedLines = [];
            string[] lines = cleaned.Split(["\r\n"], StringSplitOptions.None);

            // Flag for conditional state (to prevent broken conditionals in output)
            bool insideConditional = false;
            int conditionalLevel = 0;

            foreach (string line in lines)
            {
                // Check if line is empty or whitespace only
                if (string.IsNullOrWhiteSpace(line)) continue; // Skip empty or whitespace-only lines

                // Remove C-style comments (//), handling indentation before comments
                string lineWithoutComments = CommentRegex.Replace(line, string.Empty);

                // If line was just a comment, skip it
                if (string.IsNullOrWhiteSpace(lineWithoutComments)) continue;

                // Remove all indentation (spaces and tabs at the beginning)
                string unindentedLine = lineWithoutComments.Trim();

                // Track conditional block state
                if (unindentedLine.Contains("#{If(") || unindentedLine.Contains("#{ForEach("))
                {
                    insideConditional = true;
                    conditionalLevel++;
                }
                else if (unindentedLine.Contains("#{EndIf}") || unindentedLine.Contains("#{EndForEach}"))
                {
                    conditionalLevel--;
                    if (conditionalLevel <= 0)
                    {
                        insideConditional = false;
                        conditionalLevel = 0;
                    }
                }

                // Make sure we don't have unclosed or unopened conditional blocks
                // by doing a basic syntax check
                if ((unindentedLine.Contains("#{EndIf}") || unindentedLine.Contains("#{EndForEach}") ||
                     unindentedLine.Contains("#{Else}")) && !insideConditional && conditionalLevel <= 0)
                {
                    Log.Warning("SRTemplateProcessor: Found {Marker} outside of a conditional block - skipping line", unindentedLine);
                    continue;
                }

                // Add the cleaned line if it's not empty
                if (!string.IsNullOrEmpty(unindentedLine))
                {
                    cleanedLines.Add(unindentedLine);
                }
            }

            // Warn if we have unclosed conditional blocks
            if (insideConditional && conditionalLevel > 0)
            {
                Log.Warning("SRTemplateProcessor: Unclosed conditional blocks detected in template ({Count})", conditionalLevel);
            }

            // Rejoin lines with the proper HL7 line ending
            return string.Join("\r\n", cleanedLines);
        }

        /// <summary>
        /// Process all loop and conditional blocks in the template
        /// </summary>
        private string ProcessTemplateBlocks(string template)
        {
            // First process all For loops
            template = ProcessForEachBlocks(template);

            // Then process all If blocks
            template = ProcessIfBlocks(template);

            return template;
        }

        /// <summary>
        /// Process ForEach blocks in the template
        /// </summary>
        private string ProcessForEachBlocks(string template)
        {
          while (true)
          {
            // Find the first ForEach block in the template
            Match forEachMatch = ForEachRegex.Match(template);
            if (!forEachMatch.Success)
            {
              return template;
            }

            // Find the corresponding EndForEach
            int nestedCount = 1;
            int endForEachIndex = -1;

            for (int i = forEachMatch.Index + forEachMatch.Length; i < template.Length; i++)
            {
              if (template.Substring(i).StartsWith("#{ForEach("))
                nestedCount++;
              else if (template.Substring(i).StartsWith("#{EndForEach}"))
              {
                nestedCount--;
                if (nestedCount != 0) continue;
                endForEachIndex = i;
                break;
              }
            }

            if (endForEachIndex == -1)
            {
              Log.Warning("Unmatched #{{ForEach}} in template");
              return template;
            }

            // Extract the path and variable name
            string path = forEachMatch.Groups[1].Value;
            string varName = forEachMatch.Groups[2].Value;

            Log.Debug("SRTemplateProcessor: Processing ForEach block with path '{Path}' as '{VarName}'", path, varName);

            // Extract the block content
            string blockContent = template.Substring(forEachMatch.Index + forEachMatch.Length, endForEachIndex - (forEachMatch.Index + forEachMatch.Length));

            // Find the SR container items to iterate over
            DicomDataset[] items = GetSrItemsByPath(path, dataset);
            Log.Debug("SRTemplateProcessor: Found {Count} items to iterate over in path '{Path}'", items.Length, path);

            // Build the replacement content
            StringBuilder replacementContent = new();
            for (int i = 0; i < items.Length; i++)
            {
              // Store the current item in loop variables
              _loopVariables[varName] = items[i];
              Log.Debug("SRTemplateProcessor: ForEach iteration {Index}/{Total} for variable '{VarName}'", i + 1, items.Length, varName);

              // Process the block content with the current item
              string processedBlock = blockContent;

              // Replace loop variable references
              processedBlock = LoopVarPathRegex.Replace(processedBlock, match =>
              {
                string loopVar = match.Groups[1].Value;
                string loopPath = match.Groups[2].Value;
                string attribute = match.Groups[3].Value;

                if (loopVar != varName || !_loopVariables.TryGetValue(loopVar, out DicomDataset variable)) return string.Empty;
                DicomDataset[] pathItems = GetSrItemsByPath(loopPath, variable);
                string value = pathItems.Length > 0 ? GetAttributeValue(pathItems[0], attribute) : string.Empty;
                Log.Debug("SRTemplateProcessor: Loop variable reference {LoopVar}/{LoopPath}.{Attribute} = '{Value}'",
                          loopVar, loopPath, attribute, value);
                return value;
              });

              // Process nested blocks
              processedBlock = ProcessTemplateBlocks(processedBlock);

              replacementContent.Append(processedBlock);
            }

            Log.Debug("SRTemplateProcessor: Completed ForEach block for '{VarName}'", varName);

            // Replace the entire ForEach block with the processed content
            string result = template.Substring(0, forEachMatch.Index) + replacementContent.ToString() + template.Substring(endForEachIndex + "#{EndForEach}".Length);

            // Process any remaining ForEach blocks
            template = result;
          }
        }

        /// <summary>
        /// Process If blocks in the template
        /// </summary>
        private string ProcessIfBlocks(string template)
        {
          while (true)
          {
            // Find the first If block in the template
            Match ifMatch = IfRegex.Match(template);
            if (!ifMatch.Success)
            {
              return template;
            }

            // Find the corresponding Else and EndIf
            int nestedCount = 1;
            int elseIndex = -1;
            int endIfIndex = -1;

            for (int i = ifMatch.Index + ifMatch.Length; i < template.Length; i++)
            {
              if (template.Substring(i).StartsWith("#{If("))
                nestedCount++;
              else if (template.Substring(i).StartsWith("#{Else}") && nestedCount == 1 && elseIndex == -1)
                elseIndex = i;
              else if (template.Substring(i).StartsWith("#{EndIf}"))
              {
                nestedCount--;
                if (nestedCount != 0) continue;
                endIfIndex = i;
                break;
              }
            }

            if (endIfIndex == -1)
            {
              Log.Warning("Unmatched #{{If}} in template");
              return template;
            }

            // Extract the condition path
            string conditionPath = ifMatch.Groups[1].Value;
            Log.Debug("SRTemplateProcessor: Evaluating condition '{ConditionPath}'", conditionPath);

            // Evaluate the condition
            bool condition = EvaluateCondition(conditionPath);
            Log.Debug("SRTemplateProcessor: Condition '{ConditionPath}' evaluated to {Result}", conditionPath, condition);

            // Extract the if and else blocks
            string ifBlock = elseIndex != -1
              ? template.Substring(ifMatch.Index + ifMatch.Length, elseIndex - (ifMatch.Index + ifMatch.Length))
              : template.Substring(ifMatch.Index + ifMatch.Length, endIfIndex - (ifMatch.Index + ifMatch.Length));

            string elseBlock = elseIndex != -1
              ? template.Substring(elseIndex + "#{Else}".Length, endIfIndex - (elseIndex + "#{Else}".Length))
              : string.Empty;

            // Process the appropriate block
            string blockToProcess = condition ? ifBlock : elseBlock;
            Log.Debug("SRTemplateProcessor: Processing {BlockType} block", condition ? "IF" : "ELSE");

            // Process nested blocks
            string processedBlock = ProcessTemplateBlocks(blockToProcess);

            // Replace the entire If block with the processed content
            string result = template.Substring(0, ifMatch.Index) + processedBlock + template.Substring(endIfIndex + "#{EndIf}".Length);
            Log.Debug("SRTemplateProcessor: Completed If/Else block processing");

            // Process any remaining If blocks
            template = result;
          }
        }

        /// <summary>
        /// Process SR path expressions
        /// </summary>
        private string ProcessSrPaths(string template)
        {
            return SrPathRegex.Replace(template, match =>
            {
                string path = match.Groups[1].Value;
                string attribute = match.Groups[2].Value;
                Log.Debug("SRTemplateProcessor: Processing SR path #{SR({Path}.{Attribute})}", path, attribute);

                try
                {
                    DicomDataset[] items = GetSrItemsByPath(path, dataset);
                    if (items.Length > 0)
                    {
                        string value = GetAttributeValue(items[0], attribute);
                        Log.Debug("SRTemplateProcessor: SR path #{SR({Path}.{Attribute})} = '{Value}'", path, attribute, value);
                        return value;
                    }
                    Log.Debug("SRTemplateProcessor: No items found for SR path '{Path}'", path);
                }
                catch (Exception ex)
                {
                    Log.Warning("Error processing SR path {Path}.{Attribute}: {Message}", path, attribute, ex.Message);
                }
                return string.Empty;
            });
        }

        /// <summary>
        /// Process HL7 sequence number placeholders
        /// </summary>
        private string ProcessSequenceNumbers(string template)
        {
            return SequenceNumberRegex.Replace(template, match =>
            {
                string segmentType = match.Groups[1].Value;
                if (!_sequenceNumbers.ContainsKey(segmentType))
                {
                    _sequenceNumbers[segmentType] = 0;
                }
                _sequenceNumbers[segmentType]++;
                Log.Debug("SRTemplateProcessor: HL7 sequence number for {SegmentType} = {Value}",
                          segmentType, _sequenceNumbers[segmentType]);
                return _sequenceNumbers[segmentType].ToString();
            });
        }

        /// <summary>
        /// Get SR items matching the given path expression
        /// </summary>
        private DicomDataset[] GetSrItemsByPath(string path, DicomDataset startingDataset)
        {
            if (startingDataset == null || string.IsNullOrEmpty(path))
            {
                Log.Debug("SRTemplateProcessor: Null dataset or empty path in GetSrItemsByPath");
                return [];
            }

            // Check if path starts with a loop variable name
            string[] pathParts = path.Split('/');
            if (pathParts.Length > 0 && _loopVariables.TryGetValue(pathParts[0], out DicomDataset loopVar))
            {
                // If first part is a loop variable, use it as starting point and remove from path
                startingDataset = loopVar;
                path = string.Join("/", pathParts, 1, pathParts.Length - 1);
                Log.Debug("SRTemplateProcessor: Using loop variable '{LoopVar}' as starting point, new path: '{Path}'",
                          pathParts[0], path);
            }

            // Handle case where path contains a Code selector directly
            if (path.Contains("Code(") && !path.Contains("/"))
            {
                Match codeMatch = CodeSelectorRegex.Match(path);
                if (codeMatch.Success)
                {
                    string scheme = codeMatch.Groups[1].Value;
                    string value = codeMatch.Groups[2].Value;
                    Log.Debug("SRTemplateProcessor: Direct code selector Code({Scheme}:{Value})", scheme, value);

                    // Look for matching code in content sequence
                    if (startingDataset.Contains(DicomTag.ContentSequence))
                    {
                        DicomSequence contentSeq = startingDataset.GetSequence(DicomTag.ContentSequence);
                        if (contentSeq != null)
                        {
                            List<DicomDataset> matches = new();
                            foreach (DicomDataset item in contentSeq.Items)
                            {
                                if (MatchesConceptCode(item, scheme, value))
                                {
                                    matches.Add(item);
                                }
                            }
                            Log.Debug("SRTemplateProcessor: Found {Count} items matching code {Scheme}:{Value}",
                                      matches.Count, scheme, value);
                            return matches.ToArray();
                        }
                    }
                    Log.Debug("SRTemplateProcessor: No ContentSequence or no matching items for code {Scheme}:{Value}", scheme, value);
                    return [];
                }
            }

            // If empty path after loop variable extraction, return the starting dataset
            if (string.IsNullOrEmpty(path))
            {
                Log.Debug("SRTemplateProcessor: Empty path after loop variable extraction, returning starting dataset");
                return [startingDataset];
            }

            // Split the path into segments
            string[] pathSegments = path.Split('/');
            Log.Debug("SRTemplateProcessor: Path segments: {Segments}", string.Join(", ", pathSegments));

            // Start with the root dataset (or the provided starting dataset)
            List<DicomDataset> currentItems = [startingDataset];

            // Navigate through path segments
            foreach (string segment in pathSegments)
            {
                Log.Debug("SRTemplateProcessor: Processing path segment '{Segment}', current items: {Count}",
                          segment, currentItems.Count);
                List<DicomDataset> nextItems = new();

                // Special "items" selector to get all immediate children in ContentSequence
                if (segment.Equals("items", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Debug("SRTemplateProcessor: Using 'items' selector to get all immediate ContentSequence children");
                    foreach (DicomDataset item in currentItems)
                    {
                        if (item.Contains(DicomTag.ContentSequence))
                        {
                            DicomSequence contentSequence = item.GetSequence(DicomTag.ContentSequence);
                            if (contentSequence != null)
                            {
                                nextItems.AddRange(contentSequence.Items);
                            }
                        }
                    }
                    Log.Debug("SRTemplateProcessor: Found {Count} items with 'items' selector", nextItems.Count);
                }
                // Check if this is an index selector
                else if (segment.StartsWith("[") && segment.EndsWith("]") &&
                    int.TryParse(segment.Substring(1, segment.Length - 2), out int index))
                {
                    // Handle index-based selection for ContentSequence
                    Log.Debug("SRTemplateProcessor: Index selector [{Index}]", index);
                    foreach (DicomDataset item in currentItems)
                    {
                        if (item.Contains(DicomTag.ContentSequence))
                        {
                            DicomSequence contentSequence = item.GetSequence(DicomTag.ContentSequence);
                            if (contentSequence != null && contentSequence.Items.Count > index)
                            {
                                // Convert from 0-based to 1-based indexing (assuming template uses 1-based)
                                nextItems.Add(contentSequence.Items[index - 1]);
                            }
                        }
                    }
                    Log.Debug("SRTemplateProcessor: Found {Count} items for index [{Index}]", nextItems.Count, index);
                }
                else if (segment.StartsWith("Code(") && segment.EndsWith(")"))
                {
                    // Direct Code selector
                    Match codeMatch = CodeSelectorRegex.Match(segment);
                    if (codeMatch.Success)
                    {
                        string scheme = codeMatch.Groups[1].Value;
                        string value = codeMatch.Groups[2].Value;
                        Log.Debug("SRTemplateProcessor: Code selector Code({Scheme}:{Value})", scheme, value);

                        foreach (DicomDataset item in currentItems)
                        {
                            if (item.Contains(DicomTag.ContentSequence))
                            {
                                DicomSequence contentSequence = item.GetSequence(DicomTag.ContentSequence);
                                if (contentSequence != null)
                                {
                                    foreach (DicomDataset contentItem in contentSequence.Items)
                                    {
                                        if (MatchesConceptCode(contentItem, scheme, value))
                                        {
                                            nextItems.Add(contentItem);
                                        }
                                    }
                                }
                            }
                        }
                        Log.Debug("SRTemplateProcessor: Found {Count} items matching code {Scheme}:{Value}",
                                  nextItems.Count, scheme, value);
                    }
                }
                else
                {
                    // Normal selector-based matching
                    Log.Debug("SRTemplateProcessor: Normal selector '{Selector}'", segment);
                    foreach (DicomDataset item in currentItems)
                    {
                        // Skip if there's no ContentSequence
                        if (!item.Contains(DicomTag.ContentSequence))
                        {
                            continue;
                        }

                        // Get all items from the ContentSequence
                        DicomSequence contentSequence = item.GetSequence(DicomTag.ContentSequence);
                        if (contentSequence == null || contentSequence.Items.Count == 0)
                        {
                            continue;
                        }

                        // Match items based on the segment selector
                        foreach (DicomDataset sequenceItem in contentSequence.Items)
                        {
                            if (MatchesSelector(sequenceItem, segment))
                            {
                                nextItems.Add(sequenceItem);
                            }
                        }
                    }
                    Log.Debug("SRTemplateProcessor: Found {Count} items matching selector '{Selector}'",
                              nextItems.Count, segment);
                }

                // Update current items for the next path segment
                currentItems = nextItems;

                // If no items match the current segment, return empty array
                if (currentItems.Count == 0)
                {
                    Log.Debug("SRTemplateProcessor: No items matched segment '{Segment}', returning empty result", segment);
                    return [];
                }
            }

            Log.Debug("SRTemplateProcessor: Completed path navigation, found {Count} items", currentItems.Count);
            return currentItems.ToArray();
        }

        /// <summary>
        /// Check if a dataset matches a path selector
        /// </summary>
        private static bool MatchesSelector(DicomDataset dataset, string selector)
        {
            // Index selector [n]
            if (selector.StartsWith("[") && selector.EndsWith("]"))
            {
                // This is handled specially by the GetSrItemsByPath method
                // where it will select the nth item from the sequence
                // We always return true here as the actual indexing is done elsewhere
                bool result = int.TryParse(selector.Substring(1, selector.Length - 2), out _);
                Log.Debug("SRTemplateProcessor: Index selector matching = {Result}", result);
                return result;
            }

            // Code selector Code(Scheme:Value)
            if (selector.StartsWith("Code(") && selector.EndsWith(")"))
            {
                Match codeMatch = CodeSelectorRegex.Match(selector);
                if (codeMatch.Success)
                {
                    string scheme = codeMatch.Groups[1].Value;
                    string value = codeMatch.Groups[2].Value;
                    bool result = MatchesConceptCode(dataset, scheme, value);
                    Log.Debug("SRTemplateProcessor: Code selector match Code({Scheme}:{Value}) = {Result}",
                              scheme, value, result);
                    return result;
                }
                Log.Debug("SRTemplateProcessor: Invalid code selector syntax");
                return false;
            }

            // Type selector Type(Type)
            if (selector.StartsWith("Type(") && selector.EndsWith(")"))
            {
                string typeValue = selector.Substring("Type(".Length, selector.Length - "Type()".Length);
                bool result = dataset.TryGetSingleValue(DicomTag.ValueType, out string valueType) &&
                       string.Equals(valueType, typeValue, StringComparison.OrdinalIgnoreCase);
                Log.Debug("SRTemplateProcessor: Type selector match Type({Type}) = {Result}", typeValue, result);
                return result;
            }

            // Check if it's a numeric or alphanumeric code value
            // This will match both numeric codes (like "121070") and
            // alphanumeric codes (like "G-C171")
            if (Regex.IsMatch(selector, @"^[\w\-]+$"))
            {
                // Try to match against CodeValue directly
                bool result = MatchesCodeValue(dataset, selector);
                Log.Debug("SRTemplateProcessor: Direct code value match '{CodeValue}' = {Result}", selector, result);
                return result;
            }

            // Default to concept meaning match
            bool meaningResult = MatchesConceptMeaning(dataset, selector);
            Log.Debug("SRTemplateProcessor: Concept meaning match '{Meaning}' = {Result}", selector, meaningResult);
            return meaningResult;
        }

        /// <summary>
        /// Check if a dataset's code value directly matches the specified value
        /// </summary>
        private static bool MatchesCodeValue(DicomDataset dataset, string codeValue)
        {
            if (!dataset.Contains(DicomTag.ConceptNameCodeSequence))
            {
                Log.Debug("SRTemplateProcessor: No ConceptNameCodeSequence for code value match");
                return false;
            }

            DicomSequence conceptSeq = dataset.GetSequence(DicomTag.ConceptNameCodeSequence);
            if (conceptSeq == null || conceptSeq.Items.Count == 0)
            {
                Log.Debug("SRTemplateProcessor: Empty ConceptNameCodeSequence for code value match");
                return false;
            }

            DicomDataset conceptItem = conceptSeq.Items[0];
            string itemCodeValue = conceptItem.GetSingleValueOrDefault(DicomTag.CodeValue, string.Empty);
            bool result = string.Equals(itemCodeValue, codeValue, StringComparison.OrdinalIgnoreCase);
            Log.Debug("SRTemplateProcessor: Code value match '{ItemCodeValue}' == '{ExpectedCodeValue}' = {Result}",
                      itemCodeValue, codeValue, result);
            return result;
        }

        /// <summary>
        /// Check if a dataset's concept code matches the specified scheme and value
        /// </summary>
        private static bool MatchesConceptCode(DicomDataset dataset, string scheme, string value)
        {
            if (!dataset.Contains(DicomTag.ConceptNameCodeSequence))
            {
                Log.Debug("SRTemplateProcessor: No ConceptNameCodeSequence for concept code match");
                return false;
            }

            DicomSequence conceptSeq = dataset.GetSequence(DicomTag.ConceptNameCodeSequence);
            if (conceptSeq == null || conceptSeq.Items.Count == 0)
            {
                Log.Debug("SRTemplateProcessor: Empty ConceptNameCodeSequence for concept code match");
                return false;
            }

            DicomDataset conceptItem = conceptSeq.Items[0];

            string codeScheme = conceptItem.GetSingleValueOrDefault(DicomTag.CodingSchemeDesignator, string.Empty);
            string codeValue = conceptItem.GetSingleValueOrDefault(DicomTag.CodeValue, string.Empty);

            // Direct scheme match
            bool schemeMatch = string.Equals(codeScheme, scheme, StringComparison.OrdinalIgnoreCase);

            // More flexible scheme matching for known equivalents
            if (!schemeMatch)
            {
                // SNOMED variants (SNM, SRT, etc.)
                if ((codeScheme == "SRT" && (scheme == "SNM" || scheme == "SNOMED")) ||
                    (codeScheme == "SNM" && (scheme == "SRT" || scheme == "SNOMED")) ||
                    (codeScheme == "SNOMED" && (scheme == "SRT" || scheme == "SNM")))
                {
                    schemeMatch = true;
                    Log.Debug("SRTemplateProcessor: Flexible scheme match: {ItemScheme} matched with {ExpectedScheme}",
                              codeScheme, scheme);
                }
            }

            bool valueMatch = string.Equals(codeValue, value, StringComparison.OrdinalIgnoreCase);
            Log.Debug("SRTemplateProcessor: Concept code match: ({ItemScheme}:{ItemValue}) vs ({ExpectedScheme}:{ExpectedValue}) = {Result}",
                      codeScheme, codeValue, scheme, value, schemeMatch && valueMatch);

            return schemeMatch && valueMatch;
        }

        /// <summary>
        /// Check if a dataset's concept meaning matches the specified text
        /// </summary>
        private static bool MatchesConceptMeaning(DicomDataset dataset, string conceptMeaning)
        {
            if (!dataset.Contains(DicomTag.ConceptNameCodeSequence))
            {
                Log.Debug("SRTemplateProcessor: No ConceptNameCodeSequence for concept meaning match");
                return false;
            }

            DicomSequence conceptSeq = dataset.GetSequence(DicomTag.ConceptNameCodeSequence);
            if (conceptSeq == null || conceptSeq.Items.Count == 0)
            {
                Log.Debug("SRTemplateProcessor: Empty ConceptNameCodeSequence for concept meaning match");
                return false;
            }

            DicomDataset conceptItem = conceptSeq.Items[0];
            string codeMeaning = conceptItem.GetSingleValueOrDefault(DicomTag.CodeMeaning, string.Empty);

            // Exact match (case-insensitive)
            bool exactMatch = string.Equals(codeMeaning, conceptMeaning, StringComparison.OrdinalIgnoreCase);

            // If no exact match, try to match on contained text
            bool containsMatch = false;
            if (!exactMatch && !string.IsNullOrEmpty(conceptMeaning) && !string.IsNullOrEmpty(codeMeaning))
            {
                containsMatch = codeMeaning.IndexOf(conceptMeaning, StringComparison.OrdinalIgnoreCase) >= 0;
                if (containsMatch)
                {
                    Log.Debug("SRTemplateProcessor: Partial concept meaning match: '{ItemMeaning}' contains '{ExpectedMeaning}'",
                              codeMeaning, conceptMeaning);
                }
            }

            bool result = exactMatch || containsMatch;
            Log.Debug("SRTemplateProcessor: Concept meaning match '{ItemMeaning}' == '{ExpectedMeaning}' = {Result}",
                      codeMeaning, conceptMeaning, result);
            return result;
        }

        /// <summary>
        /// Get the value of the specified attribute from a dataset
        /// </summary>
        private static string GetAttributeValue(DicomDataset dataset, string attribute)
        {
            try
            {
                Log.Debug("SRTemplateProcessor: Getting attribute '{Attribute}' from dataset", attribute);

                // Handle code-specific attributes (e.g., Code(DCM:F-008ECBI).Numeric)
                Match codeMatch = Regex.Match(attribute, @"^Code\((.*?):(.*?)\)\.(.*?)$");
                if (codeMatch.Success)
                {
                    string codeScheme = codeMatch.Groups[1].Value;
                    string codeValue = codeMatch.Groups[2].Value;
                    string attributeType = codeMatch.Groups[3].Value;
                    Log.Debug("SRTemplateProcessor: Code-specific attribute: Code({Scheme}:{Value}).{AttributeType}",
                              codeScheme, codeValue, attributeType);

                    // Find content items with matching code
                    if (dataset.Contains(DicomTag.ContentSequence))
                    {
                        DicomSequence contentSeq = dataset.GetSequence(DicomTag.ContentSequence);
                        if (contentSeq != null)
                        {
                            foreach (DicomDataset contentItem in contentSeq.Items)
                            {
                                if (MatchesConceptCode(contentItem, codeScheme, codeValue))
                                {
                                    // Return the specific attribute of the matching content item
                                    string value = GetAttributeValue(contentItem, attributeType);
                                    Log.Debug("SRTemplateProcessor: Found content item matching Code({Scheme}:{Value}), {AttributeType} = '{Value}'",
                                              codeScheme, codeValue, attributeType, value);
                                    return value;
                                }
                            }
                        }
                    }

                    Log.Debug("SRTemplateProcessor: No matching content item found for Code({Scheme}:{Value})",
                              codeScheme, codeValue);
                    return string.Empty;
                }

                string result = string.Empty;
                switch (attribute.ToLowerInvariant())
                {
                    case "text":
                        result = dataset.GetSingleValueOrDefault(DicomTag.TextValue, string.Empty);
                        Log.Debug("SRTemplateProcessor: Text attribute = '{Value}'", result);
                        return result;

                    case "numeric":
                        // First try to get from MeasuredValueSequence if present
                        if (dataset.Contains(DicomTag.MeasuredValueSequence))
                        {
                            DicomSequence measuredValueSeq = dataset.GetSequence(DicomTag.MeasuredValueSequence);
                            if (measuredValueSeq != null && measuredValueSeq.Items.Count > 0)
                            {
                                DicomDataset measuredValue = measuredValueSeq.Items[0];
                                result = measuredValue.GetSingleValueOrDefault(DicomTag.NumericValue, string.Empty);
                                Log.Debug("SRTemplateProcessor: Numeric attribute from MeasuredValueSequence = '{Value}'", result);
                                return result;
                            }
                        }

                        // If not found in MeasuredValueSequence, try direct NumericValue
                        if (dataset.Contains(DicomTag.NumericValue))
                        {
                            result = dataset.GetSingleValueOrDefault(DicomTag.NumericValue, string.Empty);
                            Log.Debug("SRTemplateProcessor: Numeric attribute from direct NumericValue = '{Value}'", result);
                            return result;
                        }

                        // Try looking in ContentSequence for numeric content items
                        if (dataset.Contains(DicomTag.ContentSequence))
                        {
                            DicomSequence contentSeq = dataset.GetSequence(DicomTag.ContentSequence);
                            if (contentSeq != null)
                            {
                                foreach (DicomDataset contentItem in contentSeq.Items)
                                {
                                    if (contentItem.Contains(DicomTag.ValueType))
                                    {
                                        string valueType = contentItem.GetSingleValueOrDefault(DicomTag.ValueType, string.Empty);
                                        if (valueType == "NUM" || valueType == "NUMERIC")
                                        {
                                            if (contentItem.Contains(DicomTag.MeasuredValueSequence))
                                            {
                                                DicomSequence numericSeq = contentItem.GetSequence(DicomTag.MeasuredValueSequence);
                                                if (numericSeq != null && numericSeq.Items.Count > 0)
                                                {
                                                    result = numericSeq.Items[0].GetSingleValueOrDefault(DicomTag.NumericValue, string.Empty);
                                                    Log.Debug("SRTemplateProcessor: Numeric attribute from child content = '{Value}'", result);
                                                    return result;
                                                }
                                            }

                                            // Check for direct NumericValue in the content item
                                            if (contentItem.Contains(DicomTag.NumericValue))
                                            {
                                                result = contentItem.GetSingleValueOrDefault(DicomTag.NumericValue, string.Empty);
                                                Log.Debug("SRTemplateProcessor: Numeric attribute from child content direct NumericValue = '{Value}'", result);
                                                return result;
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        // If the dataset itself has ValueType NUM, it might contain the value directly
                        if (dataset.Contains(DicomTag.ValueType))
                        {
                            string valueType = dataset.GetSingleValueOrDefault(DicomTag.ValueType, string.Empty);
                            if (valueType == "NUM" || valueType == "NUMERIC")
                            {
                                // Try direct accessors again with more permissive matching
                                foreach (var tag in dataset.Where(x => x.ValueRepresentation == DicomVR.DS || x.ValueRepresentation == DicomVR.IS || x.ValueRepresentation == DicomVR.FD))
                                {
                                    result = dataset.GetSingleValueOrDefault(tag.Tag, string.Empty);
                                    if (!string.IsNullOrEmpty(result) && Regex.IsMatch(result, @"^-?\d+(\.\d+)?$"))
                                    {
                                        Log.Debug("SRTemplateProcessor: Numeric attribute from tag {Tag} = '{Value}'", tag.Tag, result);
                                        return result;
                                    }
                                }
                            }
                        }

                        // As a last resort, try to get numeric value from text content (for test data)
                        result = dataset.GetSingleValueOrDefault(DicomTag.TextValue, string.Empty);
                        if (Regex.IsMatch(result, @"^\d+(\.\d+)?$"))
                        {
                            Log.Debug("SRTemplateProcessor: Numeric attribute extracted from text = '{Value}'", result);
                            return result;
                        }

                        // Final fallback - just return '0' to prevent empty values in output
                        Log.Debug("SRTemplateProcessor: No numeric value found, returning default '0'");
                        return "0";

                    case "unitscode":
                        if (dataset.Contains(DicomTag.MeasuredValueSequence))
                        {
                            DicomSequence measuredValueSeq = dataset.GetSequence(DicomTag.MeasuredValueSequence);
                            if (measuredValueSeq == null || measuredValueSeq.Items.Count <= 0) return string.Empty;
                            DicomDataset measuredValue = measuredValueSeq.Items[0];
                            if (!measuredValue.Contains(DicomTag.MeasurementUnitsCodeSequence)) return string.Empty;
                            DicomSequence unitsCodeSeq = measuredValue.GetSequence(DicomTag.MeasurementUnitsCodeSequence);
                            if (unitsCodeSeq == null || unitsCodeSeq.Items.Count <= 0) return string.Empty;
                            DicomDataset unitsCode = unitsCodeSeq.Items[0];
                            return unitsCode.GetSingleValueOrDefault(DicomTag.CodeValue, string.Empty);
                        }
                        return string.Empty;

                    case "unitsmeaning":
                        if (dataset.Contains(DicomTag.MeasuredValueSequence))
                        {
                            DicomSequence measuredValueSeq = dataset.GetSequence(DicomTag.MeasuredValueSequence);
                            if (measuredValueSeq == null || measuredValueSeq.Items.Count <= 0) return string.Empty;
                            DicomDataset measuredValue = measuredValueSeq.Items[0];
                            if (!measuredValue.Contains(DicomTag.MeasurementUnitsCodeSequence)) return string.Empty;
                            DicomSequence unitsCodeSeq = measuredValue.GetSequence(DicomTag.MeasurementUnitsCodeSequence);
                            if (unitsCodeSeq == null || unitsCodeSeq.Items.Count <= 0) return string.Empty;
                            DicomDataset unitsCode = unitsCodeSeq.Items[0];
                            return unitsCode.GetSingleValueOrDefault(DicomTag.CodeMeaning, string.Empty);
                        }
                        return string.Empty;

                    case "datetime":
                        return dataset.GetSingleValueOrDefault(DicomTag.DateTime, string.Empty);

                    case "conceptcodevalue":
                        if (dataset.Contains(DicomTag.ConceptNameCodeSequence))
                        {
                            DicomSequence conceptSeq = dataset.GetSequence(DicomTag.ConceptNameCodeSequence);
                            if (conceptSeq == null || conceptSeq.Items.Count <= 0) return string.Empty;
                            DicomDataset conceptItem = conceptSeq.Items[0];
                            return conceptItem.GetSingleValueOrDefault(DicomTag.CodeValue, string.Empty);
                        }
                        return string.Empty;

                    case "conceptcodemeaning":
                        if (dataset.Contains(DicomTag.ConceptNameCodeSequence))
                        {
                            DicomSequence conceptSeq = dataset.GetSequence(DicomTag.ConceptNameCodeSequence);
                            if (conceptSeq == null || conceptSeq.Items.Count <= 0) return string.Empty;
                            DicomDataset conceptItem = conceptSeq.Items[0];
                            return conceptItem.GetSingleValueOrDefault(DicomTag.CodeMeaning, string.Empty);
                        }
                        return string.Empty;

                    case "conceptcodingscheme":
                        if (dataset.Contains(DicomTag.ConceptNameCodeSequence))
                        {
                            DicomSequence conceptSeq = dataset.GetSequence(DicomTag.ConceptNameCodeSequence);
                            if (conceptSeq == null || conceptSeq.Items.Count <= 0) return string.Empty;
                            DicomDataset conceptItem = conceptSeq.Items[0];
                            return conceptItem.GetSingleValueOrDefault(DicomTag.CodingSchemeDesignator, string.Empty);
                        }
                        return string.Empty;

                    default:
                        Log.Warning("Unknown attribute: {Attribute}", attribute);
                        return string.Empty;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Error getting attribute {Attribute}: {Message}", attribute, ex.Message);
                return string.Empty;
            }
        }

        /// <summary>
        /// Evaluate a condition expression (supporting numeric comparisons)
        /// </summary>
        private bool EvaluateCondition(string conditionPath)
        {
            // Check for comparison operators (>, <, >=, <=, ==, !=)
            Match comparisonMatch = Regex.Match(conditionPath, @"(.*?)\s*(>|<|>=|<=|==|!=)\s*(.+)");
            if (comparisonMatch.Success)
            {
                string leftPath = comparisonMatch.Groups[1].Value.Trim();
                string op = comparisonMatch.Groups[2].Value;
                string rightValue = comparisonMatch.Groups[3].Value.Trim();

                Log.Debug("SRTemplateProcessor: Evaluating comparison: '{LeftPath}' {Op} '{RightValue}'",
                          leftPath, op, rightValue);

                // Get the left value from the path
                string leftValue = GetValueFromPath(leftPath);
                Log.Debug("SRTemplateProcessor: Left path '{LeftPath}' resolved to '{Value}'", leftPath, leftValue);

                // Try to parse as numbers for comparison if applicable
                if (double.TryParse(leftValue, out double leftNum) && double.TryParse(rightValue, out double rightNum))
                {
                    bool result = op switch
                    {
                        ">" => leftNum > rightNum,
                        "<" => leftNum < rightNum,
                        ">=" => leftNum >= rightNum,
                        "<=" => leftNum <= rightNum,
                        "==" => leftNum == rightNum,
                        "!=" => leftNum != rightNum,
                        _ => false
                    };
                    Log.Debug("SRTemplateProcessor: Numeric comparison {Left} {Op} {Right} = {Result}",
                              leftNum, op, rightNum, result);
                    return result;
                }

                // String comparison as fallback
                bool strResult = op switch
                {
                    "==" => leftValue == rightValue,
                    "!=" => leftValue != rightValue,
                    _ => false // Other operators don't make sense for strings
                };
                Log.Debug("SRTemplateProcessor: String comparison '{Left}' {Op} '{Right}' = {Result}",
                          leftValue, op, rightValue, strResult);
                return strResult;
            }

            // Check if condition is a loop variable existence check
            // (form: "loopVar/path" or just "loopVar")
            string[] pathParts = conditionPath.Split('/');
            if (pathParts.Length > 0 && _loopVariables.TryGetValue(pathParts[0], out DicomDataset loopVar))
            {
                if (pathParts.Length == 1)
                {
                    // Just checking if the loop variable exists
                    bool result = loopVar != null;
                    Log.Debug("SRTemplateProcessor: Loop variable '{LoopVar}' exists check = {Result}",
                              pathParts[0], result);
                    return result;
                }

                // Check path from loop variable
                string remainingPath = string.Join("/", pathParts, 1, pathParts.Length - 1);
                Log.Debug("SRTemplateProcessor: Checking path '{Path}' from loop variable '{LoopVar}'",
                          remainingPath, pathParts[0]);
                DicomDataset[] items = GetSrItemsByPath(remainingPath, loopVar);
                bool pathResult = items.Length > 0;
                Log.Debug("SRTemplateProcessor: Path '{Path}' from loop variable '{LoopVar}' exists check = {Result}",
                          remainingPath, pathParts[0], pathResult);
                return pathResult;
            }

            // Simple existence check using the starting dataset
            Log.Debug("SRTemplateProcessor: Simple existence check for path '{Path}'", conditionPath);
            DicomDataset[] srItems = GetSrItemsByPath(conditionPath, dataset);
            bool existsResult = srItems.Length > 0;
            Log.Debug("SRTemplateProcessor: Path '{Path}' exists check = {Result}", conditionPath, existsResult);
            return existsResult;
        }

        /// <summary>
        /// Get a value from a path expression, handling loop variables and code selectors
        /// </summary>
        private string GetValueFromPath(string path)
        {
            Log.Debug("SRTemplateProcessor: Getting value from path '{Path}'", path);

            // Check if path starts with a loop variable
            string[] pathParts = path.Split('/');
            if (pathParts.Length > 0 && _loopVariables.TryGetValue(pathParts[0], out DicomDataset loopVar))
            {
                string remainingPath = string.Join("/", pathParts, 1, pathParts.Length - 1);
                Log.Debug("SRTemplateProcessor: Path starts with loop variable '{LoopVar}', remaining path: '{RemainingPath}'",
                          pathParts[0], remainingPath);

                // Handle attribute part separated by dot
                int lastDotIndex = remainingPath.LastIndexOf('.');
                if (lastDotIndex > 0)
                {
                    string itemPath = remainingPath.Substring(0, lastDotIndex);
                    string attribute = remainingPath.Substring(lastDotIndex + 1);
                    Log.Debug("SRTemplateProcessor: Parsing attribute reference: item path = '{ItemPath}', attribute = '{Attribute}'",
                              itemPath, attribute);

                    // Handle code selector in path
                    if (itemPath.Contains("Code("))
                    {
                        Match codeMatch = CodeSelectorRegex.Match(itemPath);
                        if (codeMatch.Success)
                        {
                            string scheme = codeMatch.Groups[1].Value;
                            string value = codeMatch.Groups[2].Value;
                            Log.Debug("SRTemplateProcessor: Code selector in loop variable path: Code({Scheme}:{Value})",
                                      scheme, value);

                            // Look for items with that code in the loop variable
                            if (loopVar.Contains(DicomTag.ContentSequence))
                            {
                                DicomSequence contentSeq = loopVar.GetSequence(DicomTag.ContentSequence);
                                if (contentSeq != null)
                                {
                                    foreach (DicomDataset item in contentSeq.Items)
                                    {
                                        if (MatchesConceptCode(item, scheme, value))
                                        {
                                            string result = GetAttributeValue(item, attribute);
                                            Log.Debug("SRTemplateProcessor: Found code match in loop variable, {Attribute} = '{Value}'",
                                                      attribute, result);
                                            return result;
                                        }
                                    }
                                }
                            }
                            Log.Debug("SRTemplateProcessor: No matching content item for code in loop variable");
                            return string.Empty;
                        }
                    }
                    else
                    {
                        // No code selector, use regular path navigation
                        DicomDataset[] items = GetSrItemsByPath(itemPath, loopVar);
                        if (items.Length > 0)
                        {
                            string result = GetAttributeValue(items[0], attribute);
                            Log.Debug("SRTemplateProcessor: Found items for path in loop variable, {Attribute} = '{Value}'",
                                      attribute, result);
                            return result;
                        }
                        Log.Debug("SRTemplateProcessor: No items found for path in loop variable");
                    }
                }
                else if (!string.IsNullOrEmpty(remainingPath))
                {
                    // Just path without attribute
                    DicomDataset[] items = GetSrItemsByPath(remainingPath, loopVar);
                    bool result = items.Length > 0;
                    Log.Debug("SRTemplateProcessor: Path existence in loop variable = {Result}", result);
                    return result ? "true" : string.Empty;
                }
                else
                {
                    // Just the loop variable itself
                    bool result = loopVar != null;
                    Log.Debug("SRTemplateProcessor: Loop variable existence = {Result}", result);
                    return result ? "true" : string.Empty;
                }
            }
            else
            {
                // Not a loop variable path, use regular path navigation
                Log.Debug("SRTemplateProcessor: Path does not start with loop variable");

                // Check if there's an attribute part
                int lastDotIndex = path.LastIndexOf('.');
                if (lastDotIndex > 0)
                {
                    string itemPath = path.Substring(0, lastDotIndex);
                    string attribute = path.Substring(lastDotIndex + 1);
                    Log.Debug("SRTemplateProcessor: Parsing attribute reference: item path = '{ItemPath}', attribute = '{Attribute}'",
                              itemPath, attribute);

                    // Check if path has a code selector
                    if (itemPath.Contains("Code("))
                    {
                        Match codeMatch = Regex.Match(itemPath, @"(.*?)\/Code\((.*?):(.*?)\)$");
                        if (codeMatch.Success)
                        {
                            string basePath = codeMatch.Groups[1].Value;
                            string scheme = codeMatch.Groups[2].Value;
                            string value = codeMatch.Groups[3].Value;
                            Log.Debug("SRTemplateProcessor: Code selector in regular path: {BasePath}/Code({Scheme}:{Value})",
                                      basePath, scheme, value);

                            // Get base path items
                            DicomDataset[] baseItems = string.IsNullOrEmpty(basePath)
                                ? new[] { dataset }
                                : GetSrItemsByPath(basePath, dataset);

                            Log.Debug("SRTemplateProcessor: Found {Count} base items for path '{BasePath}'",
                                      baseItems.Length, basePath);

                            if (baseItems.Length > 0)
                            {
                                // Find items with matching code
                                foreach (DicomDataset baseItem in baseItems)
                                {
                                    if (baseItem.Contains(DicomTag.ContentSequence))
                                    {
                                        DicomSequence contentSeq = baseItem.GetSequence(DicomTag.ContentSequence);
                                        if (contentSeq != null)
                                        {
                                            foreach (DicomDataset contentItem in contentSeq.Items)
                                            {
                                                if (MatchesConceptCode(contentItem, scheme, value))
                                                {
                                                    string result = GetAttributeValue(contentItem, attribute);
                                                    Log.Debug("SRTemplateProcessor: Found code match in base path, {Attribute} = '{Value}'",
                                                              attribute, result);
                                                    return result;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            Log.Debug("SRTemplateProcessor: No matching content item for code in base path");
                            return string.Empty;
                        }
                        else
                        {
                            Log.Debug("SRTemplateProcessor: Invalid code selector syntax in path");
                        }
                    }
                    else
                    {
                        // Regular path navigation
                        DicomDataset[] items = GetSrItemsByPath(itemPath, dataset);
                        if (items.Length > 0)
                        {
                            string result = GetAttributeValue(items[0], attribute);
                            Log.Debug("SRTemplateProcessor: Found items for path, {Attribute} = '{Value}'", attribute, result);
                            return result;
                        }
                        Log.Debug("SRTemplateProcessor: No items found for path");
                    }
                }
                else
                {
                    // No attribute part, just check existence
                    DicomDataset[] items = GetSrItemsByPath(path, dataset);
                    bool result = items.Length > 0;
                    Log.Debug("SRTemplateProcessor: Path existence = {Result}", result);
                    return result ? "true" : string.Empty;
                }
            }

            Log.Debug("SRTemplateProcessor: No value found for path '{Path}'", path);
            return string.Empty;
        }

        /// <summary>
        /// Process the actual template, replacing variables and executing conditions
        /// </summary>
        private string ProcessTemplate(string template, DicomDataset dataset)
        {
            // First, find the conditionals and process them
            string result = template;

            // Process If/Endif blocks
            result = ProcessIfBlocks(result, dataset);

            // Process the regular variables
            MatchCollection matches = VariableRegex.Matches(result);
            foreach (Match match in matches)
            {
                string variableName = match.Groups[1].Value;
                string variablePattern = $"#{{{variableName}}}";

                // Check if this is an escape pattern (e.g., #{{escaped}})
                if (variableName.StartsWith("{") && variableName.EndsWith("}"))
                {
                    // This is an escaped variable, remove the extra braces
                    result = result.Replace(variablePattern, $"#{{{variableName.Substring(1, variableName.Length - 2)}}}");
                    continue;
                }

                // Support flexible attribute access syntax (both dot notation and path notation)
                string value = GetAttributeValueWithFlexibleSyntax(variableName, dataset);

                if (value != null)
                {
                    result = result.Replace(variablePattern, value);
                    Log.Debug("SRTemplateProcessor: Replaced variable {Variable} with value {Value}", variableName, value);
                }
                else
                {
                    // If we couldn't find a value, just leave the variable as is
                    Log.Warning("SRTemplateProcessor: No value found for variable {Variable}", variableName);
                }
            }

            return result;
        }

        /// <summary>
        /// Process if/else/endif conditional blocks in the template
        /// </summary>
        private string ProcessIfBlocks(string template, DicomDataset dataset)
        {
            string result = template;

            // Find all if blocks
            Regex ifRegex = new Regex(@"#\{If\((.*?)\)\}(.*?)(?:#\{Else\}(.*?))?#\{EndIf\}", RegexOptions.Singleline);

            // Continue processing until all conditionals are resolved
            bool hasMatches = true;
            while (hasMatches)
            {
                Match match = ifRegex.Match(result);
                if (!match.Success)
                {
                    hasMatches = false;
                    continue;
                }

                string condition = match.Groups[1].Value.Trim();
                string trueBlock = match.Groups[2].Value;
                string falseBlock = match.Groups.Count > 3 ? match.Groups[3].Value : string.Empty;

                // Evaluate the condition
                bool conditionMet = EvaluateCondition(condition, dataset);

                // Replace the entire conditional block with the appropriate content
                string replacement = conditionMet ? trueBlock : falseBlock;
                result = result.Replace(match.Value, replacement);

                Log.Debug("SRTemplateProcessor: Evaluated condition {Condition} as {Result}", condition, conditionMet);
            }

            return result;
        }

        /// <summary>
        /// Evaluate a condition against the dataset
        /// </summary>
        private bool EvaluateCondition(string condition, DicomDataset dataset)
        {
            // Handle equality comparison
            if (condition.Contains("=="))
            {
                string[] parts = condition.Split(new[] { "==" }, StringSplitOptions.None);
                if (parts.Length == 2)
                {
                    string leftValue = GetConditionValue(parts[0].Trim(), dataset);
                    string rightValue = GetConditionValue(parts[1].Trim(), dataset);

                    return string.Equals(leftValue, rightValue, StringComparison.OrdinalIgnoreCase);
                }
            }

            // Handle inequality comparison
            if (condition.Contains("!="))
            {
                string[] parts = condition.Split(new[] { "!=" }, StringSplitOptions.None);
                if (parts.Length == 2)
                {
                    string leftValue = GetConditionValue(parts[0].Trim(), dataset);
                    string rightValue = GetConditionValue(parts[1].Trim(), dataset);

                    return !string.Equals(leftValue, rightValue, StringComparison.OrdinalIgnoreCase);
                }
            }

            // Handle contains operator
            if (condition.Contains("Contains("))
            {
                Regex containsRegex = new Regex(@"(.*?)\.Contains\((.*?)\)");
                Match match = containsRegex.Match(condition);

                if (match.Success)
                {
                    string varName = match.Groups[1].Value.Trim();
                    string searchValue = match.Groups[2].Value.Trim();

                    string varValue = GetConditionValue(varName, dataset);
                    string searchText = GetConditionValue(searchValue, dataset);

                    return varValue != null && varValue.Contains(searchText);
                }
            }

            // If it's just a value, check if it exists and is not empty
            string value = GetConditionValue(condition, dataset);
            return !string.IsNullOrEmpty(value);
        }

        /// <summary>
        /// Get a value for condition evaluation, handling both variables and literals
        /// </summary>
        private string GetConditionValue(string value, DicomDataset dataset)
        {
            // If it's quoted, it's a literal string
            if ((value.StartsWith("\"") && value.EndsWith("\"")) || (value.StartsWith("'") && value.EndsWith("'")))
            {
                return value.Substring(1, value.Length - 2);
            }

            // Otherwise it's a variable reference
            return GetAttributeValueWithFlexibleSyntax(value, dataset);
        }

        /// <summary>
        /// Enhanced attribute access supporting multiple syntax styles
        /// </summary>
        private string GetAttributeValueWithFlexibleSyntax(string attributePath, DicomDataset dataset)
        {
            // If the path contains a slash, treat it as a path-style navigation
            if (attributePath.Contains("/"))
            {
                string[] parts = attributePath.Split('/');
                return GetAttributeValueFromPath(dataset, parts);
            }

            // If the path contains a dot, treat it as dot notation
            if (attributePath.Contains("."))
            {
                string[] parts = attributePath.Split('.');
                return GetAttributeValueFromPath(dataset, parts);
            }

            // Single attribute access
            return GetAttributeValueFromPath(dataset, new[] { attributePath });
        }

        /// <summary>
        /// Get attribute value by navigating through a path
        /// </summary>
        private string GetAttributeValueFromPath(DicomDataset dataset, string[] path)
        {
            if (dataset == null || path == null || path.Length == 0)
            {
                return null;
            }

            try
            {
                DicomDataset currentDataset = dataset;
                string result = null;

                for (int i = 0; i < path.Length; i++)
                {
                    string part = path[i];

                    // Last part should be the attribute we want to retrieve
                    if (i == path.Length - 1)
                    {
                        // Try to get the value directly
                        try
                        {
                            // Handle common attributes explicitly as we can't convert from string
                            if (part.Equals("TextValue", StringComparison.OrdinalIgnoreCase))
                            {
                                result = currentDataset.GetSingleValueOrDefault(DicomTag.TextValue, string.Empty);
                            }
                            else if (part.Equals("ValueType", StringComparison.OrdinalIgnoreCase))
                            {
                                result = currentDataset.GetSingleValueOrDefault(DicomTag.ValueType, string.Empty);
                            }
                            else if (part.Equals("NumericValue", StringComparison.OrdinalIgnoreCase))
                            {
                                result = currentDataset.GetSingleValueOrDefault(DicomTag.NumericValue, string.Empty);
                            }
                            else if (part.Equals("DateTime", StringComparison.OrdinalIgnoreCase))
                            {
                                result = currentDataset.GetSingleValueOrDefault(DicomTag.DateTime, string.Empty);
                            }
                            else
                            {
                                // Try to get by sequence index if it's a number
                                if (int.TryParse(part, out int index))
                                {
                                    // We're looking for a specific sequence item by index
                                    // This assumes the parent dataset is already a sequence item
                                    // This is handled by the previous iterations
                                }
                                else
                                {
                                    // Check if it's a specialized value
                                    result = GetSpecializedValue(part, currentDataset);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Debug("SRTemplateProcessor: Error getting attribute value: {Error}", ex.Message);
                        }
                    }
                    else
                    {
                        // Navigate to the next dataset in the path
                        try
                        {
                            DicomSequence sequence = null;

                            // Handle common sequence tags explicitly
                            if (part.Equals("ContentSequence", StringComparison.OrdinalIgnoreCase))
                            {
                                sequence = currentDataset.GetSequence(DicomTag.ContentSequence);
                            }
                            else if (part.Equals("MeasuredValueSequence", StringComparison.OrdinalIgnoreCase))
                            {
                                sequence = currentDataset.GetSequence(DicomTag.MeasuredValueSequence);
                            }
                            else if (part.Equals("ConceptNameCodeSequence", StringComparison.OrdinalIgnoreCase))
                            {
                                sequence = currentDataset.GetSequence(DicomTag.ConceptNameCodeSequence);
                            }
                            else if (part.Equals("MeasurementUnitsCodeSequence", StringComparison.OrdinalIgnoreCase))
                            {
                                sequence = currentDataset.GetSequence(DicomTag.MeasurementUnitsCodeSequence);
                            }

                            if (sequence == null)
                            {
                                return null;
                            }

                            // If the next part is a number, get that specific sequence item
                            if (i + 1 < path.Length && int.TryParse(path[i + 1], out int index))
                            {
                                if (index < sequence.Items.Count)
                                {
                                    currentDataset = sequence.Items[index];
                                    i++; // Skip the index part
                                }
                                else
                                {
                                    // Index out of range
                                    return null;
                                }
                            }
                            else
                            {
                                // Just get the first sequence item by default
                                if (sequence.Items.Count > 0)
                                {
                                    currentDataset = sequence.Items[0];
                                }
                                else
                                {
                                    // Empty sequence
                                    return null;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Debug("SRTemplateProcessor: Error navigating sequence: {Error}", ex.Message);
                            // Sequence not found or error
                            return null;
                        }
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                Log.Warning("SRTemplateProcessor: Error getting attribute value: {Error}", ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Get specialized values from the dataset based on specific keys
        /// </summary>
        private string GetSpecializedValue(string key, DicomDataset dataset)
        {
            switch (key.ToLower())
            {
                case "value":
                    // Extract numeric value with unit if available
                    if (dataset.Contains(DicomTag.NumericValue))
                    {
                        string value = dataset.GetString(DicomTag.NumericValue);
                        if (dataset.Contains(DicomTag.MeasurementUnitsCodeSequence))
                        {
                            var unitSeq = dataset.GetSequence(DicomTag.MeasurementUnitsCodeSequence);
                            if (unitSeq.Items.Count > 0)
                            {
                                string unit = unitSeq.Items[0].GetString(DicomTag.CodeMeaning);
                                return $"{value} {unit}";
                            }
                        }
                        return value;
                    }
                    break;

                case "code":
                    // Get the code value and meaning
                    if (dataset.Contains(DicomTag.ConceptNameCodeSequence))
                    {
                        var codeSeq = dataset.GetSequence(DicomTag.ConceptNameCodeSequence);
                        if (codeSeq.Items.Count > 0)
                        {
                            string codeValue = codeSeq.Items[0].GetString(DicomTag.CodeValue);
                            string codeMeaning = codeSeq.Items[0].GetString(DicomTag.CodeMeaning);
                            return $"{codeValue} - {codeMeaning}";
                        }
                    }
                    break;
            }

            return null;
        }
    }
}

