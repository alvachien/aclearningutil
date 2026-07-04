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

        // GET: api/UserLearningHistories?contentId=1&itemId=5&page=1&pageSize=50
        [HttpGet]
        public async Task<ActionResult<List<UserLearningHistory>>> GetAll([FromQuery] int? contentId = null, [FromQuery] int? itemId = null, [FromQuery] int? page = null, [FromQuery] int? pageSize = null, CancellationToken cancellationToken = default)
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

            // Apply pagination
            const int maxPageSize = 200;
            var skip = ((page ?? 1) - 1) * Math.Clamp(pageSize ?? 50, 1, maxPageSize);
            var take = Math.Clamp(pageSize ?? 50, 1, maxPageSize);

            var histories = await query.OrderByDescending(h => h.LearnDate).Skip(skip).Take(take).ToListAsync(cancellationToken);
            return histories;
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<UserLearningHistory>> GetById(int id, CancellationToken cancellationToken)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized("User ID not found in token.");
            }

            var history = await _dbContext.UserLearningHistories
                .Include(h => h.Content)
                .FirstOrDefaultAsync(h => h.Id == id && h.UserId == userId, cancellationToken);

            if (history == null)
            {
                return NotFound();
            }
            return history;
        }

        [HttpPost]
        public async Task<ActionResult<UserLearningHistory>> Create([FromBody] UserLearningHistory history, CancellationToken cancellationToken)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized("User ID not found in token.");
            }

            var contentExists = await _dbContext.LearningContents.AnyAsync(c => c.Id == history.ContentId, cancellationToken);
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
            await _dbContext.SaveChangesAsync(cancellationToken);
            return CreatedAtAction(nameof(GetById), new { id = history.Id }, history);
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(int id, [FromBody] UserLearningHistory history, CancellationToken cancellationToken)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized("User ID not found in token.");
            }

            var existing = await _dbContext.UserLearningHistories
                .FirstOrDefaultAsync(h => h.Id == id && h.UserId == userId, cancellationToken);
            if (existing == null)
            {
                return NotFound();
            }

            var contentExists = await _dbContext.LearningContents.AnyAsync(c => c.Id == history.ContentId, cancellationToken);
            if (!contentExists)
            {
                return BadRequest("ContentId does not exist.");
            }

            existing.ContentId = history.ContentId;
            existing.ItemId = history.ItemId;
            existing.LearnDate = history.LearnDate;
            existing.SuccessIndicator = history.SuccessIndicator;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return NoContent();
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized("User ID not found in token.");
            }

            var history = await _dbContext.UserLearningHistories
                .FirstOrDefaultAsync(h => h.Id == id && h.UserId == userId, cancellationToken);
            if (history == null)
            {
                return NotFound();
            }

            _dbContext.UserLearningHistories.Remove(history);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return NoContent();
        }
    }
}
