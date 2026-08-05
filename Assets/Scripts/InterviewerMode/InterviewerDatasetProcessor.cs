using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

public enum InterviewerDelimiterMode
{
    Auto,
    Comma,
    Tab,
    Semicolon
}

public enum InterviewerNormalizationMode
{
    None,
    MinMax,
    ZScore
}

public enum InterviewerMissingValueMode
{
    DropRow,
    FillZero,
    FillMean
}

[Serializable]
public sealed class InterviewerDatasetSettings
{
    public string sourcePath;
    public string outputName = "interviewer-dataset";
    public bool hasHeader = true;
    public string labelColumn = "";
    public InterviewerDelimiterMode delimiter = InterviewerDelimiterMode.Auto;
    public InterviewerNormalizationMode normalization =
        InterviewerNormalizationMode.MinMax;
    public InterviewerMissingValueMode missingValues =
        InterviewerMissingValueMode.DropRow;
    public bool shuffle = true;
    public int randomSeed = 42;
    public int maximumRows = 50000;
    public float trainSplit = 0.70f;
    public float validationSplit = 0.15f;

    // Suggested training parameters travel with the processed dataset. The
    // processor does not start model training by itself.
    public int epochs = 10;
    public int batchSize = 32;
    public float learningRate = 0.001f;
}

[Serializable]
public sealed class InterviewerDatasetManifest
{
    public string datasetName;
    public string sourceFileName;
    public string createdAtUtc;
    public string labelColumn;
    public List<string> columns = new List<string>();
    public int sourceRows;
    public int processedRows;
    public int droppedRows;
    public int trainRows;
    public int validationRows;
    public int testRows;
    public string delimiter;
    public string normalization;
    public string missingValues;
    public bool shuffled;
    public int randomSeed;
    public int suggestedEpochs;
    public int suggestedBatchSize;
    public float suggestedLearningRate;
    public string trainFile;
    public string validationFile;
    public string testFile;
}

public sealed class InterviewerDatasetResult
{
    public bool success;
    public string message;
    public string outputDirectory;
    public string manifestPath;
    public string manifestJson;
    public string preview;
    public InterviewerDatasetManifest manifest;
}

