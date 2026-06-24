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
        public async Task<ActionResult<List<LearningContent>>> GetAll([FromQuery] int? categoryId)
        {
            IQueryable<LearningContent> query = _dbContext.LearningContents
                .Include(c => c.Category);

            if (categoryId.HasValue)
            {
                query = query.Where(c => c.CategoryId == categoryId.Value);
            }

            var contents = await query.OrderByDescending(c => c.UpdatedAt).ToListAsync();
            return contents;
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<LearningContent>> GetById(int id)
        {
            var content = await _dbContext.LearningContents
                .Include(c => c.Category)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (content == null)
            {
                return NotFound();
            }
            return content;
        }

        [HttpPost]
        public async Task<ActionResult<LearningContent>> Create([FromBody] LearningContent content)
        {
            if (string.IsNullOrWhiteSpace(content.NameChinese) || string.IsNullOrWhiteSpace(content.NameEnglish))
            {
                return BadRequest("NameChinese and NameEnglish are required.");
            }

            var categoryExists = await _dbContext.LearningContentCategories.AnyAsync(c => c.Id == content.CategoryId);
            if (!categoryExists)
            {
                return BadRequest("CategoryId does not exist.");
            }

            content.CreatedAt = DateTime.UtcNow;
            content.UpdatedAt = DateTime.UtcNow;

            _dbContext.LearningContents.Add(content);
            await _dbContext.SaveChangesAsync();
            return CreatedAtAction(nameof(GetById), new { id = content.Id }, content);
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(int id, [FromBody] LearningContent content)
        {
            var existing = await _dbContext.LearningContents.FindAsync(id);
            if (existing == null)
            {
                return NotFound();
            }

            if (string.IsNullOrWhiteSpace(content.NameChinese) || string.IsNullOrWhiteSpace(content.NameEnglish))
            {
                return BadRequest("NameChinese and NameEnglish are required.");
            }

            var categoryExists = await _dbContext.LearningContentCategories.AnyAsync(c => c.Id == content.CategoryId);
            if (!categoryExists)
            {
                return BadRequest("CategoryId does not exist.");
            }

            existing.CategoryId = content.CategoryId;
            existing.NameChinese = content.NameChinese;
            existing.NameEnglish = content.NameEnglish;
            existing.FileUrl = content.FileUrl;
            existing.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
            return NoContent();
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            var content = await _dbContext.LearningContents.FindAsync(id);
            if (content == null)
            {
                return NotFound();
            }

            _dbContext.LearningContents.Remove(content);
            await _dbContext.SaveChangesAsync();
            return NoContent();
        }
    }
}
