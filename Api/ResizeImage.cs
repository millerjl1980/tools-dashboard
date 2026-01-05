using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace Api;

public class ResizeImage
{
    private readonly ILogger<ResizeImage> _logger;

    public ResizeImage(ILogger<ResizeImage> logger)
    {
        _logger = logger;
    }

    [Function("ResizeImage")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest req)
    {
        _logger.LogInformation("Starting image resize function processing");

        try
        {
            // Get form data
            var form = await req.ReadFormAsync();
            _logger.LogInformation("Form data received successfully");

            var file = form.Files["image"];
            
            if (file == null || file.Length == 0)
            {
                _logger.LogWarning("No image file provided in request");
                return new BadRequestObjectResult("No image file provided");
            }

            _logger.LogInformation($"Processing image: {file.FileName}, Size: {file.Length} bytes, Content-Type: {file.ContentType}");

            // Validate file size (20MB limit)
            if (file.Length > 20 * 1024 * 1024)
            {
                _logger.LogWarning($"File size too large: {file.Length} bytes. Maximum allowed: 20MB");
                return new BadRequestObjectResult("File size too large. Maximum size is 20MB");
            }

            // Validate content type
            if (!IsValidImageType(file.ContentType))
            {
                _logger.LogWarning($"Invalid image format provided: {file.ContentType}");
                return new BadRequestObjectResult("Invalid image format. Supported formats: JPG, PNG, GIF, BMP, WEBP");
            }

            // Get parameters
            int? maxWidth = null;
            int? maxHeight = null;
            long? targetSize = null;

            if (!string.IsNullOrEmpty(form["maxWidth"]) && int.TryParse(form["maxWidth"], out int w)) 
                maxWidth = w > 0 ? w : null;
            if (!string.IsNullOrEmpty(form["maxHeight"]) && int.TryParse(form["maxHeight"], out int h)) 
                maxHeight = h > 0 ? h : null;
            if (!string.IsNullOrEmpty(form["targetSize"]) && long.TryParse(form["targetSize"], out long s)) 
                targetSize = s > 0 ? s : null;

            _logger.LogInformation($"Resize parameters - Width: {maxWidth}, Height: {maxHeight}, TargetSize: {targetSize}");

            // Validate parameters
            if (!maxWidth.HasValue && !maxHeight.HasValue && !targetSize.HasValue)
            {
                _logger.LogWarning("No resize parameters provided");
                return new BadRequestObjectResult("Please provide at least one resize parameter");
            }

            // Validate dimension ranges
            if (maxWidth.HasValue && (maxWidth.Value < 1 || maxWidth.Value > 10000))
            {
                _logger.LogWarning($"Invalid width value: {maxWidth.Value}");
                return new BadRequestObjectResult("Width must be between 1 and 10,000 pixels");
            }
            if (maxHeight.HasValue && (maxHeight.Value < 1 || maxHeight.Value > 10000))
            {
                _logger.LogWarning($"Invalid height value: {maxHeight.Value}");
                return new BadRequestObjectResult("Height must be between 1 and 10,000 pixels");
            }
            if (targetSize.HasValue && (targetSize.Value < 1024 || targetSize.Value > 100 * 1024 * 1024))
            {
                _logger.LogWarning($"Invalid target size value: {targetSize.Value}");
                return new BadRequestObjectResult("Target size must be between 1KB and 100MB");
            }

            // Try to load and validate the image
            Image? image = null;
            try
            {
                _logger.LogInformation("Loading image from stream");
                image = await Image.LoadAsync(file.OpenReadStream());
                _logger.LogInformation($"Image loaded successfully - Original dimensions: {image.Width}x{image.Height}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load image file - invalid image format");
                return new BadRequestObjectResult("Invalid image file. Please upload a valid image.");
            }

            using (image)
            {
                using var resultStream = new MemoryStream();

                if (targetSize.HasValue)
                {
                    _logger.LogInformation($"Resizing by target size: {targetSize.Value} bytes");
                    await ResizeByFileSize(image, resultStream, targetSize.Value, file.ContentType);
                }
                else
                {
                    _logger.LogInformation($"Resizing by dimensions - Width: {maxWidth}, Height: {maxHeight}");
                    await ResizeByDimensions(image, resultStream, maxWidth, maxHeight, file.ContentType);
                }

                _logger.LogInformation($"Resize completed. Result size: {resultStream.Length} bytes");

                // Return resized image
                var result = new FileContentResult(resultStream.ToArray(), file.ContentType)
                {
                    FileDownloadName = $"resized_{file.FileName ?? "image.jpg"}"
                };

                _logger.LogInformation("Image resize function completed successfully");
                return result;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during image processing");
            return new StatusCodeResult(500);
        }
    }

    private async Task ResizeByDimensions(Image image, MemoryStream resultStream, 
        int? maxWidth, int? maxHeight, string contentType)
    {
        _logger.LogInformation($"Starting dimension-based resize - Original: {image.Width}x{image.Height}");

        var options = new ResizeOptions
        {
            Mode = ResizeMode.Max,
            Position = AnchorPositionMode.Center
        };

        if (maxWidth.HasValue && maxHeight.HasValue)
        {
            options.Size = new Size(maxWidth.Value, maxHeight.Value);
            _logger.LogInformation($"Resizing to exact dimensions: {maxWidth.Value}x{maxHeight.Value}");
        }
        else if (maxWidth.HasValue)
        {
            options.Size = new Size(maxWidth.Value, 0);
            _logger.LogInformation($"Resizing by width: {maxWidth.Value}px");
        }
        else if (maxHeight.HasValue)
        {
            options.Size = new Size(0, maxHeight.Value);
            _logger.LogInformation($"Resizing by height: {maxHeight.Value}px");
        }

        image.Mutate(x => x.Resize(options));

        var encoder = GetEncoder(contentType);
        await image.SaveAsync(resultStream, encoder);

        _logger.LogInformation($"Dimension resize completed - New dimensions: {image.Width}x{image.Height}, Size: {resultStream.Length} bytes");
    }

    private async Task ResizeByFileSize(Image image, MemoryStream resultStream, 
        long targetSize, string contentType)
    {
        _logger.LogInformation($"Starting size-based resize - Target size: {targetSize} bytes, Original size: {image.Width}x{image.Height}");

        var quality = 90;

        while (quality > 10)
        {
            resultStream.SetLength(0);
            
            // Create new encoder with quality setting for each iteration
            if (contentType.ToLower().Contains("jpeg") || contentType.ToLower().Contains("jpg"))
            {
                var jpegEncoder = new JpegEncoder { Quality = quality };
                await image.SaveAsync(resultStream, jpegEncoder);
                _logger.LogInformation($"JPEG quality iteration - Quality: {quality}, Size: {resultStream.Length} bytes");
            }
            else
            {
                var encoder = GetEncoder(contentType);
                await image.SaveAsync(resultStream, encoder);
                _logger.LogInformation($"Non-JPEG format - Size: {resultStream.Length} bytes");
                break; // Quality adjustment not supported for other formats
            }

            if (resultStream.Length <= targetSize)
            {
                _logger.LogInformation($"Target size achieved - Quality: {quality}, Size: {resultStream.Length} bytes");
                break;
            }

            quality -= 10;
        }

        if (quality <= 10 && resultStream.Length > targetSize)
        {
            _logger.LogWarning($"Could not achieve target size. Final size: {resultStream.Length} bytes, Target: {targetSize} bytes");
        }
    }

    private IImageEncoder GetEncoder(string contentType)
    {
        _logger.LogInformation($"Getting encoder for content type: {contentType}");
        
        return contentType.ToLower() switch
        {
            "image/jpeg" or "image/jpg" => new JpegEncoder(), // Default quality
            "image/png" => new SixLabors.ImageSharp.Formats.Png.PngEncoder(),
            "image/gif" => new SixLabors.ImageSharp.Formats.Gif.GifEncoder(),
            "image/bmp" => new SixLabors.ImageSharp.Formats.Bmp.BmpEncoder(),
            "image/webp" => new SixLabors.ImageSharp.Formats.Webp.WebpEncoder(),
            _ => new JpegEncoder() // Default fallback
        };
    }

    private bool IsValidImageType(string contentType)
    {
        var validTypes = new[] { "image/jpeg", "image/jpg", "image/png", "image/gif", "image/bmp", "image/webp" };
        var isValid = Array.Exists(validTypes, type => type.Equals(contentType, StringComparison.OrdinalIgnoreCase));
        _logger.LogInformation($"Content type validation - Type: {contentType}, Valid: {isValid}");
        return isValid;
    }
}