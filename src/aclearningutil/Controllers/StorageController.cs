using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace aclearningutil.Controllers
{
    /// <summary>
    /// Controller for serving learning content files from the Storage folder.
    /// Requires authentication to access any files.
    /// URL pattern: /api/Storage/{subfolder}/{filename}
    /// e.g. /api/Storage/knowledge-exercises/data.json
    /// </summary>
    [Route("api/[controller]/{**filepath}")]
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
            "learnchinese",
            "knowledge-exercises",
            "englishlistening",
            "formula"
        };

        // Allowed file extensions
        private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".json",
            ".png",
            ".jpg",
            ".jpeg",
            ".mp3"
        };

        public StorageController(IWebHostEnvironment environment, ILogger<StorageController> logger)
        {
            _environment = environment;
            _logger = logger;
        }

        /// <summary>
        /// Serves a file from the Storage folder.
        /// </summary>
        /// <param name="filepath">Route-captured path in format "subfolder/filename" (e.g., "learnenglish/cet4.json")</param>
        [HttpGet]
        [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult GetFile(string filepath)
        {
            if (string.IsNullOrWhiteSpace(filepath))
            {
                return BadRequest("File path is required.");
            }

            // Normalize path and prevent directory traversal
            var normalizedPath = filepath.Replace('\\', '/').Trim('/');

            // Check for directory traversal attempts
            if (normalizedPath.Contains("..") || normalizedPath.Contains("//"))
            {
                _logger.LogWarning("Directory traversal attempt blocked: {Path}", filepath);
                return BadRequest("Invalid path.");
            }

            // Split path into subfolder and filename
            var parts = normalizedPath.Split('/', 2);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
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

            // Resolve the request against the actual Storage entries, segment by
            // segment, so the OrdinalIgnoreCase contract holds on case-sensitive
            // file systems (Linux) too — not just on Windows.
            var storageFolder = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, "Storage"));
            var segments = new[] { subfolder }.Concat(filename.Split('/')).ToArray();
            var fullPath = ResolveStorageFile(storageFolder, segments);
            if (fullPath is null)
            {
                return NotFound();
            }

            // Verify the resolved path is within the storage folder (extra safety check)
            if (!fullPath.StartsWith(storageFolder, StringComparison.Ordinal))
            {
                _logger.LogWarning("Path traversal blocked: resolved path {ResolvedPath} is outside storage folder {StoragePath}",
                    fullPath, storageFolder);
                return BadRequest("Invalid path.");
            }

            // Determine content type
            var contentType = extension.ToLowerInvariant() switch
            {
                ".json" => "application/json",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".mp3" => "audio/mpeg",
                _ => "application/octet-stream"
            };

            // Return file with caching
            var fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Response.Headers.Append("Cache-Control", "public, max-age=3600"); // 1 hour

            return File(fileStream, contentType);
        }

        /// <summary>
        /// Resolves path segments under the Storage folder one at a time. An exact
        /// on-disk match wins; otherwise the segment is matched case-insensitively
        /// against the directory's actual entries, so e.g. "LEARNENGLISH/Words.JSON"
        /// resolves to "learnenglish/words.json" on case-sensitive file systems as
        /// well. Returns the full path of the existing file, or null when a segment
        /// is missing, a non-final segment is not a directory, or the final segment
        /// is not a file.
        /// </summary>
        private static string? ResolveStorageFile(string storageFolder, IReadOnlyList<string> segments)
        {
            var current = storageFolder;
            for (var i = 0; i < segments.Count; i++)
            {
                var isLast = i == segments.Count - 1;
                var candidate = Path.Combine(current, segments[i]);
                if (isLast ? System.IO.File.Exists(candidate) : Directory.Exists(candidate))
                {
                    current = candidate;
                    continue;
                }

                if (!Directory.Exists(current))
                {
                    return null;
                }

                var match = Directory.EnumerateFileSystemEntries(current)
                    .FirstOrDefault(entry => string.Equals(Path.GetFileName(entry), segments[i], StringComparison.OrdinalIgnoreCase));
                if (match is null || (isLast ? !System.IO.File.Exists(match) : !Directory.Exists(match)))
                {
                    return null;
                }

                current = match;
            }

            return current;
        }
    }
}
