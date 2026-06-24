using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace aclearningutil.Controllers
{
    /// <summary>
    /// Controller for serving learning content files from the Storage folder.
    /// Requires authentication to access any files.
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class StorageController : ControllerBase
    {
        private readonly IWebHostEnvironment _environment;
        private readonly ILogger<StorageController> _logger;

        // Allowed subfolders within Storage - prevents directory traversal attacks
        private static readonly HashSet<string> AllowedSubfolders = new(StringComparer.OrdinalIgnoreCase)
        {
            "learnenglish",
            "knowledge-exercises"
        };

        // Allowed file extensions
        private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".json",
            ".png",
            ".jpg",
            ".jpeg"
        };

        public StorageController(IWebHostEnvironment environment, ILogger<StorageController> logger)
        {
            _environment = environment;
            _logger = logger;
        }

        /// <summary>
        /// Serves a file from the Storage folder.
        /// </summary>
        /// <param name="path">Relative path within Storage (e.g., "learnenglish/cet4.json")</param>
        [HttpGet]
        [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult GetFile([FromQuery] string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return BadRequest("Path parameter is required.");
            }

            // Normalize path and prevent directory traversal
            var normalizedPath = path.Replace('\\', '/').Trim('/');

            // Check for directory traversal attempts
            if (normalizedPath.Contains("..") || normalizedPath.Contains("//"))
            {
                _logger.LogWarning("Directory traversal attempt blocked: {Path}", path);
                return BadRequest("Invalid path.");
            }

            // Split path into subfolder and filename
            var parts = normalizedPath.Split('/', 2);
            if (parts.Length != 2)
            {
                return BadRequest("Path must be in format: subfolder/filename");
            }

            var subfolder = parts[0];
            var filename = parts[1];

            // Validate subfolder
            if (!AllowedSubfolders.Contains(subfolder))
            {
                _logger.LogWarning("Access attempt to disallowed subfolder: {Subfolder}", subfolder);
                return BadRequest("Invalid subfolder.");
            }

            // Validate file extension
            var extension = Path.GetExtension(filename);
            if (!AllowedExtensions.Contains(extension))
            {
                _logger.LogWarning("Access attempt to disallowed file type: {Filename}", filename);
                return BadRequest("File type not allowed.");
            }

            // Prevent filename with path separators
            if (filename.Contains('/') || filename.Contains('\\'))
            {
                return BadRequest("Invalid filename.");
            }

            // Build full path
            var storageFolder = Path.Combine(_environment.ContentRootPath, "Storage");
            var fullPath = Path.Combine(storageFolder, subfolder, filename);

            // Verify the resolved path is within the storage folder (extra safety check)
            var fullStoragePath = Path.GetFullPath(storageFolder);
            var fullFilePath = Path.GetFullPath(fullPath);
            if (!fullFilePath.StartsWith(fullStoragePath))
            {
                _logger.LogWarning("Path traversal blocked: resolved path {ResolvedPath} is outside storage folder {StoragePath}",
                    fullFilePath, fullStoragePath);
                return BadRequest("Invalid path.");
            }

            if (!System.IO.File.Exists(fullPath))
            {
                return NotFound();
            }

            // Determine content type
            var contentType = extension.ToLowerInvariant() switch
            {
                ".json" => "application/json",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                _ => "application/octet-stream"
            };

            // Return file with caching
            var fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Response.Headers.Append("Cache-Control", "public, max-age=604800"); // 1 week

            return File(fileStream, contentType);
        }
    }
}
