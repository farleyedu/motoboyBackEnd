using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace APIBack.Automation.Services
{
    public class GarageSimulationStorageService
    {
        private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf",
            ".png",
            ".jpg",
            ".jpeg",
            ".webp",
            ".doc",
            ".docx",
            ".xls",
            ".xlsx",
            ".txt"
        };

        private const long MaxFileSizeBytes = 15 * 1024 * 1024;
        private readonly IWebHostEnvironment _environment;

        public GarageSimulationStorageService(IWebHostEnvironment environment)
        {
            _environment = environment;
        }

        public async Task<UploadGarageLeadSimulationFileResponse> SaveAsync(Guid idSimulacao, IFormFile file, CancellationToken cancellationToken = default)
        {
            if (file == null || file.Length <= 0)
            {
                throw new ConversationManagementException(422, "Arquivo invalido.");
            }

            if (file.Length > MaxFileSizeBytes)
            {
                throw new ConversationManagementException(422, "Arquivo excede o limite de 15 MB.");
            }

            var extension = Path.GetExtension(file.FileName);
            if (string.IsNullOrWhiteSpace(extension) || !AllowedExtensions.Contains(extension))
            {
                throw new ConversationManagementException(422, "Extensao de arquivo nao permitida.");
            }

            var root = _environment.WebRootPath;
            if (string.IsNullOrWhiteSpace(root))
            {
                root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            }

            var relativeFolder = Path.Combine("uploads", "garagem", "simulacoes", idSimulacao.ToString("N"));
            var fullFolder = Path.Combine(root, relativeFolder);
            Directory.CreateDirectory(fullFolder);

            var storedName = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
            var fullPath = Path.Combine(fullFolder, storedName);
            await using (var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await file.CopyToAsync(stream, cancellationToken);
            }

            var relativePath = Path.Combine(relativeFolder, storedName).Replace("\\", "/");
            var data = DateTime.UtcNow;
            return new UploadGarageLeadSimulationFileResponse
            {
                Id = Guid.NewGuid(),
                Nome = Path.GetFileName(file.FileName),
                Url = "/" + relativePath,
                Tamanho = file.Length,
                ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? null : file.ContentType,
                CaminhoRelativo = relativePath,
                Data = data
            };
        }

        public Task DeleteAsync(string? caminhoRelativo)
        {
            if (string.IsNullOrWhiteSpace(caminhoRelativo))
            {
                return Task.CompletedTask;
            }

            var root = _environment.WebRootPath;
            if (string.IsNullOrWhiteSpace(root))
            {
                root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            }

            var normalized = caminhoRelativo.TrimStart('/').Replace("/", Path.DirectorySeparatorChar.ToString());
            var fullPath = Path.Combine(root, normalized);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            return Task.CompletedTask;
        }
    }
}
