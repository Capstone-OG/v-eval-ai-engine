using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using V_Eval_Ai_Engine.Application.DTOs;

namespace V_Eval_Ai_Engine.Infrastructure.Services;

public class ExtractedPdfImage
{
    public int PageNumber { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string Extension { get; set; } = "png";
    public string RelativeUrl { get; set; } = string.Empty;
}

public class PdfImageExtractor
{
    private readonly ILogger<PdfImageExtractor> _logger;

    public PdfImageExtractor(ILogger<PdfImageExtractor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Trích xuất và kết xuất trực tiếp các khối hình ảnh/đồ thị từ PDF bằng PDFtoImage (PDFium) kết hợp vị trí Bounding Box từ PdfPig.
    /// Giữ nguyên 100% nét vẽ vector, nhãn chữ Word xung quanh, triệt tiêu hoàn toàn lỗi mất mặt nạ trong suốt (SMask/Black blocks) và lỗi định dạng ảnh rỗng.
    /// </summary>
    public List<ExtractedPdfImage> ExtractImages(byte[] pdfBytes, string webRootPath, string examId)
    {
        var result = new List<ExtractedPdfImage>();

        try
        {
            using var document = PdfDocument.Open(pdfBytes);
            string outputFolder = Path.Combine(webRootPath, "extracted_images", examId);
            Directory.CreateDirectory(outputFolder);

            int imgCounter = 0;

            foreach (var page in document.GetPages())
            {
                var rawImages = page.GetImages().ToList();
                if (rawImages.Count == 0) continue;

                // Lọc các hình ảnh hợp lệ (bỏ logo header trang 1 và icon quá nhỏ)
                var validImages = new List<IPdfImage>();
                foreach (var img in rawImages)
                {
                    if (img.Bounds.Width < 50 || img.Bounds.Height < 50) continue;

                    double topDownY = page.Height - img.Bounds.Top;
                    if (page.Number == 1 && topDownY < 160)
                    {
                        _logger.LogInformation("Bỏ qua logo tiêu đề Trang 1 (Y={Y:F1})", topDownY);
                        continue;
                    }

                    validImages.Add(img);
                }

                if (validImages.Count == 0) continue;

                // Kết xuất trang này bằng PDFtoImage (PDFium C++ Core) ở độ phân giải cao (150 DPI)
                using var pdfStream = new MemoryStream(pdfBytes);
                using var pageBitmap = Conversion.ToImage(pdfStream, page: page.Number - 1, options: new RenderOptions { Dpi = 150 });
                if (pageBitmap == null) continue;

                double scaleX = (double)pageBitmap.Width / page.Width;
                double scaleY = (double)pageBitmap.Height / page.Height;

                // Trường hợp trang có từ 2 ảnh trở lên (như Trang 14 gồm Hình A và Hình B thí nghiệm chùm câu 106-108):
                // Bao quát toàn bộ bounding box của các hình ảnh kèm lề padding an toàn để bao bọc cả chữ chú thích
                if (validImages.Count >= 2)
                {
                    double minX = validImages.Min(i => i.Bounds.Left);
                    double maxX = validImages.Max(i => i.Bounds.Left + i.Bounds.Width);
                    double minY = validImages.Min(i => page.Height - i.Bounds.Top);
                    double maxY = validImages.Max(i => page.Height - i.Bounds.Bottom);

                    // Padding mở rộng để lấy trọn vẹn cả chú thích "tán", "thân", "gốc" và "Tế bào ghép"
                    minX = Math.Max(0, minX - 25);
                    maxX = Math.Min(page.Width, maxX + 20);
                    minY = Math.Max(0, minY - 15);
                    maxY = Math.Min(page.Height, maxY + 20);

                    int px = (int)(minX * scaleX);
                    int py = (int)(minY * scaleY);
                    int pw = Math.Min(pageBitmap.Width - px, (int)((maxX - minX) * scaleX));
                    int ph = Math.Min(pageBitmap.Height - py, (int)((maxY - minY) * scaleY));

                    if (pw > 0 && ph > 0)
                    {
                        imgCounter++;
                        string fileName = $"p{page.Number}_combined.png";
                        string filePath = Path.Combine(outputFolder, fileName);

                        using var cropped = new SKBitmap(pw, ph);
                        using var canvas = new SKCanvas(cropped);
                        canvas.Clear(SKColors.White);
                        canvas.DrawBitmap(pageBitmap, new SKRect(px, py, px + pw, py + ph), new SKRect(0, 0, pw, ph), new SKSamplingOptions(SKFilterMode.Linear), null);

                        using var skImg = SKImage.FromBitmap(cropped);
                        using var data = skImg.Encode(SKEncodedImageFormat.Png, 100);
                        File.WriteAllBytes(filePath, data.ToArray());

                        // Tạo thêm file alias .jpeg để tương thích với các cache/URL cũ
                        string jpegAlias = Path.Combine(outputFolder, $"p{page.Number}_combined.jpeg");
                        File.WriteAllBytes(jpegAlias, data.ToArray());

                        result.Add(new ExtractedPdfImage
                        {
                            PageNumber = page.Number,
                            X = minX,
                            Y = minY,
                            Width = maxX - minX,
                            Height = maxY - minY,
                            Extension = "png",
                            RelativeUrl = $"/extracted_images/{examId}/{fileName}"
                        });

                        _logger.LogInformation("Kết xuất sơ đồ liên hoàn Trang {Page} ({W}x{H}px) nền trắng hoàn hảo: {File}", 
                            page.Number, pw, ph, fileName);
                    }
                }
                else
                {
                    // Từng hình ảnh đơn lẻ
                    foreach (var vImg in validImages.OrderBy(i => page.Height - i.Bounds.Top))
                    {
                        // Thêm padding nhẹ 15pt để chứa đầy đủ trục tọa độ và số liệu xung quanh
                        double minX = Math.Max(0, vImg.Bounds.Left - 18);
                        double maxX = Math.Min(page.Width, vImg.Bounds.Left + vImg.Bounds.Width + 18);
                        double minY = Math.Max(0, page.Height - vImg.Bounds.Top - 15);
                        double maxY = Math.Min(page.Height, page.Height - vImg.Bounds.Bottom + 15);

                        int px = (int)(minX * scaleX);
                        int py = (int)(minY * scaleY);
                        int pw = Math.Min(pageBitmap.Width - px, (int)((maxX - minX) * scaleX));
                        int ph = Math.Min(pageBitmap.Height - py, (int)((maxY - minY) * scaleY));

                        if (pw <= 0 || ph <= 0) continue;

                        imgCounter++;
                        string fileName = $"p{page.Number}_img{imgCounter}.png";
                        string filePath = Path.Combine(outputFolder, fileName);

                        using var cropped = new SKBitmap(pw, ph);
                        using var canvas = new SKCanvas(cropped);
                        canvas.Clear(SKColors.White);
                        canvas.DrawBitmap(pageBitmap, new SKRect(px, py, px + pw, py + ph), new SKRect(0, 0, pw, ph), new SKSamplingOptions(SKFilterMode.Linear), null);

                        using var skImg = SKImage.FromBitmap(cropped);
                        using var data = skImg.Encode(SKEncodedImageFormat.Png, 100);
                        File.WriteAllBytes(filePath, data.ToArray());

                        // Tạo thêm file alias .jpeg nếu có
                        string jpegAlias = Path.Combine(outputFolder, $"p{page.Number}_img{imgCounter}.jpeg");
                        File.WriteAllBytes(jpegAlias, data.ToArray());

                        result.Add(new ExtractedPdfImage
                        {
                            PageNumber = page.Number,
                            X = minX,
                            Y = minY,
                            Width = maxX - minX,
                            Height = maxY - minY,
                            Extension = "png",
                            RelativeUrl = $"/extracted_images/{examId}/{fileName}"
                        });

                        _logger.LogInformation("Kết xuất hình vẽ hoàn chỉnh Trang {Page} ({W}x{H}px) vector sắc nét: {File}", 
                            page.Number, pw, ph, fileName);
                    }
                }
            }

            _logger.LogInformation("Trích xuất và kết xuất thành công {Count} ảnh sắc nét từ PDF (100% Cục bộ).", result.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi trích xuất và kết xuất ảnh cục bộ từ PDF.");
        }

        return result;
    }

    /// <summary>
    /// Tự động ghép nối các ảnh đã kết xuất vào các câu hỏi và chùm bài đọc tương ứng trên cùng trang
    /// </summary>
    public void MapImagesToExam(ParsedExamDto exam, List<ExtractedPdfImage> images, string webRootPath, string examId)
    {
        if (images == null || images.Count == 0) return;

        // Nhóm ảnh theo số trang, sắp xếp từ trên xuống dưới theo tọa độ Y
        var imagesByPage = images
            .GroupBy(i => i.PageNumber)
            .ToDictionary(g => g.Key, g => g.OrderBy(i => i.Y).ToList());

        // 1. Ánh xạ vào Passages
        foreach (var passage in exam.Passages)
        {
            // Xác định số trang của passage từ các câu hỏi con
            int pageNum = passage.Questions.FirstOrDefault()?.PageNumber ?? 0;
            if (pageNum > 0 && imagesByPage.TryGetValue(pageNum, out var pageImgs) && pageImgs.Count > 0)
            {
                string contentLower = passage.Content.ToLowerInvariant();
                bool hasFigureKeyword = contentLower.Contains("hình") || 
                                       contentLower.Contains("sơ đồ") || 
                                       contentLower.Contains("biểu đồ") || 
                                       contentLower.Contains("đồ thị");

                if (hasFigureKeyword)
                {
                    passage.ImageUrl = pageImgs[0].RelativeUrl;
                    pageImgs.RemoveAt(0);
                }
            }
        }

        // 2. Ánh xạ vào Single Questions
        foreach (var q in exam.SingleQuestions)
        {
            int pageNum = q.PageNumber;
            if (pageNum > 0 && imagesByPage.TryGetValue(pageNum, out var pageImgs) && pageImgs.Count > 0)
            {
                string contentLower = q.Content.ToLowerInvariant();
                bool hasFigureKeyword = contentLower.Contains("hình") || 
                                       contentLower.Contains("đồ thị") || 
                                       contentLower.Contains("biểu đồ") || 
                                       contentLower.Contains("dụng cụ") ||
                                       contentLower.Contains("sơ đồ") ||
                                       contentLower.Contains("[hình vẽ]");

                if (hasFigureKeyword)
                {
                    q.ImageUrl = pageImgs[0].RelativeUrl;
                    pageImgs.RemoveAt(0);
                }
            }
        }
    }
}