/// <summary>
/// Local CSV/TSV preprocessing used only by Interviewer Mode. Raw input never
/// leaves the machine. It produces deterministic train/validation/test files
/// and a manifest that can later be shared through the realtime bridge.
/// </summary>
public static class InterviewerDatasetProcessor
{
    public static InterviewerDatasetResult Process(
        InterviewerDatasetSettings settings,
        string outputRoot
    )
    {
        try
        {
            ValidateSettings(settings);
            string text = File.ReadAllText(settings.sourcePath);
            char delimiter = ResolveDelimiter(text, settings.delimiter);
            List<List<string>> parsed = ParseRows(text, delimiter);
            parsed.RemoveAll(IsCompletelyEmpty);
            if (parsed.Count < (settings.hasHeader ? 2 : 1))
            {
                throw new InvalidOperationException(
                    "The dataset does not contain any data rows."
                );
            }

            List<string> columns;
            int dataStart;
            if (settings.hasHeader)
            {
                columns = MakeUniqueColumns(parsed[0]);
                dataStart = 1;
            }
            else
            {
                columns = new List<string>();
                for (int index = 0; index < parsed[0].Count; index++)
                {
                    columns.Add("column_" + (index + 1));
                }
                dataStart = 0;
            }

            int sourceRows = Math.Min(
                Math.Max(0, parsed.Count - dataStart),
                settings.maximumRows
            );
            List<List<string>> rows = new List<List<string>>();
            int malformedRows = 0;
            for (
                int index = dataStart;
                index < parsed.Count && rows.Count < settings.maximumRows;
                index++
            )
            {
                List<string> row = parsed[index];
                if (row.Count != columns.Count)
                {
                    malformedRows++;
                    continue;
                }
                rows.Add(new List<string>(row));
            }
            if (rows.Count == 0)
            {
                throw new InvalidOperationException(
                    "No rows match the dataset column count."
                );
            }

            int labelIndex = ResolveLabelIndex(settings.labelColumn, columns);
            int missingDropped = ApplyMissingValueStrategy(
                rows,
                labelIndex,
                settings.missingValues
            );
            if (rows.Count == 0)
            {
                throw new InvalidOperationException(
                    "Every row was removed while handling missing values."
                );
            }

            NormalizeNumericColumns(rows, labelIndex, settings.normalization);
            if (settings.shuffle)
            {
                Shuffle(rows, settings.randomSeed);
            }

            int trainCount = (int)Math.Floor(rows.Count * settings.trainSplit);
            int validationCount = (int)Math.Floor(
                rows.Count * settings.validationSplit
            );
            if (trainCount == 0)
            {
                trainCount = 1;
            }
            if (trainCount + validationCount >= rows.Count && rows.Count > 1)
            {
                validationCount = Math.Max(0, rows.Count - trainCount - 1);
            }
            int testCount = rows.Count - trainCount - validationCount;

            string safeName = SafeFileName(settings.outputName);
            string outputDirectory = Path.Combine(outputRoot, safeName);
            Directory.CreateDirectory(outputDirectory);
            string trainPath = Path.Combine(outputDirectory, "train.csv");
            string validationPath = Path.Combine(outputDirectory, "validation.csv");
            string testPath = Path.Combine(outputDirectory, "test.csv");
            WriteCsv(trainPath, columns, rows, 0, trainCount);
            WriteCsv(
                validationPath,
                columns,
                rows,
                trainCount,
                validationCount
            );
            WriteCsv(
                testPath,
                columns,
                rows,
                trainCount + validationCount,
                testCount
            );

            InterviewerDatasetManifest manifest =
                new InterviewerDatasetManifest();
            manifest.datasetName = safeName;
            manifest.sourceFileName = Path.GetFileName(settings.sourcePath);
            manifest.createdAtUtc = DateTime.UtcNow.ToString("o");
            manifest.labelColumn = columns[labelIndex];
            manifest.columns = columns;
            manifest.sourceRows = sourceRows;
            manifest.processedRows = rows.Count;
            manifest.droppedRows = malformedRows + missingDropped;
            manifest.trainRows = trainCount;
            manifest.validationRows = validationCount;
            manifest.testRows = testCount;
            manifest.delimiter = DelimiterLabel(delimiter);
            manifest.normalization = settings.normalization.ToString();
            manifest.missingValues = settings.missingValues.ToString();
            manifest.shuffled = settings.shuffle;
            manifest.randomSeed = settings.randomSeed;
            manifest.suggestedEpochs = settings.epochs;
            manifest.suggestedBatchSize = settings.batchSize;
            manifest.suggestedLearningRate = settings.learningRate;
            manifest.trainFile = trainPath;
            manifest.validationFile = validationPath;
            manifest.testFile = testPath;

            string manifestJson = JsonConvert.SerializeObject(
                manifest,
                Formatting.Indented
            );
            string manifestPath = Path.Combine(outputDirectory, "manifest.json");
            File.WriteAllText(manifestPath, manifestJson);

            return new InterviewerDatasetResult
            {
                success = true,
                message =
                    "Processed " + rows.Count + " rows. " +
                    trainCount + " train / " +
                    validationCount + " validation / " +
                    testCount + " test.",
                outputDirectory = outputDirectory,
                manifestPath = manifestPath,
                manifestJson = manifestJson,
                preview = BuildPreview(columns, rows, 4),
                manifest = manifest
            };
        }
        catch (Exception error)
        {
            return new InterviewerDatasetResult
            {
                success = false,
                message = error.Message,
                preview = ""
            };
        }
    }

