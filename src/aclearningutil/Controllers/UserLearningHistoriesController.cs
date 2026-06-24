using System.Security.Claims;
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
    public class UserLearningHistoriesController : ControllerBase
    {
        private readonly AppDbContext _dbContext;
        private readonly ILogger<UserLearningHistoriesController> _logger;

        public UserLearningHistoriesController(AppDbContext dbContext, ILogger<UserLearningHistoriesController> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        private string GetUserId()
        {
            return User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? User.FindFirstValue("sub")
                ?? string.Empty;
        }

        // GET: api/UserLearningHistories?contentId=1&itemId=5
        [HttpGet]
        public async Task<ActionResult<List<UserLearningHistory>>> GetAll([FromQuery] int? contentId, [FromQuery] int? itemId)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized("User ID not found in token.");
            }

            IQueryable<UserLearningHistory> query = _dbContext.UserLearningHistories
                .Include(h => h.Content)
                .Where(h => h.UserId == userId);

            if (contentId.HasValue)
            {
                query = query.Where(h => h.ContentId == contentId.Value);
            }

            if (itemId.HasValue)
            {
                query = query.Where(h => h.ItemId == itemId.Value);
            }

            var histories = await query.OrderByDescending(h => h.LearnDate).ToListAsync();
            return histories;
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<UserLearningHistory>> GetById(int id)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized("User ID not found in token.");
            }

            var history = await _dbContext.UserLearningHistories
                .Include(h => h.Content)
                .FirstOrDefaultAsync(h => h.Id == id && h.UserId == userId);

            if (history == null)
            {
                return NotFound();
            }
            return history;
        }

        [HttpPost]
        public async Task<ActionResult<UserLearningHistory>> Create([FromBody] UserLearningHistory history)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized("User ID not found in token.");
            }

            var contentExists = await _dbContext.LearningContents.AnyAsync(c => c.Id == history.ContentId);
            if (!contentExists)
            {
                return BadRequest("ContentId does not exist.");
            }

            history.UserId = userId;
            if (history.LearnDate == default)
            {
                history.LearnDate = DateTime.Today;
            }

            _dbContext.UserLearningHistories.Add(history);
            await _dbContext.SaveChangesAsync();
            return CreatedAtAction(nameof(GetById), new { id = history.Id }, history);
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(int id, [FromBody] UserLearningHistory history)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized("User ID not found in token.");
            }

            var existing = await _dbContext.UserLearningHistories
                .FirstOrDefaultAsync(h => h.Id == id && h.UserId == userId);
            if (existing == null)
            {
                return NotFound();
            }

            var contentExists = await _dbContext.LearningContents.AnyAsync(c => c.Id == history.ContentId);
            if (!contentExists)
            {
                return BadRequest("ContentId does not exist.");
            }

            existing.ContentId = history.ContentId;
            existing.ItemId = history.ItemId;
            existing.LearnDate = history.LearnDate;
            existing.SuccessIndicator = history.SuccessIndicator;
            await _dbContext.SaveChangesAsync();
            return NoContent();
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized("User ID not found in token.");
            }

            var history = await _dbContext.UserLearningHistories
                .FirstOrDefaultAsync(h => h.Id == id && h.UserId == userId);
            if (history == null)
            {
                return NotFound();
            }

            _dbContext.UserLearningHistories.Remove(history);
            await _dbContext.SaveChangesAsync();
            return NoContent();
        }
    }
}
