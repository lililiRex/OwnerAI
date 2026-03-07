using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Tools.Office;

/// <summary>
/// 文档操作工具 — 使用 Office/WPS 读取和制作 Word、PDF、Excel、PPT 文档
/// <para>通过 COM 自动化或命令行调用本机安装的 Office/WPS 应用</para>
/// </summary>
[Tool("document_tool",
    "使用本机 Office 或 WPS 操作文档。支持读取、创建、转换 Word (.docx)、Excel (.xlsx)、" +
    "PowerPoint (.pptx)、PDF 文件。操作类型: read(读取内容)、create(创建文档)、convert(格式转换)、open(打开文档)",
    SecurityLevel = ToolSecurityLevel.Medium,
    TimeoutSeconds = 60)]
public sealed class DocumentTool : IOwnerAITool
{
    /// <summary>支持的文档扩展名</summary>
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt", ".pdf", ".csv", ".txt", ".rtf",
    };

    /// <summary>读取操作支持的扩展名</summary>
    private static readonly HashSet<string> ReadableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt", ".pdf", ".csv", ".txt", ".rtf",
    };

    public bool IsAvailable(ToolContext context) => context.IsOwner;

    public async ValueTask<ToolResult> ExecuteAsync(
        JsonElement parameters,
        ToolContext context,
        CancellationToken ct)
    {
        if (!parameters.TryGetProperty("action", out var actionEl))
            return ToolResult.Error("缺少参数: action (可选值: read, create, convert, open)");

        var action = actionEl.GetString()?.ToLowerInvariant();

        return action switch
        {
            "read" => await ReadDocumentAsync(parameters, ct),
            "create" => await CreateDocumentAsync(parameters, ct),
            "convert" => await ConvertDocumentAsync(parameters, ct),
            "open" => OpenDocument(parameters),
            _ => ToolResult.Error($"不支持的操作: {action}。可选值: read, create, convert, open"),
        };
    }

    // ── 读取 ─────────────────────────────────────────────────

    private static async Task<ToolResult> ReadDocumentAsync(JsonElement parameters, CancellationToken ct)
    {
        if (!parameters.TryGetProperty("path", out var pathEl))
            return ToolResult.Error("缺少参数: path (文档路径)");

        var path = pathEl.GetString();
        if (string.IsNullOrWhiteSpace(path))
            return ToolResult.Error("path 不能为空");

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            return ToolResult.Error($"文件不存在: {fullPath}");

        var ext = Path.GetExtension(fullPath);
        if (!ReadableExtensions.Contains(ext))
            return ToolResult.Error($"不支持读取此格式: {ext}");

        try
        {
            return ext.ToLowerInvariant() switch
            {
                ".txt" or ".csv" => ToolResult.Ok(await File.ReadAllTextAsync(fullPath, ct)),
                ".pdf" => await ReadPdfAsync(fullPath, ct),
                ".docx" or ".doc" or ".rtf" => await ReadWordAsync(fullPath, ct),
                ".xlsx" or ".xls" => await ReadExcelAsync(fullPath, ct),
                ".pptx" or ".ppt" => await ReadPowerPointAsync(fullPath, ct),
                _ => ToolResult.Error($"暂不支持读取: {ext}"),
            };
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"读取文档失败: {ex.Message}");
        }
    }

    // ── 创建 ─────────────────────────────────────────────────

    private static async Task<ToolResult> CreateDocumentAsync(JsonElement parameters, CancellationToken ct)
    {
        if (!parameters.TryGetProperty("path", out var pathEl))
            return ToolResult.Error("缺少参数: path (输出文件路径)");
        if (!parameters.TryGetProperty("content", out var contentEl))
            return ToolResult.Error("缺少参数: content (文档内容)");

        var path = pathEl.GetString();
        var content = contentEl.GetString() ?? "";

        if (string.IsNullOrWhiteSpace(path))
            return ToolResult.Error("path 不能为空");

        var fullPath = Path.GetFullPath(path);
        var ext = Path.GetExtension(fullPath);

        if (!SupportedExtensions.Contains(ext))
            return ToolResult.Error($"不支持创建此格式: {ext}。支持: .docx, .xlsx, .pptx, .pdf, .txt, .csv");

        try
        {
            var dir = Path.GetDirectoryName(fullPath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            return ext.ToLowerInvariant() switch
            {
                ".txt" or ".csv" => await CreateTextFileAsync(fullPath, content, ct),
                ".docx" => await CreateWordAsync(fullPath, content, ct),
                ".xlsx" => await CreateExcelAsync(fullPath, content, ct),
                ".pptx" => await CreatePowerPointAsync(fullPath, content, ct),
                ".pdf" => await CreatePdfViaWordAsync(fullPath, content, ct),
                _ => ToolResult.Error($"暂不支持创建: {ext}"),
            };
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"创建文档失败: {ex.Message}");
        }
    }

    // ── 转换 ─────────────────────────────────────────────────

    private static async Task<ToolResult> ConvertDocumentAsync(JsonElement parameters, CancellationToken ct)
    {
        if (!parameters.TryGetProperty("source", out var sourceEl))
            return ToolResult.Error("缺少参数: source (源文件路径)");
        if (!parameters.TryGetProperty("target", out var targetEl))
            return ToolResult.Error("缺少参数: target (目标文件路径)");

        var source = sourceEl.GetString();
        var target = targetEl.GetString();

        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            return ToolResult.Error("source 和 target 不能为空");

        var sourcePath = Path.GetFullPath(source);
        var targetPath = Path.GetFullPath(target);

        if (!File.Exists(sourcePath))
            return ToolResult.Error($"源文件不存在: {sourcePath}");

        var sourceExt = Path.GetExtension(sourcePath).ToLowerInvariant();
        var targetExt = Path.GetExtension(targetPath).ToLowerInvariant();

        try
        {
            var dir = Path.GetDirectoryName(targetPath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (sourceExt is ".docx" or ".doc" && targetExt == ".pdf")
                return await ConvertWordToPdfAsync(sourcePath, targetPath, ct);

            if (sourceExt is ".xlsx" or ".xls" && targetExt == ".pdf")
                return await ConvertExcelToPdfAsync(sourcePath, targetPath, ct);

            if (sourceExt is ".pptx" or ".ppt" && targetExt == ".pdf")
                return await ConvertPptToPdfAsync(sourcePath, targetPath, ct);

            return ToolResult.Error($"不支持的转换: {sourceExt} → {targetExt}");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"转换失败: {ex.Message}");
        }
    }

    // ── 打开 ─────────────────────────────────────────────────

    private static ToolResult OpenDocument(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("path", out var pathEl))
            return ToolResult.Error("缺少参数: path (文档路径)");

        var path = pathEl.GetString();
        if (string.IsNullOrWhiteSpace(path))
            return ToolResult.Error("path 不能为空");

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            return ToolResult.Error($"文件不存在: {fullPath}");

        try
        {
            Process.Start(new ProcessStartInfo { FileName = fullPath, UseShellExecute = true });
            return ToolResult.Ok($"已用默认应用打开: {fullPath}");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"打开文档失败: {ex.Message}");
        }
    }

    // ── 读取实现 ─────────────────────────────────────────────

    private static async Task<ToolResult> ReadWordAsync(string path, CancellationToken ct)
    {
        var escapedPath = path.Replace("'", "''");
        var script = $$"""
            $word = New-Object -ComObject Word.Application
            $word.Visible = $false
            try {
                $doc = $word.Documents.Open('{{escapedPath}}', $false, $true)
                $text = $doc.Content.Text
                $doc.Close($false)
                Write-Output $text
            } finally {
                $word.Quit()
                [System.Runtime.InteropServices.Marshal]::ReleaseComObject($word) | Out-Null
            }
            """;
        return await RunPowerShellAsync(script, "Word 文档", ct);
    }

    private static async Task<ToolResult> ReadExcelAsync(string path, CancellationToken ct)
    {
        var escapedPath = path.Replace("'", "''");
        var script = $$"""
            $excel = New-Object -ComObject Excel.Application
            $excel.Visible = $false
            $excel.DisplayAlerts = $false
            try {
                $wb = $excel.Workbooks.Open('{{escapedPath}}', 0, $true)
                $result = @()
                foreach ($ws in $wb.Worksheets) {
                    $result += "=== Sheet: $($ws.Name) ==="
                    $usedRange = $ws.UsedRange
                    for ($r = 1; $r -le [Math]::Min($usedRange.Rows.Count, 100); $r++) {
                        $row = @()
                        for ($c = 1; $c -le $usedRange.Columns.Count; $c++) {
                            $row += $usedRange.Cells($r, $c).Text
                        }
                        $result += ($row -join "`t")
                    }
                    if ($usedRange.Rows.Count -gt 100) { $result += "... (共 $($usedRange.Rows.Count) 行)" }
                }
                $wb.Close($false)
                Write-Output ($result -join "`n")
            } finally {
                $excel.Quit()
                [System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel) | Out-Null
            }
            """;
        return await RunPowerShellAsync(script, "Excel 文档", ct);
    }

    private static async Task<ToolResult> ReadPowerPointAsync(string path, CancellationToken ct)
    {
        var escapedPath = path.Replace("'", "''");
        var script = $$"""
            $ppt = New-Object -ComObject PowerPoint.Application
            try {
                $presentation = $ppt.Presentations.Open('{{escapedPath}}', $true, $false, $false)
                $result = @()
                foreach ($slide in $presentation.Slides) {
                    $result += "=== Slide $($slide.SlideIndex) ==="
                    foreach ($shape in $slide.Shapes) {
                        if ($shape.HasTextFrame -and $shape.TextFrame.HasText) {
                            $result += $shape.TextFrame.TextRange.Text
                        }
                    }
                }
                $presentation.Close()
                Write-Output ($result -join "`n")
            } finally {
                $ppt.Quit()
                [System.Runtime.InteropServices.Marshal]::ReleaseComObject($ppt) | Out-Null
            }
            """;
        return await RunPowerShellAsync(script, "PowerPoint 文档", ct);
    }

    private static async Task<ToolResult> ReadPdfAsync(string path, CancellationToken ct)
    {
        var escapedPath = path.Replace("'", "''");
        var script = $$"""
            try {
                $word = New-Object -ComObject Word.Application
                $word.Visible = $false
                $doc = $word.Documents.Open('{{escapedPath}}', $false, $true)
                $text = $doc.Content.Text
                $doc.Close($false)
                $word.Quit()
                [System.Runtime.InteropServices.Marshal]::ReleaseComObject($word) | Out-Null
                Write-Output $text
            } catch {
                Write-Output "无法读取 PDF 内容: $_"
            }
            """;
        return await RunPowerShellAsync(script, "PDF 文档", ct);
    }

    // ── 创建实现 ─────────────────────────────────────────────

    private static async Task<ToolResult> CreateTextFileAsync(string path, string content, CancellationToken ct)
    {
        await File.WriteAllTextAsync(path, content, Encoding.UTF8, ct);
        return ToolResult.Ok($"文本文件已创建: {path} ({content.Length} 字符)");
    }

    private static async Task<ToolResult> CreateWordAsync(string path, string content, CancellationToken ct)
    {
        var escapedContent = content.Replace("'", "''").Replace("`", "``");
        var escapedPath = path.Replace("'", "''");
        var script = $$"""
            $word = New-Object -ComObject Word.Application
            $word.Visible = $false
            try {
                $doc = $word.Documents.Add()
                $paragraphs = '{{escapedContent}}' -split "`n"
                foreach ($p in $paragraphs) {
                    $selection = $word.Selection
                    $selection.TypeText($p)
                    $selection.TypeParagraph()
                }
                $doc.SaveAs2('{{escapedPath}}', 16)
                $doc.Close()
                Write-Output "Word 文档已创建"
            } finally {
                $word.Quit()
                [System.Runtime.InteropServices.Marshal]::ReleaseComObject($word) | Out-Null
            }
            """;
        return await RunPowerShellAsync(script, "创建 Word", ct);
    }

    private static async Task<ToolResult> CreateExcelAsync(string path, string content, CancellationToken ct)
    {
        var escapedContent = content.Replace("'", "''").Replace("`", "``");
        var escapedPath = path.Replace("'", "''");
        var script = $$"""
            $excel = New-Object -ComObject Excel.Application
            $excel.Visible = $false
            $excel.DisplayAlerts = $false
            try {
                $wb = $excel.Workbooks.Add()
                $ws = $wb.Worksheets.Item(1)
                $lines = '{{escapedContent}}' -split "`n"
                $row = 1
                foreach ($line in $lines) {
                    $cols = $line -split "`t|,"
                    $col = 1
                    foreach ($cell in $cols) {
                        $ws.Cells($row, $col).Value2 = $cell.Trim()
                        $col++
                    }
                    $row++
                }
                $wb.SaveAs('{{escapedPath}}', 51)
                $wb.Close()
                Write-Output "Excel 文档已创建"
            } finally {
                $excel.Quit()
                [System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel) | Out-Null
            }
            """;
        return await RunPowerShellAsync(script, "创建 Excel", ct);
    }

    private static async Task<ToolResult> CreatePowerPointAsync(string path, string content, CancellationToken ct)
    {
        var escapedContent = content.Replace("'", "''").Replace("`", "``");
        var escapedPath = path.Replace("'", "''");
        var script = $$"""
            $ppt = New-Object -ComObject PowerPoint.Application
            try {
                $presentation = $ppt.Presentations.Add($true)
                $slides = '{{escapedContent}}' -split "---"
                foreach ($slideContent in $slides) {
                    $slide = $presentation.Slides.Add($presentation.Slides.Count + 1, 2)
                    $lines = $slideContent.Trim() -split "`n", 2
                    if ($lines.Count -ge 1) { $slide.Shapes.Title.TextFrame.TextRange.Text = $lines[0].Trim() }
                    if ($lines.Count -ge 2) { $slide.Shapes.Item(2).TextFrame.TextRange.Text = $lines[1].Trim() }
                }
                $presentation.SaveAs('{{escapedPath}}')
                $presentation.Close()
                Write-Output "PowerPoint 文档已创建"
            } finally {
                $ppt.Quit()
                [System.Runtime.InteropServices.Marshal]::ReleaseComObject($ppt) | Out-Null
            }
            """;
        return await RunPowerShellAsync(script, "创建 PowerPoint", ct);
    }

    private static async Task<ToolResult> CreatePdfViaWordAsync(string path, string content, CancellationToken ct)
    {
        var tempDocx = Path.Combine(Path.GetTempPath(), $"ownerai_{Guid.NewGuid():N}.docx");
        try
        {
            var createResult = await CreateWordAsync(tempDocx, content, ct);
            if (!createResult.Success)
                return createResult;

            return await ConvertWordToPdfAsync(tempDocx, path, ct);
        }
        finally
        {
            if (File.Exists(tempDocx))
                File.Delete(tempDocx);
        }
    }

    // ── 转换实现 ─────────────────────────────────────────────

    private static async Task<ToolResult> ConvertWordToPdfAsync(string source, string target, CancellationToken ct)
    {
        var escapedSource = source.Replace("'", "''");
        var escapedTarget = target.Replace("'", "''");
        var script = $$"""
            $word = New-Object -ComObject Word.Application
            $word.Visible = $false
            try {
                $doc = $word.Documents.Open('{{escapedSource}}')
                $doc.SaveAs2('{{escapedTarget}}', 17)
                $doc.Close()
                Write-Output "已转换为 PDF"
            } finally {
                $word.Quit()
                [System.Runtime.InteropServices.Marshal]::ReleaseComObject($word) | Out-Null
            }
            """;
        return await RunPowerShellAsync(script, "Word → PDF", ct);
    }

    private static async Task<ToolResult> ConvertExcelToPdfAsync(string source, string target, CancellationToken ct)
    {
        var escapedSource = source.Replace("'", "''");
        var escapedTarget = target.Replace("'", "''");
        var script = $$"""
            $excel = New-Object -ComObject Excel.Application
            $excel.Visible = $false
            $excel.DisplayAlerts = $false
            try {
                $wb = $excel.Workbooks.Open('{{escapedSource}}')
                $wb.ExportAsFixedFormat(0, '{{escapedTarget}}')
                $wb.Close($false)
                Write-Output "已转换为 PDF"
            } finally {
                $excel.Quit()
                [System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel) | Out-Null
            }
            """;
        return await RunPowerShellAsync(script, "Excel → PDF", ct);
    }

    private static async Task<ToolResult> ConvertPptToPdfAsync(string source, string target, CancellationToken ct)
    {
        var escapedSource = source.Replace("'", "''");
        var escapedTarget = target.Replace("'", "''");
        var script = $$"""
            $ppt = New-Object -ComObject PowerPoint.Application
            try {
                $presentation = $ppt.Presentations.Open('{{escapedSource}}', $true, $false, $false)
                $presentation.SaveAs('{{escapedTarget}}', 32)
                $presentation.Close()
                Write-Output "已转换为 PDF"
            } finally {
                $ppt.Quit()
                [System.Runtime.InteropServices.Marshal]::ReleaseComObject($ppt) | Out-Null
            }
            """;
        return await RunPowerShellAsync(script, "PPT → PDF", ct);
    }

    // ── PowerShell 执行引擎 ─────────────────────────────────

    private static async Task<ToolResult> RunPowerShellAsync(
        string script, string operationName, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };

        var output = new StringBuilder();
        var error = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) output.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) error.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.StandardInput.WriteLineAsync(script.AsMemory(), ct);
        process.StandardInput.Close();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(55));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return ToolResult.Error($"{operationName} 执行超时");
        }

        if (process.ExitCode != 0 && error.Length > 0)
        {
            if (output.Length > 0)
                return ToolResult.Ok(output.ToString().Trim());

            return ToolResult.Error($"{operationName} 失败: {error.ToString().Trim()}");
        }

        var result = output.ToString().Trim();
        return result.Length > 0
            ? ToolResult.Ok(result)
            : ToolResult.Ok($"{operationName} 操作完成");
    }
}
