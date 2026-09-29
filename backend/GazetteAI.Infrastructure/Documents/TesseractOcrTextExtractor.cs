using System.Diagnostics;
using System.Text;
using GazetteAI.Application.Documents.Interfaces;
using GazetteAI.Application.Documents.Models;
using PDFtoImage;
using UglyToad.PdfPig;

namespace GazetteAI.Infrastructure.Documents;

public sealed class TesseractOcrTextExtractor : IOcrTextExtractor
{
    private const string WindowsTesseractPath =
        @"C:\Program Files\Tesseract-OCR\tesseract.exe";

    public async Task<IReadOnlyList<ExtractedPage>> ExtractAsync(
        Stream pdfStream,
        CancellationToken cancellationToken = default)
    {
        if (!pdfStream.CanRead)
        {
            throw new ArgumentException(
                "PDF stream cannot be read.",
                nameof(pdfStream));
        }

        await using var memoryStream = new MemoryStream();

        await pdfStream.CopyToAsync(
            memoryStream,
            cancellationToken);

        var pdfBytes = memoryStream.ToArray();

        int totalPages;

        using (var document = PdfDocument.Open(pdfBytes))
        {
            totalPages = document.NumberOfPages;
        }

        var temporaryFolder = Path.Combine(
            Path.GetTempPath(),
            "GazetteAI",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(temporaryFolder);

        var extractedPages =
            new List<ExtractedPage>(totalPages);

        try
        {
            for (var pageIndex = 0;
                 pageIndex < totalPages;
                 pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var imagePath = Path.Combine(
                    temporaryFolder,
                    $"page-{pageIndex + 1}.png");

                await using (var pagePdfStream =
                    new MemoryStream(pdfBytes))
                {
                 #pragma warning disable CA1416
                    Conversion.SavePng(
                        imageFilename: imagePath,
                        pdfStream: pagePdfStream,
                        page: pageIndex);
                 #pragma warning restore CA1416
                }

                var text = await RunTesseractAsync(
                    imagePath,
                    cancellationToken);

                extractedPages.Add(
                    new ExtractedPage(
                        pageIndex + 1,
                        text.Trim()));
            }

            return extractedPages;
        }
        finally
        {
            if (Directory.Exists(temporaryFolder))
            {
                Directory.Delete(
                    temporaryFolder,
                    recursive: true);
            }
        }
    }

    private static async Task<string> RunTesseractAsync(
        string imagePath,
        CancellationToken cancellationToken)
    {
        var tesseractExecutable =
            File.Exists(WindowsTesseractPath)
                ? WindowsTesseractPath
                : "tesseract";

        var tessdataPath = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile),
            "tessdata");

        if (!Directory.Exists(tessdataPath))
        {
            throw new DirectoryNotFoundException(
                $"Tesseract language folder was not found: " +
                $"{tessdataPath}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = tesseractExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add(imagePath);
        startInfo.ArgumentList.Add("stdout");

        startInfo.ArgumentList.Add(
            "--tessdata-dir");

        startInfo.ArgumentList.Add(
            tessdataPath);

        startInfo.ArgumentList.Add("-l");
        startInfo.ArgumentList.Add("tam+sin+eng");

        startInfo.ArgumentList.Add("--psm");
        startInfo.ArgumentList.Add("6");

        using var process = new Process
        {
            StartInfo = startInfo
        };

        process.Start();

        var outputTask =
            process.StandardOutput.ReadToEndAsync(
                cancellationToken);

        var errorTask =
            process.StandardError.ReadToEndAsync(
                cancellationToken);

        await process.WaitForExitAsync(
            cancellationToken);

        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Tesseract OCR failed. {error}");
        }

        return output;
    }
}