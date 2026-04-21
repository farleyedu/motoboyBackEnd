using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace APIBack.Automation.Services
{
    public class ConversaAnexoService
    {
        private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".png", ".jpg", ".jpeg", ".webp", ".gif",
            ".doc", ".docx", ".xls", ".xlsx", ".txt", ".mp4", ".mp3", ".ogg"
        };

        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".webp", ".gif"
        };

        private const long MaxFileSizeBytes = 15 * 1024 * 1024;

        private readonly IWebHostEnvironment _environment;
        private readonly IConfiguration _configuration;

        public ConversaAnexoService(IWebHostEnvironment environment, IConfiguration configuration)
        {
            _environment = environment;
            _configuration = configuration;
        }

        public async Task<ConversaAnexoResponseDto> SalvarAsync(Guid idConversa, IFormFile file, CancellationToken cancellationToken = default)
        {
            if (file == null || file.Length <= 0)
                throw new ConversationManagementException(422, "Arquivo invalido.");

            if (file.Length > MaxFileSizeBytes)
                throw new ConversationManagementException(422, "Arquivo excede o limite de 15 MB.");

            var extension = Path.GetExtension(file.FileName);
            if (string.IsNullOrWhiteSpace(extension) || !AllowedExtensions.Contains(extension))
                throw new ConversationManagementException(422, "Tipo de arquivo nao permitido.");

            var root = _environment.WebRootPath;
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(AppContext.BaseDirectory, "wwwroot");

            var relativeFolder = Path.Combine("uploads", "conversas", idConversa.ToString("N"));
            var fullFolder = Path.Combine(root, relativeFolder);
            Directory.CreateDirectory(fullFolder);

            var storedName = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
            var fullPath = Path.Combine(fullFolder, storedName);

            await using (var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await file.CopyToAsync(stream, cancellationToken);
            }

            var relativePath = Path.Combine(relativeFolder, storedName).Replace("\\", "/");
            var publicUrl = BuildPublicUrl(relativePath);

            return new ConversaAnexoResponseDto
            {
                Id = Guid.NewGuid(),
                Nome = Path.GetFileName(file.FileName),
                Url = publicUrl,
                ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? null : file.ContentType,
                Tamanho = file.Length,
            };
        }

        public bool IsImage(string? contentType, string? url)
        {
            if (!string.IsNullOrWhiteSpace(contentType))
            {
                return contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
            }

            if (!string.IsNullOrWhiteSpace(url))
            {
                var ext = Path.GetExtension(new Uri(url).AbsolutePath);
                return ImageExtensions.Contains(ext);
            }

            return false;
        }

        private string BuildPublicUrl(string relativePath)
        {
            var baseUrl = _configuration["App:BaseUrl"]?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl))
                baseUrl = _configuration["ASPNETCORE_URLS"]?.Split(';')[0].TrimEnd('/');

            if (!string.IsNullOrWhiteSpace(baseUrl))
                return $"{baseUrl}/{relativePath}";

            return "/" + relativePath;
        }
    }
}
