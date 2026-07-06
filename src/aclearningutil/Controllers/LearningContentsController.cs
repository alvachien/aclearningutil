using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using aclearningutil.Data;
using aclearningutil.Data.Entities;

namespace aclearningutil.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class LearningContentsController : ControllerBase
    {
        private readonly AppDbContext _dbContext;
        private readonly ILogger<LearningContentsController> _logger;

        public LearningContentsController(AppDbContext dbContext, ILogger<LearningContentsController> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        [HttpGet]
        public async Task<ActionResult<List<LearningContent>>> GetAll([FromQuery] int? categoryId = null, [FromQuery] int? page = null, [FromQuery] int? pageSize = null, CancellationToken cancellationToken = default)
        {
            IQueryable<LearningContent> query = _dbContext.LearningContents
                .Include(c => c.Category);

            if (categoryId.HasValue)
            {
                query = query.Where(c => c.CategoryId == categoryId.Value);
            }

            // Apply pagination
            const int maxPageSize = 200;
            var skip = ((page ?? 1) - 1) * Math.Clamp(pageSize ?? 50, 1, maxPageSize);
            var take = Math.Clamp(pageSize ?? 50, 1, maxPageSize);

            var contents = await query.OrderByDescending(c => c.UpdatedAt).Skip(skip).Take(take).ToListAsync(cancellationToken);
            return contents;
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<LearningContent>> GetById(int id, CancellationToken cancellationToken)
        {
            var content = await _dbContext.LearningContents
                .Include(c => c.Category)
                .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

            if (content == null)
            {
                return NotFound();
            }
            return content;
        }

        [HttpPost]
        public async Task<ActionResult<LearningContent>> Create([FromBody] LearningContent content, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(content.NameChinese) || string.IsNullOrWhiteSpace(content.NameEnglish))
            {
                return BadRequest("NameChinese and NameEnglish are required.");
            }

            if (!IsValidFileUrl(content.FileUrl))
            {
                return BadRequest("FileUrl is invalid. It must be a relative path starting with 'storage/', 'data/', or a subfolder name, and must not contain '..'.");
            }

            var categoryExists = await _dbContext.LearningContentCategories.AnyAsync(c => c.Id == content.CategoryId, cancellationToken);
            if (!categoryExists)
            {
                return BadRequest("CategoryId does not exist.");
            }

            content.CreatedAt = DateTime.UtcNow;
            content.UpdatedAt = DateTime.UtcNow;

            _dbContext.LearningContents.Add(content);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return CreatedAtAction(nameof(GetById), new { id = content.Id }, content);
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(int id, [FromBody] LearningContent content, CancellationToken cancellationToken)
        {
            var existing = await _dbContext.LearningContents.FindAsync(id, cancellationToken);
            if (existing == null)
            {
                return NotFound();
            }

            if (string.IsNullOrWhiteSpace(content.NameChinese) || string.IsNullOrWhiteSpace(content.NameEnglish))
            {
                return BadRequest("NameChinese and NameEnglish are required.");
            }

            if (!IsValidFileUrl(content.FileUrl))
            {
                return BadRequest("FileUrl is invalid. It must be a relative path starting with 'storage/', 'data/', or a subfolder name, and must not contain '..'.");
            }

            var categoryExists = await _dbContext.LearningContentCategories.AnyAsync(c => c.Id == content.CategoryId, cancellationToken);
            if (!categoryExists)
            {
                return BadRequest("CategoryId does not exist.");
            }

            existing.CategoryId = content.CategoryId;
            existing.NameChinese = content.NameChinese;
            existing.NameEnglish = content.NameEnglish;
            existing.FileUrl = content.FileUrl;
            existing.Version = content.Version;
            existing.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return NoContent();
        }

        private static bool IsValidFileUrl(string? fileUrl)
        {
            if (string.IsNullOrWhiteSpace(fileUrl)) return false;
            if (fileUrl.Contains("..")) return false;
            if (Path.IsPathRooted(fileUrl)) return false;
            return true;
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
        {
            var content = await _dbContext.LearningContents.FindAsync(id, cancellationToken);
            if (content == null)
            {
                return NotFound();
            }

            // Delete dependent records first (FK relationships use DeleteBehavior.Restrict)
            var histories = await _dbContext.UserLearningHistories
                .Where(h => h.ContentId == id).ToListAsync(cancellationToken);
            if (histories.Count > 0)
            {
                _dbContext.UserLearningHistories.RemoveRange(histories);
            }

            var ratings = await _dbContext.UserLearningRatings
                .Where(r => r.ContentId == id).ToListAsync(cancellationToken);
            if (ratings.Count > 0)
            {
                _dbContext.UserLearningRatings.RemoveRange(ratings);
            }

            _dbContext.LearningContents.Remove(content);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return NoContent();
        }
    }
}