    private static void ValidateSettings(InterviewerDatasetSettings settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException("settings");
        }
        if (
            string.IsNullOrWhiteSpace(settings.sourcePath) ||
            !File.Exists(settings.sourcePath)
        )
        {
            throw new FileNotFoundException("Choose an existing CSV or TSV file.");
        }
        if (settings.maximumRows < 1 || settings.maximumRows > 1000000)
        {
            throw new ArgumentOutOfRangeException(
                "maximumRows",
                "Maximum rows must be between 1 and 1,000,000."
            );
        }
        if (settings.trainSplit <= 0f || settings.trainSplit >= 1f)
        {
            throw new ArgumentOutOfRangeException(
                "trainSplit",
                "Train split must be greater than 0 and less than 1."
            );
        }
        if (
            settings.validationSplit < 0f ||
            settings.trainSplit + settings.validationSplit >= 1f
        )
        {
            throw new ArgumentOutOfRangeException(
                "validationSplit",
                "Train and validation splits must leave room for a test split."
            );
        }
        if (settings.epochs < 1 || settings.epochs > 10000)
        {
            throw new ArgumentOutOfRangeException(
                "epochs",
                "Epochs must be between 1 and 10,000."
            );
        }
        if (settings.batchSize < 1 || settings.batchSize > 65536)
        {
            throw new ArgumentOutOfRangeException(
                "batchSize",
                "Batch size must be between 1 and 65,536."
            );
        }
        if (settings.learningRate <= 0f || settings.learningRate > 10f)
        {
            throw new ArgumentOutOfRangeException(
                "learningRate",
                "Learning rate must be greater than 0 and no more than 10."
            );
        }
    }

    private static char ResolveDelimiter(
        string text,
        InterviewerDelimiterMode mode
    )
    {
        if (mode == InterviewerDelimiterMode.Comma)
        {
            return ',';
        }
        if (mode == InterviewerDelimiterMode.Tab)
        {
            return '\t';
        }
        if (mode == InterviewerDelimiterMode.Semicolon)
        {
            return ';';
        }

        string firstLine = text.Split(new[] { "\r\n", "\n", "\r" }, 2, StringSplitOptions.None)[0];
        char[] candidates = { ',', '\t', ';' };
        char selected = ',';
        int bestCount = -1;
        foreach (char candidate in candidates)
        {
            int count = CountOutsideQuotes(firstLine, candidate);
            if (count > bestCount)
            {
                selected = candidate;
                bestCount = count;
            }
        }
        return selected;
    }

    private static int CountOutsideQuotes(string value, char delimiter)
    {
        bool quoted = false;
        int count = 0;
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] == '"')
            {
                if (
                    quoted &&
                    index + 1 < value.Length &&
                    value[index + 1] == '"'
                )
                {
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (!quoted && value[index] == delimiter)
            {
                count++;
            }
        }
        return count;
    }

    private static List<List<string>> ParseRows(string text, char delimiter)
    {
        List<List<string>> rows = new List<List<string>>();
        List<string> row = new List<string>();
        StringBuilder field = new StringBuilder();
        bool quoted = false;
        for (int index = 0; index < text.Length; index++)
        {
            char current = text[index];
            if (current == '"')
            {
                if (
                    quoted &&
                    index + 1 < text.Length &&
                    text[index + 1] == '"'
                )
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
                continue;
            }
            if (!quoted && current == delimiter)
            {
                row.Add(field.ToString().Trim());
                field.Length = 0;
                continue;
            }
            if (!quoted && (current == '\r' || current == '\n'))
            {
                if (
                    current == '\r' &&
                    index + 1 < text.Length &&
                    text[index + 1] == '\n'
                )
                {
                    index++;
                }
                row.Add(field.ToString().Trim());
                field.Length = 0;
                rows.Add(row);
                row = new List<string>();
                continue;
            }
            field.Append(current);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString().Trim());
            rows.Add(row);
        }
        return rows;
    }

    private static bool IsCompletelyEmpty(List<string> row)
    {
        return row == null || row.All(string.IsNullOrWhiteSpace);
    }

    private static List<string> MakeUniqueColumns(List<string> raw)
    {
        List<string> result = new List<string>();
        HashSet<string> used = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase
        );
        for (int index = 0; index < raw.Count; index++)
        {
            string baseName = string.IsNullOrWhiteSpace(raw[index])
                ? "column_" + (index + 1)
                : raw[index].Trim();
            string candidate = baseName;
            int suffix = 2;
            while (used.Contains(candidate))
            {
                candidate = baseName + "_" + suffix;
                suffix++;
            }
            used.Add(candidate);
            result.Add(candidate);
        }
        return result;
    }

    private static int ResolveLabelIndex(
        string requestedLabel,
        List<string> columns
    )
    {
        if (!string.IsNullOrWhiteSpace(requestedLabel))
        {
            int namedIndex = columns.FindIndex(
                value => string.Equals(
                    value,
                    requestedLabel.Trim(),
                    StringComparison.OrdinalIgnoreCase
                )
            );
            if (namedIndex >= 0)
            {
                return namedIndex;
            }
            int numericIndex;
            if (
                int.TryParse(requestedLabel, out numericIndex) &&
                numericIndex >= 0 &&
                numericIndex < columns.Count
            )
            {
                return numericIndex;
            }
            throw new InvalidOperationException(
                "Label column \"" + requestedLabel + "\" was not found."
            );
        }
        return columns.Count - 1;
    }

    private static int ApplyMissingValueStrategy(
        List<List<string>> rows,
        int labelIndex,
        InterviewerMissingValueMode mode
    )
    {
        int before = rows.Count;
        if (mode == InterviewerMissingValueMode.DropRow)
        {
            rows.RemoveAll(
                row => row.Any(value => string.IsNullOrWhiteSpace(value))
            );
            return before - rows.Count;
        }

        bool[] numeric = DetectNumericColumns(rows);
        double[] means = new double[numeric.Length];
        if (mode == InterviewerMissingValueMode.FillMean)
        {
            for (int column = 0; column < numeric.Length; column++)
            {
                if (!numeric[column] || column == labelIndex)
                {
                    continue;
                }
                double total = 0d;
                int count = 0;
                foreach (List<string> row in rows)
                {
                    double value;
                    if (
                        double.TryParse(
                            row[column],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out value
                        )
                    )
                    {
                        total += value;
                        count++;
                    }
                }
                means[column] = count == 0 ? 0d : total / count;
            }
        }

        foreach (List<string> row in rows)
        {
            for (int column = 0; column < row.Count; column++)
            {
                if (!string.IsNullOrWhiteSpace(row[column]))
                {
                    continue;
                }
                if (column == labelIndex)
                {
                    row[column] = "missing";
                }
                else if (
                    mode == InterviewerMissingValueMode.FillMean &&
                    numeric[column]
                )
                {
                    row[column] = means[column].ToString(
                        "R",
                        CultureInfo.InvariantCulture
                    );
                }
                else
                {
                    row[column] = numeric[column] ? "0" : "missing";
                }
            }
        }
        return 0;
    }

    private static bool[] DetectNumericColumns(List<List<string>> rows)
    {
        bool[] result = Enumerable.Repeat(true, rows[0].Count).ToArray();
        for (int column = 0; column < result.Length; column++)
        {
            bool sawNumber = false;
            foreach (List<string> row in rows)
            {
                if (string.IsNullOrWhiteSpace(row[column]))
                {
                    continue;
                }
                double ignored;
                if (
                    !double.TryParse(
                        row[column],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out ignored
                    )
                )
                {
                    result[column] = false;
                    break;
                }
                sawNumber = true;
            }
            result[column] = result[column] && sawNumber;
        }
        return result;
    }

    private static void NormalizeNumericColumns(
        List<List<string>> rows,
        int labelIndex,
        InterviewerNormalizationMode mode
    )
    {
        if (mode == InterviewerNormalizationMode.None)
        {
            return;
        }
        bool[] numeric = DetectNumericColumns(rows);
        for (int column = 0; column < numeric.Length; column++)
        {
            if (!numeric[column] || column == labelIndex)
            {
                continue;
            }
            double[] values = rows.Select(
                row => double.Parse(
                    row[column],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture
                )
            ).ToArray();
            double minimum = values.Min();
            double maximum = values.Max();
            double mean = values.Average();
            double variance = values.Select(
                value => (value - mean) * (value - mean)
            ).Average();
            double standardDeviation = Math.Sqrt(variance);

            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                double normalized;
                if (mode == InterviewerNormalizationMode.MinMax)
                {
                    normalized = Math.Abs(maximum - minimum) < 1e-12
                        ? 0d
                        : (values[rowIndex] - minimum) / (maximum - minimum);
                }
                else
                {
                    normalized = standardDeviation < 1e-12
                        ? 0d
                        : (values[rowIndex] - mean) / standardDeviation;
                }
                rows[rowIndex][column] = normalized.ToString(
                    "R",
                    CultureInfo.InvariantCulture
                );
            }
        }
    }

    private static void Shuffle(List<List<string>> rows, int seed)
    {
        Random random = new Random(seed);
        for (int index = rows.Count - 1; index > 0; index--)
        {
            int swapIndex = random.Next(index + 1);
            List<string> temporary = rows[index];
            rows[index] = rows[swapIndex];
            rows[swapIndex] = temporary;
        }
    }

    private static void WriteCsv(
        string path,
        List<string> columns,
        List<List<string>> rows,
        int start,
        int count
    )
    {
        using (StreamWriter writer = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            writer.WriteLine(string.Join(",", columns.Select(EscapeCsv)));
            int end = Math.Min(rows.Count, start + count);
            for (int index = start; index < end; index++)
            {
                writer.WriteLine(string.Join(",", rows[index].Select(EscapeCsv)));
            }
        }
    }

    private static string EscapeCsv(string value)
    {
        string safe = value ?? "";
        if (
            safe.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
        )
        {
            return "\"" + safe.Replace("\"", "\"\"") + "\"";
        }
        return safe;
    }

    private static string BuildPreview(
        List<string> columns,
        List<List<string>> rows,
        int maximumRows
    )
    {
        StringBuilder result = new StringBuilder();
        result.AppendLine(string.Join("  |  ", columns));
        int count = Math.Min(maximumRows, rows.Count);
        for (int index = 0; index < count; index++)
        {
            result.AppendLine(string.Join("  |  ", rows[index]));
        }
        return result.ToString().TrimEnd();
    }

    private static string SafeFileName(string value)
    {
        string candidate = string.IsNullOrWhiteSpace(value)
            ? "interviewer-dataset"
            : value.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            candidate = candidate.Replace(invalid, '-');
        }
        candidate = candidate.Trim('.', ' ');
        return string.IsNullOrWhiteSpace(candidate)
            ? "interviewer-dataset"
            : candidate;
    }

    private static string DelimiterLabel(char delimiter)
    {
        if (delimiter == '\t')
        {
            return "Tab";
        }
        if (delimiter == ';')
        {
            return "Semicolon";
        }
        return "Comma";
    }
}
